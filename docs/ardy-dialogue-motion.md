# 对话与实时动作

本阶段在同一份 Qwen 上接入动作意图和特征提取。原有三个聊天槽、视觉接口和 Unity 聊天地址保留；本机另加载小适配器与 ARDY，不常驻 Llama 教师。

2026-09-10 更新：正常启动已选用扩充训练后的预览适配器 `finetune-seed0`，发布标识 `ardy-preview-6364a5984590a06a04ff662e`。384 段独立动作审计和真实 Unity 联调已完成。新描述的特征对齐与部分双臂动作改善，但随机种子间仍有迟发、静止和不对称，不能承诺任意动作都自然。结果及成功/失败回放见 [泛化验证](ardy-generalization.md)。

同日执行层更新：新增结构化 `compose`，支持左右臂独立姿态、手腕或头部局部运动，以及实际到位后的跨轮持姿。两套 VRM 的几何约束与聊天接续验证通过；这部分由本地控制器执行，不代表 ARDY 原生生成能力改变。用法与边界见 [姿态约束与局部动作](ardy-constrained-execution.md)。

掌向扩展：可朝向交流对象并在掌面内摆手，未指定的肘角由执行器求解；明确角度或已保持的姿态不自动放宽。对话连接面板可绑定交流对象，留空使用主相机。下一次决策可读取执行失败反馈。协议、真实模型回放及规划局限见 [交流意图与掌向](ardy-palm-and-intent.md)。

## 使用

在项目根目录的 PowerShell 中启动共享 Qwen 模式：

```powershell
& Tools/neeeva_remote_llm.ps1 -Action start -Mode feature
```

在另一个终端启动动作服务（运行期间保持终端打开）：

```powershell
& Server/ARDY/start_motion_service.ps1 -FeatureUrl http://127.0.0.1:8080
```

如果动作服务已经在 `8093` 运行，不需要再次启动。启动 Qwen 时会检查并复用已有共享实例，原二进制保留。已有普通 Qwen 正在工作时，feature 模式明确要求先结束该实例，不会自动杀掉用户会话或加载第二份；普通模式也识别并复用已运行的共享实例。

动作服务启动时会校验 `Server/ARDY/runtime/active-adapter-release.json` 及对应证据，再加载新适配器；`/health` 的 `backend.adapterMode` 为 `verified-preview`。若需要比较原适配器，停止动作服务后运行同一脚本并加 `-BaselineAdapter`。这不会删除新版或改写已验收的四手势。只验证动作、无需启动嵌入服务时，共享 Qwen 启动命令可加 `-SkipEmbeddingStartup`。

正常运行 NeEEvA 聊天场景，打开 **Tools → NeEEvA → ARDY Dialogue Motion**。点击“查找当前对话与角色”，检查选择后点击“连接对话动作”。当场景存在多个角色或 ChatSample 时手动选择。连接仅在当前 Play Mode 有效，不保存或改写私有场景。

然后可直接说：

- “用你自己的左手／右手向我挥手。”
- “轻轻点一次头。”或“轻轻摇一次头。”
- “站在原地，把两只前臂稍微向前摊开、掌心向上，做一个解释事情的动作。”

四种基础手势使用已验收资源：左手为选定 ARDY 样本，右手为其明确标注镜像，点头和摇头为轻微受控曲线。明确姿态与局部关节组合走 `compose`，例如“双臂前伸，保持肩肘，双腕轻摆两次”。开放式站立上身动作走 `generate`，实时生成三窗、共六秒。“随意做一个动作”会先选择具体动作；新生成动作的自然度仍需人工检查。

“双手举过头顶”和“双前臂摊开解释”可用于试用新版，但仍有失败种子。初始历史高度修复解决了明显歪身；扩充训练进一步改善双臂动作条件，部分真实角色回放能举起双手，另一些仍不对称或偏静止。精确掌向、次数、顺序与自然度需继续评价。基础手势的验收不代表任意 `generate` 动作都已自然。

动作标签从台词和朗读文本中分离。普通聊天无需附带动作；用户直接要求支持动作时输出执行指令，不用括号描写代替。动作在完整回复结束时触发，尚未对齐首个 TTS 音节。

## 打断与播放

用户开口、新用户输入、停止、断开或明确新动作都会取消正在运动的动作；纯动作而没有语音时也能打断。例外是已经实际到位的显式 `compose end="hold"`：用户继续说话保留持姿，最多 30 秒，停止或新动作仍会释放。自动续说本身不取消身体动作，同一回复链里同一计划不会反复从头启动。程序会向后续回复提供当前动作及播放阶段。每个请求带角色 ID、轮次、单调 revision 和请求 ID；Unity 和服务两端均拒绝过期结果。

动作结束后，程序仍向 Qwen 提供已回 Animator 待机、上次请求和结束原因。上次请求不是已达到或仍保持的姿态。`compose` 额外提供 VRM 更新后的实际骨骼量测；只有明确处于实际持姿状态时才能用 `current` 接续。已回待机时，需要重新达到用户未撤销的准备姿态。原问题的证据保留在 [前举直臂与手腕挥动诊断](ardy-composition-diagnosis.md)。

实时首窗根据当前实际渲染上身姿态生成。历史经过固定 20 Hz 重采样，连续窗口每次只追加 40 个新帧，保留 16 帧用于 ARDY 内部续接，不把历史重复播放。动作结束或服务失败时平滑回到当前 Animator 姿态。欠载只短暂等待，随后停止；不无限卡在举手姿势。

当前生成范围为站立上身。根位移、腿、精细手指、足部接触不由这条通道接管。初始历史的下半身为合成静止参考，不能视为完整身体观测。仍由既有系统处理嘴型和眨眼。

合成历史根据 Core27 的脚/脚趾位置设置站立根高度，避免将 Hips 相对坐标零点误用为世界地面。Editor 默认在 `Logs/ardy-live` 保留最近八次生成的描述、种子、实际历史和响应，后续异常可直接复现；完整聊天和音频不会写入这些文件。

## 已有验证与边界

以下早期报告保留原始结果。本次新适配器的对应证据是：

- [384 段最终动作审计](../Tools/MotionAdapter/reports/native-final-supported-audit-v1.md)：36 个明确高度观察中旧版 16、新版 21、教师 32 达到必要高度；不是整体动作成功率。
- [新版实时 HTTP](../Server/ARDY/runtime/candidate-live-http-v1/report.json)：6 条 validation 描述、18 个窗口、173 项检查。
- [新版真实 Unity 播放](../Tools/MotionAdapter/reports/candidate-unity-live-v1.json)：首窗约 0.97 秒，约 1.48 秒收齐 120 帧；正常结束与取消后回 Animator 误差为 0°。
- [新版聊天动作回归](../Tools/MotionAdapter/reports/candidate-unity-dialogue-v1.json)：真实 Unity 中 479 项断言通过；[历史协议回归](../Tools/MotionAdapter/reports/candidate-unity-stream-v1.json)同时通过两个真实 VRM。
- [正常启动验证](../Tools/MotionAdapter/reports/generalization-preview-running-v1.json)：8093 实际加载上述预览发布，真实 Qwen 特征生成及取消已验证。

- [共享 Qwen HTTP 报告](../Tools/MotionAdapter/reports/shared-qwen-http.json)：28 条训练缓存抽查逐值误差为 0；聊天 KV 保留、视觉请求与队列限制通过。保留 3 × 65536 容量，未做三个槽同时填满的长时压力测试。
- [真实 Qwen 动作选择](../Tools/MotionAdapter/reports/dialogue-motion-live-qwen.json)：四种基本手势、生成手势与无需动作的普通回复共六个合成场景。使用实际生产动作协议，不读取私有角色提示或聊天记录。
- [ARDY 实时 HTTP 报告](../Tools/MotionAdapter/reports/ardy-motion-service-live.json)：共享特征、三窗续接、官方历史变换、随机状态隔离、取消与过期丢弃。
- [Unity 实际 HTTP 播放](../Tools/MotionAdapter/reports/unity-live-playmode.json)：高度修复后的正式服务实测约 1.37 秒收到首窗、1.75 秒收齐 120 帧，初始历史包含真实采样的 16 帧。正常结束和中途打断后均回到同步 Animator 姿态，Unity 精度下旋转差为 0°；验证三组请求/响应日志、正常完成与两种取消原因、过期结果拒绝，以及随后仍可播放已验收点头。这是播放链路验收，不是双手解释动作的语义验收。
- [真实 ChatSample 回归](../Tools/MotionAdapter/reports/dialogue-motion-regression.json)：167 项断言，包含动作与 TTS 分离、私有/引用/畸形标签拒绝、纯动作打断、自动续说保留动作、相同标签去重和实际动作状态上下文。
- [流式窗口与历史回归](../Tools/MotionAdapter/reports/unity-stream-regression.json)：两套真实 VRM、实际 HTTP 数据重放、顺序与来源验证、欠载退出、逆投影和根朝向不变性。续窗明确省略初始历史字段，避免 Unity 将 null 类变成空对象。
- [源文件核对](../Tools/MotionAdapter/reports/unity-live-source-verification.json)：隔离测试副本与实际项目源文件及已验收资源哈希。
- [此前高度修复的服务记录](../Tools/MotionAdapter/reports/ardy-motion-grounded-service-running.json)：原适配器、站立历史高度与显式种子修复的历史验证记录；当前运行状态以上述新版正常启动报告为准。
- [高度与随机状态服务回归](../Tools/MotionAdapter/reports/ardy-motion-grounded-history-regression.json)：31 项真实 HTTP 断言，包含脚/脚趾支撑高度、跨身份同种子重现、交错请求随机状态隔离和取消结果丢弃。
- [真实角色对照](../Tools/MotionAdapter/reports/unity-native-motion-diagnosis.json)：同种子、同文本、同观测历史，仅调整源历史站立高度的逐帧网格渲染。举手躯干最大倾斜约 48° → 3°，但仍只举一侧。[查看动画](../Logs/ardy-native-diagnostics/overhead-height-fix-seed1.gif)。

性能记录是当前机器的少量联调样本，不代表完整聊天、语音并发时的 p95。动态动作整体自然度、长时间运行以及语音节奏同步仍需后续评估。

详细字段见 [传输协议](ardy-live-protocol.md)，后端使用和边界见 [动作服务说明](../Server/ARDY/motion_service/README.md)。离线按钮与已验收素材说明仍在 [Unity 动作预览](ardy-unity-preview.md)。
