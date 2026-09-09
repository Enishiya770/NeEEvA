"""Exercise the real role prompts against an idle local LLM; produces no speech or memories.

This is a small generation evaluation, not a general Chinese/Japanese classifier.
It replaces the selected server slot's KV cache, so it refuses a busy server.
"""
import argparse
import json
from pathlib import Path
import re
import time
import urllib.request


CASES = [
    ("ja_shared_han", "请用日语只回答两个汉字：了解。", ["ja"], "了解。"),
    ("zh_shared_han", "请用中文只回答两个汉字：了解。", ["zh"], "了解。"),
    ("sentence_switch", "先用中文说‘听到哦。’，然后用日语说‘了解。’。只说这两句。", ["zh", "ja"], "听到哦。了解。"),
    ("persona", "安托涅瓦，请用日语简短介绍你的名字和在中央庭的工作。", ["ja"], None),
    ("terminology", "我刚换了一副耳机，感觉声场变了。你用日语聊聊这个感觉吧。", ["ja"], None),
    ("english", "Please reply in English with just: Understood.", ["en"], "Understood."),
]


def get_json(url):
    with urllib.request.urlopen(url, timeout=10) as response:
        return json.load(response)


def inspect(raw, expected_languages, exact_text):
    # The C# regression suite verifies streaming/protocol correctness. Here inspect
    # generated fixture content only, including a few known mixed-language failures.
    raw = re.sub(r"<(?:thought|think)>.*?</(?:thought|think)>", "", raw, flags=re.S)
    raw = raw.split("<silent/>")[0]
    language = None
    parts = []
    for token in re.split(r"(<[^>]*>)", raw):
        if not token:
            continue
        marker = re.fullmatch(r'<lang\s+code=[\"\'](ja|zh|en)[\"\']\s*/>', token)
        if marker:
            language = marker[1]
        elif token.startswith("<"):
            if token.lower().startswith("<lang"):
                language = None
        elif token.strip():
            if parts and parts[-1][0] == language:
                parts[-1][1] += token.strip()
            else:
                parts.append([language, token.strip()])
    languages = [code for code, _ in parts]
    text = "".join(text for _, text in parts)
    issues = []
    if languages != expected_languages:
        issues.append(f"language sequence {languages!r}, expected {expected_languages!r}")
    if exact_text is not None and text != exact_text:
        issues.append(f"text differs from requested fixture: {text!r}")
    for code, text in parts:
        if code == "ja" and re.search("耳机|声场|安托涅瓦|負責人|负责人|创始人|听いて", text):
            issues.append("known Chinese wording in Japanese fixture")
    return parts, issues


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="http://127.0.0.1:8080")
    parser.add_argument("--runs", type=int, default=2, choices=range(1, 6))
    parser.add_argument("--temperature", type=float, default=None,
                        help="Default: inherit the server's sampling settings, like the formal role request")
    parser.add_argument("--output", default="Logs/speech-language-check/generation.json")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    prompt_dir = root / "Assets/AIChatTookit/Prompts"
    system = "\n\n".join((prompt_dir / name).read_text(encoding="utf-8")
                           for name in ("persona.txt", "language.txt", "behavior.txt"))
    system = re.sub(r"<!-- SKILL-BEGIN:.*?<!-- SKILL-END -->", "", system, flags=re.S).strip()
    # Use the exact C# request-local contract, not a separately maintained test prompt.
    speech_source = (root / "Assets/AIChatTookit/Scripts/Chat/SpeechText.cs").read_text(encoding="utf-8")
    contract_source = re.search(r'public const string OutputContract\s*=\s*(.*?);', speech_source, re.S)[1]
    contract = "".join(json.loads(literal) for literal in re.findall(r'"(?:[^"\\]|\\.)*"', contract_source))
    records = []
    for repeat in range(args.runs):
        for name, prompt, languages, exact in CASES:
            slots = get_json(args.base + "/slots")
            if not isinstance(slots, list) or not slots or any(s.get("is_processing", True) for s in slots):
                raise RuntimeError("LLM is busy or idle state is unavailable; do not interrupt a conversation")
            payload = {
                "model": "antoneva", "messages": [{"role": "system", "content": system},
                    {"role": "system", "content": contract},
                    {"role": "user", "content": prompt}],
                "stream": False, "seed": 73 + repeat,
                "max_tokens": 256, "id_slot": slots[-1]["id"],
                "chat_template_kwargs": {"enable_thinking": False},
            }
            if args.temperature is not None:
                payload["temperature"] = args.temperature
            request = urllib.request.Request(args.base + "/v1/chat/completions",
                json.dumps(payload, ensure_ascii=False).encode("utf-8"), {"Content-Type": "application/json"})
            started = time.perf_counter()
            with urllib.request.urlopen(request, timeout=90) as response:
                result = json.load(response)
            raw = result["choices"][0]["message"]["content"]
            parts, issues = inspect(raw, languages, exact)
            record = {"case": name, "run": repeat + 1, "raw": raw, "segments": parts,
                      "issues": issues, "seconds": round(time.perf_counter() - started, 3)}
            records.append(record)
            output = root / args.output
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")
            print(json.dumps(record, ensure_ascii=False), flush=True)
    passed = sum(not row["issues"] for row in records)
    language_passed = sum(not any(not issue.startswith("text differs") for issue in row["issues"])
                          for row in records)
    print(f"Language/known mixed-word fixtures passed: {language_passed}/{len(records)}", flush=True)
    print(f"Generation fixtures passed: {passed}/{len(records)}; review prose quality separately.", flush=True)
    raise SystemExit(0 if passed == len(records) else 1)


if __name__ == "__main__":
    main()
