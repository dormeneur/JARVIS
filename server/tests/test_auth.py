import json
import time
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

import jwt
import pytest
from fastapi.testclient import TestClient


class TestSetupSecret:
    def test_generates_secret_on_first_run(self, vault_settings, tmp_vault):
        from app.services import auth
        secret = auth.get_or_create_setup_secret()
        assert secret is not None
        assert len(secret) > 16
        assert (tmp_vault / "system" / "setup_secret.txt").exists()

    def test_returns_same_secret_on_subsequent_calls(self, vault_settings, tmp_vault):
        from app.services import auth
        s1 = auth.get_or_create_setup_secret()
        s2 = auth.get_or_create_setup_secret()
        assert s1 == s2

    def test_returns_none_after_device_registered(self, client, setup_secret, tmp_vault):
        from app.services import auth
        client.post(
            "/auth/register",
            json={"device_name": "laptop", "setup_secret": setup_secret},
        )
        assert auth.get_or_create_setup_secret() is None
        assert not (tmp_vault / "system" / "setup_secret.txt").exists()


class TestRegistration:
    def test_first_device_registration(self, client, setup_secret):
        response = client.post(
            "/auth/register",
            json={"device_name": "my_phone", "setup_secret": setup_secret},
        )
        assert response.status_code == 201
        data = response.json()
        assert data["device_name"] == "my_phone"
        assert data["device_id"]
        assert data["access_token"]
        assert data["token_type"] == "bearer"

    def test_invalid_setup_secret_rejected(self, client, setup_secret):
        response = client.post(
            "/auth/register",
            json={"device_name": "bad_device", "setup_secret": "wrong-secret"},
        )
        assert response.status_code == 403
        assert response.json()["error"]["code"] == "INVALID_SETUP_SECRET"

    def test_setup_secret_invalidated_after_use(self, client, setup_secret):
        client.post(
            "/auth/register",
            json={"device_name": "first", "setup_secret": setup_secret},
        )
        response = client.post(
            "/auth/register",
            json={"device_name": "second", "setup_secret": setup_secret},
        )
        assert response.status_code == 403

    def test_additional_device_requires_auth(self, client, auth_headers):
        response = client.post(
            "/auth/register/device",
            json={"device_name": "my_tablet"},
            headers=auth_headers,
        )
        assert response.status_code == 201
        assert response.json()["device_name"] == "my_tablet"

    def test_additional_device_without_auth_fails(self, client, registered_device):
        response = client.post(
            "/auth/register/device",
            json={"device_name": "unauthorized_device"},
        )
        assert response.status_code == 422

    def test_device_limit_enforced(self, client, auth_headers, vault_settings):
        from app.services import auth as auth_svc
        original = auth_svc.settings.max_devices
        try:
            auth_svc.settings = vault_settings.__class__(
                vault_path=vault_settings.vault_path,
                jwt_secret=vault_settings.jwt_secret,
                jwt_expiry_hours=vault_settings.jwt_expiry_hours,
                max_devices=1,
            )
            response = client.post(
                "/auth/register/device",
                json={"device_name": "over_limit"},
                headers=auth_headers,
            )
            assert response.status_code == 403
            assert response.json()["error"]["code"] == "DEVICE_LIMIT"
        finally:
            auth_svc.settings = vault_settings

    def test_devices_persisted_to_json(self, client, setup_secret, tmp_vault):
        client.post(
            "/auth/register",
            json={"device_name": "persisted", "setup_secret": setup_secret},
        )
        devices_file = tmp_vault / "system" / "devices.json"
        assert devices_file.exists()
        data = json.loads(devices_file.read_text(encoding="utf-8"))
        assert len(data) == 1
        device = list(data.values())[0]
        assert device["device_name"] == "persisted"


class TestTokenCreation:
    def test_token_contains_expected_claims(self, vault_settings):
        from app.services import auth
        token, expires = auth.create_token("dev123", "my_laptop")
        payload = jwt.decode(token, vault_settings.jwt_secret, algorithms=["HS256"])
        assert payload["sub"] == "dev123"
        assert payload["device_name"] == "my_laptop"
        assert "iat" in payload
        assert "exp" in payload
        assert "jti" in payload

    def test_token_expiry_matches_config(self, vault_settings):
        from app.services import auth
        token, expires = auth.create_token("dev123", "test")
        payload = jwt.decode(token, vault_settings.jwt_secret, algorithms=["HS256"])
        iat = datetime.fromtimestamp(payload["iat"], tz=timezone.utc)
        exp = datetime.fromtimestamp(payload["exp"], tz=timezone.utc)
        delta = exp - iat
        assert abs(delta.total_seconds() - vault_settings.jwt_expiry_hours * 3600) < 2


class TestTokenValidation:
    def test_valid_token_decodes(self, vault_settings, registered_device):
        from app.services import auth
        token = registered_device["access_token"]
        payload = auth.decode_token(token)
        assert payload["sub"] == registered_device["device_id"]
        assert payload["device_name"] == "test_laptop"

    def test_expired_token_rejected(self, vault_settings):
        from app.services import auth
        from app.errors import InvalidTokenError

        auth._add_device("expired_dev")

        payload = {
            "sub": "expired_dev",
            "device_name": "test",
            "iat": datetime.now(tz=timezone.utc) - timedelta(hours=48),
            "exp": datetime.now(tz=timezone.utc) - timedelta(hours=1),
            "jti": "expired-jti",
        }
        token = jwt.encode(payload, vault_settings.jwt_secret, algorithm="HS256")

        with pytest.raises(InvalidTokenError, match="expired"):
            auth.decode_token(token)

    def test_invalid_signature_rejected(self, vault_settings, registered_device):
        from app.services import auth
        from app.errors import InvalidTokenError

        payload = {
            "sub": registered_device["device_id"],
            "device_name": "test",
            "iat": datetime.now(tz=timezone.utc),
            "exp": datetime.now(tz=timezone.utc) + timedelta(hours=1),
            "jti": "bad-sig-jti",
        }
        token = jwt.encode(payload, "wrong-secret", algorithm="HS256")

        with pytest.raises(InvalidTokenError, match="Invalid token"):
            auth.decode_token(token)

    def test_unregistered_device_token_rejected(self, vault_settings):
        from app.services import auth
        from app.errors import InvalidTokenError

        payload = {
            "sub": "nonexistent_device",
            "device_name": "ghost",
            "iat": datetime.now(tz=timezone.utc),
            "exp": datetime.now(tz=timezone.utc) + timedelta(hours=1),
            "jti": "ghost-jti",
        }
        token = jwt.encode(payload, vault_settings.jwt_secret, algorithm="HS256")

        with pytest.raises(InvalidTokenError, match="no longer registered"):
            auth.decode_token(token)


class TestTokenRevocation:
    def test_revoked_token_rejected(self, vault_settings, registered_device):
        from app.services import auth
        from app.errors import TokenRevokedError

        token = registered_device["access_token"]
        payload = jwt.decode(token, vault_settings.jwt_secret, algorithms=["HS256"])
        auth._revoke_token(payload["jti"])

        with pytest.raises(TokenRevokedError):
            auth.decode_token(token)

    def test_revoked_tokens_persisted(self, vault_settings, registered_device, tmp_vault):
        from app.services import auth

        token = registered_device["access_token"]
        payload = jwt.decode(token, vault_settings.jwt_secret, algorithms=["HS256"])
        auth._revoke_token(payload["jti"])

        revoked_file = tmp_vault / "system" / "revoked_tokens.json"
        assert revoked_file.exists()
        revoked = json.loads(revoked_file.read_text(encoding="utf-8"))
        assert payload["jti"] in revoked


class TestRefreshEndpoint:
    def test_refresh_returns_new_token(self, client, auth_headers, registered_device):
        response = client.post("/auth/refresh", headers=auth_headers)
        assert response.status_code == 200
        data = response.json()
        assert data["access_token"] != registered_device["access_token"]
        assert data["device_id"] == registered_device["device_id"]

    def test_old_token_revoked_after_refresh(self, client, auth_headers, vault_settings):
        from app.services import auth
        from app.errors import TokenRevokedError

        old_token = auth_headers["Authorization"].split(" ")[1]
        client.post("/auth/refresh", headers=auth_headers)

        with pytest.raises(TokenRevokedError):
            auth.decode_token(old_token)


class TestRevokeEndpoint:
    def test_revoke_device(self, client, auth_headers, registered_device):
        second = client.post(
            "/auth/register/device",
            json={"device_name": "to_revoke"},
            headers=auth_headers,
        )
        second_id = second.json()["device_id"]

        response = client.post(
            "/auth/revoke",
            json={"device_id": second_id},
            headers=auth_headers,
        )
        assert response.status_code == 200

        from app.services import auth
        assert auth.get_device(second_id) is None

    def test_revoke_nonexistent_device_fails(self, client, auth_headers):
        response = client.post(
            "/auth/revoke",
            json={"device_id": "nonexistent"},
            headers=auth_headers,
        )
        assert response.status_code == 404


class TestMeEndpoint:
    def test_me_returns_device_info(self, client, auth_headers, registered_device):
        response = client.get("/auth/me", headers=auth_headers)
        assert response.status_code == 200
        data = response.json()
        assert data["device_id"] == registered_device["device_id"]
        assert data["device_name"] == "test_laptop"


class TestRouteProtection:
    def test_files_without_auth_returns_error(self, client):
        response = client.get("/files")
        assert response.status_code == 422

    def test_files_with_invalid_token_returns_401(self, client):
        response = client.get("/files", headers={"Authorization": "Bearer bad-token"})
        assert response.status_code == 401

    def test_files_with_valid_token_succeeds(self, client, auth_headers):
        response = client.get("/files", headers=auth_headers)
        assert response.status_code == 200

    def test_upload_without_auth_returns_error(self, client):
        response = client.post("/upload/test.txt")
        assert response.status_code == 422

    def test_download_without_auth_returns_error(self, client):
        response = client.get("/download/readme.md")
        assert response.status_code == 422

    def test_health_without_auth_succeeds(self, client):
        response = client.get("/health")
        assert response.status_code == 200

    def test_read_file_with_auth(self, client, auth_headers):
        response = client.get("/files/readme.md", headers=auth_headers)
        assert response.status_code == 200
        assert response.json()["content"] == "# JARVIS Vault"

    def test_create_file_with_auth(self, client, auth_headers):
        response = client.post(
            "/files/new.md",
            json={"content": "test"},
            headers=auth_headers,
        )
        assert response.status_code == 201

    def test_wrong_auth_scheme_rejected(self, client, registered_device):
        response = client.get(
            "/files",
            headers={"Authorization": f"Basic {registered_device['access_token']}"},
        )
        assert response.status_code == 401
