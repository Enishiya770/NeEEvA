"""Single-shot planner replays of exported Unity recovery requests.

Uses the compiled production payload builder/parser and the exact saved capture.
No scene actions, model startup, answer repair, retry, or best-of selection.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
import time
import urllib.error
import urllib.request

from validate_autonomous_dialogue_motion import visible_content
from validate_device_goal_semantics_qwen import digest, write
from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import ROOT, PROTOCOL, NEWTONSOFT, strict_json
from validate_standing_goal_planner_qwen import CORE, PLANNER, compile_payload_probe, probe


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--turn-input", type=Path, required=True)
    parser.add_argument("--turn-image", type=Path, required=True)
    parser.add_argument("--turn-intent", choices=("turn-in-place", "orient-view"), default="turn-in-place")
    parser.add_argument("--distance-input", type=Path, required=True)
    parser.add_argument("--distance-image", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--model", default="antoneva")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    sources = {name: digest((ROOT / name).read_bytes()) for name in (PROTOCOL, PLANNER, CORE, NEWTONSOFT)}
    report = {"status": "preparing", "sources": sources, "compilation": compile_payload_probe(output),
              "scriptSha256": digest(Path(__file__).read_bytes()), "cases": [], "modelRequests": 0,
              "scope": "Single-shot real-model semantic replay of production Unity requests and frozen actual captures. Controlled preceding review/goal fixtures. No live scene execution, microphone, TTS or end-to-end task measurement."}
    paths = [("independent-turn", args.turn_input, args.turn_image, {args.turn_intent}),
             ("distance-recovery", args.distance_input, args.distance_image, {"approach-object", "reposition-view"})]
    prepared = []
    for case_id, input_path, image_path, expected in paths:
        raw_input, raw_image = input_path.read_bytes(), image_path.read_bytes()
        context = strict_json(raw_input.decode("utf-8-sig"))
        image = raw_image.decode("utf-8-sig").strip()
        if not image.startswith("data:image/"):
            raise ValueError("Expected the exact Unity-exported image data URL")
        if case_id == "independent-turn" and context.get("currentRequestOnly") is not True:
            raise ValueError("Turn case must come from the production current-request-only path")
        if case_id == "distance-recovery":
            runner = context.get("spatialRunner", {})
            if runner.get("executionRecovery", {}).get("distanceRecoveryRequired") is not True:
                raise ValueError("Distance case must include the engine's active distance recovery constraint")
        copied_input, copied_image = output / (case_id + ".input.json"), output / (case_id + ".image-data-url.txt")
        write(copied_input, context)
        copied_image.write_text(image, encoding="utf-8")
        authority = context["currentUserText"]
        authority_path = output / (case_id + ".authority.txt")
        authority_path.write_text(authority, encoding="utf-8")
        request = probe(output, "payload", str(copied_input), args.model, str(copied_image))
        write(output / (case_id + ".request.json"), request)
        item = {"id": case_id, "sourceInput": str(input_path.resolve()), "sourceImage": str(image_path.resolve()),
                "inputSha256": digest(raw_input), "imageSha256": digest(raw_image), "expectedIntents": sorted(expected)}
        report["cases"].append(item)
        prepared.append((item, request, authority_path, expected, context))
    report["status"] = "prepared"
    write(output / "report.json", report)
    if not args.execute_model:
        print(json.dumps({"status": "prepared", "cases": len(prepared)}))
        return 0
    try:
        for item, payload, authority_path, expected, context in prepared:
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
                    envelope = strict_json(response.read().decode("utf-8"))
                write(output / (item["id"] + ".envelope.json"), envelope)
                choice = envelope.get("choices", [{}])[0]
                raw = choice.get("message", {}).get("content", "")
                if choice.get("finish_reason") != "stop" or not isinstance(raw, str):
                    raise ValueError("Incomplete public model output")
                public = visible_content(raw)
                if public != raw.strip():
                    errors.append("hidden-block-in-public-output")
                response_path = output / (item["id"] + ".response.json")
                response_path.write_text(public, encoding="utf-8")
                parsed = probe(output, "decision", str(response_path), str(authority_path))
                if not parsed.get("valid"):
                    errors.append("production-parser-rejected:" + str(parsed.get("reason")))
                else:
                    decision = parsed["decision"]
                    if decision.get("intent") not in expected:
                        errors.append("recovery-not-progressed")
                    if decision.get("deviceGoal") is not None:
                        errors.append("device-goal-reproposal")
                    if decision.get("origin") != "user":
                        errors.append("changed-action-authority")
                    target = decision.get("entityTarget") or decision.get("visualTarget") or decision.get("viewChange")
                    if target and target.get("captureId") != context.get("visualObservation", {}).get("captureId"):
                        errors.append("stale-target-capture")
                    if item["id"] == "independent-turn":
                        view = decision.get("viewChange") or {}
                        if abs(float(view.get("yaw", 0)) + 30) > .01 or abs(float(view.get("pitch", 0))) > .01:
                            errors.append("requested-left-30-degree-turn-not-preserved")
            except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError, IndexError) as error:
                errors.append(type(error).__name__ + ":" + str(error))
            item.update(passed=not errors, errors=errors, productionParser=parsed,
                        requestSeconds=round(time.perf_counter() - started, 6))
            write(output / "report.json", report)
            print(json.dumps({"id": item["id"], "passed": item["passed"], "errors": errors,
                              "intent": ((parsed or {}).get("decision") or {}).get("intent")}, ensure_ascii=False), flush=True)
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
