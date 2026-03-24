"""Property-based tests for document loading.

Feature: phase-3-ai-integration
Tests Properties 3, 4, 36, 37, 38
"""

import sys
import tempfile
from pathlib import Path

# Add parent directory to path for imports
sys.path.insert(0, str(Path(__file__).parent.parent))

import pytest
from hypothesis import given, settings, strategies as st

from app.services.document_loader import (
    DocumentLoader,
    EXCLUDED_FOLDERS,
    MAX_FILE_SIZE_MB,
    SUPPORTED_EXTENSIONS,
)


# Property 3: Secrets Folder Exclusion
# Validates: Requirements 2.7, 16.4
@settings(max_examples=50)
@given(
    excluded_folder=st.sampled_from(EXCLUDED_FOLDERS),
    filename=st.text(
        min_size=1,
        max_size=20,
        alphabet=st.characters(
            blacklist_characters='/\\:*?"<>|',  # Invalid filename chars
            min_codepoint=0x20,
            max_codepoint=0x7E,
        )
    ).filter(lambda x: len(x.strip()) > 0).map(lambda x: x.strip() + ".md"),
)
def test_property_3_secrets_folder_exclusion(excluded_folder, filename):
    """Property 3: Files in EXCLUDED_FOLDERS should never be loaded.
    
    **Validates: Requirements 2.7, 16.4**
    """
    with tempfile.TemporaryDirectory() as tmpdir:
        vault_path = Path(tmpdir)
        
        # Create excluded folder with a file
        excluded_path = vault_path / excluded_folder
        excluded_path.mkdir(parents=True, exist_ok=True)
        
        test_file = excluded_path / filename
        test_file.write_text("This should be excluded", encoding="utf-8")
        
        # Load documents
        loader = DocumentLoader(str(vault_path))
        loaded_docs = list(loader.load_documents())
        
        # Verify no files from excluded folder are loaded
        for doc in loaded_docs:
            assert excluded_folder not in doc.path.split("/"), (
                f"File from excluded folder '{excluded_folder}' was loaded: {doc.path}"
            )


# Property 4: File Size Limit Enforcement
# Validates: Requirement 2.8
def test_property_4_file_size_limit_enforcement():
    """Property 4: Files larger than MAX_FILE_SIZE_MB should be skipped.
    
    **Validates: Requirement 2.8**
    """
    with tempfile.TemporaryDirectory() as tmpdir:
        vault_path = Path(tmpdir)
        
        # Create a file larger than the limit
        large_file = vault_path / "large_file.md"
        # Write just over the limit (51 MB)
        chunk_size = 1024 * 1024  # 1 MB
        with open(large_file, "w", encoding="utf-8") as f:
            for _ in range(MAX_FILE_SIZE_MB + 1):
                f.write("x" * chunk_size)
        
        # Load documents
        loader = DocumentLoader(str(vault_path))
        loaded_docs = list(loader.load_documents())
        
        # Verify large file was not loaded
        loaded_paths = [doc.path for doc in loaded_docs]
        assert "large_file.md" not in loaded_paths, (
            f"File larger than {MAX_FILE_SIZE_MB}MB was loaded"
        )


# Property 36: Supported File Types
# Validates: Requirement 2.1
@settings(max_examples=20)
@given(extension=st.sampled_from(SUPPORTED_EXTENSIONS))
def test_property_36_supported_file_types(extension):
    """Property 36: All supported extensions should extract content successfully.
    
    **Validates: Requirement 2.1**
    """
    with tempfile.TemporaryDirectory() as tmpdir:
        vault_path = Path(tmpdir)
        
        # Create a simple file for each supported type
        test_file = vault_path / f"test{extension}"
        
        if extension in [".md", ".txt"]:
            test_file.write_text("Test content", encoding="utf-8")
        elif extension == ".json":
            test_file.write_text('{"key": "value"}', encoding="utf-8")
        elif extension == ".csv":
            test_file.write_text("header1,header2\nvalue1,value2", encoding="utf-8")
        elif extension == ".pdf":
            # Skip PDF for this test as it requires special handling
            pytest.skip("PDF requires PyMuPDF document creation")
        elif extension == ".docx":
            # Skip DOCX for this test as it requires special handling
            pytest.skip("DOCX requires python-docx document creation")
        
        # Load documents
        loader = DocumentLoader(str(vault_path))
        loaded_docs = list(loader.load_documents())
        
        # Verify file was loaded and has content
        assert len(loaded_docs) == 1, f"Expected 1 document, got {len(loaded_docs)}"
        assert loaded_docs[0].content, f"No content extracted from {extension} file"


# Property 37: UTF-8 Text Preservation
# Validates: Requirement 2.6
@settings(max_examples=50)
@given(
    text_content=st.text(min_size=1, max_size=1000, alphabet=st.characters(
        blacklist_categories=("Cs",),  # Exclude surrogates
        min_codepoint=0x20,  # Start from space character
    ))
)
def test_property_37_utf8_text_preservation(text_content):
    """Property 37: Markdown/txt files should preserve all UTF-8 characters.
    
    **Validates: Requirement 2.6**
    """
    with tempfile.TemporaryDirectory() as tmpdir:
        vault_path = Path(tmpdir)
        
        # Test with markdown file
        test_file = vault_path / "test.md"
        test_file.write_text(text_content, encoding="utf-8")
        
        # Load documents
        loader = DocumentLoader(str(vault_path))
        loaded_docs = list(loader.load_documents())
        
        # Verify content is preserved
        assert len(loaded_docs) == 1
        assert loaded_docs[0].content == text_content, (
            "UTF-8 content was not preserved correctly"
        )


# Property 38: LoadedDocument Structure
# Validates: Requirement 2.9
def test_property_38_loaded_document_structure():
    """Property 38: All LoadedDocument instances should have required fields.
    
    **Validates: Requirement 2.9**
    """
    with tempfile.TemporaryDirectory() as tmpdir:
        vault_path = Path(tmpdir)
        
        # Create a test file
        test_file = vault_path / "test.md"
        test_file.write_text("Test content", encoding="utf-8")
        
        # Load documents
        loader = DocumentLoader(str(vault_path))
        loaded_docs = list(loader.load_documents())
        
        # Verify structure
        assert len(loaded_docs) == 1
        doc = loaded_docs[0]
        
        # Check all required fields exist and have correct types
        assert hasattr(doc, "path"), "LoadedDocument missing 'path' field"
        assert isinstance(doc.path, str), "path should be string"
        
        assert hasattr(doc, "content"), "LoadedDocument missing 'content' field"
        assert isinstance(doc.content, str), "content should be string"
        
        assert hasattr(doc, "last_modified"), "LoadedDocument missing 'last_modified' field"
        # datetime check
        assert hasattr(doc.last_modified, "year"), "last_modified should be datetime"
        
        assert hasattr(doc, "content_hash"), "LoadedDocument missing 'content_hash' field"
        assert isinstance(doc.content_hash, str), "content_hash should be string"
        assert len(doc.content_hash) == 64, "content_hash should be SHA-256 (64 hex chars)"
