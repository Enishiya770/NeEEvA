"""Bind measured tempo acceptance, planning failures and final source checks without equating them."""
from pathlib import Path
import hashlib
import json
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "Tools/MotionAdapter/reports/semantic-tempo-v1"
RUNTIME = ROOT / "Server/ARDY/runtime/semantic-tempo-v1"
ISOLATED = ROOT / "Server/ARDY/runtime/unity-naturalness-validation"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def relative(path):
    return path.relative_to(ROOT).as_posix()


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    tempo = read(RUNTIME / "tempo-pass-v2/tempo-regression.json")
    geometry = read(RUNTIME / "tempo-pass-v2/constraint-regression.json")
    semantic = read(RUNTIME / "final-semantic-motion-regression.json")
    dialogue = read(RUNTIME / "final-dialogue-motion-regression.json")
    live = read(RUNTIME / "final-live-http.json")
    legacy = read(RUNTIME / "first-unity-pass/playmode-regression.json")
    assert all(d["status"] == "passed" for d in (tempo, geometry, live, legacy))
    assert semantic["passed"] and dialogue["passed"]
    development = read(ROOT / "Tools/MotionAdapter/reports/semantic-motion-planning-v2.json")
    holdout = read(ROOT / "Tools/MotionAdapter/reports/semantic-motion-planning-holdout-v2.json")
    dev_audit = read(ROOT / "Tools/MotionAdapter/reports/semantic-motion-planning-v2-source-audit.json")
    hold_audit = read(ROOT / "Tools/MotionAdapter/reports/semantic-motion-planning-holdout-v2-source-audit.json")
    source_paths = [
        "Assets/AIChatTookit/Scripts/Motion/" + name + ".cs" for name in (
            "ArdyActionPlan", "ArdyActionPlanCompiler", "ArdyActionConstraintLedger", "ArdyAdaptiveTiming",
            "ArdyControlPlan", "ArdyConstraintSession", "ArdyMotionPlayer", "ArdyPalmGeometry",
            "ArdyLiveMotionController", "ArdyConstraintObserver")]
    source_paths += ["Assets/AIChatTookit/Scripts/Chat/" + name + ".cs" for name in (
        "ChatSample", "ChatSample.Motion", "ChatSample.ActionPlanning", "DialogueMotionIntent",
        "ArdyDialogueMotionBridge", "RoleOutputChannels")]
    source_paths += ["Assets/Editor/" + name + ".cs" for name in (
        "ArdyConstraintRegression", "ArdyConstraintTempoRegression", "ArdySemanticMotionRegression",
        "ArdyDialogueMotionRegression", "ArdyConstraintPreviewWindow", "ArdyDialogueMotionWindow")]
    sources = [{"path": p, "sha256": sha(ROOT / p), "matchesIsolated": sha(ROOT / p) == sha(ISOLATED / p)} for p in source_paths]
    assert all(p["matchesIsolated"] for p in sources), "Final compiled source mismatch"
    for key, name in (("timingSourceSha256", "ArdyAdaptiveTiming"), ("playerSha256", "ArdyMotionPlayer"),
                      ("sessionSha256", "ArdyConstraintSession"), ("planSha256", "ArdyControlPlan")):
        assert tempo[key] == sha(ROOT / f"Assets/AIChatTookit/Scripts/Motion/{name}.cs"), "Tempo source changed"
    baseline = read(ROOT / "Tools/MotionAdapter/reports/palm-execution-v1/source-evidence.json")
    assets = baseline["unchangedAssets"]
    assert all(sha(ROOT / a["path"]) == a["sha256"] for a in assets), "Accepted resource or adapter changed"
    reports = [RUNTIME / "tempo-pass-v2/tempo-regression.json", RUNTIME / "tempo-pass-v2/constraint-regression.json",
        RUNTIME / "final-semantic-motion-regression.json", RUNTIME / "final-dialogue-motion-regression.json",
        RUNTIME / "final-live-http.json", RUNTIME / "first-unity-pass/playmode-regression.json",
        OUT / "previews/preview-manifest.json"]
    reports += [ROOT / "Tools/MotionAdapter/reports" / name for name in (
        "semantic-motion-planning-v1.json", "semantic-motion-planning-v2.json", "semantic-motion-planning-holdout-v2.json",
        "semantic-motion-planning-v2-source-audit.json", "semantic-motion-planning-holdout-v2-source-audit.json",
        "semantic-motion-protocol-freeze-v2.json", "adaptive-timing-math-v1.json",
        "tempo-execution-v1/measurement-basis-diagnosis-v1.json")]
    measured = []
    for case in tempo["cases"]:
        if case["fixture"] != "fixture-0" or not case["derivatives"]:
            continue
        measured.append({"name": case["name"], "seconds": case["resolved"]["actualSeconds"],
            "preparationSeconds": case["resolved"]["preparationSeconds"], "completed": case["completed"],
            "joints": case["derivatives"]})
    result = {
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "status": "execution_verified_semantic_planning_experimental_opt_in_only",
        "naturalnessAccepted": False, "generalizationAccepted": False,
        "defaultBehavior": "Previous GeneratedOutputContract and accepted basic gestures; plan requires explicit experimental opt-in.",
        "modelChange": "No model or adapter training/replacement; existing shared Qwen and ARDY service were reused.",
        "unity": {"tempoCases": len(tempo["cases"]), "fixtures": len(geometry["fixtures"]),
            "completedCurves": sum(c["completed"] for c in tempo["cases"]), "checks": geometry["checks"],
            "frames": geometry["actualFrames"], "nativePngFrames": sum(len(f["pngSha256"]) for f in geometry["fixtures"]),
            "semanticChecks": semantic["checks"], "dialogueChecks": dialogue["checks"], "legacyPlaybackChecks": legacy["checks"],
            "liveHttpChecks": live["checks"], "liveFirstWindowSeconds": live["firstWindowSeconds"],
            "liveAllWindowsSeconds": live["allWindowsSeconds"], "liveTimelineFrames": live["timelineFrames"]},
        "planning": {"developmentMatched": development["passedPlanningChecks"], "developmentCount": len(development["results"]),
            "developmentSourceCoverage": [dev_audit["sourceAuditsPassed"], dev_audit["sourceAuditsApplicable"]],
            "holdoutMatched": holdout["passedPlanningChecks"], "holdoutCount": len(holdout["results"]),
            "holdoutSourceCoverage": [hold_audit["sourceAuditsPassed"], hold_audit["sourceAuditsApplicable"]],
            "holdoutRuns": 1, "modelPlanningRequests": 46,
            "meaning": "Small public diagnostics, not an estimated generalization rate. Syntax, source coverage, execution and naturalness are separate outcomes."},
        "publicReplay": {"rawTagUnedited": True, "caseId": "zh-forward-greeting", "source": "development v2",
            "endIdleHonoredOnAllFixtures": all(c["publicEndHonored"] for c in tempo["cases"] if c["name"] == "public-qwen-original-tag-adaptive"),
            "limitation": "Correctly retains forward arms and selects natural tempo, but model overlocks inferred partner palms; quote membership does not prove explicit user intent."},
        "remaining": ["Model can omit explicit elbows/current-pose goals or confuse palm direction.",
            "A matched quote does not prove semantic entailment; missing or excessive locks remain an observed failure.",
            "ARDY free motion remains the existing six-second route, not improved by this procedural timing layer.",
            "Perceived naturalness and practical scene interactions need human review."],
        "validationSequence": "Full tempo and original-tag playback preceded an Inspector synchronization fix. Final real Chat/HTTP checks cover that fix; timing/player/session/plan hashes remain identical to the full tempo run.",
        "measured": measured, "sources": sources, "unchangedAssets": assets,
        "reports": [{"path": relative(p), "sha256": sha(p)} for p in reports]}
    (OUT / "acceptance.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": result["status"], "unity": result["unity"], "planning": result["planning"],
                      "sourcesMatched": len(sources), "unchangedAssets": len(assets)}, ensure_ascii=False))


if __name__ == "__main__":
    main()
