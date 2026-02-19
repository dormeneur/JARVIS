from __future__ import annotations

from fastapi import Header

from app.errors import InvalidTokenError
from app.services import auth


def get_current_device(authorization: str = Header(...)) -> dict:
    if not authorization.startswith("Bearer "):
        raise InvalidTokenError("Authorization header must be: Bearer <token>")

    token = authorization[7:]
    payload = auth.decode_token(token)

    return {
        "device_id": payload["sub"],
        "device_name": payload["device_name"],
        "jti": payload["jti"],
    }
