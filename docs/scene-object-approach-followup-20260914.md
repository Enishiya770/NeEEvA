# 物件接近与决策恢复：2026-09-14

本批交付验证已通过，可以重新进入原房间 Play 测试。**真实 Qwen 第三轮四项决策全部通过；真实 ARDY 走近后重新确认同一盏灯，左右半周转身及取消也已通过。** 模型决策和动作执行分别验证，不代表完成了一次包含 ASR、语义识别、走路与语音外放的完整端到端会话。下述保留前两轮失败及各轮测试条件。

证据目录：[`Server/ARDY/runtime/scene-object-approach-followup-20260914`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/)。使用既有房间和角色的隔离副本；没有修改用户主场景或搭建新测试场，没有用运行时瞬移代替行走。

## 真实 Play 的失败原因

[`goal-reference-diagnosis.json`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/goal-reference-diagnosis.json) 对应保存的 `user-editor.log`：

- 前一次 `look-at`（动作尾号 `:5:3`）已正确匹配电视柜上的小灯，并在新图中确认同一实体；日志行 6258、6282、6309。它是直接观察，没有创建待续接的观察子目标。
- 用户随后说“那你能试着靠近他吗？”（行 7045）。动作 `:7:5` 的像素仍指向同一盏灯，但在行 7245 被 `observation-goal-reference-not-pending` 拒绝，尚未进入站位规划或 ARDY 请求。
- 该拒绝分支说明决策带了非空 `observationGoalId`，而引擎当时没有对应待续接目标。**旧日志没有保存完整决策 JSON，无法确定错误 ID 的具体值**；不能声称它必然等于灯的实体 ID。报告中空白的 `SceneActionFact.observationGoalId` 是校验前尚未赋值，也不能反推模型未传 ID。

这是“已知物件引用”和“观察子目标续接”的协议混淆。旧约定要求读取顶层 `pendingObservationGoal`，旧输入却仅在 `objectTargetReview.observationGoal` 内提供相关信息，加重了歧义。另有未完整结束的模型输出；旧证据不足以把 `incomplete` 一律归因于 token 耗尽。

## 本次改动及边界

1. 决策输入明确提供 `pendingObservationGoal`、允许引用的准确 goal ID 列表及实体引用规则。刚刚确认的物件可通过 `entityTarget` 接近，不必创建或填写观察 goal。
2. 错误 ID、字段、旧轮证据等可修复的决策在动作提交前被拒绝；允许取得新图后，由模型**重新决定一次**。程序不删除错误 ID 后强行执行，也不把失败动作改写成别的动作。第二次仍不合法便停止尝试并进入结果回复。
3. 请求末尾再次给出本轮真实用户原话，`origin=user` 的 evidence 只可摘自本轮；对话历史仅用于理解指代。决策输出预算为 640 tokens；未完整结束的响应仍不执行，并记录结束原因与可用的 usage，缺失数据保持未知。
4. 原地身体转身允许左右最多 180°；小幅转头仍限制为 yaw ±50°、pitch ±25°。转身轨迹同时校验累计有符号角度，不能仅凭最终朝向相同便接受反方向或多转一圈的动作。
5. 新轮用户输入、停止及取消使旧回包失权；快速停止不等待补拍或重决策。修正后的动作仍须经过语义匹配、新图同实体确认、地形与身体净距检查。抵达地面站位和重新看见物件分别记录，不代表触碰或开关操作。

对应实现：[`ChatSample.RoomTask.cs`](../Assets/AIChatTookit/Scripts/Chat/ChatSample.RoomTask.cs)、[`RoomTaskProtocol.cs`](../Assets/AIChatTookit/Scripts/Chat/RoomTaskProtocol.cs)、[`ChatQW.RoomTask.cs`](../Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.RoomTask.cs)。

## 已通过的验证

| 验证 | 结果 | 证据与限制 |
| --- | --- | --- |
| 决策恢复与取消 | 50 项通过：一次正确恢复、第二次仍错误、取消后迟到回包、误用上轮 evidence | [`guards-r1/scene-decision-repair/report.json`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/guards-r1/scene-decision-repair/report.json)。实际房间和角色视图；决策与语义匹配回包受控，不发模型或 ARDY 请求。 |
| 修正后真实走近 | 50 项通过；真实 ARDY 到达附近站位，新图重新确认同一盏灯 | [`approach-native-r1/scene-decision-repair-native/report.json`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/approach-native-r1/scene-decision-repair-native/report.json)。导航、动作生成、播放和角色摄像机真实；决策及语义 matcher 回包受控。 |
| 原地左右半周转身及中途停止 | 39 项通过 | [`half-turn-r1/scene-orient-half-turn/report.json`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/half-turn-r1/scene-orient-half-turn/report.json)。受控 ±180° 意图，实际 ARDY 和骨骼播放；不代表自由语言选择已验证。 |
| 既有整合检查 | `guards-r1/report.json` 状态为 passed | [`完整报告`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/guards-r1/report.json)，包括物件、视觉、持续跟随、快速停止、语音前动作准备等既有回归。没有 ASR 或实际语音外放。 |

恢复测试主动构造“把实体 ID 填入 observationGoalId”的坏决策，以覆盖此次失败门槛；这是**受控复现输入，不是从旧日志恢复出的精确模型输出**。补问必须返回全新合法决策，才能进入后续执行。

真实走近中，角色根位置从 `(-2.600, -0.720, -4.600)` 变为 `(-1.978, -0.720, -4.627)`，水平移动约 0.622 米。结果为 `groundArrived=true`、`objectReobserved=true`、`phase=arrived`；行走中有 3 张同实体确认图，到达图与提交前图的 capture ID 不同。`semanticTargetConfirmed=true` 在这项测试中来自受控 matcher，不能当作真实 Qwen 理解准确率。

半周转身终态实测如下；误差使用回执的实际朝向误差，未按“180°减累计角度”推算：

| 请求 | 实际累计转角 | 终态朝向误差 | 根位置总位移 | 结果 |
| --- | ---: | ---: | ---: | --- |
| 向右 180° | +173.596° | 4.114° | 0.00393 米 | turned，随后取得新视图 |
| 向左 180° | -173.951° | 3.689° | 0.00493 米 | turned，随后取得新视图 |
| 向右转动中取消 | +25.079° | 154.597° | 0.00759 米 | cancelled，未宣称完成转身 |

离线方向审计还对实际 clip 的副本构造了反向半周、多转整周但终点朝向相同的反例，均被方向校验拒绝；这些反例没有提交或播放，不是 ARDY 当次实际生成的错误动作。

## 真实 Qwen 第一轮：保留失败与测试限制

[`decision-repair-live/report.json`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/decision-repair-live/report.json) 已完成 4 次真实模型请求，整体状态 `failed`。输入来自获授权的隔离房间角色视野快照；仅检验模型决策，**没有把这些输出提交给 Unity、设备或语音播放**。

| 请求 | 实际结果 | 判断 |
| --- | --- | --- |
| 初次接近刚才的小灯 | 输出 `approach`，指向用户，而非 `approach-object` | 未满足预期；测试历史未隔离，不能将此单项直接归因为真实运行中的模型退化 |
| 错误 goal 后重新决策 | `intent=approach` 却同时给出小灯的 `visualTarget`，生产协议拒绝 | 真实字段冲突；未执行 |
| 转身看看身后 | `turn-in-place`，yaw=180、pitch=0，并引用当前 capture | 通过决策检查；实际动作能力由上面的独立 ARDY 测试验证 |
| 只解释失败、不要移动 | `inspect`，没有移动请求 | 通过此次“不移动”检查 |

四项均为 HTTP 200、`finish_reason=stop`，生成分别为 36、162、131、40 tokens；本轮这两项失败不能归因于响应截断。复核发现，首轮输入虽然有真实灯具观察事实，却还混入其他回归的对话历史，且缺少建立指代的原用户问题。因此首项只能记录为该输入下的失败，不代表用户真实会话的精确复现。补问的字段冲突可直接回溯 [`repair-object-approach.decision.json`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/decision-repair-live/repair-object-approach.decision.json)。这些原始结果保留，不覆盖。

## 第二轮：契约冲突已修，初始指代仍未通过

修正了生产动作约定中三处会误导对象接近的旧表述：笼统的“一次走近”、无条件把“再试试看”归为 `approach`、以及明确请求便“仍选择 approach”。现在区分接近用户和接近物件，并说明实体的局部 capability 列表不是导航动作菜单；schema 增加 10 个合法字段组合分支，阻止 `approach` 携带物件像素等互相矛盾的输出。回归同时隔离既有对话历史，保留原用户问题、实际角色视图与物件事实后，再导出生产请求。

- [`protocol-r2/report.json`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/protocol-r2/report.json)：room 190 项、objects 1013 项通过。
- [`guards-r2/report.json`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/guards-r2/report.json)：整合检查全部通过，其中决策恢复为 66 项；这些仍是受控模型回包，不能替代真实模型验证。
- [`decision-repair-live-r2/report.json`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/decision-repair-live-r2/report.json)：4 次真实 Qwen 决策请求，3 项通过，整体仍为 `failed`；没有动作执行或语音外放。

| 第二轮请求 | 实际结果 |
| --- | --- |
| 初次“那你能试着靠近他吗？” | 仍为 `approach`，误选用户接近；格式有效、未虚构 goal，但目标类别不对 |
| 错误 goal 后重新决策 | 正确返回 `approach-object`，使用当前 capture 与已知小灯的 `entityTarget.entityId`，省略不存在的 goal ID；通过 |
| 转身看看身后 | 正确的 180° 身体转身意图；通过 |
| 只解释、不要移动 | `inspect`，无移动请求；通过 |

四项均完整返回。第二轮后的排查发现，请求尾条强调本轮原话，但代词所需的已有背景位于较长上下文前部，可能被弱化。后续在尾条重复已有、受长度限制的 `recentDialogue` 帮助消解指代，第三轮结果如下。由于各轮测试历史和请求构造有修正，不将不同轮的通过比例当作同一固定样本集上的模型准确率提升。

## 第三轮与最终交付

请求尾条现在只复制已有 `recentDialogue` 中最多 4 条 `role + quotedText`，每条最多 400 个 UTF-16 码元，并再次保留本轮完整用户原话。它不制造助手回应，不把旧指令赋予新权限，也不在程序中选择目标。

[`decision-repair-live-r3/report.json`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/decision-repair-live-r3/report.json) 的 4 次真实 Qwen 请求全部通过，分别耗时约 3.11、3.03、2.75、2.20 秒：

- 初始代词请求和错误 goal 后的补问，均返回 `approach-object`，选择 `entityTarget.entityId=scene-object-42febf66c5e54e8dae49e17962cbf8dc`，与该次原生测试实际观察的小灯一致；两次分别引用各自当前 capture，未填写不存在的观察 goal。
- “转身看看身后”返回 `turn-in-place`、yaw=180、pitch=0；“只解释，先不要移动”返回 `inspect`，没有移动请求。

这些仍是冻结角色视图上的独立决策请求，没有把真实模型输出直接驱动角色或语音。另一次 [`approach-native-r3/scene-decision-repair-native/report.json`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/approach-native-r3/scene-decision-repair-native/report.json) 通过 66 项检查：受控决策及 matcher 经生产链路提交真实 ARDY，角色根位置最终为 `(-1.992083, -0.720000, -4.626497)`；`groundArrived=true`、`objectReobserved=true`，行走中取得 2 张同实体确认图。该测试与模型请求使用同一已观察灯的实体 ID，但不能合称为一次全语音端到端实测。

[`final-verification.json`](../Server/ARDY/runtime/scene-object-approach-followup-20260914/final-verification.json) 汇总最终校验：1212 个源文件一致，无不匹配；主场景保持不变；`guards-r2`、`approach-native-r3`、`half-turn-r1` 三套 Unity 运行均正常退出、没有 C# 编译错误，半周转身相关源码仍与其实际测试版本一致。校验时 ARDY 服务 ready、Qwen 检查正常。前两轮真实模型失败报告完整保留。

## 用户复测建议

现在可重新进入原房间 Play，依次测试：“你看到电视柜上的小灯吗？”→“那你能试着靠近它吗？”→“刚才为什么没通过？只解释，先不要移动。” 另测向左、向右转身看身后及行走/转身中喊停。真实交互还需要验证录音结束、指代识别、执行和语音外放的连续体验；上述有限样本不保证任意描述或任意房间位置都成功。

观察目标是否仍是同一盏灯、是否真实走到附近并重新看见、停止是否平滑，以及解释阶段有没有误发移动。再次失败时保留 Unity 日志中的 `Room/DecisionValidation`、`Room/DecisionRepair`、`Vision/ObjectMatch`、`Vision/Action` 和 `Room/Trace`，据实际拒绝阶段定位，不以角色口头解释代替执行证据。
