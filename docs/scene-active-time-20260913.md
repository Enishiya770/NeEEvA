# 场景时间状态与主动改变视野

实施日期：2026-09-13。针对[上一轮书架停留核查](scene-time-active-perception-review-20260913.md)，本批补充时间事实、独立的私人复查调度、主动转头/转身，以及物体目标和用户目标的事实隔离。

实现及隔离房间验证已完成，结果与限制见文末。仍使用现有一份 Qwen、适配器及 ARDY，不增加第二个语言模型，也不启用跨高度移动。真实自主持续探索还需要原房间 Play 观察，不能由接口回归代替。

## 持续维护状态，按需调用模型

Unity 在会话运行期间持续记录角色实际位置、头向、动作结果与已发生的图像采样。Qwen 仍只在请求时推理；没有请求的等待期间，没有由这条自主循环执行的连续语言模型思考。这样提供的是可核实的时间信息和行为连续性，不能据此声称角色具有人的主观时间体验。

[ChatSample.SceneTime.cs](../Assets/AIChatTookit/Scripts/Chat/ChatSample.SceneTime.cs) 提供 `SceneTimeContextJson`，随感知帧交给模型。主要字段为：

| 事实 | 含义 |
|---|---|
| `activeSeconds`、`wallClockUtc` | 分开记录会话有效运行时间与墙钟时间。 |
| `sinceLastDecisionSeconds`、`lastDecisionIntent` | 距上次私人判断多久、那次保留的简短意图。 |
| `lastSuccessfulArrival`、`lastFailedAttempt` | 保留最近实际到达及失败；一次新拒绝不会抹掉已经到达书架的事实。 |
| `stationaryForSeconds`、`viewHeldForSeconds` | 距上次超过位置/头向阈值的变化多久。当前阈值为平移 1 厘米、头向 2 度；这不是完全不动或持续专注的证明。 |
| `activity`、`purpose`、`activityForSeconds` | 当前动作阶段及目的；未选择新动作可记录为安静观察或休息，不强迫继续移动。 |
| `samples`、`sinceLastSampleSeconds` | 最近四份实际采图的标识、时间、世界空间视向及间隔；两次采样之间发生的视觉事件仍未知。 |
| `modelRequestedRecheckSeconds`、`nextReviewInSeconds` | 模型提出的复查时间与当前队列截止时间，可能因新事件、身体占用或请求预算而不同。 |

位置和头向事实由引擎测量；图像中的物体名称仍是模型解释。没有做连续视频理解或画面相似度分析，`viewHeldForSeconds` 也不能被解读为“房间内容一直没有改变”。

应用暂停、会话关闭及大于 2.5 秒的运行更新缺口不会被累加为连续观察。缺口被标为未观测，站立/头向计时重新建立。旧会话的到达记录带会话编号，不能拿其数值直接当成本次停留时长。场景视觉窗口每秒刷新预览仍只是本地预览，不代表每秒向 Qwen 提交画面。

## 复查使用一个截止时间

[SceneReviewClock.cs](../Assets/AIChatTookit/Scripts/Chat/SceneReviewClock.cs) 与 [ChatSample.cs](../Assets/AIChatTookit/Scripts/Chat/ChatSample.cs) 已把私人复查从连续发言疲劳中拆出。模型选择 45 秒时，调度按一个有效运行时间截止点处理，不再先乘成 126 秒又叠加另一道 45 秒等待门。

实际到达、观察完成、失败/受阻等结果可以排入优先复查；普通正式回复收尾不能覆盖已排队的动作结果。身体执行、跟随、语音播放或另一请求仍占用时，事件留在队列中，空闲后继续受理。优先事件不绕过追踪、身体控制权或模型并发限制。

默认成本边界：私人自主预判为滚动一分钟最多 **6 次**，请求开始间隔至少 **2 秒**。这是私人预判预算，不是包括空间决策、正式回复、台词审核在内的所有 HTTP 请求总预算。实际发声轮才计入连续发言；无声动作、私人预判和纯 `<silent/>` 不再被当成已经连续开口。

日志改为 `[Agent/Review] scheduled` 和 `waking`，分别记录 requested、due_active、actual_elapsed、queue_delay、wake reason 等。`actual_elapsed` 是有效运行时钟下的调度耗时，不是旧日志把 EffectiveSec 写成“实际”。执行忙碌、预算等待和暂停仍可能使下一次请求晚于模型所选时间，因此不承诺“2 秒后已经完成推理”。

## 可以先改变视向，再看见新的内容

[RoomTaskProtocol.cs](../Assets/AIChatTookit/Scripts/Chat/RoomTaskProtocol.cs) 增加两种方向意图。它们带当前真实 `captureId` 与观察目的，使用 `viewChange`，不需要虚构一个已经可见的物体像素，也不能同时携带 `visualTarget`。

| 意图 | 坐标与范围 | 实际执行 |
|---|---|---|
| `orient-view` | **相对当前实际头部视向**。yaw 右正左负，范围 ±50°；pitch 上正下负，范围 ±25°；至少一个轴变化达到 3°。累计头部角度仍受自然颈部范围约束。 | 通过实际头眼组件平滑转头。稳定后取得新图，默认将该视向保持最多 20 秒，给后续判断使用；到期回收，或被新的合法动作/停止/闭眼提前释放。 |
| `turn-in-place` | **相对当前身体朝向**。yaw 绝对值为 5–120°，pitch 必须为 0。 | 以当前合法地面为位置目标，通过原生 ARDY 全身执行改变朝向；不直接旋转角色 Transform 冒充生成动作。 |

实现位于 [ChatSample.SceneAction.cs](../Assets/AIChatTookit/Scripts/Chat/ChatSample.SceneAction.cs)、[NevaSceneAttention.cs](../Assets/AIChatTookit/Scripts/Perception/NevaSceneAttention.cs) 与 [ArdyRoomDialogueBridge.SceneOrient.cs](../Assets/AIChatTookit/Scripts/Chat/ArdyRoomDialogueBridge.SceneOrient.cs)。原地转身保留角色地面、身体碰撞和用户净距检查，对超出允许站位偏移的生成轨迹停止执行。

`turned` 表示原生转身已完成；只有实际视向变化后取得新图才进入 `observed`。这两个状态都不表示已走近某物体，也不自动证明对未知方向内容的猜测正确。模型需要根据新图再选择观察、走近、休息或放弃。系统没有固定左—中—右巡航脚本。

自主方向改变最多每 30 秒 **3 次**。头部观察、已有 ARDY 身体任务、连续跟随和用户讲话沿用控制权仲裁；旧的异步回调不能恢复已停止的动作。`orient-view` 和成功 `look-at` 保留新头向最多 20 秒供后续判断；期间可被新目标、用户或身体任务接管，不是额外的模型等待时间。

## 修复两处与用户位置无关的阻塞

**物体接近使用用户 XZ 净距。** [ArdyRoomObjectApproachPlanner.cs](../Assets/AIChatTookit/Scripts/Motion/ArdyRoomObjectApproachPlanner.cs) 不再要求用户相机下方必须是合法地面。角色去书架时，桌面相机位于沙发上方、悬空或房间地面以外，不应仅因此拒绝物体任务。

计划中的 `UserClearanceCenter` 只是有效用户锚点 XZ 在角色路线平面的投影，不是用户真实脚的位置。桥层继续核查 `PlayerCameraController.HasValidWorldPose`、用户绑定和当前身体净距；用户 XZ 进入路线时仍拒绝该步。角色自己的起点、目标、全路径、每步支撑及碰撞审核保持原样。**走近用户和跟随用户的目标支撑/同层规则不变**，不能借此走上家具或越过高差。

**台词只依赖与其含义相关的事实。** [ChatSample.RoomSpeechFacts.cs](../Assets/AIChatTookit/Scripts/Chat/ChatSample.RoomSpeechFacts.cs) 与 [ChatSample.RoomTask.cs](../Assets/AIChatTookit/Scripts/Chat/ChatSample.RoomTask.cs) 使用审核 `factScope` 区分图像观察、场景动作、用户目标及组合陈述。引擎仍强制保留对应动作的必要依赖，不能由模型自行去掉执行审核。

“书架看起来是空的”不再只因用户路线 revision 改变就改写成“走近失败”；“已经到你身边”仍依赖最新用户目标。相关事实在审核期间变化时允许一次有界刷新/重新审核，并记录具体 changed 字段。`inspect` 保持原有空间状态查询定义，图像外观陈述按真实陈述依赖审核。对模型提供的紧凑事实明确标注：到用户的路线评估不代表整个房间或书架不可达。

## 在现有房间试用

沿用现有启动脚本，在原房间进入 Play、启用会话并睁眼，打开 **Tools → NeEEvA → Character Scene Vision**。先让角色自由活动，观察到达书架后是否会选择保持观察、改变视向或原地转身；长时间安静本身不算失败，可随后问“你在这里站了多久”“刚才有没有看过身后”，将回复与采样和行动日志对照。若想单独验证接口，可说“向左看看有什么”“抬头看一下”或“转身看看后面”；这些受指令行为要与真实自主选择分别记录。再把桌面相机移到远离角色路线的沙发上方，检查物体走近是否仍受理；沿用原有停止和跟随测试确认控制权交接。无需创建新测试场，也不要把未开放的台阶/家具当成本次可走地面。

查看 `[Agent/Review]`、`[Vision/Action]`、`[Room/SceneOrient]`、`[Room/SpeechReviewFacts]` 时，应区分预判没有新意图、预算/忙碌排队、头部越界、原生动作受阻以及新图采集失败，不把所有静止都归因于模型没思考。

## 验收记录

证据位于 `Server/ARDY/runtime/scene-active-time-20260913/`。全部 Unity 验证使用既有 `unity-naturalness-validation` 隔离项目。没有操作主 Unity 窗口或保存主场景。受控回调验证执行与取消语义，不能算作 Qwen 自主选择成功。

| 项目 | 入口/证据 | 状态 |
|---|---|---|
| 单一截止时间、结果事件不被覆盖、预算、安静不计发言、暂停及真实时间事实 | `integration-final-direction/report.json` | 时间检查 25 项通过；实际采图方向另有集成断言 |
| 方向协议、实际头向/新图、持有及停止、原生原地转身 | `active-native-r1/scene-active-view/report.json` | 62 项通过（含 39 项协议检查）；头部实际改变约 24.60°；原生请求 -55°，实际约 -52.64°，根位置偏移约 7 毫米；另一次转身中取消成功 |
| 沙发上方/无地面用户不阻挡物体路线，旧用户目标规则及净距仍有效 | `object-planning-r1/report.json` | 51 项 Physics/NavMesh/桥层检查通过 |
| 正在持有物体任务时的用户高度变化、XZ 入侵、追踪/绑定/目标失效 | `integration-final-direction/object-approach-user-clearance-runtime.json` | 14 项生产每步 guard 检查通过；受控任务快照，不冒充完整动作播放 |
| 台词事实范围、相关变化刷新、无关用户变化不改写视觉描述 | `integration-final-direction/room-speech-scope.json` | 20 项通过；含刷新时关闭、暂停、迟到回调及一次重审上限 |
| 真实 Qwen 自主选择与执行 | `live-qwen-r1/scene-qwen-live/report.json` | 3 次 HTTP 完成：自主预判、选择目标、重新选点；实际动作被定位审核拒绝，详见下文。未证明稳定主动环顾 |
| 既有跟随任务生命周期、平滑停止 | `integration-final-direction/report.json`；`smooth-stop-r1/report.json` | 跟随生命周期 115 项；实际 Humanoid/Animator 的确定性停止检查 364 项通过。没有重新做整段连续追随自然度评测 |
| 头眼控制与 Animator/ARDY 接管 | `attention-r1/attention-regression.json` | 实际 VRM 隔离 Play 回归 47 项通过；含相对视向、保持、取消和已有动作接管 |

基础集成另覆盖传输 173 项、停止准备 45 项、空间协议 241 项及原视觉动作 98 项。各组有嵌套/重复检查，不将它们相加宣称独立测试总数。原生转身前后的图片在 `active-native-r1/scene-active-view/body-source.jpg` 与 `body-result.jpg`，实际视野从电视柜前方转向书架、窗户与台阶。

`source-manifest-final.json` 核对 1,026 个源文件/资源与隔离项目副本一致。主 Chat 场景 SHA256 前后均为 `2c7217f1034d58a6e429aba091306cd6d2ec509e182b049b7360a9e5c95b5e23`。最后仅增加采图方向时间记录后，重新跑过 `integration-final-direction` 全套本地集成；原生转身与实际 Qwen 记录来自此前同批的对应专用运行。

真实 Qwen 本次提出“观察房间环境”，选择“电视柜上的发光小灯”的像素。该点落在透明、动态或未验证材质上；一次新图选点后仍被拒绝，最终 `rejected`，没有取得新行动结果或宣称到达。该样本暴露的是现有目标定位支持范围，未把它当成自主探索成功，也没有反复调用模型直到得到成功样本。下一次 Play 重点观察它收到失败、时间与视向能力信息后是否换方向或目标。

早期验证中的问题和修正保留在同目录 `integration-r1` 至 `integration-r5`：底层 Motion 对 Player 命名空间的越层依赖已移回桥层；时间用例改为明确时钟输入；旧的“闭眼不能含任何历史 captureId”与“成功关注立刻释放头向”断言改为新行为的证据检查。最终集成与独立检查均在修正后执行。

跨高度移动、触碰/操作家具，以及手部动作进一步泛化不属于本批交付。主动环顾和时间状态接入后，仍需用真实行为记录判断模型是否选择了合理活动，不能把接口存在当作自主探索已稳定。
