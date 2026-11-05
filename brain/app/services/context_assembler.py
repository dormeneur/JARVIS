"""Context assembler service for building LLM prompts."""

import os
import logging
from pathlib import Path
from typing import Any, List, Tuple, Optional

from app.models.ask_models import Source
from app.services.document_loader import DocumentLoader
from app.services.text_chunker import TextChunker
from app.config import settings
from app.services.ollama_client import MAX_TOKENS
from app.services.tools import ToolError, safe_path
from app.services.vault_text import DATA_RULE, wrap_vault_text

logger = logging.getLogger(__name__)

# Constants
# Budget follows the context window: window - reply - ~2k for system prompt, memory index, history
# and question. 5120 at the default 8192.
MAX_CONTEXT_TOKENS = max(2048, settings.num_ctx - MAX_TOKENS - 2048)
# Oldest chat turns are dropped first once history passes this.
HISTORY_TOKEN_BUDGET = 1500
SYSTEM_PROMPT = """You are JARVIS, a personal AI assistant. You answer questions using ONLY
the provided context from the user's personal knowledge vault. If the
context doesn't contain enough information to answer, say so clearly.
""" + DATA_RULE + "\n"

class ContextAssembler:
    """Service to assemble LLM prompts from retrieved chunks and attachments."""
    
    def __init__(
        self,
        text_chunker: TextChunker,
        document_loader: DocumentLoader
    ):
        self.chunker = text_chunker
        self.loader = document_loader

    def assemble_prompt(
        self, 
        query: str, 
        retrieved_sources: List[Source], 
        attachments: List[str],
        chat_history: Optional[List[Any]] = None,
        current_directory: str = "."
    ) -> Tuple[str, List[Source]]:
        """Construct the prompt and return the final used sources.
        
        Args:
            query: The user's question
            retrieved_sources: Chunks from the vector store (includes content)
            attachments: List of specific file paths to include
            chat_history: List of Message objects containing previous turns
            current_directory: User's current working directory
            
        Returns:
            Tuple of (formatted_prompt_string, list_of_used_sources)
        """
        chat_history = chat_history or []
        used_sources = []
        context_parts = []
        current_tokens = 0
        
        # 1. Process attachments first (highest priority)
        if attachments:
            for path in attachments:
                content = self._load_attachment(path)
                if not content:
                    logger.warning(f"Attachment not found or unreadable: {path}")
                    continue
                
                # Format the context block
                block = f"[Source: {path}]\n{wrap_vault_text(path, content)}\n"
                
                # Truncate if it exceeds remaining budget
                block_tokens = self.chunker._count_tokens(block)
                if current_tokens + block_tokens > MAX_CONTEXT_TOKENS:
                    remaining = MAX_CONTEXT_TOKENS - current_tokens
                    if remaining > 50:  # Only add if we have a reasonable amount of tokens left
                        truncated_content = self._truncate_to_tokens(content, remaining - 20)
                        block = f"[Source: {path}]\n{wrap_vault_text(path, truncated_content + '...')}\n"
                        context_parts.append(block)
                        used_sources.append(Source(path=path, chunk=0, score=1.0))
                    # Budget exhausted, stop processing any more context
                    current_tokens = MAX_CONTEXT_TOKENS
                    break
                else:
                    context_parts.append(block)
                    current_tokens += block_tokens
                    used_sources.append(Source(path=path, chunk=0, score=1.0))
        
        # 2. Process retrieved chunks (fill remaining budget)
        for source in retrieved_sources:
            if current_tokens >= MAX_CONTEXT_TOKENS:
                break
                
            content = getattr(source, 'content', '')
            if not content:
                continue
                
            # If the file is already fully included via attachments, skip the retrieved chunk
            if any(s.path == source.path and s.score == 1.0 for s in used_sources):
                continue
                
            block = f"[Source: {source.path}]\n{wrap_vault_text(source.path, content)}\n"
            block_tokens = self.chunker._count_tokens(block)
            
            if current_tokens + block_tokens > MAX_CONTEXT_TOKENS:
                remaining = MAX_CONTEXT_TOKENS - current_tokens
                if remaining > 50:
                    truncated_content = self._truncate_to_tokens(content, remaining - 20)
                    block = f"[Source: {source.path}]\n{wrap_vault_text(source.path, truncated_content + '...')}\n"
                    context_parts.append(block)
                    
                    # Store clean Source for API return (remove content payload)
                    clean_source = Source(path=source.path, chunk=source.chunk, score=source.score)
                    used_sources.append(clean_source)
                current_tokens = MAX_CONTEXT_TOKENS
                break
            else:
                context_parts.append(block)
                current_tokens += block_tokens
                clean_source = Source(path=source.path, chunk=source.chunk, score=source.score)
                used_sources.append(clean_source)
                
        # 3. Assemble final prompt
        context_text = "\n".join(context_parts)
        if not context_text:
            context_text = "No relevant context found."
            
        # Inject Memory/global_index.md if it exists
        memory_index = ""
        memory_path = Path(settings.vault_path) / "Memory" / "global_index.md"
        if memory_path.exists():
            try:
                # We do not want to blow up the token count unnecessarily, but the table of contents is critical.
                raw_memory = memory_path.read_text(encoding="utf-8")
                truncated_memory = self._truncate_to_tokens(raw_memory, 1000) # Ensure it doesn't take up the whole prompt
                memory_index = f"\n\n=== GLOBAL MEMORY INDEX ===\n(Here is a list of all files in the user's vault. You can use this to know what documents exist, or ask the user for them.)\n{truncated_memory}\n=== END GLOBAL MEMORY ===\n"
            except Exception as e:
                logger.warning(f"Failed to read global memory index: {e}")
            
        # 4. Process Chat History (last 10 turns max)
        history_parts = []
        # Take the last 10 turns (this avoids massive token blooms)
        recent_history = list(chat_history[-10:]) if chat_history else []
        while len(recent_history) > 1 and sum(
            self.chunker._count_tokens(str(getattr(m, "content", None) or (m.get("content") if isinstance(m, dict) else m)))
            for m in recent_history
        ) > HISTORY_TOKEN_BUDGET:
            recent_history.pop(0)
        for msg in recent_history:
            role_name = "User" if msg.role == "user" else "JARVIS"
            # Ensure safe access whether it's dict or pydantic BaseModel
            content = getattr(msg, 'content', None) or (msg.get('content') if isinstance(msg, dict) else str(msg))
            history_parts.append(f"{role_name}: {content}")
        
        history_text = "\n".join(history_parts)
        if history_text:
            history_text = f"\n=== CHAT HISTORY ===\n{history_text}\n=== END CHAT HISTORY ===\n"
            
        prompt = f"""{SYSTEM_PROMPT}{memory_index}

Current Directory: {current_directory}

=== CONTEXT ===
{context_text}
=== END CONTEXT ===
{history_text}
User Question: {query}

Answer:"""

        return prompt, used_sources

    def _load_attachment(self, path: str) -> Optional[str]:
        """Read a file directly for attachment context.
        
        Args:
            path: Vault-relative path (e.g. 'General/profile')
            
        Returns:
            File content as text, or None if failed
        """
        try:
            full_path = safe_path(path)
        except ToolError as e:
            logger.warning(f"Attachment rejected: {path} ({e})")
            return None
        logger.info(f"Loading attachment: {path} -> {full_path}")
        try:
            if full_path.exists():
                content = self.loader._extract_content(full_path)
                logger.info(f"Attachment loaded: {path} ({len(content) if content else 0} chars)")
                return content
            else:
                logger.warning(f"Attachment file not found: {full_path}")
                return None
        except Exception as e:
            logger.error(f"Failed to load attachment {path}: {e}")
            return None

    def _truncate_to_tokens(self, text: str, max_tokens: int) -> str:
        """Truncate text to fit within a specific token count.
        
        Args:
            text: Original text
            max_tokens: Maximum allowed tokens
            
        Returns:
            Truncated text
        """
        if max_tokens <= 0:
            return ""
            
        tokens = self.chunker.encoding.encode(text)
        if len(tokens) <= max_tokens:
            return text
            
        truncated_tokens = tokens[:max_tokens]
        return self.chunker.encoding.decode(truncated_tokens)
