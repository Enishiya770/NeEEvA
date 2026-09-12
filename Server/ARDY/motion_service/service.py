"""One bounded worker, revision tombstones, and atomic history/RNG commits."""
from __future__ import annotations

import asyncio
import hashlib
import json
import time
from collections import deque
from dataclasses import dataclass, field

import httpx
import numpy as np

from .protocol import FEATURE_CONTRACT, FEATURE_TEMPLATE, QWEN_MODEL_SHA256, GenerateRequest, ServiceError


class LiveFeatureProvider:
    source = "live-qwen"

    def __init__(self, base_url="http://127.0.0.1:8080", expected_model_sha256=None):
        self.base_url = base_url.rstrip("/")
        self.expected_model_sha256 = (expected_model_sha256 or QWEN_MODEL_SHA256).lower()
        if self.expected_model_sha256 != QWEN_MODEL_SHA256:
            raise ValueError("The selected adapter requires its original Qwen model SHA256")
        self.info = {"source": self.source, "verified": False, "baseUrl": self.base_url}

    async def fetch(self, request, remaining):
        payload = {"text": request.description, "request_id": request.requestId,
                   "feature_contract": FEATURE_CONTRACT}
        try:
            async with httpx.AsyncClient(timeout=remaining) as client:
                response = await client.post(self.base_url + "/neeeva/motion-features", json=payload)
        except httpx.TimeoutException as error:
            raise ServiceError(504, "feature_timeout", "Shared Qwen feature request exceeded the deadline") from error
        except httpx.HTTPError as error:
            raise ServiceError(503, "feature_unavailable", "Shared Qwen feature route is unavailable") from error
        if response.status_code != 200:
            status = response.status_code if response.status_code in (429, 503, 504) else 502
            raise ServiceError(status, "feature_rejected", f"Shared Qwen feature route returned HTTP {response.status_code}")
        try:
            value = response.json()
            embedding = np.asarray(value["embedding"], dtype=np.float32)
            actual_hash = value["model_sha256"].lower()
            checks = (
                value["dimension"] == 2048, embedding.shape == (2048,), np.isfinite(embedding).all(),
                np.linalg.norm(embedding) > 1e-8, value["feature_contract"] == FEATURE_CONTRACT,
                value["feature_source"] == "live-qwen", value["protocol"] == "qwen-motion-raw-last-v1",
                value["request_id"] == request.requestId, value["text"] == request.description,
                value["pooling"] == "last_input_token_raw", value["template"] == FEATURE_TEMPLATE,
                value["shared_model"] is True, value["context_tokens"] == 512,
                isinstance(actual_hash, str) and len(actual_hash) == 64 and all(c in "0123456789abcdef" for c in actual_hash),
                actual_hash == self.expected_model_sha256,
            )
            if not all(checks):
                raise ValueError("Feature protocol, model, request binding or dimensions differ")
        except (KeyError, ValueError, TypeError, OverflowError, AttributeError) as error:
            raise ServiceError(502, "feature_contract_mismatch", str(error)) from error
        self.info = {"source": self.source, "verified": True, "baseUrl": self.base_url,
                     "featureContract": FEATURE_CONTRACT, "modelSha256": actual_hash}
        return {"embedding": embedding, "source": self.source, "modelSha256": actual_hash,
                "featureRequestId": request.requestId, "featureContract": FEATURE_CONTRACT}


class FixtureFeatureProvider:
    """Explicit test fixture: cannot be mistaken for a live language-model call."""
    source = "test-cache-fixture"

    def __init__(self, path, records_path):
        from Tools.MotionAdapter.data import read_bundle, read_records
        records = read_records(records_path)
        embeddings, meta = read_bundle(path, records, "qwen")
        if meta["feature_contract"] != FEATURE_CONTRACT:
            raise ValueError("Test fixture feature contract mismatch")
        self.by_text = {row["text"]: embedding for row, embedding in zip(records, embeddings)}
        self.info = {"source": self.source, "verified": True, "live": False}

    async def fetch(self, request, remaining):
        if request.description not in self.by_text:
            raise ServiceError(400, "fixture_text_missing", "Text is absent from the explicitly configured test fixture")
        return {"embedding": self.by_text[request.description].copy(), "source": self.source,
                "modelSha256": None, "featureRequestId": request.requestId, "featureContract": FEATURE_CONTRACT}


@dataclass
class CharacterState:
    revision: int
    turn: str
    cancelled: bool = False
    next_chunk: int = 0
    description: str | None = None
    seed: int = 0
    feature: dict | None = None
    backend_state: dict = field(default_factory=dict)
    job: object | None = None
    last_request_id: str | None = None
    last_fingerprint: str | None = None
    last_result: dict | None = None


@dataclass
class Job:
    request: GenerateRequest
    state: CharacterState
    fingerprint: str
    submitted: float
    deadline: float
    future: asyncio.Future
    cancelled: bool = False
    started: float | None = None
    feature_task: asyncio.Task | None = None


class MotionService:
    def __init__(self, backend, provider, queue_limit=4, character_limit=64):
        if not 1 <= queue_limit <= 64 or not 1 <= character_limit <= 1024:
            raise ValueError("Queue and character limits must be bounded")
        self.backend, self.provider = backend, provider
        # Shared by upper-body and room workers: diffusion and global RNG are mutable.
        self.backend_gate = asyncio.Lock()
        self.queue_limit, self.character_limit = queue_limit, character_limit
        self.characters = {}
        self.queue = deque()
        self.wake = asyncio.Event()
        self.worker = None
        self.running = None
        self.closed = False
        self.counts = {"submitted": 0, "completed": 0, "cancelled": 0, "failed": 0,
                       "staleResultsDiscarded": 0, "queueRejected": 0}

    async def start(self):
        await asyncio.to_thread(self.backend.load)
        self.worker = asyncio.create_task(self._worker())

    async def close(self):
        self.closed = True
        for state in self.characters.values():
            self._invalidate(state, "service_stopping")
        self.wake.set()
        if self.worker:
            await self.worker

    def health(self):
        self._prune()
        return {"service": "neeeva-ardy-motion", "ready": not self.closed and self.backend.info.get("ready", False),
                "backend": self.backend.info, "features": self.provider.info,
                "queue": {"waiting": len(self.queue), "limit": self.queue_limit,
                          "running": int(self.running is not None)},
                "characters": {"count": len(self.characters), "limit": self.character_limit},
                "counters": dict(self.counts), "baselineResourcesModified": False}

    def _prune(self):
        self.queue = deque(job for job in self.queue if not job.cancelled and not job.future.done())

    def _invalidate(self, state, reason):
        state.cancelled = True
        state.backend_state = {}
        state.feature = None
        state.last_result = None
        if state.job is not None:
            self.abort(state.job, ServiceError(409, reason, "The motion revision was cancelled or superseded"))

    def cancel(self, request):
        state = self.characters.get(request.characterId)
        if state is not None and request.revision < state.revision:
            raise ServiceError(409, "stale_revision", "Cancellation refers to an older character revision")
        if state is not None and request.revision == state.revision and request.turnId != state.turn:
            raise ServiceError(409, "turn_mismatch", "The revision belongs to another turn")
        if state is None or request.revision > state.revision:
            if state is None and len(self.characters) >= self.character_limit:
                raise ServiceError(429, "character_capacity", "Character tombstone capacity reached")
            if state is not None:
                self._invalidate(state, "superseded")
            state = CharacterState(request.revision, request.turnId)
            self.characters[request.characterId] = state
        self._invalidate(state, "cancelled")
        self._prune()
        return {"characterId": request.characterId, "turnId": request.turnId,
                "revision": request.revision, "cancelled": True}

    def abort(self, job, error):
        if not job.cancelled:
            self.counts["cancelled"] += 1
        job.cancelled = True
        if job.feature_task is not None:
            job.feature_task.cancel()
        if job.state.job is job:
            job.state.job = None
        if not job.future.done():
            job.future.set_exception(error)
            # Consume if the HTTP caller already disconnected; awaiting still raises it.
            job.future.exception()

    def submit(self, request):
        if self.closed or not self.backend.info.get("ready", False):
            raise ServiceError(503, "not_ready", "ARDY is not ready")
        state = self.characters.get(request.characterId)
        fingerprint = hashlib.sha256(json.dumps(request.model_dump(), sort_keys=True, separators=(",", ":")).encode()).hexdigest()
        if state is not None and request.revision < state.revision:
            raise ServiceError(409, "stale_revision", "A newer character revision already exists")
        if state is not None and request.revision == state.revision:
            if request.turnId != state.turn:
                raise ServiceError(409, "turn_mismatch", "The revision belongs to another turn")
            if state.cancelled:
                raise ServiceError(409, "cancelled_revision", "A cancelled revision cannot be restarted")
            if state.last_request_id == request.requestId:
                if state.last_fingerprint != fingerprint:
                    raise ServiceError(409, "request_id_reused", "requestId was reused with different content")
                done = asyncio.get_running_loop().create_future()
                done.set_result(state.last_result)
                return Job(request, state, fingerprint, time.monotonic(), float("inf"), done)
            if state.job is not None:
                if state.job.request.requestId == request.requestId and state.job.fingerprint == fingerprint:
                    return state.job
                raise ServiceError(409, "revision_busy", "Only one chunk per character revision may be pending")
            if request.description != state.description or request.seed != state.seed:
                raise ServiceError(409, "revision_condition_changed", "Description or seed changes require a new revision")
            if request.chunkIndex != state.next_chunk:
                raise ServiceError(409, "chunk_out_of_order", f"Expected chunkIndex {state.next_chunk}")
        elif request.chunkIndex != 0:
            raise ServiceError(409, "history_missing", "A new revision must begin at chunkIndex 0")
        if state is None and len(self.characters) >= self.character_limit:
            raise ServiceError(429, "character_capacity", "Character tombstone capacity reached; restart the isolated session deliberately")
        self._prune()
        reclaimable = state is not None and request.revision > state.revision and state.job in self.queue
        if len(self.queue) - int(reclaimable) >= self.queue_limit:
            self.counts["queueRejected"] += 1
            raise ServiceError(429, "queue_full", "The bounded motion generation queue is full")
        if state is None or request.revision > state.revision:
            if state is not None:
                self._invalidate(state, "superseded")
            state = CharacterState(request.revision, request.turnId, description=request.description, seed=request.seed)
            self.characters[request.characterId] = state
            self._prune()
        now = time.monotonic()
        job = Job(request, state, fingerprint, now, now + request.timeoutMs/1000, asyncio.get_running_loop().create_future())
        state.job = job
        self.queue.append(job)
        self.counts["submitted"] += 1
        self.wake.set()
        return job

    def _valid(self, job):
        return (not job.cancelled and not job.state.cancelled and job.state.job is job
                and self.characters.get(job.request.characterId) is job.state)

    async def _worker(self):
        while not self.closed:
            self._prune()
            if not self.queue:
                self.wake.clear()
                await self.wake.wait()
                continue
            job = self.queue.popleft()
            self.running = job
            job.started = time.monotonic()
            try:
                if not self._valid(job):
                    continue
                if job.deadline <= time.monotonic():
                    raise ServiceError(504, "deadline_exceeded", "Motion deadline expired while queued")
                feature_start = time.monotonic()
                feature = job.state.feature
                reused = feature is not None
                if feature is None:
                    job.feature_task = asyncio.create_task(self.provider.fetch(job.request, job.deadline-time.monotonic()))
                    try:
                        feature = await job.feature_task
                    finally:
                        job.feature_task = None
                feature_ms = (time.monotonic()-feature_start)*1000
                if not self._valid(job):
                    continue
                if job.deadline <= time.monotonic():
                    raise ServiceError(504, "deadline_exceeded", "Motion deadline expired after feature extraction")
                # One worker waits for its CUDA call even after HTTP cancellation. Never overlap
                # model diffusion mutations or process-global RNG with a second GPU inference.
                async with self.backend_gate:
                    if not self._valid(job):
                        continue
                    if job.deadline <= time.monotonic():
                        raise ServiceError(504, "deadline_exceeded", "Motion deadline expired waiting for ARDY")
                    result = await asyncio.to_thread(self.backend.generate, job.request, feature["embedding"], job.state.backend_state)
                if not self._valid(job):
                    self.counts["staleResultsDiscarded"] += 1
                    continue
                if job.deadline <= time.monotonic():
                    raise ServiceError(504, "deadline_exceeded", "Motion deadline expired during generation")
                response = {"characterId": job.request.characterId, "turnId": job.request.turnId,
                    "revision": job.request.revision, "requestId": job.request.requestId,
                    "chunkIndex": job.request.chunkIndex, "startFrame": job.request.chunkIndex*40,
                    "newFrames": 40, "historyFrames": result["historyFrames"], "fps": 20,
                    "final": job.request.chunkIndex == 2, "clip": result["clip"],
                    "historySource": result["historySource"],
                    "provenance": {"mode": "dynamic-ardy-native", "featureSource": "live-condition-reused" if reused and feature["source"] == "live-qwen" else feature["source"],
                        "featureContract": FEATURE_CONTRACT, "modelSha256": feature["modelSha256"],
                        "historySource": result["historySource"], "conditionReusedWithinRevision": reused,
                        "featureRequestId": feature["featureRequestId"], "initialHistoryProjection": result.get("projection"),
                        "derivedSeed": result.get("derivedSeed"), "languageModelsLoaded": False},
                    "timings": {"queueMs": (job.started-job.submitted)*1000, "featureMs": feature_ms,
                        "generationMs": result["generationMs"], "totalMs": (time.monotonic()-job.submitted)*1000}}
                # All state advances together, after checking deadline/cancellation/revision.
                job.state.backend_state = {"history": result["history"], "rng": result["rng"]}
                job.state.feature = feature
                job.state.next_chunk += 1
                job.state.last_request_id = job.request.requestId
                job.state.last_fingerprint = job.fingerprint
                job.state.last_result = response
                job.future.set_result(response)
                self.counts["completed"] += 1
            except asyncio.CancelledError:
                if not job.cancelled:
                    raise
            except ServiceError as error:
                self.counts["failed"] += 1
                if not job.future.done():
                    # Do not export this worker coroutine's live traceback through a
                    # caller-owned exception (test runners may clear traceback frames).
                    job.future.set_exception(ServiceError(error.status, error.code, error.message))
                    job.future.exception()
            except Exception as error:
                self.counts["failed"] += 1
                import logging
                logging.getLogger(__name__).exception("Motion generation failed")
                if not job.future.done():
                    job.future.set_exception(ServiceError(500, "generation_failed", type(error).__name__))
                    job.future.exception()
            finally:
                if job.state.job is job:
                    job.state.job = None
                self.running = None
