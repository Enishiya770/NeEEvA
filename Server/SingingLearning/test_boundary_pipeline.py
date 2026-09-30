"""Synthetic end-to-end local-boundary learning and guarded teaching routing.

Accepted-evaluation fixtures exercise routing only; their controlled perfect
metrics are not model-accuracy claims. The ordinary-training test performs real
source-group holdouts, fits and checks without editing any original annotation.
"""
import copy
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import wave

import numpy as np

from . import (boundary_evaluation, boundary_model, boundary_pipeline,
               fusion_pipeline, pitch_features, proposals, sequence_evaluation,
               sequence_pipeline)
from .config import Config
from .pipeline import Pipeline
from .store import Store
from .test_learning import FakeBackend


class BoundaryBackend(FakeBackend):
    def extract(self, path):
        result = super().extract(path)
        scores = result['timeline']['scores']
        result['timeline']['scores'] = [scores[2], scores[0], scores[1], scores[3]]
        return result


class BoundaryPipelineTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(Config(Path(self.directory.name), sequence_boundary_enabled=True,
                                  local_boundary_enabled=True, category_evidence_enabled=False))
        self.store.create_profile(dict(profile_id='alice'))
        self.backend = BoundaryBackend()
        self.pipeline = Pipeline(self.store, self.backend)

    def tearDown(self):
        self.directory.cleanup()

    def add(self, number, *, sample_count=16000, silence=False, source='user', censored_onset=False):
        rid, session = f'r{number}', f's{number}'
        self.store.create_session('alice', dict(session_id=session, content_group=f'content{number}'))
        position = np.arange(sample_count)
        frequency = np.where((position >= sample_count // 4) & (position < 3 * sample_count // 4),
                             220 + number * 31, 130 + number * 19)
        samples = np.zeros(sample_count, dtype='<i2') if silence else (
            np.sin(2 * np.pi * np.cumsum(frequency) / 16000.) * 6000).astype('<i2')
        buffer = io.BytesIO()
        with wave.open(buffer, 'wb') as writer:
            writer.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
            writer.writeframes(samples.tobytes())
        self.store.upload('alice', dict(recording_id=rid, session_id=session,
            content_group=f'content{number}', capture_serial=number), buffer.getvalue())
        regions = [dict(start_sample=0, end_sample=sample_count // 4, label='speech', tolerance_samples=0),
                   dict(start_sample=sample_count // 4, end_sample=3 * sample_count // 4, label='singing', tolerance_samples=0),
                   dict(start_sample=3 * sample_count // 4, end_sample=sample_count, label='speech', tolerance_samples=0)]
        if censored_onset:
            regions = [dict(start_sample=0, end_sample=sample_count // 2, label='singing', tolerance_samples=0),
                       dict(start_sample=sample_count // 2, end_sample=sample_count, label='speech', tolerance_samples=0)]
        self.store.annotate('alice', rid, dict(expected_revision=0, confirmed=True, source_kind=source, regions=regions))
        return self.store.recording('alice', rid)

    def dataset(self, *, censored_onset=False):
        for index in range(3):
            self.add(index, censored_onset=censored_onset)
        return self.store.snapshot('alice')['recordings']

    @staticmethod
    def fusion_disabled_fixture(*args, **kwargs):
        return dict(status='shadow_rejected', teaching_eligible=False, production_eligible=False,
                    execution_authorized=False, eligibility_reasons=['synthetic_test_isolates_local_boundary'])

    @staticmethod
    def accepted_evaluation_fixture(records, prepared, native, coarse, bootstrap, groups, **kwargs):
        # Genuine source-bound fitted heads plus a controlled guard-input fixture.
        # This does not assert that these heads achieve the manufactured metrics.
        candidate = boundary_model.fit_candidate(prepared)
        rows = []
        for rid, item in prepared.items():
            count = records[rid]['sample_count']
            mask = (item['edges'][:-1] >= count // 4) & (item['edges'][1:] <= 3 * count // 4)
            rows.append(boundary_evaluation.measure(records[rid], item['edges'], mask.astype(float), mask))
        summary = boundary_evaluation.aggregate(rows)
        references = {name: copy.deepcopy(summary) for name in sequence_evaluation.REFERENCES}
        available = candidate['boundary_training_available']
        guard = boundary_evaluation.evaluate_guard(summary, references, fold_count=len(groups),
                                                   complete=True, training_available=available)
        sequence_guard = sequence_evaluation.evaluate_guard(summary, references,
                                                            fold_count=len(groups), complete=True)
        candidate.update(teaching_eligible=guard['teaching_eligible'], evaluation_guard=guard,
                         eligibility_reasons=guard['eligibility_reasons'])
        seed = candidate['sequence_candidate']
        seed.update(teaching_eligible=sequence_guard['teaching_eligible'], evaluation_guard=sequence_guard,
                    eligibility_reasons=sequence_guard['eligibility_reasons'])
        return dict(candidates={'boundary': candidate, 'sequence': seed}, candidate=candidate,
                    guards={'boundary': guard, 'sequence': sequence_guard}, guard=guard,
                    summaries=dict(candidate=summary, sequence=summary, **references),
                    fold_count=len(groups), complete=True, boundary_training_complete=available)

    def train_accepted_fixture(self, model_id='accepted-boundary-fixture'):
        with patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture), \
             patch.object(boundary_evaluation, 'evaluate_prepared', side_effect=self.accepted_evaluation_fixture):
            return self.pipeline.train('alice', model_id)

    def test_ordinary_train_runs_shared_holdouts_and_preserves_all_user_evidence(self):
        records = self.dataset()
        originals = {rid: self.store.audio_path('alice', rid).read_bytes() for rid in records}
        revision = self.store.snapshot('alice')['profile']['dataset_revision']
        with patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture), \
             patch.object(sequence_pipeline, 'train', side_effect=AssertionError('shared controls must not fit twice')):
            report = self.pipeline.train('alice', 'ordinary-local-boundary')
        self.assertEqual(report['status'], 'candidate', report)
        nested = report['local_boundary']
        self.assertIn(nested['status'], ('eligible', 'shadow_rejected'), nested)
        self.assertEqual(nested['fold_count'], 3)
        self.assertTrue(nested['complete'])
        self.assertTrue(nested['boundary_training_complete'])
        self.assertFalse(nested['production_eligible'])
        self.assertFalse(nested['execution_authorized'])
        control = report['sequence_boundary']
        self.assertEqual(control['candidate'], nested['candidates']['sequence'])
        self.assertEqual(control['guard'], nested['guards']['sequence'])
        self.assertIn('same local-boundary folds', control['evaluation_reuse'])
        after = self.store.snapshot('alice')
        self.assertEqual(after['profile']['dataset_revision'], revision)
        self.assertEqual({rid: rec['annotation'] for rid, rec in records.items()},
                         {rid: rec['annotation'] for rid, rec in after['recordings'].items()})
        for rid, audio in originals.items():
            self.assertEqual(self.store.audio_path('alice', rid).read_bytes(), audio)
        self.assertEqual(after['current_candidate']['local_boundary']['teaching_eligible'], nested['teaching_eligible'])
        self.assertNotIn('candidate', after['current_candidate']['local_boundary'])

    def test_accepted_fixture_routes_through_bound_local_candidate_and_full_proposal(self):
        records = self.dataset()
        report = self.train_accepted_fixture()
        candidate = boundary_pipeline.eligible_candidate(report, records)
        self.assertIsNotNone(candidate, report.get('local_boundary'))
        self.assertTrue(candidate['boundary_training_available'])
        binding = proposals.plan(self.store, self.backend, 'alice', 'r0')[-1]
        self.assertEqual(binding['model_source'], 'personal_local_boundary')
        self.assertEqual(binding['boundary_candidate_id'], candidate['candidate_id'])
        self.assertNotIn('sequence_candidate_id', binding)
        result = self.pipeline.propose('alice', 'r0')
        self.assertEqual(result['model_source'], 'personal_local_boundary')
        self.assertEqual(result['decoding'], 'local_onset_offset_gated_final_viterbi')
        self.assertEqual(result['source_cell_edges'], list(range(0, 16001, 320)))
        self.assertTrue(result['requires_confirmation'])
        self.assertFalse(result['execution_authorized'])
        self.assertEqual(result['audio_sha256'], records['r0']['audio_sha256'])
        self.assertEqual(result['annotation_revision'], records['r0']['annotation_revision'])

    def test_guard_rechecks_metrics_availability_source_feature_and_policy(self):
        records = self.dataset()
        report = self.train_accepted_fixture()
        self.assertIsNotNone(boundary_pipeline.eligible_candidate(report, records))
        changed = copy.deepcopy(report)
        changed['local_boundary']['summaries']['candidate']['metrics']['speech_fp_seconds'] += 1.
        self.assertIsNone(boundary_pipeline.eligible_candidate(changed, records))
        changed = copy.deepcopy(report)
        changed['local_boundary']['boundary_training_complete'] = False
        self.assertIsNone(boundary_pipeline.eligible_candidate(changed, records))
        changed = copy.deepcopy(records)
        changed['r0']['annotation_revision'] += 1
        self.assertIsNone(boundary_pipeline.eligible_candidate(report, changed))
        changed = copy.deepcopy(records)
        changed['r0']['audio_sha256'] = '0' * 64
        self.assertIsNone(boundary_pipeline.eligible_candidate(report, changed))
        with patch.dict(pitch_features.FEATURE_POLICY, version='incompatible-local-test'):
            self.assertIsNone(boundary_pipeline.eligible_candidate(report, records))
        with patch.dict(boundary_model.POLICY, boundary_gate=.6):
            self.assertIsNone(boundary_pipeline.eligible_candidate(report, records))

    def test_missing_true_onset_cannot_be_adopted_despite_controlled_good_metrics(self):
        records = self.dataset(censored_onset=True)
        report = self.train_accepted_fixture()
        local = report['local_boundary']
        self.assertFalse(local['candidate']['boundary_training_available'])
        self.assertFalse(local['teaching_eligible'])
        self.assertIn('local_boundary_training_unavailable', local['eligibility_reasons'])
        self.assertIsNone(boundary_pipeline.eligible_candidate(report, records))
        self.assertNotEqual(proposals.plan(self.store, self.backend, 'alice', 'r0')[-1]['model_source'], 'personal_local_boundary')

    def test_failed_holdout_guard_preserves_existing_proposal_and_original_source(self):
        rec = self.add(0)
        with patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture):
            report = self.pipeline.train('alice', 'one-local-recording')
        self.assertEqual(report['local_boundary']['status'], 'shadow_rejected')
        result = self.pipeline.propose('alice', 'r0')
        self.assertEqual(result['model_source'], 'personal_provisional')
        self.assertEqual(result['audio_sha256'], rec['audio_sha256'])
        self.assertEqual(result['annotation_revision'], rec['annotation_revision'])
        self.assertTrue(result['requires_confirmation'])

    def test_runtime_failure_falls_back_to_rechecked_sequence_then_legacy(self):
        records = self.dataset()
        self.train_accepted_fixture()
        with patch.object(boundary_pipeline, 'proposal_prediction', side_effect=RuntimeError('local runtime failure')):
            result = self.pipeline.propose('alice', 'r0')
        self.assertEqual(result['model_source'], 'personal_sequence_boundary')
        self.assertEqual(result['audio_sha256'], records['r0']['audio_sha256'])
        with patch.object(boundary_pipeline, 'proposal_prediction', side_effect=RuntimeError('local runtime failure')), \
             patch.object(sequence_pipeline, 'proposal_prediction', side_effect=RuntimeError('sequence runtime failure')):
            other = self.pipeline.propose('alice', 'r1')
        self.assertEqual(other['model_source'], 'personal')
        self.assertEqual(other['audio_sha256'], records['r1']['audio_sha256'])
        self.assertFalse(other['execution_authorized'])

    def test_local_mask_preserves_unvoiced_short_eof_and_negative_interrupt(self):
        rec = self.add(0, sample_count=1617, silence=True)
        frozen = self.pipeline.feature('alice', rec)
        item, _, _ = sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        mask = np.asarray([True, True, False, True, True, True])
        prediction = dict(scores=np.where(mask, .8, .1), selected_mask=mask)
        binding = proposals.plan(self.store, self.backend, 'alice', 'r0')[-1]
        result = boundary_pipeline.make_proposal(self.pipeline, 'alice', rec, frozen, binding, item, prediction)
        self.assertEqual([(r['start_sample'], r['end_sample'], r['label']) for r in result['regions']],
                         [(0, 640, 'singing'), (640, 960, 'environment'), (960, 1617, 'singing')])
        self.assertEqual(result['selected_mask'], mask.tolist())
        self.assertEqual(result['regions'][-1]['basis'], 'learned_local_onset_offset_and_sequence')
        self.assertEqual(result['source_cell_edges'][-1], rec['sample_count'])
        self.assertFalse(result['execution_authorized'])
        self.assertTrue(result['requires_confirmation'])

    def test_playback_and_mixed_records_do_not_supply_boundary_supervision(self):
        self.dataset()
        self.add(3, source='playback')
        self.add(4, source='mixed')
        report = self.train_accepted_fixture()
        candidate = report['local_boundary']['candidate']
        self.assertEqual(set(candidate['source_bindings']), {'r0', 'r1', 'r2'})
        for stats in candidate['head']['training_statistics'].values():
            self.assertEqual(set(stats['training_ids']), {'r0', 'r1', 'r2'})

    def test_local_config_off_uses_sequence_without_local_work(self):
        self.dataset()
        store = Store(Config(Path(self.directory.name), sequence_boundary_enabled=True,
                             local_boundary_enabled=False))
        pipeline = Pipeline(store, self.backend)
        with patch.object(boundary_pipeline, 'train', side_effect=AssertionError('disabled local branch executed')), \
             patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture):
            report = pipeline.train('alice', 'local-disabled')
        self.assertNotIn('local_boundary', report)
        self.assertIn('sequence_boundary', report)
        self.assertNotEqual(pipeline.propose('alice', 'r0')['model_source'], 'personal_local_boundary')

    def test_disabled_sequence_also_disables_local_branch(self):
        self.dataset()
        store = Store(Config(Path(self.directory.name), sequence_boundary_enabled=False,
                             local_boundary_enabled=True))
        pipeline = Pipeline(store, self.backend)
        with patch.object(boundary_pipeline, 'train', side_effect=AssertionError('no-sequence local branch executed')), \
             patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture):
            report = pipeline.train('alice', 'sequence-disabled')
        self.assertNotIn('local_boundary', report)
        self.assertNotIn('sequence_boundary', report)


if __name__ == '__main__':
    unittest.main()
