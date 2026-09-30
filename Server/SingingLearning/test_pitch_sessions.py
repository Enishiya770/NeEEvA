"""Isolated synthetic capture tests: no production audio, labels or model writes."""
import io
import json
from pathlib import Path
import socket
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
import wave

import numpy as np

from .app import create_app
from .config import Config
from .pitch_features import CausalPitchStream, extract_track, provider_identity
from .pitch_sessions import PitchSessions, load_cached_track
from .store import LearningError, Store
from .test_learning import FakeBackend


def pcm(count=16000, frequency=220):
    return (np.sin(np.arange(count) * frequency * 2 * np.pi / 16000) * 9000).astype('<i2').tobytes()


def wav(raw):
    buffer = io.BytesIO()
    with wave.open(buffer, 'wb') as writer:
        writer.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
        writer.writeframes(raw)
    return buffer.getvalue()


class PitchSessionTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(Config(Path(self.directory.name), pitch_stream_max_active=2,
                                  pitch_stream_ttl_seconds=2, pitch_stream_max_duration_seconds=2))
        for profile in ('alice', 'bob'):
            self.store.create_profile(dict(profile_id=profile))
            self.store.create_session(profile, dict(session_id='session', content_group='song'))
        self.clock = 0.
        self.manager = PitchSessions(self.store, clock=lambda: self.clock)

    def tearDown(self):
        self.manager.close()
        self.directory.cleanup()

    def create(self, stream='s', profile='alice'):
        return self.manager.create(profile, dict(stream_id=stream, sample_rate=16000, channels=1))

    def upload(self, raw, rid='r', profile='alice'):
        return self.store.upload(profile, dict(recording_id=rid, session_id='session', content_group='song',
                                              capture_serial=1, source_kind='user'), wav(raw))[0]

    def assert_code(self, code, callable, *args):
        with self.assertRaises(LearningError) as caught:
            callable(*args)
        self.assertEqual(caught.exception.code, code)

    def test_exact_pcm_commit_matches_offline_frames_and_never_labels(self):
        raw = pcm(19431)
        self.create()
        self.manager.push('alice', 's', 0, raw[:16000])
        self.manager.push('alice', 's', 8000, raw[16000:])
        rec = self.upload(raw)
        before = self.store.snapshot('alice')
        result = self.manager.commit('alice', 's', dict(recording_id='r'))
        self.assertTrue(result['cache_ready'])
        self.assertFalse(result['execution_authorized'])
        track = load_cached_track(self.store, 'alice', rec, provider_identity('fft'))
        self.assertIsNotNone(track)
        replay = extract_track(np.frombuffer(raw, '<i2').astype(np.float32) / 32768., stream_id='s')
        self.assertEqual(track['frames'], replay['frames'])
        self.assertEqual(track['sample_count'], len(raw) // 2)
        self.assertEqual(self.store.snapshot('alice'), before)
        self.assertEqual(self.store.audio_path('alice', 'r').read_bytes(), wav(raw))
        self.assertTrue(self.manager.commit('alice', 's', dict(recording_id='r'))['idempotent'])

    def test_duplicate_latest_chunk_is_idempotent_but_changed_or_old_retries_fail(self):
        raw = pcm(8000)
        self.create()
        first = self.manager.push('alice', 's', 0, raw)
        retry = self.manager.push('alice', 's', 0, raw)
        self.assertTrue(retry['idempotent'])
        self.assertEqual(first['next_sample'], retry['next_sample'])
        self.assertEqual(first['frame_count'], retry['frame_count'])
        self.assert_code('pitch_stream_noncontiguous', self.manager.push, 'alice', 's', 0, pcm(8000, 330))
        self.assert_code('pitch_stream_noncontiguous', self.manager.push, 'alice', 's', 8001, raw)
        self.manager.push('alice', 's', 8000, raw)
        self.assert_code('pitch_stream_noncontiguous', self.manager.push, 'alice', 's', 0, raw)

    def test_bad_pcm_and_bounded_chunk_duration_leave_counter_unchanged(self):
        self.create()
        self.assert_code('pitch_stream_pcm16_required', self.manager.push, 'alice', 's', 0, b'x')
        self.assert_code('pitch_stream_chunk_too_large', self.manager.push, 'alice', 's', 0, pcm(16001))
        self.manager.push('alice', 's', 0, pcm())
        self.manager.push('alice', 's', 16000, pcm())
        self.assert_code('pitch_stream_too_long', self.manager.push, 'alice', 's', 32000, pcm(1))
        self.assertEqual(self.manager.status('alice', 's')['next_sample'], 32000)
        value = self.manager._sessions[('alice', 's')]
        self.assertLess(value.processor.buffered_samples, 640)
        self.assertFalse(any(self.store.root.glob('profiles/*/pitch_features/*')))

    def test_mismatched_pcm_length_and_missing_recording_never_create_cache(self):
        self.create()
        self.manager.push('alice', 's', 0, pcm())
        self.assert_code('recording_not_found', self.manager.commit, 'alice', 's', dict(recording_id='missing'))
        self.upload(pcm(8000), 'short')
        self.upload(pcm(16000, 440), 'other')
        for rid in ('short', 'other'):
            self.assert_code('pitch_stream_source_mismatch', self.manager.commit, 'alice', 's', dict(recording_id=rid))
        self.assertFalse(any(self.store.root.glob('profiles/*/pitch_features/*')))

    def test_original_wav_hash_is_verified_before_cache_commit(self):
        raw = pcm()
        self.create()
        self.manager.push('alice', 's', 0, raw)
        self.upload(raw)
        # PCM still matches, but any mutated WAV byte invalidates the binding.
        with self.store.audio_path('alice', 'r').open('ab') as stream:
            stream.write(b'changed container')
        self.assert_code('pitch_stream_source_mismatch', self.manager.commit, 'alice', 's', dict(recording_id='r'))

    def test_profile_isolation_abort_and_unconfirmed_upload_survives_failure(self):
        self.create()
        self.create(profile='bob')
        self.manager.push('alice', 's', 0, pcm())
        self.assertEqual(self.manager.status('bob', 's')['next_sample'], 0)
        self.upload(pcm())
        before = self.store.snapshot('alice')
        self.manager.abort('alice', 's')
        self.manager.abort('alice', 's')
        self.assert_code('pitch_stream_not_found', self.manager.status, 'alice', 's')
        self.assertEqual(self.store.snapshot('alice'), before)
        self.assertIsNone(self.store.recording('alice', 'r')['annotation'])
        self.assertIsNone(load_cached_track(self.store, 'alice', 'r', provider_identity('fft')))
        self.assertEqual(self.manager.status('bob', 's')['status'], 'receiving')

    def test_stream_limit_ttl_and_input_validation(self):
        self.create('one')
        self.create('two')
        self.assert_code('pitch_stream_limit', self.create, 'three')
        self.clock = 2.1
        self.create('three')
        self.assert_code('pitch_stream_not_found', self.manager.status, 'alice', 'one')
        self.assert_code('profile_not_found', self.create, 'x', 'missing')
        self.assert_code('invalid_id', self.create, '../bad')
        self.assert_code('pitch_stream_mono_16000_required', self.manager.create, 'alice', dict(stream_id='x', sample_rate=8000, channels=1))

    def test_busy_stream_has_no_hidden_queue_and_does_not_block_other_stream(self):
        self.create()
        self.create('other')
        value = self.manager._sessions[('alice', 's')]
        value.lock.acquire()
        try:
            self.assert_code('pitch_stream_busy', self.manager.push, 'alice', 's', 0, pcm(320))
            self.assert_code('pitch_stream_busy', self.manager.abort, 'alice', 's')
            self.assertEqual(self.manager.push('alice', 'other', 0, pcm(320))['next_sample'], 320)
        finally:
            value.lock.release()

    def test_provider_failure_abandons_only_features(self):
        class BrokenStream(CausalPitchStream):
            def push(self, *args, **kwargs):
                raise RuntimeError('synthetic provider fault')
        self.manager._stream_factory = lambda sid: BrokenStream(sid)
        self.create()
        self.assert_code('pitch_stream_provider_failed', self.manager.push, 'alice', 's', 0, pcm(320))
        self.assert_code('pitch_stream_not_found', self.manager.status, 'alice', 's')
        rec = self.upload(pcm())
        self.assertEqual(rec['sample_count'], 16000)
        self.assertIsNone(rec['annotation'])

    def test_committed_source_cannot_be_rebound_or_appended(self):
        self.create()
        self.manager.push('alice', 's', 0, pcm())
        self.upload(pcm())
        self.upload(pcm(), 'same')
        self.manager.commit('alice', 's', dict(recording_id='r'))
        self.assert_code('pitch_stream_commit_conflict', self.manager.commit, 'alice', 's', dict(recording_id='same'))
        self.assert_code('pitch_stream_already_committed', self.manager.push, 'alice', 's', 16000, pcm(320))

    def test_cache_wrong_provider_or_damaged_metadata_is_ignored(self):
        self.create()
        self.manager.push('alice', 's', 0, pcm())
        self.upload(pcm())
        self.manager.commit('alice', 's', dict(recording_id='r'))
        self.assertIsNone(load_cached_track(self.store, 'alice', 'r', 'different-provider'))
        path = next(self.store.profile_path('alice').glob('pitch_features/*.json'))
        track = json.loads(path.read_text())
        track['source']['sample_count'] += 1
        path.write_text(json.dumps(track))
        self.assertIsNone(load_cached_track(self.store, 'alice', 'r', provider_identity('fft')))


class PitchHttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import uvicorn
        cls.directory = tempfile.TemporaryDirectory()
        cls.app = create_app(Config(Path(cls.directory.name)), FakeBackend())
        cls.socket = socket.socket()
        cls.socket.bind(('127.0.0.1', 0))
        cls.port = cls.socket.getsockname()[1]
        cls.server = uvicorn.Server(uvicorn.Config(cls.app, log_level='error'))
        cls.thread = threading.Thread(target=lambda: cls.server.run(sockets=[cls.socket]), daemon=True)
        cls.thread.start()
        deadline = time.monotonic() + 5
        while not cls.server.started and time.monotonic() < deadline:
            time.sleep(.01)
        if not cls.server.started:
            raise RuntimeError('test_server_start_failed')

    @classmethod
    def tearDownClass(cls):
        cls.server.should_exit = True
        cls.thread.join(6)
        cls.socket.close()
        cls.directory.cleanup()

    def request(self, path, method='GET', body=None, raw=None):
        data = raw if raw is not None else json.dumps(body).encode() if body is not None else None
        request = urllib.request.Request('http://127.0.0.1:' + str(self.port) + path, data=data, method=method)
        request.add_header('Content-Type', 'application/octet-stream' if raw is not None else 'application/json')
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        try:
            with opener.open(request, timeout=5) as response:
                return response.status, json.loads(response.read())
        except urllib.error.HTTPError as response:
            return response.code, json.loads(response.read())

    def test_http_incremental_protocol_limits_commit_and_candidate_only_authority(self):
        code, health = self.request('/health')
        self.assertEqual(code, 200)
        self.assertIn('incremental_pitch_features', health['capabilities'])
        self.request('/learning/profiles', 'POST', dict(profile_id='http'))
        route = '/learning/profiles/http/pitch-streams'
        code, value = self.request(route, 'POST', dict(stream_id='s', sample_rate=16000, channels=1))
        self.assertEqual(code, 200)
        self.assertFalse(value['execution_authorized'])
        code, value = self.request(route + '/s/chunks?start_sample=0', 'PUT', raw=pcm())
        self.assertEqual(code, 200, value)
        self.assertEqual(value['next_sample'], 16000)
        self.assertEqual(self.request(route + '/s/chunks?start_sample=16000', 'PUT', raw=pcm(16001))[0], 413)
        self.assertEqual(self.request(route + '/s/chunks?start_sample=2', 'PUT', raw=pcm(320))[0], 409)
        self.app.state.store.create_session('http', dict(session_id='session', content_group='song'))
        self.app.state.store.upload('http', dict(recording_id='r', session_id='session', content_group='song', capture_serial=1), wav(pcm()))
        code, result = self.request(route + '/s/commit', 'POST', dict(recording_id='r'))
        self.assertEqual(code, 200, result)
        self.assertTrue(result['cache_ready'])
        self.assertFalse(result['execution_authorized'])
        self.assertEqual(self.request(route + '/s')[1]['status'], 'committed')
        self.assertEqual(self.request(route + '/s', 'DELETE')[0], 200)
        self.assertEqual(self.request(route + '/s')[0], 404)


if __name__ == '__main__':
    unittest.main()
