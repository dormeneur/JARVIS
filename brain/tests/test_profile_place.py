"""Filing a new fact: the model's choice is held to a safe Profile path; secrets are never filed."""
import asyncio

import pytest

from app.config import settings
from app.services import profile_place as pp


class Fake:
    def __init__(self, path):
        self.path, self.seen = path, None

    async def chat_json(self, messages, schema, **kw):
        self.seen = messages[-1]["content"]
        return {"path": self.path}


@pytest.fixture
def vault(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    for rel in ["Profile/work.md", "Profile/people/family.md", "Profile/hunches.md", "Profile/forms/x/answers.md", "Profile/a/b/deep.md"]:
        (tmp_path / rel).parent.mkdir(parents=True, exist_ok=True)
        (tmp_path / rel).write_text("x", encoding="utf-8")
    return tmp_path


def test_lists_only_knowledge_files_one_folder_deep(vault):
    assert pp.existing_files() == ["Profile/people/family.md", "Profile/work.md"]


@pytest.mark.parametrize("answer, expected", [
    ("Profile/work.md", "Profile/work.md"),
    ("Profile/Travel.md", "Profile/travel.md"),
    ("hobbies", "Profile/hobbies.md"),
    ("Profile/people/friends.md", "Profile/people/friends.md"),
    ("../../Secrets/keys.md", pp.FALLBACK),
    ("Profile/hunches.md", pp.FALLBACK),
    ("Profile/forms/new.md", pp.FALLBACK),
    ("Profile/a/b/c.md", pp.FALLBACK),
    ("Profile/My Hobbies!.md", pp.FALLBACK),
])
def test_answers_are_held_to_a_safe_path(vault, answer, expected):
    assert pp.clean(answer, pp.existing_files()) == expected


def test_place_shows_the_model_the_existing_files(vault):
    fake = Fake("Profile/people/family.md")
    assert asyncio.run(pp.place("My sister is called Priya", fake)) == "Profile/people/family.md"
    assert "Profile/work.md" in fake.seen and "hunches" not in fake.seen


def test_secrets_are_refused_before_the_model_sees_them(vault):
    fake = Fake("Profile/work.md")
    with pytest.raises(ValueError):
        asyncio.run(pp.place("my bank password: hunter22", fake))
    assert fake.seen is None
