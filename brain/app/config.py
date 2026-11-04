from pathlib import Path

from pydantic_settings import BaseSettings


class BrainSettings(BaseSettings):
    vault_path: Path = Path("/data/JARVIS")
    ollama_url: str = "http://ollama:11434"
    vectordb_url: str = "http://chromadb:8000"
    embedding_model: str = "nomic-embed-text"
    llm_model: str = "llama3"
    # Form answers are short look-ups, so a smaller model that fits the GPU answers them in seconds
    # (JARVIS_FORM_MODEL, e.g. qwen3:4b); empty means the same model as everything else.
    form_model: str = ""
    log_level: str = "INFO"

    model_config = {"env_prefix": "JARVIS_"}


settings = BrainSettings()
