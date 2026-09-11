"""CPU provenance/validation summary and six already-seen diagnostic examples.

Never opens the final-80 suite/bundles or computes new model-selection scores.
Validation values are copied from the fixed selection lock, not recomputed.
"""
import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import torch

from Server.ARDY.motion_service.adapter_selection import load_candidate_cpu
from .adapter import load_adapter
from .data import read_bundle, read_records
from .merge_extension_bundles import file_sha256

ROOT = Path(__file__).resolve().parents[2]
HERE = Path(__file__).resolve().parent


def metrics(prediction, target, std):
    p, t, s = [np.asarray(x, dtype=np.float64) for x in (prediction, target, std)]
    pn, tn = np.linalg.norm(p, axis=1), np.linalg.norm(t, axis=1)
    return {"standardized_mse": np.mean(((p-t)/s)**2, axis=1),
            "cosine": np.sum(p*t, axis=1)/(pn*tn), "norm_ratio": pn/tn,
            "relative_l2": np.linalg.norm(p-t, axis=1)/tn}


def run(args):
    summary_path = HERE / "reports/generalization-selection-summary.json"
    diagnostic_path = HERE / "reports/seen-diagnostic-feature-comparison.json"
    if summary_path.exists() or diagnostic_path.exists():
        raise FileExistsError("Existing candidate summary/diagnostic reports are never overwritten")
    torch.set_num_threads(2)
    candidate, payload, health = load_candidate_cpu(ROOT, args.selection)
    lock = json.loads(args.selection.read_text(encoding="utf-8"))
    winner = next(c for c in lock["candidates"] if c["checkpoint_sha256"] == lock["checkpoint_sha256"])
    baseline_path = HERE / "runtime/mlp-seed1.pt"
    baseline, _ = load_adapter(baseline_path, "cpu", lock["qwen_contract"])
    if not torch.equal(candidate.target_mean, baseline.target_mean) or not torch.equal(candidate.target_std, baseline.target_std):
        raise ValueError("Frozen normalization changed")
    source_paths = {"selection": args.selection, "candidate": Path(lock["checkpoint"]), "baseline": baseline_path}
    source_hashes = {name: file_sha256(path) for name, path in source_paths.items()}
    domains = {}
    for domain in ("old", "new"):
        before, after = lock["baseline_validation"][domain], winner["chosen"]["validation"][domain]
        domains[domain] = {"baseline": before, "locked_candidate": after,
                          "standardized_mse_improvement_percent": 100*(1-after["standardized_mse"]/before["standardized_mse"]),
                          "cosine_delta": after["cosine"]-before["cosine"]}
    summary = {"schema": 1, "passed": True, "utc": datetime.now(timezone.utc).isoformat(),
               "source_sha256": source_hashes, "candidate_cpu_loader": health,
               "validation_source": "Existing immutable selection lock; no new predictions or model selection",
               "selected_mode": winner["mode"], "seed": winner["seed"], "epoch": winner["chosen"]["epoch"],
               "domains": domains, "validation_group_improvement_ci": winner["chosen"]["new_group_improvement_ci"],
               "all_candidates": [{"mode": c["mode"], "seed": c["seed"], **c["chosen"]} for c in lock["candidates"]],
               "evaluation_scope": lock["policy"]["evaluation_scope"],
               "approved_for_runtime": False, "test_metrics_computed_by_this_tool": False,
               "gpu_used": False, "weights_or_selection_changed": False}
    prompts = HERE / "runtime/teacher-motion-diagnostic/prompts.jsonl"
    rows = read_records(prompts)
    if len(rows) != 6 or [r["id"] for r in rows] != ["raise-actual", "explain-actual", "raise-style", "explain-style", "motion-00004", "motion-00053"]:
        raise ValueError("Expected exactly the six already-seen diagnostic/control descriptions")
    qpath = HERE / "runtime/generalization-v1/qwen-diagnostic-six.npz"
    tpath = HERE / "runtime/teacher-motion-diagnostic/teacher-six.npz"
    q, qm = read_bundle(qpath, rows, "qwen")
    target, tm = read_bundle(tpath, rows, "teacher")
    old_rows = read_records(HERE / "runtime/prompts.jsonl")
    oldq, oldqm = read_bundle(HERE / "runtime/qwen-2000.npz", old_rows, "qwen")
    oldt, oldtm = read_bundle(HERE / "runtime/teacher-2000.npz", old_rows, "teacher")
    if qm["feature_contract"] != lock["qwen_contract"] or oldqm["feature_contract"] != lock["qwen_contract"]:
        raise ValueError("Diagnostic Qwen contract differs")
    if hashlib.sha256(json.dumps(tm["provenance"], sort_keys=True).encode()).hexdigest() != tm["feature_contract"]:
        raise ValueError("Diagnostic teacher provenance hash differs")
    controls = []
    for i, oldindex in ((4, 4), (5, 53)):
        if rows[i]["text"] != old_rows[oldindex]["text"] or not np.array_equal(target[i], oldt[oldindex]) or not np.array_equal(q[i], oldq[oldindex]):
            raise ValueError("Diagnostic old-cache teacher/Qwen controls must be elementwise exact")
        controls.append({"id": rows[i]["id"], "text": rows[i]["text"], "teacher_max_abs_error": 0., "qwen_max_abs_error": 0.})
    with torch.inference_mode():
        inputs = torch.from_numpy(q)
        before_prediction, after_prediction = baseline(inputs).numpy(), candidate(inputs).numpy()
    before, after = metrics(before_prediction, target, baseline.target_std.numpy()), metrics(after_prediction, target, baseline.target_std.numpy())
    comparisons = [{"id": row["id"], "text": row["text"], "role": "already-seen-diagnostic" if i < 4 else "old-cache-control",
                    "baseline": {key: float(value[i]) for key, value in before.items()},
                    "locked_candidate": {key: float(value[i]) for key, value in after.items()},
                    "standardized_mse_improvement_percent": float(100*(1-after["standardized_mse"][i]/before["standardized_mse"][i]))}
                   for i, row in enumerate(rows)]
    diagnostic = {"schema": 1, "passed": True, "utc": datetime.now(timezone.utc).isoformat(),
                  "scope": "Four already-seen issue examples plus two old-cache controls. Descriptive feature comparison, not a held-out evaluation or action success rate.",
                  "source_sha256": {**source_hashes, "prompts": file_sha256(prompts), "qwen": file_sha256(qpath), "teacher": file_sha256(tpath)},
                  "qwen_contract": qm["feature_contract"], "teacher_diagnostic_contract": tm["feature_contract"],
                  "teacher_old_contract": oldtm["feature_contract"], "teacher_training_composite_contract": payload["teacher_contract"],
                  "exact_cache_controls": controls, "normalization": "Frozen original baseline target_std for every error",
                  "per_record": comparisons,
                  "prediction_sha256": {"baseline": hashlib.sha256(before_prediction.tobytes()).hexdigest(),
                                        "locked_candidate": hashlib.sha256(after_prediction.tobytes()).hexdigest()},
                  "final80_opened": False, "used_for_selection": False, "gpu_used": False, "approved_for_runtime": False}
    if any(file_sha256(path) != source_hashes[name] for name, path in source_paths.items()):
        raise RuntimeError("Checkpoint or selection changed during read-only diagnosis")
    for path, report in ((summary_path, summary), (diagnostic_path, diagnostic)):
        with path.open("x", encoding="utf-8") as stream:
            json.dump(report, stream, indent=2, ensure_ascii=False, allow_nan=False)
    print(json.dumps({"validation": domains, "seen_diagnostic": comparisons}, ensure_ascii=False))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--selection", type=Path, required=True)
    run(parser.parse_args())
