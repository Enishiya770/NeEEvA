"""Source-verified frozen replay for fixed local onset/offset boundary heads."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import time

os.environ.setdefault('OPENBLAS_NUM_THREADS', '2')
os.environ.setdefault('OMP_NUM_THREADS', '2')
import numpy as np

if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from Server.SingingLearning import boundary_evaluation, pipeline, pitch_features as pitch
from Server.SingingLearning import sequence_features as frontend, temporal_fusion as fusion
from Tools.SingingSegmentation import evaluate_personal_learning as ev
from Tools.SingingSegmentation.evaluate_sequence_boundaries import load_audio, save, progress


def load_verified_fine_cache(cache_dir, rec, acoustic_metadata, frozen, track, pcm):
    """Read cached fine features only after validating all content/definition binds.

    No feature extraction or cache repair is allowed here. A missing/mismatched
    cache fails clearly rather than silently repeating work or using old code.
    """
    definition = frontend.current_definition(acoustic_metadata, track)
    binding = dict(schema='neeeva.sequence-feature-cache.v1',
        audio_sha256=rec['audio_sha256'], sample_count=rec['sample_count'], sample_rate=rec['sample_rate'],
        channels=rec['channels'], recording_id=rec['recording_id'], acoustic_sha256=fusion.digest(frozen),
        definition_sha256=fusion.digest(definition), pitch_track_sha256=fusion.digest(track))
    path = cache_dir / (fusion.digest(binding)[:48] + '.npz')
    if not path.is_file():
        raise ValueError('missing_current_fine_cache:' + rec['recording_id'])
    with np.load(path, allow_pickle=False) as data:
        saved = json.loads(str(data['metadata_json']))
        edges, features = data['edges'], data['features']
    expected_edges = np.append(np.arange(0, rec['sample_count'], 320, dtype=np.int64), rec['sample_count'])
    metadata = saved['metadata']
    if not (saved['binding'] == binding and metadata['definition'] == definition
            and metadata['definition_sha256'] == fusion.digest(definition)
            and edges.dtype.kind in 'iu' and np.array_equal(edges, expected_edges)
            and features.shape == (len(edges) - 1, len(definition['names'])) and np.isfinite(features).all()
            and saved['features_sha256'] == hashlib.sha256(features.tobytes()).hexdigest()
            and metadata['feature_values_sha256'] == hashlib.sha256(np.asarray(features, dtype='<f8').tobytes()).hexdigest()
            and saved['metadata_sha256'] == fusion.digest(metadata)
            and metadata['pitch_track_sha256'] == binding['pitch_track_sha256']
            and metadata['source']['declared_wav_sha256'] == rec['audio_sha256']
            and metadata['source']['pcm_sha256'] == hashlib.sha256(pcm).hexdigest()
            and metadata['timeline']['source_start_samples'] == edges[:-1].tolist()
            and metadata['timeline']['source_end_samples'] == edges[1:].tolist()):
        raise ValueError('fine_cache_integrity_mismatch:' + rec['recording_id'])
    return edges, features, metadata, binding, path


def prepare(snapshot, output, profile_cache):
    before = time.perf_counter()
    payload, records, previous, bootstrap, base, dataset, loaded = ev.load_snapshot(snapshot)
    ids = [rid for rid in sorted(records) if pipeline.known_regions(records[rid])]
    relations = fusion.exact_prefix_relations({rid: records[rid] for rid in ids},
        {rid: snapshot / 'recordings' / rid / 'original.wav' for rid in ids})
    groups = fusion.prefix_components(ids, relations)
    provider_hash = hashlib.sha256(pitch.provider_identity('fft').encode('utf8')).hexdigest()
    fine, native, acoustic, cache_manifest = {}, {}, {}, []
    for rid in ids:
        rec, item = records[rid], base[rid]
        _, pcm = load_audio(snapshot / 'recordings' / rid / 'original.wav', rec)
        track_path = profile_cache / 'pitch_features' / (rec['audio_sha256'] + '-' + provider_hash + '.json')
        track = ev.read_json(track_path)
        if not (track.get('source', {}).get('pcm_sha256') == hashlib.sha256(pcm).hexdigest()
                and all(track['source'].get(k) == rec[k] for k in ('audio_sha256', 'sample_count', 'sample_rate', 'channels'))):
            raise ValueError('cached_pitch_original_mismatch:' + rid)
        # Full validation of frame coordinates, finite values and provider policy.
        native_pitch = pitch.build_trailing_features(track, item['edges'][1:])
        frozen = ev.read_json(snapshot / 'features' / (rec['audio_sha256'] + '-' + rec['feature_backend_identity'] + '.json'))
        edges, features, metadata, binding, cache_path = load_verified_fine_cache(
            profile_cache / 'sequence_features', rec, item['metadata'], frozen, track, pcm)
        common = dict(recording=rec, feature_sha256=item['feature_sha256'], duplicate_prefixes=relations[rid],
            pitch_source_sha256=fusion.digest(dict(source=track['source'], provider_identity=track['provider_identity'])))
        fine[rid] = dict(common, edges=edges, features=features, feature_metadata=metadata['definition'],
                         feature_sha256=fusion.digest(binding))
        acoustic[rid] = dict(common, edges=item['edges'], rms=item['rms'], features=item['features'],
                             feature_metadata=fusion.feature_metadata(item['metadata']), pitch_source_sha256=None)
        native[rid] = dict(common, edges=item['edges'], rms=item['rms'],
            features=np.column_stack((item['features'], native_pitch)), feature_metadata=fusion.feature_metadata(item['metadata'], track))
        row = dict(recording_id=rid, fine_cache_sha256=ev.file_sha(cache_path), pitch_cache_sha256=ev.file_sha(track_path),
                   binding=binding, feature_rows=len(features), feature_columns=features.shape[1])
        cache_manifest.append(row)
        for source, suffix in ((cache_path, '.npz'), (track_path, '-pitch.json')):
            target = output / 'verified-input-cache' / (rid + suffix)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
            if ev.file_sha(source) != ev.file_sha(target):
                raise ValueError('cache_changed_during_copy:' + rid)
        progress(dict(event='verified_fine_cache_loaded', recording_id=rid, capture_serial=rec.get('capture_serial'), rows=len(features)))
    return dict(payload=payload, records=records, bootstrap=bootstrap, fine=fine, native=native, acoustic=acoustic,
        groups=groups, metadata_groups={g: [rid for rid in members if rid in ids] for g, members in dataset['groups'].items()},
        loaded=loaded, relations=relations, cache_manifest=cache_manifest, preparation_seconds=time.perf_counter() - before)


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--profile-cache', type=Path, required=True)
    args = parser.parse_args(argv)
    args.output.mkdir(parents=True, exist_ok=True)
    from Server.SingingLearning import boundary_model, sequence_model
    sources = {name: hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest() for name, module in
               [('boundary_evaluation', boundary_evaluation), ('boundary_model', boundary_model),
                ('sequence_features', frontend), ('sequence_model', sequence_model), ('pitch_features', pitch)]}
    data = prepare(args.snapshot, args.output, args.profile_cache)
    report = boundary_evaluation.evaluate_prepared(data['records'], data['fine'], data['native'], data['acoustic'],
        data['bootstrap'], data['groups'], metadata_groups=data['metadata_groups'], progress=progress, retain_predictions=True)
    report.update(source_dataset_revision=data['payload']['dataset_revision'],
        source_snapshot_sha256=fusion.digest(data['loaded']), source_files=data['loaded'],
        exact_prefix_relations=data['relations'], metadata_group_count=len(data['metadata_groups']),
        exact_prefix_component_count=len(data['groups']), preparation_seconds=data['preparation_seconds'],
        verified_cache_manifest=data['cache_manifest'], code_sha256=sources,
        extraction_policy='reuse source-verified fine63 cache only; no fine feature re-extraction')
    for path, expected in data['loaded'].items():
        if ev.file_sha(path) != expected:
            raise ValueError('source_snapshot_changed_during_evaluation:' + path)
    for name, module in [('boundary_evaluation', boundary_evaluation), ('boundary_model', boundary_model),
                         ('sequence_features', frontend), ('sequence_model', sequence_model), ('pitch_features', pitch)]:
        if hashlib.sha256(Path(module.__file__).read_bytes()).hexdigest() != sources[name]:
            raise ValueError('code_changed_during_evaluation_restart_required:' + name)
    save(args.output / 'evaluation.json', report)
    save(args.output / 'candidate.json', report['candidate'])
    compact = dict(status=report['status'], recording_count=report['recording_count'],
        source_dataset_revision=report['source_dataset_revision'], exact_prefix_component_count=len(data['groups']),
        metadata_group_count=len(data['metadata_groups']), summaries=report['summaries'], guard=report['guard'],
        comparative_deltas_vs_sequence=report['comparative_deltas_vs_sequence'],
        timing={key: value for key, value in report['timing'].items() if key != 'operations'},
        preparation_seconds=data['preparation_seconds'])
    save(args.output / 'summary.json', compact)
    progress(compact)
    return report


if __name__ == '__main__':
    main()
