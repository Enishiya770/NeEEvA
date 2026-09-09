"""Synthesize a few replay/control samples locally for listening; never auto-play audio."""
import hashlib
import json
from pathlib import Path
import re
import struct
import time
import urllib.request
import wave

ROOT = Path(__file__).resolve().parents[1]


def main():
    folder = ROOT / "Logs/speech-language-check"
    routing_path = folder / "replay-routing.json"
    routed = json.loads(routing_path.read_text(encoding="utf-8"))
    scene_path = ROOT / "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity"
    scene = scene_path.read_text(encoding="utf-8")
    config = scene.split("  m_PostURL: http://127.0.0.1:9880/tts", 1)[1].split("--- !u!", 1)[0]
    reference = re.search(r"m_ReferWavPath: (.+)", config)[1]
    local_reference = ROOT / "Assets" / reference
    if local_reference.is_file():
        reference = str(local_reference)
    prompt = json.loads(re.search(r"m_ReferenceText: (.+)", config)[1])
    if re.search(r"m_ReferenceTextLan: (\d+)", config)[1] != "2":
        raise ValueError("Scene no longer uses the expected Japanese reference")

    def pick(record_id, text, mode=0):
        record = next(r for r in routed["records"] if r["id"] == record_id)
        return next(p for p in record["tts_requests"] if p["text"] == text and p["streaming_mode"] == mode)

    mixed = "安托涅瓦として、いつでも話を聞くわ。"
    jobs = [
        ("ja_understood", pick("controls/1/ja_shared_han", "了解。"), "actual routed control"),
        ("zh_understood", pick("controls/1/zh_shared_han", "了解。"), "actual routed control"),
        ("zh_heard", pick("controls/1/sentence_switch", "听到哦。"), "actual routed control"),
        ("ja_mixed_name", pick("logged_history/1/turn1", mixed), "actual routed mixed-language failure"),
        ("ja_name_comparison", {"text": mixed.replace("安托涅瓦", "アントネーワ"), "text_lang": "ja", "streaming_mode": 0},
         "manually written pronunciation comparison; not model output or a runtime replacement"),
        ("ja_understood_stream", pick("controls/1/ja_shared_han", "了解。", 3), "actual routed streaming control"),
    ]
    audio_folder = folder / "audio"
    audio_folder.mkdir(exist_ok=True)
    report = {"routing_sha256": hashlib.sha256(routing_path.read_bytes()).hexdigest(),
              "scene_sha256": hashlib.sha256(scene_path.read_bytes()).hexdigest(),
              "listening_status": "not_listened", "samples": []}
    for name, selected, origin in jobs:
        request_body = {k: selected[k] for k in ("text", "text_lang", "streaming_mode")}
        request_body.update(ref_audio_path=reference, prompt_text=prompt, prompt_lang="ja",
                            media_type="wav", min_chunk_length=12)
        request = urllib.request.Request("http://127.0.0.1:9880/tts",
                                        json.dumps(request_body, ensure_ascii=False).encode("utf-8"),
                                        {"Content-Type": "application/json"})
        started = time.perf_counter()
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = response.read()
        if raw[:4] != b"RIFF" or raw[8:12] != b"WAVE":
            raise ValueError("TTS did not return WAV: " + name)
        # Streaming WAV can announce an empty data chunk, then append PCM until EOF.
        # Preserve raw response separately and write a playable header from actual bytes.
        offset, fmt, pcm = 12, None, None
        while offset + 8 <= len(raw):
            tag, length = raw[offset:offset + 4], struct.unpack_from("<I", raw, offset + 4)[0]
            offset += 8
            if tag == b"fmt ":
                fmt = struct.unpack_from("<HHIIHH", raw, offset)
            if tag == b"data":
                pcm = raw[offset:] if selected["streaming_mode"] else raw[offset:offset + length]
                break
            offset += length + (length % 2)
        if fmt is None or pcm is None or fmt[0] != 1 or not pcm or len(pcm) % fmt[4]:
            raise ValueError("Invalid PCM WAV: " + name)
        (audio_folder / (name + ".response.bin")).write_bytes(raw)
        path = audio_folder / (name + ".wav")
        with wave.open(str(path), "wb") as wav:
            wav.setnchannels(fmt[1])
            wav.setsampwidth(fmt[5] // 8)
            wav.setframerate(fmt[2])
            wav.writeframes(pcm)
        values = struct.unpack("<" + "h" * (len(pcm) // 2), pcm) if fmt[5] == 16 else None
        if values is not None and not any(values):
            raise ValueError("Silent audio returned: " + name)
        result = {"name": name, "origin": origin, "request": request_body,
                  "seconds": round(len(pcm) / fmt[3], 3), "response_bytes": len(raw),
                  "request_seconds": round(time.perf_counter() - started, 3),
                  "path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
        report["samples"].append(result)
        (audio_folder / "manifest.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({k: result[k] for k in ("name", "seconds", "response_bytes")}), flush=True)


if __name__ == "__main__":
    main()
