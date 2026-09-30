"""Fixed three-category evidence with group-normalized ridge and local dynamics.

Softmax scores are cost-sensitive fitted category evidence, not externally
calibrated probabilities. Node evidence is log(score)*cell_seconds/.320, with
NO subtraction of the training class prior. Each standardized feature block
is divided by sqrt(its dimension); repeating correlated columns cannot buy a
cheaper coefficient norm simply because one branch supplies more columns.

Three local softmax rows learn destination category given source category and
left/right 80 ms evidence. Known same-category supports are explicit stay
examples, including pauses inside a human-confirmed singing region. Each
observed destination receives equal training mass within its source row.
Inference uses .5*CTMC + .5*local_row, no hard event/category/F0 veto. Unknown
and tolerance supports, clip endpoints, and supports containing more than one
true category transition never supply transition targets. Missing destinations
use observed-class diagnostic predictions with CTMC backoff; adoption remains
unavailable until all node categories and at least one local row have evidence.
"""
from __future__ import annotations

import copy
import hashlib
import math
from pathlib import Path

import numpy as np

from . import boundary_model as local
from . import temporal_fusion as fusion
from . import sequence_model as sequence


CLASSES = ('speech', 'singing', 'environment')
CLASS_NAMES = CLASSES
HEAD_SCHEMA = 'neeeva.category-evidence-head.v1'
CANDIDATE_SCHEMA = 'neeeva.category-evidence-candidate.v1'
SOFTMAX_SCHEMA = 'neeeva.block-normalized-softmax.v1'
NODE_BLOCKS = [list(range(9)), list(range(9, 57)), list(range(57, 63))]
ROW_BLOCKS = [[i for part in (0, 31, 62) for i in range(part, part + 9)],
              [i for part in (0, 31, 62) for i in range(part + 9, part + 25)],
              [i for part in (0, 31, 62) for i in range(part + 25, part + 31)]]
POLICY = dict(version='three-category-block-ridge-local-mixture-v1', classes=list(CLASSES),
              speech_cost=2., lambda_l2=.1, node_block_dimensions=[9, 48, 6], row_block_dimensions=[27, 48, 18],
              normalization='train_duration_zscore_then_divide_each_block_by_sqrt_dimension',
              node_evidence='log_cost_sensitive_softmax_without_training_prior_subtraction',
              evidence_reference_seconds=.320, probability_clip=1e-6, initial_prior='uniform_three_states',
              local_context_seconds=.080, local_destination_weight='equal_total_per_observed_destination_in_each_source_row',
              event_weight='verified_exact_prefix_identical_category_event_multiplicity',
              stay_weight='known_duration_divided_by_verified_prefix_known_multiplicity',
              edge_target='wholly_known_interior_voronoi_support_with_at_most_one_category_transition',
              transition_mix=.5, exit_pseudocount_per_destination=.25, exposure_pseudoseconds=1.,
              terminal='no_forced_exit_minimum_duration_or_gap_fill',
              missing_categories='node_categories_required; local_missing_destinations_zero_with_CTMC_backoff',
              training_availability='all_three_node_classes_and_at_least_one_fitted_local_row',
              production_eligible=False, execution_authorized=False)


def _source_code_sha():
    return hashlib.sha256(Path(__file__).read_bytes()).hexdigest()


def _block_scales(blocks, dimension):
    if sorted(i for block in blocks for i in block) != list(range(dimension)) or any(not block for block in blocks):
        raise ValueError('invalid_semantic_feature_blocks')
    scale = np.ones(dimension)
    for block in blocks:
        scale[block] = math.sqrt(len(block))
    return scale


def _softmax(logits):
    shifted = logits - np.max(logits, axis=1, keepdims=True)
    exp = np.exp(shifted)
    return exp / exp.sum(axis=1, keepdims=True)


def _objective(theta, design, targets, weights, derivatives=True):
    # Last class is minus the sum of the others: this removes the softmax
    # gauge while preserving symmetric ridge over ALL class coefficient rows.
    z = design @ theta
    logits = np.column_stack((z, -z.sum(axis=1)))
    p = _softmax(logits)
    full_weights = np.column_stack((theta[:-1], -theta[:-1].sum(axis=1)))
    loss = float(np.sum(weights * (np.logaddexp.reduce(logits, axis=1) - np.sum(targets * logits, axis=1)))
                 + .05 * np.sum(full_weights * full_weights))
    if not derivatives:
        return loss
    residual = weights[:, None] * (p - targets)
    gradient = design.T @ (residual[:, :-1] - residual[:, -1:])
    gradient[:-1] += .1 * (theta[:-1] + theta[:-1].sum(axis=1, keepdims=True))
    d, k = theta.shape
    hessian = np.empty((d * k, d * k))
    for a in range(k):
        for b in range(k):
            curvature = p[:, -1] - (p[:, a] - p[:, -1]) * (p[:, b] - p[:, -1])
            if a == b:
                curvature += p[:, a]
            block = design.T @ ((weights * curvature)[:, None] * design)
            block[:-1, :-1] += .1 * (1 + int(a == b)) * np.eye(d - 1)
            hessian[a * d:(a + 1) * d, b * d:(b + 1) * d] = block
    return loss, gradient, hessian


def fit_softmax(features, targets, weights, *, feature_metadata, blocks):
    metadata = fusion.validate_metadata(feature_metadata)
    x, y, w = (np.asarray(v, dtype=np.float64) for v in (features, targets, weights))
    d = len(metadata['names'])
    if (x.ndim != 2 or x.shape[1] != d or y.shape != (len(x), 3) or w.shape != (len(x),)
            or not all(np.isfinite(v).all() for v in (x, y, w)) or np.any(y < 0) or np.any(w < 0)
            or not np.allclose(y.sum(axis=1), 1., rtol=1e-10, atol=1e-12)):
        raise ValueError('invalid_semantic_training_rows')
    keep = w > 0
    if not keep.any():
        raise ValueError('no_known_semantic_training_rows')
    x, y, w = x[keep], y[keep], w[keep]
    w = w / w.sum()
    mass = w @ y
    active = np.flatnonzero(mass > 0).tolist()
    mean = w @ x
    std = np.maximum(np.sqrt(w @ ((x - mean) ** 2)), fusion.STD_FLOOR)
    scale = _block_scales(blocks, d)
    design = np.column_stack(((x - mean) / std / scale, np.ones(len(x))))
    if len(active) == 1:
        coefficients = np.zeros((d + 1, 1))
        convergence = dict(converged=True, iterations=0, gradient_norm_inf=0., objective=0.)
    else:
        theta = np.zeros((d + 1, len(active) - 1))
        biases = np.log(mass[active]); biases -= biases.mean()
        theta[-1] = biases[:-1]
        target = y[:, active]
        for iteration in range(fusion.MAX_ITERATIONS + 1):
            loss, gradient, hessian = _objective(theta, design, target, w)
            norm = float(np.max(np.abs(gradient)))
            if not np.isfinite(loss) or not np.isfinite(gradient).all() or not np.isfinite(hessian).all():
                raise ValueError('nonfinite_semantic_optimizer_state')
            if norm <= fusion.GRADIENT_TOLERANCE:
                break
            if iteration == fusion.MAX_ITERATIONS:
                raise ValueError('semantic_optimizer_not_converged')
            direction = np.linalg.solve(hessian, gradient.flatten(order='F')).reshape(theta.shape, order='F')
            descent = float(np.sum(gradient * direction))
            if not math.isfinite(descent) or descent <= 0:
                raise ValueError('invalid_semantic_optimizer_direction')
            alpha = 1.
            for _ in range(fusion.MAX_BACKTRACKS + 1):
                updated = theta - alpha * direction
                new_loss = _objective(updated, design, target, w, derivatives=False)
                if math.isfinite(new_loss) and new_loss <= loss - fusion.ARMIJO_C * alpha * descent:
                    theta = updated
                    break
                alpha *= .5
            else:
                raise ValueError('semantic_optimizer_line_search_failed')
        coefficients = np.column_stack((theta, -theta.sum(axis=1)))
        convergence = dict(converged=True, iterations=iteration, gradient_norm_inf=norm, objective=loss)
    result = dict(schema=SOFTMAX_SCHEMA, feature_metadata=metadata, feature_metadata_sha256=fusion.digest(metadata),
                  blocks=copy.deepcopy(blocks), active_classes=active, classes=list(CLASSES),
                  weights=coefficients[:-1].tolist(), bias=coefficients[-1].tolist(),
                  standardizer=dict(mean=mean.tolist(), std=std.tolist(), block_scales=scale.tolist(),
                                    scope='provided_training_rows_only', floor=fusion.STD_FLOOR),
                  class_training_mass=mass.tolist(), convergence=convergence, lambda_l2=.1,
                  execution_authorized=False)
    return validate_softmax(result)


def validate_softmax(head, feature_metadata=None, blocks=None):
    if not isinstance(head, dict) or head.get('schema') != SOFTMAX_SCHEMA or head.get('execution_authorized') is not False:
        raise ValueError('invalid_semantic_softmax')
    metadata = fusion.validate_metadata(head.get('feature_metadata'))
    if head.get('feature_metadata_sha256') != fusion.digest(metadata) or (feature_metadata is not None and metadata != feature_metadata):
        raise ValueError('semantic_feature_definition_mismatch')
    active = head.get('active_classes')
    if (not isinstance(active, list) or not active or active != sorted(set(active))
            or any(type(i) is not int or i not in range(3) for i in active)
            or head.get('classes') != list(CLASSES) or head.get('lambda_l2') != .1
            or head.get('convergence', {}).get('converged') is not True):
        raise ValueError('invalid_semantic_softmax_policy')
    d, k = len(metadata['names']), len(active)
    standard = head.get('standardizer', {})
    if standard.get('scope') != 'provided_training_rows_only' or standard.get('floor') != fusion.STD_FLOOR:
        raise ValueError('invalid_semantic_standardizer')
    scale = _block_scales(head.get('blocks', []), d)
    if blocks is not None and head['blocks'] != blocks:
        raise ValueError('semantic_block_definition_mismatch')
    arrays = [(standard.get('mean'), (d,)), (standard.get('std'), (d,)),
              (standard.get('block_scales'), (d,)), (head.get('weights'), (d, k)), (head.get('bias'), (k,))]
    if any(np.asarray(value).shape != shape or not np.isfinite(np.asarray(value, dtype=float)).all() for value, shape in arrays):
        raise ValueError('invalid_semantic_vectors')
    if np.any(np.asarray(standard['std']) < fusion.STD_FLOOR) or not np.array_equal(standard['block_scales'], scale):
        raise ValueError('invalid_semantic_block_scale')
    mass = np.asarray(head.get('class_training_mass'), dtype=float)
    if mass.shape != (3,) or not np.isfinite(mass).all() or np.any(mass < 0) or not math.isclose(float(mass.sum()), 1., abs_tol=1e-10):
        raise ValueError('invalid_semantic_training_mass')
    if np.flatnonzero(mass > 0).tolist() != active:
        raise ValueError('semantic_active_class_mismatch')
    fusion.digest(head)
    return head


def predict_softmax(head, features, feature_metadata=None):
    validate_softmax(head, feature_metadata)
    x = np.asarray(features, dtype=np.float64)
    if x.ndim != 2 or x.shape[1] != len(head['weights']) or not np.isfinite(x).all():
        raise ValueError('invalid_semantic_prediction_features')
    z = (x - np.asarray(head['standardizer']['mean'])) / np.asarray(head['standardizer']['std'])
    z /= np.asarray(head['standardizer']['block_scales'])
    logits = z @ np.asarray(head['weights']) + np.asarray(head['bias'])
    output = np.zeros((len(x), 3))
    if len(x):
        output[:, head['active_classes']] = _softmax(logits)
    return output


def _regions(recording):
    from .pipeline import known_regions
    regions = sorted(known_regions(recording), key=lambda r: r['start_sample'])
    result = []
    for region in regions:
        a, b, c = region['start_sample'], region['end_sample'], CLASSES.index(region['label'])
        if result and a < result[-1][1]:
            raise ValueError('overlapping_semantic_known_regions')
        if result and a == result[-1][1] and c == result[-1][2]:
            result[-1] = (result[-1][0], b, c)
        else:
            result.append((a, b, c))
    return result


def _events(regions):
    return [(right[0], left[2], right[2]) for left, right in zip(regions[:-1], regions[1:])
            if left[1] == right[0] and left[2] != right[2]]


def _event_mass(rid, event, prepared, events):
    sample = event[0]
    return 1. / (1 + sum(other in events and sample < limit and event in events[other]
                        for other, limit in prepared[rid].get('duplicate_prefixes', {}).items()))


def _row_examples(prepared, ids):
    known = {rid: _regions(prepared[rid]['recording']) for rid in ids}
    events = {rid: _events(regions) for rid, regions in known.items()}
    examples = [dict(x=[], y=[], mass=[], provenance=[]) for _ in CLASSES]
    excluded = dict(unknown_support=0, multiple_transitions=0)
    metadata = None
    for rid in ids:
        item = prepared[rid]
        x, definition = local.boundary_features(item['features'], item['edges'], item['feature_metadata'],
                                                sample_rate=item['recording']['sample_rate'])
        if metadata is not None and metadata != definition:
            raise ValueError('semantic_row_feature_definition_mismatch')
        metadata = definition
        edges = np.asarray(item['edges'])
        for index, at in enumerate(edges[1:-1]):
            a, b = (int(edges[index]) + int(at)) // 2, (int(at) + int(edges[index + 2])) // 2
            if b <= a or not local._covered(known[rid], a, b):
                excluded['unknown_support'] += 1
                continue
            inside = [event for event in events[rid] if a <= event[0] < b]
            if len(inside) > 1:
                excluded['multiple_transitions'] += 1
                continue
            if inside:
                _, source, destination = inside[0]
                mass = _event_mass(rid, inside[0], prepared, events)
            else:
                source = destination = next(c for start, end, c in known[rid] if start <= a < end)
                mass = local._negative_seconds(a, b, rid, prepared, known)
            entry = examples[source]
            entry['x'].append(x[index]); entry['y'].append(destination); entry['mass'].append(mass)
            entry['provenance'].append(dict(recording_id=rid, sample=int(at), source=source, destination=destination,
                                           support=[a, b], event=inside, mass=mass))
    return examples, metadata, known, events, excluded


def _transitions(prepared, ids, masses, known, events):
    exposure = np.array([sum(float(masses[rid][name].sum()) for rid in ids) for name in CLASSES])
    counts = np.zeros((3, 3))
    for rid in ids:
        for event in events[rid]:
            counts[event[1], event[2]] += _event_mass(rid, event, prepared, events)
    rates = (counts + POLICY['exit_pseudocount_per_destination']) / (exposure[:, None] + POLICY['exposure_pseudoseconds'])
    np.fill_diagonal(rates, 0.)
    return dict(exposure_seconds=exposure.tolist(), event_counts=counts.tolist(), rates_per_second=rates.tolist(),
                training_ids=[rid for rid in ids if known[rid]])


def fit_candidate(prepared, ids=None, *, speech_cost=2.):
    if speech_cost != POLICY['speech_cost']:
        raise ValueError('semantic_requires_fixed_speech_cost_two')
    ids = sorted(prepared if ids is None else ids)
    if not ids:
        raise ValueError('no_known_semantic_training_rows')
    metadata = fusion.validate_metadata(prepared[ids[0]]['feature_metadata'])
    # Reuse the source/grid validator and exact duration/dedup implementation;
    # this deliberately does NOT fit an old binary baseline.
    local.boundary_definition(metadata)
    for rid in ids:
        item = prepared[rid]
        x, edges = np.asarray(item['features']), np.asarray(item['edges'])
        if (item['feature_metadata'] != metadata or x.shape != (len(edges) - 1, 63)
                or not np.isfinite(x).all() or edges.dtype.kind not in 'iu'
                or edges[0] != 0 or edges[-1] != item['recording']['sample_count'] or np.any(np.diff(edges) <= 0)):
            raise ValueError('invalid_semantic_training_source_grid')
    masses = fusion.training_masses(prepared, ids)
    xs, ys, ws, sources = [], [], [], {}
    for rid in ids:
        mass = np.column_stack([masses[rid][name] for name in CLASSES])
        mass[:, 0] *= speech_cost
        total = mass.sum(axis=1)
        keep = total > 0
        if not keep.any():
            continue
        item = prepared[rid]
        xs.append(np.asarray(item['features'])[keep]); ys.append(mass[keep] / total[keep, None]); ws.append(total[keep])
        sources[rid] = dict(fusion.source_binding(item['recording']), feature_sha256=item.get('feature_sha256'),
            pitch_source_sha256=item.get('pitch_source_sha256'),
            duplicate_prefixes={other: limit for other, limit in item.get('duplicate_prefixes', {}).items() if other in ids})
    if not xs:
        raise ValueError('no_known_semantic_training_rows')
    node = fit_softmax(np.concatenate(xs), np.concatenate(ys), np.concatenate(ws), feature_metadata=metadata, blocks=NODE_BLOCKS)
    examples, row_metadata, known, events, excluded = _row_examples(prepared, ids)
    rows, statistics = [], []
    for source, example in enumerate(examples):
        if not example['x']:
            rows.append(None); statistics.append(dict(source=source, status='unavailable', active_destinations=[]))
            continue
        y, mass = np.asarray(example['y']), np.asarray(example['mass'])
        totals = np.array([float(mass[y == cls].sum()) for cls in range(3)])
        active = np.flatnonzero(totals > 0)
        balanced = np.array([weight / totals[destination] / len(active) for weight, destination in zip(mass, y)])
        fitted = fit_softmax(example['x'], np.eye(3)[y], balanced, feature_metadata=row_metadata, blocks=ROW_BLOCKS)
        rows.append(fitted)
        statistics.append(dict(source=source, status='ready', active_destinations=active.tolist(),
            raw_destination_mass=totals.tolist(), rows=len(y), support_sha256=fusion.digest(example['provenance']),
            class_mass_units='off_diagonal_exact_events; diagonal_known_seconds; balanced_within_source'))
    available = node['active_classes'] == [0, 1, 2] and any(row is not None for row in rows)
    head = dict(schema=HEAD_SCHEMA, feature_metadata=metadata, node_head=node, row_heads=rows,
                row_feature_metadata=row_metadata, transitions=_transitions(prepared, ids, masses, known, events),
                row_statistics=statistics, censored_supports=excluded, policy=copy.deepcopy(POLICY),
                source_code_sha256=_source_code_sha(), execution_authorized=False)
    validate_head(head)
    candidate = dict(schema=CANDIDATE_SCHEMA, head=head, policy=copy.deepcopy(POLICY),
        source_bindings=sources, source_bindings_sha256=fusion.digest(sources),
        deduplicated_known_seconds={name: sum(float(masses[rid][name].sum()) for rid in ids) for name in CLASSES},
        semantic_training_available=available, teaching_eligible=False,
        eligibility_reasons=['heldout_guard_not_evaluated'] + ([] if available else ['semantic_training_categories_missing']),
        execution_authorized=False, production_eligible=False)
    candidate['candidate_id'] = fusion.digest(dict(head=head, policy=POLICY, source_bindings=sources))
    return candidate


def validate_head(head, feature_metadata=None):
    if (not isinstance(head, dict) or head.get('schema') != HEAD_SCHEMA or head.get('policy') != POLICY
            or head.get('source_code_sha256') != _source_code_sha() or head.get('execution_authorized') is not False):
        raise ValueError('unsupported_semantic_head_policy_or_source')
    metadata = fusion.validate_metadata(head.get('feature_metadata'))
    if feature_metadata is not None and metadata != feature_metadata:
        raise ValueError('semantic_input_feature_definition_mismatch')
    validate_softmax(head.get('node_head'), metadata, NODE_BLOCKS)
    row_metadata = local.boundary_definition(metadata)
    if head.get('row_feature_metadata') != row_metadata or len(head.get('row_heads', [])) != 3:
        raise ValueError('semantic_local_feature_definition_mismatch')
    for row in head['row_heads']:
        if row is not None:
            validate_softmax(row, row_metadata, ROW_BLOCKS)
    t = head.get('transitions', {})
    exposure, counts, rates = [np.asarray(t.get(k), dtype=float) for k in ('exposure_seconds', 'event_counts', 'rates_per_second')]
    if (exposure.shape != (3,) or counts.shape != (3, 3) or rates.shape != (3, 3)
            or any(not np.isfinite(v).all() or np.any(v < 0) for v in (exposure, counts, rates))
            or np.any(np.diag(counts) != 0) or np.any(np.diag(rates) != 0)):
        raise ValueError('invalid_semantic_transition_statistics')
    expected = (counts + POLICY['exit_pseudocount_per_destination']) / (exposure[:, None] + POLICY['exposure_pseudoseconds'])
    np.fill_diagonal(expected, 0.)
    if not np.allclose(expected, rates, rtol=1e-12, atol=0):
        raise ValueError('semantic_transition_rate_mismatch')
    fusion.digest(head)
    return head


def validate_candidate(candidate, recordings=None):
    if (not isinstance(candidate, dict) or candidate.get('schema') != CANDIDATE_SCHEMA
            or candidate.get('policy') != POLICY or candidate.get('execution_authorized') is not False
            or candidate.get('production_eligible') is not False):
        raise ValueError('unsupported_semantic_candidate')
    validate_head(candidate.get('head'))
    h = candidate['head']
    available = h['node_head']['active_classes'] == [0, 1, 2] and any(row is not None for row in h['row_heads'])
    if candidate.get('semantic_training_available') is not available:
        raise ValueError('semantic_training_availability_mismatch')
    sources = candidate.get('source_bindings', {})
    if not sources or candidate.get('source_bindings_sha256') != fusion.digest(sources):
        raise ValueError('semantic_source_bindings_hash_mismatch')
    if candidate.get('candidate_id') != fusion.digest(dict(head=h, policy=POLICY, source_bindings=sources)):
        raise ValueError('semantic_candidate_hash_mismatch')
    if recordings is not None:
        for rid, source in sources.items():
            if rid not in recordings or any(source.get(k) != v for k, v in fusion.source_binding(recordings[rid]).items()):
                raise ValueError('semantic_candidate_source_changed:' + rid)
    return candidate


def transition_matrix(rates, seconds):
    """Stable CTMC uniformization; long intervals split before Poisson summing."""
    rates = np.asarray(rates, dtype=float)
    if rates.shape != (3, 3) or not np.isfinite(rates).all() or np.any(rates < 0) or not math.isfinite(seconds) or seconds <= 0:
        raise ValueError('invalid_semantic_ctmc_input')
    exit_rate = rates.sum(axis=1)
    maximum = float(exit_rate.max())
    if maximum == 0:
        return np.eye(3)
    q = rates - np.diag(exit_rate)
    pieces = max(1, int(math.ceil(maximum * seconds / 20)))
    mu = maximum * seconds / pieces
    jump = np.eye(3) + q / maximum
    term = math.exp(-mu) * np.eye(3)
    result = term.copy()
    for index in range(1, 1000):
        term = term @ jump * (mu / index)
        result += term
        if index > mu and float(np.max(np.abs(term))) < 1e-15:
            break
    else:
        raise ValueError('semantic_ctmc_uniformization_not_converged')
    result = np.linalg.matrix_power(result, pieces)
    result = np.maximum(result, 0.)
    return result / result.sum(axis=1, keepdims=True)


def decode_categories(head, categories, edges, local_rows, *, sample_rate=16000):
    validate_head(head)
    p, edges = np.asarray(categories, dtype=float), np.asarray(edges)
    sequence._check_rate(sample_rate)
    if (p.ndim != 2 or p.shape[1] != 3 or not np.isfinite(p).all() or np.any(p < 0)
            or not np.allclose(p.sum(axis=1), 1.) or len(edges) != len(p) + 1 or edges.dtype.kind not in 'iu'
            or edges[0] != 0 or np.any(np.diff(edges) <= 0)):
        raise ValueError('invalid_semantic_decoding_grid')
    if len(local_rows) != 3:
        raise ValueError('invalid_semantic_local_rows')
    for row in local_rows:
        if row is not None:
            row = np.asarray(row)
            if row.shape != (max(0, len(p) - 1), 3) or not np.isfinite(row).all() or np.any(row < 0) or not np.allclose(row.sum(axis=1), 1.):
                raise ValueError('invalid_semantic_local_row_values')
    back = np.zeros((len(p), 3), dtype=np.int8)
    forward = np.zeros_like(p)
    transitions = np.empty((max(0, len(p) - 1), 3, 3))
    path, filtered, last_center = None, None, None
    cache = {}
    for index, probability in enumerate(p):
        a, b = int(edges[index]), int(edges[index + 1])
        center = (a + b) / 2.
        unary = np.log(np.clip(probability, POLICY['probability_clip'], 1.)) * ((b - a) / sample_rate / POLICY['evidence_reference_seconds'])
        if path is None:
            path = np.full(3, -math.log(3)) + unary
            filtered = path.copy()
        else:
            elapsed = (center - last_center) / sample_rate
            if elapsed not in cache:
                cache[elapsed] = transition_matrix(head['transitions']['rates_per_second'], elapsed)
            base = cache[elapsed]
            local_matrix = np.vstack([base[source] if row is None else row[index - 1] for source, row in enumerate(local_rows)])
            matrix = (1 - POLICY['transition_mix']) * base + POLICY['transition_mix'] * local_matrix
            transitions[index - 1] = matrix
            logt = np.log(matrix)
            joint = path[:, None] + logt
            back[index] = np.argmax(joint, axis=0)
            path = np.max(joint, axis=0) + unary
            filtered = np.logaddexp.reduce(filtered[:, None] + logt, axis=0) + unary
        path -= path.max()
        filtered -= np.logaddexp.reduce(filtered)
        forward[index] = np.exp(filtered)
        last_center = center
    states = np.zeros(len(p), dtype=np.int8)
    if len(p):
        state = int(np.argmax(path))
        for index in range(len(p) - 1, -1, -1):
            states[index] = state
            if index:
                state = int(back[index, state])
    selected = states == 1
    return dict(states=states, selected_states=states, selected_mask=selected, ranges=sequence._ranges(selected, edges),
                local_transition_matrices=transitions, forward_category_scores=forward)


def predict(head, features, edges, *, sample_rate=16000, feature_metadata=None):
    validate_head(head, feature_metadata)
    categories = predict_softmax(head['node_head'], features, head['feature_metadata'])
    x, metadata = local.boundary_features(features, edges, head['feature_metadata'], sample_rate=sample_rate)
    rows = [None if row is None else predict_softmax(row, x, metadata) for row in head['row_heads']]
    result = decode_categories(head, categories, edges, rows, sample_rate=sample_rate)
    edges = np.asarray(edges)
    available = head['node_head']['active_classes'] == [0, 1, 2] and any(row is not None for row in head['row_heads'])
    return dict(result, scores=categories[:, 1], category_scores=categories, category_names=list(CLASSES), class_names=list(CLASSES),
                edges=edges, availability_samples=np.full(len(categories), int(edges[-1]), dtype=np.int64),
                semantic_training_available=available, revision='final', full_pipeline_causal=False,
                forward_scope='filtered local-lookahead evidence; not a causal full-pipeline output',
                execution_authorized=False)
