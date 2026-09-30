"""Isolated service integration; synthetic WAVs and explicit mock guard fixtures.

The mocked evaluation cases exercise adoption checks, not model accuracy.
The ordinary-training case runs the real frontend, fit, folds and guard.
"""
import copy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import wave

import numpy as np

from . import (fusion_pipeline, pitch_features, proposals, sequence_evaluation,
               sequence_features, sequence_model, sequence_pipeline)
from .config import Config
from .pipeline import Pipeline
from .store import Store
from .test_learning import FakeBackend


class SequencePipelineTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(Config(Path(self.directory.name), sequence_boundary_enabled=True, local_boundary_enabled=False))
        self.store.create_profile(dict(profile_id='alice'))
        self.backend = FakeBackend()
        self.pipeline = Pipeline(self.store, self.backend)

    def tearDown(self):
        self.directory.cleanup()

    def add(self, number, *, sample_count=16000, silence=False):
        rid = f'r{number}'
        session = f's{number}'
        self.store.create_session('alice', dict(session_id=session, content_group=f'content{number}'))
        samples = np.zeros(sample_count, dtype='<i2') if silence else (
            np.sin(2 * np.pi * (180 + 37 * number) * np.arange(sample_count) / 16000.) * 6000).astype('<i2')
        buffer = io.BytesIO()
        with wave.open(buffer, 'wb') as writer:
            writer.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
            writer.writeframes(samples.tobytes())
        self.store.upload('alice', dict(recording_id=rid, session_id=session,
            content_group=f'content{number}', capture_serial=number), buffer.getvalue())
        self.store.annotate('alice', rid, dict(expected_revision=0, confirmed=True, source_kind='user',
            regions=[dict(start_sample=0, end_sample=sample_count // 2, label='singing', tolerance_samples=0),
                     dict(start_sample=sample_count // 2, end_sample=sample_count, label='speech', tolerance_samples=0)]))
        return self.store.recording('alice', rid)

    def dataset(self):
        for index in range(3):
            self.add(index)
        return self.store.snapshot('alice')['recordings']

    @staticmethod
    def fusion_disabled_fixture(*args, **kwargs):
        return dict(status='shadow_rejected', teaching_eligible=False, production_eligible=False,
                    execution_authorized=False, eligibility_reasons=['synthetic_test_isolates_sequence_branch'])

    @staticmethod
    def accepted_evaluation_fixture(records, prepared, native, coarse, bootstrap, groups, **kwargs):
        # Deliberately use a perfect-reference mask in this synthetic test only.
        # The candidate itself is genuinely fit/source-bound; scores here are not
        # represented as its accuracy, only as a controlled guard-input fixture.
        candidate = sequence_model.fit_candidate(prepared)
        rows = []
        for rid, item in prepared.items():
            mask = item['edges'][1:] <= records[rid]['sample_count'] // 2
            rows.append(sequence_evaluation.measure(records[rid], item['edges'], mask.astype(float), mask))
        summary = sequence_evaluation.aggregate(rows)
        references = {name: copy.deepcopy(summary) for name in sequence_evaluation.REFERENCES}
        guard = sequence_evaluation.evaluate_guard(summary, references, fold_count=len(groups), complete=True)
        candidate.update(teaching_eligible=guard['teaching_eligible'], evaluation_guard=guard,
                         eligibility_reasons=guard['eligibility_reasons'])
        return dict(candidates={'sequence': candidate}, candidate=candidate,
                    guards={'sequence': guard}, guard=guard,
                    summaries=dict(candidate=summary, **references), fold_count=len(groups), complete=True)

    def train_accepted_fixture(self, model_id='accepted-fixture'):
        with patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture), \
             patch.object(sequence_evaluation, 'evaluate_prepared', side_effect=self.accepted_evaluation_fixture):
            return self.pipeline.train('alice', model_id)

    def only_cache(self):
        files = list((self.store.profile_path('alice') / 'sequence_features').glob('*.npz'))
        self.assertEqual(len(files), 1)
        return files[0]

    def edit_cache(self, transform):
        path = self.only_cache()
        with np.load(path, allow_pickle=False) as values:
            edges, features = values['edges'].copy(), values['features'].copy()
            saved = json.loads(str(values['metadata_json']))
        transform(edges, features, saved)
        with path.open('wb') as stream:
            np.savez_compressed(stream, edges=edges, features=features,
                                metadata_json=np.asarray(json.dumps(saved, allow_nan=False)))

    def test_prepare_real_frontend_cache_hit_and_raw_source_binding(self):
        rec = self.add(0, sample_count=16017)
        fine, native, coarse = sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        self.assertEqual(fine['features'].shape, (51, 63))
        np.testing.assert_array_equal(fine['edges'][-3:], [15680, 16000, 16017])
        self.assertEqual(native['features'].shape, (4, 25))
        self.assertEqual(coarse['features'].shape, (4, 9))
        with patch.object(sequence_features, 'build_features', side_effect=AssertionError('cache should avoid frontend replay')):
            cached, _, _ = sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        np.testing.assert_array_equal(cached['features'], fine['features'])
        self.assertEqual(self.backend.calls, 1)
        with np.load(self.only_cache(), allow_pickle=False) as values:
            saved = json.loads(str(values['metadata_json']))
        with wave.open(str(self.store.audio_path('alice', rec['recording_id'])), 'rb') as source:
            pcm = source.readframes(source.getnframes())
        self.assertEqual(saved['metadata']['source']['pcm_sha256'], hashlib.sha256(pcm).hexdigest())
        self.assertEqual(saved['metadata']['timeline']['combined_features_available_samples'], [16017] * 51)

    def test_feature_definition_change_invalidates_prepared_cache(self):
        rec = self.add(0)
        sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        original = sequence_features.current_definition
        def changed(*args):
            definition = original(*args)
            definition['synthetic_definition_revision'] = 2
            return definition
        with patch.object(sequence_features, 'current_definition', side_effect=changed), \
             patch.object(sequence_features, 'build_features', wraps=sequence_features.build_features) as rebuilt:
            item, _, _ = sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        self.assertEqual(rebuilt.call_count, 1)
        self.assertEqual(item['feature_metadata']['synthetic_definition_revision'], 2)
        self.assertEqual(len(list((self.store.profile_path('alice') / 'sequence_features').glob('*.npz'))), 2)

    def test_corrupt_feature_values_rebuild_instead_of_becoming_evidence(self):
        rec = self.add(0)
        expected, _, _ = sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        self.edit_cache(lambda edges, values, metadata: values.__setitem__((0, 0), values[0, 0] + 1.))
        with patch.object(sequence_features, 'build_features', wraps=sequence_features.build_features) as rebuilt:
            item, _, _ = sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        self.assertEqual(rebuilt.call_count, 1)
        np.testing.assert_array_equal(item['features'], expected['features'])

    def test_interior_edge_tamper_rebuilds_even_when_shape_and_features_match(self):
        rec = self.add(0)
        expected, _, _ = sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        self.edit_cache(lambda edges, values, metadata: edges.__setitem__(1, edges[1] + 1))
        with patch.object(sequence_features, 'build_features', wraps=sequence_features.build_features) as rebuilt:
            item, _, _ = sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        self.assertEqual(rebuilt.call_count, 1)
        np.testing.assert_array_equal(item['edges'], expected['edges'])

    def test_cache_metadata_source_tamper_is_rebuilt(self):
        rec = self.add(0)
        sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        def change(edges, values, saved):
            saved['metadata']['source']['pcm_float32_sha256'] = '0' * 64
        self.edit_cache(change)
        with patch.object(sequence_features, 'build_features', wraps=sequence_features.build_features) as rebuilt:
            sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        self.assertEqual(rebuilt.call_count, 1)

    def test_changed_pitch_source_cannot_reuse_prepared_cache(self):
        rec = self.add(0)
        sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        track = copy.deepcopy(fusion_pipeline.pitch_track(self.store, 'alice', rec))
        track['source']['pcm_sha256'] = '0' * 64
        with patch.object(fusion_pipeline, 'pitch_track', return_value=track):
            with self.assertRaisesRegex(ValueError, 'source_content_mismatch'):
                sequence_pipeline.prepare(self.pipeline, 'alice', rec)

    def test_original_wav_change_is_rejected_even_if_fine_cache_exists(self):
        rec = self.add(0)
        sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        path = self.store.audio_path('alice', rec['recording_id'])
        raw = bytearray(path.read_bytes())
        raw[-1] ^= 1
        path.write_bytes(raw)
        with self.assertRaises(Exception):
            sequence_pipeline.prepare(self.pipeline, 'alice', rec)

    def test_ordinary_train_saves_nested_guard_without_mutating_labels_or_audio(self):
        records = self.dataset()
        originals = {rid: self.store.audio_path('alice', rid).read_bytes() for rid in records}
        before = self.store.snapshot('alice')['profile']['dataset_revision']
        with patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture):
            report = self.pipeline.train('alice', 'ordinary-sequence')
        self.assertEqual(report['status'], 'candidate', report)
        nested = report['sequence_boundary']
        self.assertIn(nested['status'], ('eligible', 'shadow_rejected'), nested)
        self.assertTrue(nested['complete'])
        self.assertEqual(nested['fold_count'], 3)
        self.assertFalse(nested['production_eligible'])
        self.assertFalse(nested['execution_authorized'])
        after = self.store.snapshot('alice')
        self.assertEqual(after['profile']['dataset_revision'], before)
        self.assertEqual({rid: rec['annotation'] for rid, rec in records.items()},
                         {rid: rec['annotation'] for rid, rec in after['recordings'].items()})
        for rid, original in originals.items():
            self.assertEqual(self.store.audio_path('alice', rid).read_bytes(), original)
        summary = after['current_candidate']['sequence_boundary']
        self.assertEqual(summary['teaching_eligible'], nested['teaching_eligible'])
        self.assertNotIn('candidate', summary)

    def test_insufficient_holdout_guard_falls_back_to_existing_proposal(self):
        self.add(0)
        with patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture):
            report = self.pipeline.train('alice', 'one-sequence')
        self.assertEqual(report['sequence_boundary']['status'], 'shadow_rejected')
        self.assertFalse(report['sequence_boundary']['teaching_eligible'])
        proposal = self.pipeline.propose('alice', 'r0')
        self.assertEqual(proposal['model_source'], 'personal_provisional')
        self.assertTrue(proposal['requires_confirmation'])

    def test_eligible_mock_guard_rechecks_statistics_source_and_feature_definition(self):
        records = self.dataset()
        report = self.train_accepted_fixture()
        self.assertIsNotNone(sequence_pipeline.eligible_candidate(report, records), report.get('sequence_boundary'))
        changed = copy.deepcopy(report)
        changed['sequence_boundary']['summaries']['candidate']['metrics']['speech_fp_seconds'] += 1.
        self.assertIsNone(sequence_pipeline.eligible_candidate(changed, records))
        changed = copy.deepcopy(records)
        changed['r0']['annotation_revision'] += 1
        self.assertIsNone(sequence_pipeline.eligible_candidate(report, changed))
        with patch.dict(pitch_features.FEATURE_POLICY, version='incompatible-test-version'):
            self.assertIsNone(sequence_pipeline.eligible_candidate(report, records))
        plan = proposals.plan(self.store, self.backend, 'alice', 'r0')[-1]
        self.assertEqual(plan['model_source'], 'personal_sequence_boundary')
        self.assertEqual(plan['sequence_candidate_id'], report['sequence_boundary']['candidate']['candidate_id'])
        proposal = self.pipeline.propose('alice', 'r0')
        self.assertEqual(proposal['model_source'], 'personal_sequence_boundary')
        self.assertEqual(len(proposal['selected_mask']), 50)
        self.assertEqual(proposal['source_cell_edges'], list(range(0, 16001, 320)))
        self.assertTrue(proposal['requires_confirmation'])
        self.assertFalse(proposal['execution_authorized'])

    def test_sequence_mask_keeps_unvoiced_cells_short_tail_and_explicit_interrupt(self):
        rec = self.add(0, sample_count=1617, silence=True)
        frozen = self.pipeline.feature('alice', rec)
        item, _, _ = sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        mask = np.asarray([True, True, False, True, True, True])
        prediction = dict(scores=np.where(mask, .8, .1), selected_mask=mask)
        binding = proposals.plan(self.store, self.backend, 'alice', 'r0')[-1]
        proposal = sequence_pipeline.make_proposal(self.pipeline, 'alice', rec, frozen, binding, item, prediction)
        self.assertEqual([(r['start_sample'], r['end_sample'], r['label']) for r in proposal['regions']],
                         [(0, 640, 'singing'), (640, 960, 'environment'), (960, 1617, 'singing')])
        self.assertEqual(proposal['selected_mask'], mask.tolist())
        self.assertEqual(proposal['source_cell_edges'][-1], 1617)
        self.assertFalse(proposal['execution_authorized'])
        self.assertTrue(proposal['requires_confirmation'])

    def test_runtime_prediction_failure_preserves_legacy_proposal(self):
        self.dataset()
        self.train_accepted_fixture()
        with patch.object(sequence_pipeline, 'proposal_prediction', side_effect=RuntimeError('synthetic cache failure')):
            proposal = self.pipeline.propose('alice', 'r0')
        self.assertEqual(proposal['model_source'], 'personal')
        self.assertTrue(proposal['requires_confirmation'])
        self.assertFalse(proposal['execution_authorized'])

    def test_config_disabled_keeps_existing_fusion_path_and_skips_sequence_work(self):
        self.dataset()
        off = Store(Config(Path(self.directory.name), sequence_boundary_enabled=False))
        pipeline = Pipeline(off, self.backend)
        with patch.object(sequence_pipeline, 'train', side_effect=AssertionError('disabled sequence must not run')):
            report = pipeline.train('alice', 'sequence-disabled')
        self.assertNotIn('sequence_boundary', report)
        self.assertTrue(report['temporal_fusion']['teaching_eligible'], report['temporal_fusion'])
        self.assertEqual(pipeline.propose('alice', 'r0')['model_source'], 'personal_temporal_fusion')


if __name__ == '__main__':
    unittest.main()
