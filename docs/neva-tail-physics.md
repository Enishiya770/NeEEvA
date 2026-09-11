# NEVA 尾巴姿势与物理

## 设计

尾巴使用独立的 `NeEEvA.Motion.NevaTailSpring` 组件，配置到场景中的 VRM 根对象。它只驱动 FoxTail 骨链，避免为尾巴重新开启整个 `Vrm10Instance`，影响已有的嘴型、眨眼、注视与动作系统。

静止外形由序列化的自然休息姿势提供：尾根有支撑，中段下垂，尾尖略回卷。前两段短骨骼保持休息姿势并随臀部运动，后面三段参与弹簧模拟；主动弯曲从中段到尾尖逐渐增强，并逐段稍微延迟。身体附近的碰撞约束用于减少尾巴穿过臀部和腿部。无需另外制作尾巴动画剪辑。

配置会保存骨骼原始局部旋转及自然休息姿势，并直接应用到编辑态的骨骼。重复配置使用保存的原始参考，不累积弯曲。场景保存后，即使还未进入 Play，尾巴也应呈自然弧线。

## Unity 中操作

1. 在场景 Hierarchy 中选中 NEVA 角色，或其骨骼子对象。
2. 执行 **NeEEvA → Tail → Configure Selected Avatar**。
3. 保存场景，进入 Play 检查静止、转身与行走时的尾巴表现。

菜单支持 Undo，修改会记录为场景中 prefab 实例的 override，不修改导入的 `.vrm` 模型文件。再次执行菜单会复用已有组件。`Vrm10Instance` 的启用状态、更新模式以及 Animator 配置应保持原样。

独立尾巴物理属于运行时效果；Scene 编辑态显示的是保存的休息姿势。它不是完整的环境刚体碰撞系统，不保证与任意家具、墙壁或地板交互。若需要坐姿等特定动作，可在此基础上补充相应的尾巴姿势或动作目标。

## 参数调节

选中角色根对象上的 `NevaTailSpring` 组件，在 Inspector 中调整：

| 参数 | 当前默认值 | 调节含义 |
| --- | --- | --- |
| `downwardAngles` | `25, 40, 60, 65, 50` 度 | 从尾根到尾尖的五段，分别相对角色水平面向下的角度，数值越大越向下。它们是各段的目标倾角，不是五次累加的局部旋转。末段比前段小，使尾尖轻微回卷。 |
| `rootStiffness` | `1.1` | 模拟部分起点（FoxTail3）的恢复强度；臀部附近的 FoxTail1–2 已固定，不受这个参数控制。 |
| `tipStiffness` | `0.45` | 尾尖回到休息姿势的强度；降低后末端更容易受运动与重力影响。中间骨骼在首尾值之间插值。 |
| `gravityPower` | `0.12` | 世界向下的重力影响；提高后尾巴在休息弧线基础上进一步下垂。 |
| `dragForce` | `0.25` | 每个模拟步的速度衰减比例，范围 `0–1`；提高后摆动更快停下，降低后余摆更多。 |
| `idleSwayDegrees` | `12` 度 | 末段的主动侧弯目标幅度，Inspector 可调 `0–30` 度；三段分别乘以 `0.35、0.65、1`，形成逐渐弯曲的形状。不是整体尾根旋转角度。 |
| `idleLiftDegrees` | `1` 度 | 后三段的小幅前后弯曲，同样按段递增并与侧弯错开相位；不再抬动尾根。 |
| `idleSwayPeriod` | `3.2` 秒 | 一次主动摆动的周期；增大后摆动更慢。 |
| `bodyCollisions` | 开启 | 使用跟随臀部和大腿的近似胶囊体，减少尾巴骨链穿过身体。 |
| `jointRadius` | `0.025` | 尾巴骨链用于碰撞检测的半径；可小幅增加以留出间距，但它不等于整条蓬松尾巴的网格厚度。 |

同时将 `idleSwayDegrees` 和 `idleLiftDegrees` 设为 `0` 可关闭主动摆动，保留身体运动引起的物理跟随。

修改 `downwardAngles` 后，需要执行组件右键/齿轮菜单中的 **Rebuild Tail / Apply Settings**，或在非 Play 状态再次执行 **NeEEvA → Tail → Configure Selected Avatar**，才会重新计算休息弧线。单纯修改这个数组不会更新已保存的休息姿势。

在 Play 中，其他物理参数通过 Inspector 修改后会在下一次更新重建模拟；重建会让尾巴回到休息姿势后重新开始。Play 中的调参用于预览，退出 Play 后需要在编辑态重新填入希望保留的数值。为了将弧线和参数可靠地保存成 prefab override，最终在非 Play 状态执行配置菜单并保存场景。

## 批量配置

Editor 入口：`NevaTailSpringSetup.ConfigureSceneBatch`，只允许在 Unity batch mode 调用。

```powershell
& '<Unity.exe 路径>' -batchmode -projectPath '<项目目录>' -executeMethod NevaTailSpringSetup.ConfigureSceneBatch -tailScene 'Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity' -logFile '<日志路径>'
```

未传 `-tailScene` 时默认使用上述 SailingMoon 场景。安装器要求场景内只有一个匹配的 FoxTail 角色，只保存目标场景，不重新生成房间或覆盖其他场景。它会保存尾巴组件及骨骼的 prefab override，保留场景已有内容。

其他 Editor 工具也可以调用 `ConfigureLoadedScene(scene, registerUndo)` 配置已加载的场景；该方法只标记场景已修改，由调用者决定何时保存。

## 尾根稳定、末端弯曲（当前版本）

上一版虽然扩大了骨链末端的位移，却主要在 FoxTail1 上施加左右旋转和抬尾；这会扰动粗大的连接处，不符合尾根稳定、末端摆动的形状。原模型蒙皮没有反绑：实际尾尖最后 20% 的顶点平均约 93% 权重属于 FoxTail5，而 FoxTail5_end 空节点没有蒙皮权重。因此，本次改为同时观察骨链和真实网格。

当前实现保持 FoxTail1–2 稳定，只模拟并驱动 FoxTail3–5。侧弯轴由每段休息方向与角色横向的叉积确定，避免绕下垂尾巴的长轴扭转。三段主动角度按 `0.35、0.65、1` 递增，并延迟 `0、0.25、0.5` 弧度的相位；前后起伏从 4° 降至 1°，保留自然下垂和弹性。

真实 Unity PlayMode、两周期实际网格测量通过：

| 测量区域 | 左右运动范围 |
| --- | --- |
| 靠臀部的三个骨骼节点位置 | 0 |
| 后三段末端节点 | 2.25 / 8.15 / 18.39 cm |
| 可见尾根区域 | 0.445 cm |
| 可见中段区域 | 2.16 cm |
| 可见尾尖区域 | 14.80 cm |

网格数据来自 `BakeMesh` 后的固定顶点分区质心：原始直尾长度前 20%、35–65%、后 20%，分别含 1103、615、382 个顶点，覆盖两个完整周期的 77 次采样。它们衡量可见区域的整体移动，不是每根毛发顶点的最大位移。

27.46 秒运行中，移动/旋转后的收敛、三次重启、Rebuild、非尾骨与表情隔离检查全部通过，无运行异常。正后方和斜后方分别录制 77 帧、12 fps、约 6.42 秒，按真实采样时间播放。

[正后方动态](../Logs/TailSpringValidation/Reports/tail-distal-bend-rear.gif) · [斜后方动态](../Logs/TailSpringValidation/Reports/tail-distal-bend.gif) · [当前运行报告](../Logs/TailSpringValidation/Reports/tail-spring-regression.json) · [当前场景审计](../Logs/TailSpringValidation/Reports/distal-scene-audit.json)

## 第一轮扩大摆幅（旧版尾根驱动）

2026-09-11 根据实际反馈增大主动摆动。旧版只有 2° 的左右目标角度，且周期为 6 秒；实测尾尖范围仅 1.69 cm，从侧面主要是在景深方向移动，难以察觉。

当时使用 12° 左右目标角度、4° 前后起伏、3.2 秒周期，两轴都作用于尾根。以下结果只验证了该旧版骨骼末端的位移，未验证摆动沿实际尾巴网格的分布，现已被上方的分段弯曲实现替代。

真实 Unity PlayMode 验证通过：

- 预热一周期后，连续测量两个周期（6.40 秒）：尾尖左右范围 **9.83 cm**，前后范围 **5.35 cm**，左右约为旧版的 **5.8 倍**。
- 27.46 秒运行中保留原有移动/旋转、收敛、三次重启、Rebuild、非尾骨和表情隔离检查，全部通过；无运行异常。
- 使用真实渲染录制 77 帧、12 fps 的后方斜视动态，按实测时间编码为 GIF，约 6.42 秒，没有加速播放。
- 主项目编译和场景加载保存通过；场景差异检查确认只改变三个主动摆动参数，模型文件、下垂弧线及其他场景对象保持原样。

[旧版摆动录制](../Logs/TailSpringValidation/Reports/tail-root-drive.gif) · [旧版运行报告](../Logs/TailSpringValidation/Reports/tail-spring-regression-root-drive.json) · [旧版场景审计](../Logs/TailSpringValidation/Reports/sway-scene-audit.json)

## 首次下垂实现的验证记录（旧版轻微摆动）

2026-09-11 已完成主项目编译，并将组件和休息弧线保存到 `Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity` 的 NEVA 实例。直接打开该场景即可预览，进入 Play 后开始模拟。

在 `Logs/TailSpringValidation` 隔离项目中，使用与主项目 SHA-256 一致的真实 `NEVA.vrm` 和现有 Animator Controller，运行 Unity 2022.3.22f1 的实际 PlayMode、Animator 和 LateUpdate，结果通过：

- 连续运行约 23.86 秒，其中 21,233 帧检查非尾骨、表情权重和骨长；没有手动推进 Animator 或物理更新。
- 尾尖稳定在尾根下方约 56.4 cm，移动和旋转角色后有跟随响应并能收敛。
- 默认 2°、6 秒的摆动观察完整一周期，尾尖左右变化范围约 1.69 cm。
- 三次禁用/启用及一次 Rebuild 均通过，重新稳定后与参考尾尖位置的最大误差约 0.0082 mm。
- 非尾骨局部旋转和表情权重与同步对照模型相同；最大非尾骨位置误差小于 0.001 mm，骨长误差小于 0.001 mm。
- `Vrm10Instance` 始终禁用，完整 `Vrm10Runtime` 未被创建；销毁时无脚本异常或尾巴缓冲区未释放警告。
- 主场景在独立 Unity 进程中重新打开并再次配置，保存结果逐字节相同，只有一个尾巴组件；安装前后 VRM 和 Animator 的序列化配置一致。场景对象审计确认其他对象的序列化数据保持原样，源 `.vrm` 未修改。

旧版报告：[运行时结果](../Logs/TailSpringValidation/Reports/tail-spring-regression-subtle.json)、[场景保存审计](../Logs/TailSpringValidation/Reports/scene-install-audit.json)。实际模型侧视渲染：[修改前](../Logs/TailSpringValidation/Reports/tail-before.png)、[自然下垂](../Logs/TailSpringValidation/Reports/tail-natural.png)。渲染图的自然姿态阶段关闭了主动摆动以便比较，其后的独立阶段验证了默认主动摆动。

本次验证覆盖模型、身体动画和表情权重隔离；未运行真实语音对话或头显实机测试，也未测试家具碰撞。当前身体碰撞是臀部与大腿的近似胶囊体约束。
