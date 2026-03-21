import hashlib
import os
from pathlib import Path
from unittest.mock import patch

import pytest

from app.config import Settings
from app.errors import (
    InvalidPathError,
    PathAlreadyExistsError,
    PathNotFoundError,
    PathTraversalError,
)


@pytest.fixture
def svc(tmp_vault: Path):
    test_settings = Settings(vault_path=tmp_vault)
    with patch("app.services.vault.settings", test_settings), \
         patch("app.services.version_tracker.settings", test_settings):
        from app.services import vault
        yield vault


def sha256_hex(data: bytes) -> str:
    return f"sha256:{hashlib.sha256(data).hexdigest()}"


class TestListDirectory:
    def test_list_root(self, svc, tmp_vault):
        result = svc.list_directory()
        assert result.path == "/"
        names = [e.name for e in result.entries]
        assert "Personal" in names
        assert "Work" in names
        assert "readme.md" in names

    def test_list_subdirectory(self, svc):
        result = svc.list_directory("Personal")
        assert result.path == "Personal"
        assert len(result.entries) == 1
        assert result.entries[0].name == "notes.md"

    def test_list_nonexistent(self, svc):
        with pytest.raises(PathNotFoundError):
            svc.list_directory("Nonexistent")

    def test_list_file_not_directory(self, svc):
        with pytest.raises(InvalidPathError):
            svc.list_directory("readme.md")

    def test_directories_sorted_first(self, svc):
        result = svc.list_directory()
        types = [e.type.value for e in result.entries]
        dir_done = False
        for t in types:
            if t == "file":
                dir_done = True
            if dir_done and t == "directory":
                pytest.fail("Directories should be sorted before files")

    def test_hidden_files_excluded(self, svc, tmp_vault):
        (tmp_vault / ".hidden_file").write_text("secret", encoding="utf-8")
        result = svc.list_directory()
        names = [e.name for e in result.entries]
        assert ".hidden_file" not in names

    def test_symlinks_excluded(self, svc, tmp_vault):
        link_path = tmp_vault / "link_to_personal"
        try:
            os.symlink(tmp_vault / "Personal", link_path)
        except OSError:
            pytest.skip("Cannot create symlink on this system")
        result = svc.list_directory()
        names = [e.name for e in result.entries]
        assert "link_to_personal" not in names

    def test_empty_directory(self, svc, tmp_vault):
        (tmp_vault / "Empty").mkdir()
        result = svc.list_directory("Empty")
        assert result.entries == []


class TestReadFile:
    def test_read_existing_file(self, svc):
        result = svc.read_file("readme.md")
        assert result.name == "readme.md"
        assert result.content == "# JARVIS Vault"
        assert result.content_hash == sha256_hex(b"# JARVIS Vault")
        assert result.size_bytes == len("# JARVIS Vault".encode("utf-8"))

    def test_read_nested_file(self, svc):
        result = svc.read_file("Personal/notes.md")
        assert "My Notes" in result.content

    def test_read_nonexistent(self, svc):
        with pytest.raises(PathNotFoundError):
            svc.read_file("nonexistent.md")

    def test_read_directory_fails(self, svc):
        with pytest.raises(InvalidPathError):
            svc.read_file("Personal")

    def test_path_traversal_rejected(self, svc):
        with pytest.raises(PathTraversalError):
            svc.read_file("../etc/passwd")


class TestCreateFile:
    def test_create_text_file(self, svc, tmp_vault):
        result = svc.create_file("new_file.md", content="# New File")
        assert result.name == "new_file.md"
        assert result.type.value == "file"
        assert (tmp_vault / "new_file.md").read_text(encoding="utf-8") == "# New File"

    def test_create_directory(self, svc, tmp_vault):
        from app.models.file_models import EntryType
        result = svc.create_file("NewFolder", entry_type=EntryType.DIRECTORY)
        assert result.name == "NewFolder"
        assert result.type.value == "directory"
        assert (tmp_vault / "NewFolder").is_dir()

    def test_create_nested_file(self, svc, tmp_vault):
        result = svc.create_file("Personal/diary.md", content="Dear diary")
        assert result.name == "diary.md"
        assert (tmp_vault / "Personal" / "diary.md").exists()

    def test_create_existing_path_fails(self, svc):
        with pytest.raises(PathAlreadyExistsError):
            svc.create_file("readme.md", content="duplicate")

    def test_create_in_nonexistent_parent_auto_creates(self, svc, tmp_vault):
        # create_file auto-creates parent directories
        result = svc.create_file("Missing/file.md", content="orphan")
        assert result.name == "file.md"
        assert (tmp_vault / "Missing" / "file.md").read_text(encoding="utf-8") == "orphan"

    def test_create_empty_file(self, svc, tmp_vault):
        result = svc.create_file("empty.md")
        assert result.size_bytes == 0
        assert (tmp_vault / "empty.md").read_text(encoding="utf-8") == ""


class TestUpdateFile:
    def test_update_existing_file(self, svc, tmp_vault):
        result = svc.update_file("readme.md", content="# Updated Vault")
        assert result.content_hash == sha256_hex(b"# Updated Vault")
        assert (tmp_vault / "readme.md").read_text(encoding="utf-8") == "# Updated Vault"

    def test_update_nonexistent_fails(self, svc):
        with pytest.raises(PathNotFoundError):
            svc.update_file("nonexistent.md", content="data")

    def test_update_directory_fails(self, svc):
        with pytest.raises(InvalidPathError):
            svc.update_file("Personal", content="data")


class TestDeletePath:
    def test_delete_file(self, svc, tmp_vault):
        assert (tmp_vault / "readme.md").exists()
        svc.delete_path("readme.md")
        assert not (tmp_vault / "readme.md").exists()

    def test_delete_directory_recursive(self, svc, tmp_vault):
        assert (tmp_vault / "Personal").exists()
        svc.delete_path("Personal")
        assert not (tmp_vault / "Personal").exists()

    def test_delete_nonexistent_fails(self, svc):
        with pytest.raises(PathNotFoundError):
            svc.delete_path("nonexistent.md")

    def test_delete_nested_file(self, svc, tmp_vault):
        svc.delete_path("Work/project.md")
        assert not (tmp_vault / "Work" / "project.md").exists()
        assert (tmp_vault / "Work").exists()


class TestFileInfoHashing:
    def test_hash_is_sha256_prefixed(self, svc):
        result = svc.read_file("readme.md")
        assert result.content_hash.startswith("sha256:")
        assert len(result.content_hash) == len("sha256:") + 64
