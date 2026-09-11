"""CPU-only descriptive summary of the one frozen supported-motion final audit."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path

import numpy as np


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def distribution(values):
    if not values:
        return None
    values = np.asarray(values, dtype=np.float64)
    return {"count": len(values), "mean": float(values.mean()), "median": float(np.median(values)),
            "p95": float(np.percentile(values, 95)), "minimum": float(values.min()), "maximum": float(values.max())}


def heights(rows):
    output = {}
    for row in rows:
        for index, observation in enumerate(row["assessment"]["observations"]):
            if observation["specification"]["metric"] == "wrist_relative_height_m" and observation["thresholdSatisfied"] is not None:
                output[(row["recordId"], row["seed"], index)] = observation
    return output


def height_summary(observations):
    reached = [o for o in observations.values() if o["thresholdSatisfied"]]
    return {"denominator": len(observations), "reached": len(reached), "notReached": len(observations)-len(reached),
            "reachedWithin1Second": sum(o["simultaneousEvent"]["firstObservedSeconds"] <= 1 for o in reached),
            "reachedWithin2Seconds": sum(o["simultaneousEvent"]["firstObservedSeconds"] <= 2 for o in reached),
            "firstObservedSecondsReachedOnly": distribution([o["simultaneousEvent"]["firstObservedSeconds"] for o in reached]),
            "longestContinuousSecondsReachedOnly": distribution([o["simultaneousEvent"]["longestContinuousSeconds"] for o in reached]),
            "frameFractionReachedOnly": distribution([o["simultaneousEvent"]["frameFraction"] for o in reached])}


def summarize(args):
    source = json.loads(args.source.read_text(encoding="utf-8"))
    plan = source["plan"]
    if source["status"] != "complete" or not plan["finalTest"] or plan["split"] != "test":
        raise ValueError("A complete, explicitly reserved final-test report is required")
    if len(source["results"]) != 384 or len(plan["recordIds"]) != 64 or set(plan["histories"]) != {"grounded"} or plan["seeds"] != [0, 1]:
        raise ValueError("The frozen supported final audit shape changed")
    if any(r["evaluationTrack"] != "supported_upper_body" or not r["assessment"]["includeInSuccessSummary"] for r in source["results"]):
        raise ValueError("Unsupported/boundary records leaked into this audit")
    conditions = ("old", "candidate", "teacher")
    grouped = {c: [r for r in source["results"] if r["condition"] == c] for c in conditions}
    expected = {(record, seed) for record in plan["recordIds"] for seed in plan["seeds"]}
    if any({(r["recordId"], r["seed"]) for r in rows} != expected or len(rows) != 128 for rows in grouped.values()):
        raise ValueError("Conditions are not the same paired 128 trajectories")
    gated = {c: heights(rows) for c, rows in grouped.items()}
    if any(set(observations) != set(gated["old"]) for observations in gated.values()):
        raise ValueError("Necessary height denominators differ between conditions")
    summaries = []
    for condition, rows in grouped.items():
        times = [w["milliseconds"] for r in rows for w in r["windows"]]
        summaries.append({"condition": condition, "trajectories": len(rows),
                          "explicitNecessaryHeightObservations": height_summary(gated[condition]),
                          "offlineGenerationWindowMilliseconds": distribution(times),
                          "firstWindowMilliseconds": distribution([r["windows"][0]["milliseconds"] for r in rows]),
                          "allThreeGenerationWindowsMilliseconds": distribution([sum(w["milliseconds"] for w in r["windows"]) for r in rows])})
    comparisons = []
    for baseline in ("old", "teacher"):
        first, second = gated["candidate"], gated[baseline]
        common = [key for key in first if first[key]["thresholdSatisfied"] and second[key]["thresholdSatisfied"]]
        comparisons.append({"condition": "candidate", "baseline": baseline,
                            "newlyReached": [list(key) for key in first if first[key]["thresholdSatisfied"] and not second[key]["thresholdSatisfied"]],
                            "noLongerReached": [list(key) for key in first if not first[key]["thresholdSatisfied"] and second[key]["thresholdSatisfied"]],
                            "pairedCommonReachedFirstTimeDifferenceSeconds": distribution([first[key]["simultaneousEvent"]["firstObservedSeconds"]-second[key]["simultaneousEvent"]["firstObservedSeconds"] for key in common]),
                            "pairedCommonReachedLongestHoldDifferenceSeconds": distribution([first[key]["simultaneousEvent"]["longestContinuousSeconds"]-second[key]["simultaneousEvent"]["longestContinuousSeconds"] for key in common])})
    groups = []
    for group in sorted({r["semanticGroup"] for r in source["results"]}):
        for condition in conditions:
            cases = [r for r in grouped[condition] if r["semanticGroup"] == group]
            groups.append({"semanticGroup": group, "condition": condition, "recordIds": sorted({r["recordId"] for r in cases}),
                           "trajectories": len(cases), "heightObservations": height_summary(heights(cases)), "semanticPass": None})
    result = {"schema": 1, "status": "complete", "sourceReport": str(args.source.resolve()), "sourceReportSha256": sha(args.source),
              "planSha256": source["planSha256"], "datasetSha256": plan["datasetSha256"], "sourceScripts": plan["sourceScripts"],
              "summarizerSha256": sha(__file__), "candidateSha256": plan["adapters"]["candidate"]["sha256"],
              "records": 64, "semanticGroups": len(groups)//3, "conditions": list(conditions), "trajectories": 384,
              "history": "Same captured Animator projected grounded16, continuation16", "seeds": [0, 1],
              "necessaryObservationPolicy": "Explicit label-defined world-Y height gates only. Both sides must satisfy a bilateral gate in the same frame. Gates are necessary observations, never complete semantic or naturalness successes. Event counts, ordering, palms and target-avatar clipping remain manual.",
              "timingPolicy": "First/hold medians exclude unreached observations. Paired timing differences include only observations reached by both conditions. Generation timings exclude Qwen encoding, networking, service queues, Unity playback and speech; this is not live end-to-end latency. Conditions run in fixed teacher/old/candidate order, so these timings are descriptive, not a controlled comparative speed benchmark.",
              "totalOfflineWallSeconds": source["totalWallSeconds"], "rows": summaries, "pairedComparisons": comparisons, "groups": groups,
              "semanticSuccessRate": None, "naturalnessSuccessRate": None, "testUsedForFurtherTuning": False}
    args.output.write_text(json.dumps(result, indent=2, allow_nan=False), encoding="utf-8")
    print(json.dumps({"report": str(args.output.resolve()), "rows": summaries, "totalOfflineWallSeconds": result["totalOfflineWallSeconds"]}, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, default=Path("Server/ARDY/runtime/generalization-final-supported-v1/report.json"))
    parser.add_argument("--output", type=Path, default=Path("Tools/MotionAdapter/reports/native-final-supported-audit-v1.json"))
    summarize(parser.parse_args())
