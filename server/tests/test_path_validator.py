import pytest

from app.errors import InvalidPathError, PathTraversalError
from app.services.path_validator import validate_path


class TestValidatePath:
    def test_valid_simple_path(self):
        assert validate_path("notes.md") == "notes.md"

    def test_valid_nested_path(self):
        assert validate_path("Personal/notes.md") == "Personal/notes.md"

    def test_valid_deep_path(self):
        assert validate_path("a/b/c/d/file.txt") == "a/b/c/d/file.txt"

    def test_normalizes_backslashes(self):
        assert validate_path("Personal\\notes.md") == "Personal/notes.md"

    def test_unicode_filename(self):
        assert validate_path("日本語/ファイル.md") == "日本語/ファイル.md"

    def test_allows_gitkeep(self):
        assert validate_path(".gitkeep") == ".gitkeep"

    def test_allows_gitignore(self):
        assert validate_path(".gitignore") == ".gitignore"

    def test_rejects_empty_path(self):
        with pytest.raises(InvalidPathError):
            validate_path("")

    def test_rejects_dotdot(self):
        with pytest.raises(PathTraversalError):
            validate_path("../etc/passwd")

    def test_rejects_dotdot_middle(self):
        with pytest.raises(PathTraversalError):
            validate_path("Personal/../../../etc/passwd")

    def test_rejects_absolute_forward_slash(self):
        with pytest.raises(PathTraversalError):
            validate_path("/etc/passwd")

    def test_rejects_absolute_backslash(self):
        with pytest.raises(PathTraversalError):
            validate_path("\\etc\\passwd")

    def test_rejects_hidden_directory(self):
        with pytest.raises(InvalidPathError):
            validate_path(".secret/file.txt")

    def test_rejects_hidden_file(self):
        with pytest.raises(InvalidPathError):
            validate_path("folder/.hidden")

    def test_rejects_dot_env(self):
        with pytest.raises(InvalidPathError):
            validate_path(".env")

    def test_rejects_too_long_path(self):
        long_path = "a/" * 300 + "file.txt"
        with pytest.raises(InvalidPathError, match="maximum length"):
            validate_path(long_path)

    def test_rejects_too_long_filename(self):
        long_name = "a" * 256 + ".txt"
        with pytest.raises(InvalidPathError, match="Filename exceeds"):
            validate_path(long_name)

    def test_rejects_too_deep_path(self):
        deep_path = "/".join(["d"] * 11) + "/file.txt"
        with pytest.raises(InvalidPathError, match="maximum depth"):
            validate_path(deep_path)
