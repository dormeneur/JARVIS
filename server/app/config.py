from pathlib import Path

from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    vault_path: Path = Path("/data/JARVIS")
    max_upload_mb: int = 100
    log_level: str = "INFO"
    jwt_secret: str = ""
    jwt_expiry_hours: int = 720
    max_devices: int = 5
    sync_timestamp_tolerance_seconds: int = 2
    brain_url: str = "http://jv-brain:8001"
    rate_limit_enabled: bool = True
    backup_path: Path = Path("/backups")
    backup_keep: int = 10
    backup_interval_hours: int = 24  # 0 disables automatic backups

    model_config = {"env_prefix": "JARVIS_"}

    @property
    def max_upload_bytes(self) -> int:
        return self.max_upload_mb * 1024 * 1024


settings = Settings()
