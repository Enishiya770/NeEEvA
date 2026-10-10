"""Guard the semantic probe's pass criteria; these are not model quality tests."""
import unittest

from validate_device_goal_semantics_qwen import cases, inspect, payload


class DeviceGoalSemanticProbeChecks(unittest.TestCase):
    def setUp(self):
        self.history = {"goalId": "historical-goal", "entityId": "verified-device",
                        "targetDescription": "previously operated device", "originalRequest": "Turn that device off."}
        self.standing = {"goalId": "standing-goal", "boundEntityId": "verified-device",
                         "originalRequest": "Reopen the device you closed.", "desiredState": "on", "status": "suspended"}
        self.rows = {row["id"]: row for row in cases(self.history, self.standing)}

    def test_original_request_is_not_replaced_by_reminder(self):
        source = self.rows["reminder-not-opened"]["input"]
        self.assertEqual(source["existingGoal"]["originalRequest"], self.standing["originalRequest"])
        self.assertNotEqual(source["existingGoal"]["originalRequest"], source["latestUserText"])
        self.assertIsNone(source["proposal"])

    def test_log_regressions_require_real_relation_not_just_approved(self):
        for name in ("reminder-not-opened", "direct-first-open", "find-it-yourself", "continue-not-moving", "act-instead-of-apology"):
            self.assertIn("expected-relation:continue", inspect(self.rows[name], {
                "verdict": "approved", "relation": "unrelated", "referentGoalId": "standing-goal", "referentEntityId": ""}))

    def test_history_identity_cannot_be_attached_to_unrelated_goal(self):
        errors = inspect(self.rows["reopen-actual-closed-device"], {
            "verdict": "approved", "relation": "new", "referentGoalId": "invented-goal", "referentEntityId": "verified-device"})
        self.assertIn("invented-or-cross-paired-referent", errors)
        self.assertIn("invented-goal-referent", errors)

    def test_other_device_must_not_reuse_known_entity(self):
        errors = inspect(self.rows["explicit-other-device"], {
            "verdict": "approved", "relation": "new", "referentGoalId": "historical-goal", "referentEntityId": "verified-device"})
        self.assertIn("different-or-unauthorized-request-must-not-inherit", errors)

    def test_clarification_must_name_standing_goal_and_proven_identity(self):
        row = self.rows["clarify-same-proven-device"]
        self.assertEqual(row["input"]["existingGoal"]["status"], "waiting_clarification")
        self.assertFalse(inspect(row, {"verdict": "approved", "relation": "clarify",
                                    "referentGoalId": "standing-goal", "referentEntityId": "verified-device"}))
        self.assertIn("expected-referentEntityId", inspect(row, {
            "verdict": "approved", "relation": "clarify", "referentGoalId": "standing-goal", "referentEntityId": ""}))

    def test_ambiguity_requires_uncertain_without_binding(self):
        errors = inspect(self.rows["two-proven-devices-ambiguous"], {
            "verdict": "approved", "relation": "new", "referentGoalId": "historical-goal", "referentEntityId": "verified-device"})
        self.assertIn("expected-verdict:uncertain", errors)
        self.assertIn("ambiguous-device-must-not-bind", errors)

    def test_model_payload_is_short_auxiliary_review_not_conversation(self):
        contracts = {"contract": "production contract", "schema": '{}', "updateSchema": '{"required":["relation"]}'}
        request = payload(self.rows["reminder-not-opened"], contracts, "configured-model")
        self.assertEqual(request["id_slot"], 1)
        self.assertFalse(request["stream"])
        self.assertFalse(request["enable_thinking"])
        self.assertEqual(request["temperature"], 0)
        self.assertEqual(len(request["messages"]), 2)
        self.assertEqual(request["response_format"]["schema"]["required"], ["relation"])
        self.assertEqual(request["model"], "configured-model")


if __name__ == "__main__":
    unittest.main()
