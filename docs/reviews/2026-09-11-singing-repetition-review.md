# 2026-09-11 最新 Unity 复读复盘

后续状态：已按本文与感知核查实施修复，详见 [修复及验证记录](2026-09-11-speech-song-loop-fix.md)。下文保留当时日志结论。

结论：本轮是正式模型反复生成相近承诺，并原样提交同一个被拒绝的演唱请求。执行器拦住了错误动作，但纠错与事项续接仍反复外放，直到预算耗尽。上轮修复了观察交付和动作校验，并未解决这种“已经收到失败事实，却没有改变方案”的收口问题。

本次读取日志和代码、保存分析记录，没有修改运行代码或重启 Unity，也没有将真实对话发送给模型回放。

## 证据范围

- 最新 Editor.log 最后写入：2026-09-11 00:48:11；快照采集：00:49:10，日本时间。
- [原始快照](../../Logs/repetition-20260911/004910/Editor.snapshot.log)，750,162 字节。
- SHA256：`5DF806E66401F8B30E2B580D17CF4C7F9B760DBEAFBDBD3F6345F8F931A16165`。
- 以下行号均引用 [换行规范化日志](../../Logs/repetition-20260911/004910/Editor.normalized.log)。日志没有给每条事件加绝对时间，因此只对感知帧使用其明确记录的时间。

## 对话与执行时序

之前的报时问答已在 [4060 行](../../Logs/repetition-20260911/004910/Editor.normalized.log:4060) 正常关闭。

最新用户整轮输入在 [4973 行](../../Logs/repetition-20260911/004910/Editor.normalized.log:4973)：中文开头是“没什么问题嗯，试着唱下歌吧。我。”，随后有两句日语歌声。分轨覆盖头部 0–5.63 秒、第一句 5.63–10.67 秒、第二句 10.67–16.46 秒。没有唱后新的口语请求。**用户在后续核查中已明确澄清：这一轮没有要求角色回唱。** 应按用户先说话、再自己演唱理解，不能把 ASR 的破碎标点当成回唱指令；角色登记的第一句演唱目标也不能倒推成用户需求。见 [感知与意图补充核查](2026-09-11-speech-to-song-perception-review.md)。

| 阶段 | 正式输出与实际结果 |
|---|---|
| 初次回答：[5455](../../Logs/repetition-20260911/004910/Editor.normalized.log:5455) | 说“了解、明白了、现在唱、准备好了、开始”。登记 `range=current, revisions=0`，同时提交 `sing start_seconds=5.43 end_seconds=10.67`。因目标待审核被拒绝。 |
| 首次事项续接，感知帧 00:47:05：[5851](../../Logs/repetition-20260911/004910/Editor.normalized.log:5851)、[6120](../../Logs/repetition-20260911/004910/Editor.normalized.log:6120) | 目标获批，再次生成相近承诺，仍提交相同起止秒数。[6154](../../Logs/repetition-20260911/004910/Editor.normalized.log:6154) 明确拒绝：目标为 current，实际动作被解析为 window。 |
| 立即工具纠错：[6202](../../Logs/repetition-20260911/004910/Editor.normalized.log:6202)、[6445](../../Logs/repetition-20260911/004910/Editor.normalized.log:6445) | 又说“了解、开始了”，提交原参数，再次被拒绝。 |
| 第二次事项续接，感知帧 00:47:21：[6806](../../Logs/repetition-20260911/004910/Editor.normalized.log:6806)、[6986](../../Logs/repetition-20260911/004910/Editor.normalized.log:6986) | 审查要求改参数；角色道歉“又说了相同的话”，随后仍说现在唱，仍提交原参数。 |
| 第三次事项续接，感知帧 00:47:37：[7291](../../Logs/repetition-20260911/004910/Editor.normalized.log:7291)、[7471](../../Logs/repetition-20260911/004910/Editor.normalized.log:7471) | 审查已明确要求用 approved 的 current；正式模型仍输出原起止秒数和相近承诺。 |
| 停止：[7844](../../Logs/repetition-20260911/004910/Editor.normalized.log:7844) | 事项进入 blocked，下一次考虑延后 146.3 秒。没有演唱完成记录。 |

这个区间共有 **5 次正式 LLM 输出、19 次句子 TTS 播放完成**，没有新的最终用户 ASR，没有自检调用，没有复读拦截日志。各播放记录对应当轮新生成的句子；日志未显示同一份生成被额外重复播放。系统有总预算，并非无限循环，但在停止前已经让用户听了五轮相近承诺。

## 确定的实现缺口

### 1. 错误请求被拦住，但缺少同一失败上的进展判定

五次提交的核心参数相同：同一 clip，原录音窗口 `[5.43,10.67]`。审批之后四次都在同一个 current/window 条件上失败。换一句台词、改成道歉，未改变目标、参数或可用素材。

[QueueHumBack](../../Assets/AIChatTookit/Scripts/Chat/ChatSample.cs:14740) 在统一 sing 校验失败时提前返回；因此没有进入后面的 [相同执行失败重试限制](../../Assets/AIChatTookit/Scripts/Chat/ChatSample.cs:14894)。现有拒绝保护可以阻止播放，却不能据此让事项调度识别“下一次原样提交不会产生不同结果”。

### 2. 等待审核被登记为失败，遗留纠错与三次事项续接叠加

初次目标 pending 属于正常等待阶段，却走了 `material_not_ready` 失败路径，设置一次待纠错标记。[待审核分支](../../Assets/AIChatTookit/Scripts/Chat/ChatSample.cs:5430) 暂停当下 continue，但保留了 `m_ToolCorrectionContinuationPending`。目标获批之后，该旧标记又触发 [6202 行](../../Logs/repetition-20260911/004910/Editor.normalized.log:6202) 的立即纠错。

ToolCorrection 与 [事项续接预算](../../Assets/AIChatTookit/Scripts/Chat/ChatSample.SelfInspection.cs:151) 分别计数。这次实际形成：初答 1 次＋事项续接 3 次＋遗留纠错 1 次。各自有上限并不能保证整体体验没有复读。

### 3. 相同失败的计数还被两种文案反复重置

同一次拒绝先经 `RecordHumBackResult` 写入“未执行：原因”，再经 `ReportToolFailureForLlm` 写入“sing 未执行：原因”。[NoteToolFailure](../../Assets/AIChatTookit/Scripts/Chat/ChatSample.cs:18190) 按完整字符串是否相等计数；两种前缀来回切换，计数就反复回到 1。日志中的后续感知帧也没有呈现累积失败次数。

失败身份应来自工具、实际参数、素材状态与拒绝原因；展示文案不适合作为唯一计数键。

### 4. 现有复读拦截只覆盖文字相同，且纠错提示施加了发言压力

[流式检查](../../Assets/AIChatTookit/Scripts/Chat/ChatSample.cs:5035) 要求开头逐字相同达到 12 字；[整轮检查](../../Assets/AIChatTookit/Scripts/Chat/ChatSample.cs:12463) 要求正文完全相同。此次五轮归一化后的最长历史共同前缀为 0／11／3／3／3 字，全文均不同，故全部通过。chain 有重置检查与即时记账，问题在判据覆盖范围。

[纠错提示](../../Assets/AIChatTookit/Scripts/Chat/ChatSample.cs:18079) 还要求本轮不要使用 silent。这会增加再次解释或承诺的压力，但不能据此断言它是全部五轮重复的唯一原因。

### 5. 已批准的目标本身也不具备所声明的素材状态

[5883 行](../../Logs/repetition-20260911/004910/Editor.normalized.log:5883) 的真实素材是：

```text
playback=evidence_only
current=unselected
revision=pending
clean=[5.43,10.67]
expanded=[5.43,16.46]
```

目标却填写 current、版本 0 并获批。因此这不是两个等价范围的名称差异：当时根本没有已选定的 current 版本。只去掉 sing 的起止秒数，仍会遇到 [版本核对](../../Assets/AIChatTookit/Scripts/Chat/ChatSample.SingingGoal.cs:320) 的 `0/pending` 不符，随后还需准备真实可播放范围。

请求窗口等于 clean，只覆盖第一句；expanded 还保留第二句的 5.79 秒。5.43 比声学岛起点 5.63 提前 0.20 秒有保留起音余量的代码依据，不能把这两个数字不同直接当成根因。

## 已交付的事实与仍不确定的部分

这次 [5915](../../Logs/repetition-20260911/004910/Editor.normalized.log:5915)、[6227](../../Logs/repetition-20260911/004910/Editor.normalized.log:6227)、[6858](../../Logs/repetition-20260911/004910/Editor.normalized.log:6858)、[7343](../../Logs/repetition-20260911/004910/Editor.normalized.log:7343) 均记录 `fact_source=execution_feedback`，用户索引保持 10，正式反馈路径也确实加入最新失败、素材及此前发言。不是上次已定位的“续接丢掉整个最新感知帧”。

另有感知冲突：[4992–5021 附近](../../Logs/repetition-20260911/004910/Editor.normalized.log:4992) 用流式普通说话预判撤回可播放别名、降级整轮；完整分轨却含两句歌声，尾部还有 melodic 窗口证据。[补充核查](2026-09-11-speech-to-song-perception-review.md) 已定位：最后一条否决草稿跑回了上一轮时区话题；最终输入保留歌词但去掉本轮旋律音符摘要，同时加上“本轮是普通说话”的断言。用户并未要求回唱，角色与目标审核却建立了回唱任务。这是复读之前的感知和意图缺口；本次实际执行停止点仍是目标／请求校验，不能把全部复读都归因于 ASR，也不能把声学 island 自动当成用户指定的范围。

## 下一步修复与验证重点

1. 把 pending 审批作为等待状态，不建立可滞留到后续 tick 的失败纠错任务。
2. 对同步校验拒绝也建立结构化失败记录；纠错与事项续接共享进展判断。相同目标、参数、素材状态与拒绝原因没有变化时，停止原样重试，转为一次必要说明或澄清。
3. 目标获准前只读核对素材是否存在、版本是否真实、current 是否已经选定，一次给出完整矛盾，避免每轮只撞到下一道拒绝。
4. 参数修正可以在内部进行；尚未提交可执行动作时不反复外放“开始唱”。文字去重只作补充，不通过粗暴降低短句阈值代替工作状态判断。
5. 增加完整多轮回归：pending 候选＋错误版本＋current/window 不符＋每次换措辞但参数不变，覆盖等待审批、遗留纠错、失败计数与最终说明。真实模型验证应包含正式参数修正轮，不能只检查审核模型的 status。

上轮 2,355 项程序检查与 8 个模型审核案例的通过记录仍属实，但覆盖不足：证明了观察传递、校验拒绝与有限预算，没有证明正式模型在反复收到同一拒绝后会改变方案。这次日志正好暴露了这一缺口。
