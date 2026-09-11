"""CPU-only paired summary of the frozen initial-history validation experiment."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

from Server.ARDY.native_motion_continuity import continuity_observations


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def distribution(values):
    values = np.asarray(values, dtype=np.float64)
    return {"count": len(values), "median": float(np.median(values)),
            "p95": float(np.percentile(values, 95)), "maximum": float(values.max())} if len(values) else None


def necessary_heights(cases):
    observations = {}
    for case in cases:
        if not case["assessment"]["includeInSuccessSummary"]:
            continue
        for number, observation in enumerate(case["assessment"]["observations"]):
            specification = observation["specification"]
            if specification["metric"] == "wrist_relative_height_m" and observation.get("thresholdSatisfied") is not None:
                observations[(case["recordId"], case["seed"], number)] = observation
    reached = [o for o in observations.values() if o["thresholdSatisfied"]]
    return observations, {
        "denominator": len(observations), "reached": len(reached), "notReached": len(observations)-len(reached),
        "reachedWithin1Second": sum(o["simultaneousEvent"]["firstObservedSeconds"] <= 1 for o in reached),
        "firstReachSecondsReachedOnly": distribution([o["simultaneousEvent"]["firstObservedSeconds"] for o in reached]),
        "longestContinuousSecondsReachedOnly": distribution([o["simultaneousEvent"]["longestContinuousSeconds"] for o in reached]),
    }


def summarize(args):
    experiment = read(args.experiment / "report.json")
    baseline = read(experiment["plan"]["baselineReport"])
    if experiment["status"] != "complete" or baseline["status"] != "complete":
        raise ValueError("Both complete reports are required")
    if digest(experiment["plan"]["baselineReport"]) != experiment["plan"]["baselineReportSha256"]:
        raise ValueError("The baseline report changed")
    with np.load(args.experiment / "projected-full16-source-pose.npz", allow_pickle=False) as archive:
        input_pose = dict(archive)
    cases = {length: [case for case in experiment["results"] if case["initialHistoryFrames"] == length] for length in (4, 8)}
    cases[16] = [case for case in baseline["results"] if case["condition"] == "candidate" and case["history"] == "grounded"]
    expected = {(case["recordId"], case["seed"], case["conditionSha256"]) for case in cases[16]}
    if len(expected) != 80 or any({(c["recordId"], c["seed"], c["conditionSha256"]) for c in rows} != expected for rows in cases.values()):
        raise ValueError("The three lengths are not the same eighty paired conditions")
    names = read(cases[16][0]["clip"])["jointNames"]
    continuity_rows = []
    continuum = {}
    for length, rows in cases.items():
        continuum[length] = {}
        for case in rows:
            if length == 16:
                with np.load(case["npz"], allow_pickle=False) as arrays:
                    continuity = continuity_observations(input_pose, arrays, names)
                path = args.experiment / (f"{case['recordId']}--candidate--baseline16--seed{case['seed']}.continuity.json")
                path.write_text(json.dumps(continuity, indent=2), encoding="utf-8")
            else:
                path = Path(case["continuityFile"])
                continuity = read(path)
            key = (case["recordId"], case["seed"])
            row = {"recordId": key[0], "seed": key[1], "initialHistoryFrames": length,
                   "continuityFile": str(path.resolve()), "clip": case["clip"],
                   "firstGlobalJumpMaxDegrees": continuity["firstFrameGlobalRotationJumpDegrees"]["maximum"],
                   "firstLocalJumpMaxDegrees": continuity["firstFrameLocalRotationJumpDegrees"]["maximum"],
                   "firstWristDisplacementMaxMeters": max(continuity["firstFrameSourceWristDisplacementMetersLeftRight"]),
                   "generatedSpeedP95DegreesPerSecond": continuity["speedSummaryGeneratedFramesOnly"]["p95"],
                   "generatedSpeedMaxDegreesPerSecond": continuity["speedSummaryGeneratedFramesOnly"]["maximum"],
                   "seamJumpMaxDegrees": max(seam["maximumStepDegrees"] for seam in continuity["windowSeams"])}
            continuum[length][key] = row
            continuity_rows.append(row)
    summaries, heights = [], {}
    for length in (4, 8, 16):
        heights[length], goals = necessary_heights(cases[length])
        metrics = list(continuum[length].values())
        summaries.append({"initialHistoryFrames": length, "trajectories": len(metrics), "necessaryHeightGoals": goals,
                          "continuity": {field: distribution([row[field] for row in metrics]) for field in
                                         ("firstGlobalJumpMaxDegrees", "firstLocalJumpMaxDegrees", "firstWristDisplacementMaxMeters",
                                          "generatedSpeedP95DegreesPerSecond", "generatedSpeedMaxDegreesPerSecond", "seamJumpMaxDegrees")}})
    pairs = []
    for length in (4, 8):
        common = [key for key in heights[16] if heights[16][key]["thresholdSatisfied"] and heights[length][key]["thresholdSatisfied"]]
        timing = [{"recordId": key[0], "seed": key[1], "observationIndex": key[2],
                   "firstReachDeltaSeconds": heights[length][key]["simultaneousEvent"]["firstObservedSeconds"] - heights[16][key]["simultaneousEvent"]["firstObservedSeconds"],
                   "longestHoldDeltaSeconds": heights[length][key]["simultaneousEvent"]["longestContinuousSeconds"] - heights[16][key]["simultaneousEvent"]["longestContinuousSeconds"]} for key in common]
        pairs.append({"initialHistoryFrames": length, "baselineFrames": 16,
                      "newlyReached": [list(key) for key in heights[16] if heights[length][key]["thresholdSatisfied"] and not heights[16][key]["thresholdSatisfied"]],
                      "noLongerReached": [list(key) for key in heights[16] if not heights[length][key]["thresholdSatisfied"] and heights[16][key]["thresholdSatisfied"]],
                      "matchedReachedTiming": timing,
                      "matchedReachedFirstTimeDeltaSeconds": distribution([row["firstReachDeltaSeconds"] for row in timing]),
                      "all80PairedContinuityDelta": {field: distribution([continuum[length][key][field]-continuum[16][key][field] for key in continuum[16]]) for field in
                                                    ("firstGlobalJumpMaxDegrees", "firstLocalJumpMaxDegrees", "generatedSpeedP95DegreesPerSecond", "generatedSpeedMaxDegreesPerSecond", "seamJumpMaxDegrees")}})
    report = {"schema": 1, "purpose": "Validation-only paired comparison; unchanged production history and no final-test predictions.",
              "sourceReport": str((args.experiment / "report.json").resolve()), "sourceReportSha256": digest(args.experiment / "report.json"),
              "baselineReportSha256": experiment["plan"]["baselineReportSha256"], "sourceScripts": experiment["plan"]["sourceScripts"],
              "basis": "Explicit necessary world-Y height observations only; simultaneous sides use the same frame. Timing medians exclude unreached cases, so paired common-reached deltas are also reported. No automatic semantics, palm, naturalness or comfort verdict.",
              "continuityBasis": "Before Unity blending/retargeting; thirteen source bones. Each distribution summarizes trajectory-level maxima or p95, not pooled frame statistics. Initial jump is from identical full-history endpoint; continuation seams are generated frames 40 and 80.",
              "productionHistoryChanged": False, "selectedHistoryLength": None,
              "rows": summaries, "pairedComparisons": pairs, "perTrajectoryContinuity": continuity_rows}
    args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"report": str(args.output.resolve()), "rows": summaries}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--experiment", type=Path, default=Path("Server/ARDY/runtime/history-length-validation-v1"))
    parser.add_argument("--output", type=Path, default=Path("Tools/MotionAdapter/reports/history-length-validation-v1.json"))
    summarize(parser.parse_args())
