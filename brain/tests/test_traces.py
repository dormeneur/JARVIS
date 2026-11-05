import asyncio
import json
from datetime import datetime, timedelta, timezone

from app.config import settings
from app.evals.traces import read_entries, summarize
from app.services import traces


def _lines(path):
    return [json.loads(x) for x in path.read_text(encoding="utf-8").splitlines()]


def test_record_writes_a_json_line(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    traces.record(route="query", model="m", ms_total=5)
    [entry] = _lines(tmp_path / ".system" / "brain" / "traces.jsonl")
    assert entry["route"] == "query" and entry["ms_total"] == 5 and "ts" in entry


def test_record_never_raises_on_an_unwritable_path(tmp_path, monkeypatch):
    blocker = tmp_path / "file"
    blocker.write_text("x")
    monkeypatch.setattr(settings, "vault_path", blocker)  # .system can't be created under a file
    traces.record(route="query")  # must not raise


def test_traced_passes_lines_through_and_records_without_text(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    monkeypatch.setattr(settings, "trace_text", False)

    async def stream():
        yield json.dumps({"type": "token", "token": "hi"}) + "\n"
        yield json.dumps({"type": "tool_start", "name": "read_file"}) + "\n"
        yield json.dumps({"type": "final", "answer": "SECRET ANSWER", "sources": [{"path": "a.md"}], "tokens_used": 7}) + "\n"

    async def run():
        return [x async for x in traces.traced("agent", stream(), model="m", query="SECRET QUESTION")]

    out = asyncio.run(run())
    assert len(out) == 3
    [entry] = _lines(tmp_path / ".system" / "brain" / "traces.jsonl")
    assert entry["tools"] == ["read_file"] and entry["sources"] == ["a.md"] and entry["output_tokens"] == 7
    assert entry["ms_first_token"] is not None and entry["error"] is None
    assert "SECRET" not in json.dumps(entry)


def test_traced_records_errors_and_text_when_enabled(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    monkeypatch.setattr(settings, "trace_text", True)

    async def stream():
        yield json.dumps({"error": "boom"}) + "\n"

    async def run():
        return [x async for x in traces.traced("query", stream(), model="m", query="why")]

    asyncio.run(run())
    [entry] = _lines(tmp_path / ".system" / "brain" / "traces.jsonl")
    assert entry["error"] == "boom" and entry["query"] == "why"


def test_summarize_groups_and_filters_by_date(tmp_path):
    now = datetime.now(tz=timezone.utc)
    old = (now - timedelta(days=30)).isoformat()
    entries = [
        {"ts": now.isoformat(), "route": "query", "model": "m", "ms_total": 100, "ms_first_token": 10, "output_tokens": 10},
        {"ts": now.isoformat(), "route": "query", "model": "m", "ms_total": 300, "ms_first_token": 30, "output_tokens": 30, "error": "x"},
        {"ts": old, "route": "query", "model": "m", "ms_total": 99999},
        {"ts": "garbage", "route": "agent", "model": "m"},
    ]
    [row] = summarize(entries, now - timedelta(days=7))
    assert row["count"] == 2 and row["errors"] == 1 and row["mean_out_tokens"] == 20
    assert 100 <= row["p50_ms"] <= 300


def test_read_entries_skips_bad_lines(tmp_path):
    f = tmp_path / "t.jsonl"
    f.write_text('{"a": 1}\nnot json\n', encoding="utf-8")
    assert list(read_entries(f)) == [{"a": 1}]
