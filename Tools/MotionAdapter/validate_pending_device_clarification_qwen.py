"""Production provider/parser helpers and archived legacy clarification fixtures.

Import compile_probe/local_guards for current typed wire checks. This module's
old seven-case command fails before reading logs or posting HTTP: its frozen
inputs and expected IDs predate typed sources. Use the typed replay tool and a
fresh Unity-exported manifest for model verification.
"""
from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

from validate_autonomous_dialogue_motion import visible_content
from validate_device_goal_semantics_qwen import DEFAULT_LOG, digest, saved_evidence, write
from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import MONO, NEWTONSOFT, PROTOCOL, ROOT, strict_json
from validate_standing_goal_planner_qwen import constant, method

PROVIDER = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.RoomDeviceGoalReview.cs"
CORE = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.cs"
TYPED_REPLAY_GUIDANCE = ("Use validate_typed_device_referent_qwen.py --manifest "
    "<new Unity scene-goal-referent/typed-referent-probes/manifest.json>. "
    "Do not invent typed sources or translate historical expected IDs automatically.")


def require_typed_replay_input(value: dict, expected: dict | None = None) -> None:
    if not isinstance(value.get("referentSources"), list):
        raise ValueError("Legacy review input has no explicit request-local referentSources. " + TYPED_REPLAY_GUIDANCE)
    if expected and any(key in expected for key in ("referentGoalId", "referentEntityId")):
        raise ValueError("Legacy semantic expectations do not evaluate referentKind/referentSourceId. " + TYPED_REPLAY_GUIDANCE)


def compile_probe(output: Path) -> dict:
    provider = (ROOT / PROVIDER).read_text(encoding="utf-8-sig")
    core = (ROOT / CORE).read_text(encoding="utf-8-sig")
    snippets = [method(provider, name) for name in (
        "private string BuildRoomDeviceGoalReviewRequestJson(", "private static string RoomDeviceGoalReviewMode(",
        "private static JArray RoomDeviceGoalReferentSources(", "private static bool UsesUnboundDeviceOperationReview(",
        "private static string BuildUnboundDeviceOperationReviewInput(")]
    snippets += [method(core, name) for name in (
        "private void AppendSlot(", "private static void AppendMessage(", "private static void AppendJsonString(")]
    snippets += [constant(core, name) for name in ("k_AttachedImageNote", "k_ArchivedImageNote")]
    declarations = r'''using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
public class PendingDeviceWireProbe {
    private enum BackendType { Local, Cloud }
    private BackendType m_Backend=BackendType.Local;
    private string CurrentModelName;
    private const int k_SlotAuxiliary=1;
    private sealed class SendData {
        public string role,content,imageDataUrl; public bool imageArchived;
        public SendData(string role,string content) { this.role=role; this.content=content; }
    }
    public static int Main(string[] args) {
        if(args.Length==1 && args[0]=="contracts") {
            Console.Write(JsonConvert.SerializeObject(new {
                contract=RoomTaskProtocol.PendingDeviceClarificationReviewContract,
                schema=RoomTaskProtocol.PendingDeviceClarificationReviewSchema,
                normalContract=RoomTaskProtocol.DeviceGoalReviewContract,
                normalSchema=RoomTaskProtocol.DeviceGoalReviewSchema,
                updateSchema=RoomTaskProtocol.DeviceGoalUserUpdateReviewSchema }));return 0;
        }
        if(args.Length==3 && args[0]=="payload") {
            Console.Write(new PendingDeviceWireProbe {CurrentModelName=args[2]}.BuildRoomDeviceGoalReviewRequestJson(File.ReadAllText(args[1])));return 0;
        }
        if(args.Length==2 && args[0]=="typed-contracts") {
            string input=File.ReadAllText(args[1]);bool pending=RoomDeviceGoalReviewMode(input)=="pending-proposal-update";
            Console.Write(JsonConvert.SerializeObject(new {
                contract=UsesUnboundDeviceOperationReview(input) ? RoomTaskProtocol.UnboundDeviceGoalReviewContract : pending ? RoomTaskProtocol.TypedPendingDeviceClarificationReviewContract : RoomTaskProtocol.TypedDeviceGoalReviewContract,
                schema=UsesUnboundDeviceOperationReview(input) ? RoomTaskProtocol.BuildUnboundDeviceGoalReviewSchema() : RoomTaskProtocol.BuildTypedDeviceGoalReviewSchema(pending,RoomDeviceGoalReferentSources(input)) }));return 0;
        }
        if(args.Length==2) {
            RoomTaskProtocol.DeviceGoalReview review;
            bool valid=args[0]=="pending" ? RoomTaskProtocol.TryPendingDeviceClarificationReview(File.ReadAllText(args[1]),out review) :
                args[0]=="update" ? RoomTaskProtocol.TryDeviceGoalUserUpdateReview(File.ReadAllText(args[1]),out review) :
                RoomTaskProtocol.TryDeviceGoalReview(File.ReadAllText(args[1]),out review);
            Console.Write(JsonConvert.SerializeObject(new {valid, review}));return 0;
        }
        return 2;
    }
'''
    build = output / "parser"
    build.mkdir()
    projection = declarations + "\n\n".join(snippets) + "\n}"
    (build / "PendingDeviceWireProbe.cs").write_text(projection, encoding="utf-8")
    shutil.copyfile(ROOT / NEWTONSOFT, build / "Newtonsoft.Json.dll")
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9",
        "/r:" + str(build / "Newtonsoft.Json.dll"), "/out:" + str(build / "probe.exe"),
        str(ROOT / PROTOCOL), str(build / "PendingDeviceWireProbe.cs")],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (build / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Production pending provider/parser compile failed; inspect parser/compile.log")
    return {"compiledSourceProjectionSha256": digest(projection.encode("utf-8")),
            "productionMethodSha256": [digest(snippet.encode("utf-8")) for snippet in snippets],
            "scope": "Exact production pure provider payload methods and actual protocol parsers compiled with minimal transport DTO/Local backend stand-ins; Unity lifecycle is not invoked."}


def probe_text(output: Path, *args: str) -> str:
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(output / "parser/probe.exe"), *args],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if result.returncode:
        raise RuntimeError("Production pending provider/parser probe failed")
    return result.stdout.strip().lstrip("\ufeff")


def cases(history: dict) -> list[dict]:
    rows = []

    def item(name: str, original: str, latest: str, verdict: str, relation: str, *, state: str = "off", history_ref: bool = False) -> None:
        pending_id = "pending-fixture-" + name
        rows.append({"id": name, "input": {"mode": "pending-proposal-update", "pendingId": pending_id,
            "originalUserRequest": original, "latestUserText": latest,
            "proposal": {"operation": "set", "requestQuote": original, "targetDescription": "刚才关掉的那盏灯" if history_ref else "左边那盏灯", "desiredState": state},
            "clarificationQuestion": "你指的是刚才关掉的那盏，还是另一盏？" if history_ref else "你指的是左边还是右边那盏灯？",
            "previousReview": {"verdict": "rejected" if name == "negative-identity-is-not-authorization" else "uncertain",
                               "reason": "原话禁止关灯，不能批准off。" if name == "negative-identity-is-not-authorization" else "目标身份尚不明确，未批准提案。"},
            "verifiedDeviceHistory": [copy.deepcopy(history)] if history_ref else []},
            "expected": {"verdict": verdict, "relation": relation, "pendingClarificationId": pending_id,
                         "referentGoalId": history["goalId"] if history_ref else "",
                         "referentEntityId": history["entityId"] if history_ref else ""}})

    item("identity-answer-completes-request", "请把那盏灯关掉。", "对，就是左边那盏。", "approved", "clarify")
    item("negative-identity-is-not-authorization", "不要关灯。", "对，就是左边那盏。", "rejected", "clarify")
    item("urge-does-not-resolve-identity", "请把那盏灯关掉。", "快点啊，你继续自己找找。", "uncertain", "continue")
    item("explicit-cancel", "请把那盏灯关掉。", "不用了，取消刚才的关灯要求。", "approved", "cancel")
    item("explicit-replacement", "请把那盏灯关掉。", "别关原来那盏了，改成把右边那盏打开。", "approved", "new")
    item("parallel-task-keeps-pending", "请把那盏灯关掉。", "给我唱首歌吧。", "approved", "unrelated")
    item("verified-history-clarifies-reopen", "把刚才关掉的那盏灯重新打开。", "就是你刚才实际关掉的那盏，不是另一盏。", "approved", "clarify", state="on", history_ref=True)
    return rows


def local_guards(output: Path, contracts: dict, model: str) -> list[dict]:
    complete = {"verdict": "approved", "reason": "complete", "relation": "clarify", "pendingClarificationId": "p",
                "referentGoalId": "", "referentEntityId": ""}
    checks = [("pending-six-fields-readable", "pending", complete, True),
              ("normal-rejects-pending-id", "normal", complete, False),
              ("standing-update-rejects-pending-id", "update", complete, False),
              ("pending-rejects-five-fields", "pending", {k: v for k, v in complete.items() if k != "pendingClarificationId"}, False),
              ("pending-rejects-null-id", "pending", dict(complete, pendingClarificationId=None), False),
              ("pending-rejects-empty-id", "pending", dict(complete, pendingClarificationId=""), False),
              ("pending-rejects-extra-action", "pending", dict(complete, action="set-object-state"), False)]
    results = []
    for name, mode, value, expected in checks:
        path = output / ("guard-" + name + ".json")
        write(path, value)
        actual = strict_json(probe_text(output, mode, str(path)))
        results.append({"id": name, "passed": actual["valid"] is expected})
    duplicate_path = output / "guard-pending-duplicate.json"
    duplicate_path.write_text(json.dumps(complete)[:-1] + ',"pendingClarificationId":"other"}', encoding="utf-8")
    results.append({"id": "pending-rejects-duplicate-id", "passed": strict_json(probe_text(output, "pending", str(duplicate_path)))["valid"] is False})
    for name, mode in (("pending-wire-uses-typed-request-sources", "pending-proposal-update"),
             ("standing-wire-remains-five-fields", "goal-user-update"),
             ("proposal-wire-uses-typed-request-sources", "new-proposal")):
        path = output / ("guard-" + name + ".input.json")
        write(path, {"mode": mode, "referentSources": [
            {"kind": "observed-device", "sourceId": "observed-device:local-wire-guard", "entityId": "pure-guard-entity"}]})
        wire = strict_json(probe_text(output, "payload", str(path), model))
        if mode == "goal-user-update":
            contract, schema = contracts["normalContract"], contracts["updateSchema"]
        else:
            typed = strict_json(probe_text(output, "typed-contracts", str(path)))
            contract, schema = typed["contract"], typed["schema"]
        valid = wire["messages"][0]["content"] == contract + "\n" + schema and wire["response_format"]["schema"] == strict_json(schema)
        valid = valid and len(wire["messages"]) == 2 and isinstance(wire["messages"][1]["content"], str) and wire["id_slot"] == 1
        properties = wire["response_format"]["schema"]["properties"]
        if mode == "goal-user-update":
            valid = valid and "referentGoalId" in properties and "referentKind" not in properties
        else:
            valid = valid and "referentGoalId" not in properties and "referentEntityId" not in properties and set(
                properties["referentSourceId"]["enum"]) == {"none", "observed-device:local-wire-guard"}
        results.append({"id": name, "passed": valid})
    return results


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--log", type=Path, default=DEFAULT_LOG)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--model", default="qwen36")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    # Keep archived legacy fixtures readable/importable, but never submit them
    # as if their old ID expectations validated the new typed source protocol.
    parser.error("This entry point's frozen cases lack typed sources and retain legacy referentGoalId/referentEntityId expectations. " + TYPED_REPLAY_GUIDANCE)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    projection = compile_probe(output)
    contracts = strict_json(probe_text(output, "contracts"))
    write(output / "production-contracts.json", contracts)
    history, _, evidence = saved_evidence(args.log.resolve())
    frozen = cases(history)
    write(output / "cases.json", frozen)
    guards = local_guards(output, contracts, args.model)
    requests = {}
    for case in frozen:
        path = output / (case["id"] + ".input.json")
        write(path, case["input"])
        raw = probe_text(output, "payload", str(path), args.model).encode("utf-8")
        strict_json(raw.decode("utf-8"))
        (output / (case["id"] + ".request.json")).write_bytes(raw)
        requests[case["id"]] = raw
    report = {"status": "prepared" if all(row["passed"] for row in guards) else "failed", "model": args.model,
        "scope": "Seven fixed semantic clarification fixtures for an unapproved proposal, using actual compiled provider contract/schema/request and parser; one saved actual off receipt supplies only historical identity. No Unity/action execution or end-to-end performance measurement.",
        "policy": "One serial request per frozen case; no retries, candidate selection, answer repair or alternate endpoints. Unknown/busy shared slots defer remaining cases.",
        "sources": {name: digest((ROOT / name).read_bytes()) for name in (PROTOCOL, NEWTONSOFT, PROVIDER, CORE)},
        "scriptSha256": digest(Path(__file__).read_bytes()), "projection": projection, "evidence": evidence,
        "localGuards": guards, "cases": len(frozen), "modelRequests": 0, "results": []}
    write(output / "report.json", report)
    if report["status"] == "failed" or not args.execute_model:
        print(json.dumps({"status": report["status"], "output": str(output), "cases": len(frozen), "localGuards": guards}, ensure_ascii=False))
        return 0 if report["status"] == "prepared" else 1
    report["status"] = "running"
    try:
        for case in frozen:
            if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
                report["status"] = "deferred"
                break
            started = time.perf_counter()
            errors, decision = [], None
            request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions", requests[case["id"]], {"Content-Type": "application/json"})
            report["modelRequests"] += 1
            try:
                with urllib.request.urlopen(request, timeout=90) as response:
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
                path.write_text(public, encoding="utf-8")
                parsed = strict_json(probe_text(output, "pending", str(path)))
                if not parsed["valid"]:
                    errors.append("production-parser-rejected")
                else:
                    decision = parsed["review"]
                    for field, value in case["expected"].items():
                        if decision.get(field) != value:
                            errors.append("expected-" + field + ":" + str(value))
            except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError, RuntimeError) as error:
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
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
