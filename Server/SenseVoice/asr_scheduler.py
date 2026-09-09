"""One inference worker, final-first queue and cooperative obsolete-job cancellation."""
import asyncio
import heapq
import itertools
import threading
import time
from concurrent.futures import Future


class AsrCancelled(BaseException):
    """Must pass through acoustic fallback handlers instead of becoming fake evidence."""


_context = threading.local()


def checkpoint():
    cancelled = getattr(_context, "cancelled", None)
    if cancelled is not None and cancelled.is_set():
        raise AsrCancelled()


class AnalysisScheduler:
    def __init__(self):
        self._condition = threading.Condition()
        self._queue = []
        self._jobs = {}
        self._cancelled = {}
        self._promoted = {}
        self._sequence = itertools.count()
        threading.Thread(target=self._run, name="asr-final", daemon=True).start()

    def submit(self, fn, args, request_id="", channel="", preview=False):
        future = Future()
        with self._condition:
            now = time.monotonic()
            self._cancelled = {k: t for k, t in self._cancelled.items() if now - t < 60}
            self._promoted = {k: t for k, t in self._promoted.items() if now - t < 60}
            if request_id and request_id in self._cancelled:
                future.set_result({"cancelled": True})
                return future
            if request_id in self._promoted:
                preview = False
            # Only supersede speculative work owned by this client; never other clients.
            if channel:
                for job in self._jobs.values():
                    if job["channel"] == channel and job["preview"]:
                        job["cancel"].set()
            key = request_id or "anonymous-" + str(next(self._sequence))
            if key in self._jobs:
                future.set_exception(ValueError("duplicate analysis request_id"))
                return future
            job = dict(key=key, channel=channel, preview=preview, cancel=threading.Event(),
                       fn=fn, args=args, future=future)
            self._jobs[key] = job
            heapq.heappush(self._queue, (1 if preview else 0, next(self._sequence), job))
            self._condition.notify()
        return future

    def cancel(self, request_id):
        if not request_id:
            return False
        with self._condition:
            # Handles a cancel arriving before the upload has been queued.
            self._cancelled[request_id] = time.monotonic()
            while len(self._cancelled) > 256:
                del self._cancelled[next(iter(self._cancelled))]
            job = self._jobs.get(request_id)
            if job:
                job["cancel"].set()
            self._condition.notify()
            return job is not None

    def promote(self, request_id):
        if not request_id:
            return False
        with self._condition:
            self._promoted[request_id] = time.monotonic()
            while len(self._promoted) > 256:
                del self._promoted[next(iter(self._promoted))]
            job = self._jobs.get(request_id)
            if job is None:
                return True  # Promotion may arrive while the upload is still in progress.
            if job["cancel"].is_set():
                return False
            job["preview"] = False
            self._queue = [(0 if j is job else p, seq, j) for p, seq, j in self._queue]
            heapq.heapify(self._queue)
            return True

    def _run(self):
        while True:
            with self._condition:
                self._condition.wait_for(lambda: bool(self._queue))
                _, _, job = heapq.heappop(self._queue)
            _context.cancelled = job["cancel"]
            try:
                checkpoint()
                value = job["fn"](*job["args"])
                checkpoint()
                job["future"].set_result(value)
            except AsrCancelled:
                job["future"].set_result({"cancelled": True})
            except Exception as exc:
                job["future"].set_exception(exc)
            finally:
                _context.cancelled = None
                with self._condition:
                    self._jobs.pop(job["key"], None)

    async def wait(self, future):
        # HTTP disconnects must not cancel the Future while a worker is completing it.
        return await asyncio.shield(asyncio.wrap_future(future))
