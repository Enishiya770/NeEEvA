"""Public real-model probe using prompts exported by the production Unity regression.

Reads an existing local inference service only. Does not execute model tools, load models,
touch the user's chat history or save reasoning channels.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import time
import urllib.request

ROOT = Path(__file__).resolve().parents[2]


def request(url, messages):
    with urllib.request.urlopen(url + "/slots", timeout=4) as response:
        slots = json.load(response)
    if any(slot.get("is_processing") for slot in slots):
        raise RuntimeError("Existing model is busy; no request submitted.")
    data = {"messages": messages, "temperature": 0, "max_tokens": 650,
            "chat_template_kwargs": {"enable_thinking": False}}
    req = urllib.request.Request(url + "/v1/chat/completions", json.dumps(data).encode(),
                                 {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=60) as response:
        result = json.load(response)
    choice = result["choices"][0]
    text = choice["message"].get("content") or ""
    for name in ("think", "thought"):
        text = re.sub(rf"<{name}\b[^>]*>[\s\S]*?</{name}\s*>", "", text, flags=re.I)
        text = re.sub(rf"<{name}\b[^>]*>[\s\S]*$", "", text, flags=re.I)
    return {"content": text.strip(), "finishReason": choice.get("finish_reason")}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--holdout", action="store_true", help="Use separate public cases without changing the exported production prompt.")
    args = parser.parse_args()
    source = ROOT / "Server/ARDY/runtime/unity-naturalness-validation/Tools/MotionAdapter/reports/agent-self-inspection-regression.json"
    fixture = json.loads(source.read_text(encoding="utf-8"))
    template = fixture["publicProbePrompt"]
    begin = template.index('{"userTurn":')
    base, length = json.JSONDecoder().raw_decode(template[begin:])
    cases = [
        ("promise-with-only-nod", base["request"], base["responses"], False, {"continue"}),
        ("answered", "你有哪些预制动作？", ["已检查注册动作资源：有左挥手、右挥手、点头、摇头。当前没有演示动作。"], False, {"closed", "none"}),
        ("waiting-for-user", "你帮我选一个。", ["你想从哪些选项里选呢？"], False, {"waiting_user"}),
        ("real-operation-pending", "检查一下本地曲库。", ["正在查询。<song_catalog query=\"\"/>"], True, {"waiting_tool"}),
        ("unavailable-explained", "检查绑定的角色。", ["检查结果显示目前没有绑定角色，所以现在无法确认身体动作。"], False, {"closed", "none"}),
        ("cancelled", "不用检查了，先保持安静。", ["好的。"], False, {"closed", "none"}),
    ]
    if args.holdout:
        cases = [
            ("voice-inspection-en", "Please inspect your voice queue and tell me if anything is still pending.",
             ["I'll take a look and get back to you shortly."], False, {"continue"}),
            ("capability-list-ja", "登録されている基本動作の名前を教えて。実演はいらないよ。",
             ["登録されているのは左手の挨拶、右手の挨拶、うなずき、首振りです。"], False, {"closed", "none"}),
            ("clarification-en", "Check the setting we discussed.",
             ["Which setting would you like me to check?"], False, {"waiting_user"}),
            ("no-real-operation", "确认一下声音队列。", ["我正在检查，稍后告诉你。<next in=\"5s\"/>"], False, {"continue"}),
        ]
    report = {"sourceSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
              "probePromptSha256": hashlib.sha256(template.encode()).hexdigest(),
              "formalContractSha256": hashlib.sha256(fixture["publicFormalContract"].encode()).hexdigest(), "cases": [],
              "scope": "Public synthetic contexts and existing Qwen only; no private persona, actual scene, TTS or motion execution.",
              "status": "running"}
    path = ROOT / "Logs/agent-self-inspection-20260910" / ("public-model-holdout.json" if args.holdout else "public-model-probe.json")
    try:
        for identifier, user, replies, busy, allowed in cases:
            facts = {**base, "request": user, "responses": replies, "toolInFlight": busy}
            if identifier in {"answered", "capability-list-ja"}:
                facts["lastInspection"] = json.dumps(fixture["actualUnboundResourceInspection"], ensure_ascii=False)
            elif identifier == "unavailable-explained":
                facts["lastInspection"] = '{"status":"no-bound-inspection-provider"}'
            prompt = template[:begin] + json.dumps(facts, ensure_ascii=False) + template[begin + length:]
            started = time.perf_counter()
            result = request(args.url, [{"role": "system", "content": "你是与用户交流的虚拟角色。"},
                                       {"role": "user", "content": prompt}])
            match = re.search(r"\{[\s\S]*\}", result["content"])
            decision = json.loads(match.group()) if match else {}
            result.update(id=identifier, expected=sorted(allowed), decision=decision,
                          passed=decision.get("work_status") in allowed and result["finishReason"] == "stop",
                          elapsedSeconds=round(time.perf_counter() - started, 3))
            report["cases"].append(result)
            print(json.dumps({"id": identifier, "passed": result["passed"], "decision": decision}, ensure_ascii=False), flush=True)
        if args.holdout:
            report["status"] = "completed"
            return all(case["passed"] for case in report["cases"])
        user = "你有哪些预制动作？请确认后告诉我。"
        previous = "ちょっと待っててね。<motion name=\"nod\"/><next in=\"5s\" focus=\"平静\"/>"
        messages = [{"role": "system", "content": "你是与用户交流的虚拟角色，简短自然地回复。" + fixture["publicFormalContract"]},
                    {"role": "user", "content": user}, {"role": "assistant", "content": previous},
                    {"role": "system", "content": "[继续上一用户事项；不是主动闲聊] 上轮尚未检查或回答，请根据工具协议实际检查后回答。"}]
        action = request(args.url, messages)
        report["inspectionRequest"] = action
        report["inspectionRequest"]["validToolSelected"] = bool(re.search(r'<body_inspect\s+scope="motion"\s*/>', action["content"]))
        messages += [{"role": "assistant", "content": action["content"]},
                     {"role": "system", "content": "[只读自检已经执行；新的程序结果]\n" +
                      json.dumps(fixture["actualUnboundResourceInspection"], ensure_ascii=False) +
                      "\n这是实际未绑定测试实例的结果。请据此回答原问题并说明限制，不要再次只说稍等。"}]
        report["resultReply"] = request(args.url, messages)
        report["status"] = "completed"
        return all(case["passed"] for case in report["cases"]) and report["inspectionRequest"]["validToolSelected"]
    except Exception as error:
        report["status"] = "failed"
        report["error"] = f"{type(error).__name__}: {error}"
        raise
    finally:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(json.dumps({"status": report["status"], "report": str(path)}, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    raise SystemExit(0 if main() else 1)
