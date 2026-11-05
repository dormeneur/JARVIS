"""Every call on the shared model must send the same num_ctx (a different one reloads the model)."""
import json
from unittest.mock import patch

import pytest

from app.config import settings
from app.services import ollama_client as oc


class _Resp:
    status_code = 200
    text = "{}"

    def __init__(self, lines=None):
        self._lines = lines or [json.dumps({"message": {"content": "x"}, "response": "x", "done": True})]

    def raise_for_status(self): ...
    def json(self): return {"response": "x", "message": {"content": "{}"}, "eval_count": 1}
    async def aread(self): return b""
    async def aiter_lines(self):
        for line in self._lines:
            yield line


class _Stream:
    def __init__(self, owner, payload): self.owner, self.payload = owner, payload
    async def __aenter__(self):
        self.owner.payloads.append(self.payload)
        return _Resp()
    async def __aexit__(self, *a): return False


class FakeHttpx:
    def __init__(self, *a, **kw): self.payloads = FakeHttpx.shared
    shared: list = []
    async def __aenter__(self): return self
    async def __aexit__(self, *a): return False
    def stream(self, method, url, json=None): return _Stream(self, json)
    async def post(self, url, json=None):
        self.payloads.append(json)
        return _Resp()


@pytest.fixture
def payloads():
    FakeHttpx.shared = []
    with patch.object(oc.httpx, "AsyncClient", FakeHttpx):
        yield FakeHttpx.shared


@pytest.mark.asyncio
async def test_every_shared_model_call_sends_num_ctx(payloads, monkeypatch):
    monkeypatch.setattr(settings, "num_ctx", 4096)
    c = oc.OllamaClient("http://fake")
    [t async for t in c.generate_streaming("hi")]
    [e async for e in c.chat_with_tools([{"role": "user", "content": "hi"}], [])]
    await c.generate("hi")
    await c.chat_json([{"role": "user", "content": "hi"}], {"type": "object"})
    assert len(payloads) == 4
    assert [p["options"]["num_ctx"] for p in payloads] == [4096] * 4


@pytest.mark.asyncio
async def test_form_model_keeps_its_own_context(payloads):
    await oc.OllamaClient("http://fake").chat_json([], {"type": "object"}, model="qwen3:4b")
    assert payloads[0]["options"]["num_ctx"] == oc.FORM_CONTEXT


@pytest.mark.asyncio
async def test_thinking_turns_get_a_bigger_reply_budget(payloads):
    c = oc.OllamaClient("http://fake")
    [e async for e in c.chat_with_tools([], [{"x": 1}], think=True)]
    [e async for e in c.chat_with_tools([], [{"x": 1}], think=False)]
    assert payloads[0]["options"]["num_predict"] == oc.MAX_TOKENS * 2
    assert payloads[1]["options"]["num_predict"] == oc.MAX_TOKENS


def test_assembler_budget_follows_context_window():
    from app.services import context_assembler as ca
    assert ca.MAX_CONTEXT_TOKENS == max(2048, settings.num_ctx - oc.MAX_TOKENS - 2048)


def test_history_is_trimmed_to_budget():
    from app.models.ask_models import Message
    from app.services.context_assembler import ContextAssembler, HISTORY_TOKEN_BUDGET
    from app.services.text_chunker import TextChunker
    from app.services.document_loader import DocumentLoader

    chunker = TextChunker()
    asm = ContextAssembler(chunker, DocumentLoader("."))
    turns = [Message(role="user" if i % 2 == 0 else "assistant", content=f"turn{i} " + "word " * 300) for i in range(10)]
    prompt, _ = asm.assemble_prompt("q?", [], [], chat_history=turns)
    hist = prompt.split("=== CHAT HISTORY ===")[1].split("=== END CHAT HISTORY ===")[0]
    assert chunker._count_tokens(hist) <= HISTORY_TOKEN_BUDGET + 50
    assert "turn9" in hist and "turn0" not in hist
