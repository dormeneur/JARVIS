"""Unit tests for chat_history trimming to 10 turns.

The context_assembler must only inject the last 10 messages into the prompt,
regardless of how many are passed. This prevents token budget explosion.
"""

import pytest
from unittest.mock import MagicMock, patch

from brain.app.services.context_assembler import ContextAssembler
from brain.app.models.ask_models import Source, Message


@pytest.fixture
def assembler():
    """Build a ContextAssembler with mocked dependencies."""
    chunker = MagicMock()
    loader = MagicMock()

    # Token counting → character length (simple stub)
    chunker._count_tokens.side_effect = lambda x: len(x)
    chunker.encoding.encode.side_effect = lambda x: list(x.encode())
    chunker.encoding.decode.side_effect = lambda x: bytes(x).decode(errors="ignore")

    loader._extract_content.return_value = "attachment content"

    # Patch settings.vault_path so the memory index lookup doesn't hit disk
    with patch("brain.app.services.context_assembler.settings") as mock_settings:
        mock_settings.vault_path = "/nonexistent"
        yield ContextAssembler(chunker, loader)


def _make_history(n: int):
    """Generate n alternating user/assistant Message objects with unique content."""
    messages = []
    for i in range(n):
        role = "user" if i % 2 == 0 else "assistant"
        # Use a format that avoids substring collisions: MSG_000, MSG_001, etc.
        messages.append(Message(role=role, content=f"MSG_{i:04d}_content"))
    return messages


class TestChatHistoryTrimming:
    def test_exactly_10_messages_all_included(self, assembler):
        """When 10 messages are sent, all 10 appear in the prompt."""
        history = _make_history(10)
        prompt, _ = assembler.assemble_prompt(
            query="test",
            retrieved_sources=[],
            attachments=[],
            chat_history=history,
        )
        for msg in history:
            assert msg.content in prompt

    def test_20_messages_only_last_10_included(self, assembler):
        """When 20 messages are sent, only messages 10-19 appear."""
        history = _make_history(20)
        prompt, _ = assembler.assemble_prompt(
            query="test",
            retrieved_sources=[],
            attachments=[],
            chat_history=history,
        )
        # First 10 must NOT be in the prompt
        for msg in history[:10]:
            assert msg.content not in prompt, f"Old message '{msg.content}' should have been trimmed"

        # Last 10 MUST be in the prompt
        for msg in history[10:]:
            assert msg.content in prompt, f"Recent message '{msg.content}' is missing from prompt"

    def test_5_messages_all_included(self, assembler):
        """When fewer than 10 messages are sent, all appear."""
        history = _make_history(5)
        prompt, _ = assembler.assemble_prompt(
            query="test",
            retrieved_sources=[],
            attachments=[],
            chat_history=history,
        )
        for msg in history:
            assert msg.content in prompt

    def test_zero_messages_no_history_section(self, assembler):
        """When no chat history is sent, the CHAT HISTORY section is absent."""
        prompt, _ = assembler.assemble_prompt(
            query="test",
            retrieved_sources=[],
            attachments=[],
            chat_history=[],
        )
        assert "=== CHAT HISTORY ===" not in prompt

    def test_100_messages_only_last_10_included(self, assembler):
        """Stress test: 100 messages → only the final 10 survive."""
        history = _make_history(100)
        prompt, _ = assembler.assemble_prompt(
            query="test",
            retrieved_sources=[],
            attachments=[],
            chat_history=history,
        )
        # Last 10 present
        for msg in history[90:]:
            assert msg.content in prompt

        # A sample from the first 90 must be absent
        for msg in history[:90]:
            assert msg.content not in prompt, f"'{msg.content}' should have been trimmed"

    def test_roles_labelled_correctly(self, assembler):
        """User messages labelled 'User:', assistant messages labelled 'JARVIS:'."""
        history = [
            Message(role="user", content="hello from user"),
            Message(role="assistant", content="hello from jarvis"),
        ]
        prompt, _ = assembler.assemble_prompt(
            query="test",
            retrieved_sources=[],
            attachments=[],
            chat_history=history,
        )
        assert "User: hello from user" in prompt
        assert "JARVIS: hello from jarvis" in prompt
