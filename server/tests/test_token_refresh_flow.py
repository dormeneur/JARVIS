"""
Token lifecycle tests: refresh, expiry, revocation, and the 401 flow
that the mobile app's handleUnauthorized() hook responds to.
"""
from datetime import datetime, timedelta, timezone

import jwt
import pytest
from fastapi.testclient import TestClient


class TestTokenRefreshFlow:
    def test_refresh_with_valid_token_returns_new_token(
        self, client: TestClient, auth_headers: dict, registered_device: dict
    ):
        resp = client.post("/auth/refresh", headers=auth_headers)
        assert resp.status_code == 200
        data = resp.json()
        assert data["access_token"] != registered_device["access_token"]
        assert data["device_id"] == registered_device["device_id"]

    def test_refresh_invalidates_old_token(
        self, client: TestClient, auth_headers: dict, vault_settings
    ):
        old_token = auth_headers["Authorization"].split(" ")[1]
        client.post("/auth/refresh", headers=auth_headers)
        # Old token should now be rejected
        resp = client.get("/files", headers={"Authorization": f"Bearer {old_token}"})
        assert resp.status_code == 401

    def test_new_token_is_usable_immediately(
        self, client: TestClient, auth_headers: dict
    ):
        refresh_resp = client.post("/auth/refresh", headers=auth_headers)
        new_token = refresh_resp.json()["access_token"]
        files_resp = client.get("/files", headers={"Authorization": f"Bearer {new_token}"})
        assert files_resp.status_code == 200

    def test_refresh_without_auth_returns_422(self, client: TestClient):
        resp = client.post("/auth/refresh")
        assert resp.status_code == 422

    def test_refresh_with_expired_token_returns_401(
        self, client: TestClient, registered_device: dict, vault_settings
    ):
        # Manually forge an expired token for the registered device
        payload = {
            "sub": registered_device["device_id"],
            "device_name": "test_laptop",
            "iat": datetime.now(tz=timezone.utc) - timedelta(hours=48),
            "exp": datetime.now(tz=timezone.utc) - timedelta(hours=1),
            "jti": "expired-refresh-jti",
        }
        expired_token = jwt.encode(payload, vault_settings.jwt_secret, algorithm="HS256")
        resp = client.post(
            "/auth/refresh",
            headers={"Authorization": f"Bearer {expired_token}"},
        )
        assert resp.status_code == 401


class TestReconnectFlow:
    """Device reconnect — app data cleared but server still has the device."""

    def test_reconnect_with_valid_secret_returns_token(
        self, client: TestClient, registered_device: dict
    ):
        resp = client.post(
            "/auth/reconnect",
            json={
                "device_name": "test_laptop",
                "device_secret": registered_device["device_secret"],
            },
        )
        assert resp.status_code == 200
        assert resp.json()["access_token"]

    def test_reconnect_with_wrong_secret_returns_401(
        self, client: TestClient
    ):
        resp = client.post(
            "/auth/reconnect",
            json={"device_name": "test_laptop", "device_secret": "wrong-secret"},
        )
        assert resp.status_code in (401, 403, 404)

    def test_reconnect_unknown_device_returns_error(self, client: TestClient):
        resp = client.post(
            "/auth/reconnect",
            json={"device_name": "ghost_device", "device_secret": "any-secret"},
        )
        assert resp.status_code in (401, 403, 404)

    def test_reconnect_token_is_usable(
        self, client: TestClient, registered_device: dict
    ):
        reconnect_resp = client.post(
            "/auth/reconnect",
            json={
                "device_name": "test_laptop",
                "device_secret": registered_device["device_secret"],
            },
        )
        new_token = reconnect_resp.json()["access_token"]
        resp = client.get("/files", headers={"Authorization": f"Bearer {new_token}"})
        assert resp.status_code == 200
