"""One-shot RVC conversion runner used by the local Unity bridge."""

from __future__ import annotations

import argparse
import json
import math
import os
import random
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parent
VENDOR = ROOT / "vendor" / "rvc"
LOCAL_PACKAGES = ROOT / "python_packages"
if LOCAL_PACKAGES.is_dir():
    sys.path.insert(0, str(LOCAL_PACKAGES))

import librosa
import numpy as np
import soundfile as sf


def voiced_median_f0(path: Path) -> float | None:
    audio, sample_rate = librosa.load(str(path), sr=16000, mono=True)
    if audio.size < 1600:
        return None
    f0 = librosa.yin(audio, fmin=65.0, fmax=600.0, sr=sample_rate)
    rms = librosa.feature.rms(y=audio, frame_length=2048, hop_length=512)[0]
    size = min(f0.size, rms.size)
    if size == 0:
        return None
    f0 = f0[:size]
    rms = rms[:size]
    active = rms > max(0.008, float(np.percentile(rms, 35)))
    values = f0[active & np.isfinite(f0) & (f0 >= 65.0) & (f0 <= 600.0)]
    if values.size < 3:
        return None
    return float(np.median(values))


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--index", type=Path, required=True)
    parser.add_argument("--semitone-shift", type=int, default=0)
    parser.add_argument("--auto-f0-adjust", default="True")
    parser.add_argument("--target-median-f0", type=float, default=275.0)
    # On held-out humming, the trained voice itself was markedly closer to the
    # character than nearest-neighbour feature mixing.  Keep retrieval opt-in.
    parser.add_argument("--index-rate", type=float, default=0.0)
    parser.add_argument("--rms-mix-rate", type=float, default=0.25)
    parser.add_argument("--protect", type=float, default=0.33)
    parser.add_argument("--seed", type=int, default=1234)
    args = parser.parse_args()

    source = args.source.resolve()
    output = args.output.resolve()
    model = args.model.resolve()
    index = args.index.resolve()
    for path in (source, model, index):
        if not path.is_file():
            raise FileNotFoundError(path)

    source_f0 = voiced_median_f0(source)
    shift = max(-12, min(12, int(args.semitone_shift)))
    auto_f0 = str(args.auto_f0_adjust).lower() in {"1", "true", "yes", "on"}
    if auto_f0 and source_f0 and source_f0 > 0:
        shift += round(12.0 * math.log2(args.target_median_f0 / source_f0))
        shift = max(-12, min(12, shift))

    os.chdir(VENDOR)
    sys.path.insert(0, str(VENDOR))
    os.environ["weight_root"] = str(model.parent)
    os.environ["index_root"] = str(index.parent)
    os.environ["outside_index_root"] = str(index.parent)
    os.environ["rmvpe_root"] = str((VENDOR / "assets" / "rmvpe").resolve())
    # RVC's Config has its own command-line parser.  Our arguments are already
    # consumed, so hide them before constructing it.
    sys.argv = [sys.argv[0]]

    import torch

    seed = int(args.seed) & 0x7FFFFFFF
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    if torch.cuda.is_available():
        torch.cuda.manual_seed_all(seed)

    from configs.config import Config
    from infer.vc.modules import VC

    config = Config()
    # The low-VRAM server path deliberately hides CUDA from this child process.
    # RVC's Config may then auto-select torch-directml/privateuseone when the
    # optional torch_directml package is installed.  RMVPE interprets that as a
    # request for rmvpe.onnx + DmlExecutionProvider, neither of which is part of
    # this deployment.  Pin the fallback to the installed PyTorch rmvpe.pt on
    # genuine CPU FP32 instead of allowing the auto-detection to change backend.
    force_cpu = os.environ.get("NEEEVA_RVC_FORCE_CPU", "").strip().lower() in {
        "1",
        "true",
        "yes",
    } or not torch.cuda.is_available()
    if force_cpu:
        config.device = "cpu"
        config.dtype = torch.float32
        config.is_half = False
        config.dml = False
        config.instead = "cpu"
        config.gpu_name = None
        config.gpu_mem = None
        config.x_pad, config.x_query, config.x_center, config.x_max = 1, 6, 38, 41
    converter = VC(config)
    converter.get_vc(model.name)
    status, result = converter.vc_single(
        0,
        str(source),
        shift,
        "rmvpe",
        str(index),
        max(0.0, min(1.0, args.index_rate)),
        0,
        max(0.0, min(1.0, args.rms_mix_rate)),
        max(0.0, min(0.5, args.protect)),
    )
    sample_rate, audio = result
    if sample_rate is None or audio is None or len(audio) == 0:
        raise RuntimeError(f"RVC conversion failed: {status}")
    output.parent.mkdir(parents=True, exist_ok=True)
    sf.write(str(output), audio, int(sample_rate), subtype="PCM_16")
    print(
        json.dumps(
            {
                "backend": "rvc-character-v2",
                "device": str(config.device),
                "execution_mode": "cpu-fp32" if force_cpu else "cuda",
                "source_median_f0": None if source_f0 is None else round(source_f0, 3),
                "target_median_f0": args.target_median_f0,
                "semitone_shift": shift,
                "index_rate": max(0.0, min(1.0, args.index_rate)),
                "rms_mix_rate": max(0.0, min(1.0, args.rms_mix_rate)),
                "protect": max(0.0, min(0.5, args.protect)),
                "seed": seed,
            }
        ),
        flush=True,
    )


if __name__ == "__main__":
    main()
