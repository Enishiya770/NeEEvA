"""Replay frozen observed-authority 9 + existing normal 18 + pending 7 cases.

The pure production provider serializer and protocol parsers are compiled by
the existing pending probe. Expectations remain sidecars, never model input.
One request per case; idle existing endpoint only; no Unity/actions/retries.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import shutil
import time
import urllib.error
import urllib.request

import validate_device_goal_semantics_qwen as normal
import validate_pending_device_clarification_qwen as pending
from validate_autonomous_dialogue_motion import visible_content
from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import ROOT, PROTOCOL, NEWTONSOFT, strict_json

FIXTURES = ROOT / "Server/ARDY/runtime/observed-goal-authority-fixtures-20261008-235440/frozen-cases.json"
NORMAL = ROOT / "Server/ARDY/runtime/device-goal-semantics-live-final-20261008-174041/frozen-cases.json"
PENDING = ROOT / "Server/ARDY/runtime/goal-review-final-source-compatibility-20261008-184011/pending/cases.json"
SOURCE_FILES = (PROTOCOL, NEWTONSOFT, pending.PROVIDER, pending.CORE)


def source_hashes():
    return {name: normal.digest((ROOT / name).read_bytes()) for name in SOURCE_FILES}


def exact_expectations(case, review):
    errors = []
    for field, expected in case["expected"].items():
        if field == "mustNotBindAnyEntity":
            if expected and (review.get("referentEntityId") or review.get("referentGoalId")):
                errors.append("unbound-goal-must-not-inherit-identity")
        elif review.get(field) != expected:
            errors.append("expected-" + field + ":" + str(expected))
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--model", default="qwen36")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    sources = source_hashes()
    projection = pending.compile_probe(output)
    contracts = strict_json(pending.probe_text(output, "contracts"))
    normal.write(output / "production-contracts.json", contracts)
    guards = pending.local_guards(output, contracts, args.model)
    guards += normal.local_checks(output)
    frozen = []
    fixture_hashes = {}
    counts = {"observed": 9, "normal": 18, "pending": 7}
    for suite, source in (("observed", FIXTURES), ("normal", NORMAL), ("pending", PENDING)):
        raw = source.read_bytes()
        fixture_hashes[str(source)] = normal.digest(raw)
        shutil.copyfile(source, output / (suite + ".frozen-cases.json"))
        cases = strict_json(raw.decode("utf-8-sig"))
        if len(cases) != counts[suite]:
            raise ValueError("Frozen " + suite + " case count changed")
        for case in cases:
            case_key = suite + "--" + case["id"]
            input_path = output / (case_key + ".input.json")
            normal.write(input_path, case["input"])
            wire = pending.probe_text(output, "payload", str(input_path), args.model).encode("utf-8")
            decoded = strict_json(wire.decode("utf-8"))
            if strict_json(decoded["messages"][1]["content"]) != case["input"]:
                raise ValueError("Provider changed frozen input")
            if len(decoded["messages"]) != 2 or decoded["id_slot"] != 1 or decoded["temperature"] != 0:
                raise ValueError("Unexpected production auxiliary payload")
            (output / (case_key + ".request.json")).write_bytes(wire)
            frozen.append((suite, case, case_key, wire))
    report = {"status": "prepared", "model": args.model, "endpoint": args.url,
              "sources": sources, "projection": projection, "scriptSha256": normal.digest(Path(__file__).read_bytes()),
              "fixtureSha256": fixture_hashes, "caseCounts": counts, "localGuards": guards,
              "modelRequests": 0, "results": [],
              "scope": "Current compiled production provider/schema/parser; frozen semantic cases. No Unity, scene actions or device operations. Observed identity remains historical context, with no prebinding. Expected labels are sidecar only.",
              "policy": "One sequential request per case to existing idle 8080 only. No retries, repair, alternate endpoints or payload/expected-label edits."}
    normal.write(output / "report.json", report)
    if not all(row["passed"] for row in guards) or not args.execute_model:
        print(json.dumps({"status": report["status"], "caseCounts": counts, "guards": len(guards), "output": str(output)}))
        return 0 if all(row["passed"] for row in guards) else 1
    report["status"] = "running"
    try:
        for suite, case, key, wire in frozen:
            if source_hashes() != sources or any(normal.digest(Path(name).read_bytes()) != value for name, value in fixture_hashes.items()):
                report["status"] = "deferred-sources-or-frozen-cases-changed"
                break
            try:
                idle = all_slots_idle(request_json(args.url.rstrip("/") + "/slots"))
            except (urllib.error.URLError, ValueError, TimeoutError):
                report["status"] = "deferred-service-unknown"
                break
            if not idle:
                report["status"] = "deferred-busy-or-unknown"
                break
            errors = []
            review = None
            started = time.perf_counter()
            report["modelRequests"] += 1
            normal.write(output / "report.json", report)
            request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions", wire,
                                             {"Content-Type": "application/json"})
            try:
                with urllib.request.urlopen(request, timeout=90) as response:
                    raw = response.read()
                    status = response.status
                (output / (key + ".envelope.json")).write_bytes(raw)
                envelope = strict_json(raw.decode("utf-8"))
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
                reply_path = output / (key + ".response.json")
                reply_path.write_text(public, encoding="utf-8")
                mode = case["input"].get("mode")
                parse_mode = "pending" if mode == "pending-proposal-update" else "update" if mode == "goal-user-update" else "normal"
                parsed = strict_json(pending.probe_text(output, parse_mode, str(reply_path)))
                normal.write(output / (key + ".parsed.json"), parsed)
                if not parsed["valid"]:
                    errors.append("production-parser-rejected")
                else:
                    review = parsed["review"]
                    errors += normal.inspect(case, review) if suite == "normal" else exact_expectations(case, review)
            except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError, RuntimeError) as error:
                errors.append(type(error).__name__ + ":" + str(error))
            row = {"suite": suite, "id": case["id"], "passed": not errors, "errors": errors,
                   "review": review, "requestSeconds": round(time.perf_counter() - started, 6)}
            report["results"].append(row)
            normal.write(output / "report.json", report)
            print(json.dumps(row, ensure_ascii=False), flush=True)
        else:
            report["status"] = "passed" if all(row["passed"] for row in report["results"]) else "failed"
    finally:
        report["sourceHashesAfter"] = source_hashes()
        normal.write(output / "report.json", report)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
