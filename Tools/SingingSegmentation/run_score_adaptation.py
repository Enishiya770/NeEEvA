"""Run a frozen, source-grouped development experiment on saved acoustic scores.

No audio inference, playback, network, production imports, or executable cuts.
Uses the user-confirmed binary labels, including explicit environment corrections.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import io
import json
from pathlib import Path
import sys
import time
import wave

import numpy as np

import evaluate_human_annotations as human
import fit_score_adapter as adapter


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + '\n', encoding='utf-8')


def group_lookup(groups, recording_ids):
    result = {}
    for group, ids in groups.items():
        for key in ids:
            if key in result:
                raise ValueError('recording_in_multiple_groups:' + key)
            result[key] = group
    if set(result) != set(recording_ids):
        raise ValueError('groups_must_cover_exact_recording_set')
    return result


def cell_sum(values, edges):
    cumulative = np.r_[0., np.cumsum(values, dtype=np.float64)]
    return cumulative[np.asarray(edges[1:])] - cumulative[np.asarray(edges[:-1])]


def assemble_training(records, training_ids, parent_groups):
    """Every parent receives equal mass; only selected training records enter fit."""
    selected = [records[key] for key in training_ids]
    totals = {}
    for key, record in zip(training_ids, selected):
        group = parent_groups[key]
        totals[group] = totals.get(group, 0.) + float(record['train_mass'].sum())
    xs, ys, weights = [], [], []
    per_record = []
    for key, record in zip(training_ids, selected):
        group = parent_groups[key]
        valid = record['train_mass'] > 0
        if not valid.any():
            continue
        if totals[group] <= 0:
            raise ValueError('empty_parent_training_mass')
        w = record['train_mass'][valid] / totals[group]
        y = record['train_positive_mass'][valid] / record['train_mass'][valid]
        xs.append(record['features'][valid]); ys.append(y); weights.append(w)
        per_record.append({'id': key, 'parent_group': group, 'native_cells_with_training_mass': int(valid.sum()),
                           'physical_known_seconds': float(record['train_mass'].sum()),
                           'physical_positive_seconds': float(record['train_positive_mass'].sum()),
                           'parent_normalized_weight': float(w.sum())})
    if not xs:
        raise ValueError('no_training_mass')
    x, y, weight = np.concatenate(xs), np.concatenate(ys), np.concatenate(weights)
    return x, y, weight, {'training_ids': list(training_ids), 'parent_physical_seconds': totals,
                         'records': per_record, 'weighted_positive_mass': float(np.dot(weight, y)),
                         'weighted_negative_mass': float(np.dot(weight, 1-y))}


def load_records(protocol, provider_dir):
    inputs = protocol['inputs']
    for key, item in inputs.items():
        if digest(item['path']) != item['sha256']:
            raise ValueError('frozen_input_changed:' + key)
    annotations = Path(inputs['annotations']['path'])
    confirmation_file = Path(inputs['confirmation']['path'])
    document = human.read_json(annotations)
    manifest = human.read_json(inputs['manifest']['path'])
    manifest_by_id = {entry['id']: entry for entry in manifest}
    confirmed, confirmation = human.validate_confirmation(annotations, confirmation_file, inputs['manifest']['sha256'])
    overrides = confirmation['content'].get('sample_label_overrides', [])
    unresolved = confirmation['content'].get('unresolved_singing_boundaries', [])
    conflicts, containment = human.audit_containment(document['recordings'], manifest_by_id, confirmed, overrides)
    if any(mask.any() for mask in conflicts.values()):
        raise ValueError('unresolved_same_pcm_label_conflict')
    duplicate = protocol['training']['duplicate_weighting']
    aliases = containment['aliases']
    if not any({a['contained_recording'], a['container_recording']} == set(duplicate['recordings'])
               and a['container_offset_sample'] == 0 and a['sample_count'] == duplicate['end_sample']
               and a['exact_pcm_bytes_equal'] for a in aliases):
        raise ValueError('duplicate_weighting_requires_verified_exact_pcm_alias')
    records, audit = {}, []
    for recording in document['recordings']:
        key, source = recording['recording_id'], recording['source']
        entry = manifest_by_id[key]
        raw = Path(entry['wav']).read_bytes()
        if hashlib.sha256(raw).hexdigest() != source['audio_sha256'] or source['audio_sha256'] != entry['sha256']:
            raise ValueError('wav_hash_mismatch:' + key)
        with wave.open(io.BytesIO(raw), 'rb') as wav:
            if (wav.getframerate(), wav.getnframes(), wav.getnchannels(), wav.getsampwidth()) != (source['sample_rate'], source['sample_count'], 1, 2):
                raise ValueError('wav_coordinates_mismatch:' + key)
        result, scores, edges, score_audit = human.verify_scores(provider_dir / (key + '.json'), source)
        features, metadata = adapter.build_features(scores, result['timeline']['columns'], return_metadata=True)
        masks = human.truth_masks(recording, key in confirmed, [x for x in overrides if x['recording_id'] == key])
        train_known = masks['confident'].copy()
        if key in protocol['training']['excluded_positive_recordings']:
            train_known &= ~masks['singing']
        physical_weight = np.ones(source['sample_count'], dtype=np.float64)
        if key in duplicate['recordings']:
            physical_weight[duplicate['start_sample']:duplicate['end_sample']] = duplicate['each_copy_weight']
        # Accumulate exact half-sample integer units before converting to time.
        # Summing fractional seconds first can yield a pure-positive cell y>1
        # by cancellation, even though its two integer counts are identical.
        sample_units = physical_weight * 2 * train_known
        if not np.isin(sample_units, (0., 1., 2.)).all():
            raise ValueError('unsupported_physical_sample_weight')
        train_mass = cell_sum(sample_units, edges) / (2*source['sample_rate'])
        positive_mass = cell_sum(sample_units * masks['singing'], edges) / (2*source['sample_rate'])
        singing_indices = [result['timeline']['columns'].index(name) for name in
                           ('Singing', 'Male singing', 'Female singing', 'Child singing') if name in result['timeline']['columns']]
        records[key] = {'source': source, 'recording': recording, 'features': features, 'feature_metadata': metadata,
                        'edges': edges, 'masks': masks, 'baseline_score': scores[:, singing_indices].max(axis=1),
                        'train_mass': train_mass,
                        'train_positive_mass': positive_mass,
                        'unresolved': [x for x in unresolved if x['recording_id'] == key],
                        'model_version': result['model_version']}
        audit.append({'id': key, 'source': source, 'wav': entry['wav'], 'saved_scores': score_audit,
                      'native_cells': len(features), 'training_known_seconds_before_duplicate_weights': int(train_known.sum()) / source['sample_rate'],
                      'excluded_positive_training_seconds': int((masks['confident'] & masks['singing'] & ~train_known).sum()) / source['sample_rate'],
                      'duplicate_adjusted_training_seconds': float(sample_units.sum()/(2*source['sample_rate'])), 'feature_metadata': metadata})
    return records, {'sources': audit, 'containment': containment}


def validate_folds(groups, parent_groups, records):
    group_lookup(groups, records)
    folds = []
    for name, test_ids in groups.items():
        test_ids = list(test_ids)
        train_ids = sorted(set(records) - set(test_ids))
        if {parent_groups[k] for k in train_ids} & {parent_groups[k] for k in test_ids}:
            raise ValueError('shared_parent_crosses_split:' + name)
        _, _, _, audit = assemble_training(records, train_ids, parent_groups)
        eligible = audit['weighted_positive_mass'] > 0 and audit['weighted_negative_mass'] > 0
        folds.append({'name': name, 'training_ids': train_ids, 'test_ids': test_ids,
                      'two_class_training_eligible': eligible, 'training_audit': audit})
    return folds


def evaluate_record(model, scheme, key, record, frame_scores, threshold, method):
    prediction = np.repeat(frame_scores >= threshold, np.diff(record['edges']))
    spans = human.ranges(prediction)
    source = record['source']
    values = human.rates(human.measurements(prediction, record['masks'], source['sample_rate'], 'confident'))
    boundaries = human.boundary_matches(spans, record['recording']['annotations'], source['sample_rate'], unresolved=record['unresolved'])
    return {'model': model, 'scheme': scheme, 'id': key, 'method': method, 'threshold': threshold,
            'metrics': values, 'prediction_ranges_samples': spans,
            'prediction_ranges_seconds': [[a/source['sample_rate'], b/source['sample_rate']] for a,b in spans],
            'boundaries': boundaries, 'execution_authorized': False}


def aggregate(rows):
    combinations = sorted({(r['model'], r['scheme'], r['method'], r['threshold']) for r in rows})
    output = []
    for model, scheme, method, threshold in combinations:
        chosen = [r for r in rows if (r['model'],r['scheme'],r['method'],r['threshold']) == (model,scheme,method,threshold)]
        for subset in ('all_seven_primary', 'excluding_c9_sensitivity', 'c9_diagnostic'):
            selected = [r for r in chosen if subset == 'all_seven_primary' or (subset == 'c9_diagnostic') == (r['id'] == 'c9')]
            values = {k: sum(r['metrics'][k] for r in selected) for k in chosen[0]['metrics'] if k.endswith('_seconds')}
            # rates() also publishes derived denominators; do not sum their names twice.
            values.pop('singing_seconds', None); values.pop('non_singing_seconds', None)
            bounds = [b for r in selected for b in r['boundaries']]
            usable = [b for b in bounds if b['boundary_truth_status'] == 'usable']
            output.append({'model': model, 'scheme': scheme, 'method': method, 'threshold': threshold, 'subset': subset,
                           'recording_ids': [r['id'] for r in selected], 'metrics': human.rates(values),
                           'matched_complete_intervals': sum(b['both_endpoints_within_tolerance'] for b in usable),
                           'usable_boundary_intervals': len(usable), 'unresolved_boundary_intervals': len(bounds)-len(usable),
                           'predicted_positive_recordings': sum(bool(r['prediction_ranges_samples']) for r in selected),
                           'fully_missed_singing_recordings': sum(r['metrics']['singing_seconds'] > 0 and r['metrics']['tp_seconds'] == 0 for r in selected)})
    return output


def run(protocol_path, output):
    if output.exists():
        raise ValueError('use_new_output_directory')
    protocol_bytes = protocol_path.read_bytes()
    protocol_sha = hashlib.sha256(protocol_bytes).hexdigest()
    protocol = json.loads(protocol_bytes)
    if protocol['schema'] != 'neeeva.score-adaptation-protocol.v1' or protocol['execution_authorized'] is not False:
        raise ValueError('unsupported_protocol')
    # This implementation is deliberately the fixed experiment, not a tuner.
    if (protocol['fit']['lambda_l2'], protocol['fit']['max_iterations'], protocol['logit_clip'], protocol['standard_deviation_floor']) != (.1, 200, 1e-5, 1e-6):
        raise ValueError('frozen_hyperparameters_mismatch')
    code_paths = [Path(__file__), Path(human.__file__), Path(adapter.__file__)]
    code_hashes = {str(p.resolve()): digest(p) for p in code_paths}
    output.mkdir(parents=True)
    write_json(output/'protocol.json', protocol)
    results, fold_audits, provenance, models, prediction_files, timing = [], [], [], [], [], []
    experiment_start = time.perf_counter()
    for model_name, directory in protocol['models'].items():
        records, source_audit = load_records(protocol, Path(directory))
        ids = sorted(records)
        parents = group_lookup(protocol['parent_source_groups'], ids)
        strict = validate_folds(protocol['conservative_song_groups'], parents, records)
        provenance.append({'model':model_name, **source_audit, 'strict_song_eligibility':strict,
                           'strict_song_training_performed':False, 'strict_song_complete_validation_possible':all(f['two_class_training_eligible'] for f in strict)})
        metadata = records[ids[0]]['feature_metadata']
        if any(r['feature_metadata'] != metadata for r in records.values()):
            raise ValueError('inconsistent_provider_features')
        for scheme, groups in [('parent_source_leave_group_out', protocol['parent_source_groups']),
                                ('content_leave_group_out', protocol['content_groups'])]:
            folds = validate_folds(groups, parents, records)
            for fold in folds:
                if not fold['two_class_training_eligible']:
                    raise ValueError('declared_development_fold_lacks_training_class')
                x, y, weight, train_audit = assemble_training(records, fold['training_ids'], parents)
                fit_start = time.perf_counter()
                fitted = adapter.fit_score_adapter(x, y, weight, feature_metadata=metadata)
                fit_seconds = time.perf_counter()-fit_start
                fitted['experiment_provenance'] = {'protocol_sha256':protocol_sha, 'model':model_name, 'scheme':scheme,
                    'fold':fold['name'], 'training_ids':fold['training_ids'], 'test_ids':fold['test_ids'],
                    'warning':'Internal development folds share speaker/song; not blind new-song acceptance.'}
                folder = output/'folds'/model_name/scheme/fold['name']; folder.mkdir(parents=True)
                write_json(folder/'adapter.json', fitted)
                fold_audits.append({'model':model_name, 'scheme':scheme, 'fold':fold['name'], **train_audit,
                                   'test_ids':fold['test_ids'], 'fit_seconds':fit_seconds,
                                   'artifact':str((folder/'adapter.json').resolve()), 'artifact_sha256':digest(folder/'adapter.json')})
                for key in fold['test_ids']:
                    record = records[key]
                    start = time.perf_counter()
                    predictions = adapter.predict_uncalibrated_score(fitted, record['features'])
                    elapsed = time.perf_counter()-start
                    if predictions.shape != (len(record['features']),) or not np.isfinite(predictions).all():
                        raise ValueError('invalid_model_predictions')
                    prediction_path = folder/(key+'-scores.json')
                    write_json(prediction_path, {'id':key, 'source':record['source'], 'source_edges_samples':record['edges'],
                        'uncalibrated_score':predictions.tolist(), 'artifact_sha256':digest(folder/'adapter.json'), 'execution_authorized':False})
                    prediction_files.append({'path':str(prediction_path.resolve()), 'sha256':digest(prediction_path)})
                    timing.append({'model':model_name,'scheme':scheme,'id':key,'native_cells':len(predictions),
                                   'head_prediction_seconds':elapsed,'excludes_backbone_and_network':True})
                    for threshold in protocol['adapter_thresholds']:
                        results.append(evaluate_record(model_name,scheme,key,record,predictions,threshold,'linear_score_adapter'))
                    for threshold in protocol['baseline_thresholds']:
                        results.append(evaluate_record(model_name,scheme,key,record,record['baseline_score'],threshold,'frozen_family_threshold'))
        x,y,w,training = assemble_training(records,ids,parents)
        fitted = adapter.fit_score_adapter(x,y,w,feature_metadata=metadata)
        fitted['experiment_provenance'] = {'protocol_sha256':protocol_sha,'model':model_name,'training_ids':ids,
            'status':'fitted_on_all_development_data_for_offline_experiments_only','held_out_acceptance':False}
        model_folder=output/'full-development-adapters'; model_folder.mkdir(exist_ok=True)
        artifact_path=model_folder/(model_name+'.json'); write_json(artifact_path,fitted)
        # Benchmark the tiny head only, after warm-up, over a fixed saved recording.
        features=records['c15']['features']
        adapter.predict_uncalibrated_score(fitted,features)
        durations=[]
        for _ in range(100):
            start=time.perf_counter(); adapter.predict_uncalibrated_score(fitted,features); durations.append(time.perf_counter()-start)
        models.append({'model':model_name,'path':str(artifact_path.resolve()),'sha256':digest(artifact_path),'training_audit':training,
                       'head_only_c15_latency_seconds':{'median':float(np.median(durations)),'p95':float(np.quantile(durations,.95)),
                       'runs':100,'scope':'Already computed feature matrix -> small head scores, CPU only. Excludes acoustic backbone, ASR, network and audio.'}})
        print(json.dumps({'model':model_name,'completed_development_fits':len(protocol['parent_source_groups'])+len(protocol['content_groups'])+1},ensure_ascii=False),flush=True)
    for p in code_paths:
        if code_hashes[str(p.resolve())] != digest(p):
            raise ValueError('code_changed_during_experiment')
    if digest(protocol_path) != protocol_sha:
        raise ValueError('protocol_changed_during_experiment')
    summary={'schema':'neeeva.score-adaptation-results.v1','protocol_sha256':protocol_sha,'aggregate':aggregate(results),
             'per_recording':results,'folds':fold_audits,'full_development_adapters':models,'prediction_timings':timing,
             'execution_authorized':False,'blind_validation':False,'acoustic_backbone_inference_count':0,
             'wall_seconds':time.perf_counter()-experiment_start}
    write_json(output/'results.json',summary)
    write_json(output/'provenance.json',{'protocol_path':str(protocol_path.resolve()),'protocol_sha256':protocol_sha,
        'code':code_hashes,'python':sys.version,'numpy':np.__version__,'sources':provenance,'prediction_files':prediction_files})
    write_report(output/'report.md',summary)
    print(json.dumps({'output':str(output),'fits':len(fold_audits)+len(models),'prediction_files':len(prediction_files),
                      'wall_seconds':summary['wall_seconds'],'production_changes':0},ensure_ascii=False))


def write_report(path, result):
    lines=['# 固定轻量分数读出实验', '',
           '所有参数和阈值在拟合前冻结。只拟合九特征线性头，复用已保存的声学分数；没有声学主模型推理、音频裁剪或生产部署。', '',
           '用户在拟合前补充c9起点约3.7秒；原波形约3.72–3.75秒起振，与其相容。v3人工起点保留原±50ms容差，c9恢复参与训练；主表包括七份，排除c9的敏感性及c9单独结果均保留在JSON。', '',
           'source分组只排除共同原PCM泄漏，仍有同段重唱；content分组将第二段所有重唱同组，但仍是同人、可能同歌的开发诊断。严格歌曲组留出会缺训练正例，所以没有声称获得新歌验证。', '',
           '| 模型 | 开发分组 | 方法 | 阈值 | 歌唱保留 | 非歌唱误收秒 | 漏唱秒 | 整段两端命中 |',
           '|---|---|---|---:|---:|---:|---:|---:|']
    for row in result['aggregate']:
        if row['subset']!='all_seven_primary':continue
        m=row['metrics']; recall='—' if m['recall'] is None else f"{100*m['recall']:.2f}%"
        lines.append(f"| {row['model']} | {row['scheme']} | {row['method']} | {row['threshold']} | {recall} | {m['fp_seconds']:.3f} | {m['fn_seconds']:.3f} | {row['matched_complete_intervals']}/{row['usable_boundary_intervals']} |")
    lines += ['', '表中全部固定阈值均报告，不以外层结果选最佳阈值。高阈值全部拒绝不能算成功。未做平滑或拼接，不能用跨过讲话的连段制造边界通过。', '',
              'folds目录保存每折标准化参数、权重、训练/留出ID；full-development-adapters是使用全部开发材料拟合的离线模型，不能拿训练预测当留出成绩。scores没有校准为概率。', '',
              '速度只测已生成特征矩阵到小头分数，既不含声学底座，也不等于Unity首音。原底座输出仍有320ms/40ms栅格及宽上下文，分类改善不自动证明首尾字音可靠。']
    Path(path).write_text('\n'.join(lines)+'\n',encoding='utf-8')


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--protocol',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    run(args.protocol,args.output)
