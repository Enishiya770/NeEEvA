"""On-demand local voice-conversion service used by Unity's hum-back path.

The preferred character RVC runs in a short-lived persistent worker so Skill-load
warm-up and streamed chunks can share model import cost. The Seed-VC fallback still
uses an isolated process and releases VRAM after every conversion.
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
# rvc_convert.py 会 chdir 到这里；缺了它推理必然崩。vendor/ 与 models/ 都不在 git 内，
# 迁移时很容易只带回模型而漏掉 vendor，所以必须纳入就绪判定，否则 /health 会绿灯诈骗。
RVC_VENDOR = RVC_ROOT / "vendor" / "rvc"
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
#
# 8/10：全套服务起来之后实测空闲只有 1109~1122 MiB，1500 这道闸把每一次回哼都
# 推到了 CPU，CUDA 那条路一次都没试过。降到 900 实测同一段 20.4s 素材：
#     CUDA  8.70 / 8.71 / 8.82s   (n=3，含服务重启后的第一次)
#     CPU  12.53 / 12.73s         (n=2)
# 省约 3.9s（31%），4/4 成功，转换期间空闲显存降到 157~198 MiB。
# 注意两件事：
#  · 首次启用时第一次转换要 33s（CUDA 内核自动调优）。那份缓存落在磁盘上，
#    之后重启服务也不会再付——上面第三个 8.70s 就是重启后的第一次。
#  · 实际空闲显存随负载在 1100~2200 MiB 之间浮动，1000 这个闸是按低点定的。
#    真正的安全网仍是 attempts=[True, False]：CUDA OOM 会自动退回 CPU。
# 环境变量可覆盖，便于在不同显存占用下重新量而不用改代码。
RVC_GPU_MIN_FREE_MIB = int(os.environ.get("RVC_GPU_MIN_FREE_MIB", "1000"))


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


def _rvc_missing() -> list[str]:
    """列出 RVC 角色转换缺少的组件，供 /health 如实上报。"""
    missing: list[str] = []
    if _rvc_python() is None:
        missing.append("python")
    for label, path in (
        ("runner", RVC_RUNNER),
        ("model", RVC_MODEL),
        ("index", RVC_INDEX),
    ):
        if not path.is_file():
            missing.append(label)
    if not RVC_VENDOR.is_dir():
        missing.append("vendor")
    return missing


def _rvc_ready() -> bool:
    return not _rvc_missing()


def _parse_rvc_metadata(stdout: str) -> dict[str, object]:
    for line in reversed(stdout.splitlines()):
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(value, dict) and value.get("backend") == "rvc-character-v2":
            return value
    return {}


def _finish_rvc_result(
    source_path: Path,
    target_path: Path,
    output_path: Path,
    runner_metadata: dict,
    source_seconds: float,
    free_mib: int | None,
    auto_f0_adjust: bool,
    semitone_shift: int,
    performance_seed: int,
    rms_mix_rate: float,
    protect: float,
    index_rate: float,
    request_id: str,
    started: float,
    used_cuda: bool,
    fell_back_to_cpu: bool,
    via_worker: bool = False,
) -> tuple[bytes, dict[str, str]]:
    """把转换结果整理成响应。常驻 worker 与一次性进程两条路共用，避免两份元数据走样。"""
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
        "requested_index_rate": index_rate,
        "device": runner_metadata.get("device", "cuda" if used_cuda else "cpu-low-vram"),
        "free_vram_mib_before": free_mib,
        "gpu_min_free_mib": RVC_GPU_MIN_FREE_MIB,
        "cpu_fallback": fell_back_to_cpu,
        "via_worker": via_worker,
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
        "X-SVC-Index-Rate": str(runner_metadata.get("index_rate", index_rate)),
        "X-SVC-Via-Worker": str(via_worker).lower(),
    }


# 每次转换 spawn 一个新 Python 是这条路上最贵的一步：实测 import torch + fairseq +
# faiss + librosa 就要 11.2 秒，而真正的计算只有约 0.21 秒/每秒音频。逐段移调要连送
# 三次，光 import 就 33 秒。常驻 worker 把这份开销摊成一次。
#
# 显存：worker 活着期间模型是驻留的。空闲超过 RVC_WORKER_IDLE_SECONDS 就自己退出，
# 把显存还回去——所以省下的是"一串转换之内"的重复 import，而不是常年占着卡。
# 设成 0 可以完全关掉常驻模式，回到每次一个新进程。
RVC_WORKER_IDLE_SECONDS = float(os.environ.get("NEEEVA_RVC_WORKER_IDLE", "90"))


class _RvcWorker:
    """一个长期活着的 rvc_convert.py --serve 子进程。任何异常都降级回一次性进程。"""

    def __init__(self) -> None:
        self._process: subprocess.Popen[str] | None = None
        self._lock = threading.Lock()
        self._last_used = 0.0
        self._timer: threading.Timer | None = None
        self._device = ""
        # worker 起来之后设备就固定了(CUDA_VISIBLE_DEVICES 是进程级的)，
        # 所以记住它是哪一种；下一次请求要的模式不同就得重开。
        self._force_cpu: bool | None = None

    def _spawn(self, force_cpu: bool) -> bool:
        rvc_python = _rvc_python()
        if rvc_python is None:
            return False
        environment = os.environ.copy()
        environment["PYTHONUTF8"] = "1"
        environment.pop("NEEEVA_RVC_FORCE_CPU", None)
        if force_cpu:
            # 与一次性进程那条路用同一套开关，保证两边行为一致。
            environment["CUDA_VISIBLE_DEVICES"] = "-1"
            environment["NEEEVA_RVC_FORCE_CPU"] = "1"
        try:
            process = subprocess.Popen(
                [
                    str(rvc_python), str(RVC_RUNNER), "--serve",
                    "--model", str(RVC_MODEL), "--index", str(RVC_INDEX),
                ],
                cwd=str(RVC_ROOT),
                env=environment,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                bufsize=1,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except OSError as exc:
            print(f"[RVC/worker] spawn failed: {exc}", file=sys.stderr, flush=True)
            return False
        # 启动要跑完那 11 秒 import 再加载模型，给足余量。
        ready_line = _read_line_with_timeout(process, 300.0)
        if not ready_line:
            _kill_quietly(process)
            return False
        try:
            ready = json.loads(ready_line)
        except ValueError:
            _kill_quietly(process)
            return False
        if not ready.get("ready"):
            _kill_quietly(process)
            return False
        self._process = process
        self._device = str(ready.get("device", ""))
        self._force_cpu = force_cpu
        print(f"[RVC/worker] ready on {self._device}", flush=True)
        return True

    def _alive(self) -> bool:
        return self._process is not None and self._process.poll() is None

    def warm_up(self, force_cpu: bool) -> dict[str, object]:
        """Load imports/model without consuming or fabricating an audio request."""
        if RVC_WORKER_IDLE_SECONDS <= 0:
            return {"ready": False, "reason": "persistent worker disabled"}
        with self._lock:
            if self._alive() and self._force_cpu != force_cpu:
                self._shutdown_locked()
            already_ready = self._alive()
            if not already_ready and not self._spawn(force_cpu):
                return {"ready": False, "reason": "worker failed to start"}
            self._last_used = time.time()
            self._schedule_idle_shutdown()
            return {
                "ready": True,
                "already_ready": already_ready,
                "device": self._device or ("cpu-low-vram" if force_cpu else "cuda"),
                "idle_seconds": RVC_WORKER_IDLE_SECONDS,
            }

    def active_force_cpu(self) -> bool | None:
        """Return the warmed worker device, waiting for an in-progress warm-up.

        A CUDA warm-up itself reduces reported free VRAM.  Re-running the ordinary
        free-memory gate afterwards would incorrectly kill that worker and respawn
        it on CPU, so a live warmed worker is the more reliable source of truth.
        """
        with self._lock:
            return self._force_cpu if self._alive() else None

    def _schedule_idle_shutdown(self) -> None:
        if self._timer is not None:
            self._timer.cancel()
        if RVC_WORKER_IDLE_SECONDS <= 0:
            return
        self._timer = threading.Timer(
            RVC_WORKER_IDLE_SECONDS, self.shutdown, kwargs={"idle_only": True})
        self._timer.daemon = True
        self._timer.start()

    def shutdown(self, idle_only: bool = False) -> None:
        with self._lock:
            # 定时器可能在等锁期间又来了一次转换：那就不该杀刚用过的 worker，
            # 否则下一次请求要白等一遍 11 秒的 import。重排一次即可。
            if idle_only and time.time() - self._last_used < RVC_WORKER_IDLE_SECONDS:
                self._schedule_idle_shutdown()
                return
            self._shutdown_locked()

    def _shutdown_locked(self) -> None:
        process = self._process
        self._process = None
        self._force_cpu = None
        if process is None:
            return
        try:
            if process.stdin is not None:
                process.stdin.write(json.dumps({"shutdown": True}) + "\n")
                process.stdin.flush()
            process.wait(timeout=10)
        except Exception:
            _kill_quietly(process)
        print("[RVC/worker] released", flush=True)

    def convert(self, params: dict, timeout: float, force_cpu: bool) -> dict | None:
        """成功返回 runner 元数据；返回 None 表示"用不了"，调用方走一次性进程。

        显存紧张时整条路会退到 CPU——而那 11 秒 import 在 CPU 上一分不少，
        所以 worker 必须同样服务 CPU 路线。8/21 实测显存只剩 484MiB，九次转换
        全走了 CPU：worker 若只挂在 CUDA 分支上，一次都不会被用到。
        """
        if RVC_WORKER_IDLE_SECONDS <= 0:
            return None
        with self._lock:
            if self._alive() and self._force_cpu != force_cpu:
                # 设备模式是进程级的，改不了，只能换一个 worker。
                self._shutdown_locked()
            if not self._alive() and not self._spawn(force_cpu):
                return None
            process = self._process
            assert process is not None and process.stdin is not None
            try:
                process.stdin.write(json.dumps(params) + "\n")
                process.stdin.flush()
            except OSError as exc:
                print(f"[RVC/worker] write failed: {exc}", file=sys.stderr, flush=True)
                _kill_quietly(process)
                self._process = None
                return None
            line = _read_line_with_timeout(process, timeout)
            if not line:
                # 超时或进程死了：这条 worker 状态已经不可信，杀掉重来。
                _kill_quietly(process)
                self._process = None
                return None
            try:
                result = json.loads(line)
            except ValueError:
                _kill_quietly(process)
                self._process = None
                return None
            self._last_used = time.time()
            self._schedule_idle_shutdown()
            if "error" in result:
                # 单次转换失败不代表 worker 坏了，但这一次要让调用方降级重试。
                print(f"[RVC/worker] convert error: {result['error']}",
                      file=sys.stderr, flush=True)
                return None
            return result


def _kill_quietly(process: "subprocess.Popen[str]") -> None:
    try:
        process.kill()
        process.communicate(timeout=5)
    except Exception:
        pass


def _read_line_with_timeout(process: "subprocess.Popen[str]", timeout: float) -> str:
    """在超时内读 worker 的一行 stdout。子进程死掉时立刻返回空串。"""
    if process.stdout is None:
        return ""
    box: list[str] = []

    def reader() -> None:
        try:
            line = process.stdout.readline()  # type: ignore[union-attr]
            if line:
                box.append(line)
        except Exception:
            pass

    thread = threading.Thread(target=reader, daemon=True)
    thread.start()
    thread.join(timeout)
    return box[0].strip() if box else ""


_rvc_worker = _RvcWorker()


def _run_rvc_conversion(
    source_bytes: bytes,
    target_path: Path,
    auto_f0_adjust: bool,
    semitone_shift: int,
    performance_seed: int,
    rms_mix_rate: float,
    protect: float,
    index_rate: float,
    interpretation: float,
    max_seconds: float,
    request_id: str,
) -> tuple[bytes, dict[str, str]]:
    global _active_process, _active_request_id
    free_mib = _cuda_free_mib()
    warmed_force_cpu = _rvc_worker.active_force_cpu()
    prefer_cuda = (not warmed_force_cpu) if warmed_force_cpu is not None else (
        free_mib is None or free_mib >= RVC_GPU_MIN_FREE_MIB
    )
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
            "--interpretation",
            str(interpretation),
            # 不传的话 rvc_convert.py 会退回默认值，角色索引权重可能为 0 → 音色偏离且沙哑
            "--index-rate",
            str(index_rate),
        ]
        # 先试常驻 worker。CUDA 和 CPU 两条路都走它——省下的 11 秒 import 与设备无关。
        # 出任何岔子都返回 None，下面那套"一次性进程 + CUDA 失败退 CPU"的老路原样兜底。
        runner_metadata: dict[str, object] = {}
        used_cuda = False
        worker_result = _rvc_worker.convert(
            {
                "source": str(source_path),
                "output": str(output_path),
                "semitone_shift": semitone_shift,
                "auto_f0_adjust": "True" if auto_f0_adjust else "False",
                "seed": performance_seed,
                "rms_mix_rate": rms_mix_rate,
                "protect": protect,
                "index_rate": index_rate,
                "interpretation": interpretation,
            },
            timeout=max(180.0, min(600.0, source_seconds * (4 if prefer_cuda else 12) + 60)),
            force_cpu=not prefer_cuda,
        )
        if worker_result is not None and output_path.is_file():
            return _finish_rvc_result(
                source_path, target_path, output_path, worker_result,
                source_seconds, free_mib, auto_f0_adjust, semitone_shift,
                performance_seed, rms_mix_rate, protect, index_rate,
                request_id, started, used_cuda=prefer_cuda,
                fell_back_to_cpu=not prefer_cuda, via_worker=True,
            )

        attempts = [True, False] if prefer_cuda else [False]
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

        return _finish_rvc_result(
            source_path, target_path, output_path, runner_metadata,
            source_seconds, free_mib, auto_f0_adjust, semitone_shift,
            performance_seed, rms_mix_rate, protect, index_rate,
            request_id, started, used_cuda, fell_back_to_cpu,
        )


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
    index_rate: float,
    interpretation: float,
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
            index_rate,
            interpretation,
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
    rvc_missing = _rvc_missing()
    rvc_ok = not rvc_missing
    backend = "rvc-character-v2" if rvc_ok else "seed-vc-f0"
    threshold = RVC_GPU_MIN_FREE_MIB if rvc_ok else 3400
    return {
        "ok": rvc_ok or VENDOR.joinpath("inference.py").is_file(),
        "backend": backend,
        # 角色专属 RVC 缺件时在这里列明，避免"绿灯但一转换就 500"
        "rvc_missing": rvc_missing,
        "busy": _conversion_lock.locked(),
        "cuda_free_mib": free_mib,
        "cuda_min_free_mib": threshold,
        "will_use": "cuda" if free_mib is None or free_mib >= threshold else "cpu-low-vram",
    }


@app.post("/warmup")
async def warmup() -> dict[str, object]:
    """Non-audio model warm-up used when Unity loads the singing Skill.

    This request runs model startup on a worker thread.  Unity does not await it
    before generating dialogue, and a concurrent conversion safely waits for and
    reuses the same worker instead of starting another model process.
    """
    if not _rvc_ready():
        raise HTTPException(503, "character RVC is not installed; warm-up unavailable")
    free_mib = _cuda_free_mib()
    force_cpu = free_mib is not None and free_mib < RVC_GPU_MIN_FREE_MIB
    result = await asyncio.to_thread(_rvc_worker.warm_up, force_cpu)
    if not result.get("ready"):
        raise HTTPException(503, str(result.get("reason", "RVC warm-up failed")))
    return {
        "ok": True,
        "backend": "rvc-character-v2",
        "cuda_free_mib_before": free_mib,
        **result,
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
    rms_mix_rate: float = Form(0.85),
    protect: float = Form(0.33),
    # 角色索引权重。0 = 不用 .index，音色偏离且沙哑；0.75 为实测较优值
    index_rate: float = Form(0.75),
    # 0 = 逐帧复刻用户演唱；>0 让角色带上自己的颤音/音准/音高游移
    interpretation: float = Form(0.0),
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
    index_rate = max(0.0, min(1.0, index_rate))
    interpretation = max(0.0, min(1.0, interpretation))
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
                index_rate,
                interpretation,
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
