"""Saving outside text: the original is kept word for word; invented summary, tags or facts are dropped."""
import asyncio

import pytest

from app.config import settings
from app.services import ingest, profile_place


class Fake:
    def __init__(self, reply):
        self.reply, self.calls = reply, 0

    async def chat_json(self, messages, schema, **kw):
        self.calls += 1
        if "file one fact" in messages[0]["content"]:
            return {"path": "Profile/skills.md"}
        return self.reply


TEXT = "Rust ownership means each value has a single owner; borrowing lets code use a value without taking it."


@pytest.fixture
def vault(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    for folder in ["Learning", "Secrets", "Profile", ".system", "Agent"]:
        (tmp_path / folder).mkdir()
    return tmp_path


def test_plans_a_note_in_the_right_folder_and_keeps_the_text(vault):
    reply = {"title": "Rust ownership basics", "folder": "learning", "summary": "Each value in Rust has a single owner.",
             "tags": ["rust", "ownership", "kubernetes"], "about_me": []}
    out = asyncio.run(ingest.plan(TEXT, "Chrome", "https://doc.rust-lang.org", Fake(reply)))
    assert out["note"]["path"] == "Learning/Rust ownership basics.md"
    content = out["note"]["content"]
    assert TEXT in content and "url: https://doc.rust-lang.org" in content
    assert "tags: [rust, ownership]" in content  # "kubernetes" isn't in the text
    assert out["facts"] == []


def test_invented_summary_and_facts_are_dropped(vault):
    reply = {"title": "x", "folder": "Learning", "summary": "Kubernetes orchestrates containers across clusters.",
             "tags": [], "about_me": ["I am an expert Kubernetes engineer"]}
    out = asyncio.run(ingest.plan(TEXT, "", None, Fake(reply)))
    assert "> " not in out["note"]["content"]
    assert out["facts"] == []


def test_facts_about_the_owner_go_to_profile(vault):
    text = "About me: I have been learning Rust for two years and I build desktop apps."
    reply = {"title": "My intro", "folder": "Learning", "summary": "", "tags": [], "about_me": ["I have been learning Rust for two years"]}
    out = asyncio.run(ingest.plan(text, "", None, Fake(reply)))
    assert out["facts"] == [{"path": "Profile/skills.md", "fact": "I have been learning Rust for two years"}]


@pytest.mark.parametrize("folder, expected", [("Secrets", "Clippings"), ("Profile", "Clippings"), ("../x", "Clippings"),
                                              ("learning", "Learning"), ("Recipes", "Recipes"), ("", "Clippings")])
def test_folders_are_held_to_safe_places(vault, folder, expected):
    assert ingest.clean_folder(folder, ingest.folders()) == expected


def test_never_overwrites_and_refuses_secrets(vault):
    (vault / "Learning" / "Rust ownership basics.md").write_text("old", encoding="utf-8")
    out = asyncio.run(ingest.plan(TEXT, "", None, Fake({"title": "Rust ownership basics", "folder": "Learning", "summary": "", "tags": [], "about_me": []})))
    assert out["note"]["path"] == "Learning/Rust ownership basics (2).md"
    with pytest.raises(ValueError):
        asyncio.run(ingest.plan("my password: hunter22", "", None, Fake({})))


def test_a_down_model_still_saves_the_text(vault):
    class Down:
        async def chat_json(self, *a, **kw):
            raise RuntimeError("ollama is down")

    out = asyncio.run(ingest.plan(TEXT, "", None, Down()))
    assert out["note"]["path"].startswith("Clippings/") and TEXT in out["note"]["content"]
