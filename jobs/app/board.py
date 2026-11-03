"""The job board, read straight from the engine's files: zero tokens.

Joins `seen_jobs.json` (scraped/ranked jobs) with `job_search_tracker.csv`
(applications) and the `documents/applications/` archive into one list of
cards, each with a `stage` the app groups by.
"""
from __future__ import annotations

import csv
import io
import json
import re
from pathlib import Path
from urllib.parse import quote

from app.config import settings

FINAL = {"hired", "rejected", "no_response", "no response", "offer_declined", "offer declined", "withdrawn"}
STAGE_BY_STATUS = {"drafted": "ready", "applied": "applied", "interview": "interviewing", "offer": "interviewing"}
ARTIFACT_ROOTS = ("cv", "cover_letters", "documents/applications", "upskill")
ARTIFACT_EXTS = {".pdf", ".txt", ".md", ".tex"}


def norm(text: str) -> str:
    return re.sub(r"\s+", " ", (text or "").strip().lower())


def subfolder(company: str, role: str) -> str:
    """documents/README.md "Subfolder naming": the engine's archive folder rule."""
    name = f"{company}_{role}".lower().replace(" ", "_")
    name = re.sub(r"[^a-z0-9_]", "", name)
    return re.sub(r"_+", "_", name).strip("_")


def recruiter_links(company: str, title: str) -> dict:
    # Same two LinkedIn people searches /scrape Step 4.5 builds - a pure function.
    base = "https://www.linkedin.com/search/results/people/?origin=GLOBAL_SEARCH_HEADER&keywords="
    return {
        "recruiters": base + quote(f"{company} recruiter"),
        "peers": base + quote(f"{company} {' '.join(title.split()[:4])}"),
    }


def _seen(ws: Path) -> dict:
    path = ws / "job_scraper" / "seen_jobs.json"
    if not path.exists():
        return {}
    doc = json.loads(path.read_text(encoding="utf-8"))
    return doc.get("seen", doc) if isinstance(doc, dict) else {}


def _tracker(ws: Path) -> list[dict]:
    path = ws / "job_search_tracker.csv"
    if not path.exists():
        return []
    return list(csv.DictReader(io.StringIO(path.read_text(encoding="utf-8"))))


def _pdf_for(ws: Path, source: str) -> str | None:
    if not source:
        return None
    pdf = Path(source).with_suffix(".pdf")
    return pdf.as_posix() if (ws / pdf).is_file() else None


def _archive(ws: Path, company: str, role: str) -> dict:
    folder = subfolder(company, role)
    path = ws / "documents" / "applications" / folder
    files = sorted(p.name for p in path.iterdir() if p.is_file()) if folder and path.is_dir() else []
    return {"folder": f"documents/applications/{folder}" if files else None, "files": files}


def build_board(runs: list[dict]) -> dict:
    ws = settings.workspace
    seen, rows = _seen(ws), _tracker(ws)
    by_pair = {(norm(r.get("company", "")), norm(r.get("role", ""))): r for r in rows}

    active = {}
    for run in runs:
        key = (run.get("args") or {}).get("job_key")
        if key and run["status"] in ("queued", "running", "awaiting_input"):
            active[key] = run["id"]

    cards, matched = [], set()
    for key, e in seen.items():
        if not isinstance(e, dict):
            continue
        status = e.get("status")
        pair = (norm(e.get("company", "")), norm(e.get("title", "")))
        row = by_pair.get(pair)
        if row:
            matched.add(pair)
        vetoed = "FAIL" in (e.get("location_verdict"), e.get("language_gate"))
        if row:
            stage = "closed" if norm(row.get("status", "")) in FINAL else STAGE_BY_STATUS.get(norm(row.get("status", "")), "applied")
        elif status in ("expired", "skipped") or vetoed:
            stage = "closed"
        elif status == "ranked":
            stage = "shortlist"
        else:
            stage = "new"
        if key in active:
            stage = "preparing"
        cards.append(_card(ws, key, e, row, stage, active.get(key)))

    for row in rows:
        pair = (norm(row.get("company", "")), norm(row.get("role", "")))
        if pair in matched:
            continue
        key = "app:" + subfolder(row.get("company", ""), row.get("role", ""))
        status = norm(row.get("status", ""))
        stage = "closed" if status in FINAL else STAGE_BY_STATUS.get(status, "applied")
        if key in active:
            stage = "preparing"
        cards.append(_card(ws, key, {"title": row.get("role"), "company": row.get("company"), "url": row.get("source")}, row, stage, active.get(key)))

    cards.sort(key=lambda c: (-(c["score"] or -1), c["deadline"] or "9999"))
    return {"cards": cards, "has_tracker": bool(rows), "seen_total": len(seen)}


def _card(ws: Path, key: str, e: dict, row: dict | None, stage: str, run_id: str | None) -> dict:
    company, title = e.get("company") or "", e.get("title") or ""
    score = e.get("rank_score")
    if row and str(row.get("fit_rating", "")).strip().isdigit():
        score = int(row["fit_rating"])
    app = None
    if row:
        app = {
            "status": row.get("status"), "date": row.get("date"), "notes": row.get("notes"),
            "cv_pdf": _pdf_for(ws, row.get("cv_file", "")),
            "cover_pdf": _pdf_for(ws, row.get("cover_letter_file", "")),
            **_archive(ws, row.get("company", ""), row.get("role", "")),
        }
    return {
        "key": key, "title": title, "company": company,
        "url": e.get("url") or (row or {}).get("source") or None,
        "location": e.get("location"), "stage": stage, "score": score,
        "verdict": e.get("rank_verdict"), "deadline": e.get("deadline") or (row or {}).get("deadline") or None,
        "posted_date": e.get("posted_date"), "portal": e.get("portal"),
        "guard": e.get("guard"), "location_verdict": e.get("location_verdict"),
        "language_gate": e.get("language_gate"), "language_note": e.get("language_note"),
        "strengths": e.get("strengths") or [], "gaps": e.get("gaps") or [],
        "fit": e.get("fit"), "links": recruiter_links(company, title) if company else {},
        "application": app, "run_id": run_id,
    }


def artifact_path(rel: str) -> Path:
    """Resolve a requested artifact and refuse anything outside the allowed
    output folders (same idea as brain's tools.safe_path)."""
    ws = settings.workspace.resolve()
    target = (ws / rel).resolve()
    if not target.is_relative_to(ws):
        raise ValueError("path escapes the workspace")
    rel_posix = target.relative_to(ws).as_posix()
    if not any(rel_posix == r or rel_posix.startswith(r + "/") for r in ARTIFACT_ROOTS):
        raise ValueError("not an artifact folder")
    if target.suffix.lower() not in ARTIFACT_EXTS:
        raise ValueError("file type not served")
    if not target.is_file():
        raise FileNotFoundError(rel)
    return target


def artifact_text(path: Path) -> str:
    """Copyable text. For a PDF this is its text layer, i.e. what an ATS reads."""
    if path.suffix.lower() != ".pdf":
        return path.read_text(encoding="utf-8", errors="replace")
    from pypdf import PdfReader

    return "\n\n".join((page.extract_text() or "").strip() for page in PdfReader(path).pages).strip()
