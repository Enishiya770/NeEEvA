"""Public semantic-plan diagnostics using the one existing Qwen; real C# validation, never motion execution."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET

from validate_dialogue_motion import ROOT
from validate_autonomous_dialogue_motion import visible_content
from validate_compositional_planning import request_json

MONO_ROOT = Path("E:/Unity_Data/2022.3.22f1/Editor/Data/MonoBleedingEdge")
BUILD = ROOT / "Server/ARDY/runtime/semantic-motion-protocol-check"
HARNESS = BUILD / "semantic-check.exe"
SUITE = ROOT / "Tools/MotionAdapter/data/semantic-motion-planning-v1.json"
SOURCE_PATHS = [
    "Tools/MotionAdapter/SemanticMotionProtocolChecks.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyActionPlan.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyActionPlanCompiler.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyActionConstraintLedger.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyControlPlan.cs",
    "Assets/AIChatTookit/Scripts/Chat/DialogueMotionIntent.cs",
    "Assets/AIChatTookit/Scripts/Chat/RoleOutputChannels.cs",
    "Assets/AIChatTookit/Scripts/Chat/SpeechText.cs",
    "Assets/AIChatTookit/Scripts/Motion/ArdyAdaptiveTiming.cs",
]


def digest(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def read_contract() -> str:
    source = (ROOT / SOURCE_PATHS[5]).read_text(encoding="utf-8-sig")
    literal = r'"(?:\\.|[^"\\])*"'
    match = re.search(r"const string SemanticOutputContract\s*=\s*(" + literal + r"(?:\s*\+\s*" + literal + r")*)\s*;", source)
    if match is None:
        raise ValueError("The complete production SemanticOutputContract is not a literal expression")
    return "".join(json.loads(value) for value in re.findall(literal, match.group(1)))


def compile_harness() -> dict:
    BUILD.mkdir(parents=True, exist_ok=True)
    command = [str(MONO_ROOT / "bin/mono.exe"), str(MONO_ROOT / "lib/mono/4.5/csc.exe"),
               "/nologo", "/langversion:latest", "/out:" + str(HARNESS), *[str(ROOT / p) for p in SOURCE_PATHS]]
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return {p: digest((ROOT / p).read_bytes()) for p in SOURCE_PATHS}


def run_cpu_checks() -> dict:
    sources = compile_harness()
    result = subprocess.run([str(MONO_ROOT / "bin/mono.exe"), str(HARNESS)], capture_output=True,
                            text=True, encoding="utf-8", errors="replace", timeout=30)
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    return {**json.loads(result.stdout.strip().lstrip("\ufeff")), "sources": sources, "exitCode": result.returncode}


def public_facts(case: dict) -> dict:
    # This is explicit synthetic fixture state, not inferred private history.
    constraints: dict[str, dict] = {}
    purpose = ""
    for index, previous in enumerate(case.get("history", []), 2):
        attrs = ET.fromstring(previous["tag"]).attrib
        if attrs.get("scope", "new") != "continue":
            constraints.clear()
        for field in filter(None, attrs.get("release", "").split(",")):
            constraints.pop(field, None)
        for field in filter(None, attrs.get("lock", "").split(",")):
            constraints[field] = {"field": field, "value": attrs[field], "userTurn": index, "evidence": attrs["evidence"]}
        purpose = attrs.get("purpose", purpose)
    return {"userTurn": len(case.get("history", [])) + 2, "userText": case["user"], "purpose": purpose,
            "constraints": list(constraints.values()), "lastPlanError": case.get("lastPlanError", "")}


def inspect_reply(content: str, case: dict, directory: Path) -> dict:
    visible = visible_content(content)
    directory.mkdir(parents=True, exist_ok=False)
    (directory / "reply.txt").write_text(visible, encoding="utf-8")
    for index, previous in enumerate(case.get("history", []), 1):
        (directory / f"{index:03d}.user.txt").write_text(previous["user"], encoding="utf-8")
        (directory / f"{index:03d}.tag.txt").write_text(previous["tag"], encoding="utf-8")
    index = len(case.get("history", [])) + 1
    (directory / f"{index:03d}.user.txt").write_text(case["user"], encoding="utf-8")
    parsed = subprocess.run([str(MONO_ROOT / "bin/mono.exe"), str(HARNESS), "--inspect", str(directory / "reply.txt"), str(directory)],
                            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
    if parsed.returncode:
        raise RuntimeError("C# fixture validation failed: " + parsed.stdout + parsed.stderr)
    result = json.loads(parsed.stdout.lstrip("\ufeff"))
    tags = re.findall(r'<motion\b(?:"[^"<>]*"|\'[^\'<>]*\'|[^\'">])*>', visible, flags=re.I)
    actual = {"name": result["name"], **(result.get("effective") or result.get("actionPlan") or {})}
    errors = []
    if len(tags) > 1 or (tags and (not visible.endswith(tags[0]) or not tags[0].endswith("/>"))):
        errors.append("motion must be the single trailing self-closing tag")
    if tags and not result["accepted"]:
        errors.append(result["rejection"])
    if result["compileError"]:
        errors.append(result["compileError"])
    mismatches = {}
    for field, expected in case["expected"].items():
        permitted = expected if isinstance(expected, list) else [expected]
        if actual.get(field) not in permitted:
            mismatches[field] = {"expected": expected, "actual": actual.get(field)}
    if case.get("noNumericDefaults") and result.get("actionPlan"):
        for field in ("leftBend", "rightBend", "amplitude", "cycles", "seconds"):
            if result["actionPlan"].get(field):
                mismatches[field] = {"expected": "omitted; no numeric user request", "actual": result["actionPlan"][field]}
    public = visible
    for tag in tags:
        public = public.replace(tag, "")
    return {"caseId": case["id"], "rawUser": case["user"], "userTurn": public_facts(case)["userTurn"],
            "sourceFacts": public_facts(case), "rawResponse": visible, "motionTags": tags,
            "publicReplyExcerpt": public.strip()[:320], "csharp": result,
            "shapeErrors": errors, "mismatches": mismatches, "planningChecksPassed": not errors and not mismatches,
            "motionSuccess": None, "purposeAppropriateness": "manual_review_required",
            "sourceMeaningEntailment": "not_proven_by_quote_membership"}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--report", type=Path, default=ROOT / "Tools/MotionAdapter/reports/semantic-motion-planning-v1.json")
    parser.add_argument("--case-set", choices=("development", "holdout"), default="development")
    parser.add_argument("--execute-model", action="store_true", help="Explicit opt-in after contract/source review; never executes motion")
    parser.add_argument("--cpu-tests", action="store_true")
    args = parser.parse_args()
    if args.cpu_tests:
        print(json.dumps(run_cpu_checks()))
        return
    suite = json.loads(SUITE.read_text(encoding="utf-8"))
    cases = suite[args.case_set]
    contract = read_contract()
    report = {"schema": 1, "status": "prepared", "caseSet": args.case_set, "cases": cases,
              "caseSha256": digest(json.dumps(cases, ensure_ascii=False, sort_keys=True).encode()),
              "suiteSha256": digest(SUITE.read_bytes()), "contract": contract, "contractSha256": digest(contract.encode()),
              "endpoint": args.url, "results": [], "modelRequests": 0,
              "scope": "Public multilingual planning only; existing shared Qwen; no private persona or reasoning, no ARDY/Unity calls.",
              "limitations": "Shape/ledger/compiler checks are not motion execution, naturalness, source-quote semantic entailment or generalization success. Development cases guide iteration; six held-out expressions are evaluated at most once.",
              "busyPolicy": "Every request requires all current slots explicitly idle; busy/unknown defers without queueing or retries."}
    if not args.execute_model:
        print(json.dumps({"status": "prepared", "cases": len(cases), "caseSha256": report["caseSha256"],
                          "contractSha256": report["contractSha256"], "modelRequests": 0}))
        return
    report["sources"] = compile_harness()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    with args.report.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
    try:
        for case in cases:
            slots = request_json(args.url.rstrip("/") + "/slots")
            if not isinstance(slots, list) or not slots or any(slot.get("is_processing") is not False for slot in slots):
                report["status"] = "deferred_busy_or_unknown"
                return
            if args.case_set == "holdout" and not report["results"]:
                with (SUITE.parent / "semantic-motion-planning-holdout-v1.once.json").open("x", encoding="utf-8") as stream:
                    json.dump({"caseSha256": report["caseSha256"], "contractSha256": report["contractSha256"], "report": str(args.report)}, stream)
            facts = public_facts(case)
            body = {"messages": [{"role": "system", "content": "你是与用户面对面交流的虚拟角色，简短自然回复。\n" + contract +
                       "\n[动作意图与用户约束；历史要求，不是已达到姿态]\n" + json.dumps(facts, ensure_ascii=False) +
                       "\n[当前身体动作；程序事实]\n" + json.dumps(case.get("bodyFacts", {"phase": "idle-Animator", "previousRequestedPoseHeld": False}), ensure_ascii=False)},
                       {"role": "user", "content": case["user"]}],
                    "temperature": 0, "max_tokens": 650, "chat_template_kwargs": {"enable_thinking": False}}
            started = time.perf_counter()
            report["modelRequests"] += 1
            response = request_json(args.url.rstrip("/") + "/v1/chat/completions", body)
            row = inspect_reply(response["choices"][0]["message"].get("content") or "", case,
                                BUILD / args.report.stem / case["id"])
            row.update(model=response.get("model"), requestId=response.get("id"), elapsedSeconds=round(time.perf_counter() - started, 3))
            report["results"].append(row)
            print(json.dumps({"caseId": case["id"], "planningChecksPassed": row["planningChecksPassed"]}), flush=True)
        report["status"] = "completed"
        report["passedPlanningChecks"] = sum(row["planningChecksPassed"] for row in report["results"])
    except Exception as error:
        report.update(status="failed", error=f"{type(error).__name__}: {error}")
        raise
    finally:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        with args.report.with_suffix(".tags.txt").open("x", encoding="utf-8") as stream:
            stream.write("\n".join(tag for row in report["results"] for tag in row["motionTags"]) + "\n")


if __name__ == "__main__":
    main()
