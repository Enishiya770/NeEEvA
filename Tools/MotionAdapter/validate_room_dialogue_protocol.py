"""Eight predeclared public Qwen room-planning probes; production C# parsing, no physical execution."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import time

from validate_dialogue_motion import ROOT
from validate_autonomous_dialogue_motion import visible_content
from validate_intent_consistency import health_snapshot, request_json, all_slots_idle

BUILD = ROOT / "Server/ARDY/runtime/room-dialogue-qwen-protocol"
MONO = Path("E:/Unity_Data/2022.3.22f1/Editor/Data/MonoBleedingEdge")
HARNESS = BUILD / "room-protocol.exe"
SOURCES = [
    "Tools/MotionAdapter/RoomDialogueProtocolProbe.cs",
    "Assets/AIChatTookit/Scripts/Chat/DialogueMotionIntent.cs",
    "Assets/AIChatTookit/Scripts/Chat/RoleOutputChannels.cs",
    "Assets/AIChatTookit/Scripts/Chat/SpeechText.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyControlPlan.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyActionPlan.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyMotionGoal.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyMotionCapabilities.cs",
]

# Expectations and all user inputs are frozen into the report before any model request.
# These are synthetic state facts using the current runtime bridge's field names.
READY = {"source": "Unity-room-runtime", "phase": "ready", "connected": True,
         "userAnchorValid": True, "bodyReserved": False, "actionId": "", "actualUserDistance": 3.0,
         "currentlyNearAndFacingUser": False, "reachedPlannedStandpoint": False,
         "reason": "房间移动已连接，可以请求走近用户或停止移动。", "desiredUserDistance": 1.0}
MOVING = {**READY, "phase": "moving", "bodyReserved": True, "actionId": "public-fixture:1:1",
          "reason": "正在沿已核实的同层路径走近用户，尚未到达。"}
CASES = [
    {"id": "zh-call-approach", "roomEnabled": True, "roomFacts": READY,
     "user": "过来我身边吧，走到我附近后面对我。", "expected": {"name": "approach"}},
    {"id": "ja-explicit-stop", "roomEnabled": True, "roomFacts": MOVING,
     "user": "そこで止まって。もう歩かなくていいよ。", "expected": {"name": "stop-moving"}},
    {"id": "en-ordinary-chat-no-motion", "roomEnabled": True, "roomFacts": READY,
     "user": "Let's just chat about your favorite colors. No movement or gestures needed.", "expected": {"name": None, "publicSpeechRequired": True}},
    {"id": "ja-question-during-approach", "roomEnabled": True, "roomFacts": MOVING,
     "user": "歩きながらでいいけど、朝ごはんはパンとご飯、どっちが好き？", "expected": {"name": None, "publicSpeechRequired": True}},
    {"id": "zh-upstairs-unreachable", "roomEnabled": True,
     "roomFacts": {**READY, "phase": "unreachable", "actionId": "public-fixture:2:1",
                   "reason": "用户位于楼上；当前仅支持同层移动，没有可达地面目标，楼梯未开放。"},
     "user": "我现在在楼上，你能走到我这里吗？", "expected": {"name": None, "publicSpeechRequired": True,
       "speechMustMatch": "(不能|无法|暂时|不支持|未开放|上不去|只能).*(楼|层|走|到|移动)|(楼梯|同层|楼上).*(不能|无法|未开放|限制|支持)"}},
    {"id": "en-room-disabled", "roomEnabled": False,
     "roomFacts": {"source": "Unity-room-runtime", "phase": "disconnected", "connected": False,
                   "userAnchorValid": False, "bodyReserved": False, "reason": "Room locomotion is not enabled in this session."},
     "user": "Please walk over to me and face me.", "expected": {"name": None, "publicSpeechRequired": True,
       "speechMustMatch": "(?i)(can't|cannot|unable|not (?:currently )?(?:able|enabled|available|supported)|couldn't|不能|无法|尚未|未启用)"}},
    {"id": "zh-forward-arms-remain-upper-body", "roomEnabled": True, "roomFacts": READY,
     "user": "请站在原地，双臂向自己正前方水平伸直并保持。不要走动。",
     "expected": {"name": "compose", "controlPlan": {"left": "forward", "right": "forward", "end": "hold"}}},
    {"id": "en-autonomous-company-opportunity", "roomEnabled": True, "roomFacts": READY,
     "user": "I'm over here in the same room and feeling a little uneasy. Your quiet company would help; choose what feels appropriate.",
     "expected": {"name": "approach"},
     "interpretation": "A fixed positive autonomy probe: approach demonstrates context-based choice; choosing speech alone fails this probe but is not a protocol violation, since autonomous movement is optional."},
]


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def run_harness(*args: str) -> str:
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(HARNESS), *args],
                            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if result.returncode:
        raise RuntimeError("Production parser harness failed: " + result.stdout + result.stderr)
    return result.stdout.strip().lstrip("\ufeff")


def compile_harness() -> dict:
    BUILD.mkdir(parents=True, exist_ok=True)
    command = [str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"),
               "/nologo", "/langversion:9", "/r:System.Web.Extensions.dll", "/out:" + str(HARNESS),
               *[str(ROOT / source) for source in SOURCES]]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    if result.returncode:
        raise RuntimeError("Harness compile failed: " + result.stdout + result.stderr)
    return {source: digest((ROOT / source).read_bytes()) for source in SOURCES}


def inspect(visible: str, case: dict, reply_path: Path) -> dict:
    reply_path.write_text(visible, encoding="utf-8")
    parsed = json.loads(run_harness("--inspect", str(reply_path), "enabled" if case["roomEnabled"] else "disabled"))
    tags = re.findall(r'<motion\b(?:"[^"<>]*"|\'[^\'<>]*\'|[^\'">])*>', visible, flags=re.I)
    errors = []
    if len(tags) > 1 or (tags and (not tags[0].endswith("/>") or not visible.endswith(tags[0]))):
        errors.append("Expected at most one trailing self-closing motion command")
    if parsed["rejection"] or parsed["malformedTool"]:
        errors.append(parsed["rejection"] or "Malformed tool output")
    expected = case["expected"]
    mismatches = {}
    if parsed["name"] != expected["name"]:
        mismatches["name"] = {"expected": expected["name"], "actual": parsed["name"]}
    if expected.get("publicSpeechRequired") and not parsed["speech"].strip():
        mismatches["publicSpeechRequired"] = {"expected": True, "actual": False}
    if expected.get("speechMustMatch") and not re.search(expected["speechMustMatch"], parsed["speech"], flags=re.S):
        mismatches["limitationExplanation"] = {"expected": "predeclared public-language limitation pattern", "actual": "not matched"}
    for key, value in expected.get("controlPlan", {}).items():
        actual = (parsed.get("controlPlan") or {}).get(key)
        if actual != value:
            mismatches["controlPlan." + key] = {"expected": value, "actual": actual}
    return {"caseId": case["id"], "publicOutput": visible, "motionTags": tags, "productionParser": parsed,
            "shapeErrors": errors, "mismatches": mismatches, "planningChecksPassed": not errors and not mismatches,
            "physicalExecution": "not attempted", "speechTruthAndNaturalness": "manual review required"}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--ardy-url", default="http://127.0.0.1:8093")
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    sources = compile_harness()
    contracts = {mode: run_harness("--contract", mode) for mode in ("enabled", "disabled")}
    canonical_cases = json.dumps(CASES, ensure_ascii=False, sort_keys=True).encode()
    report = {"schema": 1, "status": "frozen", "cases": CASES, "caseSha256": digest(canonical_cases),
              "sources": sources, "contractSha256": {mode: digest(contract.encode()) for mode, contract in contracts.items()},
              "scriptSha256": digest(Path(__file__).read_bytes()), "endpoint": args.url, "results": [], "modelRequests": 0,
              "requestSettings": {"temperature": 0, "max_tokens": 550, "enable_thinking": False},
              "scope": "Eight predeclared independent public synthetic prompts on the single existing shared Qwen, using actual generated+feedback+room contracts and production C# executable parser. This is not the full ChatSample conversation, persona, memory, microphone, TTS or Unity/ARDY execution path.",
              "limitations": "Single sample per fixed case; not a generalization rate. Synthetic runtime facts are supplied, not observed by Qwen. Autonomous choice is optional. Public language checks do not prove semantic truth. No service/model lifecycle changes, motion generation or private/reasoning output persisted.",
              "busyPolicy": "Require all slots explicitly idle before each request; defer on busy/unknown, never retry or change expected outcomes.",
              "frozenAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    with args.report.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
    print(json.dumps({"status": "frozen", "cases": len(CASES), "caseSha256": report["caseSha256"]}), flush=True)
    if not args.execute_model:
        return
    try:
        report["healthBefore"] = health_snapshot(args.url, args.ardy_url)
        if not all_slots_idle(report["healthBefore"]["slots"]):
            report["status"] = "deferred_busy_or_unknown"
            return
        report["status"] = "running"
        public_output_root = BUILD / args.report.stem
        public_output_root.mkdir(exist_ok=False)
        for case in CASES:
            slots = request_json(args.url.rstrip("/") + "/slots")
            if not all_slots_idle(slots):
                report["status"] = "deferred_busy_or_unknown"
                return
            room_contract = contracts["enabled" if case["roomEnabled"] else "disabled"]
            facts = json.dumps(case["roomFacts"], ensure_ascii=False)
            payload = {"messages": [{"role": "system", "content":
                "你是与用户在房间里面对面交流的虚拟角色，按用户所用语言简短自然回复。以下是公开合成测试，不含私人角色设定。\n" +
                room_contract + "\n[当前身体动作；程序事实]\n" + facts},
                {"role": "user", "content": case["user"]}], "temperature": 0, "max_tokens": 550,
                "chat_template_kwargs": {"enable_thinking": False}}
            started = time.perf_counter(); report["modelRequests"] += 1
            response = request_json(args.url.rstrip("/") + "/v1/chat/completions", payload)
            message = response["choices"][0]["message"]
            # Never print or persist reasoning fields, private blocks or the raw HTTP response.
            visible = visible_content(message.get("content") or "")
            row = inspect(visible, case, public_output_root / (case["id"] + ".public.txt"))
            row.update(model=response.get("model"), elapsedSeconds=round(time.perf_counter() - started, 3),
                       finishReason=response["choices"][0].get("finish_reason"))
            report["results"].append(row)
            args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            print(json.dumps({"caseId": case["id"], "planningChecksPassed": row["planningChecksPassed"],
                              "selected": row["productionParser"]["name"]}), flush=True)
        report["status"] = "completed"
        report["passedPlanningChecks"] = sum(row["planningChecksPassed"] for row in report["results"])
        report["healthAfter"] = health_snapshot(args.url, args.ardy_url)
    except Exception as error:
        # No response bodies, request contents or secret credentials are included in error logs.
        report.update(status="failed", error=f"{type(error).__name__}: {error}")
        raise
    finally:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"status": report["status"], "modelRequests": report["modelRequests"],
                          "passedPlanningChecks": report.get("passedPlanningChecks"), "report": str(args.report)}), flush=True)


if __name__ == "__main__":
    main()
