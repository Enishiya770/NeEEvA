"""Synthetic-only numerical/regression checks; no actual acoustic fitting."""
import copy
import json
import math
import unittest
from unittest.mock import patch

import numpy as np

import fit_score_adapter as adapter


LABELS = list(adapter.DIRECT_LABELS + adapter.SPEECH_FAMILY[1:] + adapter.SINGING_FAMILY[1:])
METADATA = adapter.feature_metadata(LABELS)


def sample_data():
    t = np.linspace(.05, .95, 31)
    raw = np.full((len(t), len(LABELS)), .03)
    raw[:, LABELS.index('Speech')] = 1-t
    raw[:, LABELS.index('Singing')] = t
    raw[:, LABELS.index('Male speech, man speaking')] = .8*(1-t)
    raw[:, LABELS.index('Male singing')] = .8*t
    raw[:, LABELS.index('Music')] = .1+.2*t
    x = adapter.build_features(raw, LABELS)
    y = (t > .5).astype(float)
    weights = np.linspace(.2, 2, len(t))
    return x, y, weights, raw


def fit(x, y, weights):
    return adapter.fit_score_adapter(x, y, weights, feature_metadata=METADATA)


class ScoreAdapterTests(unittest.TestCase):
    def test_nine_preregistered_features_and_optional_child(self):
        row = np.full((1, len(LABELS)), .1)
        row[0, LABELS.index('Speech')] = .2
        row[0, LABELS.index('Singing')] = .4
        row[0, LABELS.index('Male speech, man speaking')] = .6
        row[0, LABELS.index('Child singing')] = .9
        x, metadata = adapter.build_features(row, LABELS, return_metadata=True)
        self.assertEqual(x.shape, (1, 9))
        self.assertAlmostEqual(x[0, 0], math.log(.2/.8))
        self.assertAlmostEqual(x[0, 2], math.log(.6/.4))
        self.assertAlmostEqual(x[0, 3], math.log(.9/.1))
        self.assertAlmostEqual(x[0, 7], .3)
        self.assertAlmostEqual(x[0, 8], .2)
        self.assertEqual(metadata['actual_singing_family_members'], list(adapter.SINGING_FAMILY))
        reduced = [n for n in LABELS if not n.startswith('Child')]
        _, reduced_metadata = adapter.build_features(row[:, [LABELS.index(n) for n in reduced]], reduced, return_metadata=True)
        self.assertEqual(reduced_metadata['actual_singing_family_members'], list(adapter.SINGING_FAMILY[:-1]))
        with self.assertRaisesRegex(ValueError, 'missing_required_raw_labels'):
            adapter.feature_metadata([n for n in reduced if n != 'Humming'])
        with self.assertRaisesRegex(ValueError, 'missing_required_raw_labels'):
            adapter.feature_metadata([n for n in reduced if n != 'Female singing'])

    def test_extreme_frozen_scores_are_finite_and_clip_is_fixed(self):
        raw = np.vstack((np.zeros(len(LABELS)), np.ones(len(LABELS))))
        x = adapter.build_features(raw, LABELS)
        self.assertTrue(np.isfinite(x).all())
        self.assertAlmostEqual(x[0, 0], math.log(1e-5/(1-1e-5)))
        with self.assertRaises(ValueError):
            adapter.build_features(raw+.01, LABELS)

    def test_weighted_fit_has_correct_direction_and_converges(self):
        x, y, weights, _ = sample_data()
        model = fit(x, y, weights)
        score = adapter.predict_uncalibrated_score(model, x)
        self.assertLess(score[0], .2)
        self.assertGreater(score[-1], .8)
        self.assertTrue(np.all(np.diff(score) > 0))
        self.assertTrue(model['convergence']['converged'])
        self.assertLessEqual(model['convergence']['gradient_norm_inf'], 1e-8)
        self.assertLessEqual(model['convergence']['iterations'], 200)
        self.assertTrue(all(a+1e-12 >= b for a, b in zip(model['convergence']['objective_history'], model['convergence']['objective_history'][1:])))
        self.assertFalse(model['execution_authorized'])
        self.assertEqual(model['output_name'], 'uncalibrated_score')
        json.dumps(model, allow_nan=False)

    def test_weight_scaling_does_not_change_fit(self):
        x, y, weights, _ = sample_data()
        small = fit(x, y, weights)
        large = fit(x, y, weights*1e100)
        np.testing.assert_allclose(small['standardizer']['mean'], large['standardizer']['mean'], rtol=1e-12, atol=1e-12)
        np.testing.assert_allclose(small['weights'], large['weights'], rtol=1e-9, atol=1e-9)
        self.assertAlmostEqual(small['bias'], large['bias'], places=9)
        np.testing.assert_allclose(adapter.predict_uncalibrated_score(small, x), adapter.predict_uncalibrated_score(large, x), atol=1e-10)

    def test_soft_targets_weighted_prior_and_unpenalized_bias(self):
        x = np.zeros((2, 9))
        model = fit(x, [.2, .8], [3, 1])
        self.assertAlmostEqual(model['bias'], math.log(.35/.65), places=10)
        np.testing.assert_array_equal(model['weights'], np.zeros(9))
        np.testing.assert_allclose(adapter.predict_uncalibrated_score(model, x), [.35, .35], atol=1e-12)
        self.assertTrue(all(v == 1e-6 for v in model['standardizer']['std']))
        both_classes_in_each_cell = fit(x, [.5, .5], [1, 2])
        self.assertEqual(both_classes_in_each_cell['training_summary']['normalized_positive_mass'], .5)

    def test_one_class_including_zero_weight_other_class_rejected(self):
        x = np.zeros((2, 9))
        for y, weights in (([0, 0], [1, 1]), ([1, 1], [1, 1]), ([0, 1], [1, 0])):
            with self.assertRaises(adapter.TrainingDataError) as error:
                fit(x, y, weights)
            self.assertEqual(error.exception.reason, 'one_class_training')

    def test_standardizer_only_uses_weighted_train_rows(self):
        x = np.zeros((3, 9))
        x[:, 0] = [-2, 2, 1000000]
        model = fit(x, [0, 1, 1], [1, 3, 0])
        self.assertAlmostEqual(model['standardizer']['mean'][0], 1.0)
        self.assertAlmostEqual(model['standardizer']['std'][0], math.sqrt(3))
        before = copy.deepcopy(model)
        test_only = np.full((2, 9), 1e6)
        adapter.predict_uncalibrated_score(model, test_only)
        self.assertEqual(model, before)

    def test_serialization_roundtrip_and_label_order_prediction_equivalence(self):
        x, y, weights, raw = sample_data()
        model = fit(x, y, weights)
        restored = adapter.deserialize_adapter(adapter.serialize_adapter(model))
        expected = adapter.predict_uncalibrated_score(model, x)
        np.testing.assert_array_equal(expected, adapter.predict_uncalibrated_score(restored, x))
        reversed_labels = list(reversed(LABELS))
        np.testing.assert_array_equal(expected, adapter.predict_from_scores(restored, raw[:, ::-1], reversed_labels))
        reduced = [n for n in LABELS if n != 'Child singing']
        with self.assertRaisesRegex(ValueError, 'inference_family_members_differ'):
            adapter.predict_from_scores(model, raw[:, [LABELS.index(n) for n in reduced]], reduced)

    def test_objective_derivatives_match_numerical_differentiation(self):
        design = np.array([[.2, -.4, 1], [.8, .3, 1], [-.6, .5, 1]])
        theta = np.array([.3, -.2, .1]);y=np.array([.1, .7, .4]);weights=np.array([.2, .5, .3])
        _, gradient, hessian = adapter._loss_gradient_hessian(theta, design, y, weights)
        numeric_gradient=[];numeric_hessian=[];epsilon=1e-5
        for i in range(len(theta)):
            delta=np.zeros_like(theta);delta[i]=epsilon
            plus=adapter._loss_gradient_hessian(theta+delta, design, y, weights)
            minus=adapter._loss_gradient_hessian(theta-delta, design, y, weights)
            numeric_gradient.append((plus[0]-minus[0])/(2*epsilon))
            numeric_hessian.append((plus[1]-minus[1])/(2*epsilon))
        np.testing.assert_allclose(gradient, numeric_gradient, atol=1e-9)
        np.testing.assert_allclose(hessian, np.array(numeric_hessian).T, atol=1e-9)

    def test_clear_convergence_failure_returns_no_artifact(self):
        x, y, weights, _ = sample_data()
        # Test-only internal budget patch, never exposed as tuning at public API.
        with patch.object(adapter, 'MAX_ITERATIONS', 0):
            with self.assertRaises(adapter.ConvergenceError) as error:
                fit(x, y, weights)
        self.assertEqual(error.exception.reason, 'maximum_iterations_exceeded')
        self.assertFalse(error.exception.diagnostics['converged'])
        self.assertTrue(math.isfinite(error.exception.diagnostics['objective']))

    def test_fixed_thresholds_do_not_fill_holes_or_smooth(self):
        masks = adapter.threshold_masks([.6, .4, .95, .8])
        self.assertEqual(list(masks), ['0.5', '0.8', '0.95'])
        np.testing.assert_array_equal(masks['0.5'], [True, False, True, True])
        np.testing.assert_array_equal(masks['0.95'], [False, False, True, False])

    def test_invalid_data_and_artifact_authority_fail_closed(self):
        x, y, weights, _ = sample_data()
        for bad in (np.full(len(x), -1), np.full(len(x), float('nan')), np.zeros(len(x))):
            with self.assertRaises(adapter.TrainingDataError):
                fit(x, y, bad)
        model=fit(x,y,weights);model['execution_authorized']=True
        with self.assertRaises(ValueError):
            adapter.predict_uncalibrated_score(model, x)


if __name__ == '__main__':
    unittest.main()
