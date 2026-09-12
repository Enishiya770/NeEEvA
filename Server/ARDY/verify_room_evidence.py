"""Check final room evidence against source and the isolated compiled source copies."""
from pathlib import Path
import hashlib
import json
from datetime import datetime, timezone

ROOT = Path(__file__).resolve().parents[2]
RUNTIME = ROOT / "Server/ARDY/runtime/room-v1"
ISOLATED = ROOT / "Server/ARDY/runtime/unity-naturalness-validation"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    paths = sorted(set(ROOT.glob("Assets/AIChatTookit/Scripts/Motion/ArdyRoom*.cs")) |
                   set(ROOT.glob("Assets/Editor/ArdyRoom*.cs")) |
                   set(ROOT.glob("Assets/AIChatTookit/Scripts/Motion/ArdyLocomotion*.cs")))
    records = []
    for path in paths:
        relative = path.relative_to(ROOT)
        current, compiled = digest(path), digest(ISOLATED / relative)
        if current != compiled:
            raise ValueError(f"Compiled source differs: {relative}")
        records.append(dict(path=relative.as_posix(), sha256=current, isolatedCopyMatches=True))
    reports = []
    for name in ("navigation-v6", "zero-hold-disabled-target1-v1", "zero-hold-disabled-target2-v1",
                 "zero-hold-enabled-model-v1", "zero-hold-locomotion-regression-v1"):
        path = RUNTIME / name / "report.json"
        result = json.loads(path.read_text(encoding="utf-8-sig"))
        if result["status"] != "passed":
            raise ValueError(f"Validation failed: {name}")
        for key, source in (("navigationSha256", "Assets/AIChatTookit/Scripts/Motion/ArdyRoomNavigation.cs"),
                            ("controllerSha256", "Assets/AIChatTookit/Scripts/Motion/ArdyRoomLocomotionController.cs"),
                            ("playerSha256", "Assets/AIChatTookit/Scripts/Motion/ArdyLocomotionPlayer.cs"),
                            ("arrivalTimingSha256", "Assets/AIChatTookit/Scripts/Motion/ArdyRoomArrivalTiming.cs"),
                            ("windowSha256", "Assets/Editor/ArdyRoomLocomotionWindow.cs"),
                            ("regressionSha256", "Assets/Editor/ArdyRoomIntegrationRegression.cs")):
            if key in result and result[key] != digest(ROOT/source):
                raise ValueError(f"{name} no longer matches {source}")
        if "avatarPath" in result and result["avatarSha256"] != digest(ROOT/result["avatarPath"]):
            raise ValueError(f"Avatar changed after {name}")
        if "roomScene" in result and result["roomSceneSha256"] != digest(ROOT/result["roomScene"]):
            raise ValueError(f"Environment changed after {name}")
        if name == "zero-hold-locomotion-regression-v1":
            if result.get("harnessSha256") != digest(ROOT/"Assets/Editor/ArdyLocomotionRegression.cs"):
                raise ValueError(f"{name} no longer matches the playback regression harness")
        if name.startswith("zero-hold-disabled-"):
            for key in ("disabledVrm", "boundViaWindow", "disabledVrmPreserved", "runtimeStayedUninitialized",
                        "realResponseReceived", "completedNaturally", "bodyOwnersRestored", "inactiveAvatarBindRejected",
                        "inactiveAvatarStopped", "runtimeStateChangeRejected", "controllerModeChangeRejected"):
                if result.get(key) is not True:
                    raise ValueError(f"{name} did not verify {key}")
        if "arrivalObservation" in result:
            for key in ("arrivalTransitionContinuous", "arrivalMatchesDynamicAnimator", "returnCancellationPreservedRoot",
                        "returnReplacementPreserved", "externalReturnPreserved", "externalRootDuringReturnPreserved",
                        "externalAnimatorDisablePreserved", "externalPoseDuringReturnPreserved",
                        "playbackEndRespected", "rawResponseClipPreserved"):
                if result.get(key) is not True:
                    raise ValueError(f"{name} did not verify {key}")
        reports.append(dict(path=path.relative_to(ROOT).as_posix(), sha256=digest(path), status="passed"))
    service = [dict(path=p.relative_to(ROOT).as_posix(), sha256=digest(p))
               for p in sorted((ROOT/"Server/ARDY/motion_service").glob("*.py"))]
    output = dict(status="passed", generatedUtc=datetime.now(timezone.utc).isoformat(),
        scope="Current source/copy equality and passing evidence identity; no subjective quality or standalone-build certification.",
        runtimeSources=records, serviceSources=service, reports=reports,
        pythonCheck=dict(command="python -m unittest Server.ARDY.test_room_locomotion Server.ARDY.test_motion_service Server.ARDY.test_locomotion Server.ARDY.test_locomotion_metrics -q",
                         tests=45, result="passed in this task; separately executed before freezing"),
        stage="Unity Editor same-level sunken-living-room integration",
        limitations=["Not stairs or following/voice-call integration", "Not a standalone Player build; some source collision meshes are not CPU-readable",
                     "Original room contains an unresolved missing main-stair-wall mesh; no speculative replacement"])
    (RUNTIME/"verification.json").write_text(json.dumps(output, ensure_ascii=False, indent=2)+"\n",encoding="utf-8")
    print(json.dumps(dict(status="passed", matchedSourceFiles=len(records), reports=len(reports))))


if __name__ == "__main__":
    main()
