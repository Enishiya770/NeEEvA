"""On-demand local Seed-VC service used by Unity's character hum-back path.

The neural model intentionally runs in a child process.  It is slower than keeping
the model resident, but releases VRAM after every conversion so GPT-SoVITS can keep
serving normal dialogue on a 6 GB GPU.
"""

from __future__ import annotations

import argparse
import asyncio
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path


ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = ROOT.parent.parent
VENDOR = ROOT / "vendor" / "seed-vc"
LOCAL_PACKAGES = ROOT / "python_packages"
RUNNER = ROOT / "seedvc_runner.py"
LAST_CONVERSION = ROOT / "last_conversion"
RVC_ROOT = ROOT.parent / "RVC"
RVC_VENV_PYTHON = RVC_ROOT / ".venv" / "Scripts" / "python.exe"
RVC_RUNNER = RVC_ROOT / "rvc_convert.py"
RVC_MODEL = RVC_ROOT / "models" / "neeeva_character.pth"
RVC_INDEX = RVC_ROOT / "models" / "neeeva_character.index"
sys.path.insert(0, str(LOCAL_PACKAGES))

import soundfile as sf
import uvicorn
from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import Response


app = FastAPI(title="NeEEvA Seed-VC", version="1.0")
_conversion_lock = threading.Lock()
_process_lock = threading.Lock()
_active_process: subprocess.Popen[str] | None = None
_active_request_id = ""

# The RVC runner releases all allocations when its child process exits and already
# retries recoverable CUDA OOM failures on CPU.  Unity rendering can temporarily
# leave only ~1.6 GiB free at EOU; a 1.9 GiB pre-gate therefore forced an 80-second
# CPU conversion without ever trying the much faster GPU path.  Try CUDA from
# 1.5 GiB and let the real allocation result (rather than a coarse snapshot) decide.
RVC_GPU_MIN_FREE_MIB = 1500


def _is_recoverable_cuda_failure(details: str) -> bool:
    """Return true only for failures for which a CPU retry can actually help."""
    lower = (details or "").lower()
    markers = (
        "cuda out of memory",
        "out of memory",
        "cudnn_status_alloc_failed",
        "cublas_status_alloc_failed",
        "cuda error",
        "cuda runtime error",
        "driver shutting down",
    )
    return any(marker in lower for marker in markers)
_runtime_log_stream: io.TextIOWrapper | None = None


def _configure_runtime_log() -> None:
    """Redirect both Python and Uvicorn output without PowerShell stream semantics."""
    global _runtime_log_stream
    raw_path = os.environ.get("NEEEVA_SEEDVC_LOG", "").strip()
    if not raw_path:
        return
    log_path = Path(raw_path).resolve()
    log_path.parent.mkdir(parents=True, exist_ok=True)
    _runtime_log_stream = log_path.open("w", encoding="utf-8", buffering=1)
    sys.stdout = _runtime_log_stream
    sys.stderr = _runtime_log_stream
    print(f"\n[{time.strftime('%Y-%m-%d %H:%M:%S')}] Unity auto-start")


def _cuda_free_mib() -> int | None:
    try:
        result = subprocess.run(
            [
                "nvidia-smi",
                "--query-gpu=memory.free",
                "--format=csv,noheader,nounits",
            ],
            capture_output=True,
            text=True,
            timeout=5,
            check=True,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        return int(result.stdout.strip().splitlines()[0])
    except (OSError, ValueError, subprocess.SubprocessError, IndexError):
        return None


def _prepare_source(source_bytes: bytes, destination: Path, max_seconds: float) -> float:
    if len(source_bytes) < 45:
        raise ValueError("source audio is empty")
    audio, sample_rate = sf.read(io.BytesIO(source_bytes), dtype="float32", always_2d=True)
    if sample_rate < 8000 or len(audio) == 0:
        raise ValueError("source audio has an invalid sample rate or duration")
    source_seconds = len(audio) / float(sample_rate)
    # Never silently keep only the tail: that made a successful conversion sound
    # as if the character had forgotten the beginning of a performance.  The
    # caller now requests a generous complete-performance limit; audio beyond it
    # is rejected explicitly instead of being reported as a successful hum-back.
    if source_seconds > max_seconds + 0.02:
        raise ValueError(
            f"source duration {source_seconds:.2f}s exceeds the complete-conversion "
            f"limit {max_seconds:.2f}s; no audio was truncated"
        )
    sf.write(str(destination), audio, sample_rate, subtype="PCM_16")
    return source_seconds


def _normalise_result(path: Path) -> tuple[bytes, float]:
    audio, sample_rate = sf.read(str(path), dtype="float32", always_2d=True)
    if len(audio) == 0:
        raise RuntimeError("Seed-VC returned an empty WAV")
    peak = float(abs(audio).max())
    if peak > 1e-5:
        audio = audio * (0.88 / max(0.88, peak))
    buffer = io.BytesIO()
    sf.write(buffer, audio, sample_rate, format="WAV", subtype="PCM_16")
    return buffer.getvalue(), len(audio) / float(sample_rate)


def _require_complete_result(source_seconds: float, output_seconds: float) -> None:
    """Reject truncated conversions so Unity cannot announce a partial replay as success."""
    tolerance = max(0.20, source_seconds * 0.025)
    if output_seconds + tolerance < source_seconds:
        raise RuntimeError(
            f"voice conversion returned only {output_seconds:.2f}s for a "
            f"{source_seconds:.2f}s source"
        )


def _rvc_python() -> Path | None:
    venv_torch = RVC_ROOT / ".venv" / "Lib" / "site-packages" / "torch" / "__init__.py"
    if RVC_VENV_PYTHON.is_file() and venv_torch.is_file():
        return RVC_VENV_PYTHON
    candidates: list[Path] = []
    explicit_python = os.environ.get("NEEEVA_RVC_PYTHON", "").strip()
    if explicit_python:
        candidates.append(Path(explicit_python).expanduser())
    gpt_root = os.environ.get("NEEEVA_GPT_SOVITS_ROOT", "").strip()
    if gpt_root:
        candidates.append(Path(gpt_root).expanduser() / "runtime" / "python.exe")
    candidates.append(PROJECT_ROOT / "GPT-SoVITS" / "runtime" / "python.exe")
    for candidate in candidates:
        if candidate.is_file():
            return candidate
    return None


def _rvc_ready() -> bool:
    return _rvc_python() is not None and all(
        path.is_file() for path in (RVC_RUNNER, RVC_MODEL, RVC_INDEX)
    )


def _parse_rvc_metadata(stdout: str) -> dict[str, object]:
    for line in reversed(stdout.splitlines()):
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict) and value.get("backend") == "rvc-character-v2":
            return value
    return {}


def _run_rvc_conversion(
    source_bytes: bytes,
    target_path: Path,
    auto_f0_adjust: bool,
    semitone_shift: int,
    performance_seed: int,
    rms_mix_rate: float,
    protect: float,
    max_seconds: float,
    request_id: str,
) -> tuple[bytes, dict[str, str]]:
    global _active_process, _active_request_id
    free_mib = _cuda_free_mib()
    prefer_cuda = free_mib is None or free_mib >= RVC_GPU_MIN_FREE_MIB
    started = time.perf_counter()

    with tempfile.TemporaryDirectory(prefix="neeeva_rvc_") as temp_name:
        temp_dir = Path(temp_name)
        source_path = temp_dir / "source.wav"
        output_path = temp_dir / "output.wav"
        try:
            source_seconds = _prepare_source(source_bytes, source_path, max_seconds)
        except (OSError, RuntimeError, ValueError) as exc:
            raise HTTPException(400, f"invalid source audio: {exc}") from exc

        rvc_python = _rvc_python()
        if rvc_python is None:
            raise HTTPException(503, "no compatible RVC Python runtime is installed")
        command = [
            str(rvc_python),
            str(RVC_RUNNER),
            "--source",
            str(source_path),
            "--output",
            str(output_path),
            "--model",
            str(RVC_MODEL),
            "--index",
            str(RVC_INDEX),
            "--auto-f0-adjust",
            "True" if auto_f0_adjust else "False",
            "--semitone-shift",
            str(semitone_shift),
            "--seed",
            str(performance_seed),
            "--rms-mix-rate",
            str(rms_mix_rate),
            "--protect",
            str(protect),
        ]
        attempts = [True, False] if prefer_cuda else [False]
        runner_metadata: dict[str, object] = {}
        used_cuda = False
        fell_back_to_cpu = False
        stdout = ""
        stderr = ""
        for attempt_index, attempt_cuda in enumerate(attempts):
            if output_path.exists():
                output_path.unlink()
            environment = os.environ.copy()
            environment["PYTHONUTF8"] = "1"
            environment.pop("NEEEVA_RVC_FORCE_CPU", None)
            if not attempt_cuda:
                # Empty CUDA_VISIBLE_DEVICES is not applied consistently by Windows
                # child-process environments.  -1 plus an explicit runner flag makes
                # the low-VRAM route deterministic and prevents DirectML auto-fallback.
                environment["CUDA_VISIBLE_DEVICES"] = "-1"
                environment["NEEEVA_RVC_FORCE_CPU"] = "1"

            process: subprocess.Popen[str] | None = None
            try:
                process = subprocess.Popen(
                    command,
                    cwd=str(RVC_ROOT),
                    env=environment,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
                with _process_lock:
                    _active_process = process
                    _active_request_id = request_id
                try:
                    # RVC internally segments long input around its configured query/
                    # center windows.  Allow CPU conversion time to scale with the
                    # complete source instead of killing every request at three minutes.
                    timeout_scale = 4 if attempt_cuda else 12
                    conversion_timeout = max(
                        180, min(600, int(source_seconds * timeout_scale + 60))
                    )
                    stdout, stderr = process.communicate(timeout=conversion_timeout)
                except subprocess.TimeoutExpired as exc:
                    process.kill()
                    process.communicate()
                    raise HTTPException(
                        504,
                        f"RVC conversion exceeded {conversion_timeout} seconds",
                    ) from exc
            finally:
                with _process_lock:
                    if _active_process is process:
                        _active_process = None
                        _active_request_id = ""

            if process is not None and process.returncode == 0:
                used_cuda = attempt_cuda
                fell_back_to_cpu = attempt_index > 0
                runner_metadata = _parse_rvc_metadata(stdout)
                break

            details = (stderr or stdout or "RVC conversion was cancelled")[-6000:]
            print(
                f"[RVC] request={request_id} device={'cuda' if attempt_cuda else 'cpu'} "
                f"failed (exit={getattr(process, 'returncode', 'unknown')}):\n{details}",
                file=sys.stderr,
                flush=True,
            )
            if attempt_cuda and _is_recoverable_cuda_failure(details):
                print(
                    f"[RVC] request={request_id} retrying safely on CPU",
                    flush=True,
                )
                continue
            if "out of memory" in details.lower():
                raise HTTPException(507, "RVC ran out of memory on both GPU and CPU")
            raise HTTPException(500, f"RVC failed:\n{details}")

        if not output_path.is_file():
            raise HTTPException(500, "RVC completed without producing a WAV")

        wav_bytes, output_seconds = _normalise_result(output_path)
        try:
            _require_complete_result(source_seconds, output_seconds)
        except RuntimeError as exc:
            raise HTTPException(500, str(exc)) from exc
        metadata: dict[str, object] = {
            "backend": "rvc-character-v2",
            "request_id": request_id,
            "source_seconds": round(source_seconds, 3),
            "output_seconds": round(output_seconds, 3),
            "complete": True,
            "target_path": str(target_path),
            "auto_f0_adjust": auto_f0_adjust,
            "requested_semitone_shift": semitone_shift,
            "requested_performance_seed": performance_seed,
            "requested_rms_mix_rate": rms_mix_rate,
            "requested_protect": protect,
            "device": runner_metadata.get("device", "cuda" if used_cuda else "cpu-low-vram"),
            "free_vram_mib_before": free_mib,
            "gpu_min_free_mib": RVC_GPU_MIN_FREE_MIB,
            "cpu_fallback": fell_back_to_cpu,
        }
        metadata.update(runner_metadata)
        _save_last_conversion(source_path, target_path, output_path, metadata)

    elapsed = time.perf_counter() - started
    actual_shift = runner_metadata.get("semitone_shift", semitone_shift)
    return wav_bytes, {
        "X-SVC-Backend": "rvc-character-v2",
        "X-SVC-Device": str(metadata["device"]),
        "X-SVC-Free-VRAM-MiB": "unknown" if free_mib is None else str(free_mib),
        "X-SVC-Source-Seconds": f"{source_seconds:.2f}",
        "X-SVC-Output-Seconds": f"{output_seconds:.2f}",
        "X-SVC-Complete": "true",
        "X-SVC-Elapsed-Seconds": f"{elapsed:.2f}",
        "X-SVC-CPU-Fallback": str(fell_back_to_cpu).lower(),
        "X-SVC-Steps": "trained",
        "X-SVC-Auto-F0-Adjust": str(auto_f0_adjust).lower(),
        "X-SVC-Semitone-Shift": str(actual_shift),
        "X-SVC-Seed": str(runner_metadata.get("seed", "unknown")),
        "X-SVC-RMS-Mix-Rate": str(runner_metadata.get("rms_mix_rate", rms_mix_rate)),
        "X-SVC-Protect": str(runner_metadata.get("protect", protect)),
    }


def _save_last_conversion(
    source_path: Path,
    target_path: Path,
    output_path: Path,
    metadata: dict[str, object],
) -> None:
    """Keep one private A/B diagnostic set; a new conversion replaces the old one."""
    LAST_CONVERSION.mkdir(parents=True, exist_ok=True)
    pairs = (
        (source_path, LAST_CONVERSION / "source.wav"),
        (target_path, LAST_CONVERSION / f"target_reference{target_path.suffix.lower()}"),
        (output_path, LAST_CONVERSION / "output.wav"),
    )
    for source, destination in pairs:
        temporary = destination.with_suffix(destination.suffix + ".tmp")
        shutil.copy2(str(source), str(temporary))
        os.replace(str(temporary), str(destination))
    metadata_path = LAST_CONVERSION / "metadata.json"
    metadata_temp = metadata_path.with_suffix(".json.tmp")
    metadata_temp.write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    os.replace(str(metadata_temp), str(metadata_path))


def _run_conversion(
    source_bytes: bytes,
    target_path_text: str,
    diffusion_steps: int,
    auto_f0_adjust: bool,
    semitone_shift: int,
    performance_seed: int,
    rms_mix_rate: float,
    protect: float,
    max_seconds: float,
    request_id: str,
) -> tuple[bytes, dict[str, str]]:
    global _active_process, _active_request_id
    target_path = Path(target_path_text).expanduser().resolve()
    if not target_path.is_file() or target_path.suffix.lower() not in {".wav", ".flac", ".mp3"}:
        raise HTTPException(400, "target_path must point to an existing local WAV/FLAC/MP3 file")

    if _rvc_ready():
        return _run_rvc_conversion(
            source_bytes,
            target_path,
            auto_f0_adjust,
            semitone_shift,
            performance_seed,
            rms_mix_rate,
            protect,
            max_seconds,
            request_id,
        )
    if not VENDOR.joinpath("inference.py").is_file():
        raise HTTPException(503, "no character RVC model or Seed-VC fallback is installed")

    free_mib = _cuda_free_mib()
    use_cuda = free_mib is None or free_mib >= 3400
    started = time.perf_counter()

    with tempfile.TemporaryDirectory(prefix="neeeva_seedvc_") as temp_name:
        temp_dir = Path(temp_name)
        source_path = temp_dir / "source.wav"
        output_dir = temp_dir / "output"
        try:
            source_seconds = _prepare_source(source_bytes, source_path, max_seconds)
        except (OSError, RuntimeError, ValueError) as exc:
            raise HTTPException(400, f"invalid source audio: {exc}") from exc

        command = [
            sys.executable,
            str(RUNNER),
            "--source",
            str(source_path),
            "--target",
            str(target_path),
            "--output",
            str(output_dir),
            "--diffusion-steps",
            str(diffusion_steps),
            "--f0-condition",
            "True",
            "--auto-f0-adjust",
            "True" if auto_f0_adjust else "False",
            "--semi-tone-shift",
            str(semitone_shift),
            "--fp16",
            "True" if use_cuda else "False",
        ]
        environment = os.environ.copy()
        environment["PYTHONUTF8"] = "1"
        environment["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1"
        # Direct huggingface.co access is unavailable on the target machine.  The
        # compatible mirror is only used by this isolated Seed-VC child process.
        environment.setdefault("HF_ENDPOINT", "https://hf-mirror.com")
        if not use_cuda:
            environment["CUDA_VISIBLE_DEVICES"] = ""

        try:
            process = subprocess.Popen(
                command,
                cwd=str(VENDOR),
                env=environment,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
            with _process_lock:
                _active_process = process
                _active_request_id = request_id
            try:
                stdout, stderr = process.communicate(timeout=600)
            except subprocess.TimeoutExpired as exc:
                process.kill()
                process.communicate()
                raise HTTPException(504, "Seed-VC conversion exceeded 10 minutes") from exc
        finally:
            with _process_lock:
                if _active_process is locals().get("process"):
                    _active_process = None
                    _active_request_id = ""

        if process.returncode != 0:
            details = (stderr or stdout or "Seed-VC conversion was cancelled")[-6000:]
            if "out of memory" in details.lower():
                raise HTTPException(507, "Seed-VC ran out of memory; close other GPU tasks and retry")
            raise HTTPException(500, f"Seed-VC failed:\n{details}")

        candidates = sorted(output_dir.glob("vc_*.wav"), key=lambda item: item.stat().st_mtime)
        if not candidates:
            raise HTTPException(500, "Seed-VC completed without producing a WAV")
        output_path = candidates[-1]
        wav_bytes, output_seconds = _normalise_result(output_path)
        try:
            _require_complete_result(source_seconds, output_seconds)
        except RuntimeError as exc:
            raise HTTPException(500, str(exc)) from exc
        _save_last_conversion(
            source_path,
            target_path,
            output_path,
            {
                "backend": "seed-vc-f0",
                "request_id": request_id,
                "source_seconds": round(source_seconds, 3),
                "output_seconds": round(output_seconds, 3),
                "complete": True,
                "target_path": str(target_path),
                "diffusion_steps": diffusion_steps,
                "auto_f0_adjust": auto_f0_adjust,
                "semitone_shift": semitone_shift,
                "device": "cuda" if use_cuda else "cpu-low-vram",
                "free_vram_mib_before": free_mib,
            },
        )

    elapsed = time.perf_counter() - started
    return wav_bytes, {
        "X-SVC-Backend": "seed-vc-f0",
        "X-SVC-Device": "cuda" if use_cuda else "cpu-low-vram",
        "X-SVC-Free-VRAM-MiB": "unknown" if free_mib is None else str(free_mib),
        "X-SVC-Source-Seconds": f"{source_seconds:.2f}",
        "X-SVC-Output-Seconds": f"{output_seconds:.2f}",
        "X-SVC-Complete": "true",
        "X-SVC-Elapsed-Seconds": f"{elapsed:.2f}",
        "X-SVC-Steps": str(diffusion_steps),
        "X-SVC-Auto-F0-Adjust": str(auto_f0_adjust).lower(),
    }


@app.get("/health")
def health() -> dict[str, object]:
    free_mib = _cuda_free_mib()
    backend = "rvc-character-v2" if _rvc_ready() else "seed-vc-f0"
    threshold = RVC_GPU_MIN_FREE_MIB if _rvc_ready() else 3400
    return {
        "ok": _rvc_ready() or VENDOR.joinpath("inference.py").is_file(),
        "backend": backend,
        "busy": _conversion_lock.locked(),
        "cuda_free_mib": free_mib,
        "cuda_min_free_mib": threshold,
        "will_use": "cuda" if free_mib is None or free_mib >= threshold else "cpu-low-vram",
    }


@app.post("/convert")
async def convert(
    source_audio: UploadFile = File(...),
    target_path: str = Form(...),
    request_id: str = Form(""),
    diffusion_steps: int = Form(8),
    auto_f0_adjust: bool = Form(True),
    semitone_shift: int = Form(0),
    performance_seed: int = Form(1234),
    rms_mix_rate: float = Form(0.25),
    protect: float = Form(0.33),
    max_seconds: float = Form(60.0),
) -> Response:
    if _conversion_lock.locked():
        raise HTTPException(409, "another Seed-VC conversion is already running")
    source_bytes = await source_audio.read()
    diffusion_steps = max(4, min(30, diffusion_steps))
    semitone_shift = max(-12, min(12, semitone_shift))
    performance_seed = int(performance_seed) & 0x7FFFFFFF
    rms_mix_rate = max(0.0, min(1.0, rms_mix_rate))
    protect = max(0.0, min(0.5, protect))
    max_seconds = max(1.0, min(120.0, max_seconds))
    request_id = (request_id or "anonymous")[:128]

    def locked_conversion() -> tuple[bytes, dict[str, str]]:
        with _conversion_lock:
            return _run_conversion(
                source_bytes,
                target_path,
                diffusion_steps,
                auto_f0_adjust,
                semitone_shift,
                performance_seed,
                rms_mix_rate,
                protect,
                max_seconds,
                request_id,
            )

    wav_bytes, headers = await asyncio.to_thread(locked_conversion)
    return Response(content=wav_bytes, media_type="audio/wav", headers=headers)


@app.post("/cancel/{request_id}")
def cancel(request_id: str) -> dict[str, object]:
    with _process_lock:
        process = _active_process
        matches = bool(process is not None and _active_request_id == request_id)
        if matches and process.poll() is None:
            process.kill()
    return {"ok": True, "cancelled": matches, "request_id": request_id}


def main() -> None:
    _configure_runtime_log()
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=9882)
    args = parser.parse_args()
    print(f"[SeedVC] listening on http://{args.host}:{args.port}")
    print("[SeedVC] dedicated character RVC is preferred; Seed-VC remains the fallback")
    print("[SeedVC] models run on demand and release VRAM after each request")
    uvicorn.run(app, host=args.host, port=args.port, log_level="info")


if __name__ == "__main__":
    main()
