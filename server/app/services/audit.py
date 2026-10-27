"""Append-only JSON-lines audit log at <vault>/.system/audit.log (06-Security-Model).

`.system/` is unreachable through the API (dot paths are rejected), skipped by
sync, and excluded from the RAG index — the log is server-only.
"""
from __future__ import annotations

import json
import logging
from datetime import datetime, timezone
from logging.handlers import RotatingFileHandler
from pathlib import Path

from app import config

MAX_BYTES = 10 * 1024 * 1024
BACKUP_COUNT = 4  # 5 files x 10 MB = the spec's 50 MB cap

_logger = logging.getLogger("jarvis.audit")
_logger.propagate = False
_logger.setLevel(logging.INFO)
_handler_path: Path | None = None


def log_path() -> Path:
    return config.settings.vault_path / ".system" / "audit.log"


def _ensure_handler() -> None:
    # Re-bind if the vault path changed (tests swap it per case).
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


def record(event: str, **fields) -> None:
    entry = {"timestamp": datetime.now(tz=timezone.utc).isoformat(), "event": event, **fields}
    try:
        _ensure_handler()
        _logger.info(json.dumps(entry, default=str))
    except OSError:
        # Never fail a user request because the audit disk write failed.
        logging.getLogger(__name__).exception("Audit write failed: %s", entry)
