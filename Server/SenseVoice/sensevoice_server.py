"""
SenseVoiceSmall 本地 ASR 服务 (给 Unity 客户端用)

功能：
  - 接收 WAV 音频
  - 用 SenseVoiceSmall 一次推理返回：转写文本 + 语言 + 情绪 + 音频事件

接口：
  GET  /health                       健康检查
  POST /asr                          form-data: audio_file=<WAV bytes>
                                     可选 form field: language=auto|zh|en|ja|ko|yue

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
import io
import re
import time
from typing import Optional

import numpy as np
import soundfile as sf
import uvicorn
from fastapi import FastAPI, File, Form, UploadFile
from fastapi.responses import JSONResponse
from funasr import AutoModel

# ------------------------------ 模型 ------------------------------

_model: Optional[AutoModel] = None


def load_model(device: str = "cuda:0"):
    global _model
    print(f"[SenseVoice] 加载模型 (device={device}) ...")
    t0 = time.time()
    _model = AutoModel(
        model="iic/SenseVoiceSmall",
        trust_remote_code=True,
        disable_update=True,
        device=device,
    )
    print(f"[SenseVoice] 模型就绪，耗时 {time.time() - t0:.2f}s")
    _warmup()


def _warmup():
    """用 1 秒静音触发一次推理，消除首次请求的冷启动延迟"""
    try:
        silent = np.zeros(16000, dtype=np.float32)
        t0 = time.time()
        _model.generate(input=silent, cache={}, language="auto", use_itn=True)
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


# ------------------------------ FastAPI ------------------------------

app = FastAPI(title="SenseVoiceSmall ASR Server")


@app.get("/health")
def health():
    return {"ok": _model is not None}


@app.post("/asr")
async def asr(
    audio_file: UploadFile = File(...),
    language: str = Form("auto"),
):
    if _model is None:
        return JSONResponse({"error": "model not loaded"}, status_code=503)

    try:
        data = await audio_file.read()

        # 解码 WAV（支持立体声/非16k采样率的稳健处理）
        wav, sr = sf.read(io.BytesIO(data))
        if wav.ndim > 1:
            wav = wav.mean(axis=1)
        if sr != 16000:
            from scipy.signal import resample_poly
            g = np.gcd(sr, 16000)
            wav = resample_poly(wav, 16000 // g, sr // g)
        wav = wav.astype(np.float32)

        t0 = time.time()
        res = _model.generate(
            input=wav,
            cache={},
            language=language,
            use_itn=True,
        )
        dt = time.time() - t0

        raw = res[0]["text"] if res else ""
        text, lang, emotion, audio_event = parse_output(raw)
        print(
            f"[ASR] dt={dt:.2f}s lang={lang} emo={emotion} evt={audio_event} "
            f"text={text!r}"
        )
        return {
            "text": text,
            "language": lang,
            "emotion": emotion,
            "audio_event": audio_event,
            "elapsed": round(dt, 3),
        }
    except Exception as e:
        import traceback

        traceback.print_exc()
        return JSONResponse({"error": str(e)}, status_code=500)


# ------------------------------ 入口 ------------------------------


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=9881)
    parser.add_argument(
        "--device",
        default="cuda:0",
        help="推理设备: cuda:0 / cuda:1 / cpu",
    )
    args = parser.parse_args()

    load_model(device=args.device)
    print(f"[SenseVoice] 监听 http://{args.host}:{args.port}")
    uvicorn.run(app, host=args.host, port=args.port, log_level="warning")


if __name__ == "__main__":
    main()
