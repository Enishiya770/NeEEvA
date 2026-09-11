"""CPU-only label semantics; no feature export or motion generation."""
import unittest

from Tools.MotionAdapter.generalization_labels import assessment_for
from Server.ARDY.native_motion_measurements import assess
from Server.ARDY.test_native_motion_eval import fixture


def config(family,side,**spec):
    return {"family":family,"side":side,"description":"This text must not determine the labels.","specification":spec}


class GeneralizationLabelTests(unittest.TestCase):
    def test_generator_and_exported_forms_match_and_ignore_text(self):
        item=config("raise","both",action="raise",target="overhead")
        expected=assessment_for(item)
        row={"family":"raise","side":"both","text":"An unrelated description","configuration":item["specification"]}
        self.assertEqual(expected,assessment_for(row))
        item["description"]="Lower only the left hand"
        self.assertEqual(expected,assessment_for(item))

    def test_raise_and_point_use_explicit_target_not_direction(self):
        label=assessment_for(config("point","right",action="point",target="face",direction="forward"))
        height=label["machine_observations"][0]
        self.assertEqual((height["side"],height["reference"],height["expectation"]),("right","head","reach_band"))
        self.assertEqual(height["band_m"],[-.12,.12])
        missing=assessment_for(config("point","right",action="point",direction="up"))["machine_observations"][0]
        self.assertEqual(missing["expectation"],"report_only")

    def test_low_targets_state_weaker_landmark_limit(self):
        label=assessment_for(config("point","both",action="point",target="below_waist",direction="down"))
        observation=label["machine_observations"][0]
        self.assertEqual((observation["reference"],observation["expectation"],observation["threshold_m"]),("chest","below",0.0))
        self.assertTrue(any("no waist landmark" in s for s in label["manual_checks"]))

    def test_alternating_height_does_not_require_simultaneity(self):
        label=assessment_for(config("alternating_arms","both",action="alternating_raise",target="face",count=3,order="right-first"))
        heights=[o for o in label["machine_observations"] if o["metric"]=="wrist_relative_height_m"]
        self.assertEqual({o["side"] for o in heights},{"left","right"})
        self.assertTrue(all(o["side"]!="both" for o in heights))
        result=assess({"assessment":label},fixture([1.6,.9,1.6,.9],[.9,1.6,.9,1.6]))
        self.assertIsNone(result["semanticPass"])
        self.assertTrue(all(o["thresholdSatisfied"] is None for o in result["observations"] if o["specification"]["metric"] in ("event_order","repetition_count")))

    def test_head_torso_and_composites_are_report_only(self):
        for item in [config("head_look","up",action="look"),config("torso_lean","backward",action="lean",count=2),config("sequence","mixed",action="sequence",configuration="fixture-sequence",temporal="ordered_sequence")]:
            label=assessment_for(item)
            self.assertFalse(label["automatic_semantic_pass"])
            self.assertTrue(all(o["expectation"]=="report_only" for o in label["machine_observations"]))

    def test_no_invented_counts_or_unknown_family_gate(self):
        repeat=assessment_for(config("raise","left",action="raise",target="chest",ending="repeat"))
        self.assertFalse(any(o["metric"]=="repetition_count" for o in repeat["machine_observations"]))
        unknown=assessment_for(config("not_implemented","mixed",action="unknown"))
        self.assertEqual(unknown["machine_observations"],[])
        with self.assertRaises(ValueError):
            assessment_for(config("wave","left",count=True))


if __name__=="__main__":
    unittest.main()
