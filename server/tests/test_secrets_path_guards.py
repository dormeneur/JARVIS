"""
Security tests: /Secrets path guards on every server endpoint.

These run without Docker/Ollama. They verify that the API enforces
authorization boundaries for the Secrets directory at the HTTP level.
"""
import pytest
from fastapi.testclient import TestClient


# ---------------------------------------------------------------------------
# Fixtures — second device that is NOT secrets-authorized
# ---------------------------------------------------------------------------

@pytest.fixture
def unauth_device_headers(client: TestClient, auth_headers: dict) -> dict:
    """Register a second device without secrets authorization."""
    response = client.post(
        "/auth/register/device",
        json={"device_name": "guest_phone"},
        headers=auth_headers,
    )
    assert response.status_code == 201
    token = response.json()["access_token"]
    return {"Authorization": f"Bearer {token}"}


@pytest.fixture
def secrets_file(tmp_vault):
    """Put a real file in Secrets/ so path-existence checks don't interfere."""
    secrets_dir = tmp_vault / "Secrets"
    secrets_dir.mkdir(exist_ok=True)
    (secrets_dir / "key.jvs").write_bytes(b"\x00ENCRYPTED_BLOB")
    return tmp_vault


# ---------------------------------------------------------------------------
# Unauthorized device is denied on every Secrets path
# ---------------------------------------------------------------------------

class TestSecretsAuthorizationEnforced:
    def test_list_root_hides_secrets_dir(
        self, client: TestClient, unauth_device_headers: dict, secrets_file
    ):
        resp = client.get("/files", headers=unauth_device_headers)
        assert resp.status_code == 200
        entries = resp.json().get("entries", [])
        entry_paths = [e["path"] for e in entries]
        assert "Secrets" not in entry_paths
        assert not any(p.startswith("Secrets/") for p in entry_paths)

    def test_get_secrets_path_returns_403(
        self, client: TestClient, unauth_device_headers: dict, secrets_file
    ):
        resp = client.get("/files/Secrets/key.jvs", headers=unauth_device_headers)
        assert resp.status_code == 403

    def test_create_in_secrets_returns_403(
        self, client: TestClient, unauth_device_headers: dict
    ):
        resp = client.post(
            "/files/Secrets/evil.md",
            json={"content": "pwned"},
            headers=unauth_device_headers,
        )
        assert resp.status_code == 403

    def test_update_secrets_file_returns_403(
        self, client: TestClient, unauth_device_headers: dict, secrets_file
    ):
        resp = client.put(
            "/files/Secrets/key.jvs",
            json={"content": "overwritten"},
            headers=unauth_device_headers,
        )
        assert resp.status_code == 403

    def test_delete_secrets_file_returns_403(
        self, client: TestClient, unauth_device_headers: dict, secrets_file
    ):
        resp = client.delete(
            "/files/Secrets/key.jvs",
            headers=unauth_device_headers,
        )
        assert resp.status_code == 403

    def test_download_secrets_file_returns_403(
        self, client: TestClient, unauth_device_headers: dict, secrets_file
    ):
        resp = client.get(
            "/download/Secrets/key.jvs",
            headers=unauth_device_headers,
        )
        assert resp.status_code == 403


# ---------------------------------------------------------------------------
# Authorized device (first device) CAN access Secrets
# ---------------------------------------------------------------------------

class TestSecretsAuthorizedCanAccess:
    def test_authorized_can_list_secrets_dir(
        self, client: TestClient, auth_headers: dict, secrets_file
    ):
        resp = client.get("/files/Secrets", headers=auth_headers)
        assert resp.status_code == 200

    def test_authorized_can_read_secrets_file(
        self, client: TestClient, auth_headers: dict, secrets_file
    ):
        resp = client.get("/files/Secrets/key.jvs", headers=auth_headers)
        assert resp.status_code == 200


# ---------------------------------------------------------------------------
# Path traversal cannot reach secrets via encoded or relative paths
# ---------------------------------------------------------------------------

class TestPathTraversalBlocked:
    @pytest.mark.parametrize("bad_path", [
        "../Secrets/key.jvs",
        "Personal/../../Secrets/key.jvs",
    ])
    def test_traversal_paths_are_rejected(
        self, client: TestClient, auth_headers: dict, secrets_file, bad_path: str
    ):
        # Path traversal should be rejected (400/404) regardless of auth.
        resp = client.get(f"/files/{bad_path}", headers=auth_headers)
        assert resp.status_code in (400, 404, 403)
