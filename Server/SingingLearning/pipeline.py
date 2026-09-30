"""Metadata-only split, exact sample overlap, immutable candidate-only reports."""
import hashlib
import json
import threading
from pathlib import Path
import numpy as np
from Tools.SingingSegmentation import fit_score_adapter as adapter
from .store import LearningError, atomic_json, now, read_json
from . import proposals


def known_regions(recording):
    annotation = recording.get('annotation')
    if not annotation or annotation.get('confirmed') is not True or annotation.get('source_kind') != 'user':
        return []
    result = []
    for region in annotation['regions']:
        if region['label'] == 'uncertain':
            continue
        start = region['start_sample'] + region['tolerance_samples']
        end = region['end_sample'] - region['tolerance_samples']
        if end > start:
            result.append(dict(start_sample=start, end_sample=end, label=region['label']))
    return result


def groups_for(recordings):
    ids = sorted(recordings)
    parent = {r: r for r in ids}
    def find(r):
        while parent[r] != r:
            parent[r] = parent[parent[r]]
            r = parent[r]
        return r
    def union(a, b):
        a, b = find(a), find(b)
        parent[max(a, b)] = min(a, b)
    seen = {}
    for rid in ids:
        rec = recordings[rid]
        tokens = [('record', rid), ('session', rec['session_id']), ('content', rec['content_group']), ('hash', rec['audio_sha256'])]
        if rec.get('parent_recording_id'):
            tokens.append(('record', rec['parent_recording_id']))
            tokens.append(('lineage', rec['parent_recording_id']))
        tokens.extend(('lineage', x) for x in rec.get('lineage_source_ids', []))
        for token in tokens:
            if token in seen:
                union(rid, seen[token])
            else:
                seen[token] = rid
    components = {}
    for rid in ids:
        components.setdefault(find(rid), []).append(rid)
    groups = {}
    for members in components.values():
        gid = hashlib.sha256(json.dumps(members, separators=(',', ':')).encode()).hexdigest()
        groups[gid] = members
    ordered = sorted(groups)
    validation = set(ordered[-max(1, len(ordered) // 3):]) if ordered else set()
    assignment = {gid: ('validation' if gid in validation else 'train') for gid in ordered}
    by_recording = {rid: gid for gid, members in groups.items() for rid in members}
    return groups, assignment, by_recording


def dataset_summary(recordings):
    groups, assignment, by_recording = groups_for(recordings)
    seconds = dict(singing=0.0, speech=0.0, environment=0.0, excluded=0.0)
    split_mass = {s: dict(positive=0.0, negative=0.0, known_groups=set()) for s in ('train', 'validation')}
    for rid, rec in recordings.items():
        known = known_regions(rec)
        total = 0.0
        mass = split_mass[assignment[by_recording[rid]]]
        for region in known:
            duration = (region['end_sample'] - region['start_sample']) / rec['sample_rate']
            seconds[region['label']] += duration
            mass['positive' if region['label'] == 'singing' else 'negative'] += duration
            total += duration
        seconds['excluded'] += rec['duration_seconds'] - total
        if total:
            mass['known_groups'].add(by_recording[rid])
    reasons = []
    if len(groups) < 3:
        reasons.append('need_at_least_3_independent_groups')
    for split, mass in split_mass.items():
        if len(mass['known_groups']) < (2 if split == 'train' else 1):
            reasons.append(split + '_needs_more_human_confirmed_groups')
        for label in ('positive', 'negative'):
            if mass[label] <= 0:
                reasons.append(split + '_missing_' + label + '_known_duration')
        mass['known_groups'] = sorted(mass['known_groups'])
    personal_reasons = []
    if seconds['singing'] < .32:
        personal_reasons.append('need_confirmed_singing_example')
    if seconds['speech'] + seconds['environment'] < .32:
        personal_reasons.append('need_confirmed_non_singing_example')
    return dict(independent_group_count=len(groups), groups=groups, group_assignment=assignment,
                recording_groups=by_recording, label_seconds=seconds, split_known_seconds=split_mass,
                readiness_reasons=reasons, personalization_reasons=personal_reasons,
                provisional_minimum_known_seconds_per_class=.32)


def feature_arrays(feature, recording):
    source = feature.get('source', {})
    for key in ('audio_sha256', 'sample_rate', 'sample_count'):
        if type(source.get(key)) is not type(recording[key]) or source.get(key) != recording[key]:
            raise LearningError('feature_source_mismatch', key)
    timeline = feature['timeline']
    if timeline.get('coordinate_sample_rate') != recording['sample_rate']:
        raise LearningError('feature_coordinate_mismatch')
    raw_starts = timeline['source_start_samples']
    if not isinstance(raw_starts, list) or not raw_starts or any(type(x) is not int for x in raw_starts):
        raise LearningError('invalid_feature_grid')
    edges = np.asarray(raw_starts + [recording['sample_count']], dtype=np.int64)
    if edges[0] != 0 or np.any(np.diff(edges) <= 0):
        raise LearningError('invalid_feature_grid')
    features, metadata = adapter.build_features(timeline['scores'], timeline['columns'], return_metadata=True)
    if len(features) != len(edges) - 1:
        raise LearningError('feature_grid_score_mismatch')
    return edges, features, metadata


def cell_masses(recording, edges):
    pos, neg = np.zeros(len(edges) - 1), np.zeros(len(edges) - 1)
    fine = {key: np.zeros(len(edges) - 1) for key in ('singing', 'speech', 'environment')}
    for region in known_regions(recording):
        overlap = np.maximum(0, np.minimum(edges[1:], region['end_sample']) - np.maximum(edges[:-1], region['start_sample']))
        seconds = overlap / recording['sample_rate']
        fine[region['label']] += seconds
        if region['label'] == 'singing':
            pos += seconds
        else:
            neg += seconds
    return pos, neg, fine


def prediction_report(recording, edges, scores):
    positive, negative, fine = cell_masses(recording, edges)
    selected = scores >= .5
    confusion = dict(tp_seconds=float(positive[selected].sum()), fn_seconds=float(positive[~selected].sum()),
                     fp_seconds=float(negative[selected].sum()), tn_seconds=float(negative[~selected].sum()))
    ranges, start = [], None
    for i, keep in enumerate(selected):
        if keep and start is None:
            start = int(edges[i])
        if start is not None and (not keep or i == len(selected) - 1):
            end = int(edges[i] if not keep else edges[i + 1])
            ranges.append(dict(start_sample=start, end_sample=end, status='candidate'))
            start = None
    return dict(recording_id=recording['recording_id'], audio_sha256=recording['audio_sha256'],
                sample_rate=recording['sample_rate'], sample_count=recording['sample_count'],
                annotation_revision=recording['annotation_revision'], source_kind=recording.get('annotation', {}).get('source_kind') if recording.get('annotation') else 'unknown',
                threshold=.5, score_name='uncalibrated_score', scores=scores.tolist(), source_cell_edges=edges.tolist(),
                predicted_ranges=ranges, known_duration_confusion=confusion,
                known_label_seconds={k: float(v.sum()) for k, v in fine.items()},
                metric_scope='only explicit known samples after tolerance exclusion; no unknown negatives',
                boundary_scope='native acoustic cell endpoints, not verified vocal boundaries',
                execution_authorized=False)


class Pipeline:
    def __init__(self, store, backend):
        self.store, self.backend = store, backend
        # Foreground proposals and background learning share one lazy extractor.
        # Serialize cache creation/model initialization, not the entire training job.
        self._feature_lock = threading.RLock()

    def feature(self, profile, recording):
        with self._feature_lock:
            return self._feature_locked(profile, recording)

    def _feature_locked(self, profile, recording):
        identity = self.backend.identity()
        path = self.store.profile_path(profile) / 'features' / (recording['audio_sha256'] + '-' + identity + '.json')
        if path.exists():
            result = read_json(path)
        else:
            result = self.backend.extract(self.store.audio_path(profile, recording['recording_id']))
            feature_arrays(result, recording)
            result['learning_backend_identity'] = identity
            result['execution_authorized'] = False
            atomic_json(path, result)
        if result.get('learning_backend_identity') != identity:
            raise LearningError('feature_backend_identity_mismatch')
        feature_arrays(result, recording)
        self.store.update_feature(profile, recording['recording_id'], feature_status='ready',
                                  feature_backend_identity=identity, feature_error=None)
        return result

    def extract(self, profile, recording_id):
        rec = self.store.recording(profile, recording_id)
        try:
            value = self.feature(profile, rec)
            return dict(recording_id=recording_id, status='ready', backend_identity=value['learning_backend_identity'],
                        source=value['source'], cell_count=len(value['timeline']['scores']), execution_authorized=False)
        except Exception as exc:
            self.store.update_feature(profile, recording_id, feature_status='failed', feature_error=str(exc)[:400])
            raise

    def cached_proposal(self, profile, recording_id):
        rec, _, binding = proposals.plan(self.store, self.backend, profile, recording_id)
        try:
            value = self.store.proposal(profile, recording_id, binding['proposal_id'])
            return dict(status='ready', proposal=value)
        except LearningError as exc:
            if exc.code != 'proposal_not_found':
                raise
        if rec.get('proposal_id'):
            return dict(status='stale', proposal=self.store.proposal(profile, recording_id, rec['proposal_id']))
        return dict(status='missing')

    def propose(self, profile, recording_id):
        rec, head, binding = proposals.plan(self.store, self.backend, profile, recording_id)
        try:
            return self.store.proposal(profile, recording_id, binding['proposal_id'])
        except LearningError as exc:
            if exc.code != 'proposal_not_found':
                raise
        frozen = self.feature(profile, rec)
        if binding['model_source'] == 'personal_category_evidence':
            try:
                from . import semantic_pipeline
                fine, prediction = semantic_pipeline.proposal_prediction(self, profile, rec, binding)
                result = semantic_pipeline.make_proposal(self, profile, rec, frozen, binding, fine, prediction)
                return self.store.save_proposal(profile, recording_id, result)
            except Exception:
                rec, head, binding = proposals.plan(self.store, self.backend, profile, recording_id, allow_semantic=False)
        if binding['model_source'] == 'personal_local_boundary':
            try:
                from . import boundary_pipeline
                fine, prediction = boundary_pipeline.proposal_prediction(self, profile, rec, binding)
                result = boundary_pipeline.make_proposal(self, profile, rec, frozen, binding, fine, prediction)
                return self.store.save_proposal(profile, recording_id, result)
            except Exception:
                rec, head, binding = proposals.plan(self.store, self.backend, profile, recording_id, allow_boundary=False, allow_semantic=False)
        if binding['model_source'] == 'personal_sequence_boundary':
            try:
                from . import sequence_pipeline
                fine, prediction = sequence_pipeline.proposal_prediction(self, profile, rec, binding)
                result = sequence_pipeline.make_proposal(self, profile, rec, frozen, binding, fine, prediction)
                return self.store.save_proposal(profile, recording_id, result)
            except Exception:
                rec, head, binding = proposals.plan(self.store, self.backend, profile, recording_id, allow_sequence=False, allow_boundary=False, allow_semantic=False)
        edges, features, metadata = feature_arrays(frozen, rec)
        fusion_scores = None
        if binding['model_source'] == 'personal_temporal_fusion':
            try:
                from . import fusion_pipeline
                fusion_scores = fusion_pipeline.proposal_scores(self, profile, rec, frozen, binding)
            except Exception:
                # Optional pitch/cache failures never block an ordinary proposal.
                rec, head, binding = proposals.plan(self.store, self.backend, profile, recording_id, allow_fusion=False, allow_sequence=False, allow_boundary=False, allow_semantic=False)
        result = proposals.make_proposal(rec, frozen, head, binding, edges, features, metadata,
                                        self.store.audio_path(profile, recording_id), fusion_scores=fusion_scores)
        return self.store.save_proposal(profile, recording_id, result)

    def _save_candidate(self, profile, report, records):
        # Independent diagnostics cannot turn a successful legacy fit into a
        # failed job. The helper records rejection/failure with explicit reasons.
        try:
            from . import fusion_pipeline
            report['temporal_fusion'] = fusion_pipeline.train(self, profile, records)
        except Exception as exc:
            report['temporal_fusion'] = dict(status='failed', teaching_eligible=False,
                eligibility_reasons=['fusion_diagnostic_failed'], error=str(exc)[:600],
                execution_authorized=False, production_eligible=False)
        if self.store.config.sequence_boundary_enabled:
            try:
                if self.store.config.local_boundary_enabled:
                    from . import boundary_pipeline
                    if self.store.config.category_evidence_enabled:
                        from . import semantic_pipeline
                        report['category_evidence'] = semantic_pipeline.train(self, profile, records)
                        report['local_boundary'] = semantic_pipeline.local_boundary_report(report['category_evidence'])
                    else:
                        report['local_boundary'] = boundary_pipeline.train(self, profile, records)
                    report['sequence_boundary'] = boundary_pipeline.sequence_report(report['local_boundary'])
                else:
                    from . import sequence_pipeline
                    report['sequence_boundary'] = sequence_pipeline.train(self, profile, records)
            except Exception as exc:
                report['sequence_boundary'] = dict(status='failed', teaching_eligible=False,
                    eligibility_reasons=['sequence_diagnostic_failed'], error=str(exc)[:600],
                    execution_authorized=False, production_eligible=False)
                if self.store.config.local_boundary_enabled:
                    report['local_boundary'] = dict(status='failed', teaching_eligible=False,
                        eligibility_reasons=['local_boundary_diagnostic_failed'], error=str(exc)[:600],
                        execution_authorized=False, production_eligible=False)
                    if self.store.config.category_evidence_enabled:
                        report['category_evidence'] = dict(status='failed', teaching_eligible=False,
                            eligibility_reasons=['category_evidence_diagnostic_failed'], error=str(exc)[:600],
                            execution_authorized=False, production_eligible=False)
        return self.store.save_model(profile, report)

    def train(self, profile, model_id):
        state = self.store.snapshot(profile)
        try:
            return self._train(profile, model_id, state)
        except Exception as exc:
            return self.store.save_model(profile, dict(model_id=model_id, created_at=now(), status='failed',
                source_dataset_revision=state['profile']['dataset_revision'], source_profile_id=profile,
                source_snapshot=state['recordings'], dataset_summary=dataset_summary(state['recordings']),
                reasons=[getattr(exc, 'code', getattr(exc, 'reason', type(exc).__name__))],
                error=str(exc)[:600], execution_authorized=False,
                evaluation_scope='development_validation_resplit_not_frozen_blind'))

    def _train(self, profile, model_id, state):
        records = state['recordings']
        summary = dataset_summary(records)
        report = dict(model_id=model_id, created_at=now(), status='insufficient_data',
                      source_dataset_revision=state['profile']['dataset_revision'], source_profile_id=profile,
                      source_snapshot=records, dataset_summary=summary, reasons=summary['readiness_reasons'],
                      execution_authorized=False, evaluation_scope='development_validation_resplit_not_frozen_blind',
                      weighting_scope='equal training connected-group known duration; not unique-PCM deduplication',
                      protocol=dict(split='metadata connected components; SHA sorted final floor(n/3) validation',
                                    lambda_l2=.1, threshold=.5, smoothing=False, gap_fill=False,
                                    source_filter='explicit_human_user_only', normalization_scope='train_only'))
        if report['reasons']:
            if summary['personalization_reasons']:
                report['reasons'] = summary['personalization_reasons']
                return self.store.save_model(profile, report)
            return self._train_provisional(profile, report, records, summary)
        report['model_stage'] = 'development_validated'
        prepared, feature_metadata = {}, None
        for rid, rec in records.items():
            if not known_regions(rec):
                continue
            frozen = self.feature(profile, rec)
            edges, features, metadata = feature_arrays(frozen, rec)
            if feature_metadata is not None and metadata != feature_metadata:
                raise LearningError('mixed_feature_definitions')
            feature_metadata = metadata
            pos, neg, _ = cell_masses(rec, edges)
            prepared[rid] = dict(edges=edges, features=features, positive=pos, negative=neg,
                                 backend_identity=frozen['learning_backend_identity'],
                                 model_metadata=frozen.get('model_metadata'))
        group_total = {}
        for rid, item in prepared.items():
            gid = summary['recording_groups'][rid]
            if summary['group_assignment'][gid] == 'train':
                group_total[gid] = group_total.get(gid, 0) + float((item['positive'] + item['negative']).sum())
        xs, ys, weights = [], [], []
        for rid, item in prepared.items():
            gid = summary['recording_groups'][rid]
            if summary['group_assignment'][gid] != 'train':
                continue
            mass = item['positive'] + item['negative']
            valid = mass > 0
            xs.append(item['features'][valid])
            ys.append(item['positive'][valid] / mass[valid])
            weights.append(mass[valid] / group_total[gid])
        model = adapter.fit_score_adapter(np.concatenate(xs), np.concatenate(ys), np.concatenate(weights), feature_metadata=feature_metadata)
        predictions = []
        for rid, item in prepared.items():
            gid = summary['recording_groups'][rid]
            predicted = prediction_report(records[rid], item['edges'], adapter.predict_uncalibrated_score(model, item['features']))
            predicted.update(group_id=gid, split=summary['group_assignment'][gid])
            predictions.append(predicted)
        confusion = {k: sum(p['known_duration_confusion'][k] for p in predictions if p['split'] == 'validation')
                     for k in ('tp_seconds', 'fn_seconds', 'fp_seconds', 'tn_seconds')}
        report.update(status='candidate', adapter=model, predictions=predictions,
                      validation_duration_confusion=confusion,
                      feature_provenance={rid: {k: value[k] for k in ('backend_identity', 'model_metadata')} for rid, value in prepared.items()})
        return self._save_candidate(profile, report, records)

    def _train_provisional(self, profile, report, records, summary):
        """Learn from confirmed examples immediately; never invent a holdout set."""
        prepared, feature_metadata = {}, None
        group_total = {}
        for rid, rec in records.items():
            if not known_regions(rec):
                continue
            frozen = self.feature(profile, rec)
            edges, features, metadata = feature_arrays(frozen, rec)
            if feature_metadata is not None and metadata != feature_metadata:
                raise LearningError('mixed_feature_definitions')
            feature_metadata = metadata
            pos, neg, _ = cell_masses(rec, edges)
            prepared[rid] = dict(edges=edges, features=features, positive=pos, negative=neg,
                                 backend_identity=frozen['learning_backend_identity'], model_metadata=frozen.get('model_metadata'))
            gid = summary['recording_groups'][rid]
            group_total[gid] = group_total.get(gid, 0.) + float((pos + neg).sum())
        xs, ys, weights = [], [], []
        for rid, item in prepared.items():
            mass = item['positive'] + item['negative']
            valid = mass > 0
            xs.append(item['features'][valid])
            ys.append(item['positive'][valid] / mass[valid])
            weights.append(mass[valid] / group_total[summary['recording_groups'][rid]])
        model = adapter.fit_score_adapter(np.concatenate(xs), np.concatenate(ys), np.concatenate(weights), feature_metadata=feature_metadata)
        predictions = []
        for rid, item in prepared.items():
            prediction = prediction_report(records[rid], item['edges'], adapter.predict_uncalibrated_score(model, item['features']))
            prediction.update(group_id=summary['recording_groups'][rid], split='training_only')
            predictions.append(prediction)
        report.update(status='candidate', model_stage='provisional', adapter=model, predictions=predictions,
                      evaluation_scope='training_only_unvalidated_personalization',
                      validation_available=False, validation_reasons=report['reasons'], reasons=[],
                      feature_provenance={rid: {k: value[k] for k in ('backend_identity', 'model_metadata')} for rid, value in prepared.items()})
        report['protocol'].update(split='none: all confirmed examples fit a provisional head; no validation claim',
                                  provisional_minimum_known_seconds_per_class=.32)
        return self._save_candidate(profile, report, records)

    def shadow(self, profile, recording_id, model_id=None):
        state = self.store.snapshot(profile)
        current = state.get('current_candidate')
        if model_id is None:
            if not current or current.get('invalidated', False):
                raise LearningError('no_current_candidate', 'Train after correcting candidate source labels; an explicit old model remains inspectable', 409)
            model_id = current['model_id']
        model = self.store.model(profile, model_id)
        if model['status'] != 'candidate':
            raise LearningError('model_not_candidate', status=409)
        rec = self.store.recording(profile, recording_id)
        frozen = self.feature(profile, rec)
        expected = {v['backend_identity'] for v in model['feature_provenance'].values()}
        if expected != {frozen['learning_backend_identity']}:
            raise LearningError('model_feature_backend_mismatch', status=409)
        edges, features, _ = feature_arrays(frozen, rec)
        result = prediction_report(rec, edges, adapter.predict_uncalibrated_score(model['adapter'], features))
        latest = self.store.snapshot(profile)
        result.update(model_id=model_id, status='shadow',
                      stale=model['source_dataset_revision'] != latest['profile']['dataset_revision'],
                      invalidated=self.store.model_invalidated(model, latest),
                      source_dataset_revision=model['source_dataset_revision'],
                      trained_dataset_revision=model['source_dataset_revision'],
                      current_dataset_revision=latest['profile']['dataset_revision'])
        return result
