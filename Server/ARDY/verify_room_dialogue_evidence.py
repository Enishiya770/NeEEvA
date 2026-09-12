"""Verify the room-dialogue execution evidence without reclassifying model-quality failures."""
from pathlib import Path
from datetime import datetime, timezone
import hashlib
import json

ROOT = Path(__file__).resolve().parents[2]
ISOLATED = ROOT / "Server/ARDY/runtime/unity-naturalness-validation"
EVIDENCE = ROOT / "Server/ARDY/runtime/room-dialogue-v1"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    records = []
    for name in ("smoke-v4", "approach-v1", "legacy-walk-v1", "integration-v2"):
        path = EVIDENCE / name / "report.json"
        report = read(path)
        if report.get("status") != "passed":
            raise ValueError(f"Failed execution evidence: {name}")
        records.append({"path": path.relative_to(ROOT).as_posix(), "sha256": digest(path)})
    integration = read(EVIDENCE / "integration-v2/report.json")
    for key in ("connectedThroughRuntimeBridge", "vrmDisabledThroughout", "vrmRuntimeUninitialized",
                "ordinarySpeechPreservedMovement", "staleRoleCallbackRejected", "generationStopPreservedRoot",
                "playingStopPreservedRoot", "movingUserInvalidated"):
        if integration.get(key) is not True:
            raise ValueError(f"Integration did not verify {key}")
    if len(integration["cases"]) != 9 or not all(case["completed"] for case in integration["cases"]):
        raise ValueError("Incomplete room dialogue cases")
    for source in integration["sources"]:
        if source["sha256"].lower() != digest(ROOT / source["path"]):
            raise ValueError(f"Source changed after integration: {source['path']}")
    protocol = read(EVIDENCE / "smoke-v4/protocol-report.json")
    if not protocol.get("passed"):
        raise ValueError("ChatSample protocol checks failed")
    paths = sorted(set(ROOT.glob("Assets/AIChatTookit/Scripts/Motion/ArdyRoom*.cs")) |
                   set(ROOT.glob("Assets/AIChatTookit/Scripts/Chat/*Motion*.cs")) |
                   set(ROOT.glob("Assets/AIChatTookit/Scripts/Chat/ChatSample.ActionPlanning.cs")) |
                   set(ROOT.glob("Assets/AIChatTookit/Scripts/Chat/ArdyRoomDialogueBridge.cs")) |
                   set(ROOT.glob("Assets/AIChatTookit/Scripts/Player/PlayerCameraController.cs")) |
                   set(ROOT.glob("Assets/Editor/ArdyRoom*.cs")))
    compiled = []
    for path in paths:
        relative = path.relative_to(ROOT)
        if digest(path) != digest(ISOLATED / relative):
            raise ValueError(f"Isolated compiled source differs: {relative}")
        compiled.append({"path": relative.as_posix(), "sha256": digest(path)})
    review_path = ROOT / "Tools/MotionAdapter/reports/room-dialogue-qwen-v1-review.json"
    review = read(review_path)
    qwen_path = ROOT / review["sourceReport"]
    if digest(qwen_path) != review["sourceReportSha256"]:
        raise ValueError("Qwen public-output report changed after independent review")
    output = {
        "status": "passed_execution_checks", "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "scope": "Current-source isolated Unity execution checks; Qwen planning limitations are retained separately.",
        "reports": records, "compiledSourceCopies": compiled,
        "protocolChecks": protocol["checks"], "integrationChecks": integration["checks"],
        "qwenReview": {"path": review_path.relative_to(ROOT).as_posix(), "sha256": digest(review_path),
                       "passedFixedPlanningCases": review["automaticPlanningChecksPassed"], "total": review["total"]},
        "limitations": ["No full private chat ASR/TTS or hardware XR acceptance", "No continuous following or stairs",
                        "Model autonomous approach selection and factual self-description are not reliably solved",
                        "Foot contact, naturalness and standalone Player build need their own validation"],
    }
    (EVIDENCE / "verification.json").write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"status": output["status"], "sourceCopies": len(compiled), "cases": 9}))


if __name__ == "__main__":
    main()
