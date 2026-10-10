"""CPU regression for reviewed-goal step citations and recorded device wires.

Uses the production protocol and provider. Never sends HTTP or launches Unity.
An optional prior dual-stage report supplies immutable requests/responses whose
wire equality and parser outcomes are rechecked after unrelated protocol edits.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys

import validate_pending_device_authority_qwen as authority
import validate_typed_device_referent_qwen as relation
from validate_device_goal_semantics_qwen import digest, write
from validate_room_task_closure import MONO, PROTOCOL, ROOT, strict_json


def cases() -> list[dict]:
    original, latest = "现在去把你刚才看到的那盏小灯关掉。", "你先转身啊。"
    turn = {"intent": "turn-in-place", "origin": "user", "evidence": latest,
        "viewChange": {"captureId": "current-capture", "yaw": 180, "pitch": 0}}
    off = {"intent": "set-object-state", "origin": "user", "evidence": "那盏小灯关掉",
        "entityTarget": {"captureId": "current-capture", "entityId": "existing-bound-light"}, "objectState": "off"}
    proposal = {"operation": "set", "requestQuote": original, "targetDescription": "刚才看到的那盏小灯", "desiredState": "off"}
    rows = []

    def add(name: str, decision: dict | str, expected: bool, base_expected: bool, *, current: str = latest,
            reason: str | None = None) -> None:
        rows.append({"id": name, "json": decision if isinstance(decision, str) else json.dumps(decision, ensure_ascii=False),
            "original": original, "latest": current, "expected": expected, "baseExpected": base_expected, "reason": reason})

    add("latest-reviewed-turn-180", turn, True, False)
    add("original-authorized-device-step", off, True, True)
    add("fabricated-history-evidence", dict(turn, evidence="昨天你答应过先转身"), False, False,
        reason="evidence-not-in-current-user-text")
    add("missing-latest-does-not-widen", turn, False, False, current="", reason="evidence-not-in-current-user-text")
    add("invalid-turn-pitch-is-not-repaired", dict(turn, viewChange={"captureId": "current-capture", "yaw": 180, "pitch": 1}),
        False, False, reason="invalid-view-change-or-extra-fields")
    add("orient-yaw-bound-is-not-widened", dict(turn, intent="orient-view"), False, False,
        reason="invalid-view-change-or-extra-fields")
    add("original-citation-cannot-create-goal", dict(off, deviceGoal=proposal), False, True,
        reason="invalid-device-goal-change-in-continuation")
    add("none-cannot-cancel-reviewed-goal", {"intent": "none", "origin": "user", "evidence": original,
        "deviceGoal": {"operation": "cancel", "requestQuote": original}}, False, True,
        reason="invalid-device-goal-change-in-continuation")
    add("latest-citation-cannot-introduce-goal", dict(turn, deviceGoal=dict(proposal, requestQuote=latest)), False, False)
    add("null-goal-field-is-not-ignored", dict(turn, deviceGoal=None), False, False)
    add("autonomous-origin-not-a-user-goal-step", dict(turn, origin="autonomous"), False, False,
        reason="invalid-autonomous-origin")
    add("unknown-field-not-fixed-by-latest", dict(turn, invented=True), False, False,
        reason="invalid-view-change-or-extra-fields")
    add("ordinary-none-still-no-authority", {"intent": "none", "origin": "none", "evidence": ""}, True, True)
    add("duplicate-fields-still-rejected", '{"intent":"none","origin":"none","evidence":"","evidence":""}',
        False, False, reason="invalid-decision-json")
    return rows


def check_parser(output: Path) -> list[dict]:
    fixture = output / "reviewed-step-cases.json"
    write(fixture, cases())
    code = r'''using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
public static class ReviewedStepProbe {
    public static int Main(string[] args) {
        var results=new JArray();
        foreach(var row in JArray.Parse(File.ReadAllText(args[0]))) {
            string json=(string)row["json"],original=(string)row["original"],latest=(string)row["latest"];
            bool baseline=RoomTaskProtocol.TryDecision(json,original,false,out var oldDecision,out var oldReason);
            bool accepted=RoomTaskProtocol.TryReviewedGoalStepDecision(json,original,latest,out var decision,out var reason);
            bool expected=(bool)row["expected"],baseExpected=(bool)row["baseExpected"];
            bool passed=accepted==expected && baseline==baseExpected &&
                (row["reason"].Type==JTokenType.Null || (string)row["reason"]==reason) &&
                (accepted ? decision!=null && decision.deviceGoal==null : decision==null);
            results.Add(JObject.FromObject(new {id=(string)row["id"],passed,accepted,reason,baseline,oldReason,decision}));
        }
        Console.Write(results.ToString(Formatting.None));return 0;
    }
}'''
    source = output / "parser/ReviewedStepProbe.cs"
    source.write_text(code, encoding="utf-8")
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9",
        "/r:" + str(output / "parser/Newtonsoft.Json.dll"), "/out:" + str(output / "parser/reviewed-step.exe"),
        str(ROOT / PROTOCOL), str(source)], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (output / "parser/reviewed-step-compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Reviewed-goal step production protocol did not compile")
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(output / "parser/reviewed-step.exe"), str(fixture)],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if result.returncode:
        raise RuntimeError("Reviewed-goal step production regression failed to execute")
    return strict_json(result.stdout.strip().lstrip("\ufeff"))


def check_recorded_device_wires(output: Path, report_path: Path) -> dict:
    previous = strict_json(report_path.read_text(encoding="utf-8-sig"))
    folder = report_path.parent
    checks = []
    for item in previous["cases"]:
        case_id, input_path = item["id"], Path(item["input"])
        wire_path = folder / (case_id + ".relation-prepared.request.json")
        recorded = strict_json(wire_path.read_text(encoding="utf-8-sig"))
        actual = relation.probe(output, "payload", str(input_path), recorded["model"])
        checks.append({"id": case_id + ":relation-wire", "passed": actual == recorded, "recordedSha256": digest(wire_path.read_bytes())})
        authority_path = folder / (case_id + ".authority-prepared.request.json")
        if authority_path.exists():
            recorded = strict_json(authority_path.read_text(encoding="utf-8-sig"))
            actual = relation.probe(output, "authority-payload", str(input_path), recorded["model"])
            checks.append({"id": case_id + ":authority-wire", "passed": actual == recorded, "recordedSha256": digest(authority_path.read_bytes())})
        response_path = folder / (case_id + ".relation.response.json")
        actual = relation.probe(output, "validate", str(input_path), str(response_path))
        checks.append({"id": case_id + ":relation-parser", "passed": actual == item["relationParser"]})
        if item["authorityRequired"]:
            operation_path = folder / (case_id + ".operation-authority.response.json")
            combined = relation.probe(output, "combine", str(response_path), str(operation_path))
            checks.append({"id": case_id + ":conjunction", "passed": combined == item["conjunction"]})
            response_path = folder / (case_id + ".combined.response.json")
        actual = relation.probe(output, "validate", str(input_path), str(response_path))
        checks.append({"id": case_id + ":final-parser", "passed": actual == item["finalParser"]})
    return {"passed": all(row["passed"] for row in checks), "checks": checks,
        "priorReport": str(report_path), "priorReportSha256": digest(report_path.read_bytes()),
        "priorProtocolSha256": previous["sources"][PROTOCOL], "newProtocolSha256": digest((ROOT / PROTOCOL).read_bytes()),
        "scope": "Rebuilt complete production relation and conditional authority payloads are equal to the frozen model-request payloads. Recorded model responses produce identical production parses/conjunctions. No model re-request or claim that the new planner citation branch was model-tested."}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--device-report", type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    report = {"status": "preparing", "modelRequests": 0, "compilation": authority.compile_probe(output),
        "protocolSha256": digest((ROOT / PROTOCOL).read_bytes()), "scriptSha256": digest(Path(__file__).read_bytes())}
    report["parserChecks"] = check_parser(output)
    if args.device_report:
        report["deviceWireEquivalence"] = check_recorded_device_wires(output, args.device_report.resolve())
    passed = all(row["passed"] for row in report["parserChecks"]) and report.get("deviceWireEquivalence", {}).get("passed", True)
    report["status"] = "passed" if passed else "failed"
    write(output / "report.json", report)
    print(json.dumps({"status": report["status"], "parserChecks": len(report["parserChecks"]),
        "wireChecks": len(report.get("deviceWireEquivalence", {}).get("checks", [])), "modelRequests": 0}))
    return 0 if passed else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
