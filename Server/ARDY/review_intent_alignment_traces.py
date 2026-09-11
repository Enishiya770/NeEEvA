"""CPU-only audit of eight already archived motion traces; never generates motion."""
from __future__ import annotations

import argparse
import hashlib
import itertools
import json
from collections import defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path

import numpy as np

from Server.ARDY.diagnose_composition_traces import BODY_MAP, angle, matrices
from Tools.MotionAdapter.export_unity import ARDY_TO_UNITY, fk_positions, load_core27_reference

ROOT = Path(__file__).resolve().parents[2]


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(data):
    return hashlib.sha256(data).hexdigest()


def canonical(value):
    return digest(json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False).encode())


def record(path):
    return {"path": path.relative_to(ROOT).as_posix(), "sha256": digest(path.read_bytes()), "bytes": path.stat().st_size}


def bounds(value):
    value = np.asarray(value)
    return {"minimum": value.min(axis=0).tolist(), "maximum": value.max(axis=0).tolist(), "mean": value.mean(axis=0).tolist()}


def compare_rotations(a, b, indices):
    distances = angle(a[:, indices], b[:, indices])
    return {"meanDegrees": float(distances.mean()), "maxDegrees": float(distances.max()),
            "lastFrameMeanDegrees": float(distances[-1].mean()), "lastFrameMaxDegrees": float(distances[-1].max())}


def bunny_observations(rotations, names, parents, neutral, fps):
    positions = fk_positions(rotations, neutral, parents)
    head = positions[:, names.index("Head")]
    wrists = positions[:, [names.index("LeftHand"), names.index("RightHand")]]
    shoulders = positions[:, [names.index("LeftArm"), names.index("RightArm")]]
    relative = wrists - head[:, None]
    height = relative[..., 1]
    distances = np.linalg.norm(relative, axis=-1)
    highest = int(np.argmax(height.min(axis=1)))
    nearest = int(np.argmin(distances.max(axis=1)))
    samples = [{"frame": i, "seconds": i / fps, "leftRightWristMinusHeadXYZMeters": relative[i].tolist(),
                "leftRightWristMinusShoulderYMeters": (wrists[i, :, 1] - shoulders[i, :, 1]).tolist(),
                "leftRightDistanceToHeadJointMeters": distances[i].tolist()}
               for i in range(len(rotations))]
    return {
        "method": "CPU FK of exact returned Core27 global rotations and pinned source bone lengths. Unity canonical +Y up,+Z forward,+X character-right. Head is the Core27 Head joint, not scalp surface. No target VRM, blending or user video is reconstructed.",
        "leftRightWristMinusHeadXYZMeters": bounds(relative),
        "leftRightWristMinusShoulderYMeters": bounds(wrists[..., 1] - shoulders[..., 1]),
        "highestSimultaneousWristFrame": samples[highest],
        "nearestSimultaneousWristFrame": samples[nearest],
        "bestSimultaneousLowerWristHeightRelativeToHeadMeters": float(height.min(axis=1).max()),
        "minimumAcrossTimeOfFartherWristHeadDistanceMeters": float(distances.max(axis=1).min()),
        "bothWristsAtOrAboveHeadJointFrameCount": int(np.all(height >= 0, axis=1).sum()),
        "interpretation": "All sampled paired wrists remain well below the head joint; these returned trajectories do not put both wrists at the sides of the head. Individual finger shapes are not represented by this packet. This is a source-trajectory finding, not a claim about recorded actual VRM frames.",
        "frames": samples,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, default=ROOT / "Server/ARDY/runtime/intent-alignment-review-20260910")
    parser.add_argument("--output", type=Path, default=ROOT / "Tools/MotionAdapter/reports/intent-alignment-review-20260910")
    args = parser.parse_args()
    archive, output = args.archive.resolve(), args.output.resolve()
    manifest_path = archive / "archive-manifest.json"
    manifest = read(manifest_path)
    assert len(manifest["files"]) == 8
    lock = read(ROOT / "Tools/MotionAdapter/upstream-lock.json")
    names, parents, neutral, skeleton_sha = load_core27_reference(ROOT / ("Server/ARDY/vendor/ardy-" + lock["ardy"]))
    neutral = neutral @ ARDY_TO_UNITY.T
    indices = [names.index(name) for name in BODY_MAP.values()]
    results = {"schema": 1, "scope": "Eight exact motion-only traces started2026-09-10 20:17-20:22JST. No private chat transcript read.",
        "archiveManifest": record(manifest_path), "modelsLoaded": False, "gpuInference": False,
        "unityStarted": False, "servicesStarted": False, "requestsSent": 0, "regeneratedMotion": False,
        "sourceScripts": {path: digest((ROOT / path).read_bytes()) for path in (
            "Server/ARDY/review_intent_alignment_traces.py", "Server/ARDY/diagnose_composition_traces.py",
            "Tools/MotionAdapter/export_unity.py", "Assets/AIChatTookit/Scripts/Motion/ArdyMotionPlayer.cs")},
        "coreSkeletonSha256": skeleton_sha, "jointNames": names,
        "fingerCapability": {"frameFields": ["globalRotations"], "jointCount": len(names),
            "handRelatedJoints": [name for name in names if "Hand" in name],
            "independentIndexFinger": False, "independentMiddleFinger": False,
            "currentPlayerDrivenUpperBodyJoints": list(BODY_MAP.values()),
            "finding": "Packets contain Hand,HandEnd,HandThumb1 but no independent Index/Middle finger chain. The current UpperBody player maps13 torso/head/arm/hand bones and applies no generated finger joints. Thus index-finger wagging and an index+middle V shape are not controllable through this returned/action route."},
        "traces": [], "repeatedDescriptions": [],
        "limitations": ["These are requests, generated response frames and client completion events, not a recording of actual VRM rendered motion.",
            "CPU FK uses pinned source Core27 proportions and no root translation. No current scene avatar identity/proportions/Animator pose are assumed.",
            "No overall semantic success or naturalness rate is computed from eight selected user trials.",
            "Identical text and seed do not make identical input when actual initialHistory differs. Different history is a measured input difference, not proof it is the sole cause of output variation; exact feature vectors/kernel nondeterminism are not logged."]}
    arrays, descriptions = {}, defaultdict(list)
    for entry in manifest["files"]:
        path = ROOT / entry["archive"]
        assert digest(path.read_bytes()) == entry["sha256"]
        trace = read(path)
        requests = [(e, json.loads(e["json"])) for e in trace["entries"] if e["kind"] == "request"]
        responses = [(e, json.loads(e["json"])) for e in trace["entries"] if e["kind"] == "response"]
        assert len(requests) == len(responses) == 3
        first = requests[0][1]
        assert all(req["seed"] == trace["seed"] == 0 for _, req in requests)
        frames, windows = [], []
        for index, ((req_entry, req), (resp_entry, resp)) in enumerate(zip(requests, responses)):
            assert resp_entry["httpStatus"] == 200 and req["chunkIndex"] == resp["chunkIndex"] == index
            assert all(req[field] == resp[field] for field in ("characterId", "turnId", "revision", "requestId"))
            clip = resp["clip"]
            assert clip["jointNames"] == names and clip["jointParents"] == parents.tolist()
            rest = np.asarray([[q[key] for key in "xyzw"] for q in clip["restGlobalRotations"]])
            assert np.allclose(rest[:, :3], 0, atol=1e-7) and np.allclose(np.abs(rest[:, 3]), 1, atol=1e-7)
            assert clip["source"]["coordinateSystem"] == "unity-lh-y-up-z-forward"
            assert clip["text"] == req["description"] == trace["description"]
            assert len(clip["frames"]) == resp["newFrames"] == 40 and clip["fps"] == resp["fps"] == 20
            assert resp["startFrame"] == 40 * index and resp["final"] == (index == 2)
            assert set(clip["frames"][0]) == {"globalRotations"}
            frames.extend(clip["frames"])
            windows.append({"chunkIndex": index, "httpStatus": resp_entry["httpStatus"], "requestElapsedSeconds": req_entry["elapsedSeconds"],
                "responseElapsedSeconds": resp_entry["elapsedSeconds"], "requestToResponseSeconds": resp_entry["elapsedSeconds"] - req_entry["elapsedSeconds"],
                "startFrame": resp["startFrame"], "newFrames": resp["newFrames"], "historyFrames": resp["historyFrames"], "final": resp["final"],
                "timings": resp["timings"], "provenance": resp["provenance"], "clipSource": clip["source"],
                "canonicalFramesSha256": canonical(clip["frames"])})
        history = first["initialHistory"]
        motion, initial = matrices(frames), matrices(history["frames"])
        arrays[trace["turnId"]] = {"motion": motion, "history": initial, "frames": frames, "firstRequest": first}
        started = datetime.fromisoformat(trace["startedUtc"].replace("Z", "+00:00"))
        item = {"trace": record(path), "startedUtc": trace["startedUtc"], "startedJst": started.astimezone(timezone(timedelta(hours=9))).isoformat(),
            "turnId": trace["turnId"], "revision": trace["revision"], "description": trace["description"], "seed": trace["seed"],
            "derivedSeed": windows[0]["provenance"]["derivedSeed"], "finishReason": trace["finishReason"],
            "finishedElapsedSeconds": trace["entries"][-1]["elapsedSeconds"],
            "firstResponseElapsedSeconds": responses[0][0]["elapsedSeconds"], "allResponsesElapsedSeconds": responses[-1][0]["elapsedSeconds"],
            "firstRequestToFirstResponseSeconds": responses[0][0]["elapsedSeconds"] - requests[0][0]["elapsedSeconds"],
            "firstRequestToAllResponsesSeconds": responses[-1][0]["elapsedSeconds"] - requests[0][0]["elapsedSeconds"],
            "returnedFrames": len(frames), "fps": 20, "returnedDurationSeconds": len(frames) / 20,
            "initialHistoryFrames": len(initial), "initialHistoryKind": history["kind"], "initialHistoryCanonicalSha256": canonical(history),
            "returnedFrameCanonicalSha256": canonical(frames), "mask": first["mask"], "windows": windows}
        if "bunny" in trace["description"].lower():
            observations = bunny_observations(motion, names, parents, neutral, 20)
            measures = archive / (trace["turnId"] + "-source-fk.json")
            measures.write_text(json.dumps(observations, ensure_ascii=False, indent=2), encoding="utf-8")
            item["bunnySourceFK"] = {key: value for key, value in observations.items() if key != "frames"}
            item["bunnySourceFK"]["allFrameEvidence"] = record(measures)
        results["traces"].append(item)
        descriptions[trace["description"]].append(item)
    for description, group in descriptions.items():
        if len(group) < 2:
            continue
        for a, b in itertools.combinations(group, 2):
            aa, bb = arrays[a["turnId"]], arrays[b["turnId"]]
            excluded = {"characterId", "turnId", "revision", "requestId", "initialHistory"}
            meaningful_a = {k: v for k, v in aa["firstRequest"].items() if k not in excluded}
            meaningful_b = {k: v for k, v in bb["firstRequest"].items() if k not in excluded}
            pair = {"turnIds": [a["turnId"], b["turnId"]], "description": description,
                "seeds": [a["seed"], b["seed"]], "sameNonIdentityRequestFieldsExceptHistory": meaningful_a == meaningful_b,
                "initialHistoryExactlyEqual": a["initialHistoryCanonicalSha256"] == b["initialHistoryCanonicalSha256"],
                "returnedFramesExactlyEqual": a["returnedFrameCanonicalSha256"] == b["returnedFrameCanonicalSha256"],
                "initialHistory13MappedBoneDifference": compare_rotations(aa["history"], bb["history"], indices),
                "returned120Frame13MappedBoneDifference": compare_rotations(aa["motion"], bb["motion"], indices),
                "sameRecordedModelFeatureContractAndAdapter": all(a["windows"][0]["provenance"][k] == b["windows"][0]["provenance"][k]
                    for k in ("featureContract", "modelSha256")) and a["windows"][0]["clipSource"]["adapterSha256"] == b["windows"][0]["clipSource"]["adapterSha256"],
            }
            results["repeatedDescriptions"].append(pair)
    results["summary"] = {"traces": 8, "http200Responses": sum(len(t["windows"]) for t in results["traces"]),
        "allClientCompleted": all(t["finishReason"] == "completed" for t in results["traces"]),
        "totalReturnedFrames": sum(t["returnedFrames"] for t in results["traces"]),
        "firstResponseSecondsRange": [min(t["firstResponseElapsedSeconds"] for t in results["traces"]), max(t["firstResponseElapsedSeconds"] for t in results["traces"])],
        "allResponsesSecondsRange": [min(t["allResponsesElapsedSeconds"] for t in results["traces"]), max(t["allResponsesElapsedSeconds"] for t in results["traces"])],
        "repeatedDescriptionPairs": len(results["repeatedDescriptions"]),
        "allRepeatedPairsHaveDifferentActualHistory": all(not p["initialHistoryExactlyEqual"] for p in results["repeatedDescriptions"]),
        "allRepeatedPairsHaveDifferentReturnedFrames": all(not p["returnedFramesExactlyEqual"] for p in results["repeatedDescriptions"])}
    output.mkdir(parents=True, exist_ok=True)
    report_path = output / "evidence.json"
    report_path.write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    lines = ["# 本次动作试用：保存轨迹审计", "",
        "8份原始motion trace已按startedUtc确认并逐字节归档（2026-09-10 20:17–20:22 JST）。只读取已保存动作数据；未启动模型、服务或Unity，未重新生成。", "",
        "24个返回窗口全部HTTP200；每次120帧/20fps/6秒，客户端均记completed。首窗0.539–0.616秒，全三窗0.969–1.166秒。成功返回和播放完成不等于语义达成。", "",
        "| Turn | Seed | 首窗/全窗秒 | 帧数 | 完成 | 描述 |", "|---|---:|---:|---:|---|---|"]
    for item in results["traces"]:
        lines.append(f"| {item['turnId']} | {item['seed']} | {item['firstResponseElapsedSeconds']:.3f}/{item['allResponsesElapsedSeconds']:.3f} | {item['returnedFrames']} | {item['finishReason']} | {item['description']} |")
    lines += ["", "返回Core27骨架只有Hand、HandEnd、HandThumb1，没有独立食指/中指链；当前UpperBody播放器只映射13个躯干、头、手臂和整手骨骼，不驱动生成的手指关节。因此独立摇食指和食指＋中指兔耳手型缺少执行通道。", "",
        "兔耳相关4条使用原始返回旋转与固定源骨长做CPU正向运动学。以下是每段中两腕同时最高时，较低那只手腕相对Head关节的高度；负值代表仍在下方，不能解释为用户实际VRM录像：", "",
        "| Turn | 最佳同时腕高相对Head | 最近同时两腕中较远一腕距离Head |", "|---|---:|---:|"]
    for item in results["traces"]:
        if "bunnySourceFK" in item:
            fk = item["bunnySourceFK"]
            lines.append(f"| {item['turnId']} | {fk['bestSimultaneousLowerWristHeightRelativeToHeadMeters']:.3f} m | {fk['minimumAcrossTimeOfFartherWristHeadDistanceMeters']:.3f} m |")
    lines += ["", "这几段返回轨迹连双腕到头两侧的上身目标也未形成；不仅是细手指手型问题。Head是源关节位置，不是头皮表面，报告不做场景骨架、混合或衣物碰撞推断。", "",
        "| 相同描述配对 | 16帧输入history平均/最大差 | 返回120帧平均/最大差 |", "|---|---:|---:|"]
    for pair in results["repeatedDescriptions"]:
        h, m = pair["initialHistory13MappedBoneDifference"], pair["returned120Frame13MappedBoneDifference"]
        lines.append(f"| {' / '.join(pair['turnIds'])} | {h['meanDegrees']:.3f}° / {h['maxDegrees']:.3f}° | {m['meanDegrees']:.3f}° / {m['maxDegrees']:.3f}° |")
    lines += ["", "三组重复描述都使用seed0、相同模型/契约/adapter，但真实initialHistory不同，返回轨迹也并非相同。除会话标识与history外请求字段相同；因此不是相同完整输入的重放。没有保存精确特征向量，不能把全部差异单因果归为history。", "",
        "逐窗HTTP、身份回显、种子、provenance、帧SHA、输入history差异及每帧CPU位置见`evidence.json`和其中归档链接。原始文件未改，`archive-manifest.json`保留8份原字节SHA。"]
    (output / "evidence.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(json.dumps({"summary": results["summary"], "repeated": results["repeatedDescriptions"]}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
