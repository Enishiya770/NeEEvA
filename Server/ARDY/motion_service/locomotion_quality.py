"""Shared rejection-only standing locomotion checks; no pose correction."""
from __future__ import annotations

import numpy as np

from .locomotion_protocol import CORE_NAMES


MAX_QUALITY_ATTEMPTS = 3
QUALITY_SEED_STRIDE = 1000003
_UNAPPLIED_JOINTS = {"Spine1", "RightHandEnd", "RightHandThumb1", "LeftHandEnd", "LeftHandThumb1"}
_BODY_JOINTS = [i for i, name in enumerate(CORE_NAMES) if name not in _UNAPPLIED_JOINTS]


def standing_quality(roots, rotations, previous_root, previous_rotations, source_height):
    """Audit accepted history -> candidate and all candidate transitions.

    Kept identical to the existing stream gate, including its applied-bone list.
    Passing these coarse standing/discontinuity checks does not accept naturalness.
    """
    if not all(np.isfinite(a).all() for a in (roots, rotations, previous_root, previous_rotations)):
        return [{"code": "nonfinite_prefix", "message": "Committed prefix or retained history is nonfinite"}]
    reasons = []
    low, high = source_height*.72, source_height*1.25
    bad_height = np.flatnonzero((roots[:, 1] < low) | (roots[:, 1] > high))
    if len(bad_height):
        i = int(bad_height[0])
        reasons.append({"code": "standing_height", "frame": i, "value": float(roots[i, 1]), "minimum": low, "maximum": high})
    previous_roots = np.concatenate((previous_root[None], roots[:-1]))
    root_steps = np.linalg.norm(roots-previous_roots, axis=-1)
    i = int(np.argmax(root_steps))
    if root_steps[i] > .1:
        reasons.append({"code": "root_step", "frame": i, "value": float(root_steps[i]), "maximum": .1})
    previous_matrices = np.concatenate((previous_rotations[None], rotations[:-1]))
    relative = np.swapaxes(previous_matrices[:, _BODY_JOINTS], -1, -2) @ rotations[:, _BODY_JOINTS]
    angles = np.degrees(np.arccos(np.clip((np.trace(relative, axis1=-2, axis2=-1)-1)/2, -1, 1)))
    frame, selected_joint = np.unravel_index(np.argmax(angles), angles.shape)
    if angles[frame, selected_joint] > 35:
        reasons.append({"code": "joint_step", "frame": int(frame), "joint": CORE_NAMES[_BODY_JOINTS[selected_joint]],
                        "value": float(angles[frame, selected_joint]), "maximum": 35.})
    return reasons


def decoded_window_quality(decoded, retained, commit_frames, source_height):
    """Exclude reconstructed past and any unused prediction tail, as before."""
    roots = decoded["root_positions"][0, 16:16+commit_frames].detach().float().cpu().numpy()
    rotations = decoded["global_rot_mats"][0, 16:16+commit_frames].detach().float().cpu().numpy()
    previous_root = retained["root_positions"][0, -1].detach().float().cpu().numpy()
    previous_rotations = retained["global_rot_mats"][0, -1].detach().float().cpu().numpy()
    return standing_quality(roots, rotations, previous_root, previous_rotations, source_height)
