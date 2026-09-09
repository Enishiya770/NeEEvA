"""Boundary checks for rejecting unusable model output, without a model/network call."""
import json
import unittest

from check_speech_review_experiment import parse_draft, parse_review


class AcceptanceBoundary(unittest.TestCase):
    def test_identical_han_and_sentence_switch_preserve_language(self):
        for parts in ([{"language": "ja", "text": "了解。"}],
                      [{"language": "zh", "text": "了解。"}],
                      [{"language": "zh", "text": "听到哦。"}, {"language": "ja", "text": "了解。"}]):
            obj, issues = parse_review(json.dumps({"status": "unchanged", "segments": parts}), "stop", parts)
            self.assertEqual(obj["segments"], parts)
            self.assertEqual(issues, [])

    def test_truncated_and_control_output_cannot_be_released(self):
        parts = [{"language": "ja", "text": "了解。"}]
        fixtures = [(json.dumps({"status": "unchanged", "segments": parts}), "length"),
                    ('{"status":"repaired","segments":[', "stop"),
                    (json.dumps({"status": "repaired", "segments": [{"language": "ja", "text": "<silent/>"}]}), "stop"),
                    (json.dumps({"status": "needs_review", "segments": parts}), "stop"),
                    (json.dumps({"status": "unchanged", "segments": [{"language": "zh", "text": "了解。"}]}), "stop"),
                    (json.dumps({"status": "repaired", "segments": [{"language": "fr", "text": "Bonjour"}]}), "stop")]
        for raw, reason in fixtures:
            with self.subTest(raw=raw):
                obj, issues = parse_review(raw, reason, parts)
                self.assertIsNone(obj)
                self.assertTrue(issues)

    def test_complete_empty_draft_retains_mode_but_is_not_speech(self):
        draft = {"draft": "", "language": "ja", "observed_mode": "singing", "mode_confidence": .9,
                 "confidence": .4, "understanding": "", "uncertainty": "", "inner_reaction": ""}
        obj, issues = parse_draft(json.dumps(draft), "stop")
        self.assertEqual(issues, [])
        self.assertEqual(obj["observed_mode"], "singing")
        self.assertFalse(obj["draft"])
        for change in ({"language": ""}, {"confidence": True}, {"draft": "<lang code='ja'/>了解。"}):
            obj, issues = parse_draft(json.dumps({**draft, **change}), "stop")
            self.assertIsNone(obj)
            self.assertTrue(issues)

    def test_structural_validation_does_not_pretend_to_check_semantics(self):
        bad = {"status": "repaired", "segments": [{"language": "ja", "text": "我在听。"}]}
        obj, issues = parse_review(json.dumps(bad), "stop", [])
        self.assertIsNotNone(obj)
        self.assertEqual(issues, [])  # Must still fail separate content evaluation.


if __name__ == "__main__":
    unittest.main()
