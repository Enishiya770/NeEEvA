"""CPU fixtures for metric meaning, held-out gates and immutable evaluation plans."""
import argparse
import json
import tempfile
import unittest
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation

from Server.ARDY.evaluate_native_motion import build_plan,grouped_summary,reserve_output,reserve_test_audit,select_records
from Server.ARDY.native_motion_measurements import assess,measure_frames,observe
from Tools.MotionAdapter.data import write_bundle


NAMES=["Hips","Spine3","Head","LeftArm","RightArm","LeftHand","RightHand"]


def fixture(left,right,chest_pitch=0):
    count=len(left)
    p=np.zeros((count,len(NAMES),3))
    p[:,NAMES.index("Spine3"),1]=1.3
    p[:,NAMES.index("Head"),1]=1.6
    for name in ("LeftArm","RightArm"):
        p[:,NAMES.index(name),1]=1.3
    p[:,NAMES.index("LeftHand"),1]=left
    p[:,NAMES.index("RightHand"),1]=right
    p[:,NAMES.index("LeftHand"),0]=.3
    p[:,NAMES.index("RightHand"),0]=-.3
    r=np.broadcast_to(np.eye(3),(count,len(NAMES),3,3)).copy()
    r[:,NAMES.index("Spine3")]=Rotation.from_euler("x",chest_pitch,degrees=True).as_matrix()
    return measure_frames({"posed_joints":p,"global_rot_mats":r,"root_positions":np.zeros((count,3))},NAMES)


class ObservationTests(unittest.TestCase):
    def test_bilateral_height_requires_same_frame(self):
        frames=fixture([1.8,1.2,1.8,1.2],[1.2,1.8,1.2,1.8])
        spec={"metric":"wrist_relative_height_m","side":"both","reference":"head","expectation":"above","threshold_m":.05}
        self.assertFalse(observe(spec,frames)["thresholdSatisfied"])
        self.assertTrue(observe({**spec,"side":"left"},frames)["thresholdSatisfied"])
        self.assertTrue(observe({**spec,"side":"right"},frames)["thresholdSatisfied"])

    def test_height_is_world_y_not_chest_y(self):
        level=fixture([1.8]*4,[1.8]*4)
        tilted=fixture([1.8]*4,[1.8]*4,chest_pitch=70)
        np.testing.assert_allclose(level["wristRelativeHeightMeters"]["head"],tilted["wristRelativeHeightMeters"]["head"])
        self.assertFalse(np.allclose(level["wristChestLocalPositionMeters"],tilted["wristChestLocalPositionMeters"]))

    def test_side_and_event_timing(self):
        frames=fixture([.9,1.3,1.3,.9],[.9]*4)
        spec={"metric":"wrist_relative_height_m","side":"left","reference":"shoulder","expectation":"reach_band","band_m":[-.05,.05]}
        result=observe(spec,frames)
        self.assertTrue(result["thresholdSatisfied"])
        self.assertAlmostEqual(result["simultaneousEvent"]["firstObservedSeconds"],.05)
        self.assertAlmostEqual(result["simultaneousEvent"]["longestContinuousSeconds"],.1)
        self.assertFalse(observe({**spec,"side":"right"},frames)["thresholdSatisfied"])

    def test_unknown_counts_palms_and_activity_never_pass(self):
        frames=fixture([1.3]*4,[1.3]*4)
        specs=[{"metric":"repetition_count","event":"nod","expected":2},
               {"metric":"event_order","events":[{"event":"nod"}]},
               {"metric":"palm_up","side":"left"},
               {"metric":"wrist_excursion_m","side":"both","expectation":"low_activity"}]
        result=assess({"include_in_success_summary":True,"assessment":{"automatic_semantic_pass":False,"machine_observations":specs}},frames)
        self.assertIsNone(result["semanticPass"])
        self.assertTrue(all(o["thresholdSatisfied"] is None for o in result["observations"]))
        self.assertFalse(result["palmOrientationEvaluated"])

    def test_boundary_is_excluded_and_semantic_rate_unknown(self):
        result=assess({"include_in_success_summary":False,"assessment":{"machine_observations":[]}},fixture([1.3]*4,[1.3]*4))
        grouped=grouped_summary([{"condition":"teacher","family":"boundary","evaluationTrack":"boundary","semanticGroup":"unsupported","assessment":result}])[0]
        self.assertEqual(grouped["includedCases"],0)
        self.assertIsNone(grouped["semanticSuccessCount"])

    def test_invalid_pose_or_band_rejected(self):
        frames=fixture([1.3]*4,[1.3]*4)
        with self.assertRaises(ValueError):
            observe({"metric":"wrist_relative_height_m","side":"left","reference":"head","expectation":"reach_band","band_m":[1,-1]},frames)
        with self.assertRaises(ValueError):
            fixture([float("nan")]*4,[1.3]*4)


class AuditTests(unittest.TestCase):
    def test_test_requires_flag_and_split_cannot_leak(self):
        rows=[{"id":"v","split":"val"},{"id":"t","split":"test"}]
        with self.assertRaises(ValueError):
            select_records(rows,["t"],"test",False)
        with self.assertRaises(ValueError):
            select_records(rows,["v","t"],"val",False)
        with self.assertRaises(ValueError):
            select_records(rows,["v","v"],"val",False)
        self.assertEqual(select_records(rows,["t"],"test",True),[rows[1]])

    def test_marker_and_existing_results_cannot_be_overwritten(self):
        with tempfile.TemporaryDirectory() as temp:
            folder=Path(temp)/"output"
            plan={"datasetSha256":"fixture","seeds":[0,1]}
            digest=reserve_output(folder,plan)
            self.assertEqual(digest,reserve_output(folder,plan))
            marker=Path(temp)/"once.json"
            reserve_test_audit(marker,digest,plan,folder)
            with self.assertRaises(FileExistsError):
                reserve_test_audit(marker,digest,plan,folder)
            (folder/"report.json").write_text("{}")
            with self.assertRaises(ValueError):
                reserve_output(folder,plan)

    def test_feature_manifest_binds_text_and_labels_before_filtering(self):
        with tempfile.TemporaryDirectory() as temp:
            folder=Path(temp)
            rows=[{"id":"fixture-val","split":"val","semantic_group":"fixture","text":"Fixture only","assessment":{"automatic_semantic_pass":False}}]
            dataset=folder/"dataset.jsonl"
            dataset.write_text(json.dumps(rows[0])+"\n")
            write_bundle(folder/"q.npz",np.ones((1,2048)),rows,{"kind":"qwen","feature_contract":"fixture"})
            write_bundle(folder/"t.npz",np.ones((1,4096)),rows,{"kind":"teacher","feature_contract":"fixture"})
            (folder/"checkpoint.pt").write_bytes(b"plan-only checkpoint fixture")
            args=argparse.Namespace(dataset=dataset,record_ids="fixture-val",split="val",final_test=False,seeds=[0,1],adapter=[f"old={folder/'checkpoint.pt'}"],history=["cold"],qwen_bundle=folder/"q.npz",teacher_bundle=folder/"t.npz")
            plan,*_=build_plan(args)
            self.assertEqual(plan["recordIds"],["fixture-val"])
            rows[0]["text"]="Changed text"
            dataset.write_text(json.dumps(rows[0])+"\n")
            with self.assertRaises(ValueError):
                build_plan(args)


if __name__=="__main__":
    unittest.main()
