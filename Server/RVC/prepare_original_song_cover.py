"""Create a longer character-voice cover preview from a local original song.

The original singer is content, not target training data: UVR5 first separates
the lead vocal, the existing character RVC transfers timbre, and ffmpeg mixes
the converted vocal back with the separated accompaniment.
"""

from __future__ import annotations

import argparse
import gc
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional


ROOT = Path(__file__).resolve().parent
LOCAL_PACKAGES = ROOT / "python_packages"
PROJECT_ROOT = ROOT.parent.parent
DEFAULT_GPT_ROOT = Path(
    os.environ.get("NEEEVA_GPT_SOVITS_ROOT", str(PROJECT_ROOT / "GPT-SoVITS"))
).expanduser()
FFMPEG = ROOT / "vendor" / "rvc" / "ffmpeg.exe"
RVC_RUNNER = ROOT / "rvc_convert.py"
RVC_MODEL = ROOT / "models" / "neeeva_character.pth"
RVC_INDEX = ROOT / "models" / "neeeva_character.index"


def checked(command: list[str], cwd: Optional[Path] = None) -> None:
    subprocess.run(command, cwd=cwd, check=True)


def newest_wav(folder: Path, prefix: str) -> Path:
    matches = sorted(folder.glob(f"{prefix}*.wav"), key=lambda path: path.stat().st_mtime)
    if not matches:
        raise FileNotFoundError(f"UVR5 did not create {prefix}*.wav under {folder}")
    return matches[-1]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("song", type=Path)
    parser.add_argument("--start", type=float, default=42.0)
    parser.add_argument("--duration", type=float, default=45.0)
    parser.add_argument("--output-dir", type=Path, default=ROOT / "song_covers" / "latest")
    parser.add_argument("--gpt-root", type=Path, default=DEFAULT_GPT_ROOT)
    parser.add_argument("--uvr-model", default="HP5_only_main_vocal")
    parser.add_argument("--aggressiveness", type=int, default=10)
    parser.add_argument("--vocal-gain", type=float, default=1.15)
    parser.add_argument("--instrument-gain", type=float, default=0.92)
    args = parser.parse_args()

    song = args.song.resolve()
    output_dir = args.output_dir.resolve()
    gpt_root = args.gpt_root.resolve()
    uvr_root = gpt_root / "tools" / "uvr5"
    model_path = uvr_root / "uvr5_weights" / f"{args.uvr_model}.pth"
    required = (song, FFMPEG, RVC_RUNNER, RVC_MODEL, RVC_INDEX, model_path)
    for path in required:
        if not path.is_file():
            raise FileNotFoundError(path)
    if args.duration < 10.0 or args.duration > 600.0:
        raise ValueError("duration must be between 10 and 600 seconds")

    excerpt = output_dir / "original_excerpt.wav"
    raw_vocal_dir = output_dir / "uvr_vocal"
    raw_instrument_dir = output_dir / "uvr_instrument"
    isolated_vocal = output_dir / "original_vocal.wav"
    accompaniment = output_dir / "accompaniment.wav"
    character_vocal = output_dir / "character_vocal.wav"
    character_mix = output_dir / "character_cover_mix.wav"
    output_dir.mkdir(parents=True, exist_ok=True)
    raw_vocal_dir.mkdir(parents=True, exist_ok=True)
    raw_instrument_dir.mkdir(parents=True, exist_ok=True)

    checked(
        [
            str(FFMPEG),
            "-hide_banner",
            "-loglevel",
            "error",
            "-ss",
            str(max(0.0, args.start)),
            "-t",
            str(args.duration),
            "-i",
            str(song),
            "-vn",
            "-ar",
            "44100",
            "-ac",
            "2",
            "-c:a",
            "pcm_s16le",
            "-y",
            str(excerpt),
        ]
    )

    sys.path.insert(0, str(uvr_root))
    if LOCAL_PACKAGES.is_dir():
        sys.path.insert(0, str(LOCAL_PACKAGES))
    import torch
    from vr import AudioPre

    device = "cuda:0" if torch.cuda.is_available() else "cpu"
    separator = AudioPre(
        agg=max(0, min(100, args.aggressiveness)),
        model_path=str(model_path),
        device=device,
        # GTX 16-series cards can produce non-finite UVR masks in FP16.
        # FP32 is still fast enough for a song excerpt and is deterministic.
        is_half=False,
    )
    separator._path_audio_(
        str(excerpt),
        str(raw_instrument_dir),
        str(raw_vocal_dir),
        "wav",
        False,
    )
    shutil.copy2(newest_wav(raw_vocal_dir, "vocal_"), isolated_vocal)
    shutil.copy2(newest_wav(raw_instrument_dir, "instrument_"), accompaniment)
    del separator
    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.empty_cache()

    checked(
        [
            sys.executable,
            str(RVC_RUNNER),
            "--source",
            str(isolated_vocal),
            "--output",
            str(character_vocal),
            "--model",
            str(RVC_MODEL),
            "--index",
            str(RVC_INDEX),
            "--auto-f0-adjust",
            "True",
            "--seed",
            "1234",
        ],
        cwd=ROOT,
    )

    fade_out_start = max(0.0, args.duration - 0.4)
    mix_filter = (
        f"[0:a]volume={args.instrument_gain}[inst];"
        f"[1:a]volume={args.vocal_gain}[voc];"
        "[inst][voc]amix=inputs=2:duration=longest,volume=2,"
        f"alimiter=limit=0.95,afade=t=in:st=0:d=0.15,"
        f"afade=t=out:st={fade_out_start}:d=0.4[mix]"
    )
    checked(
        [
            str(FFMPEG),
            "-hide_banner",
            "-loglevel",
            "error",
            "-i",
            str(accompaniment),
            "-i",
            str(character_vocal),
            "-filter_complex",
            mix_filter,
            "-map",
            "[mix]",
            "-ar",
            "44100",
            "-c:a",
            "pcm_s16le",
            "-y",
            str(character_mix),
        ]
    )

    metadata = {
        "source": str(song),
        "start_seconds": args.start,
        "duration_seconds": args.duration,
        "separation_model": args.uvr_model,
        "rvc_model": RVC_MODEL.name,
        "rvc_seed": 1234,
        "character_vocal": str(character_vocal),
        "character_cover_mix": str(character_mix),
    }
    (output_dir / "metadata.json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(json.dumps(metadata, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
