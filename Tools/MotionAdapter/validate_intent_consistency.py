"""Public single-Qwen protocol/feedback probes. Never starts services or executes motion.

The same production parser/compiler is inspected in a CPU process. Actual avatar success
and narration entailment remain separate, explicitly unscored until independent evidence.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tempfile
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "Tools/MotionAdapter/data/intent-motion-consistency-v1.json"
FREEZE = DATA.with_suffix(".freeze.json")
ONCE = DATA.with_suffix(".holdout.once.json")
MONO = Path("E:/Unity_Data/2022.3.22f1/Editor/Data/MonoBleedingEdge")
BUILD = ROOT / "Server/ARDY/runtime/intent-consistency-cpu"
HARNESS = BUILD / "intent-consistency.exe"
EXPECTED_MODEL = "071ee2a008ec51372f990d8efbea92ec9dd0137974110ef68fbfde429c8c6dd4"
SOURCE_PATHS = [
    "Tools/MotionAdapter/MotionIntentConsistencyChecks.cs",
    "Assets/AIChatTookit/Scripts/Chat/DialogueMotionIntent.cs",
    "Assets/AIChatTookit/Scripts/Chat/RoleOutputChannels.cs",
    "Assets/AIChatTookit/Scripts/Chat/SpeechText.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyActionPlan.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyActionPlanCompiler.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyActionConstraintLedger.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyControlPlan.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyAdaptiveTiming.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyMotionGoal.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyMotionCapabilities.cs",
]


def sha(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def utc() -> str:
    return datetime.now(timezone.utc).isoformat()


def exclusive_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, ensure_ascii=False, indent=2)
        stream.write("\n")


def load_suite() -> dict:
    value = json.loads(DATA.read_text(encoding="utf-8"))
    cases = value["development"] + value["holdout"]
    if len(cases) != len({row["id"] for row in cases}) or len(cases) != len({row["user"] for row in cases}):
        raise ValueError("Suite IDs and public prompts must be unique")
    if FREEZE.exists() and json.loads(FREEZE.read_text(encoding="utf-8"))["suiteSha256"] != sha(DATA.read_bytes()):
        raise ValueError("Frozen evaluation data changed; do not run or silently refreeze it")
    return value


def freeze_suite() -> dict:
    suite = load_suite()
    value = {"suiteId": suite["suiteId"], "suiteSha256": sha(DATA.read_bytes()), "frozenAtUtc": utc(),
             "developmentCount": len(suite["development"]), "holdoutCount": len(suite["holdout"]),
             "holdoutSha256": sha(json.dumps(suite["holdout"], sort_keys=True, ensure_ascii=False).encode()),
             "modelRequestsAtFreeze": 0, "motionExecutionsAtFreeze": 0,
             "scope": "New public dataset; no old holdout reports are overwritten or reclassified."}
    exclusive_json(FREEZE, value)
    return value


def read_contract(semantic: bool = False) -> dict:
    source = (ROOT / SOURCE_PATHS[1]).read_text(encoding="utf-8-sig")
    literal = r'"(?:\\.|[^"\\])*"'
    pieces = {}
    for name in ("SemanticOutputContract" if semantic else "GeneratedOutputContract", "MotionFeedbackOutputContract"):
        match = re.search(r"const string " + name + r"\s*=\s*(" + literal + r"(?:\s*\+\s*" + literal + r")*)\s*;", source)
        if match is None:
            raise ValueError("Production contract is not ready or not a complete literal: " + name)
        pieces[name] = "".join(json.loads(token) for token in re.findall(literal, match.group(1)))
    return {"text": "\n".join(pieces.values()), "parts": {key: sha(text.encode()) for key, text in pieces.items()}}


def compile_harness() -> dict:
    BUILD.mkdir(parents=True, exist_ok=True)
    command = [str(MONO / "bin/mono.exe"), str(MONO / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:latest",
               "/out:" + str(HARNESS), *[str(ROOT / path) for path in SOURCE_PATHS]]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return {path: sha((ROOT / path).read_bytes()) for path in SOURCE_PATHS}


def inspect_reply(content: str, user: str) -> dict:
    # Only the public synthetic user is put on disk. Raw model text, including a
    # surprising private channel, is passed in memory to production RoleOutputChannels.
    BUILD.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="public-user-", dir=BUILD) as directory:
        path = Path(directory) / "user.txt"
        path.write_text(user, encoding="utf-8")
        result = subprocess.run([str(MONO / "bin/mono.exe"), str(HARNESS), str(path)], input=content,
                                capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if result.returncode:
        raise RuntimeError(result.stderr)
    return json.loads(result.stdout.lstrip("\ufeff"))


def plan_checks(parsed: dict, expected: dict, semantic: bool = False) -> dict:
    failures = []
    if parsed.get("rejection") or parsed.get("compileError") or parsed.get("malformedTool"):
        failures.append("production-parser-or-compiler-rejection")
    names = list(expected["names"])
    # The suite records geometric intent independently from the optional plan
    # compiler. Default production uses compose/generate; plan remains opt-in.
    if "plan" in names and expected.get("fields") and "compose" not in names:
        names.append("compose")
    if not semantic:
        names = [name for name in names if name != "plan"]
    if parsed.get("name") not in names:
        failures.append("wrong-action-route")
    intent = parsed.get("intent") or {}
    actual = parsed.get("effective") or intent.get("ActionPlan") or intent.get("ControlPlan") or {}
    if parsed.get("name") == "compose":
        actual = {**actual, "mode": "hold" if actual.get("joint", "none") == "none" else "oscillate"}
    for key, value in expected.get("fields", {}).items():
        if actual.get(key) != value:
            failures.append("missing-or-wrong-field:" + key)
    locks = set(filter(None, actual.get("lockFields", "").split(",")))
    for key in expected.get("requiredLocks", []) if semantic else []:
        if key not in locks or not parsed.get("ledgerValidated"):
            failures.append("missing-source-lock:" + key)
    if "replayReferences" in expected and intent.get("ReplayReference") not in expected["replayReferences"]:
        failures.append("wrong-replay-reference")
    description = intent.get("Description") or (parsed.get("compiled") or {}).get("description") or ""
    if len(description) > expected.get("maximumDescriptionCharacters", 240):
        failures.append("overlength-description")
    goal = intent.get("Goal") or (parsed.get("compiled") or {}).get("goal") or {}
    for side, value in expected.get("goal", {}).items():
        if goal.get(side + "Goal") != value:
            failures.append("missing-or-weakened-goal:" + side)
    return {"passed": not failures, "failures": failures,
            "meaning": "Protocol, explicit parameter and reference checks only; intent entailment/narration require review."}


def synthetic_body_facts(case: dict) -> dict:
    fixture = case["fixture"]
    kind = fixture["kind"]
    identifier = fixture.get("actionId", "")
    body = {"fixtureSource": "public-synthetic-evaluation-state-not-a-real-avatar-observation",
            "intentFeedbackContract": "motion-intent-feedback-v1", "phase": "idle-Animator",
            "actionId": "", "previousRequestedPoseHeld": False, "lastReplayableActionId": "",
            "replayableActions": [], "intentGoal": None, "measuredGeneratedMotion": None,
            "lastExecutionFeedback": None}
    if kind == "replay-available":
        body["lastReplayableActionId"] = fixture.get("latestActionId", identifier)
        body["replayableActions"] = [{"actionId": identifier, "requestedDescription": fixture["description"],
            "goalStatus": "unassessed", "source": {"clipSha256": fixture["replayClipSha256"], "kind": "synthetic-public-fixture"}}]
        if fixture.get("latestActionId"):
            body["replayableActions"].append({"actionId": fixture["latestActionId"], "requestedDescription": "Nod once.",
                                           "goalStatus": "unassessed", "source": {"kind": "synthetic-public-fixture"}})
    if kind == "measured-holding":
        body.update(phase="holding", previousRequestedPoseHeld=True, fixturePose=fixture.get("rightPose"))
    if "goal" in fixture:
        body["intentGoal"] = {"leftGoal": fixture["goal"].get("left", "any"),
                              "rightGoal": fixture["goal"].get("right", "any"), "requiredStableSeconds": .2}
    if kind in {"goal-unmet", "goal-unmet-repair-eligible", "measured-completed", "observation-unavailable", "prediction-only"}:
        status = "reached" if kind == "measured-completed" else "unknown" if kind in {"observation-unavailable", "prediction-only"} else "not-reached"
        observation = {"actionId": identifier, "status": status, "measurementSource": "public-synthetic-fixture",
                       "goal": body["intentGoal"], "actual": fixture.get("observed"), "fingerGoalsSupported": False,
                       "meaning": "Fixture evidence for response testing only. Reached means necessary wrist regions, not full semantics or current hold."}
        body["measuredGeneratedMotion"] = observation
        body["lastExecutionFeedback"] = {"actionId": identifier, "parentActionId": "", "responseGeneration": 7,
            "repairAttempt": fixture.get("repairAttempt", 0), "name": "generate", "description": fixture.get("description", ""),
            "status": "completed" if kind in {"measured-completed", "observation-unavailable"} else "accepted" if kind == "prediction-only" else "goal-unmet",
            "reason": "public fixture; " + kind, "goal": body["intentGoal"], "observation": observation,
            "replayOf": "", "canRepair": fixture.get("canRepair", False)}
    if kind in {"cancelled-with-late-result", "repair-inflight-cancelled"}:
        body["lastExecutionFeedback"] = {"actionId": identifier, "parentActionId": fixture.get("parentActionId", ""),
            "responseGeneration": 6, "repairAttempt": fixture.get("repairAttempt", 0), "name": "generate",
            "status": "cancelled", "reason": fixture["cancelReason"], "canRepair": False}
    body["publicFixtureDetails"] = fixture
    return body


def make_messages(case: dict, contract: str) -> list[dict]:
    ledger = {"userTurn": 2, "userText": case["user"], "purpose": "", "constraints": [], "lastPlanError": ""}
    rejection = case["fixture"].get("lastError", "")
    context = contract + "\n[动作意图与用户约束；历史要求，不是已达到姿态]\n" + json.dumps(ledger, ensure_ascii=False)
    context += "\n[当前身体动作；公开合成程序事实]\n" + json.dumps(synthetic_body_facts(case), ensure_ascii=False)
    if rejection:
        context += "\n[最近动作协议拒绝；未派发]\n" + json.dumps({"currentResponseGeneration": 7,
            "lastMotionProtocolRejection": {"source": "dialogue-motion-parser", "status": "rejected-before-dispatch",
                "responseGeneration": 6, "sequence": 1, "reason": rejection}}, ensure_ascii=False)
    # Expected values, rubrics, and held-out labels are never sent to Qwen.
    return [{"role": "system", "content": "你是面对面交流的虚拟角色。简短自然回复；以下是公开测试状态，不含任何私人角色设定。\n" + context},
            {"role": "user", "content": case["user"]}]


def request_json(url: str, payload: dict | None = None) -> dict | list:
    data = None if payload is None else json.dumps(payload, ensure_ascii=False).encode()
    request = urllib.request.Request(url, data, {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=40 if payload else 4) as response:
        return json.load(response)


def health_snapshot(base_url: str, ardy_url: str) -> dict:
    health = request_json(base_url.rstrip("/") + "/health")
    slots = request_json(base_url.rstrip("/") + "/slots")
    ardy = request_json(ardy_url.rstrip("/") + "/health")
    features = ardy.get("features", {})
    if health.get("status") != "ok" or features.get("verified") is not True or features.get("source") != "live-qwen" or features.get("modelSha256") != EXPECTED_MODEL:
        raise ValueError("Existing shared-Qwen identity/health could not be verified")
    if features.get("baseUrl", "").rstrip("/") != base_url.rstrip("/"):
        raise ValueError("ARDY health refers to a different feature endpoint")
    return {"qwenStatus": health["status"], "featureSource": features["source"], "modelSha256": features["modelSha256"],
            "slots": [{key: row.get(key) for key in ("id", "n_ctx", "is_processing")} for row in slots],
            "ardyReady": ardy.get("ready"), "adapterSha256": ardy.get("backend", {}).get("adapterSha256"),
            "queue": ardy.get("queue"), "warning": "Read-only health; no model start, stop or feature extraction."}


def all_slots_idle(slots: object) -> bool:
    return isinstance(slots, list) and len(slots) == 3 and all(row.get("is_processing") is False and row.get("n_ctx") == 65536 for row in slots)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--freeze", action="store_true")
    parser.add_argument("--execute-model", action="store_true")
    parser.add_argument("--split", choices=("development", "holdout"), default="development")
    parser.add_argument("--case", action="append", default=[])
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--ardy-url", default="http://127.0.0.1:8093")
    parser.add_argument("--semantic-experiment", action="store_true", help="Opt in to the experimental plan contract; default preserves production compose/generate")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    if args.freeze:
        if args.execute_model:
            parser.error("Freeze and model execution are separate steps")
        print(json.dumps(freeze_suite()))
        return
    suite = load_suite()
    cases = suite[args.split]
    if args.case:
        if args.split == "holdout":
            parser.error("A holdout run must include every held-out case")
        if set(args.case) - {case["id"] for case in cases}:
            parser.error("Unknown public case ID")
        cases = [case for case in cases if case["id"] in args.case]
    contract = read_contract(args.semantic_experiment)
    if not args.execute_model:
        print(json.dumps({"status": "prepared", "cases": len(cases), "suiteSha256": sha(DATA.read_bytes()),
                          "contractParts": contract["parts"], "modelRequests": 0, "motionExecutions": 0}))
        return
    if not FREEZE.exists() or not args.report:
        parser.error("Freeze public cases first and choose a new --report path")
    if args.split == "holdout" and ONCE.exists():
        parser.error("The globally reserved holdout run has already started; do not rerun under another output path")
    sources = compile_harness()
    health = health_snapshot(args.url, args.ardy_url)
    report = {"schemaVersion": 1, "suiteId": suite["suiteId"], "suiteSha256": sha(DATA.read_bytes()),
              "split": args.split, "semanticExperiment": args.semantic_experiment, "caseIds": [case["id"] for case in cases], "contractParts": contract["parts"],
              "contractSha256": sha(contract["text"].encode()), "sources": sources, "health": health,
              "startedAtUtc": utc(), "status": "prepared", "results": [], "modelRequests": 0, "motionExecutions": 0,
              "scope": "Public independent single-response probes on the existing shared Qwen. Not private-scene prompt equivalence, actual automatic-repair lifecycle, Unity outcome or narration entailment verification.",
              "repairBudget": {"automaticRepairRequests": 0, "maximumInProduction": 1, "fixtureCorrections": "A supplied historical failure tests one corrective response, not a real autonomous loop."},
              "semanticSuccess": None, "naturalnessAccepted": False}
    exclusive_json(args.report, report)
    try:
        for case in cases:
            slots = request_json(args.url.rstrip("/") + "/slots")
            if not all_slots_idle(slots):
                report["status"] = "deferred-busy-or-unknown"
                return
            if args.split == "holdout" and report["modelRequests"] == 0:
                exclusive_json(ONCE, {"suiteSha256": report["suiteSha256"], "contractSha256": report["contractSha256"],
                                      "report": str(args.report.resolve()), "reservedAtUtc": utc()})
            messages = make_messages(case, contract["text"])
            payload = {"messages": messages, "temperature": 0, "max_tokens": 700,
                       "chat_template_kwargs": {"enable_thinking": False}}
            started = time.perf_counter()
            report["modelRequests"] += 1
            response = request_json(args.url.rstrip("/") + "/v1/chat/completions", payload)
            choice = response["choices"][0]
            parsed = inspect_reply(choice["message"].get("content") or "", case["user"])
            checks = plan_checks(parsed, case["expected"], args.semantic_experiment)
            if choice.get("finish_reason") != "stop":
                checks["passed"] = False
                checks["failures"].append("model-output-not-complete")
            row = {"caseId": case["id"], "category": case["category"], "publicUser": case["user"],
                   "requestSha256": sha(json.dumps(payload, ensure_ascii=False, sort_keys=True).encode()),
                   "requestId": response.get("id"), "model": response.get("model"), "finishReason": choice.get("finish_reason"),
                   "elapsedSeconds": round(time.perf_counter() - started, 3), "csharp": parsed, "planningChecks": checks,
                   "manualRubric": case["manual"], "narrationReview": "pending", "intentEntailmentReview": "pending",
                   "actualMotionEvidence": None, "motionSuccess": None, "fixtureStateIsRealExecution": False}
            report["results"].append(row)
            print(json.dumps({"caseId": case["id"], "planningChecksPassed": checks["passed"]}), flush=True)
        report["status"] = "completed"
        report["passedPlanningChecks"] = sum(row["planningChecks"]["passed"] for row in report["results"])
    except Exception as error:
        report.update(status="failed", error=type(error).__name__ + ": " + str(error))
        raise
    finally:
        report["finishedAtUtc"] = utc()
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
