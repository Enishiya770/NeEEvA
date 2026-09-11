# 玩家自由摄像头与 PICO VR

已接入主聊天场景 `Assets/AIChatTookit/Scene/chatSample.unity`、白盒房间 `Assets/Scenes/NeEEvARoom.unity`，以及本机私有 SailingMoon Day Chat 场景。重新生成白盒房间或从主聊天场景生成私有房间时，也会带上控制器。

首次更新后，等待 Package Manager 完成下载，保存自己正在编辑的内容并重启 Unity，再重新打开场景。项目使用 Input System + 旧 Input Manager（Both），这项改动需要重启编辑器。摄像头仍是原来的对象，现在标记为 MainCamera，使用透视投影。

## 桌面操作

在 Play Mode 的 **Game** 窗口中：

| 操作 | 行为 |
| --- | --- |
| 按住鼠标右键并移动鼠标 | 转动视角 |
| 按住右键 + W/A/S/D | 沿视线前后、横向自由飞行 |
| 按住右键 + Q/E | 沿世界竖直方向下降/上升 |
| 同时按 Shift | 三倍速度 |
| 松开右键、按 Esc 或切出应用 | 释放鼠标，停止飞行 |

默认速度 2 米/秒，鼠标灵敏度 0.12 度/像素，可在 Camera 的 **Player Camera Controller** 中修改。输入框正在编辑时不接受飞行操作。自由飞行不受重力或墙壁碰撞限制。

运行中可在该组件的上下文菜单选择 **Reset View / Recenter Headset** 返回初始观察点。为其他场景启用时，选中目标 Camera，执行 **NeEEvA → Player Camera → Enable on Selected Camera** 并保存场景；每个场景只给玩家视角启用一个控制器。

## PC VR：PICO 连接电脑

1. 在电脑和头显安装并连接 [PICO Connect](https://www.picoxr.com/global/software/pico-link)，通过它启动 SteamVR。电脑负责运行 Unity 和现有的语音、模型服务。
2. 在 SteamVR 中将它设为当前 OpenXR Runtime。头显应先被运行时识别，再启动 Unity Play Mode；如果先运行游戏才连接头显，可能需要退出 Play 后重新开始，以重新初始化 XR Loader。
3. 打开场景并运行，或在 Build Settings 中把当前场景加入 Scenes In Build，选择 **Windows / x86_64** 后构建。PC 平台已启用 OpenXR、自动初始化和 Direct3D 11。
4. 头显接管画面后，现实中的转头、俯仰、侧倾、前倾、蹲下和走动驱动摄像头。运行中的 XR 显示子系统拥有视角，鼠标不会覆盖它。

没有成功启动 XR 显示子系统时，摄像头使用桌面控制。短暂丢失追踪时保留最后有效位姿，恢复后继续追踪；不会突然切换成鼠标旋转。

## PICO 一体机：Android 本地运行

已固定依赖 Unity OpenXR **1.14.3**、Input System **1.11.2** 和官方 PICO OpenXR **1.4.1**（Git 提交 `3aa3e62bff41df618529eeb60ff02c29a515dafe`）。PICO 官方仓库的设备系统要求为 **PICO OS 5.13.0 或以上**；具体机型也需支持六自由度位置追踪。参见 [PICO SDK 官方说明](https://github.com/Pico-Developer/PICO-Unity-OpenXR-SDK)。

Android 已启用 PICO Support、必需的 OpenXR Extensions 和 Neo3 / PICO 4 / PICO 4 Ultra 交互配置；使用 ARM64、IL2CPP、最低 Android API 29、OpenGLES3 和 Single Pass Instanced / Multiview。项目原有 Linear 色彩空间保持不变。

1. 在 Unity Hub 为 **2022.3.22f1** 安装 Android Build Support，包含 SDK、NDK 和 OpenJDK。
2. 建议首次用白盒 `NeEEvARoom` 验证视角和头显追踪。在 Build Settings 选择 Android，切换平台，把该场景加入 Scenes In Build。
3. 在 **Project Settings → XR Plug-in Management → Project Validation** 检查 Android 配置。需要重新生成设置时执行 **NeEEvA → Player Camera → Configure PC VR and PICO**。
4. 在 PICO 开启开发者模式并连接 USB，选择 Build And Run，或构建 APK 后安装到设备上。

这次完成的是相机与 XR 接入，没有生成 APK，也没有把完整桌面对话应用移植到 Android。头显上的 `127.0.0.1` 指向头显自身：聊天场景要连接电脑服务，必须把对应服务 URL 改成电脑局域网地址。Windows 桌面截屏、自动启动电脑进程等功能也需要另行处理；现有屏幕 UI 的 VR 射线交互、手柄移动和传送不在这次改动中。

## 相机与追踪结构

运行时生成独立、单位缩放的 `Player Camera Origin`，原摄像头作为其子对象。Unity 的 **Tracked Pose Driver (Input System)** 使用 `<XRHMD>/centerEyePosition`、`centerEyeRotation` 和 `trackingState`，在 Update 和 Before Render 更新。双眼由 OpenXR 渲染，不人为设置瞳距。

启动 VR 时，以首次有效的头显位姿对齐进入 VR 前的观察位置和水平朝向。这是以初始眼睛位置为基准的相对追踪，保留场景的初始眼高，不会再叠加一遍真实身高；后续物理位移按 1:1 映射。实际头部的俯仰与侧倾始终保留。该模式没有自动检测虚拟地面或把玩家脚底锁到地板。

实现参考 [Unity OpenXR 配置说明](https://docs.unity3d.com/Packages/com.unity.xr.openxr@1.14/manual/index.html) 和 [Tracked Pose Driver](https://docs.unity3d.com/Packages/com.unity.inputsystem@1.11/api/UnityEngine.InputSystem.XR.TrackedPoseDriver.html)。

## 验证记录

- Unity 2022.3.22f1 隔离 Play Mode：23 项检查通过，覆盖初始场景位置、斜向速度、Shift、俯仰限制、帧率独立位移、真实 TrackedPoseDriver 消费模拟头显位姿、追踪失效/恢复和桌面/VR 切换。
- 相机和插件在隔离项目切换到 Android 后编译通过；Unity OpenXR 与 PICO 配置校验没有错误，仅有两类可选的输入 API 迁移提示。
- 主工程运行时及 Editor 脚本使用 Unity 自带 C# 编译器校验通过。
- 尚未进行真实鼠标锁定 UI 操作、实体 PICO 双眼显示/延迟检查、APK 构建或房间在一体机上的性能验证。

本机报告在 `Logs/PlayerCameraValidation/Logs/`，回归入口为 `PlayerCameraRegression.RunBatch` 和 `PlayerCameraRegression.ValidateConfigurationBatch`。前者只允许在该隔离项目目录运行，避免修改正在使用的聊天场景。
