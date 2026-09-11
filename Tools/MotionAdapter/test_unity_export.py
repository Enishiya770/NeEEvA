"""Math/contract checks for the ARDY -> Unity boundary; no GPU or model required."""

import unittest

import numpy as np
from scipy.spatial.transform import Rotation

from .export_unity import (
    ARDY_TO_UNITY, build_clip, fk_positions, fk_rotations, quaternion_objects,
    unity_rotations, validate_rotations,
)


def matrices_from_objects(values):
    quaternions = [[q[k] for k in 'xyzw'] for q in values]
    return Rotation.from_quat(quaternions).as_matrix()


class UnityExportTests(unittest.TestCase):
    def test_reflection_preserves_forward_and_lifts_left_limb(self):
        # ARDY left points +X, Unity left points -X; both share up/forward.
        np.testing.assert_array_equal(ARDY_TO_UNITY @ [0, 1, 0], [0, 1, 0])
        np.testing.assert_array_equal(ARDY_TO_UNITY @ [0, 0, 1], [0, 0, 1])
        left_raise = Rotation.from_euler('z', 90, degrees=True).as_matrix()
        converted = unity_rotations(left_raise)
        np.testing.assert_allclose(converted @ [-1, 0, 0], [0, 1, 0], atol=1e-12)
        np.testing.assert_allclose(np.linalg.det(converted), 1.0, atol=1e-12)

    def test_handedness_conversion_commutes_with_fk(self):
        # Three joints have noncommuting rotations, exposing reversed multiplication.
        local = Rotation.from_euler('xyz', [[10, 20, 30], [40, -10, 20], [0, 70, 10]], degrees=True).as_matrix()[None]
        parents = [-1, 0, 1]
        neutral = np.array([[0, 0, 0], [1, 0.3, 0], [2, 0.3, 0.1]])
        global_a = fk_rotations(local, parents)
        global_u = fk_rotations(unity_rotations(local), parents)
        np.testing.assert_allclose(global_u, unity_rotations(global_a), atol=1e-12)
        positions_a = fk_positions(global_a, neutral, parents)
        positions_u = fk_positions(global_u, neutral @ ARDY_TO_UNITY, parents)
        np.testing.assert_allclose(positions_u, positions_a @ ARDY_TO_UNITY, atol=1e-12)

    def test_global_delta_retargets_different_rest_axes(self):
        # The target bind axes are unrelated to source axes. At rest it must remain
        # unchanged, and a known world-space action must left-multiply that rest.
        source_rest = Rotation.from_euler('xyz', [40, 10, -30], degrees=True).as_matrix()
        action = Rotation.from_euler('xyz', [20, -70, 15], degrees=True).as_matrix()
        target_rest = Rotation.from_euler('xyz', [-30, 60, 90], degrees=True).as_matrix()
        converted_rest = unity_rotations(source_rest)
        rest_target = converted_rest @ converted_rest.T @ target_rest
        moving_target = unity_rotations(action @ source_rest) @ converted_rest.T @ target_rest
        np.testing.assert_allclose(rest_target, target_rest, atol=1e-12)
        np.testing.assert_allclose(moving_target, unity_rotations(action) @ target_rest, atol=1e-12)

    def test_quaternion_180_crossing_is_continuous_and_roundtrips(self):
        original = Rotation.from_euler('y', [[170], [179], [181], [190]], degrees=True).as_matrix()[:, None]
        objects = quaternion_objects(original, continuous=True)
        quaternions = np.array([[frame[0][k] for k in 'xyzw'] for frame in objects])
        self.assertTrue(np.all((quaternions[1:] * quaternions[:-1]).sum(axis=-1) > 0))
        np.testing.assert_allclose(np.linalg.norm(quaternions, axis=-1), 1, atol=1e-8)
        reconstructed = np.stack([matrices_from_objects(frame) for frame in objects])
        np.testing.assert_allclose(reconstructed, original, atol=2e-8)

    def test_bad_rotations_and_mismatched_hierarchy_are_rejected(self):
        with self.assertRaisesRegex(ValueError, 'proper orthonormal'):
            validate_rotations(ARDY_TO_UNITY, 'reflection is not a rotation')
        with self.assertRaisesRegex(ValueError, 'finite'):
            validate_rotations(np.full((3, 3), np.nan), 'bad')
        names = ['Hips'] + [f'Joint{i}' for i in range(1, 27)]
        parents = np.asarray([-1] + list(range(26)))
        neutral = np.column_stack([np.zeros(27), np.arange(27), np.zeros(27)])
        rotations = np.broadcast_to(np.eye(3), (2, 27, 3, 3)).copy()
        motion = {'joint_names':names, 'joint_parents':parents, 'fps':20, 'text':'stand',
                  'global_rot_mats':rotations, 'local_rot_mats':rotations,
                  'posed_joints':fk_positions(rotations, neutral, parents)}
        args = dict(clip_id='test', expected_names=names, expected_parents=parents,
                    neutral_joints=neutral, source={})
        clip = build_clip(motion, **args)
        self.assertEqual(clip['schema'], 1)
        self.assertEqual(len(clip['frames']), 2)
        self.assertFalse(clip['source']['rootTranslationApplied'])
        self.assertNotIn('rootPositions', clip)
        self.assertEqual(clip['restGlobalRotations'][0], {'x':0, 'y':0, 'z':0, 'w':1})
        motion['joint_names'] = list(reversed(names))
        with self.assertRaisesRegex(ValueError, 'Hips'):
            build_clip(motion, **args)
        motion['joint_names'] = names
        motion['local_rot_mats'] = rotations.copy()
        motion['local_rot_mats'][0, 2] = Rotation.from_euler('x', 60, degrees=True).as_matrix()
        with self.assertRaisesRegex(ValueError, 'forward kinematics'):
            build_clip(motion, **args)
        motion['local_rot_mats'] = rotations
        motion['posed_joints'][0, 2, 0] += 1
        with self.assertRaisesRegex(ValueError, 'neutral rotation convention'):
            build_clip(motion, **args)


if __name__ == '__main__':
    unittest.main()
