"""CPU-only synthetic selection-lock regressions; no feature quality claims."""
import copy
import hashlib
import json
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

import torch

from Server.ARDY.motion_service.adapter_selection import load_candidate_cpu
from Server.ARDY.motion_service.backend import ArdyBackend
from Server.ARDY.motion_service.protocol import FEATURE_CONTRACT
from Tools.MotionAdapter.adapter import MotionAdapter
from Tools.MotionAdapter.train_extension import POLICY, balanced_score


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value), encoding="utf-8")


class CandidateLockTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(2)
        cls.temporary = tempfile.TemporaryDirectory(prefix="ardy-candidate-lock-")
        cls.root = Path(cls.temporary.name)
        cls.directory = cls.root / "candidate"
        cls.directory.mkdir()
        model = MotionAdapter("mlp")
        cls.baseline = cls.root / "Tools/MotionAdapter/runtime/mlp-seed1.pt"
        cls.baseline.parent.mkdir(parents=True)
        torch.save({"state_dict": model.state_dict()}, cls.baseline)
        cls.baseline_sha = sha(cls.baseline)
        cls.approval = cls.root / "Tools/MotionAdapter/reports/unity-preview-generation.json"
        write(cls.approval, {"sources": {"adapter_sha256": cls.baseline_sha}})
        cls.manifest = cls.root / "merge.json"
        write(cls.manifest, {"schema": 1, "passed": True, "qwen_contract": FEATURE_CONTRACT,
                            "teacher_contract": "a" * 64, "records_sha256": "b" * 64,
                            "source_sha256": {"old_checkpoint": cls.baseline_sha}})
        cls.policy = {**POLICY, "baseline_checkpoint_sha256": cls.baseline_sha,
                      "merged_manifest_sha256": sha(cls.manifest)}
        cls.policy_path = cls.directory / "preregistered-policy.json"
        write(cls.policy_path, cls.policy)
        baseline_metrics = {"old": {"standardized_mse": 1., "cosine": .95},
                            "new": {"standardized_mse": 1., "cosine": .90}}
        candidates = []
        for mode in POLICY["modes"]:
            for seed in POLICY["seeds"]:
                domains = {"old": {"standardized_mse": 1., "cosine": .95},
                           "new": {"standardized_mse": .80 + .01 * len(candidates), "cosine": .91}}
                candidates.append({"mode": mode, "seed": seed, "checkpoint": str(cls.directory / f"{mode}-seed{seed}.pt"),
                                   "checkpoint_sha256": "c" * 64, "chosen": {"epoch": 2,
                                       "validation": domains, "balanced_score": balanced_score(domains, baseline_metrics),
                                       "new_group_improvement_ci": {"lower": .1}, "eligible": True, "failed_gates": []}})
        cls.checkpoint = cls.directory / "scratch-seed0.pt"
        cls.payload = {"schema": 1, "architecture": "mlp", "qwen_contract": FEATURE_CONTRACT,
                       "teacher_contract": "a" * 64, "dataset_sha256": "b" * 64,
                       "normalization_source": "frozen-approved-baseline", "state_dict": model.state_dict(),
                       "baseline_checkpoint_sha256": cls.baseline_sha,
                       "candidate": {"mode": "scratch", "seed": 0, "epoch": 2,
                                     "eligible_on_validation": True, "approved_for_runtime": False}}
        torch.save(cls.payload, cls.checkpoint)
        candidates[0]["checkpoint_sha256"] = sha(cls.checkpoint)
        cls.lock = {"schema": 1, "selected": True, "approved_for_runtime": False, "test_metrics_computed": False,
                    "architecture": "mlp", "qwen_contract": FEATURE_CONTRACT,
                    "teacher_contract": "a" * 64, "dataset_sha256": "b" * 64,
                    "locked_utc": datetime.now(timezone.utc).isoformat(), "policy": cls.policy,
                    "preregistered_policy_sha256": sha(cls.policy_path), "merged_manifest_path": str(cls.manifest),
                    "merged_manifest_sha256": sha(cls.manifest), "baseline_validation": baseline_metrics,
                    "candidates": candidates, "checkpoint": str(cls.checkpoint), "checkpoint_sha256": sha(cls.checkpoint)}
        cls.selection = cls.directory / "selection.json"

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def setUp(self):
        write(self.selection, self.lock)

    def test_valid_real_mlp_structure_cpu_and_health_provenance(self):
        with patch("torch.cuda.init", side_effect=AssertionError("No CUDA allowed")):
            model, _, info = load_candidate_cpu(self.root, self.selection)
            self.assertEqual(tuple(model(torch.zeros(1, 2048)).shape), (1, 4096))
            self.assertEqual(next(model.parameters()).device.type, "cpu")
        self.assertEqual(info["adapterMode"], "candidate")
        self.assertFalse(info["approvedForRuntime"])
        self.assertEqual(info["adapterSelectionSha256"], sha(self.selection))
        self.assertEqual(sha(self.baseline), self.baseline_sha)

    def test_unlocked_unselected_or_approved_labels_rejected(self):
        for key, value in [("locked_utc", None), ("selected", False), ("approved_for_runtime", True),
                           ("test_metrics_computed", True), ("architecture", "linear"), ("qwen_contract", "wrong")]:
            with self.subTest(key=key):
                lock = copy.deepcopy(self.lock)
                lock[key] = value
                write(self.selection, lock)
                with self.assertRaises(ValueError):
                    load_candidate_cpu(self.root, self.selection)

    def test_checkpoint_and_manifest_sha_changes_rejected(self):
        for key in ["checkpoint_sha256", "merged_manifest_sha256", "preregistered_policy_sha256"]:
            with self.subTest(key=key):
                lock = copy.deepcopy(self.lock)
                lock[key] = "0" * 64
                write(self.selection, lock)
                with self.assertRaises(ValueError):
                    load_candidate_cpu(self.root, self.selection)

    def test_forged_eligible_or_nonwinning_candidate_rejected(self):
        for mutation in ["old_regression", "nan", "score", "winner", "missing_run"]:
            with self.subTest(mutation=mutation):
                lock = copy.deepcopy(self.lock)
                if mutation == "old_regression":
                    lock["candidates"][0]["chosen"]["validation"]["old"]["standardized_mse"] = 1.1
                elif mutation == "nan":
                    lock["candidates"][0]["chosen"]["validation"]["new"]["cosine"] = float("nan")
                elif mutation == "score":
                    lock["candidates"][0]["chosen"]["balanced_score"] = .1
                elif mutation == "winner":
                    lock["checkpoint"] = lock["candidates"][1]["checkpoint"]
                    lock["checkpoint_sha256"] = lock["candidates"][1]["checkpoint_sha256"]
                else:
                    lock["candidates"].pop()
                write(self.selection, lock)
                with self.assertRaises(ValueError):
                    load_candidate_cpu(self.root, self.selection)

    def test_payload_contract_shape_and_frozen_normalization_rejected_even_with_updated_sha(self):
        try:
            for mutation in ["contract", "shape", "nonfinite", "normalization", "eligible"]:
                with self.subTest(mutation=mutation):
                    payload = dict(self.payload)
                    payload["state_dict"] = dict(payload["state_dict"])
                    if mutation == "contract":
                        payload["teacher_contract"] = "d" * 64
                    elif mutation == "shape":
                        payload["state_dict"]["network.1.weight"] = torch.zeros(2, 2)
                    elif mutation in ("nonfinite", "normalization"):
                        payload["state_dict"]["target_mean"] = torch.full((4096,), float("nan") if mutation == "nonfinite" else 1.)
                    else:
                        payload["candidate"] = {**payload["candidate"], "eligible_on_validation": False}
                    torch.save(payload, self.checkpoint)
                    lock = copy.deepcopy(self.lock)
                    lock["checkpoint_sha256"] = lock["candidates"][0]["checkpoint_sha256"] = sha(self.checkpoint)
                    write(self.selection, lock)
                    with self.assertRaises((ValueError, RuntimeError)):
                        load_candidate_cpu(self.root, self.selection)
        finally:
            torch.save(self.payload, self.checkpoint)
            self.lock["checkpoint_sha256"] = self.lock["candidates"][0]["checkpoint_sha256"] = sha(self.checkpoint)

    def test_default_backend_and_cli_protect_formal_service(self):
        self.assertIsNone(ArdyBackend("cpu").adapter_selection)
        from Server.ARDY.motion_service.app import main
        for flags in [[], ["--port", "8094", "--test-fixture"]]:
            with self.subTest(flags=flags), patch("sys.argv", ["app", "--adapter-selection", str(self.selection), *flags]):
                with self.assertRaises(SystemExit) as result:
                    main()
                self.assertEqual(result.exception.code, 2)


if __name__ == "__main__":
    unittest.main()
