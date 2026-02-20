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


class RegistrationResponse(BaseModel):
    """Response from device registration endpoints. Returns the device secret (shown only once)."""
    access_token: str
    token_type: str = "bearer"
    expires_at: datetime
    device_id: str
    device_name: str
    device_secret: str


class RevokeRequest(BaseModel):
    device_id: str


class DeviceInfo(BaseModel):
    device_id: str
    device_name: str
    registered_at: datetime


class DeviceListResponse(BaseModel):
    devices: list[DeviceInfo]
    max_devices: int
