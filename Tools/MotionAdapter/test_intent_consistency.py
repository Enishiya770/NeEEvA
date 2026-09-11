"""No model/HTTP/Unity: actual production parser and evaluation-integrity regressions."""
import json
import unittest

import validate_intent_consistency as probe
from review_intent_consistency import combine


class IntentConsistencyTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.sources = probe.compile_harness()
        cls.suite = probe.load_suite()

    def inspect(self, reply, user="Public fixture user turn."):
        return probe.inspect_reply(reply, user)

    def test_public_suite_has_new_disjoint_cases(self):
        self.assertEqual(len(self.suite["development"]), 12)
        self.assertEqual(len(self.suite["holdout"]), 8)
        previous = json.loads((probe.ROOT / "Tools/MotionAdapter/data/semantic-motion-planning-v1.json").read_text(encoding="utf-8"))
        old_ids = {case["id"] for case in previous["development"] + previous["holdout"]}
        for case in self.suite["development"] + self.suite["holdout"]:
            self.assertNotIn(case["id"], old_ids)
            self.assertTrue(case["manual"])

    def test_contract_default_is_production_plus_feedback(self):
        value = probe.read_contract()
        self.assertEqual(set(value["parts"]), {"GeneratedOutputContract", "MotionFeedbackOutputContract"})
        self.assertIn("replay", value["text"])
        self.assertIn("leftGoal", value["text"])

    def test_exact_309_character_description_rejected(self):
        description = ("Raise both hands beside the head, keeping the torso facing forward and moving gently. " * 5)[:309]
        self.assertEqual(len(description), 309)
        value = self.inspect('<motion name="generate" text="' + description + '"/>')
        self.assertFalse(value["accepted"])
        self.assertTrue(value["rejection"])
        self.assertIsNone(value["motionSuccess"])

    def test_source_and_hold_head_invalid_plan_rejected(self):
        value = self.inspect('<motion name="plan" mode="hold" left="forward" right="forward" joint="head" axis="right" tempo="gentle" lock="left,right"/>')
        self.assertFalse(value["accepted"])

    def test_private_text_is_not_persisted_in_inspector_output(self):
        value = self.inspect('<think>DO_NOT_PERSIST_THINK</think><thought>DO_NOT_PERSIST_THOUGHT</thought><say lang="en">Hello.</say><motion name="nod"/>')
        self.assertNotIn("DO_NOT_PERSIST", json.dumps(value))
        self.assertEqual(value["name"], "nod")
        self.assertEqual(value["publicSpeech"].strip(), "Hello.")

    def test_multilingual_speech_survives_pipe_without_codepage_corruption(self):
        text = "双手还没有到头旁。まだ動いていません。"
        value = self.inspect(text)
        self.assertEqual(value["publicSpeech"], text)
        self.assertEqual(value["executableChannel"], text)

    def test_replay_reference_is_explicit_and_cannot_inject_runtime_id(self):
        value = self.inspect('<motion name="replay" ref="public-session:3:1"/>')
        self.assertTrue(value["accepted"])
        self.assertEqual(value["intent"]["ReplayReference"], "public-session:3:1")
        bad = self.inspect('<motion name="replay" ref="last" actionId="injected"/>')
        self.assertFalse(bad["accepted"])

    def test_wrong_replay_reference_is_not_a_semantic_pass(self):
        case = next(row for row in self.suite["holdout"] if row["id"] == "held-en-replay-after-pose-change")
        parsed = self.inspect('<motion name="replay" ref="last"/>', case["user"])
        self.assertIn("wrong-replay-reference", probe.plan_checks(parsed, case["expected"])["failures"])

    def test_generated_goal_requires_both_original_sides(self):
        value = self.inspect('<motion name="generate" text="Bring both hands beside the head." leftGoal="near-head"/>')
        expected = {"names": ["generate"], "goal": {"left": "near-head", "right": "near-head"}}
        self.assertIn("missing-or-weakened-goal:right", probe.plan_checks(value, expected)["failures"])
        good = self.inspect('<motion name="generate" text="Bring both hands beside the head." leftGoal="near-head" rightGoal="near-head"/>')
        self.assertTrue(probe.plan_checks(good, expected)["passed"])
        self.assertIsNone(good["motionSuccess"])

    def test_generated_goal_invalid_value_rejected(self):
        value = self.inspect('<motion name="generate" text="Raise a hand." leftGoal="individual-index-finger"/>')
        self.assertFalse(value["accepted"])

    def test_no_action_is_valid_for_status_question(self):
        value = self.inspect("The available observation does not verify that movement.")
        self.assertTrue(probe.plan_checks(value, {"names": [None, "none"]})["passed"])
        self.assertFalse(value["executionAdmissionTested"])

    def test_compose_is_not_rejected_merely_for_no_semantic_plan_mode_field(self):
        value = self.inspect('<motion name="compose" left="outward" right="forward" joint="head" axis="right" cycles="1"/>')
        checks = probe.plan_checks(value, {"names": ["plan"], "fields": {"mode": "oscillate", "left": "outward", "joint": "head"}})
        self.assertTrue(checks["passed"])

    def test_disabled_experimental_plan_does_not_pass_default_evaluation(self):
        value = self.inspect('<motion name="plan" mode="hold" right="outward"/>')
        self.assertIn("wrong-action-route", probe.plan_checks(value, {"names": ["plan"], "fields": {"right": "outward"}})["failures"])

    def test_synthetic_feedback_remains_marked_and_expected_answers_are_not_sent(self):
        for case in self.suite["development"] + self.suite["holdout"]:
            body = probe.synthetic_body_facts(case)
            self.assertEqual(body["fixtureSource"], "public-synthetic-evaluation-state-not-a-real-avatar-observation")
            messages = probe.make_messages(case, "Public test contract")
            serialized = json.dumps(messages, ensure_ascii=False)
            self.assertNotIn("manualRubric", serialized)
            self.assertNotIn("expected", serialized)
            for criterion in case["manual"]:
                self.assertNotIn(criterion, serialized)

    def test_slot_guard_requires_explicit_idle_preserved_context(self):
        good = [{"id": i, "is_processing": False, "n_ctx": 65536} for i in range(3)]
        self.assertTrue(probe.all_slots_idle(good))
        self.assertFalse(probe.all_slots_idle([]))
        self.assertFalse(probe.all_slots_idle(good[:2]))
        self.assertFalse(probe.all_slots_idle([{**row, "is_processing": None} for row in good]))

    def test_review_cannot_turn_synthetic_planning_pass_into_motion_success(self):
        source = {"suiteSha256": "a" * 64, "split": "development", "results": [{"caseId": "public",
            "csharp": {"executableChannel": "I have not reached it."}, "planningChecks": {"passed": True}}]}
        raw = json.dumps(source).encode()
        label = {"caseId": "public", "intentMatches": "pass", "narrationGrounded": "pass", "reason": "Grounded fixture answer.",
                 "publicEvidenceExcerpts": ["I have not reached it."]}
        review = {"sourceReportSha256": probe.sha(raw), "results": [label]}
        result = combine(source, review, raw)
        self.assertIsNone(result["semanticMotionSuccess"])
        self.assertIsNone(result["results"][0]["actualMotionSuccess"])
        review["sourceReportSha256"] = "b" * 64
        with self.assertRaises(ValueError):
            combine(source, review, raw)

    def test_review_rejects_cherry_picked_or_invented_evidence(self):
        source = {"suiteSha256": "a" * 64, "split": "development", "results": [{"caseId": "public",
            "csharp": {"executableChannel": "Not yet."}, "planningChecks": {"passed": True}}]}
        raw = json.dumps(source).encode()
        with self.assertRaises(ValueError):
            combine(source, {"sourceReportSha256": probe.sha(raw), "results": []}, raw)
        with self.assertRaises(ValueError):
            combine(source, {"sourceReportSha256": probe.sha(raw), "results": [{"caseId": "public", "intentMatches": "pass",
                "narrationGrounded": "pass", "reason": "Wrong evidence", "publicEvidenceExcerpts": ["Done."]}]}, raw)


if __name__ == "__main__":
    unittest.main()
