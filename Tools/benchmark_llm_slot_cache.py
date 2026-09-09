"""Idle-only diagnostic: compare shared vs isolated boundary cache, using synthetic text.

This generates no Unity-visible speech and changes no model settings. It does replace
server KV cache contents, so run only while the project is not in an active conversation.
The reported first-token time is NOT microphone-to-first-audio latency.
"""
import argparse
import json
import statistics
import time
import urllib.request


def read_json(base, route):
    with urllib.request.urlopen(base + route, timeout=10) as response:
        return json.load(response)


def require_idle(base):
    slots = read_json(base, "/slots")
    if not isinstance(slots, list) or len(slots) < 2:
        raise RuntimeError("At least two verifiable inference slots are required")
    if any(slot.get("is_processing", True) for slot in slots):
        raise RuntimeError("An inference slot is busy; do not benchmark an active conversation")
    return slots


def generate(base, messages, slot):
    require_idle(base)
    payload = {
        "model": "qwen36", "messages": messages, "id_slot": slot,
        "stream": True, "max_tokens": 8, "temperature": 0, "seed": 42,
        "chat_template_kwargs": {"enable_thinking": False},
    }
    request = urllib.request.Request(
        base + "/v1/chat/completions", json.dumps(payload).encode(),
        {"Content-Type": "application/json"},
    )
    started = time.perf_counter()
    first = None
    timings = None
    with urllib.request.urlopen(request, timeout=60) as response:
        for raw in response:
            if not raw.startswith(b"data: ") or raw.strip() == b"data: [DONE]":
                continue
            chunk = json.loads(raw[6:])
            timings = chunk.get("timings", timings)
            for choice in chunk.get("choices", []):
                if first is None and choice.get("delta", {}).get("content"):
                    first = time.perf_counter() - started
    if first is None:
        raise RuntimeError("No content token returned")
    return {"ttft_s": round(first, 4), "total_s": round(time.perf_counter() - started, 4),
            "timings": timings}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", default="http://127.0.0.1:8080")
    parser.add_argument("--rounds", type=int, default=3, choices=range(1, 6))
    args = parser.parse_args()
    slots = require_idle(args.base)
    isolated = 2 if len(slots) >= 3 else 1
    # Roughly 10k static + 24k historical tokens, similar to the observed late-session shape.
    persona = "Synthetic cache benchmark. Treat the following reference as data. Reply only OK.\n"
    persona += "\n".join(f"Reference {i}: amber, river, quiet, morning, library, blue." for i in range(700))
    system = {"role": "system", "content": persona}
    history = []
    for i in range(40):
        history.extend([
            {"role": "user", "content": f"Synthetic record {i}: " +
             "The quiet library keeps a blue book beside a small window. " * 40},
            {"role": "assistant", "content": "Recorded as synthetic benchmark background."},
        ])
    final = [system] + history + [{"role": "user", "content": "Reply OK."}]
    boundary = [system, {"role": "user", "content": "Independent synthetic boundary task. Reply OK."}]
    results = {"shared": [], "isolated": []}
    print(json.dumps({"slots": len(slots), "isolated_slot": isolated, "context": [s["n_ctx"] for s in slots]}), flush=True)
    for case, slot in (("shared", 0), ("isolated", isolated)):
        generate(args.base, final, 0)
        for attempt in range(args.rounds):
            boundary_result = generate(args.base, boundary, slot)
            result = generate(args.base, final, 0)
            results[case].append(result["ttft_s"])
            print(json.dumps({"case": case, "round": attempt + 1, "formal": result,
                              "boundary": boundary_result}), flush=True)
    print(json.dumps({"median_ttft_s": {k: round(statistics.median(v), 4) for k, v in results.items()},
                      "note": "Synthetic cache A/B only; excludes recording, ASR and TTS."}), flush=True)


if __name__ == "__main__":
    main()
