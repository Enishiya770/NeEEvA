"""Fast WAV-only RVC preprocessing without importing librosa.

The official preprocessor uses librosa only for resampling.  NeEEvA's training
corpus is already WAV, so scipy's polyphase resampler avoids a very slow import
in the bundled GPT-SoVITS Python runtime while retaining the official slicing
and normalization parameters.
"""

from __future__ import annotations

import argparse
import math
import sys
from concurrent.futures import ProcessPoolExecutor, as_completed
from pathlib import Path

import numpy as np
import soundfile as sf
from scipy import signal


ROOT = Path(__file__).resolve().parent
VENDOR = ROOT / "vendor" / "rvc"
sys.path.insert(0, str(VENDOR))

from train.dataset.slicer2 import Slicer


def load_mono(path: Path, target_rate: int) -> np.ndarray:
    audio, sample_rate = sf.read(str(path), dtype="float32", always_2d=True)
    audio = audio.mean(axis=1)
    if sample_rate != target_rate:
        divisor = math.gcd(int(sample_rate), target_rate)
        audio = signal.resample_poly(
            audio,
            target_rate // divisor,
            int(sample_rate) // divisor,
        ).astype(np.float32)
    return audio


def normalize_and_write(audio: np.ndarray, path32: Path, path16: Path, sample_rate: int) -> bool:
    peak = float(np.max(np.abs(audio))) if audio.size else 0.0
    if not np.isfinite(peak) or peak <= 0 or peak > 2.5:
        return False
    normalized = (audio / peak * (0.9 * 0.75)) + 0.25 * audio
    sf.write(str(path32), normalized.astype(np.float32), sample_rate, subtype="FLOAT")
    half = signal.resample_poly(normalized, 1, 2).astype(np.float32)
    sf.write(str(path16), half, 16000, subtype="FLOAT")
    return True


def process_clip(arguments: tuple[Path, Path, int, float]) -> tuple[str, int]:
    source, experiment, sample_rate, period = arguments
    gt_dir = experiment / "0_gt_wavs"
    wav16_dir = experiment / "1_16k_wavs"
    slicer = Slicer(
        sr=sample_rate,
        threshold=-42,
        min_length=1500,
        min_interval=400,
        hop_size=15,
        max_sil_kept=500,
    )
    b, a = signal.butter(N=5, Wn=48, btype="high", fs=sample_rate)
    audio = signal.lfilter(b, a, load_mono(source, sample_rate)).astype(np.float32)
    overlap = 0.3
    tail = period + overlap
    written = 0
    part = 0
    for sliced in slicer.slice(audio):
        block = 0
        while True:
            start = int(sample_rate * (period - overlap) * block)
            block += 1
            if len(sliced[start:]) > tail * sample_rate:
                chunk = sliced[start : start + int(period * sample_rate)]
            else:
                chunk = sliced[start:]
            name = f"{source.stem}_{part}.wav"
            part += 1
            if normalize_and_write(chunk, gt_dir / name, wav16_dir / name, sample_rate):
                written += 1
            if len(sliced[start:]) <= tail * sample_rate:
                break
    return source.name, written


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("experiment", type=Path)
    parser.add_argument("--sample-rate", type=int, default=32000)
    parser.add_argument("--workers", type=int, default=4)
    parser.add_argument("--period", type=float, default=3.7)
    args = parser.parse_args()

    source = args.source.resolve()
    experiment = args.experiment.resolve()
    gt_dir = experiment / "0_gt_wavs"
    wav16_dir = experiment / "1_16k_wavs"
    gt_dir.mkdir(parents=True, exist_ok=True)
    wav16_dir.mkdir(parents=True, exist_ok=True)
    files = sorted(source.glob("*.wav"))
    if not files:
        raise RuntimeError(f"no WAV corpus clips found in {source}")

    tasks = [(path, experiment, args.sample_rate, args.period) for path in files]
    completed = 0
    slices = 0
    with ProcessPoolExecutor(max_workers=max(1, min(args.workers, len(tasks)))) as pool:
        futures = [pool.submit(process_clip, task) for task in tasks]
        for future in as_completed(futures):
            name, count = future.result()
            completed += 1
            slices += count
            print(
                f"[RVC/preprocess] {completed}/{len(files)} {name}: {count} slices",
                flush=True,
            )
    if slices < 100:
        raise RuntimeError(f"preprocessing produced only {slices} slices")
    print(f"[RVC/preprocess] complete: {slices} slices", flush=True)


if __name__ == "__main__":
    main()
