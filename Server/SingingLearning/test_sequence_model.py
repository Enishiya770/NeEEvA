import copy
import unittest

import numpy as np

from . import sequence_model as sequence
from . import temporal_fusion as fusion


def prepared_item(rid='a', regions=None, *, samples=1000, source='user'):
    if regions is None:
        regions = [(0, 500, 'speech'), (500, 1000, 'singing')]
    recording = dict(recording_id=rid, audio_sha256='0' * 64, sample_rate=1000,
                     sample_count=samples, annotation_revision=1,
                     session_id='session', content_group='content',
                     annotation=dict(confirmed=True, source_kind=source,
                        regions=[dict(start_sample=a, end_sample=b, label=label, tolerance_samples=0)
                                 for a, b, label in regions]))
    edges = np.unique(np.r_[np.arange(0, samples, 20), samples]).astype(np.int64)
    centers = (edges[:-1] + edges[1:]) / 2.
    x = np.array([[float(any(a <= t < b and label == 'singing' for a, b, label in regions)), 0.]
                  for t in centers])
    return dict(recording=recording, edges=edges, features=x,
                feature_metadata=dict(names=['singing_cue', 'constant']),
                feature_sha256='1' * 64, pitch_source_sha256='2' * 64)


def head():
    return sequence.fit_candidate({'a': prepared_item()})['head']


class SequenceTests(unittest.TestCase):
    def test_transition_fit_uses_exact_known_adjacency_and_censors_unknown(self):
        a = prepared_item(regions=[(0, 200, 'speech'), (200, 400, 'uncertain'),
                                  (400, 600, 'singing'), (600, 800, 'environment'),
                                  (800, 1000, 'speech')])
        result = sequence.fit_transitions({'a': a}, ['a'])
        np.testing.assert_allclose(result['exposure_seconds'], [.6, .2])
        np.testing.assert_allclose(result['exits'], [0, 1])
        np.testing.assert_allclose(result['rates_per_second'], [.5 / 1.6, 1.5 / 1.2])
        self.assertEqual([v['sample'] for v in result['observations']], [600])

    def test_annotation_tolerance_is_censoring_not_bridge(self):
        a = prepared_item()
        a['recording']['annotation']['regions'][1]['tolerance_samples'] = 2
        result = sequence.fit_transitions({'a': a}, ['a'])
        self.assertEqual(result['exits'], [0., 0.])
        self.assertAlmostEqual(result['exposure_seconds'][1], .496)

    def test_heldout_and_excluded_sources_never_change_fit(self):
        a, b = prepared_item(), prepared_item('b', source='playback')
        h = prepared_item('heldout', regions=[(0, 20, 'speech'), (20, 1000, 'singing')])
        h['features'] *= 10000
        base = sequence.fit_candidate({'a': a})
        got = sequence.fit_candidate({'a': a, 'b': b, 'heldout': h}, ids=['a', 'b'])
        self.assertEqual(base['candidate_id'], got['candidate_id'])
        self.assertEqual(got['head']['transitions']['training_ids'], ['a'])
        np.testing.assert_allclose(got['head']['emission_head']['standardizer']['mean'], [1 / 3, 0])
        self.assertAlmostEqual(got['head']['initial_prior'], .5)
        self.assertAlmostEqual(got['head']['emission_prior'], 1 / 3)

    def test_exact_prefix_deduplicates_duration_and_transition_but_not_heldout(self):
        a, b = prepared_item(), prepared_item('b')
        a['duplicate_prefixes'], b['duplicate_prefixes'] = {'b': 1000}, {'a': 1000}
        joint = sequence.fit_transitions({'a': a, 'b': b}, ['a', 'b'])
        alone = sequence.fit_transitions({'a': a, 'b': b}, ['a'])
        self.assertEqual(joint['exits'], alone['exits'])
        np.testing.assert_allclose(joint['exposure_seconds'], alone['exposure_seconds'])
        self.assertEqual(joint['observations'][0]['multiplicity'], 2)
        self.assertEqual(alone['observations'][0]['multiplicity'], 1)

    def test_partner_censored_at_boundary_does_not_duplicate_exit(self):
        a = prepared_item()
        b = prepared_item('b', samples=500, regions=[(0, 500, 'speech')])
        a['duplicate_prefixes'], b['duplicate_prefixes'] = {'b': 500}, {'a': 500}
        result = sequence.fit_transitions({'a': a, 'b': b}, ['a', 'b'])
        self.assertEqual(result['exits'], [1., 0.])
        self.assertEqual(result['observations'][0]['multiplicity'], 1)

    def test_cell_duration_normalizes_emission_evidence(self):
        h = head()
        np.testing.assert_allclose(sequence._emission(h, .8, .320),
                                   16 * sequence._emission(h, .8, .020))
        np.testing.assert_allclose(sequence._emission(h, .8, .003),
                                   .003 / .320 * sequence._emission(h, .8, .320))
        # Exact CTMC transition matrices compose consistently in continuous time.
        rates = h['transitions']['rates_per_second']
        np.testing.assert_allclose(sequence._transition(rates, .020) @ sequence._transition(rates, .030),
                                   sequence._transition(rates, .050))

    def test_short_strong_negative_interrupt_is_not_unconditionally_filled(self):
        h = head()
        # A valid learned high-switch-frequency chain. A 60 ms contradiction can
        # remain an interruption; no post-decoder unconditional gap fill exists.
        t = h['transitions']
        t['exposure_seconds'], t['exits'] = [1., 1.], [60., 60.]
        t['rates_per_second'] = [30.25, 30.25]
        h['emission_prior'] = h['initial_prior'] = .5
        q = np.r_[np.full(15, 1 - 1e-6), np.full(3, 1e-6), np.full(15, 1 - 1e-6)]
        result = sequence.decode_scores(h, q, np.arange(len(q) + 1) * 20, sample_rate=1000)
        self.assertTrue(result['selected_mask'][5])
        self.assertFalse(result['selected_mask'][16])
        self.assertTrue(result['selected_mask'][-5])
        self.assertEqual(len(result['ranges']), 2)

    def test_eof_tail_and_short_recording_keep_exact_original_samples(self):
        h = head()
        for edges in (np.array([0, 7]), np.array([0, 20, 40, 43])):
            result = sequence.decode_scores(h, np.full(len(edges) - 1, .999999), edges, sample_rate=1000)
            self.assertTrue(result['selected_mask'].all())
            self.assertEqual(result['ranges'][0]['end_sample'], int(edges[-1]))
            self.assertEqual(result['final_available_sample'], int(edges[-1]))
        empty = sequence.decode_scores(h, [], np.array([0]), sample_rate=1000)
        self.assertEqual(empty['ranges'], [])
        self.assertEqual(len(empty['selected_mask']), 0)

    def test_forward_prefix_future_invariance_and_reset(self):
        h = head()
        prefix = [.1, .9, .7, .6]
        a = sequence.decode_scores(h, prefix + [.999] * 10, np.arange(15) * 20, sample_rate=1000)
        b = sequence.decode_scores(h, prefix + [.001] * 10, np.arange(15) * 20, sample_rate=1000)
        np.testing.assert_array_equal(a['forward_scores'][:4], b['forward_scores'][:4])
        online = sequence.ForwardEvidence(h, sample_rate=1000, stream_id='old')
        outputs = [online.append(q, i * 20, (i + 1) * 20, available_sample=280)
                   for i, q in enumerate(prefix)]
        np.testing.assert_array_equal([v['score'] for v in outputs], a['forward_scores'][:4])
        online.reset('new')
        first = online.append(prefix[0], 0, 20, available_sample=20)
        self.assertEqual(first['stream_id'], 'new')
        self.assertEqual(first['score'], outputs[0]['score'])
        self.assertEqual(first['revision'], 'provisional')
        self.assertTrue(first['may_revise'])
        self.assertFalse(first['execution_authorized'])

    def test_whole_file_availability_explicit_and_invalid_time_rejected(self):
        h = head()
        result = sequence.decode_scores(h, [.2, .9], np.array([0, 20, 23]), sample_rate=1000)
        self.assertEqual(result['availability_samples'].tolist(), [23, 23])
        self.assertFalse(result['full_pipeline_causal'])
        with self.assertRaisesRegex(ValueError, 'availability'):
            sequence.decode_scores(h, [.2, .9], np.array([0, 20, 23]), sample_rate=1000,
                                   availability_samples=[19, 23])
        online = sequence.ForwardEvidence(h, sample_rate=1000)
        with self.assertRaisesRegex(ValueError, 'availability'):
            online.append(.2, 0, 20, available_sample=19)
        with self.assertRaisesRegex(ValueError, 'noncontiguous'):
            online.append(.2, 1, 20, available_sample=20)

    def test_independent_control_uses_same_trained_emission_and_no_other_fit(self):
        item = prepared_item()
        h = sequence.fit_candidate({'a': item})['head']
        result = sequence.predict(h, item['features'], item['edges'], sample_rate=1000,
                                  feature_metadata=item['feature_metadata'], variant='independent')
        expected = fusion.predict_scores(h['emission_head'], item['features'], item['feature_metadata'])
        np.testing.assert_array_equal(result['scores'], expected)
        np.testing.assert_array_equal(result['selected_mask'], expected >= .5)

    def test_source_revision_definition_and_math_policy_fail_closed(self):
        item = prepared_item()
        candidate = sequence.fit_candidate({'a': item})
        sequence.validate_candidate(candidate, {'a': item['recording']})
        rec = copy.deepcopy(item['recording'])
        rec['annotation_revision'] += 1
        with self.assertRaisesRegex(ValueError, 'source_changed'):
            sequence.validate_candidate(candidate, {'a': rec})
        with self.assertRaisesRegex(ValueError, 'definition_mismatch'):
            sequence.validate_head(candidate['head'], dict(names=['renamed', 'constant']))
        bad = copy.deepcopy(candidate)
        bad['head']['policy']['evidence_reference_seconds'] = .020
        with self.assertRaisesRegex(ValueError, 'policy'):
            sequence.validate_candidate(bad)
        bad = copy.deepcopy(candidate)
        bad['head']['transitions']['rates_per_second'][0] *= 2
        with self.assertRaisesRegex(ValueError, 'rate_mismatch'):
            sequence.validate_candidate(bad)
        bad = copy.deepcopy(candidate)
        bad['source_bindings']['a']['feature_sha256'] = 'f' * 64
        with self.assertRaisesRegex(ValueError, 'bindings_hash'):
            sequence.validate_candidate(bad)
        bad = copy.deepcopy(candidate)
        bad['head']['schema'] = 'future-version'
        with self.assertRaisesRegex(ValueError, 'schema'):
            sequence.validate_candidate(bad)


if __name__ == '__main__':
    unittest.main()
