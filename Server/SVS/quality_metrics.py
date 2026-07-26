"""Offline pitch/voicing diagnostics for captured SoulX singing outputs."""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
from pathlib import Path


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--soulx-root", required=True)
    parser.add_argument("--target-metadata", required=True)
    parser.add_argument("audio", nargs="+")
    return parser.parse_args()


def main():
    args = parse_args()
    soulx_root = Path(args.soulx_root).resolve()
    os.chdir(soulx_root)
    sys.path.insert(0, str(soulx_root))

    import numpy as np
    import soundfile as sf
    from preprocess.tools.f0_extraction import F0Extractor

    metadata = json.loads(
        Path(args.target_metadata).read_text(encoding="utf-8")
    )
    target = np.concatenate(
        [
            np.asarray(
                [float(value) for value in segment["f0"].split()],
                dtype=np.float64,
            )
            for segment in metadata
        ]
    )
    extractor = F0Extractor(
        str(
            soulx_root
            / "pretrained_models"
            / "SoulX-Singer-Preprocess"
            / "rmvpe"
            / "rmvpe.pt"
        ),
        device="cpu",
        target_sr=24000,
        hop_size=480,
        verbose=False,
    )

    reports = []
    for audio_name in args.audio:
        audio_path = Path(audio_name).resolve()
        generated = extractor.process(str(audio_path), verbose=False)
        frame_count = min(len(target), len(generated))
        expected = target[:frame_count]
        actual = generated[:frame_count]
        expected_voiced = expected >= 55.0
        actual_voiced = actual >= 55.0
        common = expected_voiced & actual_voiced
        cents = (
            1200.0 * np.log2(actual[common] / expected[common])
            if np.any(common)
            else np.asarray([], dtype=np.float64)
        )
        waveform, sample_rate = sf.read(
            str(audio_path), dtype="float32", always_2d=True
        )
        reports.append(
            {
                "audio": str(audio_path),
                "seconds": round(len(waveform) / float(sample_rate), 3),
                "frames": int(frame_count),
                "voicing_recall": round(
                    float(np.sum(common))
                    / max(1, int(np.sum(expected_voiced))),
                    4,
                ),
                "false_voiced_rate": round(
                    float(np.sum(actual_voiced & ~expected_voiced))
                    / max(1, int(np.sum(~expected_voiced))),
                    4,
                ),
                "median_abs_pitch_cents": (
                    round(float(np.median(np.abs(cents))), 2)
                    if len(cents)
                    else None
                ),
                "p90_abs_pitch_cents": (
                    round(float(np.percentile(np.abs(cents), 90)), 2)
                    if len(cents)
                    else None
                ),
                "gross_pitch_error_rate_200c": (
                    round(float(np.mean(np.abs(cents) > 200.0)), 4)
                    if len(cents)
                    else None
                ),
                "octave_error_rate": (
                    round(
                        float(
                            np.mean(
                                np.abs(np.abs(cents) - 1200.0) <= 150.0
                            )
                        ),
                        4,
                    )
                    if len(cents)
                    else None
                ),
                "peak": round(float(np.max(np.abs(waveform))), 4),
                "rms_dbfs": round(
                    20.0
                    * math.log10(
                        max(1e-9, float(np.sqrt(np.mean(waveform**2))))
                    ),
                    2,
                ),
            }
        )
    print(json.dumps(reports, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
