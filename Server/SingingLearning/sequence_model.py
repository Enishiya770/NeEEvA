"""Candidate-only duration-normalized binary sequence decoder.

There are no minimum note lengths, unconditional gap repairs, or tail trimming.
Human-confirmed *adjacent* regions train a continuous-time Markov chain (CTMC).
Unknown intervals censor observations; they are never negatives or transitions.

For training-cost-weighted logistic output q and its training prior pi_w, the
two emission potentials are dt / .320 * [log((1-q)/(1-pi_w)), log(q/pi_w)].
This is a discriminative pseudo-likelihood ratio, not calibrated probability:
the regularized head and the class-dependent speech cost prevent that claim.
The fixed 320 ms evidence time prevents repeating the same coarse PANNs row at
20 ms from creating sixteen independent observations. It also applies to the
new fine features; that conservative fixed policy is evaluated, not tuned.

Each state's exit rate is (deduplicated exits + .5)/(known seconds + 1.0).
Between cell centers we use the exact two-state exp(Q * elapsed_seconds).
The initial state distribution uses the natural (not cost-weighted) training
duration prior. Forward filtering only uses preceding *supplied* feature rows;
PANNs still sees the complete recording, so the combined pipeline is not causal.
Viterbi finalization may revise prior cells and keeps the exact last sample.
"""
from __future__ import annotations

import copy
import math

import numpy as np

from . import temporal_fusion as fusion

HEAD_SCHEMA = 'neeeva.duration-sequence-head.v1'
CANDIDATE_SCHEMA = 'neeeva.duration-sequence-candidate.v1'
TRANSITION_SCHEMA = 'neeeva.duration-sequence-transitions.v1'
POLICY = dict(version='known-adjacent-ctmc-duration-evidence-v1',
              speech_cost=2.0, threshold=.5, evidence_reference_seconds=.320,
              exit_pseudocount=.5, exposure_pseudoseconds=1.0,
              probability_clip=1e-6,
              transition_interval='elapsed_time_between_cell_centers',
              transition_training='confirmed_user_adjacent_known_binary_regions_only',
              unknown_training='censored_no_negative_no_bridge',
              weighting='known_duration_and_events_divided_by_exact_prefix_known_multiplicity',
              emission_prior='cost_weighted_training_only',
              initial_prior='natural_deduplicated_training_duration',
              decoding='final_viterbi_and_revisable_forward',
              production_eligible=False, execution_authorized=False)


def _binary_regions(recording):
    # Reuse the exact service contract, including tolerance removal and source
    # exclusion, instead of inventing a second interpretation of human labels.
    from .pipeline import known_regions
    regions = sorted(known_regions(recording), key=lambda r: (r['start_sample'], r['end_sample']))
    previous = -1
    answer = []
    for r in regions:
        start, end = int(r['start_sample']), int(r['end_sample'])
        if start < previous:
            raise ValueError('overlapping_known_sequence_regions')
        previous = end
        state = int(r['label'] == 'singing')
        if answer and answer[-1][1] == start and answer[-1][2] == state:
            answer[-1] = (answer[-1][0], end, state)
        else:
            answer.append((start, end, state))
    return answer


def _known_boundary(regions, sample):
    """Both neighboring source samples must be known, independent of label."""
    return (any(a <= sample - 1 < b for a, b, _ in regions)
            and any(a <= sample < b for a, b, _ in regions))


def fit_transitions(prepared, ids, *, masses=None):
    """Train-only exact-coordinate transition statistics, including censoring."""
    ids = sorted(ids)
    masses = fusion.training_masses(prepared, ids) if masses is None else masses
    known = {rid: _binary_regions(prepared[rid]['recording']) for rid in ids}
    seconds, exits = np.zeros(2), np.zeros(2)
    transitions = []
    for rid in ids:
        mass = masses[rid]
        seconds += [float(mass['speech'].sum() + mass['environment'].sum()),
                    float(mass['singing'].sum())]
        for before, after in zip(known[rid][:-1], known[rid][1:]):
            if before[1] != after[0] or before[2] == after[2]:
                continue
            at, source, target = after[0], before[2], after[2]
            # A partner ending at this boundary has no right-hand sample and
            # therefore cannot duplicate this transition observation.
            count = 1 + sum(other in known and at < limit and _known_boundary(known[other], at)
                            for other, limit in prepared[rid].get('duplicate_prefixes', {}).items())
            weight = 1. / count
            exits[source] += weight
            transitions.append(dict(recording_id=rid, sample=at, source_state=source,
                                    target_state=target, multiplicity=count, weight=weight))
    if not np.isfinite(seconds).all() or np.any(seconds <= 0):
        raise ValueError('sequence_transition_training_requires_both_states')
    rates = (exits + POLICY['exit_pseudocount']) / (seconds + POLICY['exposure_pseudoseconds'])
    return dict(schema=TRANSITION_SCHEMA, states=['non_singing', 'singing'],
                exposure_seconds=seconds.tolist(), exits=exits.tolist(), rates_per_second=rates.tolist(),
                observations=transitions, training_ids=[rid for rid in ids if known[rid]],
                exit_pseudocount=POLICY['exit_pseudocount'],
                exposure_pseudoseconds=POLICY['exposure_pseudoseconds'])


def fit_candidate(prepared, ids=None, *, speech_cost=2.0):
    if speech_cost != POLICY['speech_cost']:
        raise ValueError('sequence_requires_fixed_speech_cost_two')
    ids = sorted(prepared if ids is None else ids)
    # This fit performs train-only normalization, exact duration soft targets,
    # source filtering and verified-prefix multiplicity weighting.
    emission = fusion.fit_candidate(prepared, ids, speech_cost=speech_cost)
    seconds = emission['deduplicated_known_seconds']
    positive, natural_negative = seconds['singing'], seconds['speech'] + seconds['environment']
    weighted_negative = speech_cost * seconds['speech'] + seconds['environment']
    transitions = fit_transitions(prepared, ids)
    head = dict(schema=HEAD_SCHEMA, emission_head=emission['head'],
                transitions=transitions,
                emission_prior=positive / (positive + weighted_negative),
                initial_prior=positive / (positive + natural_negative),
                policy=copy.deepcopy(POLICY), output='uncalibrated_sequence_state_evidence',
                execution_authorized=False)
    validate_head(head)
    sources = copy.deepcopy(emission['source_bindings'])
    candidate = dict(schema=CANDIDATE_SCHEMA, head=head, policy=copy.deepcopy(POLICY),
                     source_bindings=sources, source_bindings_sha256=fusion.digest(sources),
                     deduplicated_known_seconds=copy.deepcopy(seconds),
                     teaching_eligible=False, eligibility_reasons=['heldout_guard_not_evaluated'],
                     production_eligible=False, execution_authorized=False)
    candidate['candidate_id'] = fusion.digest(dict(head=head, policy=POLICY, source_bindings=sources))
    return candidate


def validate_head(head, feature_metadata=None):
    if not isinstance(head, dict) or head.get('schema') != HEAD_SCHEMA:
        raise ValueError('unsupported_sequence_head_schema')
    if (head.get('policy') != POLICY or head.get('execution_authorized') is not False
            or head.get('output') != 'uncalibrated_sequence_state_evidence'):
        raise ValueError('unsupported_sequence_policy')
    fusion.validate_head(head.get('emission_head'), feature_metadata)
    for name in ('emission_prior', 'initial_prior'):
        value = head.get(name)
        if not isinstance(value, (int, float)) or not math.isfinite(value) or not 0 < value < 1:
            raise ValueError('invalid_sequence_prior')
    t = head.get('transitions', {})
    if (t.get('schema') != TRANSITION_SCHEMA or t.get('states') != ['non_singing', 'singing']
            or t.get('exit_pseudocount') != POLICY['exit_pseudocount']
            or t.get('exposure_pseudoseconds') != POLICY['exposure_pseudoseconds']):
        raise ValueError('unsupported_sequence_transition_policy')
    exposure, exits, rates = [np.asarray(t.get(key), dtype=np.float64)
                             for key in ('exposure_seconds', 'exits', 'rates_per_second')]
    if (any(v.shape != (2,) or not np.isfinite(v).all() for v in (exposure, exits, rates))
            or np.any(exposure <= 0) or np.any(exits < 0) or np.any(rates <= 0)):
        raise ValueError('invalid_sequence_transition_statistics')
    expected = (exits + POLICY['exit_pseudocount']) / (exposure + POLICY['exposure_pseudoseconds'])
    if not np.allclose(rates, expected, rtol=1e-12, atol=0):
        raise ValueError('sequence_transition_rate_mismatch')
    fusion.digest(head)
    return head


def validate_candidate(candidate, recordings=None):
    if (not isinstance(candidate, dict) or candidate.get('schema') != CANDIDATE_SCHEMA
            or candidate.get('execution_authorized') is not False
            or candidate.get('production_eligible') is not False or candidate.get('policy') != POLICY):
        raise ValueError('unsupported_sequence_candidate')
    validate_head(candidate.get('head'))
    sources = candidate.get('source_bindings', {})
    if not sources or candidate.get('source_bindings_sha256') != fusion.digest(sources):
        raise ValueError('sequence_source_bindings_hash_mismatch')
    expected = fusion.digest(dict(head=candidate['head'], policy=POLICY, source_bindings=sources))
    if candidate.get('candidate_id') != expected:
        raise ValueError('sequence_candidate_hash_mismatch')
    if recordings is not None:
        for rid, source in sources.items():
            if rid not in recordings or any(source.get(k) != v for k, v in fusion.source_binding(recordings[rid]).items()):
                raise ValueError('sequence_candidate_source_changed:' + rid)
    return candidate


def _transition(rates, seconds):
    """Exact exp(Q t) for Q=[[-a,a],[b,-b]], retaining variable tail time."""
    if not math.isfinite(seconds) or seconds <= 0:
        raise ValueError('invalid_sequence_elapsed_time')
    a, b = rates
    moved = -math.expm1(-(a + b) * seconds)
    p01, p10 = a / (a + b) * moved, b / (a + b) * moved
    return np.array([[1 - p01, p01], [p10, 1 - p10]], dtype=np.float64)


def _emission(head, q, seconds):
    epsilon = POLICY['probability_clip']
    q = min(max(float(q), epsilon), 1 - epsilon)
    pi = head['emission_prior']
    return seconds / POLICY['evidence_reference_seconds'] * np.array(
        [math.log1p(-q) - math.log1p(-pi), math.log(q) - math.log(pi)])


def _log_initial(head):
    prior = head['initial_prior']
    return np.array([math.log1p(-prior), math.log(prior)])


def _normalized_log(values):
    return values - np.logaddexp(values[0], values[1])


def _check_rate(sample_rate):
    if isinstance(sample_rate, bool) or not isinstance(sample_rate, (int, np.integer)) or sample_rate <= 0:
        raise ValueError('invalid_sequence_sample_rate')


def _check_grid(scores, edges, sample_rate):
    _check_rate(sample_rate)
    q, edges = np.asarray(scores, dtype=np.float64), np.asarray(edges)
    if q.ndim != 1 or not np.isfinite(q).all() or np.any(q < 0) or np.any(q > 1):
        raise ValueError('invalid_sequence_scores')
    if (edges.ndim != 1 or len(edges) != len(q) + 1 or not np.issubdtype(edges.dtype, np.integer)
            or edges[0] != 0 or np.any(np.diff(edges) <= 0)):
        raise ValueError('invalid_sequence_source_grid')
    return q, edges


def _ranges(mask, edges):
    result = []
    for i, active in enumerate(mask):
        if not active:
            continue
        start, end = int(edges[i]), int(edges[i + 1])
        if result and result[-1]['end_sample'] == start:
            result[-1]['end_sample'] = end
        else:
            result.append(dict(start_sample=start, end_sample=end, label='singing'))
    return result


class ForwardEvidence:
    """Revisable, recursion-causal state evidence; never playback authority.

    Availability is mandatory per row. Supplying offline PANNs rows at their
    cell end would be false; callers must report when those features exist.
    State memory is O(1). ``reset`` clears both state and source coordinates.
    """
    def __init__(self, head, *, sample_rate=16000, stream_id='default'):
        validate_head(head)
        _check_rate(sample_rate)
        self.head = copy.deepcopy(head)
        self.sample_rate = int(sample_rate)
        self.reset(stream_id)

    def reset(self, stream_id='default'):
        if not isinstance(stream_id, str) or not stream_id:
            raise ValueError('invalid_sequence_stream_identity')
        self.stream_id, self.previous_end = stream_id, 0
        self.previous_center, self.log_state = None, None
        self.available = 0

    def append(self, score, start_sample, end_sample, *, available_sample):
        for v in (start_sample, end_sample, available_sample):
            if isinstance(v, bool) or not isinstance(v, (int, np.integer)):
                raise ValueError('invalid_sequence_cell_coordinate')
        if start_sample != self.previous_end or end_sample <= start_sample:
            raise ValueError('noncontiguous_sequence_cell')
        if available_sample < end_sample or available_sample < self.available:
            raise ValueError('invalid_sequence_feature_availability')
        if not isinstance(score, (int, float, np.number)) or not math.isfinite(score) or not 0 <= score <= 1:
            raise ValueError('invalid_sequence_score')
        center = (start_sample + end_sample) / 2.
        unary = _emission(self.head, score, (end_sample - start_sample) / self.sample_rate)
        if self.log_state is None:
            current = _log_initial(self.head) + unary
        else:
            matrix = np.log(_transition(self.head['transitions']['rates_per_second'],
                                       (center - self.previous_center) / self.sample_rate))
            joint = self.log_state[:, None] + matrix
            current = np.logaddexp(joint[0], joint[1]) + unary
        self.log_state = _normalized_log(current)
        self.previous_center, self.previous_end = center, int(end_sample)
        self.available = int(available_sample)
        q = math.exp(float(self.log_state[1]))
        return dict(stream_id=self.stream_id, start_sample=int(start_sample), end_sample=int(end_sample),
                    available_sample=self.available, score=q, selected=q >= .5, revision='provisional',
                    may_revise=True, execution_authorized=False,
                    causality='forward recurrence only; feature availability is supplied by caller')


def decode_scores(head, scores, edges, *, sample_rate=16000, availability_samples=None,
                  variant='sequence'):
    """Decode original-sample cells with no hidden smoothing postprocess.

    Default feature availability is EOF, matching the current whole-file
    acoustic front end. Independent control uses the same fitted emission q.
    """
    validate_head(head)
    q, edges = _check_grid(scores, edges, sample_rate)
    if variant not in ('sequence', 'independent'):
        raise ValueError('unsupported_sequence_variant')
    if availability_samples is None:
        availability = np.full(len(q), int(edges[-1]), dtype=np.int64)
    else:
        availability = np.asarray(availability_samples)
        if (availability.shape != q.shape or not np.issubdtype(availability.dtype, np.integer)
                or np.any(availability < edges[1:]) or np.any(np.diff(availability) < 0)):
            raise ValueError('invalid_sequence_feature_availability')
    online = ForwardEvidence(head, sample_rate=sample_rate)
    forward, predecessors = np.zeros(len(q)), np.zeros((len(q), 2), dtype=np.int8)
    score = None
    previous_center = None
    for i, probability in enumerate(q):
        start, end = int(edges[i]), int(edges[i + 1])
        forward[i] = online.append(float(probability), start, end, available_sample=int(availability[i]))['score']
        center = (start + end) / 2.
        unary = _emission(head, probability, (end - start) / sample_rate)
        if score is None:
            score = _log_initial(head) + unary
        else:
            transition = np.log(_transition(head['transitions']['rates_per_second'],
                                           (center - previous_center) / sample_rate))
            joint = score[:, None] + transition
            predecessors[i] = np.argmax(joint, axis=0)
            score = np.max(joint, axis=0) + unary
        score -= np.max(score)
        previous_center = center
    mask = np.zeros(len(q), dtype=bool)
    if variant == 'independent':
        mask = q >= POLICY['threshold']
    elif len(q):
        state = int(np.argmax(score))
        for i in range(len(q) - 1, -1, -1):
            mask[i] = bool(state)
            if i:
                state = int(predecessors[i, state])
    return dict(scores=q, selected_mask=mask, forward_scores=forward,
                forward_selected_mask=forward >= .5, edges=edges,
                availability_samples=availability, final_available_sample=int(availability[-1]) if len(q) else 0,
                ranges=_ranges(mask, edges), variant=variant, revision='final',
                forward_revision='provisional_may_revise',
                full_pipeline_causal=False, execution_authorized=False)


def predict(head, features, edges, *, sample_rate=16000, feature_metadata=None,
            availability_samples=None, variant='sequence'):
    validate_head(head, feature_metadata)
    q = fusion.predict_scores(head['emission_head'], features, feature_metadata)
    return decode_scores(head, q, edges, sample_rate=sample_rate,
                         availability_samples=availability_samples, variant=variant)
