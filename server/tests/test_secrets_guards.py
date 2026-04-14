import pytest
from fastapi.testclient import TestClient
from app.main import app
from app.services import auth
import os
import json
from pathlib import Path

client = TestClient(app)

@pytest.fixture
def clean_vault(tmp_path):
    vault_path = tmp_path / "vault"
    vault_path.mkdir()
    (vault_path / "system").mkdir()
    # Mock settings.vault_path
    from app.config import settings
    old_path = settings.vault_path
    settings.vault_path = vault_path
    yield vault_path
    settings.vault_path = old_path

def test_secrets_guards_and_manifest_stealth(clean_vault):
    # 1. Register Device A (First device -> Authorized)
    resp_a = client.post("/auth/register", json={
        "device_name": "Device A",
        "setup_secret": auth.get_or_create_setup_secret()
    })
    token_a = resp_a.json()["access_token"]
    id_a = resp_a.json()["device_id"]
    headers_a = {"Authorization": f"Bearer {token_a}"}

    # 2. Register Device B (Additional device -> Unauthorized)
    resp_b = client.post("/auth/register/device", json={"device_name": "Device B"}, headers=headers_a)
    token_b = resp_b.json()["access_token"]
    id_b = resp_b.json()["device_id"]
    headers_b = {"Authorization": f"Bearer {token_b}"}

    # 3. Create a secret using Device A
    secret_path = "Secrets/my_key.jvs"
    push_metadata = {
        "path": secret_path,
        "content_hash": "sha256:abc",
        "last_modified": "2026-04-14T12:00:00Z",
        "base_version": 0
    }
    client.post(
        "/sync/push",
        data={"metadata": json.dumps(push_metadata)},
        files={"file": ("my_key.jvs", b"encrypted_data")},
        headers=headers_a
    )

    # 4. Create a normal file using Device A
    normal_path = "notes.md"
    push_metadata_normal = {
        "path": normal_path,
        "content_hash": "sha256:def",
        "last_modified": "2026-04-14T12:00:00Z",
        "base_version": 0
    }
    client.post(
        "/sync/push",
        data={"metadata": json.dumps(push_metadata_normal)},
        files={"file": ("notes.md", b"normal_content")},
        headers=headers_a
    )

    # 5. FETCH MANIFEST as Device B (Unauthorized)
    # MUST NOT see Secrets/
    resp_manifest_b = client.post("/sync/manifest", json={"manifest": []}, headers=headers_b)
    to_pull_b = [e["path"] for e in resp_manifest_b.json()["to_pull"]]
    
    assert "notes.md" in to_pull_b
    assert "Secrets/my_key.jvs" not in to_pull_b
    # Also check full response body for any mention of Secrets
    assert "Secrets/" not in resp_manifest_b.text

    # 6. TRY PULL as Device B (Unauthorized)
    # MUST return 403
    resp_pull_b = client.post("/sync/pull", json={"path": secret_path}, headers=headers_b)
    assert resp_pull_b.status_code == 403

    # 7. TRY PUSH as Device B (Unauthorized)
    # MUST return 403
    resp_push_b = client.post(
        "/sync/push",
        data={"metadata": json.dumps({"path": "Secrets/hack.jvs", "content_hash": "sha256:hack", "last_modified": "2026-04-14T12:01:00Z", "base_version": 0})},
        headers=headers_b
    )
    assert resp_push_b.status_code == 403

    # 8. AUTHORIZE Device B using Device A
    client.post("/auth/authorize_secrets", params={"device_id": id_b}, headers=headers_a)

    # 9. FETCH MANIFEST as Device B (Now Authorized)
    # SHOULD see Secrets/
    resp_manifest_b2 = client.post("/sync/manifest", json={"manifest": []}, headers=headers_b)
    to_pull_b2 = [e["path"] for e in resp_manifest_b2.json()["to_pull"]]
    assert "Secrets/my_key.jvs" in to_pull_b2

    # 10. TRY PULL as Device B (Now Authorized)
    # SHOULD work
    resp_pull_b2 = client.post("/sync/pull", json={"path": secret_path}, headers=headers_b)
    assert resp_pull_b2.status_code == 200
    assert resp_pull_b2.content == b"encrypted_data"

def test_unauthorized_cannot_authorize(clean_vault):
    # Device A (Auth) -> Device B (Unauth) -> Device C (Unauth)
    resp_a = client.post("/auth/register", json={
        "device_name": "Device A",
        "setup_secret": auth.get_or_create_setup_secret()
    })
    token_a = resp_a.json()["access_token"]
    headers_a = {"Authorization": f"Bearer {token_a}"}

    resp_b = client.post("/auth/register/device", json={"device_name": "Device B"}, headers=headers_a)
    token_b = resp_b.json()["access_token"]
    headers_b = {"Authorization": f"Bearer {token_b}"}

    resp_c = client.post("/auth/register/device", json={"device_name": "Device C"}, headers=headers_a)
    id_c = resp_c.json()["device_id"]

    # Device B (Unauthorized) tries to authorize C
    resp_auth = client.post("/auth/authorize_secrets", params={"device_id": id_c}, headers=headers_b)
    assert resp_auth.status_code == 403
    assert "Only secrets-authorized" in resp_auth.json()["detail"]
