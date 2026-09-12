"""CPU-only kinematic screening of generated Core27 locomotion.

Inputs are unbatched decoded ARDY frames in metres, +Y up, +Z forward. A
consistent X reflection of positions, targets and rotations is also supported
(e.g. canonical Unity space). No floor alignment or time alignment is fitted.

Pinned ARDY 693f74d: CoreSkeleton27 names its four foot samples LeftFoot,
LeftToeBase, RightFoot, RightToeBase. motion_rep/feet.py derives training contact
labels from height < .10 m AND speed < .15 m/s; inverse() thresholds predicted
contact channels at .5. Neither provides measured physical contact. This module
therefore selects low foot intervals by HEIGHT ONLY before measuring speed, so
fast sliding feet cannot disappear from the sample through a velocity filter.

The thresholds below are predeclared engineering screens for flat-floor walking,
turning and stopping. They are not biomechanical limits or naturalness ratings.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass
from math import ceil
from typing import Mapping, Sequence

import numpy as np


CORE27_NAMES = (
    "Hips", "Spine", "Spine1", "Spine2", "Spine3", "Neck", "Head",
    "RightShoulder", "RightArm", "RightForeArm", "RightHand", "RightHandEnd",
    "RightHandThumb1", "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand",
    "LeftHandEnd", "LeftHandThumb1", "RightUpLeg", "RightLeg", "RightFoot",
    "RightToeBase", "LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase",
)
FOOT_NAMES = ("LeftFoot", "LeftToeBase", "RightFoot", "RightToeBase")


@dataclass(frozen=True)
class LocomotionThresholds:
    """Explicit v1 screening limits; changing these requires reporting the values."""

    root_rmse_m: float = .20
    root_max_error_m: float = .45
    root_end_error_m: float = .20
    heading_p95_error_deg: float = 30.0
    heading_end_error_deg: float = 25.0
    stop_speed_p95_mps: float = .15
    stop_drift_m: float = .10
    stop_target_speed_max_mps: float = .02
    root_height_span_m: float = .25
    foot_penetration_m: float = .05
    low_foot_height_m: float = .10
    both_feet_airborne_seconds: float = .20
    low_foot_speed_p95_mps: float = .30
    seam_root_step_m: float = .20
    seam_joint_step_m: float = .30
    seam_rotation_step_deg: float = 35.0
    response_dwell_seconds: float = .25

    def __post_init__(self):
        if any(not np.isfinite(value) or value <= 0 for value in asdict(self).values()):
            raise ValueError("All screening thresholds must be finite and positive")


def _finite(value, shape, label):
    array = np.asarray(value, dtype=np.float64)
    if array.shape != shape or not np.isfinite(array).all():
        raise ValueError(f"{label} must have finite shape {shape}")
    return array


def _check(value, limit, *, reason=None, strict=False):
    if value is None:
        return {"status": "unknown", "value": None, "limit": float(limit),
                "comparison": "<" if strict else "<=", "reason": reason or "Not measured"}
    passed = value < limit if strict else value <= limit
    result = {"status": "passed" if passed else "failed", "value": float(value),
              "limit": float(limit), "comparison": "<" if strict else "<="}
    if reason:
        result["reason"] = reason
    return result


def _summary(values):
    values = np.asarray(values, dtype=np.float64)
    if not values.size:
        return {"count": 0, "mean": None, "p95": None, "maximum": None}
    return {"count": int(values.size), "mean": float(values.mean()),
            "p95": float(np.percentile(values, 95)), "maximum": float(values.max())}


def _longest_run(mask):
    longest = count = 0
    for flag in mask:
        count = count + 1 if flag else 0
        longest = max(longest, count)
    return longest


def _first_dwell(mask, count):
    run = 0
    for index, flag in enumerate(mask):
        run = run + 1 if flag else 0
        if run >= count:
            return index - count + 1
    return None


def _rotation_steps(rotations):
    # Frobenius inner product is trace(R_previous^T R_next); no Euler wrap.
    cosine = (np.einsum("...ij,...ij->...", rotations[1:], rotations[:-1]) - 1) / 2
    return np.degrees(np.arccos(np.clip(cosine, -1, 1)))


def measure_locomotion(
    arrays: Mapping, names: Sequence[str], target_root_positions,
    *, fps=20, target_headings=None, window_frames=40,
    window_generation_seconds=None, stop_start_frame=None,
    target_change_frames=None, thresholds: LocomotionThresholds | None = None,
    ground_y=0.0,
):
    """Return JSON-safe measurements and independent passed/failed/unknown checks.

    ``posed_joints`` is [T,27,3], ``global_rot_mats`` is [T,27,3,3], and
    ``target_root_positions`` is [T,3] in the SAME world coordinate frame and at
    the SAME times. Root means Hips. Planar tracking deliberately ignores target Y.
    ``target_headings`` optionally contains [T,2] XZ or [T,3] direction vectors,
    not Euler angles. Actual heading is Hips rotation applied to local +Z.

    ``stop_start_frame`` is an explicitly commanded stop, not an inference from
    an apparently stationary generated tail. Stop screening needs a full final
    second of samples entirely after that frame, with stationary target positions.
    ``window_generation_seconds`` must be wall-clock durations measured with GPU
    synchronization by the caller. Missing durations are unknown; they never pass.
    This is a generator throughput check, not ASR/Qwen/network/playback latency.

    ``target_change_frames`` records command-effective generated frame indices.
    Reacquiring a positional tolerance is reported as a descriptive observation;
    causal response and request-to-motion latency remain unknown without playback
    and command timestamps. No threshold values are tuned to the supplied clip.
    """
    t = thresholds or LocomotionThresholds()
    if not isinstance(t, LocomotionThresholds):
        raise ValueError("thresholds must be LocomotionThresholds")
    if not np.isfinite(fps) or fps <= 0:
        raise ValueError("fps must be finite and positive")
    if type(window_frames) is not int or window_frames < 1:
        raise ValueError("window_frames must be a positive integer")
    if not np.isfinite(ground_y):
        raise ValueError("ground_y must be finite")
    names = list(names)
    if len(names) != 27 or len(set(names)) != 27 or set(names) != set(CORE27_NAMES):
        raise ValueError("names must contain each pinned Core27 joint exactly once")
    p = np.asarray(arrays["posed_joints"], dtype=np.float64)
    if p.ndim != 3 or p.shape[1:] != (27, 3) or len(p) < 2 or not np.isfinite(p).all():
        raise ValueError("posed_joints must be finite [T>=2,27,3]")
    frames = len(p)
    r = _finite(arrays["global_rot_mats"], (frames, 27, 3, 3), "global_rot_mats")
    if np.max(np.abs(r.swapaxes(-1, -2) @ r - np.eye(3))) > 2e-3 or np.max(np.abs(np.linalg.det(r) - 1)) > 2e-3:
        raise ValueError("global_rot_mats must be proper orthonormal rotations")
    targets = _finite(target_root_positions, (frames, 3), "target_root_positions")
    if stop_start_frame is not None and (type(stop_start_frame) is not int or not 0 <= stop_start_frame < frames):
        raise ValueError("stop_start_frame must be an in-range integer frame")
    changes = [] if target_change_frames is None else list(target_change_frames)
    if any(type(frame) is not int or not 0 <= frame < frames for frame in changes) or changes != sorted(set(changes)):
        raise ValueError("target_change_frames must be unique, increasing in-range integers")

    root = p[:, names.index("Hips")]
    planar = root[:, [0, 2]]
    target_planar = targets[:, [0, 2]]
    error = np.linalg.norm(planar - target_planar, axis=-1)
    root_velocity = np.diff(planar, axis=0) * fps
    target_velocity = np.diff(target_planar, axis=0) * fps
    speed = np.linalg.norm(root_velocity, axis=-1)
    root_rmse = float(np.sqrt(np.mean(error ** 2)))
    checks = {
        "rootPlanarRmse": _check(root_rmse, t.root_rmse_m),
        "rootPlanarMaximumError": _check(float(error.max()), t.root_max_error_m),
        "rootPlanarEndpointError": _check(float(error[-1]), t.root_end_error_m),
        "rootHeightSpan": _check(float(np.ptp(root[:, 1])), t.root_height_span_m),
    }
    report = {
        "schema": "ardy-locomotion-kinematic-screen-v1", "fps": float(fps),
        "frames": frames, "sampledSpanSeconds": (frames - 1) / fps,
        "playbackDurationSeconds": frames / fps,
        "scope": "Flat-floor Core27 source-motion screening, before avatar retargeting, collisions or navigation.",
        "coordinates": "Metres; +Y up, local +Z forward; positions, rotations and targets share one coordinate frame. No alignment fitted.",
        "thresholds": asdict(t), "checks": checks,
        "naturalness": {"status": "unknown", "reason": "Requires actual avatar playback and human review; no quality score is produced."},
        "rootTracking": {"rmseMeters": root_rmse, "maximumErrorMeters": float(error.max()),
                         "endpointErrorMeters": float(error[-1]), "errorMetersPerFrame": error.tolist(),
                         "planarSpeedMetersPerSecond": _summary(speed)},
        "rootHeight": {"minimumMeters": float(root[:, 1].min()), "maximumMeters": float(root[:, 1].max()),
                       "spanMeters": float(np.ptp(root[:, 1]))},
    }

    heading_reason = "No target headings supplied"
    heading_p95 = heading_end = None
    heading = {"status": "unknown", "reason": heading_reason}
    if target_headings is not None:
        desired = np.asarray(target_headings, dtype=np.float64)
        if desired.shape == (frames, 3):
            desired = desired[:, [0, 2]]
        if desired.shape != (frames, 2) or not np.isfinite(desired).all():
            raise ValueError("target_headings must be finite [T,2] XZ or [T,3] directions")
        lengths = np.linalg.norm(desired, axis=-1)
        if np.any(lengths < 1e-8):
            raise ValueError("target_headings must have nonzero planar length")
        desired = desired / lengths[:, None]
        actual = r[:, names.index("Hips"), [0, 2], 2]
        lengths = np.linalg.norm(actual, axis=-1)
        valid = lengths > 1e-6
        dots = np.sum(actual[valid] / lengths[valid, None] * desired[valid], axis=-1)
        degrees = np.degrees(np.arccos(np.clip(dots, -1, 1)))
        errors = [None] * frames
        for index, value in zip(np.flatnonzero(valid), degrees):
            errors[int(index)] = float(value)
        heading_reason = None if valid.all() else "Some Hips +Z directions are vertical; planar heading is undefined"
        # Missing projected headings cannot silently pass an all-frame check.
        heading_p95 = float(np.percentile(degrees, 95)) if valid.all() else None
        heading_end = errors[-1]
        heading = {"status": "measured" if valid.all() else "partially_measured",
                   "basis": "Global Hips rotation times local +Z, projected to XZ; unsigned angle to desired direction.",
                   "validFrames": int(valid.sum()), "errorDegreesPerFrame": errors,
                   "validErrorDegrees": _summary(degrees), "endpointErrorDegrees": heading_end}
    report["heading"] = heading
    checks["headingP95Error"] = _check(heading_p95, t.heading_p95_error_deg, reason=heading_reason)
    checks["headingEndpointError"] = _check(heading_end, t.heading_end_error_deg, reason=heading_reason)

    # A 20 Hz final second needs 20 intervals / 21 endpoint samples, not 20 samples.
    tail_intervals = int(ceil(fps))
    tail_start = max(0, frames - tail_intervals - 1)
    tail_speed = speed[tail_start:]
    tail_drift = np.linalg.norm(planar[tail_start:] - planar[tail_start], axis=-1)
    target_tail_speed = np.linalg.norm(target_velocity[tail_start:], axis=-1)
    stop_reason = None
    if stop_start_frame is None:
        stop_reason = "No explicit stop_start_frame; a stationary generated tail does not establish a requested stop"
    elif frames < tail_intervals + 1 or stop_start_frame > tail_start:
        stop_reason = "Less than a full final second observed after the commanded stop"
    elif float(target_tail_speed.max()) > t.stop_target_speed_max_mps:
        stop_reason = "Target trajectory is still moving during the final second"
    stop_p95 = float(np.percentile(tail_speed, 95))
    stop_drift = float(tail_drift.max())
    report["stop"] = {"commandedStartFrame": stop_start_frame,
                      "tailStartFrame": tail_start, "sampledSeconds": (frames - 1 - tail_start) / fps,
                      "speedMetersPerSecond": _summary(tail_speed),
                      "maximumExcursionFromTailStartMeters": stop_drift,
                      "endpointDisplacementMeters": float(tail_drift[-1]),
                      "planarPathLengthMeters": float(tail_speed.sum() / fps),
                      "targetSpeedMaximumMetersPerSecond": float(target_tail_speed.max()),
                      "eligibility": "eligible" if stop_reason is None else "unknown", "reason": stop_reason}
    checks["stopSpeedP95"] = _check(stop_p95 if stop_reason is None else None, t.stop_speed_p95_mps, reason=stop_reason)
    checks["stopDrift"] = _check(stop_drift if stop_reason is None else None, t.stop_drift_m, reason=stop_reason)

    feet = p[:, [names.index(name) for name in FOOT_NAMES]]
    heights = feet[..., 1] - ground_y
    penetration = np.maximum(-heights, 0)
    airborne = np.all(heights > t.low_foot_height_m, axis=-1)
    airborne_seconds = _longest_run(airborne) / fps
    foot_speed = np.linalg.norm(np.diff(feet[..., [0, 2]], axis=0), axis=-1) * fps
    low = (heights[:-1] <= t.low_foot_height_m) & (heights[1:] <= t.low_foot_height_m)
    low_speed = foot_speed[low]
    foot_report = {
        "status": "kinematic_heuristic", "sampleNames": list(FOOT_NAMES), "groundYMeters": float(ground_y),
        "limitation": "Joint samples, not mesh soles or measured contacts. Both-feet-airborne and low-foot-speed checks can flag valid hops or swing transitions; inspect playback. Predicted foot_contacts are not used as truth.",
        "minimumHeightMetersPerSample": heights.min(axis=0).tolist(),
        "maximumHeightMetersPerSample": heights.max(axis=0).tolist(),
        "maximumPenetrationMeters": float(penetration.max()),
        "penetratingSampleFraction": float(np.mean(heights < 0)),
        "bothFeetAboveLowHeightFrameFraction": float(airborne.mean()),
        "longestBothFeetAboveLowHeightSeconds": airborne_seconds,
        "lowSelection": "Both endpoints of each foot-sample interval at or below low_foot_height_m; no velocity/contact selection.",
        "lowIntervalSampleFraction": float(low.mean()),
        "lowHorizontalSpeedMetersPerSecond": _summary(low_speed),
        "perSampleLowHorizontalSpeedMetersPerSecond": {
            name: _summary(foot_speed[:, index][low[:, index]]) for index, name in enumerate(FOOT_NAMES)},
    }
    report["feet"] = foot_report
    checks["footJointPenetration"] = _check(float(penetration.max()), t.foot_penetration_m)
    checks["bothFeetAirborneDuration"] = _check(airborne_seconds, t.both_feet_airborne_seconds)
    checks["lowFootHorizontalSpeedP95"] = _check(
        float(np.percentile(low_speed, 95)) if low_speed.size else None, t.low_foot_speed_p95_mps,
        reason="Height-only kinematic heuristic; not a physical-contact or naturalness test" if low_speed.size else "No low foot intervals; sliding is unobserved")

    joint_steps = np.linalg.norm(np.diff(p, axis=0), axis=-1)
    rotation_steps = _rotation_steps(r)
    seams = []
    for frame in range(window_frames, frames, window_frames):
        index = frame - 1
        entry = {"firstNewFrame": frame, "timeSeconds": frame / fps,
                 "previousFrame": frame - 1,
                 "rootPlanarStepMeters": float(speed[index] / fps),
                 "maximumJointStepMeters": float(joint_steps[index].max()),
                 "largestPositionStepJoint": names[int(joint_steps[index].argmax())],
                 "maximumGlobalRotationStepDegrees": float(rotation_steps[index].max()),
                 "largestRotationStepJoint": names[int(rotation_steps[index].argmax())]}
        entry["checks"] = {
            "rootStep": _check(entry["rootPlanarStepMeters"], t.seam_root_step_m),
            "jointStep": _check(entry["maximumJointStepMeters"], t.seam_joint_step_m),
            "rotationStep": _check(entry["maximumGlobalRotationStepDegrees"], t.seam_rotation_step_deg)}
        seams.append(entry)
    report["windowBoundaries"] = {
        "windowFrames": window_frames, "status": "measured" if seams else "unknown",
        "reason": "No generated inter-window seam in this clip" if not seams else None,
        "scope": "Last frame of preceding generated window to first frame of next window; initial input-history join is not supplied or measured.",
        "seams": seams,
        "allFrameMaximumJointStepMeters": float(joint_steps.max()),
        "allFrameMaximumGlobalRotationStepDegrees": float(rotation_steps.max())}
    for key, field, limit in (("windowRootStep", "rootPlanarStepMeters", t.seam_root_step_m),
                              ("windowJointStep", "maximumJointStepMeters", t.seam_joint_step_m),
                              ("windowRotationStep", "maximumGlobalRotationStepDegrees", t.seam_rotation_step_deg)):
        checks[key] = _check(max(entry[field] for entry in seams) if seams else None, limit,
                             reason="No generated inter-window seam" if not seams else None)

    count = int(ceil(frames / window_frames))
    times = [] if window_generation_seconds is None else list(window_generation_seconds)
    if len(times) > count or any(value is not None and (not np.isfinite(value) or value < 0) for value in times):
        raise ValueError("window_generation_seconds must contain at most one nonnegative finite duration (or None) per generated window")
    times += [None] * (count - len(times))
    windows = []
    for index, duration in enumerate(times):
        supplied_frames = min(window_frames, frames - index * window_frames)
        budget = supplied_frames / fps
        windows.append({"windowIndex": index, "frames": supplied_frames, "playbackSeconds": budget,
                        "generationSeconds": None if duration is None else float(duration),
                        "throughput": _check(duration, budget, strict=True,
                                             reason="No synchronized inference duration supplied" if duration is None else None)})
    statuses = [entry["throughput"]["status"] for entry in windows]
    report["inference"] = {
        "status": "failed" if "failed" in statuses else "unknown" if "unknown" in statuses else "passed",
        "scope": "Synchronized generator wall time only; excludes Qwen, queueing, network and Unity. Caller owns timing provenance. Each generated window must be faster than its playback duration.",
        "windows": windows,
        "totalMeasuredGenerationSeconds": float(sum(value for value in times if value is not None)),
        "unmeasuredWindows": sum(value is None for value in times)}

    # Positional samples are endpoints: N intervals need N+1 samples to establish
    # a dwell duration, matching the final-second stop convention above.
    dwell_frames = int(ceil(t.response_dwell_seconds * fps)) + 1
    change_report = []
    for index, frame in enumerate(changes):
        next_change = changes[index + 1] if index + 1 < len(changes) else frames
        observed_error = error[frame:next_change]
        start = _first_dwell(observed_error <= t.root_end_error_m, dwell_frames)
        change_report.append({
            "targetChangeFrame": frame, "targetChangeTimeSeconds": frame / fps,
            "observationEndFrameExclusive": next_change,
            "positionToleranceMeters": t.root_end_error_m, "requiredDwellFrames": dwell_frames,
            "alreadyInsideToleranceAtChange": bool(error[frame] <= t.root_end_error_m),
            "firstSustainedWithinToleranceFrame": None if start is None else frame + start,
            "positionToleranceReacquisitionSeconds": None if start is None else start / fps,
            "observedErrorMeters": _summary(observed_error),
            "responseAssessment": {"status": "unknown", "reason": "Trajectory tolerance reacquisition is descriptive, not proof of a causal response or request-to-playback latency. Target timing, generation scheduling and actual playback must be measured separately."}})
    report["targetChanges"] = change_report
    return report
