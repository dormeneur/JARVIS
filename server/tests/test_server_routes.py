"""GET /server/routes: where else this server answers, for signed-in devices only."""



def test_routes_need_a_signed_in_device(client):
    assert client.get("/server/routes").status_code in (401, 403, 422)


def test_routes_list_the_configured_addresses(client, auth_headers, vault_settings, monkeypatch):
    monkeypatch.setattr(vault_settings, "routes", " http://100.66.98.47:8000/, http://msi.example.ts.net:8000 ,ftp://x, http://100.66.98.47:8000")
    response = client.get("/server/routes", headers=auth_headers)
    assert response.status_code == 200
    assert response.json() == {"routes": ["http://100.66.98.47:8000", "http://msi.example.ts.net:8000"]}


def test_health_stays_open_and_says_nothing_else(client):
    assert client.get("/health").json() == {"status": "ok"}
