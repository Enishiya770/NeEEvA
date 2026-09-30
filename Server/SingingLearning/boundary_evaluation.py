"""Fixed-policy evaluation of learned local onset/offset transition evidence.

The local refiner is the predeclared candidate. Existing sequence decoding and
independent emissions share its unchanged fitted sequence head as controls.
Human originals remain read-only; no held-out variant selection is performed.
"""
from __future__ import annotations

import time
import numpy as np

from . import pipeline, proposals, sequence_evaluation as common, temporal_fusion as fusion

SCHEMA = 'neeeva.local-boundary-evaluation.v1'
FALSE_POSITIVE_PARTS = ('leading', 'trailing', 'internal', 'isolated')
FALSE_POSITIVE_LABELS = ('speech', 'environment')


def _overlap(a, b, start, end):
    return max(0, min(b, end) - max(a, start))


def measure(recording, edges, scores, mask, *, retain_arrays=False):
    row = common.measure(recording, edges, scores, mask, retain_arrays=retain_arrays)
    annotation = recording.get('annotation') or {}
    gold = [r for r in annotation.get('regions', []) if r['label'] == 'singing'] if (
        annotation.get('confirmed') is True and annotation.get('source_kind') == 'user') else []
    known_negative = [r for r in pipeline.known_regions(recording) if r['label'] in FALSE_POSITIVE_LABELS]
    parts = {part + '_' + label + '_fp_seconds': 0. for part in FALSE_POSITIVE_PARTS for label in FALSE_POSITIVE_LABELS}
    for prediction in row['predicted_ranges']:
        start, end = prediction['start_sample'], prediction['end_sample']
        overlapping = [g for g in gold if _overlap(start, end, g['start_sample'], g['end_sample'])]
        if overlapping:
            first = min(g['start_sample'] for g in overlapping)
            last = max(g['end_sample'] for g in overlapping)
            intervals = [('leading', start, min(end, first)), ('internal', max(start, first), min(end, last)),
                         ('trailing', max(start, last), end)]
        else:
            intervals = [('isolated', start, end)]
        for part, left, right in intervals:
            if right <= left:
                continue
            for region in known_negative:
                parts[part + '_' + region['label'] + '_fp_seconds'] += (
                    _overlap(left, right, region['start_sample'], region['end_sample']) / recording['sample_rate'])
    if abs(sum(parts.values()) - row['metrics']['fp_seconds']) > 1e-8:
        raise ValueError('local_boundary_false_positive_partition_mismatch')
    row['false_positive_parts'] = dict(parts,
        scope='relative to raw human singing inside each selected component; no-gold components are isolated; unknown/tolerance excluded')
    eof = [g for g in row['boundaries']['gold_intervals'] if g['gold_end_sample'] == recording['sample_count']]
    row['boundaries'].update(eof_gold_count=len(eof), eof_missed_gold_count=sum(g['completely_missed'] for g in eof),
        eof_scope='recording end is censored, not evidence of a true singing-to-nonsinging event')
    return row


def aggregate(rows):
    result = common.aggregate(rows)
    result['false_positive_parts'] = {
        part + '_' + label + '_fp_seconds': sum(row['false_positive_parts'][part + '_' + label + '_fp_seconds'] for row in rows)
        for part in FALSE_POSITIVE_PARTS for label in FALSE_POSITIVE_LABELS}
    for key in ('eof_gold_count', 'eof_missed_gold_count'):
        result['boundaries'][key] = sum(row['boundaries'][key] for row in rows)
    return result


def evaluate_guard(summary, references, *, fold_count, complete=True, training_available=True):
    # Deliberately unchanged acceptance rules. A prior model's zero splits from
    # excessive merging is a comparison target, not a new immutable constraint.
    guard = common.evaluate_guard(summary, references, fold_count=fold_count, complete=complete)
    if not training_available:
        guard['eligibility_reasons'].append('local_boundary_training_unavailable')
        guard['teaching_eligible'] = False
    return guard


def evaluate_prepared(records, prepared, native_fft, acoustic, bootstrap_adapter, groups, *,
                      metadata_groups=None, progress=None, retain_predictions=True):
    from . import boundary_model as boundary, sequence_model as sequence
    ids = sorted(prepared)
    if not ids or any(set(items) != set(ids) for items in (native_fft, acoustic)):
        raise ValueError('boundary_evaluation_recording_set_mismatch')
    if any(rid not in records or not pipeline.known_regions(records[rid]) for rid in ids):
        raise ValueError('boundary_evaluation_requires_confirmed_user_known_labels')
    common.validate_groups(groups, ids)
    if metadata_groups is not None:
        common.validate_groups(metadata_groups, ids)
    started, timings = time.perf_counter(), []

    def fit(name, training_ids):
        before = time.perf_counter()
        candidate = (boundary.fit_candidate(prepared, training_ids, speech_cost=2.) if name == 'boundary' else
            fusion.fit_candidate(native_fft if name == 'native_fft_speech2' else acoustic, training_ids,
                                 speech_cost=2. if name == 'native_fft_speech2' else 1.))
        timings.append(dict(operation='fit', variant=name, seconds=time.perf_counter() - before, train_count=len(training_ids)))
        return candidate

    def predict(name, candidate, test_ids):
        rows = []
        for rid in test_ids:
            before = time.perf_counter()
            extra = None
            if name in ('boundary', 'sequence', 'independent'):
                item = prepared[rid]
                if name == 'boundary':
                    extra = boundary.predict(candidate['head'], item['features'], item['edges'],
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
            row = measure(records[rid], item['edges'], scores, selected, retain_arrays=retain_predictions)
            if name == 'boundary' and extra is not None and retain_predictions:
                row['local_evidence'] = {key: np.asarray(extra[key]).tolist() for key in
                    ('onset_scores', 'offset_scores', 'onset_gates', 'offset_gates', 'seed_selected_mask') if key in extra}
            rows.append(row)
        return rows

    def folds(units, scope):
        held = {name: [] for name in ('boundary', 'sequence', 'independent', 'native_fft_speech2', 'panns_uniform')}
        details = []
        for component, members in sorted(units.items()):
            test_ids = sorted(set(members) & set(ids))
            if not test_ids:
                continue
            training_ids = sorted(set(ids) - set(test_ids))
            row = dict(component_id=component, train_ids=training_ids, test_ids=test_ids, variants={})
            for name in ('boundary', 'native_fft_speech2', 'panns_uniform'):
                variants = ('boundary', 'sequence', 'independent') if name == 'boundary' else (name,)
                try:
                    candidate = fit(name, training_ids)
                    for variant in variants:
                        predictions = predict(variant, candidate, test_ids)
                        held[variant].extend(predictions)
                        row['variants'][variant] = dict(status='ok', summary=aggregate(predictions),
                            train_source_bindings_sha256=candidate['source_bindings_sha256'])
                        if variant == 'boundary':
                            row['variants'][variant]['training_available'] = candidate.get('boundary_training_available') is True
                except (ValueError, np.linalg.LinAlgError) as exc:
                    for variant in variants:
                        row['variants'][variant] = dict(status='unavailable', reason=str(exc))
            details.append(row)
            if progress:
                progress(dict(event='fold_completed', scope=scope, completed=len(details), total=len(units), component_id=component))
        return dict(folds=details, fold_count=len(details), aggregate={name: aggregate(rows) for name, rows in held.items()},
                    complete={name: len(rows) == len(ids) for name, rows in held.items()},
                    predictions=held if retain_predictions else None)

    candidate = fit('boundary', ids)
    same_data = {name: aggregate(predict(name, candidate, ids)) for name in ('boundary', 'sequence', 'independent')}
    fixed = aggregate(predict('bootstrap_proposal', None, ids))
    held = folds(groups, 'exact_prefix_component_holdout')
    metadata = folds(metadata_groups, 'metadata_group_holdout') if metadata_groups else None
    references = dict(bootstrap_proposal=fixed, panns_uniform=held['aggregate']['panns_uniform'],
                      native_fft_speech2=held['aggregate']['native_fft_speech2'])
    complete = all(held['complete'].values())
    training_available = candidate.get('boundary_training_available') is True and all(
        fold['variants'].get('boundary', {}).get('training_available') is True for fold in held['folds'])
    guard = evaluate_guard(held['aggregate']['boundary'], references, fold_count=held['fold_count'],
                           complete=complete, training_available=training_available)
    candidate.update(teaching_eligible=guard['teaching_eligible'], eligibility_reasons=guard['eligibility_reasons'], evaluation_guard=guard)
    local, prior = held['aggregate']['boundary'], held['aggregate']['sequence']
    sequence_guard = common.evaluate_guard(prior, references, fold_count=held['fold_count'], complete=complete)
    sequence_candidate = candidate['sequence_candidate']
    sequence_candidate.update(teaching_eligible=sequence_guard['teaching_eligible'],
                              eligibility_reasons=sequence_guard['eligibility_reasons'], evaluation_guard=sequence_guard)
    deltas = dict(scope='same-fold unchanged sequence comparator; diagnostic only, not candidate selection or extra adoption constraint',
        metrics={key: local['metrics'][key] - prior['metrics'][key] for key in ('fn_seconds', 'speech_fp_seconds', 'environment_fp_seconds')},
        boundaries={key: local['boundaries'][key] - prior['boundaries'][key] for key in
                    ('internal_fragment_splits', 'short_missed_gold_count', 'completely_missed_gold_count')})
    summaries = dict(candidate=local, sequence=prior, independent=held['aggregate']['independent'], **references)
    return dict(schema=SCHEMA, status='eligible' if guard['teaching_eligible'] else 'shadow_rejected',
        teaching_eligible=guard['teaching_eligible'], eligibility_reasons=guard['eligibility_reasons'],
        execution_authorized=False, production_eligible=False, selected_variant='boundary',
        candidates={'boundary': candidate, 'sequence': sequence_candidate}, candidate=candidate,
        guards={'boundary': guard, 'sequence': sequence_guard}, guard=guard,
        summaries=summaries, same_data=same_data, exact_prefix_component_holdout=held, metadata_group_holdout=metadata,
        fixed_references={'bootstrap_proposal': fixed}, folds=held['folds'], fold_count=held['fold_count'], complete=complete,
        recording_count=len(ids), comparative_deltas_vs_sequence=deltas, boundary_training_complete=training_available,
        timing=dict(total_seconds=time.perf_counter() - started, operations=timings,
            feature_array_bytes=sum(item['features'].nbytes + item['edges'].nbytes for item in prepared.values()),
            memory_scope='retained fine arrays; excludes optimizer/local-window copies and process memory'),
        limitations=[
            'Local boundary evidence uses lookahead; frozen PANNs uses the full recording and final decoding may revise earlier cells.',
            'Source-component folds are same-user/session development diagnostics, not cross-session/user acceptance validation.',
            'The local refiner is fixed in advance; sequence and independent are controls, not candidates selected by test performance.',
            'Unknown labels and tolerance bands never supply negative labels; recording EOF is censored rather than an offset event.',
            'The existing three-reference adoption guard is unchanged. Prior sequence continuity/recall deltas are separately reported.',
            'No threshold sweep, manual source-label revision, deployment or playback authorization occurs here.'])
