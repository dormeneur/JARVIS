"""Ollama chat client service for LLM inference."""

import json
import logging
from typing import AsyncGenerator, Dict, Any, Tuple

import httpx

from app.config import settings

logger = logging.getLogger(__name__)


def _friendly_chat_error(body: str) -> str:
    """Turn Ollama's raw error body into something the user can act on."""
    lowered = body.lower()
    if "does not support tools" in lowered:
        return (
            f"The model '{LLM_MODEL}' does not support tool calling. "
            "Set JARVIS_LLM_MODEL to a tool-capable model (e.g. qwen3:4b) and restart."
        )
    if "not found" in lowered and "model" in lowered:
        return f"Model '{LLM_MODEL}' is not pulled. Run: ollama pull {LLM_MODEL}"
    return f"AI backend error: {body[:200]}"


# Constants
# Was hardcoded to "llama3", which silently ignored JARVIS_LLM_MODEL.
LLM_MODEL = settings.llm_model
TEMPERATURE = 0.3
# Tool calls must be near-deterministic: a creative sample here means a
# malformed call or an invented path, not a more interesting answer.
TOOL_TEMPERATURE = 0.1
MAX_TOKENS = 1024
OLLAMA_GENERATE_API_PATH = "/api/generate"
OLLAMA_CHAT_API_PATH = "/api/chat"

class OllamaClient:
    """Service to interact with Ollama for text generation."""
    
    def __init__(self, ollama_url: str):
        """Initialize client with Ollama URL.
        
        Args:
            ollama_url: URL to Ollama instance (e.g., http://host.docker.internal:11434)
        """
        self.ollama_url = ollama_url.rstrip("/")
        # We need a longer timeout for LLM generation
        self.timeout = httpx.Timeout(120.0, connect=10.0)

    async def generate_streaming(self, prompt: str) -> AsyncGenerator[str, None]:
        """Generate response tokens iteratively using NDJSON streaming length.
        
        Args:
            prompt: The full assembled prompt
            
        Yields:
            Response tokens as strings
            
        Returns:
            At the end, returns the eval_count (tokens used) if needed by caller parsing, 
            but an AsyncGenerator cannot easily return a final value in standard loops.
            We will yield a special dict at the end so the caller can extract metadata.
        """
        payload = {
            "model": LLM_MODEL,
            "prompt": prompt,
            "stream": True,
            "options": {
                "temperature": TEMPERATURE,
                "num_predict": MAX_TOKENS
            }
        }
        
        try:
            async with httpx.AsyncClient(timeout=self.timeout) as client:
                async with client.stream(
                    "POST", 
                    f"{self.ollama_url}{OLLAMA_GENERATE_API_PATH}", 
                    json=payload
                ) as response:
                    response.raise_for_status()
                    
                    # Read streaming NDJSON lines
                    async for line in response.aiter_lines():
                        if not line:
                            continue
                            
                        try:
                            data = json.loads(line)
                            
                            # Yield text chunk
                            if "response" in data and data["response"]:
                                yield data["response"]
                                
                            # If done, yield a metadata dictionary
                            if data.get("done"):
                                metadata = {
                                    "__jarvis_metadata__": True,
                                    "eval_count": data.get("eval_count", 0),
                                    "total_duration": data.get("total_duration", 0)
                                }
                                yield json.dumps(metadata)
                                break
                                
                        except json.JSONDecodeError:
                            logger.error(f"Failed to parse Ollama NDJSON line: {line}")
                            continue
                            
        except Exception as e:
            logger.error(f"Failed to stream from Ollama: {e}")
            yield json.dumps({"__jarvis_error__": str(e)})

    async def chat_with_tools(
        self,
        messages: list,
        tools: list | None = None,
    ) -> AsyncGenerator[Dict[str, Any], None]:
        """Stream one assistant turn from /api/chat, with native tool calling.

        Yields dicts the caller can switch on:
          {"token": str}                     incremental prose
          {"tool_calls": [{name, arguments}]} the turn ended in tool calls
          {"done": {"eval_count": int}}      turn finished
          {"error": str}                     upstream failure

        Streaming and tools coexist: Ollama sends tool_calls in message
        chunks, so accumulate them across the stream and emit once at the end.
        """
        payload: Dict[str, Any] = {
            "model": LLM_MODEL,
            "messages": messages,
            "stream": True,
            "options": {
                "temperature": TOOL_TEMPERATURE if tools else TEMPERATURE,
                "num_predict": MAX_TOKENS,
            },
        }
        if tools:
            payload["tools"] = tools

        collected_calls: list = []

        try:
            async with httpx.AsyncClient(timeout=self.timeout) as client:
                async with client.stream(
                    "POST", f"{self.ollama_url}{OLLAMA_CHAT_API_PATH}", json=payload
                ) as response:
                    if response.status_code >= 400:
                        body = (await response.aread()).decode("utf-8", "replace")
                        logger.error("Ollama /api/chat %s: %s", response.status_code, body)
                        yield {"error": _friendly_chat_error(body)}
                        return

                    async for line in response.aiter_lines():
                        if not line:
                            continue
                        try:
                            data = json.loads(line)
                        except json.JSONDecodeError:
                            logger.error("Bad NDJSON line from Ollama: %s", line)
                            continue

                        message = data.get("message") or {}

                        for call in message.get("tool_calls") or []:
                            fn = call.get("function") or {}
                            args = fn.get("arguments")
                            # Some builds return arguments as a JSON string.
                            if isinstance(args, str):
                                try:
                                    args = json.loads(args)
                                except json.JSONDecodeError:
                                    args = {}
                            collected_calls.append(
                                {"name": fn.get("name", ""), "arguments": args or {}}
                            )

                        content = message.get("content")
                        if content:
                            yield {"token": content}

                        if data.get("done"):
                            if collected_calls:
                                yield {"tool_calls": collected_calls}
                            yield {"done": {"eval_count": data.get("eval_count", 0)}}
                            return
        except Exception as exc:  # noqa: BLE001
            logger.error("Failed to stream chat from Ollama: %s", exc)
            yield {"error": str(exc)}

    async def generate(self, prompt: str) -> Tuple[str, int]:
        """Generate a complete response (non-streaming).
        
        Args:
            prompt: Required prompt
            
        Returns:
            Tuple of (response_text, tokens_used)
        """
        payload = {
            "model": LLM_MODEL,
            "prompt": prompt,
            "stream": False,
            "options": {
                "temperature": TEMPERATURE,
                "num_predict": MAX_TOKENS
            }
        }
        
        try:
            async with httpx.AsyncClient(timeout=self.timeout) as client:
                response = await client.post(
                    f"{self.ollama_url}{OLLAMA_GENERATE_API_PATH}", 
                    json=payload
                )
                response.raise_for_status()
                data = response.json()
                
                text = data.get("response", "")
                tokens = data.get("eval_count", 0)
                
                return text, tokens
                
        except Exception as e:
            logger.error(f"Failed to generate from Ollama: {e}")
            raise
