"""Independent FastAPI app factory; no GPU or ASR loading on health calls."""
from contextlib import asynccontextmanager
import json
from pathlib import Path
from fastapi import FastAPI, File, Form, Request, UploadFile
from fastapi.responses import FileResponse, JSONResponse
from starlette.concurrency import run_in_threadpool
from .backend import PannsBackend
from .config import Config
from .jobs import JobQueue
from .pipeline import Pipeline, dataset_summary
from .pitch_sessions import PitchSessions
from .store import LearningError, Store, identifier


class ProcessLease:
    def __init__(self, root):
        self.path = Path(root) / 'service.lock'
        self.file = None

    def acquire(self):
        self.file = self.path.open('a+b')
        self.file.seek(0)
        self.file.write(b'0')
        self.file.flush()
        self.file.seek(0)
        try:
            if __import__('os').name == 'nt':
                import msvcrt
                msvcrt.locking(self.file.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(self.file, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except Exception:
            self.file.close()
            self.file = None
            raise RuntimeError('learning_data_already_owned_by_another_process')

    def release(self):
        if self.file:
            self.file.close()
            self.file = None


def create_app(config=None, backend=None):
    config = config or Config.from_env()
    store = Store(config)
    pipeline = Pipeline(store, backend or PannsBackend(config))
    pitch_sessions = PitchSessions(store)
    lease = ProcessLease(store.root)
    # Job recovery happens only after acquiring the process lease.
    @asynccontextmanager
    async def lifespan(app):
        lease.acquire()
        jobs = JobQueue(store, pipeline)
        app.state.jobs = jobs
        jobs.start()
        try:
            yield
        finally:
            pitch_sessions.close()
            jobs.close()
            # Do not allow a second writer while a long feature call still runs.
            if not jobs.workers_alive:
                lease.release()

    app = FastAPI(title='NeEEvA Personal Singing Learning', lifespan=lifespan)
    app.state.store, app.state.pipeline, app.state.config = store, pipeline, config
    app.state.pitch_sessions = pitch_sessions

    @app.exception_handler(LearningError)
    async def learning_error(request, exc):
        return JSONResponse(status_code=exc.status, content={'detail': {'code': exc.code, 'message': exc.message}})

    @app.middleware('http')
    async def payload_limit(request, call_next):
        limit = config.max_upload_bytes + 128 * 1024 if request.url.path.endswith('/recordings') and request.method == 'POST' else 1024 * 1024
        if '/pitch-streams/' in request.url.path and request.url.path.endswith('/chunks'):
            limit = pitch_sessions.max_chunk * 2
        length = request.headers.get('content-length')
        try:
            if length and int(length) > limit:
                return JSONResponse(status_code=413, content={'detail': {'code': 'request_too_large', 'message': 'Request exceeds configured size'}})
        except ValueError:
            return JSONResponse(status_code=400, content={'detail': {'code': 'invalid_content_length', 'message': 'Invalid content length'}})
        # Bound even chunked requests before multipart parsing can spool to disk.
        if request.method in ('POST', 'PUT', 'PATCH'):
            chunks, count = [], 0
            async for chunk in request.stream():
                count += len(chunk)
                if count > limit:
                    return JSONResponse(status_code=413, content={'detail': {'code': 'request_too_large', 'message': 'Request exceeds configured size'}})
                chunks.append(chunk)
            request._body = b''.join(chunks)
        return await call_next(request)

    async def body(request):
        try:
            value = await request.json()
        except ValueError as exc:
            raise LearningError('invalid_json') from exc
        if not isinstance(value, dict):
            raise LearningError('json_object_required')
        return value

    def queue():
        return app.state.jobs

    @app.get('/health')
    def health():
        return dict(ok=True, service='singing-learning', schema=1, execution_authorized=False,
                    capabilities=['acoustic_proposals', 'assisted_confirmation', 'provisional_personalization',
                                  'incremental_pitch_features', 'guarded_temporal_fusion', 'guarded_sequence_boundaries',
                                  'guarded_local_onset_offset', 'guarded_category_evidence'])

    @app.get('/learning/profiles')
    def profiles():
        return dict(profiles=store.profiles())

    @app.post('/learning/profiles')
    async def create_profile(request: Request):
        return store.create_profile(await body(request))

    @app.post('/learning/profiles/{profile_id}/sessions')
    async def create_session(profile_id: str, request: Request):
        return store.create_session(profile_id, await body(request))

    @app.post('/learning/profiles/{profile_id}/pitch-streams')
    async def create_pitch_stream(profile_id: str, request: Request):
        return await run_in_threadpool(pitch_sessions.create, profile_id, await body(request))

    @app.put('/learning/profiles/{profile_id}/pitch-streams/{stream_id}/chunks')
    async def pitch_chunk(profile_id: str, stream_id: str, start_sample: int, request: Request):
        return await run_in_threadpool(pitch_sessions.push, profile_id, stream_id, start_sample, await request.body())

    @app.get('/learning/profiles/{profile_id}/pitch-streams/{stream_id}')
    async def pitch_status(profile_id: str, stream_id: str):
        return await run_in_threadpool(pitch_sessions.status, profile_id, stream_id)

    @app.post('/learning/profiles/{profile_id}/pitch-streams/{stream_id}/commit')
    async def commit_pitch_stream(profile_id: str, stream_id: str, request: Request):
        return await run_in_threadpool(pitch_sessions.commit, profile_id, stream_id, await body(request))

    @app.delete('/learning/profiles/{profile_id}/pitch-streams/{stream_id}')
    async def abort_pitch_stream(profile_id: str, stream_id: str):
        return await run_in_threadpool(pitch_sessions.abort, profile_id, stream_id)

    @app.post('/learning/profiles/{profile_id}/recordings')
    async def upload(profile_id: str, audio_file: UploadFile = File(...), manifest: str = Form(...)):
        if len(manifest) > 96 * 1024:
            raise LearningError('manifest_too_large', status=413)
        try:
            metadata = json.loads(manifest)
        except ValueError as exc:
            raise LearningError('invalid_manifest') from exc
        raw = await audio_file.read(config.max_upload_bytes + 1)
        await audio_file.close()
        recording, fresh = store.upload(profile_id, metadata, raw)
        if fresh or recording['feature_status'] in ('pending', 'failed'):
            try:
                job = queue().enqueue(profile_id, 'features', {'recording_id': recording['recording_id']})
                # Worker may already finish; avoid overwriting a ready/failed state.
                with store.lock:
                    current = store.recording(profile_id, recording['recording_id'])
                    updates = dict(feature_job_id=job['job_id'])
                    if current['feature_status'] == 'pending':
                        updates['feature_status'] = 'queued'
                    store.update_feature(profile_id, recording['recording_id'], **updates)
            except LearningError as exc:
                if exc.code != 'queue_full':
                    raise
            recording = store.recording(profile_id, recording['recording_id'])
        if fresh or not recording.get('proposal_id'):
            try:
                job = queue().enqueue(profile_id, 'proposal', {'recording_id': recording['recording_id']})
                store.update_feature(profile_id, recording['recording_id'], proposal_job_id=job['job_id'])
            except LearningError as exc:
                if exc.code != 'queue_full':
                    raise
            recording = store.recording(profile_id, recording['recording_id'])
        return recording

    @app.get('/learning/profiles/{profile_id}/recordings')
    def recordings(profile_id: str):
        return store.recordings(profile_id)

    @app.get('/learning/profiles/{profile_id}/recordings/{recording_id}')
    def recording(profile_id: str, recording_id: str):
        return store.recording(profile_id, recording_id)

    @app.get('/learning/profiles/{profile_id}/recordings/{recording_id}/audio')
    def audio(profile_id: str, recording_id: str):
        return FileResponse(store.audio_path(profile_id, recording_id), media_type='audio/wav', filename=recording_id + '.wav')

    @app.post('/learning/profiles/{profile_id}/recordings/{recording_id}/features')
    def features(profile_id: str, recording_id: str):
        store.recording(profile_id, recording_id)
        return queue().enqueue(profile_id, 'features', {'recording_id': recording_id})

    @app.put('/learning/profiles/{profile_id}/recordings/{recording_id}/annotations')
    async def annotations(profile_id: str, recording_id: str, request: Request):
        value = await body(request)
        result = store.annotate(profile_id, recording_id, value)
        if value.get('auto_train', True):
            try:
                result['auto_train_job_id'] = queue().enqueue(profile_id, 'train', auto=True)['job_id']
            except LearningError as exc:
                if exc.code != 'queue_full':
                    raise
                result['auto_train_deferred'] = True
        return result

    @app.get('/learning/profiles/{profile_id}/recordings/{recording_id}/proposal')
    def proposal(profile_id: str, recording_id: str):
        rec = store.recording(profile_id, recording_id)
        result = pipeline.cached_proposal(profile_id, recording_id)
        job_id = rec.get('proposal_job_id')
        if result['status'] != 'ready' and job_id:
            job = queue().get(job_id)
            if job['status'] in ('queued', 'running'):
                result.update(status='pending', job_id=job_id)
        return result

    @app.post('/learning/profiles/{profile_id}/recordings/{recording_id}/proposal')
    def propose(profile_id: str, recording_id: str):
        store.recording(profile_id, recording_id)
        cached = pipeline.cached_proposal(profile_id, recording_id)
        if cached['status'] == 'ready':
            return cached
        job = queue().enqueue(profile_id, 'proposal', dict(recording_id=recording_id))
        store.update_feature(profile_id, recording_id, proposal_job_id=job['job_id'])
        return job

    @app.post('/learning/profiles/{profile_id}/train')
    def train(profile_id: str):
        return queue().enqueue(profile_id, 'train')

    @app.get('/learning/jobs/{job_id}')
    def job(job_id: str):
        return queue().get(job_id)

    @app.get('/learning/profiles/{profile_id}/status')
    def status(profile_id: str):
        state = store.snapshot(profile_id)
        summary = dataset_summary(state['recordings'])
        counts = {}
        for value in state['recordings'].values():
            key = value['feature_status']
            counts[key] = counts.get(key, 0) + 1
        return dict(profile_id=profile_id, dataset_revision=state['profile']['dataset_revision'],
                    recording_count=len(state['recordings']), feature_counts=counts,
                    label_seconds=summary['label_seconds'], independent_group_count=summary['independent_group_count'],
                    readiness_reasons=summary['readiness_reasons'], current_candidate=state['current_candidate'],
                    personalization_reasons=summary['personalization_reasons'],
                    latest_training=state['models'][-1] if state['models'] else None,
                    jobs=queue().for_profile(profile_id), execution_authorized=False)

    @app.get('/learning/profiles/{profile_id}/models')
    def models(profile_id: str):
        state = store.snapshot(profile_id)
        return dict(models=[dict(m, stale=m['source_dataset_revision'] != state['profile']['dataset_revision'],
                                 invalidated=store.model_invalidated(store.model(profile_id, m['model_id']), state)) for m in state['models']])

    @app.get('/learning/profiles/{profile_id}/models/{model_id}')
    def model(profile_id: str, model_id: str):
        result = store.model(profile_id, model_id)
        state = store.snapshot(profile_id)
        result['stale'] = result['source_dataset_revision'] != state['profile']['dataset_revision']
        result['invalidated'] = store.model_invalidated(result, state)
        return result

    @app.post('/learning/profiles/{profile_id}/shadow')
    async def shadow(profile_id: str, request: Request):
        value = await body(request)
        rec = identifier(value.get('recording_id'), 'recording_id')
        store.recording(profile_id, rec)
        model_id = value.get('model_id')
        if model_id is not None:
            store.model(profile_id, identifier(model_id, 'model_id'))
        return queue().enqueue(profile_id, 'shadow', dict(recording_id=rec, model_id=model_id))

    return app
