import asyncio
import sqlite3
from datetime import datetime, timedelta, timezone

import pytest

from app.config import settings
from app.services import proposals, tools as toolkit


@pytest.fixture
def vault(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    (tmp_path / "Profile").mkdir()
    (tmp_path / "Secrets").mkdir()
    return tmp_path


def run(coro):
    return asyncio.run(coro)


def _append(text="- likes tea", path="Profile/preferences.md", title="Remember: likes tea"):
    return proposals.create("memory", "append_to_file", {"path": path, "text": text}, title, text)


def test_create_and_list_newest_first(vault):
    a = _append("- one")
    b = _append("- two")
    assert [p["id"] for p in proposals.list_proposals()] == [b["id"], a["id"]]
    assert proposals.count_pending() == 2 and a["status"] == "pending"


def test_same_pending_proposal_is_not_duplicated(vault):
    assert _append()["id"] == _append()["id"]
    assert proposals.count_pending() == 1


def test_only_plain_write_tools_can_be_proposed(vault):
    with pytest.raises(toolkit.ToolError):
        proposals.create("x", "delete_file", {"path": "Profile/a.md"}, "t", "d")
    with pytest.raises(toolkit.ToolError):
        proposals.create("x", "read_file", {"path": "Profile/a.md"}, "t", "d")
    with pytest.raises(toolkit.ToolError):
        proposals.create("x", "nonsense", {}, "t", "d")


def test_secrets_paths_are_refused_when_filing(vault):
    for path in ("Secrets/x.md", "secrets/x.md", "../outside.md"):
        with pytest.raises(toolkit.ToolError):
            _append(path=path)
    assert proposals.count_pending() == 0


def test_approve_runs_the_tool_once_and_is_idempotent(vault):
    p = _append("- likes tea")
    done = run(proposals.decide(p["id"], True))
    assert done["status"] == "done" and "Created" in done["result"]
    assert (vault / "Profile" / "preferences.md").read_text() == "- likes tea"
    again = run(proposals.decide(p["id"], True))  # a double tap
    assert again["status"] == "done"
    assert (vault / "Profile" / "preferences.md").read_text() == "- likes tea"


def test_reject_changes_nothing(vault):
    p = _append()
    assert run(proposals.decide(p["id"], False))["status"] == "rejected"
    assert not (vault / "Profile" / "preferences.md").exists()


def test_replace_goes_stale_if_the_file_changed_meanwhile(vault):
    target = vault / "Profile" / "work.md"
    target.write_text("Role: engineer", encoding="utf-8")
    p = proposals.create("x", "replace_in_file",
                         {"path": "Profile/work.md", "old_text": "engineer", "new_text": "lead"}, "Promote", "d")
    target.write_text("Role: engineer at Acme", encoding="utf-8")  # edited on another device
    result = run(proposals.decide(p["id"], True))
    assert result["status"] == "stale"
    assert target.read_text() == "Role: engineer at Acme"


def test_append_ignores_unrelated_changes(vault):
    target = vault / "Profile" / "preferences.md"
    target.write_text("- existing\n", encoding="utf-8")
    p = _append("- new")
    target.write_text("- existing\n- edited elsewhere\n", encoding="utf-8")
    assert run(proposals.decide(p["id"], True))["status"] == "done"
    assert target.read_text().endswith("- new")


def test_a_failing_tool_is_recorded_not_raised(vault):
    p = proposals.create("x", "replace_in_file",
                         {"path": "Profile/missing.md", "old_text": "a", "new_text": "b"}, "t", "d")
    # file absent at filing time -> base_hash "absent"; still absent, so the tool itself fails
    result = run(proposals.decide(p["id"], True))
    assert result["status"] == "failed" and "no such file" in result["result"]


def test_unknown_id_raises_key_error(vault):
    with pytest.raises(KeyError):
        run(proposals.decide("nope", True))


def test_old_proposals_expire(vault):
    p = _append()
    old = (datetime.now(tz=timezone.utc) - timedelta(days=15)).isoformat()
    with sqlite3.connect(vault / ".system" / "brain" / "proposals.db") as conn:
        conn.execute("UPDATE proposals SET created_at = ?", (old,))
    assert run(proposals.decide(p["id"], True))["status"] == "expired"
    assert not (vault / "Profile" / "preferences.md").exists()


def test_expire_old_marks_pending_rows(vault):
    _append()
    old = (datetime.now(tz=timezone.utc) - timedelta(days=20)).isoformat()
    with sqlite3.connect(vault / ".system" / "brain" / "proposals.db") as conn:
        conn.execute("UPDATE proposals SET created_at = ?", (old,))
    assert proposals.expire_old() == 1 and proposals.count_pending() == 0


def test_two_devices_approving_at_once_run_it_once(vault):
    p = _append("- once")

    async def both():
        return await asyncio.gather(proposals.decide(p["id"], True), proposals.decide(p["id"], True))

    a, b = run(both())
    assert a["status"] == b["status"] == "done"
    assert (vault / "Profile" / "preferences.md").read_text() == "- once"
