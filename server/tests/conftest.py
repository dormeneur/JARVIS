import os
import json
from pathlib import Path
from unittest.mock import patch

import pytest
from fastapi.testclient import TestClient


@pytest.fixture
def tmp_vault(tmp_path: Path) -> Path:
    personal = tmp_path / "Personal"
    personal.mkdir()
    (personal / "notes.md").write_text("# My Notes\nSome content here.", encoding="utf-8")

    work = tmp_path / "Work"
    work.mkdir()
    (work / "project.md").write_text("# Project Plan", encoding="utf-8")

    (tmp_path / "readme.md").write_text("# JARVIS Vault", encoding="utf-8")

    system = tmp_path / "system"
    system.mkdir()

    return tmp_path


@pytest.fixture
def vault_settings(tmp_vault: Path):
    env = {
        "JARVIS_VAULT_PATH": str(tmp_vault),
        "JARVIS_JWT_SECRET": "test-secret-key-for-unit-tests-only",
        "JARVIS_JWT_EXPIRY_HOURS": "24",
        "JARVIS_MAX_DEVICES": "5",
    }
    with patch.dict(os.environ, env):
        from app.config import Settings
        test_settings = Settings()
        with patch("app.config.settings", test_settings), \
             patch("app.services.vault.settings", test_settings), \
             patch("app.services.auth.settings", test_settings), \
             patch("app.services.sync.settings", test_settings):
            yield test_settings


@pytest.fixture
def client(vault_settings) -> TestClient:
    from app.main import app
    return TestClient(app)


@pytest.fixture
def setup_secret(tmp_vault: Path, vault_settings) -> str:
    from app.services import auth
    secret = auth.get_or_create_setup_secret()
    assert secret is not None
    return secret


@pytest.fixture
def registered_device(client: TestClient, setup_secret: str) -> dict:
    response = client.post(
        "/auth/register",
        json={"device_name": "test_laptop", "setup_secret": setup_secret},
    )
    assert response.status_code == 201
    return response.json()


@pytest.fixture
def auth_headers(registered_device: dict) -> dict:
    return {"Authorization": f"Bearer {registered_device['access_token']}"}
