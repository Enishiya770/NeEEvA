"""Stage-one typed device-reference review from real receipts/dialogue evidence.

Compile the current production provider and parse/source gates. Without
--execute-model only prepare requests; no HTTP, device operations or Unity run.
Expected labels stay in a sidecar and every single model answer is retained.
--dialogue-evidence reconstructs the observed failed pending branch from the
actual ordered user words, released replies and prior review diagnostics. It
does not claim byte-identical historical wires or simulate a live Unity loop.
For final pending-operation authority, use validate_pending_device_authority_qwen:
this tool intentionally tests only the relation/source stage, not its conjunction.
"""
from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path
import re
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
from validate_standing_goal_planner_qwen import CORE, constant, method

PROVIDER = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.RoomDeviceGoalReview.cs"
PENDING_CONTEXT = "Assets/AIChatTookit/Scripts/Chat/ChatSample.PendingDeviceClarification.cs"


def reconstruct_dialogue_manifest(evidence_path: Path, output: Path) -> Path:
    """Freeze the reported session before any model answer; labels never enter inputs."""
    evidence_raw = evidence_path.read_bytes()
    evidence = strict_json(evidence_raw.decode("utf-8-sig"))
    pending_source = (ROOT / PENDING_CONTEXT).read_text(encoding="utf-8-sig")
    context_method = method(pending_source, "private JObject BuildPendingDeviceClarificationContext(")
    literal = r'"(?:\\.|[^"\\])*"'
    scope_match = re.search(r'\["scope"\]\s*=\s*(' + literal + r'(?:\s*\+\s*' + literal + r')*)', context_method)
    if scope_match is None:
        raise ValueError("Cannot project the exact production pending-context scope; do not invent it")
    pending_scope = "".join(json.loads(value) for value in re.findall(literal, scope_match.group(1)))
    snapshot_path = Path(evidence["source"]["path"])
    snapshot_raw = snapshot_path.read_bytes()
    if digest(snapshot_raw) != evidence["source"]["sha256"]:
        raise ValueError("Dialogue source snapshot no longer matches its read-only reconstruction")
    lines = snapshot_raw.split(b"\n")
    turns = evidence["turns"]
    by_turn = {row["userTurn"]: row for row in turns}
    if list(by_turn) != list(range(2, 17)):
        raise ValueError("Expected the complete frozen 15-turn dialogue, without selected answers")
    for turn in turns:
        user = turn["user"]
        if user["asrText"] not in lines[user["line"] - 1].decode("utf-8"):
            raise ValueError("Reconstructed user words are not present at their cited snapshot line")
    original_turn = by_turn[5]
    initial_review_event = next(event for event in original_turn["events"] if event["type"] == "device_review")
    initial_review = initial_review_event["value"]
    if initial_review["referentKind"] != "unbound" or "referentSources为空" not in initial_review["modelReason"]:
        raise ValueError("Expected the observed empty-history failure, not a synthetic source inventory")
    pending_events = [next(event for event in by_turn[index]["events"] if event["type"] == "pending_review") for index in range(6, 17)]
    pending_ids = {event["value"]["pendingId"] for event in pending_events}
    if len(pending_ids) != 1 or any(event["value"]["registered"] is not False for event in pending_events):
        raise ValueError("Expected all recorded unresolved turns to share one unregistered pending draft")
    pending_id = next(iter(pending_ids))
    # The proposal itself is copied from the logged rejected production decision.
    decision = None
    for raw_line in lines[original_turn["user"]["line"]:pending_events[0]["line"]]:
        if raw_line.startswith(b"[Room/DecisionValidation] "):
            detail = strict_json(raw_line.decode("utf-8").split("] ", 1)[1])
            candidate = strict_json(detail["candidate"])
            if candidate.get("deviceGoal"):
                decision = candidate
                break
    if decision is None:
        raise ValueError("Missing logged original device proposal")
    original = original_turn["user"]["asrText"]
    proposal = decision["deviceGoal"]
    if proposal["requestQuote"] not in original or proposal["desiredState"] != "off":
        raise ValueError("Original proposal/request provenance does not match")
    fixtures = output / "dialogue-inputs"
    fixtures.mkdir()
    cases = []
    common = {"verifiedDeviceHistory": [], "verifiedObservedDeviceHistory": [], "referentSources": []}

    def recent_before(index: int) -> list[dict]:
        recent = []
        for turn in turns:
            if turn["userTurn"] > index:
                break
            recent.append({"role": "user", "quotedText": turn["user"]["asrText"]})
            if turn["userTurn"] == index:
                break
            released = [event["value"] for event in turn["events"]
                        if event["type"] == "public_execution_input" and event["value"]]
            if not released:
                released = [event["text"] for event in turn["events"] if event["type"] == "tts_playback_completed"]
            if released:
                recent.append({"role": "assistant", "quotedText": "".join(released)[-1600:]})
        return recent[-4:]

    def add(case_id: str, context: dict, expected: dict, provenance: dict) -> None:
        write(fixtures / (case_id + ".input.json"), context)
        cases.append({"id": case_id, "input": case_id + ".input.json", "expected": expected, "provenance": provenance})

    expected_source = {"referentKind": "unbound", "referentSourceId": "none"}
    add("dialogue-turn-05-empty-history", dict(common, mode="new-proposal", currentUserText=original,
        proposal=copy.deepcopy(proposal), recentDialogue=recent_before(5)),
        dict(expected_source, verdict="approved", relation="new"),
        {"userLine": original_turn["user"]["line"], "reviewLine": initial_review_event["line"], "kind": "observed-session-reconstruction"})
    expected_relations = {6: ["clarify"], 7: ["clarify"], 8: ["clarify", "unrelated"],
        9: ["clarify", "unrelated"], 10: ["unrelated"], 11: ["unrelated"], 12: ["clarify"],
        13: ["unrelated"], 14: ["cancel"], 15: ["unrelated"], 16: ["cancel"]}
    updates, previous = [], initial_review
    for index, event in zip(range(6, 17), pending_events):
        turn = by_turn[index]
        latest = turn["user"]["asrText"]
        context = dict(common, mode="pending-proposal-update", pendingId=pending_id,
            status="waiting_clarification", registered=False, ownerCurrent=True,
            originalUserRequest=original, latestUserText=latest, currentUserText=latest,
            proposal=copy.deepcopy(proposal), actualUserUpdates=copy.deepcopy(updates[-6:]),
            authorizationHistoryComplete=len(updates) <= 6 and not any(row["textTruncated"] for row in updates),
            previousReview={"verdict": previous["verdict"], "reason": previous["modelReason"],
                "relation": previous.get("relation"), "registered": False},
            recentDialogue=recent_before(index), scope=pending_scope)
        add("dialogue-turn-" + str(index).zfill(2), context,
            dict(expected_source, verdict="approved", relation=expected_relations[index], pendingClarificationId=pending_id),
            {"userLine": turn["user"]["line"], "reviewLine": event["line"], "kind": "observed-failed-branch-reconstruction",
             "note": "Uses the actual recorded preceding failed reviews and real user words, not this replay's answers. A fixed branch test; no simulated runtime continuation after a new approval/cancellation."})
        updates.append({"userTurn": index, "text": latest, "fullTextLength": len(latest),
            "textTruncated": False, "source": "actual-accepted-user-text"})
        previous = event["value"]

    # Counterexamples are explicitly synthetic semantic controls, never labelled
    # as captured session words, and do not contribute expected text to the model.
    controls = [
        ("negative-original-identity", "不要关那盏灯。", [], "就是靠门的那盏。", ["rejected", "uncertain"]),
        ("observation-only-identity", "看看那盏灯是什么样子的。", [], "就是靠门的那盏。", ["rejected", "uncertain"]),
        ("cancelled-operation-identity", original, ["不要再关那盏灯了。"], "就是靠门的那盏。", ["rejected", "uncertain"]),
        ("replaced-operation-identity", original, ["不要关原来的灯，改成打开另一盏灯。"], "我说的是靠门的那盏。", ["rejected", "uncertain"]),
        ("affirmative-original-identity", "帮我熄灭那盏阅读灯。", [], "指的是书架旁那盏。", ["approved"]),
        ("affirmative-original-search", "帮我熄灭那盏阅读灯。", [], "先环顾一下，找到我说的同一盏。", ["approved"]),
        ("observe-with-explicit-no-change", "辨认一下那盏阅读灯，别改变开关状态。", [], "指的是书架旁那盏。", ["rejected", "uncertain"]),
        ("withdraw-then-search", "帮我熄灭那盏阅读灯。", ["刚刚的熄灯请求作废。"], "先环顾一下，找到我说的同一盏。", ["rejected", "uncertain"]),
        ("incomplete-history-search", "帮我熄灭那盏阅读灯。", ["先看看书架附近。"] * 6,
            "先环顾一下，找到我说的同一盏。", ["rejected", "uncertain"]),
        ("incomplete-history-new-permission", "帮我熄灭那盏阅读灯。", ["先看看书架附近。"] * 6,
            "是书架旁那盏，我现在明确要求你把它关掉。", ["approved"]),
    ]
    for name, control_original, control_updates, latest, verdicts in controls:
        control_id = "pending-control-" + name
        control_proposal = dict(proposal, requestQuote=control_original, targetDescription="原请求描述的那盏灯")
        context = dict(common, mode="pending-proposal-update", pendingId=control_id,
            originalUserRequest=control_original, latestUserText=latest, currentUserText=latest,
            proposal=control_proposal, actualUserUpdates=[{"userTurn": at + 2, "text": text,
                "fullTextLength": len(text), "textTruncated": False, "source": "actual-accepted-user-text"}
                for at, text in enumerate(control_updates)],
            previousReview={"verdict": "uncertain", "reason": "尚未确定执行实体。", "registered": False}, recentDialogue=[], scope=pending_scope)
        context["authorizationHistoryComplete"] = not name.startswith("incomplete-history-")
        add("control-" + name, context, dict(expected_source, verdict=verdicts,
            relation=["clarify", "new"] if name == "incomplete-history-new-permission" else "clarify",
            pendingClarificationId=control_id), {"kind": "synthetic-semantic-control"})
    manifest = {"cases": cases, "sourceEvidence": str(evidence_path), "sourceEvidenceSha256": digest(evidence_raw),
        "snapshotSha256": digest(snapshot_raw),
        "pendingContextSource": PENDING_CONTEXT, "pendingContextSourceSha256": digest((ROOT / PENDING_CONTEXT).read_bytes()),
        "pendingScopeSha256": digest(pending_scope.encode("utf-8")),
        "scope": "Twelve ordered semantic review snapshots reconstructed from this real dialogue's utterances, released speech, original proposal and review diagnostics; ten explicitly synthetic affirmative/non-authorization controls, including two incomplete-history cases. History completeness follows the current production six-row retention policy, with context built before adding latest text. Acoustic metadata, full raw transport and runtime persistence are not reproduced. Expected labels are sidecar-only. Every case is frozen before any model request."}
    path = fixtures / "manifest.json"
    write(path, manifest)
    return path


def compile_probe(output: Path) -> dict:
    provider, core = ((ROOT / path).read_text(encoding="utf-8-sig") for path in (PROVIDER, CORE))
    snippets = [method(provider, signature) for signature in (
        "private string BuildRoomDeviceGoalReviewRequestJson(",
        "private static string RoomDeviceGoalReviewMode(",
        "private static JArray RoomDeviceGoalReferentSources(",
        "private static bool UsesUnboundDeviceOperationReview(",
        "private static string BuildUnboundDeviceOperationReviewInput(",
        "private static bool TryNormalizeDeviceGoalReviewOutput(")]
    snippets += [method(core, signature) for signature in (
        "private void AppendSlot(", "private static void AppendMessage(", "private static void AppendJsonString(")]
    snippets += [constant(core, name) for name in ("k_AttachedImageNote", "k_ArchivedImageNote")]
    declarations = r'''using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
public class TypedReferentProbe {
    private enum BackendType { Local, Cloud }
    private BackendType m_Backend=BackendType.Local;
    private string CurrentModelName;
    private const int k_SlotAuxiliary=1;
    private sealed class SendData {
        public string role,content,imageDataUrl; public bool imageArchived;
        public SendData(string role,string content) { this.role=role; this.content=content; }
    }
    public static int Main(string[] args) {
        if(args.Length==3 && args[0]=="payload") {
            Console.Write(new TypedReferentProbe {CurrentModelName=args[2]}.BuildRoomDeviceGoalReviewRequestJson(File.ReadAllText(args[1])));return 0;
        }
        if(args.Length==3 && args[0]=="validate") {
            string input=File.ReadAllText(args[1]),response=File.ReadAllText(args[2]);
            RoomTaskProtocol.DeviceGoalReview review=null;
            string mode=RoomDeviceGoalReviewMode(input),reason="";
            bool update=mode=="goal-user-update";
            bool normalized=TryNormalizeDeviceGoalReviewOutput(input,response,out response,out reason);
            bool parsed=normalized && (mode=="pending-proposal-update" ? RoomTaskProtocol.TryPendingDeviceClarificationReview(response,out review) :
                update ? RoomTaskProtocol.TryDeviceGoalUserUpdateReview(response,out review) : RoomTaskProtocol.TryDeviceGoalProposalReview(response,out review));
            bool listed=parsed && (update || review.HasTypedReferent && RoomTaskProtocol.TryValidateListedDeviceReferent(review,RoomDeviceGoalReferentSources(input),out reason));
            Console.Write(JsonConvert.SerializeObject(new {parsed,listed,valid=parsed&&listed,review,reason}));return 0;
        }
        if(args.Length==2 && args[0]=="projection") {
            string input=File.ReadAllText(args[1]);
            Console.Write(UsesUnboundDeviceOperationReview(input)?BuildUnboundDeviceOperationReviewInput(input):input);return 0;
        }
        return 2;
    }
'''
    build = output / "parser"
    build.mkdir()
    projection = declarations + "\n\n".join(snippets) + "\n}"
    (build / "TypedReferentProbe.cs").write_text(projection, encoding="utf-8")
    shutil.copyfile(ROOT / NEWTONSOFT, build / "Newtonsoft.Json.dll")
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9",
        "/r:" + str(build / "Newtonsoft.Json.dll"), "/out:" + str(build / "probe.exe"),
        str(ROOT / PROTOCOL), str(build / "TypedReferentProbe.cs")],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (build / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Typed production provider/parser compile failed; inspect parser/compile.log")
    return {"compiledSourceProjectionSha256": digest(projection.encode("utf-8")),
            "productionMethodSha256": [digest(snippet.encode("utf-8")) for snippet in snippets],
            "scope": "Exact production pure provider methods, strict review parser and listed-source gate, with minimal Local backend/transport DTO stand-ins. No Unity HTTP/lifecycle."}


def probe(output: Path, *args: str) -> dict:
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(output / "parser/probe.exe"), *args],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if result.returncode:
        raise RuntimeError("Compiled production typed reference probe failed")
    return strict_json(result.stdout.strip().lstrip("\ufeff"))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--manifest", type=Path)
    source.add_argument("--dialogue-evidence", type=Path,
        help="Reconstruct the complete observed empty-history/pending dialogue branch plus non-authorization controls")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--model", default="antoneva")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    manifest_path = reconstruct_dialogue_manifest(args.dialogue_evidence.resolve(), output) if args.dialogue_evidence else args.manifest.resolve()
    manifest_raw = manifest_path.read_bytes()
    manifest = strict_json(manifest_raw.decode("utf-8-sig"))
    source_files = (PROTOCOL, PROVIDER, CORE, NEWTONSOFT) + ((PENDING_CONTEXT,) if args.dialogue_evidence else ())
    sources = {name: digest((ROOT / name).read_bytes()) for name in source_files}
    report = {"status": "preparing", "sources": sources, "compilation": compile_probe(output),
              "scriptSha256": digest(Path(__file__).read_bytes()), "manifestSha256": digest(manifest_raw),
              "sourceManifest": str(manifest_path), "cases": [], "modelRequests": 0,
              "scope": "Production text-only relation/source review stage only; pending approved clarify still requires the separate operation authority gate. " + manifest.get("scope", "Inputs are real Unity receipt exports.") +
                  " No new visual matching, live actions, authorization persistence or TTS execution.",
              "policy": "One serial request per case using production payload/parser/source gate. No retries, repair, best-of, prompt editing or expected labels in model input."}
    prepared, ids = [], set()
    for fixture in manifest["cases"]:
        case_id = fixture["id"]
        if not case_id or any(char not in "abcdefghijklmnopqrstuvwxyz0123456789-" for char in case_id) or case_id in ids:
            raise ValueError("Expected unique export case IDs")
        ids.add(case_id)
        input_path = manifest_path.parent / fixture["input"]
        input_raw = input_path.read_bytes()
        context = strict_json(input_raw.decode("utf-8-sig"))
        if context.get("mode") not in ("new-proposal", "pending-proposal-update", "goal-user-update") or not isinstance(context.get("referentSources"), list):
            raise ValueError("Expected a typed production review with request-local sources")
        copied_input = output / (case_id + ".input.json")
        copied_input.write_bytes(input_raw)
        payload = probe(output, "payload", str(copied_input), args.model)
        projected = probe(output, "projection", str(copied_input))
        if strict_json(payload["messages"][1]["content"]) != projected or len(payload["messages"]) != 2:
            raise ValueError("Production serializer changed exported review context")
        if payload.get("temperature") != 0 or payload.get("id_slot") != 1:
            raise ValueError("Unexpected production review settings")
        write(output / (case_id + ".request.json"), payload)
        item = {"id": case_id, "sourceInput": str(input_path), "inputSha256": digest(input_raw), "expected": fixture["expected"],
                "provenance": fixture.get("provenance", {"kind": "unity-export"})}
        if fixture.get("request"):
            recorded_raw = (manifest_path.parent / fixture["request"]).read_bytes()
            recorded = strict_json(recorded_raw.decode("utf-8-sig"))
            # The explicit CLI model names the same request against the selected
            # existing endpoint. Every other serialized setting/field must match.
            recorded["model"] = args.model
            if recorded != payload:
                raise ValueError("Compiled production wire differs from the actual Unity-exported wire")
            item["recordedWireSha256"] = digest(recorded_raw)
            item["recordedWireEquivalentExceptExplicitModel"] = True
        report["cases"].append(item)
        prepared.append((item, payload, copied_input))
    if not prepared:
        raise ValueError("No exported typed review cases")
    report["status"] = "prepared"
    write(output / "report.json", report)
    if not args.execute_model:
        print(json.dumps({"status": "prepared", "cases": len(prepared), "modelRequests": 0}))
        return 0
    try:
        for item, payload, input_path in prepared:
            if {name: digest((ROOT / name).read_bytes()) for name in sources} != sources:
                report["status"] = "deferred-sources-changed"
                break
            if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
                report["status"] = "deferred-busy-or-unknown"
                break
            request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions",
                json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
                {"Content-Type": "application/json", "Authorization": "Bearer ollama"})
            errors, parsed = [], None
            started = time.perf_counter()
            report["modelRequests"] += 1
            write(output / "report.json", report)
            try:
                with urllib.request.urlopen(request, timeout=12) as response:
                    response_raw = response.read()
                (output / (item["id"] + ".envelope.json")).write_bytes(response_raw)
                envelope = strict_json(response_raw.decode("utf-8-sig"))
                choices = envelope.get("choices", [])
                choice = choices[0] if len(choices) == 1 else {}
                raw = choice.get("message", {}).get("content", "")
                if choice.get("finish_reason") != "stop" or not isinstance(raw, str):
                    raise ValueError("Incomplete public model output")
                response_path = output / (item["id"] + ".response.json")
                response_path.write_text(raw, encoding="utf-8")
                if visible_content(raw) != raw.strip():
                    errors.append("hidden-block-in-public-output")
                parsed = probe(output, "validate", str(input_path), str(response_path))
                if not parsed.get("valid"):
                    errors.append("production-parser-or-source-gate-rejected:" + str(parsed.get("reason")))
                else:
                    item["oldPendingOperationApproved"] = (
                        strict_json(input_path.read_text(encoding="utf-8-sig")).get("mode") == "pending-proposal-update"
                        and parsed["review"].get("verdict") == "approved" and parsed["review"].get("relation") == "clarify")
                    for key, expected in item["expected"].items():
                        actual = parsed["review"].get(key)
                        if not (actual in expected if isinstance(expected, list) else actual == expected):
                            errors.append("expected-" + key + ":" + str(expected))
            except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError, IndexError, RuntimeError) as error:
                errors.append(type(error).__name__ + ":" + str(error))
            item.update(passed=not errors, errors=errors, productionParser=parsed,
                        requestSeconds=round(time.perf_counter() - started, 6))
            write(output / "report.json", report)
            print(json.dumps({"id": item["id"], "passed": item["passed"], "errors": errors}, ensure_ascii=False), flush=True)
        else:
            report["status"] = "passed" if all(item.get("passed") for item in report["cases"]) else "failed"
    except (urllib.error.URLError, TimeoutError, ValueError) as error:
        report.update(status="unavailable", error=type(error).__name__ + ":" + str(error))
    finally:
        write(output / "report.json", report)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
