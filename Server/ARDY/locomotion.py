"""Bounded locomotion experiment using ARDY's native spatial constraints.

This module is deliberately independent of the stationary dialogue service.
Routes are in canonical Unity metres (+Z forward, +X right); model constraints
are reflected once to ARDY. Neither root snapping nor foot IK alters outputs.
"""
from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np

from Tools.MotionAdapter.export_unity import ARDY_TO_UNITY, build_clip

FPS = 20
WINDOW = 40
HISTORY = 16
DURATION = 10
DESCRIPTION = "A person walks naturally at a comfortable pace, following the indicated path, and comes to a stop."


def smooth(value):
    value = np.clip(value, 0., 1.)
    return value * value * (3. - 2. * value)


@dataclass
class Route:
    positions: np.ndarray
    headings: np.ndarray  # Unity yaw radians, zero = +Z
    stop_frame: int
    change_frame: int | None = None

    def validate(self):
        if (self.positions.ndim != 2 or self.positions.shape[1] != 3 or
                self.headings.shape != (len(self.positions),) or
                not np.isfinite(self.positions).all() or not np.isfinite(self.headings).all() or
                np.any(np.abs(self.positions[:, 1]) > 1e-8)):
            raise ValueError("Route needs matching finite planar positions and headings")
        return self


def make_route(case="straight-stop", *, changed=False, anchor=None, velocity=None):
    """Ten seconds of flat-ground targets; redirect is revealed only at t=4s.

    The changed route starts from the last committed generated pelvis/velocity,
    not unplayed output. This harness treats each completed window as played;
    a real controller must replace it with actually rendered full-body history.
    """
    if case not in ("straight-stop", "turn-stop", "redirect-stop"):
        raise ValueError("Unknown route case")
    n = FPS * DURATION
    t = (np.arange(n) + 1) / FPS
    speed = .65 * smooth(t / .9) * (1 - smooth((t - 6.5) / 1.5))
    yaw = np.zeros(n)
    if case == "turn-stop":
        yaw = math.pi / 2 * smooth((t - 3.) / 2.5)
    velocities = np.stack((np.sin(yaw) * speed, np.zeros(n), np.cos(yaw) * speed), axis=1)
    positions = np.cumsum(velocities / FPS, axis=0)
    change = 4 * FPS if case == "redirect-stop" and changed else None
    if changed:
        if case != "redirect-stop" or anchor is None or velocity is None:
            raise ValueError("Redirect needs its actually committed root and velocity")
        anchor, velocity = np.asarray(anchor, float), np.asarray(velocity, float)
        if anchor.shape != (3,) or velocity.shape != (3,) or not np.isfinite([anchor, velocity]).all():
            raise ValueError("Invalid redirect anchor or velocity")
        anchor, velocity = anchor.copy(), velocity.copy()
        anchor[1] = velocity[1] = 0
        # Keep the measured velocity during the first instant, then smoothly
        # turn toward the new target. No teleport to a fresh route origin.
        elapsed = np.maximum(t - 4., 0)
        yaw = -math.pi / 2 * smooth(elapsed / 1.5)
        desired = np.stack((np.sin(yaw) * speed, np.zeros(n), np.cos(yaw) * speed), axis=1)
        alpha = smooth(elapsed / .7)[:, None]
        velocities[change:] = ((1-alpha) * velocity + alpha * desired)[change:]
        positions[change:] = anchor + np.cumsum(velocities[change:] / FPS, axis=0)
        directions = velocities[change:, [0, 2]]
        moving = np.linalg.norm(directions, axis=1) > .001
        for i in range(change, n):
            if moving[i-change]:
                yaw[i] = math.atan2(velocities[i, 0], velocities[i, 2])
            elif i > change:
                yaw[i] = yaw[i-1]
    return Route(positions, yaw, 8 * FPS, change).validate()


def native_targets(route, start, history_length, *, lookahead=120):
    """Future context may exceed emitted horizon; history constraints stay zero."""
    if type(start) is not int or start < 0 or start % WINDOW:
        raise ValueError("Start must be a nonnegative 40-frame boundary")
    if history_length not in (0, HISTORY):
        raise ValueError("Use no history or exactly 16 full-body frames")
    if type(lookahead) is not int or lookahead < WINDOW or lookahead % 4:
        raise ValueError("Lookahead must be a multiple of four, at least 40")
    route.validate()
    n = min(lookahead, len(route.positions)-start)
    if n < WINDOW or n % 4:
        raise ValueError("Need a complete output window and aligned future targets")
    positions = route.positions[start:start+n] @ ARDY_TO_UNITY
    # The official representation computes heading from the hip vector:
    # atan2(rightHip.z-leftHip.z, -(rightHip.x-leftHip.x)). Neutral = 0.
    # Thus ARDY heading is atan2(vx,vz), NOT atan2(vz,vx).
    headings = -route.headings[start:start+n]
    indices = np.arange(history_length, history_length+n, dtype=np.int64)
    return positions, headings, indices, history_length+n


def constraint_tensors(model, route, start, history_length, *, lookahead=120):
    import torch
    from ardy.constraints import Root2DConstraintSet
    positions, headings, indices, count = native_targets(route, start, history_length, lookahead=lookahead)
    device = model.device
    constraints = [Root2DConstraintSet(model.motion_rep.skeleton,
        torch.as_tensor(indices, device=device),
        torch.as_tensor(positions[:, [0, 2]], dtype=torch.float32, device=device),
        global_root_heading=torch.as_tensor(headings, dtype=torch.float32, device=device))]
    observed, mask = model.motion_rep.create_conditions_from_constraints(
        constraints, length=count, to_normalize=False, device=device)
    observed = model.motion_rep.normalize(observed) * mask
    if torch.count_nonzero(mask[:history_length]) or not torch.count_nonzero(mask[history_length:]):
        raise RuntimeError("Path constraints must cover future frames only")
    return observed.unsqueeze(0), mask.unsqueeze(0), count


def standing_history(backend):
    """Explicit synthetic full-body initial pose; later windows use generated FK."""
    from scipy.spatial.transform import Rotation
    from Server.ARDY.motion_service.backend import grounded_source_root
    torch = backend.torch
    root, _ = grounded_source_root(backend.neutral, backend.names)
    local = np.tile(np.eye(3), (HISTORY, 27, 1, 1))
    for name, degrees in (("LeftArm", -75), ("RightArm", 75)):
        local[:, backend.names.index(name)] = Rotation.from_euler("z", degrees, degrees=True).as_matrix()
    with torch.inference_mode():
        history = backend.model.motion_rep(
            torch.as_tensor(local[None], dtype=torch.float32, device=backend.device),
            torch.as_tensor(np.tile(root, (1, HISTORY, 1)), dtype=torch.float32, device=backend.device),
            to_normalize=True, to_canonicalize=False)
    return history, float(root[1])


def export_locomotion(arrays, route, backend, clip_id, provenance):
    rotations = build_clip(arrays, clip_id=clip_id, expected_names=backend.names,
        expected_parents=backend.parents, neutral_joints=backend.neutral, source=provenance)
    positions = np.asarray(arrays["root_positions"]) @ ARDY_TO_UNITY
    from Server.ARDY.motion_service.backend import grounded_source_root
    root, _ = grounded_source_root(backend.neutral, backend.names)
    # Foot channels here are only model predictions, never contact truth.
    contacts = np.asarray(arrays.get("foot_contacts", np.zeros((len(positions), 4))))
    feet = list(backend.model.motion_rep.skeleton.foot_joint_idx)
    left = [i for i, joint in enumerate(feet) if backend.names[joint].startswith("Left")]
    right = [i for i, joint in enumerate(feet) if backend.names[joint].startswith("Right")]
    if contacts.shape != (len(positions), len(feet)) or not left or not right:
        raise ValueError("Unexpected foot contact channel layout")
    vector = lambda p: dict(zip("xyz", map(float, p)))
    return {"schema": 1, "id": clip_id, "rotationClip": rotations,
        "sourceRootHeight": float(root[1]), "sourceOrigin": vector(np.zeros(3)), "sourceHeadingDegrees": 0.,
        "frames": [{"rootPosition": vector(p), "leftFootContact": float(np.max(c[left])),
                    "rightFootContact": float(np.max(c[right]))} for p, c in zip(positions, contacts)],
        "targets": [{"rootPosition": vector(p), "headingDegrees": float(np.degrees(h))}
                    for p, h in zip(route.positions, route.headings)],
        "source": {**provenance, "coordinateSystem": "unity-lh-y-up-z-forward",
                   "rootMeaning": "generated world pelvis, source ground Y=0; not snapped to targets",
                   "footContacts": "predicted contact channels, not observed Unity contact",
                   "sourceSkeleton": "Core27", "naturalnessAccepted": False}}
