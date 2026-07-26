"""Persistent CPU-resident SoulX inference worker.

The large checkpoint is loaded once and kept in system RAM.  It moves to CUDA
only while a song is being rendered, then returns to CPU so dialogue TTS can
reuse the small GPU between requests.
"""

from __future__ import annotations

import argparse
import gc
import json
import os
import random
import shutil
import sys
import time
from pathlib import Path

READY_PREFIX = "NEEEVA_SVS_WORKER_READY "
RESULT_PREFIX = "NEEEVA_SVS_WORKER_RESULT "


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--soulx-root", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--device", default="cuda:0")
    parser.add_argument("--n-steps", type=int, default=12)
    return parser.parse_args()


def main():
    args = parse_args()
    root = Path(args.soulx_root).resolve()
    os.chdir(root)
    sys.path.insert(0, str(root))
    sys.path.insert(0, str(Path(__file__).resolve().parent))

    load_started = time.perf_counter()
    import numpy as np
    import torch
    from cli.inference import process
    from soulx_runner import build_low_vram_model
    from soulxsinger.utils.file_utils import load_config

    config_path = root / "soulxsinger" / "config" / "soulxsinger.yaml"
    phoneset_path = root / "soulxsinger" / "utils" / "phoneme" / "phone_set.json"
    config = load_config(str(config_path))
    inference_steps = max(4, min(32, int(args.n_steps)))
    config.infer.n_steps = inference_steps

    # Load and cast once in CPU memory. CUDA is occupied only per request.
    model = build_low_vram_model(
        model_path=args.model,
        config=config,
        device="cpu",
        use_fp16=False,
    )
    model.half()
    model.mel.float()
    model.eval()
    print(
        READY_PREFIX
        + json.dumps(
            {
                "load_seconds": round(time.perf_counter() - load_started, 3),
                "inference_steps": inference_steps,
            }
        ),
        flush=True,
    )

    for raw_line in sys.stdin:
        raw_line = raw_line.strip()
        if not raw_line:
            continue
        job_started = time.perf_counter()
        job_id = ""
        moved_to_cuda = False
        try:
            job = json.loads(raw_line)
            job_id = str(job.get("job_id", ""))
            seed = int(job.get("seed", 0)) & 0xFFFFFFFF
            job_steps = max(
                4, min(32, int(job.get("n_steps", inference_steps)))
            )
            config.infer.n_steps = job_steps
            random.seed(seed)
            np.random.seed(seed)
            torch.manual_seed(seed)

            transfer_started = time.perf_counter()
            model.to(args.device)
            moved_to_cuda = True
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
            transfer_seconds = time.perf_counter() - transfer_started

            generated_dir = Path(job["work_dir"]).resolve()
            generated_dir.mkdir(parents=True, exist_ok=True)

            class InferenceArgs:
                pass

            inference = InferenceArgs()
            inference.device = args.device
            inference.model_path = args.model
            inference.config = str(config_path)
            inference.prompt_wav_path = str(job["prompt_wav"])
            inference.prompt_metadata_path = str(job["prompt_metadata"])
            inference.target_metadata_path = str(job["target_metadata"])
            inference.phoneset_path = str(phoneset_path)
            inference.save_dir = str(generated_dir)
            # Follow the user's captured melody in its original key.  The
            # upstream auto-shift moved this character prompt by +3 semitones.
            inference.auto_shift = False
            inference.pitch_shift = 0
            inference.control = "melody"
            inference.use_fp16 = True

            inference_started = time.perf_counter()
            process(inference, config, model)
            inference_seconds = time.perf_counter() - inference_started
            generated = generated_dir / "generated.wav"
            if not generated.is_file():
                raise RuntimeError("SoulX worker did not create generated.wav")
            destination = Path(job["output"]).resolve()
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(generated, destination)
            result = {
                "ok": True,
                "job_id": job_id,
                "backend": "soulx-singer-svs",
                "control": "melody",
                "inference_steps": job_steps,
                "model_transfer_seconds": round(transfer_seconds, 3),
                "inference_seconds": round(inference_seconds, 3),
                "worker_job_seconds": round(
                    time.perf_counter() - job_started, 3
                ),
            }
        except Exception as exc:
            result = {
                "ok": False,
                "job_id": job_id,
                "error": f"{type(exc).__name__}: {exc}",
            }
        finally:
            if moved_to_cuda:
                try:
                    model.to("cpu")
                except Exception as cleanup_exc:
                    result = {
                        "ok": False,
                        "job_id": job_id,
                        "error": (
                            "worker could not release CUDA model: "
                            f"{type(cleanup_exc).__name__}: {cleanup_exc}"
                        ),
                    }
            gc.collect()
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        print(RESULT_PREFIX + json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
