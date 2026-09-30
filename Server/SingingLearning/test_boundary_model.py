import copy
import unittest

import numpy as np

from . import boundary_model as boundary
from . import pitch_features as pitch
from . import sequence_features as frontend
from . import sequence_model as sequence


def item(rid='a', *, regions=None, samples=1000, source='user'):
    regions = regions or [(0, 200, 'speech'), (200, 700, 'singing'), (700, samples, 'speech')]
    names = ['panns_' + str(i) for i in range(9)]
    names += [name + '_trailing_' + str(ms) + 'ms' for ms in (80, 320, 1000) for name in pitch.FEATURE_NAMES]
    names += list(frontend.RAW_NAMES)
    edges = np.r_[np.arange(0, samples, 20, dtype=np.int64), samples]
    centers = (edges[:-1] + edges[1:]) / 2.
    truth = np.array([any(a <= center < b and kind == 'singing' for a, b, kind in regions) for center in centers], dtype=float)
    features = np.tile(truth[:, None], (1, 63))
    features[:, -6] += .1
    recording = dict(recording_id=rid, audio_sha256='0' * 64, sample_rate=1000,
        sample_count=samples, annotation_revision=1, session_id='same', content_group='same',
        annotation=dict(confirmed=True, source_kind=source,
            regions=[dict(start_sample=a, end_sample=b, label=kind, tolerance_samples=0) for a, b, kind in regions]))
    return dict(recording=recording, edges=edges, features=features, feature_metadata=dict(names=names),
                feature_sha256='1' * 64, pitch_source_sha256='2' * 64)


class BoundaryModelTests(unittest.TestCase):
    def test_local_features_use_actual_left_right_duration_not_cell_count(self):
        a = item(samples=43, regions=[(0, 20, 'speech'), (20, 43, 'singing')])
        a['features'][:] = 0.
        a['features'][1:, 0] = [2., 4.]
        rows, metadata = boundary.boundary_features(a['features'], a['edges'], a['feature_metadata'], sample_rate=1000)
        self.assertEqual(rows.shape, (2, 93))
        self.assertEqual(rows[0, 0], 0.)
        self.assertAlmostEqual(rows[0, 31], (20 * 2 + 3 * 4) / 23)
        self.assertAlmostEqual(rows[1, 0], 1.)
        self.assertEqual(rows[1, 31], 4.)
        np.testing.assert_allclose(rows[:, 62:], rows[:, 31:62] - rows[:, :31])
        self.assertFalse(metadata['full_pipeline_causal'])

    def test_separate_onset_and_offset_heads_use_exact_events(self):
        a = item()
        for kind, point in (('onset', 200), ('offset', 700)):
            x, y, weights, metadata, stats = boundary.training_examples({'a': a}, ['a'], kind)
            self.assertEqual(x.shape[1], 93)
            self.assertEqual(y.sum(), 1)
            self.assertAlmostEqual(weights[y == 1].sum(), .5)
            self.assertAlmostEqual(weights[y == 0].sum(), .5)
            self.assertEqual(stats['positive_rows_detail'][0]['event_samples'], [point])
            self.assertEqual(stats['positive_event_mass'], 1.)
        candidate = boundary.fit_candidate({'a': a})
        self.assertTrue(candidate['boundary_training_available'])
        self.assertEqual(candidate['head']['sequence_head'], candidate['sequence_candidate']['head'])
        boundary.validate_candidate(candidate, {'a': a['recording']})

    def test_half_open_voronoi_owns_boundary_once_and_uses_later_tie(self):
        a = item(regions=[(0, 210, 'speech'), (210, 700, 'singing'), (700, 1000, 'speech')])
        *_, stats = boundary.training_examples({'a': a}, ['a'], 'onset')
        self.assertEqual(stats['positive_rows'], 1)
        row = stats['positive_rows_detail'][0]
        self.assertEqual(row['edge_sample'], 220)
        self.assertEqual(row['support_start_sample'], 210)
        self.assertEqual(row['event_samples'], [210])

    def test_unknown_and_tolerance_supports_are_censored_not_negative(self):
        a = item(regions=[(0, 190, 'speech'), (190, 210, 'uncertain'),
                          (210, 700, 'singing'), (700, 1000, 'speech')])
        with self.assertRaisesRegex(ValueError, 'onset_missing_training_classes'):
            boundary.training_examples({'a': a}, ['a'], 'onset')
        *_, stats = boundary.training_examples({'a': a}, ['a'], 'offset')
        self.assertGreater(stats['censored_rows'], 0)
        self.assertEqual(stats['positive_event_mass'], 1.)
        a = item()
        a['recording']['annotation']['regions'][1]['tolerance_samples'] = 1
        with self.assertRaisesRegex(ValueError, 'missing_training_classes'):
            boundary.training_examples({'a': a}, ['a'], 'offset')

    def test_exact_prefix_events_deduplicate_only_same_event_not_known_partner(self):
        a, b = item(), item('b')
        a['duplicate_prefixes'], b['duplicate_prefixes'] = {'b': 1000}, {'a': 1000}
        *_, together = boundary.training_examples({'a': a, 'b': b}, ['a', 'b'], 'onset')
        *_, alone = boundary.training_examples({'a': a, 'b': b}, ['a'], 'onset')
        self.assertEqual(together['positive_event_mass'], 1.)
        self.assertEqual(together['positive_event_mass'], alone['positive_event_mass'])
        self.assertAlmostEqual(together['negative_known_seconds'], alone['negative_known_seconds'])
        # Human disagreement remains present; an event at another source sample
        # is not silently declared to be the same confirmed transition.
        b['recording']['annotation']['regions'][0]['end_sample'] = 220
        b['recording']['annotation']['regions'][1]['start_sample'] = 220
        *_, disputed = boundary.training_examples({'a': a, 'b': b}, ['a', 'b'], 'onset')
        self.assertEqual(disputed['positive_event_mass'], 2.)

    def test_heldout_and_playback_do_not_affect_fitted_head(self):
        a, held = item(), item('held')
        held['features'] *= 1000
        playback = item('playback', source='playback')
        baseline = boundary.fit_candidate({'a': a})
        result = boundary.fit_candidate({'a': a, 'held': held, 'playback': playback}, ids=['a', 'playback'])
        # Statistics count censored playback rows for diagnostics, so compare
        # learned parameters and actual confirmed source bindings, not JSON IDs.
        self.assertEqual(result['source_bindings'], baseline['source_bindings'])
        for kind in ('onset', 'offset'):
            self.assertEqual(result['head'][kind + '_head'], baseline['head'][kind + '_head'])

    def test_local_gates_can_recover_short_island_without_forced_switch(self):
        h = sequence.fit_candidate({'a': item()})['head']
        h['emission_prior'] = h['initial_prior'] = .5
        q = np.r_[np.full(10, .001), np.full(3, .999), np.full(10, .001)]
        edges = np.arange(len(q) + 1, dtype=np.int64) * 20
        closed = np.zeros(len(q) - 1)
        on, off = closed.copy(), closed.copy()
        on[9], off[12] = .99, .99
        seed = sequence.decode_scores(h, q, edges, sample_rate=1000)
        refined = boundary.refine_scores(h, q, edges, on, off, sample_rate=1000)
        self.assertFalse(seed['selected_mask'][11])
        np.testing.assert_array_equal(refined['selected_mask'], q > .5)
        # Opening exactly the same gates cannot synthesize a song when the
        # acoustic evidence throughout says non-singing.
        contradicted = boundary.refine_scores(h, np.full(len(q), .001), edges, on, off, sample_rate=1000)
        self.assertFalse(contradicted['selected_mask'].any())

    def test_eof_does_not_require_offset_event_and_exact_tail_survives(self):
        h = sequence.fit_candidate({'a': item()})['head']
        h['emission_prior'] = h['initial_prior'] = .5
        q = np.array([.001, .001, .999999])
        edges = np.array([0, 20, 40, 43])
        result = boundary.refine_scores(h, q, edges, [0., 1.], [0., 0.], sample_rate=1000)
        self.assertEqual(result['ranges'], [dict(start_sample=40, end_sample=43, label='singing')])
        one = boundary.refine_scores(h, [.999], np.array([0, 3]), [], [], sample_rate=1000)
        self.assertEqual(one['ranges'][0]['end_sample'], 3)

    def test_no_event_examples_fall_back_to_exact_unchanged_sequence(self):
        # There is a real onset, but the song runs to EOF, which must not create
        # a synthetic negative-side offset example.
        a = item(regions=[(0, 200, 'speech'), (200, 1000, 'singing')])
        candidate = boundary.fit_candidate({'a': a})
        self.assertFalse(candidate['boundary_training_available'])
        self.assertIsNone(candidate['head']['offset_head'])
        boundary.validate_candidate(candidate)
        result = boundary.predict(candidate['head'], a['features'], a['edges'], sample_rate=1000)
        self.assertFalse(result['boundary_model_available'])
        np.testing.assert_array_equal(result['selected_mask'], result['seed_selected_mask'])
        self.assertFalse(result['onset_gates'].any())

    def test_binding_policy_and_nonfinite_values_rejected(self):
        a = item()
        candidate = boundary.fit_candidate({'a': a})
        changed = copy.deepcopy(a['recording'])
        changed['annotation_revision'] += 1
        with self.assertRaisesRegex(ValueError, 'source_changed'):
            boundary.validate_candidate(candidate, {'a': changed})
        bad = copy.deepcopy(candidate)
        bad['head']['policy']['boundary_gate'] = .4
        with self.assertRaisesRegex(ValueError, 'policy'):
            boundary.validate_candidate(bad)
        bad = copy.deepcopy(candidate)
        bad['head']['onset_head']['weights'][0] = float('nan')
        with self.assertRaises(ValueError):
            boundary.validate_candidate(bad)
        bad = copy.deepcopy(candidate)
        bad['head']['onset_head']['feature_metadata']['extractor_source_sha256'] = 'changed'
        with self.assertRaisesRegex(ValueError, 'definition'):
            boundary.validate_candidate(bad)
        with self.assertRaisesRegex(ValueError, 'boundary_scores'):
            boundary.refine_scores(candidate['head']['sequence_head'], [.5, .5], np.array([0, 20, 40]),
                                   [float('nan')], [.5], sample_rate=1000)

    def test_forward_output_remains_explicit_seed_diagnostic(self):
        a = item()
        candidate = boundary.fit_candidate({'a': a})
        result = boundary.predict(candidate['head'], a['features'], a['edges'], sample_rate=1000)
        self.assertEqual(result['forward_variant'], 'unmodified_sequence_provisional_not_boundary_refinement')
        self.assertFalse(result['full_pipeline_causal'])
        self.assertTrue((result['availability_samples'] == 1000).all())
        self.assertEqual(result['local_boundary_available_samples'][0], 100)
        self.assertEqual(result['revision'], 'final')


if __name__ == '__main__':
    unittest.main()
