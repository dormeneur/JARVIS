"""Debug endpoints for development and testing."""

import logging
from fastapi import APIRouter, HTTPException
from app.services.document_loader import DocumentLoader, EXCLUDED_FOLDERS
from app.config import settings

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/brain/debug", tags=["debug"])


@router.get("/file-count")
async def get_file_count():
    """Count loaded files from vault.
    
    Returns:
        Dictionary with file count and breakdown by extension
    """
    try:
        loader = DocumentLoader(str(settings.vault_path))
        
        total_count = 0
        extension_counts = {}
        
        for doc in loader.load_documents():
            total_count += 1
            # Extract extension from path
            ext = doc.path.split(".")[-1] if "." in doc.path else "no_extension"
            extension_counts[ext] = extension_counts.get(ext, 0) + 1
        
        return {
            "total_files": total_count,
            "by_extension": extension_counts,
            "vault_path": str(settings.vault_path)
        }
    except Exception as e:
        logger.error(f"Failed to count files: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@router.get("/exclusions")
async def check_exclusions():
    """Verify that excluded folders are not being indexed.
    
    Returns:
        Dictionary with exclusion verification results
    """
    try:
        loader = DocumentLoader(str(settings.vault_path))
        
        secrets_files_found = 0
        excluded_files_found = {folder: 0 for folder in EXCLUDED_FOLDERS}
        total_files = 0
        
        for doc in loader.load_documents():
            total_files += 1
            
            # Check if any excluded folder appears in the path
            for folder in EXCLUDED_FOLDERS:
                if folder in doc.path.split("/"):
                    excluded_files_found[folder] += 1
                    if folder == "Secrets":
                        secrets_files_found += 1
        
        return {
            "secrets_files_found": secrets_files_found,
            "excluded_files_by_folder": excluded_files_found,
            "total_files_loaded": total_files,
            "excluded_folders": EXCLUDED_FOLDERS,
            "status": "PASS" if secrets_files_found == 0 else "FAIL"
        }
    except Exception as e:
        logger.error(f"Failed to check exclusions: {e}")
        raise HTTPException(status_code=500, detail=str(e))
