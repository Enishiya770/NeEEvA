# NeEEvA 桌面与 VR 界面

已接入 `Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity` 和 `Assets/AIChatTookit/Scene/chatSample.unity`。Unity 导入完成后，编辑状态的 Game 视图也会显示新 HUD；如果编辑器尚未重新导入外部修改，切回 Unity 并执行 `Assets > Refresh`。进入 Play 后启用实际聊天、录音和交互。

编辑状态的预览不会启动聊天、录音或 XR，也不会生成额外 Camera / EventSystem。通过 `NeEEvA > Interface > Editor Preview` 可预览输入和设置面板。临时预览不写进场景，保存、脚本重载和进入 Play 前会清理。旧 Canvas 与射线组件的隐藏状态直接保存在场景中，预览清理不会重新启用它们，因此正式 UI 初始化之前也不会闪现旧界面；各组件的原始状态另外保存在 CompanionPresentation 中，禁用新 UI 时用于恢复。

## 桌面

- 底部只保留当前短句字幕、实时语音状态以及“文字”“对话”两个小入口；右上角为偏好设置。
- `Tab` 打开/收起操作面板，`Esc` 收起。输入框聚焦时，`Tab` 留给输入导航。
- `Enter` 发送，`Shift + Enter` 换行；中文输入法候选确认时不会误发送。
- 右键视角与 WASD / Q、E / Shift 自由移动保留。打开操作面板时暂时释放摄像头的鼠标、键盘控制；关闭后恢复。
- “实时对话”开关与“按住说话”都使用原来的语音入口。实时对话开启或正在结束本轮时，按住录音暂停使用，以免两种采集流程冲突。
- “停止回应”调用原来的打断逻辑，文字发送也保留现有的打断与取消行为。

实时对话的 ASR 识别条会在录音期间立即显示每次中间识别结果，并接受后续文字修订。它独立于角色字幕的句子分页：长句在单行视口中跟随最新末尾，持续录音时不因 5 秒没有文字变化而隐藏。结束后显示最终识别结果，沿用原有短暂停留和清理规则。

ASR 条上方固定显示纯文本“您”。导入的 VRM 角色优先显示 **Project 面板中的模型资源文件名（不含 `.vrm`）**，重命名文件后，游戏与桌面字幕会同步更新；VRM 内部元数据和场景中遗留的旧名称不会覆盖新文件名。非资源模型回退到模型根对象名，去掉 `(Clone)` 后缀；只有通用根名称/空名称才回退 VRM 元数据，最后使用“角色”。标签独立于字幕正文，不参与分页或识别文本裁剪。当前 SailingMoon 模型资源为 `NeEEvA.vrm`，因此显示 `NeEEvA`。

编辑器直接读取资源路径；构建场景时 `CompanionModelNameBuildProcessor` 把当前绑定的模型引用与文件名写入播放器数据，Windows/PICO 构建无需依赖编辑器 API。缓存只对同一模型引用生效，换绑模型不会继续套用前一个模型的名称。

字幕按照正在揭示的原文、译文分页，每种语言最多两行；字号范围 24–34。双语卡片随实际内容调整高度，停止讲话且内容不再变化后淡出。完整对话仍可从历史查看；默认呈现最近 80 条，顶部可继续加载更早记录。内部工具与内心记录不会混入对话展示。

偏好设置支持字幕关闭/原文/译文/双语、8 种翻译语言、字幕字号以及系统提示等级。沿用现有翻译与设置保存逻辑。

### Windows 桌面悬浮字幕

默认开启。Unity 处于 Play 或 Windows 程序运行时，最小化窗口或切换到其他应用，当前角色字幕会出现在桌面底部，位于 Unity 所在显示器的任务栏上方。切回 Unity 后自动收起；没有字幕时沿用原来的淡出规则。

右上角 `… → 偏好设置 → 桌面悬浮字幕` 可随时关闭或重新开启，偏好会保存。角色浮窗同步当前原文/译文选择、字号和逐步显示的文字，上方居中显示同一模型名称；左下角另有带“您”标签的实时 ASR 浮窗，持续显示识别中间结果。角色字幕与 ASR 独立显示/淡出，角色没有发言时也能查看 ASR。两个浮窗上下错开避免重叠，鼠标均可穿透，不抢键盘焦点，也不新增任务栏按钮。

项目已启用后台运行，实时语音不会因切换应用而被这个功能暂停。停止 Play、禁用界面组件或重载脚本时会销毁原生浮窗。编辑器布局预览不会打开桌面浮窗；VR 模式自动暂停桌面浮窗，PICO 一体机继续使用空间字幕。

实现使用独立、无所属窗口的 Windows 分层窗口，不移动或修改 Unity 主窗口。鼠标穿透与不激活行为依据 [Microsoft 的分层窗口说明](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)；后台运行使用 [Unity 的 runInBackground](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Application-runInBackground.html)。原生文字和窗口更新均在主线程进行，只有文本、尺寸或透明度变化时才更新绘制。

## PC VR 与 PICO 一体机

- 使用现有 OpenXR 头部追踪。UI 不创建额外 Camera，也不改写头显的物理位姿。
- 常驻部分为轻量空间字幕；转头/走动超过一定范围后，字幕平滑调整位置。
- **左手 Y 短按**呼出/收起控制面板，**长按 0.7 秒**把面板重新放到面前。
- **右手射线 + 扳机**点击，**摇杆**滚动历史；右手失追时可由左手接替。
- 控制面板默认位于眼前约 1.5 米、宽约 0.78 米。打开后固定在空间中，允许自然转头观察；面板显示时字幕向下避让。
- VR 默认适合直接语音。当前 OpenXR 集成未提供已验证的一体机系统软键盘，文字输入需要连接实体键盘（或使用 PC VR 电脑键盘）。

头显实机舒适度、控制器型号差异、安卓输入法及设备权限仍需在目标 PICO 设备上验收。

## 项目结构与调整

- `CompanionPresentation` 负责运行时布局，组件挂在 ChatAgent 上。Inspector 可调字体、字幕停留时间、VR 字幕和面板距离。
- `CompanionIcon` 提供 25 种统一的线形图标，`CompanionSurface` 绘制圆角面板；它们使用 UGUI 网格，图标不依赖图片贴图或系统符号字体。
- `ChatSample.Presentation` 是只读展示适配与发送入口。原聊天、录音、TTS 和翻译流程继续使用原绑定。
- 旧 Canvas 的渲染和射线暂时关闭，作为原逻辑的数据来源保留。禁用新的 CompanionPresentation 组件即可恢复旧界面。
- `CompanionXRInteraction` 使用官方 XRI 2.6.5，复用现有 EventSystem、Camera Origin，模式切换时恢复原输入模块。
- `CompanionPresentationSetup` 会给已经打开的两个目标聊天场景补上新组件，并显示临时编辑器预览；不会重新加载或自动保存用户的其他场景修改。新增正式组件时会标记场景待保存。也可运行菜单 `NeEEvA > Interface > Apply Companion UI to Open Chat Scenes`。

字体随包附带 Noto Sans CJK SC Regular，支持中日英文等本界面文字。一体机不依赖 Windows 字体。[字体来源](https://github.com/notofonts/noto-cjk/blob/main/Sans/OTF/SimplifiedChinese/NotoSansCJKsc-Regular.otf)，SIL OFL 许可证保存在 `Assets/AIChatTookit/Font/NotoSansCJK/OFL.txt`。

## 验证

- 主项目实际运行时与 Editor 源码编译。
- Unity 2022.3 隔离渲染：图标、桌面/VR 的关闭状态、输入、历史和设置面板。
- 27 项真实 ChatSample / SubtitleOverlay / RTSpeechHandler 适配回归；使用 inactive fixture，不运行聊天网络或麦克风。
- 19 项模拟 PICO 控制器交互回归，包括扳机点击、追踪丢失、动态 Dropdown、模块恢复。
- 58 项真实 UI 与 XR 生命周期集成回归，包括面板开关、空间位置、模式切换以及重新启用。
- 两个场景的组件、字体、Camera、meta GUID 与依赖锁结构审核。
- `CompanionRuntimeIntegrationRegression` 直接调用正式组件的 Start，覆盖旧 Canvas 隐藏/恢复、数据保留、重复启动以及编辑器预览与保存清理。启动交接检查覆盖预览已销毁但 Start 尚未执行的实际渲染、保存后重开仍隐藏，以及禁用新 UI 后保存/重开的回退状态。

验证报告和离线 UI 预览位于项目的 `Logs/CompanionPresentationValidation`、`Logs/PresentationAdapterValidation`。这些是隔离验证输出，不代表实际头显测试。
