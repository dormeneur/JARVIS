import io

import pytest
from fastapi.testclient import TestClient


class TestHealth:
    def test_health_endpoint(self, client: TestClient):
        response = client.get("/health")
        assert response.status_code == 200
        assert response.json() == {"status": "ok"}


class TestListFiles:
    def test_list_root(self, client: TestClient, auth_headers: dict):
        response = client.get("/files", headers=auth_headers)
        assert response.status_code == 200
        data = response.json()
        assert data["path"] == "/"
        names = [e["name"] for e in data["entries"]]
        assert "Personal" in names

    def test_list_subdirectory(self, client: TestClient, auth_headers: dict):
        response = client.get("/files/Personal", headers=auth_headers)
        assert response.status_code == 200
        data = response.json()
        assert data["path"] == "Personal"
        assert len(data["entries"]) == 1

    def test_list_nonexistent_returns_404(self, client: TestClient, auth_headers: dict):
        response = client.get("/files/Nonexistent", headers=auth_headers)
        assert response.status_code == 404


class TestReadFile:
    def test_read_file(self, client: TestClient, auth_headers: dict):
        response = client.get("/files/readme.md", headers=auth_headers)
        assert response.status_code == 200
        data = response.json()
        assert data["name"] == "readme.md"
        assert data["content"] == "# JARVIS Vault"
        assert "content_hash" in data

    def test_read_nested_file(self, client: TestClient, auth_headers: dict):
        response = client.get("/files/Personal/notes.md", headers=auth_headers)
        assert response.status_code == 200
        assert "My Notes" in response.json()["content"]

    def test_path_traversal_returns_400(self, client: TestClient, auth_headers: dict):
        response = client.get(
            "/files/Personal/%2e%2e/%2e%2e/etc/passwd", headers=auth_headers
        )
        assert response.status_code == 400
        assert response.json()["error"]["code"] == "PATH_TRAVERSAL"


class TestCreateFile:
    def test_create_file(self, client: TestClient, auth_headers: dict):
        response = client.post(
            "/files/new_file.md",
            json={"content": "# New"},
            headers=auth_headers,
        )
        assert response.status_code == 201
        assert response.json()["name"] == "new_file.md"

        read_response = client.get("/files/new_file.md", headers=auth_headers)
        assert read_response.json()["content"] == "# New"

    def test_create_directory(self, client: TestClient, auth_headers: dict):
        response = client.post(
            "/files/NewDir",
            json={"type": "directory"},
            headers=auth_headers,
        )
        assert response.status_code == 201
        assert response.json()["type"] == "directory"

    def test_create_existing_returns_409(self, client: TestClient, auth_headers: dict):
        response = client.post(
            "/files/readme.md",
            json={"content": "dup"},
            headers=auth_headers,
        )
        assert response.status_code == 409

    def test_create_in_missing_parent_auto_creates(self, client: TestClient, auth_headers: dict):
        response = client.post(
            "/files/Missing/file.md",
            json={"content": "data"},
            headers=auth_headers,
        )
        # create_file auto-creates parent directories
        assert response.status_code == 201


class TestUpdateFile:
    def test_update_file(self, client: TestClient, auth_headers: dict):
        response = client.put(
            "/files/readme.md",
            json={"content": "# Updated"},
            headers=auth_headers,
        )
        assert response.status_code == 200

        read_response = client.get("/files/readme.md", headers=auth_headers)
        assert read_response.json()["content"] == "# Updated"

    def test_update_nonexistent_returns_404(self, client: TestClient, auth_headers: dict):
        response = client.put(
            "/files/nonexistent.md",
            json={"content": "data"},
            headers=auth_headers,
        )
        assert response.status_code == 404


class TestDeleteFile:
    def test_delete_file(self, client: TestClient, auth_headers: dict):
        response = client.delete("/files/readme.md", headers=auth_headers)
        assert response.status_code == 200

        get_response = client.get("/files/readme.md", headers=auth_headers)
        assert get_response.status_code == 404

    def test_delete_nonexistent_returns_404(self, client: TestClient, auth_headers: dict):
        response = client.delete("/files/nonexistent.md", headers=auth_headers)
        assert response.status_code == 404


class TestUploadDownload:
    def test_upload_and_download(self, client: TestClient, auth_headers: dict):
        file_content = b"binary file content here"
        response = client.post(
            "/upload/uploaded.bin",
            files={"file": ("uploaded.bin", io.BytesIO(file_content), "application/octet-stream")},
            headers=auth_headers,
        )
        assert response.status_code == 201
        assert response.json()["name"] == "uploaded.bin"

        download_response = client.get("/download/uploaded.bin", headers=auth_headers)
        assert download_response.status_code == 200
        assert download_response.content == file_content

    def test_download_nonexistent_returns_404(self, client: TestClient, auth_headers: dict):
        response = client.get("/download/nonexistent.bin", headers=auth_headers)
        assert response.status_code == 404

    def test_download_directory_returns_400(self, client: TestClient, auth_headers: dict):
        response = client.get("/download/Personal", headers=auth_headers)
        assert response.status_code == 400


class TestOpenAPIDocs:
    def test_docs_available(self, client: TestClient):
        response = client.get("/docs")
        assert response.status_code == 200

    def test_openapi_json_available(self, client: TestClient):
        response = client.get("/openapi.json")
        assert response.status_code == 200
        data = response.json()
        assert data["info"]["title"] == "JARVIS API"
