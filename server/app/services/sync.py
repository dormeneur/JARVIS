from __future__ import annotations

import hashlib
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import AsyncIterator

from app.config import settings
from app.errors import InvalidPathError, PathNotFoundError, VaultIOError
from app.services.path_validator import validate_path, resolve_vault_path, check_symlink

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

            manifest[relative] = {
                "path": relative,
                "content_hash": _sha256_file(fpath),
                "last_modified": datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc),
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
            to_push.append(path)
            continue

        if entry["content_hash"] == server_entry["content_hash"]:
            continue

        client_mtime = entry["last_modified"]
        server_mtime = server_entry["last_modified"]

        if isinstance(client_mtime, str):
            client_mtime = datetime.fromisoformat(client_mtime.replace("Z", "+00:00"))
        if not client_mtime.tzinfo:
            client_mtime = client_mtime.replace(tzinfo=timezone.utc)

        if client_mtime > server_mtime:
            to_push.append(path)
        elif server_mtime > client_mtime:
            to_pull.append(path)
        else:
            conflicts.append(path)

    for path in server_manifest:
        if path not in client_paths:
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
) -> tuple[str, bool]:
    vault_root = settings.vault_path
    validated = validate_path(relative_path)
    target = resolve_vault_path(vault_root, validated)

    parent = target.parent
    if not parent.exists():
        parent.mkdir(parents=True, exist_ok=True)

    if target.exists() and target.is_file():
        server_hash = _sha256_file(target)
        client_hash = _sha256(file_data)

        if server_hash == client_hash:
            return validated, False

        server_mtime = datetime.fromtimestamp(
            target.stat().st_mtime, tz=timezone.utc
        )

        if not client_last_modified.tzinfo:
            client_last_modified = client_last_modified.replace(tzinfo=timezone.utc)

        if client_last_modified > server_mtime:
            pass
        elif server_mtime > client_last_modified:
            pass
        else:
            conflict_rel = _conflict_path(validated)
            conflict_target = resolve_vault_path(vault_root, validate_path(conflict_rel))
            try:
                conflict_target.write_bytes(file_data)
            except OSError as exc:
                raise VaultIOError(f"Failed to write conflict file: {exc}") from exc
            return conflict_rel, True

    try:
        target.write_bytes(file_data)
    except OSError as exc:
        raise VaultIOError(f"Failed to write file during push: {exc}") from exc

    try:
        mtime_ts = client_last_modified.timestamp()
        os.utime(target, (mtime_ts, mtime_ts))
    except OSError:
        pass

    return validated, False


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
