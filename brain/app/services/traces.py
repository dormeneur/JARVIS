"""Trace log: one JSON line per AI request in <vault>/.system/brain/traces.jsonl.

Latency, token counts, the tools used and the files cited: enough to compare models and settings
with numbers instead of feel. **No question or answer text** is written unless JARVIS_TRACE_TEXT=true.
Recording never raises and never slows a request down more than one small file append.
"""
from __future__ import annotations

import json
import logging
import time
from datetime import datetime, timezone
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Any, AsyncGenerator, Dict, List, Optional

from app.config import settings

MAX_BYTES = 10 * 1024 * 1024
BACKUP_COUNT = 4  # 5 files x 10 MB

_logger = logging.getLogger("jarvis.traces")
_logger.propagate = False
_logger.setLevel(logging.INFO)
_handler_path: Optional[Path] = None


def log_path() -> Path:
    return Path(settings.vault_path) / ".system" / "brain" / "traces.jsonl"


def _ensure_handler() -> None:
    global _handler_path
    path = log_path()
    if _handler_path == path:
        return
    for h in list(_logger.handlers):
        _logger.removeHandler(h)
        h.close()
    path.parent.mkdir(parents=True, exist_ok=True)
    handler = RotatingFileHandler(path, maxBytes=MAX_BYTES, backupCount=BACKUP_COUNT, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(message)s"))
    _logger.addHandler(handler)
    _handler_path = path


def record(**fields: Any) -> None:
    """Append one trace line. Swallows every error: tracing must never break a request."""
    try:
        entry = {"ts": datetime.now(tz=timezone.utc).isoformat(timespec="seconds"), **fields}
        _ensure_handler()
        _logger.info(json.dumps(entry, default=str, ensure_ascii=False))
    except Exception:  # noqa: BLE001
        logging.getLogger(__name__).debug("trace write failed", exc_info=True)


async def traced(
    route: str, stream: AsyncGenerator[str, None], *, model: str, query: str = ""
) -> AsyncGenerator[str, None]:
    """Pass an NDJSON stream through unchanged and record one trace when it ends (or is dropped)."""
    start = time.monotonic()
    first_token: Optional[float] = None
    tools: List[str] = []
    sources: List[str] = []
    out_tokens = prompt_tokens = 0
    error: Optional[str] = None
    try:
        async for line in stream:
            try:
                data = json.loads(line)
            except (ValueError, TypeError):
                data = None
            if isinstance(data, dict):
                if first_token is None and data.get("token"):
                    first_token = time.monotonic()
                if data.get("type") == "tool_start" and data.get("name"):
                    tools.append(data["name"])
                if data.get("error"):
                    error = str(data["error"])[:200]
                if "sources" in data:
                    sources = [s.get("path", "") for s in data.get("sources") or [] if isinstance(s, dict)]
                    out_tokens = int(data.get("tokens_used") or 0)
                    prompt_tokens = int(data.get("prompt_tokens") or 0)
            yield line
    except BaseException as exc:  # includes GeneratorExit/cancel when the client goes away
        error = error or type(exc).__name__
        raise
    finally:
        fields: Dict[str, Any] = {
            "route": route,
            "model": model,
            "ms_total": int((time.monotonic() - start) * 1000),
            "ms_first_token": int((first_token - start) * 1000) if first_token else None,
            "prompt_tokens": prompt_tokens,
            "output_tokens": out_tokens,
            "tools": tools,
            "sources": sources,
            "error": error,
        }
        if settings.trace_text and query:
            fields["query"] = query
        record(**fields)
