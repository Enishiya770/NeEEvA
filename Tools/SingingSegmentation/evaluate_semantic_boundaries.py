"""Frozen category/continuation experiment, requiring an explicit fixed policy."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import sys

os.environ.setdefault('OPENBLAS_NUM_THREADS', '2')
os.environ.setdefault('OMP_NUM_THREADS', '2')
if __package__ in (None, ''):
    sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from Server.SingingLearning import semantic_evaluation, sequence_features, pitch_features
from Tools.SingingSegmentation.evaluate_local_boundaries import prepare
from Tools.SingingSegmentation.evaluate_sequence_boundaries import save, progress
from Tools.SingingSegmentation import evaluate_personal_learning as ev
from Server.SingingLearning import temporal_fusion as fusion


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--profile-cache', type=Path, required=True)
    parser.add_argument('--predeclared-policy', type=Path)
    parser.add_argument('--prepare-only', action='store_true')
    args = parser.parse_args(argv)
    if not args.prepare_only:
        if args.predeclared_policy is None:
            raise ValueError('fixed_predeclared_policy_required_before_candidate_evaluation')
        policy = ev.read_json(args.predeclared_policy)
        if not (policy.get('frozen_before_candidate_evaluation') is True or
                policy.get('frozen_before_new_candidate_evaluation') is True):
            raise ValueError('candidate_policy_not_declared_frozen')
        policy_sha = ev.file_sha(args.predeclared_policy)
    args.output.mkdir(parents=True, exist_ok=True)
    data = prepare(args.snapshot, args.output, args.profile_cache)
    coverage = semantic_evaluation.validate_prefix_partition(data['fine'], data['groups'])
    metadata_coverage = semantic_evaluation.validate_prefix_partition(data['fine'], data['metadata_groups'])
    if args.prepare_only:
        report = dict(schema='neeeva.semantic-boundary-preparation.v1', candidate_evaluated=False,
            source_dataset_revision=data['payload']['dataset_revision'], source_files=data['loaded'],
            source_snapshot_sha256=fusion.digest(data['loaded']), verified_cache_manifest=data['cache_manifest'],
            prefix_fold_coverage=coverage, metadata_fold_coverage=metadata_coverage,
            preparation_seconds=data['preparation_seconds'], execution_authorized=False)
        save(args.output / 'preparation.json', report)
        progress({key: report[key] for key in ('candidate_evaluated', 'prefix_fold_coverage', 'metadata_fold_coverage', 'preparation_seconds')})
        return report
    from Server.SingingLearning import semantic_model, boundary_model, sequence_model
    modules = [('semantic_evaluation', semantic_evaluation), ('semantic_model', semantic_model),
               ('boundary_model', boundary_model), ('sequence_model', sequence_model),
               ('sequence_features', sequence_features), ('pitch_features', pitch_features)]
    sources = {name: ev.file_sha(module.__file__) for name, module in modules}
    report = semantic_evaluation.evaluate_prepared(data['records'], data['fine'], data['native'], data['acoustic'],
        data['bootstrap'], data['groups'], metadata_groups=data['metadata_groups'], progress=progress, retain_predictions=True)
    report.update(source_dataset_revision=data['payload']['dataset_revision'],
        source_snapshot_sha256=fusion.digest(data['loaded']), source_files=data['loaded'],
        exact_prefix_relations=data['relations'], metadata_group_count=len(data['metadata_groups']),
        exact_prefix_component_count=len(data['groups']), preparation_seconds=data['preparation_seconds'],
        verified_cache_manifest=data['cache_manifest'], code_sha256=sources,
        predeclared_policy_sha256=policy_sha, predeclared_policy=policy,
        extraction_policy='reuse source-verified fine63 cache only; no feature re-extraction')
    for path, expected in data['loaded'].items():
        if ev.file_sha(path) != expected:
            raise ValueError('source_snapshot_changed_during_evaluation:' + path)
    for name, module in modules:
        if ev.file_sha(module.__file__) != sources[name]:
            raise ValueError('code_changed_during_evaluation_restart_required:' + name)
    if ev.file_sha(args.predeclared_policy) != policy_sha:
        raise ValueError('predeclared_policy_changed_during_evaluation')
    save(args.output / 'evaluation.json', report)
    save(args.output / 'candidate.json', report['candidate'])
    compact = dict(status=report['status'], recording_count=report['recording_count'],
        source_dataset_revision=report['source_dataset_revision'], exact_prefix_component_count=len(data['groups']),
        metadata_group_count=len(data['metadata_groups']), summaries=report['summaries'], guards=report['guards'],
        comparative_deltas_vs_boundary=report['comparative_deltas_vs_boundary'],
        prefix_fold_coverage=report['prefix_fold_coverage'],
        timing={key: value for key, value in report['timing'].items() if key != 'operations'},
        preparation_seconds=data['preparation_seconds'])
    save(args.output / 'summary.json', compact)
    progress(compact)
    return report


if __name__ == '__main__':
    main()
