"""One explicit final feature audit of the frozen 80 descriptions; never select a model."""
from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

from .data import read_bundle, read_records, records_hash
from .merge_extension_bundles import file_sha256, teacher_compatibility

HERE = Path(__file__).resolve().parent
SUITE = HERE / "data/generalization-eval-v1.jsonl"
SUITE_SHA256 = "bad7ec319c961c077b8ab92eb39eb8482e847c2ff67d6c39e5a8434b3e197889"
AUDIT_MARKER = HERE / "reports/generalization-eval-v1-feature-audit-once.json"
TRACKS = ("supported_upper_body", "capability_boundary")


def read_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def dump(path, value, mode="w"):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open(mode, encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
        stream.write("\n")


def validate_suite(path=SUITE):
    if file_sha256(path) != SUITE_SHA256:
        raise ValueError("The frozen final-80 description/label bytes changed")
    rows = read_records(path)
    if len(rows) != 80 or any(row["split"] != "test" for row in rows):
        raise ValueError("Final audit requires all 80 frozen test descriptions")
    counts = Counter(row["semantic_group"] for row in rows)
    if len(counts) != 40 or set(counts.values()) != {2}:
        raise ValueError("Expected 40 groups with exactly two paraphrases each")
    if Counter(row["evaluation_track"] for row in rows) != {TRACKS[0]: 64, TRACKS[1]: 16}:
        raise ValueError("Supported and boundary tracks changed")
    if any(row["include_in_success_summary"] != (row["evaluation_track"] == TRACKS[0]) for row in rows):
        raise ValueError("Capability boundaries must be excluded from success summaries")
    return rows


def validate_selection(locked, baseline_sha):
    if (locked.get("schema") != 1 or locked.get("selected") is not True
            or not locked.get("locked_utc") or locked.get("test_metrics_computed") is not False):
        raise ValueError("An eligible validation-only selection must already be locked")
    if locked.get("policy", {}).get("baseline_checkpoint_sha256") != baseline_sha:
        raise ValueError("Selection was not made against this approved old checkpoint")
    matching = [item for item in locked.get("candidates", [])
                if item.get("checkpoint_sha256") == locked.get("checkpoint_sha256")
                and item.get("checkpoint") == locked.get("checkpoint")]
    if len(matching) != 1 or matching[0].get("chosen", {}).get("eligible") is not True:
        raise ValueError("Locked winner is absent from the eligible candidate records")


def validate_native_validation(report, candidate_sha):
    plan = report.get("plan", {})
    if report.get("status") != "complete" or plan.get("split") != "val" or plan.get("finalTest"):
        raise ValueError("Complete independent native validation must precede final feature audit")
    if candidate_sha not in {item.get("sha256") for item in plan.get("adapters", {}).values()}:
        raise ValueError("Native validation did not use the locked candidate checkpoint")


def build_plan(args):
    # This checks cached data and identities only; it does not load either adapter.
    from .train_extension import validated_inputs

    merged, old_new_rows, _, _, _, merged_teacher, baseline_path = validated_inputs(args.merged_directory)
    rows = validate_suite()
    selection_sha = file_sha256(args.selection)
    locked = read_json(args.selection)
    baseline_sha = file_sha256(baseline_path)
    validate_selection(locked, baseline_sha)
    if file_sha256(args.merged_directory / "merge.json") != locked["merged_manifest_sha256"]:
        raise ValueError("Selection's merged training data changed")
    candidate_path = Path(locked["checkpoint"]).resolve()
    if file_sha256(candidate_path) != locked["checkpoint_sha256"]:
        raise ValueError("Locked candidate bytes changed")
    native = read_json(args.native_validation_report)
    validate_native_validation(native, locked["checkpoint_sha256"])
    qwen, qmeta = read_bundle(args.qwen_bundle, rows, "qwen")
    teacher, tmeta = read_bundle(args.teacher_bundle, rows, "teacher")
    if qmeta["feature_contract"] != merged["qwen_contract"]:
        raise ValueError("Frozen-suite Qwen features have a different contract")
    # A separately exported teacher component keeps its own actual contract.
    # Compatibility is checked through its exact current-model/old-cache controls.
    old_meta = merged["components"]["old"]["teacher"]
    compatibility = teacher_compatibility(old_meta, tmeta, old_new_rows[slice(*merged["ranges"]["old"])])
    files = {"suite": SUITE, "selection": args.selection, "mergedManifest": args.merged_directory / "merge.json",
             "oldCheckpoint": baseline_path, "candidateCheckpoint": candidate_path,
             "qwenBundle": args.qwen_bundle, "teacherBundle": args.teacher_bundle,
             "nativeValidationReport": args.native_validation_report}
    plan = {"schema": 1, "suite": "generalization-eval-v1", "datasetRecordsSha256": records_hash(rows),
            "files": {name: {"path": str(Path(path).resolve()), "sha256": file_sha256(path)} for name, path in files.items()},
            "selectionSha256": selection_sha, "lockedCandidateSha256": locked["checkpoint_sha256"],
            "trainingTeacherContract": merged_teacher["feature_contract"], "trainingDatasetRecordsSha256": merged["records_sha256"],
            "evaluationTeacherContract": tmeta["feature_contract"],
            "qwenContract": qmeta["feature_contract"], "teacherCompatibility": compatibility,
            "normalization": "Both old and locked candidate use the exact old checkpoint target_mean/target_std; all MSE uses old target_std.",
            "tracks": {TRACKS[0]: {"rows": 64, "groups": 32}, TRACKS[1]: {"rows": 16, "groups": 8}},
            "policy": "Final descriptive feature audit only; no model ranking, eligibility gate, tuning or automatic action-success claim.",
            "nativeValidationScope": "The linked report establishes that independent native validation ran. Manual acceptance is the operator's prerequisite for invoking --final-test; this tool does not infer naturalness from its metrics.",
            "implementationSha256": file_sha256(Path(__file__))}
    return plan, rows, qwen, teacher, baseline_path, candidate_path


def row_metrics(prediction, target, std):
    prediction, target, std = (np.asarray(value, dtype=np.float64) for value in (prediction, target, std))
    if (prediction.shape != target.shape or prediction.ndim != 2 or std.shape != (target.shape[1],)
            or not all(np.isfinite(value).all() for value in (prediction, target, std)) or np.any(std <= 0)):
        raise ValueError("Feature prediction/target/frozen scale shape or values differ")
    pnorm = np.linalg.norm(prediction, axis=1)
    tnorm = np.linalg.norm(target, axis=1)
    return {"standardized_mse": np.mean(((prediction - target) / std) ** 2, axis=1),
            "cosine": np.sum(prediction * target, axis=1) / (np.maximum(pnorm, 1e-8) * np.maximum(tnorm, 1e-8)),
            "norm_ratio": pnorm / np.maximum(tnorm, 1e-8)}


def final_descriptive_interval(group_improvements):
    values = np.asarray(group_improvements, dtype=np.float64)
    if values.ndim != 1 or len(values) < 2 or not np.isfinite(values).all():
        raise ValueError("Final descriptive bootstrap needs two finite group means")
    samples = np.random.default_rng(910).choice(values, size=(2000, len(values))).mean(axis=1)
    return {"mean_group_mse_improvement": float(values.mean()), "lower": float(np.quantile(samples, .025)),
            "upper": float(np.quantile(samples, .975)), "semantic_groups": len(values), "resamples": 2000,
            "confidence": .95, "seed": 910,
            "scope": "Final descriptive paired semantic-group bootstrap for the already locked model. Correlated paraphrases are grouped. This is not selection evidence, an eligibility gate, or proof of population-wide motion quality."}


def summarize(rows, before, after):
    tracks = {}
    for track in TRACKS:
        grouped = {}
        for i, row in enumerate(rows):
            if row["evaluation_track"] == track:
                grouped.setdefault(row["semantic_group"], []).append(i)
        groups = []
        for name, ids in sorted(grouped.items()):
            means = {label: {metric: float(np.mean(values[ids])) for metric, values in data.items()}
                     for label, data in (("old", before), ("locked_candidate", after))}
            groups.append({"semantic_group": name, "record_ids": [rows[i]["id"] for i in ids], "rows": len(ids),
                           "metrics": means, "mse_improvement": means["old"]["standardized_mse"] - means["locked_candidate"]["standardized_mse"]})
        if not groups:
            continue
        tracks[track] = {"rows": sum(group["rows"] for group in groups), "semantic_groups": len(groups),
            "include_in_supported_success_summary": track == TRACKS[0], "action_success_rate": None,
            "group_macro_metrics": {label: {metric: float(np.mean([group["metrics"][label][metric] for group in groups]))
                                             for metric in before} for label in ("old", "locked_candidate")},
            "descriptive_paired_group_interval": final_descriptive_interval([group["mse_improvement"] for group in groups]) if len(groups) >= 2 else None,
            "groups": groups,
            "interpretation": "Feature similarity only; actual requested motion remains unmeasured." if track == TRACKS[0]
                              else "Boundary feature diagnostics only; neither high similarity nor improvement shows supported execution or correct refusal. Excluded from supported success summaries."}
    return tracks


def reserve_final_audit(marker, plan, output):
    # One suite-wide marker deliberately prevents selecting another candidate
    # after viewing results, including by merely choosing a different output path.
    dump(marker, {"schema": 1, "status": "reserved_before_any_prediction", "selectionSha256": plan["selectionSha256"],
                  "suiteSha256": plan["files"]["suite"]["sha256"], "candidateSha256": plan["lockedCandidateSha256"],
                  "output": str(Path(output).resolve()), "reservedUtc": datetime.now(timezone.utc).isoformat()}, mode="x")


def run(args):
    if not args.plan_only and not args.final_test:
        raise ValueError("Actual final-80 prediction requires explicit --final-test")
    if args.output.exists():
        raise FileExistsError("Final audit never overwrites an existing output")
    plan, rows, qwen, target, baseline_path, candidate_path = build_plan(args)
    if args.plan_only:
        print(json.dumps({"planOnly": True, "plan": plan}, indent=2))
        return
    reserve_final_audit(AUDIT_MARKER, plan, args.output)
    report = {"schema": 1, "status": "running", "explicit_final_test": True, "used_for_selection": False,
              "approved_for_runtime": False, "action_success_rate": None, "plan": plan,
              "environment": "CPU-only old/locked-adapter inference on cached real Qwen/teacher features; no language model, ARDY generator, Unity, service or network calls.",
              "limitations": ["This measures feature alignment, not motion success, naturalness, precise palm/finger control or spontaneous conversational action choice.",
                              "The 32 supported and 8 boundary groups are always separate; no combined success denominator exists.",
                              "Held-out configurations and expressions can share action atoms and broad legacy priors with training.",
                              "Once inspected, these final results must not drive further tuning of this evaluation version."]}
    try:
        import torch
        from .adapter import load_adapter

        baseline, old_payload = load_adapter(baseline_path, "cpu", plan["qwenContract"])
        candidate, payload = load_adapter(candidate_path, "cpu", plan["qwenContract"])
        if (payload.get("teacher_contract") != plan["trainingTeacherContract"]
                or payload.get("dataset_sha256") != plan["trainingDatasetRecordsSha256"]
                or payload.get("architecture") != "mlp" or old_payload.get("architecture") != "mlp"
                or payload.get("baseline_checkpoint_sha256") != plan["files"]["oldCheckpoint"]["sha256"]
                or payload.get("candidate", {}).get("eligible_on_validation") is not True):
            raise ValueError("Candidate payload does not match its eligible locked provenance")
        if not torch.equal(candidate.target_std, baseline.target_std) or not torch.equal(candidate.target_mean, baseline.target_mean):
            raise ValueError("Candidate normalization differs from the required frozen old buffers")
        with torch.inference_mode():
            inputs = torch.from_numpy(qwen.astype(np.float32, copy=False))
            old_prediction = baseline(inputs).numpy()
            new_prediction = candidate(inputs).numpy()
        std = baseline.target_std.numpy()
        before, after = row_metrics(old_prediction, target, std), row_metrics(new_prediction, target, std)
        report["tracks"] = summarize(rows, before, after)
        report["per_record"] = [{"id": row["id"], "semantic_group": row["semantic_group"], "track": row["evaluation_track"],
            "old": {metric: float(values[i]) for metric, values in before.items()},
            "locked_candidate": {metric: float(values[i]) for metric, values in after.items()}} for i, row in enumerate(rows)]
        report["predictionSha256"] = {"old": hashlib.sha256(old_prediction.tobytes()).hexdigest(),
                                      "locked_candidate": hashlib.sha256(new_prediction.tobytes()).hexdigest()}
        for item in plan["files"].values():
            if file_sha256(item["path"]) != item["sha256"]:
                raise RuntimeError("An audit input changed during prediction; this run is invalid")
        report["status"] = "complete"
    except BaseException as error:
        report["status"] = "failed"
        report["error"] = {"type": type(error).__name__, "message": str(error)}
        raise
    finally:
        report["finishedUtc"] = datetime.now(timezone.utc).isoformat()
        dump(args.output, report, mode="x")
        marker = read_json(AUDIT_MARKER)
        marker.update(status=report["status"], reportSha256=file_sha256(args.output), finishedUtc=report["finishedUtc"])
        dump(AUDIT_MARKER, marker)
    print(json.dumps({"status": report["status"], "output": str(args.output), "used_for_selection": False}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("selection", "merged-directory", "qwen-bundle", "teacher-bundle", "native-validation-report", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--final-test", action="store_true")
    parser.add_argument("--plan-only", action="store_true", help="Validate cached inputs and identities only; no adapter load, prediction or audit reservation")
    run(parser.parse_args())


if __name__ == "__main__":
    main()
