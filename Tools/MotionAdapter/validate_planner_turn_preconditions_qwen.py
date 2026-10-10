"""Single-shot turn planning from Unity-exported production inputs and RGB.

Without --execute-model, compile and freeze payloads only. With it, send exactly
one request per case; preserve every answer and reject wrong intent/sign/range.
No action execution, model startup, retry, repair, angle correction or best-of.
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
              "policy": "One serial request per frozen case, production settings/parser, no retries, repair, angle rewrite or selected answers.",
              "scope": "Actual saved Unity RGB and production planning inputs with controlled user requests. Semantic planning replay only, no live action, TTS or end-to-end latency claim."}
    prepared, ids = [], set()
    for fixture in manifest["cases"]:
        case_id = fixture["id"]
        if not case_id or any(char not in "abcdefghijklmnopqrstuvwxyz0123456789-" for char in case_id) or case_id in ids:
            raise ValueError("Expected unique bounded export case IDs")
        ids.add(case_id)
        input_path = manifest_path.parent / fixture["input"]
        image_path = manifest_path.parent / fixture["image"]
        input_raw, image_raw = input_path.read_bytes(), image_path.read_bytes()
        context = strict_json(input_raw.decode("utf-8-sig"))
        image = image_raw.decode("utf-8-sig").strip()
        capture_id = context.get("visualObservation", {}).get("captureId")
        if not image.startswith("data:image/") or not capture_id or capture_id != fixture["captureId"]:
            raise ValueError("Require the exact Unity-exported source capture and image")
        if context.get("autonomous") is not False or not isinstance(context.get("currentUserText"), str):
            raise ValueError("Require an explicit controlled current user request")
        expected = {key: fixture[key] for key in ("expectedIntent", "expectedYaw", "expectedPitch")}
        if expected["expectedIntent"] not in ("orient-view", "turn-in-place"):
            raise ValueError("This replay accepts turn cases only")
        copied_input, copied_image = output / (case_id + ".input.json"), output / (case_id + ".image-data-url.txt")
        copied_input.write_bytes(input_raw)
        copied_image.write_bytes(image_raw)
        authority_path = output / (case_id + ".authority.txt")
        authority_path.write_text(context["currentUserText"], encoding="utf-8")
        payload = probe(output, "payload", str(copied_input), args.model, str(copied_image))
        write(output / (case_id + ".request.json"), payload)
        item = {"id": case_id, "sourceInput": str(input_path), "sourceImage": str(image_path),
                "inputSha256": digest(input_raw), "imageSha256": digest(image_raw), "captureId": capture_id, **expected}
        report["cases"].append(item)
        prepared.append((item, payload, authority_path))
    if not prepared:
        raise ValueError("No Unity-exported turn cases")
    report["status"] = "prepared"
    write(output / "report.json", report)
    if not args.execute_model:
        print(json.dumps({"status": "prepared", "cases": len(prepared), "modelRequests": 0}))
        return 0
    try:
        for item, payload, authority_path in prepared:
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
                    decision = parsed["decision"]
                    view = decision.get("viewChange") or {}
                    if decision.get("intent") != item["expectedIntent"]:
                        errors.append("wrong-turn-intent")
                    if abs(float(view.get("yaw", 999)) - item["expectedYaw"]) > .01 or abs(float(view.get("pitch", 999)) - item["expectedPitch"]) > .01:
                        errors.append("requested-angle-or-direction-not-preserved")
                    if decision.get("origin") != "user" or decision.get("deviceGoal") is not None:
                        errors.append("changed-action-authority")
                    if view.get("captureId") != item["captureId"]:
                        errors.append("stale-turn-capture")
            except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError, IndexError) as error:
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
