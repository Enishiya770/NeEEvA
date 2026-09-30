"""Fixed-policy, candidate-only fusion of frozen acoustics and trailing pitch.

The pitch rows may be causal. Frozen PANNs rows are not: this module never
claims that the combined classifier is a streaming acoustic model. Human
confirmed user intervals supply exact soft-label duration; unknown/source
excluded samples supply no negative targets. No model can authorize playback.
"""
from __future__ import annotations

import copy
import hashlib
import json
import math
import wave
from pathlib import Path

import numpy as np

HEAD_SCHEMA = 'neeeva.temporal-fusion-head.v1'
CANDIDATE_SCHEMA = 'neeeva.temporal-fusion-candidate.v1'
STD_FLOOR = 1e-6
MAX_ITERATIONS = 200
GRADIENT_TOLERANCE = 1e-8
MAX_BACKTRACKS = 50
ARMIJO_C = 1e-4
POLICY = dict(version='duration-prefix-fixed-cost-trailing-pitch-v1',
              speech_cost=2.0, environment_cost=1.0, singing_cost=1.0,
              threshold=.5, l2_lambda=.1,
              weighting='known_duration_divided_by_exact_prefix_known_multiplicity',
              normalization='training_rows_only',
              feature_scope='frozen_whole_recording_panns_plus_trailing_pitch',
              execution_authorized=False)


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':'),
                                    allow_nan=False).encode()).hexdigest()


def validate_metadata(metadata):
    if not isinstance(metadata, dict):
        raise ValueError('feature_metadata_required')
    names = metadata.get('names')
    if (not isinstance(names, list) or not names or
            any(not isinstance(n, str) or not n for n in names) or len(names) != len(set(names))):
        raise ValueError('invalid_feature_names')
    # The complete extractor definition, not just the vector length, binds a head.
    digest(metadata)
    return copy.deepcopy(metadata)


def feature_metadata(acoustic_metadata, pitch_track=None):
    """Shared service/evaluation feature definition, bound to the extractor."""
    if pitch_track is None:
        return dict(names=list(acoustic_metadata['names']), panns=copy.deepcopy(acoustic_metadata),
                    scope='frozen_whole_recording_panns', pitch=None)
    from . import pitch_features as pitch
    pitch_metadata = dict(feature_names=list(pitch.FEATURE_NAMES), provider=pitch_track['provider'],
        window_samples=16000, sample_rate=16000,
        definition='only frames with availability <= decision endpoint within trailing 1s',
        extractor_source_sha256=hashlib.sha256(Path(pitch.__file__).read_bytes()).hexdigest(),
        feature_policy=copy.deepcopy(pitch.FEATURE_POLICY), provider_identity=pitch_track['provider_identity'],
        confidence_semantics=pitch_track['confidence_semantics'],
        voicing_threshold=pitch_track['voicing_threshold'], min_rms=pitch_track['min_rms'])
    return dict(names=list(acoustic_metadata['names']) + list(pitch.FEATURE_NAMES),
                panns=copy.deepcopy(acoustic_metadata),
                scope='frozen_whole_recording_panns_plus_causal_trailing_pitch', pitch=pitch_metadata)


def _sigmoid(logits):
    result = np.empty_like(logits)
    keep = logits >= 0
    result[keep] = 1. / (1. + np.exp(-logits[keep]))
    exp = np.exp(logits[~keep])
    result[~keep] = exp / (1. + exp)
    return result


def _loss_gradient_hessian(theta, design, targets, weights):
    logits = design @ theta
    probability = _sigmoid(logits)
    bce = np.where(logits >= 0, (1. - targets) * logits, -targets * logits)
    bce += np.logaddexp(0., -np.abs(logits))
    objective = float(weights @ bce + .05 * (theta[:-1] @ theta[:-1]))
    gradient = design.T @ (weights * (probability - targets))
    gradient[:-1] += .1 * theta[:-1]
    curvature = weights * probability * (1. - probability)
    hessian = design.T @ (curvature[:, None] * design)
    hessian[:-1, :-1] += .1 * np.eye(design.shape[1] - 1)
    return objective, gradient, hessian


def fit_head(features, targets, weights, *, feature_metadata):
    metadata = validate_metadata(feature_metadata)
    x, y, w = (np.asarray(v, dtype=np.float64) for v in (features, targets, weights))
    if x.ndim != 2 or x.shape[1] != len(metadata['names']) or not len(x):
        raise ValueError('invalid_training_feature_shape')
    if y.shape != (len(x),) or w.shape != (len(x),):
        raise ValueError('invalid_training_target_shape')
    if not all(np.isfinite(v).all() for v in (x, y, w)) or np.any(w < 0) or np.any(y < 0) or np.any(y > 1):
        raise ValueError('invalid_training_values')
    keep = w > 0
    if not keep.any():
        raise ValueError('no_known_training_rows')
    x, y, w = x[keep], y[keep], w[keep]
    w = w / w.max()
    w = w / w.sum()
    positive = float(w @ y)
    if not 0 < positive < 1:
        raise ValueError('one_class_training')
    mean = w @ x
    std = np.maximum(np.sqrt(w @ ((x - mean) ** 2)), STD_FLOOR)
    design = np.column_stack(((x - mean) / std, np.ones(len(x))))
    theta = np.zeros(design.shape[1])
    theta[-1] = math.log(positive / (1 - positive))
    for iteration in range(MAX_ITERATIONS + 1):
        objective, gradient, hessian = _loss_gradient_hessian(theta, design, y, w)
        norm = float(np.max(np.abs(gradient)))
        if not np.isfinite(objective) or not all(np.isfinite(v).all() for v in (gradient, hessian)):
            raise ValueError('nonfinite_optimizer_state')
        if norm <= GRADIENT_TOLERANCE:
            break
        if iteration == MAX_ITERATIONS:
            raise ValueError('fusion_optimizer_not_converged')
        direction = np.linalg.solve(hessian, gradient)
        descent = float(gradient @ direction)
        if not np.isfinite(descent) or descent <= 0:
            raise ValueError('invalid_optimizer_direction')
        alpha = 1.0
        for _ in range(MAX_BACKTRACKS + 1):
            candidate = theta - alpha * direction
            loss, _, _ = _loss_gradient_hessian(candidate, design, y, w)
            if np.isfinite(loss) and loss <= objective - ARMIJO_C * alpha * descent:
                theta = candidate
                break
            alpha *= .5
        else:
            raise ValueError('fusion_optimizer_line_search_failed')
    head = dict(schema=HEAD_SCHEMA, feature_metadata=metadata,
                feature_metadata_sha256=digest(metadata), weights=theta[:-1].tolist(), bias=float(theta[-1]),
                standardizer=dict(mean=mean.tolist(), std=std.tolist(),
                                  scope='provided_training_rows_only', floor=STD_FLOOR),
                lambda_l2=.1, threshold=.5, output='uncalibrated_singing_score',
                convergence=dict(converged=True, iterations=iteration, gradient_norm_inf=norm, objective=objective),
                training_rows=len(x), execution_authorized=False)
    return validate_head(head)


def validate_head(head, feature_metadata=None):
    if not isinstance(head, dict) or head.get('schema') != HEAD_SCHEMA:
        raise ValueError('unsupported_fusion_head_schema')
    metadata = validate_metadata(head.get('feature_metadata'))
    if head.get('feature_metadata_sha256') != digest(metadata):
        raise ValueError('fusion_feature_definition_hash_mismatch')
    if feature_metadata is not None and metadata != validate_metadata(feature_metadata):
        raise ValueError('fusion_feature_definition_mismatch')
    if (head.get('execution_authorized') is not False or head.get('output') != 'uncalibrated_singing_score'
            or head.get('threshold') != .5 or head.get('lambda_l2') != .1):
        raise ValueError('invalid_fusion_head_policy')
    if head.get('convergence', {}).get('converged') is not True:
        raise ValueError('unconverged_fusion_head')
    standard = head.get('standardizer', {})
    if standard.get('scope') != 'provided_training_rows_only' or standard.get('floor') != STD_FLOOR:
        raise ValueError('invalid_fusion_standardizer')
    vectors = [np.asarray(v, dtype=np.float64) for v in
               (standard.get('mean'), standard.get('std'), head.get('weights'))]
    if any(v.shape != (len(metadata['names']),) or not np.isfinite(v).all() for v in vectors):
        raise ValueError('invalid_fusion_vectors')
    if np.any(vectors[1] < STD_FLOOR) or not math.isfinite(float(head.get('bias', float('nan')))):
        raise ValueError('invalid_fusion_scale_or_bias')
    json.dumps(head, allow_nan=False)
    return head


def predict_scores(head, features, feature_metadata=None):
    validate_head(head, feature_metadata)
    x = np.asarray(features, dtype=np.float64)
    if x.ndim != 2 or x.shape[1] != len(head['weights']) or not np.isfinite(x).all():
        raise ValueError('invalid_prediction_features')
    mean, std = (np.asarray(head['standardizer'][k]) for k in ('mean', 'std'))
    logits = ((x - mean) / std) @ np.asarray(head['weights']) + head['bias']
    if not np.isfinite(logits).all():
        raise ValueError('nonfinite_fusion_predictions')
    return _sigmoid(logits)


def source_binding(recording):
    keys = ('recording_id', 'audio_sha256', 'sample_rate', 'sample_count', 'annotation_revision',
            'session_id', 'content_group', 'parent_recording_id', 'lineage_source_ids')
    result = {key: copy.deepcopy(recording.get(key)) for key in keys}
    result['annotation_sha256'] = digest(recording.get('annotation'))
    return result


def exact_prefix_relations(recordings, audio_paths):
    """Only verified identical PCM prefixes count; filenames/metadata never suffice."""
    pcm, formats = {}, {}
    for rid, rec in recordings.items():
        path = Path(audio_paths[rid])
        if hashlib.sha256(path.read_bytes()).hexdigest() != rec['audio_sha256']:
            raise ValueError('prefix_audio_source_mismatch:' + rid)
        with wave.open(str(path), 'rb') as stream:
            formats[rid] = (stream.getnchannels(), stream.getsampwidth(), stream.getframerate())
            if stream.getnframes() != rec['sample_count'] or stream.getframerate() != rec['sample_rate']:
                raise ValueError('prefix_audio_coordinate_mismatch:' + rid)
            pcm[rid] = stream.readframes(stream.getnframes())
    pairs = {rid: {} for rid in recordings}
    ids = sorted(recordings)
    for i, a in enumerate(ids):
        for b in ids[i + 1:]:
            if formats[a] != formats[b]:
                continue
            short, long = sorted((pcm[a], pcm[b]), key=len)
            if short and long.startswith(short):
                limit = min(recordings[a]['sample_count'], recordings[b]['sample_count'])
                pairs[a][b] = pairs[b][a] = limit
    return pairs


def prefix_components(ids, relations):
    parent = {rid: rid for rid in ids}
    def find(rid):
        while parent[rid] != rid:
            rid = parent[rid]
        return rid
    for rid in ids:
        for other in relations.get(rid, {}):
            if other in parent:
                a, b = find(rid), find(other)
                parent[max(a, b)] = min(a, b)
    groups = {}
    for rid in ids:
        groups.setdefault(find(rid), []).append(rid)
    return groups


def training_masses(prepared, ids):
    # Local import avoids pipeline's optional integration cycle.
    from .pipeline import known_regions
    known = {rid: known_regions(prepared[rid]['recording']) for rid in ids}
    answer = {}
    for rid in ids:
        item = prepared[rid]
        edges = np.asarray(item['edges'], dtype=np.int64)
        rec = item['recording']
        fine = {name: np.zeros(len(edges) - 1) for name in ('singing', 'speech', 'environment')}
        partners = [(other, limit) for other, limit in item.get('duplicate_prefixes', {}).items()
                    if other in known and known[other]]
        for region in known[rid]:
            start, end = region['start_sample'], region['end_sample']
            knots = {start, end}
            knots.update(int(v) for v in edges if start < v < end)
            for other, limit in partners:
                for r in known[other]:
                    a, b = max(start, r['start_sample']), min(end, limit, r['end_sample'])
                    if a < b:
                        knots.update((a, b))
            knots = sorted(knots)
            for a, b in zip(knots[:-1], knots[1:]):
                count = 1 + sum(a < limit and any(r['start_sample'] <= a and r['end_sample'] >= b
                            for r in known[other]) for other, limit in partners)
                cell = int(np.searchsorted(edges, a, side='right') - 1)
                fine[region['label']][cell] += (b - a) / rec['sample_rate'] / count
        answer[rid] = fine
    return answer


def fit_candidate(prepared, ids=None, *, speech_cost=2.0):
    """Prepared rows use recording, edges, features, feature_metadata and hashes.

    duplicate_prefixes is optional {other_recording_id: verified_prefix_samples}.
    The public service must derive it with exact_prefix_relations, not guess it.
    """
    if speech_cost not in (1.0, 2.0):
        raise ValueError('only_predeclared_speech_costs_supported')
    ids = sorted(prepared if ids is None else ids)
    if not ids:
        raise ValueError('no_known_training_rows')
    metadata = validate_metadata(prepared[ids[0]]['feature_metadata'])
    for rid in ids:
        item = prepared[rid]
        x, edges, rec = np.asarray(item['features']), np.asarray(item['edges']), item['recording']
        if x.ndim != 2 or x.shape[1] != len(metadata['names']) or not np.isfinite(x).all():
            raise ValueError('invalid_fusion_features')
        if (edges.ndim != 1 or len(edges) != len(x) + 1 or not np.issubdtype(edges.dtype, np.integer)
                or edges[0] != 0 or edges[-1] != rec['sample_count'] or np.any(np.diff(edges) <= 0)):
            raise ValueError('invalid_fusion_source_grid')
    masses = training_masses(prepared, ids)
    xs, ys, ws, sources = [], [], [], {}
    label_seconds = dict(singing=0., speech=0., environment=0.)
    for rid in ids:
        item = prepared[rid]
        if item['feature_metadata'] != metadata:
            raise ValueError('training_feature_definition_mismatch')
        x = np.asarray(item['features'], dtype=np.float64)
        edges = np.asarray(item['edges'])
        rec = item['recording']
        if (edges.ndim != 1 or len(edges) != len(x) + 1 or not np.issubdtype(edges.dtype, np.integer)
                or edges[0] != 0 or edges[-1] != rec['sample_count'] or np.any(np.diff(edges) <= 0)):
            raise ValueError('invalid_fusion_source_grid')
        mass = masses[rid]
        positive = mass['singing']
        negative = speech_cost * mass['speech'] + mass['environment']
        total = positive + negative
        keep = total > 0
        if not keep.any():
            continue
        xs.append(x[keep]); ys.append(positive[keep] / total[keep]); ws.append(total[keep])
        sources[rid] = dict(source_binding(rec), feature_sha256=item.get('feature_sha256'),
                            pitch_source_sha256=item.get('pitch_source_sha256'),
                            duplicate_prefixes={k: v for k, v in item.get('duplicate_prefixes', {}).items() if k in ids})
        for name in label_seconds:
            label_seconds[name] += float(mass[name].sum())
    if not xs:
        raise ValueError('no_known_training_rows')
    head = fit_head(np.concatenate(xs), np.concatenate(ys), np.concatenate(ws), feature_metadata=metadata)
    policy = dict(POLICY, speech_cost=speech_cost)
    candidate = dict(schema=CANDIDATE_SCHEMA, head=head, policy=policy,
                     source_bindings=sources, source_bindings_sha256=digest(sources),
                     deduplicated_known_seconds=label_seconds,
                     teaching_eligible=False, eligibility_reasons=['heldout_guard_not_evaluated'],
                     execution_authorized=False)
    candidate['candidate_id'] = digest(dict(head=head, policy=policy, source_bindings=sources))
    return candidate


def validate_candidate(candidate, recordings=None):
    if candidate.get('schema') != CANDIDATE_SCHEMA or candidate.get('execution_authorized') is not False:
        raise ValueError('invalid_fusion_candidate')
    validate_head(candidate.get('head'))
    policy = candidate.get('policy', {})
    if policy != dict(POLICY, speech_cost=policy.get('speech_cost')) or policy.get('speech_cost') not in (1., 2.):
        raise ValueError('unsupported_fusion_policy')
    sources = candidate.get('source_bindings', {})
    if candidate.get('source_bindings_sha256') != digest(sources):
        raise ValueError('fusion_source_bindings_hash_mismatch')
    if candidate.get('candidate_id') != digest(dict(head=candidate['head'], policy=policy, source_bindings=sources)):
        raise ValueError('fusion_candidate_hash_mismatch')
    if recordings is not None:
        for rid, source in sources.items():
            if rid not in recordings or any(source.get(k) != v for k, v in source_binding(recordings[rid]).items()):
                raise ValueError('fusion_candidate_source_changed:' + rid)
    return candidate


def evaluate_guard(candidate_metrics, baseline_metrics, *, scope, fold_count):
    reasons = []
    if scope != 'exact_prefix_component_holdout' or fold_count < 3:
        reasons.append('insufficient_heldout_component_diagnostic')
    for key, reason in (('speech_fp_seconds', 'speech_false_positive_regression'),
                        ('environment_fp_seconds', 'environment_false_positive_regression'),
                        ('fn_seconds', 'singing_recall_regression')):
        actual, reference = candidate_metrics.get(key), baseline_metrics.get(key)
        if not isinstance(actual, (float, int)) or not isinstance(reference, (float, int)) or not np.isfinite([actual, reference]).all():
            reasons.append('missing_finite_metric:' + key)
        elif actual > reference + 1e-8:
            reasons.append(reason)
    return dict(teaching_eligible=not reasons, eligibility_reasons=reasons,
                scope=scope, fold_count=fold_count, execution_authorized=False,
                production_eligible=False,
                limitation='same-session development diagnostic; not independent production validation')


def evaluate_teaching_guard(candidate_summary, references, *, fold_count, complete=True):
    """Every fixed reference must pass; a tradeoff cannot silently activate it.

    Each summary has ``metrics`` (physical annotated-sample durations) and
    ``boundaries`` (raw human singing intervals). The caller must supply both
    the fixed bootstrap proposal and the duration-controlled PANNs holdout.
    """
    comparisons = {}
    missing = [name for name in ('bootstrap_proposal', 'panns_uniform') if name not in references]
    for name, reference in references.items():
        guard = evaluate_guard(candidate_summary.get('metrics', {}), reference.get('metrics', {}),
                               scope='exact_prefix_component_holdout', fold_count=fold_count)
        candidate_boundaries, reference_boundaries = candidate_summary.get('boundaries', {}), reference.get('boundaries', {})
        for key in ('completely_missed_gold_count', 'internal_fragment_splits'):
            actual, baseline = candidate_boundaries.get(key), reference_boundaries.get(key)
            if not isinstance(actual, (float, int)) or not isinstance(baseline, (float, int)) or not np.isfinite([actual, baseline]).all():
                guard['eligibility_reasons'].append('missing_finite_boundary_metric:' + key)
            elif actual > baseline:
                guard['eligibility_reasons'].append(key + '_regression')
        if not complete:
            guard['eligibility_reasons'].append('missing_heldout_predictions')
        guard['teaching_eligible'] = not guard['eligibility_reasons']
        comparisons[name] = guard
    reasons = ['missing_reference:' + name for name in missing]
    reasons.extend(name + ':' + reason for name, guard in comparisons.items() for reason in guard['eligibility_reasons'])
    return dict(teaching_eligible=not reasons, eligibility_reasons=reasons, comparisons=comparisons,
                execution_authorized=False, production_eligible=False)
