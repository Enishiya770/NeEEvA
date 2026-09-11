"""Export validated Core27 motion-space global rotations for Unity Humanoid retargeting.

Schema 1 uses Quaternion objects {x,y,z,w}, so Unity JsonUtility can read
Quaternion[] directly. Both restGlobalRotations and frames[].globalRotations
are in the character's canonical Unity space (LH, +Y up, +Z forward). They
are not Unity bone-local rotations and must be combined with the target
avatar's recorded rest orientations. Root translation is deliberately absent.

Pinned ARDY evidence: exports/mujoco.py documents RH, Y-up, Z-forward and
maps MuJoCo's +X forward to +Z. Core27 neutral joints have left limbs at +X
and toes in +Z. Thus C=diag(-1,1,1), p_u=C p_a, R_u=C R_a C^-1.
kinematics.py composes rotations without bind pre/post rotations;
postprocess.py uses identity t_pose_rotation and viser_utils.py initializes
Core27 with identity global rotations. This motion-space neutral T-pose is
distinct from the skin asset's bind_rig_transform (which must not be used as
the reference of raw ARDY motion rotations).
"""

import argparse
import ast
import hashlib
import json
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation


ARDY_TO_UNITY = np.diag([-1.0, 1.0, 1.0])
SCHEMA = 1
JOINT_COUNT = 27


def validate_rotations(value, label):
    rotations = np.asarray(value, dtype=np.float64)
    if rotations.shape[-2:] != (3, 3) or not np.isfinite(rotations).all():
        raise ValueError(f'{label}: expected finite 3x3 rotation matrices')
    error = np.max(np.abs(np.swapaxes(rotations, -1, -2) @ rotations - np.eye(3)))
    if error > 2e-3 or np.max(np.abs(np.linalg.det(rotations) - 1.0)) > 2e-3:
        raise ValueError(f'{label}: matrices are not proper orthonormal rotations')
    return rotations


def unity_rotations(rotations):
    """Conjugate rotations under a handedness change; never negate one matrix side."""
    return ARDY_TO_UNITY @ validate_rotations(rotations, 'ARDY rotations') @ ARDY_TO_UNITY


def quaternion_objects(rotations, continuous=False):
    """Return normalized xyzw objects, with temporal hemisphere continuity if requested."""
    rotations = validate_rotations(rotations, 'Unity rotations')
    shape = rotations.shape[:-2]
    quaternions = Rotation.from_matrix(rotations.reshape(-1, 3, 3)).as_quat().reshape(*shape, 4)
    if continuous:
        if quaternions.ndim != 3:
            raise ValueError('Continuous quaternions require [frames,joints,3,3] rotations')
        quaternions[0] *= np.where(quaternions[0, :, 3:4] < 0, -1, 1)
        for frame in range(1, len(quaternions)):
            dots = np.sum(quaternions[frame] * quaternions[frame-1], axis=-1, keepdims=True)
            quaternions[frame] *= np.where(dots < 0, -1, 1)
    else:
        quaternions *= np.where(quaternions[..., 3:4] < 0, -1, 1)
    def serialize(values):
        return [{key:round(float(component), 9) for key, component in zip('xyzw', q)} for q in values]
    return [serialize(frame) for frame in quaternions] if continuous else serialize(quaternions)


def validate_hierarchy(names, parents):
    if len(names) != JOINT_COUNT or len(set(names)) != JOINT_COUNT or names[0] != 'Hips':
        raise ValueError('Expected 27 unique Core27 names beginning with Hips')
    parents = np.asarray(parents)
    if parents.shape != (JOINT_COUNT,) or not np.issubdtype(parents.dtype, np.integer):
        raise ValueError('Expected 27 integer joint parents')
    if parents[0] != -1 or any(p < 0 or p >= i for i, p in enumerate(parents[1:], start=1)):
        raise ValueError('Expected one root and parents preceding children')


def fk_rotations(local_rotations, parents):
    local = validate_rotations(local_rotations, 'Local rotations')
    result = np.empty_like(local)
    for i, parent in enumerate(parents):
        result[..., i, :, :] = local[..., i, :, :] if parent < 0 else result[..., parent, :, :] @ local[..., i, :, :]
    return result


def fk_positions(global_rotations, neutral_joints, parents):
    """Root-relative positions under ARDY's identity-neutral rotation convention."""
    result = np.zeros((*global_rotations.shape[:-2], 3), dtype=np.float64)
    for i, parent in enumerate(parents):
        if parent >= 0:
            offset = neutral_joints[i] - neutral_joints[parent]
            result[..., i, :] = result[..., parent, :] + (global_rotations[..., parent, :, :] @ offset[..., None])[..., 0]
    return result


def load_core27_reference(ardy_source):
    """Read the pinned skeleton definition and CPU tensor asset without loading a model."""
    import torch
    definitions = ardy_source/'ardy/skeleton/definitions.py'
    tree = ast.parse(definitions.read_text(encoding='utf-8'))
    skeleton = next((node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == 'CoreSkeleton27'), None)
    if skeleton is None:
        raise ValueError('Pinned source has no CoreSkeleton27 definition')
    layout = None
    for node in skeleton.body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'bone_order_names_with_parents' for t in node.targets):
            layout = ast.literal_eval(node.value)
    if layout is None:
        raise ValueError('Core27 hierarchy is missing')
    names = [name for name, _ in layout]
    parents = np.asarray([-1 if parent is None else names.index(parent) for _, parent in layout], dtype=np.int64)
    validate_hierarchy(names, parents)
    asset = ardy_source/'ardy/assets/skeletons/cskel27/joints.p'
    neutral = torch.load(asset, map_location='cpu', weights_only=True).squeeze().numpy().astype(np.float64)
    if neutral.shape != (JOINT_COUNT, 3) or not np.isfinite(neutral).all():
        raise ValueError('Invalid Core27 neutral joints')
    # These checks verify both the left/right reflection and the sign of forward.
    for side, sign in [('Left', 1), ('Right', -1)]:
        if sign * neutral[names.index(side+'Hand'), 0] <= 0:
            raise ValueError('Core27 left/right convention differs from the pinned exporter')
        if neutral[names.index(side+'ToeBase'), 2] <= neutral[names.index(side+'Foot'), 2]:
            raise ValueError('Core27 forward direction differs from +Z')
    return names, parents, neutral, hashlib.sha256(asset.read_bytes()).hexdigest()


def build_clip(motion, *, clip_id, expected_names, expected_parents, neutral_joints, source):
    names = [str(name) for name in motion['joint_names']]
    parents = np.asarray(motion['joint_parents'])
    validate_hierarchy(names, parents)
    if names != expected_names or not np.array_equal(parents, expected_parents):
        raise ValueError('Motion joint order or hierarchy differs from pinned Core27')
    if not isinstance(clip_id, str) or not clip_id.strip():
        raise ValueError('Clip ID must be nonempty')
    fps_value = np.asarray(motion['fps'])
    text_value = np.asarray(motion['text'])
    if fps_value.ndim != 0 or text_value.ndim != 0:
        raise ValueError('fps and text must be scalar values')
    fps, text = float(fps_value), str(text_value)
    if not np.isfinite(fps) or fps <= 0 or fps > 240:
        raise ValueError('Invalid frame rate')
    global_rotations = validate_rotations(motion['global_rot_mats'], 'Global rotations')
    if global_rotations.ndim != 4 or global_rotations.shape[1] != JOINT_COUNT or not len(global_rotations):
        raise ValueError('Expected nonempty [frames,27,3,3] global rotations')
    if 'local_rot_mats' in motion:
        local = np.asarray(motion['local_rot_mats'])
        if local.shape != global_rotations.shape or not np.allclose(fk_rotations(local, parents), global_rotations, atol=2e-3, rtol=0):
            raise ValueError('Global rotations disagree with local-rotation forward kinematics')
    if 'posed_joints' in motion:
        posed = np.asarray(motion['posed_joints'])
        if posed.shape != (*global_rotations.shape[:2], 3) or not np.isfinite(posed).all():
            raise ValueError('Invalid posed joints')
        expected = fk_positions(global_rotations, neutral_joints, parents)
        if not np.allclose(posed-posed[:, :1], expected, atol=2e-3, rtol=0):
            raise ValueError('Motion positions disagree with the Core27 neutral rotation convention')
    rest = np.repeat(np.eye(3)[None], JOINT_COUNT, axis=0)
    frames = quaternion_objects(unity_rotations(global_rotations), continuous=True)
    return {
        'schema':SCHEMA, 'id':clip_id, 'text':text, 'fps':fps,
        'jointNames':names, 'jointParents':parents.tolist(),
        'restGlobalRotations':quaternion_objects(unity_rotations(rest)),
        'frames':[{'globalRotations':rotations} for rotations in frames],
        'source':{**source, 'sourceCoordinateSystem':'ardy-rh-y-up-z-forward',
                  'coordinateSystem':'unity-lh-y-up-z-forward', 'reflectionAxis':'x',
                  'quaternionOrder':'xyzw', 'rotationSpace':'character-global',
                  'restConvention':'Core27 motion-space neutral T-pose; identity global rotations',
                  'rootTranslationApplied':False, 'frameCount':len(frames),
                  'coordinateEvidence':'ardy/exports/mujoco.py; cskel27/joints.p left-hand +X and toes +Z',
                  'restEvidence':'ardy/skeleton/kinematics.py; ardy/postprocess.py; ardy/viz/viser_utils.py'},
    }


def export_clip(input_path, output_path, *, ardy_source=None, clip_id=None):
    input_path, output_path = Path(input_path), Path(output_path)
    lock_path = Path(__file__).with_name('upstream-lock.json')
    lock = json.loads(lock_path.read_text(encoding='utf-8-sig'))
    root = Path(__file__).resolve().parents[2]
    ardy_source = Path(ardy_source) if ardy_source is not None else root/f'Server/ARDY/vendor/ardy-{lock["ardy"]}'
    names, parents, neutral, neutral_sha = load_core27_reference(ardy_source)
    metadata = {'file':input_path.name, 'sha256':hashlib.sha256(input_path.read_bytes()).hexdigest(),
                'ardyRevision':lock['ardy'], 'coreRepo':lock['ardy_core_repo'],
                'coreRevision':lock['ardy_core_revision'], 'neutralJointsSha256':neutral_sha}
    with np.load(input_path, allow_pickle=False) as motion:
        result = build_clip(motion, clip_id=clip_id or input_path.stem, expected_names=names,
                            expected_parents=parents, neutral_joints=neutral, source=metadata)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(result, ensure_ascii=False, separators=(',', ':'), allow_nan=False)+'\n', encoding='utf-8')
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--input', type=Path, required=True, help='One ARDY Core27 NPZ clip')
    parser.add_argument('--output', type=Path, required=True, help='Destination Unity JSON TextAsset')
    parser.add_argument('--id', help='Clip ID; defaults to the input filename stem')
    parser.add_argument('--ardy-source', type=Path, help='Pinned ARDY source directory; default from upstream-lock.json')
    args = parser.parse_args()
    result = export_clip(args.input, args.output, ardy_source=args.ardy_source, clip_id=args.id)
    print(json.dumps({'id':result['id'], 'frames':len(result['frames']), 'fps':result['fps'],
                      'coordinateSystem':result['source']['coordinateSystem'], 'output':str(args.output)}))


if __name__ == '__main__':
    main()
