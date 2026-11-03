from pathlib import Path

from pydantic_settings import BaseSettings


class Settings(BaseSettings):
    # The ai-job-search workspace (bind mount). The engine is run, never edited.
    workspace: Path = Path("/work")
    # JARVIS-owned state: run records, event logs, snapshots. Kept outside the
    # workspace so nothing is written into the engine repo.
    state_dir: Path = Path("/state")
    claude_bin: str = "claude"
    # Shared with jv-api; jv-jobs is internal-only but still checks it.
    jobs_secret: str = ""
    max_parallel: int = 2
    run_timeout_minutes: int = 45
    # Claude Code's bubblewrap sandbox. Off by default: it needs user
    # namespaces, which Docker's default seccomp profile may block.
    sandbox: bool = False

    model_config = {"env_prefix": "JARVIS_"}


settings = Settings()
