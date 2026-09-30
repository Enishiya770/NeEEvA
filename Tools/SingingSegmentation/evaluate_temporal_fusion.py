"""Frozen batch experiment for fixed trailing-pitch fusion, never production permission.

The PANNs input is frozen whole-file inference. Only the pitch feature branch is
incremental. Prefix folds remain correlated within sessions and are explicitly
development diagnostics; metadata-group folds expose the actual data shortage.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys
import time
import wave

import numpy as np

if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from Server.SingingLearning import pipeline, proposals, temporal_fusion as fusion
from Tools.SingingSegmentation import evaluate_personal_learning as ev, fit_score_adapter as adapter


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding='utf8')


def waveform(path):
    with wave.open(str(path), 'rb') as stream:
        if (stream.getnchannels(), stream.getsampwidth(), stream.getframerate()) != (1, 2, 16000):
            raise ValueError('assessment_requires_mono_pcm16_16000')
        return np.frombuffer(stream.readframes(stream.getnframes()), dtype='<i2').astype(np.float32) / 32768.


def get_pitch(track_path, audio, rec, provider, pitch, model, extractor_hash):
    if hashlib.sha256(Path(pitch.__file__).read_bytes()).hexdigest() != extractor_hash:
        raise ValueError('pitch_extractor_changed_during_evaluation_restart_required')
    binding = dict(audio_sha256=rec['audio_sha256'], sample_rate=rec['sample_rate'],
                   sample_count=rec['sample_count'], provider=provider, extractor_source_sha256=extractor_hash)
    if track_path.exists():
        stored = ev.read_json(track_path)
        if stored.get('source_binding') == binding:
            return stored['track'], stored['extraction_seconds'], binding
    started = time.perf_counter()
    track = pitch.extract_track(audio, stream_id=rec['recording_id'], provider=provider,
                                sample_rate=16000, chunk_samples=320, pesto_model=model)
    elapsed = time.perf_counter() - started
    save(track_path, dict(source_binding=binding, track=track, extraction_seconds=elapsed))
    return track, elapsed, binding


def predict_record(rec, item, head, name, rms_gate=True):
    score = fusion.predict_scores(head, item['features'], item['feature_metadata'])
    mask = score >= .5
    if rms_gate:
        mask &= item['rms'] > proposals.POLICY['quiet_rms_max']
    result = ev.evaluate_mask(rec, item['edges'], score, mask)
    return dict(recording_id=rec['recording_id'], capture_serial=rec['capture_serial'],
                audio_sha256=rec['audio_sha256'], annotation_revision=rec['annotation_revision'],
                source_cell_edges=item['edges'].tolist(), eligible_known_labels=True,
                variants={name: result})


def aggregate(rows, name):
    return ev.aggregate(rows, [name])[name]


def run_folds(records, variants, groups):
    ids = sorted(next(iter(variants.values()))['prepared'])
    held = {name: [] for name in variants}
    folds = []
    for group, test in sorted(groups.items()):
        test = sorted(set(test) & set(ids))
        if not test:
            continue
        train = sorted(set(ids) - set(test))
        row = dict(group=group, train_ids=train, test_ids=test, variants={})
        for name, spec in variants.items():
            try:
                model = fusion.fit_candidate(spec['prepared'], train, speech_cost=spec['speech_cost'])
                predictions = [predict_record(records[rid], spec['prepared'][rid], model['head'], name) for rid in test]
                held[name].extend(predictions)
                row['variants'][name] = dict(status='ok', summary=aggregate(predictions, name),
                                              train_source_binding_sha256=model['source_bindings_sha256'])
            except (ValueError, np.linalg.LinAlgError) as exc:
                row['variants'][name] = dict(status='unavailable', reason=str(exc))
        folds.append(row)
    return dict(folds=folds, aggregate={name: aggregate(rows, name) for name, rows in held.items()},
                predictions=held, fold_count=len(folds),
                complete={name: len(rows) == len(ids) for name, rows in held.items()})


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--pesto-deps', type=Path)
    parser.add_argument('--providers', nargs='+', default=['fft', 'pesto'], choices=['fft', 'pesto'])
    args = parser.parse_args(argv)
    from Server.SingingLearning import pitch_features as pitch
    extractor_hash = hashlib.sha256(Path(pitch.__file__).read_bytes()).hexdigest()
    args.output.mkdir(parents=True, exist_ok=True)
    payload, records, previous, bootstrap, base, dataset, loaded = ev.load_snapshot(args.snapshot)
    ids = [rid for rid in sorted(records) if pipeline.known_regions(records[rid])]
    paths = {rid: args.snapshot / 'recordings' / rid / 'original.wav' for rid in ids}
    eligible = {rid: records[rid] for rid in ids}
    relations = fusion.exact_prefix_relations(eligible, paths)
    prefix_groups = fusion.prefix_components(ids, relations)
    model = None
    if 'pesto' in args.providers:
        if args.pesto_deps:
            sys.path.insert(0, str(args.pesto_deps.resolve()))
        import torch
        import pesto
        torch.set_num_threads(2)
        model = pesto.load_model('mir-1k_g7', step_size=20., sampling_rate=16000, streaming=True, mirror=0.)
    prepared = {name: {} for name in ['panns'] + ['panns_' + p for p in args.providers]}
    timings = []
    for rid in ids:
        rec, item = records[rid], base[rid]
        common = dict(recording=rec, edges=item['edges'], rms=item['rms'],
                      feature_sha256=item['feature_sha256'], duplicate_prefixes=relations[rid])
        metadata = fusion.feature_metadata(item['metadata'])
        prepared['panns'][rid] = dict(common, features=item['features'], feature_metadata=metadata,
                                      pitch_source_sha256=None)
        audio = waveform(paths[rid])
        for provider in args.providers:
            track, elapsed, binding = get_pitch(args.output / 'tracks' / f'{rid}-{provider}.json',
                                                 audio, rec, provider, pitch, model, extractor_hash)
            rows = pitch.build_trailing_features(track, item['edges'][1:])
            metadata = fusion.feature_metadata(item['metadata'], track)
            prepared['panns_' + provider][rid] = dict(common, features=np.column_stack((item['features'], rows)),
                feature_metadata=metadata, pitch_source_sha256=fusion.digest(binding))
            timings.append(dict(recording_id=rid, provider=provider, seconds=elapsed,
                                audio_seconds=rec['duration_seconds']))
        print(json.dumps(dict(event='features_ready', recording_id=rid, capture_serial=rec['capture_serial'])), flush=True)
    variants = {name + '_' + cost_name: dict(prepared=items, speech_cost=cost)
                for name, items in prepared.items() for cost_name, cost in [('uniform', 1.), ('speech2', 2.)]}
    same, candidates = {}, {}
    for name, spec in variants.items():
        candidate = fusion.fit_candidate(spec['prepared'], ids, speech_cost=spec['speech_cost'])
        predictions = [predict_record(records[rid], spec['prepared'][rid], candidate['head'], name) for rid in ids]
        same[name] = dict(aggregate=aggregate(predictions, name), predictions=predictions)
        candidates[name] = candidate
    prefix = run_folds(records, variants, prefix_groups)
    metadata_groups = {gid: [rid for rid in members if rid in ids] for gid, members in dataset['groups'].items()}
    metadata_holdout = run_folds(records, variants, metadata_groups)
    fixed = {name: [] for name in ['bootstrap_proposal', 'current_proposal_same_data']}
    for rid in ids:
        rec, item = records[rid], base[rid]
        prior = adapter.predict_uncalibrated_score(bootstrap, item['features'])
        personal = adapter.predict_uncalibrated_score(previous['adapter'], item['features'])
        scores = dict(bootstrap_proposal=prior, current_proposal_same_data=.75 * prior + .25 * personal)
        for name, score in scores.items():
            result = ev.evaluate_mask(rec, item['edges'], score, (score >= .5) & (item['rms'] > proposals.POLICY['quiet_rms_max']))
            fixed[name].append(dict(recording_id=rid, eligible_known_labels=True, variants={name: result}))
    fixed_summary = {name: aggregate(rows, name) for name, rows in fixed.items()}
    guards = {}
    for name in variants:
        candidate_summary = prefix['aggregate'][name]
        guards[name] = fusion.evaluate_teaching_guard(candidate_summary,
            dict(bootstrap_proposal=fixed_summary['bootstrap_proposal'], panns_uniform=prefix['aggregate']['panns_uniform']),
            fold_count=prefix['fold_count'], complete=prefix['complete'][name])
        reasons = guards[name]['eligibility_reasons']
        candidates[name].update(teaching_eligible=not reasons, eligibility_reasons=reasons, evaluation_guard=guards[name])
        save(args.output / 'candidates' / (name + '.json'), candidates[name])
    report = dict(schema='neeeva.temporal-fusion-evaluation.v1', execution_authorized=False,
        source_dataset_revision=payload['dataset_revision'], source_snapshot_sha256=fusion.digest(loaded),
        source_files=loaded, feature_variants=list(variants), recording_count=len(ids),
        metadata_group_count=len(metadata_groups), exact_prefix_component_count=len(prefix_groups),
        exact_prefix_relations=relations, fixed_policy=dict(fusion.POLICY, rms_gate=proposals.POLICY['quiet_rms_max']),
        same_data=same, exact_prefix_component_holdout=prefix, metadata_group_holdout=metadata_holdout,
        fixed_references=fixed_summary, guards=guards, extraction_timings=timings,
        limitations=['Only pitch branch is causal. Frozen PANNs rows use whole-file inference.',
          'Prefix component folds are correlated same-speaker/session development diagnostics, not independent acceptance validation.',
          'Only two independent metadata components; every metadata holdout trains on one component.',
          'Bootstrap provenance includes prior same-user development recordings; its scores are a fixed product comparator, not new-user validation.',
          'Current proposal personal head saw all labels and is reported same-data only.',
          'Threshold .5, lambda .1, speech costs1/2, duration weighting, RMS override fixed before this run.',
          'No threshold sweep, best-fold selection, annotation changes, or production activation.'])
    save(args.output / 'evaluation.json', report)
    compact = dict(recording_count=len(ids), prefix_components=len(prefix_groups),
                   metrics={name: value['metrics'] for name, value in prefix['aggregate'].items()},
                   references={name: value['metrics'] for name, value in fixed_summary.items()}, guards=guards)
    save(args.output / 'summary.json', compact)
    print(json.dumps(compact, indent=2), flush=True)
    return report


if __name__ == '__main__':
    main()
