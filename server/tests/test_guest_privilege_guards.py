"""Regression tests: a non-admin (guest) device must not reach server state,
Secrets, or admin actions through any path spelling."""
import json

import pytest
from fastapi.testclient import TestClient


@pytest.fixture
def guest_headers(client: TestClient, auth_headers: dict) -> dict:
    resp = client.post("/auth/register/device", json={"device_name": "guest"}, headers=auth_headers)
    assert resp.status_code == 201
    return {"Authorization": f"Bearer {resp.json()['access_token']}"}


@pytest.fixture
def secrets_file(tmp_vault):
    (tmp_vault / "Secrets").mkdir(exist_ok=True)
    (tmp_vault / "Secrets" / "bank.jvs").write_text("ciphertext", encoding="utf-8")


@pytest.mark.parametrize("path", ["system/devices.json", "System/devices.json", "./system/devices.json"])
def test_system_dir_unreachable_even_for_admin(client, auth_headers, path):
    assert client.get(f"/files/{path}", headers=auth_headers).status_code == 400
    assert client.get(f"/download/{path}", headers=auth_headers).status_code == 400
    assert client.post("/sync/pull", json={"path": path}, headers=auth_headers).status_code == 400


def test_system_dir_hidden_from_root_listing(client, auth_headers):
    names = [e["name"] for e in client.get("/files", headers=auth_headers).json()["entries"]]
    assert "system" not in names


def test_sync_push_to_system_rejected(client, auth_headers):
    meta = json.dumps({"path": "system/devices.json", "content_hash": "sha256:x", "last_modified": "2026-01-01T00:00:00Z"})
    resp = client.post("/sync/push", data={"metadata": meta}, files={"file": ("d", b"{}")}, headers=auth_headers)
    assert resp.status_code == 400


@pytest.mark.parametrize("path", ["Secrets/bank.jvs", "secrets/bank.jvs", "./Secrets/bank.jvs", "Secrets"])
def test_guest_blocked_from_secrets_spellings(client, guest_headers, secrets_file, path):
    assert client.get(f"/files/{path}", headers=guest_headers).status_code == 403
    assert client.delete(f"/files/{path}", headers=guest_headers).status_code == 403


def test_guest_cannot_reset_vault(client, guest_headers, tmp_vault):
    assert client.post("/files/reset", headers=guest_headers).status_code == 403
    assert (tmp_vault / "readme.md").exists()


def test_guest_cannot_revoke_admin_but_can_revoke_self(client, auth_headers, guest_headers):
    devices = client.get("/auth/devices", headers=auth_headers).json()["devices"]
    admin_id = next(d["device_id"] for d in devices if d["is_secrets_authorized"])
    guest_id = next(d["device_id"] for d in devices if not d["is_secrets_authorized"])
    assert client.post("/auth/revoke", json={"device_id": admin_id}, headers=guest_headers).status_code == 403
    assert client.post("/auth/revoke", json={"device_id": guest_id}, headers=guest_headers).status_code == 200


def test_guest_cannot_mint_devices(client, guest_headers):
    resp = client.post("/auth/register/device", json={"device_name": "x"}, headers=guest_headers)
    assert resp.status_code == 403
