from __future__ import annotations

from fastapi import Header

from app.errors import InvalidTokenError
from app.services import auth


def get_current_device(authorization: str = Header(...)) -> dict:
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

    return {
        "device_id": device_id,
        "device_name": payload["device_name"],
        "jti": payload["jti"],
        "is_secrets_authorized": device.get("is_secrets_authorized", False),
    }
