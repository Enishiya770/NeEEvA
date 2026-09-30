"""Explicit ASR endpoint parity probe: readiness, final upload and real-time WS.

Default is read-only health. --mode all/upload/stream sends the explicitly chosen
16 kHz mono PCM16 WAV; final requests use learn_speaker=false. No routing changes.
"""
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import time
import urllib.parse
import urllib.request
import uuid
import wave


def json_request(url, body=None, headers=None, timeout=45):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    request = urllib.request.Request(url, data=body, headers=headers or {})
    with opener.open(request, timeout=timeout) as response:
        return json.load(response)


def validate_health(data):
    required = {"ok": True, "asr_ok": True, "vad_ok": True, "speaker_ok": True,
                "cuda_available": True, "require_cuda": True, "streaming_preview": True,
                "stream_activity_schema": 2, "analysis_dispatch_schema": 2,
                "final_speech_window_policy": "speech-context-v1", "external_audio_upload": False}
    errors = [key for key, value in required.items() if data.get(key) != value]
    if not str(data.get("asr_device", "")).startswith("cuda"):
        errors.append("asr_device")
    return errors


def upload(base, data, language):
    boundary = "neeeva-asr-" + uuid.uuid4().hex
    payload = bytearray()
    for name, value in {"language": language, "learn_speaker": "false", "expect_singing": "false",
                        "request_id": "migration-" + uuid.uuid4().hex, "channel_id": "migration-validation"}.items():
        payload.extend((f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n").encode())
    payload.extend((f"--{boundary}\r\nContent-Disposition: form-data; name=\"audio_file\"; filename=\"validation.wav\"\r\nContent-Type: audio/wav\r\n\r\n").encode())
    payload.extend(data)
    payload.extend((f"\r\n--{boundary}--\r\n").encode())
    start = time.perf_counter()
    result = json_request(base + "/asr", bytes(payload), {"Content-Type": "multipart/form-data; boundary=" + boundary})
    return {"wallSeconds": round(time.perf_counter() - start, 4),
            "text": result.get("text", ""), "language": result.get("language"),
            "elapsed": result.get("elapsed"), "timings": result.get("timings"),
            "audio_event": result.get("audio_event"), "error": result.get("error"),
            "rejected": result.get("rejected"), "speaker_kind": result.get("speaker_kind")}


async def streaming(base, pcm, language, cancel=False):
    import websockets
    url = base.replace("http://", "ws://", 1).replace("https://", "wss://", 1) + "/stream/asr"
    start = time.perf_counter()
    partials = []
    async with websockets.connect(url, open_timeout=10, close_timeout=5, max_size=1024 * 1024) as socket:
        await socket.send(json.dumps({"event": "start", "language": language, "partial_interval_ms": 850,
                                      "min_audio_ms": 800, "partial_window_ms": 6000}))
        started = json.loads(await asyncio.wait_for(socket.recv(), 10))
        if started.get("event") != "started" or started.get("sample_rate") != 16000:
            raise ValueError("Invalid WS started response")

        async def receive():
            while True:
                row = json.loads(await asyncio.wait_for(socket.recv(), 15))
                if row.get("event") == "error":
                    raise RuntimeError(str(row.get("error")))
                if row.get("event") == "stopped":
                    return
                if row.get("event") == "partial":
                    partials.append({"receivedSeconds": round(time.perf_counter() - start, 4),
                                     **{key: row.get(key) for key in ["text", "audio_ms", "stream_infer_ms", "stream_lag_ms", "stream_rtf", "stream_coalesced", "stream_assembly"]}})

        receiver = asyncio.create_task(receive())
        try:
            stream_start = time.perf_counter()
            for offset in range(0, len(pcm), 3200):
                await socket.send(pcm[offset:offset + 3200])
                await asyncio.sleep(max(0, stream_start + min(offset + 3200, len(pcm)) / 32000 - time.perf_counter()))
            await socket.send(json.dumps({"event": "cancel" if cancel else "stop"}))
            await asyncio.wait_for(receiver, 15)
        finally:
            if not receiver.done():
                receiver.cancel()
    return {"wallSeconds": round(time.perf_counter() - start, 4), "cancel": cancel,
            "partialCount": len(partials), "firstPartialSeconds": partials[0]["receivedSeconds"] if partials else None,
            "partials": partials, "stoppedAcknowledged": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://192.168.114.163:9881")
    parser.add_argument("--mode", choices=["health", "upload", "stream", "all"], default="health")
    parser.add_argument("--wav", type=Path)
    parser.add_argument("--language", choices=["auto", "zh", "ja", "en", "ko", "yue"], default="auto")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    parsed = urllib.parse.urlparse(args.url)
    if parsed.scheme not in ("http", "https") or parsed.username or parsed.password or parsed.query or parsed.path not in ("", "/"):
        raise ValueError("Expected an HTTP origin without credentials/path/query")
    base = args.url.rstrip("/")
    health = json_request(base + "/health", timeout=10)
    report = {"schema": 1, "url": base, "mode": args.mode, "health": health, "healthErrors": validate_health(health)}
    if report["healthErrors"]:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
        raise SystemExit("ASR health contract mismatch: " + ",".join(report["healthErrors"]))
    if args.mode != "health":
        if not args.wav:
            parser.error("--wav is required for explicit audio probes")
        data = args.wav.read_bytes()
        with wave.open(str(args.wav), "rb") as audio:
            if (audio.getframerate(), audio.getnchannels(), audio.getsampwidth(), audio.getcomptype()) != (16000, 1, 2, "NONE"):
                raise ValueError("Use a 16 kHz mono PCM16 WAV without implicit resampling")
            pcm = audio.readframes(audio.getnframes())
        if not 1 <= len(pcm) / 32000 <= 60:
            raise ValueError("Explicit probe must contain 1 to 60 seconds of audio")
        report["audio"] = {"sha256": hashlib.sha256(data).hexdigest(), "seconds": len(pcm) / 32000}
        if args.mode in ("upload", "all"):
            report["final"] = upload(base, data, args.language)
        if args.mode in ("stream", "all"):
            report["stream"] = asyncio.run(streaming(base, pcm, args.language))
        if args.mode == "all":
            report["cancel"] = asyncio.run(streaming(base, pcm[:32000], args.language, cancel=True))
            report["successor"] = upload(base, data, args.language)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"output": str(args.output), "healthErrors": report["healthErrors"], "audioSent": args.mode != "health"}))
    # Accuracy and timing are reviewable evidence, never inferred from HTTP 200.
    if any(report.get(key, {}).get("error") for key in ("final", "successor")):
        raise SystemExit(1)


if __name__ == "__main__":
    main()
