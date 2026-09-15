# 闭合短语音证据与最终识别（2026-09-12）

## 已查明的实际丢失原因

Unity 的空结果在 SenseVoice 服务日志中找到了对应原因。`Server/ARDY/runtime/room-stop-follow-20260912/services-before/asr.out.log`（重启前归档）物理 LF1297、1300 都记录完整识别正确得到“停一下。”，随后被短句幻听过滤拒绝。原规则为：`EMO_UNKNOWN`、去标点后不超过 5 字、声纹不是 owner，三者同时成立就拒绝。该次短句被识别为 guest，因此即使文字正确也变成空结果。原日志没有保存完整识别的原始 FSMN 时长，不能事后断言具体数值。

这说明“停止”没有进入动作规划；不能把该次失败归咎于停止任务已经提交却没有执行。原全局幻听过滤保留，本次通过同一录音的额外证据处理短句冲突。

## 实现边界

`ClosedSpeechEvidenceTracker` 只在已确认 EOU、流式文字稳定至少 0.35 秒、音频时间推进、尾部覆盖、存在近期实际语音 VAD、非歌唱/背景及无待决文字修订时输出证据。保存录音编号、文字修订、音频覆盖范围、观察与收束时间。短句窗口限定为 6.5 秒音频、120 字；超出时仍走普通最终识别。一次录音最多发布一次，后续空包、否定修订、旧录音包都不能偷偷沿用前面的非空 partial。

RT 把这份证据交给 ChatSample 与 SenseVoice。它本身没有停止、移动、生成回复或写聊天历史的权限。ChatSample 可在最终识别运行时准备短语义决定，但必须得到同录音、同文字的最终确认后才能按绑定的任务/执行版本提交停止。普通开口不会停止角色。

最终识别非空时始终保留其实际转写。最终为空时，允许的恢复路径为：

- 明确的 `vad-insufficient-speech` 或 `recognizer-empty`，且存在本录音已闭合的稳定证据。
- `hallucination-rejected` 冲突：还必须具备歌唱补救前的原始 FSMN 语音至少 240 毫秒，并且拒绝前的完整识别文字与流式闭合候选完全一致（至少两个有效字符）。只恢复文字，否定和引用完整交给语义层。

`SelfSpeech`、未知旧服务拒因、网络错误、已取消/已脱离请求、不同终稿修订、不同录音、过期证据都不恢复。恢复单次消费，原始最终拒因、被拒文字及 `LastNoSpeech` 保留，来源标为 `closed-stream-recovery`，没有把恢复伪装成最终声学识别成功。完成预测 ASR 晋升的路径也会发布明确的 `full-preview` 最终身份。

## 验证与限制

`Server/ARDY/runtime/closed-speech-recovery-20260912/` 保存源指纹、52 项纯 C# 检查、4 项后端测试和对应拒绝日志摘录。统一隔离验证已完成：`ClosedSpeechRecoveryRegression.RunChecks()` 的 57 项检查通过，含 5 项 provider 状态检查；最终 `ArdyRoomSpeechPreparationRegression.RunChecks(chat, bridge)` 的 67 项检查通过，含真实输入交接、停止一次、超时及版本拒绝链路。完整记录见 `Server/ARDY/runtime/room-stop-follow-20260912/verification.json`。

原录音没有可用于此次离线重放的副本，且其完整原始 FSMN 时长未记录，因此不能仅靠模拟回归宣称真人语音停止已经验收。后续日志 `[Speech/ClosedEvidence]`、`[ASR/Rejected]`、`[ASR/Final]`、`[Room/Preparation]` 和 `[Room/QuickStop]` 可按 capture/task/operation 对应每一环节。

新拒绝字段需重启 SenseVoice 才生效。只重启 ASR 的现有入口为 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File Tools/neeeva_services.ps1 restart -Only asr`。脚本按监听端口 9881 查询 PID，没有 PID 文件；工作目录为 `Server/SenseVoice`，日志为 `Server/RuntimeLogs/asr.out.log`、`asr.err.log`。主任务已按此入口单独重载 ASR，并用无声合成波形确认新拒因字段生效；没有重启主 Unity。
