# ARDY 动作在 NeEEvA 角色上的播放原型

2026-09-10。当前交付是 Unity 内的固定动作播放器：左挥手使用 Qwen → MLP → ARDY 的离线生成结果；右挥手使用该左挥手的明确标注镜像；点头和摇头使用受控交流曲线。本轮修正单臂动作的胸部参照，并根据实际角色渲染替换不自然的右手、头部预览。最终资源已通过两个真实模型的编辑模式与胸部/头部专项回归，播放器也通过实际 Animator、ControlRig、嘴型和眨眼的 Play Mode 检查。按钮只读取 JSON，不启动语言模型或动作推理服务。

## 在当前聊天场景使用

1. 用 Unity 2022.3.22f1 打开项目，正常运行自己的聊天场景。
2. 打开菜单 **Tools → NeEEvA → ARDY Motion Preview**。
3. 点击 **查找当前场景角色**，再点击 **绑定角色**。如果存在多个角色，把目标实例的 `Vrm10Instance` 拖入字段。
4. 点击左手挥手、右手挥手、轻点头或轻摇头；播放中可切换，或点击 **停止并回到待机**。代码更新后等待 Unity 编译完成，再重新进入 Play Mode 并绑定角色。

面板只在本次 Play Mode 中添加播放器，退出运行会清理，不修改或保存场景。关闭面板会触发停止渐变。运行时绑定入口为 `ArdyMotionPlayer.Bind(Vrm10Instance)`，播放与停止分别为 `Play(TextAsset, ArdyMotionMask)` 和 `Stop()`。

先检查左右手是否正确、右手是否还横跨脸前、肩肘是否自然、手腕是否跳变；在正常说话、眨眼和注视时重复测试。点头/摇头现在是短而轻微的受控示意，需确认叠加在当前角色姿态上是否自然；它们不用于评价 ARDY 的头部指令遵循能力。

## 这版实际处理的内容

- ARDY Core27 是右手系、Y 向上、Z 向前，转换矩阵为 `C=diag(-1,1,1)`，旋转转换为 `C R C`。导出器验证全局旋转与局部 FK、骨架位置相符，Unity 不再重复翻轴。
- JSON 保存 27 关节的全局旋转与源休止旋转。播放器使用原模型资产烘焙的参考姿态进行重定向，不直接套用源局部四元数。两个项目模型的 `ArdyAvatarRestPose` 已随资源提供，以共享 VRM 元数据资产引用匹配，因此运行后才绑定、或稍后实例化同一 prefab，也能使用真正的参考姿态。
- 不能依赖场景实例的 `Vrm10Instance.DefaultTransformStates`：其在 VRM 组件关闭时可能直到第一次绑定才采集，得到的是当时的待机姿态。新实现避免这一路径；动态 glTF 导入可使用导入器保存的初始局部旋转，未知且没有参考资产的场景模型会拒绝绑定。
- 实际两个角色均映射 13 个上身骨骼。Core 的四段脊柱通过累计全局旋转映到 Spine / Chest / UpperChest；左右臂、颈和头可分区播放。首批按钮使用单侧手臂或头部遮罩。
- 单臂遮罩现在使用胸部参照：先去掉源胸部旋转，再把源手臂相对胸部的动作应用到当前目标胸部。原右挥手包含约 29° 的转胸；旧实现保留手臂的角色全局旋转，却由待机动画保留目标胸部，两者参照不一致。坐标修正后，实际角色较窄的肩宽仍使原右手样本遮脸，因此还需要替换该预览样本。原始 NPZ 全部保留，左挥手 JSON 字节不变。
- 受控头部资源以 `source.rotationApplication="additive-local"` 标记，作为局部增量叠加到当前动画姿态。Neck 承担总角度的 20%，Head 的局部贡献为 80%；资源存储 Neck 全局偏移 20%、Head 全局偏移 100%，其余关节与休止偏移均为单位旋转。
- 头部的淡入、淡出也混合相对偏移，再叠加当帧 Animator 姿态。零偏移不会暂时拖住底层动画；头部与挥手互相切换时分别处理传入、退出的骨骼。
- 根位置、腿部、骨长、手指和 BlendShape 由现有角色系统保持。动画为 20 FPS，播放使用四元数插值；开始、切换、停止默认 0.3 秒渐变。
- 播放器每次 Update 先还原自己的上一帧覆盖，Animator 继续评估，然后执行顺序 10000 的 LateUpdate 写入动作。VRM 默认在 11000 处理约束、视线、表情和 SpringBone。
- 有 ControlRig 时写控制骨架；没有时写 Humanoid 原骨架。ControlRig 创建时根朝向可能与导入时不同，播放器单独记录其参考坐标。播放中 Rig、目标或 VRM 更新方式改变会停止并要求重新绑定。
- 实际私有聊天场景使用 `Assets/Model/NEVA.vrm`，其 `Vrm10Instance` 被场景关闭，且无 ControlRig；此路径直接写 Humanoid，保持原配置。没有自动开启 VRM 表情系统，也没有改动 `ChatSample.m_Animator`。

## 动作来源与限制

挥手的原始生成过程使用三次 ARDY 窗口的 120 帧，共 6 秒。第一窗冷启动；之后使用自身最后 16 帧历史，传入 `num_frames=16+40`，只拼接新增 40 帧。没有重复拼入历史，也没有用重建历史覆盖已经输出的帧。当前右挥手镜像仍保留完整时间序列；原始头部基准和候选保留为审计数据。

| 按钮 | 当前实际播放内容 | 播放长度 | 原始数据参照 |
| --- | --- | --- | --- |
| 左手挥手 | ARDY 中幅、中速生成动作 | 120 帧 / 6 秒 | motion-00053，seed 0，test |
| 右手挥手 | 已认可左挥手的镜像，按当前胸部参照播放 | 120 帧 / 6 秒 | 派生自 motion-00053，seed 0，test；不是右手条件的直接输出 |
| 点头 | 一次温和下俯 8°，随后回正的受控曲线 | 37 帧 / 1.85 秒 | 仅审计关联 motion-00010，seed 1，train；不使用其运动旋转 |
| 摇头 | 左右各约 6°，随后回正的受控曲线 | 41 帧 / 2.05 秒 | 仅审计关联 motion-00048，seed 2，test；不使用其运动旋转 |

本轮另外固定原右手描述 motion-00004，生成 seed 1/2/3 三个原生候选。完整真实 VRM 参考骨架的 FK 与 Unity 测量一致：原 seed 0 右腕最小胸侧距离为 −1.084 cm；seed 1/2/3 分别为 +3.514 / +3.811 / +11.419 cm。seed 1/2 仍有遮脸，seed 3 虽不遮眼，但渲染中手掌靠近额头、略像敬礼，未选为默认预览。仅替换目标臂长而沿用源肩宽的近似计算会漏掉这个问题。

最终选择已认可左挥手的镜像：在 Unity 坐标中对全部关节的全局旋转、休止旋转施加 X 反射并交换左右关节，保持规范骨架名和父子关系；双重镜像恢复原矩阵。真实骨架分析中，其举手阶段右腕最小胸侧余量约 27.7 cm，实际渲染的手臂保持在肩外侧，脸和额头可见。元数据保留原左手生成条件与派生方式。这是演示资源的可控修饰，不能算作原生右手指令生成成功率，也没有重新训练模型。

原始 seed 0 的小幅点头和大幅摇头近似静止。之后生成的六个头部候选中，两个曾通过 pitch/yaw 可见幅度筛选，但用户认为它们不自然，已不再用作可播放头部动作。完整轨迹复核显示：点头主要是开头一次约 44.4° 的大动作，伴随约 14.0° 侧歪，之后基本保持；摇头的 yaw 范围约 12.2°，roll 侧歪却达到约 30.5°。旧筛选遗漏了侧歪、完整往返和回正要求，不能当作自然度验收。六个候选和全部失败记录保持不变。

这里沿用生成报告的前向向量 pitch/yaw 与 YXZ roll 定义；独立源审查报告另用 xyz 欧拉角并明确记录方法，数值不应混用。

`conversational_head.py` 为当前交流示意显式构造分段 quintic minimum-jerk 曲线，角度、速度和加速度在分段处连续，每个节点的速度与加速度为零；动作开始即有响应，没有三秒静止等待。点头曲线时长 1.8 秒，摇头 2.0 秒；20 FPS 资源包含两端采样，播放器将最后的回正帧再保持一帧，因此播放长度分别为 1.85 和 2.05 秒。

这两条头部曲线是**明确的受控动作，不是 ARDY 原始输出，也不是新的模型训练或适配器改进**。资源的 `text` / `displayIntent` 使用新的受控预览说明，原提示词保留在 `source.rawText`；原 NPZ SHA、条件/模型来源和原指标仍可追溯。`source.processing` 标明未使用原动作旋转，`rawMetrics` 与 `profileStatistics` 分开记录。原始训练/测试划分只描述所关联的审计数据，不能用于评价这两条手工定义曲线的泛化能力。

左挥手第一个窗口接缝的源 LeftHand 相邻旋转变化约 23°，需特别检查手腕观感。这一版没有启用 ARDY C++ 动作校正。

实际角色的开始与停止渐变由 Unity 完成；ARDY 尚未接收角色实时姿态。当前没有网络取消、迟到包处理、流式欠载恢复，也没有接生产 Qwen 的动作特征 HTTP 出口。

## 复现与报告

已提供四个 JSON TextAsset 与两个角色的参考姿态资源，直接在 Unity 播放不需要本地模型缓存。模型资产改变后，在非运行模式执行 **Tools → NeEEvA → Bake ARDY Avatar Rest Poses** 重新生成参考姿态；该操作只读取原模型资产，保持已有参考资产 GUID，不修改场景。批处理入口 `ArdyAvatarRestPoseBaker.BakeBatch`。其他模型需补入 baker 的来源列表。

重新生成原始审计动作需要此前的 Qwen 特征与 MLP、项目内 ARDY 环境。最后一步验证所有源文件后导出挥手，并应用受控头部曲线；已经有缓存时只需运行最后一步，无需再次推理：

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.generate_preview_clips
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.generate_preview_clips --head-candidates
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.generate_preview_clips --right-candidates
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.build_unity_preview_assets
```

导出与受控曲线数学检查：

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m unittest Tools.MotionAdapter.test_unity_export Tools.MotionAdapter.test_conversational_head -v
```

本轮导出与受控曲线两组 Python 测试共 11 项已通过。受控曲线测试检查单位四元数、开始与结束回正、主轴与零侧歪、Neck/Head 20%/80% 的局部组合、C2 连续性和解析导数，不替代实际角色上的自然度观察。

## 回归与视觉检查

本轮在独立 Unity 2022.3.22f1 副本中验证，未停止用户正在运行的原项目。最终资源的编辑模式、胸部/头部专项回归，以及本轮播放器的实际 Play Mode 回归均已通过。专项检查在两个真实模型上得到：共同源转胸消除、目标胸部跟随、动态待机上的零头部偏移、混合模式切换，误差均为 0°；右腕全程最小胸侧距离为 +13.77 cm；倾斜待机上的点头峰值约 7.96°、摇头约 5.99°，结束偏移为 0°。帧内断言数量不代表独立样例数量。

最终三段动作使用实际蒙皮网格逐帧渲染，保留动作结束与回待机过程，PNG 序列均为 20 FPS。GIF 合并重复帧时保留原时间轴：

- [右挥手：已认可左动作的镜像](../Logs/ardy-naturalness/NEVA-right-wave.gif)
- [一次轻点头](../Logs/ardy-naturalness/NEVA-nod.gif)
- [一次轻微左右摇头](../Logs/ardy-naturalness/NEVA-shake-head.gif)

这些预览使用固定待机姿态和独立灯光，当前聊天场景中的整体观感仍由用户复看。腕到面部点的三维距离不能证明不遮脸；此次原右样本即使相距约 32 cm 仍遮眼，所以最终选择同时查看了真实渲染。

Unity 菜单 **Tools → NeEEvA → Run ARDY Motion Regression** 在独立预览场景中检查两个真实模型；批处理入口为 `ArdyMotionRegression.RunBatch`。截图使用 `BakeMesh` 显式采样当前骨骼，避免同一次 EditMode 调用中 GPU 蒙皮缓存仍停在首帧。输出位于 `Logs/ardy-motion-preview/`，包含原始截图和 JSON。

`ArdyMotionRegression.RenderAnimationBatch` 可渲染完整左手挥手及回待机的 141 帧序列。上一版生成的 [NEVA-left-wave.gif](../Logs/ardy-motion-preview/NEVA-left-wave.gif) 为历史预览，本轮未重新渲染该 GIF。

数值回归覆盖：左右遮罩隔离、根转向 123° 的等变性、脚和骨长保留、表情权重保留、换动作/同帧停止不跳、每帧还原底层动画，以及自然播放完毕后的退出。帧内断言数量不是独立测试样例数量。

真实 Play Mode 回归入口为 `ArdyMotionPlayModeRegression.RunBatch`（使用 `-batchmode`，不要添加 `-quit` 或 `-nographics`）。它创建独立临时场景，不启动聊天和网络：

- 普通 Humanoid 的真实 Animator 从场景启动即运行，至少 0.5 秒后才绑定播放器，当时手腕已低于肩约 37 cm。左挥手时手腕升高约 53.5 cm；完整混合后与已知参考姿态的 ControlRig 手臂相对胸部旋转一致。
- 真实 ControlRig 在根转向前后分别构建，休止姿态及动作输出的对应旋转一致；停止后与同步运行的 Animator 对照一致，并继续切换到原有 thinking 状态。
- 实际 `Audio2LipScript.Update` 接收合成 aa 音素，实际 `BlinkController` 运行协程。挥手期间嘴型权重约 5–94、眨眼 0–100，逐帧嘴型与输入相符，其他表情通道保持。

此检查没有播放音频，也不证明 TTS 音素识别、声画同步、生产对话延迟或长期并发表现。

专项入口为 `ArdyMotionAnchorRegression.RunBatch`，最终三段近景渲染入口为 `ArdyMotionAnchorRegression.RenderBatch`。前者还覆盖零头部偏移在淡入/淡出期间随不断变化的底层头部姿态，以及 Head↔RightArm、UpperBody→Head 切换后回待机。右手原始失败、三个原生候选及镜像候选的完整渲染保存在隔离项目的 `Logs/ardy-naturalness/` 审计与候选子目录。

生成与样例选择的可复查记录：

- [四段基准生成](../Tools/MotionAdapter/reports/unity-preview-generation.json)
- [全部头部候选](../Tools/MotionAdapter/reports/unity-head-candidates.json)
- [三个新增原生右手候选](../Tools/MotionAdapter/reports/unity-right-candidates.json)
- [原始动作与时序审查](../Tools/MotionAdapter/reports/motion-naturalness-source-review.json)
- [真实参考骨架上的全部右手候选比较](../Tools/MotionAdapter/reports/unity-right-candidate-screening.json)
- [固定演示资源与哈希](../Tools/MotionAdapter/reports/unity-preview-assets.json)
- [Unity 编辑模式回归](../Tools/MotionAdapter/reports/unity-playback-regression.json)
- [Unity 实际播放回归](../Tools/MotionAdapter/reports/unity-playmode-regression.json)
- [胸部与头部专项回归](../Tools/MotionAdapter/reports/unity-naturalness-anchor.json)
- [最终渲染来源与逐帧记录](../Tools/MotionAdapter/reports/unity-naturalness-render.json)
- [源文件与隔离副本一致性](../Tools/MotionAdapter/reports/unity-naturalness-source-verification.json)

实时动作服务和对话连接已另行实现，使用当前实际上身姿态的短历史、共享 Qwen 特征与三窗追加，并处理取消和欠载。固定预览资源保持本页已验收版本。使用入口和本轮验证范围见 [对话与实时动作](ardy-dialogue-motion.md)。
