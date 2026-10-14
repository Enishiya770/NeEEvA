"""Bounded continuous sessions sharing one Qwen feature and the existing ARDY gate.

A completed response is a candidate, not proof of Unity playback. The next step's
previousRequestId explicitly acknowledges that candidate's commitFrames prefix.
Only that prefix becomes history. A rejected/late candidate requires cancellation
and a fresh revision with actual history; no implicit advance or tail commitment.
"""
from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import threading
import time
from collections import deque
from dataclasses import dataclass, field

from .locomotion_stream_backend import generate_stream_step
from .locomotion_stream_protocol import STREAM_PROTOCOL, check_seam
from .protocol import ServiceError


@dataclass
class StreamJob:
    request: object
    fingerprint: str
    future: asyncio.Future
    submitted: float
    deadline: float
    context: dict
    feature: dict | None
    cancelled: threading.Event = field(default_factory=threading.Event)
    feature_task: asyncio.Task | None = None
    candidate: dict | None = None
    stage: str = "queued"
    stage_started: float = 0.
    stage_ms: dict = field(default_factory=dict)
    generation_started: bool = False


@dataclass
class StreamState:
    revision: int
    session_id: str
    description: str | None = None
    seed: int = 0
    head: StreamJob | None = None


class RoomStreamService:
    def __init__(self, shared, generator=generate_stream_step, queue_limit=4, character_limit=64):
        self.shared, self.generator = shared, generator
        self.queue_limit, self.character_limit = queue_limit, character_limit
        self.states, self.queue = {}, deque()
        self.wake, self.worker = asyncio.Event(), None
        self.closed = False

    async def start(self):
        self.worker = asyncio.create_task(self._worker())

    async def close(self):
        self.closed = True
        for state in self.states.values():
            if state.head is not None:
                self.abort(state.head, ServiceError(503, "service_stopping", "Stream service is stopping"))
        self.wake.set()
        if self.worker:
            await self.worker  # Never unlock an in-flight to_thread GPU call early.
        self.states.clear()

    def submit(self, request):
        if self.closed or self.shared.closed or not self.shared.backend.info.get("ready", False):
            raise ServiceError(503, "not_ready", "ARDY stream is not ready")
        state = self.states.get(request.characterId)
        fingerprint = hashlib.sha256(request.model_dump_json().encode()).hexdigest()
        context, feature = {}, None
        replacing = state is None or request.revision > state.revision
        if state is not None and request.revision < state.revision:
            raise ServiceError(409, "stale_revision", "Stream revision is older than the current session")
        if state is None and len(self.states) >= self.character_limit:
            raise ServiceError(429, "character_limit", "Stream character/tombstone capacity reached")
        if not replacing:
            if request.sessionId != state.session_id:
                raise ServiceError(409, "session_mismatch", "A revision belongs to one stream session")
            old = state.head
            if old is None:
                raise ServiceError(409, "cancelled_revision", "Restart a cancelled stream with a newer revision")
            if request.stepIndex == old.request.stepIndex:
                if fingerprint != old.fingerprint:
                    raise ServiceError(409, "request_conflict", "A stream step is immutable; replay its exact request")
                return old
            if request.stepIndex != old.request.stepIndex + 1:
                raise ServiceError(409, "step_out_of_order", "Expected the next consecutive stream step")
            if not old.future.done() or old.cancelled.is_set() or old.candidate is None:
                raise ServiceError(409, "previous_step_unavailable", "Wait for and audit the previous step before continuing")
            if request.previousRequestId != old.request.requestId:
                raise ServiceError(409, "acknowledgement_mismatch", "previousRequestId must acknowledge the latest candidate")
            if request.requestId == old.request.requestId:
                raise ServiceError(409, "request_conflict", "Each new step needs a distinct requestId")
            if request.startFrame != old.request.startFrame + old.request.commitFrames:
                raise ServiceError(409, "frame_out_of_order", "startFrame must follow only the acknowledged prefix")
            if request.controlRevision < old.request.controlRevision:
                raise ServiceError(409, "stale_control", "Control revisions cannot move backwards")
            if request.seed != state.seed or (request.description is not None and request.description != state.description):
                raise ServiceError(409, "session_contract_changed", "Changing text or seed needs a fresh stream revision")
            try:
                check_seam(request.targets[0], old.candidate["seam"])
            except ValueError as error:
                raise ServiceError(400, "invalid_seam", str(error)) from error
            context = {"history": old.candidate["history"], "condition": old.candidate.get("condition"),
                       "description": state.description}
            feature = old.feature
        else:
            if request.stepIndex != 0:
                raise ServiceError(409, "missing_session", "A new revision must start with step zero and actual history")
            context = {"description": request.description}
        self.queue = deque(j for j in self.queue if not j.future.done())
        reclaim = state is not None and state.head is not None and state.head in self.queue
        if len(self.queue)-int(reclaim) >= self.queue_limit:
            raise ServiceError(429, "queue_full", "Stream queue is full")
        if replacing:
            if state is not None and state.head is not None:
                self.abort(state.head, ServiceError(409, "superseded", "A newer stream revision replaced this session"))
            state = StreamState(request.revision, request.sessionId, request.description, request.seed)
            self.states[request.characterId] = state
        now = time.monotonic()
        job = StreamJob(request, fingerprint, asyncio.get_running_loop().create_future(), now,
                        now+request.timeoutMs/1000, context, feature)
        job.stage_started = now
        job.future.add_done_callback(lambda future: self._terminal_trace(job, future))
        state.head = job
        self.queue.append(job)
        self.wake.set()
        return job

    def cancel(self, request):
        state = self.states.get(request.characterId)
        logging.getLogger("uvicorn.error.ardy.stream").info("[ARDY stream cancel] %s", json.dumps(dict(
            characterId=request.characterId, sessionId=request.sessionId, revision=request.revision,
            currentSessionId=state.session_id if state is not None else None,
            currentRevision=state.revision if state is not None else None,
            requestId=state.head.request.requestId if state is not None and state.head is not None else None,
            alreadyCompleted=state.head.future.done() if state is not None and state.head is not None else None), separators=(",", ":")))
        if state is not None:
            if request.revision < state.revision:
                return {"cancelled": False, "revision": state.revision, "sessionId": state.session_id}
            if request.revision == state.revision and request.sessionId != state.session_id:
                raise ServiceError(409, "session_mismatch", "Cancellation belongs to another stream session")
            if state.head is not None:
                self.abort(state.head, ServiceError(409, "cancelled_revision", "Stream was cancelled"))
        elif len(self.states) >= self.character_limit:
            raise ServiceError(429, "character_limit", "Stream character/tombstone capacity reached")
        self.states[request.characterId] = StreamState(request.revision, request.sessionId)
        return {"cancelled": True, "revision": request.revision, "sessionId": request.sessionId}

    def abort(self, job, error):
        job.cancelled.set()
        job.candidate = None
        if job.feature_task is not None:
            job.feature_task.cancel()
        if not job.future.done():
            # A caller's deadline includes feature queueing, the backend gate and generation.
            # Preserve the measured stage even when the HTTP waiter wins the timeout race.
            # Never label feature waiting as Qwen compute or infer work from a missing response.
            diagnostic = self._diagnostic(job)
            details = error.details
            if error.status >= 500:
                details = dict(details or {})
                details["streamDiagnostic"] = diagnostic
            job.future.set_exception(ServiceError(error.status, error.code, error.message, details))
            job.future.exception()
            if error.status >= 500:
                logging.getLogger(__name__).warning("[ARDY stream failure] %s", json.dumps(
                    dict(diagnostic, status=error.status, code=error.code), separators=(",", ":")))

    def _terminal_trace(self, job, future):
        diagnostic = self._diagnostic(job)
        if future.cancelled():
            diagnostic.update(outcome="future-cancelled")
        else:
            error = future.exception()
            if error is not None:
                diagnostic.update(outcome="failed-or-cancelled", code=getattr(error, "code", type(error).__name__),
                                  status=getattr(error, "status", None))
            else:
                diagnostic.update(outcome="completed", timings=future.result().get("timings"))
        # All endings, including 409 cancellation, retain the same request/session identity.
        # Client disconnect/cancel does not imply the GPU computation never completed.
        # Uvicorn configures this hierarchy at INFO; the default root logger is
        # WARNING and would otherwise silently discard successful/cancel traces.
        logging.getLogger("uvicorn.error.ardy.stream").info("[ARDY stream terminal] %s", json.dumps(diagnostic, separators=(",", ":")))

    @staticmethod
    def _stage(job, stage):
        now = time.monotonic()
        job.stage_ms[job.stage] = job.stage_ms.get(job.stage, 0.) + max(0., now-job.stage_started)*1000
        job.stage, job.stage_started = stage, now

    @staticmethod
    def _diagnostic(job):
        now = time.monotonic()
        elapsed = dict(job.stage_ms)
        elapsed[job.stage] = elapsed.get(job.stage, 0.) + max(0., now-job.stage_started)*1000
        return {"requestId": job.request.requestId, "sessionId": job.request.sessionId,
                "revision": job.request.revision, "stepIndex": job.request.stepIndex,
                "stage": job.stage, "deadlineMs": job.request.timeoutMs,
                "elapsedMs": round(max(0., now-job.submitted)*1000, 3),
                "stageElapsedMs": {key: round(value, 3) for key, value in elapsed.items()},
                "featureResolved": job.feature is not None, "generationStarted": job.generation_started,
                "scope": "Local elapsed waits; feature-wait includes transport and remote scheduling, not measured remote GPU compute. Generation started does not prove a candidate was completed or played."}

    def _valid(self, job):
        state = self.states.get(job.request.characterId)
        return (not job.cancelled.is_set() and not job.future.done() and state is not None and state.head is job and
                state.revision == job.request.revision and state.session_id == job.request.sessionId)

    async def _worker(self):
        while not self.closed:
            if not self.queue:
                self.wake.clear()
                await self.wake.wait()
                continue
            job = self.queue.popleft()
            began = time.monotonic()
            try:
                if not self._valid(job):
                    continue
                feature_ms = 0.
                reused = job.feature is not None
                if not reused:
                    remaining = job.deadline-time.monotonic()
                    if remaining <= 0:
                        raise ServiceError(504, "deadline_exceeded", "Stream deadline expired in queue")
                    self._stage(job, "feature-wait")
                    feature_start = time.monotonic()
                    job.feature_task = asyncio.create_task(self.shared.provider.fetch(job.request, remaining))
                    try:
                        job.feature = await asyncio.wait_for(job.feature_task, timeout=remaining)
                    finally:
                        job.feature_task = None
                    feature_ms = (time.monotonic()-feature_start)*1000
                self._stage(job, "backend-gate")
                gate_start = time.monotonic()
                async with self.shared.backend_gate:
                    gate_ms = (time.monotonic()-gate_start)*1000
                    if not self._valid(job):
                        continue
                    if time.monotonic() >= job.deadline:
                        raise ServiceError(504, "deadline_exceeded", "Stream deadline expired waiting for ARDY")
                    self._stage(job, "generation")
                    job.generation_started = True
                    candidate = await asyncio.to_thread(self.generator, self.shared.backend, job.request,
                        job.feature["embedding"], job.context, job.cancelled)
                if not self._valid(job):
                    continue
                if time.monotonic() >= job.deadline:
                    raise ServiceError(504, "deadline_exceeded", "Stream deadline expired during generation")
                result = candidate["clip"]
                value = job.request
                result.update(streamProtocol=STREAM_PROTOCOL, characterId=value.characterId, revision=value.revision,
                    sessionId=value.sessionId, requestId=value.requestId, stepIndex=value.stepIndex,
                    startFrame=value.startFrame, commitFrames=value.commitFrames, generationFrames=40,
                    controlRevision=value.controlRevision, previousRequestId=value.previousRequestId,
                    nextStartFrame=value.startFrame+value.commitFrames,
                    requiresClientAudit=True)
                result.setdefault("source", {}).update(featureSource=job.feature["source"],
                    featureContract=job.feature.get("featureContract"), modelSha256=job.feature["modelSha256"],
                    featureRequestId=job.feature["featureRequestId"], conditionReusedWithinSession=reused,
                    historyAcknowledgementRequestId=value.previousRequestId,
                    historyMeaning="Only the client-acknowledged prefix is available to the next step; prediction tail is not committed")
                result.setdefault("timings", {}).update(queueMs=(began-job.submitted)*1000,
                    featureMs=feature_ms, backendGateMs=gate_ms, totalMs=(time.monotonic()-job.submitted)*1000)
                job.candidate = candidate
                job.context = {}  # Retain only one completed candidate, not a chain of old histories.
                job.future.set_result(result)
            except asyncio.CancelledError:
                if not job.cancelled.is_set():
                    raise
            except asyncio.TimeoutError:
                self.abort(job, ServiceError(504, "deadline_exceeded", "Stream feature request timed out"))
            except ServiceError as error:
                self.abort(job, error)
            except Exception as error:
                logging.getLogger(__name__).exception("Stream generation failed")
                self.abort(job, ServiceError(500, "generation_failed", type(error).__name__))
