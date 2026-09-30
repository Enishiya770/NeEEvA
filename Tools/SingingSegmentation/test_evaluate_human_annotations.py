import copy
import json
from pathlib import Path
import tempfile
import unittest

import numpy as np
import evaluate_human_annotations as e


def record():
    return {'source': {'sample_count': 1000, 'sample_rate': 100, 'audio_sha256': 'a' * 64},
        'coordinate_origin_sample': 0, 'sample_range_convention': 'half-open',
        'unlabelled_sample_ranges': [[0, 200], [800, 1000]],
        'annotations': [{'label': 'singing', 'start_sample': 200, 'end_sample': 800,
            'boundary_uncertainty': {'kind': 'human_tolerance_band_not_statistical_ci',
                'start_min_sample': 190, 'start_max_sample': 210, 'end_min_sample': 790, 'end_max_sample': 810}}]}


class HumanEvaluationTests(unittest.TestCase):
    def test_blank_is_unknown_without_confirmation(self):
        m = e.truth_masks(record())
        v = e.measurements(np.ones(1000, dtype=bool), m, 100, 'nominal')
        self.assertEqual(v['tp_seconds'], 6)
        self.assertEqual(v['fp_seconds'], 0)
        self.assertEqual(v['excluded_or_unknown_seconds'], 4)

    def test_confirmed_complement_is_not_speech(self):
        m = e.truth_masks(record(), True)
        v = e.measurements(np.ones(1000, dtype=bool), m, 100, 'confident')
        self.assertAlmostEqual(v['tp_seconds'], 5.8)
        self.assertAlmostEqual(v['fp_seconds'], 3.8)
        self.assertEqual(v['explicit_speech_seconds'], 0)
        self.assertEqual(v['other_non_singing_seconds'], 3.8)

    def test_half_open_sample_duration_not_frame_count(self):
        m = e.truth_masks(record(), True)
        p = e.span_mask(1000, [[195, 205]])
        v = e.measurements(p, m, 100, 'nominal')
        self.assertEqual(v['tp_seconds'], .05)
        self.assertEqual(v['fp_seconds'], .05)
        self.assertEqual(e.ranges(p), [[195, 205]])

    def test_boundary_match_uses_same_contiguous_candidate(self):
        a = record()['annotations']
        self.assertFalse(e.boundary_matches([[200, 500], [600, 800]], a, 100)[0]['both_endpoints_within_tolerance'])
        self.assertTrue(e.boundary_matches([[190, 810]], a, 100)[0]['both_endpoints_within_tolerance'])

    def test_disputed_boundary_is_unresolved_not_failure(self):
        result = e.boundary_matches([], record()['annotations'], 100, e.span_mask(1000, [[200, 240]]))[0]
        self.assertIsNone(result['both_endpoints_within_tolerance'])
        self.assertEqual(result['boundary_truth_status'], 'unresolved_same_pcm_conflict')

    def test_conflict_exclusion_does_not_double_subtract_band(self):
        m = e.truth_masks(record(), True)
        m['confident_conflict_excluded'] = m['confident'] & ~e.span_mask(1000, [[200, 240]])
        v = e.measurements(np.ones(1000, dtype=bool), m, 100, 'confident_conflict_excluded')
        self.assertAlmostEqual(v['tp_seconds'], 5.5)
        self.assertAlmostEqual(v['excluded_or_unknown_seconds'], .7)

    def test_invalid_or_inconsistent_coordinates_rejected(self):
        for mutate in (lambda r: r['annotations'][0].update(start_sample=200.5),
                       lambda r: r.update(unlabelled_sample_ranges=[]),
                       lambda r: r.update(coordinate_origin_sample=1)):
            r = record(); mutate(r)
            with self.assertRaises(ValueError): e.truth_masks(r)

    def test_confirmation_hash_binds_both_documents(self):
        with tempfile.TemporaryDirectory() as folder:
            p = Path(folder); original = p / 'original.json'; corrected = p / 'corrected.json'; sidecar = p / 'confirmation.json'
            original.write_text('{}'); corrected.write_text(json.dumps({'dataset_id': 'x', 'source_manifest_sha256': 'b', 'recordings': [{'recording_id': 'a'}]}))
            c = {'confirmation_schema': 'neeeva.speech-singing-human-confirmation.v1', 'corrected_sha256': e.sha(corrected),
                'original_file': original.name, 'original_sha256': e.sha(original), 'dataset_id': 'x', 'source_manifest_sha256': 'b',
                'provenance': {'kind': 'human_conversation_confirmation', 'author': 'user', 'statements': ['confirmed']},
                'binary_coverage': {'complement_label': 'non_singing', 'complement_is_not_necessarily_speech': True,
                    'singing_intervals': 'singing annotations in the corrected document', 'reviewed_recording_ids': ['a']}}
            sidecar.write_text(json.dumps(c)); self.assertEqual(e.validate_confirmation(corrected, sidecar, 'b')[0], {'a'})
            original.write_text('{"changed":1}')
            with self.assertRaises(ValueError): e.validate_confirmation(corrected, sidecar, 'b')

    def test_environment_override_is_source_bound_and_restores_known_band(self):
        r = record(); r['recording_id'] = 'source'; original = copy.deepcopy(r)
        override = {'recording_id': 'source', 'source': copy.deepcopy(r['source']),
            'start_sample': 200, 'end_sample': 240, 'label': 'non_singing',
            'subtype': 'environment_sound', 'origin': 'explicit_user_conversation'}
        m = e.truth_masks(r, True, [override])
        self.assertEqual(r, original)
        self.assertFalse(m['singing'][200:240].any())
        self.assertTrue(m['confident'][200:240].all())
        self.assertFalse(m['confident'][190:200].any())
        self.assertFalse(m['explicit_speech'][200:240].any())
        self.assertEqual(int(m['confirmed_environment'].sum()), 40)
        for key, changed in [('audio_sha256', 'b' * 64), ('sample_rate', 101), ('sample_count', 1001)]:
            tampered = copy.deepcopy(override); tampered['source'][key] = changed
            with self.assertRaisesRegex(ValueError, 'override_source_mismatch'):
                e.truth_masks(r, True, [tampered])

    def test_confirmed_environment_end_is_not_a_precise_singing_start(self):
        pending = [{'annotation_index': 0, 'boundary': 'start', 'lower_bound_sample': 240,
                    'reason': 'Environment ends here; exact first sung sample is unspecified.'}]
        for candidate in ([[200, 800]], [[240, 800]]):
            b = e.boundary_matches(candidate, record()['annotations'], 100, unresolved=pending)[0]
            self.assertEqual(b['boundary_truth_status'], 'unresolved_human_boundary')
            self.assertIsNone(b['both_endpoints_within_tolerance'])
            self.assertIsNone(b['largest_overlap_start_error_seconds'])
            self.assertIsNone(b['provisional_raw_endpoint_comparison'])
            self.assertEqual(b['largest_overlap_end_error_seconds'], 0)


if __name__ == '__main__':
    unittest.main()
