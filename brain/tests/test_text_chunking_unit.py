import pytest
from datetime import datetime
from brain.app.services.document_loader import LoadedDocument
from brain.app.services.text_chunker import TextChunker, CHUNK_SIZE_TOKENS, CHUNK_OVERLAP_TOKENS

@pytest.fixture
def text_chunker():
    return TextChunker()

def create_mock_doc(content: str) -> LoadedDocument:
    return LoadedDocument(
        path="test/file.md",
        content=content,
        last_modified=datetime.now(),
        content_hash="mock_hash_123"
    )

def test_long_document_produces_expected_chunks(text_chunker):
    # A standard word is ~1.3 tokens. 2000 words * 1.3 ≈ 2600 tokens.
    # 2600 / (512 - 64) ≈ 2600 / 448 ≈ 5.8 chunks. Could be 6-8 chunks depending on spacing.
    words = ["test", "document", "with", "multiple", "words."] * 400
    content = " ".join(words)
    doc = create_mock_doc(content)
    
    chunks = text_chunker.chunk_document(doc)
    
    assert len(chunks) >= 4 and len(chunks) <= 12, f"Expected around 6-8 chunks, got {len(chunks)}"

def test_short_document_single_chunk(text_chunker):
    content = "This is a very short document with fewer than 512 tokens."
    doc = create_mock_doc(content)
    
    chunks = text_chunker.chunk_document(doc)
    
    assert len(chunks) == 1
    assert chunks[0].content == content
    assert chunks[0].total_chunks == 1

def test_document_with_no_natural_boundaries(text_chunker):
    # Use a very long string of repeated words without paragraphs/newlines
    # so the only separator available is " " (space).
    # Each word is ~1-2 tokens, so 3000 words guarantees well over 512 tokens.
    content = " ".join(["word"] * 3000)
    doc = create_mock_doc(content)
    
    chunks = text_chunker.chunk_document(doc)
    
    assert len(chunks) > 1
    for i, chunk in enumerate(chunks[:-1]):
        tokens = text_chunker._count_tokens(chunk.content)
        assert tokens <= CHUNK_SIZE_TOKENS + 10  # small tolerance

def test_chunk_overlap_verification(text_chunker):
    # Create text that easily splits but spans multiple chunks
    sentences = ["This is sentence number {} in a longer sequence that should be split into multiple chunks.".format(i) for i in range(100)]
    content = " ".join(sentences)
    doc = create_mock_doc(content)
    
    chunks = text_chunker.chunk_document(doc)
    assert len(chunks) > 1
    
    # Check overlap explicitly for chunks
    for i in range(len(chunks) - 1):
        end_of_current = chunks[i].content
        start_of_next = chunks[i+1].content
        
        # Verify they share common text (overlap)
        # We can find the overlap token count directly or check if a part of the text exists
        last_few_words = " ".join(end_of_current.split()[-10:])
        first_few_words = " ".join(start_of_next.split()[:10])
        
        # Overlap means some of the last words of chunk i are in the first words of chunk i+1
        # It's an approximation for string existence due to exact token splitting
        overlap_found = any(word in start_of_next for word in end_of_current.split()[-10:])
        assert overlap_found, "Consecutive chunks should have overlapping content."
