"""Replay logged conversations and current JSON draft prompts against an idle local LLM.

This is an offline evaluation, not a runtime language detector. Raw requests/results
are retained for human review; metadata checks alone never certify prose quality.
No returned tools are executed and no Unity history/memory is modified.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import time
import urllib.request

from check_speech_language import CASES, get_json, inspect

ROOT = Path(__file__).resolve().parents[1]
LITERAL = r'"(?:[^"\\]|\\.)*"'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def source_expression(source, method, variable, value):
    """Read the actual C# string concatenation; fail if its structure changes."""
    body = source.split("private IEnumerator " + method + "(", 1)[1]
    expression = body.split("string prompt =", 1)[1].split(";\n", 1)[0].strip()
    tokens = re.findall(LITERAL + r"|[A-Za-z_][A-Za-z_0-9]*|\S", expression)
    result = []
    for i, token in enumerate(tokens):
        if i % 2:
            if token != "+":
                raise ValueError("Unsupported C# prompt expression: " + token)
        elif token.startswith('"'):
            result.append(json.loads(token))
        elif token == variable:
            result.append(value)
        else:
            raise ValueError("Unknown variable in C# prompt: " + token)
    return "".join(result)


def load_prompts():
    paths = [ROOT / "Assets/AIChatTookit/Prompts" / name
             for name in ("persona.txt", "language.txt", "behavior.txt")]
    system = "\n\n".join(re.sub(r"<!-- SKILL-BEGIN:.*?<!-- SKILL-END -->", "",
                               p.read_text(encoding="utf-8"), flags=re.S).strip() for p in paths)
    speech = ROOT / "Assets/AIChatTookit/Scripts/Chat/SpeechText.cs"
    contract = re.search(r'public const string OutputContract\s*=\s*(.*?);',
                         speech.read_text(encoding="utf-8"), re.S)[1]
    contract = "".join(json.loads(s) for s in re.findall(LITERAL, contract))
    chat = ROOT / "Assets/AIChatTookit/Scripts/Chat/ChatSample.cs"
    scene = ROOT / "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity"
    scene_text = scene.read_text(encoding="utf-8")
    max_tokens = int(re.search(r"m_EphemeralMaxTokens: (\d+)", scene_text)[1])
    keep = int(re.search(r"m_EphemeralHistoryMessages: (\d+)", scene_text)[1])
    if keep != 0:
        raise ValueError("Replay currently requires the scene's full draft history setting")
    hashes = {str(p.relative_to(ROOT)): digest(p) for p in paths + [speech, chat, scene]}
    return system, contract, chat.read_text(encoding="utf-8"), max_tokens, hashes


def extract_turns(path):
    # Start at the second Play session's first ASR-timed turn. Keep speaker and
    # multiline ASR observations, including the user's corrections, verbatim.
    text = path.read_text(encoding="utf-8").replace("\r", "")
    lines = text.split("\n")
    turns = []
    pending = None
    index = 0
    while index < len(lines):
        line = lines[index]
        start = re.match(r'^\[Timing\] ASR done \+[^:]+: "(.*)', line)
        if start:
            at = index + 1
            fragments = [start[1]]
            while not fragments[-1].endswith('"'):
                index += 1
                if index >= len(lines):
                    raise ValueError("Unterminated ASR log event")
                fragments.append(lines[index])
            pending = {"user": "\n".join(fragments)[:-1], "user_log_line": at}
        elif line.startswith("[LLM原文] ") and pending:
            pending.update(original_assistant=line[len("[LLM原文] "):].replace("\\n", "\n"),
                           assistant_log_line=index + 1)
            turns.append(pending)
            pending = None
        index += 1
    if len(turns) != 6 or "你忘了说你自己的名字" not in turns[2]["user"]:
        raise ValueError("This replay expects the six ASR turns in the 2026-09-09 snapshot")
    # The first greeting's user message is not available in this log format;
    # do not invent it. This limitation is recorded in every report.
    return turns


def check_raw(raw, languages=None, exact=None, draft=False):
    if not draft:
        parts, issues = inspect(raw, languages or [], exact)
        if languages is None:
            issues = [s for s in issues if not s.startswith("language sequence")]
        if any(code not in ("ja", "zh", "en") for code, _ in parts):
            issues.append("missing/invalid speech language declaration")
        if not parts:
            issues.append("no audible reply; inspect private/tool-only output")
        return parts, issues
    issues = []
    try:
        # Match the production parser's outermost-brace extraction. Also expose
        # non-JSON wrappers, which its permissive parsing can otherwise conceal.
        begin, end = raw.index("{"), raw.rindex("}")
        data = json.loads(raw[begin:end + 1])
        if raw[:begin].strip() or raw[end + 1:].strip():
            issues.append("content outside JSON object")
        code, text = data.get("language"), data.get("draft")
        if not isinstance(text, str):
            raise ValueError("draft is not a string")
        if code not in ("ja", "zh", "en"):
            issues.append("missing/invalid draft language")
        if re.search(r"<[^>]*>", text):
            issues.append("control markup inside JSON draft")
        if not text.strip():
            issues.append("empty draft; no speech available for language evaluation")
        parts, text_issues = inspect(f'<lang code="{code}"/>{text}', [code], None)
        issues.extend(text_issues)
        if languages and [code] != languages:
            issues.append(f"draft language {code!r}, expected {languages!r}")
        # A known observed whole-Chinese fixture, not a general classifier.
        if code == "ja" and re.search("我在听|不用着急|慢慢想", text):
            issues.append("known whole-Chinese draft declared Japanese")
        return [[code, text]], issues
    except (ValueError, TypeError, AttributeError) as error:
        return [], ["invalid draft JSON: " + str(error)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="http://127.0.0.1:8080")
    parser.add_argument("--log", type=Path, default=ROOT / "Logs/unity-review-20260909-speech-latest.log")
    parser.add_argument("--runs", type=int, default=2, choices=range(1, 6))
    parser.add_argument("--output", type=Path, default=ROOT / "Logs/speech-language-check/replay.json")
    args = parser.parse_args()
    system, contract, chat_source, draft_budget, hashes = load_prompts()
    turns = extract_turns(args.log)
    report = {
        "schema_version": 1, "prompt_sha256": hashes, "log_sha256": digest(args.log),
        "limitations": ["Reconstructs logged user/assistant text, not captured HTTP requests.",
                         "Original dynamic memory, skill catalog and perception frames are unavailable.",
                         "Starts at the first ASR-timed turn; preceding greeting input is unavailable.",
                         "Fixed user interventions may be redundant after regenerated replies.",
                         "Formal generation uses non-streaming transport and a 512-token evaluation cap.",
                         "Known-word checks are diagnostic only; all prose requires human review.",
                         "No tools, Unity conversation/memory writes, or speaker playback."],
        "source_turns": turns, "records": [], "status": "running",
    }

    def save():
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")

    def run(case, group, run_id, history, prompt, languages=None, exact=None, draft=False):
        slots = get_json(args.base + "/slots")
        if not isinstance(slots, list) or not slots or any(s.get("is_processing", True) for s in slots):
            raise RuntimeError("LLM is busy; stop without interrupting the user's conversation")
        messages = [{"role": "system", "content": system}] + list(history)
        if not draft:
            messages.append({"role": "system", "content": contract})
        messages.append({"role": "user", "content": prompt})
        payload = {"model": "antoneva", "messages": messages, "stream": False,
                   "seed": 73 + run_id, "max_tokens": draft_budget if draft else 512,
                   "id_slot": slots[-1]["id"], "enable_thinking": False,
                   "chat_template_kwargs": {"enable_thinking": False}}
        if draft:
            payload["temperature"] = 0.2
        request = urllib.request.Request(args.base + "/v1/chat/completions",
                                        json.dumps(payload, ensure_ascii=False).encode("utf-8"),
                                        {"Content-Type": "application/json"})
        started = time.perf_counter()
        with urllib.request.urlopen(request, timeout=60) as response:
            result = json.load(response)
        choice = result["choices"][0]
        raw = choice["message"]["content"]
        parts, issues = check_raw(raw, languages, exact, draft)
        if choice.get("finish_reason") != "stop":
            issues.append("generation did not finish normally: " + str(choice.get("finish_reason")))
        record = {"id": f"{group}/{run_id + 1}/{case}", "group": group, "kind": "draft" if draft else "formal",
                  "raw": raw, "segments": parts, "expected_languages": languages,
                  "issues": issues, "human_review": "pending", "request": payload,
                  "finish_reason": choice.get("finish_reason"), "usage": result.get("usage"),
                  "seconds": round(time.perf_counter() - started, 3)}
        report["records"].append(record)
        save()
        print(json.dumps({k: record[k] for k in ("id", "raw", "issues", "seconds")}, ensure_ascii=False), flush=True)
        return raw

    try:
        for repeat in range(args.runs):
            for name, prompt, languages, exact in CASES:
                run(name, "controls", repeat, [], prompt, languages, exact)
            for group in ("logged_history", "regenerated_history"):
                history = []
                for index, turn in enumerate(turns):
                    expected = ["ja"] if index >= 4 else None
                    raw = run(f"turn{index + 1}", group, repeat, history, turn["user"], expected)
                    history += [{"role": "user", "content": turn["user"]},
                                {"role": "assistant", "content": turn["original_assistant"]
                                 if group == "logged_history" else raw}]
            # Both auxiliary paths receive the actual current C# task instructions.
            # Partial input is from the log; the singing acoustic evidence is a
            # labelled synthetic boundary fixture, not a reconstructed recording.
            for name, upto, fragment, method, variable, expected in (
                ("hesitation", 3, "嗯嗯呃我在想我在。", "RequestSpeculativeDraftAfterDelay", "transcript", None),
                ("name_feedback", 3, "因为你刚才是用日语的说法，把中文的安托涅瓦说出来了。", "RequestSpeculativeDraftAfterDelay", "transcript", None),
                ("explicit_japanese", 5, "那我们还是用日就你还是用日语跟我说就行，没关系的，不用跟我说中文。", "RequestSpeculativeDraftAfterDelay", "transcript", ["ja"]),
                ("singing_boundary", 3, "[合成测试证据] singing=0.58 pitch=0.55；当前转写：嗯嗯呃我在想我在。尚无可靠歌名或歌词。", "RequestSingingDraftAfterDelay", "evidence", None),
            ):
                history = []
                for turn in turns[:upto]:
                    history += [{"role": "user", "content": turn["user"]},
                                {"role": "assistant", "content": turn["original_assistant"]}]
                prompt = source_expression(chat_source, method, variable, fragment)
                run(name, "drafts", repeat, history, prompt, expected, draft=True)
        report["status"] = "completed_requires_human_review"
    except Exception as error:
        report["status"] = "interrupted"
        report["error"] = str(error)
        raise
    finally:
        save()
    print(f"Saved {len(report['records'])} samples to {args.output}; prose review remains required.", flush=True)


if __name__ == "__main__":
    main()
