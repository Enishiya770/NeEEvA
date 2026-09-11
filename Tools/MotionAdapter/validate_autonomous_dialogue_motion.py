"""Public-context planning smoke only: no ARDY requests, private role data or reasoning logs."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import time
import urllib.request
import xml.etree.ElementTree as ET

from validate_dialogue_motion import ROOT, read_contract


CASES = [
    {"id": "explicit-both-arms", "user": "请把双臂向上举过头顶。", "expected": "generate"},
    {"id": "choose-free-action", "user": "随意做一个动作吧，你自己选就好。", "expected": ["left-wave", "right-wave", "nod", "shake-head", "generate"]},
    {"id": "conversation-optional-autonomy", "user": "这两种方案让我有点拿不定主意：一个稳妥，一个灵活。你怎么看？", "expected": None},
    {"id": "basic-right-wave", "user": "请用你自己的右手向我挥一下手。", "expected": "right-wave"},
    {"id": "basic-nod", "user": "请轻轻点一次头。", "expected": "nod"},
    {"id": "real-object-boundary", "user": "你能拿起我桌上的杯子喝一口吗？", "expected": None},
    {"id": "choose-new-welcome-action", "user": "除挥手点头之外，用双手做个表达欢迎的动作。", "expected": "generate"},
]


def visible_content(content: str) -> str:
    # Never persist a reasoning channel, including a malformed unclosed block.
    for name in ("think", "thought"):
        content = re.sub(rf"<{name}\b[^>]*>[\s\S]*?</{name}\s*>", "", content, flags=re.I)
        content = re.sub(rf"<{name}\b[^>]*>[\s\S]*$", "", content, flags=re.I)
    return content.strip()


def inspect_reply(content: str) -> dict:
    visible = visible_content(content)
    tags = re.findall(r'<motion\b(?:"[^"<>]*"|\'[^\'<>]*\'|[^\'">])*>', visible, flags=re.I)
    selected, description, errors = None, None, []
    if len(tags) > 1:
        errors.append("multiple-motion-tags")
    for token in tags:
        try:
            tag = ET.fromstring(token)
            selected = tag.attrib.get("name")
            description = tag.attrib.get("text")
            if selected not in {"left-wave", "right-wave", "nod", "shake-head", "none", "generate"}:
                errors.append("unsupported-name")
            if set(tag.attrib) - {"name", "text"} or not token.rstrip().endswith("/>"):
                errors.append("invalid-tag-shape")
            if not visible.endswith(token):
                errors.append("motion-not-at-end")
            if selected == "generate":
                if not description or len(description) > 240 or any(c in description for c in '<>"&\r\n'):
                    errors.append("invalid-generated-description")
            elif description is not None:
                errors.append("basic-action-has-description")
        except ET.ParseError:
            errors.append("malformed-motion-xml")
    public_text = visible
    for token in tags:
        public_text = public_text.replace(token, "")
    return {"selected": selected, "description": description, "motionTags": tags,
            "publicReplyExcerpt": public_text.strip()[:400], "syntaxErrors": errors,
            "syntaxPassed": not errors,
            "limitation": "XML shape only; production C# channel isolation is a separate Unity regression."}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--report", type=Path, default=ROOT / "Tools/MotionAdapter/reports/dialogue-autonomous-planning-smoke.json")
    parser.add_argument("--plan-only", action="store_true")
    args = parser.parse_args()
    contract = read_contract()
    metadata = {"contractSha256": hashlib.sha256(contract.encode()).hexdigest(),
                "contractSource": "Assets/AIChatTookit/Scripts/Chat/DialogueMotionIntent.cs: BasicOutputContract + GeneratedOutputContract",
                "caseCount": len(CASES)}
    if args.plan_only:
        print(json.dumps({**metadata, "status": "prepared", "modelRequests": 0}, ensure_ascii=False))
        return
    report = {**metadata, "status": "running", "endpoint": args.url,
              "method": "Six initial public-context cases plus one non-basic welcome-action probe; independent real Qwen chat completions, thinking disabled and never persisted.",
              "limits": ["No motion sent to ARDY; no Unity, local GPU or services started.",
                         "Autonomous appropriateness and unsupported-capability handling require dialogue review.",
                         "Planning selection is not evidence of motion success, generalization or naturalness."], "cases": []}
    try:
        for case in CASES:
            body = {"messages": [{"role": "system", "content": "你是正在和用户面对面交流的虚拟角色。简短自然地回复。\n" + contract},
                                 {"role": "user", "content": case["user"]}],
                    "temperature": 0, "max_tokens": 600, "chat_template_kwargs": {"enable_thinking": False}}
            request = urllib.request.Request(args.url.rstrip("/") + "/v1/chat/completions",
                                             json.dumps(body).encode(), {"Content-Type": "application/json"})
            started = time.perf_counter()
            with urllib.request.urlopen(request, timeout=90) as response:
                value = json.load(response)
            result = inspect_reply(value["choices"][0]["message"].get("content") or "")
            result.update({"id": case["id"], "publicUserPrompt": case["user"], "expectedRouting": case["expected"],
                           "routingPassed": result["selected"] in (case["expected"] if isinstance(case["expected"], list) else [case["expected"]]) if case["expected"] else None,
                           "dialogueAppropriateness": "manual_pending", "motionSuccess": None,
                           "model": value.get("model"), "elapsedSeconds": round(time.perf_counter() - started, 3)})
            report["cases"].append(result)
            print(json.dumps({"id": case["id"], "selected": result["selected"], "syntaxPassed": result["syntaxPassed"],
                              "routingPassed": result["routingPassed"]}, ensure_ascii=False), flush=True)
        report["status"] = "completed"
        report["mechanicalChecksPassed"] = all(c["syntaxPassed"] and c["routingPassed"] is not False for c in report["cases"])
    except Exception as error:
        report["status"], report["error"] = "failed", f"{type(error).__name__}: {error}"
        raise
    finally:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"status": report["status"], "report": str(args.report)}), flush=True)


if __name__ == "__main__":
    main()
