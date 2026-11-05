import asyncio
import json
import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app import board
from app.config import settings
from app.runner import ACTIVE, Runner, describe_tool, parse_envelope

FAKE = f"{Path(sys.executable).as_posix()} {(Path(__file__).parent / 'fake_claude.py').as_posix()}"


@pytest.fixture
def ws(tmp_path, monkeypatch):
    work = tmp_path / "work"
    (work / ".claude" / "skills" / "job-application-assistant").mkdir(parents=True)
    (work / "CLAUDE.md").write_text("# profile\n", encoding="utf-8")
    (work / ".claude" / "skills" / "job-application-assistant" / "01-candidate-profile.md").write_text("p", encoding="utf-8")
    monkeypatch.setattr(settings, "workspace", work)
    monkeypatch.setattr(settings, "state_dir", tmp_path / "state")
    monkeypatch.setattr(settings, "claude_bin", FAKE)
    monkeypatch.setattr(settings, "max_parallel", 2)
    return work


async def _settle(r: Runner, run_id: str, timeout=20.0):
    for _ in range(int(timeout * 20)):
        if r.runs[run_id]["status"] not in ACTIVE:
            return r.runs[run_id]
        await asyncio.sleep(0.05)
    raise AssertionError(f"run still {r.runs[run_id]['status']}")


def _argv(run_id: str) -> list:
    return json.loads((settings.state_dir / "scratch" / run_id / "argv.json").read_text(encoding="utf-8"))


def test_apply_pauses_at_gate_then_resumes_same_session(ws):
    async def go():
        r = Runner()
        run = await r.submit("apply", {"posting": "https://jobs.example/1", "job_key": "acme_ml"})
        run = await _settle(r, run["id"])
        assert run["status"] == "awaiting_input"
        assert run["envelope"]["question"] == "Should I proceed with drafting?"
        assert run["session_id"] == "sess-1"
        first = _argv(run["id"])
        assert first[first.index("-p") + 1] == "/apply https://jobs.example/1"
        assert first[first.index("--permission-mode") + 1] == "dontAsk"
        assert first[first.index("--model") + 1] == "opus"
        assert "--resume" not in first

        await r.answer(run["id"], "Draft")
        run = await _settle(r, run["id"])
        assert run["status"] == "done"
        second = _argv(run["id"])
        assert second[second.index("--resume") + 1] == "sess-1"
        assert run["integrity"] == "ok"
        assert run["usage"]["output_tokens"] == 40
        assert run["denials"]  # the denied tool call is recorded for allowlist tuning
        steps = [e["text"] for e in r._events(run["id"]) if e["type"] == "step"]
        assert steps == ["Checking posting integrity"]

    asyncio.run(go())


def test_fenced_json_fallback(ws):
    async def go():
        r = Runner()
        run = await _settle(r, (await r.submit("rank", {}))["id"])
        assert run["status"] == "done" and run["envelope"]["report_markdown"] == "3 ranked"
        assert _argv(run["id"])[_argv(run["id"]).index("--model") + 1] == "sonnet"

    asyncio.run(go())


def test_corrupted_tracker_is_flagged_and_restorable(ws):
    (ws / "job_search_tracker.csv").write_text("date,company\n2026-01-01,Acme\n", encoding="utf-8")

    async def go():
        r = Runner()
        run = await _settle(r, (await r.submit("ask", {"text": "CORRUPT"}))["id"])
        assert run["integrity"] == "corrupt"
        assert "job_search_tracker.csv" in r.restore(run["id"])

    asyncio.run(go())
    assert (ws / "job_search_tracker.csv").read_text(encoding="utf-8").startswith("date,company\n2026")


def test_profile_change_is_reported(ws):
    async def go():
        r = Runner()
        return await _settle(r, (await r.submit("ask", {"text": "PROFILE"}))["id"])

    run = asyncio.run(go())
    assert run["profile_changes"] == ["CLAUDE.md"]


def test_cancel_kills_running_process(ws):
    async def go():
        r = Runner()
        run = await r.submit("ask", {"text": "SLEEP"})
        for _ in range(100):
            if run["id"] in r.procs:
                break
            await asyncio.sleep(0.05)
        await r.cancel(run["id"])
        run = await _settle(r, run["id"])
        assert run["status"] == "cancelled"

    asyncio.run(go())


def test_lots_of_stderr_does_not_stall_a_run(ws):
    async def go():
        r = Runner()
        return await _settle(r, (await r.submit("ask", {"text": "NOISY"}))["id"])

    run = asyncio.run(go())
    assert run["status"] == "done"
    assert run["envelope"]["report_markdown"] == "loud but fine"


def test_timeout_kills_the_process_and_fails_the_run(ws, monkeypatch):
    monkeypatch.setattr(settings, "run_timeout_minutes", 0.02)  # about a second

    async def go():
        r = Runner()
        return await _settle(r, (await r.submit("ask", {"text": "SLEEP"}))["id"])

    run = asyncio.run(go())
    assert run["status"] == "failed"
    assert run["error"].startswith("Timed out")


def test_lock_matrix(ws):
    r = Runner()
    running = lambda *kinds: [{"kind": k} for k in kinds]  # noqa: E731
    assert r._runnable({"kind": "apply"}, running("apply"))
    assert not r._runnable({"kind": "apply"}, running("apply", "apply"))  # max_parallel
    assert not r._runnable({"kind": "rank"}, running("scrape"))  # seen_jobs writers exclusive
    assert r._runnable({"kind": "rank"}, running("apply"))
    assert not r._runnable({"kind": "setup"}, running("apply"))  # profile waits for everything
    assert not r._runnable({"kind": "apply"}, running("expand"))  # and blocks everything


def test_bad_input_rejected(ws):
    async def go():
        r = Runner()
        with pytest.raises(ValueError):
            await r.submit("apply", {})
        with pytest.raises(ValueError):
            await r.submit("rm -rf", {})

    asyncio.run(go())


def test_parse_envelope_plain_text():
    assert parse_envelope({"result": "hi"}) == {"status": "done", "report_markdown": "hi"}
    assert parse_envelope({"result": "boom", "is_error": True})["status"] == "failed"


def test_describe_tool():
    assert describe_tool("Bash", {"command": "cd cover_letters && xelatex -interaction=nonstopmode c.tex"}) == "Compiling cover letter"
    assert describe_tool("WebFetch", {"url": "https://careers.acme.com/x"}) == "Fetching careers.acme.com"


def test_board_stages_and_links(ws):
    (ws / "job_scraper").mkdir()
    (ws / "job_scraper" / "seen_jobs.json").write_text(json.dumps({"seen": {
        "acme_ml": {"title": "ML Engineer", "company": "Acme", "url": "https://x/1", "status": "ranked", "rank_score": 80},
        "beta_dev": {"title": "Dev", "company": "Beta", "url": "https://x/2", "status": "new"},
        "gamma_x": {"title": "X", "company": "Gamma", "url": "https://x/3", "status": "ranked", "rank_score": 90, "location_verdict": "FAIL"},
        "delta_y": {"title": "Y Eng", "company": "Delta A/S", "url": "https://x/4", "status": "ranked", "rank_score": 70},
    }}), encoding="utf-8")
    (ws / "job_search_tracker.csv").write_text(
        "date,company,sector,role,role_type,channel,status,contact_person,fit_rating,notes,cv_file,cover_letter_file,source,deadline\n"
        "2026-10-01,Delta A/S,,Y Eng,,,drafted,,72,,cv/main_delta_as_y_eng.tex,,https://x/4,\n"
        "2026-09-01,Omega,,Z,,,applied,,,,,,,\n", encoding="utf-8")
    (ws / "cv").mkdir()
    (ws / "cv" / "main_delta_as_y_eng.pdf").write_bytes(b"%PDF")
    (ws / "documents" / "applications" / "delta_as_y_eng").mkdir(parents=True)
    (ws / "documents" / "applications" / "delta_as_y_eng" / "job_posting.md").write_text("p", encoding="utf-8")

    cards = {c["key"]: c for c in board.build_board([
        {"id": "r1", "status": "running", "args": {"job_key": "beta_dev"}}])["cards"]}
    assert cards["acme_ml"]["stage"] == "shortlist"
    assert cards["beta_dev"]["stage"] == "preparing" and cards["beta_dev"]["run_id"] == "r1"
    assert cards["gamma_x"]["stage"] == "closed"  # location veto
    assert cards["delta_y"]["stage"] == "ready"
    assert cards["delta_y"]["score"] == 72
    assert cards["delta_y"]["application"]["cv_pdf"] == "cv/main_delta_as_y_eng.pdf"
    assert cards["delta_y"]["application"]["files"] == ["job_posting.md"]
    assert cards["app:omega_z"]["stage"] == "applied"
    assert "Acme%20recruiter" in cards["acme_ml"]["links"]["recruiters"]


def test_artifact_path_guard(ws):
    (ws / "cv").mkdir()
    (ws / "cv" / "a.pdf").write_bytes(b"%PDF")
    (ws / "CLAUDE.md").write_text("x", encoding="utf-8")
    assert board.artifact_path("cv/a.pdf").name == "a.pdf"
    for bad in ("../etc/passwd", "CLAUDE.md", "cv/../CLAUDE.md", ".claude/settings.json", "cv/a.exe"):
        with pytest.raises((ValueError, FileNotFoundError)):
            board.artifact_path(bad)


def test_subfolder_rule():
    assert board.subfolder("Novo Nordisk A/S", "ML Engineer") == "novo_nordisk_as_ml_engineer"


def test_api_requires_secret(ws, monkeypatch):
    from app.main import app

    monkeypatch.setattr(settings, "jobs_secret", "s3cret")
    client = TestClient(app)
    assert client.get("/board").status_code == 401
    assert client.get("/board", headers={"X-Jobs-Secret": "wrong"}).status_code == 401
    assert client.get("/board", headers={"X-Jobs-Secret": "s3cret"}).status_code == 200
    monkeypatch.setattr(settings, "jobs_secret", "")
    assert client.get("/board").status_code == 401  # unset secret never means open


def test_extract_runs_claude_without_tools(ws, monkeypatch):
    from app.main import app

    monkeypatch.setattr(settings, "jobs_secret", "s")
    client = TestClient(app)
    h = {"X-Jobs-Secret": "s"}
    ok = client.post("/extract", headers=h, json={"system": "sys", "text": "Jane Doe resume", "schema": {"type": "object"}})
    assert ok.status_code == 200 and ok.json()["data"] == {"echo": "Jane Doe resume"}
    argv = json.loads((settings.state_dir / "scratch" / "extract" / "argv.json").read_text(encoding="utf-8"))
    assert argv[argv.index("--tools") + 1] == "" and "--no-session-persistence" in argv
    assert argv[argv.index("--setting-sources") + 1] == ""
    assert "--disable-slash-commands" in argv and "--strict-mcp-config" in argv
    bad = client.post("/extract", headers=h, json={"system": "s", "text": "BROKEN", "schema": {}})
    assert bad.status_code == 502
