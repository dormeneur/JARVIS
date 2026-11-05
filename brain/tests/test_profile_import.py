import asyncio
from unittest.mock import AsyncMock, MagicMock

import pytest

from app.config import settings
from app.services import profile_import as pi, proposals


@pytest.fixture
def vault(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    (tmp_path / "Personal").mkdir()
    (tmp_path / "Profile").mkdir()
    (tmp_path / "Secrets").mkdir()
    (tmp_path / "Personal" / "me.md").write_text("I am a student in Vellore. I code in Dart.", encoding="utf-8")
    (tmp_path / "Profile" / "about.md").write_text("not read by the import", encoding="utf-8")
    (tmp_path / "Secrets" / "k.txt").write_text("classified", encoding="utf-8")
    return tmp_path


def run(coro):
    return asyncio.run(coro)


def client_returning(*replies):
    c = MagicMock()
    c.chat_json = AsyncMock(side_effect=list(replies) if len(replies) > 1 else None, return_value=replies[0])
    return c


FACTS = {"facts": [
    {"topic": "education", "fact": "Studies Computer Science at Vellore Institute of Technology"},
    {"topic": "skills", "fact": "Writes Dart and Flutter"},
    {"topic": "skills", "fact": "writes  dart and flutter"},          # same fact, other spacing/case
    {"topic": "about", "fact": "Passport number K1234567"},          # an ID number: dropped
    {"topic": "about", "fact": "Card 4111111111111111 is my main card"},  # secret: dropped
]}


def test_facts_become_one_proposal_per_topic_and_secrets_never_do(vault):
    c = client_returning(FACTS)
    result = run(pi.run("Personal", client=c))
    assert result["files_read"] == 1 and result["proposals_filed"] == 2
    assert result["topics"] == {"education": 1, "skills": 1}
    by_path = {p["arguments"]["path"]: p for p in proposals.list_proposals()}
    assert set(by_path) == {"Profile/education.md", "Profile/skills.md"}
    assert by_path["Profile/skills.md"]["arguments"]["text"] == "- Writes Dart and Flutter"
    assert by_path["Profile/skills.md"]["source"] == "documents"
    assert not (vault / "Profile" / "skills.md").exists(), "nothing is written before approval"
    sent = [call.args[0][1]["content"] for call in c.chat_json.call_args_list]
    assert all("not read by the import" not in s and "classified" not in s for s in sent)


def test_second_run_reads_nothing_until_a_file_changes(vault):
    run(pi.run("Personal", client=client_returning(FACTS)))
    again = client_returning(FACTS)
    result = run(pi.run("Personal", client=again))
    assert result["files_read"] == 0 and result["files_unchanged"] == 1
    again.chat_json.assert_not_called()
    (vault / "Personal" / "me.md").write_text("I am a student in Vellore. I code in Dart. New fact.", encoding="utf-8")
    assert run(pi.run("Personal", client=client_returning({"facts": []})))["files_read"] == 1
    forced = run(pi.run("Personal", force=True, client=client_returning({"facts": []})))
    assert forced["files_read"] == 1


def test_dry_run_files_nothing_and_remembers_nothing(vault):
    result = run(pi.run("Personal", dry_run=True, client=client_returning(FACTS)))
    assert result["facts_new"] == 2 and result["proposals_filed"] == 0
    assert proposals.count_pending() == 0 and pi.load_state() == {}


def test_facts_already_in_the_profile_are_not_proposed(vault):
    (vault / "Profile" / "skills.md").write_text("- Writes Dart and Flutter\n", encoding="utf-8")
    result = run(pi.run("Personal", client=client_returning(FACTS)))
    assert result["topics"] == {"education": 1}


def test_a_failing_model_call_skips_that_file_only(vault):
    (vault / "Personal" / "b.md").write_text("Another document about me.", encoding="utf-8")
    c = MagicMock()
    c.chat_json = AsyncMock(side_effect=[RuntimeError("ollama down"), FACTS])
    result = run(pi.run("Personal", client=c))
    assert result["files_read"] == 2 and result["proposals_filed"] == 2
    assert len(pi.load_state()) == 1  # the file whose window failed is retried next time


def test_many_facts_split_into_several_proposals(vault):
    many = {"facts": [{"topic": "skills", "fact": f"Knows tool number {i}"} for i in range(10)]}
    (vault / "Personal" / "b.md").write_text("Second document about me.", encoding="utf-8")
    c = MagicMock()
    c.chat_json = AsyncMock(side_effect=[many, {"facts": [{"topic": "skills", "fact": f"Knows other thing {i}"} for i in range(10)]}])
    result = run(pi.run("Personal", client=c))
    assert result["facts_new"] == 20 and result["proposals_filed"] == 2
    assert all("part" in p["title"] for p in proposals.list_proposals())


def test_long_documents_are_read_in_windows_from_the_top():
    assert len(pi.windows("x" * 100)) == 1
    parts = pi.windows("x" * (pi.WINDOW_CHARS * 5))
    assert len(parts) == pi.MAX_WINDOWS and all(len(p) == pi.WINDOW_CHARS for p in parts)


def test_folder_matching_ignores_case_and_other_folders(vault):
    result = run(pi.run("personal/", client=client_returning(FACTS)))
    assert result["files_read"] == 1
    assert run(pi.run("Nothing", client=client_returning(FACTS)))["files_read"] == 0


def test_one_bad_window_keeps_the_files_other_windows(vault):
    (vault / "Personal" / "me.md").write_text("a" * (pi.WINDOW_CHARS + 50), encoding="utf-8")  # two windows
    c = MagicMock()
    c.chat_json = AsyncMock(side_effect=[ValueError("cut-off JSON"), FACTS])
    result = run(pi.run("Personal", client=c))
    assert result["facts_new"] == 2 and pi.load_state() == {}, "kept the good window, will retry the file"
