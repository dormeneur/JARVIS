from fastapi import Request
from fastapi.responses import JSONResponse


class VaultError(Exception):
    def __init__(self, code: str, message: str, status_code: int = 400):
        self.code = code
        self.message = message
        self.status_code = status_code
        super().__init__(message)


class PathTraversalError(VaultError):
    def __init__(self, path: str = ""):
        super().__init__(
            code="PATH_TRAVERSAL",
            message=f"Path traversal is not allowed: {path}" if path else "Path traversal is not allowed",
            status_code=400,
        )


class PathNotFoundError(VaultError):
    def __init__(self, path: str = ""):
        super().__init__(
            code="NOT_FOUND",
            message=f"Path does not exist: {path}" if path else "Path does not exist",
            status_code=404,
        )


class PathAlreadyExistsError(VaultError):
    def __init__(self, path: str = ""):
        super().__init__(
            code="ALREADY_EXISTS",
            message=f"Path already exists: {path}" if path else "Path already exists",
            status_code=409,
        )


class InvalidPathError(VaultError):
    def __init__(self, message: str = "Invalid path"):
        super().__init__(code="INVALID_PATH", message=message, status_code=400)


class FileTooLargeError(VaultError):
    def __init__(self, max_mb: int):
        super().__init__(
            code="FILE_TOO_LARGE",
            message=f"File exceeds maximum upload size of {max_mb} MB",
            status_code=413,
        )


class VaultIOError(VaultError):
    def __init__(self, message: str = "File system operation failed"):
        super().__init__(code="IO_ERROR", message=message, status_code=500)


class AuthError(Exception):
    def __init__(self, code: str, message: str, status_code: int = 401):
        self.code = code
        self.message = message
        self.status_code = status_code
        super().__init__(message)


class InvalidTokenError(AuthError):
    def __init__(self, message: str = "Invalid or expired token"):
        super().__init__(code="INVALID_TOKEN", message=message, status_code=401)


class TokenRevokedError(AuthError):
    def __init__(self):
        super().__init__(code="TOKEN_REVOKED", message="Token has been revoked", status_code=401)


class InvalidSetupSecretError(AuthError):
    def __init__(self):
        super().__init__(code="INVALID_SETUP_SECRET", message="Invalid setup secret", status_code=403)


class DeviceLimitError(AuthError):
    def __init__(self, max_devices: int):
        super().__init__(
            code="DEVICE_LIMIT",
            message=f"Maximum number of registered devices ({max_devices}) reached",
            status_code=403,
        )


class DeviceNotFoundError(AuthError):
    def __init__(self, device_id: str):
        super().__init__(
            code="DEVICE_NOT_FOUND",
            message=f"Device not found: {device_id}",
            status_code=404,
        )


async def vault_error_handler(_request: Request, exc: VaultError) -> JSONResponse:
    return JSONResponse(
        status_code=exc.status_code,
        content={"error": {"code": exc.code, "message": exc.message}},
    )


async def auth_error_handler(_request: Request, exc: AuthError) -> JSONResponse:
    return JSONResponse(
        status_code=exc.status_code,
        content={"error": {"code": exc.code, "message": exc.message}},
    )
