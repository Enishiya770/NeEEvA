"""CPU-only checks for public suites and the actual C# semantic plan/ledger/compiler."""
import hashlib
import json
from pathlib import Path
import unittest
import uuid

import validate_semantic_motion_planning as probe
from validate_dialogue_motion import read_contract as read_legacy_contract


class SemanticMotionPlanningTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cpu = probe.run_cpu_checks()
        cls.suite = json.loads(probe.SUITE.read_text(encoding="utf-8"))
        cls.artifacts = probe.BUILD / ("cpu-fixtures-" + uuid.uuid4().hex)

    def test_real_csharp_checks(self):
        self.assertEqual(self.cpu["exitCode"], 0)
        self.assertGreater(self.cpu["checks"], 500)

    def test_frozen_suite_sizes_and_unique_public_ids(self):
        self.assertEqual(len(self.suite["development"]), 20)
        self.assertEqual(len(self.suite["holdout"]), 6)
        cases = self.suite["development"] + self.suite["holdout"]
        self.assertEqual(len({case["id"] for case in cases}), 26)
        self.assertEqual(len({case["user"] for case in cases}), 26)
        self.assertEqual({case["language"] for case in cases}, {"zh", "ja", "en"})
        for case in cases:
            self.assertTrue(case["expected"])
            self.assertEqual(probe.public_facts(case)["userTurn"], 2 + len(case.get("history", [])))

    def test_legacy_contract_is_exact_original_selected_value(self):
        archived = json.loads((probe.ROOT / "Tools/MotionAdapter/reports/palm-purpose-planning-v1.json").read_text(encoding="utf-8"))
        self.assertEqual(read_legacy_contract(), archived["contract"])
        self.assertEqual(hashlib.sha256(read_legacy_contract().encode()).hexdigest(), "2e0a7326c68054c9c05798e97f855982896d40679b5ca3b4428730893c4ddaee")

    def test_new_contract_is_single_semantic_specification(self):
        contract = probe.read_contract()
        self.assertIn("name=plan", contract)
        self.assertIn("当前交流适合时可自主动作", contract)
        self.assertIn("userTurn", contract)
        self.assertIn("固定6秒窗口", contract)
        self.assertNotEqual(contract, read_legacy_contract())

    def test_all_synthetic_ledger_histories_compile_with_actual_source_ids(self):
        for case in self.suite["development"]:
            if not case.get("history"):
                continue
            # Stopping is just a harmless public parsing fixture, never dispatched.
            row = probe.inspect_reply('<motion name="none"/>', case, self.artifacts / case["id"])
            self.assertTrue(row["csharp"]["ledgerValidated"], case["id"])
            self.assertIsNone(row["motionSuccess"])

    def test_synthetic_new_user_constraint_validates_but_does_not_prove_meaning(self):
        case = {"id": "source", "user": "Right arm forward; palm down; stay still.",
                "expected": {"name": "plan", "right": "forward"}}
        reply = '<motion name="plan" mode="hold" right="forward" rightPalm="down" userTurn="2" evidence="Right arm forward; palm down; stay still." lock="right,rightPalm"/>'
        row = probe.inspect_reply(reply, case, self.artifacts / "source")
        self.assertTrue(row["planningChecksPassed"])
        self.assertTrue(row["csharp"]["ledgerValidated"])
        self.assertEqual(row["sourceMeaningEntailment"], "not_proven_by_quote_membership")

    def test_response_does_not_persist_reasoning(self):
        case = {"id": "private", "user": "Answer without moving.", "expected": {"name": None}}
        row = probe.inspect_reply('<think>PRIVATE REASONING</think>All right.', case, self.artifacts / "private")
        self.assertNotIn("PRIVATE", json.dumps(row))
        self.assertEqual(row["rawResponse"], "All right.")
        self.assertTrue(row["planningChecksPassed"])


if __name__ == "__main__":
    unittest.main()
