import sqlite3

from fastapi import APIRouter, Depends, UploadFile
from fastapi.responses import StreamingResponse

from app.dependencies import get_current_device
from app.models.file_models import (
    CreateFileRequest,
    DirectoryListing,
    FileContent,
    FileInfo,
    OperationResponse,
    UpdateFileRequest,
)
from app.services import vault
from app.services.version_tracker import VersionTracker

router = APIRouter(tags=["files"])


@router.post("/files/reset", response_model=OperationResponse)
async def reset_all_files(
    _device: dict = Depends(get_current_device),
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
    return vault.list_directory()


@router.post("/upload/{path:path}", response_model=FileInfo, status_code=201)
async def upload_file(
    path: str,
    file: UploadFile,
    _device: dict = Depends(get_current_device),
) -> FileInfo:
    return await vault.save_upload(path, file)


@router.get("/download/{path:path}")
async def download_file(
    path: str,
    _device: dict = Depends(get_current_device),
) -> StreamingResponse:
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
    return vault.get_path(path)


@router.post("/files/{path:path}", response_model=FileInfo, status_code=201)
async def create_path(
    path: str,
    body: CreateFileRequest,
    _device: dict = Depends(get_current_device),
) -> FileInfo:
    return vault.create_file(path, content=body.content, entry_type=body.type)


@router.put("/files/{path:path}", response_model=FileInfo)
async def update_path(
    path: str,
    body: UpdateFileRequest,
    _device: dict = Depends(get_current_device),
) -> FileInfo:
    return vault.update_file(path, content=body.content)


@router.delete("/files/{path:path}", response_model=OperationResponse)
async def delete_path(
    path: str,
    _device: dict = Depends(get_current_device),
) -> OperationResponse:
    vault.delete_path(path)
    
    return OperationResponse(message="Deleted successfully", path=path)
