from fastapi import APIRouter, Depends

from app import config
from app.dependencies import get_current_device

router = APIRouter(tags=["health"])


@router.get("/health")
async def health() -> dict:
    return {"status": "ok"}


@router.get("/server/routes")
async def routes(_device: dict = Depends(get_current_device)) -> dict:
    """Every address this server answers on (JARVIS_ROUTES: Tailscale, home Wi-Fi...), so a phone that
    connected one way (USB, say) can switch to another when that one goes. Signed-in devices only."""
    found = [r.strip().rstrip("/") for r in config.settings.routes.split(",") if r.strip().startswith(("http://", "https://"))]
    return {"routes": list(dict.fromkeys(found))}
