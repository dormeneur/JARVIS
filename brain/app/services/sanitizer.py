import os
import re
from typing import List, Dict, Any

ALLOWED_EXTENSIONS = {
    '.txt', '.md', '.js', '.ts', '.py', '.json', 
    '.yaml', '.yml', '.html', '.css', '.env', '.sh', '.csv'
}

MAX_ITEMS = 20
MAX_CONTENT_BYTES = 50 * 1024  # 50KB

def sanitize_path(path: str) -> str:
    """Strip out dir traversal, absolute paths, and null bytes, flattening to one level."""
    # 1. Remove null bytes
    if not path:
        return ""
    clean = path.replace('\x00', '')
    
    # 2. Get the basename to flatten deep nesting
    basename = os.path.basename(clean.replace('\\', '/'))
    
    # 3. Strip any weird leading dots or slashes just in case
    basename = basename.lstrip('/\\')
    
    return basename

def sanitize_manifest(manifest: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """Sanitize the LLM output manifest."""
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
        path = sanitize_path(raw_path)
        
        if not path:
            path = "untitled_folder" if item_type == "directory" else "untitled.txt"
            
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
            content = f"# {path}\n\nAdd your content here."
            
        # Truncation
        encoded_content = content.encode('utf-8')
        if len(encoded_content) > MAX_CONTENT_BYTES:
            # truncate nicely by character length guess, then exact bytes
            # We'll just truncate string to a safe char limit and append
            # Wait, best to truncate bytes and ignore errors
            truncated = encoded_content[:MAX_CONTENT_BYTES].decode('utf-8', errors='ignore')
            content = truncated + "\n[TRUNCATED BY SYSTEM]"

        clean_manifest.append({
            "path": path,
            "content": content,
            "type": item_type
        })
        
    return clean_manifest
