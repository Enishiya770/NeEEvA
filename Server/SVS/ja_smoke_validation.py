"""Generate a tiny independent Japanese-like SVS validation sample.

This is deliberately a no-training gate.  SoulX does not officially support
Japanese, so kana mora are represented by short English-phone approximations.
The request still contains only a score and a character prompt; the placeholder
audio is silent and cannot act as an audio-to-audio carrier.
"""

from __future__ import annotations

import argparse
import json
import math
import wave
from pathlib import Path

import requests


ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = ROOT.parent.parent
OUTPUT_ROOT = ROOT / "runtime" / "ja_smoke_validation"
PROMPT = ROOT / "prompts" / "41041_svs_zh_short.wav"


def build_score() -> dict:
    # き・み・の・こ・と・が・す・き
    lyric_units = ["き", "み", "の", "こ", "と", "が", "す", "き"]
    pitches = [64, 66, 68, 66, 64, 61, 64, 61]
    durations = [0.52, 0.52, 0.68, 0.52, 0.52, 0.68, 0.56, 0.96]
    notes = []
    cursor = 0.28
    notes.append(
        {
            "midi": 0,
            "note_name": "",
            "start_seconds": 0.0,
            "duration_seconds": cursor,
            "note_type": "rest",
            "confidence": 1.0,
        }
    )
    for index, (pitch, duration) in enumerate(zip(pitches, durations)):
        notes.append(
            {
                "midi": pitch,
                "note_name": "",
                "start_seconds": round(cursor, 3),
                "duration_seconds": duration,
                "note_type": "note",
                "confidence": 1.0,
            }
        )
        cursor += duration
        if index in {2, 5}:
            notes.append(
                {
                    "midi": 0,
                    "note_name": "",
                    "start_seconds": round(cursor, 3),
                    "duration_seconds": 0.16,
                    "note_type": "rest",
                    "confidence": 1.0,
                }
            )
            cursor += 0.16
    notes.append(
        {
            "midi": 0,
            "note_name": "",
            "start_seconds": round(cursor, 3),
            "duration_seconds": 0.35,
            "note_type": "rest",
            "confidence": 1.0,
        }
    )
    duration_seconds = round(cursor + 0.35, 3)
    frame_seconds = 0.01
    frame_count = int(round(duration_seconds / frame_seconds))
    f0_hz = [0.0] * frame_count
    energy = [0.0] * frame_count
    for note in notes:
        pitch = int(note["midi"])
        if pitch <= 0:
            continue
        start = int(round(float(note["start_seconds"]) / frame_seconds))
        end = min(
            frame_count,
            int(
                round(
                    (
                        float(note["start_seconds"])
                        + float(note["duration_seconds"])
                    )
                    / frame_seconds
                )
            ),
        )
        base_hz = 440.0 * 2.0 ** ((pitch - 69) / 12.0)
        for frame in range(start, end):
            local = (frame - start) * frame_seconds
            vibrato = (
                0.08 * math.sin(2.0 * math.pi * 5.2 * local)
                if end - start >= 45 and frame - start > 18
                else 0.0
            )
            f0_hz[frame] = round(
                base_hz * 2.0 ** (vibrato / 12.0),
                2,
            )
            energy[frame] = 0.72
    return {
        "schema_version": 1,
        "source": "ja-smoke-score-only",
        "extractor_backend": "hand-authored-validation",
        "language": "ja",
        "lyrics": "".join(lyric_units),
        "lyrics_reading": "きみのことがすき",
        "lyrics_reading_source": "hand-authored-kana",
        "lyrics_reading_complete": True,
        "lyrics_mora": ["き", "み", "の", "こ", "と", "が", "す", "き"],
        "lyrics_alignment": "one-mora-per-note",
        "duration_seconds": duration_seconds,
        "frame_seconds": frame_seconds,
        "confidence": 1.0,
        "notes": notes,
        "f0_hz": f0_hz,
        "energy": energy,
        "breath_positions_seconds": [2.0, 3.9],
        "vibrato": [],
    }


def write_silent_wav(path: Path, seconds: float) -> None:
    sample_rate = 16000
    with wave.open(str(path), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(sample_rate)
        output.writeframes(b"\x00\x00" * int(round(seconds * sample_rate)))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--url", default="http://127.0.0.1:9883/synthesize"
    )
    parser.add_argument("--seed", type=int, default=260726)
    args = parser.parse_args()

    OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
    score = build_score()
    score_path = OUTPUT_ROOT / "score.json"
    silent_path = OUTPUT_ROOT / "silent_placeholder.wav"
    output_path = OUTPUT_ROOT / "independent_role_voice.wav"
    headers_path = OUTPUT_ROOT / "response_headers.json"
    score_path.write_text(
        json.dumps(score, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    write_silent_wav(silent_path, score["duration_seconds"])

    with silent_path.open("rb") as audio_stream:
        response = requests.post(
            args.url,
            files={
                "audio_file": (
                    silent_path.name,
                    audio_stream,
                    "audio/wav",
                )
            },
            data={
                "score_json": json.dumps(score, ensure_ascii=False),
                "prompt_audio_path": str(PROMPT),
                "language": "ja",
                "prompt_language": "zh",
                "seed": str(args.seed),
                "request_id": "ja-smoke-kimi-no-koto",
                "max_seconds": "12",
                "inference_steps": "12",
            },
            timeout=300,
        )
    if response.status_code != 200:
        raise RuntimeError(
            f"SVS HTTP {response.status_code}: {response.text[-1200:]}"
        )
    if response.headers.get("X-SVS-Complete") != "1":
        raise RuntimeError("SVS response is not marked complete")
    output_path.write_bytes(response.content)
    headers_path.write_text(
        json.dumps(dict(response.headers), ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(
        json.dumps(
            {
                "output": str(output_path),
                "score": str(score_path),
                "bytes": len(response.content),
                "backend": response.headers.get("X-SVS-Backend", ""),
                "elapsed": response.headers.get("X-SVS-Elapsed", ""),
            },
            ensure_ascii=False,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
