"""Train-only local boundary gates for a fixed phrase-continuity classifier.

The first stage is the unchanged duration-normalized sequence model. The
second stage learns separate onset and offset evidence from local left/right
observations. At a locally supported edge (balanced head score >= .5), only
that direction's CTMC switch penalty may be relaxed to log(local score).
Self-state potentials and the original emission evidence are unchanged. The
refinement starts from a neutral state prior so recording-wide class balance
does not decide an introduction before local evidence is considered. These
are unnormalized path potentials, not calibrated transition probabilities.

The dynamic program can create a short singing island absent from the seed,
but only when its evidence pays both switch costs. No minimum duration, gap
fill, onset-time heuristic, F0-only cut, or forced closing at EOF is applied.
The local heads use +/-80 ms context; complete output additionally depends on
whole-file PANNs and Viterbi finalization. This is a teaching candidate only.
"""
from __future__ import annotations

import copy
import hashlib
import math
from pathlib import Path

import numpy as np

from . import sequence_features as frontend
from . import sequence_model as sequence
from . import temporal_fusion as fusion


HEAD_SCHEMA = 'neeeva.local-boundary-head.v1'
CANDIDATE_SCHEMA = 'neeeva.local-boundary-candidate.v1'
POLICY = dict(version='balanced-local-boundary-gated-ctmc-v1',
              speech_cost=2.0, context_seconds=.080, boundary_gate=.5,
              selected_inputs='9_frozen_panns_16_trailing80ms_pitch_6_waveform',
              boundary_features='duration_mean_left_right_and_right_minus_left',
              target='exact_adjacent_human_event_in_interior_edge_voronoi_support',
              censor='recording_start_EOF_and_support_touching_unknown_or_tolerance_gap',
              positive_weight='exact_event_count_divided_by_train_exact_prefix_event_multiplicity',
              negative_weight='known_duration_divided_by_train_exact_prefix_known_multiplicity',
              class_balance='half_total_weight_per_binary_class_train_only',
              lambda_l2=.1,
              switch_rule='max(original_log_switch,log_local_score)_only_when_score_at_least_half',
              emission='unchanged_sequence_prior_corrected_duration_normalized_evidence',
              initial_prior=.5, terminal='no_forced_exit_or_minimum_length',
              execution_authorized=False, production_eligible=False)


def boundary_definition(feature_metadata):
    metadata = fusion.validate_metadata(feature_metadata)
    names = metadata['names']
    if (len(names) != 63 or any(not name.endswith('_trailing_80ms') for name in names[9:25])
            or names[-6:] != list(frontend.RAW_NAMES)):
        raise ValueError('boundary_requires_versioned_63_fine_features')
    indices = list(range(25)) + list(range(57, 63))
    selected = [names[i] for i in indices]
    return dict(schema='neeeva.local-boundary-features.v1',
                names=[part + ':' + name for part in ('left80ms', 'right80ms', 'difference80ms') for name in selected],
                input_feature_metadata_sha256=fusion.digest(metadata), selected_indices=indices,
                context_seconds=POLICY['context_seconds'],
                averaging='exact_duration_weighted_piecewise_constant_source_cells',
                edge_context='observed_source_only_no_synthetic_padding',
                extractor_source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                acoustic_scope='frozen_whole_recording_PANNs', full_pipeline_causal=False)


def boundary_features(features, edges, feature_metadata, *, sample_rate=16000):
    definition = boundary_definition(feature_metadata)
    x = np.asarray(features, dtype=np.float64)
    edges = np.asarray(edges)
    sequence._check_rate(sample_rate)
    if (x.ndim != 2 or x.shape[1] != 63 or not np.isfinite(x).all()
            or edges.ndim != 1 or len(edges) != len(x) + 1
            or not np.issubdtype(edges.dtype, np.integer) or edges[0] != 0
            or np.any(np.diff(edges) <= 0)):
        raise ValueError('invalid_boundary_source_features')
    if len(x) < 2:
        return np.empty((0, len(definition['names'])), dtype=np.float64), definition
    x = x[:, definition['selected_indices']]
    widths = np.diff(edges).astype(np.float64)
    prefix = np.vstack((np.zeros((1, len(x[0]))), np.cumsum(widths[:, None] * x, axis=0)))

    def integral(points):
        index = np.minimum(np.searchsorted(edges, points, side='right') - 1, len(x) - 1)
        return prefix[index] + (points - edges[index])[:, None] * x[index]

    at = edges[1:-1].astype(np.float64)
    radius = POLICY['context_seconds'] * sample_rate
    before, after = np.maximum(0., at - radius), np.minimum(float(edges[-1]), at + radius)
    left = (integral(at) - integral(before)) / (at - before)[:, None]
    right = (integral(after) - integral(at)) / (after - at)[:, None]
    return np.column_stack((left, right, right - left)), definition


def _covered(regions, start, end):
    cursor = start
    for a, b, _ in regions:
        if b <= cursor:
            continue
        if a > cursor:
            return False
        cursor = min(end, b)
        if cursor == end:
            return True
    return False


def _events(regions):
    result = dict(onset=[], offset=[])
    for previous, current in zip(regions[:-1], regions[1:]):
        if previous[1] == current[0] and previous[2] != current[2]:
            result['onset' if current[2] else 'offset'].append(current[0])
    return result


def _negative_seconds(start, end, rid, prepared, known):
    partners = [(other, limit) for other, limit in prepared[rid].get('duplicate_prefixes', {}).items()
                if other in known and known[other]]
    knots = {start, end}
    for other, limit in partners:
        if start < limit < end:
            knots.add(limit)
        for a, b, _ in known[other]:
            if start < a < end:
                knots.add(a)
            if start < b < end:
                knots.add(b)
    knots = sorted(knots)
    total = 0.
    for a, b in zip(knots[:-1], knots[1:]):
        multiplicity = 1 + sum(b <= limit and _covered(known[other], a, b) for other, limit in partners)
        total += (b - a) / prepared[rid]['recording']['sample_rate'] / multiplicity
    return total


def training_examples(prepared, ids, kind):
    """Return auditable local rows, targets and unbalanced class-specific mass.

    Positive mass counts events; negative mass counts seconds. They are only
    compared *within* their class before each class receives half total weight.
    A contradictory same-prefix annotation is retained, not silently relabeled.
    """
    if kind not in ('onset', 'offset'):
        raise ValueError('invalid_boundary_event_kind')
    ids = sorted(ids)
    known = {rid: sequence._binary_regions(prepared[rid]['recording']) for rid in ids}
    events = {rid: _events(regions) for rid, regions in known.items()}
    xs, targets, masses, rows = [], [], [], []
    metadata = None
    censored = 0
    for rid in ids:
        item = prepared[rid]
        x, definition = boundary_features(item['features'], item['edges'], item['feature_metadata'],
                                          sample_rate=item['recording']['sample_rate'])
        if metadata is not None and definition != metadata:
            raise ValueError('boundary_training_feature_definition_mismatch')
        metadata = definition
        edges = np.asarray(item['edges'], dtype=np.int64)
        for index, point in enumerate(edges[1:-1]):
            # Half-open nearest-edge cells, ties assigned to the later edge.
            start = int((int(edges[index]) + int(point)) // 2)
            end = int((int(point) + int(edges[index + 2])) // 2)
            if end <= start or not _covered(known[rid], start, end):
                censored += 1
                continue
            positive_events = [sample for sample in events[rid][kind] if start <= sample < end]
            if positive_events:
                weight = 0.
                for sample in positive_events:
                    multiplicity = 1 + sum(other in events and sample < limit and sample in events[other][kind]
                        for other, limit in item.get('duplicate_prefixes', {}).items())
                    weight += 1. / multiplicity
            else:
                weight = _negative_seconds(start, end, rid, prepared, known)
            xs.append(x[index]); targets.append(float(bool(positive_events))); masses.append(weight)
            rows.append(dict(recording_id=rid, edge_sample=int(point), support_start_sample=start,
                             support_end_sample=end, event_samples=positive_events, mass=weight))
    if not xs:
        raise ValueError('no_known_boundary_training_edges')
    x, y, w = np.asarray(xs), np.asarray(targets), np.asarray(masses)
    positive, negative = float(w[y == 1].sum()), float(w[y == 0].sum())
    if positive <= 0 or negative <= 0:
        raise ValueError('boundary_' + kind + '_missing_training_classes')
    balanced = np.where(y == 1, .5 * w / positive, .5 * w / negative)
    statistics = dict(kind=kind, positive_event_mass=positive, negative_known_seconds=negative,
                      status='ready',
                      positive_rows=int((y == 1).sum()), negative_rows=int((y == 0).sum()),
                      censored_rows=censored, support_rows_sha256=fusion.digest(rows),
                      training_ids=[rid for rid in ids if known[rid]],
                      positive_rows_detail=[r for r in rows if r['event_samples']],
                      class_weight_totals=[float(balanced[y == label].sum()) for label in (0, 1)])
    return x, y, balanced, metadata, statistics


def fit_candidate(prepared, ids=None, *, speech_cost=2.0):
    if speech_cost != POLICY['speech_cost']:
        raise ValueError('boundary_requires_fixed_speech_cost_two')
    ids = sorted(prepared if ids is None else ids)
    seed = sequence.fit_candidate(prepared, ids, speech_cost=speech_cost)
    heads, statistics = {}, {}
    for kind in ('onset', 'offset'):
        try:
            x, y, w, metadata, stats = training_examples(prepared, ids, kind)
        except ValueError as exc:
            if str(exc) not in ('no_known_boundary_training_edges', 'boundary_' + kind + '_missing_training_classes'):
                raise
            heads[kind] = None
            statistics[kind] = dict(kind=kind, status='unavailable', reason=str(exc))
        else:
            heads[kind] = fusion.fit_head(x, y, w, feature_metadata=metadata)
            statistics[kind] = stats
    head = dict(schema=HEAD_SCHEMA, sequence_head=seed['head'], onset_head=heads['onset'],
                offset_head=heads['offset'], policy=copy.deepcopy(POLICY),
                training_statistics=statistics, execution_authorized=False)
    validate_head(head)
    sources = copy.deepcopy(seed['source_bindings'])
    result = dict(schema=CANDIDATE_SCHEMA, head=head, policy=copy.deepcopy(POLICY),
                  sequence_candidate=seed,
                  source_bindings=sources, source_bindings_sha256=fusion.digest(sources),
                  deduplicated_known_seconds=copy.deepcopy(seed['deduplicated_known_seconds']),
                  boundary_training_available=all(heads[kind] is not None for kind in ('onset', 'offset')),
                  teaching_eligible=False, eligibility_reasons=['heldout_guard_not_evaluated'],
                  execution_authorized=False, production_eligible=False)
    result['candidate_id'] = fusion.digest(dict(head=head, policy=POLICY, source_bindings=sources))
    return result


def validate_head(head, feature_metadata=None):
    if (not isinstance(head, dict) or head.get('schema') != HEAD_SCHEMA
            or head.get('policy') != POLICY or head.get('execution_authorized') is not False):
        raise ValueError('unsupported_boundary_head_or_policy')
    sequence.validate_head(head.get('sequence_head'), feature_metadata)
    definition = boundary_definition(head['sequence_head']['emission_head']['feature_metadata'])
    for kind in ('onset', 'offset'):
        stats = head.get('training_statistics', {}).get(kind, {})
        if head.get(kind + '_head') is None:
            if stats != dict(kind=kind, status='unavailable', reason=stats.get('reason')) or stats.get('reason') not in (
                    'no_known_boundary_training_edges', 'boundary_' + kind + '_missing_training_classes'):
                raise ValueError('invalid_boundary_unavailable_head')
            continue
        fusion.validate_head(head.get(kind + '_head'), definition)
        if stats.get('status') != 'ready':
            raise ValueError('invalid_boundary_training_status')
        for key in ('positive_event_mass', 'negative_known_seconds'):
            value = stats.get(key)
            if not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
                raise ValueError('invalid_boundary_training_statistics')
        if not np.allclose(stats.get('class_weight_totals', []), [.5, .5], rtol=1e-10, atol=1e-12):
            raise ValueError('invalid_boundary_class_balance')
    fusion.digest(head)
    return head


def validate_candidate(candidate, recordings=None):
    if (not isinstance(candidate, dict) or candidate.get('schema') != CANDIDATE_SCHEMA
            or candidate.get('policy') != POLICY or candidate.get('execution_authorized') is not False
            or candidate.get('production_eligible') is not False):
        raise ValueError('unsupported_boundary_candidate')
    validate_head(candidate.get('head'))
    available = all(candidate['head'].get(kind + '_head') is not None for kind in ('onset', 'offset'))
    if candidate.get('boundary_training_available') is not available:
        raise ValueError('boundary_training_availability_mismatch')
    seed = sequence.validate_candidate(candidate.get('sequence_candidate'), recordings)
    if seed['head'] != candidate['head']['sequence_head'] or seed['source_bindings'] != candidate.get('source_bindings'):
        raise ValueError('boundary_sequence_seed_mismatch')
    sources = candidate.get('source_bindings', {})
    if not sources or candidate.get('source_bindings_sha256') != fusion.digest(sources):
        raise ValueError('boundary_source_bindings_hash_mismatch')
    if candidate.get('candidate_id') != fusion.digest(dict(head=candidate['head'], policy=POLICY, source_bindings=sources)):
        raise ValueError('boundary_candidate_hash_mismatch')
    if recordings is not None:
        for rid, source in sources.items():
            if rid not in recordings or any(source.get(key) != value for key, value in fusion.source_binding(recordings[rid]).items()):
                raise ValueError('boundary_candidate_source_changed:' + rid)
    return candidate


def refine_scores(sequence_head, scores, edges, onset_scores, offset_scores, *, sample_rate=16000):
    """Second-stage local-gated path; changing a gate never forces a switch."""
    sequence.validate_head(sequence_head)
    q, edges = sequence._check_grid(scores, edges, sample_rate)
    on, off = np.asarray(onset_scores, dtype=np.float64), np.asarray(offset_scores, dtype=np.float64)
    expected = (max(0, len(q) - 1),)
    if any(v.shape != expected or not np.isfinite(v).all() or np.any(v < 0) or np.any(v > 1) for v in (on, off)):
        raise ValueError('invalid_local_boundary_scores')
    predecessors = np.zeros((len(q), 2), dtype=np.int8)
    value, last_center = None, None
    for index, probability in enumerate(q):
        start, end = int(edges[index]), int(edges[index + 1])
        center = (start + end) / 2.
        unary = sequence._emission(sequence_head, probability, (end - start) / sample_rate)
        if value is None:
            value = np.log([1 - POLICY['initial_prior'], POLICY['initial_prior']]) + unary
        else:
            transition = np.log(sequence._transition(sequence_head['transitions']['rates_per_second'],
                                                     (center - last_center) / sample_rate))
            if on[index - 1] >= POLICY['boundary_gate']:
                transition[0, 1] = max(transition[0, 1], math.log(on[index - 1]))
            if off[index - 1] >= POLICY['boundary_gate']:
                transition[1, 0] = max(transition[1, 0], math.log(off[index - 1]))
            joint = value[:, None] + transition
            predecessors[index] = np.argmax(joint, axis=0)
            value = np.max(joint, axis=0) + unary
        value -= np.max(value)
        last_center = center
    mask = np.zeros(len(q), dtype=bool)
    if len(q):
        state = int(np.argmax(value))
        for index in range(len(q) - 1, -1, -1):
            mask[index] = bool(state)
            if index:
                state = int(predecessors[index, state])
    return dict(selected_mask=mask, ranges=sequence._ranges(mask, edges),
                onset_gates=on >= POLICY['boundary_gate'], offset_gates=off >= POLICY['boundary_gate'])


def predict(head, features, edges, *, sample_rate=16000, feature_metadata=None):
    validate_head(head, feature_metadata)
    metadata = head['sequence_head']['emission_head']['feature_metadata']
    seed = sequence.predict(head['sequence_head'], features, edges, sample_rate=sample_rate,
                            feature_metadata=metadata)
    local, definition = boundary_features(features, edges, metadata, sample_rate=sample_rate)
    on = fusion.predict_scores(head['onset_head'], local, definition) if head['onset_head'] is not None else np.zeros(len(local))
    off = fusion.predict_scores(head['offset_head'], local, definition) if head['offset_head'] is not None else np.zeros(len(local))
    available = head['onset_head'] is not None and head['offset_head'] is not None
    if available:
        refined = refine_scores(head['sequence_head'], seed['scores'], edges, on, off, sample_rate=sample_rate)
    else:
        # An unsupported boundary type is not silently synthesized from pitch.
        # Keep the complete original seed, including its original initial prior.
        refined = dict(selected_mask=seed['selected_mask'], ranges=seed['ranges'],
                       onset_gates=np.zeros(len(local), dtype=bool), offset_gates=np.zeros(len(local), dtype=bool))
    return dict(seed, **refined, seed_selected_mask=seed['selected_mask'], seed_ranges=seed['ranges'],
                onset_scores=on, offset_scores=off, boundary_samples=np.asarray(edges)[1:-1],
                local_boundary_available_samples=np.minimum(np.asarray(edges)[1:-1] +
                    int(round(POLICY['context_seconds'] * sample_rate)), int(np.asarray(edges)[-1])),
                forward_variant='unmodified_sequence_provisional_not_boundary_refinement',
                boundary_model_available=available,
                variant='local_boundary_refinement', revision='final', full_pipeline_causal=False,
                execution_authorized=False)
