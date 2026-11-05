import asyncio
from datetime import datetime, timedelta, timezone
from unittest.mock import AsyncMock, MagicMock

import pytest

from app.config import settings
from app.services import scheduler
from app.services.scheduler import is_due

D = lambda h, m=0, day=10: datetime(2026, 11, day, h, m, tzinfo=timezone.utc)  # noqa: E731
DAILY = ("daily", "07:30")


def test_daily_before_and_after_the_time():
    assert is_due(D(7, 29), None, DAILY) is False  # never run, but before the slot: not due yet
    assert is_due(D(7, 30), None, DAILY) is True
    assert is_due(D(12), D(7, 30, day=9), DAILY) is True  # last ran yesterday


def test_daily_already_ran_today_is_not_due():
    assert is_due(D(9), D(7, 31), DAILY) is False


def test_daily_missed_for_days_is_due_exactly_once():
    last = D(7, 30, day=5)
    assert is_due(D(9), last, DAILY) is True
    assert is_due(D(9, 1), D(9), DAILY) is False  # after catching up it ran once


def test_interval_never_run_not_yet_and_overdue():
    spec = ("interval", 30)
    assert is_due(D(9), None, spec) is True
    assert is_due(D(9, 29), D(9), spec) is False
    assert is_due(D(9, 30), D(9), spec) is True


def test_unknown_kind_raises():
    with pytest.raises(ValueError):
        is_due(D(9), None, ("weekly", "x"))


@pytest.fixture
def vault(tmp_path, monkeypatch):
    monkeypatch.setattr(settings, "vault_path", tmp_path)
    monkeypatch.setattr(settings, "timezone", "UTC")
    monkeypatch.setattr(settings, "digest_time", "07:30")
    return tmp_path


def test_state_round_trip_and_garbage_file(vault):
    assert scheduler.load_state() == {}
    scheduler.save_state({"digest": "2026-11-10T07:30:00+00:00"})
    assert scheduler.load_state() == {"digest": "2026-11-10T07:30:00+00:00"}
    scheduler.state_path().write_text("{broken", encoding="utf-8")
    assert scheduler.load_state() == {}


def test_run_due_runs_each_job_once_and_survives_a_failing_job(vault, monkeypatch):
    calls = []

    async def ok(app):
        calls.append("ok")

    async def bad(app):
        calls.append("bad")
        raise RuntimeError("boom")

    monkeypatch.setattr(scheduler, "jobs", lambda: [("a", ("interval", 30), bad), ("b", ("interval", 30), ok)])
    now = D(9)
    assert asyncio.run(scheduler.run_due(MagicMock(), now)) == ["b"]
    assert calls == ["bad", "ok"]
    # both are recorded as run (a failing job must not hot-loop), so the next tick does nothing
    assert asyncio.run(scheduler.run_due(MagicMock(), now + timedelta(minutes=1))) == []


def test_unknown_timezone_falls_back_to_utc(vault, monkeypatch):
    monkeypatch.setattr(settings, "timezone", "Not/AZone")
    assert scheduler.local_tz() is timezone.utc


def test_default_job_list_has_the_four_jobs(vault):
    assert [name for name, _, _ in scheduler.jobs()] == ["reindex", "digest", "trash_prune", "proposal_expire"]


def test_reindex_job_does_not_start_a_second_pass(vault):
    app = MagicMock()
    app.state.indexer.is_indexing = True
    asyncio.run(scheduler._reindex(app))
    app.state.indexer.start_background_indexing.assert_not_called()
    app.state.indexer.is_indexing = False
    asyncio.run(scheduler._reindex(app))
    app.state.indexer.start_background_indexing.assert_called_once()
