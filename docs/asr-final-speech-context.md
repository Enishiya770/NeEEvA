# 普通讲话最终识别的上下文窗口

`speech-context-v1` 仅在 HTTP `/asr` 最终识别（包括客户端通过该接口发起的预测快照）选择窗口。WebSocket partial、歌唱分段、尾部补识别和默认 `generate_asr()` 保留原来的 9 秒窗口、0.8 秒重叠及短尾处理。

策略复用 ASR 前已经完成的真实 VAD 和 quick 声学分析，不增加模型识别次数，不强制中文，也不改变模型精度。

- 必须有真实语音 VAD、有效且覆盖完整原始录音的声学分析，并明确 `is_singing=false`。
- 预期歌唱、歌声绕过 VAD、缺失/非法数据或不完整的尾部分析均沿用原策略。
- 当前低旋律门要求 singing probability <0.45、sustained ratio <0.08、pitch stability <0.50，voiced ratio 与 periodicity 均 >=0.30。这是保守启发式，不是对普通讲话或混合音频的可靠真值分类。
- 通过门限的最终音频最长 18 秒保留为一窗。更长音频优先在已有 >=180ms VAD 停顿处切分：搜索当前窗 9～18 秒范围，选择最接近 15 秒的位置。没有停顿则回到 9 秒边界；相邻窗仍重叠 0.8 秒。
- VAD 时间基是原始上传 WAV。窗口规划先减去外侧静音裁剪偏移，并合并重叠 VAD 区域，不丢弃中间停顿或音频。

`/health.final_speech_window_policy` 返回策略版本。`[ASR/FinalWindows]` 日志记录本轮选择、原因和样本边界；`[ASR/Windowed]` 记录实际多窗范围。

2026-09-22 使用实际 GTX 1660 Ti、同一 SenseVoiceSmall 权重、真实 CPU FSMN-VAD 与相同录音交替验证。15.79 秒成语由两次推理变为一次，恢复“识时务者为俊杰”，识别阶段中位耗时约 254→146ms；歌唱和混合保护样本维持原输入窗口及文字。测试是少量样本，不能推导整体准确率。单窗会更长，生产 ASR 的共用锁和显存仍受实际输入长度影响。

诊断与逐次结果保存在 `work/asr-accuracy-fix-20260922/`。回归入口：在 `Server/SenseVoice` 使用当前 ASR Python 执行 `python -m unittest test_final_speech_windows`；相关混合/流式回归应同时通过。
