"""Capture six public exported chat requests as SSE, without executing their output.

Each request keeps the exported messages and slot. Only bounded generation options
are added. A fresh /slots check precedes every generation; a busy server aborts the
run. The Unity replay entry validates captured bytes with the production parser.
No private persona/history, microphone, TTS or character action is accessed.
"""
import argparse
import base64
import codecs
import copy
import hashlib
import json
from pathlib import Path
import time
import urllib.request


CASES = [
    ("greeting", "greeting_and_consecutive_chat", 0),
    ("consecutive_weather", "greeting_and_consecutive_chat", 2),
    ("heard_song_without_request", "acoustic_hearing_without_topic_or_action_request", 0),
    ("mixed_lyrics_and_tail_request", "mixed_lyrics_tail_request_then_weather", 0),
    ("weather_after_song", "mixed_lyrics_tail_request_then_weather", 1),
    ("weather_after_restart", "realtime_stop_start_then_chat", 1),
]


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def get_idle_slots(url):
    with urllib.request.urlopen(url.rstrip("/") + "/slots", timeout=5) as response:
        slots = json.load(response)
    if not isinstance(slots, list) or not slots:
        raise RuntimeError("The server did not provide a usable slot inventory; no generation submitted")
    if any(not isinstance(slot, dict) or not isinstance(slot.get("is_processing"), bool) for slot in slots):
        raise RuntimeError("A slot has unknown activity; no generation submitted")
    if any(slot["is_processing"] for slot in slots):
        raise RuntimeError("Model busy; no generation submitted")
    return [{"id": s.get("id"), "is_processing": s["is_processing"]} for s in slots]


def capture_sse(url, request, case_dir):
    wire_request = json.dumps(request, ensure_ascii=False).encode("utf-8")
    (case_dir / "request.json").write_bytes(wire_request + b"\n")
    slots = get_idle_slots(url)
    started = time.monotonic()
    wire = urllib.request.Request(url.rstrip("/") + "/v1/chat/completions", wire_request,
                                  {"Content-Type": "application/json", "Accept": "text/event-stream"})
    chunks, deltas, metadata, raw = [], [], [], bytearray()
    decoder = codecs.getincrementaldecoder("utf-8")()
    pending = ""
    finish_reason = None
    done_seen = False
    first_content = None
    first_byte = None

    def line_received(line, elapsed):
        nonlocal finish_reason, done_seen, first_content
        if not line.startswith("data:"):
            return
        payload = line[5:].strip()
        if payload == "[DONE]":
            done_seen = True
            return
        event = json.loads(payload)
        if any(key in event for key in ("usage", "timings", "timing", "cache")):
            metadata.append({"elapsedSeconds": elapsed, "event": {
                key: event[key] for key in ("usage", "timings", "timing", "cache") if key in event}})
        choices = event.get("choices") or []
        if choices:
            choice = choices[0]
            finish_reason = choice.get("finish_reason") or finish_reason
            content = (choice.get("delta") or {}).get("content") or ""
            if content:
                if first_content is None:
                    first_content = elapsed
                deltas.append({"elapsedSeconds": elapsed, "text": content})

    try:
        with urllib.request.urlopen(wire, timeout=55) as response:
            content_type = response.headers.get("Content-Type", "")
            if "text/event-stream" not in content_type.lower():
                raise RuntimeError("Expected SSE; received " + content_type)
            while True:
                packet = response.read1(65536)
                if not packet:
                    break
                elapsed = round(time.monotonic() - started, 6)
                if elapsed > 55:
                    raise TimeoutError("Public streaming request exceeded 55 seconds")
                if first_byte is None:
                    first_byte = elapsed
                raw.extend(packet)
                chunks.append({"elapsedSeconds": elapsed, "base64": base64.b64encode(packet).decode("ascii")})
                pending += decoder.decode(packet)
                while "\n" in pending:
                    line, pending = pending.split("\n", 1)
                    line_received(line.rstrip("\r"), elapsed)
            pending += decoder.decode(b"", final=True)
            if pending.strip():
                line_received(pending.rstrip("\r"), round(time.monotonic() - started, 6))
    finally:
        (case_dir / "response.sse").write_bytes(raw)
        write_json(case_dir / "wire-chunks.json", chunks)
    return {
        "idleSlotsBeforeRequest": slots, "wireChunks": chunks, "deltas": deltas,
        "content": "".join(d["text"] for d in deltas), "finishReason": finish_reason,
        "doneSeen": done_seen, "metadataEvents": metadata,
        "clientTimings": {"firstResponseByteSeconds": first_byte, "firstRawContentSeconds": first_content,
                          "completeSeconds": round(time.monotonic() - started, 6)},
        "requestSha256": hashlib.sha256(wire_request).hexdigest(),
        "responseSseSha256": hashlib.sha256(raw).hexdigest(),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--requests", required=True, help="chat-latency-public-requests.json from the isolated Unity suite")
    parser.add_argument("--out-dir", required=True, help="new output directory; prior evidence is never overwritten")
    parser.add_argument("--url", default="http://127.0.0.1:8080")
    parser.add_argument("--max-tokens", type=int, default=768)
    parser.add_argument("--idle-check-only", action="store_true")
    args = parser.parse_args()
    if args.idle_check_only:
        print(json.dumps({"idleSlots": get_idle_slots(args.url), "generationSubmitted": False}))
        return 0
    if not 64 <= args.max_tokens <= 1024:
        raise ValueError("max-tokens must be between 64 and 1024")
    source_path = Path(args.requests).resolve()
    source_bytes = source_path.read_bytes()
    source = json.loads(source_bytes.decode("utf-8-sig"))
    if source.get("publicSyntheticInputsOnly") is not True:
        raise ValueError("Refusing a file that is not the explicit public synthetic request export")
    selected = []
    for case_id, scenario_name, index in CASES:
        scenario = next(s for s in source["scenarios"] if s["name"] == scenario_name)
        original = scenario["requests"][index]
        if not isinstance(original.get("messages"), list) or original.get("stream") is not True:
            raise ValueError("Export did not contain the expected streaming wire request")
        selected.append((case_id, scenario_name, index, original))
    out = Path(args.out_dir).resolve()
    out.mkdir(parents=True, exist_ok=False)
    report = {
        "publicSyntheticInputsOnly": True, "source": str(source_path),
        "sourceSha256": hashlib.sha256(source_bytes).hexdigest(),
        "scope": "Six unchanged exported public message lists; actual model SSE and client receive times. No actions executed, private history, microphone, TTS, or user-end-to-audio claim. Scripted earlier assistant turns remain the fixture history.",
        "generationOverrides": {"temperature": 0.2, "max_tokens": args.max_tokens},
        "transportComplete": False, "productionReplayRequired": True, "cases": [],
    }
    try:
        for case_id, scenario_name, index, original in selected:
            case_dir = out / case_id
            case_dir.mkdir()
            request = copy.deepcopy(original)
            request.update(temperature=0.2, max_tokens=args.max_tokens)
            result = capture_sse(args.url, request, case_dir)
            result.update(id=case_id, sourceScenario=scenario_name, sourceRequestIndex=index,
                          expectedSpeechMode="independent", expectedSingingAction=False,
                          sourceMessagesSha256=hashlib.sha256(json.dumps(original["messages"], ensure_ascii=False).encode("utf-8")).hexdigest())
            report["cases"].append(result)
            write_json(out / "model-stream-capture.json", report)
            print(json.dumps({"id": case_id, "finishReason": result["finishReason"],
                              "rawContentSeconds": result["clientTimings"]["firstRawContentSeconds"],
                              "completeSeconds": result["clientTimings"]["completeSeconds"]}), flush=True)
            if not result["doneSeen"] or result["finishReason"] != "stop":
                raise RuntimeError("The public model response did not complete normally: " + case_id)
        report["transportComplete"] = True
    except Exception as error:
        report["error"] = f"{type(error).__name__}: {error}"
        raise
    finally:
        write_json(out / "model-stream-capture.json", report)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
