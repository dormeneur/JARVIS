"""Ollama chat client service for LLM inference."""

import json
import logging
from typing import AsyncGenerator, Dict, Any, Tuple

import httpx

logger = logging.getLogger(__name__)

# Constants
LLM_MODEL = "llama3"
TEMPERATURE = 0.3
MAX_TOKENS = 1024
OLLAMA_GENERATE_API_PATH = "/api/generate"

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
