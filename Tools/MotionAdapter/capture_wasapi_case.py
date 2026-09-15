"""Bound one explicitly triggered case to Windows output-loopback evidence.

This never opens a microphone. It opens only a verified WASAPI loopback endpoint,
with start=False, writes ready.json, then waits for --trigger JSON containing an
ISO-8601 `utc` and optional caseId/text metadata. After the trigger it records
until --done exists or --max-seconds (hard maximum 20). No volume is changed.

RMS onset is system mix activity, not speech recognition or human audibility.
Use the WAV and trigger/done correlation metadata to check its source/content.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import threading
import time
import wave
from datetime import datetime, timezone
from pathlib import Path


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="microseconds")


def write_json(path: Path, value: dict) -> None:
    # Parent can distinguish a complete marker from a partially written one.
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temp.replace(path)


def read_event(path: Path, *, require_utc: bool) -> dict:
    if path.stat().st_size > 65536:
        raise ValueError("Case marker exceeds 64 KiB")
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError("Case marker must be a JSON object")
    if require_utc:
        stamp = datetime.fromisoformat(str(value["utc"]).replace("Z", "+00:00"))
        if stamp.tzinfo is None:
            raise ValueError("Trigger utc requires a timezone")
        value["parsedUtc"] = stamp.astimezone(timezone.utc).isoformat()
    return value


def analyze_pcm(raw: bytes, rate: int, channels: int, threshold_dbfs: float, minimum_ms: float) -> dict:
    import numpy as np

    pcm = np.frombuffer(raw, dtype="<i2").astype(np.float64) / 32768.0
    pcm = pcm[: len(pcm) // channels * channels].reshape((-1, channels))
    window_frames = max(1, round(rate * 0.02))
    windows = []
    for start in range(0, len(pcm), window_frames):
        block = pcm[start : start + window_frames]
        rms = float(np.sqrt(np.mean(block * block)))
        peak = float(np.max(np.abs(block)))
        windows.append({"startSeconds": start / rate, "durationSeconds": len(block) / rate,
                        "rmsDbfs": max(-120.0, 20 * math.log10(max(rms, 1e-6))),
                        "peakDbfs": max(-120.0, 20 * math.log10(max(peak, 1e-6)))})
    first = sustained = None
    run_start = None
    run_seconds = 0.0
    for row in windows:
        if row["rmsDbfs"] >= threshold_dbfs:
            if first is None:
                first = row["startSeconds"]
            if run_start is None:
                run_start = row["startSeconds"]
            run_seconds += row["durationSeconds"]
            if sustained is None and run_seconds + 1e-9 >= minimum_ms / 1000:
                sustained = run_start
        else:
            run_start = None
            run_seconds = 0.0
    return {"thresholdDbfs": threshold_dbfs, "minimumSustainedMs": minimum_ms,
            "windowMs": 20, "firstActiveWindowSeconds": first,
            "firstSustainedActivitySeconds": sustained,
            "peakWindowRmsDbfs": max((x["rmsDbfs"] for x in windows), default=None),
            "rmsWindows": windows,
            "scope": "Activity in the Windows output mix. It can include unrelated sounds and is not a speech/content or human-audibility verdict."}


def self_test() -> None:
    import numpy as np

    rate = 48000
    data = np.zeros((rate, 2), dtype="<i2")
    # One isolated 20 ms impulse must not count as a sustained onset.
    data[4800:5760] = 8000
    data[24000:28800] = 8000
    result = analyze_pcm(data.tobytes(), rate, 2, -45, 40)
    assert result["firstActiveWindowSeconds"] == 0.1
    assert result["firstSustainedActivitySeconds"] == 0.5
    silent = analyze_pcm(bytes(rate * 4), rate, 2, -45, 40)
    assert silent["firstSustainedActivitySeconds"] is None
    empty = analyze_pcm(b"", rate, 2, -45, 40)
    assert empty["firstSustainedActivitySeconds"] is None
    print(json.dumps({"status": "passed", "checks": 4, "audioDeviceOpened": False}))


def capture(args: argparse.Namespace) -> int:
    import pyaudiowpatch as pa

    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    trigger_path, done_path = Path(args.trigger).resolve(), Path(args.done).resolve()
    if trigger_path == done_path:
        raise ValueError("Trigger and done must be different files")
    if done_path.exists():
        raise ValueError("Done marker already exists; use unique files per case")
    maximum = min(20.0, max(0.1, args.max_seconds))
    report = {"status": "waiting-trigger", "scope": "Windows WASAPI output loopback only; no microphone, no volume change, no human-audibility claim.",
              "maximumCaptureSeconds": maximum, "triggerPath": str(trigger_path), "donePath": str(done_path),
              "helperStartedUtc": utc_now(), "clock": "UTC wall clock for event correlation; perf_counter for bounds; PortAudio stream/ADC time separately recorded."}
    chunks: list[bytes] = []
    blocks: list[dict] = []
    lock = threading.Lock()
    captured_frames = 0
    limit_frames = 0
    capture_perf = 0.0
    captured_limit = threading.Event()
    audio = pa.PyAudio()
    stream = None
    try:
        device = (audio.get_default_wasapi_loopback() if args.device_index is None
                  else audio.get_device_info_by_index(args.device_index))
        host = audio.get_host_api_info_by_index(int(device["hostApi"]))
        if not device.get("isLoopbackDevice") or host["type"] != pa.paWASAPI:
            raise ValueError("Refusing non-WASAPI-loopback endpoint (microphones are never allowed)")
        channels = int(device["maxInputChannels"])
        if channels < 1:
            raise ValueError("Loopback endpoint has no capture channels")
        rate = round(device["defaultSampleRate"])
        limit_frames = int(rate * maximum)
        report.update({"device": device, "hostApi": host, "sampleRate": rate, "channels": channels,
                       "sampleFormat": "PCM signed 16-bit little endian", "framesPerBuffer": 480})

        def callback(in_data, frame_count, time_info, status_flags):
            nonlocal captured_frames
            now_perf, now_utc = time.perf_counter(), utc_now()
            with lock:
                keep = min(frame_count, limit_frames - captured_frames)
                if keep > 0:
                    chunks.append(in_data[: keep * channels * 2])
                    blocks.append({"sampleStart": captured_frames, "frames": keep,
                                   "callbackUtc": now_utc, "callbackSinceStartSeconds": now_perf - capture_perf,
                                   "inputBufferAdcTime": time_info.get("input_buffer_adc_time"),
                                   "portAudioCurrentTime": time_info.get("current_time"), "statusFlags": status_flags})
                    captured_frames += keep
                if captured_frames >= limit_frames:
                    captured_limit.set()
                    return None, pa.paComplete
            return None, pa.paContinue

        stream = audio.open(format=pa.paInt16, channels=channels, rate=rate, input=True,
                            input_device_index=int(device["index"]), frames_per_buffer=480,
                            stream_callback=callback, start=False)
        report["inputLatencySeconds"] = stream.get_input_latency()
        write_json(output / "ready.json", report)
        deadline = time.perf_counter() + min(600.0, max(1.0, args.wait_timeout_seconds))
        trigger = None
        while time.perf_counter() < deadline:
            if trigger_path.exists():
                try:
                    trigger = read_event(trigger_path, require_utc=True)
                    break
                except (ValueError, KeyError, json.JSONDecodeError):
                    # Root can still be completing a small marker write.
                    pass
            time.sleep(0.01)
        if trigger is None:
            report.update({"status": "trigger-timeout", "recordingStarted": False})
            return 2
        # Nothing was sampled before this explicit marker.
        capture_perf = time.perf_counter()
        capture_utc = datetime.now(timezone.utc)
        trigger_utc = datetime.fromisoformat(trigger["parsedUtc"])
        report.update({"status": "recording", "trigger": trigger, "recordingStarted": True,
                       "captureStartUtc": capture_utc.isoformat(timespec="microseconds"),
                       "captureStartPerfCounter": capture_perf, "portAudioTimeBeforeStart": stream.get_time(),
                       "startAfterTriggerSeconds": (capture_utc - trigger_utc).total_seconds()})
        stream.start_stream()
        write_json(output / "recording.json", report)
        stop_reason = "maximum-wall-clock-duration"
        while time.perf_counter() - capture_perf < maximum:
            if done_path.exists():
                stop_reason = "done-marker"
                try:
                    report["done"] = read_event(done_path, require_utc=False)
                except (ValueError, json.JSONDecodeError) as error:
                    report["doneReadError"] = str(error)
                break
            if captured_limit.is_set():
                stop_reason = "maximum-sample-duration"
                break
            if not stream.is_active():
                stop_reason = "capture-stream-ended"
                break
            time.sleep(0.01)
        stream.stop_stream()
        report.update({"stopReason": stop_reason, "captureEndUtc": utc_now(),
                       "wallCaptureSeconds": time.perf_counter() - capture_perf})
        with lock:
            raw = b"".join(chunks)
        wave_path = output / "system-output.wav"
        with wave.open(str(wave_path), "wb") as wav:
            wav.setnchannels(channels)
            wav.setsampwidth(2)
            wav.setframerate(rate)
            wav.writeframes(raw)
        report.update({"status": "captured", "wavPath": str(wave_path),
                       "wavSha256": hashlib.sha256(wave_path.read_bytes()).hexdigest(),
                       "capturedFrames": captured_frames, "sampleDurationSeconds": captured_frames / rate,
                       "callbackCount": len(blocks), "firstCallback": blocks[0] if blocks else None,
                       "lastCallback": blocks[-1] if blocks else None,
                       "callbackStatusNonzeroCount": sum(bool(x["statusFlags"]) for x in blocks)})
        diagnostics = analyze_pcm(raw, rate, channels, args.threshold_dbfs, args.minimum_active_ms)
        report["activity"] = diagnostics
        onset = diagnostics["firstSustainedActivitySeconds"]
        if onset is not None:
            frame = round(onset * rate)
            block = next((x for x in blocks if x["sampleStart"] <= frame < x["sampleStart"] + x["frames"]), None)
            report["onsetSampleTimelineAfterTriggerSeconds"] = report["startAfterTriggerSeconds"] + onset
            report["onsetCallback"] = block
            report["onsetCallbackAfterTriggerSeconds"] = ((datetime.fromisoformat(block["callbackUtc"]) - trigger_utc).total_seconds() if block else None)
        report["timingLimitations"] = [
            "The WAV sample timeline assumes continuous capture; callback/ADC records expose gaps and buffering.",
            "UTC trigger and callback are host clock observations, not sample-accurate loudspeaker timestamps.",
            "Windows mix activity can come from other applications; correlate known case/TTS text and listen to the saved WAV. RMS alone cannot attribute it.",
            "No microphone was opened. This does not prove physical speakers were audible to a person."]
        write_json(output / "callback-timing.json", {"blocks": blocks})
        return 0
    except Exception as error:
        report.update({"status": "failed", "error": repr(error)})
        return 1
    finally:
        if stream is not None:
            if stream.is_active():
                stream.stop_stream()
            stream.close()
        audio.terminate()
        write_json(output / "report.json", report)
        print(json.dumps({"status": report["status"], "report": str(output / "report.json")}, ensure_ascii=False))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--trigger")
    parser.add_argument("--done")
    parser.add_argument("--output")
    parser.add_argument("--device-index", type=int)
    parser.add_argument("--wait-timeout-seconds", type=float, default=180)
    parser.add_argument("--max-seconds", type=float, default=20)
    parser.add_argument("--threshold-dbfs", type=float, default=-45)
    parser.add_argument("--minimum-active-ms", type=float, default=40)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return 0
    if not all((args.trigger, args.done, args.output)):
        parser.error("--trigger, --done and --output are required")
    return capture(args)


if __name__ == "__main__":
    raise SystemExit(main())
