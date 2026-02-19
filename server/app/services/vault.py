from __future__ import annotations

import hashlib
import shutil
from datetime import datetime, timezone
from pathlib import Path
from typing import AsyncIterator

from fastapi import UploadFile

from app.config import settings
from app.errors import (
    FileTooLargeError,
    InvalidPathError,
    PathAlreadyExistsError,
    PathNotFoundError,
    VaultIOError,
)
from app.models.file_models import (
    DirectoryListing,
    EntryType,
    FileContent,
    FileInfo,
)
from app.services.path_validator import check_symlink, resolve_vault_path, validate_path

STREAM_CHUNK_SIZE = 64 * 1024


def _sha256(data: bytes) -> str:
    return f"sha256:{hashlib.sha256(data).hexdigest()}"


def _file_info(vault_root: Path, path: Path) -> FileInfo:
    relative = path.relative_to(vault_root.resolve())
    stat = path.stat()
    if path.is_dir():
        return FileInfo(
            name=path.name,
            path=str(relative).replace("\\", "/"),
            type=EntryType.DIRECTORY,
            last_modified=datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc),
        )
    return FileInfo(
        name=path.name,
        path=str(relative).replace("\\", "/"),
        type=EntryType.FILE,
        size_bytes=stat.st_size,
        last_modified=datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc),
        content_hash=_sha256(path.read_bytes()),
    )


def list_directory(relative_path: str | None = None) -> DirectoryListing:
    vault_root = settings.vault_path

    if relative_path is None:
        target = vault_root.resolve()
        display_path = ""
    else:
        target = resolve_vault_path(vault_root, relative_path)
        display_path = validate_path(relative_path)

    if not target.exists():
        raise PathNotFoundError(display_path or "/")
    if not target.is_dir():
        raise InvalidPathError(f"Path is not a directory: {display_path}")

    entries: list[FileInfo] = []
    for child in sorted(target.iterdir(), key=lambda p: (not p.is_dir(), p.name.lower())):
        if child.name.startswith(".") and child.name not in {".gitkeep", ".gitignore"}:
            continue
        if child.is_symlink():
            continue
        entries.append(_file_info(vault_root, child))

    return DirectoryListing(path=display_path or "/", entries=entries)


def read_file(relative_path: str) -> FileContent:
    vault_root = settings.vault_path
    target = resolve_vault_path(vault_root, relative_path)
    validated = validate_path(relative_path)

    if not target.exists():
        raise PathNotFoundError(validated)
    if target.is_dir():
        raise InvalidPathError(f"Path is a directory, not a file: {validated}")

    check_symlink(target)

    content_bytes = target.read_bytes()
    stat = target.stat()

    return FileContent(
        path=validated,
        name=target.name,
        content=content_bytes.decode("utf-8", errors="replace"),
        size_bytes=stat.st_size,
        last_modified=datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc),
        content_hash=_sha256(content_bytes),
    )


def get_path(relative_path: str) -> DirectoryListing | FileContent:
    vault_root = settings.vault_path
    target = resolve_vault_path(vault_root, relative_path)

    if not target.exists():
        raise PathNotFoundError(validate_path(relative_path))

    if target.is_dir():
        return list_directory(relative_path)
    return read_file(relative_path)


def create_file(relative_path: str, content: str = "", entry_type: EntryType = EntryType.FILE) -> FileInfo:
    vault_root = settings.vault_path
    target = resolve_vault_path(vault_root, relative_path)
    validated = validate_path(relative_path)

    if target.exists():
        raise PathAlreadyExistsError(validated)

    parent = target.parent
    if not parent.exists():
        raise PathNotFoundError(str(parent.relative_to(vault_root.resolve())).replace("\\", "/"))

    try:
        if entry_type == EntryType.DIRECTORY:
            target.mkdir(parents=False)
        else:
            target.write_text(content, encoding="utf-8")
    except OSError as exc:
        raise VaultIOError(f"Failed to create {validated}: {exc}") from exc

    return _file_info(vault_root, target)


def update_file(relative_path: str, content: str) -> FileInfo:
    vault_root = settings.vault_path
    target = resolve_vault_path(vault_root, relative_path)
    validated = validate_path(relative_path)

    if not target.exists():
        raise PathNotFoundError(validated)
    if target.is_dir():
        raise InvalidPathError(f"Cannot update a directory: {validated}")

    check_symlink(target)

    try:
        target.write_text(content, encoding="utf-8")
    except OSError as exc:
        raise VaultIOError(f"Failed to update {validated}: {exc}") from exc

    return _file_info(vault_root, target)


def delete_path(relative_path: str) -> None:
    vault_root = settings.vault_path
    target = resolve_vault_path(vault_root, relative_path)
    validated = validate_path(relative_path)

    if not target.exists():
        raise PathNotFoundError(validated)

    check_symlink(target)

    try:
        if target.is_dir():
            shutil.rmtree(target)
        else:
            target.unlink()
    except OSError as exc:
        raise VaultIOError(f"Failed to delete {validated}: {exc}") from exc


async def save_upload(relative_path: str, file: UploadFile) -> FileInfo:
    vault_root = settings.vault_path
    target = resolve_vault_path(vault_root, relative_path)
    validated = validate_path(relative_path)

    parent = target.parent
    if not parent.exists():
        raise PathNotFoundError(str(parent.relative_to(vault_root.resolve())).replace("\\", "/"))

    try:
        total_size = 0
        max_bytes = settings.max_upload_bytes
        with open(target, "wb") as f:
            while chunk := await file.read(STREAM_CHUNK_SIZE):
                total_size += len(chunk)
                if total_size > max_bytes:
                    f.close()
                    target.unlink(missing_ok=True)
                    raise FileTooLargeError(settings.max_upload_mb)
                f.write(chunk)
    except FileTooLargeError:
        raise
    except OSError as exc:
        raise VaultIOError(f"Failed to save upload to {validated}: {exc}") from exc

    return _file_info(vault_root, target)


async def stream_download(relative_path: str) -> tuple[str, int, AsyncIterator[bytes]]:
    vault_root = settings.vault_path
    target = resolve_vault_path(vault_root, relative_path)
    validated = validate_path(relative_path)

    if not target.exists():
        raise PathNotFoundError(validated)
    if target.is_dir():
        raise InvalidPathError(f"Cannot download a directory: {validated}")

    check_symlink(target)

    file_size = target.stat().st_size
    filename = target.name

    async def _stream() -> AsyncIterator[bytes]:
        with open(target, "rb") as f:
            while chunk := f.read(STREAM_CHUNK_SIZE):
                yield chunk

    return filename, file_size, _stream()
