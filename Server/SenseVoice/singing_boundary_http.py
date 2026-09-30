"""HTTP adapter for immutable-source local singing boundary refinement."""
import asyncio
import hashlib
import json

from fastapi import File, Form, Request, UploadFile
from fastapi.responses import JSONResponse

from singing_boundary_refinement import (ALGORITHM, BoundaryRequest, BoundaryError,
                                         BoundaryCancelled, refine_boundary)

MAX_UPLOAD_BYTES = 16 * 1024 * 1024


class BoundaryHttpService:
    def __init__(self, decode, recognize, scheduler, ready):
        self.decode, self.recognize, self.scheduler, self.ready = decode, recognize, scheduler, ready
        self.active = {}

    def cancel(self, request_id):
        state = self.active.get(request_id)
        if state is None:
            return False
        state["cancelled"] = True
        if state["child"]:
            self.scheduler.cancel(state["child"])
        return True

    @staticmethod
    def failure(reason, status=400, request=None, detail=""):
        body = request.envelope() if request else dict(schema=1, algorithm_version=ALGORITHM,
                ok=False, status="boundary_unresolved", range=None, quality="unresolved", word_timestamps=False)
        body.update(reason=reason, detail=detail)
        return JSONResponse(body, status_code=status)

    async def handle(self, audio_file, manifest, http_request=None):
        request, state = None, None
        try:
            if not self.ready():
                return self.failure("model_not_ready", 503)
            if not isinstance(manifest, str) or len(manifest) > 8192:
                raise BoundaryError("invalid_manifest", "manifest must be bounded JSON")
            try:
                payload = json.loads(manifest)
            except (ValueError, TypeError):
                raise BoundaryError("invalid_manifest", "manifest is not valid JSON")
            data = await audio_file.read(MAX_UPLOAD_BYTES + 1)
            if not data or len(data) > MAX_UPLOAD_BYTES:
                raise BoundaryError("invalid_audio", "empty or oversized WAV upload")
            digest = hashlib.sha256(data).hexdigest()
            try:
                wav = await asyncio.to_thread(self.decode, data)
            except Exception:
                raise BoundaryError("invalid_audio", "cannot decode the uploaded WAV")
            request = BoundaryRequest.parse(payload, digest, len(wav))
            if request.request_id in self.active:
                return self.failure("duplicate_request_id", 409, request)
            if len(self.active) >= 2:
                return self.failure("analysis_busy", 429, request)
            state = dict(cancelled=False, child="", count=0)
            self.active[request.request_id] = state

            def cancelled():
                return state["cancelled"] or self.scheduler.was_cancelled(request.request_id)

            async def recognize(piece):
                if cancelled() or (http_request is not None and await http_request.is_disconnected()):
                    raise BoundaryCancelled()
                state["count"] += 1
                child = request.request_id + "/boundary-" + str(state["count"])
                state["child"] = child
                # Individual bounded probes yield to normal final ASR at each boundary.
                # The same model lock is reused; no unsafe concurrent generate calls.
                job = self.scheduler.submit(self.recognize, (piece, request.language), child, "", True)
                try:
                    return await asyncio.wait_for(self.scheduler.wait(job), timeout=12)
                finally:
                    if not job.done():
                        self.scheduler.cancel(child)
                    state["child"] = ""

            try:
                result = await asyncio.wait_for(refine_boundary(wav, request, recognize, cancelled), timeout=30)
                return result
            except asyncio.TimeoutError:
                self.cancel(request.request_id)
                return self.failure("analysis_budget_exhausted", 200, request, "local analysis deadline reached")
        except BoundaryError as exc:
            return self.failure(exc.reason, 400, request, str(exc))
        finally:
            if request is not None and state is not None:
                self.cancel(request.request_id)
                self.active.pop(request.request_id, None)


def install_boundary_routes(app, decode, recognize, scheduler, ready):
    service = BoundaryHttpService(decode, recognize, scheduler, ready)

    @app.post("/singing/refine-boundary")
    async def refine(http_request: Request, audio_file: UploadFile = File(...), manifest: str = Form(...)):
        return await service.handle(audio_file, manifest, http_request)

    return service
