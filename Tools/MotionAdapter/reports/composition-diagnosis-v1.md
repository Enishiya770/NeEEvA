# 双臂前伸＋手腕轻摆：固定诊断

结论：本次记录同时存在跨轮未保持姿态，以及原生生成无法稳定把腕部动作与肩肘动作分离的问题。不是简单把“前方”映射成“侧方”，也不能说手腕完全没有运动。仅修改描述或携带起始姿态，都没有在固定八组探针中可靠完成用户要求。

## 原始证据

最近八份动作 trace 已原字节归档到 `Server/ARDY/runtime/composition-diagnosis-v1/traces`，总计 2,391,168 字节，每份 SHA256 在 archive-manifest.json。只读取动作描述、请求、返回与完成原因，没有聊天全文或音频。七条正常完成；revision35 在 5.66 秒因用户开始说话而结束。24 个返回窗口均 HTTP200。

revision50 是“双臂向前伸直并保持”；revision54 是“同样前伸，然后左右挥动”。两次开始相隔 19.795 秒，前者完成后 12.781 秒下一次才开始。revision54 初始历史末帧的双腕位于肩下约 37.2 / 38.4 厘米，与此前实际 Animator 待机参考的 13 骨骼平均角差为 1.16 度；与 revision50 最后返回姿态的平均角差为 38.23 度。因此下一条没有从先前前伸状态继续。

当前 trace 不标明实际场景模型。以下米制目标数字是完整 NEVA.vrm 资产层级、当前 Player 的 UpperBody 全权重映射的 CPU 重建，不冒充用户画面或实际 Animator 录制。原始 Core27 对照、逐帧数据和假设均保留在报告。

revision42 的侧平举，末两秒腕相对各自肩的横向 X 约 ±0.41 米、前向 Z 仅 0.035–0.047 米。revision50 前伸时，X 约 ±0.02 米、Z 约 0.366 / 0.385 米。坐标方向能区分前伸和侧展。

revision54 的末两秒，肘角中位约 82 度（180 度才是伸直），腕前伸仅约 0.232 / 0.211 米；高度变化范围 0.303 / 0.276 米，而左右变化范围仅 0.024 / 0.028 米。主要混入屈肘、放臂，而不是保持直臂的轻微腕摆。手相对前臂旋转累计 108.5 / 116.5 度，说明存在局部手腕运动；累计角度不等于动作幅度，也不证明正确挥动方向或次数。

## 固定八组真实 HTTP 对照

使用当时现有 8093 服务，启动前检查 ready、候选 SHA c1006e、queue0；没有加载新模型、重启服务或更改 Player。两种描述 × 两种历史 × seeds0/1，共八条，每条三窗口。24 窗 HTTP200、首窗 live-qwen、后两窗 live-condition-reused，八次自有 revision 的清理取消均200，最终 queue0。

描述一为原始“Both arms extend straight forward at shoulder level, palms facing inward, then wave side to side”。描述二固定为“Keeping both arms straight forward at shoulder height with elbows still, gently wave both hands side to side at the wrists twice.”，未再试其他说法。

起始历史分别是 revision50 原始请求里的实际待机16帧，以及该条三个返回片段拼接后的最后16帧。后者没有挑选峰值：根和腿置 identity，Spine1 插值、手端点跟随手，明示为生成姿态投影，并非实际 VRM 观测。该输入末帧双腕前向 Z 为 0.412 / 0.408 米、肩下约 0.072 / 0.091 米，肘约153度。

| 固定条件 | 观察 |
|---|---|
| 原文＋待机，两个 seed | 仍有明显屈肘/放臂；末两秒高度下降，未稳定保持前伸 |
| 原文＋生成末16帧，两个 seed | 起点更前伸，后续仍弯肘至约84–115度、前伸缩短；历史不是持续约束 |
| 明确腕摆＋待机，两个 seed | 末两秒双腕均约肩下0.41米，基本放手，未建立前伸姿态 |
| 明确腕摆＋生成末16帧，两个 seed | 大幅全臂耦合：末两秒腕高度范围0.58–0.65米，上臂相对胸最大偏离约109–115度，不能称为保持肩肘的轻腕摆 |

这说明持姿需要独立验证的持续约束，腕部局部动作也需要可控机制；没有证据表明当前仅凭文本和16帧历史的原生生成已经具备这两项保证。本轮没有实施这种新控制层，也没有选择新的模型。

## 复核

- `composition-diagnosis-v1.json`：原始报告哈希、八组摘要、三阶段肩/肘/腕相对旋转耦合。
- `Server/ARDY/runtime/composition-diagnosis-v1/report.json`：八条归档 trace 的时间、初始姿态、源/目标逐窗口测量。
- `Server/ARDY/runtime/composition-diagnosis-v1/http-probes/report.json`：每个真实请求/响应文件及 SHA、provenance、取消结果、服务健康记录。
- 两个目录中的 render-manifest.json：原始响应旋转完整拼接的片段，未改动作，供后续可视复核；本轮未启动 Unity。

CPU 已用已知旋转核验测量方向：中性直臂沿侧向 X，左右臂各旋转相反90度后均指向 +Z，直肘角为180度。手掌姿态、恰好两次、自然度和实际目标网格碰撞均未自动判定。
