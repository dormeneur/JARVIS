"""Summarise the trace log: p50/p95 latency and token use per route and model.

    docker exec jv-brain python -m app.evals.traces --days 7
"""
from __future__ import annotations

import argparse
import json
import statistics
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List

from app.services.traces import log_path


def _quantile(values: List[float], q: float) -> float:
    if not values:
        return 0.0
    if len(values) == 1:
        return float(values[0])
    cuts = statistics.quantiles(values, n=100, method="inclusive")
    return cuts[min(98, max(0, int(q * 100) - 1))]


def summarize(entries: Iterable[Dict[str, Any]], since: datetime) -> List[Dict[str, Any]]:
    groups: Dict[tuple, List[Dict[str, Any]]] = defaultdict(list)
    for e in entries:
        try:
            when = datetime.fromisoformat(e["ts"])
        except (KeyError, ValueError):
            continue
        if when >= since:
            groups[(e.get("route", "?"), e.get("model", "?"))].append(e)
    rows = []
    for (route, model), items in sorted(groups.items()):
        totals = [i["ms_total"] for i in items if "ms_total" in i]
        firsts = [i["ms_first_token"] for i in items if i.get("ms_first_token") is not None]
        outs = [i.get("output_tokens", 0) for i in items]
        rows.append({
            "route": route, "model": model, "count": len(items),
            "p50_ms": _quantile(totals, 0.5), "p95_ms": _quantile(totals, 0.95),
            "p50_first_ms": _quantile(firsts, 0.5),
            "mean_out_tokens": sum(outs) / len(outs) if outs else 0.0,
            "errors": sum(1 for i in items if i.get("error")),
        })
    return rows


def read_entries(path: Path) -> Iterable[Dict[str, Any]]:
    if not path.exists():
        return
    for line in path.read_text(encoding="utf-8").splitlines():
        try:
            yield json.loads(line)
        except ValueError:
            continue


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--days", type=int, default=7)
    args = parser.parse_args()
    since = datetime.now(tz=timezone.utc) - timedelta(days=args.days)
    rows = summarize(read_entries(log_path()), since)
    if not rows:
        print(f"No traces in the last {args.days} days ({log_path()}).")
        return
    print(f"{'route':<14}{'model':<18}{'n':>5}{'p50 ms':>9}{'p95 ms':>9}{'1st tok':>9}{'out tok':>9}{'err':>5}")
    for r in rows:
        print(f"{r['route']:<14}{r['model']:<18}{r['count']:>5}{r['p50_ms']:>9.0f}{r['p95_ms']:>9.0f}"
              f"{r['p50_first_ms']:>9.0f}{r['mean_out_tokens']:>9.0f}{r['errors']:>5}")


if __name__ == "__main__":
    main()
