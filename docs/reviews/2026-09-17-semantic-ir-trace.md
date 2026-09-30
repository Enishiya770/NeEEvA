# 第 0/1 阶段：Trace 与最小 RoleDecision

## 基线和代码审查

2026-09-17 检查：这是下载的源码目录，没有 `.git`，因此不存在可验证的分支、HEAD 或未提交差异。用户确认以当前文件快照改造。修改前保存相关文件原始字节及 SHA-256；写回前必须逐文件验证原始哈希，无匹配则停止，不能覆盖编辑器或用户的新修改。

主要入口：ChatSample.MarkEOU → AcceptClip / deferred ASR → StartStreaming → ChatQW first-reply / formal → RoleOutputChannels → GPTSoVITSFASTAPI.StreamVoice → AudioSource。已有 ChatRequestWireTiming、公开 say 边界、请求 generation 和播放门控，不能用新 IR 绕开。

改造前主程序集独立编译通过，已有 5 条警告。Unity 编辑器正在运行；不终止、不重启，不修改场景/序列化配置。独立编译利用当前 Unity 2022.3.22f1 引用和 C# 9 参数，输出到外部工作目录，不覆盖 Library 程序集。没有运行 Unity PlayMode、真实麦克风、模型或 TTS 请求。

## 实施顺序

1. 独立 ConversationTrace：每轮、每请求标识和单调时钟，只记录诊断元数据。
2. EOT/最终 ASR 回调、正式模型/首答模型、流式 TTS 边界接入；保留已有计时。
3. RoleDecision v1 → DecisionCompiler → 完整旧协议预检查 → 原流式回调/执行校验。
4. 隔离编译与语义边界回归通过后，以哈希保护写回。

## Trace 使用和精确定义

开启 ChatSample 现有 m_LogStreamTimings，Unity 日志中搜索 `[ConversationTrace]`。每行 JSON 带 schema、turnId、spanId、parentSpanId、kind、event、elapsedMs 和 utc。elapsedMs 相对同一 turn 起点使用 Stopwatch 单调时间；缺失事件表示未观测到，不能当作 0。独立请求各自去重，无静态全局 current turn。没有输出台词、提示词、录音、图像、密钥。

| 事件 | 语义 |
|---|---|
| eot.capture_closed | 程序结束采集；不是人工标注的用户真实停说时刻 |
| asr.final_callback | 最终 ASR 被调用方收到；可能是成功、空结果或拒识，不能理解为一定识别成功 |
| llm.send | 正式/first-reply HTTP SendWebRequest 边界 |
| llm.first_content_delta | SSE 首个非空 content delta；供应商 chunk 可能含多个 token，不能声称是服务端精确 first token |
| llm.first_valid_public_speech | 通过公开输出协议的首个非空台词片段；不等于 after_action 已获运行时批准 |
| decision.compiled / decision.rejected | 完整 JSON 编译通过或拒绝；拒绝时不执行、不发声 |
| tts.send | GPT-SoVITS 主流式请求发送 |
| tts.first_pcm | WAV 数据被解码成第一批有效 PCM 样本，不是首网络字节或轮询时刻 |
| audio.source_play_called | 调用 AudioSource.Play；缓存/预备音频也记录，但没有假造其网络请求时间 |
| audio.formal_playback_advanced | 现有正式音频推进判定；仍不等于声卡物理首音测量 |

覆盖正式 ChatQW 流式和 first-reply、AcceptClip 最终 ASR、deferred 最终 ASR，以及 GPTSoVITSFASTAPI 流式 TTS。预热/可撤销 first-reply preparation 不计正式首音。其他供应商、完整 AudioClip TTS 合成、prepared 合成阶段、非流式模型、唱歌 SVC 不具有完整串联 Trace，本轮不伪造这些阶段。缓冲播放仍记录 Play。existing generation / revision 判定保留；过期请求不写入新请求 span。

## RoleDecision v1（实验性迁移入口）

ChatQW Inspector 中新增 `m_EnableSemanticRoleDecision`，默认 false。开启后正式流式请求要求填写 JSON，关闭 first-reply 独立标签路由，使正式响应走 DecisionCompiler。现有 scene、prefab 和默认运行模式均未更改。辅助感知/规划请求继续使用现有协议。

```json
{"version":1,"speech":{"text":"こんにちは。","language":"ja"},"attention":"unchanged","intents":[]}
```

speech.language 为 zh/ja/en，text 可为空。attention 为 unchanged / visual_on / visual_off。intents 可为空或只包含一项 `{"type":"thinking.schedule","after_seconds":5}`（1–3600 秒）。对应旧协议 lang + speech independent + say、look/unlook、next；静默编译为 silent。未知字段/动作拒绝，不接收 raw tags，不接收 capture/version/revision/range 等运行时数据。表达/表情、motion、歌唱素材引用和其他业务意图尚未进入 v1，禁止假装已迁移；需要这些能力时保留默认旧模式。

JSON 完整返回且 finish_reason=stop 后才编译/发出语音。对输出总长/正文长度/深度/字段类型/重复字段及公开边界做校验；截断、错误或不支持动作时触发现有纠错通道，返回 silent，绝不把原 JSON 送给 TTS 或动作 runtime。历史仍保存兼容旧链路的编译结果。

这是最小闭环而非全量协议替换。v1 等待完整 JSON，会增加等待；不将此实现描述为低延迟改进。将来逐条可提交的 IR 流需独立设计 generation、语义撤销和 after_action 依赖，不能简单逐字段朗读。

## 验证和回退

独立回归：`Tools/SemanticIR/run-regression.ps1`（Windows、.NET 10 SDK，引用项目现有 Newtonsoft；不访问模型或包源）。覆盖每个旧协议切分点、所有 JSON 截断前缀、非法字段/类型、静默、标签注入/内部日志、语言和阶段、Trace 请求/轮次隔离与诊断失败不影响执行。

本次基线与改后完整主程序集均需编译，无新增警告；独立检查不能代替 Unity 真机语音验收。使用真实对话采集 Trace 后才可报告延迟数值。

关闭 m_EnableSemanticRoleDecision 即回到原正式标签输出；关闭 m_LogStreamTimings 停止创建串联 Trace。文件级回退须用交付备份并比对当前文件哈希，避免覆盖之后的用户修改。没有执行 git init、commit、reset，也没有改写 singing/realtime pipeline。
