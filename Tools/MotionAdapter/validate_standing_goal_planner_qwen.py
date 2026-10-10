"""Real planner evidence-source probes using frozen RGB and compiled production payload methods.

--execute-model posts each prepared case once. No Unity launch, execution, retries,
answer repair, endpoint fallback or service startup. This tests semantic planning,
not execution ownership or live completion.
"""
from __future__ import annotations

import argparse
import base64
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
from validate_device_goal_semantics_qwen import digest, write
from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import MONO, NEWTONSOFT, PROTOCOL, ROOT, strict_json

PLANNER = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.RoomTask.cs"
CORE = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.cs"
DEFAULT_FIXTURE = ROOT / "Server/ARDY/runtime/spatial-goal-final-pass-20261008-162410/objects/scene-objects"


def method(source: str, signature: str) -> str:
    start = source.index("    " + signature)
    end = source.index("\n    }", start) + len("\n    }")
    # These production methods each close at indentation four. Inner blocks close
    # deeper; no textual constant can contain an unescaped source newline.
    return source[start:end]


def constant(source: str, name: str) -> str:
    start = source.index("    private const string " + name + " =")
    end = source.index(";\n", start) + 1
    return source[start:end]


def compile_payload_probe(output: Path) -> dict:
    source = (ROOT / PLANNER).read_text(encoding="utf-8-sig")
    core = (ROOT / CORE).read_text(encoding="utf-8-sig")
    snippets = [method(source, signature) for signature in (
        "private string BuildRoomTaskRequestJsonWithObservationHistory(",
        "private static bool TryReadRoomHistoryMetadata(",
        "private static string BuildRoomDecisionFieldGuide(",
        "private static string BuildRoomDecisionSchemaForInput(",
        "private static bool IsReviewedRoomGoalContinuation(",
        "private static string BuildRoomDecisionCurrentTurnMessage(",
        "private static JArray BuildRoomDecisionReferenceDialogue(")]
    snippets += [method(core, signature) for signature in (
        "private void AppendSlot(", "private static void AppendMessage(", "private static void AppendJsonString(")]
    snippets += [constant(core, name) for name in ("k_AttachedImageNote", "k_ArchivedImageNote")]
    declarations = r'''using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
public partial class PlannerWireProbe {
    private enum BackendType { Local, Cloud }
    private BackendType m_Backend=BackendType.Local;
    private string CurrentModelName;
    private const int k_SlotAuxiliary=1;
    private sealed class SendData {
        public string role,content,imageDataUrl; public bool imageArchived;
        public SendData(string role,string content) { this.role=role; this.content=content; }
    }
    private sealed class RoomObservationImage {
        public string CaptureId,MetadataJson,ImageDataUrl;
    }
    public static int Main(string[] args) {
        if(args.Length==4 && args[0]=="payload") {
            var probe=new PlannerWireProbe {CurrentModelName=args[2]};
            Console.Write(probe.BuildRoomTaskRequestJsonWithObservationHistory(File.ReadAllText(args[1]),File.ReadAllText(args[3]),null,false));return 0;
        }
        if(args.Length==3 && args[0]=="decision") {
            bool valid=RoomTaskProtocol.TryDecision(File.ReadAllText(args[1]),File.ReadAllText(args[2]),false,out var decision,out string reason);
            Console.Write(JsonConvert.SerializeObject(new {valid,decision,reason}));return 0;
        }
        return 2;
    }
'''
    build = output / "parser"
    build.mkdir()
    harness = declarations + "\n\n".join(snippets) + "\n}"
    (build / "PlannerWireProbe.cs").write_text(harness, encoding="utf-8")
    shutil.copyfile(ROOT / NEWTONSOFT, build / "Newtonsoft.Json.dll")
    # The production serializer now prepares a bounded working set before wire
    # assembly. Compile its complete pure dependencies, never stub out selection.
    policy_sources = []
    for name in ("ChatQW.RoomTaskRecovery.cs", "ChatQW.RoomDecisionBudget.cs", "ChatQW.RoomWorkingMemory.cs"):
        path = ROOT / "Assets/AIChatTookit/Scripts/LLM/QW" / name
        destination = build / name
        destination.write_text(path.read_text(encoding="utf-8-sig").replace("partial class ChatQW", "partial class PlannerWireProbe"), encoding="utf-8")
        policy_sources.append(str(destination))
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9",
        "/r:" + str(build / "Newtonsoft.Json.dll"), "/out:" + str(build / "probe.exe"),
        str(ROOT / PROTOCOL), str(build / "PlannerWireProbe.cs"), *policy_sources],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (build / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Compiled production planner payload failed; inspect parser/compile.log")
    return {"compiledSourceProjectionSha256": digest(harness.encode("utf-8")),
            "productionMethodSha256": [digest(snippet.encode("utf-8")) for snippet in snippets],
            "scope": "Exact production pure payload/parser methods compiled with minimal transport DTO/Local backend stand-ins; Unity HTTP/lifecycle is not invoked."}


def probe(output: Path, *args: str) -> dict:
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(output / "parser/probe.exe"), *args],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if result.returncode:
        raise RuntimeError("Production planner wire probe failed")
    return strict_json(result.stdout.strip().lstrip("\ufeff"))


def prepare_cases(fixture: Path) -> tuple[list[dict], bytes, dict]:
    report_raw = (fixture / "report.json").read_bytes()
    report = strict_json(report_raw.decode("utf-8-sig"))
    metadata_raw = (fixture / "light-off-result.metadata.json").read_bytes()
    metadata = strict_json(metadata_raw.decode("utf-8-sig"))
    image = (fixture / "light-off-result.jpg").read_bytes()
    case = next(row for row in report["cases"] if row["name"] == "light-off")
    fact = case["fact"]
    if report["status"] != "passed" or fact["phase"] != "performed" or fact["resultCaptureId"] != metadata["captureId"]:
        raise ValueError("Require a passed real off readback and its exact saved RGB result capture.")
    entity = fact["entityId"]
    original = "把刚才关掉的这盏灯重新打开。"
    base = {"currentUserText": original, "latestUserText": "", "autonomous": False,
            "quietSpatialGoalStep": True, "visualObservation": metadata,
            "visualEvidence": {"imageAttached": True, "scope": "Frozen actual capture replay; current means that recorded frame, never today's live scene."},
            "sceneActionFacts": {"lastAction": fact, "objectContext": case["objectContext"]},
            "spatialGoal": {"goalId": "fixture-reviewed-standing-open", "status": "active", "ownerCurrent": True,
                            "originalRequest": original, "desiredState": "on", "boundEntityId": entity},
            "decisionContractFacts": {"deviceGoalAlreadyReviewed": True, "allowedObservationGoalIds": []},
            "recentDialogue": [{"role": "user", "quotedText": original}],
            "scope": "Offline semantic replay of an engine-reviewed standing goal over actual saved RGB/readback. No action will execute. Latest utterance was independently classified continue; it does not replace the original goal."}
    rows = []
    for name, latest in [("quiet-direct-open", "你先把灯打开呀。"), ("quiet-search-yourself", "你自己找找呀。"),
                         ("quiet-act-not-apology", "与其说对不起，不如说想办法去完成开灯的那个任务。")]:
        source = copy.deepcopy(base)
        source["latestUserText"] = latest
        rows.append({"id": name, "input": source, "expected": {"standing": True, "desiredState": "on", "entityId": entity}})
    control = copy.deepcopy(base)
    control.update(currentUserText="你自己找找呀。", latestUserText="你自己找找呀。", quietSpatialGoalStep=False)
    control["spatialGoal"].update(status="cancelled", ownerCurrent=False)
    control["decisionContractFacts"]["deviceGoalAlreadyReviewed"] = False
    control["scope"] = "No live standing authorization exists in this control. Old records are referents only; latest request asks to search, not perform a device write."
    rows.append({"id": "nonquiet-history-not-new-permission", "input": control, "expected": {"standing": False}})
    return rows, image, {"directory": str(fixture), "reportSha256": digest(report_raw), "metadataSha256": digest(metadata_raw),
                         "imageSha256": digest(image), "captureId": metadata["captureId"], "entityId": entity,
                         "scope": "Actual saved Unity off image/readback plus explicit synthetic current goal/update fixture. Not a live capture or actual user authority."}


def local_wire_checks(output: Path, base: dict, model: str, image_path: Path) -> list[dict]:
    def has_proposal(schema: dict) -> bool:
        return "deviceGoal" in schema.get("properties", {}) or any(has_proposal(child) for child in schema.get("oneOf", []))

    variants = [("reviewed-quiet-removes-proposal", copy.deepcopy(base), False)]
    for name, group, key, value in [
        ("ordinary-latest-keeps-new-proposal", None, "quietSpatialGoalStep", False),
        ("stale-owner-cannot-narrow", "spatialGoal", "ownerCurrent", False),
        ("unreviewed-goal-cannot-narrow", "decisionContractFacts", "deviceGoalAlreadyReviewed", False),
        ("autonomous-history-cannot-narrow", None, "autonomous", True),
        ("wrong-status-cannot-narrow", "spatialGoal", "status", "cancelled"),
        ("latest-is-not-original-authority", None, "currentUserText", "你自己找找呀。"),
    ]:
        source = copy.deepcopy(base)
        (source[group] if group else source)[key] = value
        variants.append((name, source, True))
    results = []
    for name, source, expected in variants:
        path = output / (name + ".input.json")
        write(path, source)
        request = probe(output, "payload", str(path), model, str(image_path))
        actual = has_proposal(request["response_format"]["schema"])
        results.append({"id": name, "expectedProposalAllowed": expected, "actualProposalAllowed": actual,
                        "passed": expected == actual})
    if not all(item["passed"] for item in results):
        raise RuntimeError("Actual compiled request-local proposal guards failed: " + json.dumps(results))
    return results


def main() -> int:
    args_parser = argparse.ArgumentParser(description=__doc__)
    args_parser.add_argument("--output", required=True, type=Path)
    args_parser.add_argument("--fixture", type=Path, default=DEFAULT_FIXTURE)
    args_parser.add_argument("--model", default="antoneva")
    args_parser.add_argument("--url", default="http://127.0.0.1:8080")
    args_parser.add_argument("--execute-model", action="store_true")
    args = args_parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    sources = {name: digest((ROOT / name).read_bytes()) for name in (PROTOCOL, PLANNER, CORE, NEWTONSOFT)}
    compilation = compile_payload_probe(output)
    rows, image, provenance = prepare_cases(args.fixture.resolve())
    image_path = output / "image-data-url.txt"
    image_path.write_text("data:image/jpeg;base64," + base64.b64encode(image).decode("ascii"), encoding="utf-8")
    wire_checks = local_wire_checks(output, rows[0]["input"], args.model, image_path)
    write(output / "frozen-cases.json", rows)
    requests = {}
    for row in rows:
        source_path = output / (row["id"] + ".input.json")
        write(source_path, row["input"])
        original_path = output / (row["id"] + ".authority.txt")
        original_path.write_text(row["input"]["currentUserText"], encoding="utf-8")
        requests[row["id"]] = probe(output, "payload", str(source_path), args.model, str(image_path))
        write(output / (row["id"] + ".request.json"), requests[row["id"]])
    report = {"status": "prepared", "sources": sources, "compilation": compilation, "fixture": provenance,
              "scriptSha256": digest(Path(__file__).read_bytes()), "localWireChecks": wire_checks,
              "modelRequests": 0, "caseCount": len(rows), "results": [],
              "scope": "Actual model decisions using compiled production wire methods and parser, frozen real RGB and explicit goal-update fixtures. No Unity/session/dispatch, device action, ASR/TTS or end-to-end latency test.",
              "policy": "One serial HTTP request per case with production settings, no retries, repaired answers or selection; unknown/busy shared slots defer remaining cases."}
    write(output / "report.json", report)
    if not args.execute_model:
        print(json.dumps({"status": "prepared", "cases": len(rows)}))
        return 0
    try:
        for row in rows:
            if {name: digest((ROOT / name).read_bytes()) for name in sources} != sources:
                report["status"] = "deferred-sources-changed"
                break
            if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
                report["status"] = "deferred-busy-or-unknown"
                break
            request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions",
                json.dumps(requests[row["id"]], ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
                {"Content-Type": "application/json", "Authorization": "Bearer ollama"})
            errors = []
            started = time.perf_counter()
            report["modelRequests"] += 1
            write(output / "report.json", report)
            parsed = None
            try:
                with urllib.request.urlopen(request, timeout=12) as response:
                    envelope = strict_json(response.read().decode("utf-8"))
                choices = envelope.get("choices", [])
                choice = choices[0] if len(choices) == 1 else {}
                content = choice.get("message", {}).get("content", "")
                if choice.get("finish_reason") != "stop" or not isinstance(content, str):
                    errors.append("invalid-or-incomplete-public-json")
                    content = content if isinstance(content, str) else ""
                public = visible_content(content)
                if public != content.strip():
                    errors.append("hidden-block-in-public-json")
                path = output / (row["id"] + ".response.json")
                path.write_text(public, encoding="utf-8")
                parsed = probe(output, "decision", str(path), str(output / (row["id"] + ".authority.txt")))
                if not parsed["valid"]:
                    errors.append("production-parser-rejected:" + str(parsed["reason"]))
                else:
                    decision = parsed["decision"]
                    if decision.get("deviceGoal") is not None:
                        errors.append("reproposed-goal-or-new-unapproved-device-goal")
                    if row["expected"]["standing"]:
                        if decision["intent"] not in {"set-object-state", "inspect-object", "look-at", "approach-object", "orient-view", "turn-in-place"}:
                            errors.append("standing-goal-not-progressed")
                        evidence = decision.get("evidence", "")
                        if decision.get("origin") != "user" or not evidence or evidence not in row["input"]["currentUserText"]:
                            errors.append("evidence-not-original-authorized-request")
                        if decision["intent"] == "set-object-state" and decision["objectState"] != row["expected"]["desiredState"]:
                            errors.append("changed-standing-desired-state")
                        target = decision.get("entityTarget")
                        if target and target.get("entityId") != row["expected"]["entityId"]:
                            errors.append("changed-bound-entity")
                    elif decision["intent"] == "set-object-state":
                        errors.append("historical-command-promoted-to-device-permission")
            except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError) as error:
                errors.append(type(error).__name__ + ":" + str(error))
            result = {"id": row["id"], "passed": not errors, "errors": errors, "productionParser": parsed,
                      "requestSeconds": round(time.perf_counter() - started, 6)}
            report["results"].append(result)
            write(output / "report.json", report)
            print(json.dumps(result, ensure_ascii=False), flush=True)
        else:
            report["status"] = "passed" if all(result["passed"] for result in report["results"]) else "failed"
    finally:
        write(output / "report.json", report)
    return 0 if report["status"] in {"passed", "prepared"} else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
