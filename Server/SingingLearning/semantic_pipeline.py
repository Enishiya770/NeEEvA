"""Source-bound category-evidence experiments within ordinary teaching jobs."""
from . import boundary_pipeline, fusion_pipeline, proposals, sequence_pipeline, temporal_fusion as fusion

REPORT_SCHEMA = 'neeeva.teaching-category-evidence-integration.v1'


def train(pipeline, profile, records):
    from . import semantic_evaluation as evaluation
    from .pipeline import known_regions
    ids = sorted(rid for rid, rec in records.items() if known_regions(rec))
    report = dict(schema=REPORT_SCHEMA, status='shadow', teaching_eligible=False,
                  production_eligible=False, execution_authorized=False,
                  provider='fft', evaluation_scope='exact_prefix_component_holdout', recording_count=len(ids),
                  limitation='same-session development; whole-file acoustics and final sequence revision')
    if (len(ids) > sequence_pipeline.MAX_RECORDINGS
            or sum(records[rid]['duration_seconds'] for rid in ids) > sequence_pipeline.MAX_AUDIO_SECONDS):
        return dict(report, status='deferred', eligibility_reasons=['category_evidence_background_budget_exceeded'])
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
        guard = result['guards']['semantic']
        report.update(schema=REPORT_SCHEMA, status='eligible' if guard['teaching_eligible'] else 'shadow_rejected',
                      teaching_eligible=guard['teaching_eligible'], eligibility_reasons=guard['eligibility_reasons'],
                      candidate=result['candidates']['semantic'], guard=guard,
                      execution_authorized=False, production_eligible=False)
        return report
    except Exception as exc:
        return dict(report, status='failed', eligibility_reasons=['category_evidence_diagnostic_failed'],
                    error=str(exc)[:600], error_type=type(exc).__name__)


def local_boundary_report(report):
    """Retain the prior control and its independent adoption checks from one run."""
    if 'boundary' not in report.get('candidates', {}) or 'boundary' not in report.get('guards', {}):
        return dict(schema=boundary_pipeline.REPORT_SCHEMA, status=report['status'],
                    teaching_eligible=False, production_eligible=False, execution_authorized=False,
                    eligibility_reasons=['shared_category_evidence_diagnostic_unavailable'])
    guard = report['guards']['boundary']
    return dict(schema=boundary_pipeline.REPORT_SCHEMA,
                status='eligible' if guard['teaching_eligible'] else 'shadow_rejected',
                teaching_eligible=guard['teaching_eligible'], eligibility_reasons=guard['eligibility_reasons'],
                production_eligible=False, execution_authorized=False,
                candidate=report['candidates']['boundary'], guard=guard,
                candidates={k: report['candidates'][k] for k in ('boundary', 'sequence')},
                guards={k: report['guards'][k] for k in ('boundary', 'sequence')},
                summaries=dict(report['summaries'], candidate=report['summaries']['boundary']),
                fold_count=report['fold_count'], complete=report['complete'],
                boundary_training_complete=report['boundary_training_complete'],
                recording_count=report['recording_count'], provider='fft',
                evaluation_scope=report['evaluation_scope'],
                evaluation_reuse='unchanged local boundary and sequence controls from same category-evidence folds')


def eligible_candidate(model, records):
    from . import semantic_model, semantic_evaluation as evaluation, sequence_features as frontend, pitch_features
    report = model.get('category_evidence') or {}
    if report.get('schema') != REPORT_SCHEMA or report.get('status') != 'eligible' or report.get('teaching_eligible') is not True:
        return None
    try:
        candidate = semantic_model.validate_candidate(report['candidate'], records)
        summary = report['summaries']
        guard = evaluation.evaluate_guard(summary['candidate'],
            {k: summary[k] for k in ('bootstrap_proposal', 'panns_uniform', 'native_fft_speech2')},
            fold_count=report['fold_count'], complete=report['complete'],
            training_available=(report.get('semantic_training_complete') is True
                                and candidate.get('semantic_training_available') is True))
        if (not guard['teaching_eligible'] or guard != report['guard']
                or candidate.get('teaching_eligible') is not True or candidate.get('evaluation_guard') != guard):
            return None
        definition = candidate['head']['feature_metadata']
        current = pitch_features.CausalPitchStream('compatibility').metadata
        if definition != frontend.current_definition(definition['acoustic']['metadata'], current):
            return None
        return candidate
    except (ValueError, KeyError, TypeError, OSError):
        return None


def proposal_prediction(pipeline, profile, rec, binding):
    from . import semantic_model
    model = pipeline.store.model(profile, binding['model_id'])
    candidate = eligible_candidate(model, pipeline.store.snapshot(profile)['recordings'])
    if candidate is None or candidate['candidate_id'] != binding.get('semantic_candidate_id'):
        raise ValueError('category_evidence_candidate_not_current_or_eligible')
    fine, _, _ = sequence_pipeline.prepare(pipeline, profile, rec)
    prediction = semantic_model.predict(candidate['head'], fine['features'], fine['edges'],
                                        feature_metadata=fine['feature_metadata'])
    return fine, prediction


def make_proposal(pipeline, profile, rec, frozen, binding, item, prediction):
    import numpy as np
    from .semantic_model import CLASS_NAMES
    result = sequence_pipeline.make_proposal(pipeline, profile, rec, frozen, binding, item, prediction)
    names = list(CLASS_NAMES)
    states = np.asarray(prediction['selected_states'])
    scores = np.asarray(prediction['category_scores'])
    edges = item['edges']
    if (list(prediction['class_names']) != names or states.shape != (len(edges) - 1,)
            or not np.issubdtype(states.dtype, np.integer) or np.any(states < 0) or np.any(states >= len(names))
            or scores.shape != (len(states), len(names)) or not np.isfinite(scores).all()
            or np.any(scores < 0) or np.any(scores > 1) or not np.allclose(scores.sum(axis=1), 1.)
            or not np.allclose(scores[:, names.index('singing')], prediction['scores'], rtol=1e-10, atol=1e-12)
            or not np.array_equal(states == names.index('singing'), prediction['selected_mask'])):
        raise ValueError('invalid_category_evidence_proposal_prediction')
    regions, first = [], 0
    for end in range(1, len(states) + 1):
        if end < len(states) and states[end] == states[first]:
            continue
        label = names[int(states[first])]
        regions.append(dict(start_sample=int(edges[first]), end_sample=int(edges[end]), label=label,
                            tolerance_samples=0, score=float(scores[first:end, int(states[first])].mean()),
                            basis='learned_category_evidence_and_transition'))
        first = end
    display = dict(speech='说话', singing='唱歌', environment='安静或环境音')
    summary = '我先听到：' + '；'.join(f"{r['start_sample']/rec['sample_rate']:.1f}–{r['end_sample']/rec['sample_rate']:.1f} 秒{display[r['label']]}" for r in regions[:6])
    if len(regions) > 6:
        summary += '；其余区间已标在波形上'
    summary += '。你可以确认，也可以拖动边界纠正我。'
    result.update(decoding='category_evidence_final_sequence',
                  regions=regions, summary=summary, class_names=names, category_scores=scores.tolist(),
                  boundary_scope='learned category evidence and category change on a 20ms timeline',
                  availability_scope='whole-file acoustic evidence and final sequence revision',
                  fine_label_scope='all three labels come from the final category decoder; user confirmation required')
    return result
