import unittest

import numpy as np
from scipy.spatial.transform import Rotation

from Server.ARDY.native_motion_continuity import UPPER_BODY, angles, continuity_observations


class ContinuityTests(unittest.TestCase):
    def fixture(self):
        names = list(UPPER_BODY)
        history = np.broadcast_to(np.eye(3), (16, len(names), 3, 3)).copy()
        generated = np.broadcast_to(np.eye(3), (120, len(names), 3, 3)).copy()
        hp = {"global_rot_mats": history, "local_rot_mats": history.copy(),
              "posed_joints": np.zeros((16, len(names), 3))}
        gp = {"global_rot_mats": generated, "local_rot_mats": generated.copy(),
              "posed_joints": np.zeros((120, len(names), 3))}
        return names, hp, gp

    def test_geodesic_not_euler_wrap_difference(self):
        a, b = Rotation.from_euler("y", [[179], [-179]], degrees=True).as_matrix()
        self.assertAlmostEqual(float(angles(a, b)), 2, places=8)

    def test_initial_join_is_separate_from_generated_speed(self):
        names, hp, gp = self.fixture()
        head = names.index("Head")
        gp["global_rot_mats"][:, head] = Rotation.from_euler("y", 30, degrees=True).as_matrix()
        report = continuity_observations(hp, gp, names)
        self.assertAlmostEqual(report["firstFrameGlobalRotationJumpDegrees"]["maximum"], 30, places=8)
        self.assertAlmostEqual(report["speedSummaryIncludingInitialJoin"]["maximum"], 600, places=8)
        self.assertLess(report["speedSummaryGeneratedFramesOnly"]["maximum"], 1e-4)
        self.assertIsNone(report["naturalnessPass"])

    def test_seam_uses_frame_39_to_40(self):
        names, hp, gp = self.fixture()
        gp["global_rot_mats"][40:, names.index("LeftHand")] = Rotation.from_euler("x", 12, degrees=True).as_matrix()
        report = continuity_observations(hp, gp, names)
        self.assertAlmostEqual(report["windowSeams"][0]["maximumStepDegrees"], 12, places=8)
        self.assertLess(report["windowSeams"][1]["maximumStepDegrees"], 1e-4)
        self.assertEqual(report["windowSeams"][0]["seconds"], 2)

    def test_last_actual_history_frame_is_the_join_reference(self):
        names, hp, gp = self.fixture()
        bone = names.index("RightHand")
        hp["global_rot_mats"][-1, bone] = Rotation.from_euler("z", 5, degrees=True).as_matrix()
        hp["posed_joints"][-1, bone, 0] = .125
        report = continuity_observations(hp, gp, names)
        self.assertAlmostEqual(report["firstFrameGlobalRotationJumpDegrees"]["maximum"], 5, places=8)
        self.assertEqual(report["firstFrameSourceWristDisplacementMetersLeftRight"], [0, .125])


if __name__ == "__main__":
    unittest.main()
