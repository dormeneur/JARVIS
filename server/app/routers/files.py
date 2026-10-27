import sqlite3

from fastapi import APIRouter, Depends, UploadFile
from fastapi.responses import StreamingResponse

from app.dependencies import ensure_path_access, get_current_device, require_admin
from app.models.file_models import (
    CreateFileRequest,
    DirectoryListing,
    FileContent,
    FileInfo,
    OperationResponse,
    UpdateFileRequest,
)
from app.services import vault
from app.services.path_validator import is_secrets_path
from app.services.version_tracker import VersionTracker

router = APIRouter(tags=["files"])


@router.post("/files/reset", response_model=OperationResponse)
async def reset_all_files(
    _device: dict = Depends(require_admin),
) -> OperationResponse:
    """Delete all user files from the vault and reset version tracking."""
    deleted_count = vault.reset_vault()

    # Wipe all version tracking entries
    tracker = VersionTracker()
    with sqlite3.connect(tracker.db_path) as conn:
        conn.execute("DELETE FROM file_versions")
        conn.commit()

    return OperationResponse(
        message=f"Reset complete. {deleted_count} items deleted.",
        path="/",
    )


@router.get("/files", response_model=DirectoryListing)
async def list_root(_device: dict = Depends(get_current_device)) -> DirectoryListing:
    listing = vault.list_directory()

    # Filter out Secrets/ for devices that are not secrets-authorized
    if not _device.get("is_secrets_authorized", False):
        listing = DirectoryListing(
            path=listing.path,
            entries=[
                e for e in listing.entries
                if not is_secrets_path(e.path)
            ],
        )

    return listing


@router.post("/upload/{path:path}", response_model=FileInfo, status_code=201)
async def upload_file(
    path: str,
    file: UploadFile,
    _device: dict = Depends(get_current_device),
) -> FileInfo:
    ensure_path_access(path, _device)
    return await vault.save_upload(path, file)


@router.get("/download/{path:path}")
async def download_file(
    path: str,
    _device: dict = Depends(get_current_device),
) -> StreamingResponse:
    ensure_path_access(path, _device)
    filename, file_size, stream = await vault.stream_download(path)
    return StreamingResponse(
        stream,
        media_type="application/octet-stream",
        headers={
            "Content-Disposition": f'attachment; filename="{filename}"',
            "Content-Length": str(file_size),
        },
    )


@router.get("/files/{path:path}", response_model=DirectoryListing | FileContent)
async def get_path(
    path: str,
    _device: dict = Depends(get_current_device),
) -> DirectoryListing | FileContent:
    ensure_path_access(path, _device)
    result = vault.get_path(path)
    if isinstance(result, DirectoryListing) and not _device.get("is_secrets_authorized", False):
        result = DirectoryListing(
            path=result.path,
            entries=[
                e for e in result.entries
                if not is_secrets_path(e.path)
            ],
        )
    return result


@router.post("/files/{path:path}", response_model=FileInfo, status_code=201)
async def create_path(
    path: str,
    body: CreateFileRequest,
    _device: dict = Depends(get_current_device),
) -> FileInfo:
    ensure_path_access(path, _device)
    return vault.create_file(path, content=body.content, entry_type=body.type)


@router.put("/files/{path:path}", response_model=FileInfo)
async def update_path(
    path: str,
    body: UpdateFileRequest,
    _device: dict = Depends(get_current_device),
) -> FileInfo:
    ensure_path_access(path, _device)
    return vault.update_file(path, content=body.content)


@router.delete("/files/{path:path}", response_model=OperationResponse)
async def delete_path(
    path: str,
    _device: dict = Depends(get_current_device),
) -> OperationResponse:
    ensure_path_access(path, _device)
    vault.delete_path(path)
    return OperationResponse(message="Deleted successfully", path=path)
