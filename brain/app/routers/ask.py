from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import StreamingResponse
from app.models.ask_models import IndexStatus, AskRequest, AskResponse, AskOptions, Source
import logging
import json

from app.services.retriever import Retriever
from app.services.context_assembler import ContextAssembler
from app.services.ollama_client import OllamaClient

# In standard FastAPI patterns, we might inject this via a dependency,
# but for simplicity here we assume the indexer instance will be attached to the app state
# in the lifespan context manager.

router = APIRouter(tags=["ask"])
logger = logging.getLogger(__name__)

async def generate_rag_stream(request_data: AskRequest, app_state):
    """Generator function that yields NDJSON lines for the StreamingResponse."""
    query = request_data.query
    attachments = request_data.attachments or []
    options = request_data.options or AskOptions()
    logger.info(f"generate_rag_stream started. Query: {query}, Options: {options}")
    
    # Extract services from app state
    embedder = app_state.embedding_pipeline
    store = app_state.vector_store
    chunker = app_state.text_chunker
    loader = app_state.document_loader
    ollama_client = OllamaClient(embedder.ollama_url)  # Use same url
    
    # 1. Retrieve context
    retriever = Retriever(embedder, store)
    retrieved_sources = await retriever.retrieve(
        query=query,
        top_k=options.top_k,
        filter_paths=options.filter_paths
    )
    
    logger.info(f"Retrieved {len(retrieved_sources)} sources from vector store.")
    
    # 2. Assemble prompt
    assembler = ContextAssembler(chunker, loader)
    prompt, final_sources = assembler.assemble_prompt(
        query=query,
        retrieved_sources=retrieved_sources,
        attachments=attachments
    )
    
    logger.info(f"Prompt assembled ({len(prompt)} chars). Opening stream from Ollama...")
    
    # 3. Stream from Ollama
    tokens_used = 0
    full_answer = ""
    
    async for token in ollama_client.generate_streaming(prompt):
        if token.startswith('{"__jarvis_metadata__"'):
            # Parsed metadata
            meta = json.loads(token)
            tokens_used = meta.get("eval_count", 0)
        elif token.startswith('{"__jarvis_error__"'):
            # Handle error
            yield json.dumps({"error": json.loads(token)["__jarvis_error__"]}) + "\n"
            return
        else:
            full_answer += token
            # We yield it similarly to Ollama
            yield json.dumps({"token": token}) + "\n"
            
    # Yield the final structured AskResponse object as the VERY LAST line
    final_response = AskResponse(
        answer=full_answer,
        sources=final_sources if options.include_sources else [],
        model="llama3",
        tokens_used=tokens_used
    )
    logger.info("Ollama stream finished. Yielding final AskResponse.")
    yield final_response.model_dump_json() + "\n"


@router.post("/brain/ask")
async def ask_brain(req: AskRequest, request: Request):
    """Query the AI knowledge base using RAG."""
    # Check if streaming is requested
    options = req.options or AskOptions()
    
    if options.stream:
        return StreamingResponse(
            generate_rag_stream(req, request.app.state),
            media_type="application/x-ndjson"
        )
    else:
        # For non-streaming, we can still use the stream generator but collect everything
        # Or just implement a simpler sync wait. For now, collect the stream.
        full_answer = ""
        sources = []
        tokens = 0
        
        async for chunk in generate_rag_stream(req, request.app.state):
            data = json.loads(chunk)
            if "token" in data:
                full_answer += data["token"]
            elif "answer" in data:  # Final AskResponse
                sources = [Source(**s) for s in data.get("sources", [])]
                tokens = data.get("tokens_used", 0)
                
        return AskResponse(
            answer=full_answer,
            sources=sources,
            model="llama3",
            tokens_used=tokens
        )


@router.post("/brain/reindex", response_model=dict)
async def trigger_reindex(request: Request):
    """Trigger a manual full incremental re-index."""
    indexer = request.app.state.indexer
    if indexer.is_indexing:
        return {"status": "indexing_already_in_progress"}
        
    # Start in background
    indexer.start_background_indexing()
    return {"status": "indexing_started"}


@router.get("/brain/index-status", response_model=IndexStatus)
async def get_index_status(request: Request):
    """Get the current status of the indexer."""
    indexer = request.app.state.indexer
    return indexer.get_status()
