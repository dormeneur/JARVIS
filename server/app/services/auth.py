from __future__ import annotations

import json
import logging
import secrets
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

import jwt

from app.config import settings
from app.errors import (
    DeviceLimitError,
    DeviceNotFoundError,
    InvalidSetupSecretError,
    InvalidTokenError,
    TokenRevokedError,
)

logger = logging.getLogger(__name__)

ALGORITHM = "HS256"


def _system_dir() -> Path:
    system_path = settings.vault_path / "system"
    system_path.mkdir(exist_ok=True)
    return system_path


def _devices_path() -> Path:
    return _system_dir() / "devices.json"


def _revoked_path() -> Path:
    return _system_dir() / "revoked_tokens.json"


def _setup_secret_path() -> Path:
    return _system_dir() / "setup_secret.txt"


# --- Device Registry ---


def _load_devices() -> dict:
    path = _devices_path()
    if not path.exists():
        return {}
    return json.loads(path.read_text(encoding="utf-8"))


def _save_devices(devices: dict) -> None:
    _devices_path().write_text(json.dumps(devices, indent=2), encoding="utf-8")


def get_device_count() -> int:
    return len(_load_devices())


def get_all_devices() -> list[dict]:
    devices = _load_devices()
    return [
        {"device_id": did, "device_name": d["device_name"], "registered_at": d["registered_at"]}
        for did, d in devices.items()
    ]


def get_device(device_id: str) -> dict | None:
    devices = _load_devices()
    info = devices.get(device_id)
    if info is None:
        return None
    return {"device_id": device_id, **info}


def _add_device(device_name: str) -> str:
    devices = _load_devices()
    if len(devices) >= settings.max_devices:
        raise DeviceLimitError(settings.max_devices)

    device_id = uuid.uuid4().hex[:12]
    devices[device_id] = {
        "device_name": device_name,
        "registered_at": datetime.now(tz=timezone.utc).isoformat(),
    }
    _save_devices(devices)
    return device_id


def remove_device(device_id: str) -> None:
    devices = _load_devices()
    if device_id not in devices:
        raise DeviceNotFoundError(device_id)
    del devices[device_id]
    _save_devices(devices)


# --- Revoked Tokens ---


def _load_revoked() -> list[str]:
    path = _revoked_path()
    if not path.exists():
        return []
    return json.loads(path.read_text(encoding="utf-8"))


def _save_revoked(revoked: list[str]) -> None:
    _revoked_path().write_text(json.dumps(revoked), encoding="utf-8")


def _is_revoked(jti: str) -> bool:
    return jti in _load_revoked()


def _revoke_token(jti: str) -> None:
    revoked = _load_revoked()
    if jti not in revoked:
        revoked.append(jti)
        _save_revoked(revoked)


def revoke_all_for_device(device_id: str) -> None:
    devices = _load_devices()
    if device_id not in devices:
        raise DeviceNotFoundError(device_id)
    remove_device(device_id)


# --- Setup Secret ---


def get_or_create_setup_secret() -> str | None:
    if get_device_count() > 0:
        path = _setup_secret_path()
        if path.exists():
            path.unlink()
        return None

    path = _setup_secret_path()
    if path.exists():
        return path.read_text(encoding="utf-8").strip()

    secret = secrets.token_urlsafe(32)
    path.write_text(secret, encoding="utf-8")
    return secret


def _validate_setup_secret(provided: str) -> None:
    path = _setup_secret_path()
    if not path.exists():
        raise InvalidSetupSecretError()

    stored = path.read_text(encoding="utf-8").strip()
    if not secrets.compare_digest(provided, stored):
        raise InvalidSetupSecretError()


def _consume_setup_secret() -> None:
    path = _setup_secret_path()
    if path.exists():
        path.unlink()


# --- JWT Tokens ---


def create_token(device_id: str, device_name: str) -> tuple[str, datetime]:
    now = datetime.now(tz=timezone.utc)
    expires = now + timedelta(hours=settings.jwt_expiry_hours)
    jti = uuid.uuid4().hex

    payload = {
        "sub": device_id,
        "device_name": device_name,
        "iat": now,
        "exp": expires,
        "jti": jti,
    }

    token = jwt.encode(payload, settings.jwt_secret, algorithm=ALGORITHM)
    return token, expires


def decode_token(token: str) -> dict:
    try:
        payload = jwt.decode(token, settings.jwt_secret, algorithms=[ALGORITHM])
    except jwt.ExpiredSignatureError:
        raise InvalidTokenError("Token has expired")
    except jwt.InvalidTokenError:
        raise InvalidTokenError("Invalid token")

    jti = payload.get("jti", "")
    if _is_revoked(jti):
        raise TokenRevokedError()

    device_id = payload.get("sub", "")
    devices = _load_devices()
    if device_id not in devices:
        raise InvalidTokenError("Device is no longer registered")

    return payload


# --- Registration Flows ---


def register_first_device(device_name: str, setup_secret: str) -> tuple[str, str, datetime]:
    _validate_setup_secret(setup_secret)

    device_id = _add_device(device_name)
    _consume_setup_secret()

    token, expires = create_token(device_id, device_name)
    return device_id, token, expires


def register_additional_device(device_name: str) -> tuple[str, str, datetime]:
    device_id = _add_device(device_name)
    token, expires = create_token(device_id, device_name)
    return device_id, token, expires


def refresh_token(device_id: str, device_name: str, old_jti: str) -> tuple[str, datetime]:
    _revoke_token(old_jti)
    return create_token(device_id, device_name)


def revoke_device(device_id: str) -> None:
    revoke_all_for_device(device_id)
