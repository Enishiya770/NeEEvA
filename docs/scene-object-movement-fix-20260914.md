# 物件观察站位与口头跟随起步修复

截至 2026-09-14，本批修复与验证完成：Unity 组合回归、两个日志低柜位置的真实走近、共享 Qwen 起步调度，以及协议和服务测试均通过。完整麦克风输入到语音输出的新 Play 体验仍需用户实测；隔离验证没有运行 ASR/TTS。

证据目录为 `Server/ARDY/runtime/scene-object-movement-fix-20260913/`。目录日期沿用开始调查的日期；用户日志事件均发生于 9 月 13 日，本文时间使用日本时间。

## 已确认的问题

### 低处电视柜的观察站位被提前拒绝

日志中有 7 次 `observe-closer` 接近电视柜被 `object-observation-station-outside-head-range` 拒绝，分布在两个 Play 会话，最新会话有 5 次。拒绝发生在选择观察站位时，尚未提交路线或请求 ARDY 行走，不能解释为 ARDY 不会走向电视柜。

对应原始快照 `user-editor.log` 的行号为 87721、89174、93704、96021、97314、100343、118528；提取结果见 [approach-diagnosis.json](../Server/ARDY/runtime/scene-object-movement-fix-20260913/approach-diagnosis.json)。旧代码同时存在两个限制：以较稀疏的屏幕网格找可见部件，可能漏掉薄柜面；把目标基本居于视野中心、颈部余量和不超过 2 米的站位作为共同前提，未区分“目标可在视野内被观察”和“头部正对目标中心”。

旧日志没有记录当时的真实眼位、候选数量与各候选的角度，因而不能把每一次拒绝进一步精确归为网格漏采或角度计算中的某一项。本批修正这些已定位的代码限制，并加入对应测量日志。

### 两次口头跟随在首段预缓冲时 HTTP 504

23:24:28 至 23:24:33、23:29:20 至 23:29:26 的口头跟随，均已接受用户目标，随后在 `stream-prebuffering` 失败。终态证据分别在 `user-editor.log` 第 110561、121448 行；首步请求耗时约 5.023 秒和 5.022 秒。23:25 的按钮跟随则取得了首段并开始实际移动，说明这两次口头失败不能简单归为房间路线不可达。

关键区别是口头动作同时触发较长的正式 Qwen 回复。首步动作条件需要同一份 Qwen 提供特征；现有原生特征服务只在聊天槽空闲且没有待调度聊天时处理特征，不会让两个解码过程同时写共享模型。旧流程先发正式回复，再以 5 秒预算请求动作首步。日志顺序、固定约 5 秒的超时，以及实际服务调度实现共同支持：**共享模型的长回复抢先占用计算，使冷启动动作特征等不到原来的首步期限。**

远端只读检查确认特征服务在等待聊天空闲后处理请求，较长提示的预填充可超过 5 秒。不过旧服务日志未逐项记录失败请求的远端排队阶段，不能声称已测得这两次失败各自在远端等待的精确时长。失败事件中的 `featureSeconds=0`、`backendSeconds=0` 也只是没有收到成功响应中的计时，不能据此推断模型没有工作。

### 旧 `decision-unavailable` 仍缺少可追溯原因

旧日志还有多次 `intent=unresolved dispatch=decision-unavailable`。旧记录没有保留被拒绝的决策载荷与具体校验门槛，HTTP 200 本身也不能证明回包是可执行的决策。**不能从现有日志反推所有这些事件的根因，更不能宣称本批已经修复它们。**

本批新增 `[Room/DecisionValidation]`，记录请求是否被传输层接受、状态、拒绝原因及最多 1,800 字符的被拒决策片段；不记录整份角色提示、图片或认证头。这用于下一次复现时区分传输、解析、协议校验及观察过期等环节，不会放宽动作授权。

## 本批实现

观察站位改为使用导入角色的中立眼位，避免已转动的头部姿态反过来改变站位计算。在原关注区域附近扫描当前图中同一实体的真实不透明像素，保留原始 0.5 米 XZ 区域限制；最终仍由完整解析器校验所选点。相机实际视野内最多使用 8 度的偏心余量，头颈限幅和合法地面审核保持独立。

到达先采一张真实新图。如果目标尚不可见，只允许一次原有范围内的视向调整，然后再采一张新图。同一实体、相同房间与角色绑定以及当前几何校验全部满足，才记为重新看见；实际地面到达仍单独记录。此流程不把柜面当落脚点，也不借视向调整执行灯具控制。

口头移动增加首步握手：本轮新提交的走近、跟随或走近物件任务，在得到首个已审核动作前缀、真实播放器姿态或明确等待/终态后，再发正式长回复。握手绑定当前动作、控制操作、用户轮次、模型和角色环境；旧任务或旧回包不能放行新一轮回复。普通聊天和停止指令不受该等待影响。

首步 ARDY 请求预算从 5 秒改为 20 秒，后续已复用特征的短步仍为 5 秒。起步等待上限 22 秒；到时只如实报告仍在准备，不取消移动，也不宣称已经成功。实际等待后另用最多 4 秒获取角色当前视图；采集失败即清除旧图，以无图事实回复。它不是等待角色走到目标后才回答。等待被新操作取代时，仅释放该代次尚未发出的聊天轮次，避免 inflight 卡住自主调度；不取消替换后的移动或清除新回复。

Python 流式服务为服务端 5xx 失败增加 `streamDiagnostic`，区分 `feature-wait`、`backend-gate` 和 `generation`，附本地单调时钟耗时、截止预算及是否已取得特征/开始生成。不把本地特征等待时间冒充远端 GPU 计算时间；已存在的质量拒绝等 4xx 返回结构保持兼容。

相关实现入口：

- `Assets/AIChatTookit/Scripts/Chat/ChatSample.SceneObjectApproach.cs`
- `Assets/AIChatTookit/Scripts/Chat/ChatSample.RoomMotionReadiness.cs`
- `Assets/AIChatTookit/Scripts/Chat/ArdyRoomDialogueBridge.MotionReadiness.cs`
- `Assets/AIChatTookit/Scripts/Motion/ArdyRoomLocomotionController.Stream.cs`
- `Assets/AIChatTookit/Scripts/Chat/ChatSample.RoomTask.cs`
- `Server/ARDY/motion_service/locomotion_stream_service.py`

本地 ARDY 服务已由主流程按现有管理方式重启成功，以加载 Python 诊断改动。没有为本次修复新增语言模型。

## 已完成的验证

Python 共 25 项检查通过：`Server.ARDY.test_stream_deadline_diagnostics` 与 `Server.ARDY.test_locomotion_stream`。新增案例分别阻塞特征获取、后台生成入口和正在运行的生成线程，检查超时阶段、一次性失败记录和晚完成不得提交动作；同时验证原有质量拒绝载荷没有被诊断字段破坏。

协议检查通过：房间协议 190 项，物件协议 606 项。来源与源文件摘要见 [protocol/report.json](../Server/ARDY/runtime/scene-object-movement-fix-20260913/protocol/report.json)。这些是协议边界检查，不是用户体验成功率。

真实调度验证使用现有共享 Qwen、隔离房间图和已有角色提示。先请求一个 ARDY 冷启动首步，再发一条正式模型请求，同时请求使用同一份特征的第二步。实测见 [scheduling-live/report.json](../Server/ARDY/runtime/scene-object-movement-fix-20260913/scheduling-live/report.json)：

| 请求 | HTTP | 实际耗时 | 已验证事实 |
| --- | --- | --- | --- |
| ARDY 首步 | 200 | 1.094 秒 | 首次取得 live-Qwen 特征，先于正式回复完成 |
| 正式 Qwen 回复 | 200 | 3.906 秒 | 第二步发出时该请求仍在运行 |
| ARDY 第二步 | 200 | 0.125 秒 | 复用首步的同一特征请求来源，在正式回复期间完成 |

该验证共 4 次 HTTP 请求：两次动作、一条正式回复，以及清理本次隔离流会话的取消请求。没有故意再制造反向调度失败。正式请求保留原角色提示、图像和事实，追加“历史隔离测试”说明并把最大输出设为 320；模型文本仅保存，没有播报或执行。

入口为 `Tools/MotionAdapter/validate_follow_feature_scheduling.py`；默认只准备输入，必须显式 `--execute` 才请求服务。测试第二步使用返回动作接缝生成有界诊断路线，**不包含 Unity 地形审核或实际角色播放，因此它证明服务调度与特征复用可以并行工作，不能替代 Unity 握手和完整对话回归。**

## Unity 结果与交付边界

最终组合回归为 [integration-r2/report.json](../Server/ARDY/runtime/scene-object-movement-fix-20260913/integration-r2/report.json)：进程正常退出，无 C# 编译错误；原有场景视觉、物件引用、物件匹配、遮挡、动作取消和真实 ARDY 走近均通过。起步握手单独通过 40 项检查，包括实采新图、旧回调跨代次防护、替换动作后的 pipeline 释放、停止绕过等待，以及 22 秒仍无结果时的诚实反馈。服务端错误说明另有 10 项检查。

两个低柜回放均使用原房间、原角色、日志目标附近的真实可见像素，直接运行原生 ARDY 行走并在到达后重新识别同一实体：

| 回放 | 初始角色根节点 | 结果 |
| --- | --- | --- |
| [low2-final](../Server/ARDY/runtime/scene-object-movement-fix-20260913/low2-final/scene-object-approach-low-2/report.json) | (-2.600, -0.720, -4.600) | 实际走近低柜，途中与到达后的新图确认同一实体 |
| [low6-final](../Server/ARDY/runtime/scene-object-movement-fix-20260913/low6-final/scene-object-approach-low-6/report.json) | (-4.176, -0.720, -3.906) | 实际走向另一低柜区域，途中与到达后的新图确认同一实体 |

两者的观察站距约 1.434 米、预测俯角约 27.56 度，头部仍受原有限幅；运动根节点连续。七个日志目标的站位几何检查也通过。逐帧检查数量随帧率变化，不当作独立成功样本数或泛化成功率。

修正过两处测试布置问题：low2-r1 在提前进入专用分支时未初始化测试相机；low6-r1 把日志的 avatarGround.y 错赋给角色 root.y，使脚底悬高，触发原有高度守卫。源日志第 118528 行 actualRoot.y=-0.72；改正测试初始放置后通过，没有放宽生产地形守卫。失败证据保留在原目录。integration-r2 后仅修改回归报告保存及第七点测试 root/ground 标注；最终两个低柜回放使用该测试版本，生产源码未再改变。

[final-verification.json](../Server/ARDY/runtime/scene-object-movement-fix-20260913/final-verification.json) 记录最终核验：1,206 个源资源与隔离项目一致，协议来源仍匹配，用户主聊天场景哈希未变，三个最终 Unity 运行全部成功且无 C# 编译错误。ARDY 已重启并确认 ready。

本批修正已定位的观察站位限制和共享 Qwen 起步争用，并使后续拒绝原因可查。Unity 的动作目标/语义前提在回归中受控；实际 Qwen 并发另有真实服务证据。这不等于完整语音链路已做端到端测试，也不证明所有物件描述均能匹配、所有自由动作都自然。旧 decision-unavailable 事件仍保留未查明限制。下一次可直接用口头跟随和自主观察走近验证，无须先按编辑器跟随按钮。
