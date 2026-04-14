import pytest
from app.services.sanitizer import sanitize_path, sanitize_manifest


# ─── sanitize_path: traversal stripping ──────────────────────────────

def test_sanitize_path_traversal():
    """Traversal sequences are stripped but remaining structure preserved."""
    assert sanitize_path("../../etc/passwd") == "etc/passwd"
    assert sanitize_path("../foo/bar.txt") == "foo/bar.txt"
    assert sanitize_path("..\\something\\evil.sh") == "something/evil.sh"


def test_sanitize_path_absolute_stripped():
    """Leading slashes and drive letters are stripped."""
    assert sanitize_path("/var/log/syslog") == "var/log/syslog"
    assert sanitize_path("C:\\Windows\\System32\\cmd.exe") == "Windows/System32/cmd.exe"


def test_sanitize_path_preserves_structure():
    """Legitimate nested paths are NOT flattened to basename."""
    assert sanitize_path("projects/src/utils.js") == "projects/src/utils.js"
    assert sanitize_path("nested/folder/file.txt") == "nested/folder/file.txt"
    assert sanitize_path("a/b/c.md") == "a/b/c.md"


def test_sanitize_path_dot_segments_removed():
    """Segments that are '.' or empty are cleaned out."""
    assert sanitize_path("a/./b/c.txt") == "a/b/c.txt"
    assert sanitize_path("a//b///c.txt") == "a/b/c.txt"


def test_sanitize_path_null_bytes():
    assert sanitize_path("file\x00.txt") == "file.txt"


# ─── sanitize_path: scope enforcement ────────────────────────────────

def test_sanitize_path_scope_no_rebase():
    """Scope does NOT rebase — bare filenames stay bare.
    Rebasing is the caller's responsibility (e.g. _prefix_shortcut_paths)."""
    result = sanitize_path("utils.js", scope="projects/invoice-app/src")
    assert result == "utils.js"  # NOT rebased


def test_sanitize_path_scope_already_inside():
    """Path already inside scope passes through unchanged."""
    result = sanitize_path("projects/invoice-app/src/utils.js", scope="projects/invoice-app/src")
    assert result == "projects/invoice-app/src/utils.js"


def test_sanitize_path_scope_blocks_traversal():
    """Traversal that would escape scope gets stripped (traversal stripped earlier in pipeline)."""
    result = sanitize_path("../../secrets/key.txt", scope="projects")
    assert ".." not in result
    # After stripping ../, becomes "secrets/key.txt"
    assert result == "secrets/key.txt"


# ─── sanitize_manifest: full pipeline ────────────────────────────────

def test_sanitize_manifest_allowlist():
    raw = [
        {"path": "good.txt", "content": "hello", "type": "file"},
        {"path": "bad.exe", "content": "malware", "type": "file"},
        {"path": "script.py", "content": "print()", "type": "file"}
    ]
    clean = sanitize_manifest(raw)
    assert len(clean) == 2
    assert clean[0]["path"] == "good.txt"
    assert clean[1]["path"] == "script.py"


def test_sanitize_manifest_truncation():
    huge_content = "A" * (60 * 1024)
    raw = [{"path": "big.md", "content": huge_content, "type": "file"}]
    clean = sanitize_manifest(raw)
    assert len(clean) == 1
    assert "[TRUNCATED BY SYSTEM]" in clean[0]["content"]
    assert len(clean[0]["content"]) <= (50 * 1024) + 100


def test_sanitize_manifest_fixes_missing():
    # missing content
    clean = sanitize_manifest([{"path": "empty.md", "type": "file"}])
    assert "Add your content here" in clean[0]["content"]

    # missing name
    clean2 = sanitize_manifest([{"type": "file", "content": "some text"}])
    assert clean2[0]["path"] == "untitled.txt"

    # missing extension
    clean3 = sanitize_manifest([{"path": "missing_ext", "type": "file", "content": "text"}])
    assert clean3[0]["path"] == "missing_ext.txt"


def test_sanitize_manifest_cap():
    raw = [{"path": f"f_{i}.txt", "content": "x"} for i in range(30)]
    clean = sanitize_manifest(raw)
    assert len(clean) == 20


def test_sanitize_manifest_preserves_nested_paths():
    """Manifest items with nested paths preserve their directory structure."""
    raw = [
        {"path": "src/components/Button.jsx", "content": "// button", "type": "file"},
        {"path": "src/utils/helpers.js", "content": "// helpers", "type": "file"},
    ]
    clean = sanitize_manifest(raw)
    assert clean[0]["path"] == "src/components/Button.jsx"
    assert clean[1]["path"] == "src/utils/helpers.js"


def test_sanitize_rejects_excessive_depth():
    """A path with 6+ segments is rejected from the manifest entirely."""
    raw = [
        {"path": "a/b/c/d/e/f/file.txt", "content": "deep", "type": "file"},   # 7 segments — rejected
        {"path": "a/b/c/d/e.txt", "content": "ok", "type": "file"},             # 5 segments — allowed
        {"path": "a/b/c/d/e/f.txt", "content": "edge", "type": "file"},         # 6 segments — rejected
    ]
    clean = sanitize_manifest(raw)
    assert len(clean) == 1
    assert clean[0]["path"] == "a/b/c/d/e.txt"


def test_sanitize_manifest_adversarial_traversal():
    """Adversarial traversal in manifest is stripped, leaving safe residual path."""
    raw = [{"path": "../../secrets/key.txt", "content": "bad", "type": "file"}]
    clean = sanitize_manifest(raw)
    assert len(clean) == 1
    assert ".." not in clean[0]["path"]
    assert clean[0]["path"] == "secrets/key.txt"
