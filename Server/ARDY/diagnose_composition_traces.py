"""Read archived motion-only traces, measure geometry and retain exact response clips.

No model, service, Unity editor, network, or GPU inference is used. Target-avatar
numbers are fully blended production UpperBody targets on the asset hierarchy;
they are not a new recording of what the user saw.
"""
from __future__ import annotations

import hashlib
import json
from datetime import datetime
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation

from Tools.MotionAdapter.export_unity import ARDY_TO_UNITY, fk_positions, load_core27_reference
from Tools.MotionAdapter.review_unity_right_candidates import VrmRestHierarchy


ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "Server/ARDY/runtime/composition-diagnosis-v1"
BODY_MAP = {"spine": "Spine", "chest": "Spine2", "upperChest": "Spine3", "neck": "Neck", "head": "Head",
            "leftShoulder": "LeftShoulder", "leftUpperArm": "LeftArm", "leftLowerArm": "LeftForeArm", "leftHand": "LeftHand",
            "rightShoulder": "RightShoulder", "rightUpperArm": "RightArm", "rightLowerArm": "RightForeArm", "rightHand": "RightHand"}


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def dump(path, value):
    Path(path).write_text(json.dumps(value, indent=2, allow_nan=False), encoding="utf-8")


def matrices(frames):
    q = np.array([[[q[k] for k in "xyzw"] for q in f["globalRotations"]] for f in frames])
    assert q.shape[1:] == (27, 4) and np.isfinite(q).all()
    return Rotation.from_quat(q.reshape(-1, 4)).as_matrix().reshape(-1, 27, 3, 3)


def angle(a, b):
    delta = np.asarray(a) @ np.asarray(b).swapaxes(-1, -2)
    return np.degrees(Rotation.from_matrix(delta.reshape(-1, 3, 3)).magnitude()).reshape(delta.shape[:-2])


def vector_range(value):
    return {"minimum": value.min(axis=0).tolist(), "maximum": value.max(axis=0).tolist(),
            "median": np.median(value, axis=0).tolist(), "peakToPeak": np.ptp(value, axis=0).tolist()}


def measure(positions, rotations, chest_frame, joints):
    shoulder, elbow, wrist = (positions[:, joints[key]] for key in ("shoulders", "elbows", "wrists"))
    chest = positions[:, joints["chest"]]
    inv = chest_frame.swapaxes(-1, -2)
    local = (inv[:, None] @ (wrist - shoulder)[..., None])[..., 0]
    wrist_in_chest = (inv[:, None] @ (wrist - chest[:, None])[..., None])[..., 0]
    upper = shoulder - elbow
    lower = wrist - elbow
    cosine = (upper*lower).sum(-1)/(np.linalg.norm(upper, axis=-1)*np.linalg.norm(lower, axis=-1))
    elbow_angle = np.degrees(np.arccos(cosine.clip(-1, 1)))
    forearm_chest = inv[:, None] @ rotations[:, joints["elbows"]]
    hand_forearm = rotations[:, joints["elbows"]].swapaxes(-1, -2) @ rotations[:, joints["wrists"]]
    return {"wristRelativeShoulderChestFrameXYZMeters": local,
            "wristRelativeShoulderWorldYMeters": wrist[..., 1]-shoulder[..., 1],
            "wristInChestXYZMeters": wrist_in_chest,
            "elbowInteriorAngleDegrees": elbow_angle,
            "handRelativeForearmStepDegrees": angle(hand_forearm[1:], hand_forearm[:-1]),
            "forearmRelativeChestStepDegrees": angle(forearm_chest[1:], forearm_chest[:-1]),
            "forearmRelativeChestRotationMatrices": forearm_chest,
            "handRelativeForearmRotationMatrices": hand_forearm}


def source_measure(rotations, names, parents, neutral):
    positions = fk_positions(rotations, neutral, parents)
    joints = {"shoulders": [names.index(s+"Arm") for s in ("Left", "Right")],
              "elbows": [names.index(s+"ForeArm") for s in ("Left", "Right")],
              "wrists": [names.index(s+"Hand") for s in ("Left", "Right")], "chest": names.index("Spine3")}
    return measure(positions, rotations, rotations[:, joints["chest"]], joints)


def target_measure(rotations, names, target):
    pp, rr = [], []
    for frame in rotations:
        desired = {target.human[h]: frame[names.index(source)] @ target.rest_rotations[target.human[h]]
                   for h, source in BODY_MAP.items() if h in target.human}
        p, r = target.fk(desired)
        pp.append(p); rr.append(r)
    pp, rr = np.array(pp), np.array(rr)
    joints = {"shoulders": [target.human[s+"UpperArm"] for s in ("left", "right")],
              "elbows": [target.human[s+"LowerArm"] for s in ("left", "right")],
              "wrists": [target.human[s+"Hand"] for s in ("left", "right")], "chest": target.chest}
    frame = rr[:, target.chest] @ target.rest_rotations[target.chest].T
    return measure(pp, rr, frame, joints)


def summarize(measurement):
    position = measurement["wristRelativeShoulderChestFrameXYZMeters"]
    samples = []
    for frame in (0, 10, 30, 60, 100, len(position)-1):
        if frame < len(position):
            samples.append({"frame": frame, "seconds": frame/20,
                            "leftRightWristRelativeShoulderXYZMeters": position[frame].tolist(),
                            "leftRightElbowInteriorAngleDegrees": measurement["elbowInteriorAngleDegrees"][frame].tolist()})
    windows = []
    for start in range(0, len(position), 40):
        end = min(start+40, len(position))
        chest = measurement["wristInChestXYZMeters"][start:end]
        step_range = slice(start, max(start, end-1))
        windows.append({"startFrame": start, "endFrameInclusive": end-1,
                        "leftRightWristRelativeShoulderXYZMeters": vector_range(position[start:end]),
                        "leftRightWorldHeightFromShoulderMeters": vector_range(measurement["wristRelativeShoulderWorldYMeters"][start:end]),
                        "leftRightElbowInteriorAngleDegrees": vector_range(measurement["elbowInteriorAngleDegrees"][start:end]),
                        "leftRightWristPathInChestMeters": np.linalg.norm(np.diff(chest, axis=0), axis=-1).sum(0).tolist(),
                        "leftRightWristNetDisplacementInChestMeters": np.linalg.norm(chest[-1]-chest[0], axis=-1).tolist(),
                        "leftRightHandRelativeForearmRotationTravelDegrees": measurement["handRelativeForearmStepDegrees"][step_range].sum(0).tolist(),
                        "leftRightForearmRelativeChestRotationTravelDegrees": measurement["forearmRelativeChestStepDegrees"][step_range].sum(0).tolist()})
    return {"samples": samples, "windows": windows, "wholeSequence": {"leftRightWristRelativeShoulderXYZMeters": vector_range(position),
            "leftRightElbowInteriorAngleDegrees": vector_range(measurement["elbowInteriorAngleDegrees"])} }


def main():
    manifest = read(OUT / "archive-manifest.json")
    for item in manifest["traces"]:
        assert sha(item["archive"]) == item["sha256"], "Archived evidence changed"
    lock = read(ROOT / "Tools/MotionAdapter/upstream-lock.json")
    names, parents, neutral, neutral_sha = load_core27_reference(ROOT / ("Server/ARDY/vendor/ardy-"+lock["ardy"]))
    neutral = (ARDY_TO_UNITY @ neutral.T).T
    target = VrmRestHierarchy("Assets/Model/NEVA.vrm")
    reference_path = ROOT / "Server/ARDY/runtime/generate-diagnostics/animator-idle-history.json"
    reference = read(reference_path)
    idle = matrices(reference.get("initialHistory", reference)["frames"])
    indices = [names.index(name) for name in BODY_MAP.values()]
    entries = sorted(((item, read(item["archive"])) for item in manifest["traces"]), key=lambda pair: pair[1]["startedUtc"])
    result = {"schema": 1, "archiveManifest": str(OUT/"archive-manifest.json"), "archiveManifestSha256": sha(OUT/"archive-manifest.json"),
              "sourceScripts": {name: sha(ROOT/name) for name in ("Server/ARDY/diagnose_composition_traces.py", "Tools/MotionAdapter/export_unity.py", "Tools/MotionAdapter/review_unity_right_candidates.py", "Assets/AIChatTookit/Scripts/Motion/ArdyMotionPlayer.cs")},
              "coordinateBasis": "Unity character canonical +Y up, +Z forward, +X character-right; left/right array order. Wrist displacement is measured from its own UpperArm shoulder pivot in chest-motion axes, not arbitrary target bone local axes.",
              "coreSkeletonSha256": neutral_sha, "targetAsset": target.metadata,
              "targetMethod": "Fully blended UpperBody desiredGlobal=sourceGlobalDelta*targetRestGlobal on the complete actual NEVA rest hierarchy. Source clip rest is identity. Shoulder-relative geometry is unaffected by a common root position/orientation. This is CPU reconstruction of the production mapping, not observed rendered frames.",
              "limitations": ["Trace does not record actual rendered poses, blend transitions, Animator Hips or node translations/scales; target calculations use the named asset hierarchy, not unrecorded scene overrides.", "Finger/palm mesh, spring bones, audio and subjective waving/naturalness are not evaluated.", "Returned future frames are retained even for a cancelled request; they are not proof those frames played.", "A completed request normally fades back to Animator according to current Player code. Initial-history observations establish whether raised pose was retained, not the exact unrecorded frame of reset."],
              "idleReference": {"path": str(reference_path), "sha256": sha(reference_path), "source": "Previously captured actual Animator idle; phase differs from current traces, so proximity is descriptive."},
              "traces": [], "modelsLoaded": False, "gpuUsed": False, "servicesStarted": False}
    prev = None
    render = []
    for item, trace in entries:
        requests = [(e, json.loads(e["json"])) for e in trace["entries"] if e["kind"] == "request"]
        responses = [(e, json.loads(e["json"])) for e in trace["entries"] if e["kind"] == "response"]
        assert len(requests) == len(responses) == 3
        assert [r[1]["chunkIndex"] for r in responses] == [0, 1, 2]
        assert all(e["httpStatus"] == 200 for e, _ in responses)
        first = requests[0][1]; history = matrices(first["initialHistory"]["frames"])
        chunks = [r["clip"] for _, r in responses]
        assert all(c["jointNames"] == names and c["jointParents"] == parents.tolist() and c["text"] == trace["description"] for c in chunks)
        assert all(np.allclose([[q[k] for k in "xyzw"] for q in c["restGlobalRotations"]], [0, 0, 0, 1], atol=1e-7) for c in chunks)
        frames = sum((c["frames"] for c in chunks), []); motion = matrices(frames)
        assert len(motion) == 120
        stem = "revision-"+str(trace["revision"])
        clip = {**chunks[0], "id": stem, "frames": frames, "source": {**chunks[0]["source"], "frameCount": 120,
                "traceArchiveSha256": item["sha256"], "diagnosticAssembly": "Exact three response frame lists concatenated; no rotations modified."}}
        clip_path = OUT / (stem+".json"); dump(clip_path, clip)
        render.append({"id": stem, "path": str(clip_path)})
        frame_data = {}; metrics = {}
        for label, function in (("core27", lambda r: source_measure(r, names, parents, neutral)), ("NEVA_fullblend_UpperBody", lambda r: target_measure(r, names, target))):
            hm, gm = function(history), function(motion)
            frame_data[label] = {"initialHistory": {k: v.tolist() for k, v in hm.items()}, "returnedMotion": {k: v.tolist() for k, v in gm.items()}}
            metrics[label] = {"initialHistory": summarize(hm), "returnedMotion": summarize(gm)}
        measurement_path = OUT/(stem+".measurements.json"); dump(measurement_path, frame_data)
        nearest_idle = angle(idle[:, indices], history[-1, indices]).mean(1)
        started = datetime.fromisoformat(trace["startedUtc"].replace("Z", "+00:00"))
        entry = {"revision": trace["revision"], "startedUtc": trace["startedUtc"], "description": trace["description"], "seed": trace["seed"],
                 "finishReason": trace["finishReason"], "finishedElapsedSeconds": trace["entries"][-1]["elapsedSeconds"],
                 "firstResponseSeconds": responses[0][0]["elapsedSeconds"], "allResponsesSeconds": responses[-1][0]["elapsedSeconds"],
                 "requestMask": first["mask"], "initialHistoryFrames": len(history), "responseHistoryFrames": [r["historyFrames"] for _, r in responses],
                 "responseProvenance": [r["provenance"] for _, r in responses], "traceArchive": item["archive"], "traceSha256": item["sha256"],
                 "combinedExactClip": str(clip_path), "frameMeasurements": str(measurement_path), "metrics": metrics,
                 "historyFinalFrameClosestRecordedIdleMean13BoneAngleDegrees": float(nearest_idle.min())}
        if prev:
            previous_start, previous_trace, previous_motion = prev
            elapsed = (started-previous_start).total_seconds()
            entry["previousArchivedTraceComparison"] = {"revision": previous_trace["revision"], "startGapSeconds": elapsed,
                "previousFinishToThisStartSeconds": elapsed-previous_trace["entries"][-1]["elapsedSeconds"],
                "inputEndpointVsPreviousReturnedEndpointMean13BoneAngleDegrees": float(angle(history[-1, indices], previous_motion[-1, indices]).mean()),
                "inputEndpointVsPreviousReturnedEndpointMax13BoneAngleDegrees": float(angle(history[-1, indices], previous_motion[-1, indices]).max())}
        result["traces"].append(entry)
        prev = started, trace, motion
    dump(OUT/"render-manifest.json", {"clips": render})
    dump(OUT/"report.json", result)
    print(json.dumps({"report": str(OUT/"report.json"), "traces": len(result["traces"]), "gpuUsed": False}, indent=2))


if __name__ == "__main__":
    main()
