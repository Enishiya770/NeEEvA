"""Replay actual Unity planner inputs once against the existing model endpoint.

Freeze the production wire and parser, keep expectations out of model input,
and retain every response. No Unity actions, retries, repair or model startup.
"""
from __future__ import annotations

import argparse
import base64
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


def check_recovery(decision: dict, context: dict, fixture: dict) -> list[str]:
    errors = []
    intent = decision.get("intent")
    if intent not in {"none", "approach-object", "reposition-view", "inspect-object", "look-at", "orient-view", "turn-in-place", "set-object-state"}:
        errors.append("unrelated-recovery-action")
    if intent != "none" and decision.get("origin") != "user":
        errors.append("changed-action-authority")
    if context.get("decisionContractFacts", {}).get("deviceGoalAlreadyReviewed") and decision.get("deviceGoal") is not None:
        errors.append("replaced-already-reviewed-goal")
    desired = fixture["expected"].get("requestedState") or (context.get("spatialGoal") or {}).get("desiredState")
    proposal = decision.get("deviceGoal")
    if proposal and (proposal.get("operation") != "set" or proposal.get("desiredState") != desired):
        errors.append("changed-requested-device-objective")
    if intent == "set-object-state" and decision.get("objectState") != desired:
        errors.append("changed-requested-device-state")
    target = decision.get("entityTarget") or decision.get("visualTarget") or decision.get("viewChange") or decision.get("viewPosition") or {}
    if target and target.get("captureId") != fixture["captureId"]:
        errors.append("stale-recovery-capture")
    scenario = fixture["scenario"]
    if scenario == "outside-operation-distance":
        if intent == "set-object-state":
            errors.append("direct-write-before-range-prerequisite")
    elif scenario == "same-rejected-target":
        expected = fixture["expected"]
        if intent in {"inspect-object", "set-object-state"}:
            entity = decision.get("entityTarget") or {}
            pixel = decision.get("visualTarget") or {}
            if expected.get("forbiddenEntityId") and entity.get("entityId") == expected["forbiddenEntityId"]:
                errors.append("same-rejected-entity-without-new-view")
            if pixel:
                previous = expected["forbiddenPixel"]
                if ((float(pixel["x"]) - previous["x"]) ** 2 + (float(pixel["y"]) - previous["y"]) ** 2) ** .5 <= .025:
                    errors.append("same-rejected-pixel-without-new-view")
                else:
                    # RGB alone cannot prove that another point belongs to a
                    # different opaque entity. Do not report an engine-equivalent
                    # pass without replaying that point through Unity grounding.
                    errors.append("needs-runtime-geometry-validation-for-other-pixel")
    else:
        raise ValueError("Unknown recovery scenario")
    return errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--model", default="antoneva")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    output, manifest_path = args.output.resolve(), args.manifest.resolve()
    output.mkdir(parents=True, exist_ok=False)
    manifest_raw = manifest_path.read_bytes()
    manifest = strict_json(manifest_raw.decode("utf-8-sig"))
    sources = {name: digest((ROOT / name).read_bytes()) for name in (PROTOCOL, PLANNER, CORE, NEWTONSOFT)}
    report = {"status": "preparing", "sources": sources, "compilation": compile_payload_probe(output),
              "scriptSha256": digest(Path(__file__).read_bytes()), "manifestSha256": digest(manifest_raw),
              "sourceManifest": str(manifest_path), "cases": [], "modelRequests": 0,
              "policy": "One serial request per exported case, production settings/parser, no retries, repair, answer rewriting or best-of.",
              "scope": "Actual saved Unity RGB and production planner inputs from controlled scene regression. Tests planner prerequisites only; no live action, TTS or end-to-end latency claim."}
    prepared, ids = [], set()
    for fixture in manifest["cases"]:
        case_id = fixture["id"]
        if not case_id or any(char not in "abcdefghijklmnopqrstuvwxyz0123456789-" for char in case_id) or case_id in ids:
            raise ValueError("Expected unique bounded export case IDs")
        ids.add(case_id)
        input_path = manifest_path.parent / fixture["input"]
        image_path = manifest_path.parent / fixture["image"]
        data_url_path = manifest_path.parent / fixture["imageDataUrl"]
        input_raw, image_raw = input_path.read_bytes(), image_path.read_bytes()
        data_url = data_url_path.read_text(encoding="utf-8-sig").strip()
        context = strict_json(input_raw.decode("utf-8-sig"))
        if not data_url.startswith("data:image/") or base64.b64decode(data_url.split(",", 1)[1], validate=True) != image_raw:
            raise ValueError("Require matching actual exported JPEG and data URL")
        capture_id = context.get("visualObservation", {}).get("captureId")
        if not capture_id or capture_id != fixture["captureId"] or context.get("autonomous") is not False:
            raise ValueError("Require the exact exported source capture and current user request")
        goal_state = (context.get("spatialGoal") or {}).get("desiredState")
        expected_state = fixture["expected"].get("requestedState")
        if expected_state and goal_state and goal_state != expected_state:
            raise ValueError("Expected sidecar state must agree with the exported goal")
        if fixture["scenario"] == "same-rejected-target" and goal_state not in {"on", "off"}:
            raise ValueError("Rejected-target continuation requires a real standing goal state")
        if fixture["scenario"] == "outside-operation-distance":
            facts = context.get("sceneActionPrerequisites", {}).get("knownObjects", [])
            if not any(row.get("withinOperationDistance") is False and row.get("currentVisibility") == "confirmed-in-current-capture" for row in facts):
                raise ValueError("Distance scenario must contain actual current outside-range facts")
        copied_input = output / (case_id + ".input.json")
        copied_image = output / (case_id + ".image-data-url.txt")
        copied_input.write_bytes(input_raw)
        copied_image.write_text(data_url, encoding="utf-8")
        authority_path = output / (case_id + ".authority.txt")
        authority_path.write_text(context["currentUserText"], encoding="utf-8")
        payload = probe(output, "payload", str(copied_input), args.model, str(copied_image))
        write(output / (case_id + ".request.json"), payload)
        item = {"id": case_id, "sourceInput": str(input_path), "sourceImage": str(image_path),
                "inputSha256": digest(input_raw), "imageSha256": digest(image_raw), "captureId": capture_id,
                "scenario": fixture["scenario"], "expected": fixture["expected"]}
        report["cases"].append(item)
        prepared.append((item, payload, authority_path, context, fixture))
    if not prepared:
        raise ValueError("No Unity-exported recovery cases")
    report["status"] = "prepared"
    write(output / "report.json", report)
    if not args.execute_model:
        print(json.dumps({"status": "prepared", "cases": len(prepared), "modelRequests": 0}))
        return 0
    try:
        for item, payload, authority_path, context, fixture in prepared:
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
                parsed = probe(output, "decision", str(response_path), str(authority_path))
                if not parsed.get("valid"):
                    errors.append("production-parser-rejected:" + str(parsed.get("reason")))
                else:
                    errors.extend(check_recovery(parsed["decision"], context, fixture))
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
