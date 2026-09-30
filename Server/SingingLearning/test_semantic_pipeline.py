"""Isolated category-evidence service integration with synthetic originals only.

Controlled accepted-guard fixtures test adoption plumbing, never model quality.
The ordinary-training test runs the actual shared held-out evaluation.
"""
import copy
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import wave

import numpy as np

from . import (boundary_evaluation, boundary_model, boundary_pipeline, fusion_pipeline,
               pitch_features, proposals, semantic_evaluation, semantic_pipeline,
               sequence_evaluation, sequence_pipeline)
from .config import Config
from .pipeline import Pipeline
from .store import Store
from .test_learning import FakeBackend


CLASSES = ('environment', 'speech', 'singing', 'singing', 'speech', 'environment', 'singing', 'environment')


class SemanticBackend(FakeBackend):
    def extract(self, path):
        result = super().extract(path)
        prior = result['timeline']['scores']
        rows = {'speech': prior[2], 'singing': prior[0], 'environment': [.001] * len(prior[0])}
        count = result['source']['sample_count']
        result['timeline']['source_start_samples'] = [i * count // 8 for i in range(8)]
        result['timeline']['scores'] = [list(rows[label]) for label in CLASSES]
        return result


class SemanticPipelineTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.store = Store(Config(Path(self.directory.name), sequence_boundary_enabled=True,
            local_boundary_enabled=True, category_evidence_enabled=True))
        self.store.create_profile(dict(profile_id='alice'))
        self.backend = SemanticBackend()
        self.pipeline = Pipeline(self.store, self.backend)

    def tearDown(self):
        self.directory.cleanup()

    def add(self, number, *, sample_count=25600, silence=False, source='user', missing_environment=False):
        rid, session = f'r{number}', f's{number}'
        self.store.create_session('alice', dict(session_id=session, content_group=f'content{number}'))
        edges = [i * sample_count // 8 for i in range(9)]
        labels = ['speech' if missing_environment and name == 'environment' else name for name in CLASSES]
        samples = np.zeros(sample_count, dtype='<i2')
        if not silence:
            for index, label in enumerate(labels):
                if label == 'environment':
                    continue
                frequency = (230 if label == 'singing' else 130) + 19 * number
                position = np.arange(edges[index], edges[index + 1])
                samples[edges[index]:edges[index + 1]] = (
                    np.sin(2 * np.pi * frequency * position / 16000.) * 6000).astype('<i2')
        buffer = io.BytesIO()
        with wave.open(buffer, 'wb') as writer:
            writer.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
            writer.writeframes(samples.tobytes())
        self.store.upload('alice', dict(recording_id=rid, session_id=session,
            content_group=f'content{number}', capture_serial=number), buffer.getvalue())
        regions = []
        for index, label in enumerate(labels):
            if regions and regions[-1]['label'] == label:
                regions[-1]['end_sample'] = edges[index + 1]
            else:
                regions.append(dict(start_sample=edges[index], end_sample=edges[index + 1],
                                    label=label, tolerance_samples=0))
        self.store.annotate('alice', rid, dict(expected_revision=0, confirmed=True,
                                             source_kind=source, regions=regions))
        return self.store.recording('alice', rid)

    def dataset(self, *, missing_environment=False):
        for index in range(3):
            self.add(index, missing_environment=missing_environment)
        return self.store.snapshot('alice')['recordings']

    @staticmethod
    def fusion_disabled_fixture(*args, **kwargs):
        return dict(status='shadow_rejected', teaching_eligible=False, production_eligible=False,
                    execution_authorized=False, eligibility_reasons=['synthetic_test_isolates_category_branch'])

    @staticmethod
    def accepted_evaluation_fixture(records, prepared, native, coarse, bootstrap, groups, **kwargs):
        from . import semantic_model
        semantic = semantic_model.fit_candidate(prepared)
        boundary = boundary_model.fit_candidate(prepared)
        sequence = boundary['sequence_candidate']
        rows = []
        for rid, item in prepared.items():
            mask = np.zeros(len(item['edges']) - 1, dtype=bool)
            for region in records[rid]['annotation']['regions']:
                if region['label'] == 'singing':
                    mask |= ((item['edges'][:-1] >= region['start_sample']) &
                             (item['edges'][1:] <= region['end_sample']))
            rows.append(semantic_evaluation.measure(records[rid], item['edges'], mask.astype(float),
                                                    mask, fine_item=item))
        # Controlled scores only exercise guards; they are not this fit's measured accuracy.
        summary = semantic_evaluation.aggregate(rows)
        references = {name: copy.deepcopy(summary) for name in sequence_evaluation.REFERENCES}
        semantic_guard = semantic_evaluation.evaluate_guard(summary, references, fold_count=len(groups),
            complete=True, training_available=semantic['semantic_training_available'])
        boundary_guard = boundary_evaluation.evaluate_guard(summary, references, fold_count=len(groups),
            complete=True, training_available=boundary['boundary_training_available'])
        sequence_guard = sequence_evaluation.evaluate_guard(summary, references, fold_count=len(groups), complete=True)
        candidates = dict(semantic=semantic, boundary=boundary, sequence=sequence)
        guards = dict(semantic=semantic_guard, boundary=boundary_guard, sequence=sequence_guard)
        for name, candidate in candidates.items():
            candidate.update(teaching_eligible=guards[name]['teaching_eligible'], evaluation_guard=guards[name],
                             eligibility_reasons=guards[name]['eligibility_reasons'])
        return dict(candidates=candidates, guards=guards, candidate=semantic, guard=semantic_guard,
            summaries=dict(candidate=summary, boundary=summary, sequence=summary, **references),
            fold_count=len(groups), complete=True,
            semantic_training_complete=semantic['semantic_training_available'],
            boundary_training_complete=boundary['boundary_training_available'])

    def train_accepted_fixture(self, model_id='accepted-category-fixture'):
        with patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture), \
             patch.object(semantic_evaluation, 'evaluate_prepared', side_effect=self.accepted_evaluation_fixture):
            return self.pipeline.train('alice', model_id)

    def test_ordinary_train_shares_controls_and_preserves_all_original_evidence(self):
        records = self.dataset()
        originals = {rid: self.store.audio_path('alice', rid).read_bytes() for rid in records}
        revision = self.store.snapshot('alice')['profile']['dataset_revision']
        with patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture), \
             patch.object(boundary_pipeline, 'train', side_effect=AssertionError('local controls must be shared')), \
             patch.object(sequence_pipeline, 'train', side_effect=AssertionError('sequence controls must be shared')):
            report = self.pipeline.train('alice', 'ordinary-category-evidence')
        self.assertEqual(report['status'], 'candidate', report)
        nested = report['category_evidence']
        self.assertIn(nested['status'], ('eligible', 'shadow_rejected'), nested)
        self.assertEqual(nested['fold_count'], 3)
        self.assertTrue(nested['complete'])
        self.assertTrue(nested['semantic_training_complete'])
        self.assertFalse(nested['production_eligible'])
        self.assertFalse(nested['execution_authorized'])
        self.assertEqual(report['local_boundary']['candidate'], nested['candidates']['boundary'])
        self.assertEqual(report['sequence_boundary']['candidate'], nested['candidates']['sequence'])
        after = self.store.snapshot('alice')
        self.assertEqual(after['profile']['dataset_revision'], revision)
        self.assertEqual({rid: rec['annotation'] for rid, rec in records.items()},
                         {rid: rec['annotation'] for rid, rec in after['recordings'].items()})
        for rid, audio in originals.items():
            self.assertEqual(self.store.audio_path('alice', rid).read_bytes(), audio)
        self.assertEqual(after['current_candidate']['category_evidence']['teaching_eligible'], nested['teaching_eligible'])
        self.assertNotIn('candidate', after['current_candidate']['category_evidence'])

    def test_accepted_fixture_routes_complete_proposal_to_source_bound_category_head(self):
        records = self.dataset()
        report = self.train_accepted_fixture()
        candidate = semantic_pipeline.eligible_candidate(report, records)
        self.assertIsNotNone(candidate, report.get('category_evidence'))
        self.assertTrue(candidate['semantic_training_available'])
        binding = proposals.plan(self.store, self.backend, 'alice', 'r0')[-1]
        self.assertEqual(binding['model_source'], 'personal_category_evidence')
        self.assertEqual(binding['semantic_candidate_id'], candidate['candidate_id'])
        self.assertNotIn('boundary_candidate_id', binding)
        self.assertNotIn('sequence_candidate_id', binding)
        result = self.pipeline.propose('alice', 'r0')
        self.assertEqual(result['model_source'], 'personal_category_evidence')
        self.assertEqual(result['decoding'], 'category_evidence_final_sequence')
        self.assertEqual(result['source_cell_edges'], list(range(0, 25601, 320)))
        self.assertEqual(result['audio_sha256'], records['r0']['audio_sha256'])
        self.assertEqual(result['annotation_revision'], records['r0']['annotation_revision'])
        self.assertTrue(result['requires_confirmation'])
        self.assertFalse(result['execution_authorized'])

    def test_guard_rechecks_quality_training_sources_definition_and_policy(self):
        from . import semantic_model
        records = self.dataset()
        report = self.train_accepted_fixture()
        self.assertIsNotNone(semantic_pipeline.eligible_candidate(report, records))
        changed = copy.deepcopy(report)
        changed['category_evidence']['summaries']['candidate']['metrics']['speech_fp_seconds'] += 1.
        self.assertIsNone(semantic_pipeline.eligible_candidate(changed, records))
        changed = copy.deepcopy(report)
        changed['category_evidence']['semantic_training_complete'] = False
        self.assertIsNone(semantic_pipeline.eligible_candidate(changed, records))
        for key, value in [('annotation_revision', records['r0']['annotation_revision'] + 1), ('audio_sha256', '0' * 64)]:
            changed = copy.deepcopy(records)
            changed['r0'][key] = value
            self.assertIsNone(semantic_pipeline.eligible_candidate(report, changed))
        with patch.dict(pitch_features.FEATURE_POLICY, version='incompatible-category-test'):
            self.assertIsNone(semantic_pipeline.eligible_candidate(report, records))
        with patch.dict(semantic_model.POLICY, version='incompatible-category-policy'):
            self.assertIsNone(semantic_pipeline.eligible_candidate(report, records))

    def test_missing_human_category_keeps_working_fallback_and_never_adopts(self):
        records = self.dataset(missing_environment=True)
        with patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture):
            report = self.pipeline.train('alice', 'category-missing-environment')
        self.assertEqual(report['status'], 'candidate')
        self.assertFalse(report['category_evidence']['teaching_eligible'])
        self.assertIsNone(semantic_pipeline.eligible_candidate(report, records))
        result = self.pipeline.propose('alice', 'r0')
        self.assertNotEqual(result['model_source'], 'personal_category_evidence')
        self.assertEqual(result['audio_sha256'], records['r0']['audio_sha256'])
        self.assertFalse(result['execution_authorized'])

    def test_failed_holdout_guard_preserves_existing_original_bound_proposal(self):
        rec = self.add(0)
        with patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture):
            report = self.pipeline.train('alice', 'single-category-example')
        self.assertEqual(report['category_evidence']['status'], 'shadow_rejected')
        result = self.pipeline.propose('alice', 'r0')
        self.assertEqual(result['model_source'], 'personal_provisional')
        self.assertEqual(result['audio_sha256'], rec['audio_sha256'])
        self.assertEqual(result['annotation_revision'], rec['annotation_revision'])

    def test_runtime_failure_walks_only_rechecked_fallbacks_without_source_change(self):
        records = self.dataset()
        self.train_accepted_fixture()
        with patch.object(semantic_pipeline, 'proposal_prediction', side_effect=RuntimeError('semantic test failure')):
            first = self.pipeline.propose('alice', 'r0')
        self.assertEqual(first['model_source'], 'personal_local_boundary')
        with patch.object(semantic_pipeline, 'proposal_prediction', side_effect=RuntimeError('semantic test failure')), \
             patch.object(boundary_pipeline, 'proposal_prediction', side_effect=RuntimeError('boundary test failure')):
            second = self.pipeline.propose('alice', 'r1')
        self.assertEqual(second['model_source'], 'personal_sequence_boundary')
        with patch.object(semantic_pipeline, 'proposal_prediction', side_effect=RuntimeError('semantic test failure')), \
             patch.object(boundary_pipeline, 'proposal_prediction', side_effect=RuntimeError('boundary test failure')), \
             patch.object(sequence_pipeline, 'proposal_prediction', side_effect=RuntimeError('sequence test failure')):
            third = self.pipeline.propose('alice', 'r2')
        self.assertEqual(third['model_source'], 'personal')
        for rid, result in [('r0', first), ('r1', second), ('r2', third)]:
            self.assertEqual(result['audio_sha256'], records[rid]['audio_sha256'])
            self.assertTrue(result['requires_confirmation'])
            self.assertFalse(result['execution_authorized'])

    def test_selected_mask_retains_unvoiced_short_eof_and_does_not_fill_negative_gap(self):
        from .semantic_model import CLASS_NAMES
        rec = self.add(0, sample_count=1617, silence=True)
        frozen = self.pipeline.feature('alice', rec)
        item, _, _ = sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        mask = np.asarray([True, True, False, True, True, True])
        categories = np.full((len(mask), 3), .1)
        states = np.where(mask, list(CLASS_NAMES).index('singing'), list(CLASS_NAMES).index('environment'))
        categories[np.arange(len(mask)), states] = .8
        prediction = dict(scores=categories[:, list(CLASS_NAMES).index('singing')], selected_mask=mask,
                          selected_states=states, class_names=list(CLASS_NAMES), category_scores=categories)
        binding = proposals.plan(self.store, self.backend, 'alice', 'r0')[-1]
        result = semantic_pipeline.make_proposal(self.pipeline, 'alice', rec, frozen, binding, item, prediction)
        self.assertEqual([(r['start_sample'], r['end_sample'], r['label']) for r in result['regions']],
                         [(0, 640, 'singing'), (640, 960, 'environment'), (960, 1617, 'singing')])
        self.assertEqual(result['regions'][-1]['basis'], 'learned_category_evidence_and_transition')
        self.assertEqual(result['selected_mask'], mask.tolist())
        self.assertEqual(result['source_cell_edges'][-1], rec['sample_count'])
        self.assertFalse(result['execution_authorized'])

    def test_semantic_classes_preserved_even_when_raw_audio_is_silent(self):
        from .semantic_model import CLASS_NAMES
        rec = self.add(0, sample_count=1617, silence=True)
        frozen = self.pipeline.feature('alice', rec)
        item, _, _ = sequence_pipeline.prepare(self.pipeline, 'alice', rec)
        states = np.asarray([0, 0, 2, 1, 1, 1], dtype=np.int64)
        categories = np.full((6, 3), .1)
        categories[np.arange(6), states] = .8
        prediction = dict(scores=categories[:, 1], selected_mask=states == 1,
                          selected_states=states, class_names=list(CLASS_NAMES), category_scores=categories)
        binding = proposals.plan(self.store, self.backend, 'alice', 'r0')[-1]
        result = semantic_pipeline.make_proposal(self.pipeline, 'alice', rec, frozen, binding, item, prediction)
        self.assertEqual([(r['start_sample'], r['end_sample'], r['label']) for r in result['regions']],
                         [(0, 640, 'speech'), (640, 960, 'environment'), (960, 1617, 'singing')])
        bad = dict(prediction, selected_states=np.asarray([0, 0, 2, 1, 1, 0], dtype=np.int64))
        with self.assertRaisesRegex(ValueError, 'invalid_category_evidence_proposal_prediction'):
            semantic_pipeline.make_proposal(self.pipeline, 'alice', rec, frozen, binding, item, bad)
        mismatched_score = dict(prediction, scores=np.full(6, .5))
        with self.assertRaisesRegex(ValueError, 'invalid_category_evidence_proposal_prediction'):
            semantic_pipeline.make_proposal(self.pipeline, 'alice', rec, frozen, binding, item, mismatched_score)

    def test_playback_mixed_and_unknown_do_not_enter_category_training(self):
        self.dataset()
        self.add(3, source='playback')
        self.add(4, source='mixed')
        rec = self.add(5)
        self.store.annotate('alice', 'r5', dict(expected_revision=rec['annotation_revision'], confirmed=True,
            source_kind='user', regions=[dict(start_sample=0, end_sample=rec['sample_count'],
                                              label='uncertain', tolerance_samples=0)]))
        report = self.train_accepted_fixture()
        self.assertEqual(set(report['category_evidence']['candidate']['source_bindings']), {'r0', 'r1', 'r2'})

    def test_category_config_off_runs_previous_control_without_semantic_training(self):
        self.dataset()
        store = Store(Config(Path(self.directory.name), sequence_boundary_enabled=True,
                             local_boundary_enabled=True, category_evidence_enabled=False))
        pipeline = Pipeline(store, self.backend)
        with patch.object(semantic_pipeline, 'train', side_effect=AssertionError('disabled category branch executed')), \
             patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture):
            report = pipeline.train('alice', 'category-disabled')
        self.assertNotIn('category_evidence', report)
        self.assertIn('local_boundary', report)
        self.assertNotEqual(pipeline.propose('alice', 'r0')['model_source'], 'personal_category_evidence')

    def test_local_off_disables_child_category_branch(self):
        self.dataset()
        store = Store(Config(Path(self.directory.name), sequence_boundary_enabled=True,
                             local_boundary_enabled=False, category_evidence_enabled=True))
        pipeline = Pipeline(store, self.backend)
        with patch.object(semantic_pipeline, 'train', side_effect=AssertionError('parent-off category branch executed')), \
             patch.object(fusion_pipeline, 'train', side_effect=self.fusion_disabled_fixture):
            report = pipeline.train('alice', 'parent-local-disabled')
        self.assertNotIn('category_evidence', report)
        self.assertNotIn('local_boundary', report)
        self.assertIn('sequence_boundary', report)


if __name__ == '__main__':
    unittest.main()
