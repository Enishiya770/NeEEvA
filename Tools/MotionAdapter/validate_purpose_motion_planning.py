"""Public purpose-to-pose planning diagnostics on existing Qwen; never execute motion."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import time
import xml.etree.ElementTree as ET

from validate_dialogue_motion import ROOT, read_contract
from validate_autonomous_dialogue_motion import visible_content
from validate_compositional_planning import request_json
from validate_compose_planning import DEFAULTS as OLD_DEFAULTS

DEFAULTS = {**OLD_DEFAULTS, "leftPalm": "keep", "rightPalm": "keep", "leftBendAuto": False, "rightBendAuto": False}
HOLD_PLAN = {**DEFAULTS, "left": "forward", "right": "forward", "leftBend": 40,
             "rightBend": 40, "leftPalm": "down", "rightPalm": "down", "end": "hold"}
HOLD_FACT = {"phase": "holding", "previousRequestedPoseHeld": True, "controlPlan": HOLD_PLAN,
             "measuredConstraintState": {"observed": True, "goalsReached": True, "phase": "holding"}}
FAILED_PLAN = {**DEFAULTS, "left": "forward", "right": "forward", "leftBend": 0,
               "rightBend": 0, "leftPalm": "partner", "rightPalm": "partner", "joint": "wrists",
               "axis": "palm-normal", "end": "hold"}
FAILED_FACT = {"phase": "idle-Animator", "previousRequestedPoseHeld": False, "lastControlPlan": FAILED_PLAN,
               "lastControlObservation": {"observed": True, "goalsReached": False, "phase": "failed"},
               "lastControlError": "palm-unreachable: fully straight forward arms require wrist swing above the 80 degree engineering limit"}

# Frozen public development prompts: task purposes, not instructions to emit chosen XML.
# Expected fields score planning only; they do not assert that geometry is reachable.
CASES = [
    {"id": "zh-independent-greeting", "language": "zh", "user": "用右手跟我打个招呼吧。",
     "expected": {"name": "right-wave"}},
    {"id": "ja-independent-farewell", "language": "ja", "user": "そろそろ行くね。左手を振って見送って。",
     "expected": {"name": "left-wave"}},
    {"id": "zh-forward-greeting", "language": "zh", "user": "双臂向前平伸，用双手跟我打个招呼。",
     "expected": {"name": "compose", "left": "forward", "right": "forward", "leftPalm": "partner",
                  "rightPalm": "partner", "joint": "wrists", "axis": "palm-normal"}, "needsReachabilityChoice": True},
    {"id": "zh-single-forward-greeting-render-fixture", "language": "zh", "user": "右臂向前伸着，跟我打个招呼。",
     "expected": {"name": "compose", "left": "none", "right": "forward", "rightPalm": "partner",
                  "joint": "right-wrist", "axis": "palm-normal"}},
    {"id": "en-forward-farewell", "language": "en", "user": "Keep both arms extended in front of you and wave goodbye to me with both hands.",
     "expected": {"name": "compose", "left": "forward", "right": "forward", "leftPalm": "partner",
                  "rightPalm": "partner", "joint": "wrists", "axis": "palm-normal"}, "needsReachabilityChoice": True},
    {"id": "ja-static-palm-display", "language": "ja", "user": "両腕を前に伸ばして、両方の手のひらを私に見せて。そのままでいて。",
     "expected": {"name": "compose", "left": "forward", "right": "forward", "leftPalm": "partner",
                  "rightPalm": "partner", "joint": "none", "end": "hold"}},
    {"id": "en-explicit-palms-down", "language": "en", "user": "Extend both arms straight forward, palms down, and keep them still.",
     "expected": {"name": "compose", "left": "forward", "right": "forward", "leftBend": 0, "rightBend": 0,
                  "leftPalm": "down", "rightPalm": "down", "joint": "none", "end": "hold"}},
    {"id": "zh-down-palm-overrides-farewell-default", "language": "zh", "user": "双臂向前平伸，掌心一直朝下，用这个姿势挥手告别。",
     "expected": {"name": "compose", "left": "forward", "right": "forward", "leftPalm": "down",
                  "rightPalm": "down", "joint": "wrists"}},
    {"id": "zh-correction-preserves-forward-hold", "language": "zh", "fact": HOLD_FACT,
     "history": [{"role": "user", "content": "双臂向前平伸，稍微屈肘，先这样保持。"},
                 {"role": "assistant", "content": '<silent/><motion name="compose" left="forward" right="forward" leftBend="40" rightBend="40" leftPalm="down" rightPalm="down" end="hold"/>'}],
     "user": "手臂别换方向，我想让你在这个基础上像面对面打招呼那样挥手。",
     "expected": {"name": "compose", "left": ["current", "forward"], "right": ["current", "forward"],
                  "leftPalm": "partner", "rightPalm": "partner", "joint": "wrists", "axis": "palm-normal"}},
    {"id": "ja-single-side-palm-display", "language": "ja", "user": "左腕を横に伸ばして、左の手のひらを私に見せて。右腕は動かさないで。",
     "expected": {"name": "compose", "left": "outward", "right": "none", "leftPalm": "partner", "joint": "none"}},
    {"id": "en-ordinary-no-forced-motion", "language": "en", "user": "What is 17 plus 26? Please answer briefly.",
     "expected": {"name": None}},
    {"id": "zh-independent-nod-regression", "language": "zh", "user": "请轻轻点一次头。", "expected": {"name": "nod"}},
    {"id": "zh-open-upper-body-dance-regression", "language": "zh",
     "user": "双脚留在原地，随音乐自然地交替用双臂画弧线，带一点躯干起伏，做连贯的自由上身舞蹈。",
     "expected": {"name": "generate"}},
    {"id": "zh-failed-straight-goal-relaxed-to-greeting", "language": "zh", "fact": FAILED_FACT,
     "history": [{"role": "user", "content": "双臂朝前平伸，肘部完全伸直，掌心朝我挥手。"}],
     "user": "那就不用坚持刚才的伸直要求了，双臂还是朝前，自然地用两只手跟我打招呼就好。",
     "expected": {"name": "compose", "left": "forward", "right": "forward", "leftPalm": "partner",
                  "rightPalm": "partner", "joint": "wrists", "axis": "palm-normal"}, "needsReachabilityChoice": True},
    {"id": "en-failed-straight-goal-still-required", "language": "en", "fact": FAILED_FACT,
     "history": [{"role": "user", "content": "Hold both arms straight forward with zero elbow bend and wave at me, palms facing me."}],
     "user": "I still require completely straight elbows, arms forward, and palms facing me. Do not change those constraints.",
     "expected": {"name": [None, "none"]}, "requiresLimitationExplanation": True},
]


def inspect_reply(content: str, case: dict) -> dict:
    visible = visible_content(content)
    tags = re.findall(r'<motion\b(?:"[^"<>]*"|\'[^\'<>]*\'|[^\'">])*>', visible, flags=re.I)
    row = {"caseId": case["id"], "motionTags": tags, "motionSuccess": None,
           "dialogueAppropriateness": "manual_review_required", "shapeErrors": [], "mismatches": {}}
    actual = {"name": None}
    try:
        if len(tags) > 1:
            raise ValueError("Multiple motion tags")
        if tags:
            if not visible.endswith(tags[0]) or not tags[0].rstrip().endswith("/>"):
                raise ValueError("Motion is not a trailing self-closing tag")
            attributes = ET.fromstring(tags[0]).attrib
            actual = {**(DEFAULTS if attributes.get("name") == "compose" else {}), **attributes}
            for key, default in DEFAULTS.items():
                if key in actual and isinstance(default, (float, int)) and not isinstance(default, bool):
                    actual[key] = float(actual[key])
            if actual["name"] == "compose":
                for side in ("left", "right"):
                    actual[side + "BendAuto"] = (actual[side] not in {"none", "current"} and
                        actual[side + "Palm"] != "keep" and side + "Bend" not in attributes)
            if actual["name"] == "generate":
                text = actual.get("text", "")
                row["generatedCharacters"] = len(text)
                row["generatedEnglishWords"] = len(text.split())
                if set(attributes) != {"name", "text"} or not 0 < len(text) <= 240 or any(c in text for c in '<>"&\r\n'):
                    raise ValueError("Invalid or oversized generated description")
        for key, expected in case["expected"].items():
            permitted = expected if isinstance(expected, list) else [expected]
            if actual.get(key) not in permitted:
                row["mismatches"][key] = {"expected": expected, "actual": actual.get(key)}
        if case.get("needsReachabilityChoice"):
            # A flexible elbow choice is a planning observation, never a geometric pass.
            row["elbowFreedomOrFlexionSelected"] = all(actual.get(side + "BendAuto", False) or actual.get(side + "Bend", 0) >= 20 for side in ("left", "right"))
            if not row["elbowFreedomOrFlexionSelected"]:
                row["mismatches"]["reachabilityChoice"] = "The plan fixes near-straight elbows for a relaxed directional palm goal instead of leaving elbow-solving freedom"
    except (ET.ParseError, ValueError) as error:
        row["shapeErrors"].append(str(error))
    public = visible
    for tag in tags:
        public = public.replace(tag, "")
    row["actualParameters"] = actual
    row["publicReplyExcerpt"] = public.strip()[:320]
    row["planningChecksPassed"] = not row["shapeErrors"] and not row["mismatches"]
    if case.get("requiresLimitationExplanation"):
        row["limitationExplanation"] = "manual_review_required"
    return row


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--report", type=Path, default=ROOT / "Tools/MotionAdapter/reports/palm-purpose-planning-v1.json")
    parser.add_argument("--plan-only", action="store_true")
    parser.add_argument("--case-set", choices=("development", "new-expressions"), default="development")
    args = parser.parse_args()
    contract = read_contract()
    cases = CASES if args.case_set == "development" else json.loads(
        (ROOT / "Tools/MotionAdapter/data/palm-purpose-new-expressions-v1.json").read_text(encoding="utf-8"))["cases"]
    report = {"schema": 2, "status": "prepared", "contract": contract,
              "contractSha256": hashlib.sha256(contract.encode()).hexdigest(), "cases": cases, "caseSet": args.case_set,
              "caseSha256": hashlib.sha256(json.dumps(cases, ensure_ascii=False, sort_keys=True).encode()).hexdigest(),
              "results": [], "endpoint": args.url,
              "scope": "Real existing Qwen; public multilingual purpose requests with synthetic controller facts. No private persona, reasoning logs, ARDY request, Unity execution or model changes.",
              "limits": "Development planning diagnostics only. A matching palm/axis/pose is not geometric reachability, motion success, naturalness or a held-out final evaluation. C# syntax/validation is checked separately.",
              "schemaChange": "Version 2 reflects the new production parser: unspecified directional-palm elbows derive BendAuto=true. This changes interpretation, not the old report or its first-run result. Prompts are unchanged.",
              "busyPolicy": "Require all existing slots idle before every request; stop without queueing on busy or unknown status."}
    if args.plan_only:
        print(json.dumps({"status": "prepared", "cases": len(cases), "caseSha256": report["caseSha256"], "contractSha256": report["contractSha256"], "modelRequests": 0}))
        return
    args.report.parent.mkdir(parents=True, exist_ok=True)
    with args.report.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
    try:
        for case in cases:
            slots = request_json(args.url.rstrip("/") + "/slots")
            if not isinstance(slots, list) or not slots or any(s.get("is_processing") is not False for s in slots):
                report["status"] = "deferred_busy_or_unknown"
                return
            if args.case_set == "new-expressions" and not report["results"]:
                marker = ROOT / "Tools/MotionAdapter/reports/palm-purpose-new-expressions-v1.once.json"
                with marker.open("x", encoding="utf-8") as stream:
                    json.dump({"caseSha256": report["caseSha256"], "contractSha256": report["contractSha256"],
                               "report": str(args.report), "scope": "One model evaluation of the frozen four new expressions"}, stream, indent=2)
            fact = case.get("fact", {"phase": "idle-Animator", "previousRequestedPoseHeld": False})
            body = {"messages": [{"role": "system", "content": "你是与用户面对面交流的虚拟角色，简短自然回复。\n" + contract +
                     "\n[当前身体动作；程序事实]\n" + json.dumps(fact, ensure_ascii=False) +
                     "\nlastControlPlan/lastControlObservation/lastControlError只记录上次尝试，不是当前姿态。"},
                     *case.get("history", []), {"role": "user", "content": case["user"]}],
                    "temperature": 0, "max_tokens": 500, "chat_template_kwargs": {"enable_thinking": False}}
            start = time.perf_counter()
            response = request_json(args.url.rstrip("/") + "/v1/chat/completions", body)
            row = inspect_reply(response["choices"][0]["message"].get("content") or "", case)
            row.update(model=response.get("model"), elapsedSeconds=round(time.perf_counter() - start, 3))
            report["results"].append(row)
            print(json.dumps({"caseId": case["id"], "planningChecksPassed": row["planningChecksPassed"]}), flush=True)
        report["status"] = "completed"
        report["planningChecksPassed"] = all(row["planningChecksPassed"] for row in report["results"])
    except Exception as error:
        report["status"], report["error"] = "failed", f"{type(error).__name__}: {error}"
        raise
    finally:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        tags_path = args.report.with_suffix(".tags.txt")
        with tags_path.open("x", encoding="utf-8") as stream:
            stream.write("\n".join(tag for row in report["results"] for tag in row["motionTags"]) + "\n")
        print(json.dumps({"status": report["status"], "cases": len(report["results"]), "report": str(args.report), "actualTags": str(tags_path)}), flush=True)


if __name__ == "__main__":
    main()
