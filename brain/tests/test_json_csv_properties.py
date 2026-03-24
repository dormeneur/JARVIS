"""Property-based tests for JSON and CSV parsing.

Feature: phase-3-ai-integration
Tests Properties 34, 35
"""

import sys
import json
import tempfile
from pathlib import Path

# Add parent directory to path for imports
sys.path.insert(0, str(Path(__file__).parent.parent))

from hypothesis import given, settings, strategies as st

from app.services.document_loader import DocumentLoader


# Property 34: JSON Round-Trip
# Validates: Requirement 21.4
@settings(max_examples=100)
@given(
    json_data=st.recursive(
        st.one_of(
            st.none(),
            st.booleans(),
            st.integers(min_value=-1000000, max_value=1000000),
            st.floats(allow_nan=False, allow_infinity=False, width=32),
            st.text(min_size=0, max_size=100, alphabet=st.characters(
                blacklist_categories=("Cs",),  # Exclude surrogates
                min_codepoint=0x20,
            )),
        ),
        lambda children: st.one_of(
            st.lists(children, max_size=10),
            st.dictionaries(
                st.text(min_size=1, max_size=20, alphabet=st.characters(
                    blacklist_categories=("Cs",),
                    min_codepoint=0x20,
                )),
                children,
                max_size=10
            ),
        ),
        max_leaves=20,
    )
)
def test_property_34_json_round_trip(json_data):
    """Property 34: JSON formatting should produce parseable text.
    
    **Validates: Requirement 21.4**
    
    For all valid JSON data, formatting with json.dumps(indent=2) should
    produce text that can be parsed back to equivalent structure.
    """
    with tempfile.TemporaryDirectory() as tmpdir:
        vault_path = Path(tmpdir)
        
        # Create JSON file
        json_file = vault_path / "test.json"
        json_file.write_text(json.dumps(json_data), encoding="utf-8")
        
        # Load document
        loader = DocumentLoader(str(vault_path))
        docs = list(loader.load_documents())
        
        # Should load successfully
        assert len(docs) == 1
        
        # Parse the formatted content back
        parsed_data = json.loads(docs[0].content)
        
        # Should be equivalent to original
        assert parsed_data == json_data, (
            "JSON round-trip failed: parsed data doesn't match original"
        )


# Property 35: CSV Structure Preservation
# Validates: Requirement 22.4
@settings(max_examples=100)
@given(
    num_cols=st.integers(min_value=1, max_value=10),
    num_rows=st.integers(min_value=1, max_value=20),
)
def test_property_35_csv_structure_preservation(num_cols, num_rows):
    """Property 35: CSV to markdown should preserve row/column structure.
    
    **Validates: Requirement 22.4**
    
    For all valid CSV files, conversion to markdown table should preserve
    the number of rows and columns.
    """
    with tempfile.TemporaryDirectory() as tmpdir:
        vault_path = Path(tmpdir)
        
        # Generate CSV content
        # Header row
        headers = [f"Col{i}" for i in range(num_cols)]
        csv_lines = [",".join(headers)]
        
        # Data rows
        for row_idx in range(num_rows):
            row = [f"R{row_idx}C{col_idx}" for col_idx in range(num_cols)]
            csv_lines.append(",".join(row))
        
        csv_content = "\n".join(csv_lines)
        
        # Create CSV file
        csv_file = vault_path / "test.csv"
        csv_file.write_text(csv_content, encoding="utf-8")
        
        # Load document
        loader = DocumentLoader(str(vault_path))
        docs = list(loader.load_documents())
        
        # Should load successfully
        assert len(docs) == 1
        markdown_content = docs[0].content
        
        # Count markdown table rows (header + separator + data rows)
        markdown_lines = [line for line in markdown_content.split("\n") if line.strip()]
        
        # Should have: header row + separator row + data rows
        expected_lines = 1 + 1 + num_rows
        assert len(markdown_lines) == expected_lines, (
            f"Expected {expected_lines} lines in markdown table, got {len(markdown_lines)}"
        )
        
        # Verify header row has correct number of columns
        header_line = markdown_lines[0]
        # Count pipe-separated columns (subtract 2 for leading/trailing pipes)
        header_cols = len([c for c in header_line.split("|") if c.strip()])
        assert header_cols == num_cols, (
            f"Expected {num_cols} columns in header, got {header_cols}"
        )
        
        # Verify separator row exists
        separator_line = markdown_lines[1]
        assert "---" in separator_line, "Separator row should contain '---'"
        
        # Verify each data row has correct number of columns
        for i, data_line in enumerate(markdown_lines[2:], start=1):
            data_cols = len([c for c in data_line.split("|") if c.strip()])
            assert data_cols == num_cols, (
                f"Row {i}: Expected {num_cols} columns, got {data_cols}"
            )


# Additional test: CSV with special characters
@settings(max_examples=50)
@given(
    cell_content=st.text(min_size=0, max_size=50, alphabet=st.characters(
        blacklist_characters=",\n\r",  # Exclude CSV delimiters
        blacklist_categories=("Cs",),
        min_codepoint=0x20,
    ))
)
def test_csv_special_characters(cell_content):
    """Test CSV handling with various text content in cells."""
    with tempfile.TemporaryDirectory() as tmpdir:
        vault_path = Path(tmpdir)
        
        # Create CSV with special content
        csv_content = f"Header\n{cell_content}"
        
        csv_file = vault_path / "test.csv"
        csv_file.write_text(csv_content, encoding="utf-8")
        
        # Load document
        loader = DocumentLoader(str(vault_path))
        docs = list(loader.load_documents())
        
        # Should load successfully
        assert len(docs) == 1
        
        # Content should be in markdown format
        markdown = docs[0].content
        assert "|" in markdown, "Should be markdown table format"
        assert "---" in markdown, "Should have separator row"


# Test: Empty CSV
def test_csv_empty_file():
    """Test handling of empty CSV files."""
    with tempfile.TemporaryDirectory() as tmpdir:
        vault_path = Path(tmpdir)
        
        # Create empty CSV
        csv_file = vault_path / "empty.csv"
        csv_file.write_text("", encoding="utf-8")
        
        # Load document
        loader = DocumentLoader(str(vault_path))
        docs = list(loader.load_documents())
        
        # Should load with empty content
        assert len(docs) == 1
        assert docs[0].content == ""


# Test: CSV with only header
def test_csv_header_only():
    """Test CSV with only header row."""
    with tempfile.TemporaryDirectory() as tmpdir:
        vault_path = Path(tmpdir)
        
        # Create CSV with only header
        csv_file = vault_path / "header_only.csv"
        csv_file.write_text("Col1,Col2,Col3", encoding="utf-8")
        
        # Load document
        loader = DocumentLoader(str(vault_path))
        docs = list(loader.load_documents())
        
        # Should load successfully
        assert len(docs) == 1
        markdown = docs[0].content
        
        # Should have header and separator, but no data rows
        lines = [line for line in markdown.split("\n") if line.strip()]
        assert len(lines) == 2  # header + separator
        assert "Col1" in lines[0]
        assert "---" in lines[1]
