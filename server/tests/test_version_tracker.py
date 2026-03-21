import tempfile
from pathlib import Path

import pytest

from app.services.version_tracker import VersionTracker


@pytest.fixture
def temp_db():
    """Create a temporary database for testing."""
    with tempfile.TemporaryDirectory() as tmpdir:
        db_path = Path(tmpdir) / "test_versions.db"
        yield db_path


class TestVersionTracker:
    def test_create_version_returns_one(self, temp_db):
        tracker = VersionTracker(temp_db)
        version = tracker.create_version("test.md", "sha256:abc123")
        assert version == 1

    def test_get_version_returns_none_for_nonexistent(self, temp_db):
        tracker = VersionTracker(temp_db)
        version = tracker.get_version("nonexistent.md")
        assert version is None

    def test_get_version_returns_created_version(self, temp_db):
        tracker = VersionTracker(temp_db)
        tracker.create_version("test.md", "sha256:abc123")
        version = tracker.get_version("test.md")
        assert version == 1

    def test_increment_version_increases_by_one(self, temp_db):
        tracker = VersionTracker(temp_db)
        tracker.create_version("test.md", "sha256:abc123")
        new_version = tracker.increment_version("test.md", "sha256:def456")
        assert new_version == 2

    def test_increment_version_creates_if_not_exists(self, temp_db):
        """Incrementing an untracked file initializes at version 2 (not 1)
        so it's distinguishable from a freshly-created file."""
        tracker = VersionTracker(temp_db)
        version = tracker.increment_version("new.md", "sha256:xyz789")
        assert version == 2

    def test_increment_version_multiple_times(self, temp_db):
        tracker = VersionTracker(temp_db)
        tracker.create_version("test.md", "sha256:v1")
        tracker.increment_version("test.md", "sha256:v2")
        tracker.increment_version("test.md", "sha256:v3")
        version = tracker.get_version("test.md")
        assert version == 3

    def test_get_version_and_hash(self, temp_db):
        tracker = VersionTracker(temp_db)
        tracker.create_version("test.md", "sha256:abc123")
        result = tracker.get_version_and_hash("test.md")
        assert result == (1, "sha256:abc123")

    def test_get_version_and_hash_returns_none_for_nonexistent(self, temp_db):
        tracker = VersionTracker(temp_db)
        result = tracker.get_version_and_hash("nonexistent.md")
        assert result is None

    def test_delete_version_removes_entry(self, temp_db):
        tracker = VersionTracker(temp_db)
        tracker.create_version("test.md", "sha256:abc123")
        tracker.delete_version("test.md")
        version = tracker.get_version("test.md")
        assert version is None

    def test_delete_version_nonexistent_does_not_error(self, temp_db):
        tracker = VersionTracker(temp_db)
        tracker.delete_version("nonexistent.md")  # Should not raise

    def test_upsert_version_creates_new(self, temp_db):
        tracker = VersionTracker(temp_db)
        tracker.upsert_version("test.md", 5, "sha256:abc123")
        version = tracker.get_version("test.md")
        assert version == 5

    def test_upsert_version_updates_existing(self, temp_db):
        tracker = VersionTracker(temp_db)
        tracker.create_version("test.md", "sha256:old")
        tracker.upsert_version("test.md", 10, "sha256:new")
        result = tracker.get_version_and_hash("test.md")
        assert result == (10, "sha256:new")

    def test_multiple_files_independent_versions(self, temp_db):
        tracker = VersionTracker(temp_db)
        tracker.create_version("file1.md", "sha256:a")
        tracker.create_version("file2.md", "sha256:b")
        tracker.increment_version("file1.md", "sha256:a2")
        
        v1 = tracker.get_version("file1.md")
        v2 = tracker.get_version("file2.md")
        
        assert v1 == 2
        assert v2 == 1

    def test_hash_updates_on_increment(self, temp_db):
        tracker = VersionTracker(temp_db)
        tracker.create_version("test.md", "sha256:v1")
        tracker.increment_version("test.md", "sha256:v2")
        
        result = tracker.get_version_and_hash("test.md")
        assert result == (2, "sha256:v2")

    def test_prev_hash_none_after_create(self, temp_db):
        """Newly created file has no prev_hash."""
        tracker = VersionTracker(temp_db)
        tracker.create_version("test.md", "sha256:v1")
        assert tracker.get_prev_hash("test.md") is None

    def test_prev_hash_set_after_increment(self, temp_db):
        """After increment, prev_hash = the old last_hash."""
        tracker = VersionTracker(temp_db)
        tracker.create_version("test.md", "sha256:v1")
        tracker.increment_version("test.md", "sha256:v2")
        assert tracker.get_prev_hash("test.md") == "sha256:v1"

    def test_prev_hash_chains_through_increments(self, temp_db):
        """Each increment shifts prev_hash to the previous last_hash."""
        tracker = VersionTracker(temp_db)
        tracker.create_version("test.md", "sha256:v1")
        tracker.increment_version("test.md", "sha256:v2")
        tracker.increment_version("test.md", "sha256:v3")
        # prev_hash should be v2 (the hash before the latest increment)
        assert tracker.get_prev_hash("test.md") == "sha256:v2"

    def test_prev_hash_none_for_nonexistent(self, temp_db):
        tracker = VersionTracker(temp_db)
        assert tracker.get_prev_hash("nonexistent.md") is None

    def test_prev_hash_none_for_untracked_increment(self, temp_db):
        """Incrementing an untracked file has no prev_hash."""
        tracker = VersionTracker(temp_db)
        tracker.increment_version("new.md", "sha256:abc")
        assert tracker.get_prev_hash("new.md") is None
