"""Replay one untouched Unity-exported pending review after a real user replacement.

Default prepares only. --execute-model requires a passed upstream report and
idle existing 8080, then sends original bytes once, without prompt edits/retries.
Legacy exports without typed source input/schema fail before compilation or HTTP
and direct the caller to the typed replay tool with a fresh Unity manifest.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import time
import urllib.error
import urllib.request

import validate_device_goal_semantics_qwen as common
import validate_pending_device_clarification_qwen as pending
from validate_autonomous_dialogue_motion import visible_content
from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import ROOT, PROTOCOL, NEWTONSOFT, strict_json

SOURCE_FILES = (PROTOCOL, NEWTONSOFT, pending.PROVIDER, pending.CORE)


def hashes():
    return {name: common.digest((ROOT / name).read_bytes()) for name in SOURCE_FILES}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--upstream-report", type=Path)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    original = args.request.resolve()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    raw = original.read_bytes()
    wire = strict_json(raw.decode("utf-8-sig"))
    if wire.get("stream") is not False or len(wire.get("messages", [])) != 2:
        raise ValueError("Require the untouched non-streaming two-message production request")
    input_text = wire["messages"][1].get("content")
    if not isinstance(input_text, str):
        raise ValueError("Require original string input; no rebuilt image/text prompt")
    value = strict_json(input_text)
    pending.require_typed_replay_input(value)
    properties = wire.get("response_format", {}).get("schema", {}).get("properties", {})
    if "referentKind" not in properties or "referentSourceId" not in properties:
        raise ValueError("Legacy exported wire does not enforce typed referent sources. " + pending.TYPED_REPLAY_GUIDANCE)
    if value.get("mode") != "pending-proposal-update" or not value.get("pendingId"):
        raise ValueError("Not the exported pending combined review")
    if value.get("originalUserRequest") != "请把当前看到的小灯关掉。" or value.get("latestUserText") != "对，就是左边那盏。":
        raise ValueError("Original or latest scripted accepted user input changed")
    updates = value.get("actualUserUpdates", [])
    replacement = [item for item in updates if item.get("text") == "不要关左边那盏灯，改为打开右边的灯。"]
    if len(replacement) != 1 or replacement[0].get("source") != "actual-accepted-user-text" or replacement[0].get("textTruncated") is True:
        raise ValueError("Require one intact source-labelled intervening accepted replacement")
    sources = hashes()
    projection = pending.compile_probe(output)
    contracts = strict_json(pending.probe_text(output, "contracts"))
    common.write(output / "production-contracts.json", contracts)
    guards = pending.local_guards(output, contracts, wire["model"])
    input_path = output / "original-input.json"
    input_path.write_text(input_text, encoding="utf-8")
    rebuilt = pending.probe_text(output, "payload", str(input_path), wire["model"])
    if strict_json(rebuilt) != wire:
        raise ValueError("Export differs from the current compiled production provider; do not edit or post it")
    # Expected labels never enter the original request or its input string.
    expected = {"allowedVerdicts": ["rejected", "uncertain"], "pendingClarificationId": value["pendingId"],
                "mustNotApproveOldOffProposal": True,
                "scope": "Original off request, intact accepted T1 forbids the left off and replaces it with right on, then identity-only T2. T2 must not restore old off authority."}
    common.write(output / "expected.json", expected)
    (output / "request.json").write_bytes(raw)
    upstream = None
    if args.upstream_report:
        upstream_path = args.upstream_report.resolve()
        upstream = {"path": str(upstream_path), "sha256": common.digest(upstream_path.read_bytes()),
                    "status": strict_json(upstream_path.read_text(encoding="utf-8-sig")).get("status")}
    report = {"status": "prepared", "model": wire["model"], "exportRequest": str(original),
              "exportRequestSha256": common.digest(raw), "sources": sources, "projection": projection,
              "scriptSha256": common.digest(Path(__file__).read_bytes()), "localGuards": guards,
              "expected": expected, "upstream": upstream, "modelRequests": 0, "errors": [],
              "scope": "One untouched actual production wire exported by the controlled Unity input-recovery regression, using accepted scripted user inputs, current compiled provider/schema/strict parser. No live human request, scene action, device operation or current visibility is claimed.",
              "policy": "One request to existing idle endpoint only after upstream passed and parent sequencing authorization. No payload edits, extra prompt, retry or alternate endpoint."}
    common.write(output / "report.json", report)
    if not all(row["passed"] for row in guards):
        report["status"] = "failed"
        common.write(output / "report.json", report)
        return 1
    if not args.execute_model:
        print(json.dumps({"status": "prepared", "modelRequests": 0, "guards": len(guards), "output": str(output)}))
        return 0
    try:
        if not upstream or upstream["status"] != "passed":
            report["status"] = "deferred-upstream-not-passed"
            return 1
        if hashes() != sources or original.read_bytes() != raw:
            report["status"] = "deferred-sources-or-export-changed"
            return 1
        if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
            report["status"] = "deferred-busy-or-unknown"
            return 1
        report["modelRequests"] = 1
        common.write(output / "report.json", report)
        started = time.perf_counter()
        request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions", raw,
                                         {"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=90) as response:
            response_bytes = response.read()
            status = response.status
        (output / "envelope.json").write_bytes(response_bytes)
        envelope = strict_json(response_bytes.decode("utf-8"))
        choices = envelope.get("choices", [])
        choice = choices[0] if len(choices) == 1 else {}
        message = choice.get("message", {})
        content = message.get("content", "")
        if status != 200 or choice.get("finish_reason") != "stop" or message.get("role") != "assistant":
            report["errors"].append("invalid-or-incomplete-envelope")
        if not isinstance(content, str) or not content.strip():
            report["errors"].append("empty-public-json")
            content = ""
        public = visible_content(content)
        if public != content.strip():
            report["errors"].append("hidden-text-in-public-content")
        response_path = output / "response.json"
        response_path.write_text(public, encoding="utf-8")
        parsed = strict_json(pending.probe_text(output, "pending", str(response_path)))
        common.write(output / "parsed.json", parsed)
        report["parsed"] = parsed
        if not parsed["valid"]:
            report["errors"].append("production-strict-pending-parser-rejected")
        else:
            review = parsed["review"]
            if review["verdict"] not in expected["allowedVerdicts"] or review.get("IsApproved"):
                report["errors"].append("old-off-must-not-be-approved-after-real-replacement")
            if review.get("pendingClarificationId") != expected["pendingClarificationId"]:
                report["errors"].append("wrong-pending-source-id")
        report["requestSeconds"] = round(time.perf_counter() - started, 6)
        report["status"] = "passed" if not report["errors"] else "failed"
    except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError, RuntimeError) as error:
        report["status"] = "failed"
        report["errors"].append(type(error).__name__ + ":" + str(error))
    finally:
        report["sourceHashesAfter"] = hashes()
        common.write(output / "report.json", report)
        print(json.dumps({"status": report["status"], "modelRequests": report["modelRequests"],
                          "errors": report["errors"], "parsed": report.get("parsed"), "output": str(output)}, ensure_ascii=False))
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
