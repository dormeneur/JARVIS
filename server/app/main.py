import asyncio
import logging
from contextlib import asynccontextmanager
from typing import AsyncIterator

from fastapi import FastAPI, Request
from fastapi.concurrency import run_in_threadpool
from fastapi.responses import JSONResponse

from app.config import settings
from app.errors import AuthError, VaultError, auth_error_handler, vault_error_handler
from app.routers import auth, backup as backup_router, files, health, sync, ask
from app.services import audit, backup, rate_limit
from app.services import auth as auth_service
from app.services.path_validator import is_secrets_path


@asynccontextmanager
async def lifespan(_app: FastAPI) -> AsyncIterator[None]:
    vault_path = settings.vault_path
    if not vault_path.exists():
        raise RuntimeError(f"Vault path does not exist: {vault_path}")
    if not vault_path.is_dir():
        raise RuntimeError(f"Vault path is not a directory: {vault_path}")
    logging.info("Vault mounted at: %s", vault_path)

    setup_secret = auth_service.get_or_create_setup_secret()
    if setup_secret:
        logging.warning(
            "\n"
            "============================================\n"
            "  JARVIS SETUP SECRET (first-time setup)\n"
            "  %s\n"
            "  Use this to register your first device.\n"
            "  This secret will be invalidated after use.\n"
            "============================================",
            setup_secret,
        )

    backup_task = asyncio.create_task(_backup_loop())
    yield
    backup_task.cancel()


async def _backup_loop() -> None:
    # Hourly check rather than a 24h sleep: a laptop that reboots daily would
    # otherwise never reach the next scheduled run.
    while True:
        try:
            if backup.is_due():
                await run_in_threadpool(backup.create_backup)
        except Exception:
            logging.exception("Scheduled vault backup failed")
        await asyncio.sleep(3600)


app = FastAPI(
    title="JARVIS API",
    description="Personal knowledge vault file management API",
    version="2.0.0",
    lifespan=lifespan,
)

app.add_exception_handler(VaultError, vault_error_handler)
app.add_exception_handler(AuthError, auth_error_handler)

# Routes whose URL tail is a vault path, so Secrets access is visible in the URL.
_PATH_ROUTES = ("/files/", "/download/", "/upload/")


def _audit_event(method: str, path: str, status: int) -> str | None:
    if status == 429:
        return "rate_limit.exceeded"
    if status in (401, 403):
        return "auth.denied"
    for prefix in _PATH_ROUTES:
        if path.startswith(prefix) and is_secrets_path(path[len(prefix):]):
            return "secrets.access"
    if method != "GET" or status >= 400:
        return "request"
    return None


@app.middleware("http")
async def security_middleware(request: Request, call_next):
    allowed, group, retry_after = rate_limit.hit(request) if settings.rate_limit_enabled else (True, "", 0)
    if allowed:
        response = await call_next(request)
    else:
        response = JSONResponse(
            status_code=429,
            content={"error": {"code": "RATE_LIMITED", "message": f"Too many {group} requests; retry in {retry_after}s"}},
            headers={"Retry-After": str(retry_after)},
        )

    event = _audit_event(request.method, request.url.path, response.status_code)
    if event:
        device = getattr(request.state, "device", None) or {}
        audit.record(
            event,
            method=request.method,
            path=request.url.path,
            status=response.status_code,
            device_id=device.get("device_id"),
            ip=request.client.host if request.client else None,
        )
    return response


app.include_router(health.router)
app.include_router(auth.router)
app.include_router(sync.router)
app.include_router(files.router)
app.include_router(ask.router)
app.include_router(backup_router.router)
