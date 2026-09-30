import copy
import unittest

import numpy as np

from . import sequence_evaluation as evaluation


def recording(regions, sample_count=1600):
    return dict(recording_id='r-test', audio_sha256='sha', annotation_revision=1,
                sample_rate=16000, sample_count=sample_count,
                annotation=dict(confirmed=True, source_kind='user', regions=regions))


def region(start, end, label='singing', tolerance=0):
    return dict(start_sample=start, end_sample=end, label=label, tolerance_samples=tolerance)


class SequenceEvaluationTests(unittest.TestCase):
    def test_exact_tiny_eof_singing_survives_boundaries(self):
        rec = recording([region(0, 1599, 'speech'), region(1599, 1600)])
        result = evaluation.measure(rec, np.array([0, 1599, 1600]), np.array([0., 0.]), np.array([False, False]))
        self.assertEqual(result['boundaries']['short_missed_gold_count'], 1)
        self.assertAlmostEqual(result['metrics']['fn_seconds'], 1 / 16000)
        self.assertEqual(result['boundaries']['gold_intervals'][0]['gold_end_sample'], 1600)

    def test_first_last_overlap_does_not_use_only_largest_fragment(self):
        rec = recording([region(0, 1600)])
        result = evaluation.measure(rec, np.array([0, 160, 320, 1280, 1440, 1600]),
                                    np.array([.9, .1, .9, .1, .9]), np.array([True, False, True, False, True]))
        b = result['boundaries']
        self.assertEqual(b['internal_fragment_splits'], 2)
        self.assertAlmostEqual(b['internal_hole_seconds'], .02)
        self.assertEqual(b['gold_intervals'][0]['start_error_seconds'], 0.)
        self.assertEqual(b['gold_intervals'][0]['end_error_seconds'], 0.)

    def test_unknown_is_not_negative(self):
        rec = recording([region(0, 800, 'uncertain'), region(800, 1600)])
        result = evaluation.measure(rec, np.array([0, 800, 1600]), np.array([.9, .9]), np.array([True, True]))
        self.assertEqual(result['metrics']['fp_seconds'], 0.)
        self.assertEqual(result['metrics']['predicted_unknown_seconds'], .05)

    def test_source_excluded_has_no_gold_or_training_duration(self):
        rec = recording([region(0, 1600)])
        rec['annotation']['source_kind'] = 'character_playback'
        result = evaluation.measure(rec, np.array([0, 1600]), np.array([.9]), np.array([True]))
        self.assertEqual(result['boundaries']['gold_count'], 0)
        self.assertEqual(result['metrics']['tp_seconds'], 0.)
        self.assertEqual(result['metrics']['fp_seconds'], 0.)

    def test_guard_rejects_short_miss_and_missing_native_reference(self):
        rec = recording([region(0, 1600)])
        row = evaluation.measure(rec, np.array([0, 1600]), np.array([.9]), np.array([True]))
        summary = evaluation.aggregate([row])
        references = {name: copy.deepcopy(summary) for name in evaluation.REFERENCES}
        bad = copy.deepcopy(summary)
        bad['boundaries']['short_missed_gold_count'] = 1
        guard = evaluation.evaluate_guard(bad, references, fold_count=3)
        self.assertFalse(guard['teaching_eligible'])
        self.assertTrue(any('short_missed' in item for item in guard['eligibility_reasons']))
        del references['native_fft_speech2']
        guard = evaluation.evaluate_guard(summary, references, fold_count=3)
        self.assertIn('missing_reference:native_fft_speech2', guard['eligibility_reasons'])

    def test_bad_coordinates_are_rejected(self):
        rec = recording([region(0, 1600)])
        with self.assertRaises(ValueError):
            evaluation.measure(rec, np.array([0, 1599]), np.array([.9]), np.array([True]))

    def test_folds_cover_once_and_holdout_is_unambiguous(self):
        evaluation.validate_groups({'g1': ['a'], 'g2': ['b']}, ['a', 'b'])
        for groups in ({'g1': ['a']}, {'g1': ['a'], 'g2': ['a', 'b']}, {'g1': ['a', 'a', 'b']}):
            with self.assertRaises(ValueError):
                evaluation.validate_groups(groups, ['a', 'b'])


if __name__ == '__main__':
    unittest.main()
