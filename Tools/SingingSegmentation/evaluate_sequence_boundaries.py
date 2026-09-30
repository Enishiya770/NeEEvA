"""Frozen replay of fine-grid singing boundaries and a fixed sequence decoder."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import time
import wave

# Bound BLAS worker overhead in this offline subprocess, not in any live server.
os.environ.setdefault('OPENBLAS_NUM_THREADS', '2')
os.environ.setdefault('OMP_NUM_THREADS', '2')
import numpy as np

if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from Server.SingingLearning import pipeline, pitch_features as pitch, temporal_fusion as fusion
from Server.SingingLearning import sequence_features, sequence_evaluation, sequence_model
from Tools.SingingSegmentation import evaluate_personal_learning as ev


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False), encoding='utf8')


def progress(value):
    print(json.dumps(value, ensure_ascii=False), flush=True)


def load_audio(path, rec):
    raw = path.read_bytes()
    if hashlib.sha256(raw).hexdigest() != rec['audio_sha256']:
        raise ValueError('sequence_snapshot_audio_changed:' + rec['recording_id'])
    with wave.open(str(path), 'rb') as stream:
        if (stream.getnchannels(), stream.getsampwidth(), stream.getframerate(), stream.getnframes()) != (1, 2, 16000, rec['sample_count']):
            raise ValueError('sequence_requires_original_mono_pcm16_16000')
        pcm = stream.readframes(stream.getnframes())
    return np.frombuffer(pcm, dtype='<i2').astype(np.float32) / 32768., pcm


def prepare(snapshot, output, prior_tracks=None):
    started = time.perf_counter()
    payload, records, previous, bootstrap, base, dataset, loaded = ev.load_snapshot(snapshot)
    ids = [rid for rid in sorted(records) if pipeline.known_regions(records[rid])]
    eligible = {rid: records[rid] for rid in ids}
    paths = {rid: snapshot / 'recordings' / rid / 'original.wav' for rid in ids}
    relations = fusion.exact_prefix_relations(eligible, paths)
    groups = fusion.prefix_components(ids, relations)
    extractor_sha = hashlib.sha256(Path(pitch.__file__).read_bytes()).hexdigest()
    fine, native, acoustic, timings = {}, {}, {}, []
    for rid in ids:
        before = time.perf_counter()
        rec, item = records[rid], base[rid]
        audio, pcm = load_audio(paths[rid], rec)
        binding = dict(audio_sha256=rec['audio_sha256'], sample_rate=16000,
                       sample_count=rec['sample_count'], provider='fft', extractor_source_sha256=extractor_sha)
        track, track_source = None, 'fresh_replay'
        for directory in [output / 'tracks', prior_tracks]:
            path = directory / (rid + '-fft.json') if directory else None
            if path and path.exists():
                cached = ev.read_json(path)
                if cached.get('source_binding') == binding:
                    track, track_source = cached['track'], str(path)
                    break
        if track is None:
            track = pitch.extract_track(audio, stream_id=rid, provider='fft', sample_rate=16000, chunk_samples=320)
        track['source'] = dict(audio_sha256=rec['audio_sha256'], pcm_sha256=hashlib.sha256(pcm).hexdigest(),
                               sample_rate=16000, sample_count=rec['sample_count'], channels=1)
        pitch.build_trailing_features(track, np.asarray([rec['sample_count']], dtype=np.int64))
        save(output / 'tracks' / (rid + '-fft.json'), dict(source_binding=binding, track=track))
        pitch_time = time.perf_counter() - before
        common = dict(recording=rec, feature_sha256=item['feature_sha256'],
                      duplicate_prefixes=relations[rid], pitch_source_sha256=fusion.digest(binding))
        acoustic[rid] = dict(common, edges=item['edges'], rms=item['rms'], features=item['features'],
                             feature_metadata=fusion.feature_metadata(item['metadata']), pitch_source_sha256=None)
        native[rid] = dict(common, edges=item['edges'], rms=item['rms'],
            features=np.column_stack((item['features'], pitch.build_trailing_features(track, item['edges'][1:]))),
            feature_metadata=fusion.feature_metadata(item['metadata'], track))
        before = time.perf_counter()
        edges, features, metadata = sequence_features.build_features(audio, item['edges'], item['features'], item['metadata'], track)
        fine_time = time.perf_counter() - before
        fine[rid] = dict(common, edges=edges, features=features, feature_metadata=metadata['definition'],
                         fine_feature_source_sha256=fusion.digest(metadata), source_feature_metadata=metadata)
        timings.append(dict(recording_id=rid, audio_seconds=rec['sample_count'] / 16000.,
                            pitch_seconds=pitch_time, fine_feature_seconds=fine_time,
                            fine_rows=len(features), feature_columns=features.shape[1], feature_bytes=features.nbytes,
                            pitch_source=track_source))
        progress(dict(event='fine_features_ready', recording_id=rid, capture_serial=rec.get('capture_serial'),
                      rows=len(features), seconds=fine_time))
    metadata_groups = {group: [rid for rid in members if rid in ids] for group, members in dataset['groups'].items()}
    return dict(payload=payload, records=records, bootstrap=bootstrap, fine=fine, native=native, acoustic=acoustic,
                groups=groups, metadata_groups=metadata_groups, loaded=loaded, relations=relations,
                timings=timings, preparation_seconds=time.perf_counter() - started)


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--prior-tracks', type=Path)
    parser.add_argument('--skip-metadata-holdout', action='store_true')
    args = parser.parse_args(argv)
    args.output.mkdir(parents=True, exist_ok=True)
    data = prepare(args.snapshot, args.output, args.prior_tracks)
    report = sequence_evaluation.evaluate_prepared(data['records'], data['fine'], data['native'], data['acoustic'],
        data['bootstrap'], data['groups'], metadata_groups=None if args.skip_metadata_holdout else data['metadata_groups'],
        progress=progress, retain_predictions=True)
    report.update(source_dataset_revision=data['payload']['dataset_revision'],
        source_snapshot_sha256=fusion.digest(data['loaded']), source_files=data['loaded'],
        exact_prefix_relations=data['relations'], metadata_group_count=len(data['metadata_groups']),
        exact_prefix_component_count=len(data['groups']), feature_preparation_timings=data['timings'],
        preparation_seconds=data['preparation_seconds'],
        code_sha256={name: hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest() for name, module in (
            ('sequence_evaluation', sequence_evaluation), ('sequence_features', sequence_features),
            ('sequence_model', sequence_model), ('pitch_features', pitch))})
    # Guard against external edits to the source snapshot during a long run.
    for path, expected in data['loaded'].items():
        if hashlib.sha256(Path(path).read_bytes()).hexdigest() != expected:
            raise ValueError('source_snapshot_changed_during_evaluation:' + path)
    save(args.output / 'evaluation.json', report)
    save(args.output / 'candidate.json', report['candidates']['sequence'])
    compact = dict(status=report['status'], recording_count=report['recording_count'],
        source_dataset_revision=report['source_dataset_revision'], exact_prefix_component_count=len(data['groups']),
        metadata_group_count=len(data['metadata_groups']), summaries=report['summaries'],
        guards=report['guards'], timing={key: value for key, value in report['timing'].items() if key != 'operations'},
        preparation_seconds=data['preparation_seconds'])
    save(args.output / 'summary.json', compact)
    progress(compact)
    return report


if __name__ == '__main__':
    main()
