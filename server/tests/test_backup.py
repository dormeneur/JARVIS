import zipfile
from unittest.mock import patch

import pytest


@pytest.fixture
def backup_dir(tmp_path_factory, vault_settings):
    d = tmp_path_factory.mktemp("backups")
    with patch.object(vault_settings, "backup_path", d):
        yield d


@pytest.fixture
def guest_headers(client, auth_headers):
    resp = client.post("/auth/register/device", json={"device_name": "guest"}, headers=auth_headers)
    return {"Authorization": f"Bearer {resp.json()['access_token']}"}


def test_export_zips_vault_including_secrets_and_version_db(client, auth_headers, backup_dir, tmp_vault):
    (tmp_vault / "Secrets").mkdir()
    (tmp_vault / "Secrets" / "k.jvs").write_bytes(b"cipher")
    client.post("/files/Notes/a.md", json={"content": "hello"}, headers=auth_headers)  # creates version DB row

    resp = client.post("/backup/export", headers=auth_headers)
    assert resp.status_code == 201
    names = zipfile.ZipFile(backup_dir / resp.json()["filename"]).namelist()
    assert {"readme.md", "Notes/a.md", "Secrets/k.jvs", "system/file_versions.db"} <= set(names)


def test_sqlite_snapshot_is_a_valid_db(client, auth_headers, backup_dir, tmp_path):
    import sqlite3
    client.post("/files/n.md", json={"content": "x"}, headers=auth_headers)
    name = client.post("/backup/export", headers=auth_headers).json()["filename"]
    out = tmp_path / "v.db"
    out.write_bytes(zipfile.ZipFile(backup_dir / name).read("system/file_versions.db"))
    rows = sqlite3.connect(out).execute("SELECT path FROM file_versions").fetchall()
    assert ("n.md",) in rows


def test_list_and_download(client, auth_headers, backup_dir):
    made = client.post("/backup/export", headers=auth_headers).json()
    listed = client.get("/backup", headers=auth_headers).json()["backups"]
    assert listed[0]["backup_id"] == made["backup_id"]
    dl = client.get(f"/backup/download/{made['backup_id']}", headers=auth_headers)
    assert dl.status_code == 200 and dl.content[:2] == b"PK"


@pytest.mark.parametrize("bad", ["..%2F..%2Fetc%2Fpasswd", "jarvis-vault-x", "readme"])
def test_download_rejects_non_backup_ids(client, auth_headers, backup_dir, bad):
    assert client.get(f"/backup/download/{bad}", headers=auth_headers).status_code in (400, 404)


def test_guest_cannot_backup(client, guest_headers, backup_dir):
    assert client.post("/backup/export", headers=guest_headers).status_code == 403
    assert client.get("/backup", headers=guest_headers).status_code == 403


def test_prune_keeps_newest(vault_settings, backup_dir):
    from app.services import backup
    for i in range(4):
        (backup_dir / f"jarvis-vault-2026010{i}T000000Z.zip").write_bytes(b"")
    with patch.object(vault_settings, "backup_keep", 2):
        backup._prune()
    assert [p.stem[-16:-8] for p in backup.list_backups()] == ["20260102", "20260103"]


def test_is_due(vault_settings, backup_dir):
    from app.services import backup
    assert backup.is_due()  # none yet
    backup.create_backup()
    assert not backup.is_due()
    with patch.object(vault_settings, "backup_interval_hours", 0):
        assert not backup.is_due()
