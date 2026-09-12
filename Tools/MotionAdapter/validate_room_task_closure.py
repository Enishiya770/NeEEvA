"""Fourteen frozen public room-task decision/review probes; one shared-Qwen request per case."""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

from validate_autonomous_dialogue_motion import visible_content
from validate_dialogue_motion import ROOT
from validate_intent_consistency import all_slots_idle, health_snapshot, request_json


MONO = Path("E:/Unity_Data/2022.3.22f1/Editor/Data/MonoBleedingEdge")
PROTOCOL = "Assets/AIChatTookit/Scripts/Chat/RoomTaskProtocol.cs"
NEWTONSOFT = "Assets/AIChatTookit/Plugin/Newtonsoft.Json.dll"
SOURCES = [PROTOCOL, NEWTONSOFT,
           "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.RoomTask.cs",
           "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.cs",
           "Assets/AIChatTookit/Scripts/Chat/ChatSample.RoomTask.cs",
           "Assets/AIChatTookit/Scripts/Chat/ArdyRoomDialogueBridge.cs",
           "Tools/MotionAdapter/validate_intent_consistency.py",
           "Tools/MotionAdapter/validate_autonomous_dialogue_motion.py"]

CURRENT = {
    "connected": True, "userAnchorValid": True, "bodyReserved": False,
    "currentTargetRevision": 8, "lastAttemptTargetRevision": 7,
    "lastAttemptAppliesToCurrentTarget": False, "attemptStatus": "no-attempt",
    "activeActionId": "", "currentlyNearAndFacingUser": False,
    "actualUserDistance": 3.0, "actualFacingErrorDegrees": 35.0,
    "desiredUserDistance": 1.2,
    "assessment": {"state": "reachable", "canPlanRoute": True,
                   "budgetChecked": False, "executionGuaranteed": False,
                   "routeLength": 1.9,
                   "reason": "当前位置附近存在经几何检查的站位；本目标尚未派发移动，生成时长与执行结果尚未验证。"},
}
SOFA_FAILURE = {
    "actionId": "public-closure:previous-sofa", "targetRevision": 7,
    "phase": "unreachable", "appliesToCurrentTarget": False,
    "reason": "旧目标的24个候选均未通过：起点0，终点5，途中19；最后一个候选终点身体胶囊碰到 SyntheticRoom/Sofa。未穷尽所有站位，未观察用户姿势。",
}
BUDGET_FAILURE = {
    "actionId": "public-closure:previous-budget", "targetRevision": 7,
    "phase": "failed", "appliesToCurrentTarget": False,
    "reason": "旧目标路线与转身需要12.3秒，按生成窗口取整14秒；单次上限10秒。本轮还没有尝试新目标。",
}


def json_text(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def decision_input(user: str, previous: dict | None = None, *, autonomous: bool = False,
                   same_target: bool = False, current: dict | None = None) -> dict:
    facts = copy.deepcopy(current if current is not None else CURRENT)
    history = copy.deepcopy(previous)
    if same_target:
        facts.update(currentTargetRevision=7, lastAttemptAppliesToCurrentTarget=True,
                     attemptStatus="attempted")
        if history:
            history["appliesToCurrentTarget"] = True
        facts["assessment"] = {"state": "unreachable", "canPlanRoute": False,
                               "budgetChecked": False, "executionGuaranteed": False,
                               "reason": "同一目标未变化，本次只读检查仍未找到通过检查的候选。"}
    dialogue = ([{"role": "user", "quotedText": "请走到我身边。"},
                 {"role": "assistant", "quotedText": "刚才那个位置的尝试没有通过检查。"}]
                if previous else [])
    return {"currentUserText": user, "autonomous": autonomous,
            "recentDialogue": json_text(dialogue), "currentFacts": facts,
            "historicalAndCurrentRoomFacts": json_text({"currentFacts": facts, "lastActionResult": history}),
            "autonomousContext": "当前没有新的用户请求、目标位置变化或其他主动接近动机。" if autonomous else ""}


def review_input(speech: str, *, dispatch: str = "attempt-submitted", phase: str = "preparing",
                 declared: str = "none", current: dict | None = None, previous: dict | None = None) -> dict:
    facts = copy.deepcopy(current if current is not None else CURRENT)
    action_id = "public-closure:current-approach" if dispatch == "attempt-submitted" else ""
    active = dispatch == "attempt-submitted" and phase in {"preparing", "moving"}
    facts.update(bodyReserved=active, activeActionId=action_id if active else "",
                 attemptStatus="active" if active else "attempted" if action_id else "no-attempt")
    if phase == "arrived":
        facts.update(actualUserDistance=1.2, actualFacingErrorDegrees=1.0,
                     currentlyNearAndFacingUser=True, lastAttemptTargetRevision=8,
                     lastAttemptAppliesToCurrentTarget=True)
    room = {"source": "Unity-room-runtime", "phase": phase, "actionId": action_id,
            "currentFacts": facts, "lastActionResult": previous,
            "executionStage": "generation" if active and phase == "preparing" else "completed" if phase == "arrived" else phase,
            "reason": "已接受本动作，正在生成，尚未到达。" if phase == "preparing" else
                      "本动作已完成且当前距离与朝向已核实。" if phase == "arrived" else "本目标没有新的行走派发。"}
    return {"intendedAction": "approach", "dispatch": dispatch, "actionId": action_id,
            "publicSpeech": speech, "requestedUserText": "请走到我身边。",
            "declaredMotion": declared, "currentRoomFacts": json_text(room)}


# These expectations are never sent to the model. No case filtering, repair or result selection.
CASES = [
    {"id": "new-position-after-sofa", "kind": "decision",
     "input": decision_input("现在这个位置呢？", SOFA_FAILURE),
     "expected": {"intent": "approach", "origin": "user"}},
    {"id": "closer-after-budget-failure", "kind": "decision",
     "input": decision_input("我靠近一点了，再试试看。", BUDGET_FAILURE),
     "expected": {"intent": "approach", "origin": "user"}},
    {"id": "user-walked-away-call-role", "kind": "decision",
     "input": decision_input("我自己往后走远了一点，你再走到我这里来。"),
     "expected": {"intent": "approach", "origin": "user"}},
    {"id": "quoted-lyric-no-room-task", "kind": "decision",
     "input": decision_input("请唱这句歌词“向我走来”，只是唱歌词，不要移动。"),
     "expected": {"intent": "none", "origin": "none"},
     "scope": "Negative room-task control; no singing capability is requested or tested by this probe."},
    {"id": "bow-is-not-approach", "kind": "decision",
     "input": decision_input("请原地向我鞠个躬。"),
     "expected": {"intent": "none", "origin": "none"},
     "scope": "Negative room-task control; no body generation or bow fulfillment is tested."},
    {"id": "ask-reason-read-only", "kind": "decision",
     "input": decision_input("刚才为什么没过来？只解释原因，先别再走。", SOFA_FAILURE),
     "expected": {"intent": "inspect", "origin": "user"}},
    {"id": "stop-active-room-motion", "kind": "decision",
     "input": decision_input("停下，不用再走了。", current={**CURRENT, "bodyReserved": True,
                   "activeActionId": "public-closure:already-moving", "attemptStatus": "active"}),
     "expected": {"intent": "stop-moving", "origin": "user"}},
    {"id": "autonomous-no-repeat-same-failure", "kind": "decision",
     "input": decision_input("", SOFA_FAILURE, autonomous=True, same_target=True),
     "expected": {"intent": "none", "origin": "none"}},
    {"id": "review-approach-cannot-say-retreat", "kind": "review",
     "input": review_input("我往后退一点，拉开和你的距离。", phase="moving"),
     "expected": {"verdict": "inconsistent", "allows": False}},
    {"id": "review-no-dispatch-cannot-claim-arrival", "kind": "review",
     "input": review_input("我这次已经走到你身边啦。", dispatch="not-submitted", phase="ready"),
     "expected": {"verdict": "inconsistent", "allows": False}},
    {"id": "review-sofa-does-not-prove-sitting", "kind": "review",
     "input": review_input("因为你正坐在沙发上，所以我不能走到你那里。", dispatch="not-submitted", phase="ready", previous=SOFA_FAILURE),
     "expected": {"verdict": "inconsistent", "allows": False}},
    {"id": "review-accepted-generation-trying-approach", "kind": "review",
     "input": review_input("我正在尝试走近你。"),
     "expected": {"verdict": "consistent", "allows": True}},
    {"id": "review-actual-arrival-currently-near", "kind": "review",
     "input": review_input("我已经走到你身边了。", phase="arrived"),
     "expected": {"verdict": "consistent", "allows": True}},
    {"id": "review-unplanned-head-shake-tag", "kind": "review",
     "input": review_input("我正在尝试走近你。", declared="shake-head"),
     "expected": {"verdict": "inconsistent", "allows": False}},
]

HARNESS_SOURCE = r'''using System;
using System.IO;
using Newtonsoft.Json;
public static class RoomTaskClosureProbe {
    public static int Main(string[] args) {
        if (args.Length == 1 && args[0] == "contracts") {
            Console.Write(JsonConvert.SerializeObject(new {
                decisionContract = RoomTaskProtocol.DecisionContract, reviewContract = RoomTaskProtocol.ReviewContract,
                decisionSchema = RoomTaskProtocol.DecisionSchema, reviewSchema = RoomTaskProtocol.ReviewSchema }));
            return 0;
        }
        if (args.Length == 4 && args[0] == "decision") {
            bool valid = RoomTaskProtocol.TryDecision(File.ReadAllText(args[1]), File.ReadAllText(args[2]),
                args[3] == "true", out var decision);
            Console.Write(JsonConvert.SerializeObject(new { valid, decision })); return 0;
        }
        if (args.Length == 2 && args[0] == "review") {
            Console.Write(JsonConvert.SerializeObject(new { allows = RoomTaskProtocol.ReviewAllows(File.ReadAllText(args[1])) }));
            return 0;
        }
        return 2;
    }
}'''


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def source_hashes() -> dict:
    return {source: digest((ROOT / source).read_bytes()) for source in SOURCES}


def run_harness(output: Path, *args: str) -> dict:
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(output / "parser/room-task-probe.exe"), *args],
                            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if result.returncode:
        raise RuntimeError("Compiled RoomTaskProtocol harness failed (exit " + str(result.returncode) + ")")
    return json.loads(result.stdout.strip().lstrip("\ufeff"))


def compile_harness(output: Path) -> dict:
    build = output / "parser"
    build.mkdir()
    (build / "RoomTaskClosureProbe.cs").write_text(HARNESS_SOURCE, encoding="utf-8")
    shutil.copyfile(ROOT / NEWTONSOFT, build / "Newtonsoft.Json.dll")
    command = [str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9",
               "/r:" + str(build / "Newtonsoft.Json.dll"), "/out:" + str(build / "room-task-probe.exe"),
               str(ROOT / PROTOCOL), str(build / "RoomTaskClosureProbe.cs")]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (build / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("RoomTaskProtocol compile failed; inspect parser/compile.log")
    return run_harness(output, "contracts")


def reject_duplicates(pairs: list[tuple[str, object]]) -> dict:
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError("duplicate-property")
        value[key] = item
    return value


def strict_json(text: str) -> object:
    def invalid_constant(_: str) -> None:
        raise ValueError("non-json-number")
    return json.loads(text, object_pairs_hook=reject_duplicates, parse_constant=invalid_constant)


def strict_public(text: str, kind: str) -> tuple[dict | None, list[str]]:
    try:
        value = strict_json(text)
    except (ValueError, TypeError):
        return None, ["not-strict-public-json"]
    fields = {"intent", "origin", "evidence"} if kind == "decision" else {"verdict", "reason"}
    if not isinstance(value, dict) or set(value) != fields or any(not isinstance(v, str) for v in value.values()):
        return None, ["closed-object-fields-or-types"]
    errors = []
    if kind == "decision":
        if value["intent"] not in {"none", "approach", "stop-moving", "inspect"} or value["origin"] not in {"none", "user", "autonomous"}:
            errors.append("invalid-decision-enum")
        if len(value["evidence"].encode("utf-16-le")) // 2 > 160:
            errors.append("evidence-exceeds-production-utf16-bound")
    else:
        if value["verdict"] not in {"consistent", "inconsistent", "uncertain"}:
            errors.append("invalid-review-verdict")
        if len(value["reason"].encode("utf-16-le")) // 2 > 160:
            errors.append("reason-exceeds-production-utf16-bound")
    return value, errors


def inspect_response(output: Path, case: dict, envelope: dict, http_status: int) -> dict:
    errors = []
    choices = envelope.get("choices")
    choice = choices[0] if isinstance(choices, list) and len(choices) == 1 and isinstance(choices[0], dict) else {}
    message = choice.get("message") if isinstance(choice.get("message"), dict) else {}
    content = message.get("content")
    if http_status != 200 or not choice or message.get("role") != "assistant":
        errors.append("invalid-production-envelope")
    if choice.get("finish_reason") != "stop":
        errors.append("incomplete-output")
    refusal = message.get("refusal")
    if message.get("tool_calls") is not None or message.get("function_call") is not None or \
            (refusal is not None and (not isinstance(refusal, str) or refusal != "")):
        errors.append("unexpected-tool-or-refusal-envelope")
    if not isinstance(content, str) or not content.strip():
        errors.append("empty-or-invalid-public-content")
        content = ""
    public = visible_content(content)
    # Hidden blocks are never persisted. Their presence is a failure, not a repair before parsing.
    redacted = public != content.strip()
    if redacted:
        errors.append("reasoning-block-in-public-content")
    public_path = output / (case["id"] + ".public.txt")
    public_path.write_text(public if redacted else content, encoding="utf-8")
    parsed, shape_errors = strict_public(content, case["kind"])
    errors.extend(shape_errors)
    production = {"valid": False} if case["kind"] == "decision" else {"allows": False}
    if not redacted:
        if case["kind"] == "decision":
            user_path = output / (case["id"] + ".user.txt")
            user_path.write_text(case["input"]["currentUserText"], encoding="utf-8")
            production = run_harness(output, "decision", str(public_path), str(user_path),
                                     str(case["input"]["autonomous"]).lower())
        else:
            production = run_harness(output, "review", str(public_path))
    evidence_exact = None
    if case["kind"] == "decision":
        if not production.get("valid"):
            errors.append("production-decision-rejected")
        if parsed and parsed.get("origin") == "user":
            evidence_exact = bool(parsed["evidence"].strip()) and parsed["evidence"] in case["input"]["currentUserText"]
            if not evidence_exact:
                errors.append("evidence-not-exact-current-user-quote")
    release_allowed = case["kind"] == "review" and not errors and production.get("allows") is True
    # Runtime expectations use only the actual C# result. Preserve independent raw-contract
    # deviations so a safe canonicalization cannot make the model's original fields look correct.
    canonical = production.get("decision") if case["kind"] == "decision" and production.get("valid") else None
    runtime_value = canonical if case["kind"] == "decision" else parsed
    mismatches = {}
    raw_mismatches = {}
    for field, expected in case["expected"].items():
        actual = release_allowed if field == "allows" else (runtime_value or {}).get(field)
        if actual != expected:
            mismatches[field] = {"expected": expected, "actual": actual}
        if field != "allows" and (parsed or {}).get(field) != expected:
            raw_mismatches[field] = {"expected": expected, "actual": (parsed or {}).get(field)}
    raw_errors = list(errors)
    if case["kind"] == "decision" and parsed and parsed.get("intent") == "none":
        for field, expected in (("origin", "none"), ("evidence", "")):
            if parsed.get(field) != expected:
                raw_mismatches[field] = {"expected": expected, "actual": parsed.get(field)}
                raw_errors.append("raw-none-contract-requires-" + field + "=" + repr(expected))
    canonicalization = {}
    if canonical and parsed:
        canonicalization = {field: {"raw": parsed.get(field), "canonical": canonical.get(field)}
                            for field in ("intent", "origin", "evidence") if parsed.get(field) != canonical.get(field)}
    return {"caseId": case["id"], "kind": case["kind"], "publicOutput": public if redacted else content,
            "publicContentSha256": digest(content.encode("utf-8")), "contentRedacted": redacted,
            "finishReason": choice.get("finish_reason"), "model": envelope.get("model"),
            "parsed": parsed if not redacted else None, "productionParser": production,
            "canonicalDecision": canonical, "canonicalizationApplied": canonicalization,
            "rawContractErrors": raw_errors, "rawContractMismatches": raw_mismatches,
            "rawContractChecksPassed": not raw_errors and not raw_mismatches,
            "releaseAllowedAfterEnvelopeAndParser": release_allowed if case["kind"] == "review" else None,
            "evidenceIsExactCurrentUserQuote": evidence_exact, "errors": errors, "mismatches": mismatches,
            "checksPassed": not errors and not mismatches,
            "unsafeReviewRelease": case["kind"] == "review" and not case["expected"]["allows"] and release_allowed,
            "physicalExecution": "not attempted", "manualMeaningReview": "required; a verdict is not independent ground truth"}


def build_payload(case: dict, contracts: dict, model: str) -> dict:
    kind = case["kind"]
    schema = contracts[kind + "Schema"]
    return {"model": model, "stream": False, "enable_thinking": False, "temperature": 0, "max_tokens": 240,
            "response_format": {"type": "json_object", "schema": json.loads(schema)},
            "messages": [{"role": "system", "content": contracts[kind + "Contract"] + "\n" + schema},
                         {"role": "user", "content": json_text(case["input"])}],
            "chat_template_kwargs": {"enable_thinking": False}, "id_slot": 1}


def completion(url: str, payload: dict) -> tuple[dict, int, str]:
    request = urllib.request.Request(url.rstrip("/") + "/v1/chat/completions", json_text(payload).encode("utf-8"),
                                     {"Content-Type": "application/json", "Authorization": "Bearer ollama"})
    # Production RoomTask uses 12 seconds. A timeout is recorded once; never resubmit the case.
    with urllib.request.urlopen(request, timeout=12) as response:
        raw = response.read()
        envelope = strict_json(raw.decode("utf-8"))
        if not isinstance(envelope, dict):
            raise ValueError("invalid-envelope-object")
        return envelope, response.status, digest(raw)


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--ardy-url", default="http://127.0.0.1:8093")
    parser.add_argument("--model", default="qwen36")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    args.output = args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    sources = source_hashes()
    contracts = compile_harness(args.output)
    if source_hashes() != sources:
        raise RuntimeError("Sources changed while compiling the production parser; freeze again before any model request")
    frozen_cases = json.dumps(CASES, ensure_ascii=False, sort_keys=True).encode("utf-8")
    (args.output / "frozen-cases.json").write_bytes(frozen_cases)
    (args.output / "production-contracts.json").write_text(json.dumps(contracts, ensure_ascii=False, indent=2), encoding="utf-8")
    payloads = {case["id"]: build_payload(case, contracts, args.model) for case in CASES}
    for case_id, payload in payloads.items():
        (args.output / (case_id + ".request.json")).write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    report = {"schema": 1, "status": "frozen", "frozenAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
              "cases": CASES, "caseSha256": digest(frozen_cases), "sources": sources,
              "scriptSha256": digest(Path(__file__).read_bytes()),
              "contractSha256": {k: digest(v.encode("utf-8")) for k, v in contracts.items()},
              "harnessSha256": digest(HARNESS_SOURCE.encode("utf-8")),
              "endpoint": args.url, "modelName": args.model, "modelRequests": 0, "results": [],
              "requestSettings": {"responseFormat": "json_object with production schema", "temperature": 0,
                                  "max_tokens": 240, "thinking": False, "id_slot": 1, "timeoutSeconds": 12},
              "scope": "14 frozen synthetic public inputs on the existing shared Qwen. Compiled production contracts and C# decision/review parser; local provider wire shape. No Unity session, persona, private history, ARDY generation, ASR, TTS, motion dispatch or live-world sensing.",
              "limitations": "One request per case, no retries/repairs/filtering. Fixed semantic checks are not a generalization rate. An uncertain rejection may be safe yet fail the expected verdict. No full chat pipeline, latency-to-voice or physical outcome claim.",
              "runtimeCheckBasis": "Decision expectations compare the compiled C# canonical decision. Separate rawContract checks retain original model field deviations, including none with origin=user and quoted evidence; normalization is never counted as raw-contract compliance.",
              "busyPolicy": "All three shared slots must be explicitly idle before each request. Busy/unknown defers the remainder without resubmitting completed cases.",
              "outputPolicy": "Retain unmodified public message JSON. Never persist separate reasoning fields or raw HTTP envelope. If hidden blocks occur in content, redact their text but fail the original content; never parse a repaired response as accepted."}
    report_path = args.output / "report.json"

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
            if source_hashes() != sources or digest(Path(__file__).read_bytes()) != report["scriptSha256"]:
                report["status"] = "deferred_sources_changed"
                return
            if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
                report["status"] = "deferred_busy_or_unknown"
                return
            started = time.perf_counter()
            report["modelRequests"] += 1
            save()
            try:
                envelope, status, envelope_hash = completion(args.url, payloads[case["id"]])
                row = inspect_response(args.output, case, envelope, status)
                row["envelopeSha256"] = envelope_hash
            except (urllib.error.URLError, TimeoutError, ValueError) as error:
                row = {"caseId": case["id"], "kind": case["kind"], "checksPassed": False,
                       "errorType": type(error).__name__, "httpStatus": getattr(error, "code", None),
                       "errors": ["single-request-transport-or-envelope-failure"], "retryAttempted": False}
            row["elapsedSeconds"] = round(time.perf_counter() - started, 3)
            report["results"].append(row)
            save()
            print(json.dumps({"caseId": row["caseId"], "checksPassed": row["checksPassed"],
                              "rawContractChecksPassed": row.get("rawContractChecksPassed")}), flush=True)
        report["healthAfter"] = health_snapshot(args.url, args.ardy_url)
        report["sourcesUnchangedAfterRequests"] = source_hashes() == sources
        report["modelIdentityUnchanged"] = report["healthBefore"]["modelSha256"] == report["healthAfter"]["modelSha256"]
        report["passedFixedChecks"] = sum(row["checksPassed"] for row in report["results"])
        report["passedRawContractChecks"] = sum(row.get("rawContractChecksPassed") is True for row in report["results"])
        report["canonicalizedDecisionCases"] = [row["caseId"] for row in report["results"] if row.get("canonicalizationApplied")]
        report["unsafeReviewReleases"] = sum(row.get("unsafeReviewRelease") is True for row in report["results"])
        report["status"] = "completed"
    except Exception as error:
        report.update(status="failed", errorType=type(error).__name__)
        raise
    finally:
        save()
        print(json.dumps({"status": report["status"], "modelRequests": report["modelRequests"], "report": str(report_path)}), flush=True)


if __name__ == "__main__":
    main()
