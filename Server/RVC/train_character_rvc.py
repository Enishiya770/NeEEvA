"""Prepare and train the dedicated NeEEvA singing voice with official RVC.

The pipeline is deliberately resumable.  Corpus preparation, preprocessing,
F0 extraction, HuBERT extraction, training and index building can be restarted
without discarding completed artifacts.
"""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parent
VENDOR = ROOT / "vendor" / "rvc"
PREPROCESSOR = ROOT / "rvc_preprocess.py"
RVC_SCRIPT_RUNNER = ROOT / "run_rvc_script.py"
DATASET = ROOT / "dataset" / "character_tts"
HUM_DATASET = ROOT / "dataset" / "character_hum"
TRAIN_WAVS = ROOT / "dataset" / "character_train_wavs"
MODELS = ROOT / "models"
EXPERIMENT = "neeeva_character_v2"
EXP_DIR = VENDOR / "logs" / EXPERIMENT


def run(command: list[str]) -> None:
    if (
        len(command) >= 2
        and Path(command[0]).resolve() == Path(sys.executable).resolve()
        and not Path(command[1]).is_absolute()
    ):
        command = [command[0], str(RVC_SCRIPT_RUNNER), *command[1:]]
    printable = " ".join(f'"{part}"' if " " in part else part for part in command)
    print(f"[RVC/train] {printable}", flush=True)
    environment = os.environ.copy()
    current_pythonpath = environment.get("PYTHONPATH", "")
    environment["PYTHONPATH"] = str(VENDOR) + (
        os.pathsep + current_pythonpath if current_pythonpath else ""
    )
    subprocess.run(command, cwd=VENDOR, env=environment, check=True)


def stage_corpus() -> int:
    manifest_path = DATASET / "manifest.json"
    if not manifest_path.is_file():
        raise RuntimeError(f"character corpus is incomplete: {manifest_path}")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if float(manifest.get("total_seconds", 0)) < 570:
        raise RuntimeError("character corpus must contain at least 9.5 minutes")

    TRAIN_WAVS.mkdir(parents=True, exist_ok=True)
    expected: set[str] = set()
    for record in manifest.get("clips", []):
        name = str(record["file"])
        expected.add(name)
        source = DATASET / name
        destination = TRAIN_WAVS / name
        if not source.is_file():
            raise RuntimeError(f"corpus clip is missing: {source}")
        if destination.is_file() and destination.stat().st_size == source.stat().st_size:
            continue
        if destination.exists():
            destination.unlink()
        try:
            os.link(source, destination)
        except OSError:
            shutil.copy2(source, destination)

    hum_manifest_path = HUM_DATASET / "manifest.json"
    hum_seconds = 0.0
    if hum_manifest_path.is_file():
        hum_manifest = json.loads(hum_manifest_path.read_text(encoding="utf-8"))
        hum_seconds = float(hum_manifest.get("total_seconds", 0))
        for record in hum_manifest.get("clips", []):
            source = HUM_DATASET / str(record["file"])
            name = f"hum_{source.name}"
            expected.add(name)
            destination = TRAIN_WAVS / name
            if not source.is_file():
                raise RuntimeError(f"vocalization corpus clip is missing: {source}")
            if destination.is_file() and destination.stat().st_size == source.stat().st_size:
                continue
            if destination.exists():
                destination.unlink()
            try:
                os.link(source, destination)
            except OSError:
                shutil.copy2(source, destination)

    for stale in TRAIN_WAVS.glob("*.wav"):
        if stale.name not in expected:
            stale.unlink()
    print(
        f"[RVC/train] staged {len(expected)} clips "
        f"({(float(manifest['total_seconds']) + hum_seconds) / 60:.2f} min, "
        f"including {hum_seconds:.1f}s vocalizations)",
        flush=True,
    )
    return len(expected)


def wav_stems(directory: Path) -> set[str]:
    return {path.stem for path in directory.glob("*.wav")}


def preprocessing_complete() -> bool:
    gt_dir = EXP_DIR / "0_gt_wavs"
    wav16_dir = EXP_DIR / "1_16k_wavs"
    gt_stems = wav_stems(gt_dir)
    staged_stems = wav_stems(TRAIN_WAVS)
    all_sources_sliced = all(
        any(output.startswith(source + "_") for output in gt_stems)
        for source in staged_stems
    )
    return (
        len(gt_stems) >= 100
        and gt_stems == wav_stems(wav16_dir)
        and all_sources_sliced
    )


def extraction_complete() -> bool:
    stems = wav_stems(EXP_DIR / "1_16k_wavs")
    features = {path.stem for path in (EXP_DIR / "3_feature768").glob("*.npy")}
    coarse_f0 = {
        path.name[: -len(".wav.npy")]
        for path in (EXP_DIR / "2a_f0").glob("*.wav.npy")
    }
    nsf_f0 = {
        path.name[: -len(".wav.npy")]
        for path in (EXP_DIR / "2b-f0nsf").glob("*.wav.npy")
    }
    return bool(stems) and stems <= features and stems <= coarse_f0 and stems <= nsf_f0


def prepare_filelist() -> int:
    gt_dir = EXP_DIR / "0_gt_wavs"
    feature_dir = EXP_DIR / "3_feature768"
    f0_dir = EXP_DIR / "2a_f0"
    f0nsf_dir = EXP_DIR / "2b-f0nsf"
    names = sorted(
        path.stem
        for path in gt_dir.glob("*.wav")
        if (feature_dir / f"{path.stem}.npy").is_file()
        and (f0_dir / f"{path.stem}.wav.npy").is_file()
        and (f0nsf_dir / f"{path.stem}.wav.npy").is_file()
    )
    if len(names) < 100:
        raise RuntimeError(f"only {len(names)} prepared training slices were found")

    lines = [
        "|".join(
            (
                str((gt_dir / f"{name}.wav").resolve()),
                str((feature_dir / f"{name}.npy").resolve()),
                str((f0_dir / f"{name}.wav.npy").resolve()),
                str((f0nsf_dir / f"{name}.wav.npy").resolve()),
                "0",
            )
        )
        for name in names
    ]
    mute = VENDOR / "logs" / "mute"
    mute_line = "|".join(
        (
            str((mute / "0_gt_wavs" / "mute32k.wav").resolve()),
            str((mute / "3_feature768" / "mute.npy").resolve()),
            str((mute / "2a_f0" / "mute.wav.npy").resolve()),
            str((mute / "2b-f0nsf" / "mute.wav.npy").resolve()),
            "0",
        )
    )
    lines.extend((mute_line, mute_line))
    (EXP_DIR / "filelist.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    shutil.copy2(VENDOR / "configs" / "v2" / "32k.json", EXP_DIR / "config.json")
    print(f"[RVC/train] filelist ready: {len(names)} voice slices", flush=True)
    return len(names)


def export_artifacts(preferred_epoch: int | None = None) -> tuple[Path, Path]:
    MODELS.mkdir(parents=True, exist_ok=True)
    source_model: Path | None = None
    if preferred_epoch is not None:
        preferred = sorted(
            (VENDOR / "assets" / "weights").glob(
                f"{EXPERIMENT}_e{preferred_epoch}_s*.pth"
            ),
            key=lambda path: path.stat().st_mtime,
        )
        if preferred:
            source_model = preferred[-1]
        else:
            raise RuntimeError(f"RVC epoch {preferred_epoch} model was not found")
    if source_model is None:
        final_model = VENDOR / "assets" / "weights" / f"{EXPERIMENT}.pth"
        source_model = final_model if final_model.is_file() else None
    if source_model is None:
        candidates = sorted(
            (VENDOR / "assets" / "weights").glob(f"{EXPERIMENT}*.pth"),
            key=lambda path: path.stat().st_mtime,
        )
        if not candidates:
            raise RuntimeError("RVC training finished without an exported voice model")
        source_model = candidates[-1]

    index_candidates = sorted(
        EXP_DIR.glob(f"added_IVF*_Flat_nprobe_*_{EXPERIMENT}_v2.index"),
        key=lambda path: path.stat().st_mtime,
    )
    if not index_candidates:
        raise RuntimeError("RVC index training finished without an added index")

    model = MODELS / "neeeva_character.pth"
    index = MODELS / "neeeva_character.index"
    shutil.copy2(source_model, model)
    shutil.copy2(index_candidates[-1], index)
    metadata = {
        "version": 1,
        "backend": "RVC v2 f0 32k",
        "experiment": EXPERIMENT,
        "model_source": source_model.name,
        "index_source": index_candidates[-1].name,
    }
    (MODELS / "neeeva_character.json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    print(f"[RVC/train] exported model: {model}", flush=True)
    return model, index


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--epochs", type=int, default=100)
    parser.add_argument("--batch-size", type=int, default=2)
    parser.add_argument("--workers", type=int, default=max(1, min(6, os.cpu_count() or 1)))
    parser.add_argument("--force-preprocess", action="store_true")
    parser.add_argument("--skip-train", action="store_true")
    parser.add_argument("--export-epoch", type=int)
    args = parser.parse_args()

    if not VENDOR.joinpath("train", "train.py").is_file():
        raise RuntimeError("official RVC repository is missing")
    stage_corpus()
    EXP_DIR.mkdir(parents=True, exist_ok=True)

    if args.force_preprocess or not preprocessing_complete():
        run(
            [
                sys.executable,
                str(PREPROCESSOR),
                str(TRAIN_WAVS.resolve()),
                str(EXP_DIR.resolve()),
                "--sample-rate",
                "32000",
                "--workers",
                str(args.workers),
                "--period",
                "3.7",
            ]
        )
    else:
        print("[RVC/train] preprocessing already complete", flush=True)

    if not extraction_complete():
        run(
            [
                sys.executable,
                "train/dataset/extract_f0.py",
                "cuda",
                "1",
                "0",
                "0",
                str(EXP_DIR.resolve()),
                "false",
            ]
        )
        run(
            [
                sys.executable,
                "train/dataset/extract_hubert_feature.py",
                "cuda",
                "1",
                "0",
                "0",
                str(EXP_DIR.resolve()),
                "v2",
                "false",
            ]
        )
    else:
        print("[RVC/train] F0 and HuBERT extraction already complete", flush=True)

    prepare_filelist()
    if not args.skip_train:
        run(
            [
                sys.executable,
                "train/train.py",
                "-e",
                EXPERIMENT,
                "-sr",
                "32k",
                "-f0",
                "1",
                "-bs",
                str(args.batch_size),
                "-g",
                "0",
                "-te",
                str(args.epochs),
                "-se",
                "25",
                "-pg",
                str((VENDOR / "assets" / "pretrained_v2" / "f0G32k.pth").resolve()),
                "-pd",
                str((VENDOR / "assets" / "pretrained_v2" / "f0D32k.pth").resolve()),
                "-l",
                "1",
                "-c",
                "0",
                "-sw",
                "1",
                "-v",
                "v2",
            ]
        )

    run(
        [
            sys.executable,
            "train/train_index.py",
            EXPERIMENT,
            "v2",
            str((VENDOR / "assets" / "indices").resolve()),
            str(args.workers),
        ]
    )
    export_artifacts(args.export_epoch)


if __name__ == "__main__":
    main()
