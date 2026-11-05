import asyncio
from unittest.mock import AsyncMock, MagicMock

import pytest

from app.config import settings
from app.services import memory_extractor as me, proposals


@pytest.fixture
def vault(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    monkeypatch.setattr(settings, "memory_extraction", True)  # conftest turns it off by default
    (tmp_path / "Profile").mkdir()
    return tmp_path


def run(coro):
    return asyncio.run(coro)


def client_returning(reply):
    c = MagicMock()
    c.chat_json = AsyncMock(return_value=reply)
    return c


TEXT = "I work at Acme as a backend engineer and I prefer tea over coffee"


def test_facts_become_one_proposal_per_topic(vault):
    reply = {"facts": [{"topic": "work", "fact": "Works at Acme as a backend engineer"},
                       {"topic": "preferences", "fact": "Prefers tea over coffee"}]}
    assert run(me.extract_and_propose(TEXT, client_returning(reply))) == 2
    pending = {p["arguments"]["path"]: p for p in proposals.list_proposals()}
    assert set(pending) == {"Profile/work.md", "Profile/preferences.md"}
    assert pending["Profile/work.md"]["arguments"]["text"] == "- Works at Acme as a backend engineer"
    assert pending["Profile/work.md"]["tool"] == "append_to_file"
    assert not (vault / "Profile" / "work.md").exists(), "nothing is written before approval"


def test_a_fact_already_in_the_profile_or_inbox_is_not_proposed_again(vault):
    (vault / "Profile" / "work.md").write_text("- Works at  ACME as a backend engineer\n", encoding="utf-8")
    reply = {"facts": [{"topic": "work", "fact": "Works at Acme as a backend engineer"},
                       {"topic": "preferences", "fact": "Prefers tea over coffee"}]}
    assert run(me.extract_and_propose(TEXT, client_returning(reply))) == 1
    assert run(me.extract_and_propose(TEXT, client_returning(reply))) == 0  # already pending now


def test_secret_looking_text_never_reaches_the_model(vault):
    c = client_returning({"facts": []})
    for text in ["my password: hunter2hunter2 for the wifi at home", "card 4111111111111111 is my main card ok",
                 "key sk-abcdefghijklmnopqrstuvwxyz0123 is mine, remember it"]:
        assert run(me.extract_and_propose(text, c)) == 0
    c.chat_json.assert_not_called()


def test_short_text_and_disabled_flag_skip_the_model(vault, monkeypatch):
    c = client_returning({"facts": []})
    assert run(me.extract_and_propose("hi there", c)) == 0
    monkeypatch.setattr(settings, "memory_extraction", False)
    assert run(me.extract_and_propose(TEXT, c)) == 0
    c.chat_json.assert_not_called()


def test_busy_extractor_skips_instead_of_queueing(vault):
    c = client_returning({"facts": [{"topic": "work", "fact": "Works at Acme"}]})

    async def scenario():
        async with me._busy:
            return await me.extract_and_propose(TEXT, c)

    assert run(scenario()) == 0
    c.chat_json.assert_not_called()


def test_model_failure_and_garbage_return_zero_not_an_exception(vault):
    boom = MagicMock()
    boom.chat_json = AsyncMock(side_effect=RuntimeError("ollama down"))
    assert run(me.extract_and_propose(TEXT, boom)) == 0
    assert run(me.extract_and_propose(TEXT, client_returning("not a dict"))) == 0
    assert run(me.extract_and_propose(TEXT, client_returning({"facts": [{"topic": "bogus", "fact": "x"}, 5, {}]}))) == 0


def test_clean_caps_count_and_length():
    raw = {"facts": [{"topic": "about", "fact": "x" * 500}] + [{"topic": "about", "fact": f"f{i}"} for i in range(10)]}
    cleaned = me._clean(raw)
    assert len(cleaned) == me.MAX_FACTS and len(cleaned[0]["fact"]) == me.MAX_FACT_CHARS


def test_a_quoted_assistant_answer_is_never_mined(vault):
    quoted = ('Regarding this earlier answer of yours:\n"""\nYou work at Globex as a pilot.\n"""\n\n'
              'Thanks a lot.')
    assert me.own_words(quoted) == "Thanks a lot."
    c = client_returning({"facts": [{"topic": "work", "fact": "Works at Globex as a pilot"}]})
    assert run(me.extract_and_propose(quoted, c)) == 0  # too short once the quote is removed -> no model call
    c.chat_json.assert_not_called()


def test_the_users_own_words_alongside_a_quote_still_count(vault):
    quoted = ('Regarding this earlier message of mine:\n"""\nhello\n"""\n\n'
              'Also note that I prefer tea over coffee in the mornings.')
    c = client_returning({"facts": [{"topic": "preferences", "fact": "Prefers tea over coffee"}]})
    assert run(me.extract_and_propose(quoted, c)) == 1
    sent = c.chat_json.call_args.args[0][1]["content"]
    assert "hello" not in sent and "prefer tea" in sent
