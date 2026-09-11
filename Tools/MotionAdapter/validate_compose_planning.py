"""Public structured-control planning diagnostics; no ARDY or avatar execution."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import time
import xml.etree.ElementTree as ET

from validate_dialogue_motion import ROOT, read_contract
from validate_autonomous_dialogue_motion import visible_content
from validate_compositional_planning import request_json


CASES = [
    {"id": "forward-hold-wrists", "user": "双臂向前水平伸展并保持，只让双手手腕绕身体竖直轴左右摆动，幅度10度、2次、局部动作3.2秒，结束后继续保持姿态。",
     "expected": {"name": "compose", "left": "forward", "right": "forward", "joint": "wrists", "axis": "up", "amplitude": 10, "cycles": 2, "seconds": 3.2, "end": "hold"}},
    {"id": "side-hold-nod", "user": "双臂向身体两侧水平伸展并保持，头部绕身体左右轴轻点一次，幅度8度，动作结束后回待机。",
     "expected": {"name": "compose", "left": "outward", "right": "outward", "joint": "head", "axis": "right", "amplitude": 8, "cycles": 1, "end": "idle"}},
    {"id": "current-left-forward-right", "user": "左臂就保持现在实际的姿态不动，右臂向自己正前方水平伸展，结束后保持这个姿态。",
     "expected": {"name": "compose", "left": "current", "right": "forward", "joint": "none", "end": "hold"}},
    {"id": "idle-restores-desired-direction", "history": [{"role": "user", "content": "请把双臂向前水平伸展，肘部伸直。"},
      {"role": "assistant", "content": '<silent/><motion name="compose" left="forward" right="forward" leftBend="0" rightBend="0"/>'}],
     "user": "现在上一次动作已经结束并回到待机。恢复刚才的目标姿态，再在保持双臂姿态时只左右摆动两只手腕，结束后回待机。",
     "expected": {"name": "compose", "left": "forward", "right": "forward", "leftBend": 0, "rightBend": 0, "joint": "wrists", "axis": "up", "end": "idle"}},
    {"id": "independent-left-wave", "user": "请用你自己的左手挥一下手。", "expected": {"name": "left-wave"}},
    {"id": "independent-nod", "user": "请轻轻点一次头。", "expected": {"name": "nod"}},
    {"id": "open-upper-body-dance", "user": "双脚留在原地，随音乐自然地交替用双臂画弧线，带一点躯干起伏，做连贯的自由上身舞蹈。", "expected": {"name": "generate"}},
    {"id": "forward-straight-hold-only", "user": "双臂朝自己正前方水平举起，双肘屈曲角设为0度保持伸直，不做局部摆动，结束后保持姿态。",
     "expected": {"name": "compose", "left": "forward", "right": "forward", "leftBend": 0, "rightBend": 0, "joint": "none", "end": "hold"}},
]

DEFAULTS = {"left": "none", "right": "none", "leftBend": 8, "rightBend": 8, "joint": "none", "axis": "up",
            "amplitude": 10, "cycles": 2, "seconds": 3.2, "end": "idle"}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--report", type=Path, default=ROOT / "Tools/MotionAdapter/reports/composition-diagnosis-v1-compose-planning.json")
    args = parser.parse_args()
    contract = read_contract()
    report = {"schema": 1, "status": "prepared", "contract": contract,
              "contractSha256": hashlib.sha256(contract.encode()).hexdigest(), "cases": CASES, "results": [],
              "scope": "Real existing Qwen, public synthetic task contexts, structured parameter selection only. No private persona, reasoning logs, ARDY requests or avatar execution.",
              "limits": "Parameter agreement is not evidence of physical control, smoothness or naturalness; production C# validates the saved actual tags separately."}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    with args.report.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2)
    try:
        for case in CASES:
            slots = request_json(args.url.rstrip("/") + "/slots")
            if not isinstance(slots, list) or not slots or any(s.get("is_processing") is not False for s in slots):
                report["status"] = "deferred_busy_or_unknown"
                return
            body = {"messages": [{"role": "system", "content": "你是与用户面对面交流的虚拟角色，简短自然回复。\n" + contract +
                                  '\n[当前身体动作；程序事实]\n{"phase":"idle"}'},
                                 *case.get("history", []), {"role": "user", "content": case["user"]}],
                    "temperature": 0, "max_tokens": 450, "chat_template_kwargs": {"enable_thinking": False}}
            start = time.perf_counter()
            response = request_json(args.url.rstrip("/") + "/v1/chat/completions", body)
            visible = visible_content(response["choices"][0]["message"].get("content") or "")
            tags = re.findall(r'<motion\b(?:"[^"<>]*"|\'[^\'<>]*\'|[^\'">])*>', visible, flags=re.I)
            row = {"caseId": case["id"], "model": response.get("model"), "motionTags": tags,
                   "publicReplyExcerpt": re.sub(r'<motion\b[^>]*>', '', visible).strip()[:240],
                   "elapsedSeconds": round(time.perf_counter() - start, 3), "planningParametersMatch": False}
            try:
                if len(tags) != 1: raise ValueError("Expected one motion tag")
                attributes = ET.fromstring(tags[0]).attrib
                actual = {**(DEFAULTS if attributes.get("name") == "compose" else {}), **attributes}
                for key in DEFAULTS:
                    if key in actual and isinstance(DEFAULTS[key], (int, float)):
                        actual[key] = float(actual[key])
                row["actualParameters"] = actual
                row["mismatches"] = {key: {"expected": expected, "actual": actual.get(key)} for key, expected in case["expected"].items() if actual.get(key) != expected}
                row["planningParametersMatch"] = not row["mismatches"]
            except (ET.ParseError, ValueError) as error:
                row["shapeError"] = str(error)
            report["results"].append(row)
            print(json.dumps({"caseId": case["id"], "planningParametersMatch": row["planningParametersMatch"]}), flush=True)
        report["status"] = "completed"
    except Exception as error:
        report["status"], report["error"] = "failed", f"{type(error).__name__}: {error}"
        raise
    finally:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        tags_path = args.report.with_suffix(".tags.txt")
        with tags_path.open("x", encoding="utf-8") as stream:
            stream.write("\n".join(tag for row in report["results"] for tag in row["motionTags"]) + "\n")
        print(json.dumps({"status": report["status"], "cases": len(report["results"]), "report": str(args.report), "actualTags": str(tags_path)}), flush=True)


if __name__ == "__main__":
    main()
