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
    # Where chunk embeddings live. "chroma": the ChromaDB container. "sqlite": a file in the vault's
    # .system/brain/ (brute-force cosine; no container needed). Switching to sqlite copies the existing
    # embeddings over from Chroma once, without re-embedding.
    vector_backend: str = "chroma"
    # Your local timezone (IANA name, e.g. Asia/Kolkata): the scheduler and digest_time use it.
    timezone: str = "UTC"
    digest_time: str = "07:30"  # HH:MM local
    # Optional ntfy topic URL; gets only the words "Your daily digest is ready", never content.
    ntfy_url: str = ""
    # Propose durable facts from chat for the Inbox (never written without approval).
    memory_extraction: bool = True
    # Fuse keyword (BM25) and vector results. Off: vector search only, as before.
    hybrid_search: bool = True
    # Add the question text to trace lines (.system/brain/traces.jsonl). Off: traces hold numbers only.
    trace_text: bool = False
    # One context window for every call on the shared model. Ollama's default silently cuts long
    # prompts from the FRONT (system prompt first), and calls with different values reload the
    # model. 8192 for qwen3:4b on a 4 GB card is ~1.2 GB of KV cache; lower it if `ollama ps` shows CPU.
    num_ctx: int = 8192
    # Let the agent model "think" before tool calls. Off: thinking can eat the whole token budget.
    agent_think: bool = False
    # Chat history DB. Empty = <vault>/.system/brain/history.db, so it survives rebuilds and is backed up.
    history_db: Path | None = None

    model_config = {"env_prefix": "JARVIS_"}


settings = BrainSettings()
