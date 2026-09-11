"""Behavioral checks for short, controlled head gestures and their provenance."""
import copy
import unittest

import numpy as np
from scipy.spatial.transform import Rotation

from .conversational_head import PROFILES, apply_profile, evaluate_profile, profile_statistics


def source_clip():
    identity = {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0}
    return {
        "schema": 1, "id": "raw", "fps": 20.0, "text": "Original large-amplitude model request",
        "jointNames": ["Hips", "Spine3", "Neck", "Head", "LeftArm"],
        "jointParents": [-1, 0, 1, 2, 1], "restGlobalRotations": [dict(identity) for _ in range(5)],
        "frames": [{"globalRotations": [dict(identity) for _ in range(5)]} for _ in range(120)],
        "source": {"sha256": "a" * 64, "file": "raw.npz", "coreRevision": "original-core-revision",
                   "coordinateSystem": "unity-lh-y-up-z-forward", "rotationSpace": "character-global"},
    }


def quaternions(clip):
    return np.asarray([[[q[key] for key in "xyzw"] for q in f["globalRotations"]] for f in clip["frames"]])


class ConversationalHeadTests(unittest.TestCase):
    def test_endpoints_and_provenance_without_mutating_input(self):
        raw = source_clip()
        before = copy.deepcopy(raw)
        for identifier, count in (("nod", 37), ("shake-head", 41)):
            result = apply_profile(raw, identifier)
            q = quaternions(result)
            self.assertEqual(len(q), count)
            self.assertTrue(np.isfinite(q).all())
            np.testing.assert_allclose(np.linalg.norm(q, axis=-1), 1, atol=1e-14)
            np.testing.assert_array_equal(q[[0, -1]], np.tile([0, 0, 0, 1], (2, 5, 1)))
            np.testing.assert_array_equal(q[:, [0, 1, 4]], np.tile([0, 0, 0, 1], (count, 3, 1)))
            self.assertEqual(result["source"]["rawText"], raw["text"])
            self.assertEqual(result["source"]["sha256"], raw["source"]["sha256"])
            self.assertEqual(result["source"]["coreRevision"], "original-core-revision")
            self.assertNotEqual(result["text"], raw["text"])
            self.assertEqual(result["text"], result["displayIntent"])
            self.assertEqual(result["source"]["rotationApplication"], "additive-local")
            self.assertTrue(result["source"]["processing"]["notNativeArdyMotion"])
            self.assertFalse(result["source"]["processing"]["rawMotionRotationsUsed"])
        self.assertEqual(raw, before)

    def test_correct_axes_amplitudes_and_immediate_response(self):
        nod = quaternions(apply_profile(source_clip(), "nod"))[:, 3]
        nod_vector = Rotation.from_quat(nod).apply([0, 0, 1])
        self.assertLess(nod_vector[1, 1], 0)  # +X rotates forward down in canonical Unity space.
        self.assertTrue(np.all(nod_vector[:, 1] <= 1e-14))
        np.testing.assert_allclose(nod_vector[:, 0], 0, atol=1e-14)
        self.assertAlmostEqual(np.rad2deg(Rotation.from_quat(nod).magnitude()).max(), 8.0)
        shake = quaternions(apply_profile(source_clip(), "shake-head"))[:, 3]
        vector = Rotation.from_quat(shake).apply([0, 0, 1])
        self.assertGreater(vector[1, 0], 0)
        self.assertLess(vector[:, 0].min(), -0.1)
        np.testing.assert_allclose(vector[:, 1], 0, atol=1e-14)
        rotvec = np.rad2deg(Rotation.from_quat(shake).as_rotvec())
        np.testing.assert_allclose(rotvec[:, [0, 2]], 0, atol=1e-14)
        self.assertAlmostEqual(rotvec[:, 1].max(), 6.0)
        self.assertAlmostEqual(rotvec[:, 1].min(), -6.0)

    def test_neck_head_local_composition_is_twenty_eighty(self):
        for identifier, axis in (("nod", 0), ("shake-head", 1)):
            q = quaternions(apply_profile(source_clip(), identifier))
            neck, head = Rotation.from_quat(q[:, 2]), Rotation.from_quat(q[:, 3])
            total = np.rad2deg(head.as_rotvec())
            neck_local = np.rad2deg(neck.as_rotvec())
            head_local = np.rad2deg((neck.inv() * head).as_rotvec())
            np.testing.assert_allclose(neck_local, total * 0.2, atol=1e-12)
            np.testing.assert_allclose(head_local, total * 0.8, atol=1e-12)
            np.testing.assert_allclose((neck * (neck.inv() * head)).as_matrix(), head.as_matrix(), atol=1e-14)

    def test_C2_continuity_and_zero_endpoint_derivatives(self):
        for identifier, profile in PROFILES.items():
            for time, angle in profile["knots"]:
                position, velocity, acceleration, jerk = evaluate_profile(np.array([time]), identifier)
                self.assertAlmostEqual(position[0], angle, places=12)
                self.assertAlmostEqual(velocity[0], 0, places=12)
                self.assertAlmostEqual(acceleration[0], 0, places=12)
                values = evaluate_profile(np.array([time - 1e-6, time + 1e-6]), identifier)
                self.assertLess(np.ptp(values[0]), 1e-9)
                self.assertLess(np.max(np.abs(values[1])), 1e-7)
                self.assertLess(np.max(np.abs(values[2])), 0.01)
                self.assertTrue(all(np.isfinite(value).all() for value in values))
            outside = evaluate_profile(np.array([-0.1, profile["duration"] + 0.1]), identifier)
            for value in outside:
                np.testing.assert_array_equal(value, 0)

    def test_derivatives_agree_with_finite_differences_and_have_bounded_steps(self):
        for identifier, profile in PROFILES.items():
            for (start, _), (end, _) in zip(profile["knots"], profile["knots"][1:]):
                time = np.linspace(start + 0.01, end - 0.01, 19)
                h = 1e-4
                position, velocity, acceleration, _ = evaluate_profile(time, identifier)
                lower = evaluate_profile(time - h, identifier)[0]
                upper = evaluate_profile(time + h, identifier)[0]
                np.testing.assert_allclose((upper - lower) / (2 * h), velocity, atol=1e-5)
                np.testing.assert_allclose((upper - 2 * position + lower) / h**2, acceleration, atol=1e-4)
            stats = profile_statistics(identifier)
            self.assertLess(stats["maximumFrameStepDegrees"], 1.6)
            self.assertLess(stats["maximumAngularSpeedDegreesPerSecond"], 31)
            self.assertLess(stats["maximumAngularAccelerationDegreesPerSecond2"], 165)
            self.assertLess(stats["maximumAngularJerkDegreesPerSecond3"], 3100)

    def test_reject_invalid_profile_contract_and_double_application(self):
        with self.assertRaises(ValueError):
            apply_profile(source_clip(), "left-wave")
        for field, value in (("fps", 30), ("schema", 2), ("fps", float("nan"))):
            raw = source_clip()
            raw[field] = value
            with self.assertRaises(ValueError):
                apply_profile(raw, "nod")
        raw = source_clip()
        raw["jointParents"][3] = 1
        with self.assertRaises(ValueError):
            apply_profile(raw, "nod")
        with self.assertRaises(ValueError):
            apply_profile(apply_profile(source_clip(), "nod"), "nod")
        with self.assertRaises(ValueError):
            evaluate_profile([float("nan")], "nod")


if __name__ == "__main__":
    unittest.main()
