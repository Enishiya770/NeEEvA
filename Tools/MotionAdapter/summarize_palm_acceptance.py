"""Compact archived actual-Unity palm acceptance without changing or rerunning motion.

Every original case metric is retained; large frame arrays remain in the hashed
archive. Phase ranges below summarize saved observations, not invented samples.
The harness checks every actual frame and stores at most 80 Hz plus transitions.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE_FILES = {
    "playerSha256": "Assets/AIChatTookit/Scripts/Motion/ArdyMotionPlayer.cs",
    "planSha256": "Assets/AIChatTookit/Scripts/Motion/ArdyControlPlan.cs",
    "sessionSha256": "Assets/AIChatTookit/Scripts/Motion/ArdyConstraintSession.cs",
    "observerSha256": "Assets/AIChatTookit/Scripts/Motion/ArdyConstraintObserver.cs",
    "harnessSha256": "Assets/Editor/ArdyConstraintRegression.cs",
    "controllerSha256": "Assets/AIChatTookit/Scripts/Motion/ArdyLiveMotionController.cs",
    "bridgeSha256": "Assets/AIChatTookit/Scripts/Chat/ArdyDialogueMotionBridge.cs",
    "palmHarnessSha256": "Assets/Editor/ArdyConstraintPalmRegression.cs",
    "palmGeometrySha256": "Assets/AIChatTookit/Scripts/Motion/ArdyPalmGeometry.cs",
    "parserSha256": "Assets/AIChatTookit/Scripts/Chat/DialogueMotionIntent.cs",
}
SAMPLE_FIELDS = (
    "leftDirectionError", "rightDirectionError", "leftBend", "rightBend",
    "leftBendError", "rightBendError", "leftWristAngle", "rightWristAngle",
)
PALM_FIELDS = (
    "leftFacingError", "rightFacingError", "leftTrackingError", "rightTrackingError",
    "leftWristSwing", "rightWristSwing", "leftForearmRoll", "rightForearmRoll",
    "leftFingerForearmAngle", "rightFingerForearmAngle",
)
OBSERVATION_FIELDS = tuple(side + field for side in ("left", "right") for field in (
    "ResolvedBend", "WristMarginDegrees", "PlannedWristSwing", "PlannedForearmRoll"))


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def relative(path: Path) -> str:
    return path.resolve().relative_to(ROOT).as_posix()


def file_record(path: Path) -> dict:
    return {"path": relative(path), "bytes": path.stat().st_size, "sha256": sha256(path)}


def decode(value):
    return json.loads(value) if isinstance(value, str) and value else value


def numeric_range(values: list[float]) -> dict:
    return {"min": min(values), "max": max(values)}


def phase_ranges(samples: list[dict]) -> dict:
    phases = defaultdict(lambda: {"count": 0, "values": defaultdict(list)})
    for sample in samples:
        phase = phases[sample["phase"]]
        phase["count"] += 1
        fields = {key: sample[key] for key in SAMPLE_FIELDS if key in sample}
        palm = sample.get("palms") or {}
        fields.update({key: palm[key] for key in PALM_FIELDS if key in palm})
        observation = decode(sample.get("observation")) or {}
        # These are labelled solver diagnostics, never used instead of actual geometry.
        fields.update({"solver." + key: observation[key] for key in OBSERVATION_FIELDS if key in observation})
        for key, value in fields.items():
            if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value):
                raise ValueError(f"Invalid {key} in saved observation")
            phase["values"][key].append(value)
    return {key: {"savedSamples": item["count"], "ranges": {
        field: numeric_range(values) for field, values in item["values"].items()
    }} for key, item in phases.items()}


def context_assertions(case: dict) -> dict | None:
    palm = case.get("palm") or {}
    if not palm.get("rejectionContext"):
        return None
    context = decode(palm["rejectionContext"])
    runtime = palm.get("outcome") == "expected-runtime-goal-loss"
    checks = {
        "exactRequestedPlanRetained": context.get("lastControlPlan") == decode(case["plan"]),
        "currentPlanIsJsonNull": "controlPlan" in context and context["controlPlan"] is None,
        "doesNotClaimHolding": context.get("previousRequestedPoseHeld") is False,
        "errorRetained": bool(context.get("lastControlError")),
        "observationMatchesExecution": isinstance(context.get("lastControlObservation"), dict) if runtime
        else "lastControlObservation" in context and context["lastControlObservation"] is None,
        "harnessVerifiedBindClearsFacts": palm.get("bindClearedRejectionContext") is True,
    }
    return {"checks": checks, "allPassed": all(checks.values()), "actualContext": context}


def png_manifest(archive: Path, fixture: dict, case: dict) -> dict:
    folder = archive / (fixture["id"] + "-" + case["name"])
    files = sorted(folder.glob("frame-*.png")) if folder.exists() else []
    if len(files) != case["renderedFrames"]:
        raise ValueError(f"Missing/extra actual PNG frames for {folder}")
    records, times = [], []
    for path in files:
        stamp = path.with_suffix(".json")
        pose = json.loads(stamp.read_text(encoding="utf-8-sig"))
        png = file_record(path)
        if png["sha256"] not in fixture["pngSha256"]:
            raise ValueError(f"PNG differs from actual harness hash: {path}")
        records.append({"png": png, "sample": file_record(stamp), "time": pose["time"],
                        "frame": pose["frame"], "phase": pose["phase"]})
        times.append(pose["time"])
    if any(b <= a for a, b in zip(times, times[1:])):
        raise ValueError(f"Nonmonotonic actual capture times in {folder}")
    return {"directory": relative(folder), "count": len(files), "frames": records}


def summarize(archive: Path) -> dict:
    report_path = archive / "constraint-regression.json"
    report = json.loads(report_path.read_text(encoding="utf-8-sig"))
    if report["status"] != "passed":
        raise ValueError("Cannot publish passing acceptance from a failed or running report")
    result = {key: value for key, value in report.items() if key != "fixtures"}
    result.update({"schema": 1, "generatedUtc": datetime.now(timezone.utc).isoformat(),
                   "fullReport": file_record(report_path), "aggregationScript": file_record(Path(__file__)),
                   "fixtures": [], "renderManifest": []})
    source_checks = []
    for key, path in SOURCE_FILES.items():
        actual = ROOT / path
        # Resolve only exact filename when an older source tree used a different folder.
        if not actual.is_file():
            matches = list((ROOT / "Assets").rglob(Path(path).name))
            if len(matches) != 1:
                raise ValueError(f"Cannot resolve source: {path}")
            actual = matches[0]
        current = file_record(actual)
        source_checks.append({**current, "reportField": key, "testedSha256": report[key],
                              "matchesCurrent": current["sha256"] == report[key]})
    result["testedSourceChecks"] = source_checks
    counts = Counter()
    for fixture_index, fixture in enumerate(report["fixtures"]):
        target = {key: value for key, value in fixture.items() if key not in ("cases", "pngSha256")}
        target["cases"] = []
        target["avatarMatchesCurrent"] = sha256(ROOT / fixture["avatar"]) == fixture["avatarSha256"]
        for case_index, case in enumerate(fixture["cases"]):
            compact = {key: value for key, value in case.items() if key != "samples"}
            compact["plan"] = decode(compact["plan"])
            samples = case["samples"]
            compact["sampleArchive"] = {"report": relative(report_path),
                "jsonPointer": f"/fixtures/{fixture_index}/cases/{case_index}/samples", "count": len(samples)}
            compact["savedPhaseRanges"] = phase_ranges(samples)
            compact["feedbackAssertions"] = context_assertions(case)
            render = png_manifest(archive, fixture, case)
            compact["actualRenderDirectory"] = render["directory"] if render["count"] else None
            if render["count"]:
                result["renderManifest"].append(render)
            target["cases"].append(compact)
            outcome = (case.get("palm") or {}).get("outcome")
            counts[outcome or ("completed-with-actual-geometry-checks" if case["finishedAt"] > 0 else "incomplete")] += 1
        result["fixtures"].append(target)
    cases = [case for fixture in result["fixtures"] for case in fixture["cases"]]
    positives = [case for case in cases if not (case.get("palm") or {}).get("expectedFailure")]
    preparatory_swings = [phase["ranges"][side + "WristSwing"]["max"]
        for case in positives for name, phase in case["savedPhaseRanges"].items() if name == "preparing"
        for side in ("left", "right") if side + "WristSwing" in phase["ranges"]]
    result["summary"] = {
        "fixtureCases": len(cases), "outcomes": dict(counts),
        "actualPngFrames": sum(item["count"] for item in result["renderManifest"]),
        "storedPoseSamples": sum(case["sampleArchive"]["count"] for case in cases),
        "maximumPositiveDirectionErrorDegrees": max(case["maximumDirectionError"] for case in positives),
        "maximumPositiveBendErrorDegrees": max(case["maximumBendError"] for case in positives),
        "maximumPositivePalmTrackingErrorDegrees": max(case["palm"]["maximumTrackingError"] for case in positives),
        "maximumPositiveWristSwingDegrees": max(case["palm"]["maximumWristSwing"] for case in positives),
        "maximumSavedPreparingWristSwingDegrees": max(preparatory_swings) if preparatory_swings else None,
        "maximumCurrentPointDriftMeters": max(case["palm"]["maximumCurrentPointDrift"] for case in positives),
        "maximumCurrentKeepRotationDriftDegrees": max(case["palm"]["maximumCurrentKeepRotationDrift"] for case in positives),
        "maximumReturnToAnimatorErrorDegrees": max(case["returnToAnimatorMaxErrorDegrees"] for case in cases),
        "allReportedSourceHashesMatchCurrent": all(item["matchesCurrent"] for item in source_checks),
        "allAvatarsMatchCurrent": all(item["avatarMatchesCurrent"] for item in result["fixtures"]),
        "allRetainedFeedbackAssertionsPass": all(case["feedbackAssertions"]["allPassed"]
            for case in cases if case["feedbackAssertions"] is not None),
    }
    result["resolvedAndBoundaryCases"] = [
        {"fixture": fixture["id"], "name": case["name"], "requestedPlan": case["plan"],
         "leftResolvedBend": case["palm"]["leftResolvedBend"], "rightResolvedBend": case["palm"]["rightResolvedBend"],
         "firstActingAtGameTime": case["palm"]["firstActingAt"],
         "secondsFromRequestToFirstActing": case["palm"]["firstActingAt"] - case["startedAt"]
            if case["palm"]["firstActingAt"] >= 0 else None, "actingFrames": case["actingFrames"],
         "actualLeftWristRangeDegrees": [case["leftWristMinimum"], case["leftWristMaximum"]],
         "actualRightWristRangeDegrees": [case["rightWristMinimum"], case["rightWristMaximum"]],
         "leftWristSignChanges": case["leftWristSignChanges"], "rightWristSignChanges": case["rightWristSignChanges"],
         "savedActingRanges": case["savedPhaseRanges"].get("acting"),
         "outcome": case["palm"].get("outcome")}
        for fixture in result["fixtures"] for case in fixture["cases"]
        if case["name"].startswith("public-qwen-") or case["name"] == "old-fixed8-policy-original-tag-horizon-target"
    ]
    result["priorEvidence"] = []
    for name in ("primary-pass-v1", "full-failed-v1", "full-failed-v2"):
        prior = archive.parent / name / "constraint-regression.json"
        if prior.is_file():
            result["priorEvidence"].append(file_record(prior))
    result["correctedFindings"] = [
        "Full-v1: fixed8 at the measured 3m shoulder-height partner is reachable, with only about0.25-0.55deg whole-curve wrist margin. The incorrect rejection expectation became a full low-margin positive; explicit0 remains a strict unreachable negative. The80deg bound was unchanged.",
        "Full-v2: JsonUtility serialized null nested control facts as default objects. Production DescribeMotionContext now writes explicit JSON null. The harness checks raw JObject null and exact retained plan, without weakening any geometric gate.",
    ]
    public = archive / "public-qwen-input.json"
    parsed = archive / "public-qwen-current-parser-plan.json"
    result["publicIntentFixture"] = {"input": file_record(public), "currentParserPlan": file_record(parsed),
        "interpretation": "The unchanged first actual Qwen XML is replayed through the current production parser. Omitted elbows become auto degrees; this is a new executor interpretation, not a rerolled or hand-edited model output."}
    result["interpretationLimits"] = [
        "Actual geometric and lifecycle acceptance for the listed models, targets, roots and plans; not exhaustive combinations or a naturalness score.",
        "Palm normal is independently measured from the actual Hand/Index/Little proximal triangle. Solver margins remain separately labelled diagnostics.",
        "Preparatory phase ranges describe saved real samples; every-frame active gates remain in the full harness result.80Hz storage cap plus phase transitions is not motion resampling.",
        "Engineered80deg wrist swing and180deg forearm roll are implementation limits, not medical safety guarantees.",
        "Finger pose, clothing/body contacts, comfort and social appropriateness require human review. Some preview extremities may leave the image; bone checks still run.",
        "This route is procedural constraints plus bounded local curves, not native ARDY or adapter retraining. A passing public example does not prove all Qwen plans valid.",
    ]
    return result


def markdown(report: dict) -> str:
    summary = report["summary"]
    lines = ["# Actual Unity palm acceptance", "", f"**{report['status']}** — {summary['fixtureCases']} fixture cases, "
        f"{report['checks']:,} numeric checks and {report['actualFrames']:,} actual sampled-loop frames; "
        f"{summary['actualPngFrames']} verified PNGs. Unity {report['unityVersion']}.", "",
        "Two real VRM assets run with real Animator updates: disabled-VRM Humanoid and actual ControlRig, "
        "root yaw25/65deg and scales0.8/1/1.2. No manual Tick, network service, audio playback or saved production scene.", "",
        "| Positive actual metric | Maximum |", "|---|---:|",
        f"| Arm direction error | {summary['maximumPositiveDirectionErrorDegrees']:.6f} deg |",
        f"| Elbow bend error | {summary['maximumPositiveBendErrorDegrees']:.6f} deg |",
        f"| Independent palm tracking error | {summary['maximumPositivePalmTrackingErrorDegrees']:.6f} deg |",
        f"| Actual wrist swing | {summary['maximumPositiveWristSwingDegrees']:.6f} deg |",
        f"| Saved preparing-phase wrist swing | {summary['maximumSavedPreparingWristSwingDegrees']:.6f} deg |",
        f"| Return-to-Animator error | {summary['maximumReturnToAnimatorErrorDegrees']:.6f} deg |", "",
        "The unchanged first public Qwen XML is re-parsed and sent through the real Chat event → Bridge → Controller → Player. "
        "Omitted elbows are solved at entry, while explicit angles remain fixed. Every public positive fixture must complete its measured curve.", "",
        "Coverage includes side/elevated partner targets, bound-target priority, start-time target snapshot, Camera and "
        "explicitly labelled character-forward fallback, current/keep, up/down and inward/outward palms, "
        "legacy keep, missing-finger/scale rejection, fixed-angle limits, retained failure feedback and Bind clearing.", "",
        "| Fixture | Avatar / branch | Cases | Uncontrolled bone error | Expression error |", "|---|---|---:|---:|---:|"]
    for fixture in report["fixtures"]:
        lines.append(f"| {fixture['id']} | {Path(fixture['avatar']).name} / {fixture['branch']} | {len(fixture['cases'])} | "
                     f"{fixture['untouchedBoneMaxErrorDegrees']:.6f} deg | {fixture['expressionMaxError']:.6f} |")
    lines += ["", "Fixed8 is a measured reachable low-margin positive; fixed0 is an explicit unreachable negative. "
        "The first failed run incorrectly assumed fixed8 could not reach. The second exposed a production JSON-null bug; "
        "it is fixed and directly verified. Both complete failed archives remain intact.", "",
        f"Full raw report: `{report['fullReport']['path']}`  ", f"SHA256: `{report['fullReport']['sha256']}`", "",
        "`acceptance.json` retains every case metric, saved phase ranges, exact plan/feedback, source hashes, "
        "and archive paths plus hashes for each PNG and pose stamp. Large sample arrays remain in the hashed raw report.", "",
        "This is geometric and lifecycle evidence. It does not automatically establish naturalness, comfort, collision-free "
        "clothing/body motion or broad Qwen planning success. Preview image coverage is separate from actual bone measurements.", ""]
    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "Tools/MotionAdapter/reports/palm-execution-v1")
    args = parser.parse_args()
    archive, output = args.archive.resolve(), args.output.resolve()
    # Both locations must stay inside this reviewable repository.
    relative(archive)
    relative(output)
    report = summarize(archive)
    if not report["summary"]["allRetainedFeedbackAssertionsPass"]:
        raise ValueError("Archived failure feedback did not satisfy the direct JSON assertions")
    output.mkdir(parents=True, exist_ok=True)
    (output / "acceptance.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (output / "acceptance.md").write_text(markdown(report), encoding="utf-8")
    print(json.dumps(report["summary"], ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
