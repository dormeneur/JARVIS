from __future__ import annotations

from datetime import datetime

from pydantic import BaseModel


class RegisterRequest(BaseModel):
    device_name: str
    setup_secret: str


class RegisterByDeviceRequest(BaseModel):
    device_name: str


class TokenResponse(BaseModel):
    access_token: str
    token_type: str = "bearer"
    expires_at: datetime
    device_id: str
    device_name: str


class RevokeRequest(BaseModel):
    device_id: str


class DeviceInfo(BaseModel):
    device_id: str
    device_name: str
    registered_at: datetime


class DeviceListResponse(BaseModel):
    devices: list[DeviceInfo]
    max_devices: int
