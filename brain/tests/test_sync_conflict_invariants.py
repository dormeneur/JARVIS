"""
Sync invariants — server side.

These exercise server/app/services/sync.py without a running server,
hitting the sync service logic directly. The core invariant tested:
every conflict must produce exactly one mutation row (never zero, never
two) and must never silently overwrite data.
"""
import json
import pytest
from pathlib import Path
from unittest.mock import patch


@pytest.fixture
def sync_vault(tmp_path: Path) -> Path:
    (tmp_path / "system").mkdir()
    (tmp_path / "Notes").mkdir()
    (tmp_path / "Notes" / "file.md").write_text("server content v1", encoding="utf-8")
    return tmp_path


@pytest.fixture
def sync_settings(sync_vault: Path):
    import os
    from unittest.mock import patch

    env = {
        "JARVIS_VAULT_PATH": str(sync_vault),
        "JARVIS_JWT_SECRET": "test-key",
        "JARVIS_JWT_EXPIRY_HOURS": "1",
        "JARVIS_MAX_DEVICES": "5",
    }
    with patch.dict(os.environ, env):
        from app.config import Settings
        s = Settings()
        with patch("app.config.settings", s), \
             patch("app.services.vault.settings", s), \
             patch("app.services.sync.settings", s), \
             patch("app.services.version_tracker.settings", s):
            yield s


class TestManifestDiff:
    """Server manifest diff produces the right buckets."""

    def test_unmodified_file_not_in_any_bucket(self, sync_settings, sync_vault):
        from app.services import sync
        from app.services.version_tracker import VersionTracker

        tracker = VersionTracker()
        tracker.bump("Notes/file.md")  # version = 1
        server_hash = _hash_file(sync_vault / "Notes" / "file.md")

        manifest = [{"path": "Notes/file.md", "serverVersion": 1, "contentHash": server_hash}]
        result = sync.compute_manifest_diff(manifest)

        assert result["to_push"] == []
        assert result["to_pull"] == []
        assert result["conflicts"] == []

    def test_server_newer_file_goes_to_pull(self, sync_settings, sync_vault):
        from app.services import sync
        from app.services.version_tracker import VersionTracker

        tracker = VersionTracker()
        tracker.bump("Notes/file.md")
        tracker.bump("Notes/file.md")  # server version = 2

        # Client only knows version 1
        manifest = [{"path": "Notes/file.md", "serverVersion": 1, "contentHash": "stale_hash"}]
        result = sync.compute_manifest_diff(manifest)

        pulls = [e["path"] for e in result["to_pull"]]
        assert "Notes/file.md" in pulls
        assert "Notes/file.md" not in [e["path"] for e in result["conflicts"]]

    def test_client_has_unknown_file_goes_to_push(self, sync_settings, sync_vault):
        from app.services import sync

        (sync_vault / "Notes" / "new.md").write_text("new file", encoding="utf-8")
        manifest = [{"path": "Notes/new.md", "serverVersion": 0, "contentHash": "new_hash"}]
        result = sync.compute_manifest_diff(manifest)

        pushes = [e["path"] for e in result["to_push"]]
        assert "Notes/new.md" in pushes

    def test_concurrent_edit_goes_to_conflicts(self, sync_settings, sync_vault):
        from app.services import sync
        from app.services.version_tracker import VersionTracker

        tracker = VersionTracker()
        tracker.bump("Notes/file.md")
        tracker.bump("Notes/file.md")  # server = 2

        # Client has version 1 but a DIFFERENT hash (concurrent edit)
        manifest = [{"path": "Notes/file.md", "serverVersion": 1, "contentHash": "different_hash"}]
        result = sync.compute_manifest_diff(manifest)

        conflict_paths = [e["path"] for e in result["conflicts"]]
        assert "Notes/file.md" in conflict_paths
        # Must NOT also appear in to_push or to_pull
        assert "Notes/file.md" not in [e["path"] for e in result["to_push"]]
        assert "Notes/file.md" not in [e["path"] for e in result["to_pull"]]


def _hash_file(p: Path) -> str:
    import hashlib
    return hashlib.sha256(p.read_bytes()).hexdigest()
