import unittest

import numpy as np

from . import semantic_evaluation as evaluation
from .test_sequence_evaluation import recording, region


class SemanticEvaluationTests(unittest.TestCase):
    def test_prefix_pair_cannot_cross_holdout_fold(self):
        a, b = recording([region(0, 1600)]), recording([region(0, 1600)])
        a['recording_id'], b['recording_id'] = 'a', 'b'
        prepared = dict(a=dict(recording=a, duplicate_prefixes={'b': 1600}),
                        b=dict(recording=b, duplicate_prefixes={'a': 1600}))
        with self.assertRaisesRegex(ValueError, 'crosses_holdout'):
            evaluation.validate_prefix_partition(prepared, {'g1': ['a'], 'g2': ['b']})
        report = evaluation.validate_prefix_partition(prepared, {'g1': ['a', 'b']})
        self.assertEqual(report['verified_prefix_pairs'], 1)
        self.assertTrue(report['every_recording_held_once'])

    def test_asymmetric_prefix_binding_is_rejected(self):
        prepared = dict(a=dict(recording=recording([]), duplicate_prefixes={'b': 1600}),
                        b=dict(recording=recording([]), duplicate_prefixes={}))
        with self.assertRaisesRegex(ValueError, 'invalid_verified'):
            evaluation.validate_prefix_partition(prepared, {'g': ['a', 'b']})

    def test_internal_nonvoiced_hole_is_diagnostic_not_negative_label(self):
        rec = recording([region(0, 1600)])
        edges = np.array([0, 320, 640, 960, 1280, 1600])
        fine = dict(edges=edges, feature_metadata={'names': [evaluation.VOICING_COLUMN, evaluation.COVERAGE_COLUMN]},
                    features=np.array([[1., 1.], [1., 1.], [0., 1.], [1., 1.], [1., 1.]]))
        row = evaluation.measure(rec, edges, np.array([.9, .9, .1, .9, .9]),
                                 np.array([True, True, False, True, True]), fine_item=fine)
        diag = row['phonation_proxy_diagnostics']
        self.assertAlmostEqual(diag['internal_hole_nonvoiced_seconds'], .02)
        self.assertAlmostEqual(row['metrics']['fn_seconds'], .02)
        self.assertEqual(row['metrics']['fp_seconds'], 0.)

    def test_unavailable_initial_pitch_is_not_called_nonvoiced(self):
        rec = recording([region(0, 1600)])
        edges = np.array([0, 1600])
        fine = dict(edges=edges, feature_metadata={'names': [evaluation.VOICING_COLUMN, evaluation.COVERAGE_COLUMN]},
                    features=np.array([[0., 0.]]))
        row = evaluation.measure(rec, edges, np.array([.1]), np.array([False]), fine_item=fine)
        self.assertAlmostEqual(row['phonation_proxy_diagnostics']['unknown_pitch_coverage_singing_seconds'], .1)
        self.assertEqual(row['phonation_proxy_diagnostics']['known_singing_nonvoiced_seconds'], 0.)

    def test_all_short_eof_intervals_reported_without_minimum_duration(self):
        rec = recording([region(0, 1599, 'speech'), region(1599, 1600)])
        row = evaluation.measure(rec, np.array([0, 1599, 1600]), np.array([.1, .9]), np.array([False, True]))
        short = row['short_interval_diagnostics'][0]
        self.assertTrue(short['touches_eof'])
        self.assertFalse(short['completely_missed'])
        self.assertAlmostEqual(short['selected_raw_overlap_seconds'], 1 / 16000)

    def test_category_proper_scores_exclude_unknown_without_training_costs(self):
        rec = recording([region(0, 800, 'speech'), region(800, 1600, 'uncertain')])
        result = evaluation.category_score_diagnostics(rec, np.array([0, 800, 1600]),
            np.array([[.5, .25, .25], [0., 1., 0.]]), ['speech', 'singing', 'environment'])
        self.assertAlmostEqual(result['known_label_seconds'], .05)
        self.assertAlmostEqual(result['duration_mean_nll'], np.log(2.))
        self.assertAlmostEqual(result['duration_mean_brier'], .375)

    def test_category_scores_are_not_silently_renormalized(self):
        with self.assertRaisesRegex(ValueError, 'invalid_semantic_category_scores'):
            evaluation.category_score_diagnostics(recording([region(0, 1600)]), np.array([0, 1600]),
                np.array([[.5, .5, .5]]), ['speech', 'singing', 'environment'])


if __name__ == '__main__':
    unittest.main()
