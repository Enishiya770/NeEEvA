"""Reviewable acoustic hypotheses, separate from confirmed training labels.

The bundled head is a small development-data prior, not a cross-user validated
classifier. Native cells are rough hypotheses; no waveform is ever cropped here.
"""
import hashlib
import io
import json
from pathlib import Path
import wave
import numpy as np
from Tools.SingingSegmentation import fit_score_adapter as adapter
from .store import LearningError, now, read_json

BOOTSTRAP_PATH = Path(__file__).parent / 'assets' / 'bootstrap-panns-development.json'
BOOTSTRAP_CHECKPOINT = 'dd3b4043a87d4ec13df8082c0fcfee3fb5084151808e47e060987a95eabdd142'
POLICY = dict(version='teaching-proposal-v1', singing_threshold=.5,
              speech_max_singing_score=.4, speech_family_min=.15,
              quiet_rms_max=.001, provisional_personal_weight=.25, smoothing=False, gap_fill=False)


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()).hexdigest()


def plan(store, backend, profile, recording_id, allow_fusion=True, allow_sequence=True, allow_boundary=True, allow_semantic=True):
    state = store.snapshot(profile)
    rec = state['recordings'].get(recording_id)
    if rec is None:
        raise LearningError('recording_not_found', status=404)
    try:
        backend_id = backend.identity()
    except Exception as exc:
        raise LearningError('proposal_backend_unavailable', str(exc)[:400], 503) from exc
    current = state.get('current_candidate')
    model = store.model(profile, current['model_id']) if current else None
    valid = (model and model['status'] == 'candidate' and not store.model_invalidated(model, state)
             and {v['backend_identity'] for v in model['feature_provenance'].values()} == {backend_id})
    if valid:
        head = model['adapter']
        source = 'personal_provisional' if model.get('model_stage') == 'provisional' else 'personal'
        model_id = model['model_id']
        provenance = dict(source_dataset_revision=model['source_dataset_revision'],
                          out_of_date=model['source_dataset_revision'] != state['profile']['dataset_revision'],
                          evaluation_scope=model['evaluation_scope'], model_stage=model.get('model_stage', 'development_validated'),
                          validation_available=model.get('model_stage') != 'provisional')
    else:
        head = read_json(BOOTSTRAP_PATH)
        source, model_id = 'bootstrap_development', 'panns-development-20260929'
        provenance = dict(head['experiment_provenance'],
                          scope='Bundled small development-data prior; not validated for new users or songs',
                          expected_checkpoint_sha256=BOOTSTRAP_CHECKPOINT)
    binding = dict(recording_id=recording_id, audio_sha256=rec['audio_sha256'],
                   annotation_revision=rec['annotation_revision'], backend_identity=backend_id,
                   model_source=source, model_id=model_id, adapter_sha256=digest(head), policy=POLICY)
    if source == 'personal_provisional':
        binding['prior_adapter_sha256'] = digest(read_json(BOOTSTRAP_PATH))
        provenance['proposal_score_blend'] = dict(personal_weight=POLICY['provisional_personal_weight'],
            bootstrap_weight=1 - POLICY['provisional_personal_weight'],
            scope='fixed conservative proposal mixture, not calibrated probabilities or validated improvement')
    if valid and allow_fusion and rec['sample_rate'] == 16000 and rec['channels'] == 1:
        from .fusion_pipeline import eligible_candidate
        candidate = eligible_candidate(model, state['recordings'])
        if candidate is not None:
            binding.update(model_source='personal_temporal_fusion', fusion_candidate_id=candidate['candidate_id'],
                           fusion_head_sha256=digest(candidate['head']))
            binding.pop('prior_adapter_sha256', None)
            provenance.pop('proposal_score_blend', None)
            provenance.update(evaluation_scope='exact_prefix_component_holdout', validation_available=False,
                              fusion_scope='guarded teaching candidate; not independent production validation',
                              pitch_provider='fft', acoustic_scope='whole-file PANNs; pitch branch causal only')
    if valid and allow_sequence and store.config.sequence_boundary_enabled and rec['sample_rate'] == 16000 and rec['channels'] == 1:
        from .sequence_pipeline import eligible_candidate
        candidate = eligible_candidate(model, state['recordings'])
        if candidate is not None:
            for key in ('prior_adapter_sha256', 'fusion_candidate_id', 'fusion_head_sha256'):
                binding.pop(key, None)
            provenance.pop('proposal_score_blend', None)
            binding.update(model_source='personal_sequence_boundary', sequence_candidate_id=candidate['candidate_id'],
                           sequence_head_sha256=digest(candidate['head']))
            provenance.update(evaluation_scope='exact_prefix_component_holdout', validation_available=False,
                boundary_scope='20ms raw/pitch evidence with final learned sequence revision',
                acoustic_scope='whole-file PANNs; complete output available after EOF', pitch_provider='fft')
    if (valid and allow_boundary and store.config.sequence_boundary_enabled and store.config.local_boundary_enabled
            and rec['sample_rate'] == 16000 and rec['channels'] == 1):
        from .boundary_pipeline import eligible_candidate
        candidate = eligible_candidate(model, state['recordings'])
        if candidate is not None:
            for key in ('prior_adapter_sha256', 'fusion_candidate_id', 'fusion_head_sha256',
                        'sequence_candidate_id', 'sequence_head_sha256'):
                binding.pop(key, None)
            provenance.pop('proposal_score_blend', None)
            binding.update(model_source='personal_local_boundary', boundary_candidate_id=candidate['candidate_id'],
                           boundary_head_sha256=digest(candidate['head']))
            provenance.update(evaluation_scope='exact_prefix_component_holdout', validation_available=False,
                boundary_scope='learned local onset/offset gates with 80ms right context and final sequence revision',
                acoustic_scope='whole-file PANNs; complete output available after EOF', pitch_provider='fft')
    if (valid and allow_semantic and store.config.sequence_boundary_enabled and store.config.local_boundary_enabled
            and store.config.category_evidence_enabled and rec['sample_rate'] == 16000 and rec['channels'] == 1):
        from .semantic_pipeline import eligible_candidate
        candidate = eligible_candidate(model, state['recordings'])
        if candidate is not None:
            for key in ('prior_adapter_sha256', 'fusion_candidate_id', 'fusion_head_sha256',
                        'sequence_candidate_id', 'sequence_head_sha256', 'boundary_candidate_id', 'boundary_head_sha256'):
                binding.pop(key, None)
            provenance.pop('proposal_score_blend', None)
            binding.update(model_source='personal_category_evidence', semantic_candidate_id=candidate['candidate_id'],
                           semantic_head_sha256=digest(candidate['head']))
            provenance.update(evaluation_scope='exact_prefix_component_holdout', validation_available=False,
                boundary_scope='three-category evidence and category-conditioned transitions on a 20ms grid',
                acoustic_scope='whole-file PANNs; complete output available after EOF', pitch_provider='fft')
    return rec, head, dict(binding, proposal_id='p-' + digest(binding)[:48], model_provenance=provenance)


def compatible_features(expected, actual):
    # Column order is irrelevant: build_features indexes each raw class by name.
    return ({k: v for k, v in expected.items() if k != 'input_label_order'} ==
            {k: v for k, v in actual.items() if k != 'input_label_order'})


def cell_rms(path, recording, edges):
    raw = Path(path).read_bytes()
    if hashlib.sha256(raw).hexdigest() != recording['audio_sha256']:
        raise LearningError('proposal_audio_hash_changed', status=409)
    with wave.open(io.BytesIO(raw), 'rb') as source:
        if (source.getframerate(), source.getnframes(), source.getnchannels(), source.getsampwidth()) != (
                recording['sample_rate'], recording['sample_count'], recording['channels'], 2):
            raise LearningError('proposal_audio_dimensions_changed', status=409)
        pcm = np.frombuffer(source.readframes(source.getnframes()), dtype='<i2').reshape(-1, source.getnchannels()).astype(np.float64) / 32768.
    # Max channel energy avoids cancellation in stereo anti-phase recordings.
    return np.asarray([float(np.sqrt(np.mean(pcm[a:b] ** 2, axis=0)).max()) for a, b in zip(edges[:-1], edges[1:])])


def make_proposal(recording, feature, head, binding, edges, features, metadata, audio_path, fusion_scores=None):
    if not compatible_features(head['feature_metadata'], metadata):
        raise LearningError('proposal_feature_definition_mismatch', status=409)
    if binding['model_source'] in ('bootstrap_development', 'personal_provisional') and feature.get('model_version') != BOOTSTRAP_CHECKPOINT:
        raise LearningError('bootstrap_checkpoint_mismatch', status=409)
    scores = adapter.predict_uncalibrated_score(head, features)
    extra = {}
    if binding['model_source'] == 'personal_temporal_fusion':
        if feature.get('model_version') != BOOTSTRAP_CHECKPOINT:
            raise LearningError('fusion_checkpoint_mismatch', status=409)
        scores = np.asarray(fusion_scores, dtype=np.float64)
        if scores.shape != (len(features),) or not np.isfinite(scores).all() or np.any(scores < 0) or np.any(scores > 1):
            raise LearningError('invalid_fusion_proposal_scores', status=409)
        extra['pitch_feature_scope'] = 'causal trailing pitch; combined PANNs classifier uses whole-recording acoustics'
    if binding['model_source'] == 'personal_provisional':
        prior = read_json(BOOTSTRAP_PATH)
        if digest(prior) != binding['prior_adapter_sha256'] or not compatible_features(prior['feature_metadata'], metadata):
            raise LearningError('provisional_prior_mismatch', status=409)
        baseline = adapter.predict_uncalibrated_score(prior, features)
        extra = dict(personal_raw_scores=scores.tolist(), prior_scores=baseline.tolist(),
                     score_blend=binding['model_provenance']['proposal_score_blend'])
        weight = POLICY['provisional_personal_weight']
        scores = baseline * (1 - weight) + scores * weight
    rms = cell_rms(audio_path, recording, edges)
    columns, raw = feature['timeline']['columns'], np.asarray(feature['timeline']['scores'])
    speech = raw[:, [columns.index(name) for name in metadata['actual_speech_family_members']]].max(axis=1)
    labels, bases = [], []
    for score, energy, spoken in zip(scores, rms, speech):
        if energy <= POLICY['quiet_rms_max']:
            label, basis = 'environment', 'very_low_pcm_energy'
        elif score >= POLICY['singing_threshold']:
            label, basis = 'singing', ('acoustic_and_trailing_pitch_head' if binding['model_source'] == 'personal_temporal_fusion' else 'acoustic_singing_head')
        elif score < POLICY['speech_max_singing_score'] and spoken >= POLICY['speech_family_min']:
            label, basis = 'speech', 'speech_family_and_low_singing_score'
        else:
            label, basis = 'uncertain', 'insufficient_or_conflicting_acoustic_evidence'
        labels.append(label)
        bases.append(basis)
    regions, first = [], 0
    for end in range(1, len(labels) + 1):
        if end < len(labels) and labels[end] == labels[first]:
            continue
        regions.append(dict(start_sample=int(edges[first]), end_sample=int(edges[end]), label=labels[first],
                            tolerance_samples=0, score=float(scores[first:end].mean()), basis=bases[first]))
        first = end
    names = dict(speech='说话', singing='唱歌', environment='安静或环境音', uncertain='暂时不确定')
    summary = '我先听到：' + '；'.join(f"{r['start_sample']/recording['sample_rate']:.1f}–{r['end_sample']/recording['sample_rate']:.1f} 秒{names[r['label']]}" for r in regions[:6])
    if len(regions) > 6:
        summary += '；其余区间已标在波形上'
    summary += '。你可以确认，也可以拖动边界纠正我。'
    return dict(binding, **extra, schema='neeeva.teaching-proposal.v1', created_at=now(), status='proposal',
                sample_rate=recording['sample_rate'], sample_count=recording['sample_count'],
                regions=regions, summary=summary, requires_confirmation=True,
                source_cell_edges=edges.tolist(), scores=scores.tolist(), score_name='uncalibrated_score',
                boundary_scope='rough native acoustic cells; inspect the original waveform before confirming',
                acoustic_context_seconds=feature.get('model_metadata', {}).get('theoretical_receptive_field_seconds'),
                fine_label_scope='speech uses raw acoustic evidence; environment only very low PCM energy; other negative cells remain uncertain',
                execution_authorized=False)
