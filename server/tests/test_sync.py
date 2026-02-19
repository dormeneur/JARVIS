import hashlib
import io
import json
import os
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

import pytest
from fastapi.testclient import TestClient


def sha256hex(data: bytes) -> str:
    return f"sha256:{hashlib.sha256(data).hexdigest()}"


# === Sync Service Unit Tests ===


class TestBuildServerManifest:
    def test_builds_manifest_from_vault(self, vault_settings, tmp_vault):
        from app.services import sync
        manifest = sync.build_server_manifest()
        assert "readme.md" in manifest
        assert "Personal/notes.md" in manifest
        assert "Work/project.md" in manifest

    def test_excludes_system_directory(self, vault_settings, tmp_vault):
        from app.services import sync
        (tmp_vault / "system").mkdir(exist_ok=True)
        (tmp_vault / "system" / "devices.json").write_text("{}", encoding="utf-8")
        manifest = sync.build_server_manifest()
        assert "system/devices.json" not in manifest

    def test_excludes_hidden_files(self, vault_settings, tmp_vault):
        from app.services import sync
        (tmp_vault / ".secret").write_text("hidden", encoding="utf-8")
        manifest = sync.build_server_manifest()
        assert ".secret" not in manifest

    def test_includes_gitkeep(self, vault_settings, tmp_vault):
        from app.services import sync
        (tmp_vault / ".gitkeep").write_text("", encoding="utf-8")
        manifest = sync.build_server_manifest()
        assert ".gitkeep" in manifest

    def test_manifest_entry_has_hash_and_mtime(self, vault_settings, tmp_vault):
        from app.services import sync
        manifest = sync.build_server_manifest()
        entry = manifest["readme.md"]
        assert entry["content_hash"].startswith("sha256:")
        assert isinstance(entry["last_modified"], datetime)

    def test_empty_vault_returns_empty(self, vault_settings, tmp_vault):
        from app.services import sync
        for item in tmp_vault.iterdir():
            if item.is_dir():
                import shutil
                shutil.rmtree(item)
            else:
                item.unlink()
        manifest = sync.build_server_manifest()
        assert manifest == {}


class TestDiffManifests:
    def test_client_only_files_to_push(self, vault_settings):
        from app.services import sync
        client = [
            {"path": "new.md", "content_hash": "sha256:abc", "last_modified": "2026-01-01T00:00:00Z"},
        ]
        server = {}
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert "new.md" in to_push
        assert to_pull == []
        assert conflicts == []

    def test_server_only_files_to_pull(self, vault_settings):
        from app.services import sync
        client = []
        server = {
            "server_file.md": {
                "path": "server_file.md",
                "content_hash": "sha256:xyz",
                "last_modified": datetime(2026, 1, 1, tzinfo=timezone.utc),
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert "server_file.md" in to_pull
        assert conflicts == []

    def test_matching_hashes_ignored(self, vault_settings):
        from app.services import sync
        same_hash = "sha256:samehash"
        client = [
            {"path": "file.md", "content_hash": same_hash, "last_modified": "2026-01-01T00:00:00Z"},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": same_hash,
                "last_modified": datetime(2026, 1, 1, tzinfo=timezone.utc),
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert to_pull == []
        assert conflicts == []

    def test_client_newer_to_push(self, vault_settings):
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:new", "last_modified": "2026-02-01T00:00:00Z"},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:old",
                "last_modified": datetime(2026, 1, 1, tzinfo=timezone.utc),
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert "file.md" in to_push
        assert to_pull == []

    def test_server_newer_to_pull(self, vault_settings):
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:old", "last_modified": "2026-01-01T00:00:00Z"},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:new",
                "last_modified": datetime(2026, 2, 1, tzinfo=timezone.utc),
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert "file.md" in to_pull

    def test_equal_timestamps_different_hash_conflict(self, vault_settings):
        from app.services import sync
        ts = "2026-01-15T12:00:00Z"
        client = [
            {"path": "file.md", "content_hash": "sha256:aaa", "last_modified": ts},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:bbb",
                "last_modified": datetime(2026, 1, 15, 12, 0, 0, tzinfo=timezone.utc),
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    def test_mixed_scenario(self, vault_settings):
        from app.services import sync
        client = [
            {"path": "client_only.md", "content_hash": "sha256:c1", "last_modified": "2026-01-01T00:00:00Z"},
            {"path": "both_same.md", "content_hash": "sha256:same", "last_modified": "2026-01-01T00:00:00Z"},
            {"path": "client_newer.md", "content_hash": "sha256:cn", "last_modified": "2026-03-01T00:00:00Z"},
            {"path": "server_newer.md", "content_hash": "sha256:sn_old", "last_modified": "2026-01-01T00:00:00Z"},
        ]
        server = {
            "both_same.md": {"path": "both_same.md", "content_hash": "sha256:same", "last_modified": datetime(2026, 1, 1, tzinfo=timezone.utc)},
            "client_newer.md": {"path": "client_newer.md", "content_hash": "sha256:cn_old", "last_modified": datetime(2026, 1, 1, tzinfo=timezone.utc)},
            "server_newer.md": {"path": "server_newer.md", "content_hash": "sha256:sn", "last_modified": datetime(2026, 3, 1, tzinfo=timezone.utc)},
            "server_only.md": {"path": "server_only.md", "content_hash": "sha256:so", "last_modified": datetime(2026, 1, 1, tzinfo=timezone.utc)},
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert "client_only.md" in to_push
        assert "client_newer.md" in to_push
        assert "server_only.md" in to_pull
        assert "server_newer.md" in to_pull
        assert conflicts == []


class TestPushFile:
    def test_push_new_file(self, vault_settings, tmp_vault):
        from app.services import sync
        data = b"# Brand new file"
        mtime = datetime(2026, 2, 1, tzinfo=timezone.utc)
        result_path, is_conflict = sync.push_file("pushed.md", data, mtime)
        assert result_path == "pushed.md"
        assert not is_conflict
        assert (tmp_vault / "pushed.md").read_bytes() == data

    def test_push_overwrites_when_client_newer(self, vault_settings, tmp_vault):
        from app.services import sync
        old_mtime = datetime(2026, 1, 1, tzinfo=timezone.utc)
        os.utime(tmp_vault / "readme.md", (old_mtime.timestamp(), old_mtime.timestamp()))

        new_data = b"# Updated by client"
        new_mtime = datetime(2026, 3, 1, tzinfo=timezone.utc)
        result_path, is_conflict = sync.push_file("readme.md", new_data, new_mtime)
        assert result_path == "readme.md"
        assert not is_conflict
        assert (tmp_vault / "readme.md").read_bytes() == new_data

    def test_push_conflict_when_same_timestamp(self, vault_settings, tmp_vault):
        from app.services import sync
        server_mtime = datetime.fromtimestamp(
            (tmp_vault / "readme.md").stat().st_mtime, tz=timezone.utc
        )
        different_data = b"# Different content from client"
        result_path, is_conflict = sync.push_file("readme.md", different_data, server_mtime)
        assert is_conflict
        assert "_conflict_" in result_path
        assert (tmp_vault / "readme.md").read_text(encoding="utf-8") == "# JARVIS Vault"

    def test_push_creates_parent_dirs(self, vault_settings, tmp_vault):
        from app.services import sync
        data = b"nested content"
        mtime = datetime(2026, 2, 1, tzinfo=timezone.utc)
        result_path, is_conflict = sync.push_file("Deep/Nested/file.md", data, mtime)
        assert not is_conflict
        assert (tmp_vault / "Deep" / "Nested" / "file.md").exists()

    def test_push_identical_hash_skips_write(self, vault_settings, tmp_vault):
        from app.services import sync
        existing_data = (tmp_vault / "readme.md").read_bytes()
        mtime = datetime(2026, 2, 1, tzinfo=timezone.utc)
        result_path, is_conflict = sync.push_file("readme.md", existing_data, mtime)
        assert result_path == "readme.md"
        assert not is_conflict


class TestPullFile:
    @pytest.mark.anyio
    async def test_pull_existing_file(self, vault_settings, tmp_vault):
        from app.services import sync
        filename, size, stream = await sync.pull_file("readme.md")
        assert filename == "readme.md"
        chunks = []
        async for chunk in stream:
            chunks.append(chunk)
        content = b"".join(chunks)
        assert content == b"# JARVIS Vault"

    @pytest.mark.anyio
    async def test_pull_nonexistent_raises(self, vault_settings, tmp_vault):
        from app.services import sync
        from app.errors import PathNotFoundError
        with pytest.raises(PathNotFoundError):
            await sync.pull_file("nonexistent.md")

    @pytest.mark.anyio
    async def test_pull_directory_raises(self, vault_settings, tmp_vault):
        from app.services import sync
        from app.errors import InvalidPathError
        with pytest.raises(InvalidPathError):
            await sync.pull_file("Personal")


# === Sync Router Integration Tests ===


class TestSyncManifestEndpoint:
    def test_empty_manifest_returns_all_server_files(self, client, auth_headers):
        response = client.post(
            "/sync/manifest",
            json={"manifest": []},
            headers=auth_headers,
        )
        assert response.status_code == 200
        data = response.json()
        pull_paths = [e["path"] for e in data["to_pull"]]
        assert "readme.md" in pull_paths
        assert "Personal/notes.md" in pull_paths
        assert data["to_push"] == []
        assert data["conflicts"] == []

    def test_full_sync_manifest(self, client, auth_headers, tmp_vault):
        server_hash = sha256hex((tmp_vault / "readme.md").read_bytes())
        server_mtime = datetime.fromtimestamp(
            (tmp_vault / "readme.md").stat().st_mtime, tz=timezone.utc
        ).isoformat()

        response = client.post(
            "/sync/manifest",
            json={
                "manifest": [
                    {"path": "readme.md", "content_hash": server_hash, "last_modified": server_mtime},
                    {"path": "client_only.md", "content_hash": "sha256:xxx", "last_modified": "2026-01-01T00:00:00Z"},
                ]
            },
            headers=auth_headers,
        )
        assert response.status_code == 200
        data = response.json()
        push_paths = [e["path"] for e in data["to_push"]]
        pull_paths = [e["path"] for e in data["to_pull"]]
        assert "client_only.md" in push_paths
        assert "Personal/notes.md" in pull_paths
        assert "readme.md" not in push_paths
        assert "readme.md" not in pull_paths

    def test_manifest_requires_auth(self, client):
        response = client.post("/sync/manifest", json={"manifest": []})
        assert response.status_code == 422

    def test_manifest_rejects_traversal(self, client, auth_headers):
        response = client.post(
            "/sync/manifest",
            json={
                "manifest": [
                    {"path": "../etc/passwd", "content_hash": "sha256:x", "last_modified": "2026-01-01T00:00:00Z"},
                ]
            },
            headers=auth_headers,
        )
        assert response.status_code == 400


class TestSyncPushEndpoint:
    def test_push_new_file(self, client, auth_headers):
        meta = json.dumps({
            "path": "synced.md",
            "content_hash": sha256hex(b"# Synced content"),
            "last_modified": "2026-02-19T12:00:00Z",
        })
        response = client.post(
            "/sync/push",
            data={"metadata": meta},
            files={"file": ("synced.md", io.BytesIO(b"# Synced content"), "application/octet-stream")},
            headers=auth_headers,
        )
        assert response.status_code == 200
        data = response.json()
        assert len(data["accepted"]) == 1
        assert data["accepted"][0]["path"] == "synced.md"
        assert data["conflicts"] == []

    def test_push_requires_auth(self, client):
        meta = json.dumps({"path": "x.md", "content_hash": "sha256:x", "last_modified": "2026-01-01T00:00:00Z"})
        response = client.post("/sync/push", data={"metadata": meta})
        assert response.status_code == 422

    def test_push_conflict_response(self, client, auth_headers, tmp_vault):
        server_mtime = datetime.fromtimestamp(
            (tmp_vault / "readme.md").stat().st_mtime, tz=timezone.utc
        ).isoformat()

        meta = json.dumps({
            "path": "readme.md",
            "content_hash": sha256hex(b"conflicting content"),
            "last_modified": server_mtime,
        })
        response = client.post(
            "/sync/push",
            data={"metadata": meta},
            files={"file": ("readme.md", io.BytesIO(b"conflicting content"), "application/octet-stream")},
            headers=auth_headers,
        )
        assert response.status_code == 200
        data = response.json()
        assert len(data["conflicts"]) == 1
        assert "_conflict_" in data["conflicts"][0]["path"]
        assert data["accepted"] == []


class TestSyncPullEndpoint:
    def test_pull_file(self, client, auth_headers):
        response = client.post(
            "/sync/pull",
            json={"path": "readme.md"},
            headers=auth_headers,
        )
        assert response.status_code == 200
        assert response.content == b"# JARVIS Vault"
        assert "Content-Disposition" in response.headers

    def test_pull_nonexistent_returns_404(self, client, auth_headers):
        response = client.post(
            "/sync/pull",
            json={"path": "nonexistent.md"},
            headers=auth_headers,
        )
        assert response.status_code == 404

    def test_pull_requires_auth(self, client):
        response = client.post("/sync/pull", json={"path": "readme.md"})
        assert response.status_code == 422
