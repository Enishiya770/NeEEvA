import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import wave

import numpy as np

from Server.SingingLearning import pipeline, proposals
from Tools.SingingSegmentation import evaluate_personal_learning as run
from Tools.SingingSegmentation import fit_score_adapter as adapter


def recording(regions, count=20, rate=10, source='user'):
    return dict(recording_id='r-test', sample_rate=rate, sample_count=count, channels=1,
                annotation_revision=1, annotation=dict(confirmed=True, source_kind=source, regions=regions))


def region(start, end, label, tolerance=0):
    return dict(start_sample=start, end_sample=end, label=label, tolerance_samples=tolerance)


class ExactMetricsTests(unittest.TestCase):
    def test_exact_overlap_tolerance_unknown_and_excluded_source(self):
        rec = recording([region(0, 7, 'singing', 1), region(7, 13, 'speech'),
                         region(13, 17, 'uncertain'), region(17, 20, 'environment')])
        edges = np.array([0, 10, 20])
        result = run.evaluate_mask(rec, edges, np.array([.8, .1]), np.array([True, False]))
        metrics = result['metrics']
        self.assertAlmostEqual(metrics['tp_seconds'], .5)
        self.assertAlmostEqual(metrics['speech_fp_seconds'], .3)
        self.assertAlmostEqual(metrics['tn_seconds'], .6)
        self.assertAlmostEqual(metrics['predicted_unknown_seconds'], .2)
        self.assertAlmostEqual(metrics['singing_precision'], .625)
        rec['annotation']['source_kind'] = 'playback'
        excluded = run.evaluate_mask(rec, edges, np.array([.8, .1]), np.array([True, False]))
        self.assertEqual(excluded['metrics']['tp_seconds'], 0.)
        self.assertEqual(excluded['metrics']['fp_seconds'], 0.)
        self.assertIsNone(excluded['metrics']['singing_precision'])

    def test_fragmentation_and_matching_do_not_invent_missed_endpoints(self):
        rec = recording([region(2, 12, 'singing'), region(15, 19, 'singing')])
        predictions = [dict(start_sample=0, end_sample=5), dict(start_sample=7, end_sample=13)]
        result = run.boundary_report(rec, predictions)
        first, missed = result['gold_intervals']
        self.assertEqual(first['matched_prediction_index'], 1)
        self.assertEqual(first['internal_fragment_splits'], 1)
        self.assertEqual(first['start_error_samples'], 5)
        self.assertEqual(first['end_error_samples'], 1)
        self.assertTrue(missed['completely_missed'])
        self.assertNotIn('end_error_samples', missed)

    def test_one_prediction_merging_two_gold_is_not_two_correct_boundaries(self):
        rec = recording([region(1, 7, 'singing'), region(9, 12, 'singing')])
        result = run.boundary_report(rec, [dict(start_sample=0, end_sample=15)])
        self.assertEqual(result['matched_count'], 1)
        self.assertEqual(result['unmatched_gold_count'], 1)
        self.assertEqual(result['completely_missed_gold_count'], 0)
        self.assertEqual(result['predictions_merging_gold_intervals'], 1)

    def test_label_grid_oracle_is_minimum_binary_cell_duration_error(self):
        rec = recording([region(2, 13, 'singing'), region(13, 20, 'speech')])
        edges = np.array([0, 10, 20])
        pos, neg, _ = pipeline.cell_masses(rec, edges)
        prepared = {'r-test': dict(edges=edges, positive=pos, negative=neg)}
        result = run.grid_oracle({'r-test': rec}, prepared)
        oracle = result['aggregate']['native_grid_oracle']['metrics']['minimum_fp_plus_fn_seconds']
        for first in (False, True):
            for second in (False, True):
                metric = run.evaluate_mask(rec, edges, np.array([.5, .5]), np.array([first, second]))['metrics']
                self.assertLessEqual(oracle, metric['fp_seconds'] + metric['fn_seconds'] + 1e-12)
        self.assertAlmostEqual(oracle, .3)


class FittingAndProposalTests(unittest.TestCase):
    def test_train_only_group_weights_and_normalization(self):
        metadata = adapter.feature_metadata(adapter.REQUIRED_LABELS)
        prepared = {
            'a': dict(group_id='small', features=np.array([[0.] * 9, [1.] * 9]), positive=np.array([0., 1.]), negative=np.array([1., 0.])),
            'b': dict(group_id='large', features=np.array([[2.] * 9, [3.] * 9]), positive=np.array([0., 9.]), negative=np.array([9., 0.])),
            'held': dict(group_id='large', features=np.array([[1e9] * 9]), positive=np.array([1e9]), negative=np.array([0.])),
        }
        x, y, weights = run.assemble_training(prepared, ['a', 'b'])
        np.testing.assert_allclose(weights, [.5, .5, .5, .5])
        model = adapter.fit_score_adapter(x, y, weights, feature_metadata=metadata)
        changed = copy.deepcopy(prepared)
        changed['held']['features'] *= -1000
        changed['held']['negative'] += 1e12
        x2, y2, w2 = run.assemble_training(changed, ['a', 'b'])
        np.testing.assert_array_equal(x, x2)
        np.testing.assert_array_equal(y, y2)
        np.testing.assert_array_equal(weights, w2)
        np.testing.assert_allclose(model['standardizer']['mean'], [1.5] * 9)
        _, _, duration = run.assemble_training(prepared, ['a', 'b'], 'duration')
        np.testing.assert_allclose(duration, [1., 1., 9., 9.])
        _, _, sqrt = run.assemble_training(prepared, ['a', 'b'], 'sqrt_group_duration')
        self.assertAlmostEqual(sqrt[2:].sum() / sqrt[:2].sum(), 3.)

    def test_one_class_folds_are_skipped(self):
        records = {'yes': recording([region(0, 20, 'singing')]), 'no': recording([region(0, 20, 'speech')])}
        prepared = {}
        for rid, rec in records.items():
            edges = np.array([0, 10, 20])
            pos, neg, _ = pipeline.cell_masses(rec, edges)
            prepared[rid] = dict(group_id=rid, features=np.zeros((2, 9)), positive=pos, negative=neg,
                                 metadata=adapter.feature_metadata(adapter.REQUIRED_LABELS))
        result = run.holdout(records, prepared, {}, True)
        self.assertEqual(result['completed_folds'], 0)
        self.assertEqual(result['skipped_folds'], 2)
        self.assertTrue(all(f['reason'] == 'one_class_training' for f in result['folds']))

    def test_deployed_scores_and_labels_match_real_proposal_policy(self):
        metadata = adapter.feature_metadata(adapter.REQUIRED_LABELS)
        personal = adapter.fit_score_adapter(np.array([[0.] * 9, [1.] * 9]), np.array([0., 1.]), np.ones(2), feature_metadata=metadata)
        bootstrap = copy.deepcopy(personal)
        bootstrap['bias'] += .4
        raw = np.full((4, len(adapter.REQUIRED_LABELS)), .7)
        features, metadata = adapter.build_features(raw, adapter.REQUIRED_LABELS, return_metadata=True)
        rec = recording([region(0, 40, 'singing')], count=40)
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary)
            audio = path / 'original.wav'
            with wave.open(str(audio), 'wb') as stream:
                stream.setnchannels(1)
                stream.setsampwidth(2)
                stream.setframerate(10)
                stream.writeframes(np.array([0] * 10 + [9000] * 30, dtype='<i2').tobytes())
            rec['audio_sha256'] = hashlib.sha256(audio.read_bytes()).hexdigest()
            prior = path / 'bootstrap.json'
            prior.write_text(json.dumps(bootstrap), encoding='utf-8')
            edges = np.array([0, 10, 20, 30, 40])
            feature = dict(model_version=proposals.BOOTSTRAP_CHECKPOINT,
                           timeline=dict(columns=list(adapter.REQUIRED_LABELS), scores=raw.tolist()))
            binding = dict(model_source='personal_provisional', prior_adapter_sha256=proposals.digest(bootstrap),
                           model_provenance=dict(proposal_score_blend=dict(personal_weight=.25, bootstrap_weight=.75)))
            with patch.object(proposals, 'BOOTSTRAP_PATH', prior):
                actual = proposals.make_proposal(rec, feature, personal, binding, edges, features, metadata, audio)
            item = dict(features=features, edges=edges, rms=proposals.cell_rms(audio, rec, edges),
                        speech=np.full(4, .7), feature_sha256='test', group_id='test')
            evaluation = run.evaluate_record(rec, item, personal, bootstrap)
            np.testing.assert_allclose(evaluation['variants']['deployed_proposal']['scores'], actual['scores'])
            actual_labels = []
            for i in range(4):
                actual_labels.append(next(r['label'] for r in actual['regions'] if r['start_sample'] <= edges[i] < r['end_sample']))
            self.assertEqual(evaluation['deployed_proposal_labels'], actual_labels)
            self.assertEqual(evaluation['variants']['deployed_proposal']['selected_cells'], [v == 'singing' for v in actual_labels])
            self.assertEqual(actual_labels[0], 'environment')


if __name__ == '__main__':
    unittest.main()
