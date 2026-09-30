import copy
import threading
import time
import uuid
from .store import LearningError, atomic_json, identifier, now, read_json


class JobQueue:
    """One foreground worker and one training worker, with bounded lane queues.

    Full training/holdout diagnostics must not delay a ready proposal merely
    because they own the worker. The two lanes share persisted job state and
    locking, but each has ``queue_limit`` pending slots. Training is globally
    serial; queued automatic successors continue to coalesce per profile.
    """
    def __init__(self, store, pipeline):
        self.store, self.pipeline = store, pipeline
        self.condition = threading.Condition(threading.RLock())
        self.items, self.pending, self.thread = {}, [], None
        self.training_thread = None
        self.closed = False
        self.directory = store.root / 'jobs'
        self.directory.mkdir(exist_ok=True)
        for path in self.directory.glob('*.json'):
            value = read_json(path)
            if value['status'] in ('queued', 'running'):
                value.update(status='interrupted', finished_at=now(), error='service_restarted_before_completion')
                atomic_json(path, value)
            self.items[value['job_id']] = value

    def start(self):
        with self.condition:
            if self.closed:
                raise LearningError('worker_closed', status=503)
            if self.thread is None:
                self.thread = threading.Thread(target=self._run, args=(False,), daemon=True,
                                               name='singing-learning-worker')
                self.thread.start()
            if self.training_thread is None:
                self.training_thread = threading.Thread(target=self._run, args=(True,), daemon=True,
                                                        name='singing-learning-training-worker')
                self.training_thread.start()

    @property
    def workers_alive(self):
        """A caller may release the process's data lease only when this is false."""
        with self.condition:
            return any(worker is not None and worker.is_alive()
                       for worker in (self.thread, self.training_thread))

    def close(self, timeout=5.0):
        with self.condition:
            self.closed = True
            for job_id in self.pending:
                self.items[job_id].update(status='interrupted', error='service_shutdown', finished_at=now())
                self._save(job_id)
            self.pending.clear()
            self.condition.notify_all()
        # A timed-out task can still write its result. Do not pretend shutdown
        # succeeded or release the owning process's data lease in that case.
        deadline = time.monotonic() + max(0., float(timeout))
        for worker in (self.thread, self.training_thread):
            if worker and worker is not threading.current_thread():
                worker.join(timeout=max(0., deadline - time.monotonic()))

    def _save(self, job_id):
        atomic_json(self.directory / (job_id + '.json'), self.items[job_id])

    def enqueue(self, profile, kind, payload=None, auto=False):
        self.store.snapshot(profile)
        payload = payload or {}
        with self.condition:
            if self.closed:
                raise LearningError('worker_closed', status=503)
            # Features are deduplicated while queued/running; training has at
            # most one pending successor to a running snapshot.
            for job_id, job in self.items.items():
                duplicate = job['profile_id'] == profile and job['kind'] == kind and job['payload'] == payload
                if duplicate and (job['status'] == 'queued' or kind in ('features', 'proposal') and job['status'] == 'running'):
                    if kind == 'train' and auto:
                        job['not_before'] = time.time() + self.store.config.auto_train_debounce_seconds
                        self._save(job_id)
                        self.condition.notify_all()
                    return dict(job_id=job_id, status=job['status'], coalesced=True)
            training = kind == 'train'
            lane_pending = sum((self.items[jid]['kind'] == 'train') == training for jid in self.pending)
            if lane_pending >= self.store.config.queue_limit:
                lane = 'Training' if training else 'Foreground'
                raise LearningError('queue_full', lane + ' queue is full; saved data remains available for retry', 429)
            job_id = uuid.uuid4().hex
            self.items[job_id] = dict(job_id=job_id, profile_id=profile, kind=kind, payload=payload,
                                     status='queued', created_at=now(), auto=auto,
                                     queue_lane='training' if training else 'foreground',
                                     not_before=time.time() + (self.store.config.auto_train_debounce_seconds if auto else 0),
                                     execution_authorized=False)
            self.pending.append(job_id)
            self._save(job_id)
            self.condition.notify_all()
            return dict(job_id=job_id, status='queued', coalesced=False)

    def get(self, job_id):
        identifier(job_id, 'job_id')
        with self.condition:
            if job_id not in self.items:
                raise LearningError('job_not_found', status=404)
            return copy.deepcopy(self.items[job_id])

    def for_profile(self, profile):
        with self.condition:
            return [copy.deepcopy(v) for v in self.items.values() if v['profile_id'] == profile][-30:]

    def _run(self, training=False):
        while True:
            with self.condition:
                while True:
                    if self.closed:
                        return
                    # A ready job from the other lane must not cause busy waits
                    # or let a second worker claim the same training snapshot.
                    lane = [jid for jid in self.pending if (self.items[jid]['kind'] == 'train') == training]
                    ready = [jid for jid in lane if self.items[jid]['not_before'] <= time.time()]
                    if ready:
                        job_id = ready[0]
                        self.pending.remove(job_id)
                        job = self.items[job_id]
                        job.update(status='running', started_at=now())
                        self._save(job_id)
                        break
                    delay = min((self.items[j]['not_before'] - time.time() for j in lane), default=10)
                    self.condition.wait(timeout=max(.01, min(10, delay)))
            started = time.perf_counter()
            try:
                if job['kind'] == 'features':
                    result = self.pipeline.extract(job['profile_id'], job['payload']['recording_id'])
                elif job['kind'] == 'train':
                    report = self.pipeline.train(job['profile_id'], job_id)
                    result = {k: report[k] for k in ('model_id', 'status', 'source_dataset_revision', 'stale_at_completion', 'execution_authorized')}
                    if 'temporal_fusion' in report:
                        result['temporal_fusion'] = self.store.fusion_summary(report['temporal_fusion'])
                    if 'sequence_boundary' in report:
                        result['sequence_boundary'] = self.store.fusion_summary(report['sequence_boundary'])
                    if 'local_boundary' in report:
                        result['local_boundary'] = self.store.fusion_summary(report['local_boundary'])
                    if 'category_evidence' in report:
                        result['category_evidence'] = self.store.fusion_summary(report['category_evidence'])
                    result.update(reasons=report.get('reasons', []), dataset_summary=report['dataset_summary'])
                    result.update(model_stage=report.get('model_stage'), evaluation_scope=report.get('evaluation_scope'))
                    if 'validation_duration_confusion' in report:
                        result['validation_duration_confusion'] = report['validation_duration_confusion']
                elif job['kind'] == 'shadow':
                    result = self.pipeline.shadow(job['profile_id'], **job['payload'])
                elif job['kind'] == 'proposal':
                    result = self.pipeline.propose(job['profile_id'], **job['payload'])
                else:
                    raise RuntimeError('unknown_job_kind')
                with self.condition:
                    job.update(status='completed', result=result)
            except Exception as exc:
                with self.condition:
                    job.update(status='failed', error=dict(code=getattr(exc, 'code', getattr(exc, 'reason', type(exc).__name__)), message=str(exc)[:600]))
            finally:
                with self.condition:
                    job.update(finished_at=now(), wall_seconds=time.perf_counter() - started)
                    self._save(job_id)
                    self.condition.notify_all()
