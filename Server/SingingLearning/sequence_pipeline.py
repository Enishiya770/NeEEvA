"""Fine boundary experiments in the teaching service, with guarded adoption.

Original recordings and human revisions stay authoritative. Prepared features
are disposable, source-bound caches; no sequence prediction authorizes playback.
"""
import hashlib
import json
import os
import uuid
import wave

import numpy as np

from . import fusion_pipeline, proposals, temporal_fusion as fusion


REPORT_SCHEMA = 'neeeva.teaching-sequence-integration.v1'
MAX_RECORDINGS = 64
MAX_AUDIO_SECONDS = 1200


def prepare(pipeline, profile, rec):
    from . import sequence_features as frontend
    from .pipeline import feature_arrays
    frozen = pipeline.feature(profile, rec)
    if frozen.get('model_version') != proposals.BOOTSTRAP_CHECKPOINT:
        raise ValueError('sequence_bootstrap_checkpoint_mismatch')
    native_edges, acoustic, acoustic_metadata = feature_arrays(frozen, rec)
    path = pipeline.store.audio_path(profile, rec['recording_id'])
    if hashlib.sha256(path.read_bytes()).hexdigest() != rec['audio_sha256']:
        raise ValueError('sequence_original_audio_changed')
    track = fusion_pipeline.pitch_track(pipeline.store, profile, rec)
    with wave.open(str(path), 'rb') as reader:
        if (reader.getnchannels(), reader.getsampwidth(), reader.getframerate(), reader.getnframes()) != (1, 2, 16000, rec['sample_count']):
            raise ValueError('sequence_original_audio_coordinates_changed')
        pcm = reader.readframes(reader.getnframes())
    if track.get('source', {}).get('pcm_sha256') != hashlib.sha256(pcm).hexdigest():
        raise ValueError('sequence_pitch_source_content_mismatch')
    definition = frontend.current_definition(acoustic_metadata, track)
    binding = dict(schema='neeeva.sequence-feature-cache.v1',
                   audio_sha256=rec['audio_sha256'], sample_count=rec['sample_count'],
                   sample_rate=rec['sample_rate'], channels=rec['channels'],
                   recording_id=rec['recording_id'], acoustic_sha256=fusion.digest(frozen),
                   definition_sha256=fusion.digest(definition), pitch_track_sha256=fusion.digest(track))
    expected_edges = np.append(np.arange(0, rec['sample_count'], 320, dtype=np.int64), rec['sample_count'])
    cache = pipeline.store.profile_path(profile) / 'sequence_features' / (fusion.digest(binding)[:48] + '.npz')
    edges = features = metadata = None
    if cache.exists():
        try:
            with np.load(cache, allow_pickle=False) as data:
                saved = json.loads(str(data['metadata_json']))
                candidate_edges, candidate_features = data['edges'], data['features']
            valid = (saved['binding'] == binding and saved['metadata']['definition'] == definition
                     and candidate_edges.dtype.kind in 'iu' and candidate_edges.ndim == 1
                     and np.array_equal(candidate_edges, expected_edges)
                     and candidate_features.shape == (len(candidate_edges) - 1, len(definition['names']))
                     and np.isfinite(candidate_features).all()
                     and saved['features_sha256'] == hashlib.sha256(candidate_features.tobytes()).hexdigest()
                     and saved['metadata_sha256'] == fusion.digest(saved['metadata'])
                     and saved['metadata']['pitch_track_sha256'] == binding['pitch_track_sha256']
                     and saved['metadata']['source']['declared_wav_sha256'] == rec['audio_sha256']
                     and saved['metadata']['source']['pcm_sha256'] == track['source']['pcm_sha256']
                     and saved['metadata']['timeline']['source_start_samples'] == candidate_edges[:-1].tolist()
                     and saved['metadata']['timeline']['source_end_samples'] == candidate_edges[1:].tolist())
            if valid:
                edges, features, metadata = candidate_edges, candidate_features, saved['metadata']
        except (OSError, ValueError, KeyError, IndexError, TypeError):
            pass
    if edges is None:
        audio = np.frombuffer(pcm, dtype='<i2').astype(np.float32) / 32768.
        edges, features, metadata = frontend.build_features(audio, native_edges, acoustic, acoustic_metadata, track)
        if metadata['definition'] != definition:
            raise ValueError('sequence_feature_definition_changed')
        saved = dict(binding=binding, metadata=metadata,
                     features_sha256=hashlib.sha256(features.tobytes()).hexdigest(),
                     metadata_sha256=fusion.digest(metadata))
        cache.parent.mkdir(parents=True, exist_ok=True)
        temporary = cache.with_name(cache.name + '-' + uuid.uuid4().hex + '.tmp')
        try:
            with temporary.open('wb') as stream:
                np.savez_compressed(stream, edges=edges, features=features,
                                    metadata_json=np.asarray(json.dumps(saved, allow_nan=False)))
            os.replace(temporary, cache)
        finally:
            if temporary.exists():
                temporary.unlink()
    fine = dict(recording=rec, edges=edges, features=features, feature_metadata=definition,
                feature_sha256=fusion.digest(binding),
                pitch_source_sha256=fusion.digest(dict(source=track['source'], provider_identity=track['provider_identity'])))
    native, coarse = fusion_pipeline.prepare(pipeline, profile, rec, frozen)
    return fine, native, coarse


def train(pipeline, profile, records):
    from . import sequence_evaluation as evaluation
    from .pipeline import known_regions
    ids = sorted(rid for rid, rec in records.items() if known_regions(rec))
    report = dict(schema=REPORT_SCHEMA, status='shadow', teaching_eligible=False,
                  production_eligible=False, execution_authorized=False,
                  provider='fft', evaluation_scope='exact_prefix_component_holdout',
                  recording_count=len(ids),
                  limitation='same-session development; full-file PANNs and final sequence revision')
    if len(ids) > MAX_RECORDINGS or sum(records[rid]['duration_seconds'] for rid in ids) > MAX_AUDIO_SECONDS:
        return dict(report, status='deferred', eligibility_reasons=['sequence_background_budget_exceeded'])
    if not ids:
        return dict(report, status='insufficient_data', eligibility_reasons=['no_confirmed_user_labels'])
    try:
        eligible = {rid: records[rid] for rid in ids}
        relations = fusion.exact_prefix_relations(eligible, {rid: pipeline.store.audio_path(profile, rid) for rid in ids})
        prepared, native, coarse = {}, {}, {}
        for rid in ids:
            prepared[rid], native[rid], coarse[rid] = prepare(pipeline, profile, records[rid])
            for item in (prepared[rid], native[rid], coarse[rid]):
                item['duplicate_prefixes'] = relations[rid]
        result = evaluation.evaluate_prepared(records, prepared, native, coarse,
            fusion_pipeline.read_json(proposals.BOOTSTRAP_PATH), fusion.prefix_components(ids, relations),
            retain_predictions=False)
        report.update(result)
        guard = result['guards']['sequence']
        report.update(schema=REPORT_SCHEMA, status='eligible' if guard['teaching_eligible'] else 'shadow_rejected',
                      teaching_eligible=guard['teaching_eligible'], eligibility_reasons=guard['eligibility_reasons'],
                      candidate=result['candidates']['sequence'], guard=guard,
                      execution_authorized=False, production_eligible=False)
        return report
    except Exception as exc:
        return dict(report, status='failed', eligibility_reasons=['sequence_diagnostic_failed'],
                    error=str(exc)[:600], error_type=type(exc).__name__)


def eligible_candidate(model, records):
    from . import sequence_evaluation as evaluation, sequence_model, sequence_features as frontend
    from . import pitch_features
    report = model.get('sequence_boundary') or {}
    if report.get('schema') != REPORT_SCHEMA or report.get('status') != 'eligible' or report.get('teaching_eligible') is not True:
        return None
    try:
        candidate = sequence_model.validate_candidate(report['candidate'], records)
        summary = report['summaries']
        guard = evaluation.evaluate_guard(summary['candidate'],
            {k: summary[k] for k in ('bootstrap_proposal', 'panns_uniform', 'native_fft_speech2')},
            fold_count=report['fold_count'], complete=report['complete'])
        if (not guard['teaching_eligible'] or guard != report['guard']
                or candidate.get('teaching_eligible') is not True or candidate.get('evaluation_guard') != guard):
            return None
        definition = candidate['head']['emission_head']['feature_metadata']
        current = pitch_features.CausalPitchStream('compatibility').metadata
        if definition != frontend.current_definition(definition['acoustic']['metadata'], current):
            return None
        return candidate
    except (ValueError, KeyError, TypeError, OSError):
        return None


def proposal_prediction(pipeline, profile, rec, binding):
    from . import sequence_model
    model = pipeline.store.model(profile, binding['model_id'])
    candidate = eligible_candidate(model, pipeline.store.snapshot(profile)['recordings'])
    if candidate is None or candidate['candidate_id'] != binding.get('sequence_candidate_id'):
        raise ValueError('sequence_candidate_not_current_or_eligible')
    fine, _, _ = prepare(pipeline, profile, rec)
    prediction = sequence_model.predict(candidate['head'], fine['features'], fine['edges'],
                                        feature_metadata=fine['feature_metadata'])
    return fine, prediction


def make_proposal(pipeline, profile, rec, frozen, binding, item, prediction):
    from .pipeline import feature_arrays
    from .store import now
    edges, scores, selected = item['edges'], np.asarray(prediction['scores']), np.asarray(prediction['selected_mask'])
    if (scores.shape != (len(edges) - 1,) or selected.shape != scores.shape or selected.dtype != np.bool_
            or not np.isfinite(scores).all() or np.any(scores < 0) or np.any(scores > 1)):
        raise ValueError('invalid_sequence_proposal_prediction')
    native_edges, _, acoustic_metadata = feature_arrays(frozen, rec)
    columns = frozen['timeline']['columns']
    raw = np.asarray(frozen['timeline']['scores'])
    native_speech = raw[:, [columns.index(name) for name in acoustic_metadata['actual_speech_family_members']]].max(axis=1)
    native_index = np.searchsorted(native_edges[1:], edges[:-1], side='right')
    rms = proposals.cell_rms(pipeline.store.audio_path(profile, rec['recording_id']), rec, edges)
    labels = []
    for score, keep, energy, spoken in zip(scores, selected, rms, native_speech[native_index]):
        if keep:
            labels.append('singing')
        elif energy <= proposals.POLICY['quiet_rms_max']:
            labels.append('environment')
        elif score < proposals.POLICY['speech_max_singing_score'] and spoken >= proposals.POLICY['speech_family_min']:
            labels.append('speech')
        else:
            labels.append('uncertain')
    regions, first = [], 0
    for end in range(1, len(labels) + 1):
        if end < len(labels) and labels[end] == labels[first]:
            continue
        regions.append(dict(start_sample=int(edges[first]), end_sample=int(edges[end]), label=labels[first],
                            tolerance_samples=0, score=float(scores[first:end].mean()),
                            basis='learned_sequence_and_fine_waveform_pitch' if labels[first] == 'singing' else 'non_singing_evidence'))
        first = end
    names = dict(speech='说话', singing='唱歌', environment='安静或环境音', uncertain='暂时不确定')
    summary = '我先听到：' + '；'.join(f"{r['start_sample']/rec['sample_rate']:.1f}–{r['end_sample']/rec['sample_rate']:.1f} 秒{names[r['label']]}" for r in regions[:6])
    if len(regions) > 6:
        summary += '；其余区间已标在波形上'
    summary += '。你可以确认，也可以拖动边界纠正我。'
    return dict(binding, schema='neeeva.teaching-proposal.v1', created_at=now(), status='proposal',
                sample_rate=rec['sample_rate'], sample_count=rec['sample_count'], regions=regions, summary=summary,
                requires_confirmation=True, execution_authorized=False,
                source_cell_edges=edges.tolist(), scores=scores.tolist(), score_name='uncalibrated_local_emission_score',
                selected_mask=selected.tolist(), decoding='final_viterbi_sequence_revision',
                boundary_scope='20ms actual waveform/pitch evidence; frozen coarse acoustics; inspect original waveform',
                acoustic_context_seconds=frozen.get('model_metadata', {}).get('theoretical_receptive_field_seconds'),
                availability_scope='final sequence uses later evidence; PANNs available after whole-recording inference',
                fine_label_scope='sequence selects singing; negative quiet cells environment, strong speech evidence speech, others uncertain')
