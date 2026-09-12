"""Eight frozen public live-Qwen room-routing probes; no Unity or motion execution."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import time

from validate_autonomous_dialogue_motion import visible_content
from validate_intent_consistency import all_slots_idle, health_snapshot, request_json
from validate_room_dialogue_protocol import ROOT, compile_harness, run_harness


READY = {"source": "Unity-room-runtime", "phase": "ready", "connected": True,
         "userAnchorValid": True, "bodyReserved": False, "actionId": "",
         "actualUserDistance": 3.0, "actualFacingErrorDegrees": 45.0,
         "desiredUserDistance": 1.0, "currentlyNearAndFacingUser": False,
         "reachedPlannedStandpoint": False, "reason": "房间移动已连接，可以请求走近用户或停止移动。"}
MOVING = {**READY, "phase": "moving", "bodyReserved": True,
          "actionId": "synthetic-review:1:1", "reason": "正在沿已核实的同层路径走近用户，尚未到达。"}
STALE_SINGING = {"source": "synthetic-historical-context", "historicalOnly": True,
                "previousUserRequest": "请把刚才那句歌词唱给我听。", "phase": "failed",
                "workActive": False, "reason": "旧演唱任务的录音引用不可用，未开始播放。"}
BUDGET_REASON = ("目标路径/转向及收尾需要约 12.30 秒，按生成窗口取整为 14.0 秒（280 帧）；"
                 "本次上限 10.0 秒（200 帧）。这是单次动作时长限制，请先选择更近的位置。 ")
FLOOR_REASON = ("用户与角色不在已验证的同层地面：角色地面 Y=-0.730 米，用户地面 Y=0.000 米，"
                "高差 0.730 米，上限 0.040 米。本阶段不能跨台阶或楼层走近。")

# Frozen before the first HTTP model request; every case gets one request, without repairs.
CASES = [
    {"id": "walk-towards-me", "user": "走向我。", "facts": READY,
     "expected": {"motion": "approach", "independent": True}},
    {"id": "try-walking-towards-me", "user": "试着向我走来。", "facts": READY,
     "expected": {"motion": "approach", "independent": True}},
    {"id": "come-here-with-stale-singing", "user": "来我身边。", "facts": READY,
     "historicalContext": STALE_SINGING, "expected": {"motion": "approach", "independent": True}},
    {"id": "stop-while-moving", "user": "停下。", "facts": MOVING,
     "expected": {"motion": "stop-moving", "independent": True}},
    {"id": "no-need-to-walk", "user": "不用走了。", "facts": MOVING,
     "historicalContext": STALE_SINGING, "expected": {"motion": "stop-moving", "independent": True}},
    {"id": "explain-target-budget", "user": "你刚才为什么没走过来？", 
     "facts": {**READY, "phase": "failed", "actionId": "synthetic-review:2:1", "reason": BUDGET_REASON},
     "expected": {"motion": None, "speechRequired": True, "speechPattern": r"(时长|时间|秒|单次).*(限制|上限|超过|超出)|(限制|上限|超过|超出).*(时长|时间|秒|单次)"}},
    {"id": "explain-measured-floor-rejection", "user": "你刚才为什么没走过来？",
     "facts": {**READY, "phase": "unreachable", "userAnchorValid": False,
               "actionId": "synthetic-review:3:1", "reason": FLOOR_REASON,
               "latestExecutionObservation": {"avatarGround": {"x": 0, "y": -.73, "z": 0},
                   "support": {"hitPoint": {"x": 0, "y": 0, "z": 3}, "hitPath": "SyntheticRoom/SupportSurface"}}},
     "expected": {"motion": None, "speechRequired": True, "speechPattern": r"高差|高度差|同层|同一层|地面.*高|高.*地面"}},
    {"id": "explicit-lyric-negative-control", "user": "请唱这句歌词“向我走来”，不要走动。", "facts": READY,
     "expected": {"forbiddenMotions": ["approach", "stop-moving"]},
     "scope": "Negative movement control only; singing fulfillment is not evaluated or executed."},
]


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def inspect(public: str, case: dict, path: Path) -> dict:
    path.write_text(public, encoding="utf-8")
    parsed = json.loads(run_harness("--inspect", str(path), "enabled"))
    errors = []
    expected = case["expected"]
    tags = re.findall(r"<motion\b[^<>]*>", public, flags=re.I)
    phases = re.findall(r'<speech\s+mode\s*=\s*[\"\']([^\"\']+)[\"\']\s*/>', public, flags=re.I)
    if len(tags) > 1:
        errors.append("multiple-motion-tags")
    if parsed["rejection"] or parsed["malformedTool"]:
        errors.append(parsed["rejection"] or "malformed-tool-output")
    if "motion" in expected and parsed["name"] != expected["motion"]:
        errors.append("motion-selection: expected=" + str(expected["motion"]) + " actual=" + str(parsed["name"]))
    if parsed["name"] in expected.get("forbiddenMotions", []):
        errors.append("negative-control-issued-room-motion")
    if expected.get("independent") and phases != ["independent"]:
        errors.append("room-action-requires-single-independent-speech-declaration")
    if case["id"] != "explicit-lyric-negative-control" and "after_action" in phases:
        errors.append("room-only-response-used-after-action")
    if expected.get("speechRequired") and not parsed["speech"].strip():
        errors.append("missing-factual-explanation")
    if expected.get("speechPattern") and not re.search(expected["speechPattern"], parsed["speech"], re.S):
        errors.append("missing-predeclared-root-cause-language")
    if "body_inspect" in public.lower():
        errors.append("unexpected-body-inspect-tool-or-claim")
    if case["id"] != "explicit-lyric-negative-control" and re.search(r"<(?:sing|sing_goal|singing_stop|clip_confirm)\b", public, re.I):
        errors.append("room-request-misrouted-to-singing")
    return {"caseId": case["id"], "publicOutput": public, "parser": parsed,
            "speechDeclarations": phases, "errors": errors, "checksPassed": not errors,
            "truthfulnessReview": "manual review required", "physicalExecution": "not attempted"}


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--ardy-url", default="http://127.0.0.1:8093")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    report_path = args.output / "report.json"
    sources = compile_harness()
    contract = run_harness("--contract", "enabled")
    (args.output / "production-contract.txt").write_text(contract, encoding="utf-8")
    cases_bytes = json.dumps(CASES, ensure_ascii=False, sort_keys=True).encode("utf-8")
    (args.output / "frozen-cases.json").write_bytes(cases_bytes)
    report = {"schema": 1, "status": "frozen", "frozenAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
              "caseSha256": digest(cases_bytes), "contractSha256": digest(contract.encode("utf-8")),
              "sources": sources, "scriptSha256": digest(Path(__file__).read_bytes()),
              "endpoint": args.url, "cases": CASES, "results": [], "modelRequests": 0,
              "settings": {"temperature": 0, "max_tokens": 650, "enable_thinking": False},
              "scope": "Eight fixed synthetic public routing probes on the existing shared Qwen using the current compiled production room contract and executable-channel parser. No Unity, ARDY motion generation, ASR, TTS, private persona or real user history.",
              "outputPolicy": "Retain model content after removing think/thought blocks; omit separate reasoning fields and full HTTP body. Public outputs remain uncorrected. Do not interpret a content hash as the hidden reasoning channel.",
              "limitations": "One sample per fixed case; not a success or generalization rate. Synthetic facts are supplied, not sensed. Negative lyric case checks absence of room movement only. Real runtime state changes and stop latency are not tested.",
              "busyPolicy": "Require all slots explicitly idle before each request; defer on busy or unknown, without retries."}

    def save() -> None:
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    save()
    print(json.dumps({"status": "frozen", "cases": len(CASES), "caseSha256": report["caseSha256"]}), flush=True)
    if not args.execute_model:
        return
    try:
        report["healthBefore"] = health_snapshot(args.url, args.ardy_url)
        if not all_slots_idle(report["healthBefore"]["slots"]):
            report["status"] = "deferred_busy_or_unknown"
            return
        report["status"] = "running"
        for case in CASES:
            if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
                report["status"] = "deferred_busy_or_unknown"
                return
            context = "你是与用户在房间里面对面交流的虚拟角色，用中文简短自然回应。以下都是公开合成测试状态，不含私人设定。\n" + contract
            if case.get("historicalContext"):
                context += "\n[历史上下文]\n" + json.dumps(case["historicalContext"], ensure_ascii=False)
            context += "\n[当前房间动作；程序事实]\n" + json.dumps(case["facts"], ensure_ascii=False)
            payload = {"messages": [{"role": "system", "content": context}, {"role": "user", "content": case["user"]}],
                       "temperature": 0, "max_tokens": 650, "chat_template_kwargs": {"enable_thinking": False}}
            (args.output / (case["id"] + ".request.json")).write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
            started = time.perf_counter()
            report["modelRequests"] += 1
            save()
            response = request_json(args.url.rstrip("/") + "/v1/chat/completions", payload)
            content = response["choices"][0]["message"].get("content") or ""
            public = visible_content(content)
            result = inspect(public, case, args.output / (case["id"] + ".public.txt"))
            result.update(model=response.get("model"), finishReason=response["choices"][0].get("finish_reason"),
                          elapsedSeconds=round(time.perf_counter() - started, 3),
                          contentSha256=digest(content.encode("utf-8")), contentRedacted=public != content.strip())
            report["results"].append(result)
            save()
            print(json.dumps({"caseId": case["id"], "checksPassed": result["checksPassed"], "motion": result["parser"]["name"]}), flush=True)
        report["healthAfter"] = health_snapshot(args.url, args.ardy_url)
        report["sourcesUnchangedAfterRequests"] = all(digest((ROOT / p).read_bytes()) == h for p, h in sources.items())
        report["modelIdentityUnchanged"] = report["healthBefore"]["modelSha256"] == report["healthAfter"]["modelSha256"]
        report["passedFixedChecks"] = sum(row["checksPassed"] for row in report["results"])
        report["status"] = "completed"
    except Exception as error:
        report.update(status="failed", errorType=type(error).__name__)
        raise
    finally:
        save()
        print(json.dumps({"status": report["status"], "modelRequests": report["modelRequests"], "report": str(report_path)}), flush=True)


if __name__ == "__main__":
    main()
