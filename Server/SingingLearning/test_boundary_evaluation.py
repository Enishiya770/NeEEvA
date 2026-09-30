import unittest

import numpy as np

from . import boundary_evaluation as evaluation
from .test_sequence_evaluation import recording, region


class BoundaryEvaluationTests(unittest.TestCase):
    def test_spoken_intro_is_leading_not_internal_false_positive(self):
        rec = recording([region(0, 320, 'speech'), region(320, 1280), region(1280, 1600, 'environment')])
        row = evaluation.measure(rec, np.array([0, 1600]), np.array([.9]), np.array([True]))
        self.assertAlmostEqual(row['false_positive_parts']['leading_speech_fp_seconds'], .02)
        self.assertAlmostEqual(row['false_positive_parts']['trailing_environment_fp_seconds'], .02)
        self.assertAlmostEqual(sum(v for v in row['false_positive_parts'].values() if isinstance(v, float)), row['metrics']['fp_seconds'])

    def test_bridged_speech_and_isolated_noise_are_separate(self):
        rec = recording([region(0, 320), region(320, 640, 'speech'), region(640, 960),
                         region(960, 1280, 'uncertain'), region(1280, 1600, 'environment')])
        row = evaluation.measure(rec, np.array([0, 960, 1280, 1600]), np.array([.9, .1, .9]), np.array([True, False, True]))
        self.assertAlmostEqual(row['false_positive_parts']['internal_speech_fp_seconds'], .02)
        self.assertAlmostEqual(row['false_positive_parts']['isolated_environment_fp_seconds'], .02)
        self.assertAlmostEqual(row['metrics']['fp_seconds'], .04)

    def test_unknown_intro_and_tolerance_are_not_false_positive(self):
        rec = recording([region(0, 800, 'uncertain'), region(800, 1600)])
        row = evaluation.measure(rec, np.array([0, 1600]), np.array([.9]), np.array([True]))
        self.assertEqual(row['false_positive_parts']['leading_speech_fp_seconds'], 0.)
        self.assertEqual(row['metrics']['fp_seconds'], 0.)

    def test_tiny_eof_gold_remains_censored_and_visible(self):
        rec = recording([region(0, 1599, 'speech'), region(1599, 1600)])
        row = evaluation.measure(rec, np.array([0, 1600]), np.array([.1]), np.array([False]))
        summary = evaluation.aggregate([row])
        self.assertEqual(summary['boundaries']['eof_gold_count'], 1)
        self.assertEqual(summary['boundaries']['eof_missed_gold_count'], 1)
        self.assertEqual(summary['boundaries']['short_missed_gold_count'], 1)
        self.assertAlmostEqual(summary['metrics']['fn_seconds'], 1 / 16000)

    def test_guard_policy_does_not_add_sequence_zero_split_constraint(self):
        row = evaluation.measure(recording([region(0, 1600)]), np.array([0, 1600]), np.array([.9]), np.array([True]))
        summary = evaluation.aggregate([row])
        refs = {name: summary for name in ('bootstrap_proposal', 'panns_uniform', 'native_fft_speech2')}
        self.assertTrue(evaluation.evaluate_guard(summary, refs, fold_count=3)['teaching_eligible'])
        self.assertFalse(evaluation.evaluate_guard(summary, refs, fold_count=3, training_available=False)['teaching_eligible'])


if __name__ == '__main__':
    unittest.main()
