from fastapi import APIRouter, HTTPException, Request
from pydantic import ValidationError
from typing import List, Dict, Any
import logging
import json
import re

from app.models.ask_models import GenerateFilesRequest, GenerateFileManifestItem
from app.services.ollama_client import OllamaClient
from app.services.sanitizer import sanitize_manifest

router = APIRouter(tags=["generate"])
logger = logging.getLogger(__name__)

GENERATE_SYSTEM_PROMPT = """You are an expert file system scaffold generator. 
The user will provide a prompt describing files or folders they want to create. 
You must output ONLY valid JSON. The output must be a list of objects exactly matching this schema:
[
  {
    "path": "filename.ext",
    "content": "file contents here, can be markdown or code",
    "type": "file" // or "directory"
  }
]
Do NOT wrap the JSON in Markdown backticks. Do NOT include any explanations. ONLY output the JSON array.
If no filename is given, use untitled.txt. If no extension is given, infer it or use .txt.
If it is a text/code file, generate an appropriate placeholder content or the full code requested.
Ensure all path separators are standard slashes '/'.
"""

@router.post("/brain/generate-files/dry-run", response_model=List[GenerateFileManifestItem])
async def generate_files_dry_run(req: GenerateFilesRequest, request: Request):
    """Dry-run file generation (returns manifest without intent to write)."""
    return await _process_prompt_for_files(req, request)

@router.post("/brain/generate-files", response_model=List[GenerateFileManifestItem])
async def generate_files(req: GenerateFilesRequest, request: Request):
    """Generate a file scaffold manifest based on natural language."""
    return await _process_prompt_for_files(req, request)

async def _process_prompt_for_files(req: GenerateFilesRequest, request: Request) -> List[GenerateFileManifestItem]:
    logger.info(f"Generating files for prompt: {req.prompt}")
    
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

    # 2. If no shortcut matches, use LLM
    if parsed is None:
        embedder = request.app.state.embedding_pipeline
        ollama_client = OllamaClient(embedder.ollama_url)
        
        # Construct the query prompt
        user_prompt = f"Target Directory Context: {req.current_directory}\nRequest: {req.prompt}"
        full_prompt = f"{GENERATE_SYSTEM_PROMPT}\n\n{user_prompt}"
        
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
            # Strip potential markdown formatting if model didn't listen
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

    # Sanitize and validate
    sanitized = sanitize_manifest(parsed)
    
    # Map back to Pydantic models
    manifest_items = []
    for item in sanitized:
        try:
            manifest_items.append(GenerateFileManifestItem(**item))
        except ValidationError as e:
            logger.error(f"Validation error on sanitized item: {e}")
            continue
            
    return manifest_items
