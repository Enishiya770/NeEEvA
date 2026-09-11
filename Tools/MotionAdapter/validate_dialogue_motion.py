"""Synthetic dialogue contract checks on the live shared Qwen server, with no private chat data."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import time
import urllib.request
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def read_contract(allow_generated: bool = True):
    source = (ROOT / "Assets/AIChatTookit/Scripts/Chat/DialogueMotionIntent.cs").read_text(encoding="utf-8-sig")
    parts = []
    # GeneratedOutputContract is now the complete single specification, including
    # the basic vocabulary. Production Chat selects it instead of concatenating
    # two overlapping routing specifications. Basic-only bridges keep Basic.
    for name in (("GeneratedOutputContract",) if allow_generated else ("BasicOutputContract",)):
        # A semicolon inside a C# string is content, not the end of the declaration.
        # Accept the current literal-concatenation form and fail rather than silently
        # sending a partial production contract to the model.
        literal = r'"(?:\\.|[^"\\])*"'
        match = re.search(r"const string " + name + r"\s*=\s*(" + literal + r"(?:\s*\+\s*" + literal + r")*)\s*;", source)
        if match is None:
            raise ValueError("Cannot read complete literal C# contract: " + name)
        expression = match.group(1)
        parts.append("".join(json.loads(token) for token in re.findall(r'"(?:\\.|[^"\\])*"', expression)))
    return "".join(parts)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--report", type=Path, default=ROOT / "Tools/MotionAdapter/reports/dialogue-motion-live-qwen.json")
    args = parser.parse_args()
    contract = read_contract()
    report = {"status": "running", "endpoint": args.url, "contractSource": "DialogueMotionIntent.cs constants",
              "method": "Real Qwen chat completion with the production motion output contract and synthetic user requests; XML shape/selected intent check.",
              "limitation": "No private role prompt, microphone or TTS; executable-channel and streaming isolation tested separately in Unity.", "cases": []}
    cases = [("请用你自己的左手向我挥一下手。", "left-wave"), ("请用你自己的右手向我挥一下手。", "right-wave"),
             ("请轻轻点一次头。", "nod"), ("请轻轻摇一次头。", "shake-head"),
             ("请站在原地，把两只前臂向前稍微摊开、掌心向上，做一个解释事情的上身动作。", "generate"),
             ("今天我们聊聊你喜欢什么颜色，不需要做任何身体动作。", None)]
    try:
        for prompt, expected in cases:
            body = {"messages": [{"role": "system", "content": "你是正在和用户面对面交流的虚拟角色。简短自然地回复。\n" + contract},
                                 {"role": "user", "content": prompt}], "temperature": 0, "max_tokens": 300,
                    "chat_template_kwargs": {"enable_thinking": False}}
            started = time.perf_counter()
            request = urllib.request.Request(args.url + "/v1/chat/completions", json.dumps(body).encode(), {"Content-Type": "application/json"})
            with urllib.request.urlopen(request, timeout=40) as response:
                value = json.load(response)
            content = value["choices"][0]["message"]["content"]
            tags = re.findall(r'<motion\b(?:"[^"]*"|[^">])*>', content)
            selected = None
            if tags:
                assert len(tags) == 1, "Multiple action tags"
                tag = ET.fromstring(tags[0])
                selected = tag.attrib["name"]
                assert content.strip().endswith(tags[0]), "Action not placed at response end"
                assert set(tag.attrib) <= {"name", "text"}
                if selected == "generate":
                    description = tag.attrib["text"]
                    assert 0 < len(description) <= 240 and all(c not in description for c in '<>"')
            report["cases"].append({"user": prompt, "expected": expected, "selected": selected, "response": content,
                                    "elapsedSeconds": time.perf_counter() - started, "passed": selected == expected})
            assert selected == expected, f"Expected {expected}, got {selected}"
        report["status"] = "passed"
    except Exception as error:
        report["status"], report["error"] = "failed", str(error)
        raise
    finally:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"status": report["status"], "cases": len(report["cases"]), "report": str(args.report)}))


if __name__ == "__main__":
    main()
