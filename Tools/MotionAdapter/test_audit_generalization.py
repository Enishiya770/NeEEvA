"""Synthetic CPU observations and static frozen-manifest checks; no real test predictions."""
import copy
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import numpy as np

from . import audit_generalization as audit


class FinalFeatureAuditTests(unittest.TestCase):
    def test_frozen_suite_static_only(self):
        rows = audit.validate_suite()
        self.assertEqual(len(rows), 80)
        self.assertEqual(sum(r["include_in_success_summary"] for r in rows), 64)

    def test_explicit_final_flag_required_before_any_plan_or_prediction(self):
        args = SimpleNamespace(plan_only=False, final_test=False)
        with patch.object(audit, "build_plan") as plan:
            with self.assertRaisesRegex(ValueError, "explicit --final-test"):
                audit.run(args)
            plan.assert_not_called()

    def test_selection_requires_locked_eligible_validation_candidate(self):
        selection = {"schema": 1, "selected": True, "locked_utc": "synthetic-fixture", "test_metrics_computed": False,
                     "checkpoint": "synthetic.pt", "checkpoint_sha256": "candidate", "policy": {"baseline_checkpoint_sha256": "old"},
                     "candidates": [{"checkpoint": "synthetic.pt", "checkpoint_sha256": "candidate", "chosen": {"eligible": True}}]}
        audit.validate_selection(selection, "old")
        for change in ("eligible", "test", "baseline"):
            bad = copy.deepcopy(selection)
            if change == "eligible": bad["candidates"][0]["chosen"]["eligible"] = False
            elif change == "test": bad["test_metrics_computed"] = True
            else: bad["policy"]["baseline_checkpoint_sha256"] = "other"
            with self.assertRaises(ValueError): audit.validate_selection(bad, "old")

    def test_native_validation_must_be_val_and_use_locked_candidate(self):
        report = {"status": "complete", "plan": {"split": "val", "finalTest": False, "adapters": {"candidate": {"sha256": "selected"}}}}
        audit.validate_native_validation(report, "selected")
        for field, value in (("split", "test"), ("finalTest", True)):
            bad = copy.deepcopy(report)
            bad["plan"][field] = value
            with self.assertRaises(ValueError): audit.validate_native_validation(bad, "selected")
        with self.assertRaises(ValueError): audit.validate_native_validation(report, "other")

    def test_marker_prevents_second_candidate_or_output_for_same_suite(self):
        with tempfile.TemporaryDirectory() as temporary:
            marker = Path(temporary) / "audit-once.json"
            plan = {"selectionSha256": "first-selection", "files": {"suite": {"sha256": "fixed-suite"}}, "lockedCandidateSha256": "first-model"}
            audit.reserve_final_audit(marker, plan, Path(temporary) / "first.json")
            saved = json.loads(marker.read_text())
            self.assertEqual(saved["selectionSha256"], "first-selection")
            self.assertEqual(saved["candidateSha256"], "first-model")
            second = {**plan, "selectionSha256": "second-selection", "lockedCandidateSha256": "second-model"}
            with self.assertRaises(FileExistsError):
                audit.reserve_final_audit(marker, second, Path(temporary) / "different-output.json")
            self.assertEqual(json.loads(marker.read_text()), saved)

    def test_metrics_use_explicit_frozen_old_std(self):
        values = audit.row_metrics(np.array([[0., 0.]]), np.array([[2., 8.]]), np.array([2., 4.]))
        self.assertAlmostEqual(values["standardized_mse"][0], 2.5)
        self.assertEqual(values["cosine"][0], 0)
        with self.assertRaises(ValueError):
            audit.row_metrics(np.zeros((1, 2)), np.ones((1, 2)), np.array([1., 0.]))

    def test_boundary_separate_and_group_macro_not_paraphrase_weighted(self):
        rows = [{"id": str(i), "semantic_group": group, "evaluation_track": track}
                for i, (group, track) in enumerate([( "s1", audit.TRACKS[0])] * 3 + [("s2", audit.TRACKS[0])]
                                                  + [("b1", audit.TRACKS[1]), ("b2", audit.TRACKS[1])])]
        old = {"standardized_mse": np.array([1., 1., 1., 3., 100., 100.]), "cosine": np.ones(6), "norm_ratio": np.ones(6)}
        new = {"standardized_mse": np.array([.5, .5, .5, 2., 200., 200.]), "cosine": np.ones(6), "norm_ratio": np.ones(6)}
        result = audit.summarize(rows, old, new)
        self.assertEqual(set(result), set(audit.TRACKS))
        self.assertEqual(result[audit.TRACKS[0]]["group_macro_metrics"]["old"]["standardized_mse"], 2)
        self.assertEqual(result[audit.TRACKS[0]]["group_macro_metrics"]["locked_candidate"]["standardized_mse"], 1.25)
        self.assertFalse(result[audit.TRACKS[1]]["include_in_supported_success_summary"])
        self.assertIsNone(result[audit.TRACKS[0]]["action_success_rate"])
        self.assertIsNone(result[audit.TRACKS[1]]["action_success_rate"])

    def test_final_bootstrap_wording_is_descriptive_not_selection_evidence(self):
        result = audit.final_descriptive_interval([1., 3., 2.])
        self.assertEqual(result["mean_group_mse_improvement"], 2)
        self.assertEqual(result["semantic_groups"], 3)
        self.assertIn("Final descriptive", result["scope"])
        self.assertNotIn("Validation selection evidence", result["scope"])


if __name__ == "__main__":
    unittest.main()
