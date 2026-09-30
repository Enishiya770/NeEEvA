"""Offline, exact-sample diagnostics for an immutable personal-learning snapshot.

Run from the repository root with ``python -m
Tools.SingingSegmentation.evaluate_personal_learning --snapshot DIR --output DIR
--group-holdout --recording-holdout``. No acoustic backbone or service is loaded.
The snapshot contains recordings.json, model.json, bootstrap.json, features/
<audio SHA>-<backend identity>.json and recordings/<recording ID>/original.wav.
Outputs are diagnostics, never an acceptance decision or execution authority.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
from collections import Counter
from pathlib import Path
import sys

import numpy as np

if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from Server.SingingLearning import pipeline, proposals
from Tools.SingingSegmentation import fit_score_adapter as adapter

VARIANTS = ('bootstrap_raw', 'bootstrap_proposal', 'personal_raw',
            'blended_score', 'deployed_proposal')
CONFUSION = ('tp_seconds', 'fn_seconds', 'fp_seconds', 'tn_seconds',
             'speech_fp_seconds', 'environment_fp_seconds',
             'predicted_singing_seconds', 'predicted_unknown_seconds')


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def file_sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def ratio(top, bottom):
    return float(top / bottom) if bottom else None


def summarize_confusion(confusion):
    result = dict(confusion)
    tp, fn, fp = (result[k] for k in ('tp_seconds', 'fn_seconds', 'fp_seconds'))
    result.update(singing_recall=ratio(tp, tp + fn),
                  singing_precision=ratio(tp, tp + fp),
                  singing_f1=ratio(2 * tp, 2 * tp + fp + fn))
    return result


def ranges_for_mask(edges, mask):
    ranges, first = [], None
    for i, selected in enumerate(mask):
        if selected and first is None:
            first = int(edges[i])
        if first is not None and (not selected or i == len(mask) - 1):
            ranges.append(dict(start_sample=first,
                               end_sample=int(edges[i] if not selected else edges[i + 1])))
            first = None
    return ranges


def interval_overlap(a, b):
    return max(0, min(a['end_sample'], b['end_sample']) -
               max(a['start_sample'], b['start_sample']))


def match_intervals(gold, predicted):
    """Maximum total positive overlap, order-preserving one-to-one matching.

    Gold and predictions are each sorted and non-overlapping. Matching never
    invents an endpoint for a missed interval; fragmentation is measured
    separately against every overlapping prediction, including unmatched ones.
    """
    values = np.zeros((len(gold) + 1, len(predicted) + 1), dtype=np.int64)
    action = np.zeros_like(values, dtype=np.int8)
    for i, g in enumerate(gold, 1):
        for j, p in enumerate(predicted, 1):
            overlap = interval_overlap(g, p)
            candidates = [(int(values[i - 1, j]), 1), (int(values[i, j - 1]), 2)]
            if overlap:
                candidates.append((int(values[i - 1, j - 1]) + overlap, 3))
            values[i, j], action[i, j] = max(candidates)
    matched, i, j = {}, len(gold), len(predicted)
    while i and j:
        move = action[i, j]
        if move == 3:
            matched[i - 1] = j - 1
            i, j = i - 1, j - 1
        elif move == 1:
            i -= 1
        else:
            j -= 1
    return matched


def boundary_report(recording, predicted):
    ann = recording.get('annotation') or {}
    gold = [r for r in ann.get('regions', []) if r['label'] == 'singing'] if (
        ann.get('confirmed') is True and ann.get('source_kind') == 'user') else []
    matched = match_intervals(gold, predicted)
    rows = []
    for i, g in enumerate(gold):
        indices = [j for j, p in enumerate(predicted) if interval_overlap(g, p)]
        row = dict(gold_index=i, gold_start_sample=g['start_sample'],
                   gold_end_sample=g['end_sample'], tolerance_samples=g.get('tolerance_samples', 0),
                   overlapping_prediction_indices=indices, overlapping_fragments=len(indices),
                   internal_fragment_splits=max(0, len(indices) - 1),
                   completely_missed=not indices, matched_prediction_index=matched.get(i))
        if i in matched:
            p = predicted[matched[i]]
            rate = recording['sample_rate']
            start = p['start_sample'] - g['start_sample']
            end = p['end_sample'] - g['end_sample']
            tol = g.get('tolerance_samples', 0)
            row.update(predicted_start_sample=p['start_sample'], predicted_end_sample=p['end_sample'],
                       overlap_samples=interval_overlap(g, p),
                       start_error_samples=start, end_error_samples=end,
                       start_error_seconds=start / rate, end_error_seconds=end / rate,
                       start_error_outside_tolerance_seconds=max(0, abs(start) - tol) / rate,
                       end_error_outside_tolerance_seconds=max(0, abs(end) - tol) / rate)
        rows.append(row)
    merged = sum(sum(bool(interval_overlap(g, p)) for g in gold) > 1 for p in predicted)
    return dict(gold_intervals=rows, gold_count=len(gold), predicted_count=len(predicted),
                matched_count=len(matched), unmatched_gold_count=len(gold) - len(matched),
                completely_missed_gold_count=sum(r['completely_missed'] for r in rows),
                internal_fragment_splits=sum(r['internal_fragment_splits'] for r in rows),
                predictions_merging_gold_intervals=merged,
                unmatched_prediction_count=len(predicted) - len(matched),
                endpoint_scope='raw confirmed endpoints; signed predicted minus gold; positive start is late, positive end is overrun',
                matching='maximum total positive overlap, order-preserving one-to-one')


def evaluate_mask(recording, edges, scores, mask):
    positive, negative, fine = pipeline.cell_masses(recording, edges)
    seconds = np.diff(edges) / recording['sample_rate']
    confusion = dict(tp_seconds=float(positive[mask].sum()), fn_seconds=float(positive[~mask].sum()),
                     fp_seconds=float(negative[mask].sum()), tn_seconds=float(negative[~mask].sum()),
                     speech_fp_seconds=float(fine['speech'][mask].sum()),
                     environment_fp_seconds=float(fine['environment'][mask].sum()),
                     predicted_singing_seconds=float(seconds[mask].sum()),
                     predicted_unknown_seconds=float(np.maximum(0, seconds - positive - negative)[mask].sum()))
    ranges = ranges_for_mask(edges, mask)
    return dict(metrics=summarize_confusion(confusion), predicted_ranges=ranges,
                scores=scores.tolist(), selected_cells=mask.tolist(),
                boundaries=boundary_report(recording, ranges))


def scores_and_masks(item, personal, bootstrap):
    prior = adapter.predict_uncalibrated_score(bootstrap, item['features'])
    fitted = adapter.predict_uncalibrated_score(personal, item['features'])
    weight = proposals.POLICY['provisional_personal_weight']
    blended = (1 - weight) * prior + weight * fitted
    active = item['rms'] > proposals.POLICY['quiet_rms_max']
    threshold = proposals.POLICY['singing_threshold']
    scores = dict(bootstrap_raw=prior, bootstrap_proposal=prior, personal_raw=fitted,
                  blended_score=blended, deployed_proposal=blended)
    masks = {name: score >= threshold for name, score in scores.items()}
    for name in ('bootstrap_proposal', 'deployed_proposal'):
        masks[name] &= active
    return scores, masks


def evaluate_record(recording, item, personal, bootstrap):
    scores, masks = scores_and_masks(item, personal, bootstrap)
    variants = {name: evaluate_mask(recording, item['edges'], scores[name], masks[name])
                for name in VARIANTS}
    _, _, fine = pipeline.cell_masses(recording, item['edges'])
    annotation = recording.get('annotation') or {}
    labels = []
    for score, rms, speech in zip(scores['deployed_proposal'], item['rms'], item['speech']):
        if rms <= proposals.POLICY['quiet_rms_max']:
            labels.append('environment')
        elif score >= proposals.POLICY['singing_threshold']:
            labels.append('singing')
        elif score < proposals.POLICY['speech_max_singing_score'] and speech >= proposals.POLICY['speech_family_min']:
            labels.append('speech')
        else:
            labels.append('uncertain')
    return dict(recording_id=recording['recording_id'], sample_rate=recording['sample_rate'],
                sample_count=recording['sample_count'], audio_sha256=recording['audio_sha256'],
                annotation_revision=recording['annotation_revision'],
                confirmed_source_kind=annotation.get('source_kind', 'unknown'),
                eligible_known_labels=bool(pipeline.known_regions(recording)),
                group_id=item['group_id'], feature_sha256=item['feature_sha256'],
                source_cell_edges=item['edges'].tolist(), cell_rms=item['rms'].tolist(),
                known_regions=pipeline.known_regions(recording),
                annotated_regions=annotation.get('regions', []),
                known_label_seconds={key: float(mass.sum()) for key, mass in fine.items()},
                deployed_proposal_labels=labels, variants=variants)


def aggregate(records, variants=VARIANTS):
    result = {}
    for name in variants:
        selected = [r['variants'][name] for r in records if r['eligible_known_labels']]
        confusion = {key: sum(p['metrics'][key] for p in selected) for key in CONFUSION}
        boundaries = [p['boundaries'] for p in selected]
        matches = [r for b in boundaries for r in b['gold_intervals'] if r['matched_prediction_index'] is not None]
        endpoint = {}
        for side in ('start', 'end'):
            errors = [r[side + '_error_seconds'] for r in matches]
            endpoint[side + '_signed_mean_seconds'] = float(np.mean(errors)) if errors else None
            endpoint[side + '_absolute_mean_seconds'] = float(np.mean(np.abs(errors))) if errors else None
            endpoint[side + '_absolute_median_seconds'] = float(np.median(np.abs(errors))) if errors else None
            endpoint[side + '_absolute_p90_seconds'] = float(np.percentile(np.abs(errors), 90)) if errors else None
        endpoint.update({key: sum(b[key] for b in boundaries) for key in (
            'gold_count', 'predicted_count', 'matched_count', 'unmatched_gold_count',
            'completely_missed_gold_count', 'internal_fragment_splits',
            'predictions_merging_gold_intervals', 'unmatched_prediction_count')})
        result[name] = dict(metrics=summarize_confusion(confusion), boundaries=endpoint,
                            metric_recordings=len(selected))
    return result


def assemble_training(prepared, ids, weighting='group_equal'):
    # Fixed diagnostic alternatives; never choose one by held-out performance.
    exponent = {'group_equal': 1., 'duration': 0., 'sqrt_group_duration': .5}[weighting]
    group_total = {}
    for rid in ids:
        item = prepared[rid]
        group_total[item['group_id']] = group_total.get(item['group_id'], 0.) + float((item['positive'] + item['negative']).sum())
    xs, ys, weights = [], [], []
    for rid in ids:
        item = prepared[rid]
        mass = item['positive'] + item['negative']
        keep = mass > 0
        if not np.any(keep):
            continue
        xs.append(item['features'][keep])
        ys.append(item['positive'][keep] / mass[keep])
        weights.append(mass[keep] / group_total[item['group_id']] ** exponent)
    if not xs:
        raise adapter.TrainingDataError('no_known_training_rows')
    return np.concatenate(xs), np.concatenate(ys), np.concatenate(weights)


def holdout(records, prepared, bootstrap, by_group, weighting='group_equal'):
    eligible = [rid for rid in sorted(records) if pipeline.known_regions(records[rid])]
    units = {}
    for rid in eligible:
        units.setdefault(prepared[rid]['group_id'] if by_group else rid, []).append(rid)
    folds, all_predictions = [], []
    for unit, test_ids in sorted(units.items()):
        train_ids = [rid for rid in eligible if rid not in test_ids]
        fold = dict(held_out_unit=unit, train_recording_ids=train_ids, test_recording_ids=test_ids,
                    train_group_count=len({prepared[rid]['group_id'] for rid in train_ids}),
                    test_group_count=len({prepared[rid]['group_id'] for rid in test_ids}),
                    train_known_seconds={key: sum(float(prepared[rid][key].sum()) for rid in train_ids)
                                         for key in ('positive', 'negative')},
                    independence_scope='metadata_connected_group_holdout' if by_group else 'correlated_recording_holdout_not_independent')
        try:
            x, y, weights = assemble_training(prepared, train_ids, weighting)
            fitted = adapter.fit_score_adapter(x, y, weights, feature_metadata=prepared[train_ids[0]]['metadata'])
        except (adapter.TrainingDataError, adapter.ConvergenceError) as exc:
            fold.update(status='skipped', reason=exc.reason, diagnostics=exc.diagnostics)
            folds.append(fold)
            continue
        predictions = [evaluate_record(records[rid], prepared[rid], fitted, bootstrap) for rid in test_ids]
        fold.update(status='completed', adapter=fitted, metrics=aggregate(predictions), predictions=predictions)
        folds.append(fold)
        all_predictions.extend(predictions)
    return dict(weighting=weighting, scope='exploratory_connected_group_holdout_insufficient_for_acceptance' if by_group else
                'leave_recording_out_correlated_same_session_diagnostic_not_independent_validation',
                warning='No threshold tuning; prior is fixed but its independence from this batch cannot be established from its c* provenance IDs. No random frame split.',
                unit_count=len(units), completed_folds=sum(f['status'] == 'completed' for f in folds),
                skipped_folds=sum(f['status'] == 'skipped' for f in folds),
                aggregate=aggregate(all_predictions), folds=folds)


def grid_oracle(records, prepared):
    """Label-derived duration-error floor, not a predictor or accepted model."""
    predictions, nearest = [], []
    for rid, rec in sorted(records.items()):
        if not pipeline.known_regions(rec):
            continue
        item = prepared[rid]
        pos, neg = item['positive'], item['negative']
        mass = pos + neg
        score = np.divide(pos, mass, out=np.zeros_like(pos), where=mass > 0)
        result = evaluate_mask(rec, item['edges'], score, pos > neg)
        predictions.append(dict(recording_id=rid, eligible_known_labels=True,
                                variants={'native_grid_oracle': result}, source_cell_edges=item['edges'].tolist()))
        for i, region in enumerate(rec['annotation']['regions']):
            if region['label'] != 'singing':
                continue
            for side in ('start', 'end'):
                endpoint = region[side + '_sample']
                edge = int(item['edges'][np.argmin(np.abs(item['edges'] - endpoint))])
                nearest.append(dict(recording_id=rid, region_index=i, endpoint=side,
                                    gold_sample=endpoint, nearest_native_edge_sample=edge,
                                    nearest_native_edge_error_seconds=(edge - endpoint) / rec['sample_rate']))
    summary = aggregate(predictions, ('native_grid_oracle',))
    metric = summary['native_grid_oracle']['metrics']
    metric['minimum_fp_plus_fn_seconds'] = metric['fp_seconds'] + metric['fn_seconds']
    absolute = [abs(row['nearest_native_edge_error_seconds']) for row in nearest]
    return dict(scope='gold_label_oracle_only_not_predictive_performance',
                rule='select a whole cell iff known singing mass > known nonsinging mass; unknown mass ignored; no RMS exclusion',
                purpose='minimum possible FP+FN duration for any binary head using this native grid',
                aggregate=summary, predictions=predictions, nearest_gold_endpoints=nearest,
                nearest_endpoint_absolute_mean_seconds=float(np.mean(absolute)) if absolute else None,
                nearest_endpoint_absolute_max_seconds=max(absolute) if absolute else None)


def weighting_ablations(records, prepared, bootstrap, recording_holdout):
    result = {}
    ids = [rid for rid in sorted(records) if pipeline.known_regions(records[rid])]
    for weighting in ('duration', 'sqrt_group_duration'):
        x, y, weights = assemble_training(prepared, ids, weighting)
        fitted = adapter.fit_score_adapter(x, y, weights, feature_metadata=prepared[ids[0]]['metadata'])
        predictions = [evaluate_record(rec, prepared[rid], fitted, bootstrap) for rid, rec in sorted(records.items())]
        value = dict(weighting=weighting, adapter=fitted, execution_authorized=False,
                     scope='fixed_weighting_ablation_not_model_selection_or_deployment',
                     same_data=dict(scope='training_batch_diagnostic', aggregate=aggregate(predictions), predictions=predictions))
        if recording_holdout:
            value['recording_holdout'] = holdout(records, prepared, bootstrap, False, weighting)
        result[weighting] = value
    return result


def load_snapshot(snapshot, model_path=None, bootstrap_path=None):
    loaded = {}
    def read(path):
        loaded[str(path.resolve())] = file_sha(path)
        return read_json(path)
    payload = read(snapshot / 'recordings.json')
    raw = payload['recordings']
    records = {r['recording_id']: r for r in raw} if isinstance(raw, list) else raw
    if len(records) != len(raw):
        raise ValueError('duplicate_recording_ids')
    model = read(model_path or snapshot / 'model.json')
    bootstrap = read(bootstrap_path or snapshot / 'bootstrap.json')
    adapter.validate_adapter(model['adapter'])
    adapter.validate_adapter(bootstrap)
    if model.get('model_stage') != 'provisional':
        raise ValueError('75_25_deployed_proposal_requires_provisional_model')
    if model['source_dataset_revision'] != payload['dataset_revision']:
        raise ValueError('model_dataset_revision_mismatch')
    source = model.get('source_snapshot', {})
    if set(source) != set(records):
        raise ValueError('model_source_recording_set_mismatch')
    # Match labels/source precisely; incidental mutable job IDs do not matter.
    for rid, rec in records.items():
        for key in ('annotation', 'annotation_revision', 'audio_sha256', 'sample_rate', 'sample_count',
                    'session_id', 'content_group', 'parent_recording_id', 'lineage_source_ids'):
            if rec.get(key) != source[rid].get(key):
                raise ValueError('model_source_mismatch:' + rid + ':' + key)
    summary = pipeline.dataset_summary(records)
    prepared = {}
    backend_ids = {v['backend_identity'] for v in model['feature_provenance'].values()}
    for rid, rec in sorted(records.items()):
        identity = rec['feature_backend_identity']
        path = snapshot / 'features' / (rec['audio_sha256'] + '-' + identity + '.json')
        feature = read(path)
        if feature.get('learning_backend_identity') != identity or backend_ids != {identity}:
            raise ValueError('feature_backend_identity_mismatch:' + rid)
        if feature.get('model_version') != proposals.BOOTSTRAP_CHECKPOINT:
            raise ValueError('bootstrap_checkpoint_mismatch:' + rid)
        edges, features, metadata = pipeline.feature_arrays(feature, rec)
        for head in (bootstrap, model['adapter']):
            if not proposals.compatible_features(head['feature_metadata'], metadata):
                raise ValueError('feature_definition_mismatch:' + rid)
        audio = snapshot / 'recordings' / rid / 'original.wav'
        loaded[str(audio.resolve())] = file_sha(audio)
        rms = proposals.cell_rms(audio, rec, edges)
        positive, negative, _ = pipeline.cell_masses(rec, edges)
        columns = feature['timeline']['columns']
        speech = np.asarray(feature['timeline']['scores'])[:, [columns.index(k) for k in metadata['actual_speech_family_members']]].max(axis=1)
        prepared[rid] = dict(features=features, edges=edges, metadata=metadata, rms=rms, speech=speech,
                             positive=positive, negative=negative,
                             feature_sha256=loaded[str(path.resolve())], group_id=summary['recording_groups'][rid])
    return payload, records, model, bootstrap, prepared, summary, loaded


def coverage_report(records, summary):
    source_count, source_seconds = Counter(), Counter()
    rows = []
    for rid, rec in sorted(records.items()):
        ann = rec.get('annotation') or {}
        kind = ann.get('source_kind', 'unconfirmed') if ann.get('confirmed') is True else 'unconfirmed'
        source_count[kind] += 1
        source_seconds[kind] += rec['sample_count'] / rec['sample_rate']
        known = pipeline.known_regions(rec)
        seconds = sum(r['end_sample'] - r['start_sample'] for r in known) / rec['sample_rate']
        rows.append(dict(recording_id=rid, confirmed_source_kind=kind,
                         annotation_revision=rec['annotation_revision'],
                         duration_seconds=rec['sample_count'] / rec['sample_rate'], known_seconds=seconds,
                         excluded_seconds=rec['sample_count'] / rec['sample_rate'] - seconds,
                         eligible=bool(known), reason='explicit_confirmed_user_known_regions' if known else
                         ('source_not_confirmed_user' if kind != 'user' else 'no_known_regions_after_tolerance')))
    total = sum(source_seconds.values())
    known = sum(summary['label_seconds'][k] for k in ('singing', 'speech', 'environment'))
    return dict(source_recording_counts=dict(source_count), source_seconds=dict(source_seconds),
                total_seconds=total, known_seconds=known, known_fraction=ratio(known, total),
                eligible_recording_count=sum(r['eligible'] for r in rows),
                excluded_recording_count=sum(not r['eligible'] for r in rows),
                recording_exposure_scope='summed original recording durations; overlapping source PCM may be repeated; not unique-source duration',
                recordings=rows)


def diagnostic_findings(report, prepared):
    current = report['same_data']['aggregate']
    prior, personal, deployed = (current[name]['metrics'] for name in
                                ('bootstrap_proposal', 'personal_raw', 'deployed_proposal'))
    groups = {}
    for item in prepared.values():
        mass = float((item['positive'] + item['negative']).sum())
        if not mass:
            continue
        group = groups.setdefault(item['group_id'], dict(known_seconds=0., singing_seconds=0., eligible_recordings=0))
        group['known_seconds'] += mass
        group['singing_seconds'] += float(item['positive'].sum())
        group['eligible_recordings'] += 1
    total = sum(g['known_seconds'] for g in groups.values())
    sqrt_total = sum(g['known_seconds'] ** .5 for g in groups.values())
    for group in groups.values():
        group.update(group_equal_weight=1 / len(groups), duration_weight=group['known_seconds'] / total,
                     sqrt_group_duration_weight=group['known_seconds'] ** .5 / sqrt_total,
                     singing_fraction=group['singing_seconds'] / group['known_seconds'])
    oracle_error = report['native_grid_oracle']['aggregate']['native_grid_oracle']['metrics']['minimum_fp_plus_fn_seconds']
    return dict(scope='descriptive_diagnostics_not_acceptance_or_causal_proof', training_group_weights=groups,
                group_equal_weighted_singing_fraction=sum(g['group_equal_weight'] * g['singing_fraction'] for g in groups.values()),
                duration_weighted_singing_fraction=sum(g['duration_weight'] * g['singing_fraction'] for g in groups.values()),
                deployed_vs_bootstrap=dict(singing_seconds_gained=deployed['tp_seconds'] - prior['tp_seconds'],
                                          added_false_positive_seconds=deployed['fp_seconds'] - prior['fp_seconds'],
                                          added_speech_false_positive_seconds=deployed['speech_fp_seconds'] - prior['speech_fp_seconds'],
                                          f1_change=deployed['singing_f1'] - prior['singing_f1']),
                personal_vs_bootstrap_added_speech_false_positive_seconds=personal['speech_fp_seconds'] - prior['speech_fp_seconds'],
                deployed_fp_plus_fn_seconds=deployed['fp_seconds'] + deployed['fn_seconds'],
                native_grid_oracle_fp_plus_fn_seconds=oracle_error,
                deployed_error_above_native_grid_minimum_seconds=deployed['fp_seconds'] + deployed['fn_seconds'] - oracle_error,
                low_energy_rule_changed_selected_cells=sum(sum(a != b for a, b in zip(
                    rec['variants']['blended_score']['selected_cells'], rec['variants']['deployed_proposal']['selected_cells']))
                    for rec in report['same_data']['predictions'] if rec['eligible_known_labels']))


def write_csv(path, rows):
    keys = list(dict.fromkeys(key for row in rows for key in row))
    with path.open('w', encoding='utf-8-sig', newline='') as stream:
        writer = csv.DictWriter(stream, fieldnames=keys)
        writer.writeheader()
        for row in rows:
            writer.writerow({key: json.dumps(value, ensure_ascii=False) if isinstance(value, (list, dict)) else value
                             for key, value in row.items()})


def write_outputs(output, report):
    output.mkdir(parents=True, exist_ok=True)
    (output / 'evaluation.json').write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False), encoding='utf-8')
    summaries, recordings, endpoints, cells, ranges = [], [], [], [], []
    scopes = [('same_data', report['same_data'])]
    for key in ('group_holdout', 'recording_holdout'):
        if key in report:
            scopes.append((key, report[key]))
    for weighting, value in report.get('weighting_ablations', {}).items():
        for key in ('same_data', 'recording_holdout'):
            if key in value:
                scopes.append(('ablation_' + weighting + '_' + key, value[key]))
    for scope, data in scopes:
        for name, result in data['aggregate'].items():
            summaries.append(dict(scope=scope, variant=name, **result['metrics'], **result['boundaries']))
        batches = [(None, data['predictions'])] if 'predictions' in data else [
            (fold['held_out_unit'], fold['predictions']) for fold in data['folds'] if fold['status'] == 'completed']
        for fold_id, predictions in batches:
            for rec in predictions:
                common = dict(scope=scope, fold_id=fold_id, recording_id=rec['recording_id'],
                              sample_rate=rec['sample_rate'], confirmed_source_kind=rec['confirmed_source_kind'],
                              eligible_known_labels=rec['eligible_known_labels'], group_id=rec['group_id'])
                for name, result in rec['variants'].items():
                    recordings.append(dict(common, variant=name, **rec['known_label_seconds'], **result['metrics'],
                                           predicted_range_count=len(result['predicted_ranges'])))
                    endpoints.extend(dict(common, variant=name, **row) for row in result['boundaries']['gold_intervals'])
                    ranges.extend(dict(common, variant=name, **r, start_seconds=r['start_sample'] / rec['sample_rate'],
                                       end_seconds=r['end_sample'] / rec['sample_rate']) for r in result['predicted_ranges'])
                for i, (start, end) in enumerate(zip(rec['source_cell_edges'][:-1], rec['source_cell_edges'][1:])):
                    row = dict(common, start_sample=start, end_sample=end, start_seconds=start / rec['sample_rate'],
                               end_seconds=end / rec['sample_rate'], rms=rec['cell_rms'][i],
                               deployed_label=rec['deployed_proposal_labels'][i])
                    for name, result in rec['variants'].items():
                        row[name + '_score'] = result['scores'][i]
                        row[name + '_selected'] = result['selected_cells'][i]
                    cells.append(row)
    oracle = report['native_grid_oracle']
    for name, result in oracle['aggregate'].items():
        summaries.append(dict(scope='gold_label_oracle', variant=name, **result['metrics'], **result['boundaries']))
    for filename, rows in (('summary.csv', summaries), ('per_recording.csv', recordings),
                            ('endpoints.csv', endpoints), ('timelines.csv', cells), ('ranges.csv', ranges),
                            ('source_coverage.csv', report['coverage']['recordings']),
                            ('oracle_nearest_endpoints.csv', oracle['nearest_gold_endpoints'])):
        write_csv(output / filename, rows)
    lines = [
        '# Frozen personal singing batch diagnostics', '',
        '**Same-data results are training-batch diagnostics, not held-out accuracy or deployment acceptance.**', '',
        f"Dataset revision: {report['dataset_revision']}; model: `{report['model_id']}`.",
        f"Source coverage: {report['coverage']['eligible_recording_count']} eligible recordings of {report['recording_count']}; "
        f"{report['coverage']['known_seconds']:.3f} known seconds of {report['coverage']['total_seconds']:.3f} recording seconds.",
        f"Connected metadata groups: {report['dataset_summary']['independent_group_count']}. "
        'Readiness reasons: ' + ', '.join(report['dataset_summary']['readiness_reasons']) + '.', '',
        'All thresholds and the 75% bootstrap / 25% personal mixture are fixed by the existing proposal policy. '
        'Raw heads use score >= 0.5. Proposal variants additionally exclude cells whose maximum-channel PCM RMS is <= 0.001. '
        'The bootstrap proposal is included to make the deployed comparison use the same low-energy rule.', '',
        '| Scope | Variant | Singing recall | Singing precision | Speech FP s | Environment FP s | Singing FN s |',
        '|---|---|---:|---:|---:|---:|---:|']
    for row in summaries:
        pct = lambda v: 'n/a' if v is None else f'{v:.2%}'
        lines.append(f"| {row['scope']} | {row['variant']} | {pct(row['singing_recall'])} | {pct(row['singing_precision'])} | "
                     f"{row['speech_fp_seconds']:.3f} | {row['environment_fp_seconds']:.3f} | {row['fn_seconds']:.3f} |")
    findings = report['diagnostic_findings']
    delta = findings['deployed_vs_bootstrap']
    lines += ['', f"The current deployed mixture gains {delta['singing_seconds_gained']:.3f} s of known singing "
              f"and adds {delta['added_false_positive_seconds']:.3f} s of false positives relative to bootstrap proposals. "
              f"Its F1 change is {delta['f1_change']:+.5f}. The unblended personal head adds "
              f"{findings['personal_vs_bootstrap_added_speech_false_positive_seconds']:.3f} s of speech false positives. "
              f"The low-energy rule changes {findings['low_energy_rule_changed_selected_cells']} selected cells on eligible recordings.", '',
              'These observed tradeoffs do not establish an improvement in usable singing extraction. Missed singing '
              'intervals, speech false positives and large endpoint errors require review before any crop or playback use. '
              'No new acceptance threshold or model choice is inferred from this batch.', '',
              f"Equal group weighting produces a {findings['group_equal_weighted_singing_fraction']:.2%} weighted singing target mass, "
              f"versus {findings['duration_weighted_singing_fraction']:.2%} under ordinary duration weighting. "
              'The JSON records each group duration and its exact weight under all three fixed alternatives. '
              'Large changes under these alternatives demonstrate sensitivity to group weighting; they do not alone '
              'identify an optimal weighting policy. Additional independently collected sessions with singing, speech '
              'and environment labels are needed to compare that policy.']
    lines += ['', 'Known-sample durations use exact intersections on original PCM coordinates, after excluding boundary '
              'tolerance bands, uncertain regions and blank gaps. Playback, mixed and unconfirmed recordings never '
              'contribute training labels or metric denominators. Durations are recording exposure, not deduplicated source PCM.', '',
              'Boundary errors compare matched native prediction intervals with original confirmed singing endpoints. '
              'Positive start error means a late start; positive end error means an overrun. Matching maximizes total '
              'overlap with one-to-one, order-preserving matches. Missed intervals receive no invented endpoint; '
              'fragmentation and merged gold intervals are counted separately. Endpoint means describe matched intervals only.', '',
              'Connected-group holdout refits the personal head and standardizer using training groups only. With fewer '
              'than three independent groups this is exploratory; the service validation eligibility rules still apply. '
              'Leave-recording-out can share session, content or source PCM with training and is explicitly a correlated '
              'diagnostic. One-class/no-data folds are skipped and retained in JSON. No random frame split is used. '
              'The fixed bootstrap has earlier development provenance, but c* IDs do not establish independence from this batch.', '',
              'Fixed weighting ablations, when requested, replace equal total group weight with either ordinary known-duration '
              'weighting or square-root total group duration. Every fold recomputes weights and standardization from training '
              'data only. These are diagnostic alternatives, not a fitted choice or a proposed deployment.', '',
              f"The label-derived native-grid oracle has minimum FP+FN of {oracle['aggregate']['native_grid_oracle']['metrics']['minimum_fp_plus_fn_seconds']:.3f} s. "
              'It selects each cell using gold duration overlap and is not predictive performance. Nearest-edge endpoint '
              'errors measure grid quantization only; multi-second errors beyond that are not explained by grid quantization alone.', '',
              'Detailed artifacts: `evaluation.json` contains frozen input hashes, model provenance, fold adapters and '
              'full per-recording results. `summary.csv`, `per_recording.csv`, `timelines.csv`, `ranges.csv`, '
              '`endpoints.csv` and `source_coverage.csv` support inspection. No live service state or snapshot file is written.']
    (output / 'REPORT.md').write_text('\n'.join(lines) + '\n', encoding='utf-8')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--model', type=Path, help='Frozen full personal model report; default snapshot/model.json')
    parser.add_argument('--bootstrap', type=Path, help='Frozen bootstrap adapter; default snapshot/bootstrap.json')
    parser.add_argument('--group-holdout', action='store_true')
    parser.add_argument('--recording-holdout', action='store_true')
    parser.add_argument('--weighting-ablations', action='store_true', help='Fixed duration and sqrt-group-duration diagnostics; never model selection')
    args = parser.parse_args(argv)
    snapshot, output = args.snapshot.resolve(), args.output.resolve()
    if output == snapshot or snapshot in output.parents or output in snapshot.parents:
        raise ValueError('output_must_be_separate_from_snapshot')
    payload, records, model, bootstrap, prepared, summary, loaded = load_snapshot(snapshot, args.model, args.bootstrap)
    predictions = [evaluate_record(rec, prepared[rid], model['adapter'], bootstrap) for rid, rec in sorted(records.items())]
    report = dict(schema='neeeva.personal-singing-offline-evaluation.v1', execution_authorized=False,
                  dataset_revision=payload['dataset_revision'], model_id=model['model_id'], recording_count=len(records),
                  evaluation_scope='same_data_training_diagnostics_with_optional_exploratory_refit_holdouts',
                  score_name='uncalibrated_score', snapshot_path=str(snapshot), input_sha256=loaded,
                  policy=dict(proposals.POLICY), threshold_tuning=False, random_frame_split=False,
                  model_stage=model['model_stage'], model_adapter_sha256=proposals.digest(model['adapter']),
                  bootstrap_adapter_sha256=proposals.digest(bootstrap),
                  bootstrap_provenance=bootstrap.get('experiment_provenance'),
                  dataset_summary=summary, coverage=coverage_report(records, summary),
                  same_data=dict(scope='training_batch_diagnostics_not_held_out', aggregate=aggregate(predictions), predictions=predictions))
    report['native_grid_oracle'] = grid_oracle(records, prepared)
    if args.group_holdout:
        report['group_holdout'] = holdout(records, prepared, bootstrap, True)
    if args.recording_holdout:
        report['recording_holdout'] = holdout(records, prepared, bootstrap, False)
    if args.weighting_ablations:
        report['weighting_ablations'] = weighting_ablations(records, prepared, bootstrap, args.recording_holdout)
    report['diagnostic_findings'] = diagnostic_findings(report, prepared)
    if any(file_sha(path) != expected for path, expected in loaded.items()):
        raise ValueError('frozen_input_changed_during_evaluation')
    report['frozen_inputs_unchanged'] = True
    write_outputs(output, report)
    print(json.dumps(dict(output=str(output), model_id=model['model_id'],
                          same_data=report['same_data']['aggregate']), ensure_ascii=False, allow_nan=False))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
