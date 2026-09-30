# 2026-09-27：任务收尾、空间交接、录唱来源与回声续录修复

对应分析：`docs/log-review-20260927-1308.md`。本次按用户“按你说的做”实施，针对 12:43–13:03 测试中已复现的失败链路。原始日志固定在 `work/log-evaluation-20260927-1308/Editor.log`；修改前文件按原目录结构保存在 `work/runtime-convergence-20260927/before/`。

## 结论与范围

问题由协议上下文、任务生命周期、执行控制权及录音来源管理共同造成。确有旧协议干扰：合法语言元数据会被写成“未知标签，动作未执行”的反馈；部分审核还继承角色的旧工具格式。它不能解释全部回归，尤其不能解释匹配期间视角复位及被拒回声 PCM 再次进入续录。

本轮修复这些具体路径，没有更换模型、改变 DGX 部署、调整已有跟随避障距离、保存场景或启动主项目的新 Play 会话。角色仍可以拒绝、改变主意或提出不同目标；动作声明的核验不能倒过来授权一个本未提交的动作。

## 生产变化

### 1. 原子设备目标的完成条件与一次性收尾

- `ActorPlanRuntime` / `RoomTaskProtocol` 允许首次观察识别出设备后，**在实际开关操作或必要接近之前**，登记同一实体的最终开关状态。过去只能在第一个规划回合登记，导致先找灯再关灯漏掉完成条件。
- `approach-object` 可以携带最终状态条件，但到达本身不算开关成功。只有同一设备、同一目标的真实执行状态验证才能结束该目标。
- compound 目标及独立后续任务不被压缩成一次开关。禁止事后补合同来吸收旧的未验证写入；恢复 checkpoint 时检查原始目标、来源、实体、状态和登记前的观察历史。
- `ChatSample.TaskSettlement` 给终止结果登记本轮工作标记。晚到的反馈不能重新启动已结束的同一工作；新用户轮次、正在执行的工具、尚未交付的独立查询结果和未完成演唱保留自己的生命周期。
- Room 已有成功执行记录、剩余目标仍是原整句、又无具体后续路由时，保留实际成功记录并报告剩余部分未确认，避免再把整句交给 General 重做。独立的新查询尾项继续走原工具转交。
- 发布收尾前先领取该计划版本的一次性发布权，防止同步日志回调或重复完成回调造成双重收尾。

### 2. 物体操作准备期间保持已获得的视角

- `ChatSample.SceneAttentionHandoff` 与 `NevaSceneAttention` 在下一物体动作通过原有检查后，将已稳定视线直接交给新动作，再进行异步匹配。
- 保留同一角色、眼部、身体控制权及旧动作来源检查；旧 verifier 的释放只影响旧 owner。取消、失败、超时释放新 owner。
- 被拒绝的中间提案不再抹掉真正保持视角的 owner 来源，因此下一次有效重试仍可继承视角。
- `visibleKnownObjects[].deviceOperationPrerequisite` 提供当前可见已登记设备的真实距离门槛。太远时 Room 必须明确安排接近、保留开关目标，并在接近后重新获取同实体的最新观察；执行器不偷偷代走导航，也不放松视觉时效检查。

### 3. 去除本轮路径中的旧格式冲突与多余串行选择

- `<lang>` / `<speech>` 等现有元数据不再被误认作未知动作。真正的未知片段只产生局部解析数据，不再说整轮动作都没发生或要求重写旧标签。
- 已包含空间能力字段的 Actor 提案直接进入 Room，避免再调用一次非空间 skill selector。该变化减少一段串行模型请求，不等于全链路延迟已经实测下降。
- “初判采纳动作、复审却判纯对话”的矛盾会进入原有有界失败处理，不再把尚未落实的承诺原样播出。复审 prompt 明确区分主动转移视线和单纯说“看见了”。不增加第三次模型请求，也不把所有用户命令强制执行。

### 4. 录唱来源、独立审核及专用请求

- 素材选择使用稳定 clip 身份，ready/pending 的转换不会重新编号。按真实录音事件排序，事件序号不等于歌曲段号。
- 独立审核获得所有候选的完整转写、范围、来源事务、当前原话、近期真实输入、角色当前选择与实际播放记录。`goal.expected` 只是待审自述，不能把第二段描述移植到第一段音频。
- 已启用的标准录唱路径使用专用规划器规则与完整 JSON 事实两条消息，去掉角色扮演前缀、旧 assistant 标签历史、通用工具手册、重复感知帧和 TrailingContext 对录唱的再次决策干扰。角色本轮的选择保存在结构化 current_intent 中。普通 WorkReview 也不再继承角色 system 指令。
- pending 不等于没录到；ASR 混合语言、未知歌名不等于音频损坏。接受回唱须实际提交素材；reply 用于真实拒绝或必要澄清。
- 候选表补充实际 WAV 检查得到的 `RawAudioRetained`、`RawAudioByteCount`、`HasAvailableOriginalAudio`、`PreparedAudioAvailable`，明确区分音频留存、播放准备和声音来源确认。之前只有 pending 与“未选播放范围”的状态，模型会错误推断第二段没有录到。这些字段不替模型判断哪条是第二段，也不提升播放权限。
- 来源审核只核验语义与实际材料，不重复臆造已经通过编译器检查的字段错误。命名范围强制 null 坐标；自定义 window 才填写数值。current/retained/mixed 关系由真实 clip/capture 事实支持。
- 实际传输固定 `enable_thinking=false`、`temperature=0`、`max_tokens=512`，避免正式路径落到服务默认采样而与短输出验证不一致，也不受普通对话 thinking 开关影响。schema 关联素材与其可用范围：尚未选定播放范围的 pending 素材不能选 current。此类可在现有表内修复的错误留在有界修复路径，不扩大成 General handoff。
- **General、自定义 prompt fallback 和真正表外音频操作的显式 handoff 仍保留旧工具能力。** 本轮没有宣称全工程已移除 XML。来源确认、版本、音高、PCM 和播放检查也没有绕过。

### 5. 被拒回声的录音继承隔离

- sealed capture 的最终 self-speech 拒绝绑定在录音身份上，晚到或 detached 回复仍能登记。换续录编号不洗掉该拒绝事实。
- 新续录不继承已拒旧 PCM 或其累计流式识别状态，预录缓冲也受旧录音终点限制。
- 已形成的续录只裁除被拒的继承前缀，保留新用户后缀。如果祖先拒绝在请求发出后才到达，在发布前撤回混合结果并重新识别后缀。
- 预识别可能已保留更长的混合音频，因此隔离时还撤销该 capture 的旧候选、播放别名和旧 clip 绑定。真实新用户后缀重新取证并获得新音源身份，防止旧数值版本或 pending 目标套用到不同 PCM；其他录音不受影响。普通 pending→ready 准备过程仍保持稳定 clip。
- 不按文字相似度删用户句子，不把全部外放期间的录音禁掉，不改变正常语音结束门槛。

## 验证与证据

验证汇总由 `work/runtime-convergence-20260927/integration/validation-summary.json` 记录。最终 run-07 的 25 组隔离 Unity 回归全部通过，明确报告的数值断言为 **1,699**；布尔检查及两个请求 exporter 不计作数值断言。使用新编译的 388 个 C# 源文件及 164 文件测试依赖闭包，编译后源码哈希变化为 0。

其中，回声续录与旧素材撤销 51 项、任务收尾及重入 36 项、视线及设备交接 64 项、标准 prompt 与实际 provider wire 31+17 项、稳定录音及 PCM 留存事实 15 项通过。十套独立 pure suites 通过，最新录唱编译器 185 项、任务闭环 81 项；输入两套 pure 各 32 项，未重复加进 Unity 断言总数。

主 Unity PID 15620 与命令行保留，9 个相关 scene 加 QualitySettings 共 10 个文件哈希一致；隔离 Unity 已退出，临时替换的两份 fixture DLL 已按原始字节恢复。`SceneObjectReferenceRegression` 的真实房间 PlayMode 组已编译但没有执行；本轮实际运行的是 EditMode 对象、合成 PCM 和受控回调验证。

详细分项证据：

- `work/runtime-convergence-20260927/scene-handoff-review.md`
- `work/runtime-convergence-20260927/input-isolation-review.md`
- `work/runtime-convergence-20260927/recording-convergence.md`
- `work/runtime-convergence-20260927/integration/edit-results.json`
- `work/runtime-convergence-20260927/integration/pure-results.json`

这些验证能检查实际生产路由、控制权、状态闭环、材料绑定和请求构建，不能替代真实麦克风、外放重叠声、完整场景视觉以及最终演唱试听。少量 Qwen 语义案例也不能给出总体准确率；模型若在初判和复审中一致误解内容，程序不能只凭元数据证明它错了。

### 实际 Qwen 的 Actor / Room 请求

`actor-room-production-report.json`：生产请求构建器、生产 schema、实际 Qwen HTTP 及生产 parser，6/6 通过。c41 原句采用受控上下文，其余用例也是受控重建；不是原场景逐字逐图回放。

| 用例 | 实际结果 | 本次 HTTP 耗时 |
|---|---|---:|
| “好，这就把视线移过去。” | adopted_action | 1.24 s |
| 明确不转头、继续聊天 | none | 1.72 s |
| 身体右转 90° | replan / body_turn / 90° | 1.77 s |
| 已确认灯具，但距离超限 | 同实体 approach，登记 off 条件，保留关灯目标 | 3.71 s |
| 接近完成，原条件仍有效 | set off，剩余目标为空 | 3.30 s |
| 先关灯再向右转身 90° | set off，不压缩成原子目标，保留具体转身尾项 | 2.36 s |

后两次设备动作没有实际执行；inspect/arrived 是预置事实，Room 未附 RGB，故这些数据不证明视觉识别、导航或开关成功，也不等于用户首音延迟。原始请求与响应完整保存在 `work/runtime-convergence-20260927/actor-room-production-*`。

### 实际 Qwen 的录唱规划与独立审核

最终 facts 版本的一次完整回放：**决策 8/8 达到各自期望、审核 2/2 达到各自期望**。判分包含真实生产编译结果和有序 refs、范围兼容性；编译成 handoff 不算提交演唱。本次没有 handoff。

| 用例 | 本次结果 |
|---|---|
| 请求第二段 | 提交真实第二段 clip |
| 混合 ASR 歌词纠正 | 提交真实第二段 clip |
| 回到第一段 | 提交第一段 clip |
| 第一段后接第二段 | 按第一段→第二段顺序提交 |
| 取消、引用请求 | 不提交演唱 |
| 角色已采纳第二段目标 | 实际编译 sing 至第二段 |
| 角色已明确拒绝 | 保留拒绝，不提交演唱 |
| 错选第一段的待审草案 | revise，独立指出第二段 refs |
| 正确选择 pending 第二段的草案 | approved，来源关系、范围与坐标编码一致 |

**不能据此宣称输出稳定或总体准确率为 100%。** 在最终完整回放之前，对同版本前两个用例做的预检为 0/2：模型明确说“这次先不唱”，没有再谎称未录到或口头答应演唱。两次 `requestWire` 完全一致（包括素材身份、温度和 schema），后来完整回放却选择 perform；这份分支波动原样保留，不归咎某个未经验证的服务原因。没有通过修改预期答案、强制 perform 或删除拒绝权限来取得最终一次通过。用户要求的“已采纳意图能落实”和“角色可拒绝”分别测试，缺少已采纳目标的直达路径仍需在真实会话中观察其选择是否合适。

本次决策 HTTP 全响应耗时 1.97–2.82 秒，中位 2.07 秒；两次审核为 4.68 / 4.55 秒。只包含各自请求，不含用户停顿、录音结束、前置规划、TTS、音频准备与实际播放，不能作为首音指标。

证据：`recording-final-decision-model.json`、`recording-final-review-model.json`、`recording-audio-availability-two-model.json` 及 `recording-convergence.md`，均在 `work/runtime-convergence-20260927/`。早期缺采样参数、误把 pending 当未录制及范围 handoff 的失败另存于 `recording-pre-sampling/` 和后续迭代原始记录，未用最后一次通过覆盖。

## 下一轮场景验收

在主 Unity 完成脚本重编译后重新进入 Play，优先连续验证：先转头找到灯再关灯、距离较远时接近再操作；分别录两段再要求第二段、回到第一段及两段顺序回唱；外放说话中插入真实用户语音。对每轮对照真实 action ID / entity / clip 与最终声音，而不只看口头回复。首音延迟需要用新的 capture-close 到 AudioSource 实际播放进度重新统计。
