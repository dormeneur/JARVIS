from unittest.mock import patch

import httpx
import pytest

from app.config import settings
from app.routers import jobs


@pytest.fixture
def guest_headers(client, auth_headers):
    resp = client.post("/auth/register/device", json={"device_name": "guest"}, headers=auth_headers)
    return {"Authorization": f"Bearer {resp.json()['access_token']}"}


@pytest.fixture
def upstream():
    seen = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return httpx.Response(201, json={"id": "r1"}, headers={"content-type": "application/json"})

    with patch.object(settings, "jobs_secret", "s3cret"), patch.object(
        jobs, "_client", lambda: httpx.AsyncClient(transport=httpx.MockTransport(handler))
    ):
        yield seen


def test_admin_request_is_forwarded_with_secret(client, auth_headers, upstream):
    resp = client.post("/jobs/runs?x=1", json={"kind": "rank"}, headers=auth_headers)
    assert resp.status_code == 201 and resp.json() == {"id": "r1"}
    req = upstream[0]
    assert req.url.path == "/runs" and req.url.params["x"] == "1"
    assert req.headers["X-Jobs-Secret"] == "s3cret"
    assert b'"rank"' in req.content


def test_guest_device_cannot_reach_job_engine(client, guest_headers, upstream):
    assert client.get("/jobs/board", headers=guest_headers).status_code == 403
    assert upstream == []


def test_unauthenticated_rejected(client, upstream):
    assert client.get("/jobs/board").status_code in (401, 422)


def test_engine_down_is_503(client, auth_headers):
    def down(request):
        raise httpx.ConnectError("refused")

    with patch.object(jobs, "_client", lambda: httpx.AsyncClient(transport=httpx.MockTransport(down))):
        resp = client.get("/jobs/status", headers=auth_headers)
    assert resp.status_code == 503
