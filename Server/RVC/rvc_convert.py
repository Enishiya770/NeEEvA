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

# vendor/rvc/infer/audio.py 在导入时二选一：CUDA 可用则用 torchaudio 解码，否则回落到
# ffmpeg。本脚本每次转换都是新进程，所以这个选择每次重做——CUDA 初始化偶发失败时就会
# 走 ffmpeg 分支，而它是以裸名 "ffmpeg" 调 subprocess.Popen 的，不在 PATH 上就直接
# FileNotFoundError，整次转换失败。vendor 目录里本就带了 ffmpeg.exe，这里补进 PATH，
# 让回落分支真正可用。
if (VENDOR / "ffmpeg.exe").is_file() or (VENDOR / "ffmpeg").is_file():
    os.environ["PATH"] = str(VENDOR) + os.pathsep + os.environ.get("PATH", "")

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


def shape_performance(
    f0: "np.ndarray", strength: float, seed: int, fps: float = 100.0
) -> "np.ndarray":
    """在 F0 曲线上叠加角色自己的演唱表情，让她不是逐帧复刻用户。

    只改音高、不碰时间轴：用户的节奏、咬字、气口全部保留（这正是变声器方案唯一
    胜过歌声合成的地方），而颤音、音准习惯、音高微移这些"谁在唱"的特征换成她的。
    RVC 内部本来就要从 F0 重新合成，所以这里改曲线不会引入时间拉伸的 artifact。

    三层，都随 seed 变化，同一次演唱内部保持一致：
      1. 持续音上的颤音——用户唱平的长音，她会带自己的揉音（速率/深度/相位每次不同）
      2. 音准——把持续音的中心朝十二平均律拉近，她唱得比用户准一些
      3. 缓慢音高游移——避免听上去像精确复制
    """
    if strength <= 0:
        return f0
    voiced = f0 > 1.0
    if not voiced.any():
        return f0

    rng = np.random.default_rng(seed & 0x7FFFFFFF)
    cents = np.zeros_like(f0, dtype=np.float64)

    # 分段：连续的有声区间
    edges = np.flatnonzero(np.diff(voiced.astype(np.int8)))
    starts = ([0] if voiced[0] else []) + list(edges[voiced[edges + 1]] + 1)
    ends = list(edges[~voiced[edges + 1]] + 1) + ([len(f0)] if voiced[-1] else [])

    semitone = 1200.0 / 12.0
    for s, e in zip(starts, ends):
        seg = f0[s:e]
        if seg.size < int(0.12 * fps):
            continue
        seg_cents = 1200.0 * np.log2(seg / seg[0])
        # 用一阶差分找"持续"的子段：音高基本不动的地方才适合加揉音
        flat = np.abs(np.gradient(seg_cents)) < 8.0
        run_start = None
        for i in range(len(flat) + 1):
            inside = i < len(flat) and flat[i]
            if inside and run_start is None:
                run_start = i
            elif not inside and run_start is not None:
                run_len = i - run_start
                if run_len >= int(0.30 * fps):
                    t = np.arange(run_len) / fps
                    rate = 4.9 + rng.random() * 1.4          # 4.9-6.3 Hz
                    depth = (18.0 + rng.random() * 28.0) * strength
                    phase = rng.random() * 2 * np.pi
                    # 揉音不会一开口就满幅，用 0.18s 渐入
                    ramp = np.clip(t / 0.18, 0.0, 1.0)
                    cents[s + run_start:s + i] += (
                        depth * ramp * np.sin(2 * np.pi * rate * t + phase)
                    )
                    # 音准：整段朝最近半音靠拢
                    center = float(np.median(seg_cents[run_start:i]))
                    target = round(center / semitone) * semitone
                    cents[s + run_start:s + i] += (target - center) * 0.5 * strength
                run_start = None

        # 缓慢游移：低频随机走，尺度约 1.5 秒
        n_ctrl = max(2, int((e - s) / fps / 1.5) + 1)
        ctrl = rng.normal(0.0, 9.0 * strength, n_ctrl)
        cents[s:e] += np.interp(
            np.linspace(0, n_ctrl - 1, e - s), np.arange(n_ctrl), ctrl
        )

    shaped = f0.copy()
    shaped[voiced] = f0[voiced] * np.power(2.0, cents[voiced] / 1200.0)
    return shaped


def install_performance_shaping(strength: float, seed: int) -> None:
    """包一层 Pipeline.get_f0，改返回的 F0 曲线。不修改 vendor 源码。"""
    from infer.vc.pipeline import Pipeline

    original = Pipeline.get_f0
    f0_mel_min = 1127 * math.log(1 + 50 / 700)
    f0_mel_max = 1127 * math.log(1 + 1100 / 700)

    def patched(self, x, p_len, f0_up_key, f0_method):
        _, f0bak = original(self, x, p_len, f0_up_key, f0_method)
        f0bak = shape_performance(f0bak, strength, seed)
        # 与 vendor 相同的方式由新曲线重算 coarse 索引
        f0_mel = 1127 * np.log(1 + f0bak / 700)
        f0_mel[f0_mel > 0] = (f0_mel[f0_mel > 0] - f0_mel_min) * 254 / (
            f0_mel_max - f0_mel_min
        ) + 1
        f0_mel[f0_mel <= 1] = 1
        f0_mel[f0_mel > 255] = 255
        return np.rint(f0_mel).astype(np.int32), f0bak

    Pipeline.get_f0 = patched


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
    # 0.0 等于完全不用角色索引(.index)，实测音色偏离且明显沙哑；0.75 沙哑显著减轻
    parser.add_argument("--index-rate", type=float, default=0.75)
    parser.add_argument("--rms-mix-rate", type=float, default=0.85)
    parser.add_argument("--protect", type=float, default=0.33)
    parser.add_argument("--seed", type=int, default=1234)
    # 0 = 逐帧复刻用户演唱；>0 时在 F0 曲线上叠加角色自己的颤音/音准/音高游移。
    parser.add_argument("--interpretation", type=float, default=0.0)
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
    interpretation = max(0.0, min(1.0, float(args.interpretation)))
    if interpretation > 0:
        install_performance_shaping(interpretation, seed)
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
                "interpretation": interpretation,
            }
        ),
        flush=True,
    )


if __name__ == "__main__":
    main()
