import json
import os
import secrets
from pathlib import Path
import pytest
from fastapi.testclient import TestClient
from app.main import app
from app.services import auth

# Crypto imports
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.kdf.pbkdf2 import PBKDF2HMAC
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

client = TestClient(app)

@pytest.fixture
def clean_vault(tmp_path):
    vault_path = tmp_path / "vault"
    vault_path.mkdir()
    (vault_path / "system").mkdir()
    from app.config import settings
    old_path = settings.vault_path
    settings.vault_path = vault_path
    yield vault_path
    settings.vault_path = old_path

def derive_key(pin: str, salt: bytes) -> bytes:
    """Simulate Dart's PBKDF2 derivation."""
    kdf = PBKDF2HMAC(
        algorithm=hashes.SHA256(),
        length=32,
        salt=salt,
        iterations=100000,
    )
    return kdf.derive(pin.encode())

def encrypt_secret(pin: str, plaintext: str) -> bytes:
    """Simulate Dart's client-side encryption."""
    salt = os.urandom(16)
    key = derive_key(pin, salt)
    aesgcm = AESGCM(key)
    nonce = os.urandom(12)
    ciphertext = aesgcm.encrypt(nonce, plaintext.encode(), None)
    # Blob format: salt(16) + nonce(12) + ciphertext
    return salt + nonce + ciphertext

def decrypt_secret(pin: str, blob: bytes) -> str:
    """Simulate Dart's client-side decryption."""
    salt = blob[:16]
    nonce = blob[16:28]
    ciphertext = blob[28:]
    key = derive_key(pin, salt)
    aesgcm = AESGCM(key)
    return aesgcm.decrypt(nonce, ciphertext, None).decode()

def test_phase4_e2e_sync_and_revocation(clean_vault):
    # 1. Register Device A (Authorized by default)
    resp_a = client.post("/auth/register", json={
        "device_name": "Device A",
        "setup_secret": auth.get_or_create_setup_secret()
    })
    id_a = resp_a.json()["device_id"]
    headers_a = {"Authorization": f"Bearer {resp_a.json()['access_token']}"}

    # 2. Register Device B (Unauthorized by default)
    resp_b = client.post("/auth/register/device", json={"device_name": "Device B"}, headers=headers_a)
    id_b = resp_b.json()["device_id"]
    headers_b = {"Authorization": f"Bearer {resp_b.json()['access_token']}"}

    # 3. Device A: Encrypt and Push Secret
    pin = "1234"
    plaintext = "TopSecretValue_2026"
    blob = encrypt_secret(pin, plaintext)
    
    secret_path = "Secrets/vault.jvs"
    push_metadata = {
        "path": secret_path,
        "content_hash": "sha256:test",
        "last_modified": "2026-04-14T12:00:00Z",
        "base_version": 0
    }
    client.post(
        "/sync/push",
        data={"metadata": json.dumps(push_metadata)},
        files={"file": ("vault.jvs", blob)},
        headers=headers_a
    )

    # 4. Device B: Verify Stealth & Blocked (Initial)
    # Manifest stealth
    resp_manifest_b = client.post("/sync/manifest", json={"manifest": []}, headers=headers_b)
    assert secret_path not in [e["path"] for e in resp_manifest_b.json()["to_pull"]]
    assert "Secrets/" not in resp_manifest_b.text
    
    # Blocked download
    resp_pull_b = client.post("/sync/pull", json={"path": secret_path}, headers=headers_b)
    assert resp_pull_b.status_code == 403

    # 5. Device A: Authorize Device B
    client.post("/auth/authorize_secrets", params={"device_id": id_b, "authorized": True}, headers=headers_a)

    # 6. Device B: Pull and Decrypt
    # Manifest now includes it
    resp_manifest_b_auth = client.post("/sync/manifest", json={"manifest": []}, headers=headers_b)
    assert secret_path in [e["path"] for e in resp_manifest_b_auth.json()["to_pull"]]
    
    # Download works
    resp_pull_b_auth = client.post("/sync/pull", json={"path": secret_path}, headers=headers_b)
    assert resp_pull_b_auth.status_code == 200
    
    # Decryption Proof
    decrypted = decrypt_secret(pin, resp_pull_b_auth.content)
    assert decrypted == plaintext
    print(f"\n[E2E Success] Device B decrypted: {decrypted}")

    # 7. Device A: Revoke Device B
    client.post("/auth/authorize_secrets", params={"device_id": id_b, "authorized": False}, headers=headers_a)

    # 8. Device B: Verify Stealth & Blocked (After Revocation)
    resp_manifest_b_revoked = client.post("/sync/manifest", json={"manifest": []}, headers=headers_b)
    assert "Secrets/" not in resp_manifest_b_revoked.text
    
    resp_pull_b_revoked = client.post("/sync/pull", json={"path": secret_path}, headers=headers_b)
    assert resp_pull_b_revoked.status_code == 403
    print("[E2E Success] Revocation confirmed: Device B blocked again.")
