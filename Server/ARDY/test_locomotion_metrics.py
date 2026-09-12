"""Analytic, CPU-only fixtures test metric semantics rather than model quality."""
import json
import unittest

import numpy as np

from Server.ARDY.locomotion_metrics import CORE27_NAMES, FOOT_NAMES, LocomotionThresholds, measure_locomotion


class LocomotionMetricsTests(unittest.TestCase):
    def fixture(self, frames=120):
        names = list(CORE27_NAMES)
        positions = np.zeros((frames, 27, 3))
        positions[:, :, 1] = 1
        for name in FOOT_NAMES:
            positions[:, names.index(name), 1] = 0
        rotations = np.broadcast_to(np.eye(3), (frames, 27, 3, 3)).copy()
        targets = positions[:, names.index("Hips")].copy()
        return {"posed_joints": positions, "global_rot_mats": rotations}, names, targets

    def test_exact_stationary_stop_with_measured_windows(self):
        arrays, names, targets = self.fixture()
        report = measure_locomotion(arrays, names, targets, stop_start_frame=80,
                                    window_generation_seconds=[.8, .9, .7],
                                    target_headings=np.tile([0, 1], (120, 1)))
        self.assertTrue(all(check["status"] == "passed" for check in report["checks"].values()))
        self.assertEqual(report["stop"]["tailStartFrame"], 99)
        self.assertEqual(report["stop"]["sampledSeconds"], 1)
        self.assertEqual(report["inference"]["status"], "passed")
        self.assertEqual(report["naturalness"]["status"], "unknown")
        json.dumps(report, allow_nan=False)

    def test_tracking_is_timed_world_error_without_fitted_alignment(self):
        arrays, names, targets = self.fixture()
        arrays["posed_joints"][:, :, 0] += .3
        targets[:, 1] += 50  # Target pelvis Y is not a planar trajectory error.
        report = measure_locomotion(arrays, names, targets)
        self.assertAlmostEqual(report["rootTracking"]["rmseMeters"], .3)
        self.assertEqual(report["checks"]["rootPlanarRmse"]["status"], "failed")
        self.assertEqual(report["checks"]["rootPlanarMaximumError"]["status"], "passed")
        self.assertEqual(report["checks"]["rootPlanarEndpointError"]["status"], "failed")

    def test_endpoint_and_rmse_do_not_hide_one_frame_teleport(self):
        arrays, names, targets = self.fixture()
        arrays["posed_joints"][50, names.index("Hips"), 0] = 1
        report = measure_locomotion(arrays, names, targets)
        self.assertEqual(report["checks"]["rootPlanarRmse"]["status"], "passed")
        self.assertEqual(report["checks"]["rootPlanarMaximumError"]["status"], "failed")
        self.assertEqual(report["checks"]["rootPlanarEndpointError"]["status"], "passed")
        self.assertEqual(report["windowBoundaries"]["allFrameMaximumJointStepMeters"], 1)

    def test_stop_needs_explicit_command_full_second_and_stationary_target(self):
        arrays, names, targets = self.fixture()
        report = measure_locomotion(arrays, names, targets)
        self.assertEqual(report["checks"]["stopDrift"]["status"], "unknown")
        report = measure_locomotion(arrays, names, targets, stop_start_frame=100)
        self.assertEqual(report["checks"]["stopSpeedP95"]["status"], "unknown")
        targets[:, 0] = np.arange(120) / 20
        report = measure_locomotion(arrays, names, targets, stop_start_frame=80)
        self.assertIn("still moving", report["stop"]["reason"])
        self.assertEqual(report["checks"]["stopDrift"]["status"], "unknown")

    def test_out_and_back_stop_drift_cannot_hide_behind_zero_endpoint(self):
        arrays, names, targets = self.fixture()
        arrays["posed_joints"][99:, names.index("Hips"), 0] = np.r_[np.linspace(0, .2, 11), np.linspace(.18, 0, 10)]
        report = measure_locomotion(arrays, names, targets, stop_start_frame=80)
        self.assertEqual(report["stop"]["endpointDisplacementMeters"], 0)
        self.assertAlmostEqual(report["stop"]["maximumExcursionFromTailStartMeters"], .2)
        self.assertEqual(report["checks"]["stopDrift"]["status"], "failed")
        self.assertEqual(report["checks"]["stopSpeedP95"]["status"], "failed")

    def test_fast_low_feet_are_not_filtered_by_false_predicted_contacts(self):
        arrays, names, targets = self.fixture()
        for name in FOOT_NAMES:
            arrays["posed_joints"][:, names.index(name), 0] = np.arange(120) / 20
        arrays["foot_contacts"] = np.zeros((120, 4), dtype=bool)
        report = measure_locomotion(arrays, names, targets)
        self.assertAlmostEqual(report["feet"]["lowHorizontalSpeedMetersPerSecond"]["p95"], 1)
        self.assertEqual(report["checks"]["lowFootHorizontalSpeedP95"]["status"], "failed")

    def test_airborne_feet_make_sliding_unknown_not_passed(self):
        arrays, names, targets = self.fixture()
        for name in FOOT_NAMES:
            arrays["posed_joints"][:, names.index(name), 1] = .2
        report = measure_locomotion(arrays, names, targets)
        self.assertEqual(report["checks"]["lowFootHorizontalSpeedP95"]["status"], "unknown")
        self.assertEqual(report["checks"]["bothFeetAirborneDuration"]["status"], "failed")
        self.assertEqual(report["feet"]["longestBothFeetAboveLowHeightSeconds"], 6)

    def test_airborne_requires_all_four_samples_and_reports_penetration(self):
        arrays, names, targets = self.fixture()
        arrays["posed_joints"][:, names.index("LeftFoot"), 1] = .5
        arrays["posed_joints"][:, names.index("RightToeBase"), 1] = -.06
        report = measure_locomotion(arrays, names, targets)
        self.assertEqual(report["checks"]["bothFeetAirborneDuration"]["status"], "passed")
        self.assertEqual(report["checks"]["footJointPenetration"]["status"], "failed")
        self.assertAlmostEqual(report["feet"]["maximumPenetrationMeters"], .06)

    def test_window_seam_uses_39_to_40_and_geodesic_rotation(self):
        arrays, names, targets = self.fixture()
        arrays["posed_joints"][40:, :, 0] = .31
        angle = np.deg2rad(40)
        arrays["global_rot_mats"][40:, names.index("LeftHand")] = [[np.cos(angle), -np.sin(angle), 0], [np.sin(angle), np.cos(angle), 0], [0, 0, 1]]
        report = measure_locomotion(arrays, names, targets)
        seam, second = report["windowBoundaries"]["seams"]
        self.assertEqual((seam["previousFrame"], seam["firstNewFrame"]), (39, 40))
        self.assertEqual(seam["checks"]["rootStep"]["status"], "failed")
        self.assertEqual(seam["checks"]["jointStep"]["status"], "failed")
        self.assertAlmostEqual(seam["maximumGlobalRotationStepDegrees"], 40)
        self.assertEqual(seam["checks"]["rotationStep"]["status"], "failed")
        self.assertLess(second["maximumGlobalRotationStepDegrees"], 1e-4)

    def test_missing_or_equal_budget_timing_does_not_pass(self):
        arrays, names, targets = self.fixture()
        report = measure_locomotion(arrays, names, targets)
        self.assertEqual(report["inference"]["status"], "unknown")
        report = measure_locomotion(arrays, names, targets, window_generation_seconds=[.5, 2, .5])
        self.assertEqual(report["inference"]["status"], "failed")
        report = measure_locomotion(arrays, names, targets, window_generation_seconds=[.5])
        self.assertEqual(report["inference"]["unmeasuredWindows"], 2)
        self.assertEqual(report["inference"]["status"], "unknown")

    def test_short_final_window_uses_its_actual_playback_budget(self):
        arrays, names, targets = self.fixture(50)
        report = measure_locomotion(arrays, names, targets, window_generation_seconds=[.5, .6])
        self.assertEqual(report["inference"]["windows"][1]["playbackSeconds"], .5)
        self.assertEqual(report["inference"]["status"], "failed")

    def test_heading_uses_direction_and_is_reflection_invariant(self):
        arrays, names, targets = self.fixture()
        angle = np.deg2rad(20)
        arrays["global_rot_mats"][:, names.index("Hips")] = [[np.cos(angle), 0, np.sin(angle)], [0, 1, 0], [-np.sin(angle), 0, np.cos(angle)]]
        directions = np.tile([0, 0, 1], (120, 1))
        original = measure_locomotion(arrays, names, targets, target_headings=directions)
        self.assertAlmostEqual(original["heading"]["endpointErrorDegrees"], 20)
        mirror = np.diag([-1, 1, 1])
        arrays["posed_joints"] = arrays["posed_joints"] @ mirror
        arrays["global_rot_mats"] = mirror @ arrays["global_rot_mats"] @ mirror
        reflected = measure_locomotion(arrays, names, targets @ mirror, target_headings=directions @ mirror)
        self.assertAlmostEqual(reflected["heading"]["endpointErrorDegrees"], 20)

    def test_vertical_root_forward_makes_heading_unknown(self):
        arrays, names, targets = self.fixture()
        arrays["global_rot_mats"][:, names.index("Hips")] = [[1, 0, 0], [0, 0, -1], [0, 1, 0]]
        report = measure_locomotion(arrays, names, targets, target_headings=np.tile([0, 1], (120, 1)))
        self.assertEqual(report["heading"]["validFrames"], 0)
        self.assertEqual(report["checks"]["headingP95Error"]["status"], "unknown")
        json.dumps(report, allow_nan=False)

    def test_response_dwell_cannot_leak_past_next_target_change(self):
        arrays, names, targets = self.fixture()
        arrays["posed_joints"][:43, names.index("Hips"), 0] = 1
        report = measure_locomotion(arrays, names, targets, target_change_frames=[40, 45])
        first, second = report["targetChanges"]
        self.assertIsNone(first["positionToleranceReacquisitionSeconds"])
        self.assertEqual(second["positionToleranceReacquisitionSeconds"], 0)
        self.assertTrue(second["alreadyInsideToleranceAtChange"])
        self.assertEqual(second["responseAssessment"]["status"], "unknown")

    def test_response_reports_first_sustained_not_transient_tolerance(self):
        arrays, names, targets = self.fixture()
        arrays["posed_joints"][40:50, names.index("Hips"), 0] = 1
        arrays["posed_joints"][43, names.index("Hips"), 0] = 0
        report = measure_locomotion(arrays, names, targets, target_change_frames=[40])
        observation = report["targetChanges"][0]
        self.assertEqual(observation["firstSustainedWithinToleranceFrame"], 50)
        self.assertEqual(observation["positionToleranceReacquisitionSeconds"], .5)

    def test_input_contract_rejects_nonfinite_invalid_rotations_and_names(self):
        arrays, names, targets = self.fixture()
        for change in (lambda a: a["posed_joints"].__setitem__((0, 0, 0), np.nan),
                       lambda a: a["global_rot_mats"].__setitem__((0, 0, 0, 0), 2)):
            invalid = {key: value.copy() for key, value in arrays.items()}
            change(invalid)
            with self.assertRaises(ValueError):
                measure_locomotion(invalid, names, targets)
        with self.assertRaises(ValueError):
            measure_locomotion(arrays, ["Hips"] * 27, targets)
        with self.assertRaises(ValueError):
            measure_locomotion(arrays, names, targets, target_headings=np.zeros((120, 2)))
        with self.assertRaises(ValueError):
            measure_locomotion(arrays, names, targets, target_change_frames=[45, 40])
        with self.assertRaises(ValueError):
            LocomotionThresholds(root_rmse_m=-1)

    def test_joint_names_are_used_instead_of_assumed_order(self):
        arrays, names, targets = self.fixture()
        arrays["posed_joints"][:, names.index("RightToeBase"), 1] = -.08
        permutation = np.arange(27)[::-1]
        arrays = {key: value[:, permutation] for key, value in arrays.items()}
        report = measure_locomotion(arrays, [names[index] for index in permutation], targets)
        self.assertEqual(report["rootTracking"]["rmseMeters"], 0)
        self.assertAlmostEqual(report["feet"]["maximumPenetrationMeters"], .08)

    def test_explicit_ground_plane_and_root_height_excursion(self):
        arrays, names, targets = self.fixture()
        arrays["posed_joints"][50, names.index("Hips"), 1] += .3
        arrays["posed_joints"][:, :, 1] += 2
        report = measure_locomotion(arrays, names, targets, ground_y=2)
        self.assertAlmostEqual(report["rootHeight"]["spanMeters"], .3)
        self.assertEqual(report["checks"]["rootHeightSpan"]["status"], "failed")
        self.assertEqual(report["feet"]["maximumPenetrationMeters"], 0)
        self.assertEqual(report["checks"]["bothFeetAirborneDuration"]["status"], "passed")

    def test_rotation_wrap_is_two_degrees_not_358(self):
        arrays, names, targets = self.fixture()
        for start, end, degrees in ((0, 40, 179), (40, 120, -179)):
            angle = np.deg2rad(degrees)
            arrays["global_rot_mats"][start:end, names.index("Hips")] = [[np.cos(angle), 0, np.sin(angle)], [0, 1, 0], [-np.sin(angle), 0, np.cos(angle)]]
        report = measure_locomotion(arrays, names, targets)
        self.assertAlmostEqual(report["windowBoundaries"]["seams"][0]["maximumGlobalRotationStepDegrees"], 2)

    def test_response_dwell_requires_sample_endpoints_covering_full_duration(self):
        arrays, names, targets = self.fixture(45)
        report = measure_locomotion(arrays, names, targets, target_change_frames=[40])
        observation = report["targetChanges"][0]
        self.assertEqual(observation["requiredDwellFrames"], 6)
        # Five samples at 20 Hz cover only .20 seconds, less than the .25 second dwell.
        self.assertIsNone(observation["positionToleranceReacquisitionSeconds"])


if __name__ == "__main__":
    unittest.main()
