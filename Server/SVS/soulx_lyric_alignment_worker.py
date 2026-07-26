"""Persistent timestamped singing-lyric recognizer for Mandarin/Cantonese."""

from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
import time
from pathlib import Path

READY_PREFIX = "NEEEVA_SVS_ALIGN_READY "
RESULT_PREFIX = "NEEEVA_SVS_ALIGN_RESULT "


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--soulx-root", required=True)
    parser.add_argument("--device", default="cpu")
    return parser.parse_args()


def main():
    args = parse_args()
    root = Path(args.soulx_root).resolve()
    os.chdir(root)
    sys.path.insert(0, str(root))
    started = time.perf_counter()

    import soundfile as sf
    from preprocess.tools.f0_extraction import F0Extractor
    from preprocess.tools.lyric_transcription import LyricTranscriber
    from preprocess.tools.vocal_detection import VocalDetector

    f0_extractor = F0Extractor(
        model_path=str(
            root
            / "pretrained_models"
            / "SoulX-Singer-Preprocess"
            / "rmvpe"
            / "rmvpe.pt"
        ),
        device=args.device,
        verbose=False,
    )
    transcriber = LyricTranscriber(
        zh_model_path=str(
            root
            / "pretrained_models"
            / "SoulX-Singer-Preprocess"
            / "speech_seaco_paraformer_large_asr_nat-zh-cn-16k-common-vocab8404-pytorch"
        ),
        en_model_path=str(
            root
            / "pretrained_models"
            / "SoulX-Singer-Preprocess"
            / "parakeet-tdt-0.6b-v2"
            / "parakeet-tdt-0.6b-v2.nemo"
        ),
        device=args.device,
        verbose=False,
    )
    print(
        READY_PREFIX
        + json.dumps({"load_seconds": round(time.perf_counter() - started, 3)}),
        flush=True,
    )

    for raw_line in sys.stdin:
        raw_line = raw_line.strip()
        if not raw_line:
            continue
        job_id = ""
        job_started = time.perf_counter()
        try:
            job = json.loads(raw_line)
            job_id = str(job.get("job_id", ""))
            language = str(job.get("language", "Mandarin"))
            if language not in {"Mandarin", "Cantonese"}:
                raise ValueError("timestamp alignment currently supports zh/yue")
            audio_path = Path(job["audio"]).resolve()
            audio_info = sf.info(str(audio_path))
            audio_seconds = audio_info.frames / float(audio_info.samplerate)
            with tempfile.TemporaryDirectory(
                prefix="neeeva_lyric_align_"
            ) as temp_name:
                temp_dir = Path(temp_name)
                full_f0 = f0_extractor.process(
                    str(audio_path), verbose=False
                )
                detector = VocalDetector(
                    cut_wavs_output_dir=str(temp_dir / "cuts"),
                    verbose=False,
                )
                segments = detector.process(
                    str(audio_path), f0=full_f0, verbose=False
                )
                words = []
                durations = []
                cursor = 0.0
                for segment in segments:
                    start = float(segment["start_time_ms"]) / 1000.0
                    end = float(segment["end_time_ms"]) / 1000.0
                    if start > cursor + 1e-4:
                        words.append("<SP>")
                        durations.append(start - cursor)
                    segment_path = str(segment["wav_fn"])
                    f0_extractor.process(
                        segment_path,
                        f0_path=str(Path(segment_path).with_suffix("")) + "_f0.npy",
                        verbose=False,
                    )
                    local_words, local_durations = transcriber.process(
                        segment_path,
                        language=language,
                        verbose=False,
                    )
                    words.extend(local_words)
                    durations.extend(local_durations)
                    cursor = end
                if cursor < audio_seconds - 1e-4:
                    words.append("<SP>")
                    durations.append(audio_seconds - cursor)
            result = {
                "ok": True,
                "job_id": job_id,
                "words": [str(value) for value in words],
                "durations": [round(float(value), 6) for value in durations],
                "elapsed_seconds": round(
                    time.perf_counter() - job_started, 3
                ),
            }
        except Exception as exc:
            result = {
                "ok": False,
                "job_id": job_id,
                "error": f"{type(exc).__name__}: {exc}",
            }
        print(RESULT_PREFIX + json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
