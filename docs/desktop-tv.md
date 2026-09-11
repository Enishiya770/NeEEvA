# 电视显示电脑桌面

SailingMoon Day Chat 场景中的 `tv_frame` 使用新增的 `Desktop Screen` 子对象显示 Windows 主显示器。外框材质与原模型保留。画面放在凹进去的屏幕前，使用不受场景灯光影响的材质，并按桌面宽高比留黑边。

## 使用

1. 等待 Unity 编译完成，重新打开 `Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity`，进入 Play Mode。
2. 看向电视即可看到实时桌面。摄像头自由移动和 PC VR 都能观看。
3. 如果当前打开的场景尚未重新载入，选中 `tv_frame`，执行 **NeEEvA → TV → Show Desktop on Selected tv_frame**，再保存场景。重复执行不会创建第二层屏幕。

在 `tv_frame → Desktop Screen → Desktop Tv Screen` 组件中设置：

| 设置 | 默认值与含义 |
| --- | --- |
| Monitor Index | `0` 主显示器；`1` 起为其他显示器，按桌面位置排序 |
| Frames Per Second | `12`，可设为 1–30；看视频时可提高，但会增加采集与上传开销 |
| Max Width / Max Height | `1280 × 720`，保持比例缩小，不强行拉伸 |
| Exclude Unity Windows | 默认开启；仅在这台电视的采集中排除 Unity，其他截图/录屏不受此开关影响。Game 视图有焦点时按 `F8` 可统一切换所有正在采集的电视 |
| Flip X / Flip Y | 默认关闭，用于手动调整画面方向 |
| Brightness | `1`；电视使用独立自发光式显示材质 |

运行中可以修改上述设置；Status 显示采集状态和画面尺寸，Displayed Frames 显示已上传帧数。取消勾选该组件或退出 Play Mode 会停止采集。

`Exclude Unity Windows` 开启后，本进程的 Unity 编辑器主窗口、浮动窗口或 Windows 游戏窗口仅从电视自己的采集画面中排除，现实显示器上的 Unity 仍照常显示和接收输入，普通截图和 OBS 可以继续采集它。此版本已改为每个电视采集实例的局部过滤，不再用窗口全局截图排除实现开关。

本机已验证局部过滤能取得被排除窗口后面的背景，同时普通 GDI 采集仍能看到该窗口，窗口的 display affinity 保持不变。此路径目前限定 **Windows 64 位、系统只有一个显示器**；使用旧版 Magnification 图像回调，显卡/Windows 组合仍有兼容性限制。检测到多显示器（包括运行中接入）或无有效回调时，会停止这台电视的过滤采集并显示原因，不会改用全局排除。关闭开关可恢复普通 GDI 整屏采集，后者支持选择多个显示器。

每台电视的开关相互独立；F8 只是同时切换所有正在采集的电视的快捷方式。运行时设置接口 `DesktopTvScreen.SetUnityCaptureExclusion(bool)` 可供 UI Toggle 或未来 VR 设置面板调用。更换采集模式时短暂清空旧帧，避免新模式启动失败后继续显示旧画面。

升级后等待 Unity 编译完成，退出并重新进入 Play Mode，让旧版全局排除状态得到清理。今后录制 Unity 无需关闭电视过滤。该开关只排除当前 Unity 进程的窗口；OBS 预览或 SteamVR 等其他进程的镜像画面仍可能造成另一条反馈循环，应另行避免把预览放在电视正在采集的桌面上。

关闭排除后，如果 Unity 的 Game 窗口仍在被采集的显示器上，电视会再次形成镜中镜。另一种办法是使用扩展显示：把 Unity 和 VR 镜像窗口移到未被采集的显示器，让电视采集另一台显示器。仅戴上 PC VR 头显不会自动排除电脑上的 Unity 窗口。

## 避免递归与后续桌面交互

当前已实现真实显示器的整屏采集与 Unity 窗口排除开关。后续反向操作按独立虚拟显示器方向设计，尚未安装显示器驱动，也未加入指定窗口采集或输入回传：

- **指定应用窗口**：改用支持窗口捕获的 Windows.Graphics.Capture，让电视显示选定的浏览器、播放器等应用。采集来源不包含 Unity，即可避免自我递归；最小化、弹出窗口与应用兼容性需要实测。该模式显示选定应用，不代表整个桌面。
- **完整桌面**：优先使用独立的扩展显示器，或后续安装并配置虚拟显示器，将工作应用放在被采集的显示器上，Unity 留在另一台显示器。Windows 的“虚拟桌面”切换功能不等于虚拟显示器。
- **独立工作区用于反向操作**：后续由 Windows 虚拟显示器承载被操作的应用，电视显示该显示器，并把手柄/鼠标操作回传到它的坐标范围。现有 `Monitor Index` 按 Windows 显示器枚举选择来源；具体驱动的兼容性、显示器标识持久化与重连仍需后续实现和验证。

未来可将鼠标或 VR 手柄射线落在电视上的位置换算为真实桌面坐标，再通过 Windows `SendInput` 执行鼠标移动、点击、拖拽、滚轮与键盘输入。坐标换算须扣除电视黑边，并匹配翻转、显示器位置和 DPI。Windows 输入注入受应用权限级别限制。

排除窗口只影响采集，不会使 Unity 在真实桌面上自动让出鼠标命中或键盘焦点。交互时还需管理目标应用焦点，并区分“场景移动”和“操作电脑”模式，避免 WASD、右键与自由摄像头冲突；退出或断连时释放已按下的按钮。PC VR 可在电脑端实现采集和输入桥接；PICO 独立 APK 需要电脑端发送视频，头显端接收画面并回传操作，并提供明确的连接授权与断开入口。

官方接口参考：[局部窗口过滤](https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetwindowfilterlist)、[图像回调的兼容性说明](https://learn.microsoft.com/en-us/windows/win32/api/magnification/nf-magnification-magsetimagescalingcallback)、[Windows 屏幕与窗口采集](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture)、[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)。

## 支持范围

- **Windows 编辑器 / Windows 游戏 / PC VR**：直接采集电脑本机显示器并贴到电视上。
- **PICO 一体机独立运行 APK**：不能直接读取另一台电脑的桌面；需要电脑端视频推流和头显接收端。这次没有添加跨设备串流。
- 当前显示的是桌面画面；没有添加电视音频传输或通过电视反向操作鼠标键盘的功能。

## 实现

`WindowsDesktopFrameSource` 在后台线程采集，关闭过滤时使用 GDI，开启时使用 `WindowsFilteredDesktopCapture`；复用图像缓冲并只保留最新一帧。`DesktopTvScreen` 在 Unity 主线程上传 BGRA32 纹理；材质属性按屏幕实例设置。`DesktopScreen.shader` 支持普通渲染和 XR 立体渲染宏。窗口焦点变化不改变默认的主显示器来源，显示器布局会定期刷新。

`WindowsFilteredDesktopCapture` 为采集创建隐藏的 magnifier 窗口，用 `MagSetWindowFilterList` 仅过滤自己的采集结果，再把回调图像转换为电视需要的 BGRA32 缓冲。不会修改 Unity 窗口的 display affinity，也不会临时隐藏真实窗口、切换焦点或改变全屏放大设置。旧版 `WindowsCaptureExclusion` 仅保留生命周期恢复入口，电视不再创建它的全局排除租约。

所有局部过滤实例的原生操作由同一个专用线程执行，以满足此接口的线程限制；每台电视仍拥有独立的过滤窗口、采集尺寸和纹理。最后一个实例退出后释放放大采集运行时。生命周期清理会停止并等待后台采集线程结束。

场景设置只针对名为 `tv_frame` 的电视，未配置 `tv_frame (1)`。重新执行私有聊天场景生成菜单时，会自动为主电视加入桌面屏幕。私有房间模型和场景保留在原有的本机私有目录中。

## 已验证

- 本机真实桌面采集：取得 24 帧，尺寸 1280×720；透明度、缓冲区复用、停止采集和 GDI 资源释放检查通过。
- Unity 实际渲染：屏幕内侧位置、外框保留、颜色与上下左右方向、横竖黑边、翻转和着色器编译检查通过。
- Unity Play Mode：真实桌面纹理绑定到 Renderer，停用后清除纹理，重新启用并切换到 640×360 后继续更新；安装后的场景引用和重复配置检查通过。
- 主工程运行时与 Editor C# 编译通过。尚未进行实体 VR 头显内的显示与帧率测试。
- 局部过滤本机原生对比实测：专用采集得到后方绿色背景，同一时刻普通 GDI 采集仍显示红色前景窗口，窗口 affinity 始终为 0；没有保存用户桌面图像。
- 最新 Unity Play Mode 回归通过：3840×2160 桌面输出 1280×720/640×360，两台电视相反开关状态、同时过滤、停用/重启、尺寸改变及退出 Play Mode 均正常；测试窗口 affinity 全程保持 0。该 batch 测试的探针位于屏幕外，因此不把它计入被过滤窗口数；局部过滤像素效果由前述原生对比测试验证。
- 新后端的已知 BGRA 颜色、上下方向及缩小通过；同线程多实例和全部退出后换新线程重建通过，重复初始化后 GDI/USER 资源数保持稳定。Windows 与非 Windows 编译分支通过。

验证脚本、指标和合成测试图保存在本机 `LocalPrivate/TvDesktopValidation/`。真实桌面测试只记录指标，没有把桌面画面保存为测试图片。
