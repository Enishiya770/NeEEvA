"""One fixed public A/B pair; no private conversation, no motion execution, no retry."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import time
import urllib.request

from Tools.MotionAdapter import validate_intent_consistency as checks

ROOT = Path(__file__).resolve().parents[2]
WORK = ROOT / "Server/ARDY/runtime/feedback-placement-v1"
REPORT = ROOT / "Tools/MotionAdapter/reports/feedback-placement-v1/ab-public.json"
PUBLIC_USER = "双臂向前平伸，用双手跟我打招呼，掌心朝我。"
REJECTED_ASSISTANT = '<lang code="zh"/>好的，我把双臂伸到前面向你打招呼。<motion name="compose" left="forward" right="forward" palm="partner" joint="wrists" axis="palm-normal" amplitude="8" cycles="2" seconds="3.2" end="idle"/>'
FEEDBACK = ("[最新动作执行反馈；发生于紧前的 assistant 回复之后]\n"
            "刚才的 compose 指令被解析器拒绝，未派发任何动作。原因：未知属性 palm；合法的两侧掌向属性为 leftPalm 和 rightPalm。"
            "用户的原意和双臂前伸、掌心朝我的要求不变；只剩一次修订机会。"
            "请现在给出一次非空公开修订：可用正确的动作标签并简短说明尚待执行，或用一句公开话说明无法执行的限制。"
            "不能把被拒动作说成已完成，不要只返回静默。")


def sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def speech_contract() -> str:
    source = (ROOT / "Assets/AIChatTookit/Scripts/Chat/SpeechText.cs").read_text(encoding="utf-8-sig")
    literal = r'"(?:\\.|[^"\\])*"'
    match = re.search(r"const string OutputContract\s*=\s*(" + literal + r"(?:\s*\+\s*" + literal + r")*)\s*;", source)
    if not match:
        raise RuntimeError("Cannot freeze the complete production speech contract")
    return "".join(json.loads(token) for token in re.findall(literal, match.group(1)))


def idle() -> dict:
    value = checks.health_snapshot("http://127.0.0.1:8080", "http://127.0.0.1:8093")
    if not checks.all_slots_idle(value["slots"]) or not value["ardyReady"] or value["queue"].get("waiting") != 0 or value["queue"].get("running") != 0:
        raise RuntimeError("Existing user services are busy; no model request issued")
    return value


def public_stream(payload: dict) -> dict:
    content = []
    result = {"finishReason": None, "requestId": None, "model": None, "usage": None}
    started = time.perf_counter()
    request = urllib.request.Request("http://127.0.0.1:8080/v1/chat/completions",
        json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode(),
        {"Content-Type": "application/json", "Accept": "text/event-stream"})
    with urllib.request.urlopen(request, timeout=45) as response:
        result["httpStatus"] = response.status
        for line in response:
            line = line.decode("utf-8").strip()
            if not line.startswith("data:") or line[5:].strip() == "[DONE]":
                continue
            data = json.loads(line[5:])
            result["requestId"] = data.get("id", result["requestId"])
            result["model"] = data.get("model", result["model"])
            if data.get("usage"):
                result["usage"] = data["usage"]
            for choice in data.get("choices", []):
                if choice.get("finish_reason"):
                    result["finishReason"] = choice["finish_reason"]
                # Never inspect or persist reasoning_content. Unexpected inline private
                # blocks are passed in memory directly to the production channel parser.
                delta = choice.get("delta", {}).get("content")
                if delta:
                    content.append(delta)
    result["elapsedSeconds"] = time.perf_counter() - started
    result["csharp"] = checks.inspect_reply("".join(content), PUBLIC_USER)
    parsed = result["csharp"]
    result["publicNonempty"] = bool(parsed.get("publicSpeech", "").strip() or parsed.get("accepted"))
    result["validActionTag"] = bool(parsed.get("accepted") and not parsed.get("rejection") and not parsed.get("compileError"))
    result["motionExecuted"] = False
    return result


def main() -> None:
    WORK.mkdir(parents=True, exist_ok=True)
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    if REPORT.exists() or (WORK / "once.json").exists():
        raise RuntimeError("This fixed A/B pair has already been reserved; no rerun or selection is allowed")
    checks.BUILD = WORK / "parser"
    checks.HARNESS = checks.BUILD / "intent-consistency.exe"
    parser_hashes = checks.compile_harness()
    contract = checks.read_contract(False)
    speech = speech_contract()
    base = {"role": "system", "content": "你是面对面交流的虚拟角色，简短自然回复。以下全部是公开合成对照，不包含私人角色设定。"}
    context = {"role": "system", "content": contract["text"] + "\n" + speech}
    user = {"role": "user", "content": PUBLIC_USER}
    assistant = {"role": "assistant", "content": REJECTED_ASSISTANT}
    feedback = {"role": "system", "content": FEEDBACK}
    arms = [("A-before-last-user", [base, context, feedback, user, assistant]),
            ("B-after-rejected-assistant", [base, context, user, assistant, feedback])]
    common = {"model": "qwen36", "stream": True, "stream_options": {"include_usage": True},
              "temperature": 0, "seed": 0, "max_tokens": 700, "id_slot": 1,
              "enable_thinking": False, "chat_template_kwargs": {"enable_thinking": False}}
    requests = [{"id": key, "payload": {**common, "messages": messages}} for key, messages in arms]
    for row in requests:
        row["requestSha256"] = sha(json.dumps(row["payload"], ensure_ascii=False, separators=(",", ":")).encode())
    rejected = checks.inspect_reply(REJECTED_ASSISTANT, PUBLIC_USER)
    if rejected["accepted"] or not rejected["rejection"]:
        raise RuntimeError("The frozen invalid palm attribute was not rejected by the current production parser")
    report = {"status": "prepared", "startedAtUtc": checks.utc(), "modelRequests": 0,
        "design": "A then B, one request each, fixed beforehand. Only short feedback position changes; complete ordinary contracts remain before the same public user in both. Same rejected assistant retained. No private prompt/history/images/holdout.",
        "limitation": "Two public requests are a paired mechanism probe, not an error-rate estimate or reproduction of the private live conversation. Transport requests reproduce message order independently; they do not execute ChatQW or motion/TTS.",
        "privateHandling": "reasoning_content ignored; unexpected inline private content parsed only in memory and discarded by production RoleOutputChannels; saved replies contain public channels and private character counts only.",
        "parserSourceSha256": parser_hashes, "contractPartsSha256": contract["parts"],
        "speechContractSha256": sha(speech.encode()), "rejectedAssistantParser": rejected,
        "providerSourceSha256": sha((ROOT / "Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.cs").read_bytes()),
        "templateSourceSha256": sha((ROOT / "Tools/Remote/qwen36_chat_template.jinja").read_bytes()),
        "requests": requests, "results": []}
    initial_health = idle()
    report["initialHealth"] = initial_health
    checks.exclusive_json(WORK / "once.json", {"reservedAtUtc": checks.utc(), "requests": [{"id": row["id"], "sha256": row["requestSha256"]} for row in requests]})
    try:
        for row in requests:
            health = idle()
            checks.exclusive_json(WORK / (row["id"] + ".attempt.json"), {"attemptedAtUtc": checks.utc(), "requestSha256": row["requestSha256"]})
            report["modelRequests"] += 1
            result = public_stream(row["payload"])
            report["results"].append({"id": row["id"], "requestSha256": row["requestSha256"], "healthBefore": health, **result})
            REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            print(json.dumps({"id": row["id"], "publicNonempty": result["publicNonempty"], "validActionTag": result["validActionTag"], "finishReason": result["finishReason"]}), flush=True)
        report["status"] = "completed"
    except Exception as error:
        report["status"] = "failed-or-busy-no-retry"
        report["error"] = type(error).__name__ + ": " + str(error)
        raise
    finally:
        report["finishedAtUtc"] = checks.utc()
        REPORT.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
