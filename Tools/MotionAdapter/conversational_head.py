"""Explicitly controlled conversational head curves, not native ARDY output.

The source ARDY clip supplies only its skeleton schema and audit provenance.
Its motion rotations are replaced by a short C2-continuous minimum-jerk curve.
Stored Neck/Head rotations are global offsets: Neck receives 20% of the
angle, Head the total, so the resulting Head-local contribution is 80%.
"""
from __future__ import annotations

import copy

import numpy as np


NECK_SHARE = 0.2
PROFILES = {
    "nod": {
        "axis": 0, "axis_name": "canonical Unity +X: pitch down",
        "duration": 1.8, "amplitude": 8.0,
        "knots": ((0.0, 0.0), (0.7, 8.0), (1.8, 0.0)),
        "intent": "Controlled conversational preview: one gentle 8-degree nod, then return to the current neutral pose.",
    },
    "shake-head": {
        "axis": 1, "axis_name": "canonical Unity +Y: yaw",
        "duration": 2.0, "amplitude": 6.0,
        "knots": ((0.0, 0.0), (0.5, 6.0), (1.25, -6.0), (2.0, 0.0)),
        "intent": "Controlled conversational preview: one gentle left-right head shake, 6 degrees each way, then return to the current neutral pose.",
    },
}


def evaluate_profile(times, clip_id):
    """Return angle and analytic velocity/acceleration/jerk in degrees and seconds.

Outside the motion duration the pose is identity. Every segment begins and
ends with zero angular velocity and acceleration; third derivatives can
change at a knot without an acceleration discontinuity.
"""
    if clip_id not in PROFILES:
        raise ValueError("Only nod and shake-head have controlled conversational profiles")
    times = np.asarray(times, dtype=np.float64)
    if not np.isfinite(times).all():
        raise ValueError("Profile sample times must be finite")
    result = [np.zeros_like(times) for _ in range(4)]
    knots = PROFILES[clip_id]["knots"]
    for (start, first), (end, last) in zip(knots, knots[1:]):
        active = (times >= start) & (times <= end)
        phase = (times[active] - start) / (end - start)
        duration, delta = end - start, last - first
        position = phase**3 * (10.0 + phase * (-15.0 + 6.0 * phase))
        velocity = 30.0 * phase**2 * (1.0 - phase)**2
        acceleration = 60.0 * phase * (1.0 - phase) * (1.0 - 2.0 * phase)
        jerk = 60.0 - 360.0 * phase + 360.0 * phase**2
        result[0][active] = first + delta * position
        result[1][active] = delta * velocity / duration
        result[2][active] = delta * acceleration / duration**2
        result[3][active] = delta * jerk / duration**3
    return tuple(result)


def axis_quaternion(angle_degrees, axis):
    half = np.deg2rad(float(angle_degrees)) / 2.0
    components = [0.0, 0.0, 0.0, float(np.cos(half))]
    components[axis] = float(np.sin(half))
    return dict(zip("xyzw", components))


def profile_statistics(clip_id, fps=20.0):
    profile = PROFILES[clip_id]
    count = round(profile["duration"] * fps) + 1
    times = np.arange(count) / fps
    angle, velocity, acceleration, _ = evaluate_profile(times, clip_id)
    dense_times = np.linspace(0, profile["duration"], 4001)
    _, dense_velocity, dense_acceleration, dense_jerk = evaluate_profile(dense_times, clip_id)
    return {
        "frameCount": count, "fps": float(fps), "sampleSpanSeconds": float(times[-1]),
        "playbackDurationSeconds": count / fps,  # Unity's clip duration includes the last frame.
        "totalAngleMinimumDegrees": float(angle.min()), "totalAngleMaximumDegrees": float(angle.max()),
        "neckGlobalPeakDegrees": float(np.max(np.abs(angle)) * NECK_SHARE),
        "headLocalPeakDegrees": float(np.max(np.abs(angle)) * (1.0 - NECK_SHARE)),
        "maximumFrameStepDegrees": float(np.max(np.abs(np.diff(angle)))),
        "maximumAngularSpeedDegreesPerSecond": float(np.max(np.abs(dense_velocity))),
        "maximumAngularAccelerationDegreesPerSecond2": float(np.max(np.abs(dense_acceleration))),
        "maximumAngularJerkDegreesPerSecond3": float(np.max(np.abs(dense_jerk))),
        "startEndAnglesDegrees": [float(angle[0]), float(angle[-1])],
        "startEndVelocityDegreesPerSecond": [float(velocity[0]), float(velocity[-1])],
        "startEndAccelerationDegreesPerSecond2": [float(acceleration[0]), float(acceleration[-1])],
        "statisticsMethod": "Analytic piecewise quintic derivatives; maxima sampled at 4001 times, not empirical model-motion measurements",
    }


def apply_profile(clip, clip_id):
    """Return a new additive-local preview; never mutate the raw source clip."""
    if clip_id not in PROFILES:
        raise ValueError("Only nod and shake-head support apply_profile")
    if clip.get("schema") != 1 or float(clip.get("fps", 0)) != 20.0:
        raise ValueError("Expected a schema-1 source clip at 20 FPS")
    names, parents = clip["jointNames"], clip["jointParents"]
    if len(set(names)) != len(names) or len(parents) != len(names):
        raise ValueError("Invalid source joint layout")
    if "Neck" not in names or "Head" not in names:
        raise ValueError("Controlled head profiles require Neck and Head joints")
    neck, head = names.index("Neck"), names.index("Head")
    if parents[head] != neck:
        raise ValueError("Head must be a direct child of Neck")
    raw_source = clip.get("source", {})
    if not raw_source.get("sha256") or not isinstance(clip.get("text"), str):
        raise ValueError("Raw motion SHA256 and original text provenance are required")
    if raw_source.get("rotationApplication") == "additive-local":
        raise ValueError("A controlled profile cannot be applied twice")

    profile = PROFILES[clip_id]
    times = np.arange(round(profile["duration"] * 20) + 1) / 20.0
    angles, _, _, _ = evaluate_profile(times, clip_id)
    result = copy.deepcopy(clip)
    identity = {"x": 0.0, "y": 0.0, "z": 0.0, "w": 1.0}
    result["restGlobalRotations"] = [dict(identity) for _ in names]
    result["frames"] = []
    for angle in angles:
        rotations = [dict(identity) for _ in names]
        rotations[neck] = axis_quaternion(angle * NECK_SHARE, profile["axis"])
        rotations[head] = axis_quaternion(angle, profile["axis"])
        result["frames"].append({"globalRotations": rotations})
    result["id"] = clip_id
    result["text"] = profile["intent"]
    result["displayIntent"] = profile["intent"]
    result["source"].update({
        "rawText": clip["text"], "rawFrameCount": len(clip["frames"]),
        "frameCount": len(result["frames"]), "rotationApplication": "additive-local",
        "processing": {
            "kind": "controlled-conversational-envelope", "version": 1,
            "notNativeArdyMotion": True, "rawMotionRotationsUsed": False,
            "rawSourceStatus": "Raw ARDY head candidate rejected as unnatural; retained only for audit provenance",
            "curve": "Piecewise quintic minimum jerk, C2 continuous; zero velocity and acceleration at every knot",
            "durationSeconds": profile["duration"], "amplitudeDegrees": profile["amplitude"],
            "axis": profile["axis_name"], "neckShare": NECK_SHARE, "headLocalShare": 1.0 - NECK_SHARE,
            "globalOffsetConvention": "Neck = 20% total, Head = total; all other joints and rest offsets are identity",
            "application": "Add the local rotational offsets to the current animated head/neck pose; do not replace the body pose",
            "knots": [{"timeSeconds": time, "totalAngleDegrees": angle} for time, angle in profile["knots"]],
        },
        "profileStatistics": profile_statistics(clip_id),
    })
    return result
