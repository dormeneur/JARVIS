import json
import os
import tempfile
import pytest
from pathlib import Path
from unittest.mock import patch

from app.services.fs_tree import build_fs_tree, persist_fs_tree, load_fs_tree


def test_build_fs_tree():
    """Creates a temp dir with known structure, verifies output list."""
    with tempfile.TemporaryDirectory() as tmpdir:
        root = Path(tmpdir)
        # Create structure:
        # notes/
        # notes/todo.md
        # projects/
        # projects/app/
        # projects/app/main.py
        # README.md
        (root / "notes").mkdir()
        (root / "notes" / "todo.md").write_text("todo")
        (root / "projects").mkdir()
        (root / "projects" / "app").mkdir()
        (root / "projects" / "app" / "main.py").write_text("print()")
        (root / "README.md").write_text("# root")

        tree = build_fs_tree(vault_path=root, max_paths=500)

        assert "README.md" in tree
        assert "notes/" in tree
        assert "notes/todo.md" in tree
        assert "projects/" in tree
        assert "projects/app/" in tree
        assert "projects/app/main.py" in tree
        assert len(tree) == 6


def test_build_fs_tree_respects_cap():
    """Creates 600 files, verifies output is capped at 500."""
    with tempfile.TemporaryDirectory() as tmpdir:
        root = Path(tmpdir)
        for i in range(600):
            (root / f"file_{i:04d}.txt").write_text(f"content {i}")

        tree = build_fs_tree(vault_path=root, max_paths=500)
        assert len(tree) <= 500


def test_build_fs_tree_skips_hidden():
    """Hidden files and directories are excluded."""
    with tempfile.TemporaryDirectory() as tmpdir:
        root = Path(tmpdir)
        (root / ".git").mkdir()
        (root / ".git" / "config").write_text("git config")
        (root / ".hidden_file").write_text("secret")
        (root / "visible.txt").write_text("hello")

        tree = build_fs_tree(vault_path=root, max_paths=500)
        assert "visible.txt" in tree
        assert not any(".git" in p for p in tree)
        assert not any(".hidden" in p for p in tree)


def test_build_fs_tree_empty_dir():
    """Empty vault returns empty list."""
    with tempfile.TemporaryDirectory() as tmpdir:
        tree = build_fs_tree(vault_path=Path(tmpdir), max_paths=500)
        assert tree == []


def test_build_fs_tree_nonexistent():
    """Nonexistent vault path returns empty list."""
    tree = build_fs_tree(vault_path=Path("/nonexistent/path/xyz"), max_paths=500)
    assert tree == []


def test_persist_and_load_roundtrip():
    """Persist then load returns the same tree."""
    test_tree = ["notes/", "notes/todo.md", "projects/", "README.md"]

    with tempfile.TemporaryDirectory() as tmpdir:
        snapshot_path = Path(tmpdir) / "fs_tree.json"
        with patch("app.services.fs_tree._SNAPSHOT_PATH", snapshot_path), \
             patch("app.services.fs_tree._SNAPSHOT_DIR", Path(tmpdir)):
            persist_fs_tree(test_tree)
            loaded = load_fs_tree()
            assert loaded == test_tree


def test_load_fs_tree_missing():
    """Loading when no snapshot file exists returns []."""
    with patch("app.services.fs_tree._SNAPSHOT_PATH", Path("/nonexistent/fs_tree.json")):
        assert load_fs_tree() == []
