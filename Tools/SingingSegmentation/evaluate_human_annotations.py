"""Score immutable saved acoustic candidates against source-bound human labels.

No inference, network, playback, model training or audio editing. Unlabelled
audio is unknown unless a hash-bound human confirmation explicitly labels its
binary singing complement. All durations use integer original WAV samples.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import io
import json
from pathlib import Path
import sys
import wave

import numpy as np

THRESHOLDS = (.1, .2, .5)
SCOPES = ('nominal', 'confident', 'nominal_conflict_excluded', 'confident_conflict_excluded')


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))


def require(condition, message):
    if not condition:
        raise ValueError(message)


def ranges(mask):
    edges = np.diff(np.r_[False, np.asarray(mask, dtype=bool), False].astype(np.int8))
    return [[int(a), int(b)] for a, b in zip(np.flatnonzero(edges == 1), np.flatnonzero(edges == -1))]


def span_mask(count, spans):
    result = np.zeros(count, dtype=bool)
    for start, end in spans:
        require(type(start) is int and type(end) is int and 0 <= start <= end <= count, 'invalid_sample_range')
        result[start:end] = True
    return result


def validate_recording(record):
    source = record['source']
    n, sr = source['sample_count'], source['sample_rate']
    require(type(n) is int and n > 0 and type(sr) is int and sr > 0, 'invalid_source_dimensions')
    require(record['coordinate_origin_sample'] == 0 and record['sample_range_convention'] == 'half-open', 'unsupported_coordinate_system')
    previous = 0
    for label in record['annotations']:
        start, end = label['start_sample'], label['end_sample']
        require(type(start) is int and type(end) is int and previous <= start < end <= n, 'overlap_or_invalid_human_range')
        require(label['label'] in ('singing', 'speech', 'uncertain'), 'unknown_human_label')
        u = label['boundary_uncertainty']
        require(u['kind'] == 'human_tolerance_band_not_statistical_ci', 'invalid_human_uncertainty')
        for name in ('start_min_sample', 'start_max_sample', 'end_min_sample', 'end_max_sample'):
            require(type(u[name]) is int, 'non_integer_boundary')
        require(0 <= u['start_min_sample'] <= start <= u['start_max_sample'] <= n and
                0 <= u['end_min_sample'] <= end <= u['end_max_sample'] <= n, 'invalid_boundary_band')
        previous = end
    labelled = span_mask(n, [(a['start_sample'], a['end_sample']) for a in record['annotations']])
    require(ranges(~labelled) == record['unlabelled_sample_ranges'], 'unlabelled_ranges_do_not_match_annotations')


def validate_overrides(record, overrides):
    occupied = np.zeros(record['source']['sample_count'], dtype=bool)
    for item in overrides:
        require(item['recording_id'] == record.get('recording_id') and all(
            item['source'][key] == record['source'][key] for key in ('audio_sha256', 'sample_rate', 'sample_count')),
            'override_source_mismatch')
        require(item['label'] == 'non_singing' and item['subtype'] == 'environment_sound' and
                item['origin'] == 'explicit_user_conversation', 'unsupported_override')
        start, end = item['start_sample'], item['end_sample']
        mask = span_mask(len(occupied), [(start, end)])
        require(start < end and not (mask & occupied).any(), 'empty_or_overlapping_override')
        occupied |= mask
    return occupied


def truth_masks(record, confirmed=False, overrides=()):
    validate_recording(record)
    n = record['source']['sample_count']
    by_label = {name: span_mask(n, [(a['start_sample'], a['end_sample']) for a in record['annotations'] if a['label'] == name])
                for name in ('singing', 'speech', 'uncertain')}
    bands = {}
    for name in ('singing', 'speech'):
        spans = []
        for a in record['annotations']:
            if a['label'] != name:
                continue
            u = a['boundary_uncertainty']
            spans.extend([(u['start_min_sample'], u['start_max_sample']), (u['end_min_sample'], u['end_max_sample'])])
        bands[name] = span_mask(n, spans)
    known = ~by_label['uncertain'] if confirmed else by_label['singing'] | by_label['speech']
    annotation_blank = ~np.logical_or.reduce(list(by_label.values()))
    confident = known & ~bands['singing']
    if not confirmed:
        confident &= ~bands['speech']
    environment = validate_overrides(record, overrides)
    # New explicit labels supersede old nominal labels and restore known
    # samples inside an old tolerance band. They do not establish a new onset.
    by_label['singing'][environment] = False
    by_label['speech'][environment] = False
    known[environment] = True
    confident[environment] = True
    return {'singing': by_label['singing'], 'explicit_speech': by_label['speech'],
            'confirmed_environment': environment,
            'nominal': known, 'confident': confident, 'speech_bands': bands['speech'],
            'singing_bands': bands['singing'], 'annotation_blank': annotation_blank}


def measurements(prediction, masks, sr, scope):
    known, positive = masks[scope], masks['singing']
    negative = known & ~positive
    explicit = negative & masks['explicit_speech']
    if scope.startswith('confident'):
        explicit &= ~masks['speech_bands']
    other = negative & ~masks['explicit_speech']
    if scope.startswith('confident'):
        other &= ~masks['speech_bands']
    seconds = lambda mask: int(np.count_nonzero(mask)) / sr
    return {'tp_seconds': seconds(prediction & known & positive),
            'fn_seconds': seconds(~prediction & known & positive),
            'fp_seconds': seconds(prediction & negative), 'tn_seconds': seconds(~prediction & negative),
            'explicit_speech_seconds': seconds(explicit), 'explicit_speech_fp_seconds': seconds(explicit & prediction),
            'other_non_singing_seconds': seconds(other), 'other_non_singing_fp_seconds': seconds(other & prediction),
            'confirmed_environment_seconds': seconds(negative & masks['confirmed_environment']),
            'confirmed_environment_fp_seconds': seconds(prediction & negative & masks['confirmed_environment']),
            'excluded_or_unknown_seconds': seconds(~known), 'prediction_on_unknown_seconds': seconds(prediction & ~known)}


def rates(values):
    result = dict(values)
    tp, fp, fn, tn = (values[k + '_seconds'] for k in ('tp', 'fp', 'fn', 'tn'))
    def divide(a, b):
        return a / b if b else None
    result.update(singing_seconds=tp + fn, non_singing_seconds=fp + tn,
                  recall=divide(tp, tp + fn), precision=divide(tp, tp + fp),
                  false_positive_rate=divide(fp, fp + tn), f1=divide(2 * tp, 2 * tp + fp + fn),
                  explicit_speech_false_positive_rate=divide(values['explicit_speech_fp_seconds'], values['explicit_speech_seconds']),
                  other_non_singing_false_positive_rate=divide(values['other_non_singing_fp_seconds'], values['other_non_singing_seconds']))
    return result


def boundary_matches(predicted_ranges, annotations, sr, conflict_mask=None, unresolved=()):
    result = []
    for annotation_index, a in enumerate(annotations):
        if a['label'] != 'singing':
            continue
        u = a['boundary_uncertainty']
        disputed = conflict_mask is not None and any(conflict_mask[max(0, lo):min(len(conflict_mask), hi + 1)].any()
            for lo, hi in ((u['start_min_sample'], u['start_max_sample']), (u['end_min_sample'], u['end_max_sample'])))
        pending = [item for item in unresolved if item['annotation_index'] == annotation_index]
        pending_edges = {item['boundary'] for item in pending}
        both = [i for i, (x, y) in enumerate(predicted_ranges)
                if u['start_min_sample'] <= x <= u['start_max_sample'] and u['end_min_sample'] <= y <= u['end_max_sample']]
        overlapping = [(i, max(0, min(y, a['end_sample']) - max(x, a['start_sample'])))
                       for i, (x, y) in enumerate(predicted_ranges)]
        overlapping = [(i, overlap) for i, overlap in overlapping if overlap > 0]
        best = max(overlapping, key=lambda item: item[1])[0] if overlapping else None
        best_range = predicted_ranges[best] if best is not None else None
        result.append({'human_range_samples': [a['start_sample'], a['end_sample']],
                       'human_tolerance': u, 'boundary_truth_status': 'unresolved_human_boundary' if pending else 'unresolved_same_pcm_conflict' if disputed else 'usable',
                       'unresolved_boundaries': pending,
                       'both_endpoints_within_tolerance': None if disputed or pending else bool(both),
                       'provisional_raw_endpoint_comparison': None if pending else bool(both),
                       'matching_candidate_indices': [] if pending else both, 'overlapping_candidate_count': len(overlapping),
                       'largest_overlap_candidate': best_range,
                       'largest_overlap_start_error_seconds': (best_range[0] - a['start_sample']) / sr if best_range and 'start' not in pending_edges else None,
                       'largest_overlap_end_error_seconds': (best_range[1] - a['end_sample']) / sr if best_range and 'end' not in pending_edges else None,
                       'selection_scope': 'Largest overlap is a retrospective descriptive match, not a deployable candidate selector.'})
    return result


def validate_confirmation(annotations_path, confirmation_path, manifest_sha):
    document = read_json(annotations_path)
    if confirmation_path is None:
        return set(), None
    c = read_json(confirmation_path)
    require(c.get('confirmation_schema') == 'neeeva.speech-singing-human-confirmation.v1', 'unsupported_confirmation_schema')
    require(c['corrected_sha256'] == sha(annotations_path), 'confirmation_corrected_hash_mismatch')
    original = Path(confirmation_path).parent / c['original_file']
    require(sha(original) == c['original_sha256'], 'confirmation_original_hash_mismatch')
    require(c['dataset_id'] == document['dataset_id'] and c['source_manifest_sha256'] == manifest_sha == document['source_manifest_sha256'], 'confirmation_dataset_mismatch')
    require(c['provenance']['kind'] == 'human_conversation_confirmation' and c['provenance']['author'] == 'user' and c['provenance']['statements'], 'confirmation_not_human')
    coverage = c['binary_coverage']
    require(coverage['complement_label'] == 'non_singing' and coverage['complement_is_not_necessarily_speech'] is True and
            coverage['singing_intervals'] == 'singing annotations in the corrected document', 'unsupported_binary_confirmation')
    ids = coverage['reviewed_recording_ids']
    require(len(set(ids)) == len(ids) and set(ids) <= {r['recording_id'] for r in document['recordings']}, 'confirmation_recording_ids_invalid')
    records = {r['recording_id']: r for r in document['recordings']}
    overrides = c.get('sample_label_overrides', [])
    require(all(item.get('recording_id') in records for item in overrides), 'override_recording_missing')
    for key, record in records.items():
        subset = [item for item in overrides if item['recording_id'] == key]
        if subset:
            validate_overrides(record, subset)
        for item in subset:
            require(type(item.get('statement_index')) is int and 0 <= item['statement_index'] < len(c['provenance']['statements']),
                    'override_human_statement_missing')
    seen = set()
    for item in c.get('unresolved_singing_boundaries', []):
        key, index = item['recording_id'], item['annotation_index']
        require(key in records and type(index) is int and 0 <= index < len(records[key]['annotations']) and
                records[key]['annotations'][index]['label'] == 'singing' and item['boundary'] in ('start', 'end') and
                isinstance(item.get('reason'), str) and bool(item['reason'].strip()), 'invalid_unresolved_boundary')
        identity = (key, index, item['boundary'])
        require(identity not in seen, 'duplicate_unresolved_boundary')
        seen.add(identity)
        if 'lower_bound_sample' in item:
            lower = item['lower_bound_sample']
            require(type(lower) is int and 0 <= lower <= records[key]['source']['sample_count'], 'invalid_boundary_lower_bound')
    return set(ids), {'file': str(Path(confirmation_path).resolve()), 'sha256': sha(confirmation_path),
                     'original_file': str(original.resolve()), 'original_sha256': sha(original), 'content': c}


def verify_scores(path, expected_source):
    r = read_json(path)
    for key in ('audio_sha256', 'sample_rate', 'sample_count'):
        require(r['source'][key] == expected_source[key], 'score_source_mismatch:' + str(path))
    require(r['status'] == 'candidate' and r['execution_authorized'] is False and r['supported_range'] is None, 'unexpected_execution_authority')
    timeline, metadata = r['timeline'], r['model_metadata']
    scores = np.asarray(timeline['scores'], dtype=np.float64)
    require(scores.ndim == 2 and scores.shape[1] == len(timeline['columns']) and np.isfinite(scores).all() and
            ((scores >= 0) & (scores <= 1)).all(), 'invalid_saved_scores')
    n, sr = expected_source['sample_count'], expected_source['sample_rate']
    cell, rate = timeline['analysis_cell_samples'], metadata['analysis_sample_rate']
    require(type(cell) is int and cell > 0 and type(rate) is int and rate > 0 and timeline['coordinate_sample_rate'] == sr, 'missing_time_support')
    edges = [min(n, (i * cell * sr + rate // 2) // rate) for i in range(len(scores) + 1)]
    require(edges[0] == 0 and edges[-1] == n and all(b > a for a, b in zip(edges, edges[1:])) and
            edges[:-1] == timeline['source_start_samples'], 'saved_time_grid_mismatch')
    raw_path = Path(path).with_suffix('.framewise.npz')
    with np.load(raw_path, allow_pickle=False) as raw:
        frames, labels = raw['scores'], list(raw['labels'])
        require(int(raw['analysis_sample_rate']) == rate, 'npz_sample_rate_mismatch')
        selected = frames[:, [labels.index(k) for k in timeline['columns']]]
        if metadata.get('interpolation') == 'nearest-repeat-32-then-last-cell-padding':
            require(cell == 32 * int(raw['hop_samples']), 'npz_panns_hop_mismatch')
            native = selected[::32]
            for start in range(0, len(frames), 32):
                require(np.array_equal(selected[start:start + 32], np.repeat(selected[start:start + 1], min(32, len(frames) - start), axis=0)), 'panns_repeat_mismatch')
        else:
            require(cell == int(raw['hop_samples']), 'npz_native_hop_mismatch')
            native = selected
        require(np.array_equal(native, scores), 'npz_json_scores_mismatch')
    return r, scores, edges, {'json_file': str(Path(path).resolve()), 'json_sha256': sha(path),
                              'npz_file': str(raw_path.resolve()), 'npz_sha256': sha(raw_path),
                              'source_verified': True, 'native_scores_match_npz': True,
                              'model_version': r['model_version'], 'provider': r['provider'], 'metadata': metadata}


def write_json(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding='utf-8')


def audit_containment(records, manifest, confirmed, overrides=()):
    """Exact whole-recording PCM containment only; no approximate audio matching."""
    pcm, masks, source = {}, {}, {}
    for r in records:
        key = r['recording_id']; raw = Path(manifest[key]['wav']).read_bytes()
        require(hashlib.sha256(raw).hexdigest() == r['source']['audio_sha256'] == manifest[key]['sha256'], 'alias_source_hash_mismatch')
        with wave.open(io.BytesIO(raw), 'rb') as wav:
            require(wav.getnchannels() == 1 and wav.getsampwidth() == 2 and wav.getframerate() == r['source']['sample_rate'] and
                    wav.getnframes() == r['source']['sample_count'], 'alias_source_dimensions_mismatch')
            pcm[key] = wav.readframes(wav.getnframes())
        masks[key] = truth_masks(r, key in confirmed, [item for item in overrides if item['recording_id'] == key]); source[key] = r['source']
    conflicts = {key: np.zeros(s['sample_count'], dtype=bool) for key, s in source.items()}
    aliases, contained = [], set()
    for small in sorted(pcm, key=lambda k: (len(pcm[k]), k)):
        for large in sorted(pcm):
            if len(pcm[small]) >= len(pcm[large]) or source[small]['sample_rate'] != source[large]['sample_rate']:
                continue
            offset = pcm[large].find(pcm[small])
            if offset < 0 or offset % 2:
                continue
            offset //= 2; count = source[small]['sample_count']
            a, b = masks[small], masks[large]
            conflict = a['nominal'] & b['nominal'][offset:offset + count] & (a['singing'] != b['singing'][offset:offset + count])
            confident_conflict = conflict & a['confident'] & b['confident'][offset:offset + count]
            conflicts[small] |= conflict; conflicts[large][offset:offset + count] |= conflict
            contained.add(small)
            aliases.append({'contained_recording': small, 'container_recording': large, 'container_offset_sample': offset,
                'sample_count': count, 'sample_rate': source[small]['sample_rate'], 'exact_pcm_bytes_equal': True,
                'contained_pcm_sha256': hashlib.sha256(pcm[small]).hexdigest(),
                'nominal_disagreement_ranges_in_contained_samples': ranges(conflict),
                'confident_on_both_copies_disagreement_ranges_in_contained_samples': ranges(confident_conflict),
                'nominal_disagreement_seconds_per_copy': int(conflict.sum()) / source[small]['sample_rate'],
                'confident_on_both_copies_disagreement_seconds_per_copy': int(confident_conflict.sum()) / source[small]['sample_rate']})
    return conflicts, {'method': 'Exact whole WAV PCM byte containment at sample-aligned offsets. No approximate matches and no speaker-independent assumption.',
        'aliases': aliases, 'file_weighted_seconds': sum(s['sample_count'] / s['sample_rate'] for s in source.values()),
        'seconds_after_removing_whole_contained_duplicates': sum(s['sample_count'] / s['sample_rate'] for key, s in source.items() if key not in contained),
        'unique_duration_scope': 'Whole-contained-duplicate accounting only; remaining partial overlaps are not ruled out.',
        'conflict_policy': 'Preserve annotations. Secondary/primary-conclusion masks remove every nominal disagreement from BOTH source copies, after existing boundary masks, without double subtraction.'}


def run(args):
    if args.dependencies:
        sys.path.insert(0, str(args.dependencies.resolve()))
    module_path = Path(__file__).resolve().parents[2] / 'Server' / 'SenseVoice'
    sys.path.insert(0, str(module_path))
    from singing_segmentation import CandidatePolicy, SINGING_FAMILY, family_max_scores, candidate_timeline
    document = read_json(args.annotations)
    require(document['annotation_schema'] == 'neeeva.speech-singing-human.v1' and
            document['provenance']['kind'] == 'human_manual' and document['provenance']['model_candidates_shown'] is False and
            document['provenance']['labels_are_not_model_predictions'] is True, 'unsupported_annotation_provenance')
    manifest_sha = sha(args.manifest)
    require(document['source_manifest_sha256'] == manifest_sha, 'annotation_manifest_hash_mismatch')
    manifest = read_json(args.manifest)
    manifest_by_id = {r['id']: r for r in manifest}
    records = document['recordings']
    require(len(manifest_by_id) == len(manifest) and len({r['recording_id'] for r in records}) == len(records) and
            {r['recording_id'] for r in records} == set(manifest_by_id), 'manifest_recording_set_mismatch')
    confirmed, confirmation = validate_confirmation(args.annotations, args.confirmation, manifest_sha)
    overrides = confirmation['content'].get('sample_label_overrides', []) if confirmation else []
    unresolved = confirmation['content'].get('unresolved_singing_boundaries', []) if confirmation else []
    conflicts, containment = audit_containment(records, manifest_by_id, confirmed, overrides)
    source_audit, model_audit, details, coverage = [], [], [], []
    for recording in records:
        identity, source = recording['recording_id'], recording['source']
        entry = manifest_by_id[identity]
        raw = Path(entry['wav']).read_bytes()
        digest = hashlib.sha256(raw).hexdigest()
        require(digest == entry['sha256'] == source['audio_sha256'], 'original_wav_hash_mismatch:' + identity)
        with wave.open(io.BytesIO(raw), 'rb') as wav:
            require(wav.getframerate() == source['sample_rate'] and wav.getnframes() == source['sample_count'] and wav.getsampwidth() == 2 and wav.getnchannels() == 1,
                    'original_wav_dimensions_mismatch:' + identity)
            audio_meta = {'channels': wav.getnchannels(), 'sample_width_bytes': wav.getsampwidth(), 'sample_rate': wav.getframerate(), 'sample_count': wav.getnframes()}
        source_audit.append({'id': identity, 'wav': str(Path(entry['wav']).resolve()), 'sha256': digest, 'bytes': len(raw), **audio_meta})
        masks = truth_masks(recording, identity in confirmed, [item for item in overrides if item['recording_id'] == identity])
        masks['nominal_conflict_excluded'] = masks['nominal'] & ~conflicts[identity]
        masks['confident_conflict_excluded'] = masks['confident'] & ~conflicts[identity]
        sr, n = source['sample_rate'], source['sample_count']
        coverage.append({'id': identity, 'source': source, 'duration_seconds': n / sr, 'document_review_status': recording['review_status'],
                         'binary_complement_human_confirmed': identity in confirmed,
                         **{key + '_seconds': int(mask.sum()) / sr for key, mask in masks.items()},
                         'original_blank_ranges_samples': recording['unlabelled_sample_ranges'],
                         'confident_exclusion_ranges_samples': ranges(~masks['confident']),
                         'same_pcm_conflict_ranges_samples': ranges(conflicts[identity]),
                         'extra_conflict_exclusion_after_human_band_seconds': int((masks['confident'] & conflicts[identity]).sum()) / sr})
        for model_name, directory in args.results:
            path = directory / (identity + '.json')
            original, scores, edges, audit = verify_scores(path, source)
            require(original['evaluation_sample']['id'] == identity and original['evaluation_sample']['sha256'] == digest, 'model_evaluation_manifest_mismatch')
            model_audit.append({'model': model_name, 'id': identity, **audit})
            _, singing = family_max_scores(scores, original['timeline']['columns'])
            for threshold in THRESHOLDS:
                raw_prediction = np.repeat(singing >= threshold, np.diff(edges))
                policy = CandidatePolicy(active_threshold=threshold, score_aggregation='family_max')
                segments, _ = candidate_timeline(scores, original['timeline']['columns'], original['source'], original['model_version'], policy,
                    cell_analysis_samples=original['timeline']['analysis_cell_samples'], analysis_rate=original['model_metadata']['analysis_sample_rate'],
                    context_support_seconds=original['model_metadata'].get('global_context_seconds') or original['model_metadata']['theoretical_receptive_field_seconds'])
                policy_prediction = span_mask(n, [(s['start_sample'], s['end_sample']) for s in segments if s['label'] == 'singing'])
                for mode, prediction in [('family_threshold', raw_prediction), ('existing_candidate_policy_singing', policy_prediction)]:
                    spans = ranges(prediction)
                    details.append({'model': model_name, 'id': identity, 'threshold': threshold, 'mode': mode,
                        'group': 'contains_singing' if masks['singing'].any() else 'no_singing',
                        'provider': original['provider'], 'policy': None if mode == 'family_threshold' else policy.__dict__,
                        'prediction_ranges_samples': spans, 'prediction_ranges_seconds': [[a / sr, b / sr] for a, b in spans],
                        'boundary_matches': boundary_matches(spans, recording['annotations'], sr, conflicts[identity],
                            [item for item in unresolved if item['recording_id'] == identity]),
                        'metrics': {scope: rates(measurements(prediction, masks, sr, scope)) for scope in SCOPES}})
    aggregates = []
    for model_name, _ in args.results:
        for threshold in THRESHOLDS:
            for mode in ('family_threshold', 'existing_candidate_policy_singing'):
                selected = [r for r in details if r['model'] == model_name and r['threshold'] == threshold and r['mode'] == mode]
                for group in ('all', 'contains_singing', 'no_singing'):
                    rows = selected if group == 'all' else [r for r in selected if r['group'] == group]
                    item = {'model': model_name, 'threshold': threshold, 'mode': mode, 'group': group, 'metrics': {}}
                    for scope in SCOPES:
                        keys = ('tp_seconds', 'fn_seconds', 'fp_seconds', 'tn_seconds', 'explicit_speech_seconds', 'explicit_speech_fp_seconds',
                                'other_non_singing_seconds', 'other_non_singing_fp_seconds', 'excluded_or_unknown_seconds', 'prediction_on_unknown_seconds')
                        keys += ('confirmed_environment_seconds', 'confirmed_environment_fp_seconds')
                        item['metrics'][scope] = rates({k: sum(r['metrics'][scope][k] for r in rows) for k in keys})
                    matches = [b for r in rows for b in r['boundary_matches']]
                    item['human_singing_intervals'] = len(matches)
                    item['usable_boundary_intervals'] = sum(b['boundary_truth_status'] == 'usable' for b in matches)
                    item['unresolved_boundary_intervals'] = sum(b['boundary_truth_status'] != 'usable' for b in matches)
                    item['intervals_with_one_contiguous_candidate_matching_both_ends'] = sum(b['both_endpoints_within_tolerance'] is True for b in matches)
                    aggregates.append(item)
    config = {'thresholds': THRESHOLDS, 'family': SINGING_FAMILY, 'aggregation': 'maximum independent sigmoid, not a union or calibrated probability',
              'time_weighting': 'Exact original WAV sample intersections; piecewise-constant native score cells, no interpolation.',
              'primary_mask': 'confident_conflict_excluded: known binary labels excluding singing tolerance bands and exact same-PCM contradictory labels on both copies. Unconfirmed blanks/explicit uncertain labels remain unknown.',
              'override_policy': 'Source-bound explicit user environment labels supersede original labels and restore confident known samples within that exact span only. An unresolved onset is not replaced with the end of an override.',
              'candidate_policy': CandidatePolicy(score_aggregation='family_max').__dict__,
              'notes': ['Nominal labels are reported separately from confident interiors.',
                        'Non-singing includes speech, pauses and noise. No complete speech truth is inferred.',
                        'FP seconds mean predicted singing over human-confirmed non-singing, not measured speech contamination of a delivered crop.',
                        'Single-person, seven-source development batch; metrics are descriptive, not held-out/generalization estimates.',
                        'No scores were fitted, no best threshold was selected, and no model inference or execution occurred.',
                        'A matching candidate interval is only an offline boundary comparison, not production crop verification.']}
    report = {'schema': 'neeeva.human-acoustic-evaluation.v1', 'configuration': config, 'annotation_complete_flag': document['complete'],
              'sample_label_overrides': overrides, 'unresolved_singing_boundaries': unresolved,
              'coverage': coverage, 'exact_pcm_containment': containment, 'aggregate': aggregates, 'per_recording': details, 'execution_authorized': False}
    output = args.output; output.mkdir(parents=True, exist_ok=True)
    require(not (output / 'evaluation.json').exists(), 'evaluation_already_exists_use_new_output_directory')
    provenance = {'script': str(Path(__file__).resolve()), 'script_sha256': sha(__file__),
                  'segmentation_module_sha256': sha(module_path / 'singing_segmentation.py'), 'python': sys.version, 'numpy': np.__version__,
                  'argv': sys.argv, 'annotations': str(args.annotations.resolve()), 'annotation_sha256': sha(args.annotations),
                  'manifest': str(args.manifest.resolve()), 'manifest_sha256': manifest_sha, 'confirmation': confirmation,
                  'sources': source_audit, 'saved_models': model_audit, 'exact_pcm_containment': containment, 'configuration': config}
    write_json(output / 'evaluation.json', report)
    write_json(output / 'verification.json', provenance)
    for name, rows in [('aggregate.csv', aggregates), ('per-recording.csv', details)]:
        flat = [{**{k: r[k] for k in ('model', 'threshold', 'mode', 'group')}, **({'id': r['id']} if 'id' in r else {}),
                 'scope': scope, **r['metrics'][scope]} for r in rows for scope in SCOPES]
        with (output / name).open('w', newline='', encoding='utf-8-sig') as stream:
            writer = csv.DictWriter(stream, fieldnames=list(flat[0])); writer.writeheader(); writer.writerows(flat)
    write_report(output / 'report.md', report)
    print(json.dumps({'output': str(output), 'verified_wavs': len(source_audit), 'verified_saved_score_pairs': len(model_audit),
                      'configuration_count': len(aggregates), 'per_recording_rows': len(details), 'inference_requests': 0}, ensure_ascii=False))


def write_report(path, report):
    lines = ['# 人工标注离线评分', '', '只使用已经保存的模型分数；没有重新推理、训练、裁剪或播放。原标注 complete=false 保持不变；全片二元标签来自另存并绑定 SHA 的用户明确确认。', '',
             '歌唱家族分数是 Singing / Male singing / Female singing / Child singing 的最大独立 sigmoid，既不是校准概率，也不包含 Humming、Rapping 或 Music。主表排除人工歌唱边界误差带及同一PCM的标签矛盾区（两份均剔除）；原始文件加权名义结果、仅排边界带结果、各片明细与精确样本区间见 evaluation.json 和 CSV。', '',
             '| 模型 | 阈值 | 评分对象 | 歌唱召回 | 非歌唱误报率 | TP 秒 | FP 秒 | 漏掉歌唱秒 | F1 | 两端命中 |',
             '|---|---:|---|---:|---:|---:|---:|---:|---:|---:|']
    if report.get('sample_label_overrides'):
        lines[4] = '歌唱家族分数是 Singing / Male singing / Female singing / Child singing 的最大独立 sigmoid，既不是校准概率，也不包含 Humming、Rapping 或 Music。主表先应用源哈希绑定的人工校正，再排除其余人工边界误差带及剩余同源矛盾；原文件级名义结果、确定区间、各片明细与精确样本区间见 evaluation.json 和 CSV。'
    def pct(v): return '—' if v is None else f'{100*v:.2f}%'
    for row in report['aggregate']:
        if row['group'] != 'all': continue
        m = row['metrics']['confident_conflict_excluded']
        mode = '原始阈值' if row['mode'] == 'family_threshold' else '现有候选策略'
        lines.append(f"| {row['model']} | {row['threshold']} | {mode} | {pct(m['recall'])} | {pct(m['false_positive_rate'])} | {m['tp_seconds']:.3f} | {m['fp_seconds']:.3f} | {m['fn_seconds']:.3f} | {pct(m['f1'])} | {row['intervals_with_one_contiguous_candidate_matching_both_ends']}/{row['usable_boundary_intervals']}（另{row['unresolved_boundary_intervals']}未定） |")
    lines += ['', '原始标注没有被覆盖。精确 PCM 重复与冲突审计：', '', '```json', json.dumps(report['exact_pcm_containment'], ensure_ascii=False, indent=2), '```', '',
              '| 模型 | 阈值 | 组别 | 原始阈值 FP 秒 | 非歌唱分母秒 | 显式 speech FP 秒 | 其它非歌唱 FP 秒 | 已确认环境音 FP 秒 |',
              '|---|---:|---|---:|---:|---:|---:|---:|']
    for row in report['aggregate']:
        if row['mode'] != 'family_threshold' or row['group'] == 'all': continue
        m = row['metrics']['confident_conflict_excluded']
        lines.append(f"| {row['model']} | {row['threshold']} | {row['group']} | {m['fp_seconds']:.3f} | {m['non_singing_seconds']:.3f} | {m['explicit_speech_fp_seconds']:.3f} | {m['other_non_singing_fp_seconds']:.3f} | {m['confirmed_environment_fp_seconds']:.3f} |")
    if report.get('sample_label_overrides'):
        first = next(r for r in report['aggregate'] if r['group'] == 'all')['metrics']['confident_conflict_excluded']
        lines += ['', f"人工确认将 c8 和 c9 的 3.00–3.41 秒明确标为纯环境音。两份逐样本相同的区间共按文件计 {first['confirmed_environment_seconds']:.2f} 秒（唯一内容 0.41 秒），属于其它非歌唱的子集，不能相加重复计数。校正在原样本范围内应用；当前人工边界带仍保留。当前确定评分区间：歌唱 {first['singing_seconds']:.7f} 秒，非歌唱 {first['non_singing_seconds']:.7f} 秒，排除 {first['excluded_or_unknown_seconds']:.2f} 秒。同源标签冲突已解除。"]
        if report.get('unresolved_singing_boundaries'):
            lines += ['', 'c9 原来 3 秒的名义歌唱起点已被这条确认否定，但人工没有给出精确首个歌音。3.41 秒仅作为起点下界，不是自动推定的起点；该例两端验收仍为 unresolved，起点误差不计算，其余三份按原误差带验收。原标注 JSON 和首轮评分完整保留。']
        else:
            lines += ['', '本版人工已补充 c9 约 3.7 秒起唱，标注按 3.65–3.75 秒容差带处理，四份歌唱区间均可比较两端。波形起振与该范围相容，只作辅助核对；声音属于歌唱由人工确认，不由波形能量判定。本版没有把 3.41 秒环境音终点当作歌唱起点。以前的标注、确认和评分均保留。']
    lines += ['', '“原始阈值”仅比较 family-max≥阈值。现有候选策略另外要求比讲话家族高 0.15，且歌唱连续段至少 0.64 秒；mixed/uncertain 均未计作可用歌唱候选。两种结果不可混用。', '',
              '“两端命中”要求同一连续预测段的起点、终点分别落入该人工歌唱段的误差带，不拼接不同碎片的两个端点。每片报告同时保留最大重叠候选的误差；这是事后描述，不是可部署的选段算法。', '',
              'PANNs 的评分单元为 320 ms，其重复输出不构成 10 ms 精度。FrameMN06/MN10 为 40 ms 栅格，实际模型依赖整段 10 秒 chunk 上下文；栅格同样不证明裁切精度。', '',
              'c8 与 c9 的前缀 PCM 相同，但模型输入上下文不同：c9 后续有歌声，单独 c8 没有。chunk 上下文会使同一前缀得到不同模型分数；这里只校验标签一致性，不要求其分数相同。去除完整重复文件后的时长仅为数据量备注，不据此宣称泛化或重加权评估。只有当前人工确认仍标为 unresolved 的边界才不计入已定成功/失败分母。', '',
              '本批为同一人的七份开发录音，不能代表泛化或新用户效果。FP 秒是落入已确认非歌唱区的预测时长，不是实际送入 SVC 的讲话污染率；非歌唱也可能是停顿或噪音。没有部署或实际裁剪，因此不能据此宣布生产歌唱链成功。']
    Path(path).write_text('\n'.join(lines) + '\n', encoding='utf-8')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--annotations', type=Path, required=True)
    p.add_argument('--confirmation', type=Path)
    p.add_argument('--manifest', type=Path, required=True)
    p.add_argument('--results', action='append', required=True, help='name=directory; repeat per saved model')
    p.add_argument('--dependencies', type=Path)
    p.add_argument('--output', type=Path, required=True)
    args = p.parse_args()
    args.results = [(value.split('=', 1)[0], Path(value.split('=', 1)[1])) for value in args.results]
    require(len({name for name, _ in args.results}) == len(args.results), 'duplicate_model_name')
    run(args)


if __name__ == '__main__':
    main()
