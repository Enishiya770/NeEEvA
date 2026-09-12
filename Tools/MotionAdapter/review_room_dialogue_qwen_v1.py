"""Attach explicit human/model review notes without changing frozen Qwen cases or results."""
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "Tools/MotionAdapter/reports/room-dialogue-qwen-v1.json"
TARGET = ROOT / "Tools/MotionAdapter/reports/room-dialogue-qwen-v1-review.json"


def main():
    original_bytes = SOURCE.read_bytes()
    source = json.loads(original_bytes)
    assert source["status"] == "completed" and source["modelRequests"] == 8
    assert source["caseSha256"] == "298aa4233a9fc98cdb76331606899185180e5f31b51616e4dda3787a8aa3b605"
    report = {
        "sourceReport": str(SOURCE.relative_to(ROOT)),
        "sourceReportSha256": hashlib.sha256(original_bytes).hexdigest(),
        "frozenCaseSha256": source["caseSha256"],
        "automaticPlanningChecksPassed": source["passedPlanningChecks"], "total": 8,
        "reviewMethod": "Read only the eight saved public outputs, declared synthetic runtime facts and production parser results. No re-prompting, execution, private persona or reasoning data. Expectations and original automatic results are unchanged.",
        "findings": [
            {"caseId": "zh-call-approach", "observation": "Selected the executable approach channel; public output was action-only. This proves submission syntax, not arrival or voice behavior."},
            {"caseId": "ja-explicit-stop", "observation": "Selected stop-moving, with Japanese speech. The language says it will wait until called; no actual stopping was performed by this probe."},
            {"caseId": "en-ordinary-chat-no-motion", "observation": "Fixed expectation was no tag; Qwen emitted none. This is an unnecessary upper-body stop command and remains a failed automatic case. It is not stop-moving and does not itself cancel the room route under the new dispatch contract. The comment about having no eyes reflects the synthetic prompt and is not a verified real-persona statement."},
            {"caseId": "ja-question-during-approach", "observation": "Answered an ordinary question without resending approach or issuing a stop, consistent with movement continuing through conversation. Actual movement continuity needs Unity tests."},
            {"caseId": "zh-upstairs-unreachable", "observation": "Explicitly described inability to go upstairs and same-level scope; no substitute movement or gesture was submitted."},
            {"caseId": "en-room-disabled", "observation": "Correctly declined walking and submitted no motion, so the frozen route test passed. However, 'I am already facing you' has no supporting room orientation observation in the disabled fixture. It is an unsupported pose claim and must not be presented as factual-feedback success."},
            {"caseId": "zh-forward-arms-remain-upper-body", "observation": "Correctly chose compose with both arms forward and hold rather than room movement. The user requested straight arms but leftBend/rightBend were omitted, yielding the production default 8 degrees. The fixed route/field test passed, but complete geometric fidelity was not established; no avatar execution was attempted."},
            {"caseId": "en-autonomous-company-opportunity", "observation": "Selected nod instead of the frozen positive probe's expected approach. Because autonomous approach is optional, this is failure to demonstrate that behavior in this example, not a malformed-command violation. 'I'm right here with you' can be reassurance but cannot prove arrival at the user's side; fixture distance was 3 m."}
        ],
        "assessment": "The sample supports explicit call/stop routing, same-level/disabled capability boundaries and no movement command on the mid-walk question. It does not establish reliable autonomous approach or fully observation-grounded speech. Keep those as visible limitations rather than claiming 6/8 is an end-to-end success rate.",
        "changesToFrozenCasesOrResults": False,
        "additionalModelRequests": 0
    }
    with TARGET.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
    print(json.dumps({"review": str(TARGET), "sourcePreserved": SOURCE.read_bytes() == original_bytes}))


if __name__ == "__main__":
    main()
