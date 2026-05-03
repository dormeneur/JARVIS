from __future__ import annotations

import hashlib
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import AsyncIterator

from app.config import settings
from app.errors import InvalidPathError, PathNotFoundError, VaultIOError
from app.services.path_validator import validate_path, resolve_vault_path, check_symlink
from app.services.version_tracker import VersionTracker

STREAM_CHUNK_SIZE = 64 * 1024
SYSTEM_DIR = "system"

ALLOWED_HIDDEN = {".gitkeep", ".gitignore"}


def _sha256(data: bytes) -> str:
    return f"sha256:{hashlib.sha256(data).hexdigest()}"


def _sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(STREAM_CHUNK_SIZE):
            h.update(chunk)
    return f"sha256:{h.hexdigest()}"


def _is_skippable(name: str) -> bool:
    if name == SYSTEM_DIR:
        return True
    if name.startswith(".") and name not in ALLOWED_HIDDEN:
        return True
    return False


def build_server_manifest() -> dict[str, dict]:
    vault_root = settings.vault_path.resolve()
    manifest: dict[str, dict] = {}
    version_tracker = VersionTracker()

    for dirpath, dirnames, filenames in os.walk(vault_root):
        dirnames[:] = [
            d for d in dirnames
            if not _is_skippable(d) and not Path(dirpath, d).is_symlink()
        ]

        for fname in filenames:
            if _is_skippable(fname):
                continue

            fpath = Path(dirpath) / fname
            if fpath.is_symlink():
                continue

            relative = str(fpath.relative_to(vault_root)).replace("\\", "/")
            stat = fpath.stat()
            content_hash = _sha256_file(fpath)

            # Get or initialize version and tracked hash
            version_info = version_tracker.get_version_and_hash(relative)
            if version_info is None:
                # File exists on disk but was never tracked — initialize and
                # bump to version 2 so any mobile client at version 1 will
                # see a mismatch and trigger a conflict instead of a silent push.
                version_tracker.create_version(relative, content_hash)
                version = version_tracker.increment_version(relative, content_hash)
                tracked_hash = content_hash
                prev_hash = None
            else:
                version, tracked_hash = version_info
                prev_hash = version_tracker.get_prev_hash(relative)

                # Detect files modified on disk outside the API (e.g. direct
                # filesystem edits from a laptop).  The tracked_hash is what
                # the version_tracker last recorded.  If the on-disk content
                # differs, bump the version so mobile sees the change.
                if content_hash != tracked_hash:
                    version = version_tracker.increment_version(relative, content_hash)
                    prev_hash = tracked_hash  # old hash becomes prev_hash
                    tracked_hash = content_hash

            manifest[relative] = {
                "path": relative,
                "content_hash": content_hash,
                "last_modified": datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc),
                "version": version,
                "tracked_hash": tracked_hash,
                "prev_hash": prev_hash,
            }

    return manifest


def diff_manifests(
    client_entries: list[dict],
    server_manifest: dict[str, dict],
) -> tuple[list[str], list[str], list[str]]:
    to_push: list[str] = []
    to_pull: list[str] = []
    conflicts: list[str] = []

    client_paths = set()

    for entry in client_entries:
        path = entry["path"]
        client_paths.add(path)

        server_entry = server_manifest.get(path)

        if server_entry is None:
            if entry.get('has_local_changes', False):
                # Client created or modified offline — push to server
                to_push.append(path)
            else:
                # File exists on client but not server — server deleted it.
                # Append to to_pull so the client's 404 handler removes the local copy.
                to_pull.append(path)
            continue

        if entry["content_hash"] == server_entry["content_hash"]:
            # Hashes match -> no sync needed
            continue

        # Content differs — use version numbers to determine who changed.
        client_hash = entry["content_hash"]
        client_version = entry.get("version")
        server_version = server_entry.get("version")
        prev_hash = server_entry.get("prev_hash")
        has_local_changes = entry.get("has_local_changes")

        if client_version is not None and server_version is not None:
            if client_version == server_version:
                # Versions equal but content differs → only mobile changed
                # (mobile edited locally but hasn't pushed yet)
                to_push.append(path)

            elif client_version < server_version:
                # Server is ahead — it was updated since mobile last synced.
                # Did mobile also change?
                if has_local_changes is False:
                    # Mobile explicitly reports no local changes → pull
                    to_pull.append(path)
                elif has_local_changes is True:
                    # Mobile has local changes AND server is ahead → conflict
                    conflicts.append(path)
                else:
                    # Flag not available — fall back to prev_hash comparison
                    if prev_hash is not None and client_hash == prev_hash:
                        to_pull.append(path)
                    elif client_hash == server_entry["content_hash"]:
                        # Mobile somehow already has the new content
                        pass
                    else:
                        conflicts.append(path)

            else:
                # client_version > server_version: abnormal, push to recover
                to_push.append(path)
        else:
            # No version info → fall back to safe conflict
            conflicts.append(path)

    for path in server_manifest:
        if path not in client_paths:
            # File only exists on server -> pull
            to_pull.append(path)

    return sorted(to_push), sorted(to_pull), sorted(conflicts)


def push_file(
    relative_path: str,
    file_data: bytes,
    client_last_modified: datetime,
    base_version: int | None = None,
) -> tuple[str, bool, int | None]:
    """
    Push a file to the vault.

    Returns:
        tuple of (path, is_conflict, version)
        - path: The original file path (never a conflict path)
        - is_conflict: True if a concurrent edit was detected
        - version: Current server version if conflict, new version if success
    """
    vault_root = settings.vault_path
    validated = validate_path(relative_path)
    target = resolve_vault_path(vault_root, validated)
    version_tracker = VersionTracker()

    parent = target.parent
    if not parent.exists():
        parent.mkdir(parents=True, exist_ok=True)

    file_existed = target.exists() and target.is_file()

    if file_existed:
        server_hash = _sha256_file(target)
        client_hash = _sha256(file_data)

        if server_hash == client_hash:
            # Hashes match -> no write needed, return current version
            version = version_tracker.get_version(validated)
            if version is None:
                version = version_tracker.create_version(validated, server_hash)
            return validated, False, version

        # Hashes differ -> check for version conflict
        server_version = version_tracker.get_version(validated)

        # Conflict detection — no files are written to disk.
        # The client stores the local snapshot in SQLite and handles
        # resolution entirely on the mobile side.
        if base_version is not None:
            server_version_for_check = server_version if server_version is not None else 0

            if base_version != server_version_for_check:
                # Concurrent edit detected — return conflict with server version
                return validated, True, server_version_for_check or 1

            # Safety net: even when versions match, check if the server file
            # was modified outside the push flow (e.g. direct disk edit or
            # files API without version tracking).
            tracked_info = version_tracker.get_version_and_hash(validated)
            if tracked_info is None:
                # File exists on disk but has no version tracking entry.
                # We can't verify its history → treat as conflict to be safe.
                current_ver = server_version if server_version is not None else 1
                return validated, True, current_ver
            else:
                _, tracked_hash = tracked_info
                if tracked_hash != server_hash:
                    # Server file differs from what version_tracker recorded
                    # -> file was modified outside the sync flow -> conflict
                    current_ver = server_version if server_version is not None else 1
                    return validated, True, current_ver
        else:
            # Fallback: timestamp-based conflict detection
            server_mtime = datetime.fromtimestamp(
                target.stat().st_mtime, tz=timezone.utc
            )
            if not client_last_modified.tzinfo:
                client_last_modified = client_last_modified.replace(tzinfo=timezone.utc)

            time_diff_seconds = abs((client_last_modified - server_mtime).total_seconds())
            tolerance_seconds = settings.sync_timestamp_tolerance_seconds

            if time_diff_seconds <= tolerance_seconds:
                current_ver = server_version if server_version is not None else 1
                return validated, True, current_ver

    # No conflict -> write file and increment version
    try:
        target.write_bytes(file_data)
    except OSError as exc:
        raise VaultIOError(f"Failed to write file during push: {exc}") from exc

    try:
        mtime_ts = client_last_modified.timestamp()
        os.utime(target, (mtime_ts, mtime_ts))
    except OSError:
        pass

    # Update version tracking
    new_hash = _sha256(file_data)
    if file_existed:
        # Existing file that passed conflict checks → increment
        new_version = version_tracker.increment_version(validated, new_hash)
    else:
        # Brand-new file → create at version 1
        new_version = version_tracker.create_version(validated, new_hash)

    return validated, False, new_version


async def pull_file(relative_path: str) -> tuple[str, int, AsyncIterator[bytes]]:
    vault_root = settings.vault_path
    validated = validate_path(relative_path)
    target = resolve_vault_path(vault_root, validated)

    if not target.exists():
        raise PathNotFoundError(validated)
    if target.is_dir():
        raise InvalidPathError(f"Cannot pull a directory: {validated}")

    check_symlink(target)

    file_size = target.stat().st_size
    filename = target.name

    async def _stream() -> AsyncIterator[bytes]:
        with open(target, "rb") as f:
            while chunk := f.read(STREAM_CHUNK_SIZE):
                yield chunk

    return filename, file_size, _stream()
