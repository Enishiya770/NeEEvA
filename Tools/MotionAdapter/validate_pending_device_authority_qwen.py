"""Replay frozen dialogue using the production two-stage pending device review.

Stage one determines the relation. Only its valid approved clarification invokes
the separate old-operation authority check, under the same total 12 second budget.
This is a fixed semantic conjunction, never a retry or response selection. Every
wire and response is retained. No Unity, device action, or persistence is executed.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys
import time
import urllib.error
import urllib.request

import validate_typed_device_referent_qwen as relation
from validate_autonomous_dialogue_motion import visible_content
from validate_device_goal_semantics_qwen import digest, write
from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import MONO, PROTOCOL, ROOT, strict_json
from validate_standing_goal_planner_qwen import constant, method


def compile_probe(output: Path) -> dict:
    base = relation.compile_probe(output)
    provider = (ROOT / relation.PROVIDER).read_text(encoding="utf-8-sig")
    snippets = [method(provider, signature) for signature in (
        "private static string BuildPendingDeviceOperationReviewInput(",
        "private string BuildPendingDeviceOperationReviewRequestJson(",
        "private static bool RequiresPendingDeviceOperationReview(",
        "private static bool TryApplyPendingDeviceOperationReview(")]
    snippets += [constant(provider, name) for name in (
        "PendingDeviceOperationReviewSchema", "PendingDeviceOperationReviewContract", "PendingDeviceFreshOperationReviewContract")]
    path = output / "parser/TypedReferentProbe.cs"
    projection = path.read_text(encoding="utf-8")
    dispatch = r'''
        if(args.Length==3 && args[0]=="authority-payload") {
            Console.Write(new TypedReferentProbe {CurrentModelName=args[2]}.BuildPendingDeviceOperationReviewRequestJson(File.ReadAllText(args[1])));return 0;
        }
        if(args.Length==3 && args[0]=="needs-authority") {
            string input=File.ReadAllText(args[1]);
            RoomTaskProtocol.DeviceGoalReview review;
            bool valid=RoomTaskProtocol.TryPendingDeviceClarificationReview(File.ReadAllText(args[2]),out review) &&
                review.HasTypedReferent && RoomTaskProtocol.TryValidateListedDeviceReferent(review,RoomDeviceGoalReferentSources(input),out _);
            Console.Write(JsonConvert.SerializeObject(new {valid,required=valid&&RequiresPendingDeviceOperationReview(RoomDeviceGoalReviewMode(input),review)}));return 0;
        }
        if(args.Length==3 && args[0]=="combine") {
            bool valid=TryApplyPendingDeviceOperationReview(File.ReadAllText(args[1]),File.ReadAllText(args[2]),out var combined,out var status);
            Console.Write(JsonConvert.SerializeObject(new {valid,combined,status}));return 0;
        }
'''
    if projection.count("        return 2;") != 1:
        raise ValueError("Unexpected production-projection dispatch")
    projection = projection.replace("        return 2;", dispatch + "        return 2;")
    projection = projection.rstrip()[:-1] + "\n" + "\n\n".join(snippets) + "\n}\n"
    path.write_text(projection, encoding="utf-8")
    result = subprocess.run([str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9",
        "/r:" + str(output / "parser/Newtonsoft.Json.dll"), "/out:" + str(output / "parser/probe.exe"),
        str(ROOT / PROTOCOL), str(path)], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=45)
    (output / "parser/dual-stage-compile.log").write_text(result.stdout + result.stderr, encoding="utf-8")
    if result.returncode:
        raise RuntimeError("Production dual-stage pure projection failed compilation")
    return {"relation": base, "projectionSha256": digest(projection.encode("utf-8")),
        "authorityMethodSha256": [digest(value.encode("utf-8")) for value in snippets],
        "scope": "Exact production request builders, authority projection, mandatory-stage predicate, strict parser and conjunction. Unity HTTP cancellation/lifecycle is not simulated."}


def check_gate(output: Path) -> dict:
    fixtures = output / "pure-gate"
    fixtures.mkdir()
    stage_one = {"verdict": "approved", "reason": "目标补充", "relation": "clarify", "referentKind": "unbound",
        "referentSourceId": "none", "pendingClarificationId": "pure-pending"}
    first = fixtures / "relation.json"
    write(first, stage_one)
    checks = []
    cases = [("approved", {"verdict": "approved", "reason": "原操作仍获授权"}, True, "approved"),
        ("rejected", {"verdict": "rejected", "reason": "真实中间人类撤销"}, True, "rejected"),
        ("uncertain", {"verdict": "uncertain", "reason": "原话不足"}, True, "uncertain"),
        ("extra-field", {"verdict": "approved", "reason": "无", "relation": "clarify"}, False, None),
        ("missing-reason", {"verdict": "approved"}, False, None)]
    for name, second, expected_valid, verdict in cases:
        second_path = fixtures / (name + ".json")
        write(second_path, second)
        result = relation.probe(output, "combine", str(first), str(second_path))
        passed = result["valid"] == expected_valid
        if verdict:
            combined = strict_json(result["combined"])
            passed = passed and combined["verdict"] == verdict and combined["relation"] == "clarify"
            passed = passed and combined["pendingClarificationId"] == stage_one["pendingClarificationId"]
        checks.append({"id": name, "passed": passed, "result": result})
    input_path = fixtures / "input.json"
    write(input_path, {"mode": "pending-proposal-update", "referentSources": []})
    for candidate_relation in ("clarify", "cancel", "new", "unrelated", "continue"):
        write(first, dict(stage_one, relation=candidate_relation))
        actual = relation.probe(output, "needs-authority", str(input_path), str(first))
        checks.append({"id": "required-" + candidate_relation,
            "passed": actual["required"] == (candidate_relation == "clarify"), "result": actual})
    duplicate = fixtures / "duplicate.json"
    duplicate.write_text('{"verdict":"rejected","verdict":"approved","reason":"重复"}', encoding="utf-8")
    write(first, stage_one)
    actual = relation.probe(output, "combine", str(first), str(duplicate))
    checks.append({"id": "duplicate", "passed": not actual["valid"], "result": actual})
    human_input = {"originalUserRequest": "关掉阅读灯。", "latestUserText": "先找到它。",
        "actualUserUpdates": [{"userTurn": 2, "text": "旧请求作废。", "textTruncated": False,
            "fullTextLength": 6, "assistant": "不应进入独立审核"}],
        "proposal": {"operation": "set", "requestQuote": "关掉阅读灯。", "targetDescription": "阅读灯", "desiredState": "off"},
        "previousReview": {"verdict": "approved"}, "recentDialogue": [{"role": "assistant", "quotedText": "已经批准"}]}
    for complete in (True, False):
        write(input_path, dict(human_input, authorizationHistoryComplete=complete))
        payload = relation.probe(output, "authority-payload", str(input_path), "antoneva")
        projected = strict_json(payload["messages"][1]["content"])
        expected_keys = {"authorizationHistoryComplete", "originalUserRequest", "actualUserUpdates", "latestUserText", "proposal"} if complete else {
            "authorizationHistoryComplete", "latestUserText", "proposal"}
        passed = set(projected) == expected_keys and projected["latestUserText"] == human_input["latestUserText"]
        if complete:
            passed = passed and projected["authorizationHistoryComplete"] is True
            passed = passed and projected["actualUserUpdates"][0]["text"] == "旧请求作废。"
            passed = passed and "assistant" not in projected["actualUserUpdates"][0]
        else:
            passed = passed and projected["authorizationHistoryComplete"] is False and "requestQuote" not in projected["proposal"]
        checks.append({"id": "human-projection-complete-" + str(complete).lower(), "passed": passed, "projected": projected})
    result = {"passed": all(row["passed"] for row in checks), "checks": checks}
    write(output / "pure-gate-report.json", result)
    if not result["passed"]:
        raise RuntimeError("Production conjunction regression failed")
    return result


def model_stage(output: Path, case_id: str, name: str, payload: dict, url: str, deadline: float, report: dict) -> Path:
    prefix = output / (case_id + "." + name)
    write(Path(str(prefix) + ".request.json"), payload)
    remaining = deadline - time.perf_counter()
    if remaining <= 0:
        raise TimeoutError("Shared production 12 second review deadline exhausted")
    if not all_slots_idle(request_json(url.rstrip("/") + "/slots")):
        raise RuntimeError("Model slots busy or unknown before semantic stage")
    remaining = deadline - time.perf_counter()
    if remaining <= 0:
        raise TimeoutError("Shared production 12 second review deadline exhausted")
    request = urllib.request.Request(url.rstrip("/") + "/v1/chat/completions",
        json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8"),
        {"Content-Type": "application/json", "Authorization": "Bearer ollama"})
    report["modelRequests"] += 1
    write(output / "report.json", report)
    started = time.perf_counter()
    detail = {"stage": name, "startedWithinBudgetSeconds": round(12 - remaining, 6)}
    try:
        with urllib.request.urlopen(request, timeout=remaining) as response:
            raw_envelope = response.read()
            detail["httpCode"] = response.status
        Path(str(prefix) + ".envelope.json").write_bytes(raw_envelope)
        envelope = strict_json(raw_envelope.decode("utf-8-sig"))
        choices = envelope.get("choices", [])
        choice = choices[0] if len(choices) == 1 else {}
        raw = choice.get("message", {}).get("content", "")
        if choice.get("finish_reason") != "stop" or not isinstance(raw, str):
            raise ValueError("Incomplete public model output")
        response_path = Path(str(prefix) + ".response.json")
        response_path.write_text(raw, encoding="utf-8")
        if visible_content(raw) != raw.strip():
            raise ValueError("Hidden block in public model output")
        if time.perf_counter() >= deadline:
            raise TimeoutError("Shared production 12 second review deadline exhausted")
        return response_path
    except urllib.error.HTTPError as error:
        detail["httpCode"] = error.code
        Path(str(prefix) + ".http-error.txt").write_bytes(error.read())
        raise
    finally:
        detail["requestSeconds"] = round(time.perf_counter() - started, 6)
        write(Path(str(prefix) + ".http.json"), detail)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dialogue-evidence", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--model", default="antoneva")
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    manifest_path = relation.reconstruct_dialogue_manifest(args.dialogue_evidence.resolve(), output)
    manifest = strict_json(manifest_path.read_text(encoding="utf-8-sig"))
    source_files = (PROTOCOL, relation.PROVIDER, relation.CORE, relation.NEWTONSOFT, relation.PENDING_CONTEXT)
    sources = {path: digest((ROOT / path).read_bytes()) for path in source_files}
    report = {"status": "preparing", "sources": sources, "compilation": compile_probe(output),
        "pureGate": check_gate(output), "sourceManifest": str(manifest_path), "manifestSha256": digest(manifest_path.read_bytes()),
        "scriptSha256": digest(Path(__file__).read_bytes()), "fixtureBuilderSha256": digest(Path(relation.__file__).read_bytes()),
        "cases": [], "modelRequests": 0,
        "policy": "Fixed serial semantic decomposition, never a retry: relation/source then ONLY approved clarify gets operation authority. Shared 12 second budget; conjunctive approval; no assistant or previous review enters authority. Every answer retained. Expected labels are sidecars only.",
        "scope": manifest["scope"] + " No Unity lifecycle, live planner continuation, persistence, device actuation or TTS. Safety/equivalence here is the review gate outcome only."}
    prepared = []
    for fixture in manifest["cases"]:
        path = manifest_path.parent / fixture["input"]
        source_input = strict_json(path.read_text(encoding="utf-8-sig"))
        payload = relation.probe(output, "payload", str(path), args.model)
        if strict_json(payload["messages"][1]["content"]) != source_input:
            raise ValueError("Production relation serializer changed fixture")
        authority_payload = None
        if source_input["mode"] == "pending-proposal-update":
            authority_payload = relation.probe(output, "authority-payload", str(path), args.model)
            authority = strict_json(authority_payload["messages"][1]["content"])
            complete = source_input.get("authorizationHistoryComplete", True)
            expected_keys = {"authorizationHistoryComplete", "originalUserRequest", "actualUserUpdates", "latestUserText", "proposal"} if complete else {
                "authorizationHistoryComplete", "latestUserText", "proposal"}
            if set(authority) != expected_keys:
                raise ValueError("Authority input includes nonhuman context")
            if authority["latestUserText"] != source_input["latestUserText"]:
                raise ValueError("Authority projection changed latest human words")
            if complete:
                if authority["authorizationHistoryComplete"] is not True:
                    raise ValueError("Complete authority history lacks its explicit branch marker")
                if authority["originalUserRequest"] != source_input["originalUserRequest"]:
                    raise ValueError("Authority projection changed original human words")
                if [row["text"] for row in authority["actualUserUpdates"]] != [row["text"] for row in source_input["actualUserUpdates"]]:
                    raise ValueError("Authority projection changed actual chronological human updates")
            elif authority["authorizationHistoryComplete"] is not False or "requestQuote" in authority["proposal"]:
                raise ValueError("Incomplete history leaked stale operation authority")
            write(output / (fixture["id"] + ".authority-prepared.request.json"), authority_payload)
        write(output / (fixture["id"] + ".relation-prepared.request.json"), payload)
        item = {"id": fixture["id"], "input": str(path), "expected": fixture["expected"], "provenance": fixture["provenance"]}
        report["cases"].append(item)
        prepared.append((item, path, payload, authority_payload))
    report["status"] = "prepared"
    write(output / "report.json", report)
    if not args.execute_model:
        print(json.dumps({"status": "prepared", "cases": len(prepared), "pureChecks": len(report["pureGate"]["checks"])}))
        return 0
    for item, input_path, payload, authority_payload in prepared:
        if {path: digest((ROOT / path).read_bytes()) for path in sources} != sources:
            report["status"] = "deferred-sources-changed"
            break
        errors = []
        started = time.perf_counter()
        item["authorityRequired"] = False
        try:
            first_path = model_stage(output, item["id"], "relation", payload, args.url, started + 12, report)
            first = relation.probe(output, "validate", str(input_path), str(first_path))
            item["relationParser"] = first
            if not first["valid"]:
                raise ValueError("Production relation parser/source gate rejected")
            final_path = first_path
            if authority_payload is not None:
                needs = relation.probe(output, "needs-authority", str(input_path), str(first_path))
                item["authorityRequired"] = needs["required"]
                if needs["required"]:
                    second_path = model_stage(output, item["id"], "operation-authority", authority_payload, args.url, started + 12, report)
                    combined = relation.probe(output, "combine", str(first_path), str(second_path))
                    item["conjunction"] = combined
                    if not combined["valid"]:
                        raise ValueError("Production operation authority parser rejected")
                    final_path = output / (item["id"] + ".combined.response.json")
                    final_path.write_text(combined["combined"], encoding="utf-8")
            final = relation.probe(output, "validate", str(input_path), str(final_path))
            item["finalParser"] = final
            if not final["valid"]:
                raise ValueError("Production final parser/source gate rejected")
            review = final["review"]
            old_approved = authority_payload is not None and review["verdict"] == "approved" and review["relation"] == "clarify"
            item["oldPendingOperationApproved"] = old_approved
            item["exactExpectedMatch"] = all(review.get(key) in value if isinstance(value, list) else review.get(key) == value
                for key, value in item["expected"].items())
            negative_control = item["id"].startswith("control-") and item["expected"]["verdict"] != ["approved"]
            if negative_control:
                item["behaviorExpectation"] = "must-not-register-old-operation"
                if old_approved:
                    errors.append("negative-control-restored-old-operation")
            elif not item["exactExpectedMatch"]:
                # Non-approval of the old operation is allowed to continue a fresh
                # current-only planner by runtime. This probe does not claim that
                # planner actually moved/searched; record exact discrepancy too.
                safe_current_only = authority_payload is not None and not old_approved
                item["compatibleWithCurrentOnlyFallback"] = safe_current_only
                if not safe_current_only:
                    errors.append("unexpected-review-semantics")
        except (urllib.error.URLError, TimeoutError, ValueError, TypeError, KeyError, IndexError, RuntimeError) as error:
            errors.append(type(error).__name__ + ":" + str(error))
        item.update(passed=not errors, errors=errors, totalSeconds=round(time.perf_counter() - started, 6))
        write(output / "report.json", report)
        print(json.dumps({"id": item["id"], "passed": item["passed"], "authorityRequired": item["authorityRequired"],
            "exactExpectedMatch": item.get("exactExpectedMatch"), "errors": errors}, ensure_ascii=False), flush=True)
    else:
        report["status"] = "passed" if all(row.get("passed") for row in report["cases"]) else "failed"
    report["exactExpectedMatches"] = sum(row.get("exactExpectedMatch", False) for row in report["cases"])
    write(output / "report.json", report)
    return 0 if report["status"] == "passed" else 1


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    raise SystemExit(main())
