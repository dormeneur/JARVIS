"""Per-client, per-endpoint-group request limits (06-Security-Model endpoint matrix).

Deviations from the spec table, on purpose: sync and files push/pull one
request per file, so the spec's 10/min and 60/min would break a normal sync.
The strict limits sit where abuse matters: unauthenticated credential
endpoints, the LLM, and backups.
"""
from __future__ import annotations

import hashlib
import time

from fastapi import Request

# Unauthenticated endpoints that accept a credential. Keyed by IP only, so a
# fake Authorization header cannot buy a fresh bucket. Behind `tailscale serve`
# every client shows as 127.0.0.1, which makes this one shared bucket — fine
# for endpoints a real user hits a few times a year.
CREDENTIAL_PATHS = frozenset({"/auth/register", "/auth/reconnect", "/auth/invite/register"})
CREDENTIAL_LIMIT = 10

# First matching prefix wins.
GROUP_LIMITS: list[tuple[str, int]] = [
    ("/health", 120),
    ("/backup/export", 2),
    ("/ask", 30),
    ("/sync", 3000),
]
DEFAULT_LIMIT = 600

# ponytail: fixed one-minute window, cleared wholesale each minute so memory is
# bounded by one minute of clients. Allows up to 2x a limit across a window
# boundary; switch to a sliding window if that burst ever matters.
_window = -1
_counts: dict[tuple[str, str], int] = {}


def reset() -> None:
    global _window
    _window = -1
    _counts.clear()


def _classify(request: Request) -> tuple[str, int, str]:
    path = request.url.path
    ip = request.client.host if request.client else "unknown"
    if path in CREDENTIAL_PATHS:
        return "credential", CREDENTIAL_LIMIT, ip

    group, limit = next(
        ((prefix, lim) for prefix, lim in GROUP_LIMITS if path.startswith(prefix)),
        ("default", DEFAULT_LIMIT),
    )
    token = request.headers.get("authorization")
    who = hashlib.sha256(token.encode()).hexdigest()[:16] if token else ip
    return group, limit, who


def hit(request: Request) -> tuple[bool, str, int]:
    """Count this request. Returns (allowed, group, seconds_until_reset)."""
    global _window
    now = time.time()
    window = int(now // 60)
    if window != _window:
        _window = window
        _counts.clear()

    group, limit, who = _classify(request)
    key = (group, who)
    _counts[key] = _counts.get(key, 0) + 1
    retry_after = 60 - int(now % 60)
    return _counts[key] <= limit, group, retry_after
