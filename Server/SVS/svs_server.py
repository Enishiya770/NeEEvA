"""On-demand singing voice synthesis bridge for NeEEvA.

This service is intentionally separate from Seed-VC.  It transcribes the
performance into lyrics/notes/F0 and asks an SVS model to render new singing in
the character voice. The model stays in system RAM but moves onto the small GPU
only for inference, then releases VRAM back to dialogue TTS.
"""

from __future__ import annotations

import argparse
import asyncio
import hashlib
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
from typing import Optional

import soundfile as sf
import uvicorn
from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import JSONResponse, Response

from japanese_score import prepare_japanese_score
from japanese_phonemes import (
    japanese_g2p_transform,
    validate_against_phoneset,
)
from soulx_runner import (
    _prompt_cache_path,
    _valid_metadata,
    build_direct_target_metadata,
    fast_g2p_transform,
)

ROOT = Path(__file__).resolve().parent
PROJECT_ROOT = ROOT.parent.parent
DEFAULT_SOULX_ROOT = ROOT / "vendor" / "SoulX-Singer"
RUNNER = ROOT / "soulx_runner.py"
WORKER = ROOT / "soulx_inference_worker.py"
ALIGNMENT_WORKER = ROOT / "soulx_lyric_alignment_worker.py"
WORKER_READY_PREFIX = "NEEEVA_SVS_WORKER_READY "
WORKER_RESULT_PREFIX = "NEEEVA_SVS_WORKER_RESULT "
ALIGNMENT_READY_PREFIX = "NEEEVA_SVS_ALIGN_READY "
ALIGNMENT_RESULT_PREFIX = "NEEEVA_SVS_ALIGN_RESULT "
SUPPORTED_LANGUAGES = {
    "zh": "Mandarin",
    "cmn": "Mandarin",
    "mandarin": "Mandarin",
    "en": "English",
    "english": "English",
    "yue": "Cantonese",
    "cantonese": "Cantonese",
}
JAPANESE_LANGUAGE_ALIASES = {"ja", "jp", "japanese"}

app = FastAPI(title="NeEEvA Singing Voice Synthesis", version="1.0")
_synthesis_lock = threading.Lock()
_process_lock = threading.Lock()
_active_process: Optional[subprocess.Popen] = None
_active_request_id = ""
_cancelled_request_ids: set[str] = set()
_runtime_log_stream: Optional[io.TextIOWrapper] = None
_worker_process: Optional[subprocess.Popen] = None
_worker_ready = threading.Event()
_worker_start_lock = threading.Lock()
_worker_io_lock = threading.Lock()
_worker_boot_metadata: dict = {}
_worker_error = ""
_worker_stderr_stream: Optional[io.TextIOWrapper] = None
_alignment_process: Optional[subprocess.Popen] = None
_alignment_ready = threading.Event()
_alignment_start_lock = threading.Lock()
_alignment_io_lock = threading.Lock()
_alignment_boot_metadata: dict = {}
_alignment_error = ""
_alignment_stderr_stream: Optional[io.TextIOWrapper] = None


def _configure_runtime_log() -> None:
    """Redirect Python/Uvicorn output without PowerShell stream conversion."""
    global _runtime_log_stream
    raw_path = os.environ.get("NEEEVA_SVS_LOG", "").strip()
    if not raw_path:
        return
    log_path = Path(raw_path).resolve()
    log_path.parent.mkdir(parents=True, exist_ok=True)
    _runtime_log_stream = log_path.open("a", encoding="utf-8", buffering=1)
    sys.stdout = _runtime_log_stream
    sys.stderr = _runtime_log_stream
    print(f"\n[{time.strftime('%Y-%m-%d %H:%M:%S')}] SVS startup")


def _soulx_root() -> Path:
    configured = os.environ.get("NEEEVA_SOULX_ROOT", "").strip()
    return Path(configured).expanduser().resolve() if configured else DEFAULT_SOULX_ROOT


def _model_path(root: Path) -> Path:
    configured = os.environ.get("NEEEVA_SOULX_MODEL", "").strip()
    return (
        Path(configured).expanduser().resolve()
        if configured
        else root / "pretrained_models" / "SoulX-Singer" / "model.pt"
    )


def _runner_python() -> Path:
    configured = os.environ.get("NEEEVA_SVS_RUNNER_PYTHON", "").strip()
    candidates = [
        Path(configured).expanduser() if configured else None,
        ROOT / ".venv" / "Scripts" / "python.exe",
        _soulx_root() / ".venv" / "Scripts" / "python.exe",
        Path(sys.executable),
    ]
    for candidate in candidates:
        if candidate is not None and candidate.is_file():
            return candidate.resolve()
    return Path(sys.executable)


def _japanese_runner_path() -> Optional[Path]:
    configured = os.environ.get("NEEEVA_JA_SVS_RUNNER", "").strip()
    return Path(configured).expanduser().resolve() if configured else None


def _japanese_model_path() -> Optional[Path]:
    configured = os.environ.get("NEEEVA_JA_SVS_MODEL", "").strip()
    return Path(configured).expanduser().resolve() if configured else None


def _japanese_runner_python() -> Path:
    configured = os.environ.get("NEEEVA_JA_SVS_PYTHON", "").strip()
    if configured:
        candidate = Path(configured).expanduser()
        if candidate.is_file():
            return candidate.resolve()
    return _runner_python()


def _allow_experimental_japanese() -> bool:
    """内置假名适配是否可以对外声明"支持日语"。

    该适配把假名逐个映射到 SoulX 的**英语**音素表（SoulX 官方不支持日语），
    听感是"英语口音的日语"。默认不声明支持，让语种路由把日语降级到 9882 的
    角色歌声转换——那条路用的是用户真实演唱，咬字自然、音色由 RVC 负责。
    想试验这个近似实现时设 NEEEVA_SVS_ALLOW_EXPERIMENTAL_JA=1。
    配置了外部日语渲染器(NEEEVA_JA_SVS_RUNNER/MODEL)时本开关无关紧要。
    """
    return os.environ.get(
        "NEEEVA_SVS_ALLOW_EXPERIMENTAL_JA", "0"
    ).strip().lower() not in {"", "0", "false", "no", "off"}


def _japanese_backend_state() -> dict:
    runner = _japanese_runner_path()
    model = _japanese_model_path()
    external_missing = []
    if runner is None or not runner.is_file():
        external_missing.append("runner")
    if model is None or not model.exists():
        external_missing.append("model")
    if not external_missing:
        return {
            "backend": "diffsinger-ja",
            "implementation": "external-score-runner",
            "backend_available": True,
            "runner": str(runner),
            "model": str(model),
            "runner_python": str(_japanese_runner_python()),
            "missing": [],
            "input": "kana-mora-score",
            "audio_to_audio_conversion": False,
            "experimental": False,
        }

    root = _soulx_root()
    builtin_required = {
        "repository": root / "cli" / "inference.py",
        "config": root / "soulxsinger" / "config" / "soulxsinger.yaml",
        "phoneset": (
            root / "soulxsinger" / "utils" / "phoneme" / "phone_set.json"
        ),
        "model": _model_path(root),
    }
    builtin_missing = [
        name for name, path in builtin_required.items() if not path.is_file()
    ]
    # 文件齐全 ≠ 应当对外声明支持日语：这是英语音素近似，默认不参与语种路由，
    # 好让日语降级到更自然的真实演唱转换。
    opt_in = _allow_experimental_japanese()
    return {
        "backend": "soulx-ja-phone-adapter-experimental",
        "implementation": "builtin-soulx-phone-adapter",
        "backend_available": (not builtin_missing) and opt_in,
        "experimental_opt_in": opt_in,
        "experimental_opt_in_env": "NEEEVA_SVS_ALLOW_EXPERIMENTAL_JA",
        "runner": str(RUNNER),
        "model": str(_model_path(root)),
        "runner_python": str(_runner_python()),
        "missing": [f"soulx:{name}" for name in builtin_missing]
        + ([] if opt_in else ["experimental-ja-not-opted-in"]),
        "input": "kana-mora-score",
        "audio_to_audio_conversion": False,
        "experimental": True,
        "external_runner_missing": external_missing,
    }


def _inference_steps() -> int:
    try:
        configured = int(os.environ.get("NEEEVA_SVS_INFERENCE_STEPS", "12"))
    except ValueError:
        configured = 12
    return max(4, min(32, configured))


def _persistent_worker_enabled() -> bool:
    return os.environ.get("NEEEVA_SVS_PERSISTENT_WORKER", "1").strip().lower() not in {
        "0",
        "false",
        "no",
        "off",
    }


def _acoustic_alignment_enabled() -> bool:
    return os.environ.get(
        "NEEEVA_SVS_ACOUSTIC_LYRIC_ALIGNMENT", "1"
    ).strip().lower() not in {"0", "false", "no", "off"}


def _worker_alive() -> bool:
    return _worker_process is not None and _worker_process.poll() is None


def _start_worker() -> None:
    global _worker_process, _worker_error, _worker_stderr_stream
    if not _persistent_worker_enabled() or not WORKER.is_file():
        return
    with _worker_start_lock:
        if _worker_alive():
            return
        _worker_ready.clear()
        _worker_error = ""
        worker_log = ROOT / "runtime" / "soulx_worker.log"
        worker_log.parent.mkdir(parents=True, exist_ok=True)
        if _worker_stderr_stream is not None:
            _worker_stderr_stream.close()
        _worker_stderr_stream = worker_log.open(
            "a", encoding="utf-8", buffering=1
        )
        command = [
            str(_runner_python()),
            str(WORKER),
            "--soulx-root",
            str(_soulx_root()),
            "--model",
            str(_model_path(_soulx_root())),
            "--n-steps",
            str(_inference_steps()),
        ]
        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        try:
            _worker_process = subprocess.Popen(
                command,
                cwd=str(_soulx_root()),
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=_worker_stderr_stream,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
                creationflags=creation_flags,
            )
        except Exception as exc:
            _worker_error = f"{type(exc).__name__}: {exc}"
            return
        threading.Thread(
            target=_wait_for_worker_boot,
            name="SoulXWorkerBoot",
            daemon=True,
        ).start()


def _wait_for_worker_boot() -> None:
    global _worker_boot_metadata, _worker_error
    process = _worker_process
    if process is None or process.stdout is None:
        return
    try:
        for line in process.stdout:
            line = line.strip()
            if not line.startswith(WORKER_READY_PREFIX):
                continue
            _worker_boot_metadata = json.loads(line[len(WORKER_READY_PREFIX) :])
            _worker_ready.set()
            print(
                "[SVS] persistent worker ready "
                f"load={_worker_boot_metadata.get('load_seconds', '?')}s "
                f"steps={_worker_boot_metadata.get('inference_steps', '?')}",
                flush=True,
            )
            return
        _worker_error = f"worker exited during startup (code={process.poll()})"
    except Exception as exc:
        _worker_error = f"{type(exc).__name__}: {exc}"


def _ensure_worker_ready(timeout: float = 20.0) -> bool:
    if not _persistent_worker_enabled():
        return False
    if not _worker_alive():
        _start_worker()
    return _worker_ready.wait(timeout) and _worker_alive()


def _restart_worker_after_exit(process: subprocess.Popen) -> None:
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        return
    _start_worker()


def _cached_prompt_metadata(prompt_path: Path, language: str) -> tuple[Path, str]:
    cache_dir = ROOT / "runtime" / "prompt_cache"
    cache_dir.mkdir(parents=True, exist_ok=True)
    cached = _prompt_cache_path(prompt_path, language, cache_dir)
    if _valid_metadata(cached, language):
        return cached, "hit"
    sidecar = prompt_path.with_suffix(".json")
    if not _valid_metadata(sidecar, language):
        raise RuntimeError("prompt metadata cache is not ready")
    temporary = cached.with_suffix(".tmp")
    shutil.copy2(sidecar, temporary)
    os.replace(temporary, cached)
    return cached, "imported-sidecar"


def _run_worker_job(job: dict) -> dict:
    process = _worker_process
    if (
        process is None
        or process.stdin is None
        or process.stdout is None
        or process.poll() is not None
    ):
        raise RuntimeError("persistent SoulX worker is unavailable")
    with _worker_io_lock:
        process.stdin.write(json.dumps(job, ensure_ascii=False) + "\n")
        process.stdin.flush()
        while True:
            line = process.stdout.readline()
            if not line:
                raise RuntimeError(
                    f"persistent SoulX worker exited (code={process.poll()})"
                )
            line = line.strip()
            if not line.startswith(WORKER_RESULT_PREFIX):
                continue
            result = json.loads(line[len(WORKER_RESULT_PREFIX) :])
            if str(result.get("job_id", "")) != str(job.get("job_id", "")):
                continue
            if not result.get("ok"):
                raise RuntimeError(str(result.get("error", "worker failed")))
            return result


def _restart_worker_and_retry(job: dict, first_error: Exception) -> dict:
    """Replace a worker that lost its protocol pipe and retry one job once."""
    global _worker_process, _worker_error
    failed_process = _worker_process
    _worker_ready.clear()
    if failed_process is not None:
        if failed_process.poll() is None:
            failed_process.kill()
        try:
            failed_process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            raise RuntimeError(
                f"{first_error}; failed SoulX worker did not terminate"
            ) from first_error
        if _worker_process is failed_process:
            _worker_process = None

    _worker_error = (
        "worker request failed and was restarted: "
        f"{type(first_error).__name__}: {first_error}"
    )
    print(
        f"[SVS] {_worker_error}; retrying once with a fresh persistent worker",
        file=sys.stderr,
        flush=True,
    )
    _start_worker()
    if not (_worker_ready.wait(25.0) and _worker_alive()):
        raise RuntimeError(
            f"{first_error}; replacement SoulX worker was not ready: "
            f"{_worker_error or 'unknown startup failure'}"
        ) from first_error
    return _run_worker_job(job)


def _alignment_alive() -> bool:
    return _alignment_process is not None and _alignment_process.poll() is None


def _start_alignment_worker() -> None:
    global _alignment_process, _alignment_error, _alignment_stderr_stream
    if not _acoustic_alignment_enabled() or not ALIGNMENT_WORKER.is_file():
        return
    with _alignment_start_lock:
        if _alignment_alive():
            return
        _alignment_ready.clear()
        _alignment_error = ""
        log_path = ROOT / "runtime" / "soulx_alignment_worker.log"
        log_path.parent.mkdir(parents=True, exist_ok=True)
        if _alignment_stderr_stream is not None:
            _alignment_stderr_stream.close()
        _alignment_stderr_stream = log_path.open(
            "a", encoding="utf-8", buffering=1
        )
        command = [
            str(_runner_python()),
            str(ALIGNMENT_WORKER),
            "--soulx-root",
            str(_soulx_root()),
            "--device",
            "cpu",
        ]
        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        try:
            _alignment_process = subprocess.Popen(
                command,
                cwd=str(_soulx_root()),
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=_alignment_stderr_stream,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
                creationflags=creation_flags,
            )
        except Exception as exc:
            _alignment_error = f"{type(exc).__name__}: {exc}"
            return
        threading.Thread(
            target=_wait_for_alignment_boot,
            name="SoulXLyricAlignmentBoot",
            daemon=True,
        ).start()


def _wait_for_alignment_boot() -> None:
    global _alignment_boot_metadata, _alignment_error
    process = _alignment_process
    if process is None or process.stdout is None:
        return
    try:
        for line in process.stdout:
            line = line.strip()
            if not line.startswith(ALIGNMENT_READY_PREFIX):
                continue
            _alignment_boot_metadata = json.loads(
                line[len(ALIGNMENT_READY_PREFIX) :]
            )
            _alignment_ready.set()
            print(
                "[SVS] acoustic lyric aligner ready "
                f"load={_alignment_boot_metadata.get('load_seconds', '?')}s",
                flush=True,
            )
            return
        _alignment_error = (
            f"alignment worker exited during startup (code={process.poll()})"
        )
    except Exception as exc:
        _alignment_error = f"{type(exc).__name__}: {exc}"


def _ensure_alignment_ready(timeout: float = 20.0) -> bool:
    if not _acoustic_alignment_enabled():
        return False
    if not _alignment_alive():
        _start_alignment_worker()
    return _alignment_ready.wait(timeout) and _alignment_alive()


def _run_alignment_job(job: dict) -> dict:
    process = _alignment_process
    if (
        process is None
        or process.stdin is None
        or process.stdout is None
        or process.poll() is not None
    ):
        raise RuntimeError("acoustic lyric alignment worker is unavailable")
    with _alignment_io_lock:
        process.stdin.write(json.dumps(job, ensure_ascii=False) + "\n")
        process.stdin.flush()
        while True:
            line = process.stdout.readline()
            if not line:
                raise RuntimeError(
                    f"acoustic lyric aligner exited (code={process.poll()})"
                )
            line = line.strip()
            if not line.startswith(ALIGNMENT_RESULT_PREFIX):
                continue
            result = json.loads(line[len(ALIGNMENT_RESULT_PREFIX) :])
            if str(result.get("job_id", "")) != str(job.get("job_id", "")):
                continue
            if not result.get("ok"):
                raise RuntimeError(
                    str(result.get("error", "lyric alignment failed"))
                )
            return result


def _acoustic_alignment(
    target_path: Path, language: str, request_id: str
) -> tuple[dict | None, str]:
    if language not in {"Mandarin", "Cantonese"}:
        return None, "unsupported-language"
    digest = hashlib.sha256()
    digest.update(b"acoustic-lyrics-v2-segmented")
    digest.update(language.encode("utf-8"))
    with target_path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    cache_dir = ROOT / "runtime" / "alignment_cache"
    cache_dir.mkdir(parents=True, exist_ok=True)
    cache_path = cache_dir / f"{digest.hexdigest()}.json"
    try:
        cached = json.loads(cache_path.read_text(encoding="utf-8"))
        if cached.get("words") and cached.get("durations"):
            return cached, "hit"
    except (OSError, json.JSONDecodeError):
        pass
    if not _ensure_alignment_ready():
        return None, "worker-not-ready"
    result = _run_alignment_job(
        {
            "job_id": f"align-{request_id}",
            "audio": str(target_path),
            "language": language,
        }
    )
    alignment = {
        "words": result["words"],
        "durations": result["durations"],
        "elapsed_seconds": result.get("elapsed_seconds", 0),
    }
    temporary = cache_path.with_suffix(".tmp")
    temporary.write_text(
        json.dumps(alignment, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    os.replace(temporary, cache_path)
    return alignment, "miss-generated"


def _backend_state() -> dict:
    root = _soulx_root()
    required = {
        "repository": root / "cli" / "inference.py",
        "config": root / "soulxsinger" / "config" / "soulxsinger.yaml",
        "phoneset": root / "soulxsinger" / "utils" / "phoneme" / "phone_set.json",
        "model": _model_path(root),
        "preprocess_models": (
            root / "pretrained_models" / "SoulX-Singer-Preprocess"
        ),
    }
    missing = [
        name
        for name, path in required.items()
        if not (path.is_dir() if name == "preprocess_models" else path.is_file())
    ]
    soulx_available = not missing and RUNNER.is_file()
    japanese = _japanese_backend_state()
    supported_languages = (
        ["zh", "en", "yue"] if soulx_available else []
    ) + (["ja"] if japanese["backend_available"] else [])
    aggregate_missing = []
    if not soulx_available:
        aggregate_missing.extend(f"soulx:{name}" for name in missing)
    if not japanese["backend_available"]:
        aggregate_missing.extend(
            f"ja:{name}" for name in japanese["missing"]
        )
    return {
        "backend": "svs-language-router",
        "backend_available": bool(supported_languages),
        "soulx_root": str(root),
        "runner_python": str(_runner_python()),
        "missing": aggregate_missing,
        "supported_languages": supported_languages,
        "backends": {
            "zh_en_yue": {
                "backend": "soulx-singer-svs",
                "backend_available": soulx_available,
                "missing": missing,
            },
            "ja": japanese,
        },
        "score_schema": 1,
        "audio_to_audio_conversion": False,
        "on_demand_process": not _persistent_worker_enabled(),
        "gpu_on_demand": True,
        "inference_steps": _inference_steps(),
        "persistent_worker_enabled": _persistent_worker_enabled(),
        "persistent_worker_ready": _worker_ready.is_set() and _worker_alive(),
        "persistent_worker_error": _worker_error,
        "acoustic_lyric_alignment_enabled": _acoustic_alignment_enabled(),
        "acoustic_lyric_alignment_ready": (
            _alignment_ready.is_set() and _alignment_alive()
        ),
        "acoustic_lyric_alignment_error": _alignment_error,
    }


def _validate_score(raw: str) -> dict:
    try:
        score = json.loads(raw or "")
    except json.JSONDecodeError as exc:
        raise HTTPException(400, f"invalid SingingScore JSON: {exc}") from exc
    if not isinstance(score, dict) or int(score.get("schema_version", 0)) != 1:
        raise HTTPException(400, "SingingScore schema_version 1 is required")
    notes = score.get("notes")
    f0 = score.get("f0_hz")
    if not isinstance(notes, list) or not notes:
        raise HTTPException(422, "SingingScore contains no notes")
    if not isinstance(f0, list) or not any(float(value or 0) > 1 for value in f0):
        raise HTTPException(422, "SingingScore contains no voiced F0")
    return score


def _language_name(language: str) -> str:
    key = (language or "").strip().lower()
    if key not in SUPPORTED_LANGUAGES:
        raise HTTPException(
            422,
            "unsupported_language: SoulX-Singer SVS currently supports "
            "Mandarin, English and Cantonese; use a Japanese SVS backend for ja",
        )
    return SUPPORTED_LANGUAGES[key]


def _language_code(language: str) -> str:
    key = (language or "").strip().lower()
    if key in JAPANESE_LANGUAGE_ALIASES:
        return "ja"
    if key in SUPPORTED_LANGUAGES:
        name = SUPPORTED_LANGUAGES[key]
        return {"Mandarin": "zh", "English": "en", "Cantonese": "yue"}[name]
    raise HTTPException(422, f"unsupported_language: {language or '(empty)'}")


def _safe_prompt_path(raw_path: str) -> Path:
    configured = os.environ.get("NEEEVA_SVS_PROMPT_WAV", "").strip()
    value = configured or (raw_path or "").strip()
    if not value:
        raise HTTPException(400, "character prompt audio path is required")
    path = Path(value).expanduser().resolve()
    if not path.is_file() or path.suffix.lower() not in {".wav", ".mp3", ".flac"}:
        raise HTTPException(400, "character prompt audio is missing or unsupported")
    if path.stat().st_size > 100 * 1024 * 1024:
        raise HTTPException(400, "character prompt audio is unexpectedly large")
    return path


def _write_target_audio(data: bytes, path: Path, max_seconds: float) -> float:
    if len(data) <= 44:
        raise HTTPException(400, "target singing audio is empty")
    try:
        audio, sample_rate = sf.read(
            io.BytesIO(data), dtype="float32", always_2d=True
        )
    except Exception as exc:
        raise HTTPException(400, f"invalid target singing audio: {exc}") from exc
    if sample_rate < 8000 or len(audio) == 0:
        raise HTTPException(400, "target singing audio has invalid format")
    seconds = len(audio) / float(sample_rate)
    if seconds > max_seconds + 0.02:
        raise HTTPException(
            400,
            f"target performance is {seconds:.2f}s, above the {max_seconds:.2f}s limit",
        )
    sf.write(str(path), audio, sample_rate, subtype="PCM_16")
    return seconds


def _normalise_result(path: Path) -> tuple[bytes, float]:
    audio, sample_rate = sf.read(str(path), dtype="float32", always_2d=True)
    if len(audio) == 0:
        raise RuntimeError("SVS returned an empty WAV")
    peak = float(abs(audio).max())
    if peak > 1e-5:
        audio *= 0.9 / max(0.9, peak)
    output = io.BytesIO()
    sf.write(output, audio, sample_rate, format="WAV", subtype="PCM_16")
    return output.getvalue(), len(audio) / float(sample_rate)


def _capture_enabled() -> bool:
    return os.environ.get("NEEEVA_SVS_SAVE_CAPTURES", "1").strip().lower() not in {
        "0",
        "false",
        "no",
        "off",
    }


def _save_capture(
    request_id: str,
    source_path: Path,
    score_path: Path,
    output_path: Path,
    metadata: dict,
    prompt_metadata: Optional[Path] = None,
    target_metadata: Optional[Path] = None,
) -> str:
    if not _capture_enabled():
        return ""
    safe_id = "".join(
        character if character.isalnum() or character in "-_" else "_"
        for character in request_id
    )[:48]
    capture_id = f"{time.strftime('%Y%m%d_%H%M%S')}_{safe_id or 'request'}"
    capture_root = ROOT / "runtime" / "captures"
    capture_dir = capture_root / capture_id
    capture_dir.mkdir(parents=True, exist_ok=False)
    shutil.copy2(source_path, capture_dir / "source.wav")
    shutil.copy2(score_path, capture_dir / "singing_score.json")
    shutil.copy2(output_path, capture_dir / "generated.wav")
    if prompt_metadata is not None and prompt_metadata.is_file():
        shutil.copy2(prompt_metadata, capture_dir / "prompt_metadata.json")
    if target_metadata is not None and target_metadata.is_file():
        shutil.copy2(target_metadata, capture_dir / "target_metadata.json")
    capture_metadata = {**metadata, "request_id": request_id}
    (capture_dir / "result.json").write_text(
        json.dumps(capture_metadata, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    captures = sorted(
        (path for path in capture_root.iterdir() if path.is_dir()),
        key=lambda path: path.stat().st_mtime,
        reverse=True,
    )
    for expired in captures[20:]:
        shutil.rmtree(expired)
    return capture_id


def _run_japanese_synthesis(
    source_bytes: bytes,
    score: dict,
    prompt_path: Path,
    prompt_language: str,
    seed: int,
    request_id: str,
    max_seconds: float,
    inference_steps: int,
) -> tuple[bytes, dict]:
    """Run a score-only Japanese renderer.

    The source recording is retained only in the diagnostic capture.  It is
    deliberately not passed to the renderer, which prevents this adapter from
    quietly becoming audio-to-audio voice conversion.
    """
    global _active_process, _active_request_id
    state = _japanese_backend_state()
    if not state["backend_available"]:
        raise HTTPException(
            503,
            "Japanese score SVS is not installed: " + ", ".join(state["missing"]),
        )
    if state.get("implementation") == "builtin-soulx-phone-adapter":
        return _run_synthesis(
            source_bytes,
            score,
            prompt_path,
            "JapaneseAdapter",
            prompt_language,
            seed,
            request_id,
            max_seconds,
            inference_steps,
            backend_name="soulx-ja-phone-adapter-experimental",
            use_acoustic_alignment=False,
        )

    runner = Path(state["runner"])
    model = Path(state["model"])
    seed = int(seed) & 0xFFFFFFFF

    with tempfile.TemporaryDirectory(prefix="neeeva_ja_svs_") as temp_name:
        started = time.perf_counter()
        temp_dir = Path(temp_name)
        source_path = temp_dir / "source.wav"
        score_path = temp_dir / "singing_score.json"
        output_path = temp_dir / "generated.wav"
        source_seconds = _write_target_audio(
            source_bytes, source_path, max_seconds=max_seconds
        )
        score_path.write_text(
            json.dumps(score, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        command = [
            str(_japanese_runner_python()),
            str(runner),
            "--model",
            str(model),
            "--score",
            str(score_path),
            "--output",
            str(output_path),
            "--seed",
            str(seed),
            "--request-id",
            request_id,
        ]
        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        try:
            with _process_lock:
                _active_request_id = request_id
                _active_process = subprocess.Popen(
                    command,
                    cwd=str(runner.parent),
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    creationflags=creation_flags,
                )
                process = _active_process
            stdout, stderr = process.communicate(timeout=300)
        except subprocess.TimeoutExpired as exc:
            process.kill()
            process.communicate()
            raise HTTPException(
                504, "Japanese SVS synthesis exceeded 300 seconds"
            ) from exc
        finally:
            with _process_lock:
                _active_process = None
                _active_request_id = ""

        if process.returncode != 0 or not output_path.is_file():
            with _process_lock:
                was_cancelled = request_id in _cancelled_request_ids
                _cancelled_request_ids.discard(request_id)
            if was_cancelled:
                raise HTTPException(499, "Japanese SVS synthesis was cancelled")
            details = (stderr or stdout or "runner produced no output").strip()
            raise HTTPException(500, details[-1200:])

        wav_bytes, output_seconds = _normalise_result(output_path)
        runner_metadata = {}
        for line in reversed((stdout or "").splitlines()):
            try:
                candidate = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(candidate, dict):
                runner_metadata = candidate
                break
        metadata = {
            **runner_metadata,
            "backend": str(runner_metadata.get("backend") or "diffsinger-ja"),
            "score_schema": 1,
            "target_language": "Japanese",
            "lyrics_source": str(
                score.get("lyrics_reading_source", "sensevoice-kana")
            ),
            "resolved_lyrics": str(score.get("lyrics_reading", "")),
            "alignment_method": str(
                runner_metadata.get("alignment_method") or "kana-mora-score"
            ),
            "target_metadata": "singing-score-kana-direct",
            "source_seconds": round(source_seconds, 3),
            "output_seconds": round(output_seconds, 3),
            "elapsed_seconds": round(time.perf_counter() - started, 3),
            "audio_to_audio_conversion": False,
        }
        metadata["capture_id"] = _save_capture(
            request_id,
            source_path,
            score_path,
            output_path,
            metadata,
        )
        print(
            f"[SVS/JA] request={request_id} ok "
            f"mora={len(score.get('lyrics_mora', []))} "
            f"source={source_seconds:.3f}s output={output_seconds:.3f}s "
            f"total={metadata['elapsed_seconds']:.3f}s",
            flush=True,
        )
        return wav_bytes, metadata


def _run_synthesis(
    source_bytes: bytes,
    score: dict,
    prompt_path: Path,
    target_language: str,
    prompt_language: str,
    seed: int,
    request_id: str,
    max_seconds: float,
    inference_steps: int,
    backend_name: str = "soulx-singer-svs",
    use_acoustic_alignment: bool = True,
) -> tuple[bytes, dict]:
    global _active_process, _active_request_id
    seed = int(seed) & 0xFFFFFFFF
    with _process_lock:
        _cancelled_request_ids.discard(request_id)
    state = _backend_state()
    soulx_state = state["backends"]["zh_en_yue"]
    if not soulx_state["backend_available"]:
        raise HTTPException(
            503,
            "SoulX-Singer SVS is not installed: "
            + ", ".join(soulx_state["missing"]),
        )

    with tempfile.TemporaryDirectory(prefix="neeeva_svs_") as temp_name:
        started = time.perf_counter()
        temp_dir = Path(temp_name)
        target_path = temp_dir / "target.wav"
        output_path = temp_dir / "generated.wav"
        score_path = temp_dir / "singing_score.json"
        diagnostic_prompt_path = temp_dir / "prompt_metadata.json"
        diagnostic_target_path = temp_dir / "target_metadata.json"
        source_seconds = _write_target_audio(
            source_bytes, target_path, max_seconds=max_seconds
        )
        score_path.write_text(
            json.dumps(score, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )

        # Fast path: the fixed prompt metadata and Unity's aligned SingingScore
        # are ready without ASR, while a CPU-resident worker avoids reloading
        # the 2.6 GB checkpoint for every turn.
        direct_error = ""
        try:
            preprocess_started = time.perf_counter()
            prompt_metadata, prompt_cache = _cached_prompt_metadata(
                prompt_path, prompt_language
            )
            acoustic_alignment = None
            acoustic_alignment_cache = "disabled"
            acoustic_alignment_error = ""
            if use_acoustic_alignment:
                try:
                    acoustic_alignment, acoustic_alignment_cache = (
                        _acoustic_alignment(
                            target_path, target_language, request_id
                        )
                    )
                except Exception as alignment_exc:
                    acoustic_alignment_error = (
                        f"{type(alignment_exc).__name__}: {alignment_exc}"
                    )
                    acoustic_alignment_cache = "failed-safe-fallback"
                    print(
                        f"[SVS] request={request_id} acoustic lyric alignment "
                        f"unavailable: {acoustic_alignment_error}; "
                        "using note-boundary fallback",
                        file=sys.stderr,
                        flush=True,
                    )
            g2p_transformer = (
                japanese_g2p_transform
                if target_language == "JapaneseAdapter"
                else fast_g2p_transform
            )
            target_metadata_path = temp_dir / "target_metadata.json"
            direct_metadata, score_f0_frames = build_direct_target_metadata(
                score,
                target_language,
                g2p_transformer,
                acoustic_alignment=acoustic_alignment,
            )
            if target_language == "JapaneseAdapter":
                validate_against_phoneset(
                    direct_metadata[0]["phoneme"].split(),
                    (
                        _soulx_root()
                        / "soulxsinger"
                        / "utils"
                        / "phoneme"
                        / "phone_set.json"
                    ),
                )
            target_metadata_path.write_text(
                json.dumps(direct_metadata, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
            preprocess_seconds = time.perf_counter() - preprocess_started
            if _ensure_worker_ready(timeout=20.0):
                worker = _worker_process
                with _process_lock:
                    _active_request_id = request_id
                    _active_process = worker
                try:
                    worker_job = {
                        "job_id": request_id,
                        "seed": seed,
                        "n_steps": inference_steps,
                        "prompt_wav": str(prompt_path),
                        "prompt_metadata": str(prompt_metadata),
                        "target_metadata": str(target_metadata_path),
                        "work_dir": str(temp_dir / "worker_generated"),
                        "output": str(output_path),
                    }
                    try:
                        worker_result = _run_worker_job(worker_job)
                    except Exception as worker_error:
                        worker_result = _restart_worker_and_retry(
                            worker_job, worker_error
                        )
                finally:
                    with _process_lock:
                        _active_process = None
                        _active_request_id = ""
                if not output_path.is_file():
                    raise RuntimeError("persistent worker produced no output")
                wav_bytes, output_seconds = _normalise_result(output_path)
                metadata = {
                    **worker_result,
                    "backend": backend_name,
                    "score_schema": 1,
                    "score_f0_frames": score_f0_frames,
                    "target_language": target_language,
                    "prompt_cache": prompt_cache,
                    "target_metadata": "singing-score-direct",
                    "alignment_method": direct_metadata[0].get(
                        "alignment_method", "unknown"
                    ),
                    "lyrics_source": direct_metadata[0].get(
                        "lyrics_source", "score"
                    ),
                    "resolved_lyrics": direct_metadata[0].get(
                        "resolved_lyrics", str(score.get("lyrics", ""))
                    ),
                    "acoustic_alignment_cache": acoustic_alignment_cache,
                    "acoustic_alignment_error": acoustic_alignment_error,
                    "cleaned_note_events": direct_metadata[0].get(
                        "cleaned_note_events", 0
                    ),
                    "f0_cleaning": direct_metadata[0].get("f0_cleaning", {}),
                    "preprocess_seconds": round(preprocess_seconds, 3),
                    "model_load_seconds": 0.0,
                    "persistent_worker": True,
                    "source_seconds": round(source_seconds, 3),
                    "output_seconds": round(output_seconds, 3),
                    "runner_seconds": round(
                        time.perf_counter() - started, 3
                    ),
                    "elapsed_seconds": round(
                        time.perf_counter() - started, 3
                    ),
                }
                metadata["capture_id"] = _save_capture(
                    request_id,
                    target_path,
                    score_path,
                    output_path,
                    metadata,
                    prompt_metadata=prompt_metadata,
                    target_metadata=target_metadata_path,
                )
                print(
                    f"[SVS] request={request_id} ok "
                    f"source={metadata['source_seconds']:.3f}s "
                    f"output={metadata['output_seconds']:.3f}s "
                    f"total={metadata['elapsed_seconds']:.3f}s "
                    f"prompt_cache={prompt_cache} "
                    f"target={metadata['alignment_method']} "
                    f"lyrics={metadata['lyrics_source']} "
                    f"alignment_cache={acoustic_alignment_cache} "
                    f"preprocess={metadata['preprocess_seconds']}s "
                    f"transfer={metadata.get('model_transfer_seconds', '?')}s "
                    f"inference={metadata.get('inference_seconds', '?')}s "
                    f"steps={metadata.get('inference_steps', '?')} "
                    "persistent_worker=1",
                    flush=True,
                )
                return wav_bytes, metadata
            direct_error = "persistent worker was not ready within 20 seconds"
        except Exception as exc:
            direct_error = f"{type(exc).__name__}: {exc}"

        with _process_lock:
            was_cancelled = request_id in _cancelled_request_ids
            _cancelled_request_ids.discard(request_id)
        if was_cancelled:
            if not _worker_alive():
                _start_worker()
            raise HTTPException(499, "SVS synthesis was cancelled")

        if direct_error:
            print(
                f"[SVS] request={request_id} persistent fast path unavailable: "
                f"{direct_error}; falling back to one-shot runner",
                file=sys.stderr,
                flush=True,
            )
            if not _worker_alive():
                _start_worker()

        command = [
            str(_runner_python()),
            str(RUNNER),
            "--soulx-root",
            str(_soulx_root()),
            "--model",
            str(_model_path(_soulx_root())),
            "--prompt",
            str(prompt_path),
            "--target",
            str(target_path),
            "--score",
            str(score_path),
            "--output",
            str(output_path),
            "--target-language",
            target_language,
            "--prompt-language",
            prompt_language,
            "--seed",
            str(seed),
            "--cache-dir",
            str(ROOT / "runtime" / "prompt_cache"),
            "--n-steps",
            str(inference_steps),
            "--diagnostic-prompt-metadata",
            str(diagnostic_prompt_path),
            "--diagnostic-target-metadata",
            str(diagnostic_target_path),
        ]
        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        try:
            with _process_lock:
                _active_request_id = request_id
                _active_process = subprocess.Popen(
                    command,
                    cwd=str(_soulx_root()),
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    creationflags=creation_flags,
                )
                process = _active_process
            stdout, stderr = process.communicate(timeout=300)
        except subprocess.TimeoutExpired as exc:
            process.kill()
            process.communicate()
            raise HTTPException(504, "SVS synthesis exceeded 300 seconds") from exc
        finally:
            with _process_lock:
                _active_process = None
                _active_request_id = ""
        if process.returncode != 0 or not output_path.is_file():
            with _process_lock:
                was_cancelled = request_id in _cancelled_request_ids
                _cancelled_request_ids.discard(request_id)
            if was_cancelled:
                raise HTTPException(499, "SVS synthesis was cancelled")
            details = (stderr or stdout or "runner produced no output").strip()
            print(
                f"[SVS] request={request_id} runner failed "
                f"code={process.returncode} seed={seed}\n{details}",
                file=sys.stderr,
                flush=True,
            )
            raise HTTPException(500, details[-1200:])

        wav_bytes, output_seconds = _normalise_result(output_path)
        metadata = {}
        for line in reversed((stdout or "").splitlines()):
            try:
                candidate = json.loads(line)
            except json.JSONDecodeError:
                continue
            if isinstance(candidate, dict) and candidate.get("backend"):
                metadata = candidate
                break
        metadata.update(
            {
                "backend": backend_name,
                "source_seconds": round(source_seconds, 3),
                "output_seconds": round(output_seconds, 3),
                "elapsed_seconds": round(time.perf_counter() - started, 3),
            }
        )
        metadata["capture_id"] = _save_capture(
            request_id,
            target_path,
            score_path,
            output_path,
            metadata,
            prompt_metadata=diagnostic_prompt_path,
            target_metadata=diagnostic_target_path,
        )
        print(
            f"[SVS] request={request_id} ok "
            f"source={metadata['source_seconds']:.3f}s "
            f"output={metadata['output_seconds']:.3f}s "
            f"total={metadata['elapsed_seconds']:.3f}s "
            f"prompt_cache={metadata.get('prompt_cache', '?')} "
            f"target={metadata.get('target_metadata', '?')} "
            f"preprocess={metadata.get('preprocess_seconds', '?')}s "
            f"model={metadata.get('model_load_seconds', '?')}s "
            f"inference={metadata.get('inference_seconds', '?')}s",
            f"steps={metadata.get('inference_steps', '?')}",
            flush=True,
        )
        return wav_bytes, metadata


@app.on_event("startup")
def startup_worker():
    _start_worker()
    _start_alignment_worker()


@app.on_event("shutdown")
def shutdown_worker():
    if _worker_alive():
        _worker_process.kill()
    if _alignment_alive():
        _alignment_process.kill()


@app.get("/health")
def health():
    state = _backend_state()
    return {"ok": True, **state}


@app.post("/cancel")
def cancel(request_id: str = Form("")):
    with _process_lock:
        if _active_process is None:
            return {"ok": True, "cancelled": False}
        if request_id and request_id != _active_request_id:
            return JSONResponse(
                {"ok": False, "error": "request id does not match active synthesis"},
                status_code=409,
            )
        _cancelled_request_ids.add(_active_request_id)
        cancelled_worker = _active_process is _worker_process
        cancelled_process = _active_process
        _active_process.kill()
        if cancelled_worker:
            threading.Thread(
                target=_restart_worker_after_exit,
                args=(cancelled_process,),
                name="SoulXWorkerRestart",
                daemon=True,
            ).start()
        return {"ok": True, "cancelled": True}


@app.post("/synthesize")
async def synthesize(
    audio_file: UploadFile = File(...),
    score_json: str = Form(...),
    prompt_audio_path: str = Form(""),
    language: str = Form(""),
    prompt_language: str = Form("en"),
    seed: int = Form(0),
    request_id: str = Form(""),
    max_seconds: float = Form(60.0),
    inference_steps: int = Form(0),
):
    score = _validate_score(score_json)
    requested_language = language or str(score.get("language", ""))
    target_code = _language_code(requested_language)
    max_seconds = max(2.0, min(120.0, float(max_seconds)))
    source_bytes = await audio_file.read()
    effective_request_id = (request_id or "").strip()[:80] or str(time.time_ns())
    effective_seed = int(seed) & 0xFFFFFFFF
    effective_steps = (
        max(4, min(32, int(inference_steps)))
        if int(inference_steps) > 0
        else _inference_steps()
    )
    if target_code == "ja":
        # 语种路由：日语后端不可用时明确拒绝，让 Unity 降级到 9882 的角色歌声转换
        # （用户真实演唱 → 角色音色），而不是用英语音素近似硬唱。
        ja_state = _japanese_backend_state()
        if not ja_state.get("backend_available"):
            raise HTTPException(
                422,
                "unsupported_language: 日语渲染后端不可用 ("
                + ", ".join(ja_state.get("missing") or ["unknown"])
                + ")。配置 NEEEVA_JA_SVS_RUNNER/MODEL 使用外部日语渲染器，"
                + "或设 NEEEVA_SVS_ALLOW_EXPERIMENTAL_JA=1 启用内置英语音素近似。",
            )
        try:
            score = prepare_japanese_score(score)
        except ValueError as exc:
            raise HTTPException(422, str(exc)) from exc
        target_language = "Japanese"
        prompt_language_name = _language_name(
            os.environ.get("NEEEVA_SVS_PROMPT_LANGUAGE", "").strip()
            or prompt_language
        )
        prompt_path = _safe_prompt_path(prompt_audio_path)
    else:
        target_language = _language_name(requested_language)
        prompt_language_name = _language_name(
            os.environ.get("NEEEVA_SVS_PROMPT_LANGUAGE", "").strip()
            or prompt_language
        )
        prompt_path = _safe_prompt_path(prompt_audio_path)

    if not _synthesis_lock.acquire(blocking=False):
        raise HTTPException(409, "another singing synthesis is already running")
    try:
        wav_bytes, metadata = await asyncio.get_running_loop().run_in_executor(
            None,
            lambda: (
                _run_japanese_synthesis(
                    source_bytes,
                    score,
                    prompt_path,
                    prompt_language_name,
                    effective_seed,
                    effective_request_id,
                    max_seconds,
                    effective_steps,
                )
                if target_code == "ja"
                else _run_synthesis(
                    source_bytes,
                    score,
                    prompt_path,
                    target_language,
                    prompt_language_name,
                    effective_seed,
                    effective_request_id,
                    max_seconds,
                    effective_steps,
                )
            ),
        )
    finally:
        _synthesis_lock.release()

    headers = {
        "X-SVS-Complete": "1",
        "X-SVS-Backend": str(metadata["backend"]),
        "X-SVS-Mode": "transcription-svs",
        "X-SVS-Score-Schema": "1",
        "X-SVS-Elapsed": str(metadata["elapsed_seconds"]),
        "X-SVS-Output-Seconds": str(metadata["output_seconds"]),
        "X-SVS-Source-Seconds": str(metadata["source_seconds"]),
        "X-SVS-Prompt-Cache": str(metadata.get("prompt_cache", "unknown")),
        "X-SVS-Target-Metadata": str(
            metadata.get("target_metadata", "unknown")
        ),
        "X-SVS-Alignment": str(
            metadata.get("alignment_method", "unknown")
        ),
        "X-SVS-Lyrics-Source": str(
            metadata.get("lyrics_source", "unknown")
        ),
        "X-SVS-Alignment-Cache": str(
            metadata.get("acoustic_alignment_cache", "unknown")
        ),
        "X-SVS-Preprocess-Seconds": str(
            metadata.get("preprocess_seconds", "unknown")
        ),
        "X-SVS-Model-Load-Seconds": str(
            metadata.get("model_load_seconds", "unknown")
        ),
        "X-SVS-Model-Transfer-Seconds": str(
            metadata.get("model_transfer_seconds", "unknown")
        ),
        "X-SVS-Inference-Seconds": str(
            metadata.get("inference_seconds", "unknown")
        ),
        "X-SVS-Inference-Steps": str(
            metadata.get("inference_steps", "unknown")
        ),
        "X-SVS-Runner-Seconds": str(
            metadata.get("runner_seconds", "unknown")
        ),
        "X-SVS-Persistent-Worker": (
            "1" if metadata.get("persistent_worker") else "0"
        ),
        "X-SVS-Capture": str(metadata.get("capture_id", "")),
    }
    return Response(content=wav_bytes, media_type="audio/wav", headers=headers)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=9883)
    args = parser.parse_args()
    _configure_runtime_log()
    uvicorn.run(app, host=args.host, port=args.port)


if __name__ == "__main__":
    main()
