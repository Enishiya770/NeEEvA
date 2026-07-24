"""Generate a small character-vocalization corpus for humming timbre coverage."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path

import requests
import soundfile as sf


PHRASES = (
    "んー……ふふーん。んんー、ふーん……ラララ、ラララー。",
    "ふーん、ふふふーん……んー、んんー。ララ、ララー。",
    "んんー……あー、ふーん。ふふーん、んー……。",
    "ラララー、ラララ……んー、ふーん、ふふーん。",
    "うーん……ふふふ、ふーん。あー……んんー。",
    "んー、んー、ふふーん……ラララ、ララー、んー。",
    "ふー……ふふーん、んんー。あー、ラララー……。",
    "んんんー……ふーん、ふふふーん。んー、あー。",
    "ララ、ラララー……ふーん。んー、んんー……。",
    "あー……んー、ふふーん。うー、ふーん、んんー。",
    "ふふふーん、ふーん……ラララー。んー、んー。",
    "んー……ラララ、ララー。ふーん、ふふーん……。",
    "うー、あー……んんー。ふふーん、ふーん、んー。",
    "ララー……んー、ふふふーん。あー、んんー。",
    "ふーん、んー……ふふーん。ラララ、ラララー。",
    "んんー、んー……あー。ふーん、ふふふーん……。",
    "ラララー、ふふーん……んー、うー、あー。",
    "ふー……んんー、ふーん。ラララ、んー……。",
    "んー、ふふーん……あー、あー。ララー、んんー。",
    "うーん、ふーん……ふふふーん。んー、ララー。",
    "あー、んー……ラララ。ふーん、んんー、ふー。",
    "ふふーん……んー、ララー。うー、ふーん……。",
    "んんー、あー……ふーん。ラララ、ふふーん。",
    "ララー、ラララ……んー。ふーん、ふふふーん。",
    "ふーん……うー、あー。んんー、んー、ララー。",
    "んー、んんー……ふふーん。ララ、ふーん……。",
    "あー……ラララー、んー。ふふーん、うーん。",
    "ふふふーん、んんー……ラララ。ふー、んー。",
    "うー、ふーん……あー。ララー、んー、んんー。",
    "んー……ふーん、ラララー。ふふーん、あー……。",
)


def duration(path: Path) -> float:
    info = sf.info(str(path))
    return float(info.frames) / float(info.samplerate)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--endpoint", default="http://127.0.0.1:9880/tts")
    parser.add_argument(
        "--output",
        default=str(Path(__file__).resolve().parent / "dataset" / "character_hum"),
    )
    parser.add_argument("--reference", required=True)
    parser.add_argument("--prompt-text", required=True)
    parser.add_argument("--target-seconds", type=float, default=150.0)
    args = parser.parse_args()

    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    records: list[dict[str, object]] = []
    total = 0.0
    for index, text in enumerate(PHRASES, start=1):
        path = output / f"hum_{index:03d}.wav"
        if not path.is_file():
            payload = {
                "ref_audio_path": str(Path(args.reference).resolve()).replace("\\", "/"),
                "prompt_text": args.prompt_text,
                "prompt_lang": "ja",
                "text": text,
                "text_lang": "ja",
                "streaming_mode": 0,
                "media_type": "wav",
            }
            print(f"[RVC/hum-data] generating {path.name}", flush=True)
            response = requests.post(args.endpoint, json=payload, timeout=180)
            response.raise_for_status()
            if not response.content.startswith(b"RIFF"):
                raise RuntimeError(f"TTS returned non-WAV data for {path.name}")
            temporary = path.with_suffix(".wav.tmp")
            temporary.write_bytes(response.content)
            os.replace(str(temporary), str(path))
        seconds = duration(path)
        if seconds < 2.0:
            raise RuntimeError(f"vocalization clip is too short: {path}")
        records.append({"file": path.name, "seconds": round(seconds, 3), "text": text})
        total += seconds
        print(f"[RVC/hum-data] ready {path.name}: total={total:.1f}s", flush=True)
        if total >= args.target_seconds:
            break

    manifest = {
        "version": 1,
        "source": "GPT-SoVITS character humming/vocalizations",
        "reference": str(Path(args.reference).resolve()),
        "total_seconds": round(total, 3),
        "clips": records,
    }
    (output / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    if total < args.target_seconds:
        raise RuntimeError(f"vocalization corpus reached only {total:.1f}s")
    print(f"[RVC/hum-data] complete: {total:.1f}s / {len(records)} clips", flush=True)


if __name__ == "__main__":
    main()
