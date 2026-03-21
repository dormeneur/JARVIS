from __future__ import annotations

from datetime import datetime

from pydantic import BaseModel


class ManifestEntry(BaseModel):
    path: str
    content_hash: str
    last_modified: datetime
    version: int | None = None  # Server version number for conflict detection
    has_local_changes: bool | None = None  # True if mobile has local edits for this file


class ManifestRequest(BaseModel):
    manifest: list[ManifestEntry]


class SyncPathEntry(BaseModel):
    path: str
    version: int | None = None  # Server version (for conflict resolution)


class ManifestResponse(BaseModel):
    to_push: list[SyncPathEntry]
    to_pull: list[SyncPathEntry]
    conflicts: list[SyncPathEntry]


class PushMetadata(BaseModel):
    path: str
    content_hash: str
    last_modified: datetime
    base_version: int | None = None  # Client's known server version


class PushResultEntry(BaseModel):
    path: str
    version: int | None = None  # New server version after successful push


class PushResponse(BaseModel):
    accepted: list[PushResultEntry]
    conflicts: list[PushResultEntry]


class PullRequest(BaseModel):
    path: str
