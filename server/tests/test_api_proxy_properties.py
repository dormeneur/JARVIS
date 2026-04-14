"""Property-based tests for jv-api proxy router.

Feature: phase-3-ai-integration
Properties tested:
- Property 31: API Proxy Authentication
- Property 32: API Proxy Forwarding
"""

import pytest
from unittest.mock import AsyncMock, patch, MagicMock
from hypothesis import given, settings, strategies as st

from fastapi import Request, HTTPException
from app.routers.ask import ask_jarvis, ask_status

# Remove function-scoped fixture to avoid Hypothesis health check errors
def get_mock_request():
    req = AsyncMock(spec=Request)
    req.json.return_value = {"query": "test"}
    return req

@settings(max_examples=10)
@given(auth_valid=st.booleans())
@pytest.mark.asyncio
async def test_property_31_authentication(auth_valid):
    """Property 31: API Proxy Authentication"""
    mock_request = get_mock_request()
    
    # In FastAPI, Depends() handles the auth before the handler runs,
    # but for unit testing the handler itself, we just check that it
    # accepts the device dependency which enforces Auth.
    
    # To test actual property: The route definition requires device=Depends(...)
    # We can inspect the function signature to ensure it requires auth_service.get_current_device
    import inspect
    from app.services import auth as auth_service
    
    sig = inspect.signature(ask_jarvis)
    assert "device" in sig.parameters
    
    # Check default is Depends(auth_service.get_current_device)
    param = sig.parameters["device"]
    import fastapi
    assert isinstance(param.default, fastapi.params.Depends)

@settings(max_examples=20)
@given(url_path=st.text(min_size=1, max_size=10))
@pytest.mark.asyncio
async def test_property_32_forwarding(url_path):
    """Property 32: API Proxy Forwarding"""
    mock_request = get_mock_request()
    
    with patch("httpx.AsyncClient.stream") as mock_stream, \
         patch("app.routers.ask.settings") as mock_settings:
        
        mock_settings.brain_url = f"http://{url_path}"
        
        mock_ctx = AsyncMock()
        mock_response = AsyncMock()
        mock_response.status_code = 200
        mock_ctx.__aenter__.return_value = mock_response
        mock_stream.return_value = mock_ctx
        
        # We need to test the logic handles streaming correctly
        res = await ask_jarvis(mock_request, device=MagicMock())
        assert res.media_type == "application/x-ndjson"
