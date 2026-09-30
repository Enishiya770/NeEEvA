# Observability / Trace v2

本轮只增加诊断，不改变对话路由、发言审核、角色心理活动、歌唱选材或播放条件。使用现有 ChatSample.m_LogStreamTimings 开关；日志前缀为 [ConversationTrace]。新增记录不包含原始台词、歌词或音频内容。

## 时间与关联

- schema 2 的 elapsedMs 从接纳本次录音开始计时，包含预录缓冲的录音并不等于物理发声起点。EOT 采用 eot.capture_closed，计算 EOT 延迟需要相减。schema 1 通常从 EOT 开始。
- turnId 串联同一输入；spanId 区分并发请求；parentSpanId 表示调用关联。比较网络耗时必须使用同一 span，不能把两个请求的首尾配在一起。
- Mark 每个 span 每类事件只记录一次；Sample 用于有界采样。partial 最多每秒一次、每段录音最多 300 次。F0/MIDI 每批 200 帧、最多各 6000 帧，截断会明确标记。

## 事件覆盖与解释

| 链路 | 主要记录 | 限制 |
| --- | --- | --- |
| 录音/识别 | capture.started、asr.partial_first/sample、eot.capture_closed、asr.send/transport_complete/result、既有 asr.final_callback | 记录已接受的录音与回调；没有提供的数据不推算 |
| 声学 | audio.analysis_response、audio.f0_frames、audio.evidence、audio.pitch_frames | 服务端主唱段/恢复边界与 turn_segments；不是完整逐词对齐，也不保证拥有服务端内部所有声学岛 |
| 房间决策 | room.plan_begin、room.http_send/complete、room.decision_resolved；预取阶段的 room.preparation_send/complete/reused/discarded | 预取是请求级决策提示，不代表最终 ASR 已确认或获得执行权；同时记录 HTTP 状态、错误、当前请求状态，不能仅靠耗时断言超时 |
| 模型 | llm.send、llm.first_content_delta、role.thought_opened/closed、role.action_candidate、llm.first_public_candidate、llm.semantic_output_validated | first content 是收到的内容块，不是服务端 token 时间；action candidate 不等于可执行决策；完整输出校验不是首决策时刻 |
| 发言放行 | speech.gate_selected、speech.first_buffered/forwarded、speech.generation_complete、speech.review_begin/complete、vision.capture_requested/returned、speech.released/silent_fallback、speech.action_gate_wait | 协议认可的 public speech、审核放行和实际播放是不同时间点 |
| TTS/播放 | 既有 tts.send、tts.first_pcm、audio.source_play_called、audio.formal_playback_advanced | Play 调用/播放进度不能证明扬声器实际发声或用户听到了 |
| 歌唱 | singing.material_resolved、singing.execution_observed、svc.input/send/transport_complete、singing.source_play_called、singing.finished | 本轮覆盖全段、分段和 fast-full 上传；缓存/预转换等其他分支不保证完整覆盖 |

“&lt;thought&gt;”是有意保留的角色心理活动。这里只记录首个心理活动块的边界，不输出其正文、不取消它，也不把它计为对外发言。嵌套块直到最外层结束才闭合。

svc.input 描述实际提交的 WAV，包括长度、采样参数、摘要，以及已跟踪的源录音时间区间。裁剪、变速、分块、拼接的映射跟随实际缓冲区；拼接静音单独列出。它不修改音频，也不把声明的歌词范围冒充为实际裁剪范围。

mappingComplete=false 表示来源缺失或冲突，不能据此推断歌词对齐正确。相同字节来自不同录音时，无法判定归属的克隆保持未知。对象上的 clipRef/revision 标记不能仅凭字节相同传播给任意克隆。摘要缓存仅保留最多 128 项元数据，不保留 PCM。实际输入仍需用新日志验证。

服务端当前响应未提供逐词时间戳，明确记录 wordTimestampsAvailable=false。客户端已经得到的 F0、边界、分段时间保留其坐标来源，不能当成已经完成的 AudioEvidenceTimeline/MaterialResolver。

## 汇总与检查

只读汇总 Unity Editor.log：

~~~powershell
python Tools/Observability/summarize_trace.py "$env:LOCALAPPDATA/Unity/Editor/Editor.log" --out trace-summary.json
~~~

汇总兼容 schema 1/2，保留缺失事件和分请求耗时，不导出台词。若部分请求被取消、走其他分支或尚未结束，缺失本身不代表程序错误。

Windows + .NET 10 SDK 下运行纯逻辑回归（无需启动 Unity）：

~~~powershell
powershell -ExecutionPolicy Bypass -File Tools/Observability/Run-Checks.ps1
python Tools/Observability/test_summary.py
~~~

测试使用项目实际的 RoleDecision、RoleOutputChannels、ConversationTrace、WaveformTrace。覆盖原有协议、所有流式切分位置的心理活动隔离、诊断回调异常不改变行为、实际 WAV 来源映射、并发请求汇总和缺失数据。

## 下一轮运行验收

1. 普通语音对话：确认同一 turn 串起录音、EOT、识别、决策、发言审核、TTS 和播放，区分首内容、public speech 与审核放行。
2. 一次包含心理活动的回复：确认心理活动持续存在，TTS 只接收公开台词。
3. “说话→唱两句→说话→请求复唱”：比较声明选材、clean/expanded 和实际 svc.input 来源；逐词信息缺失保持未知。
4. 打断或快速连续两轮：确认旧请求保留自己的 turn/span，日志中的 current/cancelled 状态反映实际回调。

需要真实 Play Mode 对话来完成这些检查；静态编译和纯逻辑回归不能替代麦克风、服务器、Unity AudioSource 的端到端验证。

## 后续优先级

0. 补观测并采集新基线。
1. 完整 Semantic IR / RoleDecision：保留角色心理活动，明确 speech/expression/attention/intents；运行期 ID、版本和音频边界由程序补齐，经 DecisionCompiler 兼容旧 runtime。已有 v1 原型默认关闭，并非此阶段已完成。
2. Singing grounding：AudioEvidenceTimeline、语义材料与真实波形对齐，以及实际执行反馈。
3. 实时化：被动持续感知 → prefetch → speculative decision 的确认/提交/丢弃。
4. 流式歌唱切割。

房间决策超时、发言整段缓冲和审核等待作为基线中的独立问题记录。本轮不提前改变这些策略。


## 2026-09-18：交付状态与观测收口

- llm.first_public_candidate 仅表示当前流式前缀可解析；完整输出仍可能被拒绝。汇总工具兼容旧 llm.first_valid_public_speech，指标改名为 llmSendToPublicCandidateMs。
- speech.first_forwarded 表示候选转交下游，不代表通过歌唱门控。speech.withheld.* / speech.discarded.* 记录拦截/撤回原因；speech.queued.tts 记录实际入合成队列，仍不代表播放。
- delivery.round_closed 区分 Played、Quiet、Suppressed、AwaitingValidation、Rejected、Undelivered。成功完成的正式 PCM/WAV chunk 才提供播放完成事实；缓存填充语不计入正式交付。Quiet/Suppressed 可结束调度等待，但不是“已听到回复”。
- 内容待审、被拒或被撤回时，空队列不再清除等待回应标记。既有等待上限和用户接管仍能结束等待，delivery.wait_ended 独立记录原因；不把这些路径宣称成成功交付。待审目标原有的 singing-goal-review 排程可以越过等待标记，仍受原有请求预算、审核和执行校验约束。
- goal.review_begin/view_ready/callback/end 串联一次歌唱目标审核；review.http_send/complete/cancelled 区分传输。caller_cancelled、transport_cancelled、transport_timeout、owner_changed、invalid_or_unavailable_decision、decision_processed 等是不同结局。decision_processed 并非批准，需同时看 decisionStatus/goalStatus。
- eou.result / eou.decision_rejected 区分 transport_timeout、transport_error、http_error、invalid_envelope、output_truncated、empty_output、invalid_decision。其他提供方未暴露传输状态时使用 empty_or_unavailable，不推测根因。
- 未更改 thought、唱歌选材、实质审核标准、超时值或重试次数。

新增回归直接调用生产代码中的交付收尾 helper，并使用最小宿主提供状态。测试覆盖空队列、扣留、待审、错误、有意安静、复读抑制、真实正式播放完成，以及迟到审核不能覆盖新归属；不代替 Unity Play Mode 端到端测试。


## Review handoff and boundary consumption

- `review.handoff_begin/refresh/end`: a reviewed candidate refreshes its image at most once before a new formal decision. Missing images never recycle stale pixels. A new user turn, model, formal generation or work epoch cancels the handoff. Capture timeout is bounded.
- `goal.execution_handoff/execution_request`: distinguishes an approved goal from an explicit executable request. `goal.cancellation_reply` grants one result-based role decision for the same withheld cancellation response; it neither replays old text nor creates a sing action.
- `eou.request_end`: request-scoped terminal including caller cancellation. `eou.consumer_checked/pending/coverage_wait/apply_rejected/consumer_end` expose consumption and rejection facts even when RT timing logs are disabled. They do not change confidence, freshness, speech interruption or endpoint rules.
- `speech.discarded.queue` includes the actual suppression cause, removed chunk counts and whether formal audio already completed. `delivery.round_closed` also includes rejection and completed-character facts. Partial playback is not complete task delivery.
- The portable checks include the production handoff coordinator and ChatSample handoff partial with minimal capture/session stubs. They cover delayed or duplicate callbacks, refreshed/missing/still-stale images, cancellation and owner changes; real Unity audio/model integration still requires a new run.
