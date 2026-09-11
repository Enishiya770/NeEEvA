"""Actual production parser regressions for capability checks independent of goal metadata."""
import unittest

import validate_intent_consistency as probe


class GeneratedCapabilityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        probe.compile_harness()

    def test_unsupported_finger_without_goal_is_rejected(self):
        result = probe.inspect_reply('<motion name="generate" text="Wag only the right index finger."/>', "Public capability fixture.")
        self.assertFalse(result["accepted"])
        self.assertIn("independent-fingers-unsupported", result["rejection"])

    def test_unsupported_finger_with_valid_goal_is_rejected(self):
        result = probe.inspect_reply('<motion name="generate" text="Wag only the right index finger." rightGoal="forward"/>', "Public capability fixture.")
        self.assertFalse(result["accepted"])
        self.assertIn("independent-fingers-unsupported", result["rejection"])

    def test_supported_generate_is_not_rejected_for_omitting_goal(self):
        result = probe.inspect_reply('<motion name="generate" text="Raise the right hand."/>', "Public capability fixture.")
        self.assertTrue(result["accepted"])

    def test_basic_still_uses_its_own_attribute_contract(self):
        self.assertTrue(probe.inspect_reply('<motion name="nod"/>', "Public capability fixture.")["accepted"])
        self.assertFalse(probe.inspect_reply('<motion name="nod" text="Raise the hand."/>', "Public capability fixture.")["accepted"])


if __name__ == "__main__":
    unittest.main()
