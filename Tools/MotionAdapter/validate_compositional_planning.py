"""Old/new planning-only comparison with public synthetic contexts; never submits motion."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import time
import urllib.request

from validate_dialogue_motion import ROOT, read_contract
from validate_autonomous_dialogue_motion import inspect_reply


CASES = [
    {"id": "forward-hold-wrists", "expected": "generate", "user": "请把双臂向前水平伸直到肩高，保持肩膀和肘部不动，只用两只手腕左右摆动两次，躯干保持正对我。"},
    {"id": "followup-idle-preserve-goal", "expected": "generate",
     "history": [{"role": "user", "content": "请把双臂向前平举到肩高，双肘伸直，掌心相对，保持这个姿势。"},
                 {"role": "assistant", "content": '<silent/><motion name="generate" text="Extend both arms straight forward at shoulder height, palms facing inward, holding the pose."/>'}],
     "user": "对，就在这个基础上，只让双手手腕左右摆动两次，肩膀、肘和躯干保持不动。"},
    {"id": "side-hold-with-nod", "expected": "generate", "user": "双臂向身体两侧水平伸直到肩高并保持，肩肘和躯干不动，同时轻轻点一次头。"},
    {"id": "unilateral-hold-other-extend", "expected": "generate", "user": "左臂向前平举到肩高并伸直保持，左肩左肘不动，同时右臂朝身体右侧伸直到肩高，躯干始终朝前。"},
    {"id": "independent-left-wave", "expected": "left-wave", "user": "请用你自己的左手挥一下手。"},
    {"id": "independent-right-wave", "expected": "right-wave", "user": "请用你自己的右手挥一下手。"},
    {"id": "independent-nod", "expected": "nod", "user": "请轻轻点一次头。"},
    {"id": "independent-shake", "expected": "shake-head", "user": "请轻轻摇一次头。"},
]


def request_json(url: str, body: dict | None = None) -> dict | list:
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(url, data, {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=20 if body is not None else 3) as response:
        return json.load(response)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--before", type=Path, default=ROOT / "Tools/MotionAdapter/reports/composition-diagnosis-v1-planning-contract-before.json")
    parser.add_argument("--report", type=Path, default=ROOT / "Tools/MotionAdapter/reports/composition-diagnosis-v1-planning-comparison.json")
    parser.add_argument("--plan-only", action="store_true")
    args = parser.parse_args()
    before = json.loads(args.before.read_text(encoding="utf-8"))
    contracts = {"old": before["contract"], "new": read_contract()}
    report = {"schema": 1, "status": "prepared", "endpoint": args.url,
              "contracts": {key: {"text": value, "sha256": hashlib.sha256(value.encode()).hexdigest()} for key, value in contracts.items()},
              "cases": CASES, "results": [], "busyPolicy": "Before every request require all existing /slots is_processing=false; stop without queueing if busy/unknown. No service or model changes.",
              "scope": "Real Qwen planning with independent synthetic public contexts; no private chat/persona, no reasoning content saved, no ARDY request.",
              "limitations": ["XML/routing checks do not establish preservation of every semantic constraint; compare descriptions manually.",
                              "No semantic action success, wrist-control capability, naturalness or feedback-controlled sequencing is established.",
                              "These are development diagnostics, not held-out final motion evaluation."]}
    if args.plan_only:
        print(json.dumps({"status": "prepared", "cases": len(CASES), "expectedRequests": 2 * len(CASES), "modelRequests": 0}))
        return
    args.report.parent.mkdir(parents=True, exist_ok=True)
    # An interrupted/busy attempt is retained, never silently replaced by a later run.
    with args.report.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
    try:
        for case in CASES:
            for version, contract in contracts.items():
                slots = request_json(args.url.rstrip("/") + "/slots")
                if not isinstance(slots, list) or not slots or any(s.get("is_processing") is not False for s in slots):
                    report["status"] = "deferred_busy_or_unknown"
                    return
                body = {"messages": [{"role": "system", "content": "你是正在和用户面对面交流的虚拟角色，简短自然地回复。\n" + contract +
                                      '\n[当前身体动作；程序事实]\n{"phase":"idle"}\n此状态是当前事实，历史对话仅描述过去的动作目标。'},
                                     *case.get("history", []), {"role": "user", "content": case["user"]}],
                        "temperature": 0, "max_tokens": 450, "chat_template_kwargs": {"enable_thinking": False}}
                start = time.perf_counter()
                value = request_json(args.url.rstrip("/") + "/v1/chat/completions", body)
                result = inspect_reply(value["choices"][0]["message"].get("content") or "")
                result.update(caseId=case["id"], contractVersion=version, model=value.get("model"),
                              routingPassed=result["selected"] == case["expected"], elapsedSeconds=round(time.perf_counter() - start, 3),
                              constraintCoverage="manual_pending", motionSuccess=None)
                report["results"].append(result)
                print(json.dumps({k: result[k] for k in ["caseId", "contractVersion", "selected", "routingPassed", "syntaxPassed"]}), flush=True)
        report["status"] = "completed"
    except Exception as error:
        report["status"] = "failed"
        report["error"] = f"{type(error).__name__}: {error}"
        raise
    finally:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"status": report["status"], "completedRequests": len(report["results"]), "report": str(args.report)}), flush=True)


if __name__ == "__main__":
    main()
