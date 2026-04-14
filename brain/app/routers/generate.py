from fastapi import APIRouter, HTTPException, Request
from pydantic import ValidationError
from typing import List, Dict, Any
import logging
import json
import re

from app.models.ask_models import GenerateFilesRequest, GenerateFileManifestItem
from app.services.ollama_client import OllamaClient
from app.services.sanitizer import sanitize_manifest
from app.services.fs_tree import load_fs_tree, refresh_fs_tree

router = APIRouter(tags=["generate"])
logger = logging.getLogger(__name__)

GENERATE_SYSTEM_PROMPT = """You are an expert file system scaffold generator. 
The user will provide a prompt describing files or folders they want to create. 
You must output ONLY valid JSON. The output must be a list of objects exactly matching this schema:
[
  {{
    "path": "relative/path/filename.ext",
    "content": "file contents here, can be markdown or code",
    "type": "file"
  }}
]
Do NOT wrap the JSON in Markdown backticks. Do NOT include any explanations. ONLY output the JSON array.
If no filename is given, use untitled.txt. If no extension is given, infer it or use .txt.
If it is a text/code file, generate an appropriate placeholder content or the full code requested.
Ensure all path separators are standard slashes '/'.
Use RELATIVE paths only — never absolute paths, never start with /.
"""

FILESYSTEM_CONTEXT_TEMPLATE = """
The current filesystem contains these paths:
{fs_paths}

Place new files in the most logical location based on this structure and the user's prompt.
If the user is working in a specific directory, prefer creating files relative to that location.
If no suitable location is obvious, place files at the root level.
"""


def _build_prompt(req: GenerateFilesRequest, fs_tree: List[str]) -> str:
    """Construct the full prompt including filesystem context."""
    parts = [GENERATE_SYSTEM_PROMPT]

    # Inject filesystem context if available
    if fs_tree:
        fs_snippet = json.dumps(fs_tree[:200])  # Cap injected paths to keep prompt small
        parts.append(FILESYSTEM_CONTEXT_TEMPLATE.format(fs_paths=fs_snippet))
    else:
        parts.append("\nNo existing filesystem context available. Place files at root level.\n")

    # User prompt with directory context
    if req.current_directory and req.current_directory != ".":
        parts.append(f"The user is currently in directory: {req.current_directory}")
        parts.append(f"Create files relative to this directory unless the prompt clearly specifies otherwise.")

    parts.append(f"\nUser request: {req.prompt}")
    return "\n\n".join(parts)


def _prefix_shortcut_paths(parsed: List[Dict], current_directory: str) -> List[Dict]:
    """Prefix shortcut template paths with current_directory."""
    if not current_directory or current_directory == ".":
        return parsed

    prefix = current_directory.strip("/")
    result = []
    for item in parsed:
        item_copy = dict(item)
        item_copy["path"] = f"{prefix}/{item_copy['path']}"
        result.append(item_copy)
    return result


@router.post("/brain/generate-files/dry-run", response_model=List[GenerateFileManifestItem])
async def generate_files_dry_run(req: GenerateFilesRequest, request: Request):
    """Dry-run file generation (returns manifest without intent to write)."""
    return await _process_prompt_for_files(req, request)

@router.post("/brain/generate-files", response_model=List[GenerateFileManifestItem])
async def generate_files(req: GenerateFilesRequest, request: Request):
    """Generate a file scaffold manifest based on natural language."""
    return await _process_prompt_for_files(req, request)

@router.post("/brain/refresh-fs-tree")
async def refresh_tree():
    """Rebuild the filesystem tree snapshot."""
    tree = refresh_fs_tree()
    return {"status": "ok", "paths_indexed": len(tree)}


async def _process_prompt_for_files(req: GenerateFilesRequest, request: Request) -> List[GenerateFileManifestItem]:
    logger.info(f"Generating files for prompt: {req.prompt}")

    # Load filesystem context
    fs_tree = load_fs_tree()

    # Determine scope for sanitization
    scope = req.current_directory if req.current_directory and req.current_directory != "." else None

    # 1. Match Shortcuts first
    lower_prompt = req.prompt.lower()
    parsed = None

    if "react component" in lower_prompt:
        parsed = [
            {"path": "ComponentName.jsx", "content": "export default function ComponentName() {\n  return <div>ComponentName</div>;\n}", "type": "file"},
            {"path": "ComponentName.module.css", "content": ".container {\n  display: flex;\n}", "type": "file"},
            {"path": "index.js", "content": "export { default } from './ComponentName';\n", "type": "file"}
        ]
    elif "python module" in lower_prompt:
        parsed = [
            {"path": "__init__.py", "content": "# Init module\n", "type": "file"},
            {"path": "main.py", "content": "def main():\n    pass\n\nif __name__ == '__main__':\n    main()\n", "type": "file"},
            {"path": "README.md", "content": "# Python Module\n", "type": "file"}
        ]
    elif "express route" in lower_prompt:
        parsed = [
            {"path": "router.js", "content": "const express = require('express');\nconst router = express.Router();\nconst controller = require('./controller');\n\nrouter.get('/', controller.handleRequest);\nmodule.exports = router;\n", "type": "file"},
            {"path": "controller.js", "content": "exports.handleRequest = (req, res) => {\n  res.send('ok');\n};\n", "type": "file"},
            {"path": "README.md", "content": "# Express Route\n", "type": "file"}
        ]

    if parsed is not None:
        # Prefix shortcut paths with current_directory
        parsed = _prefix_shortcut_paths(parsed, req.current_directory)
    else:
        # 2. No shortcut match — use LLM
        embedder = request.app.state.embedding_pipeline
        ollama_client = OllamaClient(embedder.ollama_url)

        full_prompt = _build_prompt(req, fs_tree)

        full_response = ""
        try:
            async for token in ollama_client.generate_streaming(full_prompt):
                if token.startswith('{"__jarvis_error__"'):
                    error_msg = json.loads(token)["__jarvis_error__"]
                    raise HTTPException(status_code=500, detail=error_msg)
                elif not token.startswith('{"__jarvis_metadata__"'):
                    full_response += token
        except Exception as e:
            logger.error(f"Ollama generation failed: {e}")
            raise HTTPException(status_code=500, detail=f"Generation failed: {str(e)}")

        # Parse JSON
        try:
            clean_json = full_response.strip()
            if clean_json.startswith("```json"):
                clean_json = clean_json[7:]
            if clean_json.startswith("```"):
                clean_json = clean_json[3:]
            if clean_json.endswith("```"):
                clean_json = clean_json[:-3]

            parsed = json.loads(clean_json.strip())
            if not isinstance(parsed, list):
                parsed = [parsed]
        except json.JSONDecodeError as e:
            logger.error(f"Failed to parse LLM output as JSON: {full_response}")
            raise HTTPException(status_code=500, detail="Failed to parse structure from AI")

    # Sanitize and validate (with scope if current_directory is set)
    sanitized = sanitize_manifest(parsed, scope=scope)

    # Map back to Pydantic models
    manifest_items = []
    for item in sanitized:
        try:
            manifest_items.append(GenerateFileManifestItem(**item))
        except ValidationError as e:
            logger.error(f"Validation error on sanitized item: {e}")
            continue

    return manifest_items
