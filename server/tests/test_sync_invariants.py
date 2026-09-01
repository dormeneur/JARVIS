"""
Sync engine invariants — the most important guarantees from 5-version-control.md:

1. No silent overwrites
2. Concurrent edits always go to conflicts (never silently pulled or pushed)
3. Server-only changes produce a pull, not a conflict
4. Client-only changes produce a push, not a conflict
5. Already-in-sync files produce nothing

These run entirely without network/Docker/Ollama — pure sync service logic.
"""
import hashlib
import pytest
from pathlib import Path
from unittest.mock import patch
from datetime import datetime, timezone


def sha256_hex(data: bytes) -> str:
    return "sha256:" + hashlib.sha256(data).hexdigest()


@pytest.fixture
def sync_env(tmp_vault: Path, vault_settings):
    """Patch sync service settings to point at tmp_vault, yield helpers."""
    from app.services import sync as sync_svc
    from app.services.version_tracker import VersionTracker

    def reset_tracker():
        tracker = VersionTracker()
        # Wipe the version DB between tests
        import sqlite3
        with sqlite3.connect(tracker.db_path) as conn:
            conn.execute("DELETE FROM file_versions")
            conn.commit()

    reset_tracker()
    return sync_svc, VersionTracker, tmp_vault


class TestDiffManifestBuckets:
    """Unit tests for diff_manifests()."""

    def test_identical_file_no_action(self, sync_env, tmp_vault):
        svc, TrackerCls, vault = sync_env

        # Use the actual on-disk content so the hash matches exactly
        content = (vault / "readme.md").read_bytes()
        h = sha256_hex(content)

        # Seed version tracker with the real hash
        tracker = TrackerCls()
        tracker.create_version("readme.md", h)

        client = [{"path": "readme.md", "content_hash": h,
                   "version": 1, "has_local_changes": False}]
        server = svc.build_server_manifest()

        to_push, to_pull, conflicts = svc.diff_manifests(client, server)
        assert "readme.md" not in to_push
        assert "readme.md" not in to_pull
        assert "readme.md" not in conflicts

    def test_server_newer_non_conflicting_produces_pull(self, sync_env, tmp_vault):
        svc, TrackerCls, vault = sync_env
        # Server has v2, client only knows v1 and reports no local changes
        content_v1 = b"version one"
        content_v2 = b"version two"
        (tmp_vault / "readme.md").write_bytes(content_v2)

        tracker = TrackerCls()
        h1 = sha256_hex(content_v1)
        h2 = sha256_hex(content_v2)
        tracker.create_version("readme.md", h1)
        tracker.increment_version("readme.md", h2)

        client = [{"path": "readme.md", "content_hash": h1,
                   "version": 1, "has_local_changes": False}]
        server = svc.build_server_manifest()

        to_push, to_pull, conflicts = svc.diff_manifests(client, server)
        assert "readme.md" in to_pull
        assert "readme.md" not in conflicts
        assert "readme.md" not in to_push

    def test_client_only_change_produces_push(self, sync_env, tmp_vault):
        svc, TrackerCls, vault = sync_env
        content = b"server content"
        (tmp_vault / "readme.md").write_bytes(content)

        tracker = TrackerCls()
        h = sha256_hex(content)
        tracker.create_version("readme.md", h)

        # Client has the same version but different hash (local edit)
        client_hash = sha256_hex(b"client local edit")
        client = [{"path": "readme.md", "content_hash": client_hash,
                   "version": 1, "has_local_changes": True}]
        server = svc.build_server_manifest()

        to_push, to_pull, conflicts = svc.diff_manifests(client, server)
        assert "readme.md" in to_push
        assert "readme.md" not in conflicts
        assert "readme.md" not in to_pull

    def test_concurrent_edit_produces_conflict(self, sync_env, tmp_vault):
        svc, TrackerCls, vault = sync_env
        # Server was bumped to v2, client has v1 AND has local changes
        content_v2 = b"server version 2"
        (tmp_vault / "readme.md").write_bytes(content_v2)

        tracker = TrackerCls()
        tracker.create_version("readme.md", sha256_hex(b"version 1"))
        tracker.increment_version("readme.md", sha256_hex(content_v2))

        client_hash = sha256_hex(b"mobile local edit")
        client = [{"path": "readme.md", "content_hash": client_hash,
                   "version": 1, "has_local_changes": True}]
        server = svc.build_server_manifest()

        to_push, to_pull, conflicts = svc.diff_manifests(client, server)
        assert "readme.md" in conflicts
        # Must not appear elsewhere — that would be a silent overwrite
        assert "readme.md" not in to_push
        assert "readme.md" not in to_pull

    def test_server_only_file_added_goes_to_pull(self, sync_env, tmp_vault):
        """File on server the client has never seen → pull."""
        svc, TrackerCls, vault = sync_env
        # Client sends an empty manifest (no files known)
        server = svc.build_server_manifest()
        to_push, to_pull, conflicts = svc.diff_manifests([], server)
        # Every file the server has should be offered for pull
        for path in server:
            assert path in to_pull


class TestPushFileConflictDetection:
    """push_file() must refuse to overwrite on version mismatch."""

    def test_push_new_file_succeeds(self, sync_env, tmp_vault):
        svc, TrackerCls, vault = sync_env
        now = datetime.now(tz=timezone.utc)
        path, is_conflict, version = svc.push_file(
            "Notes/new.md", b"fresh content", now, base_version=0
        )
        assert not is_conflict
        assert version == 1
        assert (vault / "Notes" / "new.md").read_bytes() == b"fresh content"

    def test_push_with_stale_base_version_is_conflict(self, sync_env, tmp_vault):
        svc, TrackerCls, vault = sync_env
        now = datetime.now(tz=timezone.utc)

        # Create file at v1
        svc.push_file("Notes/contested.md", b"v1 content", now, base_version=0)
        # Server bumps to v2
        svc.push_file("Notes/contested.md", b"v2 content", now, base_version=1)

        # Client tries to push with base_version=1 (stale)
        _, is_conflict, server_ver = svc.push_file(
            "Notes/contested.md", b"mobile edit", now, base_version=1
        )
        assert is_conflict
        assert server_ver == 2
        # File on disk must be unchanged — no silent overwrite
        assert (vault / "Notes" / "contested.md").read_bytes() == b"v2 content"

    def test_push_with_correct_base_version_succeeds(self, sync_env, tmp_vault):
        svc, TrackerCls, vault = sync_env
        now = datetime.now(tz=timezone.utc)

        svc.push_file("Notes/ok.md", b"initial", now, base_version=0)
        _, is_conflict, version = svc.push_file(
            "Notes/ok.md", b"updated", now, base_version=1
        )
        assert not is_conflict
        assert version == 2
        assert (vault / "Notes" / "ok.md").read_bytes() == b"updated"
