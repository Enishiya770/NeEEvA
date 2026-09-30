import copy
import unittest

import numpy as np

from . import semantic_model as semantic
from .test_boundary_model import item as boundary_item


def item(rid='a', regions=None, samples=1000, source='user'):
    regions = regions or [(0, 300, 'environment'), (300, 600, 'speech'), (600, samples, 'singing')]
    result = boundary_item(rid, regions=regions, samples=samples, source=source)
    centers = (result['edges'][:-1] + result['edges'][1:]) / 2.
    for i, center in enumerate(centers):
        label = next(label for a, b, label in regions if a <= center < b)
        cls = semantic.CLASSES.index(label) if label in semantic.CLASSES else 2
        result['features'][i, :9] = np.tile(np.eye(3)[cls], 3)
        result['features'][i, -6:] = np.tile(np.eye(3)[cls], 2)
    return result


class SemanticTests(unittest.TestCase):
    def test_softmax_gradient_and_hessian_finite_differences(self):
        rng = np.random.default_rng(431)
        design = np.column_stack((rng.normal(size=(7, 2)), np.ones(7)))
        targets = rng.uniform(size=(7, 3)); targets /= targets.sum(axis=1, keepdims=True)
        weights = rng.uniform(size=7); weights /= weights.sum()
        theta = rng.normal(scale=.2, size=(3, 2))
        loss, gradient, hessian = semantic._objective(theta, design, targets, weights)
        flat = theta.flatten(order='F'); eps = 1e-5
        numerical_gradient, numerical_hessian = np.zeros_like(flat), np.zeros_like(hessian)
        for index in range(len(flat)):
            left, right = flat.copy(), flat.copy()
            left[index] -= eps; right[index] += eps
            l = semantic._objective(left.reshape(theta.shape, order='F'), design, targets, weights)
            r = semantic._objective(right.reshape(theta.shape, order='F'), design, targets, weights)
            numerical_gradient[index] = (r[0] - l[0]) / (2 * eps)
            numerical_hessian[:, index] = (r[1] - l[1]).flatten(order='F') / (2 * eps)
        np.testing.assert_allclose(gradient.flatten(order='F'), numerical_gradient, rtol=1e-6, atol=1e-8)
        np.testing.assert_allclose(hessian, numerical_hessian, rtol=1e-6, atol=1e-8)
        self.assertTrue(np.isfinite(loss))

    def test_repeating_a_whole_correlated_block_does_not_buy_cheaper_ridge(self):
        x = np.array([[-2., -1.], [-1., 1.], [0., -2.], [.5, 2.], [1., .5], [2., 1.]])
        y = np.eye(3)[[0, 0, 2, 2, 1, 1]]
        original = semantic.fit_softmax(x, y, np.ones(6), feature_metadata=dict(names=['acoustic', 'pitch']), blocks=[[0], [1]])
        repeated = np.column_stack((x[:, 0], np.tile(x[:, 1, None], (1, 8))))
        clone = semantic.fit_softmax(repeated, y, np.ones(6),
            feature_metadata=dict(names=['acoustic'] + ['pitch_' + str(i) for i in range(8)]), blocks=[[0], list(range(1, 9))])
        np.testing.assert_allclose(semantic.predict_softmax(original, x), semantic.predict_softmax(clone, repeated), atol=1e-7)

    def test_train_only_normalization_ignores_zero_weight_rows(self):
        x = np.array([[-1., 3.], [1., 5.], [9999., -9999.]])
        head = semantic.fit_softmax(x, np.eye(3)[[0, 1, 2]], [1., 3., 0.],
                                     feature_metadata=dict(names=['a', 'b']), blocks=[[0], [1]])
        np.testing.assert_allclose(head['standardizer']['mean'], [.5, 4.5])
        self.assertEqual(head['active_classes'], [0, 1])
        self.assertTrue((semantic.predict_softmax(head, x)[:, 2] == 0).all())

    def test_node_three_classes_and_subset_destination_rows_are_available(self):
        a = item()
        candidate = semantic.fit_candidate({'a': a})
        self.assertTrue(candidate['semantic_training_available'])
        self.assertEqual(candidate['head']['node_head']['active_classes'], [0, 1, 2])
        self.assertEqual(candidate['head']['row_heads'][1]['active_classes'], [1])
        semantic.validate_candidate(candidate, {'a': a['recording']})
        result = semantic.predict(candidate['head'], a['features'], a['edges'], sample_rate=1000)
        self.assertEqual(result['class_names'], list(semantic.CLASS_NAMES))
        np.testing.assert_array_equal(result['selected_mask'], result['selected_states'] == 1)
        np.testing.assert_array_equal(result['scores'], result['category_scores'][:, 1])
        np.testing.assert_allclose(result['category_scores'].sum(axis=1), 1.)
        self.assertFalse(result['full_pipeline_causal'])
        self.assertTrue((result['availability_samples'] == 1000).all())

    def test_known_singing_pause_is_stay_target_not_phonation_offset(self):
        a = item()
        a['features'][35:40, -6:] = 0.
        examples, _, _, events, excluded = semantic._row_examples({'a': a}, ['a'])
        song = examples[1]
        self.assertTrue(song['y'])
        self.assertEqual(set(song['y']), {1})
        self.assertFalse(any(event[1] == 1 for event in events['a']))
        self.assertEqual(excluded['unknown_support'], 0)

    def test_multiple_transition_support_and_unknown_are_censored(self):
        a = item(regions=[(0, 310, 'speech'), (310, 315, 'singing'), (315, 600, 'speech'),
                          (600, 700, 'uncertain'), (700, 1000, 'environment')])
        examples, _, _, _, excluded = semantic._row_examples({'a': a}, ['a'])
        self.assertEqual(excluded['multiple_transitions'], 1)
        self.assertGreater(excluded['unknown_support'], 0)
        for row in examples:
            self.assertFalse(any(record['sample'] == 320 for record in row['provenance']))

    def test_exact_prefix_event_and_duration_dedup_are_train_only(self):
        a, b = item(), item('b')
        a['duplicate_prefixes'], b['duplicate_prefixes'] = {'b': 1000}, {'a': 1000}
        alone = semantic.fit_candidate({'a': a, 'b': b}, ids=['a'])
        together = semantic.fit_candidate({'a': a, 'b': b})
        np.testing.assert_allclose(alone['head']['transitions']['event_counts'], together['head']['transitions']['event_counts'])
        np.testing.assert_allclose(alone['head']['transitions']['exposure_seconds'], together['head']['transitions']['exposure_seconds'])
        np.testing.assert_allclose(alone['head']['node_head']['weights'], together['head']['node_head']['weights'], atol=1e-8)

    def test_heldout_or_playback_values_do_not_affect_learned_heads(self):
        a, b, c = item(), item('heldout'), item('playback', source='playback')
        b['features'] *= 9999
        baseline = semantic.fit_candidate({'a': a})
        result = semantic.fit_candidate({'a': a, 'heldout': b, 'playback': c}, ids=['a', 'playback'])
        self.assertEqual(result['head']['node_head'], baseline['head']['node_head'])
        self.assertEqual(result['head']['row_heads'], baseline['head']['row_heads'])
        self.assertEqual(set(result['source_bindings']), {'a'})

    def test_missing_node_category_returns_safe_diagnostic_not_adoptable_model(self):
        a = item(regions=[(0, 500, 'speech'), (500, 1000, 'singing')])
        candidate = semantic.fit_candidate({'a': a})
        self.assertFalse(candidate['semantic_training_available'])
        self.assertIsNone(candidate['head']['row_heads'][2])
        semantic.validate_candidate(candidate)
        result = semantic.predict(candidate['head'], a['features'], a['edges'], sample_rate=1000)
        self.assertTrue((result['category_scores'][:, 2] == 0).all())
        self.assertTrue(np.isfinite(result['local_transition_matrices']).all())
        self.assertTrue((result['local_transition_matrices'] > 0).all())

    def test_ctmc_semigroup_and_local_mixture_are_stochastic(self):
        rates = np.array([[0., .2, .1], [.05, 0., .07], [.03, .1, 0.]])
        np.testing.assert_allclose(semantic.transition_matrix(rates, .02) @ semantic.transition_matrix(rates, .03),
                                   semantic.transition_matrix(rates, .05), atol=1e-12)
        a = item(); h = semantic.fit_candidate({'a': a})['head']
        rows = [np.eye(3)[np.full(1, source)] for source in range(3)]
        result = semantic.decode_categories(h, [[.7, .2, .1], [.7, .2, .1]], np.array([0, 20, 40]), rows, sample_rate=1000)
        expected = .5 * semantic.transition_matrix(h['transitions']['rates_per_second'], .02) + .5 * np.eye(3)
        np.testing.assert_allclose(result['local_transition_matrices'][0], expected)
        np.testing.assert_allclose(result['local_transition_matrices'].sum(axis=2), 1.)

    def test_direct_category_evidence_does_not_subtract_training_prior(self):
        a = item(); h = semantic.fit_candidate({'a': a})['head']
        empty_rows = [np.empty((0, 3))] * 3
        result = semantic.decode_categories(h, [[.4, .45, .15]], np.array([0, 3]), empty_rows, sample_rate=1000)
        self.assertEqual(result['selected_states'].tolist(), [1])
        changed = copy.deepcopy(h)
        changed['node_head']['class_training_mass'] = [.1, .8, .1]
        other = semantic.decode_categories(changed, [[.4, .45, .15]], np.array([0, 3]), empty_rows, sample_rate=1000)
        np.testing.assert_array_equal(result['selected_states'], other['selected_states'])

    def test_strong_real_short_song_and_exact_eof_tail_are_not_hard_removed(self):
        h = semantic.fit_candidate({'a': item()})['head']
        categories = np.tile([.999999, .0000005, .0000005], (23, 1))
        categories[10:13] = [.0000005, .999999, .0000005]
        # Local rows condition destinations on the source category, allowing
        # observed onset/offset evidence while retaining stays elsewhere.
        rows = [np.tile(np.eye(3)[source], (22, 1)) for source in range(3)]
        rows[0][9] = [0., 1., 0.]; rows[1][12] = [1., 0., 0.]
        result = semantic.decode_categories(h, categories, np.arange(24) * 20, rows, sample_rate=1000)
        self.assertTrue(result['selected_mask'][10:13].all())
        self.assertFalse(result['selected_mask'][:10].any())
        self.assertFalse(result['selected_mask'][13:].any())
        tail = semantic.decode_categories(h, np.tile([.001, .998, .001], (3, 1)), np.array([0, 20, 40, 43]),
            [np.tile(np.eye(3)[source], (2, 1)) for source in range(3)], sample_rate=1000)
        self.assertEqual(tail['ranges'][0]['end_sample'], 43)

    def test_policy_code_source_revision_and_nonfinite_fail_closed(self):
        a = item(); candidate = semantic.fit_candidate({'a': a})
        changed = copy.deepcopy(a['recording']); changed['annotation_revision'] += 1
        with self.assertRaisesRegex(ValueError, 'source_changed'):
            semantic.validate_candidate(candidate, {'a': changed})
        for change in ('source', 'policy', 'nan'):
            bad = copy.deepcopy(candidate)
            if change == 'source': bad['head']['source_code_sha256'] = 'changed'
            if change == 'policy': bad['head']['policy']['transition_mix'] = .9
            if change == 'nan': bad['head']['node_head']['weights'][0][0] = float('nan')
            with self.assertRaises(ValueError): semantic.validate_candidate(bad)


if __name__ == '__main__':
    unittest.main()
