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

            # Get or initialize version
            version = version_tracker.get_version(relative)
            if version is None:
                version = version_tracker.create_version(relative, content_hash)

            manifest[relative] = {
                "path": relative,
                "content_hash": content_hash,
                "last_modified": datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc),
                "version": version,
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
            # File only exists on client → push
            to_push.append(path)
            continue

        if entry["content_hash"] == server_entry["content_hash"]:
            # Hashes match → no sync needed
            continue

        # Hashes differ → check for version conflict
        client_base_version = entry.get("version")
        server_version = server_entry.get("version")

        if client_base_version is not None and server_version is not None:
            # Version-based conflict detection
            if client_base_version != server_version:
                # Client's base version doesn't match server → concurrent edit detected
                conflicts.append(path)
            else:
                # Versions match but hashes differ → client has newer changes
                to_push.append(path)
        else:
            # Fallback to timestamp-based logic for backward compatibility
            # (when client or server doesn't have version info yet)
            client_mtime = entry["last_modified"]
            server_mtime = server_entry["last_modified"]

            if isinstance(client_mtime, str):
                client_mtime = datetime.fromisoformat(client_mtime.replace("Z", "+00:00"))
            if not client_mtime.tzinfo:
                client_mtime = client_mtime.replace(tzinfo=timezone.utc)

            # Calculate absolute time difference in seconds
            time_diff_seconds = abs((client_mtime - server_mtime).total_seconds())
            tolerance_seconds = settings.sync_timestamp_tolerance_seconds

            # If timestamps are within tolerance and hashes differ → conflict
            if time_diff_seconds <= tolerance_seconds:
                conflicts.append(path)
            elif client_mtime > server_mtime:
                to_push.append(path)
            else:
                to_pull.append(path)

    for path in server_manifest:
        if path not in client_paths:
            # File only exists on server → pull
            to_pull.append(path)

    return sorted(to_push), sorted(to_pull), sorted(conflicts)


def _conflict_path(original_path: str) -> str:
    p = Path(original_path)
    ts = datetime.now(tz=timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    return str(p.with_stem(f"{p.stem}_conflict_{ts}")).replace("\\", "/")


def push_file(
    relative_path: str,
    file_data: bytes,
    client_last_modified: datetime,
    base_version: int | None = None,
) -> tuple[str, bool, int | None]:
    """
    Push a file to the vault.
    
    Returns:
        tuple of (path, is_conflict, new_version)
        - path: The path where the file was written (may be conflict path)
        - is_conflict: True if a conflict was detected
        - new_version: The new version number (None if conflict)
    """
    vault_root = settings.vault_path
    validated = validate_path(relative_path)
    target = resolve_vault_path(vault_root, validated)
    version_tracker = VersionTracker()

    parent = target.parent
    if not parent.exists():
        parent.mkdir(parents=True, exist_ok=True)

    if target.exists() and target.is_file():
        server_hash = _sha256_file(target)
        client_hash = _sha256(file_data)

        if server_hash == client_hash:
            # Hashes match → no write needed, but return current version
            version = version_tracker.get_version(validated)
            if version is None:
                version = version_tracker.create_version(validated, server_hash)
            return validated, False, version

        # Hashes differ → check for version conflict
        server_version = version_tracker.get_version(validated)
        
        if base_version is not None and server_version is not None:
            # Version-based conflict detection
            if base_version != server_version:
                # Concurrent edit detected → create conflict file
                conflict_rel = _conflict_path(validated)
                conflict_target = resolve_vault_path(vault_root, validate_path(conflict_rel))
                try:
                    conflict_target.write_bytes(file_data)
                except OSError as exc:
                    raise VaultIOError(f"Failed to write conflict file: {exc}") from exc
                
                # Create version tracking for conflict file (use upsert to avoid duplicates)
                conflict_hash = _sha256(file_data)
                version_tracker.upsert_version(conflict_rel, 1, conflict_hash)
                
                return conflict_rel, True, None
        else:
            # Fallback to timestamp-based conflict detection for backward compatibility
            server_mtime = datetime.fromtimestamp(
                target.stat().st_mtime, tz=timezone.utc
            )

            if not client_last_modified.tzinfo:
                client_last_modified = client_last_modified.replace(tzinfo=timezone.utc)

            # Calculate absolute time difference in seconds
            time_diff_seconds = abs((client_last_modified - server_mtime).total_seconds())
            tolerance_seconds = settings.sync_timestamp_tolerance_seconds

            # If timestamps are within tolerance and hashes differ → conflict
            if time_diff_seconds <= tolerance_seconds:
                conflict_rel = _conflict_path(validated)
                conflict_target = resolve_vault_path(vault_root, validate_path(conflict_rel))
                try:
                    conflict_target.write_bytes(file_data)
                except OSError as exc:
                    raise VaultIOError(f"Failed to write conflict file: {exc}") from exc
                
                # Create version tracking for conflict file (use upsert to avoid duplicates)
                conflict_hash = _sha256(file_data)
                version_tracker.upsert_version(conflict_rel, 1, conflict_hash)
                
                return conflict_rel, True, None

    # No conflict → write file and increment version
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
    new_version = version_tracker.increment_version(validated, new_hash)

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
