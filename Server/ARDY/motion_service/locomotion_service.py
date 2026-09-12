"""Bounded, cancellable room jobs sharing the existing backend and feature provider."""
from __future__ import annotations

import asyncio
import hashlib
import logging
import threading
import time
from collections import deque
from dataclasses import dataclass, field

from .locomotion_backend import generate_room_motion
from .protocol import ServiceError


@dataclass
class RoomJob:
    request: object
    fingerprint: str
    future: asyncio.Future
    submitted: float
    deadline: float
    cancelled: threading.Event = field(default_factory=threading.Event)
    feature_task: asyncio.Task | None = None


class RoomLocomotionService:
    def __init__(self, shared, generator=generate_room_motion, queue_limit=4, character_limit=64):
        self.shared, self.generator = shared, generator
        self.queue_limit, self.character_limit = queue_limit, character_limit
        self.states, self.queue = {}, deque()
        self.wake, self.worker, self.running = asyncio.Event(), None, None
        self.closed = False

    async def start(self):
        self.worker = asyncio.create_task(self._worker())

    async def close(self):
        self.closed = True
        for state in self.states.values():
            if state[1] is not None:
                self.abort(state[1], ServiceError(503, "service_stopping", "Room service is stopping"))
        self.wake.set()
        if self.worker:
            # Never abandon a running to_thread inference or release its backend lock early.
            await self.worker

    def submit(self, request):
        if self.closed or self.shared.closed or not self.shared.backend.info.get("ready", False):
            raise ServiceError(503, "not_ready", "Room service is not ready")
        state = self.states.get(request.characterId)
        fingerprint = hashlib.sha256(request.model_dump_json().encode()).hexdigest()
        if state is not None:
            revision, old = state
            if request.revision < revision or (request.revision == revision and old is None):
                raise ServiceError(409, "stale_revision", "Use a revision newer than the cancellation")
            if request.revision == revision:
                if old.fingerprint != fingerprint:
                    raise ServiceError(409, "revision_conflict", "A revision identifies one immutable room request")
                return old
        elif len(self.states) >= self.character_limit:
            raise ServiceError(429, "character_limit", "Room character/tombstone capacity reached")
        self.queue = deque(j for j in self.queue if not j.future.done())
        reclaimable = state is not None and state[1] in self.queue
        if len(self.queue)-int(reclaimable) >= self.queue_limit:
            raise ServiceError(429, "queue_full", "Room generation queue is full")
        if state is not None and state[1] is not None:
            self.abort(state[1], ServiceError(409, "superseded", "A newer room route replaced this request"))
        now = time.monotonic()
        job = RoomJob(request, fingerprint, asyncio.get_running_loop().create_future(), now, now+request.timeoutMs/1000)
        self.states[request.characterId] = (request.revision, job)
        self.queue.append(job)
        self.wake.set()
        return job

    def cancel(self, request):
        state = self.states.get(request.characterId)
        if state is not None and request.revision < state[0]:
            return {"cancelled": False, "revision": state[0]}
        if state is None and len(self.states) >= self.character_limit:
            raise ServiceError(429, "character_limit", "Room character/tombstone capacity reached")
        if state is not None and state[1] is not None:
            if request.revision == state[0] and request.requestId and request.requestId != state[1].request.requestId:
                raise ServiceError(409, "request_mismatch", "Cancellation requestId does not match")
            self.abort(state[1], ServiceError(409, "cancelled_revision", "Room route cancelled"))
        self.states[request.characterId] = (request.revision, None)
        return {"cancelled": True, "revision": request.revision}

    def abort(self, job, error):
        job.cancelled.set()
        if job.feature_task is not None:
            job.feature_task.cancel()
        if not job.future.done():
            job.future.set_exception(ServiceError(error.status, error.code, error.message))
            job.future.exception()

    def _valid(self, job):
        return (not job.cancelled.is_set() and not job.future.done() and
                self.states.get(job.request.characterId) == (job.request.revision, job))

    async def _worker(self):
        while not self.closed:
            if not self.queue:
                self.wake.clear()
                await self.wake.wait()
                continue
            job = self.queue.popleft()
            self.running = job
            began = time.monotonic()
            try:
                if not self._valid(job):
                    continue
                remaining = job.deadline-time.monotonic()
                if remaining <= 0:
                    raise ServiceError(504, "deadline_exceeded", "Room deadline expired in queue")
                feature_start = time.monotonic()
                job.feature_task = asyncio.create_task(self.shared.provider.fetch(job.request, remaining))
                try:
                    feature = await asyncio.wait_for(job.feature_task, timeout=remaining)
                finally:
                    job.feature_task = None
                feature_ms = (time.monotonic()-feature_start)*1000
                async with self.shared.backend_gate:
                    if not self._valid(job):
                        continue
                    if time.monotonic() >= job.deadline:
                        raise ServiceError(504, "deadline_exceeded", "Room deadline expired waiting for ARDY")
                    result = await asyncio.to_thread(self.generator, self.shared.backend, job.request,
                                                    feature["embedding"], job.cancelled)
                if not self._valid(job):
                    continue
                if time.monotonic() >= job.deadline:
                    raise ServiceError(504, "deadline_exceeded", "Room deadline expired during generation")
                result.setdefault("timings", {}).update(queueMs=(began-job.submitted)*1000,
                    featureMs=feature_ms, totalMs=(time.monotonic()-job.submitted)*1000)
                result.setdefault("source", {}).update(featureSource=feature["source"],
                    featureContract=feature.get("featureContract"), modelSha256=feature["modelSha256"],
                    featureRequestId=feature["featureRequestId"])
                job.future.set_result(result)
            except asyncio.CancelledError:
                if not job.cancelled.is_set():
                    raise
            except asyncio.TimeoutError:
                self.abort(job, ServiceError(504, "deadline_exceeded", "Room feature request timed out"))
            except ServiceError as error:
                self.abort(job, error)
            except Exception as error:
                logging.getLogger(__name__).exception("Room generation failed")
                self.abort(job, ServiceError(500, "generation_failed", type(error).__name__))
            finally:
                self.running = None
