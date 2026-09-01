"""
RAG pipeline: /Secrets must never be indexed or fed to the LLM.

These tests run without Ollama/ChromaDB — they test the loader layer only.
The e2e test (test_rag_secrets_exclusion_e2e.py) covers the full pipeline.
"""
import pytest
from pathlib import Path

from app.services.document_loader import DocumentLoader, EXCLUDED_FOLDERS


@pytest.fixture
def vault_with_secrets(tmp_path: Path) -> Path:
    """Create a vault with a Secrets/ dir that must never be indexed."""
    (tmp_path / "Notes").mkdir()
    (tmp_path / "Notes" / "public.md").write_text("public content", encoding="utf-8")

    (tmp_path / "Secrets").mkdir()
    (tmp_path / "Secrets" / "master_key.jvs").write_bytes(b"\x00ENCRYPTED")
    (tmp_path / "Secrets" / "passwords.md").write_text("hunter2", encoding="utf-8")

    (tmp_path / "system").mkdir()
    (tmp_path / "system" / "devices.json").write_text('{"dev1": {}}', encoding="utf-8")
    return tmp_path


def _load_all_paths(vault_path: Path) -> list[str]:
    loader = DocumentLoader(str(vault_path))
    return [doc.path for doc in loader.load_documents()]


class TestSecretsNeverIndexed:
    def test_secrets_folder_excluded_from_index(self, vault_with_secrets):
        paths = _load_all_paths(vault_with_secrets)
        for p in paths:
            assert not p.startswith("Secrets"), f"Secrets file leaked into index: {p}"

    def test_system_folder_excluded_from_index(self, vault_with_secrets):
        paths = _load_all_paths(vault_with_secrets)
        for p in paths:
            assert not p.startswith("system"), f"system file leaked into index: {p}"

    def test_public_notes_are_indexed(self, vault_with_secrets):
        paths = _load_all_paths(vault_with_secrets)
        assert any("Notes/public.md" in p or "public.md" in p for p in paths)

    def test_excluded_folders_constant_includes_secrets(self):
        assert "Secrets" in EXCLUDED_FOLDERS

    def test_excluded_folders_constant_includes_system(self):
        assert "system" in EXCLUDED_FOLDERS

    def test_deeply_nested_secrets_file_excluded(self, vault_with_secrets):
        """Secrets nested inside another folder should still be excluded."""
        nested = vault_with_secrets / "Work" / "Secrets"
        nested.mkdir(parents=True)
        (nested / "nested_key.txt").write_text("classified", encoding="utf-8")

        paths = _load_all_paths(vault_with_secrets)
        for p in paths:
            # The file is at Work/Secrets/nested_key.txt — it starts with Secrets
            # as a path component, so any part match must block it.
            parts = Path(p).parts
            assert "Secrets" not in parts, f"Nested secrets leaked: {p}"

    def test_content_hash_is_stable_for_same_file(self, vault_with_secrets):
        """Hash must be deterministic — the indexer's change-detection depends on it."""
        loader = DocumentLoader(str(vault_with_secrets))
        docs = list(loader.load_documents())
        assert len(docs) > 0

        docs2 = list(loader.load_documents())
        hashes1 = {d.path: d.content_hash for d in docs}
        hashes2 = {d.path: d.content_hash for d in docs2}
        assert hashes1 == hashes2
