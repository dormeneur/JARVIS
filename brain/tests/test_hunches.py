"""Hunches: the stack across sessions, the daily cap, where answers land, and what is never asked."""
import asyncio
import json
from datetime import datetime, timezone

import pytest

from app.config import settings
from app.services import hunches


@pytest.fixture
def vault(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    monkeypatch.setattr(hunches, "schedule_generate", lambda: None)  # no background model calls in tests
    return tmp_path


def add(trait="Works late at night", question="You do your real work after 10 pm, don't you?"):
    with hunches._connect() as conn:
        hid = f"h{conn.execute('SELECT COUNT(*) FROM hunches').fetchone()[0]}"
        conn.execute(
            "INSERT INTO hunches (id, created, trait, topic, question, options, evidence, status) VALUES (?,?,?,?,?,?,?,?)",
            (hid, datetime.now(timezone.utc).isoformat(), trait, "habits", question,
             json.dumps(["Spot on", "Only on deadlines", "Morning person"]), json.dumps(["VS Code at 23:00"]), "open"),
        )
    return hid


class FakeClient:
    def __init__(self, reply):
        self.reply, self.prompts = reply, []

    async def chat_json(self, messages, schema, **kw):
        self.prompts.append(messages[-1]["content"])
        return self.reply


GOOD = {"trait": "Learns by watching", "topic": "learning", "question": "You learn new tools from videos, not docs, right?",
        "options": ["Always", "Depends", "I read docs"], "evidence": ["9 tutorial videos, 1 docs page"]}


def test_skip_stacks_until_the_next_session(vault):
    a, b = add(), add("Plans on paper", "You sketch plans on paper before coding, don't you?")
    first = hunches.next_hunch("s1")["hunch"]
    assert first["id"] == a
    hunches.skip(a, "s1")
    assert hunches.next_hunch("s1")["hunch"]["id"] == b  # not asked again this session
    again = hunches.next_hunch("s2")
    assert again["hunch"]["id"] == a and again["stacked"] == 1  # the stack comes first next time


def test_a_hunch_is_served_once_per_session(vault):
    a, b = add(), add("Plans on paper", "You sketch plans on paper before coding, don't you?")
    assert hunches.next_hunch("s1")["hunch"]["id"] == a
    assert hunches.next_hunch("s1")["hunch"]["id"] == b  # a, unanswered, isn't served again now
    assert hunches.next_hunch("s1")["hunch"] is None
    assert hunches.next_hunch("s2")["hunch"]["id"] == a  # but is next session


def test_daily_cap_applies_only_when_unprompted(vault):
    ids = [add(f"Trait number {i} unique{i}", f"Question {i} about thing{i} here?") for i in range(hunches.DAILY_CAP + 2)]
    served = [hunches.next_hunch("s")["hunch"] for _ in range(hunches.DAILY_CAP)]
    for h in served:
        hunches.answer(h["id"], 0)
    assert hunches.next_hunch("s")["hunch"] is None
    assert hunches.next_hunch("s", on_demand=True)["hunch"]["id"] in ids


def test_answers_land_in_readable_files(vault):
    yes, no, typed = add(), add("Hates meetings", "You'd rather write than sit in a meeting?"), add("Tea person", "Tea over coffee?")
    assert hunches.answer(yes, 0)["saved_to"] == hunches.TRAITS
    assert hunches.answer(no, 2)["saved_to"] == hunches.NOT_ME
    hunches.answer(typed, text="Coffee, always")
    traits = (vault / hunches.TRAITS).read_text(encoding="utf-8")
    assert "Works late at night — yes: “Spot on”" in traits and "in your words: “Coffee, always”" in traits
    assert "Hates meetings" in (vault / hunches.NOT_ME).read_text(encoding="utf-8")
    assert hunches.answer(yes, 0)["said"] == "Already answered."  # double tap
    assert "none" in (vault / hunches.VIEW).read_text(encoding="utf-8")


def test_answer_needs_a_choice_or_text(vault):
    with pytest.raises(ValueError):
        hunches.answer(add())


def test_clean_drops_sensitive_malformed_and_repeats():
    bad = [
        dict(GOOD, trait="Worried about health", question="You've been anxious about your health lately?"),
        dict(GOOD, options=["Yes", "No"]),
        dict(GOOD, evidence=[]),
        dict(GOOD, trait="Learns by watching videos"),  # a repeat of an existing guess
    ]
    out = hunches.clean({"hunches": bad + [dict(GOOD, trait="Keeps a tidy desktop", question="You clean up your files every Friday?")]},
                        existing=["Learns by watching videos"])
    assert [h["trait"] for h in out] == ["Keeps a tidy desktop"]
    assert hunches.clean("not json", []) == []


def test_secret_looking_titles_are_never_stored(vault):
    n = hunches.record_observations([
        {"app": "chrome", "title": "Your code is 482913", "seconds": 30},
        {"app": "chrome", "title": "Rust tutorial - YouTube", "seconds": 30},
        {"app": "", "title": "x", "seconds": 30},
    ])
    assert n == 1


def test_generate_uses_evidence_and_files_new_hunches(vault):
    hunches.record_observations([{"app": "Code", "title": "agent.py - JARVIS", "seconds": 1800}])
    client = FakeClient({"hunches": [GOOD]})
    assert asyncio.run(hunches.generate(client)) == 1
    assert "agent.py - JARVIS" in client.prompts[0]
    assert GOOD["question"] in (vault / hunches.VIEW).read_text(encoding="utf-8")
    assert asyncio.run(hunches.generate(FakeClient({"hunches": [GOOD]}))) == 0  # never the same guess twice


def test_generate_needs_something_noticed_first(vault):
    assert asyncio.run(hunches.generate(FakeClient({"hunches": [GOOD]}))) == 0


def test_describe_activity_summarises_apps_titles_and_hours():
    rows = [{"app": "Code", "title": "main.py", "seconds": 3600, "at": "2026-11-20T22:30:00+00:00"},
            {"app": "chrome", "title": "Rust - YouTube", "seconds": 600, "at": "2026-11-21T10:00:00+00:00"}]
    text = hunches.describe_activity(rows, tz=timezone.utc)
    assert "Code: 60 min — main.py" in text and "Rust - YouTube" in text and "Busiest hours" in text


def test_busiest_hours_are_on_the_users_clock_not_the_containers():
    from zoneinfo import ZoneInfo

    rows = [{"app": "Code", "title": "x", "seconds": 3600, "at": "2026-11-20T17:00:00+00:00"}]  # 22:30 in India
    assert "22:00" in hunches.describe_activity(rows, tz=ZoneInfo("Asia/Kolkata"))


def test_pasted_summary_lines_are_not_evidence():
    out = hunches.clean({"hunches": [dict(GOOD, evidence=["Apps by time, with their most-used windows: - chrome", "- 9 tutorials this week"])]}, [])
    assert out[0]["evidence"] == ["9 tutorials this week"]


def test_bare_labels_become_replies_a_person_would_tap():
    out = hunches.clean({"hunches": [dict(GOOD, options=["Agree", "partly", "Disagree."])]}, [])
    assert out[0]["options"] == ["Spot on", "Sort of", "Not really"]


def test_a_few_minutes_of_activity_is_not_enough_to_guess(vault):
    hunches.record_observations([{"app": "Code", "title": "x", "seconds": 300}])
    assert asyncio.run(hunches.generate(FakeClient({"hunches": [GOOD]}))) == 0
