"""Prepare or single-pass replay the actual FirstReply release-review payload/parser.

Default is offline compilation and preparation only. --execute-model sends each
frozen case once with the production six-second deadline. Optional
--allow-production-grounding-repair applies the production one-repair rule only
to a syntactically complete but ungrounded review; never to a wrong expectation.
This tests the independent semantic gate, not a live avatar, TTS or full turn.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

from validate_device_goal_semantics_qwen import digest, write
from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import ROOT, MONO, NEWTONSOFT, strict_json
from validate_standing_goal_planner_qwen import CORE, constant, method

PROVIDER = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.FirstReply.cs"
PROTOCOL = "Assets/AIChatTookit/Scripts/LLM/FirstReplyProtocol.cs"
CASES = "Assets/Editor/FirstReplyTransportRegression.cs"
PURE = [PROTOCOL, "Assets/AIChatTookit/Scripts/Chat/SpeechText.cs",
        "Assets/AIChatTookit/Scripts/Chat/RoleOutputChannels.cs",
        "Assets/AIChatTookit/Scripts/Chat/RoleOutputPublicBoundary.cs",
        "Assets/Editor/FirstReplyProtocolRegression.cs",
        "Assets/AIChatTookit/Scripts/LLM/FormalResponseStage.cs",
        "Assets/AIChatTookit/Scripts/LLM/CommittedSpatialReplyProtocol.cs",
        "Assets/AIChatTookit/Scripts/LLM/QW/SceneFactContextCompactor.cs"]


def compile_probe(output: Path) -> dict:
    source = (ROOT / PROVIDER).read_text(encoding="utf-8-sig")
    core = (ROOT / CORE).read_text(encoding="utf-8-sig")
    cases = (ROOT / CASES).read_text(encoding="utf-8-sig")
    snippets = [method(source, signature) for signature in (
        "private string BuildFirstReplyReviewWire(", "private string BuildFirstReplyWire(")]
    snippets += [method(core, signature) for signature in (
        "private void AppendSlot(", "private static void AppendMessage(", "private static void AppendJsonString(",
        "private string BuildStageRequestJsonCore(", "private static SendData MessageForRequest(",
        "private static string BuildCommittedReplyWritingInstruction(", "private sealed class ObservationEnvelope :",
        "private string CompactScenePromptContext(", "private static uint StableBlockHash(", "private static int EstimateTokens(")]
    snippets += [constant(core, name) for name in ("k_AttachedImageNote", "k_ArchivedImageNote", "k_FrameUserMarker")]
    frame_start = core.index("    private static readonly string[] s_FrameKeepPrefixes =")
    snippets.append(core[frame_start:core.index("\n    };", frame_start) + len("\n    };")])
    snippets += [method(cases, "public static JArray SemanticCases(")]
    declarations = r'''using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
public class FirstReplyWireProbe {
    private string BuildStageRequestJsonForMessages(List<SendData> m,bool s,string c,string f,FormalResponseStage stage)
        => BuildStageRequestJsonCore(m,s,c,f,stage);
    private static string ProjectSceneAttentionText(string context)
        => throw new InvalidOperationException("Routing attention requires the Unity replay; this compiled probe supports the review payload only.");
    private enum BackendType { Local, Cloud }
    private BackendType m_Backend = BackendType.Local;
    private string CurrentModelName;
    private const int k_SlotAuxiliary = 1;
    private const int k_SlotMainConversation = 0;
    private bool m_LogRequestStats=false, m_CompactSceneFactContext=true, m_EnableThinking=false;
    private string ActiveSkillContext="", SkillCatalogContext="", TrailingContext="", SpokenPrefix="";
    private static class Debug { public static void Log(object value) {} }
    private string BuildSpatialGoalResultRequestJsonForMessages(List<SendData> history,bool stream,string context,string feedback,string image)
        => throw new InvalidOperationException("Unreachable terminal-result branch in first-reply probe.");
    private class SendData {
        public string role,content,imageDataUrl; public bool imageArchived;
        public SendData(string role,string content) { this.role=role;this.content=content; }
    }
    public static int Main(string[] args) {
        if(args[0]=="checks") {
            FirstReplyProtocolRegression.RunChecks(); Console.Write(FirstReplyProtocolRegression.LastReport);return 0;
        }
        if(args[0]=="cases") { Console.Write(SemanticCases());return 0; }
        if(args[0]=="review") {
            bool valid=FirstReplyProtocol.TryCurrentReview(File.ReadAllText(args[1]),out string route,out JObject understanding,out string requestKind,out bool? sufficient,out string demand);
            Console.Write(new JObject { ["valid"]=valid,["route"]=route,["questionUnderstanding"]=understanding,["requestKind"]=requestKind,["candidateSufficient"]=sufficient,["responseDemand"]=demand });return 0;
        }
        if(args[0]=="resolve-review") {
            var fixture=JObject.Parse(File.ReadAllText(args[1]));
            var parser=new FirstReplyStreamParser(()=>true,null);parser.Push((string)fixture["candidate"]);
            var candidate=parser.Finish(true,"stop");
            bool valid=FirstReplyProtocol.TryGroundedReview(File.ReadAllText(args[2]),(string)fixture["user"],out string route,out JObject understanding,out string kind,out bool? sufficient,out string demand,out JObject proof);
            var result=valid ? FirstReplyProtocol.ResolveReviewedCompletion(candidate,route,"probe",understanding,kind,sufficient,demand) : null;
            Console.Write(new JObject { ["valid"]=valid && result?.Succeeded==true,["route"]=result?.Disposition.ToString().ToLowerInvariant(),
                ["reviewedRoute"]=route,["requestKind"]=result?.RequestKind,["candidateSufficient"]=result?.CandidateSufficient,["responseDemand"]=result?.ResponseDemand,
                ["questionUnderstanding"]=result?.QuestionUnderstanding,["speechCharacters"]=result?.SpeechCharacters,
                ["repairableGrounding"]=!valid && FirstReplyProtocol.CanRepairGrounding(File.ReadAllText(args[2])) });return 0;
        }
        if(args[0]=="repair-payload") {
            Console.Write(FirstReplyProtocol.BuildGroundingRepairWire(File.ReadAllText(args[1]),File.ReadAllText(args[2])));return 0;
        }
        if(args[0]=="preflight-review") {
            bool valid=FirstReplyProtocol.TryGroundedReview(File.ReadAllText(args[1]),File.ReadAllText(args[2]),
                out string route,out JObject understanding,out string kind,out bool? sufficient,out string demand,out JObject proof);
            bool handoff=valid && !(kind=="answer-only" && demand=="short-conversation");
            var result=handoff ? FirstReplyProtocol.ResolveReviewedCompletion(null,route,"probe",understanding,kind,sufficient,demand) : null;
            Console.Write(new JObject { ["valid"]=valid, ["handoff"]=handoff,
                ["route"]=valid ? (handoff ? result?.Disposition.ToString().ToLowerInvariant() : "ordinary") : null,
                ["requestKind"]=kind, ["responseDemand"]=demand,
                ["repairableGrounding"]=!valid && FirstReplyProtocol.CanRepairGrounding(File.ReadAllText(args[1])) });return 0;
        }
        if(args[0]=="parse-route") {
            var parser=new FirstReplyStreamParser(()=>true,null);parser.Push(File.ReadAllText(args[1]));
            var result=parser.Finish(true,args[2]);
            Console.Write(new JObject { ["valid"]=result!=null && result.Succeeded,
                ["route"]=result?.Disposition.ToString().ToLowerInvariant(),["failure"]=result?.Failure });return 0;
        }
        if(args[0]=="routing-payload") {
            var item=JObject.Parse(File.ReadAllText(args[1]));
            var probe=new FirstReplyWireProbe { CurrentModelName=args[2] };
            var history=new List<SendData> {
                new SendData("system","Synthetic embodied Unity character. Available abilities are described by the runtime; a spoken promise never executes them. Follow the user's explicit language choice."),
                new SendData("user",(string)item["user"])
            };
            Console.Write(probe.BuildFirstReplyWire(history,(string)item["context"],true));return 0;
        }
        if(args[0]=="payload") {
            var item=JObject.Parse(File.ReadAllText(args[1]));
            var current=new SendData("user",(string)item["user"]);
            var history=new List<SendData> {
                new SendData("system","PERSONA_MUST_NOT_REVIEW_CANARY"),
                new SendData("user","之前我们聊过散步。") { imageDataUrl="IMAGE_MUST_NOT_REVIEW_CANARY" },
                new SendData("assistant","是的，那只是聊天。"), current
            };
            if(item["history"] is JArray prior) {
                history.Clear();
                foreach(JObject row in prior) history.Add(new SendData((string)row["role"],(string)row["text"]));
                history.Add(current);
            }
            var parser=new FirstReplyStreamParser(()=>true,null);parser.Push((string)item["candidate"]);
            var candidate=parser.Finish(true,"stop");
            if(candidate==null || candidate.Disposition==FirstReplyDisposition.Failed) return 3;
            var probe=new FirstReplyWireProbe { CurrentModelName=args[2] };
            Console.Write(probe.BuildFirstReplyReviewWire(current,history,(string)item["context"],candidate));return 0;
        }
        return 2;
    }
'''
    build = output / "parser"
    build.mkdir()
    harness = declarations + "\n\n".join(snippets) + "\n}"
    (build / "FirstReplyWireProbe.cs").write_text(harness, encoding="utf-8")
    shutil.copyfile(ROOT / NEWTONSOFT, build / "Newtonsoft.Json.dll")
    command = [str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9",
               "/r:" + str(build / "Newtonsoft.Json.dll"), "/out:" + str(build / "probe.exe"),
               *[str(ROOT / name) for name in PURE], str(build / "FirstReplyWireProbe.cs")]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (build / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Production FirstReply compile failed; inspect parser/compile.log")
    return {"compiledProjectionSha256": digest(harness.encode("utf-8")),
            "scope": "Production review payload, contract, grammar, grounding parser and bounded repair builder. Synthetic history/runtime and inert Unity logging. Routing with scene attention is unsupported here; use Unity replay. No expectations enter payloads."}


def probe(output: Path, *args: str):
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(output / "parser/probe.exe"), *args],
                            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
    if result.returncode:
        raise RuntimeError("Production FirstReply probe failed: " + result.stderr[:500])
    return strict_json(result.stdout.strip().lstrip("\ufeff"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--model", default="antoneva")
    parser.add_argument("--execute-model", action="store_true")
    parser.add_argument("--allow-production-grounding-repair", action="store_true")
    parser.add_argument("--mode", choices=("review", "routing", "both"), default="review")
    parser.add_argument("--routing-wires", type=Path, help="For routing/both, use actual Unity-exported <case-id>.routing.request.json wires, including current scene attention.")
    parser.add_argument("--only", nargs="*", help="Explicit fixture ids for a necessary follow-up; no expectation changes.")
    parser.add_argument("--fixtures", type=Path, help="Frozen JSON array with complete user/context/history and evaluator-only expected routes/kinds.")
    args = parser.parse_args()
    if args.mode != "review" and not args.routing_wires:
        parser.error("Routing now requires --routing-wires from FirstReplyTransportRegression's Unity export; the isolated review probe does not approximate scene attention.")
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    files = list(dict.fromkeys([PROVIDER, CORE, CASES, NEWTONSOFT, *PURE]))
    sources = {name: digest((ROOT / name).read_bytes()) for name in files}
    report = {"status": "preparing", "sources": sources, "compilation": compile_probe(output),
              "pureChecks": probe(output, "checks"), "scriptSha256": digest(Path(__file__).read_bytes()),
              "modelRequests": 0, "cases": [], "productionDeadlineSeconds": {"review": 6, "routing": 25},
              "policy": "One serial request per case; one production grounding repair on parser rejection only when explicitly enabled. No best-of, expectation-driven retry, tool execution or TTS.",
              "groundingRepairEnabled": args.allow_production_grounding_repair,
              "scope": "Production independent release-review and first-reply routing wires/parsers. Synthetic controlled runtime summaries. No avatar, action execution or TTS; expected routes are evaluator-only."}
    prepared = []
    fixtures = strict_json(args.fixtures.read_text(encoding="utf-8-sig")) if args.fixtures else probe(output, "cases")
    if args.fixtures:
        report["fixtureSource"] = {"path": str(args.fixtures.resolve()), "sha256": digest(args.fixtures.read_bytes())}
        report["scope"] += " Additional source-labelled fixtures may carry reconstructed recorded dialogue; inspect each fixture provenance, not an exact historical-wire claim."
    for fixture in fixtures:
        case_id = fixture["id"]
        if args.only and case_id not in args.only:
            continue
        fixture_path = output / (case_id + ".fixture.json")
        write(fixture_path, fixture)
        modes = ["review"] if args.mode == "review" else ["routing"] if args.mode == "routing" else ["review", "routing"]
        for mode in modes:
            if mode == "routing" and fixture["candidate"] != "<route>evidence</route>":
                continue  # Other fixture expectations concern candidate quality, not routing.
            wire = probe(output, "payload", str(fixture_path), args.model) if mode == "review" else strict_json(
                (args.routing_wires / (case_id + ".routing.request.json")).read_text(encoding="utf-8-sig"))
            if mode == "review":
                input_value = strict_json(wire["messages"][1]["content"])
                if input_value["latestUserText"] != fixture["user"] or "MUST_NOT_REVIEW_CANARY" in json.dumps(wire):
                    raise ValueError("Review projection changed the current user or copied hidden persona/image context")
            elif wire["messages"][-1]["content"] != fixture["user"]:
                raise ValueError("Routing projection changed the complete latest user")
            request_path = output / (case_id + "." + mode + ".request.json")
            write(request_path, wire)
            item = {"id": case_id + "." + mode, "fixtureId": case_id, "mode": mode, "expectedRoute": fixture["expectedRoute"],
                    "requestSha256": digest(request_path.read_bytes())}
            if "expectedRequestKinds" in fixture:
                item["expectedRequestKinds"] = fixture["expectedRequestKinds"]
            report["cases"].append(item); prepared.append((item, wire))
    report["status"] = "prepared"; write(output / "report.json", report)
    if not args.execute_model:
        print(json.dumps({"status": "prepared", "cases": len(prepared), "modelRequests": 0,
                          "pureChecks": report["pureChecks"]["checks"]})); return 0
    for item, wire in prepared:
        if {name: digest((ROOT / name).read_bytes()) for name in files} != sources:
            report["status"] = "deferred-sources-changed"; break
        if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
            report["status"] = "deferred-busy-or-unknown"; break
        errors, parsed = [], None
        request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions",
            json.dumps(wire, ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
            {"Content-Type": "application/json", "Authorization": "Bearer ollama"})
        started = time.perf_counter(); elapsed = None
        deadline = 6 if item["mode"] == "review" else 25
        report["modelRequests"] += 1; write(output / "report.json", report)
        try:
            with urllib.request.urlopen(request, timeout=deadline) as response:
                raw_envelope = response.read()
            elapsed = time.perf_counter() - started
            (output / (item["id"] + (".envelope.json" if item["mode"] == "review" else ".sse.txt"))).write_bytes(raw_envelope)
            if item["mode"] == "routing":
                content, finish, usage = [], None, None
                for line in raw_envelope.decode("utf-8-sig").splitlines():
                    if not line.startswith("data: ") or line == "data: [DONE]":
                        continue
                    event = strict_json(line[6:])
                    if event.get("usage"):
                        usage = event["usage"]
                    for choice in event.get("choices", []):
                        content.append(choice.get("delta", {}).get("content") or "")
                        finish = choice.get("finish_reason") or finish
                envelope = {"choices": [{"finish_reason": finish, "message": {"role": "assistant", "content": "".join(content)}}], "usage": usage}
            else:
                envelope = strict_json(raw_envelope.decode("utf-8-sig"))
            choices = envelope.get("choices", [])
            if len(choices) != 1 or choices[0].get("finish_reason") != "stop":
                raise ValueError("Incomplete model output")
            message = choices[0].get("message", {})
            if message.get("role") != "assistant" or not isinstance(message.get("content"), str) or \
                    message.get("tool_calls") is not None or message.get("function_call") is not None or message.get("refusal"):
                raise ValueError("Invalid completion envelope")
            response_path = output / (item["id"] + ".response.json")
            # Parser evidence must retain model bytes: Windows text mode would
            # turn the required LF after a route head into CRLF and falsely fail.
            response_path.write_bytes(message["content"].encode("utf-8"))
            parsed = probe(output, "resolve-review", str(output / (item["fixtureId"] + ".fixture.json")), str(response_path)) if item["mode"] == "review" else probe(output, "parse-route", str(response_path), choices[0]["finish_reason"])
            item["usage"] = envelope.get("usage")
            if item["mode"] == "review":
                item["rawReview"] = strict_json(message["content"])
                if args.allow_production_grounding_repair and parsed.get("repairableGrounding"):
                    item["initialParser"] = parsed
                    item["initialRequestSeconds"] = elapsed
                    repair_wire = probe(output, "repair-payload", str(output / (item["id"] + ".request.json")), str(response_path))
                    write(output / (item["id"] + ".repair.request.json"), repair_wire)
                    repair_request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions",
                        json.dumps(repair_wire, ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
                        {"Content-Type": "application/json", "Authorization": "Bearer ollama"})
                    if elapsed > deadline:
                        raise ValueError("Initial review exceeded production deadline; no repair allowed")
                    report["modelRequests"] += 1
                    repair_started = time.perf_counter()
                    with urllib.request.urlopen(repair_request, timeout=6) as response:
                        repair_raw = response.read()
                    item["repairRequestSeconds"] = time.perf_counter() - repair_started
                    elapsed = max(elapsed, item["repairRequestSeconds"])
                    (output / (item["id"] + ".repair.envelope.json")).write_bytes(repair_raw)
                    repair_envelope = strict_json(repair_raw.decode("utf-8-sig"))
                    repair_choices = repair_envelope.get("choices", [])
                    if len(repair_choices) != 1 or repair_choices[0].get("finish_reason") != "stop":
                        raise ValueError("Incomplete repair output")
                    repair_message = repair_choices[0].get("message", {})
                    if repair_message.get("role") != "assistant" or not isinstance(repair_message.get("content"), str) or repair_message.get("tool_calls") or repair_message.get("function_call") or repair_message.get("refusal"):
                        raise ValueError("Invalid repair completion envelope")
                    repair_path = output / (item["id"] + ".repair.response.json")
                    repair_path.write_bytes(repair_message["content"].encode("utf-8"))
                    parsed = probe(output, "resolve-review", str(output / (item["fixtureId"] + ".fixture.json")), str(repair_path))
                    item["repairReview"] = strict_json(repair_message["content"])
                    item["repairUsage"] = repair_envelope.get("usage")
            if not parsed["valid"] or parsed["route"] != item["expectedRoute"]:
                errors.append("wrong-or-invalid-semantic-route")
            if item["mode"] == "review" and item.get("expectedRequestKinds") and parsed.get("requestKind") not in item["expectedRequestKinds"]:
                errors.append("wrong-current-request-kind")
        except (urllib.error.URLError, TimeoutError, ValueError, KeyError, TypeError) as error:
            errors.append(type(error).__name__ + ":" + str(error))
        if elapsed is None:
            elapsed = time.perf_counter() - started
        if elapsed > deadline:
            errors.append("exceeds-production-" + item["mode"] + "-deadline")
        item.update(passed=not errors, errors=errors, productionParser=parsed, requestSeconds=round(elapsed, 6))
        write(output / "report.json", report)
        print(json.dumps({"id": item["id"], "passed": item["passed"], "seconds": item["requestSeconds"],
                          "errors": errors}, ensure_ascii=False), flush=True)
        if elapsed > deadline:
            report["status"] = "failed-deadline"; break
    else:
        report["status"] = "passed" if all(item.get("passed") for item in report["cases"]) else "failed"
    write(output / "report.json", report)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
