"""Shared test setup."""
import pytest

from app.config import settings


@pytest.fixture(autouse=True)
def _no_background_memory_extraction(monkeypatch):
    """Chat tests must not start the background fact extractor (it would call a real model).
    test_memory_extractor.py turns it back on for the calls it makes directly."""
    monkeypatch.setattr(settings, "memory_extraction", False)
