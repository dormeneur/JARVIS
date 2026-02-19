from __future__ import annotations

import json

from fastapi import APIRouter, Depends, Form, UploadFile
from fastapi.responses import StreamingResponse

from app.dependencies import get_current_device
from app.models.sync_models import (
    ManifestEntry,
    ManifestRequest,
    ManifestResponse,
    PullRequest,
    PushMetadata,
    PushResponse,
    PushResultEntry,
    SyncPathEntry,
)
from app.services import sync
from app.services.path_validator import validate_path

router = APIRouter(prefix="/sync", tags=["sync"])


@router.post("/manifest", response_model=ManifestResponse)
async def sync_manifest(
    body: ManifestRequest,
    _device: dict = Depends(get_current_device),
) -> ManifestResponse:
    for entry in body.manifest:
        validate_path(entry.path)

    server_manifest = sync.build_server_manifest()

    client_entries = [
        {
            "path": e.path,
            "content_hash": e.content_hash,
            "last_modified": e.last_modified,
        }
        for e in body.manifest
    ]

    to_push, to_pull, conflicts = sync.diff_manifests(client_entries, server_manifest)

    return ManifestResponse(
        to_push=[SyncPathEntry(path=p) for p in to_push],
        to_pull=[SyncPathEntry(path=p) for p in to_pull],
        conflicts=[SyncPathEntry(path=p) for p in conflicts],
    )


@router.post("/push", response_model=PushResponse)
async def sync_push(
    metadata: str = Form(...),
    file: UploadFile = None,
    _device: dict = Depends(get_current_device),
) -> PushResponse:
    meta = PushMetadata.model_validate_json(metadata)
    validate_path(meta.path)

    file_data = b""
    if file is not None:
        file_data = await file.read()

    result_path, is_conflict = sync.push_file(
        relative_path=meta.path,
        file_data=file_data,
        client_last_modified=meta.last_modified,
    )

    if is_conflict:
        return PushResponse(
            accepted=[],
            conflicts=[PushResultEntry(path=result_path)],
        )

    return PushResponse(
        accepted=[PushResultEntry(path=result_path)],
        conflicts=[],
    )


@router.post("/pull")
async def sync_pull(
    body: PullRequest,
    _device: dict = Depends(get_current_device),
) -> StreamingResponse:
    validate_path(body.path)
    filename, file_size, stream = await sync.pull_file(body.path)

    return StreamingResponse(
        stream,
        media_type="application/octet-stream",
        headers={
            "Content-Disposition": f'attachment; filename="{filename}"',
            "Content-Length": str(file_size),
        },
    )
