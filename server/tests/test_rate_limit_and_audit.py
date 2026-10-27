import json


def _audit_lines(tmp_vault):
    path = tmp_vault / ".system" / "audit.log"
    return [json.loads(l) for l in path.read_text(encoding="utf-8").splitlines()] if path.exists() else []


def test_credential_endpoint_limited_even_with_fake_auth_header(client):
    body = {"device_name": "nobody", "device_secret": "guess"}
    codes = [
        client.post("/auth/reconnect", json=body, headers={"Authorization": f"Bearer fake{i}"}).status_code
        for i in range(12)
    ]
    assert codes[:10] == [401] * 10
    assert codes[10:] == [429, 429]


def test_429_carries_retry_after(client):
    for _ in range(10):
        client.post("/auth/reconnect", json={"device_name": "x", "device_secret": "y"})
    resp = client.post("/auth/reconnect", json={"device_name": "x", "device_secret": "y"})
    assert resp.status_code == 429
    assert 0 < int(resp.headers["Retry-After"]) <= 60
    assert resp.json()["error"]["code"] == "RATE_LIMITED"


def test_mutation_is_audited_with_device_identity(client, auth_headers, registered_device, tmp_vault):
    client.post("/files/Notes/new.md", json={"content": "hi"}, headers=auth_headers)
    entry = next(e for e in _audit_lines(tmp_vault) if e["path"] == "/files/Notes/new.md")
    assert entry["event"] == "request"
    assert entry["method"] == "POST"
    assert entry["status"] == 201
    assert entry["device_id"] == registered_device["device_id"]


def test_failed_auth_and_rate_limit_are_audited(client, tmp_vault):
    client.get("/files", headers={"Authorization": "Bearer nope"})
    for _ in range(11):
        client.post("/auth/reconnect", json={"device_name": "x", "device_secret": "y"})
    events = {e["event"] for e in _audit_lines(tmp_vault)}
    assert {"auth.denied", "rate_limit.exceeded"} <= events


def test_plain_reads_are_not_audited(client, auth_headers, tmp_vault):
    client.get("/files/readme.md", headers=auth_headers)
    assert not any(e["path"] == "/files/readme.md" for e in _audit_lines(tmp_vault))


def test_audit_log_is_not_reachable_via_api(client, auth_headers, tmp_vault):
    client.post("/files/x.md", json={"content": ""}, headers=auth_headers)
    assert client.get("/files/.system/audit.log", headers=auth_headers).status_code == 400


def test_reset_preserves_audit_log(client, auth_headers, tmp_vault):
    client.post("/files/x.md", json={"content": ""}, headers=auth_headers)
    client.post("/files/reset", headers=auth_headers)
    assert (tmp_vault / ".system" / "audit.log").exists()
