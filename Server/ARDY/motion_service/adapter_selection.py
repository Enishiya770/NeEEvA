"""Validate a locked feature-qualified candidate for explicit isolated testing.

This local provenance lock is not a signature or a runtime-quality approval.
No default checkpoint, report, feature bundle or service configuration is edited.
"""
from __future__ import annotations

import hashlib
import io
import json
import math
from datetime import datetime
from pathlib import Path

from .protocol import FEATURE_CONTRACT


def _read_bound_json(path, expected):
    data = Path(path).read_bytes()
    if hashlib.sha256(data).hexdigest() != expected:
        raise ValueError(f"Candidate provenance SHA256 mismatch: {Path(path).name}")
    return json.loads(data)


def load_candidate_cpu(root, selection_path):
    """Fail closed before CUDA/model loading; deserialize only weights on CPU."""
    import torch
    from Tools.MotionAdapter.adapter import MotionAdapter
    from Tools.MotionAdapter.train_extension import POLICY, balanced_score, eligibility

    root, path = Path(root), Path(selection_path).resolve()
    data = path.read_bytes()
    lock = json.loads(data)
    if (lock.get("schema") != 1 or lock.get("selected") is not True
            or lock.get("approved_for_runtime") is not False
            or lock.get("test_metrics_computed") is not False):
        raise ValueError("Candidate requires a validation-only selection lock; it is not approved for runtime")
    try:
        timestamp = datetime.fromisoformat(lock["locked_utc"])
        if timestamp.tzinfo is None:
            raise ValueError("Missing lock timezone")
    except (KeyError, TypeError, ValueError) as error:
        raise ValueError("Candidate selection has no valid lock timestamp") from error
    if lock.get("architecture") != "mlp" or lock.get("qwen_contract") != FEATURE_CONTRACT:
        raise ValueError("Candidate architecture or raw Qwen feature contract differs")
    policy = _read_bound_json(path.parent / "preregistered-policy.json", lock["preregistered_policy_sha256"])
    if policy != lock["policy"] or any(policy.get(key) != value for key, value in POLICY.items()):
        raise ValueError("Candidate selection policy differs from the preregistered fixed gates")
    manifest = _read_bound_json(lock["merged_manifest_path"], lock["merged_manifest_sha256"])
    if (manifest.get("schema") != 1 or manifest.get("passed") is not True
            or manifest.get("qwen_contract") != FEATURE_CONTRACT
            or manifest.get("teacher_contract") != lock.get("teacher_contract")
            or manifest.get("records_sha256") != lock.get("dataset_sha256")
            or policy.get("merged_manifest_sha256") != lock["merged_manifest_sha256"]):
        raise ValueError("Candidate merged feature/data contracts differ from the locked selection")
    candidates = lock["candidates"]
    if len(candidates) != 4 or {(c["mode"], c["seed"]) for c in candidates} != {
            (mode, seed) for mode in POLICY["modes"] for seed in POLICY["seeds"]}:
        raise ValueError("Candidate selection is missing the fixed four-candidate comparison")
    qualified = []
    for candidate in candidates:
        chosen = candidate["chosen"]
        failures = eligibility(chosen["validation"], lock["baseline_validation"], chosen["new_group_improvement_ci"])
        score = balanced_score(chosen["validation"], lock["baseline_validation"])
        if (chosen["eligible"] is not (not failures) or chosen["failed_gates"] != failures
                or not math.isfinite(score) or not math.isclose(score, chosen["balanced_score"], rel_tol=1e-9)):
            raise ValueError("Candidate eligibility or selection score does not match the fixed validation gates")
        if not failures:
            qualified.append(candidate)
    if not qualified:
        raise ValueError("No eligible candidate in the selection")
    winner = min(qualified, key=lambda c: c["chosen"]["balanced_score"])
    checkpoint_path = Path(lock["checkpoint"]).resolve()
    if (checkpoint_path.parent != path.parent or checkpoint_path != Path(winner["checkpoint"]).resolve()
            or lock["checkpoint_sha256"] != winner["checkpoint_sha256"]):
        raise ValueError("Selected checkpoint is not the locked winning candidate beside selection.json")
    checkpoint_bytes = checkpoint_path.read_bytes()
    if hashlib.sha256(checkpoint_bytes).hexdigest() != lock["checkpoint_sha256"]:
        raise ValueError("Selected candidate checkpoint SHA256 changed")
    payload = torch.load(io.BytesIO(checkpoint_bytes), map_location="cpu", weights_only=True)
    if (payload.get("schema") != 1 or payload.get("architecture") != "mlp"
            or payload.get("qwen_contract") != FEATURE_CONTRACT
            or payload.get("teacher_contract") != lock["teacher_contract"]
            or payload.get("dataset_sha256") != lock["dataset_sha256"]
            or payload.get("normalization_source") != "frozen-approved-baseline"):
        raise ValueError("Candidate checkpoint schema, architecture, contracts or normalization source differ")
    if payload.get("candidate") != {"mode": winner["mode"], "seed": winner["seed"],
            "epoch": winner["chosen"]["epoch"], "eligible_on_validation": True, "approved_for_runtime": False}:
        raise ValueError("Candidate checkpoint does not bind the locked validation winner")
    approved = json.loads((root / "Tools/MotionAdapter/reports/unity-preview-generation.json").read_text(encoding="utf-8"))
    baseline_bytes = (root / "Tools/MotionAdapter/runtime/mlp-seed1.pt").read_bytes()
    baseline_sha = hashlib.sha256(baseline_bytes).hexdigest()
    if (baseline_sha != approved["sources"]["adapter_sha256"]
            or baseline_sha != policy.get("baseline_checkpoint_sha256")
            or baseline_sha != payload.get("baseline_checkpoint_sha256")
            or baseline_sha != manifest["source_sha256"]["old_checkpoint"]):
        raise ValueError("Candidate baseline differs from the unchanged evaluated runtime checkpoint")
    baseline = torch.load(io.BytesIO(baseline_bytes), map_location="cpu", weights_only=True)
    state = payload["state_dict"]
    if (not all(torch.is_tensor(value) and torch.isfinite(value).all() for value in state.values())
            or not (state["target_std"] > 0).all()
            or any(not torch.equal(state[key], baseline["state_dict"][key]) for key in ("target_mean", "target_std"))):
        raise ValueError("Candidate weights must be finite and preserve the approved mean/std exactly")
    model = MotionAdapter("mlp")
    model.load_state_dict(state, strict=True)  # Enforces the real 2048 -> 4096 MLP tensor dimensions.
    return model.eval(), payload, {
        "adapter": checkpoint_path.stem, "adapterMode": "candidate", "candidate": True,
        "approvedForRuntime": False, "adapterSha256": lock["checkpoint_sha256"],
        "adapterSelectionSha256": hashlib.sha256(data).hexdigest(),
        "adapterSelectionLockedUtc": lock["locked_utc"], "adapterSelection": str(path),
        "teacherContract": lock["teacher_contract"], "datasetSha256": lock["dataset_sha256"],
        "candidateUsage": "Explicit isolated HTTP evaluation; feature qualification does not approve motion quality",
    }
