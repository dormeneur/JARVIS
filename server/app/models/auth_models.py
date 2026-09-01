from __future__ import annotations

from datetime import datetime

from pydantic import BaseModel


class RegisterRequest(BaseModel):
    device_name: str
    setup_secret: str


class RegisterByDeviceRequest(BaseModel):
    device_name: str


class ReconnectRequest(BaseModel):
    device_name: str
    device_secret: str


class TokenResponse(BaseModel):
    access_token: str
    token_type: str = "bearer"
    expires_at: datetime
    device_id: str
    device_name: str
    is_secrets_authorized: bool = False


class RegistrationResponse(BaseModel):
    """Response from device registration endpoints. Returns the device secret (shown only once)."""
    access_token: str
    token_type: str = "bearer"
    expires_at: datetime
    device_id: str
    device_name: str
    device_secret: str
    is_secrets_authorized: bool = False


class RevokeRequest(BaseModel):
    device_id: str


class DeviceInfo(BaseModel):
    device_id: str
    device_name: str
    registered_at: datetime
    is_secrets_authorized: bool = False


class DeviceListResponse(BaseModel):
    devices: list[DeviceInfo]
    max_devices: int


# --- QR Invite ---

class InviteTokenResponse(BaseModel):
    """Returned to the admin device so it can render the QR code."""
    invite_token: str
    expires_at: datetime
    ttl_seconds: int


class InviteRegisterRequest(BaseModel):
    """Sent by the guest device after scanning the QR."""
    device_name: str
    invite_token: str
    server_url: str  # echo'd from the QR payload; used by mobile to pre-fill URL
