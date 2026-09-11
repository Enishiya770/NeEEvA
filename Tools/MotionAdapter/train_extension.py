"""Four preregistered MLP candidates; select on old/new validation only.

An explicit audit subcommand evaluates the one locked winner on test data.
Neither command changes the approved runtime adapter or service configuration.
"""
import argparse
import json
import math
import random
from datetime import datetime, timezone
from pathlib import Path

import numpy as np
import torch
from torch.nn import functional as F

from .adapter import MotionAdapter, load_adapter
from .data import read_bundle, read_records, records_hash
from .merge_extension_bundles import file_sha256
from .train import prediction_metrics


POLICY = {
    "schema": 1, "modes": ["scratch", "finetune"], "seeds": [0, 1],
    "learning_rates": {"scratch": 1e-4, "finetune": 5e-5},
    "max_epochs": 100, "patience": 10, "batch_size": 256,
    "training_domain_weights": {"old": .5, "new": .5},
    "validation_score": "0.5 * old_MSE / old_baseline_MSE + 0.5 * new_MSE / new_baseline_MSE",
    "normalization": "Frozen approved old checkpoint target_mean and target_std for BOTH candidates and all retention metrics",
    "old_max_mse_ratio": 1.05, "old_max_cosine_drop": .001,
    "new_max_mse_ratio": .90, "new_min_cosine_delta": 0,
    "new_mse_improvement_ci": {"method": "paired bootstrap of semantic-group mean MSE improvement",
                               "resamples": 2000, "seed": 910, "confidence": .95, "lower_bound_min_exclusive": 0},
    "test_policy": "Never evaluate test during selection. Only explicit audit of the locked eligible winner.",
    "evaluation_scope": {"old_validation_and_test": "Retention audit, not unseen-action generalization: new broad data shares wave/raise/nod and other action semantics with old holdouts.",
                         "new_validation_and_test": "Within-extension configuration/expression splits; shared action atoms are allowed.",
                         "frozen_eval80": "Separate frozen configuration/expression audit with shared action atoms allowed; never used for candidate selection."},
    "deployment": "Feature-qualified candidates still need motion evaluation; approved runtime checkpoint is never replaced.",
}


def balanced_score(domains, baseline):
    return sum(.5 * domains[d]["standardized_mse"] / max(baseline[d]["standardized_mse"], 1e-12)
               for d in ["old", "new"])


def eligibility(domains, baseline, improvement_ci):
    if (not all(math.isfinite(block[key]) for pair in [domains, baseline] for block in pair.values()
                for key in ["standardized_mse", "cosine"]) or not math.isfinite(improvement_ci["lower"])):
        return ["nonfinite-validation-metrics"]
    failed = []
    if domains["old"]["standardized_mse"] > baseline["old"]["standardized_mse"] * POLICY["old_max_mse_ratio"]:
        failed.append("old-validation-mse-regression")
    if domains["old"]["cosine"] < baseline["old"]["cosine"] - POLICY["old_max_cosine_drop"]:
        failed.append("old-validation-cosine-regression")
    if domains["new"]["standardized_mse"] > baseline["new"]["standardized_mse"] * POLICY["new_max_mse_ratio"]:
        failed.append("new-validation-mse-improvement-under-10-percent")
    if domains["new"]["cosine"] < baseline["new"]["cosine"]:
        failed.append("new-validation-cosine-regression")
    if improvement_ci["lower"] <= 0:
        failed.append("new-validation-group-bootstrap-does-not-establish-positive-improvement")
    return failed


def group_bootstrap_improvement(old_losses, new_losses, groups):
    if len(old_losses) != len(new_losses) or len(groups) != len(old_losses):
        raise ValueError("Paired validation loss/group dimensions differ")
    if not np.isfinite(old_losses).all() or not np.isfinite(new_losses).all():
        raise ValueError("Nonfinite paired validation losses")
    by_group = {}
    for difference, group in zip(np.asarray(old_losses) - np.asarray(new_losses), groups):
        by_group.setdefault(group, []).append(float(difference))
    if len(by_group) < 2:
        raise ValueError("At least two independent validation semantic groups are required")
    means = np.array([np.mean(by_group[key]) for key in sorted(by_group)], dtype=np.float64)
    rng = np.random.default_rng(POLICY["new_mse_improvement_ci"]["seed"])
    samples = rng.choice(means, size=(POLICY["new_mse_improvement_ci"]["resamples"], len(means))).mean(axis=1)
    return {"lower": float(np.quantile(samples, .025)), "upper": float(np.quantile(samples, .975)),
            "mean_group_improvement": float(means.mean()), "groups": len(means),
            "scope": "Validation selection evidence with correlated paraphrases grouped; not a final test confidence claim"}


def validated_inputs(directory):
    manifest = json.loads((directory / "merge.json").read_text(encoding="utf-8"))
    if not manifest.get("passed") or manifest.get("schema") != 1:
        raise ValueError("Missing successful component merge")
    for name, expected in manifest["output_sha256"].items():
        if file_sha256(manifest["outputs"][name]) != expected:
            raise ValueError(f"Merged artifact changed after compatibility validation: {name}")
    baseline_path = Path(manifest["source_paths"]["old_checkpoint"])
    if file_sha256(baseline_path) != manifest["source_sha256"]["old_checkpoint"]:
        raise ValueError("The approved baseline checkpoint changed")
    rows = read_records(manifest["outputs"]["dataset"])
    q, qm = read_bundle(manifest["outputs"]["qwen"], rows, "qwen")
    target, tm = read_bundle(manifest["outputs"]["teacher"], rows, "teacher")
    if (records_hash(rows) != manifest["records_sha256"] or qm["feature_contract"] != manifest["qwen_contract"]
            or tm["feature_contract"] != manifest["teacher_contract"]):
        raise ValueError("Merged manifest contracts changed")
    return manifest, rows, q, target, qm, tm, baseline_path


def domain_indices(rows, ranges, split):
    result = {domain: [i for i in range(*bounds) if rows[i]["split"] == split]
              for domain, bounds in ranges.items()}
    if any(not result.get(domain) for domain in ["old", "new"]):
        raise ValueError(f"Both domains need a nonempty {split} split")
    return result


def evaluate(model, q, target, ids, std):
    with torch.no_grad():
        prediction = model(q[ids])
        metrics = prediction_metrics(prediction, target[ids], std)
        losses = ((prediction - target[ids]) / std).square().mean(dim=1).cpu().numpy()
    return metrics, losses


def residual_loss(model, q, target):
    standardized = model.standardized(q)
    prediction = standardized * model.target_std + model.target_mean
    return F.mse_loss(standardized, (target - model.target_mean) / model.target_std) + \
        .1 * (1 - F.cosine_similarity(prediction, target)).mean()


def save_candidate(path, state, payload, qmeta, tmeta, dataset_hash, baseline_sha, mode, seed, epoch, eligible):
    if path.exists():
        raise FileExistsError("Candidate checkpoint already exists")
    torch.save({"schema": 1, "architecture": "mlp", "state_dict": state,
                "qwen_contract": qmeta["feature_contract"], "teacher_contract": tmeta["feature_contract"],
                "dataset_sha256": dataset_hash, "baseline_checkpoint_sha256": baseline_sha,
                "candidate": {"mode": mode, "seed": seed, "epoch": epoch, "eligible_on_validation": eligible,
                              "approved_for_runtime": False},
                "normalization_source": "frozen-approved-baseline"}, path)


def fit_candidate(q, target, rows, train_ids, val_ids, baseline_model, baseline_payload,
                  baseline_metrics, baseline_losses, mode, seed, epochs, patience, batch):
    torch.manual_seed(seed)
    np.random.seed(seed)
    random.seed(seed)
    model = MotionAdapter("mlp").to(q.device)
    if mode == "finetune":
        model.load_state_dict(baseline_model.state_dict(), strict=True)
    else:
        model.target_mean.copy_(baseline_model.target_mean)
        model.target_std.copy_(baseline_model.target_std)
    std = baseline_model.target_std.detach()
    optimizer = torch.optim.AdamW(model.parameters(), lr=POLICY["learning_rates"][mode], weight_decay=1e-2)
    half = batch // 2
    steps = max(math.ceil(len(train_ids[d]) / half) for d in ["old", "new"])
    train_tensors = {d: torch.tensor(train_ids[d], device=q.device) for d in ["old", "new"]}
    groups = [rows[i]["semantic_group"] for i in val_ids["new"]]
    best_score, stale = float("inf"), 0
    best_overall = best_eligible = None
    history = []
    for epoch in range(1, epochs + 1):
        model.train()
        for _ in range(steps):
            # Each domain contributes exactly half the objective regardless of
            # dataset size. Uniform draws within each domain are reproducible.
            losses = []
            for domain in ["old", "new"]:
                pool = train_tensors[domain]
                ids = pool[torch.randint(len(pool), (half,), device=q.device)]
                losses.append(residual_loss(model, q[ids], target[ids]))
            loss = .5 * losses[0] + .5 * losses[1]
            if not torch.isfinite(loss):
                raise RuntimeError("Nonfinite balanced residual loss")
            optimizer.zero_grad(set_to_none=True)
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.parameters(), 1)
            optimizer.step()
        model.eval()
        domains, losses = {}, {}
        for domain in ["old", "new"]:
            domains[domain], losses[domain] = evaluate(model, q, target, val_ids[domain], std)
        ci = group_bootstrap_improvement(baseline_losses["new"], losses["new"], groups)
        score = balanced_score(domains, baseline_metrics)
        failures = eligibility(domains, baseline_metrics, ci)
        entry = {"epoch": epoch, "validation": domains, "balanced_score": score,
                 "new_group_improvement_ci": ci, "eligible": not failures, "failed_gates": failures}
        history.append(entry)
        state = None
        if score < best_score:
            best_score, stale = score, 0
            state = {key: value.detach().cpu().clone() for key, value in model.state_dict().items()}
            best_overall = (entry, state)
        else:
            stale += 1
        if not failures and (best_eligible is None or score < best_eligible[0]["balanced_score"]):
            if state is None:
                state = {key: value.detach().cpu().clone() for key, value in model.state_dict().items()}
            best_eligible = (entry, state)
        if epoch == 1 or epoch % 10 == 0:
            print(json.dumps({"mode": mode, "seed": seed, **entry}), flush=True)
        if stale >= patience:
            break
    chosen, state = best_eligible if best_eligible is not None else best_overall
    if not torch.equal(state["target_std"], baseline_model.target_std.cpu()) or not torch.equal(
            state["target_mean"], baseline_model.target_mean.cpu()):
        raise RuntimeError("Frozen baseline normalization changed")
    return state, {"mode": mode, "seed": seed, "chosen": chosen, "history": history,
                   "test_metrics_computed": False, "normalization_frozen": True,
                   "balanced_training_steps_per_epoch": steps}


def select(args):
    manifest, rows, q, target, qm, tm, baseline_path = validated_inputs(args.merged_directory)
    if not 1 <= args.epochs <= 100 or not 1 <= args.patience <= 10 or args.batch_size < 2 or args.batch_size % 2:
        raise ValueError("Use <=100 epochs, <=10 patience and an even batch size >=2")
    output = args.output_directory.resolve()
    if output.exists() and any(output.iterdir()):
        raise FileExistsError("Use a new empty candidate directory")
    output.mkdir(parents=True, exist_ok=True)
    policy = {**POLICY, "epochs": args.epochs, "patience": args.patience, "batch_size": args.batch_size,
              "merged_manifest_sha256": file_sha256(args.merged_directory / "merge.json"),
              "baseline_checkpoint_sha256": file_sha256(baseline_path),
              "preregistered_utc": datetime.now(timezone.utc).isoformat()}
    (output / "preregistered-policy.json").write_text(json.dumps(policy, indent=2), encoding="utf-8")
    train_ids = domain_indices(rows, manifest["ranges"], "train")
    val_ids = domain_indices(rows, manifest["ranges"], "val")
    # Test rows have no prediction/statistics path in select(); normalization
    # comes only from the already-fixed baseline checkpoint buffers.
    q = torch.as_tensor(q, dtype=torch.float32, device=args.device)
    target = torch.as_tensor(target, dtype=torch.float32, device=args.device)
    baseline, payload = load_adapter(baseline_path, args.device, qm["feature_contract"])
    baseline_metrics, baseline_losses = {}, {}
    for domain in ["old", "new"]:
        baseline_metrics[domain], baseline_losses[domain] = evaluate(baseline, q, target, val_ids[domain], baseline.target_std)
    reports = []
    for mode in POLICY["modes"]:
        for seed in POLICY["seeds"]:
            state, report = fit_candidate(q, target, rows, train_ids, val_ids, baseline, payload,
                                          baseline_metrics, baseline_losses, mode, seed,
                                          args.epochs, args.patience, args.batch_size)
            checkpoint = output / f"{mode}-seed{seed}.pt"
            save_candidate(checkpoint, state, payload, qm, tm, records_hash(rows),
                           policy["baseline_checkpoint_sha256"], mode, seed, report["chosen"]["epoch"], report["chosen"]["eligible"])
            report.update(checkpoint=str(checkpoint), checkpoint_sha256=file_sha256(checkpoint),
                          baseline_validation=baseline_metrics, policy=policy)
            checkpoint.with_suffix(".report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
            reports.append(report)
    qualified = [report for report in reports if report["chosen"]["eligible"]]
    winner = min(qualified, key=lambda report: report["chosen"]["balanced_score"]) if qualified else None
    selection = {"schema": 1, "selected": winner is not None, "checkpoint": winner["checkpoint"] if winner else None,
                 "checkpoint_sha256": winner["checkpoint_sha256"] if winner else None,
                 "architecture": "mlp", "qwen_contract": qm["feature_contract"],
                 "teacher_contract": tm["feature_contract"], "dataset_sha256": records_hash(rows),
                 "merged_manifest_path": str((args.merged_directory / "merge.json").resolve()),
                 "merged_manifest_sha256": policy["merged_manifest_sha256"],
                 "preregistered_policy_sha256": file_sha256(output / "preregistered-policy.json"),
                 "baseline_validation": baseline_metrics, "policy": policy,
                 "candidates": [{key: report[key] for key in ["mode", "seed", "chosen", "checkpoint", "checkpoint_sha256"]}
                                for report in reports],
                 "locked_utc": datetime.now(timezone.utc).isoformat(), "test_metrics_computed": False,
                 "approved_for_runtime": False}
    if file_sha256(baseline_path) != policy["baseline_checkpoint_sha256"]:
        raise RuntimeError("Approved baseline checkpoint changed during training")
    (output / "selection.json").write_text(json.dumps(selection, indent=2), encoding="utf-8")
    print(json.dumps({"selected": selection["selected"], "checkpoint": selection["checkpoint"], "test_metrics_computed": False}))


def audit(args):
    if args.output.exists():
        raise FileExistsError("Audit report already exists")
    locked = json.loads(args.selection.read_text(encoding="utf-8"))
    if not locked.get("selected") or not locked.get("locked_utc"):
        raise ValueError("Audit requires an eligible candidate already locked by validation selection")
    if (file_sha256(locked["checkpoint"]) != locked["checkpoint_sha256"]
            or file_sha256(args.merged_directory / "merge.json") != locked["merged_manifest_sha256"]):
        raise ValueError("Locked candidate or dataset changed before explicit test audit")
    manifest, rows, q, target, qm, tm, baseline_path = validated_inputs(args.merged_directory)
    ids = domain_indices(rows, manifest["ranges"], "test")
    q, target = torch.tensor(q, device=args.device), torch.tensor(target, device=args.device)
    baseline, _ = load_adapter(baseline_path, args.device, qm["feature_contract"])
    candidate, payload = load_adapter(locked["checkpoint"], args.device, qm["feature_contract"])
    if payload["dataset_sha256"] != records_hash(rows) or payload["teacher_contract"] != tm["feature_contract"]:
        raise ValueError("Audit candidate's teacher/data provenance changed")
    result = {"schema": 1, "explicit_audit": True, "selection_sha256": file_sha256(args.selection),
              "checkpoint_sha256": locked["checkpoint_sha256"], "domains": {}, "used_for_selection": False,
              "approved_for_runtime": False, "evaluation_scope": POLICY["evaluation_scope"]}
    for domain in ["old", "new"]:
        before, before_losses = evaluate(baseline, q, target, ids[domain], baseline.target_std)
        after, after_losses = evaluate(candidate, q, target, ids[domain], baseline.target_std)
        result["domains"][domain] = {"baseline": before, "candidate": after,
                                     "paired_group_mse_improvement": group_bootstrap_improvement(
                                         before_losses, after_losses, [rows[i]["semantic_group"] for i in ids[domain]])}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps({"explicit_audit": True, "output": str(args.output)}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    train = commands.add_parser("select")
    train.add_argument("--output-directory", type=Path, required=True)
    train.add_argument("--epochs", type=int, default=100)
    train.add_argument("--patience", type=int, default=10)
    train.add_argument("--batch-size", type=int, default=256)
    explicit_audit = commands.add_parser("audit")
    explicit_audit.add_argument("--selection", type=Path, required=True)
    explicit_audit.add_argument("--output", type=Path, required=True)
    for command in [train, explicit_audit]:
        command.add_argument("--merged-directory", type=Path, required=True)
        command.add_argument("--device", default="cpu")
    args = parser.parse_args()
    select(args) if args.command == "select" else audit(args)


if __name__ == "__main__":
    main()
