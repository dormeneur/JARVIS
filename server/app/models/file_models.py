from __future__ import annotations

from datetime import datetime
from enum import Enum

from pydantic import BaseModel


class EntryType(str, Enum):
    FILE = "file"
    DIRECTORY = "directory"


class FileInfo(BaseModel):
    name: str
    path: str
    type: EntryType
    size_bytes: int | None = None
    last_modified: datetime | None = None
    content_hash: str | None = None


class DirectoryListing(BaseModel):
    path: str
    entries: list[FileInfo]


class FileContent(BaseModel):
    path: str
    name: str
    content: str
    size_bytes: int
    last_modified: datetime
    content_hash: str


class CreateFileRequest(BaseModel):
    content: str = ""
    type: EntryType = EntryType.FILE


class UpdateFileRequest(BaseModel):
    content: str


class OperationResponse(BaseModel):
    message: str
    path: str
