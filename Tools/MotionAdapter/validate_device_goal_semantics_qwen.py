"""Frozen device-goal semantic probes using compiled production contracts/parser.

Default mode prepares evidence and runs local parser guards only. --execute-model
sends each frozen case once to the existing model, without Unity, tool execution,
answer selection, retries, alternate endpoints, or service startup.
"""
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
from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import MONO, NEWTONSOFT, PROTOCOL, ROOT, strict_json

PROVIDER = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.RoomDeviceGoalReview.cs"
PLANNER = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.RoomTask.cs"
DEFAULT_LOG = ROOT / "Server/ARDY/runtime/long-task-diagnosis-20261008-171143/Editor.log"
HARNESS = r'''using System;
using System.IO;
using Newtonsoft.Json;
public static class DeviceGoalSemanticProbe {
    public static int Main(string[] args) {
        if (args.Length == 1 && args[0] == "contracts") {
            Console.Write(JsonConvert.SerializeObject(new {
                contract=RoomTaskProtocol.DeviceGoalReviewContract,
                schema=RoomTaskProtocol.DeviceGoalReviewSchema,
                updateSchema=RoomTaskProtocol.DeviceGoalUserUpdateReviewSchema })); return 0;
        }
        if (args.Length == 2 && (args[0] == "review" || args[0] == "update")) {
            RoomTaskProtocol.DeviceGoalReview review;
            bool valid=args[0] == "update" ? RoomTaskProtocol.TryDeviceGoalUserUpdateReview(File.ReadAllText(args[1]),out review)
                : RoomTaskProtocol.TryDeviceGoalReview(File.ReadAllText(args[1]),out review);
            Console.Write(JsonConvert.SerializeObject(new {valid, review})); return 0;
        }
        return 2;
    }
}'''


def digest(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def write(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def source_hashes() -> dict:
    return {name: digest((ROOT / name).read_bytes()) for name in (PROTOCOL, NEWTONSOFT, PROVIDER, PLANNER)}


def harness(output: Path, *args: str) -> dict:
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(output / "parser/probe.exe"), *args],
                            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if result.returncode:
        raise RuntimeError("Production device-goal parser probe failed: " + str(result.returncode))
    return strict_json(result.stdout.strip().lstrip("\ufeff"))


def compile_harness(output: Path) -> dict:
    build = output / "parser"
    build.mkdir()
    (build / "Probe.cs").write_text(HARNESS, encoding="utf-8")
    shutil.copyfile(ROOT / NEWTONSOFT, build / "Newtonsoft.Json.dll")
    command = [str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9",
               "/r:" + str(build / "Newtonsoft.Json.dll"), "/out:" + str(build / "probe.exe"),
               str(ROOT / PROTOCOL), str(build / "Probe.cs")]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (build / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Production protocol compile failed; inspect parser/compile.log")
    return harness(output, "contracts")


def saved_evidence(log: Path) -> tuple[dict, dict, dict]:
    """Extract only engine-authored goal facts; LF line numbers match rg -n."""
    raw = log.read_bytes()
    closed = opened = None
    closed_line = opened_line = None
    for number, line in enumerate(raw.split(b"\n"), 1):
        text = line.decode("utf-8-sig", errors="strict").rstrip("\r")
        if text.startswith("[Vision/Goal] satisfied "):
            value = strict_json(text.split("[Vision/Goal] satisfied ", 1)[1])
            if value.get("desiredState") == "off" and value.get("completionEvidenceValid") is True:
                closed, closed_line = value, number
        elif text.startswith("[Vision/Goal] registered "):
            value = strict_json(text.split("[Vision/Goal] registered ", 1)[1])
            if closed is not None and value.get("desiredState") == "on":
                opened, opened_line = value, number
                break
    if closed is None or opened is None:
        raise ValueError("Require saved actual successful off proof followed by a registered on request.")
    proof = closed["completionEvidence"]
    entity = closed["boundEntityId"]
    if not entity or proof.get("entityId") != entity or proof.get("actual", {}).get("on") is not False:
        raise ValueError("Saved engine proof does not establish the same bound device was off.")
    history = {"goalId": closed["goalId"], "entityId": entity,
               "originalRequest": closed["originalRequest"], "requestQuote": closed["requestQuote"],
               "targetDescription": closed["targetDescription"], "desiredState": "off",
               "identitySource": "saved-engine-semantically-matched-device-completion",
               "verified": True, "actionId": proof["actionId"], "resultCaptureId": proof["resultCaptureId"],
               "observedUtc": proof["observedUtc"], "actualReadback": proof["actual"],
               "scope": "Historical same-device referent only; not a current image, state or execution permission."}
    standing = {"goalId": opened["goalId"], "originalRequest": opened["originalRequest"],
                "requestQuote": opened["requestQuote"], "targetDescription": opened["targetDescription"],
                "desiredState": "on", "status": "suspended", "authorized": True,
                "boundEntityId": entity, "bindingSourceGoalId": closed["goalId"],
                "scope": "Frozen semantic test of retaining this previously reviewed unfinished goal; no live transaction or action."}
    return history, standing, {"log": str(log), "sha256": digest(raw), "successfulOffLine": closed_line,
                               "registeredOnLine": opened_line,
                               "scope": "Saved engine proof plus an explicit synthetic retained-goal update context, not live world state."}


def cases(history: dict, standing: dict) -> list[dict]:
    rows = []

    def update(name: str, text: str, relation: str, *, clarify: bool = False) -> None:
        goal = copy.deepcopy(standing)
        if clarify:
            goal.update(status="waiting_clarification", clarificationQuestion="你指的是刚才我实际关掉的那盏，还是另一盏？")
        rows.append({"id": name, "input": {"mode": "goal-user-update", "currentUserText": text,
                    "latestUserText": text, "proposal": None, "existingGoal": goal,
                    "verifiedDeviceHistory": [copy.deepcopy(history)]},
                    "expected": {"verdict": "approved", "relation": relation,
                                 "goalId": goal["goalId"] if relation in {"continue", "clarify", "cancel"} else None,
                                 "entityId": history["entityId"] if relation == "clarify" else None}})

    update("reminder-not-opened", "你还没有把灯打开啊。", "continue")
    update("direct-first-open", "你先把灯打开呀。", "continue")
    update("find-it-yourself", "你自己找找呀。", "continue")
    update("continue-not-moving", "继续啊，怎么不动啊？", "continue")
    update("act-instead-of-apology", "与其说对不起，不如说想办法去完成开灯的那个任务。", "continue")
    update("ask-why-failed", "为什么你打不开那盏灯呢？", "unrelated")
    update("ordinary-chat", "你今天过得怎么样？", "unrelated")
    update("parallel-song-request", "给我唱首歌吧。", "unrelated")
    update("parallel-user-approach", "你先来我身边一下。", "unrelated")
    update("clarify-same-proven-device", "就是你刚才关掉的那盏，不是另一盏。", "clarify", clarify=True)
    update("explicit-cancel", "不用开了，取消这个开灯任务。", "cancel")
    update("replace-with-another-device", "别开原来那盏了，改成把另一盏打开。", "new")

    def proposal(name: str, text: str, target: str, expected: str, *, known: bool = False) -> None:
        rows.append({"id": name, "input": {"mode": "new-proposal", "currentUserText": text,
                    "proposal": {"operation": "set", "requestQuote": text,
                                 "targetDescription": target, "desiredState": "on"},
                    "existingGoal": {"status": "none"}, "verifiedDeviceHistory": [copy.deepcopy(history)]},
                    "expected": {"verdict": expected, "relation": "new" if expected == "approved" else None,
                                 "goalId": history["goalId"] if known else None,
                                 "entityId": history["entityId"] if known else None,
                                 "mustNotBindHistoricalEntity": not known}})

    proposal("reopen-actual-closed-device", "把刚才你关掉的那盏灯重新打开。", "刚才你关掉的那盏灯", "approved", known=True)
    proposal("explicit-other-device", "把窗边那盏还没操作过的灯打开。", "窗边还没操作过的灯", "approved")
    proposal("negative-request", "不要把刚才关掉的那盏灯打开。", "刚才关掉的那盏灯", "rejected")
    proposal("reported-someone-elses-command", "他刚才对别人说把那盏灯打开，我只是转述。", "刚才关掉的那盏灯", "rejected")
    proposal("inspection-only", "只看看那盏灯是什么，不要开关它。", "刚才关掉的那盏灯", "rejected")
    ambiguous = copy.deepcopy(history)
    ambiguous.update(goalId="fixture-other-proven-goal", entityId="fixture-other-proven-entity",
                     targetDescription="此前关掉的另一盏灯", originalRequest="把另一盏灯关掉。",
                     identitySource="synthetic-verified-second-device-ambiguity-control")
    if "actualReadback" in ambiguous:
        ambiguous["actualReadback"]["entityId"] = ambiguous["entityId"]
    item = copy.deepcopy(next(row for row in rows if row["id"] == "reopen-actual-closed-device"))
    item.update(id="two-proven-devices-ambiguous", expected={"verdict": "uncertain", "mustNotBindAnyEntity": True})
    item["input"]["currentUserText"] = "把之前关掉的一盏灯打开。"
    item["input"]["proposal"].update(requestQuote=item["input"]["currentUserText"], targetDescription="之前关掉的一盏灯")
    item["input"]["verifiedDeviceHistory"].append(ambiguous)
    rows.append(item)
    return rows


def payload(case: dict, contracts: dict, model: str) -> dict:
    schema_text = contracts["updateSchema" if case["input"]["mode"] == "goal-user-update" else "schema"]
    return {"model": model, "stream": False, "enable_thinking": False, "temperature": 0,
            "max_tokens": 384, "response_format": {"type": "json_object", "schema": strict_json(schema_text)},
            "messages": [{"role": "system", "content": contracts["contract"] + "\n" + schema_text},
                         {"role": "user", "content": json.dumps(case["input"], ensure_ascii=False, separators=(",", ":"))}],
            "chat_template_kwargs": {"enable_thinking": False}, "id_slot": 1}


def inspect(case: dict, decision: dict) -> list[str]:
    errors = []
    expected = case["expected"]
    for name in ("verdict", "relation"):
        if expected.get(name) is not None and decision.get(name) != expected[name]:
            errors.append("expected-" + name + ":" + str(expected[name]))
    for key, output_key in (("goalId", "referentGoalId"), ("entityId", "referentEntityId")):
        if expected.get(key) is not None and decision.get(output_key) != expected[key]:
            errors.append("expected-" + output_key)
    known = case["input"]["verifiedDeviceHistory"]
    goal = case["input"].get("existingGoal", {})
    goal_id, entity_id = decision.get("referentGoalId", ""), decision.get("referentEntityId", "")
    allowed_pairs = {(item["goalId"], item["entityId"]) for item in known}
    if goal.get("goalId") and goal.get("boundEntityId"):
        allowed_pairs.add((goal["goalId"], goal["boundEntityId"]))
    if entity_id and (goal_id, entity_id) not in allowed_pairs:
        errors.append("invented-or-cross-paired-referent")
    if goal_id and goal_id not in {item[0] for item in allowed_pairs}:
        errors.append("invented-goal-referent")
    if expected.get("mustNotBindAnyEntity") and entity_id:
        errors.append("ambiguous-device-must-not-bind")
    # A rejected proposal can correctly explain which historical entity the user
    # mentioned. Its IsApproved=false cannot register a binding or authorize a
    # write. The prohibited inheritance is an approved *other* device proposal.
    if expected.get("mustNotBindHistoricalEntity") and decision.get("verdict") == "approved" and entity_id:
        errors.append("different-or-unauthorized-request-must-not-inherit")
    return errors


def local_checks(output: Path) -> list[dict]:
    checks = [
        ("legacy-readable", "review", '{"verdict":"approved","reason":"legacy"}', True),
        ("legacy-cannot-resume", "update", '{"verdict":"approved","reason":"legacy"}', False),
        ("complete-update-readable", "update", '{"verdict":"approved","reason":"x","relation":"continue","referentGoalId":"g","referentEntityId":""}', True),
        ("missing-referent-field", "update", '{"verdict":"approved","reason":"x","relation":"continue","referentGoalId":"g"}', False),
        ("duplicate-verdict", "update", '{"verdict":"rejected","verdict":"approved","reason":"x","relation":"continue","referentGoalId":"g","referentEntityId":"e"}', False),
        ("extra-action-field", "update", '{"verdict":"approved","reason":"x","relation":"continue","referentGoalId":"g","referentEntityId":"e","action":"on"}', False),
        ("invalid-relation", "update", '{"verdict":"approved","reason":"x","relation":"execute","referentGoalId":"g","referentEntityId":"e"}', False),
        ("null-reference", "update", '{"verdict":"approved","reason":"x","relation":"continue","referentGoalId":null,"referentEntityId":""}', False),
    ]
    rows = []
    for name, mode, text, expected in checks:
        path = output / ("parser-" + name + ".json")
        path.write_text(text, encoding="utf-8")
        actual = harness(output, mode, str(path))
        rows.append({"id": name, "expected": expected, "actual": actual["valid"], "passed": actual["valid"] == expected})
    if not all(row["passed"] for row in rows):
        raise RuntimeError("Compiled production parser local checks failed: " + json.dumps(rows))
    return rows


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-log", type=Path, default=DEFAULT_LOG)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--model", default="antoneva")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    sources = source_hashes()
    contracts = compile_harness(output)
    checks = local_checks(output)
    history, standing, provenance = saved_evidence(args.source_log.resolve())
    frozen = cases(history, standing)
    write(output / "frozen-cases.json", frozen)
    write(output / "production-contracts.json", contracts)
    requests = {case["id"]: payload(case, contracts, args.model) for case in frozen}
    for name, request in requests.items():
        write(output / (name + ".request.json"), request)
    report = {"status": "prepared", "sources": sources, "scriptSha256": digest(Path(__file__).read_bytes()),
              "sourceEvidence": provenance, "caseCount": len(frozen), "localParserChecks": checks,
              "modelRequests": 0, "modelName": args.model, "endpoint": args.url, "results": [],
              "scope": "Actual model semantics over frozen inputs and compiled production protocol. No Unity, ASR, TTS, current image, device write or end-to-end latency measurement. Saved engine proof supplies historical referents only.",
              "policy": "One request per frozen case, serial auxiliary slot 1, no retries, answer selection or repairs; all shared slots must be explicitly idle before each request."}
    write(output / "report.json", report)
    if not args.execute_model:
        print(json.dumps({"status": report["status"], "cases": len(frozen), "parserChecks": len(checks)}))
        return 0
    try:
        for case in frozen:
            if source_hashes() != sources or digest(Path(__file__).read_bytes()) != report["scriptSha256"]:
                report["status"] = "deferred-sources-changed"
                break
            try:
                idle = all_slots_idle(request_json(args.url.rstrip("/") + "/slots"))
            except (urllib.error.URLError, TimeoutError, ValueError, TypeError) as error:
                report["status"] = "deferred-service-unavailable-or-unknown"
                report["serviceCheckError"] = type(error).__name__
                break
            if not idle:
                report["status"] = "deferred-busy-or-unknown"
                break
            request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions",
                json.dumps(requests[case["id"]], ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
                {"Content-Type": "application/json", "Authorization": "Bearer ollama"})
            started = time.perf_counter()
            report["modelRequests"] += 1
            write(output / "report.json", report)
            errors = []
            decision = None
            try:
                with urllib.request.urlopen(request, timeout=12) as response:
                    envelope = strict_json(response.read().decode("utf-8"))
                    status = response.status
                choices = envelope.get("choices", [])
                choice = choices[0] if len(choices) == 1 else {}
                message = choice.get("message", {})
                content = message.get("content", "")
                if status != 200 or choice.get("finish_reason") != "stop" or message.get("role") != "assistant":
                    errors.append("invalid-or-incomplete-envelope")
                if not isinstance(content, str) or not content.strip():
                    errors.append("empty-public-json")
                    content = ""
                public = visible_content(content)
                if public != content.strip():
                    errors.append("hidden-text-in-public-content")
                path = output / (case["id"] + ".response.json")
                # Hidden blocks are not preserved or accepted as a repaired answer.
                path.write_text(public, encoding="utf-8")
                parsed = harness(output, "update" if case["input"]["mode"] == "goal-user-update" else "review", str(path))
                if not parsed["valid"] or public != content.strip():
                    errors.append("production-parser-rejected")
                else:
                    decision = parsed["review"]
                    errors.extend(inspect(case, decision))
            except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError) as error:
                errors.append(type(error).__name__ + ":" + str(error))
            row = {"id": case["id"], "passed": not errors, "errors": errors, "review": decision,
                   "requestSeconds": round(time.perf_counter() - started, 6)}
            report["results"].append(row)
            write(output / "report.json", report)
            print(json.dumps(row, ensure_ascii=False), flush=True)
        else:
            report["status"] = "passed" if all(row["passed"] for row in report["results"]) else "failed"
    finally:
        write(output / "report.json", report)
    return 0 if report["status"] in {"prepared", "passed"} else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
