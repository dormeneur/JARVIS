"""Brain helpers for resume extraction: text from a vault file (Secrets refused), local JSON."""
from types import SimpleNamespace

from fastapi.testclient import TestClient

from app.api import app
from app.config import settings
from app.routers import career
from app.services.document_loader import DocumentLoader


def test_resume_text_reads_vault_files_but_never_secrets(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    (tmp_path / "Profile" / "resume").mkdir(parents=True)
    (tmp_path / "Profile" / "resume" / "cv.md").write_text("Asha Rao, Backend Engineer", encoding="utf-8")
    (tmp_path / "Secrets").mkdir()
    (tmp_path / "Secrets" / "cv.md").write_text("TOPSECRET", encoding="utf-8")
    (tmp_path / "Profile" / "photo.png").write_bytes(b"x")
    app.state.document_loader = DocumentLoader(str(tmp_path))
    client = TestClient(app)
    ok = client.post("/brain/career/resume-text", json={"path": "Profile/resume/cv.md"})
    assert ok.status_code == 200 and ok.json()["text"] == "Asha Rao, Backend Engineer"
    assert client.post("/brain/career/resume-text", json={"path": "Secrets/cv.md"}).status_code == 400
    assert client.post("/brain/career/resume-text", json={"path": "Profile/photo.png"}).status_code == 400
    assert client.post("/brain/career/resume-text", json={"path": "Profile/missing.pdf"}).status_code == 400


def test_extract_local_uses_the_schema_and_a_bigger_budget(monkeypatch):
    seen = {}

    async def fake_chat_json(self, messages, schema, model=None, max_tokens=0, timeout=None):
        seen.update(schema=schema, max_tokens=max_tokens, user=messages[1]["content"])
        return {"experience": []}

    monkeypatch.setattr(career.OllamaClient, "chat_json", fake_chat_json)
    app.state.embedding_pipeline = SimpleNamespace(ollama_url="http://ollama:11434")
    resp = TestClient(app).post("/brain/career/extract-local",
                                json={"system": "s", "text": "resume", "schema": {"type": "object"}})
    assert resp.json() == {"data": {"experience": []}}
    assert seen["schema"] == {"type": "object"} and seen["max_tokens"] == career.LOCAL_MAX_TOKENS
    assert "<document>\nresume\n</document>" in seen["user"]
