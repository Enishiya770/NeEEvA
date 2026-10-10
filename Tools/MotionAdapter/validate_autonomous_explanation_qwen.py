"""Replay exported autonomous explanation/format-repair requests exactly once.

Compile the production wire and autonomous parser. The positive motion control is
explicitly derived from the same frozen capture, never labeled a live request.
No Unity launch, retries, repairs, service startup or real action execution.
"""
from __future__ import annotations

import argparse
import copy
import json
from pathlib import Path
import subprocess
import sys
import time
import urllib.request

from validate_device_goal_semantics_qwen import digest, write
from validate_intent_consistency import all_slots_idle, request_json
from validate_pending_dialogue_planner_qwen import compile_history_payload_probe
from validate_room_task_closure import MONO, PROTOCOL, ROOT, strict_json
from validate_standing_goal_planner_qwen import CORE, PLANNER, probe


def compile_probe(output: Path) -> dict:
    metadata = compile_history_payload_probe(output)
    path = output / "parser/PlannerWireProbe.cs"
    source = path.read_text(encoding="utf-8")
    marker = '        if(args.Length==3 && args[0]=="decision")'
    entry = '''        if(args.Length==4 && args[0]=="autonomous-decision") {
            var input=JObject.Parse(File.ReadAllText(args[2]));
            bool valid=RoomTaskProtocol.TryAutonomousDecision(File.ReadAllText(args[1]),
                (string)input["autonomousIntention"],args[3]=="true",out var decision,out string reason);
            Console.Write(JsonConvert.SerializeObject(new {valid,decision,reason}));return 0;
        }
'''
    if source.count(marker) != 1:
        raise ValueError("Production projection entrypoint changed")
    source = source.replace(marker, entry + marker)
    path.write_text(source, encoding="utf-8")
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"),
        "/nologo", "/langversion:9", "/r:" + str(output / "parser/Newtonsoft.Json.dll"),
        "/out:" + str(output / "parser/probe.exe"), str(ROOT / PROTOCOL), str(path)],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (output / "parser/autonomous-compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Autonomous production parser compilation failed")
    metadata["autonomousAdapterSha256"] = digest(entry.encode("utf-8"))
    metadata["finalProjectionSha256"] = digest(source.encode("utf-8"))
    return metadata


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--model", help="Explicit override of only the exported model field")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    fixture = args.fixture.resolve()
    source_paths = (PROTOCOL, PLANNER, CORE)
    sources = {name: digest((ROOT / name).read_bytes()) for name in source_paths}
    report = {"status": "preparing", "sources": sources, "modelRequests": 0, "cases": [],
        "scope": "Single-shot real-model semantic replay of controlled Unity exports and one explicitly derived positive autonomy case. Frozen RGB, no scene actions or live end-to-end success claim."}
    try:
        report["compilation"] = compile_probe(output)
        prepared = []
        for case_id in ("initial-explanation", "repair-remains-none", "derived-positive-autonomy"):
            source_id = "initial-explanation" if case_id.startswith("derived-") else case_id
            paths = {kind: fixture / (source_id + suffix) for kind, suffix in (
                ("input", ".input.json"), ("request", ".request.json"),
                ("image", ".image-data-url.txt"), ("history", ".history.json"))}
            raw = {kind: path.read_bytes() for kind, path in paths.items()}
            context = strict_json(raw["input"].decode("utf-8-sig"))
            exported = strict_json(raw["request"].decode("utf-8-sig"))
            if context.get("autonomous") is not True or context.get("currentUserText") or context.get("latestUserText"):
                raise ValueError("Require actual autonomous input with no replayed human text")
            if context.get("latestGoalUpdate") is not None:
                raise ValueError("Autonomy must not inherit a human cancellation receipt")
            derived = case_id.startswith("derived-")
            if derived:
                context = copy.deepcopy(context)
                context["autonomousIntention"] = "向左转身30度，查看侧面的空间。"
                context.pop("decisionRepair", None)
            input_path = output / (case_id + ".input.json")
            if derived:
                write(input_path, context)
            else:
                # The production builder embeds this complete input string in a
                # message. JSON reformatting (including Windows newline changes)
                # changes the actual model prompt even when parsed values agree.
                input_path.write_bytes(raw["input"])
                if input_path.read_bytes() != raw["input"]:
                    raise ValueError(case_id + ": frozen input bytes were not preserved")
            image_path = output / (case_id + ".image-data-url.txt")
            image_path.write_bytes(raw["image"])
            history_path = output / (case_id + ".history.json")
            history_path.write_bytes(raw["history"])
            payload = probe(output, "payload", str(input_path), args.model or exported["model"], str(image_path), str(history_path))
            comparison = copy.deepcopy(exported)
            if args.model is not None:
                comparison["model"] = args.model
            if not derived and payload != comparison:
                write(output / (case_id + ".rebuilt-request.json"), payload)
                raise ValueError(case_id + ": production rebuilt wire differs from export")
            preserve = context.get("decisionRepair", {}).get("preserveNoAction") is True
            if (case_id == "repair-remains-none") != preserve:
                raise ValueError("No-action format repair scope missing or leaked")
            write(output / (case_id + ".request.json"), payload)
            item = {"id": case_id, "requestOrigin": "derived-positive-control" if derived else "actual-unmodified-Unity-export",
                "expected": "turn-left-30" if derived else "none", "wireEquivalent": not derived, "modelOverride": args.model,
                "preparedInputSha256": digest(input_path.read_bytes()), "inputBytesPreserved": not derived,
                "sources": {kind: {"path": str(paths[kind]), "sha256": digest(data)} for kind, data in raw.items()}}
            report["cases"].append(item)
            prepared.append((item, payload, input_path, preserve))
        report["status"] = "prepared"
        write(output / "report.json", report)
        if args.execute_model:
            for item, payload, input_path, preserve in prepared:
                if {name: digest((ROOT / name).read_bytes()) for name in source_paths} != sources:
                    raise ValueError("Sources changed after preparation")
                if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
                    raise ValueError("Shared model busy or unknown; remaining cases deferred")
                started = time.perf_counter()
                report["modelRequests"] += 1
                write(output / "report.json", report)
                req = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions",
                    json.dumps(payload, ensure_ascii=False).encode("utf-8"),
                    {"Content-Type": "application/json", "Authorization": "Bearer ollama"})
                with urllib.request.urlopen(req, timeout=15) as response:
                    envelope = strict_json(response.read().decode("utf-8"))
                write(output / (item["id"] + ".envelope.json"), envelope)
                choice = envelope.get("choices", [{}])[0]
                content = choice.get("message", {}).get("content", "")
                if choice.get("finish_reason") != "stop" or not isinstance(content, str):
                    raise ValueError("Incomplete model output")
                response_path = output / (item["id"] + ".response.json")
                response_path.write_text(content, encoding="utf-8")
                parsed = probe(output, "autonomous-decision", str(response_path), str(input_path), str(preserve).lower())
                decision = parsed.get("decision") or {}
                view = decision.get("viewChange") or {}
                if item["expected"] == "none":
                    behavior = decision.get("intent") == "none" and decision.get("origin") == "none" and not decision.get("deviceGoal")
                else:
                    behavior = decision.get("intent") == "turn-in-place" and decision.get("origin") == "autonomous" and abs(view.get("yaw", 999) + 30) < .01
                item.update(seconds=round(time.perf_counter() - started, 3), parsed=parsed,
                    passed=bool(parsed.get("valid") and behavior))
                write(output / "report.json", report)
            report["status"] = "passed" if all(item.get("passed") for item in report["cases"]) else "failed"
    except Exception as exc:
        report.update(status="failed", error=type(exc).__name__ + ": " + str(exc))
    write(output / "report.json", report)
    print(json.dumps({"status": report["status"], "modelRequests": report["modelRequests"],
        "cases": [{"id": item["id"], "passed": item.get("passed"), "seconds": item.get("seconds")} for item in report["cases"]],
        "error": report.get("error")}, ensure_ascii=False))
    return 0 if report["status"] in ("passed", "prepared") else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
