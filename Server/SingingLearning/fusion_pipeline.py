"""Guarded teaching-only fusion integration; legacy proposals remain the fallback.

Evaluation folds exclude identical PCM prefixes together. They are correlated
development diagnostics, never independent user/session validation. Only the
pitch branch is incremental; frozen PANNs features still use the whole file.
"""
import hashlib
from pathlib import Path
import wave

import numpy as np

from . import pitch_features as pitch, temporal_fusion as fusion, proposals
from .pitch_sessions import load_cached_track, _cache_path
from .store import atomic_json, read_json


REPORT_SCHEMA = 'neeeva.teaching-fusion-integration.v1'
MAX_GUARD_RECORDINGS = 64


def _source(rec):
    return {key: rec[key] for key in ('audio_sha256', 'sample_rate', 'sample_count', 'channels')}


def pitch_track(store, profile, rec):
    if rec['sample_rate'] != 16000 or rec['channels'] != 1:
        raise ValueError('fusion_requires_original_mono_16000_pcm')
    identity = pitch.provider_identity('fft')
    cached = load_cached_track(store, profile, rec, identity)
    if cached is not None:
        # Verify frame content as well as cache/source binding before reuse.
        try:
            pitch.build_trailing_features(cached, np.asarray([rec['sample_count']], dtype=np.int64))
            return cached
        except (ValueError, KeyError, TypeError):
            # Disposable features can always be reconstructed from the original.
            pass
    path = store.audio_path(profile, rec['recording_id'])
    if hashlib.sha256(path.read_bytes()).hexdigest() != rec['audio_sha256']:
        raise ValueError('fusion_original_audio_changed')
    with wave.open(str(path), 'rb') as audio:
        if (audio.getnchannels(), audio.getsampwidth(), audio.getframerate(), audio.getnframes()) != (1, 2, 16000, rec['sample_count']):
            raise ValueError('fusion_original_audio_coordinates_changed')
        pcm = audio.readframes(audio.getnframes())
    track = pitch.extract_track(np.frombuffer(pcm, dtype='<i2').astype(np.float32) / 32768.,
                                stream_id=rec['recording_id'], provider='fft')
    track.update(cache_schema=1, execution_authorized=False,
                 source=dict(_source(rec), pcm_sha256=hashlib.sha256(pcm).hexdigest()),
                 provenance=dict(kind='causal_offline_replay', recording_id=rec['recording_id']))
    # Shared lock prevents fixed-name atomic temp-file collisions with a live
    # stream commit for exactly this source/provider at the same time.
    with store.lock:
        atomic_json(_cache_path(store, profile, rec['audio_sha256'], identity), track)
    return track


def prepare(pipeline, profile, rec, frozen=None):
    from .pipeline import feature_arrays
    frozen = pipeline.feature(profile, rec) if frozen is None else frozen
    if frozen.get('model_version') != proposals.BOOTSTRAP_CHECKPOINT:
        raise ValueError('fusion_bootstrap_checkpoint_mismatch')
    edges, acoustic, acoustic_metadata = feature_arrays(frozen, rec)
    track = pitch_track(pipeline.store, profile, rec)
    trailing = pitch.build_trailing_features(track, edges[1:])
    common = dict(recording=rec, edges=edges,
                  rms=proposals.cell_rms(pipeline.store.audio_path(profile, rec['recording_id']), rec, edges),
                  feature_sha256=fusion.digest(frozen),
                  pitch_source_sha256=fusion.digest(dict(source=_source(rec), provider_identity=track['provider_identity'])))
    item = dict(common, features=np.column_stack((acoustic, trailing)),
                feature_metadata=fusion.feature_metadata(acoustic_metadata, track))
    acoustic_item = dict(common, features=acoustic, pitch_source_sha256=None,
                         feature_metadata=fusion.feature_metadata(acoustic_metadata))
    return item, acoustic_item


def measure(rec, item, scores):
    """Exact known-label overlap plus whole-interval fragmentation/miss counts."""
    from .pipeline import cell_masses, known_regions
    edges = item['edges']
    selected = (np.asarray(scores) >= .5) & (item['rms'] > proposals.POLICY['quiet_rms_max'])
    positive, negative, fine = cell_masses(rec, edges)
    metrics = dict(tp_seconds=float(positive[selected].sum()), fn_seconds=float(positive[~selected].sum()),
                   fp_seconds=float(negative[selected].sum()), tn_seconds=float(negative[~selected].sum()),
                   speech_fp_seconds=float(fine['speech'][selected].sum()),
                   environment_fp_seconds=float(fine['environment'][selected].sum()))
    ranges, first = [], None
    for index, keep in enumerate(selected):
        if keep and first is None:
            first = int(edges[index])
        if first is not None and (not keep or index == len(selected) - 1):
            ranges.append((first, int(edges[index] if not keep else edges[index + 1])))
            first = None
    # Same definition as the offline report: raw human boundaries are used
    # for fragmentation; tolerance is excluded only from duration metrics.
    ann = rec.get('annotation') or {}
    gold = [r for r in ann.get('regions', []) if r['label'] == 'singing'] if known_regions(rec) else []
    overlaps = [sum(min(g['end_sample'], b) > max(g['start_sample'], a) for a, b in ranges) for g in gold]
    return dict(recording_id=rec['recording_id'], metrics=metrics,
                boundaries=dict(completely_missed_gold_count=sum(v == 0 for v in overlaps),
                                internal_fragment_splits=sum(max(0, v - 1) for v in overlaps)),
                predicted_ranges=[dict(start_sample=a, end_sample=b) for a, b in ranges])


def aggregate(rows):
    metric_keys = ('tp_seconds', 'fn_seconds', 'fp_seconds', 'tn_seconds', 'speech_fp_seconds', 'environment_fp_seconds')
    boundary_keys = ('completely_missed_gold_count', 'internal_fragment_splits')
    return dict(metrics={key: sum(row['metrics'][key] for row in rows) for key in metric_keys},
                boundaries={key: sum(row['boundaries'][key] for row in rows) for key in boundary_keys},
                metric_recordings=len(rows))


def train(pipeline, profile, records):
    from .pipeline import known_regions
    ids = sorted(rid for rid, rec in records.items() if known_regions(rec))
    report = dict(schema=REPORT_SCHEMA, status='shadow', teaching_eligible=False,
                  execution_authorized=False, production_eligible=False,
                  policy=dict(fusion.POLICY, speech_cost=2.), provider='fft',
                  evaluation_scope='exact_prefix_component_holdout',
                  limitation='same-session development diagnostics; frozen whole-file PANNs with incremental pitch only')
    if len(ids) > MAX_GUARD_RECORDINGS:
        return dict(report, status='deferred', eligibility_reasons=['guard_recording_limit'], max_recordings=MAX_GUARD_RECORDINGS)
    try:
        if not ids:
            return dict(report, status='insufficient_data', eligibility_reasons=['no_confirmed_user_labels'])
        prepared, acoustic = {}, {}
        eligible = {rid: records[rid] for rid in ids}
        paths = {rid: pipeline.store.audio_path(profile, rid) for rid in ids}
        relations = fusion.exact_prefix_relations(eligible, paths)
        for rid in ids:
            prepared[rid], acoustic[rid] = prepare(pipeline, profile, records[rid])
            prepared[rid]['duplicate_prefixes'] = acoustic[rid]['duplicate_prefixes'] = relations[rid]
        candidate = fusion.fit_candidate(prepared, speech_cost=2.)
        groups = fusion.prefix_components(ids, relations)
        bootstrap = read_json(proposals.BOOTSTRAP_PATH)
        # The service keeps its old adapter dependency encapsulated by proposals.
        prior_rows = [measure(records[rid], prepared[rid], proposals.adapter.predict_uncalibrated_score(bootstrap, acoustic[rid]['features'])) for rid in ids]
        held, baseline_held, folds = [], [], []
        for group_id, test_ids in sorted(groups.items()):
            training_ids = sorted(set(ids) - set(test_ids))
            fold = dict(component_id=group_id, train_ids=training_ids, test_ids=test_ids)
            try:
                model = fusion.fit_candidate(prepared, training_ids, speech_cost=2.)
                baseline = fusion.fit_candidate(acoustic, training_ids, speech_cost=1.)
                predictions = [measure(records[rid], prepared[rid], fusion.predict_scores(model['head'], prepared[rid]['features'], prepared[rid]['feature_metadata'])) for rid in test_ids]
                comparisons = [measure(records[rid], acoustic[rid], fusion.predict_scores(baseline['head'], acoustic[rid]['features'], acoustic[rid]['feature_metadata'])) for rid in test_ids]
                held.extend(predictions)
                baseline_held.extend(comparisons)
                fold.update(status='ok', candidate=aggregate(predictions), panns_uniform=aggregate(comparisons))
            except (ValueError, np.linalg.LinAlgError) as exc:
                fold.update(status='unavailable', reason=str(exc)[:400])
            folds.append(fold)
        summaries = dict(candidate=aggregate(held), bootstrap_proposal=aggregate(prior_rows), panns_uniform=aggregate(baseline_held))
        complete = len(held) == len(ids) and len(baseline_held) == len(ids)
        guard = fusion.evaluate_teaching_guard(summaries['candidate'],
                  {key: summaries[key] for key in ('bootstrap_proposal', 'panns_uniform')},
                  fold_count=len(folds), complete=complete)
        candidate.update(teaching_eligible=guard['teaching_eligible'], eligibility_reasons=guard['eligibility_reasons'], evaluation_guard=guard)
        report.update(status='eligible' if guard['teaching_eligible'] else 'shadow_rejected',
                      teaching_eligible=guard['teaching_eligible'], eligibility_reasons=guard['eligibility_reasons'],
                      candidate=candidate, summaries=summaries, guard=guard, fold_count=len(folds), complete=complete,
                      folds=folds, exact_prefix_relations=relations, recording_count=len(ids))
        return report
    except Exception as exc:
        return dict(report, status='failed', eligibility_reasons=['fusion_diagnostic_failed'],
                    error=str(exc)[:600], error_type=type(exc).__name__)


def eligible_candidate(model, records):
    """Recheck the saved gate and source/feature policy; never trust a boolean alone."""
    report = model.get('temporal_fusion') or {}
    if report.get('schema') != REPORT_SCHEMA or report.get('status') != 'eligible' or report.get('teaching_eligible') is not True:
        return None
    try:
        candidate = fusion.validate_candidate(report['candidate'], records)
        if candidate.get('teaching_eligible') is not True or candidate['policy']['speech_cost'] != 2.:
            return None
        summaries = report['summaries']
        guard = fusion.evaluate_teaching_guard(summaries['candidate'],
                  {key: summaries[key] for key in ('bootstrap_proposal', 'panns_uniform')},
                  fold_count=report['fold_count'], complete=report['complete'])
        if not guard['teaching_eligible'] or guard != report['guard'] or candidate['evaluation_guard'] != guard:
            return None
        metadata = candidate['head']['feature_metadata']
        # Build today's full definition: any provider, feature-policy, or
        # extractor source change invalidates yesterday's fusion head.
        current = pitch.CausalPitchStream('compatibility').metadata
        if metadata != fusion.feature_metadata(metadata['panns'], current):
            return None
        return candidate
    except (ValueError, KeyError, TypeError, OSError):
        return None


def proposal_scores(pipeline, profile, rec, frozen, binding):
    model = pipeline.store.model(profile, binding['model_id'])
    state = pipeline.store.snapshot(profile)
    candidate = eligible_candidate(model, state['recordings'])
    if candidate is None or candidate['candidate_id'] != binding.get('fusion_candidate_id'):
        raise ValueError('fusion_candidate_not_current_or_eligible')
    item, _ = prepare(pipeline, profile, rec, frozen)
    return fusion.predict_scores(candidate['head'], item['features'], item['feature_metadata'])
