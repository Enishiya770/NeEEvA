"""Install official RVC model assets from the reachable Hugging Face mirror."""

from __future__ import annotations

import argparse
import os
import shutil
import zipfile
from dataclasses import dataclass
from pathlib import Path

import requests


ROOT = Path(__file__).resolve().parent
VENDOR = ROOT / "vendor" / "rvc"
MIRROR = os.environ.get("RVC_HF_MIRROR", "https://hf-mirror.com").rstrip("/")


@dataclass(frozen=True)
class Asset:
    remote: str
    local: str
    min_bytes: int

    @property
    def url(self) -> str:
        return f"{MIRROR}/lj1995/VoiceConversionWebUI/resolve/main/{self.remote}"

    @property
    def path(self) -> Path:
        return VENDOR / self.local


ASSETS = (
    Asset("hubert_base/config.json", "assets/hubert_base/config.json", 500),
    Asset("hubert_base/preprocessor_config.json", "assets/hubert_base/preprocessor_config.json", 100),
    Asset("hubert_base/pytorch_model.bin", "assets/hubert_base/pytorch_model.bin", 180_000_000),
    Asset("pretrained_v2/f0G32k.pth", "assets/pretrained_v2/f0G32k.pth", 70_000_000),
    Asset("pretrained_v2/f0D32k.pth", "assets/pretrained_v2/f0D32k.pth", 140_000_000),
    Asset("mute.zip", ".model-downloads/mute.zip", 300_000),
)


def download(asset: Asset) -> Path:
    destination = asset.path
    if destination.is_file() and destination.stat().st_size >= asset.min_bytes:
        return destination
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_suffix(destination.suffix + ".download")
    offset = partial.stat().st_size if partial.exists() else 0
    headers = {"User-Agent": "NeEEvA-RVC/1.0"}
    if offset:
        headers["Range"] = f"bytes={offset}-"
    print(f"[RVC/setup] downloading {asset.remote} (resume={offset / 1048576:.1f} MiB)", flush=True)
    with requests.get(asset.url, headers=headers, stream=True, timeout=(30, 120)) as response:
        response.raise_for_status()
        append = offset > 0 and response.status_code == 206
        with partial.open("ab" if append else "wb") as output:
            for chunk in response.iter_content(4 * 1024 * 1024):
                if chunk:
                    output.write(chunk)
    if partial.stat().st_size < asset.min_bytes:
        raise RuntimeError(f"incomplete RVC asset: {asset.remote}")
    os.replace(str(partial), str(destination))
    return destination


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--ffmpeg-dir",
        default=os.environ.get(
            "NEEEVA_FFMPEG_DIR",
            str(ROOT.parent.parent / "GPT-SoVITS" / "runtime"),
        ),
    )
    args = parser.parse_args()
    if not VENDOR.joinpath("train", "train.py").is_file():
        raise RuntimeError("RVC vendor repository is missing")

    paths = {asset.remote: download(asset) for asset in ASSETS}

    seed_rmvpe = ROOT.parent / "SeedVC" / "local_models" / "rmvpe" / "rmvpe.pt"
    rvc_rmvpe = VENDOR / "assets" / "rmvpe" / "rmvpe.pt"
    rvc_rmvpe.parent.mkdir(parents=True, exist_ok=True)
    if not rvc_rmvpe.is_file() or rvc_rmvpe.stat().st_size < 170_000_000:
        if not seed_rmvpe.is_file():
            raise RuntimeError(f"shared RMVPE model is missing: {seed_rmvpe}")
        shutil.copy2(str(seed_rmvpe), str(rvc_rmvpe))

    mute_dir = VENDOR / "logs" / "mute"
    if not mute_dir.joinpath("0_gt_wavs").is_dir():
        mute_dir.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(paths["mute.zip"]) as archive:
            archive.extractall(str(VENDOR / "logs"))

    ffmpeg_dir = Path(args.ffmpeg_dir)
    for name in ("ffmpeg.exe", "ffprobe.exe"):
        source = ffmpeg_dir / name
        destination = VENDOR / name
        if source.is_file() and not destination.is_file():
            shutil.copy2(str(source), str(destination))

    print("[RVC/setup] official assets ready", flush=True)


if __name__ == "__main__":
    main()
