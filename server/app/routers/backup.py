from __future__ import annotations

from datetime import datetime, timezone

from fastapi import APIRouter, Depends, HTTPException
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import FileResponse

from app.dependencies import require_admin
from app.services import backup

router = APIRouter(prefix="/backup", tags=["backup"])


def _describe(path) -> dict:
    stat = path.stat()
    return {
        "backup_id": path.stem,
        "filename": path.name,
        "size_bytes": stat.st_size,
        "created_at": datetime.fromtimestamp(stat.st_mtime, tz=timezone.utc).isoformat(),
    }


@router.get("")
async def list_backups(_admin: dict = Depends(require_admin)) -> dict:
    return {"backups": [_describe(p) for p in reversed(backup.list_backups())]}


@router.post("/export", status_code=201)
async def export_backup(_admin: dict = Depends(require_admin)) -> dict:
    path = await run_in_threadpool(backup.create_backup)
    return _describe(path)


@router.get("/download/{backup_id}")
async def download_backup(backup_id: str, _admin: dict = Depends(require_admin)) -> FileResponse:
    if not backup.BACKUP_ID_RE.match(backup_id):
        raise HTTPException(status_code=400, detail="Invalid backup id")
    path = backup.backup_dir() / f"{backup_id}.zip"
    if not path.is_file():
        raise HTTPException(status_code=404, detail="Backup not found")
    return FileResponse(path, media_type="application/zip", filename=path.name)
