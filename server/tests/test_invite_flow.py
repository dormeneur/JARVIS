"""
QR invite registration flow tests.

Covers:
1. Admin can create an invite token
2. Non-admin cannot create an invite token
3. Guest can register with a valid token
4. Token is single-use (reuse fails)
5. Expired token is rejected
6. Guest JWT is NOT secrets-authorized
"""
import time
from unittest.mock import patch
from datetime import datetime, timedelta, timezone

import pytest
from fastapi.testclient import TestClient


@pytest.fixture
def admin_headers(auth_headers):
    """auth_headers points to the first device, which is always secrets-authorized."""
    return auth_headers


@pytest.fixture
def guest_headers(client: TestClient, admin_headers: dict) -> dict:
    """Register a second device that is NOT secrets-authorized."""
    resp = client.post(
        "/auth/register/device",
        json={"device_name": "guest_phone"},
        headers=admin_headers,
    )
    assert resp.status_code == 201
    return {"Authorization": f"Bearer {resp.json()['access_token']}"}


class TestCreateInvite:
    def test_admin_can_create_invite(self, client: TestClient, admin_headers: dict):
        resp = client.post("/auth/invite", headers=admin_headers)
        assert resp.status_code == 201
        data = resp.json()
        assert "invite_token" in data
        assert data["ttl_seconds"] > 0
        assert "expires_at" in data

    def test_non_admin_cannot_create_invite(
        self, client: TestClient, guest_headers: dict
    ):
        resp = client.post("/auth/invite", headers=guest_headers)
        assert resp.status_code == 403

    def test_unauthenticated_cannot_create_invite(self, client: TestClient):
        resp = client.post("/auth/invite")
        assert resp.status_code == 422  # missing auth header


class TestRegisterViaInvite:
    def test_valid_token_registers_device(
        self, client: TestClient, admin_headers: dict
    ):
        # Admin creates invite
        invite_resp = client.post("/auth/invite", headers=admin_headers)
        token = invite_resp.json()["invite_token"]

        # Guest registers
        resp = client.post(
            "/auth/invite/register",
            json={
                "device_name": "new_guest",
                "invite_token": token,
                "server_url": "http://localhost:8000",
            },
        )
        assert resp.status_code == 201
        data = resp.json()
        assert data["device_name"] == "new_guest"
        assert data["access_token"]
        assert data["device_secret"]
        # Guests are never secrets-authorized
        assert data["is_secrets_authorized"] is False

    def test_registered_device_can_authenticate(
        self, client: TestClient, admin_headers: dict
    ):
        invite_resp = client.post("/auth/invite", headers=admin_headers)
        token = invite_resp.json()["invite_token"]

        reg_resp = client.post(
            "/auth/invite/register",
            json={
                "device_name": "auth_guest",
                "invite_token": token,
                "server_url": "http://localhost:8000",
            },
        )
        new_token = reg_resp.json()["access_token"]
        me_resp = client.get(
            "/auth/me", headers={"Authorization": f"Bearer {new_token}"}
        )
        assert me_resp.status_code == 200
        assert me_resp.json()["device_name"] == "auth_guest"

    def test_token_is_single_use(self, client: TestClient, admin_headers: dict):
        invite_resp = client.post("/auth/invite", headers=admin_headers)
        token = invite_resp.json()["invite_token"]

        # First registration succeeds
        client.post(
            "/auth/invite/register",
            json={
                "device_name": "first",
                "invite_token": token,
                "server_url": "http://localhost:8000",
            },
        )

        # Second registration with same token fails
        resp = client.post(
            "/auth/invite/register",
            json={
                "device_name": "second",
                "invite_token": token,
                "server_url": "http://localhost:8000",
            },
        )
        assert resp.status_code in (400, 403, 422)

    def test_invalid_token_rejected(self, client: TestClient):
        resp = client.post(
            "/auth/invite/register",
            json={
                "device_name": "attacker",
                "invite_token": "totally-fake-token",
                "server_url": "http://localhost:8000",
            },
        )
        assert resp.status_code in (400, 403, 422)

    def test_guest_cannot_access_secrets(
        self, client: TestClient, admin_headers: dict, tmp_vault
    ):
        """Guest JWT must be denied access to the Secrets directory."""
        # Create a file in Secrets
        (tmp_vault / "Secrets").mkdir(exist_ok=True)
        (tmp_vault / "Secrets" / "key.jvs").write_bytes(b"\x00ENCRYPTED")

        invite_resp = client.post("/auth/invite", headers=admin_headers)
        token = invite_resp.json()["invite_token"]
        reg_resp = client.post(
            "/auth/invite/register",
            json={
                "device_name": "secrets_attacker",
                "invite_token": token,
                "server_url": "http://localhost:8000",
            },
        )
        guest_token = reg_resp.json()["access_token"]

        resp = client.get(
            "/files/Secrets/key.jvs",
            headers={"Authorization": f"Bearer {guest_token}"},
        )
        assert resp.status_code == 403

    def test_guest_cannot_create_invite(
        self, client: TestClient, admin_headers: dict
    ):
        """A device registered via invite must not be able to create new invites."""
        invite_resp = client.post("/auth/invite", headers=admin_headers)
        token = invite_resp.json()["invite_token"]
        reg_resp = client.post(
            "/auth/invite/register",
            json={
                "device_name": "cascade_guest",
                "invite_token": token,
                "server_url": "http://localhost:8000",
            },
        )
        guest_jwt = reg_resp.json()["access_token"]

        resp = client.post(
            "/auth/invite",
            headers={"Authorization": f"Bearer {guest_jwt}"},
        )
        assert resp.status_code == 403


class TestInviteTokenExpiry:
    def test_expired_token_is_rejected(
        self, client: TestClient, admin_headers: dict, vault_settings
    ):
        """Simulate an already-expired token in the token store."""
        from app.services import auth as auth_svc

        # Create a token that's already expired
        expired = (
            datetime.now(tz=timezone.utc) - timedelta(minutes=1)
        ).isoformat()
        tokens = auth_svc._load_invite_tokens()
        tokens["expired-token-xyz"] = expired
        auth_svc._save_invite_tokens(tokens)

        resp = client.post(
            "/auth/invite/register",
            json={
                "device_name": "late_guest",
                "invite_token": "expired-token-xyz",
                "server_url": "http://localhost:8000",
            },
        )
        assert resp.status_code in (400, 403, 422)
