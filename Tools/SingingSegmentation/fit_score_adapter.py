"""Pure offline adapter for frozen acoustic scores, with no data splitting.

X, metadata = build_features(scores, labels, return_metadata=True)
model = fit_score_adapter(X_train, y_train, weights_train,
                          feature_metadata=metadata)
scores = predict_uncalibrated_score(model, X_test)

Only the caller selects training rows and computes known-sample overlap weights.
This module never reads labels/audio/files, selects thresholds, smooths scores,
fills gaps, filters segments, or grants playback/crop authority.
"""
from __future__ import annotations

import copy
import json
import math
import time

import numpy as np

SCHEMA = 'neeeva.frozen-score-adapter.v1'
LOGIT_CLIP = 1e-5
STD_FLOOR = 1e-6
L2_LAMBDA = 0.1
MAX_ITERATIONS = 200
GRADIENT_TOLERANCE = 1e-8
ARMIJO_C = 1e-4
MAX_BACKTRACKS = 50
FIXED_THRESHOLDS = (0.5, 0.8, 0.95)

SPEECH_FAMILY = ('Speech', 'Male speech, man speaking',
                 'Female speech, woman speaking', 'Child speech, kid speaking')
SINGING_FAMILY = ('Singing', 'Male singing', 'Female singing', 'Child singing')
DIRECT_LABELS = ('Speech', 'Singing', 'Music', 'Humming', 'Rapping')
REQUIRED_LABELS = DIRECT_LABELS + SPEECH_FAMILY[1:3] + SINGING_FAMILY[1:3]
FEATURE_NAMES = (
    'speech_direct_logit', 'singing_direct_logit',
    'speech_family_max_logit', 'singing_family_max_logit',
    'music_logit', 'humming_logit', 'rapping_logit',
    'singing_family_minus_speech_family', 'singing_direct_minus_speech_direct',
)
FEATURE_DEFINITIONS = (
    'logit(clip(Speech, 1e-5, 1-1e-5))',
    'logit(clip(Singing, 1e-5, 1-1e-5))',
    'logit(clip(max(actual_speech_family_members), 1e-5, 1-1e-5))',
    'logit(clip(max(actual_singing_family_members), 1e-5, 1-1e-5))',
    'logit(clip(Music, 1e-5, 1-1e-5))',
    'logit(clip(Humming, 1e-5, 1-1e-5))',
    'logit(clip(Rapping, 1e-5, 1-1e-5))',
    'max(actual_singing_family_members) - max(actual_speech_family_members)',
    'Singing - Speech',
)


class TrainingDataError(ValueError):
    def __init__(self, reason, diagnostics=None):
        self.reason = reason
        self.diagnostics = diagnostics or {}
        super().__init__(reason)


class ConvergenceError(RuntimeError):
    def __init__(self, reason, diagnostics):
        self.reason = reason
        self.diagnostics = diagnostics
        super().__init__(reason)


def feature_metadata(labels):
    """Fixed definition and actual members, in canonical family order."""
    labels = list(labels)
    if not all(isinstance(v, str) for v in labels) or len(set(labels)) != len(labels):
        raise ValueError('invalid_or_duplicate_label_names')
    missing = [name for name in REQUIRED_LABELS if name not in labels]
    if missing:
        raise ValueError('missing_required_raw_labels: ' + ', '.join(missing))
    return {
        'names': list(FEATURE_NAMES), 'definitions': list(FEATURE_DEFINITIONS),
        'logit_clip': LOGIT_CLIP, 'input_label_order': labels,
        'family_member_order': 'canonical_parent_male_female_optional_child',
        'actual_speech_family_members': [v for v in SPEECH_FAMILY if v in labels],
        'actual_singing_family_members': [v for v in SINGING_FAMILY if v in labels],
        'family_aggregation': 'maximum_raw_score_not_probability_union',
    }


def build_features(scores, labels, *, return_metadata=False):
    metadata = feature_metadata(labels)
    labels = metadata['input_label_order']
    values = np.asarray(scores, dtype=np.float64)
    if values.ndim != 2 or values.shape[1] != len(labels):
        raise ValueError('invalid_raw_score_shape')
    if not np.isfinite(values).all() or np.any(values < 0) or np.any(values > 1):
        raise ValueError('raw_scores_must_be_finite_in_unit_interval')
    direct = {name: values[:, labels.index(name)] for name in DIRECT_LABELS}
    speech_family = np.max(values[:, [labels.index(v) for v in metadata['actual_speech_family_members']]], axis=1)
    singing_family = np.max(values[:, [labels.index(v) for v in metadata['actual_singing_family_members']]], axis=1)
    seven = np.column_stack((direct['Speech'], direct['Singing'], speech_family,
                             singing_family, direct['Music'], direct['Humming'], direct['Rapping']))
    clipped = np.clip(seven, LOGIT_CLIP, 1.0 - LOGIT_CLIP)
    logits = np.log(clipped) - np.log1p(-clipped)
    result = np.column_stack((logits, singing_family-speech_family,
                              direct['Singing']-direct['Speech']))
    return (result, metadata) if return_metadata else result


def _features(values, *, allow_empty=False):
    values = np.asarray(values, dtype=np.float64)
    if values.ndim != 2 or values.shape[1] != len(FEATURE_NAMES) or (not allow_empty and not len(values)):
        raise TrainingDataError('features_must_have_shape_N_by_9')
    if not np.isfinite(values).all():
        raise TrainingDataError('nonfinite_features')
    return values


def _validate_metadata(metadata):
    if not isinstance(metadata, dict):
        raise ValueError('feature_metadata_required')
    expected = feature_metadata(metadata.get('input_label_order', []))
    if metadata != expected:
        raise ValueError('feature_metadata_not_fixed_definition')
    return copy.deepcopy(expected)


def _sigmoid(logits):
    logits = np.asarray(logits, dtype=np.float64)
    result = np.empty_like(logits)
    positive = logits >= 0
    result[positive] = 1.0 / (1.0 + np.exp(-logits[positive]))
    negative_exp = np.exp(logits[~positive])
    result[~positive] = negative_exp / (1.0 + negative_exp)
    return result


def _loss_gradient_hessian(theta, design, targets, normalized_weights):
    logits = design @ theta
    probability = _sigmoid(logits)
    # Branch form avoids loss of small soft-label contributions for large z.
    bce = np.where(logits >= 0, (1.0-targets)*logits, -targets*logits)
    bce += np.logaddexp(0.0, -np.abs(logits))
    objective = float(normalized_weights @ bce + L2_LAMBDA/2.0 * (theta[:-1] @ theta[:-1]))
    gradient = design.T @ (normalized_weights * (probability-targets))
    gradient[:-1] += L2_LAMBDA * theta[:-1]
    curvature = normalized_weights * probability * (1.0-probability)
    hessian = design.T @ (curvature[:, None] * design)
    hessian[:-1, :-1] += L2_LAMBDA * np.eye(design.shape[1]-1)
    return objective, gradient, hessian


def fit_score_adapter(train_features, targets, sample_weights, *, feature_metadata):
    """Fit only provided rows; return an artifact only after convergence.

    Soft target y is allowed. Weighted positive/negative masses must both be
    nonzero; zero-weight rows cannot rescue one-class training. Weight scaling
    changes neither preprocessing nor the normalized objective.
    """
    started = time.perf_counter()
    metadata = _validate_metadata(feature_metadata)
    x = _features(train_features)
    y = np.asarray(targets, dtype=np.float64)
    weights = np.asarray(sample_weights, dtype=np.float64)
    if y.shape != (len(x),) or weights.shape != (len(x),):
        raise TrainingDataError('target_or_weight_shape_mismatch')
    if not np.isfinite(y).all() or np.any(y < 0) or np.any(y > 1):
        raise TrainingDataError('targets_must_be_finite_in_unit_interval')
    if not np.isfinite(weights).all() or np.any(weights < 0) or not np.any(weights > 0):
        raise TrainingDataError('weights_must_be_finite_nonnegative_with_positive_mass')
    weight_scale = float(np.max(weights))
    normalized = weights / weight_scale
    scaled_weight_sum = float(np.sum(normalized))
    normalized /= scaled_weight_sum
    positive_mass = float(normalized @ y)
    negative_mass = float(normalized @ (1.0-y))
    if positive_mass <= 0 or negative_mass <= 0:
        raise TrainingDataError('one_class_training', {
            'normalized_positive_mass': positive_mass, 'normalized_negative_mass': negative_mass})
    mean = normalized @ x
    variance = normalized @ ((x-mean)**2)
    raw_std = np.sqrt(variance)
    std = np.maximum(raw_std, STD_FLOOR)
    standardized = (x-mean)/std
    if not all(np.isfinite(v).all() for v in (mean, raw_std, std, standardized)):
        raise TrainingDataError('nonfinite_weighted_standardization')
    design = np.column_stack((standardized, np.ones(len(x))))
    theta = np.zeros(len(FEATURE_NAMES)+1)
    theta[-1] = math.log(positive_mass)-math.log(negative_mass)
    history = []
    total_backtracks = 0
    for iteration in range(MAX_ITERATIONS+1):
        objective, gradient, hessian = _loss_gradient_hessian(theta, design, y, normalized)
        gradient_norm = float(np.max(np.abs(gradient)))
        history.append(objective)
        diagnostics = {'converged': False, 'iterations': iteration, 'objective': objective,
            'gradient_norm_inf': gradient_norm, 'max_iterations': MAX_ITERATIONS,
            'gradient_tolerance': GRADIENT_TOLERANCE, 'total_backtracks': total_backtracks,
            'objective_history': list(history), 'fit_wall_seconds': time.perf_counter()-started}
        if not math.isfinite(objective) or not np.isfinite(gradient).all() or not np.isfinite(hessian).all():
            raise ConvergenceError('nonfinite_optimizer_state', diagnostics)
        if gradient_norm <= GRADIENT_TOLERANCE:
            diagnostics.update(converged=True, reason='gradient_tolerance_met')
            break
        if iteration == MAX_ITERATIONS:
            raise ConvergenceError('maximum_iterations_exceeded', diagnostics)
        try:
            direction = np.linalg.solve(hessian, gradient)
        except np.linalg.LinAlgError as error:
            raise ConvergenceError('newton_system_failed', diagnostics) from error
        descent = float(gradient @ direction)
        if not math.isfinite(descent) or descent <= 0 or not np.isfinite(direction).all():
            raise ConvergenceError('invalid_newton_descent', diagnostics)
        alpha = 1.0
        for backtrack in range(MAX_BACKTRACKS+1):
            candidate = theta-alpha*direction
            candidate_objective, _, _ = _loss_gradient_hessian(candidate, design, y, normalized)
            if math.isfinite(candidate_objective) and candidate_objective <= objective-ARMIJO_C*alpha*descent:
                theta = candidate
                total_backtracks += backtrack
                break
            alpha *= 0.5
        else:
            raise ConvergenceError('armijo_line_search_failed', diagnostics)
    weight_total = weight_scale * scaled_weight_sum
    artifact = {
        'schema': SCHEMA, 'model': 'weighted_standardized_l2_logistic',
        'execution_authorized': False, 'output_name': 'uncalibrated_score',
        'feature_metadata': metadata,
        'standardizer': {'mean': mean.tolist(), 'std': std.tolist(), 'raw_std': raw_std.tolist(),
            'std_floor': STD_FLOOR, 'fit_scope': 'provided_training_rows_only',
            'weighting': 'same_normalized_sample_weights_as_objective'},
        'weights': theta[:-1].tolist(), 'bias': float(theta[-1]), 'lambda': L2_LAMBDA,
        'objective_definition': 'sum(normalized_weight * BCE(soft_target,sigmoid(Xw+b))) + lambda/2 * sum(w^2)',
        'bias_penalized': False,
        'optimization': {'method': 'deterministic_newton_armijo', 'armijo_c': ARMIJO_C,
            'backtrack_factor': 0.5, 'max_backtracks_per_iteration': MAX_BACKTRACKS},
        'convergence': diagnostics,
        'training_summary': {'rows': len(x), 'positive_weight_rows': int(np.count_nonzero(weights > 0)),
            'normalized_positive_mass': positive_mass, 'normalized_negative_mass': negative_mass,
            'input_weight_scale': weight_scale, 'scaled_weight_sum': scaled_weight_sum,
            'input_weight_sum': weight_total if math.isfinite(weight_total) else None},
        'scope': 'Offline exploratory score adapter. No posterior calibration claim, split construction, threshold tuning, smoothing, gap filling, segment filtering or execution authority.',
    }
    # Fail before returning if any metadata accidentally became non-JSON finite.
    json.dumps(artifact, allow_nan=False)
    return artifact


def validate_adapter(artifact):
    if not isinstance(artifact, dict) or artifact.get('schema') != SCHEMA:
        raise ValueError('unsupported_adapter_schema')
    if artifact.get('execution_authorized') is not False or artifact.get('output_name') != 'uncalibrated_score':
        raise ValueError('adapter_cannot_grant_execution_or_calibration')
    _validate_metadata(artifact.get('feature_metadata'))
    if artifact.get('lambda') != L2_LAMBDA or artifact.get('bias_penalized') is not False:
        raise ValueError('adapter_objective_not_fixed_definition')
    if artifact.get('convergence', {}).get('converged') is not True:
        raise ValueError('unconverged_adapter')
    standardizer = artifact.get('standardizer', {})
    if standardizer.get('std_floor') != STD_FLOOR or standardizer.get('fit_scope') != 'provided_training_rows_only':
        raise ValueError('invalid_standardizer_definition')
    vectors = [np.asarray(v, dtype=np.float64) for v in
               (standardizer.get('mean'), standardizer.get('std'), artifact.get('weights'))]
    if any(v.shape != (len(FEATURE_NAMES),) or not np.isfinite(v).all() for v in vectors):
        raise ValueError('invalid_adapter_vectors')
    if np.any(vectors[1] < STD_FLOOR) or not math.isfinite(float(artifact.get('bias', float('nan')))):
        raise ValueError('invalid_adapter_scale_or_bias')
    return artifact


def predict_uncalibrated_score(artifact, features):
    validate_adapter(artifact)
    x = _features(features, allow_empty=True)
    mean = np.asarray(artifact['standardizer']['mean'])
    std = np.asarray(artifact['standardizer']['std'])
    logits = ((x-mean)/std) @ np.asarray(artifact['weights']) + artifact['bias']
    if not np.isfinite(logits).all():
        raise ValueError('nonfinite_prediction_logits')
    return _sigmoid(logits)


def predict_from_scores(artifact, scores, labels):
    features, metadata = build_features(scores, labels, return_metadata=True)
    expected = artifact.get('feature_metadata', {})
    for name in ('actual_speech_family_members', 'actual_singing_family_members'):
        if metadata[name] != expected.get(name):
            raise ValueError('inference_family_members_differ_from_training')
    return predict_uncalibrated_score(artifact, features)


def serialize_adapter(artifact):
    validate_adapter(artifact)
    return json.dumps(artifact, ensure_ascii=False, indent=2, sort_keys=True, allow_nan=False)


def deserialize_adapter(payload):
    artifact = json.loads(payload)
    return validate_adapter(artifact)


def threshold_masks(uncalibrated_scores):
    values = np.asarray(uncalibrated_scores, dtype=np.float64)
    if values.ndim != 1 or not np.isfinite(values).all() or np.any(values < 0) or np.any(values > 1):
        raise ValueError('uncalibrated_scores_must_be_finite_unit_vector')
    return {str(threshold): values >= threshold for threshold in FIXED_THRESHOLDS}
