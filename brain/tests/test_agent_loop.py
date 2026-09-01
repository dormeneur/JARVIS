"""Agent loop orchestration: approval gating, replay, and loop bounds.

Uses a scripted fake in place of Ollama so these run with no model, no
network, and no GPU — the logic under test is ours, not the model's.
"""

import asyncio
import json
from typing import Any, Dict, List

import pytest

from app.config import settings
from app.models.ask_models import AgentRequest, ToolTranscriptEntry


# --- scaffolding -----------------------------------------------------------

class FakeOllama:
    """Replays a scripted list of turns; records the messages it was sent."""

    def __init__(self, turns: List[List[Dict[str, Any]]]):
        self._turns = list(turns)
        self.seen_messages: List[List[Dict[str, Any]]] = []
        self.seen_tools: List[Any] = []

    def __call__(self, ollama_url):  # stand in for OllamaClient(url)
        return self

    async def chat_with_tools(self, messages, tools=None):
        self.seen_messages.append(list(messages))
        self.seen_tools.append(tools)
        events = self._turns.pop(0) if self._turns else [{"token": "done"}, {"done": {"eval_count": 1}}]
        for e in events:
            yield e


class FakeRetriever:
    def __init__(self, *a, **kw):
        pass

    async def retrieve(self, query=None, top_k=5, filter_paths=None, **kw):
        return []


class FakeState:
    class _Embedder:
        ollama_url = "http://fake"

    embedding_pipeline = _Embedder()
    vector_store = None


def turn_calling(name: str, **args):
    return [
        {"tool_calls": [{"name": name, "arguments": args}]},
        {"done": {"eval_count": 1}},
    ]


def turn_saying(text: str):
    return [{"token": text}, {"done": {"eval_count": 1}}]


async def collect(gen) -> List[Dict[str, Any]]:
    return [json.loads(line) for line in [x async for x in gen]]


@pytest.fixture
def agent_env(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    (tmp_path / "Notes").mkdir()
    (tmp_path / "Notes" / "a.md").write_text("alpha", encoding="utf-8")

    from app.routers import agent as agent_mod

    monkeypatch.setattr(agent_mod, "Retriever", FakeRetriever)
    return agent_mod, tmp_path


def run_agent(agent_mod, req):
    return asyncio.run(collect(agent_mod._run_agent(req, FakeState())))


# --- approval gating -------------------------------------------------------

def test_mutation_without_grant_stops_for_approval(agent_env, monkeypatch):
    agent_mod, vault = agent_env
    monkeypatch.setattr(
        agent_mod, "OllamaClient",
        FakeOllama([turn_calling("create_file", path="Notes/new.md", content="hi")]),
    )

    events = run_agent(agent_mod, AgentRequest(query="make a note"))
    types = [e["type"] for e in events]

    assert "approval_required" in types
    # Nothing ran, and no final answer was fabricated past the pause.
    assert "tool_result" not in types
    assert "final" not in types
    assert not (vault / "Notes" / "new.md").exists()


def test_granted_mutation_runs_without_prompting(agent_env, monkeypatch):
    agent_mod, vault = agent_env
    monkeypatch.setattr(
        agent_mod, "OllamaClient",
        FakeOllama([
            turn_calling("create_file", path="Notes/new.md", content="hi"),
            turn_saying("Created it."),
        ]),
    )

    events = run_agent(
        agent_mod,
        AgentRequest(query="make a note", granted_tools=["create_file"]),
    )
    types = [e["type"] for e in events]

    assert "approval_required" not in types
    assert "tool_result" in types
    assert (vault / "Notes" / "new.md").read_text() == "hi"


def test_read_tools_never_require_approval(agent_env, monkeypatch):
    agent_mod, _ = agent_env
    monkeypatch.setattr(
        agent_mod, "OllamaClient",
        FakeOllama([turn_calling("read_file", path="Notes/a.md"), turn_saying("It says alpha.")]),
    )

    events = run_agent(agent_mod, AgentRequest(query="what is in a.md"))
    types = [e["type"] for e in events]

    assert "approval_required" not in types
    result = next(e for e in events if e["type"] == "tool_result")
    assert result["ok"] is True
    assert "alpha" in result["summary"]


# --- resume via transcript -------------------------------------------------

def test_approved_transcript_entry_executes_on_replay(agent_env, monkeypatch):
    agent_mod, vault = agent_env
    monkeypatch.setattr(agent_mod, "OllamaClient", FakeOllama([turn_saying("Done.")]))

    run_agent(
        agent_mod,
        AgentRequest(
            query="make a note",
            tool_transcript=[
                ToolTranscriptEntry(
                    name="create_file",
                    arguments={"path": "Notes/approved.md", "content": "yes"},
                    approved=True,
                )
            ],
        ),
    )
    assert (vault / "Notes" / "approved.md").read_text() == "yes"


def test_denied_transcript_entry_does_not_execute(agent_env, monkeypatch):
    agent_mod, vault = agent_env
    fake = FakeOllama([turn_saying("Understood.")])
    monkeypatch.setattr(agent_mod, "OllamaClient", fake)

    run_agent(
        agent_mod,
        AgentRequest(
            query="delete it",
            tool_transcript=[
                ToolTranscriptEntry(
                    name="delete_file", arguments={"path": "Notes/a.md"}, approved=False
                )
            ],
        ),
    )

    assert (vault / "Notes" / "a.md").exists(), "denied call must not run"
    # The model is told it was denied so it can respond sensibly.
    tool_msg = [m for m in fake.seen_messages[0] if m.get("role") == "tool"][-1]
    assert "denied" in tool_msg["content"].lower()


def test_denial_survives_even_if_tool_is_session_granted(agent_env, monkeypatch):
    """An explicit deny must beat a standing grant for that same tool."""
    agent_mod, vault = agent_env
    monkeypatch.setattr(agent_mod, "OllamaClient", FakeOllama([turn_saying("ok")]))

    run_agent(
        agent_mod,
        AgentRequest(
            query="delete it",
            granted_tools=["delete_file"],
            tool_transcript=[
                ToolTranscriptEntry(
                    name="delete_file", arguments={"path": "Notes/a.md"}, approved=False
                )
            ],
        ),
    )
    assert (vault / "Notes" / "a.md").exists()


# --- robustness ------------------------------------------------------------

def test_loop_is_bounded(agent_env, monkeypatch):
    """A model that calls tools forever must not spin the server forever."""
    agent_mod, _ = agent_env
    monkeypatch.setattr(
        agent_mod, "OllamaClient",
        FakeOllama([turn_calling("read_file", path="Notes/a.md") for _ in range(50)]),
    )

    events = run_agent(agent_mod, AgentRequest(query="loop"))
    starts = [e for e in events if e["type"] == "tool_start"]

    assert len(starts) <= agent_mod.MAX_ITERATIONS
    assert events[-1]["type"] == "final"


def test_failing_tool_reports_error_and_keeps_going(agent_env, monkeypatch):
    agent_mod, _ = agent_env
    monkeypatch.setattr(
        agent_mod, "OllamaClient",
        FakeOllama([turn_calling("read_file", path="Notes/missing.md"), turn_saying("Not found.")]),
    )

    events = run_agent(agent_mod, AgentRequest(query="read missing"))
    result = next(e for e in events if e["type"] == "tool_result")

    assert result["ok"] is False
    assert "no such file" in result["summary"].lower()
    assert events[-1]["type"] == "final"  # loop recovered rather than dying


def test_secrets_tool_call_is_refused_not_executed(agent_env, monkeypatch):
    agent_mod, vault = agent_env
    (vault / "Secrets").mkdir()
    (vault / "Secrets" / "k.txt").write_text("classified", encoding="utf-8")
    monkeypatch.setattr(
        agent_mod, "OllamaClient",
        FakeOllama([turn_calling("read_file", path="Secrets/k.txt"), turn_saying("Can't.")]),
    )

    events = run_agent(agent_mod, AgentRequest(query="read secrets"))
    result = next(e for e in events if e["type"] == "tool_result")

    assert result["ok"] is False
    assert "classified" not in json.dumps(events), "secret content leaked into the stream"


def test_upstream_error_surfaces_and_stops(agent_env, monkeypatch):
    agent_mod, _ = agent_env
    monkeypatch.setattr(
        agent_mod, "OllamaClient",
        FakeOllama([[{"error": "model does not support tools"}]]),
    )

    events = run_agent(agent_mod, AgentRequest(query="hi"))
    assert events[-1]["type"] == "error"
    assert "tools" in events[-1]["error"]


def test_tools_are_offered_to_the_model(agent_env, monkeypatch):
    agent_mod, _ = agent_env
    fake = FakeOllama([turn_saying("hello")])
    monkeypatch.setattr(agent_mod, "OllamaClient", fake)

    run_agent(agent_mod, AgentRequest(query="hi"))
    names = {t["function"]["name"] for t in fake.seen_tools[0]}

    assert {"create_file", "read_file", "delete_file", "search_vault"} <= names
