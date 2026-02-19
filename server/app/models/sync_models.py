from __future__ import annotations

from datetime import datetime

from pydantic import BaseModel


class ManifestEntry(BaseModel):
    path: str
    content_hash: str
    last_modified: datetime


class ManifestRequest(BaseModel):
    manifest: list[ManifestEntry]


class SyncPathEntry(BaseModel):
    path: str


class ManifestResponse(BaseModel):
    to_push: list[SyncPathEntry]
    to_pull: list[SyncPathEntry]
    conflicts: list[SyncPathEntry]


class PushMetadata(BaseModel):
    path: str
    content_hash: str
    last_modified: datetime


class PushResultEntry(BaseModel):
    path: str


class PushResponse(BaseModel):
    accepted: list[PushResultEntry]
    conflicts: list[PushResultEntry]


class PullRequest(BaseModel):
    path: str
