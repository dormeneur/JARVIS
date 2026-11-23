"""Hunches: JARVIS learns who the user is by guessing and asking, not from a question bank.

Loop: the desktop app sends what was in front (app + window title, aggregated) -> the local model
reads a week of that plus recent vault activity and what is already known, and writes a few
*hunches*: a guess about the user, the evidence for it, and a one-tap question with three answers
(agree / partly / disagree). The notch asks them one at a time. An answer lands in the vault as
plain Markdown both the user and JARVIS read:

  Profile/traits.md   confirmed or partly-true traits, with what was noticed
  Profile/not-me.md   wrong guesses, so they are never asked again
  Profile/hunches.md  the open guesses and the stack (a view, rewritten on every change)

Skipping puts a hunch on the *stack*: it is not asked again this session, and comes back first in the
next one. Unprompted questions are capped per day; asking from the notch home is not.

Privacy: everything here runs on the local model. Observations live in .system/brain/hunches.db
(never synced or indexed) and are pruned after 30 days. Guesses about health, religion, politics,
sexuality, ethnicity or money are dropped before anyone sees them.
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
import re
import sqlite3
import uuid
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Set

from app.config import settings
from app.services import tools as toolkit
from app.services.document_loader import EXCLUDED_FOLDERS
from app.services.ollama_client import OllamaClient

logger = logging.getLogger(__name__)

DAILY_CAP = 6
KEEP_DAYS = 30
EVIDENCE_DAYS = 7
MAX_OPEN = 8          # don't pile up guesses nobody has seen yet
MAX_NEW = 4
MIN_SECONDS, MIN_NOTES = 1800, 5  # half an hour in front of the PC, or a handful of notes, before guessing
TRAITS, NOT_ME, VIEW = "Profile/traits.md", "Profile/not-me.md", "Profile/hunches.md"
TOPICS = ["work", "habits", "learning", "personality", "interests", "preferences", "social", "goals"]
OPEN, STACKED, ANSWERED = "open", "stacked", "answered"

SYSTEM_PROMPT = """You are a perceptive observer, like a detective who notices small details.
From the evidence about one person, form bold but grounded guesses about who they are: their habits,
how they work and learn, what they care about, their tastes and personality.

Rules:
- Read between the lines. Prefer a surprising guess about personality, motivation or habits over an
  obvious fact (someone who codes all day is a programmer: that's not a hunch).
- "evidence": 1-3 short, specific details you noticed, each under 12 words, in your own words
  (an app at a time of day, a window title, a pattern across days). Never copy a whole line.
- "question": the guess said to them as a confident hunch, under 25 words, ending as a question.
- "options": exactly 3 replies they could tap, at most 4 words each, said in their voice about THIS
  guess. The first says yes, the second "sort of" with a twist, the third says no with what's true
  instead. Not the words agree/partly/disagree, and not the same replies for different guesses.
- Don't repeat anything already known, already guessed, or listed as wrong.
- Never guess about health, religion, politics, sexuality, ethnicity, money or private relationships.
- Fewer strong guesses beat many weak ones. Return an empty list if the evidence is thin."""

SCHEMA = {
    "type": "object",
    "properties": {
        "hunches": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "trait": {"type": "string"},
                    "topic": {"type": "string", "enum": TOPICS},
                    "question": {"type": "string"},
                    "options": {"type": "array", "items": {"type": "string"}},
                    "evidence": {"type": "array", "items": {"type": "string"}},
                },
                "required": ["trait", "topic", "question", "options", "evidence"],
            },
        }
    },
    "required": ["hunches"],
}

_SENSITIVE = re.compile(
    r"\b(health|illness|disease|diagnos|depress|anxiety|adhd|autis|medic|therap|pregnan|relig|god\b|pray|church|"
    r"mosque|temple|faith|politic|vote|voting|election|party\b|sex|gay|lesbian|queer|dating|girlfriend|boyfriend|"
    r"ethnic|race\b|racial|caste|debt|loan|salary|income|money|broke\b|weight|diet)",
    re.IGNORECASE,
)
# Window titles can carry codes, card numbers or tokens: never stored.
_SECRETISH = re.compile(r"\b\d{6,}\b|sk-[A-Za-z0-9]{16,}|password|passcode|\botp\b", re.IGNORECASE)

_SUMMARY_HEADS = ("Time at the computer", "Busiest hours", "Apps by time", "No activity recorded")
_BARE_REPLIES = {"agree", "partly", "disagree", "yes", "no", "maybe", "partially", "somewhat"}

_generating = asyncio.Lock()
_tasks: Set[asyncio.Task] = set()


# --- store -------------------------------------------------------------------------------------

def _db_path() -> Path:
    return Path(settings.vault_path) / ".system" / "brain" / "hunches.db"


def _connect() -> sqlite3.Connection:
    path = _db_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(path, timeout=30)
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA journal_mode=DELETE")  # never WAL on a Docker Desktop bind mount
    conn.execute("CREATE TABLE IF NOT EXISTS observations (at TEXT, app TEXT, title TEXT, seconds INTEGER)")
    conn.execute(
        "CREATE TABLE IF NOT EXISTS hunches (id TEXT PRIMARY KEY, created TEXT, trait TEXT, topic TEXT, "
        "question TEXT, options TEXT, evidence TEXT, status TEXT, skipped_session TEXT, asked_at TEXT, "
        "answered_at TEXT, answer TEXT, served_session TEXT)"
    )
    return conn


def _now() -> datetime:
    return datetime.now(tz=timezone.utc)


def _public(r: sqlite3.Row) -> Dict[str, Any]:
    return {"id": r["id"], "question": r["question"], "options": json.loads(r["options"]),
            "evidence": json.loads(r["evidence"]), "trait": r["trait"], "topic": r["topic"], "status": r["status"]}


def record_observations(samples: Iterable[Dict[str, Any]]) -> int:
    """Stores what was in front. Titles that look like they hold a code or secret are dropped."""
    rows = []
    for s in samples:
        app, title = str(s.get("app", ""))[:80].strip(), " ".join(str(s.get("title", "")).split())[:200]
        seconds = int(s.get("seconds") or 0)
        if not app or seconds <= 0 or _SECRETISH.search(title):
            continue
        rows.append((str(s.get("at") or _now().isoformat()), app, title, min(seconds, 3600)))
    with _connect() as conn:
        conn.executemany("INSERT INTO observations VALUES (?,?,?,?)", rows)
        conn.execute("DELETE FROM observations WHERE at < ?", ((_now() - timedelta(days=KEEP_DAYS)).isoformat(),))
    return len(rows)


# --- evidence ----------------------------------------------------------------------------------

def _hour(at: str) -> Optional[datetime]:
    try:
        return datetime.fromisoformat(at)
    except ValueError:
        return None


def describe_activity(rows: List[sqlite3.Row], tz=None) -> str:
    """A week of foreground windows as a short, model-friendly summary. Pure, so it is tested."""
    if not rows:
        return "No activity recorded yet."
    if tz is None:
        from app.services.scheduler import local_tz  # JARVIS_TIMEZONE, the same clock the digest uses

        tz = local_tz()
    by_app: Dict[str, int] = Counter()
    titles: Dict[str, Counter] = defaultdict(Counter)
    hours: Counter = Counter()
    weekend = 0
    for r in rows:
        by_app[r["app"]] += r["seconds"]
        if r["title"]:
            titles[r["app"]][r["title"]] += r["seconds"]
        at = _hour(r["at"])
        if at:
            local = at.astimezone(tz)  # the user's clock: the container's own is UTC
            hours[local.hour] += r["seconds"]
            weekend += r["seconds"] if local.weekday() >= 5 else 0
    total = sum(by_app.values()) or 1
    lines = [f"Time at the computer over the last {EVIDENCE_DAYS} days: about {total // 3600} h "
             f"({100 * weekend // total}% on weekends)."]
    busiest = [h for h, _ in hours.most_common(4)]
    lines.append("Busiest hours (24 h clock): " + ", ".join(f"{h:02d}:00" for h in sorted(busiest)))
    lines.append("Apps by time, with their most-used windows:")
    for app, secs in by_app.most_common(12):
        top = "; ".join(t[:80] for t, _ in titles[app].most_common(3))
        lines.append(f"- {app}: {secs // 60} min" + (f" — {top}" if top else ""))
    return "\n".join(lines)


def _recent_notes(limit: int = 15) -> List[str]:
    root, cutoff, found = Path(settings.vault_path), (_now() - timedelta(days=EVIDENCE_DAYS)).timestamp(), []
    excluded = {f.casefold() for f in EXCLUDED_FOLDERS}
    for folder, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if not d.startswith(".") and d.casefold() not in excluded and d != "system"]
        for n in names:
            p = Path(folder, n)
            try:
                if p.suffix.lower() in (".md", ".txt") and p.stat().st_mtime > cutoff:
                    found.append((p.stat().st_mtime, p.relative_to(root).as_posix()))
            except OSError:
                continue
    return [rel for _, rel in sorted(found, reverse=True)[:limit] if not rel.startswith("Profile/")]


def _read(rel: str, cap: int) -> str:
    try:
        p = toolkit.safe_path(rel)
        return p.read_text(encoding="utf-8", errors="replace")[-cap:] if p.is_file() else ""
    except toolkit.ToolError:
        return ""


def build_prompt(activity: str, notes: List[str], known: str, wrong: str, guessed: List[str]) -> str:
    return "\n\n".join([
        "EVIDENCE: WHAT WAS IN FRONT OF THEM\n" + activity,
        "NOTES THEY CHANGED THIS WEEK\n" + ("\n".join(f"- {n}" for n in notes) or "none"),
        "ALREADY KNOWN ABOUT THEM (don't ask again)\n" + (known.strip() or "nothing yet"),
        "WRONG GUESSES (never repeat)\n" + (wrong.strip() or "none"),
        "ALREADY GUESSED, WAITING FOR AN ANSWER (don't repeat)\n" + ("\n".join(f"- {g}" for g in guessed) or "none"),
    ])


# --- validation --------------------------------------------------------------------------------

def _words(text: str) -> Set[str]:
    return {w for w in re.findall(r"[a-z]{3,}", text.casefold())}


def _similar(a: str, b: str) -> bool:
    wa, wb = _words(a), _words(b)
    return bool(wa and wb) and len(wa & wb) / len(wa | wb) >= 0.5


def _short(text: Any, limit: int) -> str:
    """At most `limit` characters, cut at a word (a button label must not end mid-word)."""
    text = " ".join(str(text).split())
    return text if len(text) <= limit else text[:limit].rsplit(" ", 1)[0].rstrip(",;:-")


def clean(raw: Any, existing: List[str]) -> List[Dict[str, Any]]:
    """The model's hunches that are well-formed, not sensitive and not a repeat. Pure, so it is tested."""
    out: List[Dict[str, Any]] = []
    items = raw.get("hunches", []) if isinstance(raw, dict) else []
    for item in items if isinstance(items, list) else []:
        if not isinstance(item, dict):
            continue
        trait = " ".join(str(item.get("trait", "")).split())[:140]
        question = " ".join(str(item.get("question", "")).split())[:220]
        options = [_short(o, 36) for o in item.get("options") or [] if str(o).strip()][:3]
        # A small model sometimes pastes a whole summary line: keep only short, specific details.
        evidence = [" ".join(str(e).split()).lstrip("- ")[:120] for e in item.get("evidence") or []
                    if str(e).strip() and not str(e).lstrip("- ").startswith(_SUMMARY_HEADS)][:3]
        if len(options) == 3 and all(o.casefold().strip(".!") in _BARE_REPLIES for o in options):
            options = ["Spot on", "Sort of", "Not really"]  # the model gave labels, not replies
        topic = item.get("topic") if item.get("topic") in TOPICS else "personality"
        if len(trait) < 3 or len(question) < 10 or len(options) != 3 or not evidence:
            continue
        if _SENSITIVE.search(trait + " " + question) or _SECRETISH.search(question):
            continue
        if any(_similar(trait, e) or _similar(question, e) for e in existing + [h["trait"] for h in out]):
            continue
        out.append({"trait": trait, "topic": topic, "question": question, "options": options, "evidence": evidence})
    return out[:MAX_NEW]


# --- generation --------------------------------------------------------------------------------

def _known_texts(conn: sqlite3.Connection) -> List[str]:
    rows = conn.execute("SELECT trait, question FROM hunches").fetchall()
    return [r["trait"] for r in rows] + [r["question"] for r in rows]


async def generate(client: Optional[OllamaClient] = None) -> int:
    """Writes up to MAX_NEW new hunches. Returns how many. Skips when one is already running."""
    if _generating.locked():
        return 0
    async with _generating:
        with _connect() as conn:
            if conn.execute("SELECT COUNT(*) FROM hunches WHERE status = ?", (OPEN,)).fetchone()[0] >= MAX_OPEN:
                return 0
            since = (_now() - timedelta(days=EVIDENCE_DAYS)).isoformat()
            rows = conn.execute("SELECT * FROM observations WHERE at >= ?", (since,)).fetchall()
            waiting = [r["trait"] for r in conn.execute("SELECT trait FROM hunches WHERE status != ?", (ANSWERED,))]
            existing = _known_texts(conn)
        notes = await asyncio.to_thread(_recent_notes)
        if sum(r["seconds"] for r in rows) < MIN_SECONDS and len(notes) < MIN_NOTES:
            return 0  # too little noticed yet: a guess from nothing is a question bank
        prompt = build_prompt(describe_activity(rows), notes, _read(TRAITS, 3000), _read(NOT_ME, 1500), waiting)
        client = client or OllamaClient(settings.ollama_url)
        reply = await client.chat_json(
            [{"role": "system", "content": SYSTEM_PROMPT}, {"role": "user", "content": prompt}], SCHEMA, timeout=180
        )
        fresh = clean(reply, existing + _bullets(_read(TRAITS, 6000)) + _bullets(_read(NOT_ME, 6000)))
        with _connect() as conn:
            for h in fresh:
                conn.execute(
                    "INSERT INTO hunches (id, created, trait, topic, question, options, evidence, status) VALUES (?,?,?,?,?,?,?,?)",
                    (uuid.uuid4().hex[:12], _now().isoformat(), h["trait"], h["topic"], h["question"],
                     json.dumps(h["options"]), json.dumps(h["evidence"]), OPEN),
                )
        if fresh:
            await asyncio.to_thread(write_view)
        return len(fresh)


def _bullets(text: str) -> List[str]:
    return [ln.lstrip("- ").split(" — ")[0] for ln in text.splitlines() if ln.startswith("- ")]


def schedule_generate() -> None:
    """Fire and forget (from a request with nothing left to ask)."""
    try:
        task = asyncio.get_running_loop().create_task(_safe_generate())
    except RuntimeError:
        return
    _tasks.add(task)
    task.add_done_callback(_tasks.discard)


async def _safe_generate() -> None:
    try:
        await generate()
    except Exception:  # noqa: BLE001 - background work must never take anything down
        logger.warning("Hunch generation failed", exc_info=True)


# --- asking ------------------------------------------------------------------------------------

def _today_start() -> str:
    local = datetime.now().astimezone()
    return local.replace(hour=0, minute=0, second=0, microsecond=0).astimezone(timezone.utc).isoformat()


def next_hunch(session: str, on_demand: bool = False) -> Dict[str, Any]:
    """The next hunch to ask in this session, or none. The stack (skipped in an earlier session) comes
    first; anything skipped in this session waits for the next. Unprompted asks are capped per day."""
    with _connect() as conn:
        asked_today = conn.execute("SELECT COUNT(*) FROM hunches WHERE asked_at >= ?", (_today_start(),)).fetchone()[0]
        stacked = conn.execute("SELECT COUNT(*) FROM hunches WHERE status = ?", (STACKED,)).fetchone()[0]
        left = max(0, DAILY_CAP - asked_today)
        row = None
        if on_demand or left > 0:
            # Once per session each: a hunch served and left unanswered (the app closed) comes back next session.
            row = conn.execute(
                "SELECT * FROM hunches WHERE status IN (?, ?) AND COALESCE(served_session, '') != ? "
                "ORDER BY status = ? DESC, created ASC LIMIT 1",
                (STACKED, OPEN, session, STACKED),
            ).fetchone()
        if row is not None:
            conn.execute("UPDATE hunches SET asked_at = ?, served_session = ? WHERE id = ?", (_now().isoformat(), session, row["id"]))
        remaining_open = conn.execute("SELECT COUNT(*) FROM hunches WHERE status = ?", (OPEN,)).fetchone()[0]
    if remaining_open <= 1:
        schedule_generate()  # keep a few ready
    return {"hunch": _public(row) if row else None, "stacked": stacked, "left_today": max(0, left - (row is not None))}


def skip(hunch_id: str, session: str) -> bool:
    with _connect() as conn:
        changed = conn.execute(
            "UPDATE hunches SET status = ?, skipped_session = ? WHERE id = ? AND status != ?",
            (STACKED, session, hunch_id, ANSWERED),
        ).rowcount
    if changed:
        write_view()
    return bool(changed)


def answer(hunch_id: str, choice: Optional[int] = None, text: Optional[str] = None) -> Dict[str, str]:
    """Records the answer and writes it where the user can read it. choice 0/1/2 = agree/partly/disagree."""
    text = " ".join((text or "").split())[:500]
    with _connect() as conn:
        row = conn.execute("SELECT * FROM hunches WHERE id = ?", (hunch_id,)).fetchone()
        if row is None:
            raise KeyError(hunch_id)
        if row["status"] == ANSWERED:
            return {"saved_to": "", "said": "Already answered."}  # a double tap is harmless
        options = json.loads(row["options"])
        if text:
            said = text
        elif choice in (0, 1, 2):
            said = options[choice]
        else:
            raise ValueError("an answer needs a choice (0-2) or text")
        conn.execute("UPDATE hunches SET status = ?, answered_at = ?, answer = ? WHERE id = ?",
                     (ANSWERED, _now().isoformat(), json.dumps({"choice": choice, "text": said}), hunch_id))
    noticed = "; ".join(json.loads(row["evidence"]))
    day = datetime.now().astimezone().strftime("%Y-%m-%d")
    if choice == 2 and not text:
        target, line = NOT_ME, f"- {row['trait']} — you said “{said}” ({day}). Noticed: {noticed}\n"
    else:
        how = "in your words" if text else ("yes" if choice == 0 else "partly")
        target, line = TRAITS, f"- {row['trait']} — {how}: “{said}” ({day}). Noticed: {noticed}\n"
    _append(target, line)
    write_view()
    return {"saved_to": target, "said": said}


def _append(rel: str, line: str) -> None:
    path = toolkit.safe_path(rel)
    path.parent.mkdir(parents=True, exist_ok=True)
    head = "" if path.exists() else {
        TRAITS: "# What JARVIS knows about you\n\nConfirmed from your answers to its hunches. Edit freely.\n\n",
        NOT_ME: "# Wrong guesses\n\nJARVIS never asks these again. Delete a line to let it reconsider.\n\n",
    }.get(rel, "")
    old = path.read_bytes().decode("utf-8", errors="replace") if path.exists() else ""
    if old and not old.endswith("\n"):
        old += "\n"
    toolkit._write(path, head + old + line if head else old + line)


def write_view() -> None:
    """Profile/hunches.md: what JARVIS currently suspects, so nothing it thinks about you is hidden."""
    with _connect() as conn:
        rows = conn.execute("SELECT * FROM hunches WHERE status != ? ORDER BY status, created", (ANSWERED,)).fetchall()
    lines = ["# JARVIS's open hunches", "",
             "Guesses it hasn't asked you about yet, and the ones you put on the stack for later.",
             "Written by JARVIS; your answers go to traits.md and not-me.md.", ""]
    for status, title in ((OPEN, "## Waiting to ask"), (STACKED, "## On the stack")):
        group = [r for r in rows if r["status"] == status]
        lines.append(title)
        lines += [f"- {r['question']} (noticed: {'; '.join(json.loads(r['evidence']))})" for r in group] or ["- none"]
        lines.append("")
    view = toolkit.safe_path(VIEW)
    view.parent.mkdir(parents=True, exist_ok=True)  # a new vault has no Profile/ yet
    toolkit._write(view, "\n".join(lines))


def status() -> Dict[str, int]:
    with _connect() as conn:
        count = dict(conn.execute("SELECT status, COUNT(*) FROM hunches GROUP BY status").fetchall())
        observations = conn.execute("SELECT COUNT(*) FROM observations").fetchone()[0]
    return {"open": count.get(OPEN, 0), "stacked": count.get(STACKED, 0), "answered": count.get(ANSWERED, 0),
            "observations": observations}
