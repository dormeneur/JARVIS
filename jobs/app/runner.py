"""Runs ai-job-search commands through headless Claude Code.

One run = one engine command (`/apply <url>`, `/rank`, ...). Each execution is
a single `claude -p` turn; when the command reaches a point where it would ask
the user something, the headless contract makes it stop with
`status: "awaiting_input"`, and the user's answer resumes the same Claude
session (`--resume`). Nothing in the engine repo is modified to make this work.

Spec: DOCUMENTATION/OFFICIAL-DOCS/12-Job-Search-Integration.md §3.4-3.7.
"""
from __future__ import annotations

import asyncio
import csv
import io
import json
import logging
import os
import re
import shlex
import shutil
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import AsyncIterator, Callable
from urllib.parse import urlsplit

from app.config import settings

logger = logging.getLogger(__name__)

SEEN = "job_scraper/seen_jobs.json"
TRACKER = "job_search_tracker.csv"
MAX_ARG = 50_000

HEADLESS_CONTRACT = """You are running headless for the JARVIS app. There is no terminal user.
Whenever the workflow tells you to ask the user something, or to wait for a choice or a
confirmation, stop at that point and return status "awaiting_input" with the question and
its options. Never guess the answer and never continue past that gate. When the command
finishes, return status "done" with the human-readable report in report_markdown and every
file you wrote in artifacts (paths relative to the workspace root). Put structured facts
you already have (company, role, url, fit_score, verdict, guard, deadline, strengths, gaps)
in data. Use the SCRATCHPAD environment variable as your scratchpad directory.
All other rules in this workspace apply unchanged."""

ENVELOPE_SCHEMA = {
    "type": "object",
    "properties": {
        "status": {"type": "string", "enum": ["done", "awaiting_input", "failed"]},
        "stage": {"type": "string"},
        "question": {"type": "string"},
        "options": {"type": "array", "items": {"type": "string"}},
        "report_markdown": {"type": "string"},
        "artifacts": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {"kind": {"type": "string"}, "path": {"type": "string"}},
                "required": ["path"],
            },
        },
        "data": {"type": "object"},
    },
    "required": ["status", "report_markdown"],
}


def claude_settings() -> dict:
    # Layered on top of the engine's own .claude/settings.json, which still
    # loads and keeps its allow/deny lists. lualatex/xelatex are safe to allow
    # here only because the image's shims refuse shell escape (jobs/bin/).
    return {
        "permissions": {
            "allow": [
                "Read", "Glob", "Grep", "Edit", "Write", "WebFetch", "WebSearch",
                "Agent", "Task", "Skill", "TodoWrite",
                "Bash(lualatex:*)", "Bash(xelatex:*)", "Bash(bun --version)",
                "Bash(rm -f cv/main_*)", "Bash(rm -f cover_letters/cover_*)",
            ],
            "deny": [
                "Bash(git push:*)", "Bash(git remote:*)", "Bash(gh:*)",
                "Bash(env:*)", "Bash(printenv:*)", "Read(~/.claude/**)",
            ],
        },
        "sandbox": {"enabled": settings.sandbox},
    }


def _need(a: dict, key: str) -> str:
    value = str(a.get(key) or "").strip()
    if not value:
        raise ValueError(f"'{key}' is required")
    return value


def _opt(a: dict, key: str) -> str:
    return str(a.get(key) or "").strip()


@dataclass(frozen=True)
class Kind:
    prompt: Callable[[dict], str]
    model: str
    lock: str | None = None  # "seen" (exclusive among seen writers) | "profile" (exclusive with all)


# Model per kind (§3.7): mechanical commands on Sonnet, drafting on Opus.
KINDS: dict[str, Kind] = {
    "scrape": Kind(lambda a: f"/scrape {_opt(a, 'focus')}".strip(), "sonnet", "seen"),
    "portal_health": Kind(lambda a: "/scrape health", "sonnet", "seen"),
    "rank": Kind(lambda a: f"/rank {_opt(a, 'options')}".strip(), "sonnet", "seen"),
    "apply": Kind(lambda a: f"/apply {_need(a, 'posting')}", "opus"),
    "outcome": Kind(lambda a: f"/outcome {_need(a, 'company')} {_opt(a, 'note')}".strip(), "sonnet"),
    "followup": Kind(lambda a: f"/outcome followup {_opt(a, 'target')}".strip(), "sonnet"),
    "stale": Kind(lambda a: f"/outcome stale {_opt(a, 'days')}".strip(), "sonnet"),
    "interview": Kind(lambda a: f"/interview {_need(a, 'company')}", "opus"),
    "upskill": Kind(lambda a: f"/upskill {_opt(a, 'url')}".strip(), "sonnet"),
    "expand": Kind(lambda a: "/expand", "opus", "profile"),
    "setup": Kind(lambda a: f"/setup {_opt(a, 'options')}".strip(), "opus", "profile"),
    "ask": Kind(lambda a: _need(a, "text"), "opus"),
}

ACTIVE = ("queued", "running")


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def describe_tool(name: str, inp: dict) -> str:
    """One human-readable progress line per tool call, for the phone."""
    if name == "Bash":
        cmd = str(inp.get("command", ""))
        for needle, label in (
            ("posting_guard", "Checking posting integrity"),
            ("xelatex", "Compiling cover letter"),
            ("lualatex", "Compiling CV"),
            ("verify_layout", "Checking page layout"),
            ("verify_pdf", "Checking PDF and ATS text"),
            ("fetch_page", "Fetching page (browser retry)"),
            ("rank_state", "Updating ranking state"),
            ("scrape_state", "Updating scrape history"),
            ("salary_lookup", "Looking up salary benchmark"),
            ("-search/cli", "Searching job board"),
        ):
            if needle in cmd:
                return label
        return f"Running: {cmd[:80]}"
    if name == "WebFetch":
        return f"Fetching {urlsplit(str(inp.get('url', ''))).netloc or 'page'}"
    if name == "WebSearch":
        return f"Searching the web: {str(inp.get('query', ''))[:80]}"
    if name in ("Agent", "Task"):
        return f"Subagent: {str(inp.get('description') or inp.get('subagent_type') or '')[:80]}"
    if name in ("Read", "Write", "Edit"):
        verb = "Reading" if name == "Read" else "Writing"
        return f"{verb} {Path(str(inp.get('file_path', ''))).name}"
    return name


def parse_envelope(result: dict) -> dict:
    """The run's typed final answer. Tolerant: if the model ignored the schema,
    fall back to a trailing ```json block, then to the plain text."""
    structured = result.get("structured_output")
    if isinstance(structured, dict) and "status" in structured:
        return structured
    text = str(result.get("result") or "")
    candidates = [text] + re.findall(r"```json\s*(\{.*?\})\s*```", text, re.S)[::-1]
    for candidate in candidates:
        try:
            parsed = json.loads(candidate)
        except (ValueError, TypeError):
            continue
        if isinstance(parsed, dict) and "status" in parsed:
            return parsed
    status = "failed" if result.get("is_error") else "done"
    return {"status": status, "report_markdown": text}


class Runner:
    def __init__(self) -> None:
        self.runs: dict[str, dict] = {}
        self.events: dict[str, list[dict]] = {}
        self.procs: dict[str, asyncio.subprocess.Process] = {}
        self.changed = asyncio.Condition()
        self._tasks: set[asyncio.Task] = set()

    # ── paths ─────────────────────────────────────────────────────────────
    @property
    def runs_dir(self) -> Path:
        return settings.state_dir / "runs"

    def _snap_dir(self, run_id: str) -> Path:
        return settings.state_dir / "snapshots" / run_id

    def _save(self, run: dict) -> None:
        run["updated"] = _now()
        self.runs_dir.mkdir(parents=True, exist_ok=True)
        tmp = self.runs_dir / f"{run['id']}.json.tmp"
        tmp.write_text(json.dumps(run, indent=1), encoding="utf-8")
        tmp.replace(self.runs_dir / f"{run['id']}.json")

    def load(self) -> None:
        if not self.runs_dir.exists():
            return
        for path in self.runs_dir.glob("*.json"):
            run = json.loads(path.read_text(encoding="utf-8"))
            if run["status"] == "running":
                # The process died with the container; the Claude session
                # survives in its volume, so the run can be retried.
                run["status"] = "failed"
                run["error"] = "Interrupted by a restart. Retry to resume."
                self._save(run)
            self.runs[run["id"]] = run

    def _events(self, run_id: str) -> list[dict]:
        if run_id not in self.events:
            path = self.runs_dir / f"{run_id}.ndjson"
            lines = path.read_text(encoding="utf-8").splitlines() if path.exists() else []
            self.events[run_id] = [json.loads(line) for line in lines if line.strip()]
        return self.events[run_id]

    async def _emit(self, run_id: str, event: dict) -> None:
        event = {"t": _now(), **event}
        self._events(run_id).append(event)
        self.runs_dir.mkdir(parents=True, exist_ok=True)
        with (self.runs_dir / f"{run_id}.ndjson").open("a", encoding="utf-8") as f:
            f.write(json.dumps(event) + "\n")
        async with self.changed:
            self.changed.notify_all()

    # ── public API ────────────────────────────────────────────────────────
    async def submit(self, kind: str, args: dict) -> dict:
        if kind not in KINDS:
            raise ValueError(f"unknown kind '{kind}'")
        if any(len(str(v)) > MAX_ARG for v in args.values()):
            raise ValueError("argument too long")
        prompt = KINDS[kind].prompt(args)
        run = {
            "id": uuid.uuid4().hex[:12], "kind": kind, "args": args,
            "status": "queued", "created": _now(), "session_id": None,
            "pending_prompt": prompt, "envelope": None, "error": None,
            "usage": {"input_tokens": 0, "output_tokens": 0, "cost_usd": 0.0},
            "denials": [], "integrity": None, "profile_changes": [],
        }
        self.runs[run["id"]] = run
        self._save(run)
        await self._emit(run["id"], {"type": "status", "status": "queued"})
        self.schedule()
        return run

    async def answer(self, run_id: str, text: str) -> dict:
        run = self.runs[run_id]
        if run["status"] not in ("awaiting_input", "done", "failed", "cancelled"):
            raise ValueError(f"run is {run['status']}")
        if not run["session_id"]:
            raise ValueError("run has no Claude session to resume")
        text = text.strip()
        if not text or len(text) > MAX_ARG:
            raise ValueError("answer must be 1-50000 characters")
        run.update(status="queued", pending_prompt=text, error=None)
        self._save(run)
        await self._emit(run_id, {"type": "answer", "text": text[:500]})
        await self._emit(run_id, {"type": "status", "status": "queued"})
        self.schedule()
        return run

    async def retry(self, run_id: str) -> dict:
        run = self.runs[run_id]
        if run["status"] not in ("failed", "cancelled"):
            raise ValueError(f"run is {run['status']}")
        prompt = "Continue where you left off." if run["session_id"] else KINDS[run["kind"]].prompt(run["args"])
        run.update(status="queued", pending_prompt=prompt, error=None)
        self._save(run)
        self.schedule()
        return run

    async def cancel(self, run_id: str) -> dict:
        run = self.runs[run_id]
        proc = self.procs.get(run_id)
        if proc and proc.returncode is None:
            proc.kill()
        if run["status"] in ACTIVE or run["status"] == "awaiting_input":
            run["status"] = "cancelled"
            self._save(run)
            await self._emit(run_id, {"type": "status", "status": "cancelled"})
        return run

    async def follow(self, run_id: str) -> AsyncIterator[dict]:
        """Every event so far, then new ones until the run stops being active."""
        sent = 0
        while True:
            events = self._events(run_id)
            while sent < len(events):
                yield events[sent]
                sent += 1
            if self.runs[run_id]["status"] not in ACTIVE:
                return
            async with self.changed:
                try:
                    await asyncio.wait_for(self.changed.wait(), timeout=15)
                except asyncio.TimeoutError:
                    yield {"type": "ping"}

    # ── scheduling ────────────────────────────────────────────────────────
    def _runnable(self, run: dict, running: list[dict]) -> bool:
        if len(running) >= settings.max_parallel:
            return False
        lock = KINDS[run["kind"]].lock
        held = {KINDS[r["kind"]].lock for r in running}
        if "profile" in held or (lock == "profile" and running):
            return False
        return not (lock == "seen" and "seen" in held)

    def schedule(self) -> None:
        # ponytail: FIFO. Deadline-first ordering (§3.6) when a queue ever
        # holds more than a handful of runs.
        running = [r for r in self.runs.values() if r["status"] == "running"]
        for run in sorted(self.runs.values(), key=lambda r: r["created"]):
            if run["status"] == "queued" and self._runnable(run, running):
                run["status"] = "running"
                running.append(run)
                task = asyncio.create_task(self._execute(run))
                self._tasks.add(task)
                task.add_done_callback(self._tasks.discard)

    # ── snapshots (§3.6) ──────────────────────────────────────────────────
    def _state_files(self) -> list[Path]:
        ws = settings.workspace
        files = [ws / SEEN, ws / TRACKER, ws / "CLAUDE.md"]
        files += sorted((ws / ".claude" / "skills").glob("**/*.md"))
        return [f for f in files if f.is_file()]

    def _snapshot(self, run_id: str) -> None:
        snap = self._snap_dir(run_id)
        if snap.exists():
            return  # first execution only: restore goes back to before the run
        for f in self._state_files():
            dest = snap / f.relative_to(settings.workspace)
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(f, dest)
        snap.mkdir(parents=True, exist_ok=True)

    def restore(self, run_id: str) -> list[str]:
        # ponytail: restores files that existed before the run; a file the run
        # created (e.g. a first tracker) is left in place.
        snap = self._snap_dir(run_id)
        if not snap.exists():
            raise ValueError("no snapshot for this run")
        restored = []
        for f in snap.rglob("*"):
            if f.is_file():
                rel = f.relative_to(snap)
                shutil.copy2(f, settings.workspace / rel)
                restored.append(rel.as_posix())
        return restored

    def _check_integrity(self, run_id: str) -> str:
        ws, snap = settings.workspace, self._snap_dir(run_id)
        try:
            if (ws / SEEN).exists():
                json.loads((ws / SEEN).read_text(encoding="utf-8"))
            if (ws / TRACKER).exists():
                rows = list(csv.reader(io.StringIO((ws / TRACKER).read_text(encoding="utf-8"))))
                if not rows or rows[0][0] != "date" or any(len(r) != len(rows[0]) for r in rows[1:] if r):
                    return "corrupt"
                before = snap / TRACKER
                if before.exists():
                    old = list(csv.reader(io.StringIO(before.read_text(encoding="utf-8"))))
                    if len(rows) < len(old):
                        return "corrupt"
        except (ValueError, OSError, IndexError):
            return "corrupt"
        return "ok"

    def _profile_changes(self, run_id: str) -> list[str]:
        snap, ws = self._snap_dir(run_id), settings.workspace
        changed = []
        for f in self._state_files():
            rel = f.relative_to(ws)
            if rel.as_posix() in (SEEN, TRACKER):
                continue
            old = snap / rel
            if not old.exists() or old.read_bytes() != f.read_bytes():
                changed.append(rel.as_posix())
        return changed

    # ── execution ─────────────────────────────────────────────────────────
    def argv(self, run: dict) -> list[str]:
        kind = KINDS[run["kind"]]
        model = run["args"].get("model") or kind.model
        argv = [
            *shlex.split(settings.claude_bin), "-p", run["pending_prompt"],
            "--output-format", "stream-json", "--verbose",
            "--permission-mode", "dontAsk", "--permission-prompts", "none",
            "--settings", json.dumps(claude_settings()),
            "--append-system-prompt", HEADLESS_CONTRACT,
            "--json-schema", json.dumps(ENVELOPE_SCHEMA),
            "--model", str(model),
        ]
        if run["session_id"]:
            argv += ["--resume", run["session_id"]]
        return argv

    async def _execute(self, run: dict) -> None:
        run_id = run["id"]
        self._save(run)
        await self._emit(run_id, {"type": "status", "status": "running"})
        scratch = settings.state_dir / "scratch" / run_id
        scratch.mkdir(parents=True, exist_ok=True)
        result: dict | None = None
        stderr = ""
        try:
            self._snapshot(run_id)
            proc = await asyncio.create_subprocess_exec(
                *self.argv(run),
                cwd=settings.workspace,
                env={**os.environ, "SCRATCHPAD": str(scratch)},
                stdin=asyncio.subprocess.DEVNULL,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
                limit=16 * 1024 * 1024,
            )
            self.procs[run_id] = proc
            # Drain stderr alongside stdout: a full stderr pipe would block Claude mid-run.
            stderr_task = asyncio.create_task(proc.stderr.read())
            try:
                result = await asyncio.wait_for(
                    self._consume(run, proc), timeout=settings.run_timeout_minutes * 60
                )
            finally:
                if proc.returncode is None and result is None:
                    proc.kill()
                await proc.wait()
                stderr = (await stderr_task).decode("utf-8", "replace")
        except asyncio.TimeoutError:
            run["error"] = f"Timed out after {settings.run_timeout_minutes} min. Retry resumes the session."
        except (OSError, ValueError) as e:
            run["error"] = f"Could not start Claude Code: {e}"
        finally:
            self.procs.pop(run_id, None)

        if run["status"] == "cancelled":
            pass
        elif result is not None:
            envelope = parse_envelope(result)
            run["envelope"] = envelope
            run["status"] = envelope.get("status") if envelope.get("status") in ("done", "awaiting_input", "failed") else "done"
            if run["status"] == "failed":
                run["error"] = str(result.get("result") or "Claude reported an error")[:2000]
        else:
            run["status"] = "failed"
            if not run["error"]:
                run["error"] = (stderr or "Claude Code exited without a result")[-2000:]

        run["integrity"] = self._check_integrity(run_id)
        run["profile_changes"] = self._profile_changes(run_id)
        self._save(run)
        await self._emit(run_id, {
            "type": "status", "status": run["status"], "envelope": run["envelope"],
            "error": run["error"], "integrity": run["integrity"],
            "profile_changes": run["profile_changes"], "usage": run["usage"],
        })
        self.schedule()

    async def _consume(self, run: dict, proc: asyncio.subprocess.Process) -> dict | None:
        run_id, result = run["id"], None
        async for raw in proc.stdout:
            try:
                msg = json.loads(raw)
            except ValueError:
                continue
            if msg.get("session_id") and not run["session_id"]:
                run["session_id"] = msg["session_id"]
                self._save(run)
            kind = msg.get("type")
            if kind == "assistant":
                for block in msg.get("message", {}).get("content", []):
                    if block.get("type") == "tool_use":
                        await self._emit(run_id, {"type": "step", "text": describe_tool(block.get("name", ""), block.get("input") or {})})
            elif kind == "user":
                for block in msg.get("message", {}).get("content", []) or []:
                    if isinstance(block, dict) and block.get("type") == "tool_result" and block.get("is_error"):
                        text = json.dumps(block.get("content"))[:300]
                        if "permission" in text.lower():
                            run["denials"].append(text)
                            await self._emit(run_id, {"type": "denied", "text": text})
            elif kind == "result":
                result = msg
                usage = msg.get("usage") or {}
                run["usage"]["input_tokens"] += int(usage.get("input_tokens") or 0) + int(usage.get("cache_read_input_tokens") or 0) + int(usage.get("cache_creation_input_tokens") or 0)
                run["usage"]["output_tokens"] += int(usage.get("output_tokens") or 0)
                run["usage"]["cost_usd"] = round(run["usage"]["cost_usd"] + float(msg.get("total_cost_usd") or 0), 4)
                for d in msg.get("permission_denials") or []:
                    run["denials"].append(json.dumps(d)[:300])
        return result


async def claude_auth_status() -> dict:
    """`claude auth status`: costs no tokens."""
    try:
        proc = await asyncio.create_subprocess_exec(
            *shlex.split(settings.claude_bin), "auth", "status",
            stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.STDOUT,
        )
        out, _ = await asyncio.wait_for(proc.communicate(), timeout=20)
    except (OSError, asyncio.TimeoutError) as e:
        return {"ok": False, "detail": str(e)}
    return {"ok": proc.returncode == 0, "detail": out.decode("utf-8", "replace").strip()[:500]}


runner = Runner()
