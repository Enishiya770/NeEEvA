**2026-09-27：完整空间目标、有限任务收尾与失败恢复**

本次根据 11:12–11:25 JST 的 Unity 会话修复工程路径，依据为 [日志评估](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/docs/log-review-20260927-1127.md)。本报告区分程序回归、真实 Qwen 决策回放与尚未进行的新一轮 Unity 场景体验。没有重新部署、重启或调整 Spark 服务。

**实现内容**

1. **单一设备目标以真实结果收尾。** 执行前可以登记完整采纳目标、计划版本、准确实体和目标开关状态。执行后核对 action/entity、终态、新观测、设备绑定和完整状态读回，条件满足后直接结束该目标。如果剩余文字只是同一个原始单一目标，不再因此追加 Room 和 General 请求。复合目标、未知目标、独立剩余步骤以及持续跟随不能借一次开关成功全部清空。`already-satisfied` 保持“原本已经满足”的语义。
2. **成功结果与回复失败分开。** 同一输入里，关灯等动作已经成功，后续草稿格式或理解失败时保留实际完成的步骤；不再把后续回复失败说成此前没有做动作。失败关闭按模型、输入代和工作 epoch 去重，纠错流程换 speech round 不会重新播同一故障回执。下一真实用户输入仍能正常回复；Fast 开启、Actor compiler 关闭的配置也能结束失败。
3. **完整目标贯通判定、规划和中断记忆。** Actor v5 增加执行范围、精确角度、设备目标状态和实体字段。固定头部预设仍只有左右 18°、上下 12°，不能代替身体转身 90°、寻找物体或后续开灯。完整解释独立存为 `CompiledGoal`；原角色草稿继续作为来源证据。两者一起进入后续规划和检查点，避免中断后只剩“我转过去”的简略台词。历史计划投影只保留事实，移除“继续下一步”等实时规划规则，不自动恢复旧任务。
4. **给 Fast 和 Actor 提供有限空间记忆。** 保存已观察实体的来源、时间、观察时位置/视角及历史设备结果；默认最多投影六个对象，并保留最近操作对象作为纠正的指代候选。历史位置不证明当前可见、可达或状态未变，操作仍须通过原有同实体新观测和执行检查。没有扫描未见物体来制造全知场景描述。
5. **物体匹配期间保留已稳定头部姿势。** 在同代物体准备、旧姿势已稳定且仍由同一 owner 保持时，不再仅因为 scene action 进入 preparing 就让旧姿势回正。新视线真实接管后，旧任务只释放自己的 owner，不释放后继任务。身体旋转、用户插话、失效绑定及真实视角过期检查保留。物理角色表现仍需新场景验证。
6. **无效 Fast 判定不再自动变成空间任务。** 无效响应允许最多一次重新决策，再次失败则结束本轮恢复；HTTP 失败不盲目重试。歌词、引用和反馈不会仅因 Fast 格式失败而直接创建 Room 目标。有效模型明确选择 legacy 时仍可正常规划。
7. **保留模型明确填写的等待条件。** 对严格合法的 v5 单一头部预设，当模型同时填写 `kind=act` 与 `condition=user_count_three` 时，编译为待触发 wait，保留原 kind 供诊断。条件不是从用户关键词推断的，仍要求完整草稿证据、原有数字来源检查和当前 owner。claim、复合动作、部分证据、精确角度等不适用此转换。

主要生产文件为 [ActorIntentProtocol.cs](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/Assets/AIChatTookit/Scripts/Chat/ActorIntentProtocol.cs)、[ActorPlanRuntime.cs](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/Assets/AIChatTookit/Scripts/Chat/ActorPlanRuntime.cs)、[ChatSample.ActorPlan.cs](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/Assets/AIChatTookit/Scripts/Chat/ChatSample.ActorPlan.cs)、[SpatialObjectMemory.cs](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/Assets/AIChatTookit/Scripts/Chat/SpatialObjectMemory.cs)、[ChatSample.FastDecisionRecovery.cs](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/Assets/AIChatTookit/Scripts/Chat/ChatSample.FastDecisionRecovery.cs) 和 [ChatSample.FastHeadSceneHandoff.cs](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/Assets/AIChatTookit/Scripts/Chat/ChatSample.FastHeadSceneHandoff.cs)。配套上下文、Fast 提示词、Room 可选完成条件和纠错入口同步更新。

**延迟与并行评估**

上次会话普通回复首音中位数约 4.04 秒；录唱约 21.57 秒。两次开关灯在真实动作完成后又等待约 11.65、12.45 秒才开始回复。这次针对的是后两类多余的后续规划/纠错，而不是承诺所有对话首音都会下降同样的秒数。

设备快速收尾需要执行前已有合法单一目标完成条件；未知像素开始的 inspect→set 流程，以及模型仍判成 compound 的目标，不会事后凭一次开关成功改成 atomic。该保守边界仍可能保留后续规划等待。

录唱的目标审核依赖当轮素材和规划结果，不能直接与规划同时启动。此前测得素材规划中位约 5.37 秒、审核约 9.50 秒、SVC 约 0.52 秒；为半秒级转换引入推测执行、旧素材取消和版本管理，本轮收益不足。保留已有 TTS 准备与 Actor 判定并行。日志中一次 76.48 秒 queueDelay 是旧截止时间的诊断值，该轮规划结束至审核开始实际约 0.078 秒，不能据此前者声称消除了 76 秒等待。

**真实 Qwen 回放：通过结构验证不等于语义全面通过**

只在 Spark 模型空闲时，使用生产 prompt/schema 工厂生成的请求顺序访问 `192.168.114.175:8888`。请求直接发送到模型，不提交 Unity 动作、不播放音频、不改对话历史。Actor 使用生产上限 384 token，Fast 使用 256；全部保留请求、原始响应和调用前后 metrics。

- 最终保留版 Actor 共 17 个样本，全部通过生产解析器与结构校验。完整请求耗时中位 1.747 秒，范围 1.324–2.115 秒，全部正常结束，没有输出截断。这是离线判定 HTTP 耗时，不能与用户停止讲话到首音直接比较，也不是严格的前后性能 A/B。
- “转身 90° 再开灯”、原日志 c15 简略承接、关→开纠正、明确拒绝、只做小幅头部动作、选择走向用户、引用不行动、数三等待、解除姿势及三段录唱，在最终样本里保留了关键意图边界。c21 和明确单一开灯样本提供了可绑定的 lamp-1/on 条件。
- **原日志 c23 草稿仍被判成 compound。** 模型把“看到了……我把它打开”中的感知描述再次解释成确认步骤。其开灯目标还在，但不会取得本次新增的单一设备直接收尾条件。因此不能宣称该原句已经稳定省去全部后续模型调用。
- **Fast 六个样本仍有真实失败。** 歌词初次及唯一恢复请求都引用了旧的“先停下跟随”，会被当前输入证据检查拒绝；代码关闭错误恢复，不提交停止或自动创建 Room 任务。另一“重新开灯”样本由角色自行增加了数三等待；历史位置问答仍出现未被样本事实支持的颜色/相对位置。普通跟随与 90° 复合目标路由正常。不能把六个 HTTP 成功当作六个语义成功。
- 一版四分支等待 schema 在实测中把“等数三”输出成 act/none，可能提前动作，已撤回并保存失败证据。最终保留三分支及显式条件保留编译。最终数三样本正确输出 wait；这不保证未来模型永远不会漏掉条件。

回放证据：[Actor 最终响应](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/spatial-goal-closure-20260927/live-actor-retained/results.json)、[Fast 最终响应](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/spatial-goal-closure-20260927/live-fast-release/results.json)、[耗时统计](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/spatial-goal-closure-20260927/retained-model-timing.json)。实验候选与保留版分别存档，没有用早期较好结果替代最终结果。

**本地验证与边界**

最终完整编译覆盖 378 个 C# 源文件；19 组隔离 Unity EditMode 测试全部通过，共报告 1583 条断言。新增等待调度检查从原始响应经生产解析器进入实际 Actor callback，验证一、二不触发，三只触发一次，重复回调、旧 epoch、无来源等不能执行。10 套纯回归均通过；受影响源码修改后补跑对应套件，Actor 最终 789 条、plan 108 条、FastContext 830 条检查通过。最终编译与 EditMode 对应源码哈希均一致，未发现测试结束后的源码变化。

隔离测试使用受控回调与合成组件；没有把它称为实机视觉、真实麦克风或音频播放测试。主 Unity 进程 15620 保留；主场景与 QualitySettings 哈希不变；隔离测试进程已结束，两份测试工程 DLL 按运行前字节恢复。详细结果见 [独立验证报告](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/spatial-goal-closure-20260927/integration/review.md)、[验证摘要与源码哈希](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/spatial-goal-closure-20260927/integration/validation-summary.json)、[Actor 生产解析结果](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/spatial-goal-closure-20260927/live-actor-retained/production-validation.json) 和 [Fast 生产解析结果](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/spatial-goal-closure-20260927/live-fast-release/production-validation.json)。

复合目标的 `planRemaining` 仍依赖模型理解；本轮保证完整目标可继续被看见和恢复，没有为任意复杂目标建立全部条件的程序证明。错误的空余项仍可能导致复合计划提前结束，这一限制没有通过简单计数、自然语言正则或额外每轮调用掩盖。后续重点应是对这类剩余步骤和 Fast 历史干扰做有对照的评估，而不是继续堆叠提示词并假定已经生效。

下一轮场景验证应覆盖：同一盏灯单独开/关后的回复收尾；先保持偏头再观察灯；90° 转身后操作灯并中途更正关→开；数到三之前不行动、三之后只行动一次；持续跟随中仅唱一段歌时不停止跟随、不触发无关空间任务。等 Unity 编译完成后重新进入一次 Play，才能验证新代码的完整体验。
