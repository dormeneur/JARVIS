"""Form answers: only vault-backed answers survive, and /Secrets never reaches the model."""

import json
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from app.api import app
from app.config import settings
from app.models.ask_models import Source
from app.routers import forms
from app.routers.forms import FormQuestion, accept

GIVEN = {
    "Profile/about.md": "# About\n- **Name:** Asha Rao\n- Open to: Position 1, Position 2; Yes",
    "Profile/standard-answers.md": "## Why us?",
}
EV = "Name: Asha Rao"


def q(kind="text", options=None):
    return FormQuestion(id="q1", text="Name", kind=kind, options=options or [])


def test_answer_needs_a_given_source():
    assert accept(q(), "Asha Rao", "Profile/about.md", EV, GIVEN) == ("Asha Rao", "Profile/about.md")
    assert accept(q(), "Asha Rao", "about.md", EV, GIVEN) == ("Asha Rao", "Profile/about.md")
    assert accept(q(), "Asha Rao", "Personal/diary.md", EV, GIVEN) is None
    assert accept(q(), "Asha Rao", "", EV, GIVEN) is None


@pytest.mark.parametrize("empty", ["", "  ", "null", "Unknown.", "N/A"])
def test_non_answers_are_unknown(empty):
    assert accept(q(), empty, "Profile/about.md", EV, GIVEN) is None


def test_evidence_must_be_in_the_cited_file():
    assert accept(q(), "Asha Rao", "Profile/about.md", "Name: Asha R. Rao", GIVEN) is None
    assert accept(q(), "Asha Rao", "Profile/about.md", "", GIVEN) is None


def test_choice_must_be_an_offered_option():
    choice = q("choice", ["Yes", "No"])
    assert accept(choice, "yes", "Profile/about.md", "Yes", GIVEN) == ("Yes", "Profile/about.md")
    assert accept(choice, "Maybe", "Profile/about.md", "Yes", GIVEN) is None
    boxes = q("checkbox", ["Position 1", "Position 2"])
    assert accept(boxes, "position 1; Position 2", "Profile/about.md", "Yes", GIVEN)[0] == "Position 1; Position 2"
    assert accept(boxes, "Position 1; Position 9", "Profile/about.md", "Yes", GIVEN) is None


@pytest.fixture
def vault(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    (tmp_path / "Profile").mkdir()
    (tmp_path / "Profile" / "about.md").write_text("Name: Asha Rao", encoding="utf-8")
    (tmp_path / "Secrets").mkdir()
    (tmp_path / "Secrets" / "keys.md").write_text("TOPSECRET", encoding="utf-8")
    return tmp_path


def test_stream_answers_from_profile_and_never_sends_secrets(vault, monkeypatch):
    sent = []

    async def fake_retrieve(self, query, top_k=5, filter_paths=None):
        return [Source(path="Secrets/keys.md", chunk=0, score=0.9, content="TOPSECRET")]

    async def fake_chat_json(self, messages, schema, model=None):
        sent.append(json.dumps(messages))
        return {"answers": [
            {"id": "name", "answer": "Asha Rao", "source": "Profile/about.md", "evidence": "Name: Asha Rao"},
            {"id": "salary", "answer": "$100k", "source": "Profile/about.md", "evidence": "Asha"},
        ]}

    monkeypatch.setattr(forms.Retriever, "retrieve", fake_retrieve)
    monkeypatch.setattr(forms.OllamaClient, "chat_json", fake_chat_json)
    app.state.embedding_pipeline = SimpleNamespace(ollama_url="http://ollama:11434")
    app.state.vector_store = None

    body = {"questions": [
        {"id": "name", "text": "Name"},
        {"id": "salary", "text": "Salary expectations", "kind": "dropdown", "options": ["Under $50,000", "$150,000+"]},
    ]}
    # No lifespan: the state above stands in for the real services.
    response = TestClient(app).post("/brain/ai/form-answers", json=body)
    lines = [json.loads(line) for line in response.text.splitlines()]
    assert lines[0] == {"working": 0}
    lines = lines[1:]

    assert lines[0] == {"id": "name", "answer": "Asha Rao", "source": "Profile/about.md", "open": False}
    assert lines[1] == {"id": "salary", "answer": None, "source": None, "open": False}  # not an offered option
    assert lines[-1] == {"done": True}
    assert "Asha Rao" in sent[0] and "TOPSECRET" not in sent[0]


def test_compose_streams_a_draft_from_profile_and_research_only(vault, monkeypatch):
    sent = []

    async def fake_retrieve(self, query, top_k=5, filter_paths=None):
        return [Source(path="Secrets/keys.md", chunk=0, score=0.9, content="TOPSECRET")]

    async def fake_chat(self, messages, tools=None, think=None):
        sent.append((json.dumps(messages), think))
        yield {"token": "I admire "}
        yield {"token": "your work."}
        yield {"done": {"eval_count": 2}}

    monkeypatch.setattr(forms.Retriever, "retrieve", fake_retrieve)
    monkeypatch.setattr(forms.OllamaClient, "chat_with_tools", fake_chat)
    app.state.embedding_pipeline = SimpleNamespace(ollama_url="http://ollama:11434")
    app.state.vector_store = None

    body = {"question": "Why do you want to work at Acme?", "research": "Acme builds rockets."}
    lines = [json.loads(line) for line in TestClient(app).post("/brain/ai/compose-answer", json=body).text.splitlines()]

    assert "".join(line.get("token", "") for line in lines) == "I admire your work."
    assert lines[-1] == {"done": True, "sources": ["Profile/about.md"]}
    prompt, think = sent[0]
    assert think is False and "Acme builds rockets." in prompt and "Asha Rao" in prompt and "TOPSECRET" not in prompt


def test_attachments_are_read_in_full_but_secrets_are_refused(vault):
    (vault / "Profile" / "resume").mkdir()
    (vault / "Profile" / "resume" / "cv.md").write_text("Worked at Acme 2020-2024", encoding="utf-8")
    state = SimpleNamespace(document_loader=SimpleNamespace(_extract_content=lambda path: path.read_text(encoding="utf-8")))

    loaded = forms.load_attachments(state, ["Profile/resume/cv.md", "Secrets/keys.md", "../outside.md"])

    assert loaded == [("Profile/resume/cv.md", "Worked at Acme 2020-2024")]
