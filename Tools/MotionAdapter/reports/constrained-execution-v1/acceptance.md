# 显式约束执行验收 v1

实际 Unity 2022.3.22f1 PlayMode 验收通过（进程30364退出0）：4个动作角色及4个同步 Animator 对照，49个fixture-case，17,867个实际运行帧，93.04秒。完整保留失败负例、运行数据与446张实际网格帧；报告中的通过指几何约束和生命周期，不是自然度或开放动作语义成功率。

| 实例 | 模型资产 | 更新路径 | 根yaw / scale | case数 |
|---|---|---|---|---|
| fixture-0 | NEVA.vrm | VRM disabled + Animator | 25° / 1 | 13 |
| fixture-1 | NeEEvA.vrm | VRM disabled + Animator | 65° / 0.8 | 12 |
| fixture-2 | NEVA.vrm | ControlRig | 65° / 1.2 | 12 |
| fixture-3 | NeEEvA.vrm | ControlRig | 65° / 1 | 12 |

## 实测结果

- 固定方向误差最大0.05234°，肘屈曲误差最大0.01979°，均低于5°/4°门槛。覆盖forward、outward、up、down、forward-up、outward-up与current，肘角覆盖0°、8°、110°。
- 局部动作期间肩位对同步Animator的残差≤0.00494毫米，肘相对肩漂移≤0.00137毫米，上/前臂旋转漂移0°。躯干原有移动保留；首次失败的约5毫米肩肘共同平移没有被锁死或放宽门槛掩盖。
- 双腕角色up轴±10°、2次摆动，两边均记录3次非零半波换向；头部角色right轴约±8°、1次摆动。包含不对称左current/右forward以及已保持姿态上的current双腕接续。
- 所有停止、禁用、到期和失联恢复后，实际骨骼与仍运行的Animator对照最大误差0°。根、未控制腿/躯干和未控制表情通道保持。
- NEVA Humanoid上真实BlinkController与Audio2LipScript.Update持续工作：嘴型权重变化89、眨眼变化100；aa输入是合成viseme，未播放音频或声称真实语音同步。

## 真实生命周期与负例

使用保持inactive的ChatSample阻止聊天Awake/Start及网络，通过其实际DispatchDialogueMotion/CancelDialogueMotion事件连接真实Bridge和Controller，动作仍在正常Player→Animator→VRM→12000观察器之后由12500测试组件采样。没有手调Tick、Animator.Update、VRM.Process或用harness代叫Observe。

- joint=none、end=hold且seconds=6：四实例都在1.348秒实测准备达标后直接holding，未空等6秒acting。
- 持姿时依次触发user-started-speaking、barge-in、new-user-turn，真实后续帧仍保持且revision不变；再通过新的current腕动作接续。动态运动期间用户开口则确实取消并回待机。
- 真实Controller持姿30秒自动到期；显式stop、禁用/重新绑定均清除动作。禁用Player和Controller请求均被拒绝，重新启用后没有执行被拒绝的旧计划。
- 11900阶段实际骨骼覆盖阻止准备达标：3秒失败且从未进入acting。
- 已holding时关闭真实Observer：0.5秒后报告observation-timeout、observationFresh=false并释放；恢复观察器没有复活动作。

## 证据与边界

本路线是几何IK约束和受控局部曲线，未改变adapter或把结果当成原生ARDY轨迹。组合覆盖是上述具体组合，并非所有参数笛卡尔积。衣物/身体碰撞和自然度仍需人工观看。

预览按实际hips/head/臂长构图，并从真实当前网格BakeMesh，保留实际时间戳，没有姿态修正或运动重采样。outward预览有角色左指尖出画，不能声称画幅覆盖完整；完整实际骨骼仍参与几何验收。

- 紧凑逐case报告：[acceptance.json](acceptance.json)，保留全部case聚合指标及源码/模型SHA。
- 原始逐帧报告：`Server/ARDY/runtime/constrained-execution-v1/full-pass-v1/constraint-regression.json`。
- 原始报告SHA256：`ea455e98cea8fd87561f7ee0dcea71677c7e5276621311257e24329426c46412`（247,584,985 bytes）。
- 首次失败证据：`Server/ARDY/runtime/constrained-execution-v1/primary-failure-v1.json`；测量归因：`Tools/MotionAdapter/reports/constraint-primary-measurement-diagnosis-v1.json`。
- 可复现入口：在隔离工程运行`ArdyConstraintRegression.RunBatch`；入口自行退出，不能添加`-quit`，不改变或保存正式场景。
