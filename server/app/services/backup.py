"""Zip snapshots of the whole vault into settings.backup_path (outside the vault).

Restore = unzip over an empty vault folder (see HOW_TO_RUN.md). Secrets stay
protected inside: .jvs files are already AES-GCM ciphertext.
"""
from __future__ import annotations

import logging
import os
import re
import sqlite3
import threading
import time
import zipfile
from datetime import datetime, timezone
from pathlib import Path

from app import config

logger = logging.getLogger(__name__)

PREFIX = "jarvis-vault-"
BACKUP_ID_RE = re.compile(r"^jarvis-vault-\d{8}T\d{6}Z$")
_lock = threading.Lock()


def backup_dir() -> Path:
    return config.settings.backup_path


def _sqlite_snapshot(path: Path) -> bytes:
    # A raw file copy of a live SQLite DB can be torn mid-write; the backup API can't.
    src = sqlite3.connect(f"file:{path.as_posix()}?mode=ro", uri=True)
    dst = sqlite3.connect(":memory:")
    try:
        src.backup(dst)
        return dst.serialize()
    finally:
        src.close()
        dst.close()


def list_backups() -> list[Path]:
    d = backup_dir()
    if not d.exists():
        return []
    return sorted(p for p in d.glob(f"{PREFIX}*.zip") if BACKUP_ID_RE.match(p.stem))


def create_backup() -> Path:
    with _lock:
        dest = backup_dir()
        dest.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now(tz=timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        final = dest / f"{PREFIX}{stamp}.zip"
        partial = final.with_name(final.name + ".partial")
        root = config.settings.vault_path.resolve()
        dest_resolved = dest.resolve()

        with zipfile.ZipFile(partial, "w", zipfile.ZIP_DEFLATED) as zf:
            for dirpath, dirnames, filenames in os.walk(root):
                here = Path(dirpath)
                # Never back up the backups, even if someone points backup_path into the vault.
                dirnames[:] = [d for d in dirnames if (here / d).resolve() != dest_resolved]
                for name in filenames:
                    p = here / name
                    if p.is_symlink():
                        continue
                    arcname = p.relative_to(root).as_posix()
                    try:
                        if p.suffix == ".db":
                            zf.writestr(arcname, _sqlite_snapshot(p))
                        else:
                            zf.write(p, arcname)
                    except (FileNotFoundError, sqlite3.DatabaseError):
                        # Deleted mid-walk, or a ".db" that isn't SQLite: fall back to bytes.
                        if p.exists():
                            zf.write(p, arcname)

        partial.replace(final)
        _prune()
        logger.info("Vault backup written: %s (%d bytes)", final, final.stat().st_size)
        return final


def _prune() -> None:
    keep = max(config.settings.backup_keep, 1)
    for old in list_backups()[:-keep]:
        old.unlink(missing_ok=True)


def is_due() -> bool:
    hours = config.settings.backup_interval_hours
    if hours <= 0:
        return False
    existing = list_backups()
    if not existing:
        return True
    return time.time() - existing[-1].stat().st_mtime >= hours * 3600
