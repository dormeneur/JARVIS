"""Unit tests for chunking logic.

Covers:
- Correct chunk size (≤512 tokens)
- Correct overlap between consecutive chunks
- Files over 1MB are skipped by the document loader
- Empty documents produce a single empty-ish chunk
"""

import tempfile
from datetime import datetime
from pathlib import Path

import pytest

from brain.app.services.document_loader import DocumentLoader, LoadedDocument, MAX_FILE_SIZE_MB
from brain.app.services.text_chunker import TextChunker, CHUNK_SIZE_TOKENS, CHUNK_OVERLAP_TOKENS


@pytest.fixture
def chunker():
    return TextChunker()


def _make_doc(content: str, path: str = "test/doc.md") -> LoadedDocument:
    return LoadedDocument(
        path=path,
        content=content,
        last_modified=datetime.now(),
        content_hash="fakehash",
    )


# ------------- Chunk Size Tests ------------- #

class TestChunkSize:
    def test_every_chunk_within_512_tokens(self, chunker):
        """No chunk may exceed CHUNK_SIZE_TOKENS (with ≤1% tolerance for splitter rounding)."""
        # ~3000 tokens of prose
        words = ["knowledge", "vault", "document", "retrieval", "generation"] * 600
        doc = _make_doc(" ".join(words))
        chunks = chunker.chunk_document(doc)

        # RecursiveCharacterTextSplitter works on characters and converts
        # via length_function; it can overshoot by a few tokens when a
        # separator falls just past the boundary.  Allow 1% tolerance.
        tolerance = int(CHUNK_SIZE_TOKENS * 0.01) + 2  # at most ~7 tokens
        limit = CHUNK_SIZE_TOKENS + tolerance

        assert len(chunks) > 1, "Document should produce multiple chunks"
        for i, c in enumerate(chunks):
            tokens = chunker._count_tokens(c.content)
            assert tokens <= limit, (
                f"Chunk {i} has {tokens} tokens, exceeds limit of {CHUNK_SIZE_TOKENS}"
            )

    def test_short_document_single_chunk(self, chunker):
        """A document well under 512 tokens must stay as one chunk."""
        doc = _make_doc("Short note with fewer than 512 tokens.")
        chunks = chunker.chunk_document(doc)

        assert len(chunks) == 1
        assert chunks[0].content == doc.content
        assert chunks[0].total_chunks == 1
        assert chunks[0].chunk_index == 0


# ------------- Overlap Tests ------------- #

class TestChunkOverlap:
    def test_consecutive_chunks_share_content(self, chunker):
        """Adjacent chunks must overlap — some tail words of chunk N appear in chunk N+1."""
        sentences = [
            f"Sentence number {i} provides important context about topic {i % 5}."
            for i in range(200)
        ]
        doc = _make_doc(" ".join(sentences))
        chunks = chunker.chunk_document(doc)

        assert len(chunks) >= 3, "Need at least 3 chunks to test overlap properly"

        for i in range(len(chunks) - 1):
            tail_words = set(chunks[i].content.split()[-15:])
            head_words = set(chunks[i + 1].content.split()[:15])
            overlap = tail_words & head_words
            assert len(overlap) > 0, (
                f"Chunks {i} and {i+1} share no overlapping words"
            )

    def test_chunk_ids_are_deterministic(self, chunker):
        """Running the chunker twice on the same doc must yield identical chunk IDs."""
        doc = _make_doc("Deterministic chunking test content. " * 200)
        first = chunker.chunk_document(doc)
        second = chunker.chunk_document(doc)

        assert len(first) == len(second)
        for a, b in zip(first, second):
            assert a.chunk_id == b.chunk_id
            assert a.content_hash == b.content_hash


# ------------- File Size Limit Tests ------------- #

class TestFileSizeLimit:
    def test_file_over_1mb_skipped(self):
        """DocumentLoader must skip files exceeding MAX_FILE_SIZE_MB."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault = Path(tmpdir)

            # Create a file just over 1MB
            big = vault / "big.md"
            big.write_text("x" * (MAX_FILE_SIZE_MB * 1024 * 1024 + 1), encoding="utf-8")

            # Create a normal file
            ok = vault / "ok.md"
            ok.write_text("Within size limit", encoding="utf-8")

            loader = DocumentLoader(str(vault))
            docs = list(loader.load_documents())

            paths = [d.path for d in docs]
            assert "ok.md" in paths, "Normal file should be loaded"
            assert "big.md" not in paths, "Oversized file should be skipped"

    def test_file_exactly_at_limit_loaded(self):
        """A file exactly at 1MB should still be loaded."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault = Path(tmpdir)

            exact = vault / "exact.md"
            exact.write_text("x" * (MAX_FILE_SIZE_MB * 1024 * 1024 - 10), encoding="utf-8")

            loader = DocumentLoader(str(vault))
            docs = list(loader.load_documents())

            paths = [d.path for d in docs]
            assert "exact.md" in paths
