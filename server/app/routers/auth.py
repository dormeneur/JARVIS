from __future__ import annotations

from fastapi import APIRouter, Depends

from app.config import settings
from app.dependencies import get_current_device
from app.models.auth_models import (
    DeviceInfo,
    DeviceListResponse,
    ReconnectRequest,
    RegistrationResponse,
    RegisterByDeviceRequest,
    RegisterRequest,
    RevokeRequest,
    TokenResponse,
)
from app.services import auth

router = APIRouter(prefix="/auth", tags=["auth"])


@router.post("/register", response_model=RegistrationResponse, status_code=201)
async def register_device(body: RegisterRequest) -> RegistrationResponse:
    device_id, device_secret, token, expires = auth.register_first_device(
        device_name=body.device_name,
        setup_secret=body.setup_secret,
    )
    return RegistrationResponse(
        access_token=token,
        expires_at=expires,
        device_id=device_id,
        device_name=body.device_name,
        device_secret=device_secret,
    )


@router.post("/register/device", response_model=RegistrationResponse, status_code=201)
async def register_additional_device(
    body: RegisterByDeviceRequest,
    current: dict = Depends(get_current_device),
) -> RegistrationResponse:
    device_id, device_secret, token, expires = auth.register_additional_device(
        device_name=body.device_name,
    )
    return RegistrationResponse(
        access_token=token,
        expires_at=expires,
        device_id=device_id,
        device_name=body.device_name,
        device_secret=device_secret,
    )


@router.post("/reconnect", response_model=TokenResponse, status_code=200)
async def reconnect_device(body: ReconnectRequest) -> TokenResponse:
    """Reconnect to an existing registered device without authentication.
    
    Requires the device_secret that was provided during registration.
    Use this when your app data/secure storage was cleared but the device
    is still registered with the server (e.g., after flutter run or app reinstall).
    
    Returns a new JWT token for the device.
    """
    device_id, token, expires = auth.reconnect_device(
        device_name=body.device_name,
        device_secret=body.device_secret,
    )
    return TokenResponse(
        access_token=token,
        expires_at=expires,
        device_id=device_id,
        device_name=body.device_name,
    )


@router.post("/refresh", response_model=TokenResponse)
async def refresh_token(current: dict = Depends(get_current_device)) -> TokenResponse:
    token, expires = auth.refresh_token(
        device_id=current["device_id"],
        device_name=current["device_name"],
        old_jti=current["jti"],
    )
    return TokenResponse(
        access_token=token,
        expires_at=expires,
        device_id=current["device_id"],
        device_name=current["device_name"],
    )


@router.post("/revoke")
async def revoke_device(
    body: RevokeRequest,
    current: dict = Depends(get_current_device),
) -> dict:
    auth.revoke_device(body.device_id)
    return {"message": f"Device {body.device_id} revoked"}


@router.get("/me", response_model=DeviceInfo)
async def get_current_device_info(
    current: dict = Depends(get_current_device),
) -> DeviceInfo:
    device = auth.get_device(current["device_id"])
    return DeviceInfo(
        device_id=device["device_id"],
        device_name=device["device_name"],
        registered_at=device["registered_at"],
    )


@router.get("/devices", response_model=DeviceListResponse)
async def list_devices(
    current: dict = Depends(get_current_device),
) -> DeviceListResponse:
    devices = auth.get_all_devices()
    return DeviceListResponse(
        devices=[DeviceInfo(**d) for d in devices],
        max_devices=settings.max_devices,
    )
