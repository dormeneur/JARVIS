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
from app.services.version_tracker import VersionTracker

router = APIRouter(prefix="/sync", tags=["sync"])


@router.post("/manifest", response_model=ManifestResponse)
async def sync_manifest(
    body: ManifestRequest,
    _device: dict = Depends(get_current_device),
) -> ManifestResponse:
    for entry in body.manifest:
        validate_path(entry.path)

    server_manifest = sync.build_server_manifest()

    # Filter out Secrets/ paths for devices that are not secrets-authorized.
    # Authorization state is read live from the registry in get_current_device.
    if not _device.get("is_secrets_authorized", False):
        server_manifest = {
            p: e for p, e in server_manifest.items()
            if not p.startswith("Secrets/")
        }

    client_entries = [
        {
            "path": e.path,
            "content_hash": e.content_hash,
            "last_modified": e.last_modified,
            "version": e.version,
            "has_local_changes": e.has_local_changes,
        }
        for e in body.manifest
    ]

    to_push, to_pull, conflicts = sync.diff_manifests(client_entries, server_manifest)

    return ManifestResponse(
        to_push=[SyncPathEntry(path=p) for p in to_push],
        to_pull=[SyncPathEntry(path=p) for p in to_pull],
        conflicts=[
            SyncPathEntry(path=p, version=server_manifest[p].get("version"))
            for p in conflicts
        ],
    )


@router.post("/push", response_model=PushResponse)
async def sync_push(
    metadata: str = Form(...),
    file: UploadFile = None,
    _device: dict = Depends(get_current_device),
) -> PushResponse:
    meta = PushMetadata.model_validate_json(metadata)
    validate_path(meta.path)

    # Secrets/ paths are restricted to secrets-authorized devices only.
    if meta.path.startswith("Secrets/") and not _device.get("is_secrets_authorized", False):
        from fastapi import HTTPException
        raise HTTPException(status_code=403, detail="Unauthorized to push to Secrets directory")

    file_data = b""
    if file is not None:
        file_data = await file.read()

    result_path, is_conflict, new_version = sync.push_file(
        relative_path=meta.path,
        file_data=file_data,
        client_last_modified=meta.last_modified,
        base_version=meta.base_version,
    )

    if is_conflict:
        return PushResponse(
            accepted=[],
            conflicts=[PushResultEntry(path=result_path, version=new_version)],
        )

    return PushResponse(
        accepted=[PushResultEntry(path=result_path, version=new_version)],
        conflicts=[],
    )


@router.post("/pull")
async def sync_pull(
    body: PullRequest,
    _device: dict = Depends(get_current_device),
) -> StreamingResponse:
    validate_path(body.path)

    # Secrets/ paths are restricted to secrets-authorized devices only.
    if body.path.startswith("Secrets/") and not _device.get("is_secrets_authorized", False):
        from fastapi import HTTPException
        raise HTTPException(status_code=403, detail="Unauthorized to pull from Secrets directory")

    filename, file_size, stream = await sync.pull_file(body.path)

    # Look up current server version for this file
    tracker = VersionTracker()
    version = tracker.get_version(body.path) or 1

    return StreamingResponse(
        stream,
        media_type="application/octet-stream",
        headers={
            "Content-Disposition": f'attachment; filename="{filename}"',
            "Content-Length": str(file_size),
            "X-File-Version": str(version),
        },
    )
