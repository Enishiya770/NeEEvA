"""Guarded teaching proposals with separately learned local onset/offset cues.

Reuses source-bound fine features and evaluates the old sequence in the same
folds. Neither a diagnostic candidate nor an accepted teaching proposal grants
permission to crop or play singing audio.
"""
from . import (fusion_pipeline, proposals, sequence_pipeline,
               temporal_fusion as fusion)


REPORT_SCHEMA = 'neeeva.teaching-local-boundary-integration.v1'


def train(pipeline, profile, records):
    from . import boundary_evaluation as evaluation
    from .pipeline import known_regions
    ids = sorted(rid for rid, rec in records.items() if known_regions(rec))
    report = dict(schema=REPORT_SCHEMA, status='shadow', teaching_eligible=False,
                  production_eligible=False, execution_authorized=False,
                  provider='fft', evaluation_scope='exact_prefix_component_holdout',
                  recording_count=len(ids),
                  limitation='same-session development; local right context, full-file PANNs and final revision')
    if (len(ids) > sequence_pipeline.MAX_RECORDINGS
            or sum(records[rid]['duration_seconds'] for rid in ids) > sequence_pipeline.MAX_AUDIO_SECONDS):
        return dict(report, status='deferred', eligibility_reasons=['local_boundary_background_budget_exceeded'])
    if not ids:
        return dict(report, status='insufficient_data', eligibility_reasons=['no_confirmed_user_labels'])
    try:
        eligible = {rid: records[rid] for rid in ids}
        relations = fusion.exact_prefix_relations(eligible, {rid: pipeline.store.audio_path(profile, rid) for rid in ids})
        prepared, native, coarse = {}, {}, {}
        for rid in ids:
            prepared[rid], native[rid], coarse[rid] = sequence_pipeline.prepare(pipeline, profile, records[rid])
            for item in (prepared[rid], native[rid], coarse[rid]):
                item['duplicate_prefixes'] = relations[rid]
        result = evaluation.evaluate_prepared(records, prepared, native, coarse,
            fusion_pipeline.read_json(proposals.BOOTSTRAP_PATH), fusion.prefix_components(ids, relations),
            retain_predictions=False)
        report.update(result)
        guard = result['guards']['boundary']
        report.update(schema=REPORT_SCHEMA, status='eligible' if guard['teaching_eligible'] else 'shadow_rejected',
                      teaching_eligible=guard['teaching_eligible'], eligibility_reasons=guard['eligibility_reasons'],
                      candidate=result['candidates']['boundary'], guard=guard,
                      execution_authorized=False, production_eligible=False)
        return report
    except Exception as exc:
        return dict(report, status='failed', eligibility_reasons=['local_boundary_diagnostic_failed'],
                    error=str(exc)[:600], error_type=type(exc).__name__)


def sequence_report(report):
    """Expose the unchanged sequence control without fitting its folds twice."""
    if 'sequence' not in report.get('candidates', {}) or 'sequence' not in report.get('guards', {}):
        return dict(schema=sequence_pipeline.REPORT_SCHEMA, status=report['status'],
                    teaching_eligible=False, production_eligible=False, execution_authorized=False,
                    eligibility_reasons=['shared_local_boundary_diagnostic_unavailable'])
    guard = report['guards']['sequence']
    return dict(schema=sequence_pipeline.REPORT_SCHEMA,
                status='eligible' if guard['teaching_eligible'] else 'shadow_rejected',
                teaching_eligible=guard['teaching_eligible'], eligibility_reasons=guard['eligibility_reasons'],
                production_eligible=False, execution_authorized=False,
                candidate=report['candidates']['sequence'], guard=guard,
                candidates={'sequence': report['candidates']['sequence']}, guards={'sequence': guard},
                summaries=dict(report['summaries'], candidate=report['summaries']['sequence']),
                fold_count=report['fold_count'], complete=report['complete'],
                recording_count=report['recording_count'], provider='fft',
                evaluation_scope=report['evaluation_scope'],
                evaluation_reuse='unchanged sequence control from same local-boundary folds')


def eligible_candidate(model, records):
    from . import boundary_model, boundary_evaluation as evaluation, sequence_features as frontend, pitch_features
    report = model.get('local_boundary') or {}
    if report.get('schema') != REPORT_SCHEMA or report.get('status') != 'eligible' or report.get('teaching_eligible') is not True:
        return None
    try:
        candidate = boundary_model.validate_candidate(report['candidate'], records)
        summary = report['summaries']
        guard = evaluation.evaluate_guard(summary['candidate'],
            {k: summary[k] for k in ('bootstrap_proposal', 'panns_uniform', 'native_fft_speech2')},
            fold_count=report['fold_count'], complete=report['complete'],
            training_available=(report.get('boundary_training_complete') is True
                                and candidate.get('boundary_training_available') is True))
        if (not guard['teaching_eligible'] or guard != report['guard']
                or candidate.get('teaching_eligible') is not True or candidate.get('evaluation_guard') != guard):
            return None
        definition = candidate['head']['sequence_head']['emission_head']['feature_metadata']
        current = pitch_features.CausalPitchStream('compatibility').metadata
        if definition != frontend.current_definition(definition['acoustic']['metadata'], current):
            return None
        return candidate
    except (ValueError, KeyError, TypeError, OSError):
        return None


def proposal_prediction(pipeline, profile, rec, binding):
    from . import boundary_model
    model = pipeline.store.model(profile, binding['model_id'])
    candidate = eligible_candidate(model, pipeline.store.snapshot(profile)['recordings'])
    if candidate is None or candidate['candidate_id'] != binding.get('boundary_candidate_id'):
        raise ValueError('local_boundary_candidate_not_current_or_eligible')
    fine, _, _ = sequence_pipeline.prepare(pipeline, profile, rec)
    prediction = boundary_model.predict(candidate['head'], fine['features'], fine['edges'],
                                        feature_metadata=fine['feature_metadata'])
    return fine, prediction


def make_proposal(pipeline, profile, rec, frozen, binding, item, prediction):
    result = sequence_pipeline.make_proposal(pipeline, profile, rec, frozen, binding, item, prediction)
    result.update(decoding='local_onset_offset_gated_final_viterbi',
                  boundary_scope='20ms timeline; learned local onset/offset cues with 80ms right context',
                  availability_scope='boundary right context plus final revision; PANNs available after whole-recording inference',
                  fine_label_scope='local-boundary decoder selects singing; other classes are non-singing evidence')
    for region in result['regions']:
        if region['label'] == 'singing':
            region['basis'] = 'learned_local_onset_offset_and_sequence'
    return result
