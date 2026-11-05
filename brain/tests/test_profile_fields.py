"""Form fields answered from Profile/profile.json without a model call (14-Career-Copilot.md §7.2)."""
import json
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from app.api import app
from app.config import settings
from app.routers import forms
from app.services.profile_fields import answer, pick_option

PROFILE = {
    "schema": 1,
    "setup": {"completed": ["resume", "details", "auth"]},
    "contact": {"first_name": "Asha", "middle_name": "", "last_name": "Rao", "preferred_name": "",
                "email": "asha@example.com", "phone_country": "+91", "phone": "98765 43210"},
    "address": {"line1": "12 MG Road", "line2": "", "city": "Bengaluru", "postal": "560001",
                "country": "IN", "country_name": "India", "region": "IN-KA", "region_name": "Karnataka"},
    "links": {"linkedin": "https://linkedin.com/in/asha", "github": "", "website": ""},
    "work_auth": {"by_country": {"IN": {"name": "India", "authorized": True, "sponsorship": False},
                                "US": {"name": "United States", "authorized": False, "sponsorship": True}},
                  "clearance": False, "government_ties": False, "worked_at": ["Acme"]},
    "preferences": {"onsite_ok": True, "relocate": False, "start_now": False, "notice_days": 30,
                    "transport": True, "accommodations": False,
                    "salary": {"amount": 2400000, "currency": "INR", "period": "year"}},
    "background": {"gender": "decline", "ethnicity": "decline", "veteran": "decline", "disability": "decline"},
}


def a(text, options=None, country="", company=""):
    found = answer(text, options or [], PROFILE, country, company)
    return found[0] if found else None


@pytest.mark.parametrize("label,expected", [
    ("First Name*", "Asha"), ("Legal last name (required)", "Rao"), ("Full name", "Asha Rao"),
    ("Preferred name", "Asha"), ("Email address", "asha@example.com"), ("Phone number", "+91 98765 43210"),
    ("Address line 1", "12 MG Road"), ("City", "Bengaluru"), ("ZIP / Postal code", "560001"),
    ("Country", "India"), ("State/Province", "Karnataka"), ("LinkedIn profile URL", "https://linkedin.com/in/asha"),
    ("Notice period", "30 days"), ("Expected salary", "2,400,000 INR per year"),
    ("Are you willing to relocate?", "No"), ("Do you have reliable transportation?", "Yes"),
])
def test_basic_fields(label, expected):
    assert a(label) == expected


def test_unknown_or_empty_fields_go_to_the_model():
    assert a("GitHub") is None  # empty in the profile
    assert a("Why do you want to work here?") is None
    assert a("How did you hear about us? (LinkedIn, Indeed, ...)") is None
    assert answer("First name", [], {}) is None


def test_authorization_follows_the_country():
    assert a("Are you legally authorized to work in India?") == "Yes"
    assert a("Are you legally authorized to work in the United States?") == "No"
    assert a("Will you now or in the future require visa sponsorship?", country="US") == "Yes"
    assert a("Will you now or in the future require visa sponsorship?") is None  # which country? ask the user
    assert a("Are you authorized to work in Germany?") is None  # never answered for Germany
    assert a("Are you authorized to work in this country?", country="IN") == "Yes"


def test_worked_here_before_needs_the_company():
    assert a("Have you previously worked for us?", company="Acme") == "Yes"
    assert a("Have you ever been employed by Beta Corp?", company="Beta Corp") == "No"
    assert a("Have you previously worked for us?") is None
    assert a("Have you ever worked remotely?", company="Acme") is None


def test_options_and_self_identification():
    assert a("Are you authorized to work in India?", ["Yes, I am", "No, I am not"]) == "Yes, I am"
    assert a("Gender", ["Male", "Female", "I don't wish to answer"]) == "I don't wish to answer"
    assert a("Race / Ethnicity") == "Prefer not to say"
    assert a("Do you embrace change?") is None  # "race" only as a word
    assert a("Country", ["Thailand", "Indonesia"]) is None  # India not offered: leave it to the user
    assert pick_option("India", ["India", "Indonesia"]) == "India"


def test_government_id_is_not_government_ties():
    assert a("Government-issued ID number") is None
    assert a("Do you have any relatives in government or public office?") == "No"


def test_stream_answers_profile_fields_without_the_model(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    (tmp_path / "Profile").mkdir()
    (tmp_path / "Profile" / "profile.json").write_text(json.dumps(PROFILE), encoding="utf-8")
    calls = []

    async def fake_chat_json(self, messages, schema, model=None):
        calls.append(messages)
        return {"answers": []}

    monkeypatch.setattr(forms.OllamaClient, "chat_json", fake_chat_json)
    app.state.embedding_pipeline = SimpleNamespace(ollama_url="http://ollama:11434")
    app.state.vector_store = None
    body = {"questions": [{"id": "f", "text": "First name"}, {"id": "e", "text": "Email"}]}
    lines = [json.loads(x) for x in TestClient(app).post("/brain/ai/form-answers", json=body).text.splitlines()]
    assert lines == [
        {"id": "f", "answer": "Asha", "source": "Profile/profile.json", "open": False},
        {"id": "e", "answer": "asha@example.com", "source": "Profile/profile.json", "open": False},
        {"done": True},
    ]
    assert calls == []
