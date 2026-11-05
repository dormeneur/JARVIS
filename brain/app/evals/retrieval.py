"""Retrieval eval: does the search find the right file for the questions you actually ask?

Run inside the brain container:
    docker exec jv-brain python -m app.evals.retrieval --label baseline
    docker exec jv-brain python -m app.evals.retrieval --bootstrap 20     # draft questions

The golden set lives in the VAULT (<vault>/.system/evals/retrieval.jsonl), never in this repo:
it contains questions about your own life. One JSON object per line:
    {"q": "Where did I study?", "expect": ["Profile/education.md"]}
"""

from __future__ import annotations

import argparse
import asyncio
import json
import random
import time
from pathlib import Path
from typing import Any, Dict, Iterable, List

from app.config import settings

TOP_K = 10


def _norm(path: str) -> str:
    return path.replace("\\", "/").casefold()


def score(ranked_paths: List[str], expected: Iterable[str]) -> Dict[str, float]:
    """hit@1 / hit@5 / reciprocal rank for one question. Pure, so it is unit-tested."""
    want = {_norm(p) for p in expected}
    ranked = [_norm(p) for p in ranked_paths]
    first = next((i for i, p in enumerate(ranked) if p in want), None)
    return {
        "hit1": 1.0 if first == 0 else 0.0,
        "hit5": 1.0 if first is not None and first < 5 else 0.0,
        "rr": 1.0 / (first + 1) if first is not None and first < TOP_K else 0.0,
    }


def aggregate(rows: List[Dict[str, float]]) -> Dict[str, float]:
    n = len(rows)
    if not n:
        return {"n": 0, "hit1": 0.0, "hit5": 0.0, "mrr": 0.0}
    return {
        "n": n,
        "hit1": sum(r["hit1"] for r in rows) / n,
        "hit5": sum(r["hit5"] for r in rows) / n,
        "mrr": sum(r["rr"] for r in rows) / n,
    }


def evals_dir() -> Path:
    return Path(settings.vault_path) / ".system" / "evals"


def load_golden(path: Path) -> List[Dict[str, Any]]:
    items = []
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        item = json.loads(line)
        if item.get("q") and item.get("expect"):
            items.append(item)
    return items


async def _indexing_in_progress() -> bool:
    import httpx

    try:
        async with httpx.AsyncClient(timeout=3) as client:
            r = await client.get("http://localhost:8001/brain/status")
            return bool(r.json().get("indexing"))
    except Exception:  # noqa: BLE001 - if we can't tell, carry on
        return False


async def _build():
    from app.services.embedding_pipeline import EmbeddingPipeline
    from app.services.retriever import Retriever
    from app.services.vector_store import VectorStore

    from app.services.keyword_index import KeywordIndex

    embedder = EmbeddingPipeline(settings.ollama_url)
    store = VectorStore(settings.vectordb_url)
    keyword = KeywordIndex(Path(settings.vault_path) / ".system" / "brain" / "fts.db") if settings.hybrid_search else None
    return embedder, store, Retriever(embedder, store, keyword)


async def run(label: str) -> int:
    golden_path = evals_dir() / "retrieval.jsonl"
    if not golden_path.exists():
        print(f"No golden set yet. Create {golden_path} with one JSON per line:")
        print('  {"q": "Where did I study?", "expect": ["Profile/education.md"]}')
        print("Tip: `python -m app.evals.retrieval --bootstrap 20` drafts candidates to review.")
        return 0
    if await _indexing_in_progress():
        print("Indexing is in progress, try again when it finishes.")
        return 1

    embedder, store, retriever = await _build()
    try:
        indexed = {_norm(m.get("source_path", "")) for m in await store.get_all_metadata()}
        rows, misses, stale = [], [], 0
        for item in load_golden(golden_path):
            expect = item["expect"]
            if not any(_norm(p) in indexed for p in expect):
                stale += 1
                print(f"[stale] not in the index, skipped: {item['q']!r} -> {expect}")
                continue
            ranked = [s.path for s in await retriever.retrieve(item["q"], top_k=TOP_K)]
            r = score(ranked, expect)
            rows.append(r)
            if not r["hit5"]:
                misses.append((item["q"], expect, ranked[:3]))
    finally:
        await embedder.close()

    summary = aggregate(rows)
    print(
        f"\n{label}: n={summary['n']}  hit@1={summary['hit1']:.2f}  "
        f"hit@5={summary['hit5']:.2f}  MRR={summary['mrr']:.2f}  (stale skipped: {stale})"
    )
    for q, expect, top in misses:
        print(f"  MISS {q!r}\n       want {expect}\n       got  {top}")

    from app.services.incremental_indexer import INDEX_VERSION

    line = {
        "ts": time.strftime("%Y-%m-%dT%H:%M:%S"), "label": label, **summary, "stale": stale,
        "index_version": INDEX_VERSION, "embedding_model": settings.embedding_model,
        "hybrid": bool(getattr(settings, "hybrid_search", False)),
    }
    evals_dir().mkdir(parents=True, exist_ok=True)
    with open(evals_dir() / "history.jsonl", "a", encoding="utf-8") as f:
        f.write(json.dumps(line) + "\n")
    return 0


async def bootstrap(n: int) -> int:
    """Ask the LOCAL model for one question per random file. Written to a candidates file for the
    owner to review: it is never the golden set itself."""
    from app.services.document_loader import DocumentLoader
    from app.services.ollama_client import OllamaClient
    from app.services.tools import safe_path

    embedder, store, _ = await _build()
    try:
        paths = sorted({m.get("source_path") for m in await store.get_all_metadata() if m.get("source_path")})
    finally:
        await embedder.close()
    random.shuffle(paths)
    loader, client = DocumentLoader(settings.vault_path), OllamaClient(settings.ollama_url)
    schema = {"type": "object", "properties": {"question": {"type": "string"}}, "required": ["question"]}
    out = []
    for path in paths:
        if len(out) >= n:
            break
        try:
            text = loader._extract_content(safe_path(path, must_exist=True)) or ""
            if len(text) < 200:
                continue
            prompt = (
                "Write ONE short question a person would ask whose answer is in this note. "
                "Don't mention the file name.\n\n" + text[:2500]
            )
            reply = await client.chat_json([{"role": "user", "content": prompt}], schema)
            out.append({"q": reply.get("question", "").strip(), "expect": [path]})
        except Exception as exc:  # noqa: BLE001
            print(f"skip {path}: {exc}")
    evals_dir().mkdir(parents=True, exist_ok=True)
    target = evals_dir() / "retrieval.candidates.jsonl"
    if target.exists():  # never overwrite a file the owner may be reviewing
        target = target.with_name(f"retrieval.candidates-{time.strftime('%Y%m%d-%H%M%S')}.jsonl")
    target.write_text("\n".join(json.dumps(o) for o in out if o["q"]) + "\n", encoding="utf-8")
    print(f"Wrote {len(out)} candidates to {target}. Review them, then copy the good lines into retrieval.jsonl.")
    return 0


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--label", default="run")
    parser.add_argument("--bootstrap", type=int, metavar="N", help="draft N candidate questions with the local model")
    args = parser.parse_args()
    raise SystemExit(asyncio.run(bootstrap(args.bootstrap) if args.bootstrap else run(args.label)))


if __name__ == "__main__":
    main()
