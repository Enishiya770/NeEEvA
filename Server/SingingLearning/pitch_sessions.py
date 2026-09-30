"""Bounded, optional incremental pitch features bound to immutable uploaded audio.

Capture/upload stays independent: abandoning a stream loses only a feature cache.
No labels, recordings, or execution decisions are created by this module.
"""
from contextlib import contextmanager
from dataclasses import dataclass, field
import hashlib
import re
import threading
import time
import wave

import numpy as np

from .store import LearningError, atomic_json, identifier, integer, now, read_json


def _cache_path(store, profile, audio_sha256, provider_identity):
    if not isinstance(audio_sha256, str) or not re.fullmatch('[0-9a-f]{64}', audio_sha256):
        raise LearningError('invalid_audio_sha256')
    if not isinstance(provider_identity, str) or not provider_identity or len(provider_identity) > 512:
        raise LearningError('invalid_pitch_provider_identity')
    identity_hash = hashlib.sha256(provider_identity.encode('utf-8')).hexdigest()
    return store.profile_path(profile) / 'pitch_features' / (audio_sha256 + '-' + identity_hash + '.json')


def load_cached_track(store, profile, recording, provider_identity):
    """Return only a validated cache binding, or None; callers may replay offline.

    ``recording`` accepts its id or a metadata dict obtained from Store. This
    cache contains no human labels and is independent of annotation revisions.
    """
    rec = store.recording(profile, recording) if isinstance(recording, str) else recording
    path = _cache_path(store, profile, rec['audio_sha256'], provider_identity)
    if not path.exists():
        return None
    try:
        track = read_json(path)
        source = track['source']
        valid = (track.get('cache_schema') == 1 and track.get('provider_identity') == provider_identity
                 and track.get('execution_authorized') is False and track.get('causal') is True
                 and track.get('sample_count') == rec['sample_count']
                 and track.get('sample_rate') == rec['sample_rate']
                 and all(source.get(k) == rec[k] for k in ('audio_sha256', 'sample_rate', 'sample_count', 'channels'))
                 and isinstance(track.get('frames'), list)
                 and isinstance(source.get('pcm_sha256'), str)
                 and re.fullmatch('[0-9a-f]{64}', source['pcm_sha256']))
        return track if valid else None
    except (ValueError, KeyError, TypeError, OSError):
        return None


@dataclass
class _Session:
    profile_id: str
    stream_id: str
    processor: object
    created_at: str
    touched: float
    lock: object = field(default_factory=threading.Lock)
    digest: object = field(default_factory=hashlib.sha256)
    frames: list = field(default_factory=list)
    sample_count: int = 0
    last_start: int = -1
    last_size: int = 0
    last_digest: str = ''
    committed_recording_id: str = ''
    committed_path: str = ''


class PitchSessions:
    """At most N live sessions; no internal task queue and one operation per stream."""

    def __init__(self, store, stream_factory=None, clock=time.monotonic):
        self.store, self.config, self.clock = store, store.config, clock
        self._stream_factory = stream_factory
        self._sessions = {}
        self._lock = threading.RLock()
        self._cache_lock = threading.Lock()
        self.max_active = max(1, min(32, int(self.config.pitch_stream_max_active)))
        self.ttl = max(1.0, min(3600.0, float(self.config.pitch_stream_ttl_seconds)))
        self.max_chunk = max(1, min(16000, int(self.config.pitch_stream_max_chunk_samples)))
        self.max_samples = int(16000 * min(120.0, self.config.max_duration_seconds,
                                           self.config.pitch_stream_max_duration_seconds))

    def _profile(self, profile):
        identifier(profile, 'profile_id')
        actual = self.store.snapshot(profile)['profile']['profile_id']
        if actual != profile:
            raise LearningError('profile_case_mismatch', status=409)

    def _prune_locked(self):
        expired = [key for key, value in self._sessions.items()
                   if self.clock() - value.touched >= self.ttl and not value.lock.locked()]
        for key in expired:
            del self._sessions[key]

    def _status(self, value, **extra):
        return dict(profile_id=value.profile_id, stream_id=value.stream_id,
                    status='committed' if value.committed_recording_id else 'receiving',
                    next_sample=value.sample_count, sample_rate=16000, channels=1,
                    frame_count=len(value.frames), provider=value.processor.metadata['provider'],
                    provider_identity=value.processor.metadata['provider_identity'],
                    max_chunk_samples=self.max_chunk, max_samples=self.max_samples,
                    execution_authorized=False, **extra)

    def create(self, profile, body):
        self._profile(profile)
        stream_id = identifier(body.get('stream_id'), 'stream_id')
        if integer(body.get('sample_rate'), 'sample_rate', 1) != 16000 or integer(body.get('channels'), 'channels', 1) != 1:
            raise LearningError('pitch_stream_mono_16000_required')
        with self._lock:
            self._prune_locked()
            key = (profile, stream_id)
            existing = self._sessions.get(key)
            if existing is not None:
                existing.touched = self.clock()
                return self._status(existing, idempotent=True)
            if len(self._sessions) >= self.max_active:
                raise LearningError('pitch_stream_limit', status=429)
            if self._stream_factory is None:
                from .pitch_features import CausalPitchStream
                processor = CausalPitchStream(stream_id, provider='fft', sample_rate=16000,
                                               max_chunk_samples=self.max_chunk)
            else:
                processor = self._stream_factory(stream_id)
            value = _Session(profile, stream_id, processor, now(), self.clock())
            self._sessions[key] = value
            return self._status(value, idempotent=False)

    @contextmanager
    def _session(self, profile, stream_id):
        self._profile(profile)
        identifier(stream_id, 'stream_id')
        with self._lock:
            self._prune_locked()
            value = self._sessions.get((profile, stream_id))
            if value is None:
                raise LearningError('pitch_stream_not_found', status=404)
            if not value.lock.acquire(blocking=False):
                raise LearningError('pitch_stream_busy', status=429)
            value.touched = self.clock()
        try:
            yield value
        finally:
            value.touched = self.clock()
            value.lock.release()

    def status(self, profile, stream_id):
        with self._session(profile, stream_id) as value:
            return self._status(value)

    def push(self, profile, stream_id, start_sample, raw):
        integer(start_sample, 'start_sample')
        if not raw or len(raw) % 2:
            raise LearningError('pitch_stream_pcm16_required')
        count = len(raw) // 2
        if count > self.max_chunk:
            raise LearningError('pitch_stream_chunk_too_large', status=413)
        chunk_digest = hashlib.sha256(raw).hexdigest()
        with self._session(profile, stream_id) as value:
            if (start_sample == value.last_start and count == value.last_size
                    and chunk_digest == value.last_digest):
                return self._status(value, idempotent=True, added_frames=0)
            if value.committed_recording_id:
                raise LearningError('pitch_stream_already_committed', status=409)
            if start_sample != value.sample_count:
                raise LearningError('pitch_stream_noncontiguous',
                                    'Expected start_sample ' + str(value.sample_count), 409)
            if value.sample_count + count > self.max_samples:
                raise LearningError('pitch_stream_too_long', status=413)
            samples = np.frombuffer(raw, dtype='<i2').astype(np.float32) / 32768.0
            try:
                frames = value.processor.push(samples, stream_id=stream_id, start_sample=start_sample)
                if not isinstance(frames, list):
                    raise ValueError('pitch frames must be a list')
            except Exception as exc:
                # A partially mutated provider cannot safely retry. Original
                # recording upload remains completely independent and usable.
                with self._lock:
                    self._sessions.pop((profile, stream_id), None)
                raise LearningError('pitch_stream_provider_failed', str(exc), 503) from exc
            value.frames.extend(frames)
            value.digest.update(raw)
            value.sample_count += count
            value.last_start, value.last_size, value.last_digest = start_sample, count, chunk_digest
            return self._status(value, idempotent=False, added_frames=len(frames))

    def commit(self, profile, stream_id, body):
        rid = identifier(body.get('recording_id'), 'recording_id')
        with self._session(profile, stream_id) as value:
            if value.committed_recording_id:
                if value.committed_recording_id != rid:
                    raise LearningError('pitch_stream_commit_conflict', status=409)
                return self._status(value, recording_id=rid, cache_ready=True, idempotent=True)
            rec = self.store.recording(profile, rid)
            if (rec['sample_rate'] != 16000 or rec['channels'] != 1
                    or rec['sample_count'] != value.sample_count or value.sample_count <= 0):
                raise LearningError('pitch_stream_source_mismatch', status=409)
            original = self.store.audio_path(profile, rid)
            wav_digest = hashlib.sha256()
            with original.open('rb') as stream:
                for block in iter(lambda: stream.read(65536), b''):
                    wav_digest.update(block)
            pcm_digest = hashlib.sha256()
            try:
                with wave.open(str(original), 'rb') as audio:
                    if (audio.getnchannels(), audio.getsampwidth(), audio.getframerate(), audio.getnframes(), audio.getcomptype()) != (1, 2, 16000, value.sample_count, 'NONE'):
                        raise LearningError('pitch_stream_source_mismatch', status=409)
                    for block in iter(lambda: audio.readframes(32768), b''):
                        pcm_digest.update(block)
            except (wave.Error, EOFError) as exc:
                raise LearningError('pitch_stream_source_mismatch', status=409) from exc
            if wav_digest.hexdigest() != rec['audio_sha256'] or pcm_digest.digest() != value.digest.digest():
                raise LearningError('pitch_stream_source_mismatch', status=409)
            track = value.processor.track(value.frames)
            track.update(cache_schema=1, execution_authorized=False,
                         source=dict(audio_sha256=rec['audio_sha256'], pcm_sha256=pcm_digest.hexdigest(),
                                     sample_rate=16000, sample_count=value.sample_count, channels=1),
                         provenance=dict(kind='verified_incremental_pcm', stream_id=stream_id,
                                         recording_id=rid, created_at=now()))
            path = _cache_path(self.store, profile, rec['audio_sha256'], track['provider_identity'])
            with self._cache_lock, self.store.lock:
                atomic_json(path, track)
            value.committed_recording_id, value.committed_path = rid, str(path)
            return self._status(value, recording_id=rid, cache_ready=True, idempotent=False)

    def abort(self, profile, stream_id):
        self._profile(profile)
        identifier(stream_id, 'stream_id')
        with self._lock:
            value = self._sessions.get((profile, stream_id))
            if value is not None:
                if not value.lock.acquire(blocking=False):
                    raise LearningError('pitch_stream_busy', status=429)
                try:
                    del self._sessions[(profile, stream_id)]
                finally:
                    value.lock.release()
        return dict(profile_id=profile, stream_id=stream_id, status='aborted', execution_authorized=False)

    def load_cached_track(self, profile, recording):
        from .pitch_features import provider_identity
        return load_cached_track(self.store, profile, recording, provider_identity('fft'))

    def close(self):
        with self._lock:
            self._sessions.clear()
