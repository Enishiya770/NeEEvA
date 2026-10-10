"""Replay exact exported pending-dialogue planner requests once per case.

Default prepares only: compile the production payload/parser, preserve the exact
Unity input/image, and compare its rebuilt payload with the actual exported wire.
Expected decisions remain sidecar data. No Unity, actions, service startup, retry,
response repair or best-of selection. Preserve every exported historical image.
An explicitly derived reviewed-goal probe is reported separately from untouched
requests; it tests whether a newly approved goal still respects "turn first".
"""
from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path
import subprocess
import sys
import time
import urllib.error
import urllib.request

from validate_autonomous_dialogue_motion import visible_content
from validate_device_goal_semantics_qwen import digest, write
from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import ROOT, PROTOCOL, NEWTONSOFT, MONO, strict_json
from validate_standing_goal_planner_qwen import CORE, PLANNER, compile_payload_probe, probe


def compile_history_payload_probe(output: Path) -> dict:
    """Extend only the standalone DTO entrypoint, retaining production methods."""
    metadata = compile_payload_probe(output)
    harness = output / "parser/PlannerWireProbe.cs"
    text = harness.read_text(encoding="utf-8")
    old = 'if(args.Length==4 && args[0]=="payload")'
    call = 'File.ReadAllText(args[3]),null,false)'
    if text.count(old) != 1 or text.count(call) != 1:
        raise ValueError("Production projection entrypoint changed; refuse an unverified adapter")
    helper = '''    private static IReadOnlyList<RoomObservationImage> ReadHistory(string path) {
        if(string.IsNullOrEmpty(path)) return null;
        var result=new List<RoomObservationImage>();
        foreach(JObject frame in JArray.Parse(File.ReadAllText(path)))
            result.Add(new RoomObservationImage { CaptureId=(string)frame["captureId"],
                MetadataJson=(string)frame["metadataJson"],ImageDataUrl=(string)frame["imageDataUrl"] });
        return result;
    }
'''
    text = text.replace(old, 'if((args.Length==4 || args.Length==5) && args[0]=="payload")')
    text = text.replace(call, 'File.ReadAllText(args[3]),ReadHistory(args.Length==5?args[4]:null),false)')
    text = text.replace('    public static int Main(string[] args)', helper + '    public static int Main(string[] args)')
    reviewed_entry = '''        if(args.Length==4 && args[0]=="reviewed-decision") {
            bool valid=RoomTaskProtocol.TryReviewedGoalStepDecision(File.ReadAllText(args[1]),File.ReadAllText(args[2]),File.ReadAllText(args[3]),out var decision,out string reason);
            Console.Write(JsonConvert.SerializeObject(new {valid,decision,reason}));return 0;
        }
'''
    decision_entry = '        if(args.Length==3 && args[0]=="decision")'
    if text.count(decision_entry) != 1:
        raise ValueError("Production decision projection entrypoint changed")
    text = text.replace(decision_entry, reviewed_entry + decision_entry)
    harness.write_text(text, encoding="utf-8")
    build = output / "parser"
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9",
        "/r:" + str(build / "Newtonsoft.Json.dll"), "/out:" + str(build / "probe.exe"), str(ROOT / PROTOCOL), str(harness)],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (build / "history-adapter-compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise ValueError("History-aware production projection compilation failed")
    metadata["baseProjectionSha256"] = metadata["compiledSourceProjectionSha256"]
    metadata["compiledSourceProjectionSha256"] = digest(text.encode("utf-8"))
    metadata["historyAdapterSha256"] = digest(helper.encode("utf-8"))
    metadata["reviewedDecisionAdapterSha256"] = digest(reviewed_entry.encode("utf-8"))
    metadata["scope"] += " The standalone entrypoint also reads exported RoomObservationImage DTOs and passes them to the unchanged production builder."
    return metadata


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--model", help="Explicitly override only the exported model field")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    manifest_path = args.manifest.resolve()
    raw_manifest = manifest_path.read_bytes()
    manifest = strict_json(raw_manifest.decode("utf-8-sig"))
    (output / "source-manifest.json").write_bytes(raw_manifest)
    source_paths = (PROTOCOL, PLANNER, CORE, NEWTONSOFT)
    sources = {name: digest((ROOT / name).read_bytes()) for name in source_paths}
    report = {"status": "preparing", "sourceManifest": str(manifest_path),
              "sourceManifestSha256": digest(raw_manifest), "sources": sources,
              "scriptSha256": digest(Path(__file__).read_bytes()), "cases": [], "modelRequests": 0,
              "scope": "Single-shot semantic replay of actual Unity planner inputs and separately labeled derived probes over exact frozen images and production wire. Preceding review outcomes were controlled. No live scene execution or end-to-end task-success claim."}
    prepared = []
    try:
        report["compilation"] = compile_history_payload_probe(output)
        case_ids = set()
        for case in manifest["cases"]:
            case_id = case["id"]
            if not isinstance(case_id, str) or not case_id or any(c not in "abcdefghijklmnopqrstuvwxyz0123456789-" for c in case_id) or case_id in case_ids:
                raise ValueError("Invalid or duplicate case ID")
            case_ids.add(case_id)
            raw = {name: (manifest_path.parent / case[name]).read_bytes()
                   for name in ("input", "image", "request", "observationHistory")}
            context = strict_json(raw["input"].decode("utf-8-sig"))
            image = raw["image"].decode("utf-8-sig")
            exported = strict_json(raw["request"].decode("utf-8-sig"))
            history = strict_json(raw["observationHistory"].decode("utf-8-sig"))
            expected = case["expected"]
            mode = case.get("mode", "current-only")
            if not isinstance(history, list) or len(history) != case["historyCount"]:
                raise ValueError(case_id + ": historical attachment count mismatch")
            if not image.startswith("data:image/") or context.get("autonomous") is not False:
                raise ValueError(case_id + ": require an actual image and user planner")
            if mode == "current-only":
                if context.get("currentRequestOnly") is not True or context.get("quietSpatialGoalStep") is not False or context.get("spatialGoal") is not None:
                    raise ValueError(case_id + ": an executing old goal must not supply authority")
                if context.get("latestUserText") != context.get("currentUserText") or not context.get("currentUserText"):
                    raise ValueError(case_id + ": the complete latest accepted request must remain the planning source")
                if context.get("latestGoalUpdate", {}).get("cancellationApplied") is not expected["cancellationApplied"]:
                    raise ValueError(case_id + ": actual cancellation outcome disagrees with the sidecar")
            elif mode == "reviewed-pending-turn":
                goal = context.get("spatialGoal") or {}
                if not case.get("requestOrigin", "").startswith("derived-"):
                    raise ValueError(case_id + ": derived approved-goal fixture must not be labeled an untouched request")
                if context.get("currentRequestOnly") is True or context.get("quietSpatialGoalStep") is not True or context.get("decisionContractFacts", {}).get("deviceGoalAlreadyReviewed") is not True:
                    raise ValueError(case_id + ": require a reviewed standing-goal planner")
                if goal.get("ownerCurrent") is not True or goal.get("executionCurrent") is not True or goal.get("status") not in ("active", "searching") or not goal.get("goalId") or goal.get("desiredState") != "off" or goal.get("originalRequest") != context.get("currentUserText"):
                    raise ValueError(case_id + ": preserve actual original approved off objective")
                if not context.get("latestUserText") or goal.get("latestUserText") != context["latestUserText"]:
                    raise ValueError(case_id + ": latest turn-first request must also reach the goal context")
                for name in ("originalInput", "originalRequest"):
                    raw[name] = (manifest_path.parent / case[name]).read_bytes()
                original_input = strict_json(raw["originalInput"].decode("utf-8-sig"))
                for field in ("visualObservation", "currentUserText"):
                    if context.get(field) != original_input.get(field):
                        raise ValueError(case_id + ": derived input altered original capture or operation authority")
                for field in ("goalId", "originalRequest", "desiredState", "boundEntityId"):
                    if goal.get(field) != (original_input.get("spatialGoal") or {}).get(field):
                        raise ValueError(case_id + ": derived input altered actual goal identity or desired state")
            else:
                raise ValueError(case_id + ": unknown fixture mode")
            if context.get("visualObservation", {}).get("captureId") != case["sourceCaptureId"]:
                raise ValueError(case_id + ": exported current capture mismatch")
            input_path = output / (case_id + ".input.json")
            image_path = output / (case_id + ".image-data-url.txt")
            input_path.write_bytes(raw["input"])
            image_path.write_bytes(raw["image"])
            (output / (case_id + ".exported-request.json")).write_bytes(raw["request"])
            history_path = output / (case_id + ".history.json")
            history_path.write_bytes(raw["observationHistory"])
            for name in ("originalInput", "originalRequest"):
                if name in raw:
                    (output / (case_id + "." + name + ".json")).write_bytes(raw[name])
            authority = output / (case_id + ".authority.txt")
            authority.write_text(context["currentUserText"], encoding="utf-8")
            latest_authority = output / (case_id + ".latest-user.txt")
            latest_authority.write_text(context["latestUserText"], encoding="utf-8")
            model = args.model or exported["model"]
            payload = probe(output, "payload", str(input_path), model, str(image_path), str(history_path))
            comparison = copy.deepcopy(exported)
            if args.model is not None:
                comparison["model"] = args.model
            if payload != comparison:
                write(output / (case_id + ".rebuilt-request.json"), payload)
                raise ValueError(case_id + ": production rebuild is not JSON-equivalent to the exported wire")
            attached = [part.get("image_url", {}).get("url") for message in payload["messages"]
                        if isinstance(message.get("content"), list) for part in message["content"]
                        if part.get("type") == "image_url"]
            if not attached or attached[-1] != image or attached.count(image) != 1:
                raise ValueError(case_id + ": exact current image must appear once after any actual history attachments")
            write(output / (case_id + ".request.json"), payload)
            item = {"id": case_id, "mode": mode, "requestOrigin": case.get("requestOrigin", "actual-unmodified-planner-request"),
                    "fixtureFocus": case.get("fixtureFocus"), "expected": expected,
                    "sourceFiles": {name: {"path": str((manifest_path.parent / case[name]).resolve()), "sha256": digest(data)} for name, data in raw.items()},
                    "wireEquivalent": True, "historyCount": len(history), "attachedImageCount": len(attached), "sourceCaptureId": case["sourceCaptureId"]}
            report["cases"].append(item)
            prepared.append((item, payload, authority, latest_authority, context))
        if not prepared:
            raise ValueError("Manifest contains no replay cases")
        report["status"] = "prepared"
        write(output / "report.json", report)
        if not args.execute_model:
            print(json.dumps({"status": "prepared", "cases": len(prepared), "modelRequests": 0}))
            return 0
        for item, payload, authority, latest_authority, context in prepared:
            if {name: digest((ROOT / name).read_bytes()) for name in source_paths} != sources:
                report["status"] = "deferred-sources-changed"
                break
            if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
                report["status"] = "deferred-busy-or-unknown"
                break
            errors = []
            parsed = None
            started = time.perf_counter()
            report["modelRequests"] += 1
            write(output / "report.json", report)
            try:
                request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions",
                    json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
                    {"Content-Type": "application/json", "Authorization": "Bearer ollama"})
                with urllib.request.urlopen(request, timeout=15) as response:
                    envelope = strict_json(response.read().decode("utf-8"))
                write(output / (item["id"] + ".envelope.json"), envelope)
                choice = envelope.get("choices", [{}])[0]
                raw_reply = choice.get("message", {}).get("content", "")
                if choice.get("finish_reason") != "stop" or not isinstance(raw_reply, str):
                    raise ValueError("Incomplete public model output")
                public = visible_content(raw_reply)
                if public != raw_reply.strip():
                    errors.append("hidden-block-in-public-output")
                response_path = output / (item["id"] + ".response.json")
                response_path.write_text(public, encoding="utf-8")
                parsed = probe(output, "reviewed-decision", str(response_path), str(authority), str(latest_authority)) if item["mode"] == "reviewed-pending-turn" else probe(output, "decision", str(response_path), str(authority))
                if not parsed.get("valid"):
                    errors.append("production-parser-rejected:" + str(parsed.get("reason")))
                else:
                    decision = parsed["decision"]
                    view = decision.get("viewChange") or {}
                    expected = item["expected"]
                    if decision.get("intent") != expected["intent"]:
                        errors.append("latest-turn-action-not-preserved")
                    if decision.get("deviceGoal") is not None or decision.get("objectState") is not None:
                        errors.append("device-step-overrode-turn-first" if item["mode"] == "reviewed-pending-turn" else "old-device-authority-reintroduced")
                    if decision.get("origin") != "user":
                        errors.append("changed-action-authority")
                    if view.get("captureId") != context["visualObservation"]["captureId"]:
                        errors.append("stale-or-missing-current-view")
                    yaw, pitch = float(view.get("yaw", 0)), float(view.get("pitch", 0))
                    if expected.get("anyNonzeroBodyTurn"):
                        if abs(yaw) <= 1 or abs(yaw) > 180 or abs(pitch) > .01:
                            errors.append("turn-first-requires-a-nonzero-legal-body-turn")
                    elif abs(yaw - expected["yaw"]) > .01 or abs(pitch - expected["pitch"]) > .01:
                        errors.append("requested-angle-not-preserved")
            except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError, IndexError) as error:
                errors.append(type(error).__name__ + ":" + str(error))
            item.update(passed=not errors, errors=errors, productionParser=parsed,
                        requestSeconds=round(time.perf_counter() - started, 6))
            write(output / "report.json", report)
            print(json.dumps({"id": item["id"], "passed": item["passed"], "errors": errors}, ensure_ascii=False), flush=True)
        else:
            report["status"] = "passed" if all(item.get("passed") for item in report["cases"]) else "failed"
    except Exception as error:
        report.update(status="failed", error=type(error).__name__ + ":" + str(error))
    finally:
        write(output / "report.json", report)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
