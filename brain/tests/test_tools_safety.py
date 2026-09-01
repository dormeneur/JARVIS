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
