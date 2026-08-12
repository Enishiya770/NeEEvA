"""Lightweight singing perception for NeEEvA.

The fast path uses an FFT autocorrelation pitch tracker and is suitable for
streaming/VAD hints.  The final path upgrades likely singing to torchcrepe
when it is installed.  Missing torchcrepe never prevents the ASR service from
starting; the deterministic NumPy fallback remains available.
"""

from __future__ import annotations

import math
import os
import threading
from typing import Dict, Iterable, List, Optional, Tuple

import numpy as np
from singing_score import build_singing_score


NOTE_NAMES = ("C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B")

# 排查「回哼从哪句开始/到哪句结束」时用。一次 thorough 分析只打一行，
# 排查完可用 NEEEVA_LOG_ISLAND=0 关掉。
_LOG_ISLAND = os.environ.get("NEEEVA_LOG_ISLAND", "1") != "0"

# 候选岛的取舍判据：把这座岛单独当成一段音频跑一次 analyze()，用它的
# singing_probability，而不是岛内候选窗的平均分。
#
# 8/10 用 29 段人工标注(用户逐段听音频打的起唱/唱完)量出来的：
#     岛分类准确率   候选窗均分 90%  →  岛内 prob 93%
#     起点误差中位   0.22s → 0.13s   最大 5.22s → 2.22s   超 1s 3/27 → 1/27
#     终点误差最大   13.81s → 1.85s                      超 1s 2/27 → 1/27
# 在此之前我用 LLM 给切片打标签量过一次，得到"两个判据都只有 75%/79%、完全
# 分不开"——那是错的：LLM 面对两三秒的歌词切片(「简单点。」)会压倒性地判成说话
# (31 次假阴、1 次假阳)，标签本身有偏。真值一到，结论就反过来了。
#
# 8/11 又撞上反例：三轮【说话+唱歌】被整段复读，切片核实真实起唱在 9.60s / 7.80s，
# 而系统只裁到 6.03s / 2.43s。原因是**说话岛打了 0.60 和 0.63**，擦着下限过闸。
# 这已经是这块的第五个阈值反例，按注释里定的规矩该换判据而不是继续挪数——
# 换的办法是给逐岛分析**带上该岛自己的转写**（island_transcriber），
# 让一直存在却从没喂到数据的 speech_density_penalty 生效：
# 说话字密度高扣分多，唱歌字少音长几乎不扣。同样那两座说话岛 0.60→0.16、0.63→0.24。
#
# 117 座岛（真值来自用户逐段标注）：岛分类 90% → 94%。
# 更要紧的是边界（9 段有 wav 的标注素材）：
#     均分(旧)        起点中位 0.33s 最大 5.22s  >1s 2/9
#     岛内prob 0.60   起点中位 0.38s 最大 6.53s  >1s 3/9
#     带转写          起点中位 0.22s 最大 0.43s  >1s 0/9
# 而且 0.45 / 0.50 / 0.55 三个下限**结果完全一致**——这是这块第一次出现
# "阈值随便取都一样"，说明判据本身有余量，不再是在拟合阈值。
# （对比：不带转写时窗口只有 ±0.015 宽。）
# 样本仍只有 9 段有 wav 的标注素材，其余被落盘上限挤掉了。
_ISLAND_PROB_FLOOR = 0.50
# 低于这个整段概率就不做细化：那多半是普通对话轮，而这条路径直接决定她开口的
# 延迟(实测细化每座岛 0.24s、一轮约 3.6 座，整段 analyze 从 0.89s 涨到 1.53s)。
# 0.40 与 Unity 侧模糊带下沿一致。
_ISLAND_REFINE_MIN_PROBABILITY = 0.40
# 再加一道时长闸。细化要解决的是「说话+唱歌」混合轮的起唱点，而混合轮都不短：
# 29 段标注里需要纠正边界的全部 ≥11.8s。短句要么整段说话要么整段唱，岛占比接近
# 100%，旧判据本来就对。不细化短句 = 维持已知没问题的行为，同时让模糊带里那些
# 两三秒的轮次一秒都不多花。
_ISLAND_REFINE_MIN_SECONDS = 8.0


def hz_to_midi(hz: np.ndarray) -> np.ndarray:
    hz = np.asarray(hz, dtype=np.float64)
    return 69.0 + 12.0 * np.log2(np.maximum(hz, 1e-6) / 440.0)


def midi_to_note(value: float) -> str:
    if not np.isfinite(value):
        return ""
    note = int(round(float(value)))
    octave = note // 12 - 1
    return f"{NOTE_NAMES[note % 12]}{octave}"


def _clamp01(value: float) -> float:
    return float(max(0.0, min(1.0, value)))


def _median_filter(values: np.ndarray, width: int = 5) -> np.ndarray:
    values = np.asarray(values, dtype=np.float32)
    if values.size < 3:
        return values.copy()
    width = max(3, int(width) | 1)
    radius = width // 2
    padded = np.pad(values, (radius, radius), mode="edge")
    windows = np.lib.stride_tricks.sliding_window_view(padded, width)
    return np.median(windows, axis=-1).astype(np.float32)


class SingingAnalyzer:
    """Extract pitch/melody features and estimate whether a clip is singing."""

    sample_rate = 16000

    def __init__(
        self,
        device: str = "cpu",
        enable_torchcrepe: bool = True,
        singing_threshold: float = 0.58,
    ):
        self.device = device
        self.enable_torchcrepe = bool(enable_torchcrepe)
        self.singing_threshold = float(singing_threshold)
        self._crepe = None
        self._crepe_checked = False
        self._crepe_lock = threading.Lock()
        # 细化候选岛时会对切片再调一次 analyze()，用这个标志挡住无限递归。
        self._nested_island_probe = False
        # 可选：f(wav) -> str，把一小段音频转写成文字。由服务端注入(它才有 ASR)。
        # 给了之后逐岛打分会带上该岛自己的转写，speech_density_penalty 随之生效——
        # 那是把"说得快"和"唱得慢"分开的关键，见 _ISLAND_PROB_FLOOR 的说明。
        self.island_transcriber = None
        # 音频指纹 → (已累计次数, 平均后的周期性)。同一段音频被重复分析时用来降方差，
        # 见 _track_crepe 里的说明。只保留最近几段。
        self._periodicity_history: Dict[tuple, Tuple[int, np.ndarray]] = {}

    @property
    def torchcrepe_available(self) -> bool:
        return self._load_torchcrepe() is not None

    def _load_torchcrepe(self):
        if not self.enable_torchcrepe:
            return None
        if self._crepe_checked:
            return self._crepe
        with self._crepe_lock:
            if self._crepe_checked:
                return self._crepe
            self._crepe_checked = True
            try:
                import torchcrepe  # type: ignore

                self._crepe = torchcrepe
                print(f"[Singing] torchcrepe ready (device={self.device})")
            except Exception as exc:
                self._crepe = None
                print(f"[Singing] torchcrepe unavailable, using FFT fallback: {exc}")
        return self._crepe

    def analyze(
        self,
        wav: np.ndarray,
        lyrics: str = "",
        audio_event: str = "",
        thorough: bool = False,
        language: str = "",
        force_score: bool = False,
    ) -> Dict:
        signal = np.asarray(wav, dtype=np.float32).reshape(-1)
        if signal.size > self.sample_rate * 45:
            signal = signal[-self.sample_rate * 45 :]

        duration = signal.size / float(self.sample_rate)
        if duration < 0.35 or signal.size == 0:
            return self._empty(duration)

        # Remove DC and scale only enough to make the tracker insensitive to
        # microphone gain.  Do not hard-normalize silence into a loud signal.
        signal = signal - float(np.mean(signal))
        rms = float(np.sqrt(np.mean(signal * signal) + 1e-12))
        if rms < 2e-4:
            return self._empty(duration)
        tracker_signal = np.clip(signal / max(rms * 8.0, 1.0), -1.0, 1.0)

        pitch, periodicity, hop_seconds = self._track_fft(tracker_signal)
        result = self._summarize(
            pitch,
            periodicity,
            hop_seconds,
            duration,
            lyrics,
            audio_event,
            backend="fft-autocorrelation",
            signal=signal,
            language=language,
            include_score=thorough,
            force_score=force_score,
        )

        # Normal speech stays on the cheap path.  A likely melodic clip gets a
        # more accurate final pass; this avoids adding CREPE latency to every
        # conversational utterance.
        should_upgrade = thorough and (
            result["singing_probability"] >= 0.42
            or str(audio_event).lower() == "bgm"
            or (result["periodicity_mean"] >= 0.68 and result["voiced_ratio"] >= 0.45)
        )
        if should_upgrade:
            try:
                # 必须和 FFT 那一遍喂同一个信号。原来这里传的是未归一化的 signal，
                # 而 FFT 拿的是 tracker_signal(除以 rms*8 后裁剪)，两者算出的周期性
                # 量纲不同——同一段尾部说话，FFT 那遍打 0.61、crepe 那遍打 0.81，
                # 而岛的分数下限 0.78 对两者一视同仁。
                crepe_result = self._track_crepe(tracker_signal)
                if crepe_result is not None:
                    pitch, periodicity, hop_seconds = crepe_result
                    result = self._summarize(
                        pitch,
                        periodicity,
                        hop_seconds,
                        duration,
                        lyrics,
                        audio_event,
                        backend="torchcrepe-tiny",
                        signal=signal,
                        language=language,
                        include_score=thorough,
                        force_score=force_score,
                        # 只有 crepe 这一遍做候选岛细化：FFT 那一遍的结果马上就被
                        # 覆盖，白花时间；嵌套调用里也必须关掉，否则无限递归。
                        island_signal=None if self._nested_island_probe else signal,
                    )
            except Exception as exc:
                # A CUDA/driver mismatch must not break ASR.  Keep the already
                # computed fallback result and make the degradation explicit.
                result["pitch_backend"] = "fft-autocorrelation"
                result["pitch_warning"] = str(exc)[:160]

        return result

    def _track_fft(self, signal: np.ndarray) -> Tuple[np.ndarray, np.ndarray, float]:
        frame_length = 640  # 40 ms
        hop = 160  # 10 ms
        if signal.size < frame_length:
            signal = np.pad(signal, (0, frame_length - signal.size))

        frames = np.lib.stride_tricks.sliding_window_view(signal, frame_length)[::hop]
        if frames.shape[0] > 4500:
            frames = frames[-4500:]
        window = np.hanning(frame_length).astype(np.float32)
        framed = frames.astype(np.float32, copy=False) * window
        energy = np.sqrt(np.mean(framed * framed, axis=1) + 1e-12)

        spectrum = np.fft.rfft(framed, n=1024, axis=1)
        autocorr = np.fft.irfft(spectrum * np.conjugate(spectrum), n=1024, axis=1)
        autocorr = autocorr[:, :frame_length]

        min_lag = max(2, int(self.sample_rate / 900.0))
        max_lag = min(frame_length - 2, int(self.sample_rate / 55.0))
        search = autocorr[:, min_lag : max_lag + 1]
        best_rel = np.argmax(search, axis=1)
        best_lag = best_rel + min_lag
        rows = np.arange(search.shape[0])
        peak = autocorr[rows, best_lag]
        periodicity = peak / np.maximum(autocorr[:, 0], 1e-9)

        # Parabolic peak interpolation reduces note jitter without a costly
        # second tracker pass.
        left = autocorr[rows, np.maximum(best_lag - 1, 0)]
        right = autocorr[rows, np.minimum(best_lag + 1, frame_length - 1)]
        denom = left - 2.0 * peak + right
        offset = np.where(np.abs(denom) > 1e-8, 0.5 * (left - right) / denom, 0.0)
        offset = np.clip(offset, -1.0, 1.0)
        pitch = self.sample_rate / np.maximum(best_lag.astype(np.float32) + offset, 1.0)

        energy_floor = max(1e-4, float(np.percentile(energy, 25)) * 0.8)
        silent = energy < energy_floor
        periodicity = np.clip(periodicity, 0.0, 1.0).astype(np.float32)
        periodicity[silent] = 0.0
        pitch = pitch.astype(np.float32)
        pitch[silent] = 0.0
        return pitch, periodicity, hop / float(self.sample_rate)

    @staticmethod
    def _signal_key(signal: np.ndarray) -> tuple:
        """给同一段音频一个廉价指纹。长度 + 首尾/中段抽样的字节哈希即可——
        我们要区分的是"同一轮被重复分析"和"不同的音频"，不需要抗碰撞。"""
        raw = signal.tobytes()
        head = raw[:4096]
        tail = raw[-4096:]
        middle = raw[len(raw) // 2: len(raw) // 2 + 4096]
        return (signal.size, hash(head), hash(middle), hash(tail))

    def _track_crepe(self, signal: np.ndarray):
        torchcrepe = self._load_torchcrepe()
        if torchcrepe is None:
            return None
        import torch

        audio = torch.from_numpy(signal.astype(np.float32)).unsqueeze(0)
        with self._crepe_lock:
            pitch, periodicity = torchcrepe.predict(
                audio,
                self.sample_rate,
                160,
                55.0,
                900.0,
                model="tiny",
                batch_size=1024,
                device=self.device,
                return_periodicity=True,
            )
        pitch = pitch.squeeze(0).detach().cpu().numpy().astype(np.float32)
        periodicity = periodicity.squeeze(0).detach().cpu().numpy().astype(np.float32)

        # torchcrepe 对同一段音频不是确定性的：实测同一个 wav 连跑三次，
        # 整段 prob = 0.7380 / 0.7360 / 0.7550(voiced_ratio 三次相同，抖动全在周期性)。
        # 岛的分数只在十几个窗口上平均，抖动被放大——8/9 实测同一段尾部说话三次分别
        # 打了 0.69 / 0.66 / 0.78，而下限就在 0.78，最后那次擦线通过，多留了 1.7s 说话。
        #
        # 而同一段音频本来就会被分析多次(推测 ASR 与最终 ASR 各一次，日志里可见两条
        # 完全相同的 dur)。把历次周期性做平均，方差按 √n 下降，成本为零。
        key = self._signal_key(signal)
        with self._crepe_lock:
            previous = self._periodicity_history.get(key)
            if previous is not None and previous[1].shape == periodicity.shape:
                count = previous[0] + 1
                periodicity = (previous[1] * previous[0] + periodicity) / count
                self._periodicity_history[key] = (count, periodicity)
            else:
                self._periodicity_history[key] = (1, periodicity)
                count = 1
            # 只保留最近若干段，防止长会话把内存吃光
            while len(self._periodicity_history) > 8:
                self._periodicity_history.pop(next(iter(self._periodicity_history)))
        if count > 1 and _LOG_ISLAND:
            print(f"[Island] 周期性取 {count} 次分析的平均(同一段音频重复分析)",
                  flush=True)
        return pitch, periodicity, 0.01

    def _make_island_prober(self, signal: np.ndarray):
        """→ f(起, 止) 返回把这一小段单独当成一段音频分析得到的 singing_probability。

        `_nested_island_probe` 挡住递归：这里再调 analyze 会又走到 _summarize，
        若不关掉细化就会无限套下去。
        """
        def probe(start_seconds: float, end_seconds: float) -> float:
            lo = max(0, int(start_seconds * self.sample_rate))
            hi = min(signal.size, int(end_seconds * self.sample_rate))
            if hi - lo < int(1.2 * self.sample_rate):
                return float("nan")          # 太短，分析不可靠，交回给旧判据
            piece = signal[lo:hi]
            # 带上这一小段自己的转写。没有它就没有 speech_density_penalty，
            # 说话和唱的分数会挤在一起（实测说话 0.60/0.63 擦着下限过闸）。
            lyrics = ""
            if self.island_transcriber is not None:
                try:
                    lyrics = self.island_transcriber(piece) or ""
                except Exception:
                    lyrics = ""
            self._nested_island_probe = True
            try:
                got = self.analyze(
                    piece, lyrics=lyrics, thorough=True, force_score=True)
            except Exception:
                return float("nan")
            finally:
                self._nested_island_probe = False
            return float(got.get("singing_probability", float("nan")))

        return probe

    def _summarize(
        self,
        pitch: np.ndarray,
        periodicity: np.ndarray,
        hop_seconds: float,
        duration: float,
        lyrics: str,
        audio_event: str,
        backend: str,
        signal: np.ndarray,
        language: str,
        include_score: bool,
        force_score: bool,
        island_signal: Optional[np.ndarray] = None,
    ) -> Dict:
        pitch = np.asarray(pitch, dtype=np.float32).reshape(-1)
        periodicity = np.asarray(periodicity, dtype=np.float32).reshape(-1)
        count = min(pitch.size, periodicity.size)
        pitch = pitch[:count]
        periodicity = periodicity[:count]
        voiced = (pitch >= 55.0) & (pitch <= 900.0) & (periodicity >= 0.42)

        if not np.any(voiced):
            result = self._empty(duration)
            result["pitch_backend"] = backend
            return result

        midi = np.full(count, np.nan, dtype=np.float32)
        midi[voiced] = hz_to_midi(pitch[voiced]).astype(np.float32)
        voiced_midi = midi[voiced]
        low = float(np.percentile(voiced_midi, 5))
        high = float(np.percentile(voiced_midi, 95))
        median = float(np.median(voiced_midi))
        voiced_ratio = float(np.mean(voiced))
        periodicity_mean = float(np.mean(periodicity[voiced]))

        # Interpolate only for local shape statistics.  Original voiced masks
        # remain authoritative for voiced ratio and pitch range.
        idx = np.arange(count)
        valid_idx = idx[voiced]
        interpolated = np.interp(idx, valid_idx, voiced_midi).astype(np.float32)
        smoothed = _median_filter(interpolated, 5)
        delta = np.abs(np.diff(smoothed))
        continuity = _clamp01(1.0 - float(np.median(delta)) / 0.9) if delta.size else 0.0

        window_frames = max(5, int(round(0.28 / max(hop_seconds, 1e-3))))
        window_frames = min(window_frames, max(5, count))
        if count >= window_frames:
            pitch_windows = np.lib.stride_tricks.sliding_window_view(smoothed, window_frames)
            voiced_windows = np.lib.stride_tricks.sliding_window_view(voiced.astype(np.float32), window_frames)
            local_std = np.std(pitch_windows, axis=1)
            local_voiced = np.mean(voiced_windows, axis=1)
            sustained_ratio = float(np.mean((local_std <= 0.75) & (local_voiced >= 0.70)))
        else:
            sustained_ratio = continuity * voiced_ratio

        stability = _clamp01(0.55 * continuity + 0.45 * sustained_ratio)
        text_chars = len("".join(ch for ch in (lyrics or "") if not ch.isspace()))
        chars_per_sec = text_chars / max(duration, 0.1)
        slow_lyrics = 0.5 if text_chars == 0 else _clamp01((5.0 - chars_per_sec) / 3.2)
        duration_score = _clamp01((duration - 0.45) / 2.0)
        periodicity_score = _clamp01((periodicity_mean - 0.38) / 0.45)
        voiced_score = _clamp01((voiced_ratio - 0.18) / 0.62)
        range_score = _clamp01((high - low) / 8.0)
        bgm_boost = 0.07 if str(audio_event).lower() == "bgm" else 0.0

        contour = self._build_contour(smoothed, voiced, hop_seconds)
        timeline_frame_seconds = 0.10
        timeline = self._build_timeline(
            smoothed,
            voiced,
            hop_seconds,
            timeline_frame_seconds,
        )
        timeline_bucket = max(
            1, int(round(timeline_frame_seconds / max(hop_seconds, 1e-3)))
        )
        full_timeline_frames = int(math.ceil(smoothed.size / timeline_bucket))
        timeline_start_seconds = max(
            0.0,
            (full_timeline_frames - len(timeline)) * timeline_frame_seconds,
        )
        note_sequence = self._note_sequence(contour)
        rounded_notes = [int(round(value)) for value in contour]
        note_changes = sum(
            1 for index in range(1, len(rounded_notes))
            if rounded_notes[index] != rounded_notes[index - 1]
        )
        note_change_rate = note_changes / max(duration, 0.1)

        # Fluent speech is also periodic, but it usually contains more symbols
        # per second and continuously churns through pitch bins.  These two
        # penalties prevent clean, expressive speech from being mistaken for
        # singing while preserving slow lyrics and melisma.
        speech_density_penalty = (
            0.0 if text_chars == 0 else 0.24 * _clamp01((chars_per_sec - 2.5) / 2.0)
        )
        pitch_churn_penalty = 0.16 * _clamp01((note_change_rate - 2.5) / 3.0)

        probability = _clamp01(
            0.25 * periodicity_score
            + 0.21 * sustained_ratio
            + 0.16 * voiced_score
            + 0.14 * continuity
            + 0.10 * duration_score
            + 0.07 * slow_lyrics
            + 0.07 * range_score
            + bgm_boost
            - speech_density_penalty
            - pitch_churn_penalty
        )
        # 边界要在 probability 之后算：细化那一层(每座候选岛单独跑一次 analyze)
        # 只在这一轮确实像唱歌时才做，普通对话轮一秒都不多花——那条路径直接决定
        # 她开口的延迟。8/10 实测每座岛 0.24s、一轮约 3.6 座，合计约 +0.9s。
        refine_islands = (
            island_signal is not None
            and duration >= _ISLAND_REFINE_MIN_SECONDS
            and (probability >= _ISLAND_REFINE_MIN_PROBABILITY or force_score)
        )
        singing_start_seconds, singing_end_seconds = self._estimate_singing_start(
            smoothed,
            voiced,
            periodicity,
            hop_seconds,
            duration,
            self._make_island_prober(island_signal) if refine_islands else None,
        )

        low_hz = float(440.0 * (2.0 ** ((low - 69.0) / 12.0)))
        high_hz = float(440.0 * (2.0 ** ((high - 69.0) / 12.0)))
        median_hz = float(440.0 * (2.0 ** ((median - 69.0) / 12.0)))

        result = {
            "analysis_available": True,
            "is_singing": probability >= self.singing_threshold,
            "singing_probability": round(probability, 3),
            "duration": round(duration, 3),
            "pitch_backend": backend,
            "voiced_ratio": round(voiced_ratio, 3),
            "periodicity_mean": round(periodicity_mean, 3),
            "pitch_stability": round(stability, 3),
            "sustained_ratio": round(sustained_ratio, 3),
            "pitch_min_hz": round(low_hz, 2),
            "pitch_max_hz": round(high_hz, 2),
            "pitch_median_hz": round(median_hz, 2),
            "pitch_low_note": midi_to_note(low),
            "pitch_high_note": midi_to_note(high),
            "pitch_median_note": midi_to_note(median),
            "pitch_contour_midi": contour,
            # Recognition contour intentionally skips rests so melody-DTW can
            # tolerate phrasing differences.  The timeline retains zero-valued
            # rests at a fixed cadence and is therefore suitable for replay.
            "pitch_timeline_midi": timeline,
            "pitch_timeline_frame_seconds": timeline_frame_seconds,
            # The boundary is acoustic rather than transcript based.  It lets
            # the Unity client discard a short spoken preface before saving or
            # voice-converting a sing-along, while retaining a small breath/
            # attack pre-roll at the first sung phrase.
            "singing_start_seconds": round(singing_start_seconds, 3),
            "singing_end_seconds": round(singing_end_seconds, 3),
            "pitch_timeline_start_seconds": round(timeline_start_seconds, 3),
            "note_sequence": note_sequence,
            "note_change_rate": round(note_change_rate, 3),
            "lyrics_chars_per_sec": round(chars_per_sec, 3),
        }
        result["summary"] = self._summary(result)
        if include_score and (
            force_score
            or probability >= 0.30
            or str(audio_event).lower() == "bgm"
        ):
            result["singing_score"] = build_singing_score(
                pitch,
                periodicity,
                hop_seconds,
                duration,
                lyrics=lyrics,
                language=language,
                signal=signal,
                sample_rate=self.sample_rate,
                extractor_backend=backend,
                confidence=probability,
            )
        return result

    @staticmethod
    def _estimate_singing_start(
        smoothed_midi: np.ndarray,
        voiced: np.ndarray,
        periodicity: np.ndarray,
        hop_seconds: float,
        duration: float,
        island_prob=None,
    ) -> Tuple[float, float]:
        """Locate the sustained melodic region in a mixed utterance.

        ``island_prob(start, end) -> float`` 可选：给出把某座候选岛单独分析得到的
        singing_probability。给了就用它取舍候选岛（更准，代价是每座岛一次分析），
        没给就退回候选窗均分那套。返回 NaN 表示这座岛测不了，该岛退回旧判据。

        Returns ``(start, end)`` in seconds.  ``(0.0, duration)`` is the
        conservative fallback: failure to find a boundary must never cut away
        real singing.

        A single clean spoken vowel can look tonal, so the detector scores
        overlapping 1.1 s windows and then chooses a long run of windows rather
        than reacting to one frame.  Returning zero is the conservative
        fallback: failure to find a boundary must never cut away real singing.
        """
        count = min(smoothed_midi.size, voiced.size, periodicity.size)
        if count <= 0 or duration < 2.0:
            return 0.0, float(duration)

        hop = max(float(hop_seconds), 1e-3)
        window = max(8, int(round(1.10 / hop)))
        stride = max(1, int(round(0.10 / hop)))
        if count < window:
            return 0.0, float(duration)

        candidates: List[Tuple[int, float]] = []
        for start in range(0, count - window + 1, stride):
            end = start + window
            local_voiced_mask = voiced[start:end]
            voiced_ratio = float(np.mean(local_voiced_mask))
            if voiced_ratio < 0.48:
                continue

            local_periodicity = periodicity[start:end][local_voiced_mask]
            periodicity_mean = (
                float(np.mean(local_periodicity)) if local_periodicity.size else 0.0
            )
            local_pitch = smoothed_midi[start:end]
            local_delta = np.abs(np.diff(local_pitch))
            median_delta = float(np.median(local_delta)) if local_delta.size else 9.0
            continuity = _clamp01(1.0 - median_delta / 1.15)

            # This threshold intentionally sits below the final singing gate:
            # its job is only to find the boundary after the whole clip has
            # already been accepted as an expected singing performance.
            score = (
                0.42 * voiced_ratio
                + 0.38 * _clamp01((periodicity_mean - 0.40) / 0.38)
                + 0.20 * continuity
            )
            if periodicity_mean >= 0.53 and continuity >= 0.24 and score >= 0.54:
                candidates.append((start, score))

        if not candidates:
            return 0.0, float(duration)

        # Merge neighbouring melodic windows, tolerating consonants and short
        # breaths.  A spoken preface may create a tiny candidate island; the
        # longest sustained island is normally the actual sung phrase.
        max_gap_frames = max(stride, int(round(0.45 / hop)))
        runs: List[Tuple[int, int, float, int]] = []
        run_start = candidates[0][0]
        run_end = run_start + window
        score_sum = candidates[0][1]
        score_count = 1
        previous = candidates[0][0]
        for start, score in candidates[1:]:
            if start - previous <= max_gap_frames:
                run_end = max(run_end, start + window)
                score_sum += score
                score_count += 1
            else:
                runs.append((run_start, run_end, score_sum, score_count))
                run_start = start
                run_end = start + window
                score_sum = score
                score_count = 1
            previous = start
        runs.append((run_start, run_end, score_sum, score_count))

        viable = [
            run for run in runs
            if (run[1] - run[0]) * hop >= 1.65 and run[3] >= 4
        ]
        if not viable:
            return 0.0, float(duration)

        best = max(
            viable,
            key=lambda run: (
                (run[1] - run[0]) * (0.75 + 0.25 * (run[2] / run[3])),
                -run[0],
            ),
        )

        # 一首歌里的换气会把同一段演唱切成好几座岛，只取一座就会丢掉半句。但「合格」
        # (>=1.65s 且 >=4 个窗口) 挡不住说话——一句连贯的口语照样能凑出一座长岛。
        # 8/7 的实测把这两件事分得很干净，靠的是分数而不是长度:
        #     runs=[2.00-8.10/0.91* 8.10-11.20/0.87* 10.80-14.50/0.88* 14.60-17.40/0.71*]
        # 前三座是同一段演唱，第四座是唱完接的说话。长度上第四座并不短(2.8s)，
        # 分数上却差了 0.16。同一条日志里另一轮 best 甚至直接选中了说话那座
        # (14.40-21.20/0.66，因为它最长)，把整段演唱丢在外面。
        #
        # 所以: 先按分数筛掉不像唱的岛，再以幸存者里最长的那座为锚向两侧合并。
        # 与 top 取小是为了只有一座低分岛时不会把它也筛掉。
        #
        # 原来还叠了一个 top-0.10 的相对项，它只会让门槛更严：8/9 实测一段
        # runs=[…4.40-11.50/0.83 11.40-14.40/0.84 14.10-17.80/0.95]，因为 top=0.95
        # 把下限抬到 0.85，两座真岛被筛掉，起唱点从 4.40s 跳到 14.43s。
        # 遇到特别干净的一段反而更容易切错，所以去掉相对项。
        #
        # 下限原为 0.78，依据是「唱 0.83~0.95 / 说 0.61~0.74，两簇之间是空的」。
        # 8/9 实测打破了这个前提：「啊，我先唱这一首吧，嗯」——开头长「啊」、结尾
        # 长「嗯」，全是持续元音——组成一座 0.00-2.30s 的岛，均分 0.786，比她真正
        # 起唱那几秒(0.555/0.631/0.677)还高。它以 0.006 之差过闸，进而获得桥接资格
        # (与歌声之间未覆盖间隔只有 0.20s，远小于 1.20s 容忍度)，把 span 拉到 0，
        # 最后被 <0.45 那道保护抹成 0.00 —— 整段说话被当成歌回哼了出去。
        # 抬到 0.80 后：手上 21 段素材只有这一段的边界变了(0.00s → 2.83s，与流式
        # 锚点 2.82s 一致)，全语料里均分落在 [0.78,0.80) 的合格岛也只有它那一座。
        # 余量很薄(同段真歌声最低 0.825)——8/10 果然又撞上反例(说话 0.809 / 唱 0.733)，
        # 于是换判据：有 island_prob 时改用「把这座岛单独分析一次」的概率，
        # 均分只作为拿不到概率时的退路。依据见文件顶部 _ISLAND_PROB_FLOOR。
        mean_scores = [run[2] / max(1, run[3]) for run in viable]
        scores, floor_used, probed = mean_scores, 0.80, False
        if island_prob is not None:
            measured = [island_prob(run[0] * hop, run[1] * hop) for run in viable]
            # 全部测到才换判据。只测到一部分时两种分数量纲不同(均分 0.6~0.9 /
            # 概率 0.3~0.8)，混在一起比大小是错的，宁可整段退回旧判据。
            if all(value == value for value in measured):
                scores, floor_used, probed = measured, _ISLAND_PROB_FLOOR, True
        score_of = {id(run): score for run, score in zip(viable, scores)}
        top_score = max(scores)
        keep_floor = min(top_score, floor_used)
        kept = [
            run for run, score in zip(viable, scores)
            if score >= keep_floor - 1e-9
        ]
        # 锚只在「过了分数下限的岛」里挑，所以这里按长度挑就够了——分数负责筛掉
        # 说话，长度负责在两段独立演唱之间选素材更多的那段。反过来(按分数挑锚)会
        # 在两段质量相当时随机选中较短的一段。
        anchor = max(kept, key=lambda run: (run[1] - run[0], run[2] / max(1, run[3])))

        # 桥接要看的是「这段时间里到底有没有旋律」，而不是「两座合格岛之间隔多远」。
        # 8/9 实测同一段音频两次请求给出完全不同的边界：
        #   ① runs=[… 4.00-11.00/0.81 10.80-17.00/0.85]        → 起唱 4.33s  正确
        #   ② runs=[… 4.00-9.80/0.82  9.30-10.90/0.79  11.10-17.00/0.85] → 起唱 11.43s 切掉前半
        # ② 里同一段演唱被切成三块，中间那块 1.60s 差 0.05s 没过 1.65s 的合格线，
        # 于是两座合格岛之间凭空出现 1.30s 空档，又差 0.10s 桥不过去。可那 1.60s
        # 明明证明了那段时间有旋律——真实空档只有 0.20s。
        # 所以：合格岛决定谁能当锚，**全部**候选岛决定谁能当桥。
        coverage: List[List[int]] = []
        for lo, hi in sorted((run[0], run[1]) for run in runs):
            if coverage and lo <= coverage[-1][1]:
                coverage[-1][1] = max(coverage[-1][1], hi)
            else:
                coverage.append([lo, hi])

        def uncovered(begin: int, end: int) -> int:
            """[begin, end) 里没有被任何候选岛覆盖的帧数。"""
            if end <= begin:
                return 0
            gap = end - begin
            for lo, hi in coverage:
                left, right = max(begin, lo), min(end, hi)
                if right > left:
                    gap -= right - left
            return max(0, gap)

        # 试过"桥不许跨过一座合格但被分数否掉的岛"，已撤回。8/10 实测 46 段：
        # 修好了 2 段(1.23s→6.83s、0.00s→10.13s，切片核对过起点确实在那里)，
        # 但也切坏了 1 段本来正确的——那一段 3.93-7.00s 唱的是「简单点，说话的方式」
        # (切片 ASR prob=0.61)，它的岛分数低于下限被当成"说话"挡住了桥，起点从
        # 3.93s 推到 10.03s，砍掉 6.1 秒真歌声。尾侧更糟：一段 5.13-18.11s 的
        # 日文演唱被截成 5.13-9.67s。
        # 根因是分数本身分不开——实测 说话 0.786/0.805/0.809 与 唱 0.733/0.778
        # 完全交错，任何建立在这个分数上的规则都会同时误伤两边。
        bridge_frames = max(max_gap_frames, int(round(1.20 / hop)))
        span_start, span_end = anchor[0], anchor[1]
        merged = True
        while merged:
            merged = False
            for run in kept:
                if run[0] < span_start and uncovered(run[1], span_start) <= bridge_frames:
                    span_start = run[0]
                    merged = True
                if run[1] > span_end and uncovered(span_end, run[0]) <= bridge_frames:
                    span_end = run[1]
                    merged = True

        # The first qualifying 1.1 s window normally straddles the transition
        # from speech into song. Its midpoint is a better onset estimate than
        # its leading edge; then retain 220 ms for breath and note attack.
        start_frame = span_start + window // 2
        start_seconds = max(0.0, start_frame * hop - 0.22)

        # 结束位置对称处理：最后一个合格窗口同样横跨"唱→说"的过渡，取它的中点比取
        # 尾缘更准，再留 220ms 给收音尾巴。span_end 一直都算出来了，只是以前没返回——
        # 于是"唱完之后接的那段说话"被整段留在回哼素材里，实测被当成歌词唱了回去。
        end_frame = max(start_frame, span_end - window // 2)
        end_seconds = min(duration, end_frame * hop + 0.22)

        # Tiny trims are inaudible and risk shaving the opening note of an
        # already-pure singing clip.
        if start_seconds < 0.45 or duration - start_seconds < 1.2:
            start_seconds = 0.0
        # 尾部同理：裁不到 0.45s 就别裁，免得削掉最后一个音的收尾
        if duration - end_seconds < 0.45 or end_seconds - start_seconds < 1.2:
            end_seconds = duration

        # 被分数下限挡掉的岛：记下它在跨度的哪一侧、分数、以及**与跨度的间隔**
        # （负数表示重叠）。
        #
        # 位置本身已被证明没有区分度：8/9 实测一个渐弱的收尾长音(0.74)出现在最后，
        # 位置上和"唱完转说话"一模一样。剩下的线索是间隔——
        #   渐弱收尾 23.40-25.20/0.74  与保留区间重叠 0.60s   ← 该并进来
        #   唱完说话 17.30-19.20/0.74  与跨度间隔 0.10s       ← 该挡住
        # 物理上说得通：长音衰减与前一个音连续，滑动窗口会重叠；而唱完转说话
        # 中间要换气，会留一道缝。但目前 1 比 1，先只记录不改判定。
        blocked_before, blocked_after = [], []
        for run, score in zip(viable, scores):
            if run in kept:
                continue
            if run[0] < span_start:
                blocked_before.append((score, (span_start - run[1]) * hop))
            else:
                blocked_after.append((score, (run[0] - span_end) * hop))

        # 只在真的裁掉了东西时打印——流式模式每轮会调用本函数十几次，无条件打印会淹掉日志。
        if _LOG_ISLAND and (start_seconds > 0.0 or end_seconds < duration):
            print(
                "[Island] dur={:.2f}s cand={} viable={}/{} floor={:.2f} runs=[{}] "
                "挡掉[前:{} 后:{}] "
                "anchor=({:.2f},{:.2f}) oldBest=({:.2f},{:.2f}) "
                "span=({:.2f},{:.2f}) -> ({:.2f},{:.2f})".format(
                    duration,
                    len(candidates),
                    len(viable),
                    len(runs),
                    keep_floor,
                    " ".join(
                        # * = 合格(长度/窗口数)，+ = 通过分数下限、参与合并。
                        # 细化生效时括号里是这座岛单独分析出来的概率——它才是取舍
                        # 依据，斜杠前那个均分只留着做对照。
                        "{:.2f}-{:.2f}/{:.2f}{}{}".format(
                            r[0] * hop,
                            r[1] * hop,
                            r[2] / max(1, r[3]),
                            "(p{:.2f})".format(score_of[id(r)])
                            if probed and id(r) in score_of else "",
                            ("+" if r in kept else "*") if r in viable else "",
                        )
                        for r in runs
                    ),
                    # 分数@间隔，间隔为负表示与保留跨度重叠
                    " ".join("{:.2f}@{:+.2f}s".format(s, g)
                             for s, g in blocked_before) or "-",
                    " ".join("{:.2f}@{:+.2f}s".format(s, g)
                             for s, g in blocked_after) or "-",
                    anchor[0] * hop,
                    anchor[1] * hop,
                    best[0] * hop,
                    best[1] * hop,
                    span_start * hop,
                    span_end * hop,
                    start_seconds,
                    end_seconds,
                )
            )
        return float(start_seconds), float(end_seconds)

    def _build_contour(
        self, smoothed_midi: np.ndarray, voiced: np.ndarray, hop_seconds: float
    ) -> List[float]:
        bucket = max(1, int(round(0.10 / max(hop_seconds, 1e-3))))
        values: List[float] = []
        for start in range(0, smoothed_midi.size, bucket):
            end = min(smoothed_midi.size, start + bucket)
            local_mask = voiced[start:end]
            if np.mean(local_mask) < 0.35:
                continue
            values.append(round(float(np.median(smoothed_midi[start:end][local_mask])), 2))
        if len(values) > 240:
            indices = np.linspace(0, len(values) - 1, 240).astype(int)
            values = [values[i] for i in indices]
        return values

    @staticmethod
    def _build_timeline(
        smoothed_midi: np.ndarray,
        voiced: np.ndarray,
        hop_seconds: float,
        frame_seconds: float = 0.10,
    ) -> List[float]:
        """Build a fixed-rate playable pitch track.

        MIDI 0 is an explicit rest.  Unlike ``_build_contour`` this preserves
        leading/inter-note/trailing silence so a remembered phrase can be
        hummed back with recognisable timing instead of only its pitch shape.
        """
        bucket = max(1, int(round(frame_seconds / max(hop_seconds, 1e-3))))
        values: List[float] = []
        for start in range(0, smoothed_midi.size, bucket):
            end = min(smoothed_midi.size, start + bucket)
            local_mask = voiced[start:end]
            if local_mask.size == 0 or float(np.mean(local_mask)) < 0.35:
                values.append(0.0)
                continue
            values.append(round(float(np.median(smoothed_midi[start:end][local_mask])), 2))

        # Match the analyser's 45 s signal window.  A 30 s tail cap used to discard
        # the opening of otherwise valid long performances before Unity could apply
        # its streaming onset anchor.
        max_frames = max(1, int(round(45.0 / max(frame_seconds, 1e-3))))
        if len(values) > max_frames:
            values = values[-max_frames:]
        return values

    @staticmethod
    def _note_sequence(contour: Iterable[float]) -> str:
        notes: List[str] = []
        for value in contour:
            note = midi_to_note(value)
            if note and (not notes or notes[-1] != note):
                notes.append(note)
        if len(notes) > 18:
            notes = notes[:18] + ["…"]
        return "-".join(notes)

    @staticmethod
    def _summary(result: Dict) -> str:
        if not result.get("analysis_available"):
            return "没有取得可靠的歌唱音高信息"
        kind = "较像歌唱/哼唱" if result.get("is_singing") else "更像普通说话"
        notes = result.get("note_sequence") or "旋律轮廓不足"
        return (
            f"{kind}（概率{result.get('singing_probability', 0):.2f}）；"
            f"音域{result.get('pitch_low_note', '?')}～{result.get('pitch_high_note', '?')}；"
            f"音高稳定度{result.get('pitch_stability', 0):.2f}；"
            f"旋律片段{notes}"
        )

    @staticmethod
    def _empty(duration: float) -> Dict:
        return {
            "analysis_available": False,
            "is_singing": False,
            "singing_probability": 0.0,
            "duration": round(float(duration), 3),
            "pitch_backend": "none",
            "voiced_ratio": 0.0,
            "periodicity_mean": 0.0,
            "pitch_stability": 0.0,
            "sustained_ratio": 0.0,
            "pitch_min_hz": 0.0,
            "pitch_max_hz": 0.0,
            "pitch_median_hz": 0.0,
            "pitch_low_note": "",
            "pitch_high_note": "",
            "pitch_median_note": "",
            "pitch_contour_midi": [],
            "pitch_timeline_midi": [],
            "pitch_timeline_frame_seconds": 0.10,
            "singing_start_seconds": 0.0,
            "singing_end_seconds": 0.0,
            "pitch_timeline_start_seconds": 0.0,
            "note_sequence": "",
            "note_change_rate": 0.0,
            "lyrics_chars_per_sec": 0.0,
            "summary": "没有取得可靠的歌唱音高信息",
        }
