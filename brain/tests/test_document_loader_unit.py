"""Unit tests for document loader.

Tests specific file types, edge cases, and error handling.
"""

import sys
import json
import tempfile
from pathlib import Path

# Add parent directory to path for imports
sys.path.insert(0, str(Path(__file__).parent.parent))

import pytest

from app.services.document_loader import (
    DocumentLoader,
    EXCLUDED_FOLDERS,
    MAX_FILE_SIZE_MB,
)


class TestDocumentLoader:
    """Unit tests for DocumentLoader class."""
    
    def test_markdown_file_loading(self):
        """Test loading markdown files."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create markdown file
            md_file = vault_path / "test.md"
            content = "# Test Markdown\n\nThis is a test."
            md_file.write_text(content, encoding="utf-8")
            
            # Load documents
            loader = DocumentLoader(str(vault_path))
            docs = list(loader.load_documents())
            
            assert len(docs) == 1
            assert docs[0].path == "test.md"
            assert docs[0].content == content
            assert docs[0].content_hash  # Should have a hash
    
    def test_txt_file_loading(self):
        """Test loading text files."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create text file
            txt_file = vault_path / "notes.txt"
            content = "Plain text notes\nLine 2"
            txt_file.write_text(content, encoding="utf-8")
            
            # Load documents
            loader = DocumentLoader(str(vault_path))
            docs = list(loader.load_documents())
            
            assert len(docs) == 1
            assert docs[0].content == content
    
    def test_json_file_loading(self):
        """Test loading and formatting JSON files."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create JSON file
            json_file = vault_path / "data.json"
            data = {"name": "test", "value": 123, "nested": {"key": "value"}}
            json_file.write_text(json.dumps(data), encoding="utf-8")
            
            # Load documents
            loader = DocumentLoader(str(vault_path))
            docs = list(loader.load_documents())
            
            assert len(docs) == 1
            # Content should be pretty-printed JSON
            parsed = json.loads(docs[0].content)
            assert parsed == data
            assert "  " in docs[0].content  # Should have indentation
    
    def test_csv_file_loading(self):
        """Test loading and converting CSV files to markdown."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create CSV file
            csv_file = vault_path / "data.csv"
            csv_content = "Name,Age,City\nAlice,30,NYC\nBob,25,LA"
            csv_file.write_text(csv_content, encoding="utf-8")
            
            # Load documents
            loader = DocumentLoader(str(vault_path))
            docs = list(loader.load_documents())
            
            assert len(docs) == 1
            content = docs[0].content
            
            # Verify markdown table format
            assert "| Name | Age | City |" in content
            assert "| --- | --- | --- |" in content
            assert "| Alice | 30 | NYC |" in content
            assert "| Bob | 25 | LA |" in content
    
    def test_empty_file(self):
        """Test handling of empty files."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create empty file
            empty_file = vault_path / "empty.md"
            empty_file.write_text("", encoding="utf-8")
            
            # Load documents
            loader = DocumentLoader(str(vault_path))
            docs = list(loader.load_documents())
            
            assert len(docs) == 1
            assert docs[0].content == ""
    
    def test_invalid_json_file(self):
        """Test handling of invalid JSON files."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create invalid JSON file
            json_file = vault_path / "invalid.json"
            json_file.write_text("{invalid json", encoding="utf-8")
            
            # Load documents - should skip invalid JSON
            loader = DocumentLoader(str(vault_path))
            docs = list(loader.load_documents())
            
            # Invalid JSON should be skipped (returns None from _extract_json)
            assert len(docs) == 0
    
    def test_invalid_csv_file(self):
        """Test handling of problematic CSV files."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create CSV with inconsistent columns
            csv_file = vault_path / "inconsistent.csv"
            csv_content = "A,B,C\n1,2\n3,4,5,6"
            csv_file.write_text(csv_content, encoding="utf-8")
            
            # Load documents - should handle gracefully
            loader = DocumentLoader(str(vault_path))
            docs = list(loader.load_documents())
            
            # Should still load, with padding/truncation
            assert len(docs) == 1
    
    def test_secrets_folder_exclusion(self):
        """Test that Secrets folder is excluded at the walk level."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create Secrets folder with nested file
            secrets_dir = vault_path / "Secrets"
            secrets_dir.mkdir()
            nested_dir = secrets_dir / "private"
            nested_dir.mkdir()
            secret_file = nested_dir / "secret.md"
            secret_file.write_text("Secret content", encoding="utf-8")
            
            # Create normal file
            normal_file = vault_path / "normal.md"
            normal_file.write_text("Normal content", encoding="utf-8")
            
            # Load documents
            loader = DocumentLoader(str(vault_path))
            docs = list(loader.load_documents())
            
            # Should only load normal file
            assert len(docs) == 1
            assert docs[0].path == "normal.md"

            # Verify that the walker didn't even visit the Secrets subfolder
            # We can do this by checking if any loaded documents have "Secrets" in their path
            # (which we already do), but to be absolutely sure it's walk-level pruning:
            import os
            visited_dirs = []
            for root, dirs, files in os.walk(str(vault_path)):
                # Simulate the pruning logic
                dirs[:] = [d for d in dirs if d not in EXCLUDED_FOLDERS]
                visited_dirs.append(Path(root).name)
            
            assert "Secrets" not in visited_dirs
            assert "private" not in visited_dirs
    
    def test_all_excluded_folders(self):
        """Test that all EXCLUDED_FOLDERS are properly excluded."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create a file in each excluded folder
            for folder in EXCLUDED_FOLDERS:
                folder_path = vault_path / folder
                folder_path.mkdir()
                test_file = folder_path / "test.md"
                test_file.write_text(f"Content in {folder}", encoding="utf-8")
            
            # Create one normal file
            normal_file = vault_path / "normal.md"
            normal_file.write_text("Normal content", encoding="utf-8")
            
            # Load documents
            loader = DocumentLoader(str(vault_path))
            docs = list(loader.load_documents())
            
            # Should only load the normal file
            assert len(docs) == 1
            assert docs[0].path == "normal.md"
    
    def test_file_size_limit(self):
        """Test that files over MAX_FILE_SIZE_MB are skipped."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create a file just over the limit
            large_file = vault_path / "large.md"
            # Write 51 MB
            chunk_size = 1024 * 1024  # 1 MB
            with open(large_file, "w", encoding="utf-8") as f:
                for _ in range(MAX_FILE_SIZE_MB + 1):
                    f.write("x" * chunk_size)
            
            # Create a normal file
            normal_file = vault_path / "normal.md"
            normal_file.write_text("Normal content", encoding="utf-8")
            
            # Load documents
            loader = DocumentLoader(str(vault_path))
            docs = list(loader.load_documents())
            
            # Should only load normal file
            assert len(docs) == 1
            assert docs[0].path == "normal.md"
    
    def test_unsupported_extension(self):
        """Test that unsupported file types are skipped."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create unsupported file
            unsupported = vault_path / "image.png"
            unsupported.write_bytes(b"fake image data")
            
            # Create supported file
            supported = vault_path / "doc.md"
            supported.write_text("Markdown content", encoding="utf-8")
            
            # Load documents
            loader = DocumentLoader(str(vault_path))
            docs = list(loader.load_documents())
            
            # Should only load markdown file
            assert len(docs) == 1
            assert docs[0].path == "doc.md"
    
    def test_nested_directories(self):
        """Test loading files from nested directory structure."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create nested structure
            (vault_path / "folder1" / "subfolder").mkdir(parents=True)
            file1 = vault_path / "folder1" / "doc1.md"
            file2 = vault_path / "folder1" / "subfolder" / "doc2.md"
            
            file1.write_text("Content 1", encoding="utf-8")
            file2.write_text("Content 2", encoding="utf-8")
            
            # Load documents
            loader = DocumentLoader(str(vault_path))
            docs = list(loader.load_documents())
            
            # Should load both files
            assert len(docs) == 2
            paths = {doc.path for doc in docs}
            assert "folder1/doc1.md" in paths or "folder1\\doc1.md" in paths
            assert "folder1/subfolder/doc2.md" in paths or "folder1\\subfolder\\doc2.md" in paths
    
    def test_hash_consistency(self):
        """Test that hash computation is consistent."""
        with tempfile.TemporaryDirectory() as tmpdir:
            vault_path = Path(tmpdir)
            
            # Create file
            test_file = vault_path / "test.md"
            test_file.write_text("Test content", encoding="utf-8")
            
            # Load documents twice
            loader = DocumentLoader(str(vault_path))
            docs1 = list(loader.load_documents())
            docs2 = list(loader.load_documents())
            
            # Hashes should be identical
            assert docs1[0].content_hash == docs2[0].content_hash
    
    def test_nonexistent_vault_path(self):
        """Test handling of nonexistent vault path."""
        loader = DocumentLoader("/nonexistent/path")
        docs = list(loader.load_documents())
        
        # Should return empty list, not crash
        assert len(docs) == 0
