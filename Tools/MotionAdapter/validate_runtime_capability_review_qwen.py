"""Replay untouched Unity-exported factual speech reviews once each.

Expected verdicts remain in the export manifest, outside model messages. Default
mode verifies exports and compiles the actual provider/parser without HTTP.
--execute-model requires the passing upstream work-feedback report; no Unity,
action execution, model startup, retries, payload edits or answer repair.
"""
from __future__ import annotations

import argparse
import base64
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
from validate_standing_goal_planner_qwen import constant, method

PROVIDER = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.RoomTask.cs"
CORE = "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.cs"
CASES = (
    ("unsupported-no-physical-hand", "inconsistent"),
    ("unsupported-approval-means-no-capability", "inconsistent"),
    ("supported-runtime-control-with-unconfirmed-target", "consistent"),
)
SOURCES = (PROTOCOL, NEWTONSOFT, PROVIDER, CORE,
           "Assets/AIChatTookit/Scripts/Chat/ChatSample.SceneObjects.cs",
           "Assets/AIChatTookit/Scripts/Chat/ChatSample.WorkSceneFeedback.cs",
           "Assets/Editor/WorkSceneFeedbackRegression.cs",
           "Assets/Editor/InputRecoveryRoomRegression.cs",
           "Assets/AIChatTookit/Scripts/Chat/ChatSample.PendingDeviceClarification.cs")


def sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def write(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + "\n", encoding="utf-8")


def hashes() -> dict:
    return {name: sha((ROOT / name).read_bytes()) for name in SOURCES}


def compile_probe(output: Path) -> dict:
    provider = (ROOT / PROVIDER).read_text(encoding="utf-8-sig")
    core = (ROOT / CORE).read_text(encoding="utf-8-sig")
    snippets = [method(provider, signature) for signature in (
        "private string BuildRoomTaskRequestJsonWithObservationHistory(",
        "private static bool TryReadRoomHistoryMetadata(",
        "private static string BuildRoomDecisionFieldGuide(",
        "private static string BuildRoomDecisionSchemaForInput(",
        "private static bool IsReviewedRoomGoalContinuation(",
        "private static string BuildRoomDecisionCurrentTurnMessage(",
        "private static JArray BuildRoomDecisionReferenceDialogue(",
        "private static JObject BuildRoomTaskIncompleteDiagnostic(",
        "private static bool TryReadRoomTaskCompletion(")]
    snippets += [method(core, signature) for signature in (
        "private void AppendSlot(", "private static void AppendMessage(", "private static void AppendJsonString(")]
    snippets += [constant(core, name) for name in ("k_AttachedImageNote", "k_ArchivedImageNote")]
    declarations = r'''using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
public class RuntimeCapabilityReviewProbe {
    private static class Debug { public static void LogWarning(object value) {} }
    private enum BackendType { Local, Cloud }
    private BackendType m_Backend=BackendType.Local;
    private string CurrentModelName;
    private const int k_SlotAuxiliary=1;
    private sealed class SendData {
        public string role,content,imageDataUrl; public bool imageArchived;
        public SendData(string role,string content) { this.role=role; this.content=content; }
    }
    private sealed class RoomObservationImage { public string CaptureId,MetadataJson,ImageDataUrl; }
    public static int Main(string[] args) {
        Console.InputEncoding=new UTF8Encoding(false);
        Console.OutputEncoding=new UTF8Encoding(false);
        if(args.Length==1 && args[0]=="contracts") {
            Console.Write(JsonConvert.SerializeObject(new {contract=RoomTaskProtocol.ReviewContract,
                schema=RoomTaskProtocol.ReviewSchema, imageNote=k_AttachedImageNote}));return 0;
        }
        if(args.Length==4 && args[0]=="payload") {
            Console.Write(new RuntimeCapabilityReviewProbe {CurrentModelName=args[2]}
                .BuildRoomTaskRequestJsonWithObservationHistory(File.ReadAllText(args[1]),File.ReadAllText(args[3]),null,true));return 0;
        }
        if(args.Length==1 && (args[0]=="review" || args[0]=="envelope")) {
            string raw=Console.In.ReadToEnd(), output=raw, status="direct-review", scope;
            bool accepted=args[0]=="review" || TryReadRoomTaskCompletion(raw,out output,out status);
            bool valid=RoomTaskProtocol.TryReviewScope(output,out scope);
            Console.Write(JsonConvert.SerializeObject(new {accepted,status,valid=accepted && valid,scope,
                allows=accepted && RoomTaskProtocol.ReviewAllows(output),
                review=accepted && valid ? JObject.Parse(output) : null}));return 0;
        }
        return 2;
    }
'''
    projection = declarations + "\n\n".join(snippets) + "\n}"
    build = output / "parser"
    build.mkdir()
    (build / "Probe.cs").write_text(projection, encoding="utf-8")
    shutil.copyfile(ROOT / NEWTONSOFT, build / "Newtonsoft.Json.dll")
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9",
        "/r:" + str(build / "Newtonsoft.Json.dll"), "/out:" + str(build / "probe.exe"),
        str(ROOT / PROTOCOL), str(build / "Probe.cs")], capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=45)
    (build / "compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Production review provider/parser compilation failed; inspect parser/compile.log")
    return {"projectionSha256": sha(projection.encode("utf-8")),
            "productionMethodSha256": [sha(value.encode("utf-8")) for value in snippets],
            "scope": "Exact production payload, completion-envelope and strict Review parsers with minimal Local transport DTOs and a silent diagnostic sink; no Unity lifecycle or HTTP."}


def probe(output: Path, *args: str, stdin: str | None = None) -> str:
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(output / "parser/probe.exe"), *args],
        input=stdin, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if result.returncode:
        raise RuntimeError("Compiled production review probe failed")
    return result.stdout.strip().lstrip("\ufeff")


def guards(output: Path) -> list[dict]:
    base = '{"verdict":"inconsistent","reason":"unsupported capability denial","factScope":"scene-action"}'
    samples = [("strict-current-review", base, True),
               ("legacy-two-fields-remains-supported", '{"verdict":"consistent","reason":"factual"}', True),
               ("duplicate-verdict-rejected", base[:-1] + ',"verdict":"consistent"}', False),
               ("extra-action-rejected", base[:-1] + ',"action":"set-object-state"}', False),
               ("null-scope-rejected", base.replace('"scene-action"', 'null'), False),
               ("invalid-verdict-rejected", base.replace('"inconsistent"', '"approved"'), False)]
    rows = [{"id": name, "passed": strict_json(probe(output, "review", stdin=raw))["valid"] is expected}
            for name, raw, expected in samples]
    envelope = {"choices": [{"finish_reason": "length", "message": {"role": "assistant", "content": base}}]}
    rows.append({"id": "incomplete-envelope-rejected", "passed":
                 strict_json(probe(output, "envelope", stdin=json.dumps(envelope)))["valid"] is False})
    envelope["choices"][0]["finish_reason"] = "stop"
    parsed = strict_json(probe(output, "envelope", stdin=json.dumps(envelope)))
    rows.append({"id": "ascii-envelope-positive", "passed": parsed["accepted"] is True and
                 parsed["valid"] is True and parsed["review"]["verdict"] == "inconsistent"})
    envelope["choices"][0]["message"]["tool_calls"] = []
    rows.append({"id": "tool-envelope-rejected", "passed":
                 strict_json(probe(output, "envelope", stdin=json.dumps(envelope)))["valid"] is False})
    unicode_reason = "已连接的软件接口无需用手按开关。ちゃんと消したという結果は未確認。"
    unicode_review = {"verdict": "inconsistent", "reason": unicode_reason, "factScope": "scene-action"}
    parsed = strict_json(probe(output, "review", stdin=json.dumps(unicode_review, ensure_ascii=False)))
    rows.append({"id": "unicode-review-stdin-roundtrip", "passed": parsed["valid"] is True and
                 parsed["review"]["reason"] == unicode_reason})
    envelope = {"choices": [{"finish_reason": "stop", "message": {"role": "assistant",
                "content": json.dumps(unicode_review, ensure_ascii=False)}}]}
    parsed = strict_json(probe(output, "envelope", stdin=json.dumps(envelope, ensure_ascii=False)))
    rows.append({"id": "unicode-envelope-stdin-roundtrip", "passed": parsed["valid"] is True and
                 parsed["review"]["reason"] == unicode_reason})
    return rows


def prepare_exports(exported: Path, output: Path, contracts: dict, upstream: Path,
                    recovery_report: Path | None) -> tuple[list[dict], dict]:
    upstream_raw = upstream.read_bytes()
    upstream_report = strict_json(upstream_raw.decode("utf-8-sig"))
    if upstream_report.get("status") != "passed":
        raise ValueError("The upstream work-feedback fixture must pass before its requests are replayed")
    manifest_raw = (exported / "manifest.json").read_bytes()
    manifest = strict_json(manifest_raw.decode("utf-8-sig"))
    rows, export_hashes = [], {str(exported / "manifest.json"): sha(manifest_raw), str(upstream): sha(upstream_raw)}
    selections = []
    for name, expected in CASES:
        matches = [case for case in manifest["cases"] if case["name"] == name]
        if len(matches) != 1 or matches[0]["expectedVerdict"] != expected:
            raise ValueError("Missing, duplicate or changed expected label: " + name)
        selections.append((exported, matches[0]))
    if recovery_report is not None:
        recovery_raw = recovery_report.read_bytes()
        recovery = strict_json(recovery_raw.decode("utf-8-sig"))
        records = [case for case in recovery.get("cases", []) if isinstance(case, dict) and
                   case.get("name") == "actual-no-speech-recovery-unapproved-draft-false-success"]
        if recovery.get("status") != "passed" or len(records) != 1 or records[0].get("duplicateUserMessages") != 0 or records[0].get("falseSuccessTtsRequests") != 0:
            raise ValueError("Require the passed actual empty-input recovery fixture and its unperformed claim evidence")
        export_hashes[str(recovery_report)] = sha(recovery_raw)
        selections.append((recovery_report.parent, {
            "name": "input-recovery-false-device-success", "expectedVerdict": "inconsistent",
            "candidate": "あなたの言う通りに、ちゃんと消したから。",
            "captureId": records[0]["recoveryCaptureId"], "requestFile": "false-success-review.request.json"}))
    for directory, case in selections:
        name, expected = case["name"], case["expectedVerdict"]
        request_path = (directory / case["requestFile"]).resolve()
        if request_path.parent != directory:
            raise ValueError("Request must be directly inside the export directory")
        raw = request_path.read_bytes()
        wire = strict_json(raw.decode("utf-8-sig"))
        messages = wire["messages"]
        if wire.get("stream") is not False or len(messages) != 2 or messages[0]["role"] != "system" or messages[1]["role"] != "user":
            raise ValueError("Require the exact two-message non-streaming production review")
        if messages[0]["content"] != contracts["contract"] + "\n" + contracts["schema"] or wire["response_format"]["schema"] != strict_json(contracts["schema"]):
            raise ValueError("Exported review contract/schema differs from current compiled production")
        parts = messages[1]["content"]
        images = [part["image_url"]["url"] for part in parts if part.get("type") == "image_url"]
        texts = [part["text"] for part in parts if part.get("type") == "text"]
        if len(images) != 1 or len(texts) != 1 or not texts[0].startswith(contracts["imageNote"]):
            raise ValueError("Require one exact production current-image attachment and factual input")
        input_text = texts[0][len(contracts["imageNote"]):]
        facts = strict_json(input_text)
        image_header, image_payload = images[0].split(",", 1)
        extensions = {"data:image/jpeg;base64": "jpg", "data:image/png;base64": "png"}
        if image_header not in extensions:
            raise ValueError("Require the original supported real RGB image encoding")
        rgb = base64.b64decode(image_payload, validate=True)
        if not rgb or facts["publicSpeech"] != case["candidate"] or facts["visualObservation"]["captureId"] != case["captureId"] or facts["visualObservation"].get("source") != "character-scene-rgb":
            raise ValueError("Candidate or captured image ownership differs from the export manifest")
        control = facts["sceneActionFacts"]["objectContext"]["runtimeDeviceControl"]
        if control["implementationConnected"] is not True or control["featureEnabled"] is not True or control["physicalHandRequired"] is not False:
            raise ValueError("The actual input does not contain the connected enabled nonphysical control interface")
        (output / (name + ".input.json")).write_text(input_text, encoding="utf-8")
        (output / (name + ".image-url.txt")).write_text(images[0], encoding="utf-8")
        rebuilt = probe(output, "payload", str(output / (name + ".input.json")), wire["model"],
                        str(output / (name + ".image-url.txt"))).encode("utf-8")
        if rebuilt != raw:
            raise ValueError("Export is not byte-identical to its compiled current provider request: " + name)
        (output / (name + ".request.json")).write_bytes(raw)
        image_file = name + ".rgb." + extensions[image_header]
        (output / image_file).write_bytes(rgb)
        export_hashes[str(request_path)] = sha(raw)
        rows.append({"id": name, "expectedVerdict": expected, "captureId": case["captureId"],
                     "requestSha256": sha(raw), "rgbSha256": sha(rgb), "rgbFile": image_file, "model": wire["model"]})
    return rows, export_hashes


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--export", type=Path)
    parser.add_argument("--upstream-report", type=Path)
    parser.add_argument("--recovery-report", type=Path,
                        help="Append the exact false-success review from a passed InputRecoveryRoom fixture")
    parser.add_argument("--prior-adapter-report", type=Path,
                        help="Retain the earlier Windows-console adapter failure and require identical requests/labels")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--compile-only", action="store_true")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    if args.compile_only and args.execute_model:
        parser.error("--compile-only cannot send model requests")
    if not args.compile_only and (args.export is None or args.upstream_report is None):
        parser.error("--export and --upstream-report are required for an exact export replay")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    source_hashes = hashes()
    projection = compile_probe(output)
    contracts = strict_json(probe(output, "contracts"))
    write(output / "production-contracts.json", contracts)
    local_guards = guards(output)
    rows, export_hashes = ([], {}) if args.compile_only else prepare_exports(args.export.resolve(), output, contracts,
        args.upstream_report.resolve(), args.recovery_report.resolve() if args.recovery_report is not None else None)
    report = {"status": "prepared" if all(row["passed"] for row in local_guards) else "failed",
              "scope": "Three unchanged actual Unity-produced capability speech reviews and optionally one actual empty-input recovery false-success review, all with real room RGB; tests current Qwen review judgments, not device execution or end-to-end task completion.",
              "policy": "One serial request per case; exact request bytes, no retries, payload changes, repair, answer selection, model startup or Unity actions. Expected labels remain separate.",
              "sources": source_hashes, "scriptSha256": sha(Path(__file__).read_bytes()), "projection": projection,
              "localGuards": local_guards, "exportHashes": export_hashes, "cases": rows, "results": [], "modelRequests": 0}
    if args.prior_adapter_report is not None:
        prior_path = args.prior_adapter_report.resolve()
        prior_raw = prior_path.read_bytes()
        prior = strict_json(prior_raw.decode("utf-8-sig"))
        if prior.get("status") != "failed" or [(case["id"], case["expectedVerdict"], case["requestSha256"], case["rgbSha256"]) for case in prior["cases"]] != [(case["id"], case["expectedVerdict"], case["requestSha256"], case["rgbSha256"]) for case in rows]:
            raise ValueError("The preserved earlier attempt must have identical request bytes, RGB and expected labels")
        report["priorAdapterAttempt"] = {"path": str(prior_path), "sha256": sha(prior_raw),
            "status": prior["status"], "modelRequests": prior["modelRequests"],
            "cause": "Windows Mono Console stdin/stdout defaults did not match Python UTF-8. Offline positive envelope guards isolated this adapter fault. Original failure/public files remain untouched; no model-answer or prompt adjustment."}
    write(output / "report.json", report)
    if not args.execute_model or report["status"] == "failed":
        print(json.dumps({"status": report["status"], "cases": len(rows), "modelRequests": 0, "localGuards": local_guards}, ensure_ascii=False))
        return 0 if report["status"] == "prepared" else 1
    report["status"] = "running"
    try:
        for case in rows:
            if hashes() != source_hashes:
                raise RuntimeError("Production source changed since export verification; no remaining requests sent")
            if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
                report["status"] = "deferred"
                break
            raw = (output / (case["id"] + ".request.json")).read_bytes()
            if sha(raw) != case["requestSha256"]:
                raise RuntimeError("Frozen request bytes changed; no request sent")
            errors, parsed, http_status, envelope_hash = [], None, None, None
            started = time.perf_counter()
            report["modelRequests"] += 1
            write(output / "report.json", report)
            request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions", raw, {"Content-Type": "application/json"})
            try:
                with urllib.request.urlopen(request, timeout=90) as response:
                    envelope_raw, http_status = response.read(), response.status
                # Retain original HTTP bytes before decoding or any production
                # completion parser; public JSON is also kept separately below.
                (output / (case["id"] + ".envelope.json")).write_bytes(envelope_raw)
                envelope_hash = sha(envelope_raw)
                envelope_text = envelope_raw.decode("utf-8")
                envelope = strict_json(envelope_text)
                parsed = strict_json(probe(output, "envelope", stdin=envelope_text))
                if http_status != 200 or not parsed["valid"]:
                    errors.append("production-completion-or-review-parser-rejected")
                choices = envelope.get("choices", [])
                choice = choices[0] if len(choices) == 1 else {}
                content = choice.get("message", {}).get("content", "")
                public = visible_content(content) if isinstance(content, str) else ""
                if not isinstance(content, str) or public != content.strip():
                    errors.append("invalid-or-hidden-public-content")
                (output / (case["id"] + ".response.json")).write_text(public, encoding="utf-8")
                if parsed["valid"] and (parsed["review"]["verdict"] != case["expectedVerdict"] or parsed["allows"] is not (case["expectedVerdict"] == "consistent")):
                    errors.append("actual-verdict-does-not-match-separate-expected-label")
                write(output / (case["id"] + ".parsed.json"), parsed)
            except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError, RuntimeError) as error:
                errors.append(type(error).__name__ + ":" + str(error))
            row = {"id": case["id"], "passed": not errors, "errors": errors, "parsed": parsed,
                   "httpStatus": http_status, "envelopeSha256": envelope_hash,
                   "requestSeconds": round(time.perf_counter() - started, 6)}
            report["results"].append(row)
            write(output / "report.json", report)
            print(json.dumps(row, ensure_ascii=False), flush=True)
        else:
            report["status"] = "passed" if all(row["passed"] for row in report["results"]) else "failed"
    except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError, RuntimeError) as error:
        report["status"] = "failed"
        report["error"] = type(error).__name__ + ":" + str(error)
    finally:
        report["sourcesAfter"] = hashes()
        report["sourcesUnchanged"] = report["sourcesAfter"] == source_hashes
        if not report["sourcesUnchanged"]:
            report["status"] = "failed"
        write(output / "report.json", report)
        print(json.dumps({"status": report["status"], "modelRequests": report["modelRequests"], "output": str(output)}, ensure_ascii=False), flush=True)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
