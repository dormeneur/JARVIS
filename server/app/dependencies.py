from __future__ import annotations

from fastapi import Depends, Header, HTTPException, Request

from app.errors import InvalidTokenError
from app.services import auth
from app.services.path_validator import is_secrets_path


def get_current_device(request: Request, authorization: str = Header(...)) -> dict:
    if not authorization.startswith("Bearer "):
        raise InvalidTokenError("Authorization header must be: Bearer <token>")

    token = authorization[7:]
    payload = auth.decode_token(token)
    device_id = payload["sub"]

    # Always load authorization state from the live device registry,
    # not the JWT payload. JWTs are static — they encode state at issuance
    # time. Reading from the registry ensures revocation and authorization
    # changes take effect immediately without requiring a token refresh.
    device = auth.get_device(device_id)
    if device is None:
        raise InvalidTokenError("Device not found in registry")

    current = {
        "device_id": device_id,
        "device_name": payload["device_name"],
        "jti": payload["jti"],
        "is_secrets_authorized": device.get("is_secrets_authorized", False),
    }
    request.state.device = current
    return current


def require_admin(current: dict = Depends(get_current_device)) -> dict:
    # Secrets-authorized devices are the admin tier (06-Security-Model: admin:manage).
    if not current.get("is_secrets_authorized", False):
        raise HTTPException(status_code=403, detail="Only admin devices may do this")
    return current


def ensure_path_access(path: str, device: dict) -> None:
    if is_secrets_path(path) and not device.get("is_secrets_authorized", False):
        raise HTTPException(status_code=403, detail="Unauthorized to access Secrets directory")
