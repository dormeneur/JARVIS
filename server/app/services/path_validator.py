from pathlib import Path, PurePosixPath

from app.errors import InvalidPathError, PathTraversalError

ALLOWED_DOT_ENTRIES = frozenset({".gitkeep", ".gitignore"})
MAX_PATH_DEPTH = 10
MAX_FILENAME_LENGTH = 255
MAX_PATH_LENGTH = 500


def validate_path(relative_path: str) -> str:
    if not relative_path:
        raise InvalidPathError("Path must not be empty")

    if len(relative_path) > MAX_PATH_LENGTH:
        raise InvalidPathError(f"Path exceeds maximum length of {MAX_PATH_LENGTH} characters")

    normalized = relative_path.replace("\\", "/")

    if normalized.startswith("/"):
        raise PathTraversalError(relative_path)

    parts = PurePosixPath(normalized).parts

    if len(parts) > MAX_PATH_DEPTH:
        raise InvalidPathError(f"Path exceeds maximum depth of {MAX_PATH_DEPTH} levels")

    for part in parts:
        if part == "..":
            raise PathTraversalError(relative_path)

        if len(part) > MAX_FILENAME_LENGTH:
            raise InvalidPathError(f"Filename exceeds maximum length of {MAX_FILENAME_LENGTH} characters")

        if part.startswith(".") and part not in ALLOWED_DOT_ENTRIES:
            raise InvalidPathError(f"Hidden files or directories are not allowed: {part}")

    return normalized


def resolve_vault_path(vault_root: Path, relative_path: str) -> Path:
    validated = validate_path(relative_path)
    target = (vault_root / validated).resolve()
    vault_resolved = vault_root.resolve()

    if not str(target).startswith(str(vault_resolved)):
        raise PathTraversalError(relative_path)

    if target.is_symlink():
        raise InvalidPathError("Symlinks are not allowed")

    return target


def check_symlink(path: Path) -> None:
    if path.is_symlink():
        raise InvalidPathError("Symlinks are not allowed")
    for parent in path.parents:
        if parent.is_symlink():
            raise InvalidPathError("Symlinks in path hierarchy are not allowed")
