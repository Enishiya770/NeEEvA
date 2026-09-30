"""Shared exact-sample development evaluation for fine singing boundaries.

All folds are source-component holdouts, not independent deployment validation.
The fixed sequence variant is the candidate; the independent variant is an
ablation, never a held-out selection search. No original labels are modified.
"""
from __future__ import annotations

import time
import numpy as np

from . import pipeline, proposals, temporal_fusion as fusion

SCHEMA = 'neeeva.sequence-boundary-evaluation.v1'
SHORT_MAX_SECONDS = .5
REFERENCES = ('bootstrap_proposal', 'panns_uniform', 'native_fft_speech2')
METRIC_KEYS = ('tp_seconds', 'fn_seconds', 'fp_seconds', 'tn_seconds',
               'speech_fp_seconds', 'environment_fp_seconds',
               'predicted_singing_seconds', 'predicted_unknown_seconds')
BOUNDARY_KEYS = ('gold_count', 'predicted_count', 'completely_missed_gold_count',
                 'short_gold_count', 'short_missed_gold_count', 'internal_fragment_splits',
                 'leading_missed_seconds', 'trailing_missed_seconds', 'internal_hole_seconds',
                 'completely_missed_seconds', 'predictions_merging_gold_intervals')


def ranges_for_mask(edges, mask):
    ranges, start = [], None
    for index, selected in enumerate(mask):
        if selected and start is None:
            start = int(edges[index])
        if start is not None and (not selected or index == len(mask) - 1):
            ranges.append(dict(start_sample=start, end_sample=int(edges[index] if not selected else edges[index + 1])))
            start = None
    return ranges


def _overlap(a, b):
    return max(0, min(a['end_sample'], b['end_sample']) - max(a['start_sample'], b['start_sample']))


def summarize_metrics(metrics):
    tp, fn, fp = (metrics[key] for key in ('tp_seconds', 'fn_seconds', 'fp_seconds'))
    def ratio(a, b):
        return float(a / b) if b else None
    return dict(metrics, singing_recall=ratio(tp, tp + fn), singing_precision=ratio(tp, tp + fp),
                singing_f1=ratio(2 * tp, 2 * tp + fn + fp))


def measure(recording, edges, scores, mask, *, retain_arrays=False):
    edges = np.asarray(edges)
    scores, mask = np.asarray(scores), np.asarray(mask)
    if (edges.ndim != 1 or not np.issubdtype(edges.dtype, np.integer) or len(edges) < 2
            or edges[0] != 0 or edges[-1] != recording['sample_count'] or np.any(np.diff(edges) <= 0)
            or scores.shape != (len(edges) - 1,) or mask.shape != scores.shape or mask.dtype != np.bool_
            or not np.isfinite(scores).all()):
        raise ValueError('invalid_sequence_evaluation_grid')
    positive, negative, fine = pipeline.cell_masses(recording, edges)
    seconds = np.diff(edges) / recording['sample_rate']
    metrics = dict(tp_seconds=float(positive[mask].sum()), fn_seconds=float(positive[~mask].sum()),
        fp_seconds=float(negative[mask].sum()), tn_seconds=float(negative[~mask].sum()),
        speech_fp_seconds=float(fine['speech'][mask].sum()), environment_fp_seconds=float(fine['environment'][mask].sum()),
        predicted_singing_seconds=float(seconds[mask].sum()),
        predicted_unknown_seconds=float(np.maximum(0, seconds - positive - negative)[mask].sum()))
    predicted = ranges_for_mask(edges, mask)
    annotation = recording.get('annotation') or {}
    # Raw confirmed boundaries deliberately retain even a very short EOF interval.
    # Duration confusion separately excludes unknown labels and boundary tolerance.
    gold = [r for r in annotation.get('regions', []) if r['label'] == 'singing'] if (
        annotation.get('confirmed') is True and annotation.get('source_kind') == 'user') else []
    rows = []
    rate = recording['sample_rate']
    for index, region in enumerate(gold):
        overlaps = [r for r in predicted if _overlap(region, r)]
        duration = (region['end_sample'] - region['start_sample']) / rate
        row = dict(gold_index=index, gold_start_sample=region['start_sample'], gold_end_sample=region['end_sample'],
                   duration_seconds=duration, short=duration <= SHORT_MAX_SECONDS, completely_missed=not overlaps,
                   internal_fragment_splits=max(0, len(overlaps) - 1), tolerance_samples=region.get('tolerance_samples', 0))
        if overlaps:
            first, last = overlaps[0]['start_sample'], overlaps[-1]['end_sample']
            leading = max(0, first - region['start_sample']) / rate
            trailing = max(0, region['end_sample'] - last) / rate
            recovered = sum(_overlap(region, value) for value in overlaps) / rate
            row.update(predicted_start_sample=first, predicted_end_sample=last,
                start_error_seconds=(first - region['start_sample']) / rate,
                end_error_seconds=(last - region['end_sample']) / rate,
                leading_missed_seconds=leading, trailing_missed_seconds=trailing,
                internal_hole_seconds=max(0., duration - recovered - leading - trailing), completely_missed_seconds=0.)
        else:
            row.update(leading_missed_seconds=0., trailing_missed_seconds=0., internal_hole_seconds=0.,
                       completely_missed_seconds=duration)
        rows.append(row)
    boundaries = dict(gold_count=len(gold), predicted_count=len(predicted),
        completely_missed_gold_count=sum(r['completely_missed'] for r in rows),
        short_gold_count=sum(r['short'] for r in rows),
        short_missed_gold_count=sum(r['short'] and r['completely_missed'] for r in rows),
        internal_fragment_splits=sum(r['internal_fragment_splits'] for r in rows),
        predictions_merging_gold_intervals=sum(sum(bool(_overlap(g, p)) for g in gold) > 1 for p in predicted),
        gold_intervals=rows, endpoint_scope='first/last actual overlapping prediction; missed gold has no invented endpoint',
        duration_scope='raw human singing intervals; physical confusion metrics exclude boundary tolerance',
        short_max_seconds=SHORT_MAX_SECONDS)
    for key in ('leading_missed_seconds', 'trailing_missed_seconds', 'internal_hole_seconds', 'completely_missed_seconds'):
        boundaries[key] = sum(row[key] for row in rows)
    result = dict(recording_id=recording['recording_id'], capture_serial=recording.get('capture_serial'),
        audio_sha256=recording['audio_sha256'], annotation_revision=recording.get('annotation_revision'),
        metrics=summarize_metrics(metrics), boundaries=boundaries, predicted_ranges=predicted)
    if retain_arrays:
        result.update(source_cell_edges=edges.tolist(), scores=scores.tolist(), selected_cells=mask.tolist())
    return result


def aggregate(rows):
    metrics = {key: sum(row['metrics'][key] for row in rows) for key in METRIC_KEYS}
    boundaries = {key: sum(row['boundaries'][key] for row in rows) for key in BOUNDARY_KEYS}
    intervals = [r for row in rows for r in row['boundaries']['gold_intervals'] if not r['completely_missed']]
    for side in ('start', 'end'):
        values = [r[side + '_error_seconds'] for r in intervals]
        for suffix, value in (
            ('signed_mean_seconds', np.mean(values) if values else None),
            ('absolute_mean_seconds', np.mean(np.abs(values)) if values else None),
            ('absolute_median_seconds', np.median(np.abs(values)) if values else None),
            ('absolute_p90_seconds', np.percentile(np.abs(values), 90) if values else None)):
            boundaries[side + '_' + suffix] = float(value) if value is not None else None
    return dict(metrics=summarize_metrics(metrics), boundaries=boundaries, metric_recordings=len(rows))


def evaluate_guard(summary, references, *, fold_count, complete=True):
    guard = fusion.evaluate_teaching_guard(summary, references, fold_count=fold_count, complete=complete)
    for name in REFERENCES:
        if name not in references and 'missing_reference:' + name not in guard['eligibility_reasons']:
            guard['eligibility_reasons'].append('missing_reference:' + name)
    for name, reference in references.items():
        actual = summary.get('boundaries', {}).get('short_missed_gold_count')
        baseline = reference.get('boundaries', {}).get('short_missed_gold_count')
        reason = None
        if not isinstance(actual, (float, int)) or not isinstance(baseline, (float, int)) or not np.isfinite([actual, baseline]).all():
            reason = 'missing_finite_boundary_metric:short_missed_gold_count'
        elif actual > baseline:
            reason = 'short_missed_gold_count_regression'
        if reason:
            guard['eligibility_reasons'].append(name + ':' + reason)
            guard['comparisons'][name]['eligibility_reasons'].append(reason)
            guard['comparisons'][name]['teaching_eligible'] = False
    guard['teaching_eligible'] = not guard['eligibility_reasons']
    guard['production_eligible'] = False
    guard['execution_authorized'] = False
    return guard


def validate_groups(groups, ids):
    expected = set(ids)
    observed = []
    for members in groups.values():
        known = [rid for rid in members if rid in expected]
        if len(set(known)) != len(known):
            raise ValueError('duplicate_recording_within_sequence_fold')
        observed.extend(known)
    if len(observed) != len(set(observed)) or set(observed) != expected:
        raise ValueError('sequence_folds_must_partition_eligible_recordings')


def evaluate_prepared(records, prepared, native_fft, acoustic, bootstrap_adapter, groups, *,
                      metadata_groups=None, progress=None, retain_predictions=True):
    from . import sequence_model as sequence
    ids = sorted(prepared)
    if not ids or any(set(items) != set(ids) for items in (native_fft, acoustic)):
        raise ValueError('sequence_evaluation_recording_set_mismatch')
    if any(rid not in records or not pipeline.known_regions(records[rid]) for rid in ids):
        raise ValueError('sequence_evaluation_requires_confirmed_user_known_labels')
    validate_groups(groups, ids)
    if metadata_groups is not None:
        validate_groups(metadata_groups, ids)
    start = time.perf_counter()
    timings = []

    def fit(which, train_ids):
        before = time.perf_counter()
        result = (sequence.fit_candidate(prepared, train_ids, speech_cost=2.) if which == 'sequence' else
                  fusion.fit_candidate(native_fft if which == 'native_fft_speech2' else acoustic,
                                       train_ids, speech_cost=2. if which == 'native_fft_speech2' else 1.))
        timings.append(dict(operation='fit', variant=which, seconds=time.perf_counter() - before, train_count=len(train_ids)))
        return result

    def predict(which, candidate, test_ids):
        rows = []
        for rid in test_ids:
            before = time.perf_counter()
            if which == 'sequence':
                item = prepared[rid]
                output = sequence.predict(candidate['head'], item['features'], item['edges'],
                    sample_rate=records[rid]['sample_rate'], feature_metadata=item['feature_metadata'], variant=which)
                score, mask = output['scores'], output['selected_mask']
            elif which == 'independent':
                item = prepared[rid]
                score = fusion.predict_scores(candidate['head']['emission_head'], item['features'], item['feature_metadata'])
                mask = score >= .5
            else:
                item = native_fft[rid] if which == 'native_fft_speech2' else acoustic[rid]
                score = (proposals.adapter.predict_uncalibrated_score(bootstrap_adapter, item['features'])
                         if which == 'bootstrap_proposal' else fusion.predict_scores(candidate['head'], item['features'], item['feature_metadata']))
                mask = (score >= .5) & (item['rms'] > proposals.POLICY['quiet_rms_max'])
            timings.append(dict(operation='predict', variant=which, recording_id=rid,
                                seconds=time.perf_counter() - before, rows=len(item['features'])))
            rows.append(measure(records[rid], item['edges'], score, mask, retain_arrays=retain_predictions))
        return rows

    def folds(units, scope):
        held = {name: [] for name in ('sequence', 'independent', 'native_fft_speech2', 'panns_uniform')}
        detail = []
        for group, members in sorted(units.items()):
            test_ids = sorted(set(members) & set(ids))
            if not test_ids:
                continue
            train_ids = sorted(set(ids) - set(test_ids))
            row = dict(component_id=group, train_ids=train_ids, test_ids=test_ids, variants={})
            for name in ('sequence', 'native_fft_speech2', 'panns_uniform'):
                variants = ('sequence', 'independent') if name == 'sequence' else (name,)
                try:
                    candidate = fit(name, train_ids)
                    for variant in variants:
                        predictions = predict(variant, candidate, test_ids)
                        held[variant].extend(predictions)
                        row['variants'][variant] = dict(status='ok', summary=aggregate(predictions),
                            train_source_bindings_sha256=candidate['source_bindings_sha256'])
                except (ValueError, np.linalg.LinAlgError) as exc:
                    for variant in variants:
                        row['variants'][variant] = dict(status='unavailable', reason=str(exc))
            detail.append(row)
            if progress:
                progress(dict(event='fold_completed', scope=scope, completed=len(detail), total=len(units), component_id=group))
        return dict(folds=detail, fold_count=len(detail), aggregate={name: aggregate(rows) for name, rows in held.items()},
                    complete={name: len(rows) == len(ids) for name, rows in held.items()},
                    predictions=held if retain_predictions else None)

    candidates = {'sequence': fit('sequence', ids)}
    same_data = {name: aggregate(predict(name, candidates['sequence'], ids)) for name in ('sequence', 'independent')}
    fixed_rows = predict('bootstrap_proposal', None, ids)
    fixed = aggregate(fixed_rows)
    held = folds(groups, 'exact_prefix_component_holdout')
    metadata = folds(metadata_groups, 'metadata_group_holdout') if metadata_groups else None
    references = dict(bootstrap_proposal=fixed, panns_uniform=held['aggregate']['panns_uniform'],
                      native_fft_speech2=held['aggregate']['native_fft_speech2'])
    complete = all(held['complete'].values())
    guards = {name: evaluate_guard(held['aggregate'][name], references, fold_count=held['fold_count'], complete=complete)
              for name in ('sequence', 'independent')}
    candidate = candidates['sequence']
    candidate.update(teaching_eligible=guards['sequence']['teaching_eligible'],
                     eligibility_reasons=guards['sequence']['eligibility_reasons'], evaluation_guard=guards['sequence'])
    summaries = dict(candidate=held['aggregate']['sequence'], independent=held['aggregate']['independent'], **references)
    return dict(schema=SCHEMA, status='eligible' if guards['sequence']['teaching_eligible'] else 'shadow_rejected',
        teaching_eligible=guards['sequence']['teaching_eligible'], eligibility_reasons=guards['sequence']['eligibility_reasons'],
        execution_authorized=False, production_eligible=False, selected_variant='sequence',
        candidates=candidates, candidate=candidate, guards=guards, guard=guards['sequence'],
        summaries=summaries, same_data=same_data, exact_prefix_component_holdout=held, metadata_group_holdout=metadata,
        fixed_references=dict(bootstrap_proposal=fixed), folds=held['folds'], fold_count=held['fold_count'], complete=complete,
        recording_count=len(ids), timing=dict(total_seconds=time.perf_counter() - start, operations=timings,
            feature_array_bytes=sum(item['features'].nbytes + item['edges'].nbytes for item in prepared.values()),
            memory_scope='retained fine feature/edge arrays only; excludes optimizer copies and process/runtime memory'),
        limitations=[
            'Only pitch/raw feature branches are causal. Frozen PANNs input uses whole-file context.',
            'Final sequence decoding can revise earlier decisions using later evidence; forward diagnostics are separate.',
            'Exact-prefix components remain correlated within this user/session, not independent acceptance validation.',
            'Metadata holdout has only the supplied session/content components, often one training component per fold.',
            'Fine independent is a same-fit ablation; sequence is fixed before testing and never selected by fold scores.',
            'Bootstrap is a fixed product comparator with prior same-user development provenance.',
            'Labels, threshold .5, speech cost2, feature policy and sequence policy are fixed; no threshold sweep.',
            'Native reference RMS gates remain unchanged; fine variants have no destructive frame-energy veto.',
            'No candidate authorizes production singing crop or playback.'])
