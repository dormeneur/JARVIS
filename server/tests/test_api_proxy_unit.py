"""Unit tests for the ask proxy router."""

import pytest
import json
from unittest.mock import patch, AsyncMock, MagicMock
from fastapi import Request, HTTPException

from app.routers.ask import ask_status, ask_index_status, ask_reindex

@pytest.mark.asyncio
async def test_ask_status_forwarding():
    with patch("httpx.AsyncClient.get") as mock_get:
        mock_resp = MagicMock()
        mock_resp.json.return_value = {"status": "ok"}
        mock_resp.raise_for_status.return_value = None
        mock_get.return_value = mock_resp
        
        res = await ask_status(device=MagicMock())
        assert res["status"] == "ok"
        assert mock_get.called

@pytest.mark.asyncio
async def test_ask_index_status_error():
    with patch("httpx.AsyncClient.get") as mock_get:
        mock_get.side_effect = Exception("Connection Failed")
        
        with pytest.raises(HTTPException) as exc:
            await ask_index_status(device=MagicMock())
            
        assert exc.value.status_code == 503

@pytest.mark.asyncio
async def test_ask_reindex():
    with patch("httpx.AsyncClient.post") as mock_post:
        mock_resp = MagicMock()
        mock_resp.json.return_value = {"status": "indexing_started"}
        mock_resp.raise_for_status.return_value = None
        mock_post.return_value = mock_resp
        
        res = await ask_reindex(device=MagicMock())
        assert res["status"] == "indexing_started"
