"""Synthetic end-to-end guarded fusion, isolated from user recordings."""
import copy
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import wave

import numpy as np

from . import fusion_pipeline, pitch_features, temporal_fusion
from .config import Config
from .pipeline import Pipeline
from .store import Store
from .test_learning import FakeBackend


class FusionPipelineTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(Config(Path(self.directory.name), sequence_boundary_enabled=False))
        self.store.create_profile(dict(profile_id='alice'))
        self.store.create_session('alice', dict(session_id='session', content_group='song'))
        self.pipeline = Pipeline(self.store, FakeBackend())

    def tearDown(self):
        self.directory.cleanup()

    def add(self, number, rate=16000, source='user', confirmed=True):
        rid = 'r' + str(number)
        t = np.arange(rate * 4) / rate
        samples = (np.sin(2 * np.pi * (180 + 20 * number) * t) * 6000).astype('<i2')
        buffer = io.BytesIO()
        with wave.open(buffer, 'wb') as audio:
            audio.setparams((1, 2, rate, 0, 'NONE', 'not compressed'))
            audio.writeframes(samples.tobytes())
        self.store.upload('alice', dict(recording_id=rid, session_id='session', content_group='song', capture_serial=number), buffer.getvalue())
        if confirmed:
            self.store.annotate('alice', rid, dict(expected_revision=0, confirmed=True, source_kind=source,
                regions=[dict(start_sample=0, end_sample=rate * 2, label='singing', tolerance_samples=0),
                         dict(start_sample=rate * 2, end_sample=rate * 4, label='speech', tolerance_samples=0)]))
        return self.store.recording('alice', rid)

    def dataset(self):
        for index in range(3):
            self.add(index)
        return self.store.snapshot('alice')['recordings']

    def test_ordinary_train_saves_guarded_candidate_and_summary_without_label_changes(self):
        records = self.dataset()
        report = self.pipeline.train('alice', 'fusion-lesson')
        self.assertEqual(report['status'], 'candidate')
        nested = report['temporal_fusion']
        self.assertEqual(nested['status'], 'eligible', nested)
        self.assertTrue(nested['complete'])
        self.assertEqual(nested['fold_count'], 3)
        self.assertEqual(nested['candidate']['policy']['speech_cost'], 2.)
        self.assertFalse(nested['production_eligible'])
        self.assertFalse(nested['execution_authorized'])
        self.assertEqual(self.store.snapshot('alice')['profile']['dataset_revision'], 6)
        self.assertEqual({rid: rec['annotation'] for rid, rec in records.items()},
                         {rid: rec['annotation'] for rid, rec in self.store.snapshot('alice')['recordings'].items()})
        summary = self.store.snapshot('alice')['current_candidate']['temporal_fusion']
        self.assertTrue(summary['teaching_eligible'])
        self.assertNotIn('candidate', summary)

    def test_only_eligible_bound_head_drives_candidate_proposals(self):
        self.dataset()
        report = self.pipeline.train('alice', 'fusion-lesson')
        self.assertTrue(report['temporal_fusion']['teaching_eligible'])
        proposal = self.pipeline.propose('alice', 'r0')
        self.assertEqual(proposal['model_source'], 'personal_temporal_fusion')
        self.assertFalse(proposal['execution_authorized'])
        self.assertTrue(proposal['requires_confirmation'])
        self.assertNotIn('score_blend', proposal)
        self.assertFalse(proposal['model_provenance']['validation_available'])
        self.assertEqual(proposal['fusion_candidate_id'], report['temporal_fusion']['candidate']['candidate_id'])

    def test_insufficient_folds_reject_without_changing_legacy_proposal(self):
        self.add(0)
        report = self.pipeline.train('alice', 'one-lesson')
        self.assertEqual(report['status'], 'candidate')
        self.assertEqual(report['temporal_fusion']['status'], 'shadow_rejected')
        self.assertFalse(report['temporal_fusion']['teaching_eligible'])
        proposal = self.pipeline.propose('alice', 'r0')
        self.assertEqual(proposal['model_source'], 'personal_provisional')
        self.assertIn('score_blend', proposal)

    def test_fusion_failure_is_isolated_and_other_formats_keep_legacy(self):
        self.add(0, rate=8000)
        report = self.pipeline.train('alice', 'eight-k')
        self.assertEqual(report['status'], 'candidate')
        self.assertEqual(report['temporal_fusion']['status'], 'failed')
        self.assertIn('mono_16000', report['temporal_fusion']['error'])
        self.assertEqual(self.pipeline.propose('alice', 'r0')['model_source'], 'personal_provisional')
        with patch.object(fusion_pipeline, 'train', side_effect=RuntimeError('synthetic failure')):
            second = self.pipeline.train('alice', 'error-lesson')
        self.assertEqual(second['status'], 'candidate')
        self.assertEqual(second['temporal_fusion']['status'], 'failed')

    def test_guard_boolean_tamper_source_change_and_feature_version_are_rejected(self):
        records = self.dataset()
        report = self.pipeline.train('alice', 'guard-test')
        self.assertIsNotNone(fusion_pipeline.eligible_candidate(report, records))
        tampered = copy.deepcopy(report)
        tampered['temporal_fusion']['summaries']['candidate']['metrics']['speech_fp_seconds'] = 20
        self.assertIsNone(fusion_pipeline.eligible_candidate(tampered, records))
        changed = copy.deepcopy(records)
        changed['r0']['annotation_revision'] += 1
        self.assertIsNone(fusion_pipeline.eligible_candidate(report, changed))
        with patch.dict(pitch_features.FEATURE_POLICY, version='future-incompatible-policy'):
            self.assertIsNone(fusion_pipeline.eligible_candidate(report, records))

    def test_runtime_pitch_failure_uses_legacy_instead_of_failing_proposal(self):
        self.dataset()
        self.pipeline.train('alice', 'runtime-failure')
        with patch.object(fusion_pipeline, 'proposal_scores', side_effect=RuntimeError('cache unavailable')):
            proposal = self.pipeline.propose('alice', 'r0')
        self.assertEqual(proposal['model_source'], 'personal_provisional')
        self.assertFalse(proposal['execution_authorized'])

    def test_playback_mixed_and_unconfirmed_never_enter_nested_training(self):
        self.dataset()
        self.add(3, source='playback')
        self.add(4, source='mixed')
        self.add(5, confirmed=False)
        report = self.pipeline.train('alice', 'sources')
        sources = report['temporal_fusion']['candidate']['source_bindings']
        self.assertEqual(set(sources), {'r0', 'r1', 'r2'})

    def test_pcm_duplicate_prefixes_stay_together_and_reduce_training_mass(self):
        records = self.dataset()
        original = self.store.audio_path('alice', 'r0').read_bytes()
        self.store.upload('alice', dict(recording_id='same', session_id='session', content_group='song', capture_serial=4), original)
        self.store.annotate('alice', 'same', dict(expected_revision=0, confirmed=True, source_kind='user',
            regions=copy.deepcopy(records['r0']['annotation']['regions'])))
        report = self.pipeline.train('alice', 'duplicates')['temporal_fusion']
        self.assertEqual(report['fold_count'], 3)
        self.assertEqual(report['exact_prefix_relations']['same']['r0'], 64000)
        self.assertAlmostEqual(report['candidate']['deduplicated_known_seconds']['singing'], 6.)
        self.assertTrue(any(set(fold['test_ids']) == {'r0', 'same'} for fold in report['folds']))

    def test_guard_duration_metrics_match_known_sample_overlap(self):
        rec = self.add(0)
        item, _ = fusion_pipeline.prepare(self.pipeline, 'alice', rec)
        measured = fusion_pipeline.measure(rec, item, np.ones(4))
        self.assertEqual(measured['metrics']['speech_fp_seconds'], 2.)
        self.assertEqual(measured['metrics']['fn_seconds'], 0.)
        missed = fusion_pipeline.measure(rec, item, np.zeros(4))
        self.assertEqual(missed['metrics']['fn_seconds'], 2.)
        self.assertEqual(missed['boundaries']['completely_missed_gold_count'], 1)


if __name__ == '__main__':
    unittest.main()
