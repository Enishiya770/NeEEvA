"""Exact diagnostics for fixed category-aware singing boundary candidates.

This module does not invent phonation labels. FFT voicing is reported only as
a diagnostic proxy inside confirmed human categories, never used to change a
label, acceptance rule, threshold, or candidate prediction.
"""
from __future__ import annotations

import time
import numpy as np

from . import boundary_evaluation as boundary_eval, pipeline, proposals, sequence_evaluation as common, temporal_fusion as fusion

SCHEMA = 'neeeva.semantic-boundary-evaluation.v1'
VOICING_COLUMN = 'pitch_latest_voiced_trailing_80ms'
COVERAGE_COLUMN = 'pitch_coverage_fraction_trailing_80ms'
PHONATION_KEYS = ('known_singing_voiced_seconds', 'known_singing_nonvoiced_seconds',
                  'missed_singing_voiced_seconds', 'missed_singing_nonvoiced_seconds',
                  'internal_hole_voiced_seconds', 'internal_hole_nonvoiced_seconds',
                  'speech_false_positive_voiced_seconds', 'speech_false_positive_nonvoiced_seconds',
                  'unknown_pitch_coverage_singing_seconds')


def validate_prefix_partition(prepared, groups):
    """No verified identical source prefix may cross train/test component folds."""
    ids = set(prepared)
    common.validate_groups(groups, ids)
    membership = {rid: group for group, members in groups.items() for rid in members if rid in ids}
    checked = set()
    for rid, item in prepared.items():
        for other, limit in item.get('duplicate_prefixes', {}).items():
            if other not in ids:
                continue
            if (type(limit) is not int or limit <= 0 or limit > min(item['recording']['sample_count'], prepared[other]['recording']['sample_count'])
                    or prepared[other].get('duplicate_prefixes', {}).get(rid) != limit):
                raise ValueError('invalid_verified_prefix_relation')
            if membership[rid] != membership[other]:
                raise ValueError('verified_prefix_crosses_holdout_fold:' + rid + ':' + other)
            checked.add(tuple(sorted((rid, other))))
    return dict(eligible_recordings=len(ids), fold_count=len(groups), verified_prefix_pairs=len(checked),
                every_recording_held_once=True, verified_prefix_pairs_separated=False)


def _phonation_diagnostics(recording, edges, mask, row, fine_item):
    answer = dict(available=False, scope='FFT latest-voiced diagnostic proxy, not human phonation or semantic pause labels')
    if fine_item is None:
        return dict(answer, reason='no_fine_feature_evidence')
    names = fine_item['feature_metadata']['names']
    if VOICING_COLUMN not in names or COVERAGE_COLUMN not in names:
        return dict(answer, reason='no_matching_pitch_proxy_columns')
    fine_edges = np.asarray(fine_item['edges'])
    if not np.isin(edges, fine_edges).all():
        return dict(answer, reason='prediction_grid_not_exactly_projectable_to_fine_grid')
    selected = np.asarray(mask)[np.searchsorted(np.asarray(edges)[1:], fine_edges[:-1], side='right')]
    features = np.asarray(fine_item['features'])
    latest = features[:, names.index(VOICING_COLUMN)]
    coverage = features[:, names.index(COVERAGE_COLUMN)]
    if latest.shape != selected.shape or not np.isfinite(latest).all() or not np.isfinite(coverage).all():
        raise ValueError('invalid_semantic_phonation_diagnostic_features')
    if np.any((latest != 0.) & (latest != 1.)):
        raise ValueError('phonation_latest_voiced_must_be_binary')
    available, voiced = coverage > 0., latest == 1.
    positive, _, fine_mass = pipeline.cell_masses(recording, fine_edges)
    interior = np.zeros(len(selected), dtype=bool)
    for gold in row['boundaries']['gold_intervals']:
        overlaps = [value for value in row['predicted_ranges'] if
                    min(value['end_sample'], gold['gold_end_sample']) > max(value['start_sample'], gold['gold_start_sample'])]
        for left, right in zip(overlaps[:-1], overlaps[1:]):
            interior |= (fine_edges[:-1] >= left['end_sample']) & (fine_edges[1:] <= right['start_sample'])
    values = dict(known_singing_voiced_seconds=float(positive[available & voiced].sum()),
                  known_singing_nonvoiced_seconds=float(positive[available & ~voiced].sum()),
                  missed_singing_voiced_seconds=float(positive[available & voiced & ~selected].sum()),
                  missed_singing_nonvoiced_seconds=float(positive[available & ~voiced & ~selected].sum()),
                  internal_hole_voiced_seconds=float(positive[available & voiced & interior & ~selected].sum()),
                  internal_hole_nonvoiced_seconds=float(positive[available & ~voiced & interior & ~selected].sum()),
                  speech_false_positive_voiced_seconds=float(fine_mass['speech'][available & voiced & selected].sum()),
                  speech_false_positive_nonvoiced_seconds=float(fine_mass['speech'][available & ~voiced & selected].sum()),
                  unknown_pitch_coverage_singing_seconds=float(positive[~available].sum()))
    return dict(answer, available=True, **values)


def measure(recording, edges, scores, mask, *, fine_item=None, retain_arrays=False):
    row = boundary_eval.measure(recording, edges, scores, mask, retain_arrays=retain_arrays)
    row['phonation_proxy_diagnostics'] = _phonation_diagnostics(recording, edges, mask, row, fine_item)
    edges, scores = np.asarray(edges), np.asarray(scores)
    short = []
    for gold in row['boundaries']['gold_intervals']:
        if not gold['short']:
            continue
        start, end = gold['gold_start_sample'], gold['gold_end_sample']
        widths = np.maximum(0, np.minimum(edges[1:], end) - np.maximum(edges[:-1], start))
        active = widths > 0
        recovered = sum(max(0, min(end, p['end_sample']) - max(start, p['start_sample'])) for p in row['predicted_ranges'])
        short.append(dict(recording_id=recording['recording_id'], capture_serial=recording.get('capture_serial'),
            gold_index=gold['gold_index'], start_sample=start, end_sample=end,
            duration_seconds=(end-start)/recording['sample_rate'], touches_start=start == 0,
            touches_eof=end == recording['sample_count'], completely_missed=gold['completely_missed'],
            selected_raw_overlap_seconds=recovered/recording['sample_rate'],
            score_min=float(scores[active].min()), score_max=float(scores[active].max()),
            score_duration_mean=float(widths @ scores / widths.sum()),
            scope='all human singing intervals <=0.5s; raw boundaries, no hand-picked recording policy'))
    row['short_interval_diagnostics'] = short
    return row


def category_score_diagnostics(recording, edges, scores, names):
    """Unweighted physical-label proper scores; training costs do not hide error."""
    values = np.asarray(scores, dtype=np.float64)
    if (len(names) != 3 or set(names) != {'speech', 'singing', 'environment'}
            or values.shape != (len(edges) - 1, 3) or not np.isfinite(values).all()
            or np.any(values < 0) or np.any(values > 1)
            or not np.allclose(values.sum(axis=1), 1., rtol=1e-7, atol=1e-9)):
        raise ValueError('invalid_semantic_category_scores')
    _, _, fine = pipeline.cell_masses(recording, np.asarray(edges))
    mass = np.column_stack([fine[name] for name in names])
    known = float(mass.sum())
    nll = float(-np.sum(mass * np.log(np.maximum(values, 1e-12))))
    brier = float(np.sum(mass.sum(axis=1) * (np.square(values).sum(axis=1) + 1.) - 2. * (mass * values).sum(axis=1)))
    return dict(available=True, known_label_seconds=known, nll_seconds=nll, brier_seconds=max(0., brier),
        duration_mean_nll=nll / known if known else None,
        duration_mean_brier=max(0., brier) / known if known else None,
        class_names=list(names), log_floor=1e-12,
        scope='physical known-label duration, no class costs; scores from cost-trained head are not claimed calibrated')


def aggregate(rows):
    result = boundary_eval.aggregate(rows)
    available = [row['phonation_proxy_diagnostics'] for row in rows if row['phonation_proxy_diagnostics']['available']]
    result['phonation_proxy_diagnostics'] = dict(
        {key: sum(value[key] for value in available) for key in PHONATION_KEYS},
        metric_recordings=len(available), total_recordings=len(rows),
        scope='FFT proxy partition of human labels; not verified phonation/pause truth; diagnostic only')
    result['short_interval_diagnostics'] = [short for row in rows for short in row['short_interval_diagnostics']]
    category = [row['category_score_diagnostics'] for row in rows if row.get('category_score_diagnostics', {}).get('available')]
    known = sum(value['known_label_seconds'] for value in category)
    nll = sum(value['nll_seconds'] for value in category)
    brier = sum(value['brier_seconds'] for value in category)
    result['category_score_diagnostics'] = dict(metric_recordings=len(category), known_label_seconds=known,
        nll_seconds=nll, brier_seconds=brier,
        duration_mean_nll=nll / known if known else None, duration_mean_brier=brier / known if known else None,
        scope='semantic three-class head only; physical known-label proper-score diagnostic, no calibration or adoption claim')
    return result


def evaluate_guard(summary, references, *, fold_count, complete=True, training_available=True):
    guard = common.evaluate_guard(summary, references, fold_count=fold_count, complete=complete)
    if not training_available:
        guard['eligibility_reasons'].append('semantic_training_unavailable')
        guard['teaching_eligible'] = False
    return guard


def evaluate_prepared(records, prepared, native_fft, acoustic, bootstrap_adapter, groups, *,
                      metadata_groups=None, progress=None, retain_predictions=True):
    from . import semantic_model as semantic, boundary_model as boundary, sequence_model as sequence
    ids = sorted(prepared)
    if not ids or any(set(items) != set(ids) for items in (native_fft, acoustic)):
        raise ValueError('semantic_evaluation_recording_set_mismatch')
    if any(rid not in records or not pipeline.known_regions(records[rid]) for rid in ids):
        raise ValueError('semantic_evaluation_requires_confirmed_user_known_labels')
    coverage = validate_prefix_partition(prepared, groups)
    metadata_coverage = validate_prefix_partition(prepared, metadata_groups) if metadata_groups else None
    started, timings = time.perf_counter(), []
    variants = ('semantic', 'semantic_node', 'boundary', 'sequence', 'independent', 'native_fft_speech2', 'panns_uniform')

    def fit(name, training_ids):
        before = time.perf_counter()
        if name == 'semantic':
            candidate = semantic.fit_candidate(prepared, training_ids, speech_cost=2.)
        elif name == 'boundary':
            candidate = boundary.fit_candidate(prepared, training_ids, speech_cost=2.)
        else:
            candidate = fusion.fit_candidate(native_fft if name == 'native_fft_speech2' else acoustic,
                training_ids, speech_cost=2. if name == 'native_fft_speech2' else 1.)
        timings.append(dict(operation='fit', variant=name, seconds=time.perf_counter() - before, train_count=len(training_ids)))
        return candidate

    def predict(name, candidate, test_ids):
        rows = []
        for rid in test_ids:
            before, extra = time.perf_counter(), None
            if name in ('semantic', 'semantic_node', 'boundary', 'sequence', 'independent'):
                item = prepared[rid]
                if name == 'semantic_node':
                    categories = semantic.predict_softmax(candidate['head']['node_head'], item['features'], item['feature_metadata'])
                    states = np.argmax(categories, axis=1)
                    scores, selected = categories[:, 1], states == 1
                    extra = dict(category_scores=categories, category_names=list(semantic.CLASSES), states=states)
                elif name in ('semantic', 'boundary'):
                    module = semantic if name == 'semantic' else boundary
                    extra = module.predict(candidate['head'], item['features'], item['edges'],
                        sample_rate=records[rid]['sample_rate'], feature_metadata=item['feature_metadata'])
                    scores, selected = extra['scores'], extra['selected_mask']
                elif name == 'sequence':
                    extra = sequence.predict(candidate['head']['sequence_head'], item['features'], item['edges'],
                        sample_rate=records[rid]['sample_rate'], feature_metadata=item['feature_metadata'])
                    scores, selected = extra['scores'], extra['selected_mask']
                else:
                    scores = fusion.predict_scores(candidate['head']['sequence_head']['emission_head'],
                                                   item['features'], item['feature_metadata'])
                    selected = scores >= .5
            else:
                item = native_fft[rid] if name == 'native_fft_speech2' else acoustic[rid]
                scores = (proposals.adapter.predict_uncalibrated_score(bootstrap_adapter, item['features'])
                          if name == 'bootstrap_proposal' else fusion.predict_scores(candidate['head'], item['features'], item['feature_metadata']))
                selected = (scores >= .5) & (item['rms'] > proposals.POLICY['quiet_rms_max'])
            timings.append(dict(operation='predict', variant=name, recording_id=rid,
                                seconds=time.perf_counter() - before, rows=len(item['features'])))
            row = measure(records[rid], item['edges'], scores, selected, fine_item=prepared[rid], retain_arrays=retain_predictions)
            if name in ('semantic', 'semantic_node'):
                row['category_score_diagnostics'] = category_score_diagnostics(
                    records[rid], item['edges'], extra['category_scores'], extra['category_names'])
            if retain_predictions and extra is not None:
                if name in ('semantic', 'semantic_node'):
                    row['category_names'] = extra['category_names']
                    row['semantic_evidence'] = {key: np.asarray(extra[key]).tolist() for key in
                        ('category_scores', 'states', 'local_transition_matrices', 'forward_category_scores') if key in extra}
                elif name == 'boundary':
                    row['local_evidence'] = {key: np.asarray(extra[key]).tolist() for key in
                        ('onset_scores', 'offset_scores', 'onset_gates', 'offset_gates', 'seed_selected_mask') if key in extra}
            rows.append(row)
        return rows

    def folds(units, scope):
        held = {name: [] for name in variants}
        details = []
        for component, members in sorted(units.items()):
            test_ids = sorted(set(members) & set(ids))
            if not test_ids:
                continue
            training_ids = sorted(set(ids) - set(test_ids))
            masses = fusion.training_masses(prepared, training_ids)
            label_seconds = {label: sum(float(value[label].sum()) for value in masses.values()) for label in ('speech', 'singing', 'environment')}
            row = dict(component_id=component, train_ids=training_ids, test_ids=test_ids, variants={},
                training_known_seconds=label_seconds, training_missing_categories=[k for k, v in label_seconds.items() if v <= 0])
            for name in ('semantic', 'boundary', 'native_fft_speech2', 'panns_uniform'):
                predicted_variants = (('boundary', 'sequence', 'independent') if name == 'boundary' else
                                      ('semantic', 'semantic_node') if name == 'semantic' else (name,))
                try:
                    candidate = fit(name, training_ids)
                    for variant in predicted_variants:
                        predictions = predict(variant, candidate, test_ids)
                        held[variant].extend(predictions)
                        row['variants'][variant] = dict(status='ok', summary=aggregate(predictions),
                            train_source_bindings_sha256=candidate['source_bindings_sha256'])
                        if variant in ('semantic', 'boundary'):
                            row['variants'][variant]['training_available'] = candidate.get(variant + '_training_available') is True
                except (ValueError, np.linalg.LinAlgError) as exc:
                    for variant in predicted_variants:
                        row['variants'][variant] = dict(status='unavailable', reason=str(exc))
            details.append(row)
            if progress:
                progress(dict(event='fold_completed', scope=scope, completed=len(details), total=len(units), component_id=component))
        complete = {name: len(rows) == len(ids) and len({r['recording_id'] for r in rows}) == len(ids) for name, rows in held.items()}
        return dict(folds=details, fold_count=len(details), aggregate={name: aggregate(rows) for name, rows in held.items()},
                    complete=complete, metric_recording_counts={name: len(rows) for name, rows in held.items()},
                    predictions=held if retain_predictions else None)

    candidates = {name: fit(name, ids) for name in ('semantic', 'boundary')}
    candidates['sequence'] = candidates['boundary']['sequence_candidate']
    same_data = {name: aggregate(predict(name, candidates['semantic'] if name in ('semantic', 'semantic_node') else candidates['boundary'], ids))
                 for name in ('semantic', 'semantic_node', 'boundary', 'sequence', 'independent')}
    fixed = aggregate(predict('bootstrap_proposal', None, ids))
    held = folds(groups, 'exact_prefix_component_holdout')
    metadata = folds(metadata_groups, 'metadata_group_holdout') if metadata_groups else None
    references = dict(bootstrap_proposal=fixed, panns_uniform=held['aggregate']['panns_uniform'],
                      native_fft_speech2=held['aggregate']['native_fft_speech2'])
    reference_complete = all(held['complete'][key] for key in ('panns_uniform', 'native_fft_speech2'))
    availability = {name: candidates[name].get(name + '_training_available') is True and all(
        fold['variants'].get(name, {}).get('training_available') is True for fold in held['folds']) for name in ('semantic', 'boundary')}
    guards = dict(
        semantic=evaluate_guard(held['aggregate']['semantic'], references, fold_count=held['fold_count'],
            complete=held['complete']['semantic'] and reference_complete, training_available=availability['semantic']),
        boundary=boundary_eval.evaluate_guard(held['aggregate']['boundary'], references, fold_count=held['fold_count'],
            complete=held['complete']['boundary'] and reference_complete, training_available=availability['boundary']),
        sequence=common.evaluate_guard(held['aggregate']['sequence'], references, fold_count=held['fold_count'],
            complete=held['complete']['sequence'] and reference_complete))
    for name in ('semantic', 'boundary', 'sequence'):
        candidates[name].update(teaching_eligible=guards[name]['teaching_eligible'],
            eligibility_reasons=guards[name]['eligibility_reasons'], evaluation_guard=guards[name])
    actual, prior = held['aggregate']['semantic'], held['aggregate']['boundary']
    deltas = dict(scope='same-fold prior local-boundary comparator; diagnostic only, no variant selection',
        metrics={key: actual['metrics'][key] - prior['metrics'][key] for key in ('fn_seconds', 'speech_fp_seconds', 'environment_fp_seconds')},
        boundaries={key: actual['boundaries'][key] - prior['boundaries'][key] for key in
                    ('internal_fragment_splits', 'short_missed_gold_count', 'completely_missed_gold_count')})
    guard = guards['semantic']
    summaries = dict(candidate=actual, semantic_node=held['aggregate']['semantic_node'], boundary=prior, sequence=held['aggregate']['sequence'],
                     independent=held['aggregate']['independent'], **references)
    coverage.update(predicted_unique_recordings={name: held['metric_recording_counts'][name] for name in variants},
                    prediction_coverage_complete=all(held['complete'].values()))
    return dict(schema=SCHEMA, status='eligible' if guard['teaching_eligible'] else 'shadow_rejected',
        teaching_eligible=guard['teaching_eligible'], eligibility_reasons=guard['eligibility_reasons'],
        execution_authorized=False, production_eligible=False, selected_variant='semantic',
        candidates=candidates, candidate=candidates['semantic'], guards=guards, guard=guard,
        summaries=summaries, same_data=same_data, exact_prefix_component_holdout=held, metadata_group_holdout=metadata,
        fixed_references={'bootstrap_proposal': fixed}, folds=held['folds'], fold_count=held['fold_count'],
        complete=all(held['complete'].values()), complete_by_variant=held['complete'], recording_count=len(ids),
        semantic_training_complete=availability['semantic'], boundary_training_complete=availability['boundary'],
        comparative_deltas_vs_boundary=deltas, prefix_fold_coverage=coverage, metadata_fold_coverage=metadata_coverage,
        timing=dict(total_seconds=time.perf_counter() - started, operations=timings,
            feature_array_bytes=sum(item['features'].nbytes + item['edges'].nbytes for item in prepared.values()),
            memory_scope='retained fine arrays; excludes optimizer/local-window copies and process memory'),
        limitations=[
            'Three-category outputs are uncalibrated model scores, not verified posterior probabilities.',
            'Local edge evidence uses right context and frozen PANNs sees the full recording; final decoding is not a live causal decision.',
            'Prefix folds are same-user/session development diagnostics; all identical verified prefixes stay within one fold.',
            'Metadata-group diagnostics report missing training categories and cannot establish cross-session generalization.',
            'FFT voiced/nonvoiced partitions are diagnostic proxies, not human pause labels and never a classification veto.',
            'Every <=0.5s human singing interval is reported, including EOF intervals; none is selected as a special-case policy.',
            'The fixed three-reference acceptance guard is unchanged; previous boundary results are comparative diagnostics.',
            'No threshold sweep, best-fold selection, source-label edit or production playback authorization.'])
