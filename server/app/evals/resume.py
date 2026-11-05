"""Resume extraction eval (14-Career-Copilot.md §4.3): is "found X roles, Y schools, Z skills" right?

Run inside jv-api (jv-jobs up for Claude; stop it, or pass --local, for the local model):
    docker exec jv-api python -m app.evals.resume
    docker exec jv-api python -m app.evals.resume --bootstrap Profile/resume/cv.pdf   # draft an expected file

Expected files live in the VAULT (<vault>/.system/evals/resume-*.json), never in this repo:
    {"path": "Profile/resume/cv.pdf",
     "roles": [{"title": "...", "company": "...", "start": "2023-01", "end": null}],
     "schools": ["..."], "skills": ["..."]}
A bootstrapped file is the current extraction: check every line by hand before trusting it.
Bar to ship: every role and school found with exact dates, skills precision >= 0.95, recall >= 0.85.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import time
from pathlib import Path

from app.config import settings
from app.routers import career
from app.services import resume


def _fold(text) -> str:
    return " ".join(str(text or "").casefold().split())


def score(expected: dict, profile: dict) -> dict:
    got_roles = {(_fold(r["company"]), _fold(r["title"])): r for r in profile["experience"]}
    roles = expected.get("roles") or []
    found = [r for r in roles if (_fold(r["company"]), _fold(r["title"])) in got_roles]
    dates = [r for r in found
             if (got := got_roles[(_fold(r["company"]), _fold(r["title"]))])["start"] == r.get("start")
             and got["end"] == r.get("end")]
    schools = {_fold(e["school"]) for e in profile["education"]}
    want_skills = {_fold(s) for s in expected.get("skills") or []}
    got_skills = {_fold(s) for items in profile["skills"].values() for s in items}
    hit = len(want_skills & got_skills)
    return {
        "roles_found": f"{len(found)}/{len(roles)}",
        "role_dates_exact": f"{len(dates)}/{len(found)}",
        "extra_roles": len(got_roles) - len(found),
        "schools_found": f"{sum(_fold(s) in schools for s in expected.get('schools') or [])}/{len(expected.get('schools') or [])}",
        "skills_precision": round(hit / len(got_skills), 3) if got_skills else None,
        "skills_recall": round(hit / len(want_skills), 3) if want_skills else None,
    }


async def _run(path: str, local: bool) -> tuple[dict, str, float]:
    if local:
        settings.jobs_secret = ""  # makes _extract skip Claude
    start = time.time()
    text = await career._resume_text(path)
    raw, engine = await career._extract(text)
    return resume.check(raw, text)["profile"], engine, time.time() - start


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--bootstrap", metavar="PATH", help="write an expected file from the current extraction")
    parser.add_argument("--local", action="store_true", help="use the local model even when jv-jobs is up")
    args = parser.parse_args()
    folder = settings.vault_path / ".system" / "evals"

    if args.bootstrap:
        profile, engine, _ = asyncio.run(_run(args.bootstrap, args.local))
        out = folder / f"resume-{Path(args.bootstrap).stem}.json"
        if out.exists():
            out = out.with_name(f"{out.stem}-{time.strftime('%Y%m%d-%H%M%S')}.json")
        folder.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps({
            "path": args.bootstrap,
            "roles": [{k: r[k] for k in ("title", "company", "start", "end")} for r in profile["experience"]],
            "schools": [e["school"] for e in profile["education"]],
            "skills": [s for items in profile["skills"].values() for s in items],
        }, indent=2, ensure_ascii=False), encoding="utf-8")
        print(f"Wrote {out.name} ({engine}). Check every line by hand before using it.")
        return

    files = sorted(folder.glob("resume-*.json"))
    if not files:
        print(f"No expected files in {folder}. Draft one with --bootstrap <vault path of a resume>.")
        return
    for file in files:
        expected = json.loads(file.read_text(encoding="utf-8"))
        profile, engine, elapsed = asyncio.run(_run(expected["path"], args.local))
        print(json.dumps({"file": file.name, "engine": engine, "seconds": round(elapsed, 1), **score(expected, profile)}))


if __name__ == "__main__":
    main()
