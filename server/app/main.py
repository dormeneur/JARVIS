import logging
from contextlib import asynccontextmanager
from typing import AsyncIterator

from fastapi import FastAPI

from app.config import settings
from app.errors import AuthError, VaultError, auth_error_handler, vault_error_handler
from app.routers import auth, files, health, sync
from app.services import auth as auth_service


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

    yield


app = FastAPI(
    title="JARVIS API",
    description="Personal knowledge vault file management API",
    version="2.0.0",
    lifespan=lifespan,
)

app.add_exception_handler(VaultError, vault_error_handler)
app.add_exception_handler(AuthError, auth_error_handler)

app.include_router(health.router)
app.include_router(auth.router)
app.include_router(sync.router)
app.include_router(files.router)
