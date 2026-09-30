"""Synthetic audio only, frozen fake scores and actual deterministic adapter fit."""
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
from .app import create_app, ProcessLease
from .config import Config
from .jobs import JobQueue
from .pipeline import Pipeline, cell_masses, dataset_summary, feature_arrays, groups_for
from .proposals import BOOTSTRAP_CHECKPOINT
from .store import LearningError, Store, wav_info

LABELS = ['Speech', 'Singing', 'Male speech, man speaking', 'Female speech, woman speaking',
          'Male singing', 'Female singing', 'Music', 'Humming', 'Rapping',
          'Child speech, kid speaking', 'Child singing', 'Conversation', 'Narration, monologue']


def audio(seed=1, seconds=4, rate=8000):
    buffer = io.BytesIO()
    with wave.open(buffer, 'wb') as writer:
        writer.setparams((1, 2, rate, 0, 'NONE', 'not compressed'))
        writer.writeframes(np.full(int(seconds * rate), seed, dtype='<i2').tobytes())
    return buffer.getvalue()


class FakeBackend:
    def __init__(self):
        self.calls = 0
        self.entered, self.release = None, None

    def identity(self):
        return 'frozen-fake-v1'

    def extract(self, path):
        self.calls += 1
        if self.entered:
            self.entered.set()
            if not self.release.wait(4):
                raise RuntimeError('test_barrier_timeout')
        with wave.open(str(path), 'rb') as source:
            rate, count = source.getframerate(), source.getnframes()
        from hashlib import sha256
        score = [[.1,.9,.1,.1,.9,.9,.7,.2,.1], [.2,.8,.2,.2,.8,.8,.6,.1,.2],
                 [.9,.1,.9,.9,.1,.1,.1,.1,.1], [.8,.2,.8,.8,.2,.2,.2,.1,.1]]
        score = [row + [0., 0., 0., 0.] for row in score]
        return dict(status='candidate', model_version=BOOTSTRAP_CHECKPOINT, source=dict(audio_sha256=sha256(Path(path).read_bytes()).hexdigest(), sample_rate=rate, sample_count=count),
                    timeline=dict(columns=LABELS, scores=score, coordinate_sample_rate=rate,
                                  source_start_samples=[0, count//4, count//2, 3*count//4]),
                    model_metadata=dict(provider='synthetic-frozen-fixture'), execution_authorized=False)


class LearningTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.config = Config(Path(self.directory.name), auto_train_debounce_seconds=.04)
        self.store = Store(self.config)
        self.store.create_profile(dict(profile_id='alice'))
        self.backend = FakeBackend()
        self.pipeline = Pipeline(self.store, self.backend)

    def tearDown(self):
        self.directory.cleanup()

    def add(self, number, source='user', session=None, content=None, parent=None, lineage=None, seed=None):
        rid = 'r' + str(number)
        session, content = session or 's'+str(number), content or 'song'+str(number)
        self.store.create_session('alice', dict(session_id=session, content_group=content))
        manifest = dict(recording_id=rid, session_id=session, content_group=content, capture_serial=number,
                        source_kind=source, parent_recording_id=parent, lineage_json=lineage)
        return self.store.upload('alice', manifest, audio(seed if seed is not None else number+1))[0]

    def label(self, rid, source='user', expected=0, regions=None):
        if regions is None:
            regions = [dict(start_sample=0, end_sample=16000, label='singing', tolerance_samples=0),
                       dict(start_sample=16000, end_sample=32000, label='environment', tolerance_samples=0)]
        return self.store.annotate('alice', rid, dict(expected_revision=expected, confirmed=True, source_kind=source, regions=regions))

    def dataset(self):
        for i in range(3):
            self.add(i)
            self.label('r'+str(i))

    def test_hash_metadata_verified_and_original_unchanged(self):
        rec = self.add(0)
        self.assertEqual(self.store.audio_path('alice', 'r0').read_bytes(), audio(1))
        info = wav_info(audio(1), self.config)
        self.assertEqual(rec['audio_sha256'], info['audio_sha256'])
        with self.assertRaisesRegex(LearningError, 'audio_sha256'):
            self.store.upload('alice', dict(recording_id='bad', session_id='s0', content_group='song0', capture_serial=0, audio_sha256='wrong'), audio(1))

    def test_path_and_case_alias_are_rejected(self):
        for profile in ('../x', 'C:', 'CON', 'a/b', 'x.y'):
            with self.assertRaises(LearningError):
                self.store.create_profile(dict(profile_id=profile))
        self.add(0)
        with self.assertRaisesRegex(LearningError, 'recording_case_conflict'):
            self.store.upload('alice', dict(recording_id='R0', session_id='s0', content_group='song0', capture_serial=0), audio(8))

    def test_pcm_duration_and_truncation_are_bounded(self):
        with self.assertRaises(LearningError):
            wav_info(b'not wave', self.config)
        with self.assertRaisesRegex(LearningError, 'truncated'):
            wav_info(audio()[:-4], self.config)
        with self.assertRaisesRegex(LearningError, 'audio_too_long'):
            wav_info(audio(seconds=2), Config(Path(self.directory.name), max_duration_seconds=1))

    def test_append_only_cas_and_source_confirmation(self):
        self.add(0)
        self.label('r0', source='playback')
        self.assertEqual(dataset_summary(self.store.snapshot('alice')['recordings'])['label_seconds']['singing'], 0)
        with self.assertRaisesRegex(LearningError, 'Refresh'):
            self.label('r0', expected=0)
        self.label('r0', expected=1)
        versions = list((self.store.profile_path('alice') / 'recordings/r0/annotations').glob('*.json'))
        self.assertEqual(len(versions), 2)
        with self.assertRaisesRegex(LearningError, 'human_confirmation'):
            self.store.annotate('alice', 'r0', dict(expected_revision=2, confirmed=False, source_kind='user', regions=[]))

    def test_uncertain_blank_environment_and_tolerance_exact_overlap(self):
        rec = self.add(0)
        self.label('r0', regions=[dict(start_sample=100, end_sample=8100, label='singing', tolerance_samples=100),
                                 dict(start_sample=8200, end_sample=10000, label='uncertain'),
                                 dict(start_sample=12000, end_sample=18000, label='environment', tolerance_samples=0)])
        rec = self.store.recording('alice', 'r0')
        edges = np.array([0,8000,16000,24000,32000])
        pos, neg, fine = cell_masses(rec, edges)
        self.assertAlmostEqual(pos.sum(), 7800/8000)
        self.assertAlmostEqual(neg.sum(), 6000/8000)
        self.assertAlmostEqual(neg[1], 4000/8000)
        self.assertEqual(fine['speech'].sum(), 0)

    def test_invalid_and_overlapping_sample_ranges_rejected(self):
        self.add(0)
        for regions in ([dict(start_sample=0.1,end_sample=100,label='singing')],
                        [dict(start_sample=0,end_sample=33000,label='singing')],
                        [dict(start_sample=0,end_sample=100,label='singing'),dict(start_sample=50,end_sample=120,label='speech')]):
            with self.assertRaises(LearningError):
                self.label('r0', regions=regions)

    def test_metadata_connected_groups_all_leakage_links(self):
        self.add(0, lineage={'regions':[{'sourceId':'pcm:guid:1'}]})
        self.add(1, parent='pcm:guid:1', lineage='')
        self.add(2, session='s1', content='song1')
        self.add(3, content='song1')
        self.add(4, parent='r3')
        self.add(5, seed=5)  # same original as r4
        groups, assignment, by = groups_for(self.store.snapshot('alice')['recordings'])
        self.assertEqual(len(groups), 1)
        self.assertEqual(len(set(by.values())), 1)

    def test_insufficient_data_no_reshuffle_or_fake_model(self):
        self.add(0)
        self.label('r0', regions=[dict(start_sample=0, end_sample=32000, label='singing')])
        report = self.pipeline.train('alice', 'one')
        self.assertEqual(report['status'], 'insufficient_data')
        self.assertNotIn('adapter', report)
        self.assertEqual(self.backend.calls, 0)

    def test_one_session_two_classes_updates_only_provisional_proposals(self):
        self.add(0, seed=1000)
        initial = self.pipeline.propose('alice', 'r0')
        self.label('r0')
        report = self.pipeline.train('alice', 'first-lesson')
        self.assertEqual(report['status'], 'candidate')
        self.assertEqual(report['model_stage'], 'provisional')
        self.assertEqual(report['evaluation_scope'], 'training_only_unvalidated_personalization')
        self.assertFalse(report['validation_available'])
        self.assertNotIn('validation_duration_confusion', report)
        self.assertEqual({p['split'] for p in report['predictions']}, {'training_only'})
        self.assertEqual(report['dataset_summary']['independent_group_count'], 1)
        proposed = self.pipeline.propose('alice', 'r0')
        self.assertEqual(proposed['model_source'], 'personal_provisional')
        self.assertFalse(proposed['model_provenance']['validation_available'])
        self.assertNotEqual(proposed['proposal_id'], initial['proposal_id'])
        np.testing.assert_allclose(proposed['scores'], np.asarray(proposed['prior_scores'])*.75 + np.asarray(proposed['personal_raw_scores'])*.25)
        self.assertEqual(proposed['score_blend']['personal_weight'], .25)
        self.assertGreater(np.abs(np.asarray(proposed['scores']) - np.asarray(proposed['prior_scores'])).max(), .01)
        self.assertIn('prior_adapter_sha256', proposed)
        self.assertEqual(self.store.recording('alice', 'r0')['annotation_revision'], 1)
        self.assertFalse(proposed['execution_authorized'])
        for i in (1, 2):
            self.add(i)
            self.label('r' + str(i))
        validated = self.pipeline.train('alice', 'later-lessons')
        self.assertEqual(validated['model_stage'], 'development_validated')
        self.assertIn('validation_duration_confusion', validated)
        self.assertEqual(self.pipeline.propose('alice', 'r0')['model_source'], 'personal')

    def test_tiny_opposite_annotation_does_not_claim_personal_fit(self):
        self.add(0)
        self.label('r0', regions=[dict(start_sample=0, end_sample=31000, label='singing'),
                                 dict(start_sample=31000, end_sample=32000, label='speech')])
        report = self.pipeline.train('alice', 'tiny')
        self.assertEqual(report['status'], 'insufficient_data')
        self.assertEqual(report['reasons'], ['need_confirmed_non_singing_example'])
        self.assertEqual(self.backend.calls, 0)

    def test_fit_validation_prediction_and_train_only_stats(self):
        self.dataset()
        report = self.pipeline.train('alice', 'fit')
        self.assertEqual(report['status'], 'candidate', report)
        self.assertFalse(report['execution_authorized'])
        self.assertEqual(len(report['dataset_summary']['groups']), 3)
        train = [p for p in report['predictions'] if p['split']=='train']
        valid = [p for p in report['predictions'] if p['split']=='validation']
        self.assertEqual((len(train),len(valid)), (2,1))
        self.assertEqual(report['validation_duration_confusion'], dict(tp_seconds=2.,fn_seconds=0.,fp_seconds=0.,tn_seconds=2.))
        self.assertEqual(valid[0]['predicted_ranges'], [dict(start_sample=0,end_sample=16000,status='candidate')])
        self.assertEqual(report['adapter']['standardizer']['fit_scope'], 'provided_training_rows_only')
        prediction = self.pipeline.shadow('alice', 'r0')
        self.assertFalse(prediction['stale'])
        self.assertEqual(self.backend.calls, 3)

    def test_validation_distribution_does_not_enter_standardizer(self):
        self.dataset()
        state=self.store.snapshot('alice')
        summary=dataset_summary(state['recordings'])
        heldout=next(rid for rid,gid in summary['recording_groups'].items() if summary['group_assignment'][gid]=='validation')
        extract=self.backend.extract
        def different_validation(path):
            result=extract(path)
            if Path(path).parent.name == heldout:
                for row in result['timeline']['scores']: row[6]=.999
            return result
        self.backend.extract=different_validation
        report=self.pipeline.train('alice','trainonly')
        self.assertEqual(report['status'],'candidate')
        arrays=[]
        train=[]
        for rid,rec in state['recordings'].items():
            _,features,_=feature_arrays(self.pipeline.feature('alice',rec),rec)
            arrays.append(features)
            if rid != heldout: train.append(features)
        mean=np.asarray(report['adapter']['standardizer']['mean'])
        np.testing.assert_allclose(mean,np.concatenate(train).mean(axis=0),atol=1e-12)
        self.assertGreater(np.abs(mean-np.concatenate(arrays).mean(axis=0)).max(),1.)

    def test_new_recording_default_shadow_outdated_then_correction_invalidates(self):
        self.dataset()
        self.pipeline.train('alice','fit')
        self.add(9)
        prediction = self.pipeline.shadow('alice','r9')
        self.assertTrue(prediction['stale'])
        self.assertFalse(prediction['invalidated'])
        self.label('r0', expected=1, source='mixed')
        with self.assertRaisesRegex(LearningError, 'Train after correcting'):
            self.pipeline.shadow('alice','r9')
        old = self.pipeline.shadow('alice','r9','fit')
        self.assertTrue(old['invalidated'])
        self.assertEqual(self.store.model('alice','fit')['source_snapshot']['r0']['annotation_revision'],1)

    def test_concurrent_annotation_during_fit_never_current(self):
        self.dataset()
        self.backend.entered, self.backend.release = threading.Event(), threading.Event()
        results=[]
        thread=threading.Thread(target=lambda:results.append(self.pipeline.train('alice','racing')))
        thread.start()
        self.assertTrue(self.backend.entered.wait(2))
        self.label('r0', expected=1, source='unknown')
        self.backend.release.set()
        thread.join(5)
        self.assertTrue(results[0]['invalidated_at_completion'])
        self.assertIsNone(self.store.snapshot('alice')['current_candidate'])

    def test_failed_train_preserves_source_revisions(self):
        self.dataset()
        self.backend.identity = lambda: (_ for _ in ()).throw(RuntimeError('fixture_backend_missing'))
        report = self.pipeline.train('alice','failure')
        self.assertEqual(report['status'],'failed')
        self.assertEqual(len(report['source_snapshot']),3)
        self.assertFalse(report['execution_authorized'])

    def test_feature_source_binding_and_cache(self):
        rec=self.add(0)
        one=self.pipeline.feature('alice',rec)
        self.pipeline.feature('alice',rec)
        self.assertEqual(self.backend.calls,1)
        one['source']['sample_count']+=1
        with self.assertRaises(LearningError):
            feature_arrays(one,rec)

    def test_single_worker_coalescing_async_persistence(self):
        self.dataset()
        jobs=JobQueue(self.store,self.pipeline)
        first=jobs.enqueue('alice','train',auto=True)
        second=jobs.enqueue('alice','train',auto=True)
        self.assertEqual(first['job_id'],second['job_id'])
        self.assertEqual(jobs.get(first['job_id'])['status'],'queued')
        jobs.start()
        deadline=time.monotonic()+5
        while jobs.get(first['job_id'])['status'] in ('queued','running') and time.monotonic()<deadline:
            time.sleep(.01)
        self.assertEqual(jobs.get(first['job_id'])['result']['status'],'candidate')
        jobs.close()
        reopened=JobQueue(self.store,self.pipeline)
        self.assertEqual(reopened.get(first['job_id'])['status'],'completed')
        reopened.close()

    def test_queue_bound_and_restart_interruption(self):
        self.add(0)
        config=Config(Path(self.directory.name),queue_limit=1)
        store=Store(config)
        jobs=JobQueue(store,Pipeline(store,self.backend))
        pending=jobs.enqueue('alice','features',dict(recording_id='r0'))
        with self.assertRaisesRegex(LearningError,'Foreground queue'):
            jobs.enqueue('alice','features',dict(recording_id='another-recording'))
        training=jobs.enqueue('alice','train')
        restarted=JobQueue(store,self.pipeline)
        self.assertEqual(restarted.get(pending['job_id'])['status'],'interrupted')
        self.assertEqual(restarted.get(training['job_id'])['status'],'interrupted')
        restarted.close()
        jobs.close()

    def test_store_lease_excludes_second_writer(self):
        one,two=ProcessLease(self.store.root),ProcessLease(self.store.root)
        one.acquire()
        try:
            with self.assertRaises(RuntimeError): two.acquire()
        finally:
            one.release()
        two.acquire()
        two.release()

    def test_first_proposal_needs_no_personal_model_and_never_labels(self):
        self.add(0)
        before = self.store.snapshot('alice')['profile']['dataset_revision']
        result = self.pipeline.propose('alice', 'r0')
        self.assertEqual(result['model_source'], 'bootstrap_development')
        self.assertFalse(result['model_provenance']['held_out_acceptance'])
        self.assertTrue(result['requires_confirmation'])
        self.assertFalse(result['execution_authorized'])
        self.assertEqual(result['regions'][0]['start_sample'], 0)
        self.assertEqual(result['regions'][-1]['end_sample'], 32000)
        self.assertEqual({r['label'] for r in result['regions']}, {'environment'})
        self.assertIsNone(self.store.recording('alice', 'r0')['annotation'])
        self.assertEqual(self.store.snapshot('alice')['profile']['dataset_revision'], before)
        self.assertEqual(self.pipeline.propose('alice', 'r0'), result)
        self.assertEqual(self.backend.calls, 1)
        self.assertEqual(self.pipeline.cached_proposal('alice', 'r0')['status'], 'ready')

    def test_proposal_personal_preferred_correction_falls_back_and_cas(self):
        self.dataset()
        self.pipeline.train('alice', 'personal')
        result = self.pipeline.propose('alice', 'r0')
        self.assertEqual(result['model_source'], 'personal')
        self.store.annotate('alice', 'r0', dict(expected_revision=1, confirmed=True, source_kind='user',
            regions=result['regions'], proposal_id=result['proposal_id'], correction_text='开头是说话'))
        annotation = self.store.recording('alice', 'r0')['annotation']
        self.assertEqual(annotation['assistance']['proposal_id'], result['proposal_id'])
        self.assertEqual(annotation['correction_text'], '开头是说话')
        self.assertEqual(self.pipeline.cached_proposal('alice', 'r0')['status'], 'stale')
        with self.assertRaisesRegex(LearningError, 'Refresh the proposed'):
            self.store.annotate('alice', 'r0', dict(expected_revision=2, confirmed=True, source_kind='user',
                regions=[], proposal_id=result['proposal_id']))
        fresh = self.pipeline.propose('alice', 'r0')
        self.assertEqual(fresh['model_source'], 'bootstrap_development')
        self.assertEqual(fresh['annotation_revision'], 2)
        self.assertNotEqual(fresh['proposal_id'], result['proposal_id'])
        self.assertEqual(self.store.recording('alice', 'r0')['annotation'], annotation)

    def test_proposal_source_hash_and_checkpoint_binding(self):
        self.add(0)
        self.pipeline.extract('alice', 'r0')
        path = self.store.audio_path('alice', 'r0')
        path.write_bytes(audio(3))
        with self.assertRaisesRegex(LearningError, 'proposal_audio_hash_changed'):
            self.pipeline.propose('alice', 'r0')
        self.add(1)
        extract = self.backend.extract
        def different_checkpoint(path):
            value = extract(path)
            value['model_version'] = 'unrelated-model'
            return value
        self.backend.extract = different_checkpoint
        with self.assertRaisesRegex(LearningError, 'bootstrap_checkpoint_mismatch'):
            self.pipeline.propose('alice', 'r1')

    def test_annotation_during_proposal_never_replaces_confirmed_revision(self):
        self.add(0)
        self.backend.entered, self.backend.release = threading.Event(), threading.Event()
        results = []
        thread = threading.Thread(target=lambda: results.append(self.pipeline.propose('alice', 'r0')))
        thread.start()
        self.assertTrue(self.backend.entered.wait(2))
        self.label('r0')
        self.backend.release.set()
        thread.join(5)
        self.assertEqual(results[0]['annotation_revision'], 0)
        current = self.store.recording('alice', 'r0')
        self.assertEqual(current['annotation_revision'], 1)
        self.assertNotIn('proposal_id', current)
        self.assertEqual(self.pipeline.cached_proposal('alice', 'r0')['status'], 'missing')


class HttpTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        import uvicorn
        cls.directory=tempfile.TemporaryDirectory()
        cls.backend=FakeBackend()
        cls.app=create_app(Config(Path(cls.directory.name),auto_train_debounce_seconds=.01),cls.backend)
        cls.socket=socket.socket()
        cls.socket.bind(('127.0.0.1',0))
        cls.port=cls.socket.getsockname()[1]
        cls.server=uvicorn.Server(uvicorn.Config(cls.app,log_level='error'))
        cls.thread=threading.Thread(target=lambda:cls.server.run(sockets=[cls.socket]),daemon=True)
        cls.thread.start()
        deadline=time.monotonic()+5
        while not cls.server.started and time.monotonic()<deadline: time.sleep(.01)
        if not cls.server.started: raise RuntimeError('test_server_start_failed')

    @classmethod
    def tearDownClass(cls):
        cls.server.should_exit=True
        cls.thread.join(6)
        cls.socket.close()
        cls.directory.cleanup()

    def request(self,path,value=None,method='GET',raw=None,content_type=None):
        data=raw if raw is not None else json.dumps(value).encode() if value is not None else None
        req=urllib.request.Request('http://127.0.0.1:'+str(self.port)+path,data=data,method=method)
        if data is not None: req.add_header('Content-Type',content_type or 'application/json')
        opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
        try:
            with opener.open(req,timeout=4) as response: return response.status,response.read()
        except urllib.error.HTTPError as response: return response.code,response.read()

    def test_http_full_capture_confirmation_poll_audio_and_revision(self):
        status,body=self.request('/health')
        self.assertEqual(status,200)
        self.assertIn('acoustic_proposals',json.loads(body)['capabilities'])
        self.assertEqual(self.backend.calls,0)
        self.assertEqual(self.request('/learning/profiles',dict(profile_id='bob'),'POST')[0],200)
        self.assertEqual(self.request('/learning/profiles/bob/sessions',dict(session_id='s',content_group='song'),'POST')[0],200)
        manifest=dict(recording_id='r',session_id='s',content_group='song',capture_serial=1,
                      source_kind='unknown',parent_recording_id='pcm:guid:3',lineage_json='')
        raw=audio(10)
        boundary='SyntheticLearningBoundary'
        multipart=(('--'+boundary+'\r\nContent-Disposition: form-data; name="manifest"\r\n\r\n'+json.dumps(manifest)+'\r\n--'+boundary+'\r\nContent-Disposition: form-data; name="audio_file"; filename="arbitrary.wav"\r\nContent-Type: audio/wav\r\n\r\n').encode()+raw+('\r\n--'+boundary+'--\r\n').encode())
        status,body=self.request('/learning/profiles/bob/recordings',method='POST',raw=multipart,content_type='multipart/form-data; boundary='+boundary)
        self.assertEqual(status,200,body)
        rec=json.loads(body)
        self.assertEqual(rec['sample_count'],32000)
        self.assertEqual(self.request('/learning/profiles/bob/recordings/r/audio')[1],raw)
        self.assertIn('proposal_job_id',rec)
        deadline=time.monotonic()+4
        while time.monotonic()<deadline:
            value=json.loads(self.request('/learning/jobs/'+rec['proposal_job_id'])[1])
            if value['status'] not in ('queued','running'): break
            time.sleep(.02)
        self.assertEqual(value['status'],'completed',value)
        proposal=value['result']
        self.assertEqual(proposal['model_source'],'bootstrap_development')
        route='/learning/profiles/bob/recordings/r/proposal'
        self.assertEqual(json.loads(self.request(route)[1])['proposal']['proposal_id'],proposal['proposal_id'])
        self.assertEqual(json.loads(self.request(route,{},'POST')[1])['status'],'ready')
        annotation=dict(expected_revision=0,confirmed=True,source_kind='user',proposal_id=proposal['proposal_id'],
                        correction_text='我确认前半段是唱歌',regions=[dict(start_sample=0,end_sample=16000,label='singing',tolerance_samples=0)])
        status,body=self.request('/learning/profiles/bob/recordings/r/annotations',annotation,'PUT')
        self.assertEqual(status,200,body)
        job=json.loads(body)['auto_train_job_id']
        deadline=time.monotonic()+4
        while time.monotonic()<deadline:
            status,body=self.request('/learning/jobs/'+job)
            value=json.loads(body)
            if value['status'] not in ('queued','running'): break
            time.sleep(.02)
        self.assertEqual(value['result']['status'],'insufficient_data')
        self.assertEqual(self.request('/learning/profiles/bob/recordings/r/annotations',annotation,'PUT')[0],409)
        self.assertEqual(json.loads(self.request(route)[1])['status'],'stale')
        self.assertEqual(json.loads(self.request('/learning/profiles/bob/status')[1])['recording_count'],1)


if __name__ == '__main__':
    unittest.main()
