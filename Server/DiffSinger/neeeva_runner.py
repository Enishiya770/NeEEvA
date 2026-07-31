"""NeEEvA 外部日语歌声渲染器（DiffSinger ONNX）。

被 Server/SVS/svs_server.py 的语种路由调用，接口由服务端固定：

    python neeeva_runner.py --model <音源目录> --score <score.json>
                            --output <out.wav> --seed <n> --request-id <id>

读入项目的 SingingScore（假名 mora + 音符 + 逐帧 F0），输出 44.1kHz WAV。
输出的是音源本身的音色，**不是角色音色**——角色音色由 Unity 侧的
svc-post-polish（9882 的 RVC）负责，这正是 m_EnableSVSRVCPostPolish 的用途。

为什么不用 SoulX 的内置日语适配：那条路把假名映射到 SoulX 的英语音素表，
唱出来是英语口音。这里用的是原生日语音素集的 DiffSinger 音源。

管线（有意跳过 variance 模型）：
    mora → 日语音素 → tokens
    音符时值 → 每个音素的帧数
    score 的 f0(10ms) → 重采样到声码器帧率(11.61ms)
    acoustic.onnx → mel → nsf_hifigan.onnx → waveform
dur.onnx / pitch.onnx 只在缺时值或缺 F0 时才需要，而 SingingScore 两者都有，
直接用真实数据比让模型再预测一遍更贴合原唱。
"""

from __future__ import annotations

import argparse
import json
import math
import sys
import wave
from pathlib import Path

import numpy as np
import onnxruntime as ort

# ---------------------------------------------------------------- 假名 → 日语音素
# 音素集见音源的 phonemes.txt（NNSVS/ENUNU 系）：
#   母音 a i u e o / 撥音 N / 促音 cl / 无声 SP / 気息 AP
_VOWELS = {"あ": "a", "い": "i", "う": "u", "え": "e", "お": "o"}

_ROWS: dict[str, tuple[str, str]] = {}


def _row(consonant: str, kana: str, vowels: str = "aiueo") -> None:
    for ch, v in zip(kana, vowels):
        _ROWS[ch] = (consonant, v)


_row("k", "かきくけこ")
_row("g", "がぎぐげご")
_row("s", "さすせそ", "aueo")  # し 不在此列，读作 sh i
_ROWS["し"] = ("sh", "i")
_row("z", "ざずぜぞ", "aueo")
_ROWS["じ"] = ("j", "i")
_row("t", "たてと", "aeo")
_ROWS["ち"] = ("ch", "i")
_ROWS["つ"] = ("ts", "u")
_row("d", "だでど", "aeo")
_ROWS["ぢ"] = ("j", "i")
_ROWS["づ"] = ("z", "u")
_row("n", "なにぬねの")
_row("h", "はひへほ", "aieo")
_ROWS["ふ"] = ("f", "u")
_row("b", "ばびぶべぼ")
_row("p", "ぱぴぷぺぽ")
_row("m", "まみむめも")
_row("r", "らりるれろ")
_ROWS["や"] = ("y", "a")
_ROWS["ゆ"] = ("y", "u")
_ROWS["よ"] = ("y", "o")
_ROWS["わ"] = ("w", "a")
_ROWS["を"] = ("", "o")

# 拗音：基底假名 → 腭化辅音
_PALATAL = {
    "き": "ky", "ぎ": "gy", "し": "sh", "じ": "j", "ち": "ch", "に": "ny",
    "ひ": "hy", "び": "by", "ぴ": "py", "み": "my", "り": "ry",
}
_SMALL_VOWEL = {"ゃ": "a", "ゅ": "u", "ょ": "o", "ぁ": "a", "ぃ": "i",
                "ぅ": "u", "ぇ": "e", "ぉ": "o"}

# 外来音（ファ/ティ/ウィ 等）
_FOREIGN = {
    "ファ": ("f", "a"), "フィ": ("f", "i"), "フェ": ("f", "e"), "フォ": ("f", "o"),
    "ティ": ("t", "i"), "ディ": ("d", "i"), "トゥ": ("t", "u"), "ドゥ": ("d", "u"),
    "ウィ": ("w", "i"), "ウェ": ("w", "e"), "ウォ": ("w", "o"),
    "シェ": ("sh", "e"), "ジェ": ("j", "e"), "チェ": ("ch", "e"),
    "ツァ": ("ts", "a"), "ツェ": ("ts", "e"), "ツォ": ("ts", "o"),
    "ヴァ": ("b", "a"), "ヴィ": ("b", "i"), "ヴ": ("b", "u"),
    "ヴェ": ("b", "e"), "ヴォ": ("b", "o"),
}

_KATA_TO_HIRA = {chr(c): chr(c - 0x60) for c in range(0x30A1, 0x30F7)}

REST_PHONE = "SP"
BREATH_PHONE = "AP"

# 低于此值视为无声帧。人声基频不会低于这个量级，variance 模型在休止段
# 输出的 6-16Hz 属于"此处无音高"的表示，不能当作真实基频送进声码器。
UNVOICED_HZ = 50.0


def _to_hiragana(text: str) -> str:
    return "".join(_KATA_TO_HIRA.get(ch, ch) for ch in text)


def mora_to_phonemes(mora: str, prev_vowel: str | None) -> list[str]:
    """把一个假名 mora 转成音素序列。未知 mora 抛错而不是静默出怪音。"""
    raw = (mora or "").strip()
    if not raw:
        raise ValueError("empty mora")
    if raw in _FOREIGN:
        c, v = _FOREIGN[raw]
        return [c, v] if c else [v]

    kana = _to_hiragana(raw)

    # 长音符：延长前一个母音
    if kana in {"ー", "〜", "～"}:
        if not prev_vowel:
            raise ValueError("长音符出现在首位，没有可延长的母音")
        return [prev_vowel]
    if kana == "ん":
        return ["N"]
    if kana == "っ":
        return ["cl"]
    if kana in _VOWELS:
        return [_VOWELS[kana]]
    if kana in _ROWS:
        c, v = _ROWS[kana]
        return [c, v] if c else [v]
    # 拗音（2 字，第二字为小书假名）
    if len(kana) == 2 and kana[1] in _SMALL_VOWEL:
        base, small = kana[0], kana[1]
        vowel = _SMALL_VOWEL[small]
        if base in _PALATAL:
            return [_PALATAL[base], vowel]
        if base in _ROWS:
            return [_ROWS[base][0], vowel]
    raise ValueError(f"unsupported Japanese mora: {mora!r}")


# ---------------------------------------------------------------- 乐谱 → 模型输入
class VoiceBank:
    def __init__(self, root: Path):
        self.root = root
        self.phonemes = [
            line.strip()
            for line in (root / "phonemes.txt").read_text(encoding="utf-8").splitlines()
            if line.strip()
        ]
        self.token_of = {p: i for i, p in enumerate(self.phonemes)}
        self.acoustic = root / "acoustic.onnx"
        if not self.acoustic.is_file():
            raise FileNotFoundError(f"missing acoustic.onnx under {root}")
        # variance 模型可选：有就能生成带真人音高微动的 F0，没有则退回乐谱 F0
        self.linguistic = root / "linguistic.onnx"
        self.pitch = root / "dspitch" / "pitch.onnx"

    @property
    def has_pitch_model(self) -> bool:
        return self.linguistic.is_file() and self.pitch.is_file()

    def token(self, phone: str) -> int:
        if phone not in self.token_of:
            raise ValueError(f"phoneme {phone!r} not in this voicebank's phoneme set")
        return self.token_of[phone]


def _resample_f0(f0: list[float], src_dt: float, dst_dt: float, dst_len: int) -> np.ndarray:
    """把逐帧 F0 从 score 帧率重采样到声码器帧率。

    只在有声段之间插值：0 表示无声，直接用最近邻取值避免把 0 和有效基频
    平均出一个滑音。
    """
    src = np.asarray(f0, dtype=np.float64)
    if src.size == 0:
        return np.zeros(dst_len, dtype=np.float32)
    dst = np.zeros(dst_len, dtype=np.float64)
    for i in range(dst_len):
        t = i * dst_dt
        pos = t / src_dt
        lo = int(math.floor(pos))
        hi = lo + 1
        if lo >= src.size:
            dst[i] = src[-1]
            continue
        a = src[lo]
        b = src[hi] if hi < src.size else a
        if a <= 0.0 or b <= 0.0:
            dst[i] = a if abs(pos - lo) <= 0.5 else b
        else:
            frac = pos - lo
            dst[i] = a + (b - a) * frac
    return dst.astype(np.float32)


def build_inputs(score: dict, vb: VoiceBank, hop_seconds: float,
                 consonant_frames: int, transpose: int = 0) -> dict:
    """把 SingingScore 展开成模型输入。

    返回 dict 而不是元组：除 acoustic 需要的 tokens/durations/f0 外，
    variance(pitch) 模型还需要词级切分与音符级数据。
    这里的"词"就是一个音符（一 mora 一音符），休止符自成一词。
    """
    notes = score.get("notes") or []
    mora = list(score.get("lyrics_mora") or [])
    if not notes:
        raise ValueError("score has no notes")

    sung = [n for n in notes if str(n.get("note_type")) != "rest"]
    if len(mora) != len(sung):
        raise ValueError(
            f"mora/note mismatch: {len(mora)} mora vs {len(sung)} sung notes; "
            "score must be one-mora-per-note"
        )

    phones: list[str] = []
    frames: list[int] = []
    word_div: list[int] = []      # 每个"词"(音符)含几个音素
    word_dur: list[int] = []      # 每个"词"占几帧
    note_midi: list[float] = []   # 每个音符的 MIDI（休止=0）
    note_dur: list[int] = []
    prev_vowel: str | None = None
    mora_iter = iter(mora)

    for note in notes:
        dur_s = float(note.get("duration_seconds") or 0.0)
        n_frames = max(1, int(round(dur_s / hop_seconds)))
        raw_midi = float(note.get("midi") or 0)
        note_midi.append(raw_midi + transpose if raw_midi > 0 else 0.0)
        note_dur.append(n_frames)
        word_dur.append(n_frames)
        if str(note.get("note_type")) == "rest":
            phones.append(REST_PHONE)
            frames.append(n_frames)
            word_div.append(1)
            continue

        unit = next(mora_iter)
        seq = mora_to_phonemes(unit, prev_vowel)
        # 记住本音符的母音，供后续长音符延长
        for p in reversed(seq):
            if p in {"a", "i", "u", "e", "o", "N"}:
                prev_vowel = p
                break

        if len(seq) == 1:
            phones.append(seq[0])
            frames.append(n_frames)
            word_div.append(1)
        else:
            # 辅音占固定短帧，剩余给母音；音符太短时按比例压缩辅音
            c_frames = min(consonant_frames, max(1, n_frames // 2))
            phones.append(seq[0])
            frames.append(c_frames)
            phones.append(seq[1])
            frames.append(max(1, n_frames - c_frames))
            word_div.append(2)

    # 休止符的 note_midi 不能填 0：音源的 dspitch 配置是 use_note_rest=false，
    # 模型不认识"休止"这个概念，会把 MIDI 0 当成真实音符渲染（0 号音 ≈ 8.18Hz），
    # 在每个休止处生成荒谬的音高过渡，污染整条 F0 曲线。改为沿用相邻实音的音高，
    # 让曲线在休止处保持连续。
    for i, m in enumerate(note_midi):
        if m > 0:
            continue
        prev_m = next((note_midi[j] for j in range(i - 1, -1, -1) if note_midi[j] > 0), 0.0)
        next_m = next((note_midi[j] for j in range(i + 1, len(note_midi)) if note_midi[j] > 0), 0.0)
        note_midi[i] = prev_m or next_m or 60.0

    tokens = np.array([[vb.token(p) for p in phones]], dtype=np.int64)
    durations = np.array([frames], dtype=np.int64)
    total_frames = int(durations.sum())

    src_dt = float(score.get("frame_seconds") or 0.01)
    f0 = _resample_f0(list(score.get("f0_hz") or []), src_dt, hop_seconds, total_frames)
    if transpose:
        f0 = np.where(f0 > 0, f0 * (2.0 ** (transpose / 12.0)), f0).astype(np.float32)
    # 无声帧补一个低频占位，nsf-hifigan 对全 0 的 F0 会产生爆音
    voiced = f0 > 0
    fill = float(np.median(f0[voiced])) if voiced.any() else 220.0
    f0 = np.where(voiced, f0, fill).astype(np.float32)

    return {
        "tokens": tokens,
        "durations": durations,
        "f0": f0[None, :],
        "word_div": np.array([word_div], dtype=np.int64),
        "word_dur": np.array([word_dur], dtype=np.int64),
        "note_midi": np.array([note_midi], dtype=np.float32),
        "note_dur": np.array([note_dur], dtype=np.int64),
        "total_frames": total_frames,
    }


def predict_f0(vb: VoiceBank, inp: dict, providers: list[str],
               speedup: int) -> np.ndarray | None:
    """用音源自带的 variance 模型生成带真人音高微动的 F0（Hz）。

    直接照搬乐谱里那条规整的 F0 会让演唱听起来发死（"像机器人"），
    因为它缺少起音滑入、音间过渡和自然抖动。pitch.onnx 正是为此而生。
    失败时返回 None，由调用方退回乐谱 F0。
    """
    try:
        ling = ort.InferenceSession(str(vb.linguistic), providers=providers)
        encoder_out, _ = ling.run(
            ["encoder_out", "x_masks"],
            {"tokens": inp["tokens"], "word_div": inp["word_div"],
             "word_dur": inp["word_dur"]},
        )
        n_frames = inp["total_frames"]
        pitch_sess = ort.InferenceSession(str(vb.pitch), providers=providers)
        pred = pitch_sess.run(
            ["pitch_pred"],
            {
                "encoder_out": encoder_out,
                "ph_dur": inp["durations"],
                "note_midi": inp["note_midi"],
                "note_dur": inp["note_dur"],
                # retake 全 True = 整条曲线都由模型生成；pitch 传乐谱曲线作基准
                "pitch": inp["f0"],
                "retake": np.ones((1, n_frames), dtype=bool),
                "speedup": np.array(speedup, dtype=np.int64),
            },
        )[0]
        out = np.asarray(pred, dtype=np.float32).reshape(1, -1)
        # 输出可能是 MIDI 半音也可能是 Hz，按量级判断后统一成 Hz
        median = float(np.median(out[out > 0])) if (out > 0).any() else 0.0
        if 0 < median < 130:
            out = (440.0 * 2.0 ** ((out - 69.0) / 12.0)).astype(np.float32)

        # 休止段模型会输出 6-16Hz 这种次声值。直接喂给 nsf-hifigan 会产生低频
        # 轰鸣/杂音，所以按有声阈值判定后统一填成有声段中位值——与乐谱 F0 的
        # 处理保持一致（声码器不喜欢 0 或极低的 F0）。
        voiced = out >= UNVOICED_HZ
        if voiced.any():
            out = np.where(voiced, out, float(np.median(out[voiced]))).astype(np.float32)
        return out
    except Exception as exc:
        print(json.dumps({"warning": f"pitch model unavailable: {exc}"},
                         ensure_ascii=False), flush=True)
        return None


# ---------------------------------------------------------------- 推理
def synthesize(model_dir: Path, score: dict, out_path: Path, seed: int,
               depth: int, speedup: int, consonant_frames: int,
               target_peak: float = 0.9, use_pitch_model: bool = True,
               transpose: int = 0) -> dict:
    vb = VoiceBank(model_dir)
    root = Path(__file__).resolve().parent
    vocoder_path = root / "vocoder" / "nsf_hifigan.onnx"
    if not vocoder_path.is_file():
        raise FileNotFoundError(f"missing vocoder: {vocoder_path}")

    import yaml  # 延后导入，缺 pyyaml 时给出更清楚的报错

    vocoder_cfg = yaml.safe_load((root / "vocoder" / "vocoder.yaml").read_text(encoding="utf-8"))
    sample_rate = int(vocoder_cfg.get("sample_rate", 44100))
    hop_size = int(vocoder_cfg.get("hop_size", 512))
    hop_seconds = hop_size / sample_rate

    np.random.seed(seed & 0x7FFFFFFF)

    inp = build_inputs(score, vb, hop_seconds, consonant_frames, transpose)
    tokens, durations = inp["tokens"], inp["durations"]
    f0 = inp["f0"]

    providers = ["CUDAExecutionProvider", "CPUExecutionProvider"]
    available = set(ort.get_available_providers())
    providers = [p for p in providers if p in available] or ["CPUExecutionProvider"]

    f0_source = "score"
    if use_pitch_model and vb.has_pitch_model:
        predicted = predict_f0(vb, inp, providers, speedup)
        if predicted is not None and predicted.shape[1] == inp["total_frames"]:
            f0 = predicted
            f0_source = "variance-model"

    acoustic = ort.InferenceSession(str(vb.acoustic), providers=providers)
    mel = acoustic.run(
        ["mel"],
        {
            "tokens": tokens,
            "durations": durations,
            "f0": f0,
            "depth": np.array(depth, dtype=np.int64),
            "speedup": np.array(speedup, dtype=np.int64),
        },
    )[0]

    # 声码器需要 mel 与 f0 帧数一致
    n_frames = mel.shape[1]
    if f0.shape[1] != n_frames:
        if f0.shape[1] > n_frames:
            f0 = f0[:, :n_frames]
        else:
            f0 = np.pad(f0, ((0, 0), (0, n_frames - f0.shape[1])), mode="edge")

    vocoder = ort.InferenceSession(str(vocoder_path), providers=providers)
    wave_out = vocoder.run(["waveform"], {"mel": mel, "f0": f0})[0]

    audio = np.asarray(wave_out, dtype=np.float32).reshape(-1)

    # 峰值归一化：nsf-hifigan 出来的电平很低（实测峰值约 0.17 / RMS 0.03，比
    # SoulX 输出低约 14dB）。下游 RVC 的 HuBERT 特征与 F0 提取在这种电平下会
    # 明显劣化，听感就是沙哑。这里抬到接近满量程，保留动态不做压缩。
    peak = float(np.max(np.abs(audio))) if audio.size else 0.0
    gain = 1.0
    if peak > 1e-6 and target_peak > 0:
        gain = target_peak / peak
        audio = audio * gain
    audio = np.clip(audio, -1.0, 1.0)
    pcm = (audio * 32767.0).astype("<i2")
    out_path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(out_path), "wb") as fh:
        fh.setnchannels(1)
        fh.setsampwidth(2)
        fh.setframerate(sample_rate)
        fh.writeframes(pcm.tobytes())

    return {
        "backend": "diffsinger-ja",
        "voicebank": model_dir.name,
        "sample_rate": sample_rate,
        "frames": int(n_frames),
        "seconds": round(len(audio) / sample_rate, 3),
        "phonemes": int(tokens.shape[1]),
        "providers": providers,
        "raw_peak": round(peak, 4),
        "normalize_gain": round(gain, 3),
        "f0_source": f0_source,
        "transpose": transpose,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--score", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--seed", type=int, default=0)
    parser.add_argument("--request-id", default="")
    # 浅扩散深度与采样加速比，可按听感调整
    parser.add_argument("--depth", type=int, default=1000)
    parser.add_argument("--speedup", type=int, default=10)
    parser.add_argument("--consonant-frames", type=int, default=3)
    # 归一化目标峰值；设 0 可关闭（下游若自带增益处理时用）
    parser.add_argument("--target-peak", type=float, default=0.9)
    # 用音源自带的 variance 模型生成 F0（更自然）；--no-pitch-model 退回乐谱 F0
    parser.add_argument("--no-pitch-model", dest="use_pitch_model",
                        action="store_false", default=True)
    # 整体移调(半音)。角色 RVC 模型中位约 288Hz，乐谱明显偏高时下移能减轻紧绷
    parser.add_argument("--transpose", type=int, default=0)
    args = parser.parse_args()

    score = json.loads(args.score.read_text(encoding="utf-8"))
    try:
        info = synthesize(
            args.model, score, args.output, args.seed,
            args.depth, args.speedup, args.consonant_frames,
            args.target_peak, args.use_pitch_model, args.transpose,
        )
    except Exception as exc:  # 让服务端能在日志里看到确切原因
        print(json.dumps({"error": f"{type(exc).__name__}: {exc}"}, ensure_ascii=False), flush=True)
        return 1
    info["request_id"] = args.request_id
    print(json.dumps(info, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
