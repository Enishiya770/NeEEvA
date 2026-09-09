"""Offline A/B experiment; no changes to Unity speech, tool execution, or memory.

Reuses recorded model outputs and current role prompt for draft comparisons.
The reviewer has its own small prompt. JSON shape checks are never a semantic oracle.
"""
import argparse
import hashlib
import json
import math
from pathlib import Path
import re
import statistics
import time
import urllib.request

from check_speech_language_replay import ROOT, load_prompts

CONFIG = ROOT / "Tools/speech_review_experiment"
LANGUAGES = ["ja", "zh", "en"]
SEGMENT = {"type": "object", "properties": {"language": {"type": "string", "enum": LANGUAGES},
            "text": {"type": "string"}}, "required": ["language", "text"], "additionalProperties": False}
REVIEW_SCHEMA = {"type": "object", "properties": {
    "status": {"type": "string", "enum": ["unchanged", "repaired", "needs_review"]},
    "segments": {"type": "array", "items": SEGMENT}},
    "required": ["status", "segments"], "additionalProperties": False}
DRAFT_SCHEMA = {"type": "object", "properties": {
    "draft": {"type": "string", "maxLength": 24},
    "language": {"type": "string", "enum": LANGUAGES},
    "observed_mode": {"type": "string", "enum": ["speech", "singing", "uncertain"]},
    "mode_confidence": {"type": "number", "minimum": 0, "maximum": 1},
    "confidence": {"type": "number", "minimum": 0, "maximum": 1},
    **{s: {"type": "string", "maxLength": 8} for s in ("understanding", "uncertainty", "inner_reaction")}},
    "required": ["draft", "language", "observed_mode", "mode_confidence", "confidence",
                 "understanding", "uncertainty", "inner_reaction"], "additionalProperties": False}


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def normalize_segments(parts):
    # Coalesce adjacent identical languages; preserve every character.
    result = []
    for part in parts:
        if not part["text"]:
            continue
        if result and result[-1]["language"] == part["language"]:
            result[-1]["text"] += part["text"]
        else:
            result.append(dict(part))
    return result


def parse_review(raw, finish, before):
    """Strict, fail-closed structural validation. No automatic repair or Han heuristics."""
    try:
        if finish != "stop":
            raise ValueError("incomplete generation: " + str(finish))
        obj = json.loads(raw)
        if not isinstance(obj, dict) or set(obj) != {"status", "segments"}:
            raise ValueError("invalid review fields")
        if obj["status"] not in ("unchanged", "repaired", "needs_review"):
            raise ValueError("invalid status")
        if not isinstance(obj["segments"], list):
            raise ValueError("segments must be an array")
        for part in obj["segments"]:
            if not isinstance(part, dict) or set(part) != {"language", "text"}:
                raise ValueError("invalid segment fields")
            if part["language"] not in LANGUAGES or not isinstance(part["text"], str) or not part["text"].strip():
                raise ValueError("missing language or speech")
            if re.search(r"<[^>]*>", part["text"]):
                raise ValueError("control markup in reviewed speech")
        if obj["status"] == "needs_review":
            if obj["segments"]:
                raise ValueError("unresolved result contains releasable speech")
        elif not obj["segments"]:
            raise ValueError("empty result")
        if obj["status"] == "unchanged" and obj["segments"] != before:
            raise ValueError("unchanged status modified text or language")
        return obj, []
    except (ValueError, KeyError, TypeError) as error:
        return None, [str(error)]


def parse_draft(raw, finish):
    try:
        if finish != "stop":
            raise ValueError("incomplete generation: " + str(finish))
        obj = json.loads(raw)
        if not isinstance(obj, dict) or set(obj) != set(DRAFT_SCHEMA["required"]):
            raise ValueError("missing/extra draft fields")
        if obj["language"] not in LANGUAGES or obj["observed_mode"] not in ("speech", "singing", "uncertain"):
            raise ValueError("invalid language/mode")
        for field in ("mode_confidence", "confidence"):
            if type(obj[field]) not in (float, int) or not math.isfinite(obj[field]) or not 0 <= obj[field] <= 1:
                raise ValueError("invalid confidence")
        for field in ("draft", "understanding", "uncertainty", "inner_reaction"):
            if not isinstance(obj[field], str) or re.search(r"<[^>]*>", obj[field]):
                raise ValueError("invalid text or control markup")
        return obj, []
    except (ValueError, TypeError, KeyError) as error:
        return None, [str(error)]


def clean_user(text):
    text = re.sub(r"^\[说话人:[^\]]*\]\s*", "", text)
    return text.split(" [同轮分轨观测", 1)[0].strip()


def context_from_record(record):
    messages = [m for m in record["request"]["messages"] if m["role"] != "system"]
    current = messages[-1]["content"]
    if record["kind"] == "draft":
        current = current.split("\n", 2)[-1].split("\n\n", 1)[0]
    return clean_user(current), [{"role": m["role"], "text": clean_user(m["content"])[:1200]}
                                 for m in messages[-5:-1]]


def build_cases(replay, routing):
    cases = []
    route_by_id = {r["id"]: r for r in routing["records"]}
    for record in replay["records"]:
        routed = route_by_id[record["id"]]
        if routed["status"] != "routed_without_synthesis":
            continue  # Never send partial JSON as an audible candidate.
        parts = [{"language": p["language"], "text": p["text"]} for p in routed["source_segments"]]
        current, history = context_from_record(record)
        cases.append({"id": record["id"], "origin": "recorded_model_output", "segments": parts,
                      "current_user": current, "recent_dialogue": history,
                      "expected_languages": record.get("expected_languages")})
    def add(name, user, parts, exact=None, history=None, languages=None):
        cases.append({"id": "guard/" + name, "origin": "explicitly_constructed_fixture",
                      "segments": [{"language": lang, "text": text} for lang, text in parts],
                      "current_user": user, "recent_dialogue": history or [],
                      "expected_exact": exact, "expected_languages": languages})
    add("whole_chinese_bad_label", "你可以用中文回应我。", [("ja", "嗯，我在听哦。不用着急，慢慢想。")],
        [["zh", "嗯，我在听哦。不用着急，慢慢想。"]], languages=["zh"])
    add("ja_han", "请用日语只回答：了解。", [("ja", "了解。")], [["ja", "了解。"]], languages=["ja"])
    add("zh_han", "请用中文只回答：了解。", [("zh", "了解。")], [["zh", "了解。"]], languages=["zh"])
    add("natural_switch", "先用中文说听到哦。然后用日语说了解。", [("zh", "听到哦。"), ("ja", "了解。")],
        [["zh", "听到哦。"], ["ja", "了解。"]], languages=["zh", "ja"])
    add("explicit_quoted_name", "请用日语解释，但提到中文原名安托涅瓦时按中文念这个名字。",
        [("ja", "中国語の名前は「安托涅瓦」です。")], languages=["ja", "zh", "ja"])
    add("known_kanji_brand_number", "请保持日语，只说下面这句。", [("ja", "東京でSonyの製品を3個買いました。")],
        [["ja", "東京でSonyの製品を3個買いました。"]], languages=["ja"])
    add("unknown_reading", "请用日语告诉我虚构人名焰珞珀的官方日语读法。资料没有提供读音。",
        [("ja", "焰珞珀はエンラポと読みます。")], languages=["ja"])
    add("negation_number", "请保留日语原话中的否定和数字。", [("ja", "私は3個買っていません。明日は行きません。")],
        [["ja", "私は3個買っていません。明日は行きません。"]], languages=["ja"])
    add("correct_name", "日语简单自我介绍。", [("ja", "アントネーワです。")],
        [["ja", "アントネーワです。"]], languages=["ja"])
    add("ordinary_term", "请用日语回答耳机声场的变化。", [("ja", "耳机的声场は変わりますものね。")], languages=["ja"])
    return cases


def summarize(records):
    result = {}
    for arm in sorted({r["arm"] for r in records}):
        rows = [r for r in records if r["arm"] == arm]
        times = sorted(r["seconds"] for r in rows)
        result[arm] = {"count": len(rows), "structurally_valid": sum(r["parsed"] is not None for r in rows),
                       "p50_seconds": round(statistics.median(times), 3),
                       "p95_seconds": times[math.ceil(len(times) * .95) - 1],
                       "format_or_fixture_flags": sum(bool(r["issues"]) for r in rows),
                       "needs_review": sum((r["parsed"] or {}).get("status") == "needs_review" for r in rows)}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="http://127.0.0.1:8080")
    parser.add_argument("--runs", type=int, default=2, choices=range(1, 6))
    parser.add_argument("--suite", choices=("all", "drafts", "review"), default="all")
    parser.add_argument("--seed-start", type=int, default=73)
    parser.add_argument("--compact-prompt", type=Path, default=CONFIG / "draft_compact.txt")
    parser.add_argument("--counterbalance", action="store_true", help="Rotate draft arm order to expose order/cache effects")
    parser.add_argument("--output", type=Path, default=ROOT / "Logs/speech-language-check/review-experiment.json")
    args = parser.parse_args()
    folder = ROOT / "Logs/speech-language-check"
    replay, routing = read_json(folder / "replay.json"), read_json(folder / "replay-routing.json")
    if routing["input_sha256"] != sha(folder / "replay.json"):
        raise ValueError("Routing and generation evidence do not match")
    reviewer = (CONFIG / "reviewer.txt").read_text(encoding="utf-8")
    compact = args.compact_prompt.read_text(encoding="utf-8")
    system, _, _, budget, hashes = load_prompts()
    persona = (ROOT / "Assets/AIChatTookit/Prompts/persona.txt").read_text(encoding="utf-8")
    # Only name facts enter the independent reviewer; no behavioral role prompt.
    known_names = "\n".join(line for line in persona.splitlines() if line.startswith(("- 姓名:", "- 名称对应:")))
    report = {"status": "running", "seed_start": args.seed_start, "counterbalance": args.counterbalance,
              "compact_prompt": str(args.compact_prompt), "compact_prompt_sha256": sha(args.compact_prompt),
              "source_sha256": sha(folder / "replay.json"),
              "routing_sha256": sha(folder / "replay-routing.json"), "prompt_sha256": hashes,
              "experiment_prompt_sha256": {p.name: sha(p) for p in CONFIG.glob("*.txt")},
              "limitations": ["Same local model, independent task prompt; no guarantee of semantic correctness.",
                              "Offline full-response validation, not live sentence-gated playback.",
                              "Latency is idle HTTP elapsed time, includes prefill and output; no concurrency benchmark.",
                              "Only recorded context is available; not an exact original HTTP replay.",
                              "JSON shape / selected fixtures are not semantic quality acceptance."], "records": []}

    def save():
        args.output.parent.mkdir(parents=True, exist_ok=True)
        report["summary"] = summarize(report["records"]) if report["records"] else {}
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    def call(case_id, arm, repeat, messages, cap, schema=None, before=None, case=None):
        with urllib.request.urlopen(args.base + "/slots", timeout=5) as response:
            slots = json.load(response)
        if not isinstance(slots, list) or not slots or any(s.get("is_processing", True) for s in slots):
            raise RuntimeError("Local LLM is busy; stopping without interrupting a conversation")
        payload = {"model": "antoneva", "stream": False, "messages": messages, "temperature": 0.2 if arm.startswith("draft") else 0,
                   "max_tokens": cap, "seed": args.seed_start + repeat, "enable_thinking": False,
                   "chat_template_kwargs": {"enable_thinking": False}, "id_slot": slots[-1]["id"]}
        if schema:
            payload["response_format"] = {"type": "json_object", "schema": schema}
        request = urllib.request.Request(args.base + "/v1/chat/completions",
                                        json.dumps(payload, ensure_ascii=False).encode("utf-8"),
                                        {"Content-Type": "application/json"})
        started = time.perf_counter()
        with urllib.request.urlopen(request, timeout=60) as response:
            result = json.load(response)
        seconds = round(time.perf_counter() - started, 3)
        choice = result["choices"][0]
        raw, finish = choice["message"]["content"], choice.get("finish_reason")
        parsed, issues = parse_draft(raw, finish) if arm.startswith("draft") else parse_review(raw, finish, before)
        if case and parsed and "segments" in parsed and parsed["status"] != "needs_review":
            parts = normalize_segments(parsed["segments"])
            if case.get("expected_languages") and [p["language"] for p in parts] != case["expected_languages"]:
                issues.append("fixture: wrong requested language sequence")
            if case.get("expected_exact") and [[p["language"], p["text"]] for p in parts] != case["expected_exact"]:
                issues.append("fixture: exact text/language changed")
        row = {"id": f"{arm}/{repeat + 1}/{case_id}", "case_id": case_id, "arm": arm, "run": repeat + 1,
               "request": payload, "raw": raw, "finish_reason": finish, "seconds": seconds,
               "usage": result.get("usage"), "parsed": parsed, "issues": issues, "text_review": "pending"}
        if before is not None:
            row["before"] = before
        report["records"].append(row)
        save()
        print(json.dumps({k: row[k] for k in ("id", "raw", "issues", "seconds")}, ensure_ascii=False), flush=True)

    try:
        if args.suite in ("all", "drafts"):
            for repeat in range(args.runs):
                # Four distinct contexts; both arms get identical role/context and seed.
                for index, source in enumerate([r for r in replay["records"] if r["kind"] == "draft" and "/1/" in r["id"]]):
                    messages = [dict(m) for m in source["request"]["messages"]]
                    messages[0]["content"] = system
                    # Preserve task/evidence and mode instructions, replace only verbose output specification.
                    shortened = [dict(m) for m in messages]
                    instruction = shortened[-1]["content"]
                    prefix = instruction.split("只输出", 1)[0]
                    shortened[-1]["content"] = prefix + compact
                    arms = [("draft_baseline", messages, None), ("draft_compact", shortened, None),
                            ("draft_compact_schema", shortened, DRAFT_SCHEMA)]
                    if args.counterbalance:
                        rotate = (index + repeat) % len(arms)
                        arms = arms[rotate:] + arms[:rotate]
                    for arm, request_messages, schema in arms:
                        call(source["id"], arm, repeat, request_messages, budget, schema)
        if args.suite in ("all", "review"):
            cases = build_cases(replay, routing)
            report["review_cases"] = cases
            for repeat in range(args.runs):
                for case in cases:
                    data = {k: case[k] for k in ("segments", "current_user", "recent_dialogue")}
                    data["known_names"] = known_names
                    messages = [{"role": "system", "content": reviewer},
                                {"role": "user", "content": json.dumps(data, ensure_ascii=False)}]
                    call(case["id"], "review", repeat, messages, 768, REVIEW_SCHEMA, case["segments"], case)
        report["status"] = "completed_requires_text_review"
    except Exception as error:
        report["status"], report["error"] = "interrupted", str(error)
        raise
    finally:
        save()
    print(json.dumps(report["summary"], ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
