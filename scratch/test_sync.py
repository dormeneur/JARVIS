import json
import jwt
import requests
import time
from datetime import datetime, timedelta, timezone

SECRET = "wb5ksHdkqBVDmtFfoDUzPzwil0Q-syRNUhR_VC3T7bE"
ALGORITHM = "HS256"
DEVICES_PATH = "B:/JARVIS/system/devices.json"

with open(DEVICES_PATH, "r") as f:
    devices = json.load(f)

# The authorized device
auth_device_id = "f3a8f75671d0"
auth_payload = {
    "sub": auth_device_id,
    "device_name": "moto",
    "jti": "test-jti-1",
    "iat": int(time.time()),
    "exp": int(time.time()) + 3600
}
auth_token = jwt.encode(auth_payload, SECRET, algorithm=ALGORITHM)

# Add an unauthorized device for testing
unauth_device_id = "test-unauth-123"
devices[unauth_device_id] = {
    "device_name": "test_unauth",
    "device_secret": "fake_secret",
    "registered_at": datetime.now(timezone.utc).isoformat(),
    "is_secrets_authorized": False
}
with open(DEVICES_PATH, "w") as f:
    json.dump(devices, f)

unauth_payload = {
    "sub": unauth_device_id,
    "device_name": "test_unauth",
    "jti": "test-jti-2",
    "iat": int(time.time()),
    "exp": int(time.time()) + 3600
}
unauth_token = jwt.encode(unauth_payload, SECRET, algorithm=ALGORITHM)

def test_manifest(token, name):
    print(f"\n--- Testing Manifest for {name} ---")
    res = requests.post("http://localhost:8000/sync/manifest", json={"manifest": []}, headers={"Authorization": f"Bearer {token}"})
    print(res.status_code)
    if res.status_code == 200:
        manifest = res.json()
        secrets_paths = [p["path"] for p in manifest.get("to_pull", []) if p["path"].startswith("Secrets/")]
        print(f"Found Secrets/ paths: {len(secrets_paths)} paths")
    else:
        print(res.text)

def test_push(token, name, filename):
    print(f"\n--- Testing Push for {name} ---")
    meta = f'{{"path": "Secrets/{filename}", "last_modified": "2026-04-30T00:00:00Z", "base_version": 1, "content_hash": "fakehash"}}'
    res = requests.post(
        "http://localhost:8000/sync/push",
        data={"metadata": meta},
        files={"file": (filename, b"secret data")},
        headers={"Authorization": f"Bearer {token}"}
    )
    print(res.status_code, res.text)

test_manifest(auth_token, "Authorized Device")
test_push(auth_token, "Authorized Device", "test_auth.jvs")

test_manifest(unauth_token, "Unauthorized Device")
test_push(unauth_token, "Unauthorized Device", "test_unauth.jvs")

# Cleanup the unauthorized device
del devices[unauth_device_id]
with open(DEVICES_PATH, "w") as f:
    json.dump(devices, f)
