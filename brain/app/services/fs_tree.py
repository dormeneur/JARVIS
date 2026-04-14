"""Filesystem tree snapshot service for LLM context injection."""

import json
import logging
import os
from pathlib import Path
from typing import List

from app.config import settings

logger = logging.getLogger(__name__)

# Persisted snapshot location (inside the brain container at /app/data/)
_SNAPSHOT_DIR = Path("/app/data")
_SNAPSHOT_PATH = _SNAPSHOT_DIR / "fs_tree.json"

MAX_PATHS = 500


def build_fs_tree(vault_path: Path = None, max_paths: int = MAX_PATHS) -> List[str]:
    """Walk the vault directory and collect relative paths (files + dirs).

    Returns a sorted flat list capped at *max_paths* entries.
    """
    if vault_path is None:
        vault_path = settings.vault_path

    if not vault_path.exists():
        logger.warning(f"Vault path does not exist: {vault_path}")
        return []

    paths: List[str] = []
    try:
        for root, dirs, files in os.walk(vault_path):
            # Skip hidden directories
            dirs[:] = [d for d in dirs if not d.startswith('.')]

            rel_root = os.path.relpath(root, vault_path).replace("\\", "/")
            if rel_root != ".":
                paths.append(rel_root + "/")

            for fname in files:
                if fname.startswith('.'):
                    continue
                if rel_root == ".":
                    paths.append(fname)
                else:
                    paths.append(f"{rel_root}/{fname}")

                if len(paths) >= max_paths:
                    break
            if len(paths) >= max_paths:
                break
    except OSError as e:
        logger.error(f"Error walking vault: {e}")
        return []

    paths.sort()
    return paths[:max_paths]


def persist_fs_tree(tree: List[str]) -> None:
    """Write the tree snapshot to disk."""
    _SNAPSHOT_DIR.mkdir(parents=True, exist_ok=True)
    try:
        _SNAPSHOT_PATH.write_text(json.dumps(tree, indent=2), encoding="utf-8")
        logger.info(f"Persisted fs_tree snapshot with {len(tree)} paths")
    except OSError as e:
        logger.error(f"Failed to persist fs_tree: {e}")


def load_fs_tree() -> List[str]:
    """Read the persisted snapshot, returning [] if missing."""
    if not _SNAPSHOT_PATH.exists():
        return []
    try:
        data = json.loads(_SNAPSHOT_PATH.read_text(encoding="utf-8"))
        if isinstance(data, list):
            return data
    except (json.JSONDecodeError, OSError) as e:
        logger.warning(f"Failed to load fs_tree snapshot: {e}")
    return []


def refresh_fs_tree() -> List[str]:
    """Build and persist a fresh snapshot. Returns the tree."""
    tree = build_fs_tree()
    persist_fs_tree(tree)
    return tree
