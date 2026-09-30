# 2026-09-26 实际对话评估与 Spark 迁移建议

本次仅分析日志、阅读源码、查询官方兼容资料和只读检查两台 Spark；未改变生产代码、服务配置或部署位置。

## 证据与统计口径

- 日志快照：[Editor.snapshot.log](../work/log-evaluation-20260926-0002/Editor.snapshot.log)，来自本机最新 Unity Editor.log。对话事件覆盖日本时间 2026-09-25 23:58:09 至 2026-09-26 00:02:17，00:02:26 前后是退出清理。
- 可复算统计：[analyze.py](../work/log-evaluation-20260926-0002/analyze.py)、[analysis.json](../work/log-evaluation-20260926-0002/analysis.json)。没有把系统提示词或凭据复制到本文。
- Spark 实况：[spark-inventory.json](../work/log-evaluation-20260926-0002/spark-inventory.json)。资源数值是检查时的快照，不是对话期间的资源曲线。
- 这是实时语音会话：12 次 capture 均以 `effective-quiet` 结束。不能沿用历史某次“全部按住说话”的模式假设。
- 7 轮观察到正式 AudioSource 推进。其他片段包含续说/新输入替代；不能把它们全部计为服务失败。
- EOT 指录音关闭；AudioSource 推进不是物理扬声器首音。`estimatedStopToCloseMs` 是声学估计，不是人工标注的实际停说时间。

## 延迟和 TTS

| 指标 | 结果 |
| --- | --- |
| 录音关闭到正式 AudioSource 首次推进 | 7 轮，均值 5.97 s，中位数 5.87 s，范围 4.26–8.13 s |
| 估计停说到录音关闭 | 12 段，约 1.50–2.43 s，中位数 1.71 s |
| 录音关闭后等待最终 ASR 回调 | 均值 0.74 s，中位数 0.58 s，范围约 0–2.13 s；预先识别能覆盖部分等待 |
| FastLoop 完成决策 | 10 次，均值 1.99 s，范围 1.42–2.60 s |
| ActorIntent 完成判断 | 9 次，均值 0.99 s，范围 0.86–1.20 s |
| TTS 首 PCM | 17 次有观测，均值 0.89 s，中位数 0.78 s |
| TTS 预缓冲就绪 | 15 次有观测，均值 2.45 s，中位数 2.60 s |
| 正常完成接收与播放的 TTS | 14 段，合计音频 66.95 s，请求接收耗时合计 98.07 s |
| 正常完成 TTS 的请求耗时 / 音频时长 | 每段 1.32–1.96，算术平均 1.53；按总时长加权约 1.46 |
| 再缓冲 | 14 段中 7 段发生，合计 9 次暂停、11.88 s 等待 |
| 欠载补零 | 合计 2.24 s；与暂停不同，不能直接当成人工测得的可闻停顿 |
| 句间衔接 | 7 次，均值 1.13 s，范围 0.31–1.68 s；目标停顿通常 0.30 s |

RTF 统计只纳入 HTTP 接收 `Success` 且播放成功的 14 段。另有一段被打断的 HTTP 接收报告，虽记录了 200 响应头，但最终结果为 `ConnectionError`，收到的音频不完整；其约 2.20 的比值不得当作完整合成性能。

最后一轮询问整体视野，从录音关闭到开口约 8.13 s，可拆为：最终 ASR 约 1.59 s、FastLoop 约 2.60 s、ActorIntent 约 1.03 s、后续 TTS/播放约 2.89 s，以及少量调度。不是这 8 秒全部花在 TTS，也不能靠迁移 TTS 消除静音收尾策略或两次模型调用。

本次 `audio.stream_clip_created`、欠载位置和暂停字段证明使用了上一轮修复后的播放器。短句基准中的改善不能直接推广到本次更长的真实回复：最后一段 80 字、14.58 s 音频，接收用时 19.30 s，播放再缓冲约 2.84 s。预缓冲很快到达 1.5 s 上限，无法持续掩盖后端慢于播放的吞吐。

意图判断与 TTS 的并行并非每轮都实现。日志有 5 次草稿音频准备，3 次批准并采用（其中一轮随后被用户输入打断），2 次因最终文本纠正而丢弃。其他未准备的具体门控原因没有独立日志，不能推定它们全都成功重叠。

## 视觉输入与动作判断

12 轮 `vision.latest_state` 都是 `ready / image=true / fresh=true`，图像年龄约 0.13–0.85 s。但新快路径未将图像交给回答模型：

- [ChatSample.FastLoop.cs](../Assets/AIChatTookit/Scripts/Chat/ChatSample.FastLoop.cs) 中 `TryDispatchFastLoop` 只向快路径后端传文字上下文与 prompt；`imageUrl` 留给历史登记和 legacy 回退。
- `BuildFastLoopContext` 提供头部角度、眼睛开闭和任务状态，没有当前画面或可靠的视觉观察。眼睛睁开不是看见物体的证据。
- [ChatQW.FastLoop.cs](../Assets/AIChatTookit/Scripts/LLM/QW/ChatQW.FastLoop.cs) 构建纯文本 system/user 消息。
- 协议虽要求视觉问题走 legacy，本次完成的 10 个 FastLoop 决策却全部走 `reply`，没有一次交给携图路径。

因此“你能看到什么”“描述整体视野”被无视觉输入的文字模型回答。修复方向是确保需要当前视觉证据的问答实际进入携图或已完成视觉观察的路径，加入真实出站 payload 和可见物回答回归。不能只检查截图是否存在，也不能仅靠增加提示词保证资料已被使用。

第 9、10 段视觉追问又分别被 ActorIntent 判为 `claim/head.hold`，执行层因无对应已完成头部任务而使用 `unsupported-completion-claim` 纠正，原预热音频被丢弃。日志仅保留准备首句和分类，没有完整私有草稿及裁判 evidence，不能断言全部裁判依据都错误。可以确认的是视觉问答被头部完成声明检查替换成了不合语境的动作纠正，需补视觉能力描述与动作完成声明的边界用例及短 evidence 观测。

本次没有实际 head/act/wait/cancel/replan/legacy 完成决策，也没有移动、歌唱或等待计划的正式受理记录。这段日志不构成那些执行器再次失败的证据。

5 次 Room 预计算都被丢弃，`completed/accepted/finalConfirmed/consumed` 都为 false。归属应看 `captureSerial`（5、7、8、10、11），不能只用沿用旧 response 的 turnId。末尾 `room-bridge-disabled/destroyed` 是退出清理，不是正常对话期间断线。

## 两台 Spark 的建议分工

只读检查确认两台都是 ARM64 / GB10。初步分工如下，精确版本与采集时刻以 inventory 为准：

| 位置 | 检查时现状 | 建议 |
| --- | --- | --- |
| Windows 电脑 | 本次对话后检查时本地语音端口已不再监听；当前 GPU 空闲不能代表对话期间负载 | 保留 Unity 渲染、麦克风/视角采集、播放、角色控制与即时取消 |
| `.175` | Qwen vLLM，约 100 GiB GPU 分配/预留，系统可用约 8 GiB；`gpu-memory-utilization=0.84`、`max-model-len=262144`、`max-num-seqs=1` | 先维持 LLM 角色；这些分配包含 KV/缓存预留，不是模型净重，不宜立即叠加全部服务 |
| `.163` | ARDY、特征和既有 Gradio worker 等约 34 GiB GPU 分配，系统可用约 80 GiB | 优先试 TTS，再 ASR；RVC/Seed-VC/SVS 分服务验证并控制重任务准入 |

主机到 Spark 的当前以太网协商速率是 100 Mbps，短 ping 样本均小于 1 ms。32 kHz、16 bit、单声道 PCM 仅约 0.512 Mbps；16 kHz 单声道上传约 0.256 Mbps，常规音频不应被链路吞吐主导。但模型传输、高频视觉图像与并发请求仍需单独测量。Spark 间高速直连不会自动升级电脑到 Spark 的这段链路。

NVIDIA 公布 Spark 为 20 核 Arm、128 GB CPU/GPU 统一内存、273 GB/s 带宽。内存容量支持放下更多模型，但不保证同一 GPU 上同时推理不会互相增加延迟。[硬件规格](https://docs.nvidia.com/dgx/dgx-spark/hardware.html)

## 迁移可行性与已知障碍

| 服务 | 判断 | 必须处理的事项 |
| --- | --- | --- |
| TTS / GPT-SoVITS | 优先级最高，可迁但需实测 | 当前本地是 Windows PyTorch 2.0.0+cu118；重建 ARM/Blackwell 环境，保持源码、角色权重、参考音频及语言资源；验证流式 WAV 与取消 |
| ASR / SenseVoice | 第二阶段，相对容易 | 主体已上传音频；同步 VAD、声纹、音高、歌曲库及参考音频，保留已调好的识别和最终分句行为 |
| 角色 RVC / 9882 | 兼容风险中等偏高 | Windows 安装器锁 cu118；需要 ARM FAISS/音频依赖、角色模型/index/HuBERT/RMVPE。当前项目已有 Transformers HuBERT 兼容层，不应笼统说只能被旧 fairseq 卡死 |
| Seed-VC / 9882 备用路线 | 单独验证 | 不复制 Windows 的 python_packages；固定实际 vendor/权重版本，替换底层依赖后检查实际角色转换路线 |
| SVS / SoulX-Singer / 9883 | 最后验证，风险最高 | 现有安装器与上游依赖多处固定旧 Torch/NeMo/音频组件；分开验证基础推理、预处理、歌词/音高对齐、常驻 worker 与实际语言路线 |
| 其他模型服务，如 embedding | 先确认是否启用再迁 | 同样固定模型、数据和 API；不能仅因启动器列出了端口，就认定本次对话使用了它 |

NVIDIA 的 Spark 依赖表提供 CUDA 13.0 和框架容器支持路径；这不等于当前旧服务的所有依赖已兼容。应每服务独立环境，避免安装语音依赖时覆盖正在工作的 vLLM 或 ARDY 环境。[官方依赖表](https://docs.nvidia.com/dgx/dgx-spark-porting-guide/porting/dependencies.html)、[容器运行支持](https://docs.nvidia.com/dgx/dgx-spark/nvidia-container-runtime-for-docker.html)

GPT-SoVITS 上游安装器已有 aarch64/arm64 分支，ARM 上选择普通 onnxruntime；这是迁移可行的依据，不是本项目旧版本和角色音色已通过 Spark 验收的证明。[上游安装脚本](https://github.com/RVC-Boss/GPT-SoVITS/blob/main/install.sh)

不能只修改 URL：TTS 的 `ref_audio_path`、9882 的 `target_path`、9883 的 `prompt_audio_path` 是服务端文件路径。Unity 当前可能把它们解析为 Windows 绝对路径，Spark 无法直接读取。需要服务端资源 ID/路径映射或上传协议；9883 已有 `NEEEVA_SVS_PROMPT_WAV` 覆盖入口。ASR 的声纹和歌曲资料应有一个明确主存储，避免两边分别学习而状态分叉。Windows 启动器也需接入远端 systemd/容器，不能把 `.venv/Scripts` 和 PowerShell 进程逻辑原样搬过去。

## 建议实施顺序与验收

1. 并行准备 TTS 迁移和快路径视觉修复。前者解决本机吞吐压力，后者解决没有给模型所需资料的问题，互不替代。
2. 在 `.163` 新建隔离 TTS 服务，固定当前源码、权重、参考音频与语言资源；保留本机地址作为回退。先测当前精度，另行评估低精度收益，避免同时更改音色和部署位置。
3. 用 Unity 前台真实场景请求新服务，测短句、长句、中日文、首次/热启动、连续说话、取消与新请求紧接旧请求。不能只验证端口和 health。
4. 性能门槛建议以“稳定快于实时且有余量”为中心，例如长句 RTF 尽量低于 0.7–0.8，并在 ARDY/LLM 工作时保持可接受的首音和低再缓冲。这是验收目标，不是已测得的 Spark 性能。
5. TTS 达标后迁 ASR，复测长句识别与收尾。再迁 RVC/Seed-VC，最后 SVS；重型歌唱任务需要明确的队列、准入和取消策略，避免同时把实时语音拖慢。

当前建议是将 AI 推理服务逐步迁入两台 Spark，优先利用 `.163` 的余量；保留本机交互执行端。迁移值得做，但不能宣称一次全搬就能同时解决视觉、动作协议和所有延迟问题。
