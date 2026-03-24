from pathlib import Path

from pydantic_settings import BaseSettings


class BrainSettings(BaseSettings):
    vault_path: Path = Path("/data/JARVIS")
    ollama_url: str = "http://ollama:11434"
    vectordb_url: str = "http://chromadb:8000"
    embedding_model: str = "nomic-embed-text"
    llm_model: str = "llama3"
    log_level: str = "INFO"

    model_config = {"env_prefix": "JARVIS_"}


settings = BrainSettings()
