"""Safety guards for the agent tool layer.

The model chooses these arguments, so the vault boundary and the /Secrets
exclusion have to hold against hostile or confused input — not just typos.
"""

import asyncio

import pytest

from app.config import settings
from app.services import tools as toolkit


@pytest.fixture
def vault(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    (tmp_path / "Notes").mkdir()
    (tmp_path / "Notes" / "hello.md").write_text("hello world", encoding="utf-8")
    (tmp_path / "Secrets").mkdir()
    (tmp_path / "Secrets" / "keys.jvs").write_text("TOPSECRET", encoding="utf-8")
    return tmp_path


def run(coro):
    return asyncio.run(coro)


# --- containment -----------------------------------------------------------

@pytest.mark.parametrize(
    "escape",
    [
        "../outside.txt",
        "../../etc/passwd",
        "Notes/../../outside.txt",
        "Notes/../../../..",
    ],
)
def test_traversal_cannot_escape_vault(vault, escape):
    with pytest.raises(toolkit.ToolError):
        toolkit.safe_path(escape, must_exist=False)


@pytest.mark.parametrize(
    "absolute",
    ["/etc/passwd", "C:/Windows/system32/drivers/etc/hosts", "/Notes/hello.md"],
)
def test_absolute_paths_are_rerooted_into_the_vault(vault, absolute):
    """Absolute-looking paths are re-rooted, not honoured.

    A model writing "/etc/passwd" must not touch the host's /etc — it lands
    at <vault>/etc/passwd instead. Contained, so it is allowed to resolve.
    """
    resolved = toolkit.safe_path(absolute, must_exist=False)
    assert vault in resolved.parents or resolved == vault


def test_absolute_looking_path_resolves_to_vault_file(vault):
    # "/Notes/hello.md" should mean the vault's Notes, not the filesystem root.
    assert toolkit.safe_path("/Notes/hello.md", must_exist=True).exists()


# --- /Secrets is unreachable by every tool ---------------------------------

@pytest.mark.parametrize("path", ["Secrets", "Secrets/keys.jvs", "/Secrets/keys.jvs"])
def test_secrets_is_blocked(vault, path):
    with pytest.raises(toolkit.ToolError, match="off-limits"):
        toolkit.safe_path(path)


def test_read_file_cannot_exfiltrate_secrets(vault):
    with pytest.raises(toolkit.ToolError):
        run(toolkit.execute("read_file", {"path": "Secrets/keys.jvs"}))


def test_delete_cannot_target_secrets(vault):
    with pytest.raises(toolkit.ToolError):
        run(toolkit.execute("delete_file", {"path": "Secrets/keys.jvs"}))
    assert (vault / "Secrets" / "keys.jvs").exists()


def test_list_directory_hides_secrets(vault):
    out = run(toolkit.execute("list_directory", {"path": "."}))
    assert "Secrets" not in out
    assert "Notes/" in out


# --- risk classification drives the approval prompt ------------------------

def test_read_tools_need_no_approval():
    for name in ("read_file", "list_directory", "search_vault"):
        assert not toolkit.TOOLS[name].requires_approval


def test_mutating_tools_require_approval():
    for name in ("create_file", "edit_file", "move_file", "delete_file"):
        assert toolkit.TOOLS[name].requires_approval


def test_unknown_tool_defaults_to_risky():
    # An invented tool name must never fall through as auto-approved.
    assert toolkit.risk_of("rm_minus_rf") != toolkit.RISK_READ


# --- happy paths -----------------------------------------------------------

def test_create_then_read_roundtrip(vault):
    run(toolkit.execute("create_file", {"path": "Notes/new.md", "content": "abc"}))
    assert (vault / "Notes" / "new.md").read_text() == "abc"
    out = run(toolkit.execute("read_file", {"path": "Notes/new.md"}))
    assert "abc" in out


def test_read_pdf_uses_extractor_not_raw_bytes(vault, monkeypatch):
    (vault / "Notes" / "cv.pdf").write_bytes(b"%PDF-1.4\x00\xff binary junk")
    monkeypatch.setattr(
        toolkit.DocumentLoader, "_extract_content", lambda self, p: "Interned at Acme"
    )
    out = run(toolkit.execute("read_file", {"path": "Notes/cv.pdf"}))
    assert "Interned at Acme" in out and "%PDF" not in out


def test_create_refuses_to_clobber(vault):
    with pytest.raises(toolkit.ToolError, match="already exists"):
        run(toolkit.execute("create_file", {"path": "Notes/hello.md", "content": "x"}))
    assert (vault / "Notes" / "hello.md").read_text() == "hello world"


def test_move_into_existing_folder_keeps_filename(vault):
    (vault / "Archive").mkdir()
    run(toolkit.execute("move_file", {"source": "Notes/hello.md", "destination": "Archive"}))
    assert (vault / "Archive" / "hello.md").exists()
    assert not (vault / "Notes" / "hello.md").exists()


def test_binary_extensions_are_not_writable(vault):
    with pytest.raises(toolkit.ToolError, match="cannot write"):
        run(toolkit.execute("create_file", {"path": "Notes/evil.exe", "content": "x"}))


def test_tool_errors_are_returned_not_raised_as_crashes(vault):
    with pytest.raises(toolkit.ToolError, match="no such file"):
        run(toolkit.execute("read_file", {"path": "Notes/missing.md"}))


def test_schemas_are_wellformed_for_ollama():
    for schema in toolkit.tool_schemas():
        assert schema["type"] == "function"
        fn = schema["function"]
        assert fn["name"] and fn["description"]
        assert fn["parameters"]["type"] == "object"
        for req in fn["parameters"].get("required", []):
            assert req in fn["parameters"]["properties"], f"{fn['name']}: {req} undeclared"


# --- casing / attachment paths ------------------------------------------------

@pytest.mark.parametrize("path", ["secrets/keys.jvs", "SECRETS/keys.jvs", "System/devices.json"])
def test_excluded_folders_blocked_regardless_of_case(vault, path):
    # The vault is a case-insensitive Windows bind mount in production.
    with pytest.raises(toolkit.ToolError):
        toolkit.safe_path(path)


@pytest.mark.parametrize("path", ["Secrets/keys.jvs", "../outside.txt"])
def test_rag_attachments_cannot_pull_secrets_or_escape(vault, path):
    from app.services.context_assembler import ContextAssembler

    assembler = ContextAssembler(text_chunker=None, document_loader=None)
    assert assembler._load_attachment(path) is None


# --- targeted edits, truncation guard, trash (task 0.4) ----------------------

def _call(name, **args):
    return run(toolkit.execute(name, args))


def test_replace_in_file_exactly_once(vault):
    (vault / "Notes" / "r.md").write_text("one two three", encoding="utf-8")
    assert "Edited" in _call("replace_in_file", path="Notes/r.md", old_text="two", new_text="2")
    assert (vault / "Notes" / "r.md").read_text() == "one 2 three"


def test_replace_in_file_not_found_and_ambiguous(vault):
    (vault / "Notes" / "r.md").write_text("aa aa", encoding="utf-8")
    with pytest.raises(toolkit.ToolError, match="not found"):
        _call("replace_in_file", path="Notes/r.md", old_text="zz", new_text="x")
    with pytest.raises(toolkit.ToolError, match="2 times"):
        _call("replace_in_file", path="Notes/r.md", old_text="aa", new_text="x")
    with pytest.raises(toolkit.ToolError, match="empty"):
        _call("replace_in_file", path="Notes/r.md", old_text="", new_text="x")


def test_replace_in_file_handles_crlf(vault):
    (vault / "Notes" / "w.md").write_bytes(b"line1\r\nline2\r\nline3\r\n")
    _call("replace_in_file", path="Notes/w.md", old_text="line1\r\nline2", new_text="X")
    assert (vault / "Notes" / "w.md").read_bytes() == b"X\nline3\n"


def test_append_creates_and_adds_missing_newline(vault):
    _call("append_to_file", path="Profile/skills.md", text="- python")
    assert (vault / "Profile" / "skills.md").read_text() == "- python"
    _call("append_to_file", path="Profile/skills.md", text="- rust\n")
    assert (vault / "Profile" / "skills.md").read_text() == "- python\n- rust\n"


def test_append_respects_size_cap(vault):
    with pytest.raises(toolkit.ToolError, match="exceeds"):
        _call("append_to_file", path="Notes/big.md", text="x" * (toolkit.MAX_WRITE_BYTES + 1))


def test_edit_file_refuses_a_truncated_rewrite(vault):
    (vault / "Notes" / "big.md").write_text("word " * 600, encoding="utf-8")  # 3000 chars
    with pytest.raises(toolkit.ToolError, match="truncated"):
        _call("edit_file", path="Notes/big.md", content="word " * 100)
    assert len((vault / "Notes" / "big.md").read_text()) == 3000


def test_edit_file_allows_short_rewrite_of_a_small_file(vault):
    (vault / "Notes" / "small.md").write_text("w " * 250, encoding="utf-8")  # 500 chars
    _call("edit_file", path="Notes/small.md", content="new")
    assert (vault / "Notes" / "small.md").read_text() == "new"


def test_edits_keep_the_old_version_in_trash(vault):
    (vault / "Notes" / "t.md").write_text("original", encoding="utf-8")
    _call("edit_file", path="Notes/t.md", content="changed")
    kept = list((vault / ".system" / "brain" / "trash").rglob("t.md"))
    assert len(kept) == 1 and kept[0].read_text() == "original"


def test_delete_moves_to_trash_and_prune_removes_old(vault):
    (vault / "Notes" / "d").mkdir()
    (vault / "Notes" / "d" / "x.md").write_text("keep me", encoding="utf-8")
    out = _call("delete_file", path="Notes/d")
    assert "trash" in out and not (vault / "Notes" / "d").exists()
    stamp = next((vault / ".system" / "brain" / "trash").iterdir())
    assert (stamp / "Notes" / "d" / "x.md").read_text() == "keep me"

    assert toolkit.prune_trash() == 0  # fresh
    old = stamp.parent / "20200101-000000-000000"
    stamp.rename(old)
    assert toolkit.prune_trash() == 1 and not old.exists()


@pytest.mark.parametrize("tool,args", [
    ("append_to_file", {"path": "Secrets/x.md", "text": "a"}),
    ("append_to_file", {"path": "secrets/x.md", "text": "a"}),
    ("replace_in_file", {"path": "secrets/keys.jvs", "old_text": "TOP", "new_text": "x"}),
])
def test_new_tools_refuse_secrets(vault, tool, args):
    with pytest.raises(toolkit.ToolError):
        _call(tool, **args)
    assert (vault / "Secrets" / "keys.jvs").read_text() == "TOPSECRET"
