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
                 consonant_frames: int) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
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
    prev_vowel: str | None = None
    mora_iter = iter(mora)

    for note in notes:
        dur_s = float(note.get("duration_seconds") or 0.0)
        n_frames = max(1, int(round(dur_s / hop_seconds)))
        if str(note.get("note_type")) == "rest":
            phones.append(REST_PHONE)
            frames.append(n_frames)
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
        else:
            # 辅音占固定短帧，剩余给母音；音符太短时按比例压缩辅音
            c_frames = min(consonant_frames, max(1, n_frames // 2))
            phones.append(seq[0])
            frames.append(c_frames)
            phones.append(seq[1])
            frames.append(max(1, n_frames - c_frames))

    tokens = np.array([[vb.token(p) for p in phones]], dtype=np.int64)
    durations = np.array([frames], dtype=np.int64)
    total_frames = int(durations.sum())

    src_dt = float(score.get("frame_seconds") or 0.01)
    f0 = _resample_f0(list(score.get("f0_hz") or []), src_dt, hop_seconds, total_frames)
    # 无声帧补一个低频占位，nsf-hifigan 对全 0 的 F0 会产生爆音
    voiced = f0 > 0
    if voiced.any():
        fill = float(np.median(f0[voiced]))
    else:
        fill = 220.0
    f0 = np.where(voiced, f0, fill).astype(np.float32)
    return tokens, durations, f0[None, :]


# ---------------------------------------------------------------- 推理
def synthesize(model_dir: Path, score: dict, out_path: Path, seed: int,
               depth: int, speedup: int, consonant_frames: int) -> dict:
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

    tokens, durations, f0 = build_inputs(score, vb, hop_seconds, consonant_frames)

    providers = ["CUDAExecutionProvider", "CPUExecutionProvider"]
    available = set(ort.get_available_providers())
    providers = [p for p in providers if p in available] or ["CPUExecutionProvider"]

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

    audio = np.clip(np.asarray(wave_out, dtype=np.float32).reshape(-1), -1.0, 1.0)
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
    args = parser.parse_args()

    score = json.loads(args.score.read_text(encoding="utf-8"))
    try:
        info = synthesize(
            args.model, score, args.output, args.seed,
            args.depth, args.speedup, args.consonant_frames,
        )
    except Exception as exc:  # 让服务端能在日志里看到确切原因
        print(json.dumps({"error": f"{type(exc).__name__}: {exc}"}, ensure_ascii=False), flush=True)
        return 1
    info["request_id"] = args.request_id
    print(json.dumps(info, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
