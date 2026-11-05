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


# --- proposal inbox proxy -----------------------------------------------------------------

from app.routers.ask import ask_count_proposals, ask_decide_proposal, ask_list_proposals  # noqa: E402


def _brain_response(payload, status=200):
    import httpx

    request = httpx.Request("GET", "http://brain")
    return httpx.Response(status, json=payload, request=request)


@pytest.mark.asyncio
async def test_list_and_count_proposals_forward_to_brain():
    with patch("httpx.AsyncClient.request", new=AsyncMock(return_value=_brain_response({"proposals": []}))) as req:
        assert await ask_list_proposals(device={"device_id": "d"}) == {"proposals": []}
        assert req.call_args.args[0] == "GET" and req.call_args.args[1].endswith("/brain/proposals")
    with patch("httpx.AsyncClient.request", new=AsyncMock(return_value=_brain_response({"pending": 3}))):
        assert await ask_count_proposals(device={"device_id": "d"}) == {"pending": 3}


@pytest.mark.asyncio
async def test_decision_is_forwarded_and_audited_without_content(tmp_path):
    request = MagicMock(spec=Request)
    request.json = AsyncMock(return_value={"approve": True})
    result = {"id": "p1", "status": "done", "result": "SECRET FILE CONTENT"}
    with patch("httpx.AsyncClient.request", new=AsyncMock(return_value=_brain_response(result))), \
            patch("app.routers.ask.audit.record") as record:
        out = await ask_decide_proposal("p1", request, device={"device_id": "phone"})
    assert out["status"] == "done"
    record.assert_called_once_with("proposal.decision", proposal_id="p1", approve=True, status="done", device_id="phone")


@pytest.mark.asyncio
async def test_decision_requires_a_boolean_and_maps_404():
    bad = MagicMock(spec=Request)
    bad.json = AsyncMock(return_value={"approve": "yes"})
    with pytest.raises(HTTPException) as exc:
        await ask_decide_proposal("p1", bad, device={})
    assert exc.value.status_code == 422

    good = MagicMock(spec=Request)
    good.json = AsyncMock(return_value={"approve": False})
    with patch("httpx.AsyncClient.request", new=AsyncMock(return_value=_brain_response({}, status=404))):
        with pytest.raises(HTTPException) as exc:
            await ask_decide_proposal("nope", good, device={})
    assert exc.value.status_code == 404


@pytest.mark.asyncio
async def test_digest_run_forwards_force_flag_and_is_admin_gated():
    from app.dependencies import require_admin
    from app.routers.ask import ask_run_digest

    with patch("httpx.AsyncClient.request", new=AsyncMock(return_value=_brain_response({"written": "Digest/x.md"}))) as req:
        out = await ask_run_digest(force=True, device={"is_secrets_authorized": True})
    assert out == {"written": "Digest/x.md"} and req.call_args.kwargs["params"] == {"force": "true"}

    with pytest.raises(HTTPException) as exc:  # a non-admin device is refused by the dependency
        require_admin({"is_secrets_authorized": False})
    assert exc.value.status_code == 403
