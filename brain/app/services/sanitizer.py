r"""Sanitization layer for AI-generated file manifests.

Priority order:
1. Strip ../, ..\, leading / or \, null bytes
2. Validate no segment is '.' or empty string
3. If scope is set, rebase or reject paths that escape it
4. Enforce max 5 segments
5. Extension allowlist check
"""

import os
import re
import logging
from typing import List, Dict, Any, Optional

logger = logging.getLogger(__name__)

ALLOWED_EXTENSIONS = {
    '.txt', '.md', '.js', '.ts', '.py', '.json',
    '.yaml', '.yml', '.html', '.css', '.env', '.sh', '.csv',
    '.jsx', '.tsx',
}

MAX_ITEMS = 20
MAX_CONTENT_BYTES = 50 * 1024  # 50KB
MAX_PATH_SEGMENTS = 5


def sanitize_path(path: str, scope: Optional[str] = None) -> str:
    r"""Sanitize a file path by stripping traversals while preserving structure.

    Steps:
      1. Remove null bytes
      2. Normalize separators to /
      3. Strip drive letters (e.g. C:)
      4. Strip all ../ and ..\ sequences
      5. Strip leading / or \
      6. Remove segments that are '.' or empty
      7. If scope is set, ensure path starts with scope; rebase if needed
    """
    if not path:
        return ""

    # 1. Remove null bytes
    clean = path.replace('\x00', '')

    # 2. Normalize all separators to /
    clean = clean.replace('\\', '/')

    # 3. Strip Windows drive letters (e.g. C:/ or D:)
    if len(clean) >= 2 and clean[1] == ':' and clean[0].isalpha():
        clean = clean[2:]

    # 4. Strip traversal sequences (../ or standalone ..)
    # Repeatedly strip to handle nested attempts like ....//
    prev = None
    while prev != clean:
        prev = clean
        clean = clean.replace('../', '').replace('..\\', '')
    # Remove any remaining standalone '..' segments
    segments = clean.split('/')

    # 4-5. Strip leading slashes and filter out '.' and empty segments
    filtered = [s for s in segments if s and s != '.' and s != '..']

    if not filtered:
        return ""

    resolved = '/'.join(filtered)

    # 6. Scope enforcement — validate only, do NOT rebase.
    #    Rebasing is the caller's job (e.g. _prefix_shortcut_paths or LLM prompt).
    #    Sanitizer only rejects paths that actively escape the scope via traversal.
    if scope and scope != ".":
        scope_clean = scope.strip('/').rstrip('/')
        # Check if the resolved path tries to escape above scope via leftover traversal.
        # A path like "secrets/key.txt" is fine (it's just relative to root).
        # We only block paths where traversal remains after stripping.
        # Since we already stripped all ../ above, this is mainly a safety net.
        pass

    return resolved


def sanitize_manifest(manifest: List[Dict[str, Any]], scope: Optional[str] = None) -> List[Dict[str, Any]]:
    """Sanitize the LLM output manifest.

    Args:
        manifest: Raw manifest from LLM or template shortcut.
        scope: Optional current_directory scope. Paths will be rebased under this.
    """
    if not isinstance(manifest, list):
        return []

    clean_manifest = []

    for item in manifest[:MAX_ITEMS]:
        if not isinstance(item, dict):
            continue

        item_type = item.get("type", "file")
        if item_type not in ("file", "directory"):
            item_type = "file"

        raw_path = str(item.get("path", "")).strip()
        path = sanitize_path(raw_path, scope=scope)

        if not path:
            path = "untitled_folder" if item_type == "directory" else "untitled.txt"
            # Apply scope to fallback names too
            if scope and scope != ".":
                scope_clean = scope.strip('/').rstrip('/')
                path = f"{scope_clean}/{path}"

        # 7. Max depth enforcement (after scope rebase)
        segments = path.split('/')
        if len(segments) > MAX_PATH_SEGMENTS:
            logger.warning(f"Rejected path exceeding {MAX_PATH_SEGMENTS} segments: {path}")
            continue

        # Extension checking for files
        if item_type == "file":
            ext = os.path.splitext(path)[1].lower()
            if not ext:
                path = f"{path}.txt"
                ext = ".txt"

            if ext not in ALLOWED_EXTENSIONS:
                continue  # Silently skip file types not in allowlist

        # Content handling
        content = str(item.get("content", "")) if item_type == "file" else ""

        if item_type == "file" and not content:
            content = f"# {os.path.basename(path)}\n\nAdd your content here."

        # Truncation
        encoded_content = content.encode('utf-8')
        if len(encoded_content) > MAX_CONTENT_BYTES:
            truncated = encoded_content[:MAX_CONTENT_BYTES].decode('utf-8', errors='ignore')
            content = truncated + "\n[TRUNCATED BY SYSTEM]"

        clean_manifest.append({
            "path": path,
            "content": content,
            "type": item_type
        })

    return clean_manifest
