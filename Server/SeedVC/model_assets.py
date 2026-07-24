"""Download and locate the exact official model assets required by Seed-VC.

The installed huggingface_hub is too old for some Xet-backed files on the reachable
mirror, so large assets use resumable plain HTTPS downloads instead.
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path

import requests


ROOT = Path(__file__).resolve().parent
MODEL_ROOT = ROOT / "local_models"
VENDOR_CHECKPOINTS = ROOT / "vendor" / "seed-vc" / "checkpoints"
MIRROR = os.environ.get("SEEDVC_HF_MIRROR", "https://hf-mirror.com").rstrip("/")


@dataclass(frozen=True)
class Asset:
    key: str
    repo: str
    filename: str
    relative_path: str
    min_bytes: int
    cached_glob: str = ""

    @property
    def url(self) -> str:
        return f"{MIRROR}/{self.repo}/resolve/main/{self.filename}"

    @property
    def destination(self) -> Path:
        return MODEL_ROOT / self.relative_path


ASSETS = (
    Asset(
        "seed_checkpoint",
        "Plachta/Seed-VC",
        "DiT_seed_v2_uvit_whisper_base_f0_44k_bigvgan_pruned_ft_ema_v2.pth",
        "seed-vc/model.pth",
        800_000_000,
        "models--Plachta--Seed-VC/snapshots/*/DiT_seed_v2_uvit_whisper_base_f0_44k_bigvgan_pruned_ft_ema_v2.pth",
    ),
    Asset(
        "seed_config",
        "Plachta/Seed-VC",
        "config_dit_mel_seed_uvit_whisper_base_f0_44k.yml",
        "seed-vc/config.yml",
        1_000,
        "models--Plachta--Seed-VC/snapshots/*/config_dit_mel_seed_uvit_whisper_base_f0_44k.yml",
    ),
    Asset("rmvpe", "lj1995/VoiceConversionWebUI", "rmvpe.pt", "rmvpe/rmvpe.pt", 170_000_000),
    Asset("campplus", "funasr/campplus", "campplus_cn_common.bin", "campplus/campplus_cn_common.bin", 25_000_000),
    Asset("bigvgan_config", "nvidia/bigvgan_v2_44khz_128band_512x", "config.json", "bigvgan/config.json", 1_000),
    Asset(
        "bigvgan_model",
        "nvidia/bigvgan_v2_44khz_128band_512x",
        "bigvgan_generator.pt",
        "bigvgan/bigvgan_generator.pt",
        480_000_000,
    ),
    Asset("whisper_config", "openai/whisper-small", "config.json", "whisper-small/config.json", 1_000),
    Asset(
        "whisper_preprocessor",
        "openai/whisper-small",
        "preprocessor_config.json",
        "whisper-small/preprocessor_config.json",
        1_000,
    ),
    Asset(
        "whisper_model",
        "openai/whisper-small",
        "model.safetensors",
        "whisper-small/model.safetensors",
        950_000_000,
    ),
)


def _existing_path(asset: Asset) -> Path | None:
    if asset.destination.is_file() and asset.destination.stat().st_size >= asset.min_bytes:
        return asset.destination
    if asset.cached_glob:
        for candidate in VENDOR_CHECKPOINTS.glob(asset.cached_glob):
            if candidate.is_file() and candidate.stat().st_size >= asset.min_bytes:
                return candidate
    return None


def _download(asset: Asset) -> Path:
    destination = asset.destination
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_suffix(destination.suffix + ".download")
    offset = partial.stat().st_size if partial.exists() else 0
    headers = {"User-Agent": "NeEEvA-SeedVC/1.0"}
    if offset:
        headers["Range"] = f"bytes={offset}-"
    print(
        f"[SeedVC/models] downloading {asset.key} from {asset.url} "
        f"(resume={offset / 1024 / 1024:.1f} MiB)",
        flush=True,
    )
    with requests.get(asset.url, headers=headers, stream=True, timeout=(30, 90)) as response:
        response.raise_for_status()
        append = offset > 0 and response.status_code == 206
        mode = "ab" if append else "wb"
        if not append:
            offset = 0
        written = offset
        next_report = written + 128 * 1024 * 1024
        with partial.open(mode) as output:
            for chunk in response.iter_content(chunk_size=4 * 1024 * 1024):
                if not chunk:
                    continue
                output.write(chunk)
                written += len(chunk)
                if written >= next_report:
                    print(f"[SeedVC/models] {asset.key}: {written / 1024 / 1024:.0f} MiB", flush=True)
                    next_report = written + 128 * 1024 * 1024
    if partial.stat().st_size < asset.min_bytes:
        raise RuntimeError(
            f"downloaded {asset.key} is incomplete: {partial.stat().st_size} bytes"
        )
    os.replace(str(partial), str(destination))
    print(f"[SeedVC/models] ready {asset.key}: {destination}", flush=True)
    return destination


def ensure_model_assets() -> dict[str, Path]:
    resolved: dict[str, Path] = {}
    for asset in ASSETS:
        path = _existing_path(asset)
        resolved[asset.key] = path if path is not None else _download(asset)
    return resolved
