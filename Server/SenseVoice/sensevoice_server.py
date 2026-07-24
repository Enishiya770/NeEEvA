"""
SenseVoiceSmall 本地 ASR 服务 (给 Unity 客户端用)

功能：
  - 接收 WAV 音频
  - 用 SenseVoiceSmall 一次推理返回：转写文本 + 语言 + 情绪 + 音频事件
  - 对可能的歌唱/哼唱提取音高、音域、稳定度和旋律轮廓
  - 提供角色可自主调用的本地哼唱曲库 / MusicBrainz 检索

接口：
  GET  /health                       健康检查
  POST /asr                          form-data: audio_file=<WAV bytes>
                                     可选 form field: language=auto|zh|en|ja|ko|yue
  POST /songs/search                 歌曲候选检索（默认原始音频不离开本机）
  POST /songs/catalog/remember       保存可不命名的本地歌唱/哼唱 WAV
  POST /songs/catalog/rename         给歌曲记忆命名并重命名受管 WAV
  POST /songs/catalog/forget         删除歌曲记忆及其受管 WAV
  POST /songs/catalog/sing           解析已记住片段或可靠的后续段落
  POST /songs/catalog/add            remember 的兼容别名
  WS   /stream/asr                   PCM16/16k/mono 小帧，返回可回滚 partial

返回 JSON:
  {
    "text": "...",          # 转写文本（已去掉所有特殊标签）
    "language": "ja",       # 识别出的语言
    "emotion": "HAPPY",     # NEUTRAL/HAPPY/SAD/ANGRY/FEARFUL/DISGUSTED/SURPRISED
    "audio_event": "Speech",# Speech/Laughter/Applause/BGM/Cry 等
    "elapsed": 0.21         # 推理耗时(秒)
  }

启动：
  python sensevoice_server.py                      # 默认 cuda:0, 端口 9881
  python sensevoice_server.py --device cpu         # CPU 模式
  python sensevoice_server.py --port 9882          # 换端口
"""

import argparse
import asyncio
import base64
import io
import json
import os
import re
import threading
import time
from typing import Optional

import numpy as np
import soundfile as sf
import uvicorn
from fastapi import FastAPI, File, Form, UploadFile, WebSocket, WebSocketDisconnect
from fastapi.responses import JSONResponse
from funasr import AutoModel
from speaker_identity import SpeakerIdentityStore
from singing_analysis import SingingAnalyzer
from song_search import SongSearchEngine

# ------------------------------ 模型 ------------------------------

_model: Optional[AutoModel] = None
_vad_model: Optional[AutoModel] = None
_speaker_model: Optional[AutoModel] = None
_speaker_store: Optional[SpeakerIdentityStore] = None
_singing_analyzer: Optional[SingingAnalyzer] = None
_song_search_engine: Optional[SongSearchEngine] = None
_vad_min_speech_ms = 160
# SenseVoice 的同一个模型实例不保证并发 generate 安全。流式 partial 与
# EOU 后的正式 /asr 共用这一把锁；单次 SenseVoice 推理很短，正式请求最多
# 只会等待当前 partial 收尾，不会同时把两份模型塞进显存。
_asr_lock = threading.Lock()


def generate_asr(wav: np.ndarray, language: str = "auto"):
    with _asr_lock:
        return _model.generate(
            input=wav,
            cache={},
            language=language,
            use_itn=True,
        )


def load_model(
    device: str = "cuda:0",
    vad_device: str = "cpu",
    speaker_device: str = "cpu",
    speaker_profile_path: str = "speaker_profiles.json",
    speaker_match_threshold: float = 0.55,
    speaker_session_threshold: float = 0.48,
    auto_owner_bootstrap: bool = True,
    ai_voice_wav: str = "",
    pitch_device: str = "cpu",
    enable_torchcrepe: bool = True,
    song_catalog_path: str = "song_catalog.json",
):
    global _model, _vad_model, _speaker_model, _speaker_store
    global _singing_analyzer, _song_search_engine
    print(f"[SenseVoice] 加载模型 (device={device}) ...")
    t0 = time.time()
    _model = AutoModel(
        model="iic/SenseVoiceSmall",
        trust_remote_code=True,
        disable_update=True,
        device=device,
    )
    print(f"[SenseVoice] 模型就绪，耗时 {time.time() - t0:.2f}s")

    # VAD 很小，默认放在 CPU，避免和 SenseVoice / GPT-SoVITS 抢显存。
    print(f"[SenseVoice] 加载 FSMN-VAD (device={vad_device}) ...")
    vad_t0 = time.time()
    _vad_model = AutoModel(
        model="fsmn-vad",
        disable_update=True,
        device=vad_device,
    )
    print(f"[SenseVoice] FSMN-VAD 就绪，耗时 {time.time() - vad_t0:.2f}s")

    print(f"[SenseVoice] 加载 CAM++ 声纹模型 (device={speaker_device}) ...")
    speaker_t0 = time.time()
    _speaker_model = AutoModel(
        model="cam++",
        disable_update=True,
        device=speaker_device,
    )
    _speaker_store = SpeakerIdentityStore(
        speaker_profile_path,
        match_threshold=speaker_match_threshold,
        session_threshold=speaker_session_threshold,
        auto_owner_bootstrap=auto_owner_bootstrap,
    )
    print(
        f"[SenseVoice] CAM++ 就绪，耗时 {time.time() - speaker_t0:.2f}s，"
        f"档案={_speaker_store.path}"
    )

    _singing_analyzer = SingingAnalyzer(
        device=pitch_device,
        enable_torchcrepe=enable_torchcrepe,
    )
    _song_search_engine = SongSearchEngine(song_catalog_path, _singing_analyzer)
    print(
        f"[Singing] 感知已启用（快速FFT + "
        f"{'可选torchcrepe' if enable_torchcrepe else 'FFT-only'}），"
        f"本地歌曲={_song_search_engine.catalog_count}"
    )

    # 角色的参考音频是固定身份锚点。它只用于识别扬声器残留回声，
    # 真正的 AI 发言仍由 Unity 的 TTS 播放状态直接标记。
    if ai_voice_wav:
        ai_path = os.path.abspath(ai_voice_wav)
        if os.path.isfile(ai_path) and not _speaker_store.has_profile(SpeakerIdentityStore.AI_ID):
            try:
                ai_wav = decode_audio_path(ai_path)
                ai_embedding, _ = extract_speaker_embedding(ai_wav)
                if ai_embedding is not None:
                    _speaker_store.enroll_fixed(
                        SpeakerIdentityStore.AI_ID,
                        "角色自己",
                        "ai",
                        ai_embedding,
                        int(ai_wav.size * 1000 / 16000),
                        replace=True,
                    )
                    print(f"[Speaker] AI_SELF enrolled from {ai_path}")
            except Exception as exc:
                print(f"[Speaker] AI_SELF enrollment skipped: {exc}")
    _warmup()


def _warmup():
    """用 1 秒静音触发一次推理，消除首次请求的冷启动延迟"""
    try:
        silent = np.zeros(16000, dtype=np.float32)
        t0 = time.time()
        generate_asr(silent, "auto")
        _vad_model.generate(input=silent, cache={}, disable_pbar=True)
        _speaker_model.generate(input=np.zeros(32000, dtype=np.float32), cache={}, disable_pbar=True)
        print(f"[SenseVoice] 预热完成，耗时 {time.time() - t0:.2f}s")
    except Exception as e:
        print(f"[SenseVoice] 预热失败（不影响使用）: {e}")


# ------------------------------ 输出解析 ------------------------------

# SenseVoice 原始输出格式示例:
#   <|ja|><|NEUTRAL|><|Speech|><|woitn|>こんにちは、お元気ですか？
# 前 4 个标签分别是: 语言 / 情绪 / 音频事件 / 是否逆文本标准化
_tag_re = re.compile(r"<\|([^|]+)\|>")


def parse_output(raw: str):
    tags = _tag_re.findall(raw)
    text = _tag_re.sub("", raw).strip()
    lang = tags[0] if len(tags) >= 1 else ""
    emotion = tags[1] if len(tags) >= 2 else ""
    audio_event = tags[2] if len(tags) >= 3 else ""
    # tags[3] 一般是 withitn / woitn，对业务无用，不返回
    return text, lang, emotion, audio_event


def common_prefix(a: str, b: str):
    """返回相邻两版假设共同的前缀；它只是提示，不承诺永不回滚。"""
    limit = min(len(a), len(b))
    i = 0
    while i < limit and a[i] == b[i]:
        i += 1
    return a[:i]


def recognize_stream_partial(wav: np.ndarray, language: str):
    """流式会话的轻量 partial：不做声纹学习，也不写任何持久状态。"""
    t0 = time.time()
    res = generate_asr(wav, language)
    elapsed = time.time() - t0
    raw = res[0]["text"] if res else ""
    text, lang, emotion, audio_event = parse_output(raw)
    result = {
        "text": text,
        "language": lang,
        "emotion": emotion,
        "audio_event": audio_event,
        "elapsed": round(elapsed, 3),
    }
    if _singing_analyzer is not None:
        # 只看最近 8 秒并走快速跟踪器，避免长会话 partial 的 CPU 开销递增。
        singing = _singing_analyzer.analyze(
            wav[-8 * 16000 :], text, audio_event, thorough=False
        )
        result.update(singing_response_fields(singing, include_contour=False))
    return result


def singing_response_fields(analysis: Optional[dict], include_contour: bool = True):
    """Flatten singing output for Unity JsonUtility while retaining one schema."""
    analysis = analysis or {}
    fields = {
        "singing_analysis_available": bool(analysis.get("analysis_available", False)),
        "is_singing": bool(analysis.get("is_singing", False)),
        "singing_probability": float(analysis.get("singing_probability", 0.0)),
        "pitch_backend": str(analysis.get("pitch_backend", "none")),
        "voiced_ratio": float(analysis.get("voiced_ratio", 0.0)),
        "pitch_stability": float(analysis.get("pitch_stability", 0.0)),
        "sustained_ratio": float(analysis.get("sustained_ratio", 0.0)),
        "pitch_min_hz": float(analysis.get("pitch_min_hz", 0.0)),
        "pitch_max_hz": float(analysis.get("pitch_max_hz", 0.0)),
        "pitch_median_hz": float(analysis.get("pitch_median_hz", 0.0)),
        "pitch_low_note": str(analysis.get("pitch_low_note", "")),
        "pitch_high_note": str(analysis.get("pitch_high_note", "")),
        "pitch_median_note": str(analysis.get("pitch_median_note", "")),
        "note_sequence": str(analysis.get("note_sequence", "")),
        "singing_summary": str(analysis.get("summary", "")),
        "singing_start_seconds": float(analysis.get("singing_start_seconds", 0.0)),
        "pitch_timeline_start_seconds": float(
            analysis.get("pitch_timeline_start_seconds", 0.0)
        ),
    }
    if include_contour:
        fields["pitch_contour_midi"] = analysis.get("pitch_contour_midi", []) or []
        fields["pitch_timeline_midi"] = analysis.get("pitch_timeline_midi", []) or []
        fields["pitch_timeline_frame_seconds"] = float(
            analysis.get("pitch_timeline_frame_seconds", 0.10)
        )
    return fields


def is_tonal_vocal(analysis: Optional[dict], short_probe: bool = False):
    """Allow humming through speech VAD without opening the gate for noise."""
    if not analysis or not analysis.get("analysis_available"):
        return False
    probability_floor = 0.50 if short_probe else 0.45
    periodicity_floor = 0.67 if short_probe else 0.60
    voiced_floor = 0.42 if short_probe else 0.34
    return (
        float(analysis.get("singing_probability", 0.0)) >= probability_floor
        and float(analysis.get("periodicity_mean", 0.0)) >= periodicity_floor
        and float(analysis.get("voiced_ratio", 0.0)) >= voiced_floor
    )


def is_expected_singing_performance(analysis: Optional[dict]):
    """Stricter multi-frame gate used only after the user invited a sing-along.

    The expected-mode pass omits ASR text-density penalties so mixed turns such
    as "I'll start" + singing can recover.  Requiring periodicity, voiced ratio,
    sustained frames and either probability or stability keeps ordinary speech
    from being promoted merely because the caller expects music.
    """
    if not analysis or not analysis.get("analysis_available"):
        return False
    timeline = analysis.get("pitch_timeline_midi", []) or []
    frame_seconds = float(analysis.get("pitch_timeline_frame_seconds", 0.10))
    playable_seconds = sum(
        1 for value in timeline
        if isinstance(value, (int, float)) and float(value) > 1.0
    ) * max(0.02, frame_seconds)

    legacy_gate = (
        float(analysis.get("duration", 0.0)) >= 2.5
        and float(analysis.get("periodicity_mean", 0.0)) >= 0.58
        and float(analysis.get("voiced_ratio", 0.0)) >= 0.32
        and float(analysis.get("sustained_ratio", 0.0)) >= 0.16
        and (
            float(analysis.get("singing_probability", 0.0)) >= 0.43
            or float(analysis.get("pitch_stability", 0.0)) >= 0.56
        )
    )
    # Recovery gate for a long clip captured while sing-along is explicitly
    # armed. This covers lyrical performances whose consonants reduce the
    # sustained-frame score (the observed failure was p=.39/stability=.56),
    # while the conjunction and playable-duration requirement keep ordinary
    # spoken commands out. Fast lyrical passages often have fewer long held
    # frames; the July 22 miss was p=.36/stability=.53/sustained=.11, with
    # strong voiced coverage, so expected mode accepts sustained >= .10.
    conservative_recovery = (
        float(analysis.get("duration", 0.0)) >= 4.0
        and float(analysis.get("singing_probability", 0.0)) >= 0.35
        and float(analysis.get("pitch_stability", 0.0)) >= 0.52
        and float(analysis.get("voiced_ratio", 0.0)) >= 0.45
        and float(analysis.get("sustained_ratio", 0.0)) >= 0.10
        and playable_seconds >= 1.2
    )
    return legacy_gate or conservative_recovery


def describe_expected_singing_gate(analysis: Optional[dict]):
    """Compact reason string for tuning expected-performance false negatives."""
    if not analysis or not analysis.get("analysis_available"):
        return "analysis-unavailable"
    timeline = analysis.get("pitch_timeline_midi", []) or []
    frame_seconds = max(
        0.02, float(analysis.get("pitch_timeline_frame_seconds", 0.10))
    )
    playable_seconds = sum(
        1 for value in timeline
        if isinstance(value, (int, float)) and float(value) > 1.0
    ) * frame_seconds
    values = {
        "duration": float(analysis.get("duration", 0.0)),
        "prob": float(analysis.get("singing_probability", 0.0)),
        "stability": float(analysis.get("pitch_stability", 0.0)),
        "voiced": float(analysis.get("voiced_ratio", 0.0)),
        "sustained": float(analysis.get("sustained_ratio", 0.0)),
        "playable": playable_seconds,
    }
    floors = {
        "duration": 4.0,
        "prob": 0.35,
        "stability": 0.52,
        "voiced": 0.45,
        "sustained": 0.10,
        "playable": 1.2,
    }
    failed = [name for name, floor in floors.items() if values[name] < floor]
    return (
        f"duration={values['duration']:.2f}/4.00 "
        f"prob={values['prob']:.2f}/0.35 "
        f"stability={values['stability']:.2f}/0.52 "
        f"voiced={values['voiced']:.2f}/0.45 "
        f"sustained={values['sustained']:.2f}/0.10 "
        f"playable={values['playable']:.2f}/1.20 "
        f"failed={','.join(failed) if failed else 'none'}"
    )


# ------------------------------ 音频 / VAD ------------------------------


def decode_wav(data: bytes):
    """把上传的 WAV 统一成 16kHz mono float32。"""
    wav, sr = sf.read(io.BytesIO(data))
    if wav.ndim > 1:
        wav = wav.mean(axis=1)
    if sr != 16000:
        from scipy.signal import resample_poly

        g = np.gcd(sr, 16000)
        wav = resample_poly(wav, 16000 // g, sr // g)
    return wav.astype(np.float32)


def decode_audio_path(path: str):
    wav, sr = sf.read(path)
    if wav.ndim > 1:
        wav = wav.mean(axis=1)
    if sr != 16000:
        from scipy.signal import resample_poly

        g = np.gcd(sr, 16000)
        wav = resample_poly(wav, 16000 // g, sr // g)
    return wav.astype(np.float32)


def run_vad(wav: np.ndarray, min_speech_ms: Optional[int] = None):
    """返回是否包含足够长的人声、有效人声毫秒数和人声区间。"""
    if _vad_model is None or wav.size == 0:
        return False, 0, [], 0.0

    threshold_ms = _vad_min_speech_ms if min_speech_ms is None else min_speech_ms
    t0 = time.time()
    result = _vad_model.generate(input=wav, cache={}, disable_pbar=True)
    elapsed = time.time() - t0

    raw_segments = []
    if result and isinstance(result[0], dict):
        raw_segments = result[0].get("value", []) or []

    # 某些 FunASR 版本会多包一层 list，兼容两种返回格式。
    if (
        len(raw_segments) == 1
        and isinstance(raw_segments[0], list)
        and raw_segments[0]
        and isinstance(raw_segments[0][0], list)
    ):
        raw_segments = raw_segments[0]

    duration_ms = int(round(wav.size * 1000.0 / 16000.0))
    segments = []
    speech_ms = 0
    for item in raw_segments:
        if not isinstance(item, (list, tuple)) or len(item) < 2:
            continue
        begin = max(0, min(duration_ms, int(item[0])))
        end = max(begin, min(duration_ms, int(item[1])))
        if end <= begin:
            continue
        segments.append([begin, end])
        speech_ms += end - begin

    return speech_ms >= max(1, int(threshold_ms)), speech_ms, segments, elapsed


def trim_to_speech(wav: np.ndarray, segments, margin_ms: int = 120):
    """去掉首尾大段静音，同时保留说话中间的自然停顿。"""
    return trim_to_speech_with_offset(wav, segments, margin_ms)[0]


def trim_to_speech_with_offset(wav: np.ndarray, segments, margin_ms: int = 120):
    """Trim outer silence and return the start offset in the uploaded WAV."""
    if not segments:
        return wav, 0.0
    begin_ms = max(0, segments[0][0] - margin_ms)
    end_ms = min(int(wav.size * 1000 / 16000), segments[-1][1] + margin_ms)
    begin = int(begin_ms * 16)
    end = int(end_ms * 16)
    if end <= begin:
        return wav, 0.0
    return wav[begin:end], begin / 16000.0


def extract_speaker_embedding(wav: np.ndarray):
    """Extract a normalized 192-D CAM++ embedding."""
    if _speaker_model is None or wav.size < 16000 * 0.6:
        return None, 0.0
    t0 = time.time()
    result = _speaker_model.generate(input=wav, cache={}, disable_pbar=True)
    elapsed = time.time() - t0
    if not result or not isinstance(result[0], dict):
        return None, elapsed
    embedding = result[0].get("spk_embedding")
    if embedding is None:
        return None, elapsed
    if hasattr(embedding, "detach"):
        embedding = embedding.detach().cpu().numpy()
    vector = np.asarray(embedding, dtype=np.float32).reshape(-1)
    norm = float(np.linalg.norm(vector))
    if vector.size == 0 or not np.all(np.isfinite(vector)) or norm < 1e-8:
        return None, elapsed
    return vector / norm, elapsed


def unknown_speaker_meta():
    return {
        "speaker_id": "unknown",
        "speaker_name": "无法确认的说话人",
        "speaker_kind": "unknown",
        "speaker_status": "unknown",
        "speaker_confidence": 0.0,
        "speaker_self_confidence": 0.0,
        "speaker_is_new": False,
        "speaker_persistent": False,
        "speaker_enrollment_progress": 0.0,
    }


def identify_speaker(wav: np.ndarray, speech_ms: int, learn: bool = True):
    if _speaker_store is None:
        return unknown_speaker_meta(), 0.0
    embedding, elapsed = extract_speaker_embedding(wav)
    if embedding is None:
        return unknown_speaker_meta(), elapsed
    identity = (
        _speaker_store.identify_and_learn(embedding, speech_ms)
        if learn
        else _speaker_store.identify_only(embedding, speech_ms)
    )
    return identity, elapsed


# ------------------------------ FastAPI ------------------------------

app = FastAPI(title="SenseVoiceSmall ASR Server")


@app.get("/health")
def health():
    return {
        "ok": _model is not None and _vad_model is not None and _speaker_model is not None,
        "asr_ok": _model is not None,
        "vad_ok": _vad_model is not None,
        "speaker_ok": _speaker_model is not None and _speaker_store is not None,
        "speaker_profiles": len(_speaker_store.list_profiles(False)) if _speaker_store else 0,
        "streaming_preview": True,
        "singing_analysis": _singing_analyzer is not None,
        "song_search": _song_search_engine is not None,
        "local_song_catalog": _song_search_engine.catalog_count if _song_search_engine else 0,
        "external_audio_upload": False,
    }


@app.websocket("/stream/asr")
async def stream_asr(websocket: WebSocket):
    """接收 PCM16/16k/mono 音频帧并周期性推送可回滚的 partial。

    协议：
      client text  {"event":"start","language":"auto",...}
      client bytes PCM16 little-endian
      client text  {"event":"stop"} 或 {"event":"cancel"}

      server json  {"event":"partial","text":...,"stable_text":...}

    这是低显存设备上的滚动推理后端。Unity 只依赖这个协议；将来替换为
    真正的流式模型时，上层临时理解/草稿逻辑无需再改。
    """
    await websocket.accept()
    if _model is None:
        await websocket.send_json({"event": "error", "error": "model not loaded"})
        await websocket.close(code=1011)
        return

    language = "auto"
    partial_interval_ms = 850
    min_audio_ms = 800
    max_audio_ms = 30000
    audio = bytearray()
    last_inferred_bytes = 0
    last_text = ""
    started = False

    try:
        while True:
            message = await websocket.receive()
            if message.get("type") == "websocket.disconnect":
                break

            text_message = message.get("text")
            if text_message is not None:
                try:
                    command = json.loads(text_message)
                except json.JSONDecodeError:
                    command = {"event": text_message.lower()}

                event = str(command.get("event", "")).lower()
                if event == "start":
                    language = str(command.get("language", "auto") or "auto")
                    partial_interval_ms = max(
                        400, min(2500, int(command.get("partial_interval_ms", 850)))
                    )
                    min_audio_ms = max(
                        400, min(3000, int(command.get("min_audio_ms", 800)))
                    )
                    started = True
                    await websocket.send_json(
                        {
                            "event": "started",
                            "sample_rate": 16000,
                            "partial_interval_ms": partial_interval_ms,
                        }
                    )
                elif event in ("stop", "cancel"):
                    await websocket.send_json({"event": "stopped"})
                    break
                continue

            chunk = message.get("bytes")
            if not started or not chunk:
                continue

            # PCM16 必须是偶数字节；极少数网络分片若截在半个 sample 上就丢尾字节。
            if len(chunk) % 2:
                chunk = chunk[:-1]
            if not chunk:
                continue

            max_bytes = max_audio_ms * 16 * 2
            room = max_bytes - len(audio)
            if room > 0:
                audio.extend(chunk[:room])

            audio_ms = len(audio) // (16 * 2)
            new_audio_ms = (len(audio) - last_inferred_bytes) // (16 * 2)
            if audio_ms < min_audio_ms or new_audio_ms < partial_interval_ms:
                continue

            # 在 worker 线程里跑模型，避免阻塞 FastAPI 的 websocket event loop。
            pcm = np.frombuffer(bytes(audio), dtype="<i2").astype(np.float32) / 32768.0
            loop = asyncio.get_running_loop()
            result = await loop.run_in_executor(
                None, recognize_stream_partial, pcm, language
            )
            last_inferred_bytes = len(audio)

            current = result.get("text", "") or ""
            stable = common_prefix(last_text, current) if last_text else ""
            revision = bool(last_text and not current.startswith(last_text))
            last_text = current
            result.update(
                {
                    "event": "partial",
                    "stable_text": stable,
                    "unstable_text": current[len(stable):],
                    "revision": revision,
                    "audio_ms": audio_ms,
                }
            )
            await websocket.send_json(result)
    except WebSocketDisconnect:
        pass
    except Exception as exc:
        import traceback

        traceback.print_exc()
        try:
            await websocket.send_json({"event": "error", "error": str(exc)})
        except Exception:
            pass


@app.get("/speakers")
def speakers(include_session: bool = True):
    if _speaker_store is None:
        return JSONResponse({"error": "speaker store not loaded"}, status_code=503)
    return {
        "profile_path": _speaker_store.path,
        "profiles": _speaker_store.list_profiles(include_session),
    }


@app.post("/speakers/enroll")
async def enroll_speaker(
    audio_file: UploadFile = File(...),
    speaker_id: str = Form(...),
    display_name: str = Form(...),
    kind: str = Form("guest"),
    replace: bool = Form(False),
):
    if _speaker_model is None or _speaker_store is None:
        return JSONResponse({"error": "speaker model not loaded"}, status_code=503)
    try:
        wav = decode_wav(await audio_file.read())
        has_speech, speech_ms, segments, vad_dt = run_vad(wav, min_speech_ms=700)
        if not has_speech:
            return JSONResponse({"error": "not enough speech for enrollment"}, status_code=400)
        wav = trim_to_speech(wav, segments)
        embedding, speaker_dt = extract_speaker_embedding(wav)
        if embedding is None:
            return JSONResponse({"error": "speaker embedding failed"}, status_code=400)
        profile = _speaker_store.enroll_fixed(
            speaker_id, display_name, kind, embedding, speech_ms, replace
        )
        return {"ok": True, "profile": profile, "vad_elapsed": vad_dt, "speaker_elapsed": speaker_dt}
    except Exception as exc:
        return JSONResponse({"error": str(exc)}, status_code=400)


@app.post("/speakers/rename")
def rename_speaker(
    speaker_id: str = Form(...),
    display_name: str = Form(...),
):
    try:
        return {"ok": True, "profile": _speaker_store.rename(speaker_id, display_name)}
    except Exception as exc:
        return JSONResponse({"error": str(exc)}, status_code=400)


@app.post("/speakers/merge")
def merge_speakers(
    source_id: str = Form(...),
    target_id: str = Form(...),
):
    try:
        return {"ok": True, "profile": _speaker_store.merge(source_id, target_id)}
    except Exception as exc:
        return JSONResponse({"error": str(exc)}, status_code=400)


@app.post("/speakers/delete")
def delete_speaker(speaker_id: str = Form(...)):
    try:
        _speaker_store.delete(speaker_id)
        return {"ok": True}
    except Exception as exc:
        return JSONResponse({"error": str(exc)}, status_code=400)


@app.post("/speakers/reset-owner")
def reset_owner():
    if _speaker_store is None:
        return JSONResponse({"error": "speaker store not loaded"}, status_code=503)
    _speaker_store.reset_owner()
    return {"ok": True}


@app.post("/vad")
async def vad(
    audio_file: UploadFile = File(...),
    min_speech_ms: int = Form(160),
    speaker_check: bool = Form(False),
):
    """正式 ASR 前的短音频人声探测接口。"""
    if _vad_model is None:
        return JSONResponse({"error": "VAD model not loaded"}, status_code=503)

    try:
        wav = decode_wav(await audio_file.read())
        min_ms = max(80, min(2000, int(min_speech_ms)))
        is_speech, speech_ms, segments, elapsed = run_vad(wav, min_ms)
        singing = (
            _singing_analyzer.analyze(wav, thorough=False)
            if _singing_analyzer is not None
            else None
        )
        singing_override = not is_speech and is_tonal_vocal(singing, short_probe=True)
        if singing_override:
            is_speech = True
            duration_ms = int(round(wav.size * 1000.0 / 16000.0))
            speech_ms = max(speech_ms, int(duration_ms * float(singing.get("voiced_ratio", 0.0))))
            segments = [[0, duration_ms]]
        result = {
            "is_speech": is_speech,
            "speech_ms": speech_ms,
            "segments": segments,
            "elapsed": round(elapsed, 3),
        }
        result.update(singing_response_fields(singing, include_contour=False))
        # When speech-VAD already accepted the probe, defer the speech-vs-song
        # decision to streaming ASR where lyrics density is available.  The
        # short probe only declares singing when it was specifically needed to
        # rescue humming from speech-VAD rejection.
        result["is_singing"] = bool(singing_override)
        if speaker_check:
            speaker_meta = unknown_speaker_meta()
            speaker_dt = 0.0
            if is_speech:
                speaker_wav = trim_to_speech(wav, segments)
                speaker_meta, speaker_dt = identify_speaker(speaker_wav, speech_ms, learn=False)
            result.update(speaker_meta)
            result["speaker_elapsed"] = round(speaker_dt, 3)
            result["elapsed"] = round(elapsed + speaker_dt, 3)
        return result
    except Exception as e:
        import traceback

        traceback.print_exc()
        return JSONResponse({"error": str(e)}, status_code=500)


@app.post("/asr")
async def asr(
    audio_file: UploadFile = File(...),
    language: str = Form("auto"),
    learn_speaker: bool = Form(True),
    expect_singing: bool = Form(False),
):
    if _model is None:
        return JSONResponse({"error": "model not loaded"}, status_code=503)

    try:
        data = await audio_file.read()

        wav = decode_wav(data)

        # 第二道保险：没有足够长的人声就不运行 SenseVoice，避免噪声幻听。
        # 纯哼唱可能被 speech-VAD 拒绝，所以并行的音高门可有条件放行。
        has_speech, speech_ms, segments, vad_dt = run_vad(wav)
        quick_singing = (
            _singing_analyzer.analyze(wav, thorough=False)
            if _singing_analyzer is not None
            else None
        )
        singing_vad_override = not has_speech and is_tonal_vocal(quick_singing)
        if singing_vad_override:
            has_speech = True
            duration_ms = int(round(wav.size * 1000.0 / 16000.0))
            speech_ms = max(
                speech_ms,
                int(duration_ms * float(quick_singing.get("voiced_ratio", 0.0))),
            )
            segments = [[0, duration_ms]]
        if not has_speech:
            print(
                f"[ASR] rejected by VAD dt={vad_dt:.3f}s "
                f"speech={speech_ms}ms duration={wav.size / 16000:.2f}s"
            )
            result = {
                "text": "",
                "language": "",
                "emotion": "",
                "audio_event": "NoSpeech",
                "no_speech": True,
                "speech_ms": speech_ms,
                "vad_elapsed": round(vad_dt, 3),
                "elapsed": round(vad_dt, 3),
            }
            result.update(singing_response_fields(quick_singing))
            result.update(unknown_speaker_meta())
            return result

        wav, audio_content_start_seconds = trim_to_speech_with_offset(wav, segments)

        speaker_meta, speaker_dt = identify_speaker(wav, speech_ms, learn=learn_speaker)
        if speaker_meta.get("speaker_kind") == "ai":
            print(
                f"[ASR] rejected AI_SELF echo score={speaker_meta.get('speaker_confidence')} "
                f"speech={speech_ms}ms"
            )
            result = {
                "text": "",
                "language": "",
                "emotion": "",
                "audio_event": "SelfSpeech",
                "no_speech": True,
                "speech_ms": speech_ms,
                "vad_elapsed": round(vad_dt, 3),
                "speaker_elapsed": round(speaker_dt, 3),
                "elapsed": round(vad_dt + speaker_dt, 3),
            }
            result.update(speaker_meta)
            return result

        t0 = time.time()
        res = generate_asr(wav, language)
        dt = time.time() - t0

        raw = res[0]["text"] if res else ""
        text, lang, emotion, audio_event = parse_output(raw)
        singing = (
            _singing_analyzer.analyze(
                wav,
                lyrics=text,
                audio_event=audio_event,
                thorough=True,
            )
            if _singing_analyzer is not None
            else quick_singing
        )
        expected_singing_override = False
        # In armed sing-along mode, recover a long acoustically melodic clip
        # before the transcript-free tail fallback. Keeping the full analysis
        # preserves the beginning of the melody and its spoken-preface boundary.
        if (
            expect_singing
            and not bool((singing or {}).get("is_singing", False))
            and is_expected_singing_performance(singing)
        ):
            singing["is_singing"] = True
            singing["summary"] = "Recovered in armed sing-along mode; " + str(
                singing.get("summary", "")
            )
            expected_singing_override = True
        expected_analysis = None
        if expect_singing and _singing_analyzer is not None and not bool(
            (singing or {}).get("is_singing", False)
        ):
            # Conversation VAD intentionally keeps a spoken lead-in and the song
            # in one turn.  Re-evaluate only the last 12 seconds without using the
            # full mixed transcript as a speech-density penalty.
            expected_tail = wav[-12 * 16000 :]
            expected_analysis = _singing_analyzer.analyze(
                expected_tail,
                lyrics="",
                audio_event=audio_event,
                thorough=True,
            )
            if is_expected_singing_performance(expected_analysis):
                tail_offset = max(0.0, wav.size / 16000.0 - 12.0)
                expected_analysis["singing_start_seconds"] = tail_offset + float(
                    expected_analysis.get("singing_start_seconds", 0.0)
                )
                expected_analysis["pitch_timeline_start_seconds"] = tail_offset + float(
                    expected_analysis.get("pitch_timeline_start_seconds", 0.0)
                )
                singing = expected_analysis
                singing["is_singing"] = True
                singing["summary"] = (
                    "待唱状态下由尾部多帧音高证据确认；" +
                    str(singing.get("summary", ""))
                )
                expected_singing_override = True
        if expect_singing and not bool((singing or {}).get("is_singing", False)):
            print(
                "[SingingGate] expected performance rejected "
                f"full=({describe_expected_singing_gate(singing)}) "
                f"tail=({describe_expected_singing_gate(expected_analysis)})"
            )
        print(
            f"[ASR] dt={dt:.2f}s lang={lang} emo={emotion} evt={audio_event} "
            f"spk={speaker_meta.get('speaker_id')}({speaker_meta.get('speaker_confidence')}) "
            f"learn={learn_speaker} expect_sing={expect_singing} "
            f"sing={float((singing or {}).get('singing_probability', 0.0)):.2f} "
            f"expected_override={expected_singing_override} "
            f"text={text!r}"
        )
        result = {
            "text": text,
            "language": lang,
            "emotion": emotion,
            "audio_event": audio_event,
            "no_speech": False,
            "speech_ms": speech_ms,
            "vad_elapsed": round(vad_dt, 3),
            "speaker_elapsed": round(speaker_dt, 3),
            "elapsed": round(dt + vad_dt + speaker_dt, 3),
        }
        result.update(singing_response_fields(singing))
        result["audio_content_start_seconds"] = round(audio_content_start_seconds, 3)
        result["singing_expected"] = bool(expect_singing)
        result["singing_expected_override"] = bool(expected_singing_override)
        if singing_vad_override:
            result["is_singing"] = True
        result.update(speaker_meta)
        return result
    except Exception as e:
        import traceback

        traceback.print_exc()
        return JSONResponse({"error": str(e)}, status_code=500)


# ------------------------------ 歌曲检索 ------------------------------


@app.get("/songs/catalog")
def song_catalog_status():
    if _song_search_engine is None:
        return JSONResponse({"error": "song search engine not loaded"}, status_code=503)
    return {
        "ok": True,
        "local_song_catalog": _song_search_engine.catalog_count,
        "catalog_path": _song_search_engine.catalog_path,
        "audio_dir": _song_search_engine.audio_dir,
        "songs": _song_search_engine.catalog_summaries(),
        "external_audio_upload": False,
    }


def _resample_catalog_timeline(
    values, source_frame_seconds: float, start_seconds: float, end_seconds: float,
    target_frame_seconds: float = 0.10,
):
    timeline = np.asarray(values or [], dtype=np.float32)
    if timeline.size == 0:
        return []
    source_frame_seconds = max(0.02, float(source_frame_seconds or 0.10))
    source_duration = timeline.size * source_frame_seconds
    start_seconds = max(0.0, min(source_duration, float(start_seconds or 0.0)))
    if end_seconds <= start_seconds:
        end_seconds = source_duration
    end_seconds = max(start_seconds, min(source_duration, float(end_seconds)))
    count = max(1, int(round((end_seconds - start_seconds) / target_frame_seconds)))
    sample_times = start_seconds + np.arange(count, dtype=np.float32) * target_frame_seconds
    indices = np.clip((sample_times / source_frame_seconds).astype(np.int32), 0, timeline.size - 1)
    return timeline[indices].astype(float).tolist()


def _compose_catalog_performance(plan: dict, max_seconds: float):
    selected = plan.get("selected_references", []) or []
    if not selected:
        raise ValueError("song performance plan contains no audio")
    audio_parts = []
    pitch_timeline = []
    gap_seconds = 0.08
    gap_samples = int(round(gap_seconds * 16000))
    gap_frames = max(1, int(round(gap_seconds / 0.10)))
    for index, reference in enumerate(selected):
        path = str(reference.get("absolute_wav_path", ""))
        if not path or not os.path.isfile(path):
            raise FileNotFoundError(path or "missing managed song WAV")
        wav = decode_audio_path(path)
        full_seconds = float(wav.size) / 16000.0
        start_seconds = max(0.0, min(full_seconds, float(reference.get("slice_start_seconds", 0.0) or 0.0)))
        requested_end = float(reference.get("slice_end_seconds", 0.0) or 0.0)
        end_seconds = full_seconds if requested_end <= start_seconds else min(full_seconds, requested_end)
        start_sample = int(round(start_seconds * 16000))
        end_sample = int(round(end_seconds * 16000))
        clip = wav[start_sample:end_sample]
        if clip.size < 1600:
            continue
        if index > 0 and audio_parts:
            audio_parts.append(np.zeros(gap_samples, dtype=np.float32))
            pitch_timeline.extend([0.0] * gap_frames)
        audio_parts.append(clip.astype(np.float32))
        pitch_timeline.extend(_resample_catalog_timeline(
            reference.get("pitch_timeline_midi", []) or [],
            float(reference.get("pitch_timeline_frame_seconds", 0.10) or 0.10),
            start_seconds,
            end_seconds,
        ))
    if not audio_parts:
        raise ValueError("remembered song WAV has no playable audio")
    combined = np.concatenate(audio_parts)
    duration_seconds = float(combined.size) / 16000.0
    if duration_seconds > float(max_seconds) + 0.25:
        raise ValueError(
            f"resolved song audio is {duration_seconds:.1f}s, above the {float(max_seconds):.1f}s limit"
        )
    output = io.BytesIO()
    sf.write(output, combined, 16000, format="WAV", subtype="PCM_16")
    if not any(value > 0.0 for value in pitch_timeline) and _singing_analyzer is not None:
        analysis = _singing_analyzer.analyze(combined, lyrics="", thorough=False)
        pitch_timeline = analysis.get("pitch_timeline_midi", []) or []
    return output.getvalue(), pitch_timeline, duration_seconds


@app.post("/songs/catalog/sing")
async def sing_remembered_song(
    audio_file: Optional[UploadFile] = File(None),
    song_id: str = Form(""),
    title: str = Form(""),
    query: str = Form(""),
    mode: str = Form("memory"),
    max_seconds: float = Form(60.0),
    seed: int = Form(1234),
):
    """Resolve local remembered audio; never invent an unavailable continuation."""
    if _song_search_engine is None:
        return JSONResponse({"error": "song search engine not loaded"}, status_code=503)
    try:
        query_contour = []
        if audio_file is not None:
            audio_bytes = await audio_file.read()
            if audio_bytes and _singing_analyzer is not None:
                wav = decode_wav(audio_bytes)
                analysis = await asyncio.get_running_loop().run_in_executor(
                    None,
                    lambda: _singing_analyzer.analyze(wav, lyrics=query, thorough=True),
                )
                query_contour = analysis.get("pitch_contour_midi", []) or []

        plan = await asyncio.get_running_loop().run_in_executor(
            None,
            lambda: _song_search_engine.resolve_performance(
                song_id=song_id,
                title=title,
                mode=mode,
                query_contour=query_contour,
                query_lyrics=query,
                max_seconds=max_seconds,
                seed=seed,
            ),
        )
        wav_bytes, timeline, duration_seconds = await asyncio.get_running_loop().run_in_executor(
            None, lambda: _compose_catalog_performance(plan, max_seconds)
        )
        # Internal absolute paths are never exposed to the Unity/LLM response.
        selected = []
        for reference in plan.pop("selected_references", []):
            selected.append({
                "reference_id": str(reference.get("id", "")),
                "segment_group_id": str(reference.get("segment_group_id", "")),
                "sequence_index": int(reference.get("sequence_index", 0) or 0),
            })
        plan.update({
            "ok": True,
            "action": "sing",
            "audio_base64": base64.b64encode(wav_bytes).decode("ascii"),
            "pitch_timeline_midi": timeline,
            "pitch_timeline_frame_seconds": 0.10,
            "duration_seconds": round(duration_seconds, 3),
            "selected": selected,
            "external_audio_upload": False,
        })
        return plan
    except (ValueError, KeyError, FileNotFoundError) as exc:
        return JSONResponse({
            "ok": False,
            "action": "sing",
            "error": str(exc),
        }, status_code=400)
    except Exception as exc:
        import traceback

        traceback.print_exc()
        return JSONResponse({"ok": False, "action": "sing", "error": str(exc)}, status_code=500)


@app.post("/songs/search")
async def search_song(
    audio_file: Optional[UploadFile] = File(None),
    query: str = Form(""),
    mode: str = Form("auto"),
    max_results: int = Form(5),
):
    """Search song candidates; uploaded microphone audio remains local."""
    if _song_search_engine is None:
        return JSONResponse({"error": "song search engine not loaded"}, status_code=503)
    try:
        audio_bytes = await audio_file.read() if audio_file is not None else None
        contour = []
        if audio_bytes and _singing_analyzer is not None and mode.lower() in ("auto", "hum", "catalog"):
            wav = decode_wav(audio_bytes)
            analysis = await asyncio.get_running_loop().run_in_executor(
                None,
                lambda: _singing_analyzer.analyze(wav, lyrics=query, thorough=True),
            )
            contour = analysis.get("pitch_contour_midi", []) or []

        result = await asyncio.get_running_loop().run_in_executor(
            None,
            lambda: _song_search_engine.search(
                query=query,
                mode=mode,
                melody_contour=contour,
                max_results=max_results,
            ),
        )
        return result
    except Exception as exc:
        import traceback

        traceback.print_exc()
        return JSONResponse({"error": str(exc)}, status_code=500)


@app.post("/songs/catalog/remember")
@app.post("/songs/catalog/add")
async def add_song_reference(
    audio_file: UploadFile = File(...),
    song_id: str = Form(""),
    title: str = Form(""),
    artist: str = Form(""),
    lyrics: str = Form(""),
    aliases: str = Form(""),
    reason: str = Form(""),
):
    """Remember a local singing clip; title is intentionally optional."""
    if _song_search_engine is None:
        return JSONResponse({"error": "song search engine not loaded"}, status_code=503)
    try:
        audio_bytes = await audio_file.read()
        wav = decode_wav(audio_bytes)
        alias_list = [value.strip() for value in aliases.split("|") if value.strip()]
        result = await asyncio.get_running_loop().run_in_executor(
            None,
            lambda: _song_search_engine.remember_clip(
                title=title,
                artist=artist,
                wav=wav,
                wav_bytes=audio_bytes,
                lyrics=lyrics,
                aliases=alias_list,
                reason=reason,
                song_id=song_id,
            ),
        )
        result.update({
            "ok": True,
            "action": "remember",
            "local_song_catalog": _song_search_engine.catalog_count,
            "external_audio_upload": False,
        })
        return result
    except (ValueError, KeyError) as exc:
        return JSONResponse({"error": str(exc)}, status_code=400)
    except Exception as exc:
        import traceback

        traceback.print_exc()
        return JSONResponse({"error": str(exc)}, status_code=500)


@app.post("/songs/catalog/rename")
async def rename_song_reference(
    song_id: str = Form(...),
    title: str = Form(...),
    artist: str = Form(""),
    aliases: str = Form(""),
):
    """Rename one remembered song and its physical WAV reference files."""
    if _song_search_engine is None:
        return JSONResponse({"error": "song search engine not loaded"}, status_code=503)
    try:
        alias_list = [value.strip() for value in aliases.split("|") if value.strip()]
        result = await asyncio.get_running_loop().run_in_executor(
            None,
            lambda: _song_search_engine.rename_song(
                song_id=song_id,
                title=title,
                artist=artist,
                aliases=alias_list if aliases.strip() else None,
            ),
        )
        result.update({
            "ok": True,
            "action": "rename",
            "local_song_catalog": _song_search_engine.catalog_count,
            "external_audio_upload": False,
        })
        return result
    except (ValueError, KeyError) as exc:
        return JSONResponse({"error": str(exc)}, status_code=400)
    except Exception as exc:
        import traceback

        traceback.print_exc()
        return JSONResponse({"error": str(exc)}, status_code=500)


@app.post("/songs/catalog/forget")
async def forget_song_reference(song_id: str = Form(...)):
    """Forget one local song record and delete only its managed WAV files."""
    if _song_search_engine is None:
        return JSONResponse({"error": "song search engine not loaded"}, status_code=503)
    try:
        result = await asyncio.get_running_loop().run_in_executor(
            None,
            lambda: _song_search_engine.forget_song(song_id),
        )
        result.update({
            "ok": True,
            "action": "forget",
            "local_song_catalog": _song_search_engine.catalog_count,
            "external_audio_upload": False,
        })
        return result
    except (ValueError, KeyError) as exc:
        return JSONResponse({"error": str(exc)}, status_code=400)
    except Exception as exc:
        import traceback

        traceback.print_exc()
        return JSONResponse({"error": str(exc)}, status_code=500)


# ------------------------------ 入口 ------------------------------


def main():
    global _vad_min_speech_ms
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=9881)
    parser.add_argument(
        "--device",
        default="cuda:0",
        help="推理设备: cuda:0 / cuda:1 / cpu",
    )
    parser.add_argument(
        "--vad-device",
        default="cpu",
        help="FSMN-VAD 推理设备，默认 cpu 以节省显存",
    )
    parser.add_argument(
        "--vad-min-speech-ms",
        type=int,
        default=160,
        help="整段音频至少包含多少毫秒人声才允许进入 ASR",
    )
    parser.add_argument("--speaker-device", default="cpu")
    parser.add_argument(
        "--speaker-profile-path",
        default=os.path.join(
            os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
            "NeEEvA",
            "speaker_profiles.json",
        ),
    )
    parser.add_argument("--speaker-match-threshold", type=float, default=0.55)
    parser.add_argument("--speaker-session-threshold", type=float, default=0.48)
    parser.add_argument("--disable-auto-owner", action="store_true")
    parser.add_argument(
        "--ai-voice-wav",
        default=os.path.join("Assets", "Model", "41041.wav"),
        help="角色参考音频，用于建立锁定的 AI_SELF 声纹",
    )
    parser.add_argument(
        "--pitch-device",
        default="cpu",
        help="torchcrepe 音高推理设备；默认 cpu，避免与 ASR/TTS 抢显存",
    )
    parser.add_argument(
        "--disable-torchcrepe",
        action="store_true",
        help="只使用内置 FFT 音高跟踪器（更轻，但音高精度稍低）",
    )
    parser.add_argument(
        "--song-catalog-path",
        default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "song_catalog.json"),
        help="本地哼唱曲库 JSON 路径",
    )
    args = parser.parse_args()

    _vad_min_speech_ms = max(80, min(2000, args.vad_min_speech_ms))
    load_model(
        device=args.device,
        vad_device=args.vad_device,
        speaker_device=args.speaker_device,
        speaker_profile_path=args.speaker_profile_path,
        speaker_match_threshold=args.speaker_match_threshold,
        speaker_session_threshold=args.speaker_session_threshold,
        auto_owner_bootstrap=not args.disable_auto_owner,
        ai_voice_wav=args.ai_voice_wav,
        pitch_device=args.pitch_device,
        enable_torchcrepe=not args.disable_torchcrepe,
        song_catalog_path=args.song_catalog_path,
    )
    print(f"[SenseVoice] 监听 http://{args.host}:{args.port}")
    uvicorn.run(app, host=args.host, port=args.port, log_level="warning")


if __name__ == "__main__":
    main()
