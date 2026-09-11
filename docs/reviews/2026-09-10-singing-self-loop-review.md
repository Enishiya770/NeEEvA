# 2026-09-10 Unity 歌唱与自主闭环评估

本次依据最新 Editor.log 快照，重点从截图 23:15:54 前的完整用户请求，追踪到最后一次“第一段前半部分仍未补回”的反馈。只做日志与代码评估，没有修改运行代码、素材或角色记忆，也没有重新启动 Unity。

结论：演唱执行链能够工作，自检和未完成事项续接也确实运行了；但正式角色未完整收到续接时的新执行事实，生成的动作参数没有可靠地对应用户要求，完成后又缺少目标验收。这些断点共同造成“承诺行动→重复确认/闲聊→错误解释”，以及后来的“说补回开头→实际重播旧裁剪”。

## 证据范围

- 原始日志：[Editor.snapshot.log](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.snapshot.log)。源日志最后写入时间为 2026-09-10 23:22:58；快照采集于约 23:28。
- 原始快照 SHA256：`618B9F6247A8E3875B0F7E48CA206E734E7857BD9514A1FEDCBF7AEE00B12AA2`。
- 为避免裸 CR 造成工具行号差异，以下日志引用统一指向 [Editor.normalized.log](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log)，仅统一换行，保留原始快照。
- 23:15:54～23:16:37 的绝对时间来自用户截图；其余按日志对话顺序描述。感知帧时间不等于后续响应完成时间。
- 本评估没有试听原始录音；可确认保留了额外头部音频及相关转写线索，不能仅凭日志保证扩大范围后恰好包含全部目标旋律。

## 对话与实际执行

| 时点/对话阶段 | 用户要求、角色表达 | 实际执行与判断 |
|---|---|---|
| 23:15:54 前 | 用户：把刚才唱的两段连起来唱给我听。 | 明确要求演唱两个现有录音片段。不是只确认来源。 |
| 23:15:54 | 角色答应连接演唱，说“少し待ってて”。 | 只发 `clip_confirm` 和 `next`。确认第一段来源，未提交 `sing`。 |
| 23:16:10～13 | 续接后说“さあ、いくわよ”。 | 又确认同一个 clip，并发 `continue`；仍未提交演唱。 |
| 23:16:16～22 | 说有点紧张、与用户在一起很安心。 | 生成闲聊和下一次思考时间，continue 链结束，歌唱 Skill lease 释放。用户要求尚未完成。 |
| 23:16:34～37 | 用户问为什么没有唱；角色说发声功能尚未连接。 | 此前没有演唱请求，所以不存在这次演唱请求失败的证据。TTS 正常发声，SVC 预热也报告 ready。该解释没有依据。 |
| 用户要求检查技能后 | 角色调用 `body_inspect scope="runtime"`，收到结果后改口。 | 自检实际执行，随后真正发出 `sing`，但只选择第二段。11.4 秒音频完整播放，用户随后明确反馈已经听到且很好听。 |
| 用户再次要求两段连接 | 指出刚才只唱了一段。 | 这次提交两个 refs，约 19.9 秒音频完整播放。 |
| 用户指出第一段漏了开头 | 角色答应补回；未完成事项审查也明确提出使用 expanded。 | 实际 `sing` 只在 `reason` 写 expanded，未传 `range`；解析仍为 current，播放第一段的旧 clean 范围。 |
| 用户进一步限定“只唱第一段” | 提示漏掉了含“卢浮宫”的部分。 | 角色仍提交两个 refs，仍缺少 range，实际又播原来的两段和旧范围。两个要求都没有落实。 |
| 用户再指出没有修好 | 角色说检查过，原录音本来不含那部分。 | 没有扩展播放或修订动作支持此结论。保留的头部及相关转写反而要求继续核查。 |

主要日志入口：

- [最初完整请求](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:24716)、[首次只有来源确认](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:25050)、[重复确认与 continue](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:25394)、[闲聊结束链条](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:25630)。
- [未经核验的能力解释](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:26243)、[自检实际执行](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:27167)、[首次真实 sing](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:27589)、[播放完成](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:27879)、[用户确认听到](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:28268)。
- [补回开头的续接决定](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:30790)、[实际仍是 current](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:31001)。
- [完整的“只唱第一段”要求](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:32241)、[仍发两个 refs](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:32696)、[旧范围的完成记录](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:32972)、[最后的缺失断言](E:/VRproject/VR20250611/NeEEvA/Logs/singing-followup-20260910/Editor.normalized.log:34174)。

## 确定的实现断点

### 1. 新感知帧没有完整交给事项续接的正式模型

[DispatchPreparedAgentTick](E:/VRproject/VR20250611/NeEEvA/Assets/AIChatTookit/Scripts/Chat/ChatSample.cs:8792) 同时持有 frame 与 workFeedback，但事项续接分支仅令 workFeedback 等于 BuildWorkContinuationContext。后者包含请求、模型已经输出的内容、检查结果、动作反馈、是否有工具运行等字段，没有歌唱素材与最新歌唱执行事实。

随后 [StartStreaming 的 workFeedback 分支](E:/VRproject/VR20250611/NeEEvA/Assets/AIChatTookit/Scripts/Chat/ChatSample.cs:4514) 只转交 transientSystemContext。 [DispatchWorkFeedback](E:/VRproject/VR20250611/NeEEvA/Assets/AIChatTookit/Scripts/Chat/ChatSample.SelfInspection.cs:219) 补充的是动作/自检上下文，没有把 frame 取回。ChatQW 最后把 executionFeedback 加入请求末尾；这条路径中 frame 没有进入模型请求。

因此日志里“FireTick 打印了某事实”不能证明正式角色实际看到了该事实。该次日志中 frame 已是 confirmed_list=2，但历史 user 消息里仍是 confirmed_list=1/pending_list=1。请求诊断只抽取最后 user，单看它不足以下结论；上述参数传递链才是确定证据。

更进一步，[BuildPerceptionFrame](E:/VRproject/VR20250611/NeEEvA/Assets/AIChatTookit/Scripts/Chat/ChatSample.cs:9304) 在构造时就消费了来源确认结果。预判先构造 frame，正式续接又丢掉 frame，会使工具结果被预判看到，却没有完整交回真正作出行动的角色。这能解释重复确认的倾向，但不能宣称已证明模型每次错误的唯一原因。

### 2. 续接会运行，但不保证任务上下文贯穿整条链

23:16:10 并非没有运行新机制。调用栈明确经过 `BeginAutonomyIntentProbe:8887`、`DispatchWorkFeedback`：这是进度预判无法解析或 work_status 无效后的受限恢复分支。该分支没有打印“未完成事项放行”，所以从截图表面容易看漏。

该次为什么格式无效尚不确定：现有日志没有保存诊断所需的 finish_reason/简化状态。进度判定复用了 128 token 的临时草稿预算且没有专用 JSON schema，是值得检查的风险，不能直接断言本次发生截断。

随后 `continue` 走 [另一条正式续接路径](E:/VRproject/VR20250611/NeEEvA/Assets/AIChatTookit/Scripts/Chat/ChatSample.cs:5629)。上一轮的事项上下文是临时请求内容，没有持续附带到该链。因此“有下一轮”与“下一轮仍围绕原事项、收到新结果”不是同一件事。

### 3. 描述中的 expanded 不会改变实际音频范围

[ConfigureUnifiedSing](E:/VRproject/VR20250611/NeEEvA/Assets/AIChatTookit/Scripts/Chat/ChatSample.SingingProtocol.cs:216) 在缺少 range 时使用 current，reason 不参与选择。当前素材的 clean 为 `[8.19,16.70]`，expanded 为 `[2.63,16.70]`，额外保留头部 5.56 秒。

模型多次说补回开头，实际输出的控制标签没有 `range="expanded"`，也没有调用 `clip_revise`。执行器没有吞掉参数，而是按收到的 current 播放。因此 revision 0、clean 范围不变是符合当前指令的结果，但不符合用户目标。

用户进一步要求只唱第一段后，两个 refs 仍同时存在，是另一个独立的目标失配。

只针对本次已有保留头部，合理的候选动作形状为：

```xml
<sing refs="clip:4542fee1-1" range="expanded" reason="只重唱第一段，纳入保留的前置录音"/>
```

也可先通过 `clip_revise` 修订并核对版本，再播放 current。这里仅展示契约，没有实际执行。扩大范围能否完整补回指定旋律，还需依据音频/转写与用户反馈验收。

### 4. 自检能力与完成证据还不充分

[runtime 自检](E:/VRproject/VR20250611/NeEEvA/Assets/AIChatTookit/Scripts/Chat/ChatSample.SelfInspection.cs:205) 读取 agentRunning、speechPlaying、队列、toolInFlight、memoryAvailable；没有核验 singing 是否启用、服务健康、具体演唱请求失败原因。因此它是有效的本地状态检查，却不能单独证明“歌唱准备没有完成”或“服务已连接”。后续真实播放与用户反馈证明能力可用。

`clip_confirm` 只确认来源。它的反馈中 [played=false](E:/VRproject/VR20250611/NeEEvA/Assets/AIChatTookit/Scripts/Chat/ChatSample.SingingProtocol.cs:184) 是本次确认动作没有播放的固定表述，不代表历史播放记录被清零。后半段已有完整播放记录时仍返回这个字段，作用范围不清晰，容易进一步混淆模型。

“Unity 已播完”只能说明动作执行完成，不能证明播的是用户指定的片段和范围。这次 PlaybackFact 已保留 refs、revision、source、raw 范围，足以核验“两个 refs＋旧 clean”并没有满足“只唱第一段＋补开头”。但这些事实尚未被稳定地用于目标验收。

## 对闭环方向的建议

优先把已有只读观察和受控工具串成可靠闭环，暂不需要赋予角色任意修改工程文件的权限。

1. 所有事项续接、自检结果和 continue 链，都交付最新的执行事实，并带上当前用户目标及后续修正；工具结果应在正式消费后再标记已交付。
2. 用结构化目标表达本轮要处理的 refs、顺序、范围、完成条件。提交前核对解析后的动作是否满足这些条件；reason 只用于解释，不能充当参数。
3. 将“来源已确认、可播放、已受理、正在生成/播放、Unity 播完、用户确认听到、满足指定内容”分开记录。核验能力或宣布无法完成时引用对应的实际证据。
4. 用户指出没有修好后，对相同 refs、版本、范围的原样重试识别为没有改变方案，重新检查；不要把重复播完记作修复成功。
5. 让进度审查有可诊断的结构化输出、预算和失败状态。安静可以保留，但不能把“没有新话题”用于关闭仍可推进的用户事项。

后续验证应覆盖完整多轮场景：确认后真正演唱、工具结果到达正式模型、continue 保留事项、用户改为只唱第一段、expanded 真正改变版本和范围、播放完成但目标未满足时继续纠正，以及无证据时不编造能力故障或录音缺失原因。仅测试单轮进度分类和模拟工具闭环不足以覆盖本次问题。
