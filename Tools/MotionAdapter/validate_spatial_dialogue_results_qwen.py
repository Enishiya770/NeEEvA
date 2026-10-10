"""Single-shot generic result-report probes using production projection/provider.

Prepare by default. --execute-model uses the already-running model with no
retry; all synthetic facts, raw answers and source hashes remain in evidence.
Envelope validity is reported separately from required human semantic review.
"""
from __future__ import annotations
import argparse
import copy
import json
from pathlib import Path
import sys

from validate_intent_consistency import all_slots_idle, request_json
from validate_room_task_closure import ROOT, PROTOCOL
from validate_spatial_result_qwen import compile_probe, probe, digest, write, POLICY, PROVIDER, CORE


def fixtures():
    base = {"originalUserRequest": "打开书架旁的投影仪，请用中文报告。", "facts": {
        "goal": {"goalId": "synthetic-projector-goal", "registered": True, "ownerCurrent": True,
            "status": "waiting_clarification", "desiredState": "on", "boundEntityId": "synthetic-projector-entity",
            "targetDescription": "书架旁的投影仪", "completionEvidenceValid": False,
            "operationSummary": {"operationAttempted": False, "stateWriteObserved": False,
                "scope": "Synthetic engine-owner fact: this request stopped in searching before any device operation."},
            "reason": "search-no-progress", "targetKnowledge": {"identityKnown": True,
                "historicalPositionKnown": True, "currentVisibility": "not-established"},
            "searchSummary": {"searchAttempted": True, "completedStepCount": 0,
                "latestSearchStatus": "incomplete", "latestSearchReason": "search-no-progress",
                "latestSearchDecisions": 3, "latestSearchMotionSteps": 0}},
        "lastAction": None, "runner": {"phase": "incomplete", "terminalReason": "search-no-progress"}}}
    rows = [{"id": "known-projector-page-search", "data": copy.deepcopy(base), "expectedReport": "target-unconfirmed",
             "semanticRequirement": "State that reopening is incomplete because examining the images did not reconfirm the known projector. Do not say no search, forgot identity, ask which projector/room, claim a head/body sweep, or invent a wall switch."}]
    missing = copy.deepcopy(base)
    missing["originalUserRequest"] = "Turn off the fan beside the door. Please answer in English."
    missing["facts"]["goal"].update(targetDescription="fan beside the door", desiredState="off",
        reason="runner-current-observation-unavailable", searchSummary={"searchAttempted": False,
        "completedStepCount": 0, "latestSearchStatus": None}, status="waiting_clarification")
    rows.append({"id": "known-fan-no-new-image", "data": missing, "expectedReport": "observation-unavailable",
                 "semanticRequirement": "Report unable to switch off because no current image was obtained. Identity remains known; do not claim a failed search or ask which device or room."})
    far = copy.deepcopy(base)
    far["originalUserRequest"] = "机の上のラジオをつけて。日本語で答えて。"
    far["facts"]["goal"].update(targetDescription="机の上のラジオ", status="blocked", reason="device-outside-nearby-operation-distance",
        searchSummary={"searchAttempted": True, "latestSearchStatus": "found", "completedStepCount": 1})
    far["facts"]["lastAction"] = {"phase": "rejected", "intent": "set-object-state",
        "reason": "device-outside-nearby-operation-distance", "requestedState": "on"}
    far["facts"]["goal"]["operationSummary"] = {"operationAttempted": True, "stateWriteObserved": False,
        "lastOperationPhase": "rejected"}
    rows.append({"id": "known-radio-too-far", "data": far, "expectedReport": "too-far",
                 "semanticRequirement": "Report in Japanese that switching on is incomplete because the known radio is still too far. Do not substitute a lamp, forget identity or invent a physical fault."})
    success = copy.deepcopy(base)
    success["originalUserRequest"] = "Please turn off the display. Answer in English."
    goal = success["facts"]["goal"]
    goal.update(targetDescription="display", status="satisfied", desiredState="off", reason="actual-state-verified",
        operationSummary={"operationAttempted": True, "stateWriteObserved": True, "lastOperationPhase": "performed"},
        completionEvidenceValid=True, completionEvidence={"entityId": goal["boundEntityId"], "desiredState": "off",
        "resultCaptureId": "synthetic-new-verification-capture", "actual": {"registered": True,
        "entityId": goal["boundEntityId"], "on": False}})
    rows.append({"id": "display-off-verified", "data": success, "expectedReport": "off",
                 "semanticRequirement": "State the display is confirmed off in English; do not substitute a lamp, describe pressing a button or add room-wide effects."})
    ambiguity = {"originalUserRequest": "打开之前关掉的一台投影仪，请用中文回答。", "facts": {
        "goal": {"status": "waiting_clarification", "registered": False, "pendingRetained": True,
            "requestAssessment": "target-unclear", "targetDescription": "之前关掉的一台投影仪",
            "reason": "Two previously operated projectors match, one by the door and one by the window; user has not specified which.",
            "searchSummary": {"searchAttempted": False}, "targetKnowledge": {"identityKnown": False}},
        "latestGoalReview": {"requestAssessment": "target-unclear", "pendingRetained": True,
            "reason": "Two previously operated projectors match, one by the door and one by the window; user has not specified which."},
        "lastAction": None}}
    rows.append({"id": "genuine-target-ambiguity", "data": ambiguity, "expectedReport": "clarification",
                 "semanticRequirement": "Say search has not started, then ask which of the two projectors (door/window). Do not claim attempted search or missing permission."})
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--execute-model", action="store_true")
    args = parser.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    sources = {name: digest(ROOT / name) for name in (PROTOCOL, POLICY, PROVIDER, CORE,
        "Assets/AIChatTookit/Prompts/persona.txt", "Assets/AIChatTookit/Prompts/language.txt")}
    report = {"sources": sources, "scriptSha256": digest(Path(__file__)), "status": "preparing", "modelRequests": 0,
        "scope": "Synthetic engine-fact controls through compiled production result projection/provider and envelope parser. No physical actions, TTS, live Unity source facts, retries or best-answer selection. Semantic checks require reading the preserved first responses.", "cases": []}
    try:
        compile_probe(output)
        prepared = []
        for row in fixtures():
            path = output / (row["id"] + ".input.json")
            write(path, row["data"])
            data = probe(output, "prepare", path, ROOT)
            payload = data.pop("wire")
            item = {key: val for key, val in row.items() if key != "data"}
            item["production"] = data
            item["projectionPassed"] = payload is not None and data["expectedReport"] == data["projectedReport"] == row["expectedReport"]
            report["cases"].append(item)
            write(output / (row["id"] + ".request.json"), payload)
            prepared.append((item, payload))
        write(output / "report.json", report)
        if not all(item["projectionPassed"] for item in report["cases"]):
            raise ValueError("Production outcome projection differs from the frozen expected report")
        for item, payload in prepared:
            if not args.execute_model:
                continue
            if any(digest(ROOT / name) != value for name, value in sources.items()):
                raise RuntimeError("Sources changed; no further model request sent")
            if not all_slots_idle(request_json(args.url.rstrip("/") + "/slots")):
                raise RuntimeError("Model busy or slot status unknown; no request sent")
            report["modelRequests"] += 1
            write(output / "report.json", report)
            response = request_json(args.url.rstrip("/") + "/v1/chat/completions", payload)
            write(output / (item["id"] + ".envelope.json"), response)
            choice = response["choices"][0]
            raw = choice["message"]["content"]
            path = output / (item["id"] + ".response.txt")
            path.write_text(raw, encoding="utf-8")
            item["validation"] = probe(output, "parse", path)
            item["envelopePassed"] = item["validation"]["valid"] and choice["finish_reason"] == "stop"
            item["semanticStatus"] = "requires-independent-reading"
            write(output / "report.json", report)
            print(json.dumps({"id": item["id"], "envelopePassed": item["envelopePassed"], "output": raw}, ensure_ascii=False), flush=True)
        report["status"] = "completed-envelope-check-only" if args.execute_model else "prepared"
    except Exception as error:
        report.update(status="failed", error=str(error))
    finally:
        report["sourcesUnchanged"] = all(digest(ROOT / name) == value for name, value in sources.items())
        write(output / "report.json", report)
    print(json.dumps({"status": report["status"], "modelRequests": report["modelRequests"]}))
    return 1 if report["status"] == "failed" or not report["sourcesUnchanged"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
