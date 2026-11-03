"""Proxy to jv-jobs, the ai-job-search runner (12-Job-Search-Integration.md).

Admin-only: runs spend the owner's Claude subscription and read their career
data, so guest devices never reach it. jv-jobs is internal-only and also
checks a shared secret, which only this proxy holds.
"""
import httpx
from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import StreamingResponse
from starlette.background import BackgroundTask

from app.config import settings
from app.dependencies import require_admin

router = APIRouter(tags=["jobs"])


def _client() -> httpx.AsyncClient:
    # No read timeout: /runs/{id}/events streams for as long as a run lasts.
    return httpx.AsyncClient(timeout=httpx.Timeout(30.0, read=None))


# Out of the OpenAPI schema: one catch-all route for two methods would give two
# operations the same id, and jv-jobs documents its own endpoints.
@router.api_route("/jobs/{rest:path}", methods=["GET", "POST"], include_in_schema=False)
async def jobs_proxy(rest: str, request: Request, _admin: dict = Depends(require_admin)) -> StreamingResponse:
    client = _client()
    upstream = client.build_request(
        request.method,
        f"{settings.jobs_url.rstrip('/')}/{rest}",
        params=request.query_params,
        content=await request.body(),
        headers={
            "X-Jobs-Secret": settings.jobs_secret,
            "Content-Type": request.headers.get("content-type", "application/json"),
        },
    )
    try:
        response = await client.send(upstream, stream=True)
    except httpx.RequestError:
        await client.aclose()
        raise HTTPException(
            status_code=503,
            detail="Job engine is not running. Start it with: docker compose --profile jobs up -d",
        )

    async def close() -> None:
        await response.aclose()
        await client.aclose()

    headers = {k: v for k in ("content-type", "content-disposition") if (v := response.headers.get(k))}
    return StreamingResponse(
        response.aiter_bytes(), status_code=response.status_code, headers=headers, background=BackgroundTask(close)
    )
