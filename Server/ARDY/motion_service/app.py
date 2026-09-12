"""Run with python -m Server.ARDY.motion_service.app (loopback only)."""
from __future__ import annotations

import argparse
import asyncio
import time
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from .backend import ArdyBackend, ROOT
from .protocol import CancelRequest, GenerateRequest, ServiceError
from .service import FixtureFeatureProvider, LiveFeatureProvider, MotionService
from .locomotion_service import RoomLocomotionService
from .locomotion_protocol import LocomotionRequest, LocomotionCancel
from .locomotion_backend import capabilities


def create_app(service):
    room = RoomLocomotionService(service)
    @asynccontextmanager
    async def lifespan(app):
        await service.start()
        await room.start()
        try:
            yield
        finally:
            await room.close()
            await service.close()

    app = FastAPI(title="NeEEvA local ARDY motion", lifespan=lifespan)
    app.state.motion_service = service
    app.state.room_service = room

    @app.exception_handler(ServiceError)
    async def service_error(request, error):
        return JSONResponse(error.payload(), status_code=error.status)

    @app.exception_handler(RequestValidationError)
    async def validation_error(request, error):
        return JSONResponse({"error": {"code": "invalid_request", "message": "Motion request violates the fixed schema",
                                      "fields": [".".join(map(str, e["loc"])) for e in error.errors()]}}, status_code=400)

    @app.middleware("http")
    async def bounded_body(request, call_next):
        # JSON history is below 40 KiB. Reject large/chunked payloads before parsing.
        if request.method == "POST":
            header = request.headers.get("content-length")
            try:
                if header is not None and int(header) > 131072:
                    return JSONResponse({"error": {"code": "body_too_large", "message": "Maximum body is 128 KiB"}}, status_code=413)
            except ValueError:
                return JSONResponse({"error": {"code": "invalid_content_length", "message": "Invalid content length"}}, status_code=400)
            body = bytearray()
            async for block in request.stream():
                body.extend(block)
                if len(body) > 131072:
                    return JSONResponse({"error": {"code": "body_too_large", "message": "Maximum body is 128 KiB"}}, status_code=413)
            request._body = bytes(body)
        return await call_next(request)

    @app.get("/health")
    async def health():
        value = service.health()
        return JSONResponse(value, status_code=200 if value["ready"] else 503)

    @app.post("/v1/motion/cancel")
    async def cancel(value: CancelRequest):
        return service.cancel(value)

    @app.get("/v1/locomotion/capabilities")
    async def room_capabilities():
        return capabilities(service.backend)

    @app.post("/v1/locomotion/cancel")
    async def room_cancel(value: LocomotionCancel):
        return room.cancel(value)

    @app.post("/v1/locomotion/generate")
    async def room_generate(value: LocomotionRequest, request: Request):
        return await await_job(room, room.submit(value), request)

    async def await_job(owner, job, request):
        try:
            while not job.future.done():
                if await request.is_disconnected():
                    owner.abort(job, ServiceError(499, "client_disconnected", "The motion client disconnected"))
                    break
                remaining = job.deadline-time.monotonic()
                if remaining <= 0:
                    owner.abort(job, ServiceError(504, "deadline_exceeded", "Motion request exceeded its deadline"))
                    break
                await asyncio.wait({job.future}, timeout=min(0.05, remaining))
            return job.future.result()
        except asyncio.CancelledError:
            owner.abort(job, ServiceError(499, "client_disconnected", "The motion request was cancelled"))
            raise

    @app.post("/v1/motion/generate")
    async def generate(value: GenerateRequest, request: Request):
        return await await_job(service, service.submit(value), request)

    return app


def adapter_configuration(args, root=ROOT):
    """Explicit modes override active preview; invalid active files never fall back."""
    if args.baseline_adapter:
        return None, None
    if args.adapter_selection is not None:
        return args.adapter_selection, None
    active = root / "Server/ARDY/runtime/active-adapter-release.json"
    release = args.adapter_release if args.adapter_release is not None else (active if active.exists() else None)
    return None, release


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", type=int, default=8093)
    parser.add_argument("--feature-url", default="http://127.0.0.1:8080")
    parser.add_argument("--expected-model-sha256")
    parser.add_argument("--device", default="cuda")
    adapters = parser.add_mutually_exclusive_group()
    adapters.add_argument("--adapter-selection", type=Path,
                         help="Explicit isolated test of a locked eligible selection.json")
    adapters.add_argument("--adapter-release", type=Path, help="Validated evidence-bound preview release; allowed on port 8093")
    adapters.add_argument("--baseline-adapter", action="store_true", help="Explicitly use the preserved original baseline, ignoring any active release")
    parser.add_argument("--queue-limit", type=int, default=4)
    parser.add_argument("--character-limit", type=int, default=64)
    parser.add_argument("--test-fixture", action="store_true", help="Explicit offline protocol testing only, never claims live Qwen")
    args = parser.parse_args()
    if args.adapter_selection is not None and (args.port == 8093 or args.test_fixture):
        parser.error("Candidate testing requires a separate explicit --port and live Qwen features (no --test-fixture)")
    selection, release = adapter_configuration(args)
    if release is not None and args.test_fixture:
        parser.error("Verified preview releases require live Qwen features; use --baseline-adapter for cache fixtures")
    if args.test_fixture:
        provider = FixtureFeatureProvider(ROOT / "Tools/MotionAdapter/runtime/qwen-2000.npz",
                                          ROOT / "Tools/MotionAdapter/runtime/prompts.jsonl")
    else:
        provider = LiveFeatureProvider(args.feature_url, args.expected_model_sha256)
    service = MotionService(ArdyBackend(args.device, adapter_selection=selection, adapter_release=release), provider,
                            args.queue_limit, args.character_limit)
    import uvicorn
    uvicorn.run(create_app(service), host="127.0.0.1", port=args.port, workers=1,
                limit_concurrency=32, timeout_keep_alive=5)


if __name__ == "__main__":
    main()
