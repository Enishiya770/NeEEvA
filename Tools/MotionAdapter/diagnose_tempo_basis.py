"""Explain the fixed-root versus actual relative-wrist cadence diagnostic."""
import argparse
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def source(path):
    return {"path": path.relative_to(ROOT).as_posix(), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "bytes": path.stat().st_size}


def changes(samples, key, threshold, origin):
    result, previous = [], 0
    for sample in samples:
        value = sample[key]
        sign = 1 if value > threshold else -1 if value < -threshold else 0
        if sign and previous and sign != previous:
            result.append({"frame": sample["frame"], "gameTime": sample["time"], "secondsFromActing": sample["time"] - origin, "angle": value})
        if sign:
            previous = sign
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "Tools/MotionAdapter/reports/tempo-execution-v1")
    args = parser.parse_args()
    archive, output = args.archive.resolve(), args.output.resolve()
    root_file, local_file = archive / "constraint-regression.json", archive / "tempo-regression.json"
    root = json.loads(root_file.read_text(encoding="utf-8-sig"))
    local = json.loads(local_file.read_text(encoding="utf-8-sig"))
    fixture = next(f for f in root["fixtures"] if f["id"] == "fixture-2")
    case = next(c for c in fixture["cases"] if c["name"] == "adaptive-hold-source")
    measured = next(c for c in local["cases"] if c["fixture"] == "fixture-2" and c["name"] == case["name"])
    root_samples = [s for s in case["samples"] if s["phase"] == "acting"]
    local_samples = [s for s in measured["samples"] if s["phase"] == "acting"]
    local_by_frame = {s["frame"]: s for s in local_samples}
    matched = []
    for sample in root_samples:
        if sample["frame"] not in local_by_frame:
            continue
        pose = local_by_frame[sample["frame"]]
        command = json.loads(sample["observation"])["commandedLocalAngle"]
        matched.append({"frame": sample["frame"], "gameTime": sample["time"], "localGameTimeDouble": pose["time"],
                        "secondsFromActing": pose["time"] - measured["firstActingAt"],
                        "fixedRootAngle": sample["leftWristAngle"], "actualRelativeAngle": pose["leftAngle"],
                        "commandDiagnosticOnly": command, "actualForearmRoll": sample["palms"]["leftForearmRoll"]})
    result = {
        "status": "measurement-basis-error-confirmed", "archive": archive.relative_to(ROOT).as_posix(),
        "rawReports": [source(root_file), source(local_file)], "script": source(Path(__file__)),
        "fixture": {key: fixture[key] for key in ("id", "avatar", "avatarSha256", "branch", "yaw", "scale")},
        "case": case["name"], "plan": json.loads(case["plan"]), "originalFailure": root["status"],
        "testedSourceHashes": {key: value for key, value in root.items() if key.endswith("Sha256")},
        "tempoHarnessSha256": local["tempoHarnessSha256"], "timingSourceSha256": local["timingSourceSha256"],
        "expectedInternalSignChanges": 3,
        "fixedRootChangesAtOriginalPoint5Threshold": changes(root_samples, "leftWristAngle", .5, measured["firstActingAt"]),
        "relativeChangesAtIndependentPoint15Threshold": changes(local_samples, "leftAngle", .15, measured["firstActingAt"]),
        "relativeChangesAtSamePoint5Threshold": changes(local_samples, "leftAngle", .5, measured["firstActingAt"]),
        "pairedFrameCount": len(matched),
        "maximumActualRelativeMinusCommandDegrees": max(abs(v["actualRelativeAngle"] - v["commandDiagnosticOnly"]) for v in matched),
        "actualForearmRollFirstLast": [root_samples[0]["palms"]["leftForearmRoll"], root_samples[-1]["palms"]["leftForearmRoll"]],
        "tailActualSamples": matched[-12:],
        "geometry": {key: case[key] for key in ("maximumDirectionError", "maximumBendError", "maximumShoulderVariation", "maximumElbowVariation", "maximumArmRotationVariation")},
        "interpretation": "Actual forearm axial roll is permitted for constrained palms. A fixed root-space neutral includes that parent rotation, so its late +0.68855deg reading is not an extra wrist oscillation. The independently measured hand-relative-forearm curve completes exactly3 internal sign changes and approaches zero.",
        "testCorrection": "Only Tempo completion uses the already independently sampled relative-joint angle. Exact sign changes remain2*cycles-1, amplitude tolerances remain1.5deg wrists/2.5deg head. Old fixed-root samples/statistics, failed reports, core and old Palm/legacy checks remain unchanged.",
    }
    assert len(result["fixedRootChangesAtOriginalPoint5Threshold"]) == 4
    assert len(result["relativeChangesAtIndependentPoint15Threshold"]) == len(result["relativeChangesAtSamePoint5Threshold"]) == 3
    output.mkdir(parents=True, exist_ok=True)
    (output / "measurement-basis-diagnosis-v1.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    tail = result["tailActualSamples"][-1]
    md = f"""# Tempo sample-basis diagnosis

The failed `fixture-2 / adaptive-hold-source` counted **4** sign changes in the old fixed-root hand projection. The actual hand-relative-forearm curve has **3** at both the old ±0.5° threshold and the independent ±0.15° threshold. The requested two cycles are intact.

The permitted actual forearm roll changes from {result['actualForearmRollFirstLast'][0]:.6f}° to {result['actualForearmRollFirstLast'][1]:.6f}°. Near the end, frame {tail['frame']} at game time {tail['gameTime']:.6f}s reads {tail['fixedRootAngle']:.6f}° in the old projection, but {tail['actualRelativeAngle']:.8f}° in the actual relative joint. That parent-rotation offset crosses the old positive threshold and creates a spurious fourth count.

Across {len(matched)} same-frame comparisons, actual relative angle differs from the separately recorded command by at most {result['maximumActualRelativeMinusCommandDegrees']:.8f}°. The command is corroboration; cadence is still measured from actual bones. Arm direction error is0°, elbow error {case['maximumBendError']:.8f}°, and shoulder/elbow residuals remain at micrometre scale.

Only Tempo completion now checks the independently measured relative-joint curve, with **exactly2×cycles−1** internal sign changes and unchanged amplitude tolerances. No runtime curve, geometry threshold, old Palm regression or legacy statistics were altered. Full failed evidence is preserved.

Raw fixed-root report: `{source(root_file)['path']}`  
SHA256: `{source(root_file)['sha256']}`

Raw independent report: `{source(local_file)['path']}`  
SHA256: `{source(local_file)['sha256']}`

The companion JSON contains timestamps, all crossing events, paired tail samples, forearm roll, source hashes and geometry measurements.
"""
    (output / "measurement-basis-diagnosis-v1.md").write_text(md, encoding="utf-8")
    print(json.dumps({"pairedFrames": len(matched), "maxLocalCommandErrorDegrees": result["maximumActualRelativeMinusCommandDegrees"], "tail": tail}, indent=2))


if __name__ == "__main__":
    main()
