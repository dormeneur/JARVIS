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

    def test_disk_modification_bumps_version(self, vault_settings, tmp_vault):
        """When a file is modified on disk outside the API, build_server_manifest
        should detect the change and auto-bump the version."""
        from app.services import sync
        from app.services.version_tracker import VersionTracker

        # First build — initializes version tracking
        manifest1 = sync.build_server_manifest()
        assert "readme.md" in manifest1
        v1 = manifest1["readme.md"]["version"]

        # Modify the file on disk (simulates laptop editing outside the API)
        readme = tmp_vault / "readme.md"
        readme.write_text("modified content", encoding="utf-8")

        # Second build — should detect the change and bump version
        manifest2 = sync.build_server_manifest()
        v2 = manifest2["readme.md"]["version"]
        assert v2 > v1, f"Version should have increased: {v2} > {v1}"
        assert manifest2["readme.md"]["content_hash"] == sha256hex(b"modified content")

    def test_unmodified_file_keeps_version(self, vault_settings, tmp_vault):
        """When a file is NOT modified between builds, the version stays the same."""
        from app.services import sync

        manifest1 = sync.build_server_manifest()
        v1 = manifest1["readme.md"]["version"]

        manifest2 = sync.build_server_manifest()
        v2 = manifest2["readme.md"]["version"]
        assert v2 == v1, f"Version should stay the same: {v2} == {v1}"


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

    def test_version_match_different_hash_to_push(self, vault_settings):
        """Client and server have same version but different hash → push (only mobile changed)"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:new", "last_modified": "2026-02-01T00:00:00Z", "version": 5},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:old",
                "last_modified": datetime(2026, 1, 1, tzinfo=timezone.utc),
                "version": 5,
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert "file.md" in to_push
        assert to_pull == []
        assert conflicts == []

    def test_server_ahead_mobile_untouched_to_pull(self, vault_settings):
        """Server version ahead, mobile hash matches prev_hash → only server changed → pull"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:old", "last_modified": "2026-01-01T00:00:00Z", "version": 2},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:new",
                "last_modified": datetime(2026, 2, 1, tzinfo=timezone.utc),
                "version": 3,
                "prev_hash": "sha256:old",  # matches mobile's hash
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert "file.md" in to_pull
        assert conflicts == []

    def test_server_ahead_mobile_also_changed_conflict(self, vault_settings):
        """Server version ahead, mobile hash differs from prev_hash → both changed → conflict"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:mobile_edit", "last_modified": "2026-02-01T00:00:00Z", "version": 2},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:server_edit",
                "last_modified": datetime(2026, 2, 1, tzinfo=timezone.utc),
                "version": 3,
                "prev_hash": "sha256:old",  # differs from mobile's hash
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    def test_server_ahead_no_prev_hash_conflict(self, vault_settings):
        """Server version ahead but no prev_hash available → safe fallback to conflict"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:client", "last_modified": "2026-02-01T00:00:00Z", "version": 2},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:server",
                "last_modified": datetime(2026, 1, 1, tzinfo=timezone.utc),
                "version": 3,
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    # --- has_local_changes flag tests ---

    def test_has_local_changes_false_server_ahead_pull(self, vault_settings):
        """Mobile reports no local changes + server ahead → pull"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:old", "last_modified": "2026-01-01T00:00:00Z", "version": 2, "has_local_changes": False},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:new",
                "last_modified": datetime(2026, 2, 1, tzinfo=timezone.utc),
                "version": 3,
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert "file.md" in to_pull
        assert conflicts == []

    def test_has_local_changes_true_server_ahead_conflict(self, vault_settings):
        """Mobile reports local changes + server ahead → conflict"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:mobile_edit", "last_modified": "2026-02-01T00:00:00Z", "version": 2, "has_local_changes": True},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:server_edit",
                "last_modified": datetime(2026, 2, 1, tzinfo=timezone.utc),
                "version": 3,
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    def test_has_local_changes_false_overrides_prev_hash_mismatch(self, vault_settings):
        """has_local_changes=False takes priority over prev_hash mismatch → pull"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:old_v1", "last_modified": "2026-01-01T00:00:00Z", "version": 2, "has_local_changes": False},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:new",
                "last_modified": datetime(2026, 2, 1, tzinfo=timezone.utc),
                "version": 5,
                "prev_hash": "sha256:old_v4",  # Doesn't match client hash (multi-version jump)
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert "file.md" in to_pull
        assert conflicts == []

    def test_has_local_changes_multi_version_jump_pull(self, vault_settings):
        """Client 3 versions behind, no local changes → pull (prev_hash alone would fail here)"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:v2", "last_modified": "2026-01-01T00:00:00Z", "version": 2, "has_local_changes": False},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:v5",
                "last_modified": datetime(2026, 2, 1, tzinfo=timezone.utc),
                "version": 5,
                "prev_hash": "sha256:v4",  # Only one step back, won't match v2
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert "file.md" in to_pull
        assert conflicts == []

    def test_has_local_changes_none_falls_back_to_prev_hash(self, vault_settings):
        """No has_local_changes flag → falls back to prev_hash comparison"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:old", "last_modified": "2026-01-01T00:00:00Z", "version": 2},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:new",
                "last_modified": datetime(2026, 2, 1, tzinfo=timezone.utc),
                "version": 3,
                "prev_hash": "sha256:old",  # Matches client hash → pull
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert "file.md" in to_pull
        assert conflicts == []

    def test_has_local_changes_false_same_version_still_push(self, vault_settings):
        """has_local_changes=False but same version + different hash → push (hash mismatch is on mobile side)"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:mobile", "last_modified": "2026-02-01T00:00:00Z", "version": 3, "has_local_changes": False},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:server",
                "last_modified": datetime(2026, 1, 1, tzinfo=timezone.utc),
                "version": 3,
            },
        }
        # Same version → push (the has_local_changes check only applies when client_version < server_version)
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert "file.md" in to_push
        assert to_pull == []
        assert conflicts == []

    def test_client_version_ahead_to_push(self, vault_settings):
        """Client version > server version (abnormal) → push to recover"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:client", "last_modified": "2026-02-01T00:00:00Z", "version": 5},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:server",
                "last_modified": datetime(2026, 1, 1, tzinfo=timezone.utc),
                "version": 3,
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert "file.md" in to_push
        assert to_pull == []
        assert conflicts == []

    def test_version_mismatch_conflict(self, vault_settings):
        """Client version < server version, no prev_hash → conflict"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:client", "last_modified": "2026-02-01T00:00:00Z", "version": 3},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:server",
                "last_modified": datetime(2026, 1, 1, tzinfo=timezone.utc),
                "version": 5,
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    def test_version_mismatch_even_with_newer_timestamp(self, vault_settings):
        """Version mismatch with no prev_hash triggers conflict even if client timestamp is newer"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:client", "last_modified": "2026-03-01T00:00:00Z", "version": 2},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:server",
                "last_modified": datetime(2026, 1, 1, tzinfo=timezone.utc),
                "version": 3,
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    def test_fallback_no_version_different_hash_conflict(self, vault_settings):
        """No version info, different hashes → conflict (both sides exist with different content)"""
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
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    def test_client_newer_different_hash_conflict(self, vault_settings):
        """Client newer timestamp, different hashes → conflict (both sides exist)"""
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
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    def test_server_newer_different_hash_conflict(self, vault_settings):
        """Server newer timestamp, different hashes → conflict (both sides exist)"""
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
        assert to_pull == []
        assert "file.md" in conflicts

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

    def test_timestamps_within_tolerance_conflict(self, vault_settings):
        """Timestamps within 2 seconds with different hashes → conflict"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:aaa", "last_modified": "2026-01-15T12:00:00Z"},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:bbb",
                "last_modified": datetime(2026, 1, 15, 12, 0, 1, tzinfo=timezone.utc),  # 1 second diff
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    def test_timestamps_outside_tolerance_client_newer(self, vault_settings):
        """Different hashes, both sides exist → conflict regardless of timestamps"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:new", "last_modified": "2026-01-15T12:00:05Z"},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:old",
                "last_modified": datetime(2026, 1, 15, 12, 0, 0, tzinfo=timezone.utc),  # 5 seconds diff
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    def test_timestamps_outside_tolerance_server_newer(self, vault_settings):
        """Different hashes, both sides exist → conflict regardless of timestamps"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:old", "last_modified": "2026-01-15T12:00:00Z"},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:new",
                "last_modified": datetime(2026, 1, 15, 12, 0, 5, tzinfo=timezone.utc),  # 5 seconds diff
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    def test_tolerance_boundary_exactly_2_seconds(self, vault_settings):
        """Exactly 2 seconds difference → conflict (within tolerance)"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:aaa", "last_modified": "2026-01-15T12:00:00Z"},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:bbb",
                "last_modified": datetime(2026, 1, 15, 12, 0, 2, tzinfo=timezone.utc),  # exactly 2 seconds
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    def test_tolerance_boundary_just_over_2_seconds(self, vault_settings):
        """Different hashes, both sides exist → conflict regardless of timestamp difference"""
        from app.services import sync
        client = [
            {"path": "file.md", "content_hash": "sha256:new", "last_modified": "2026-01-15T12:00:02.1Z"},
        ]
        server = {
            "file.md": {
                "path": "file.md",
                "content_hash": "sha256:old",
                "last_modified": datetime(2026, 1, 15, 12, 0, 0, tzinfo=timezone.utc),  # 2.1 seconds
            },
        }
        to_push, to_pull, conflicts = sync.diff_manifests(client, server)
        assert to_push == []
        assert to_pull == []
        assert "file.md" in conflicts

    def test_tolerance_with_custom_setting(self, vault_settings):
        """Test with custom tolerance setting"""
        from app.services import sync
        from app.config import settings
        
        # Temporarily change tolerance
        original_tolerance = settings.sync_timestamp_tolerance_seconds
        try:
            settings.sync_timestamp_tolerance_seconds = 5
            
            client = [
                {"path": "file.md", "content_hash": "sha256:aaa", "last_modified": "2026-01-15T12:00:00Z"},
            ]
            server = {
                "file.md": {
                    "path": "file.md",
                    "content_hash": "sha256:bbb",
                    "last_modified": datetime(2026, 1, 15, 12, 0, 4, tzinfo=timezone.utc),  # 4 seconds
                },
            }
            to_push, to_pull, conflicts = sync.diff_manifests(client, server)
            assert "file.md" in conflicts  # Within 5 second tolerance
        finally:
            settings.sync_timestamp_tolerance_seconds = original_tolerance

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
        assert "server_only.md" in to_pull
        assert "client_newer.md" in conflicts
        assert "server_newer.md" in conflicts
        assert len(to_push) == 1  # only client_only.md
        assert len(to_pull) == 1  # only server_only.md


class TestPushFile:
    def test_push_new_file(self, vault_settings, tmp_vault):
        from app.services import sync
        data = b"# Brand new file"
        mtime = datetime(2026, 2, 1, tzinfo=timezone.utc)
        result_path, is_conflict, version = sync.push_file("pushed.md", data, mtime)
        assert result_path == "pushed.md"
        assert not is_conflict
        assert version == 1
        assert (tmp_vault / "pushed.md").read_bytes() == data

    def test_push_new_file_creates_version(self, vault_settings, tmp_vault):
        from app.services import sync
        from app.services.version_tracker import VersionTracker
        
        data = b"# New file"
        mtime = datetime(2026, 2, 1, tzinfo=timezone.utc)
        sync.push_file("new.md", data, mtime)
        
        tracker = VersionTracker()
        version = tracker.get_version("new.md")
        assert version == 1

    def test_push_with_matching_version_increments(self, vault_settings, tmp_vault):
        from app.services import sync
        from app.services.version_tracker import VersionTracker
        
        # Create initial file
        initial_data = b"# Initial"
        mtime1 = datetime(2026, 1, 1, tzinfo=timezone.utc)
        _, _, v1 = sync.push_file("file.md", initial_data, mtime1)
        assert v1 == 1
        
        # Update with matching version
        new_data = b"# Updated"
        mtime2 = datetime(2026, 2, 1, tzinfo=timezone.utc)
        _, is_conflict, v2 = sync.push_file("file.md", new_data, mtime2, base_version=1)
        assert not is_conflict
        assert v2 == 2
        assert (tmp_vault / "file.md").read_bytes() == new_data

    def test_push_with_version_mismatch_creates_conflict(self, vault_settings, tmp_vault):
        from app.services import sync
        
        # Create initial file (version 1)
        initial_data = b"# Initial"
        mtime1 = datetime(2026, 1, 1, tzinfo=timezone.utc)
        _, _, v1 = sync.push_file("version_test.md", initial_data, mtime1)
        assert v1 == 1
        
        # Server updates to version 2 (with correct base_version)
        server_update = b"# Server update"
        mtime2 = datetime(2026, 2, 1, tzinfo=timezone.utc)
        _, _, v2 = sync.push_file("version_test.md", server_update, mtime2, base_version=1)
        assert v2 == 2
        
        # Client tries to push with base_version=1 (stale)
        client_update = b"# Client update"
        mtime3 = datetime(2026, 3, 1, tzinfo=timezone.utc)
        result_path, is_conflict, version = sync.push_file("version_test.md", client_update, mtime3, base_version=1)
        
        assert is_conflict
        assert result_path == "version_test.md"  # Returns original path
        assert version == 2  # Returns current server version
        # Server version should be preserved (no overwrite on conflict)
        assert (tmp_vault / "version_test.md").read_bytes() == server_update

    def test_push_version_mismatch_even_with_newer_timestamp(self, vault_settings, tmp_vault):
        """Version mismatch triggers conflict even if client has newer timestamp"""
        from app.services import sync
        
        # Create file at version 1
        sync.push_file("file.md", b"# V1", datetime(2026, 1, 1, tzinfo=timezone.utc))
        
        # Update to version 2
        sync.push_file("file.md", b"# V2", datetime(2026, 2, 1, tzinfo=timezone.utc), base_version=1)
        
        # Client with stale version 1 tries to push with much newer timestamp
        result_path, is_conflict, version = sync.push_file(
            "file.md",
            b"# Client with newer timestamp",
            datetime(2026, 12, 31, tzinfo=timezone.utc),  # Much newer timestamp
            base_version=1  # But stale version
        )
        
        assert is_conflict
        assert result_path == "file.md"  # Returns original path
        assert version == 2  # Returns current server version

    def test_push_identical_hash_returns_current_version(self, vault_settings, tmp_vault):
        from app.services import sync
        
        # Create initial file
        data = b"# Same content"
        mtime = datetime(2026, 1, 1, tzinfo=timezone.utc)
        _, _, v1 = sync.push_file("file.md", data, mtime)
        
        # Push same content again
        _, is_conflict, v2 = sync.push_file("file.md", data, mtime, base_version=1)
        
        assert not is_conflict
        assert v2 == v1  # Version unchanged

    def test_push_fallback_to_timestamp_when_no_base_version(self, vault_settings, tmp_vault):
        """Falls back to timestamp-based conflict detection when base_version is None"""
        from app.services import sync
        
        # Create file
        sync.push_file("file.md", b"# Initial", datetime(2026, 1, 1, tzinfo=timezone.utc))
        
        # Push without base_version (old client)
        server_mtime = datetime.fromtimestamp(
            (tmp_vault / "file.md").stat().st_mtime, tz=timezone.utc
        )
        
        # Within tolerance → conflict
        client_mtime = server_mtime + timedelta(seconds=1)
        result_path, is_conflict, _ = sync.push_file(
            "file.md",
            b"# Different content",
            client_mtime,
            base_version=None
        )
        
        assert is_conflict
        assert result_path == "file.md"  # Returns original path

    def test_push_overwrites_when_client_newer(self, vault_settings, tmp_vault):
        from app.services import sync
        old_mtime = datetime(2026, 1, 1, tzinfo=timezone.utc)
        os.utime(tmp_vault / "readme.md", (old_mtime.timestamp(), old_mtime.timestamp()))

        new_data = b"# Updated by client"
        new_mtime = datetime(2026, 3, 1, tzinfo=timezone.utc)
        result_path, is_conflict, version = sync.push_file("readme.md", new_data, new_mtime)
        assert result_path == "readme.md"
        assert not is_conflict
        assert version is not None
        assert (tmp_vault / "readme.md").read_bytes() == new_data

    def test_push_conflict_when_same_timestamp(self, vault_settings, tmp_vault):
        from app.services import sync
        server_mtime = datetime.fromtimestamp(
            (tmp_vault / "readme.md").stat().st_mtime, tz=timezone.utc
        )
        different_data = b"# Different content from client"
        result_path, is_conflict, version = sync.push_file("readme.md", different_data, server_mtime)
        assert is_conflict
        assert result_path == "readme.md"  # Returns original path
        assert version is not None  # Returns current server version
        assert (tmp_vault / "readme.md").read_text(encoding="utf-8") == "# JARVIS Vault"

    def test_push_conflict_within_tolerance(self, vault_settings, tmp_vault):
        """Push with timestamp within tolerance window → conflict"""
        from app.services import sync
        server_mtime = datetime.fromtimestamp(
            (tmp_vault / "readme.md").stat().st_mtime, tz=timezone.utc
        )
        # Client timestamp 1 second later (within 2 second tolerance)
        client_mtime = server_mtime + timedelta(seconds=1)
        different_data = b"# Different content from client"
        result_path, is_conflict, version = sync.push_file("readme.md", different_data, client_mtime)
        assert is_conflict
        assert result_path == "readme.md"  # Returns original path
        assert version is not None  # Returns current server version
        assert (tmp_vault / "readme.md").read_text(encoding="utf-8") == "# JARVIS Vault"

    def test_push_overwrites_outside_tolerance(self, vault_settings, tmp_vault):
        """Push with timestamp outside tolerance window → overwrite"""
        from app.services import sync
        server_mtime = datetime.fromtimestamp(
            (tmp_vault / "readme.md").stat().st_mtime, tz=timezone.utc
        )
        # Client timestamp 5 seconds later (outside 2 second tolerance)
        client_mtime = server_mtime + timedelta(seconds=5)
        different_data = b"# Different content from client"
        result_path, is_conflict, version = sync.push_file("readme.md", different_data, client_mtime)
        assert not is_conflict
        assert result_path == "readme.md"
        assert version is not None
        assert (tmp_vault / "readme.md").read_bytes() == different_data

    def test_push_creates_parent_dirs(self, vault_settings, tmp_vault):
        from app.services import sync
        data = b"nested content"
        mtime = datetime(2026, 2, 1, tzinfo=timezone.utc)
        result_path, is_conflict, version = sync.push_file("Deep/Nested/file.md", data, mtime)
        assert not is_conflict
        assert version == 1
        assert (tmp_vault / "Deep" / "Nested" / "file.md").exists()

    def test_push_conflict_preserves_version_tracking(self, vault_settings, tmp_vault):
        """On conflict, original file's version tracking is unchanged"""
        from app.services import sync
        from app.services.version_tracker import VersionTracker
        
        # Create file
        sync.push_file("file.md", b"# V1", datetime(2026, 1, 1, tzinfo=timezone.utc))
        sync.push_file("file.md", b"# V2", datetime(2026, 2, 1, tzinfo=timezone.utc), base_version=1)
        
        # Attempt conflicting push
        result_path, is_conflict, version = sync.push_file(
            "file.md",
            b"# Conflict",
            datetime(2026, 3, 1, tzinfo=timezone.utc),
            base_version=1
        )
        
        assert is_conflict
        assert result_path == "file.md"
        assert version == 2  # Returns current server version
        
        # Original file's version should be unchanged
        tracker = VersionTracker()
        assert tracker.get_version("file.md") == 2


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
        assert data["conflicts"][0]["path"] == "readme.md"  # Returns original path
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

    def test_pull_includes_version_header(self, client, auth_headers, tmp_vault):
        """X-File-Version header must be present with a valid integer version."""
        # First push a file so it has a known version in the tracker
        meta = json.dumps({
            "path": "versioned_pull.md",
            "content_hash": sha256hex(b"# Versioned content"),
            "last_modified": "2026-02-25T12:00:00Z",
        })
        push_resp = client.post(
            "/sync/push",
            data={"metadata": meta},
            files={"file": ("versioned_pull.md", io.BytesIO(b"# Versioned content"), "application/octet-stream")},
            headers=auth_headers,
        )
        assert push_resp.status_code == 200
        pushed_version = push_resp.json()["accepted"][0]["version"]

        # Now pull and verify the header
        response = client.post(
            "/sync/pull",
            json={"path": "versioned_pull.md"},
            headers=auth_headers,
        )
        assert response.status_code == 200
        assert "X-File-Version" in response.headers
        header_version = int(response.headers["X-File-Version"])
        assert header_version == pushed_version

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
