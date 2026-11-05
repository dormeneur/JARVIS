import asyncio
import json
import os
import time
from datetime import datetime, timezone
from unittest.mock import AsyncMock, MagicMock, patch

import pytest

from app.config import settings
from app.services import digest, proposals

NOW = datetime(2026, 11, 10, 8, 0, tzinfo=timezone.utc)


@pytest.fixture
def vault(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    monkeypatch.setattr(settings, "timezone", "UTC")
    monkeypatch.setattr(settings, "ntfy_url", "")
    return tmp_path


def touch(vault, rel, text="x", age_hours=1):
    p = vault / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")
    t = NOW.timestamp() - age_hours * 3600
    os.utime(p, (t, t))
    return p


def fake_app():
    app = MagicMock()
    app.state.vector_store.get_all_metadata = AsyncMock(return_value=[{"source_path": "Notes/a.md", "summary": "A note about plans."}])
    return app


def run_digest(app=None, **kw):
    with patch("app.services.digest.OllamaClient") as client:
        client.return_value.generate = AsyncMock(return_value=("## Changed yesterday\n- plans", 5))
        out = asyncio.run(digest.run_digest(app or fake_app(), now=NOW, **kw))
        return out, client.return_value.generate


def test_writes_a_digest_for_changed_files_and_is_idempotent(vault):
    touch(vault, "Notes/a.md")
    out, gen = run_digest()
    assert out == "Digest/2026-11-10.md"
    assert (vault / out).read_text().startswith("# Digest 2026-11-10")
    prompt = gen.call_args.args[0]
    assert "Notes/a.md: A note about plans." in prompt
    assert run_digest()[0] is None                    # second run: already written
    assert run_digest(force=True)[0] == out           # unless forced


def test_quiet_day_writes_nothing(vault):
    touch(vault, "Notes/old.md", age_hours=72)
    out, gen = run_digest()
    assert out is None and not (vault / "Digest").exists()
    gen.assert_not_called()


def test_digest_memory_forms_and_secrets_are_never_listed(vault):
    touch(vault, "Notes/a.md")
    touch(vault, "Digest/2026-11-09.md")
    touch(vault, "Memory/global_index.md")
    touch(vault, "Profile/forms/job/answers.md")
    touch(vault, "Secrets/pw.txt", "classified")
    _, gen = run_digest()
    prompt = gen.call_args.args[0]
    for hidden in ("Digest/", "Memory/", "Profile/forms", "Secrets", "classified"):
        assert hidden not in prompt


def test_pending_proposals_make_a_digest_even_on_a_quiet_day(vault):
    proposals.create("memory", "append_to_file", {"path": "Profile/work.md", "text": "- x"}, "Remember: x", "- x")
    out, gen = run_digest()
    assert out is not None and "Remember: x" in gen.call_args.args[0]


def test_push_says_only_that_a_digest_exists(vault, monkeypatch):
    monkeypatch.setattr(settings, "ntfy_url", "http://ntfy.invalid/topic")
    touch(vault, "Notes/secret-plans.md", "TOP SECRET CONTENT")
    sent = {}

    class FakeClient:
        def __init__(self, *a, **k): ...
        async def __aenter__(self): return self
        async def __aexit__(self, *a): return False
        async def post(self, url, content=None, headers=None):
            sent.update(url=url, content=content, headers=headers)

    with patch("app.services.digest.httpx.AsyncClient", FakeClient):
        assert run_digest()[0] is not None
    assert sent["content"] == "Your daily digest is ready"
    assert "secret-plans" not in json.dumps(sent) and "TOP SECRET" not in json.dumps(sent)


def test_a_failing_push_does_not_fail_the_digest(vault, monkeypatch):
    monkeypatch.setattr(settings, "ntfy_url", "http://ntfy.invalid/topic")
    touch(vault, "Notes/a.md")

    class Boom:
        def __init__(self, *a, **k): ...
        async def __aenter__(self): raise RuntimeError("network down")
        async def __aexit__(self, *a): return False

    with patch("app.services.digest.httpx.AsyncClient", Boom):
        assert run_digest()[0] == "Digest/2026-11-10.md"
