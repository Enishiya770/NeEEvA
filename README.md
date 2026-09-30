# NeEEvA · DGX Spark 服务版

基于 Unity 的交互式 AI 角色项目。角色可以通过语音和文字与用户交流，使用视觉、记忆和技能，并在场景中执行移动、跟随、头部姿态、表情等行为。

**当前分支：`spark-all-services-20260930`。** 这是截至 2026-09-30 的开发快照：Unity、麦克风采集、界面、渲染和音频播放运行在用户电脑；LLM、动作、ASR、TTS、变声和声音学习服务运行在两台 DGX Spark。歌唱分段仍是实验功能，不能把本分支视为已经解决全部歌唱识别问题的正式发行版。

## 当前包含什么

- 实时语音与按住说话：增量转写、最终转写、发声结束判断和回复打断。
- 流式 LLM 与 TTS：按句组织输出，异步合成、缓冲、播放和取消。
- 快慢思考框架：短输出决策、角色输出意图识别、执行计划、条件等待及执行结果反馈。当前判断主要使用 Qwen，尚未部署 Jev 或新的多模态歌唱判断模型。
- 角色行动：场景空间感知、移动与跟随、注视和头部姿态、表情与动作桥接。最终效果取决于场景绑定、导航和动作服务配置。
- 视觉和记忆：可控视觉输入、图谱记忆、临时理解与心里话；临时候选与已正式输出的内容分开处理。
- 声音教学：角色先提出候选区间，用户通过波形选区、拖动边界或自然语言纠正，再确认说话／唱歌／环境音；原音、标注和后台学习结果持久化保存。
- 歌唱素材与变声：保留素材来源、版本和授权边界，通过 RVC 将合适素材转换为角色音色。SoulX-Singer／9883 不在本分支默认启动链中。

## 服务拓扑

下面的 IP 是当前双 Spark 环境的示例值；更换设备后需修改相应启动参数和 Unity Inspector 地址。

| 位置 | 服务 | 默认地址／端口 | 说明 |
| --- | --- | --- | --- |
| 用户电脑 | Unity 2022.3.22f1 | — | 采集、UI、场景、角色、播放 |
| Spark A：`192.168.114.175` | Qwen / vLLM | `8888/v1/chat/completions` | 模型别名 `qwen3.6-35b-a3b` |
| Spark B：`192.168.114.163` | Native ARDY | `8093` | 动作服务，`/health` |
| Spark B | 动作特征服务 | `8890` | ARDY 服务端使用，部署合同见动作文档 |
| Spark B | GPT-SoVITS TTS | `9880/tts` | 独立容器，CUDA FP32 |
| Spark B | SenseVoice ASR | `9881` | 转写、VAD、说话人和歌唱相关接口 |
| Spark B | 角色 RVC | `9882/convert` | 独立容器，CUDA |
| Spark B | 声音学习 | `9884` | 独立容器；默认 CPU、2 线程，教学候选与后台训练 |

声音学习服务目前使用 Spark 的 CPU，不表示必须把所有任务都放到 GPU。其声音分类结果需要用户确认，`execution_authorized=false`；服务上线不等于候选分类器获得正式裁切或播放权限。

## 在已部署的双 Spark 环境启动

需要 Windows PowerShell、OpenSSH、Git，以及 Unity Hub 安装的 **Unity 2022.3.22f1**。Spark 需要已有模型、对应服务环境及 SSH 登录权限；下面的启动入口不会自动下载数十 GB 的模型。

```powershell
git clone --branch spark-all-services-20260930 --single-branch https://github.com/Enishiya770/NeEEvA.git
cd NeEEvA

# 检查脚本和目标参数，不启动服务
./Tools/start_spark_all.ps1 -CheckOnly

# 启动全部已部署的 Spark 服务；不回退到本机推理
./start_spark_all.cmd

# 查看远端服务状态
./status_spark_all.cmd

# 打开 Unity，并为该新 Editor 进程禁用系统代理转发
./start_unity_direct_services.cmd
```

默认 SSH 用户为 `spark_root_001`，密钥路径为 `$env:USERPROFILE/.ssh/id_ed25519_dgx_spark`。仓库不包含私钥。更换地址或密钥：

```powershell
./Tools/start_spark_all.ps1 -LlmHost 192.168.114.175 -ServiceHost 192.168.114.163 -IdentityFile 'C:/Users/your-name/.ssh/your-key'
```

启动参数只控制服务管理，**不会自动改写场景中的地址**。本分支的公开示例 `Assets/AIChatTookit/Scene/chatSample.unity` 已配置上述 Spark ASR、TTS、VC 地址及 OpenAICompatible 后端；Qwen、ARDY 和声音学习组件使用当前 Spark 默认配置。其他旧演示场景仍保留各自历史配置。

统一入口沿用当前 ARDY 开发环境的独立特征模型与未验证适配器选项。动作适配器仍属于开发配置，不能据此宣称已通过全部动作泛化验证。入口复用现有健康服务，不强制重启，也不自动覆盖远端版本。

## Unity 场景与配置

打开 `Assets/AIChatTookit/Scene/chatSample.unity`。等待 Unity 导入完成后，在 Inspector 核对：

| 组件／字段 | 当前值 |
| --- | --- |
| ChatQW `m_Backend` | `OpenAICompatible` |
| ChatQW `m_OpenAICompatibleUrl` | `http://192.168.114.175:8888/v1/chat/completions` |
| ChatQW `m_OpenAICompatibleModelName` | `qwen3.6-35b-a3b` |
| ChatQW `m_ArdyServiceUrl` | `http://192.168.114.163:8093` |
| SenseVoice 服务地址 | `http://192.168.114.163:9881` |
| GPTSoVITS `m_PostURL` | `http://192.168.114.163:9880/tts` |
| GPTSoVITS `m_ReferWavPath` | `/home/spark_root_001/neeeva-voice/tts/release/refs/41041.wav` |
| GPTSoVITS `m_VoiceConversionReferencePath` | `/home/spark_root_001/neeeva-voice/vc/release/refs/41041.wav` |
| ChatSample `m_HumSVCURL` | `http://192.168.114.163:9882/convert` |
| ChatSample `m_AutoStartHumSVC` | 关闭，避免启动旧本机服务 |
| 声音学习面板 | `http://192.168.114.163:9884` |

远端参考音频字段必须是对应 Spark 服务能访问的 Linux 路径，不能填 Windows 文件路径。LLM 还支持环境变量 `NEEEVA_OPENAI_BASE_URL`、`NEEEVA_OPENAI_MODEL`、`NEEEVA_OPENAI_API_KEY`；这里的 BASE_URL 使用完整聊天接口 URL。

本地开发使用的 SailingMoon 房间因素材再分发限制未包含在公开仓库。公开示例可用于对话与服务接入；完整房间中的空间交互需要自行准备场景资源、导航和角色绑定。已有合法本地素材时，可参考 [私有房间集成说明](Tools/SailingMoon/README.md)。

`start_voice.cmd`、`start_all.cmd` 等旧入口为兼容用途保留。`start_voice.cmd` 依赖被 Git 忽略的 `Server/RuntimeLogs/voice_routes.json`，缺省会选择本机服务；**本分支请优先使用 `start_spark_all.cmd`**。旧 `set-*-route.ps1` 绑定本地私有房间，不应直接用于刚克隆的公开示例。

## 首次部署与模型资源

GitHub 保存源码、Unity 必要资源、部署脚本和说明，**不包含完整推理环境、模型权重、用户录音、声纹、私人记忆或学习数据**。从零部署需要自行准备有权使用的模型与角色资源。

- **LLM / ARDY：**[双 Spark 动作服务说明](docs/spark-ardy-stack.md)，入口 `Tools/neeeva_spark_ardy_stack.ps1`。需要 vLLM 模型、ARDY Core 和匹配的动作特征／适配器。
- **TTS：**[Spark TTS 说明](Server/TTS/README.md)，部署入口 `Tools/TTS/deploy-spark-tts.ps1`。使用明确指定的 GPT-SoVITS 源码、权重和参考音频。
- **ASR：**[迁移与运行状态](docs/spark-voice-deployment-20260926.md)、[准备工具](Tools/ASR/README.md)。需要 SenseVoice、FSMN-VAD、CAM++ 等资源；早期准备文档是历史记录，部署完成状态以迁移文档为准。
- **RVC：**[Spark VC 说明](Server/VC/README.md)，部署入口 `Tools/VC/deploy-spark-vc.ps1`。
- **声音学习：**`Server/SingingLearning/spark/` 提供独立 Docker 环境，部署入口如下。

```powershell
./Tools/SingingSegmentation/deploy-spark-learning.ps1 `
  -Python 'C:/path/to/python.exe' `
  -Checkpoint 'D:/models/Cnn14_DecisionLevelMax_mAP=0.385.pth' `
  -Labels 'D:/models/class_labels_indices.csv' `
  -Dependencies 'D:/panns-deps'

./Tools/SingingSegmentation/manage-spark-learning.ps1 -Action status
./Tools/SingingSegmentation/manage-spark-learning.ps1 -Action verify
```

`Dependencies` 内需包含 `panns_inference/models.py` 和 `panns_inference/pytorch_utils.py`。检查点采用固定 DecisionLevelMax 版本，打包脚本校验 SHA-256，不会用同名的其他模型静默替代。参数缺省指向开发机的本地实验目录，克隆者应显式提供资源路径。资源获取背景见 [分段工具说明](Tools/SingingSegmentation/README.md)。

首次部署脚本只打包服务代码与指定模型，不打包用户数据；默认远端目录为 `/home/spark_root_001/neeeva-voice/learning`，容器名为 `neeeva-spark-learning`。已有不同版本时脚本拒绝覆盖，升级需单独安排切换。Spark 若无法直接访问包仓库，可为安装过程显式传入 `-BuildProxy`；正常推理不依赖安装代理。

## 声音教学与数据保存

在角色面板进入“声音学习／教她”，选择用户档案，采集声音，检查角色给出的候选区间。可以回放选区、拖动边界、选择类别，或用语言指出“前面是在说话，后面才是唱歌”，再确认调整后的区间。

- 已保存原音、人工标注及模型结果存放于 Spark 的 `learning/data/`，退出 Unity Play 不会删除服务端数据。
- Unity 尚未上传的录音保存在 `Application.persistentDataPath/PersonalSingingLearning/outbox`。迁移服务地址后，旧地址的待上传队列不会自动发送给新地址，需要检查处理。
- 角色外放回声、混合声音和未确认片段不能当作已经确认的用户歌唱训练样本。
- 当前分类器只能提出教学候选。音高融合、连续状态、局部边界和三分类实验均保留评估与采用检查；未通过检查的候选不能进入正式裁切。
- “文本＋预训练声音理解＋小型判断模型”的新路线仍处于下一阶段计划中，本分支没有把它描述为已实现功能。

详细状态见 [教学交互](docs/voice-teaching-interaction-20260930.md)、[三类证据评估](docs/category-evidence-implementation-20260930.md) 和 [声音学习 API](Server/SingingLearning/API.md)。文档中指向 `work/`、`Logs/` 或运行数据的证据路径属于本地实验档案，不随公开仓库发布。

## 目录

```text
Assets/AIChatTookit/       Unity 对话、语音、记忆、意图执行与教学界面
Assets/Editor/            编辑器工具与集成回归入口
Server/ARDY/              动作服务与原生桥接
Server/SenseVoice/        ASR、VAD、说话人及歌唱相关服务
Server/TTS/               Spark GPT-SoVITS 包装与部署
Server/VC/                Spark 角色 RVC 包装与部署
Server/SingingLearning/   采集档案、标注、候选分类、训练与评估
Tools/                    启动、部署、运输层检查及离线回归工具
docs/                     架构、部署和阶段评估记录
Packages/                 Unity 包依赖
ProjectSettings/          Unity 项目设置
```

## 状态检查与常见问题

- **服务启动但 Unity 报 502：**先检查电脑到 Spark 的直连，再使用 `start_unity_direct_services.cmd` 启动新的 Editor。已打开的 Editor 不会继承新的代理环境。
- **ASR / TTS 正常，角色不执行动作：**检查 ARDY、动作特征合同、场景绑定和执行反馈。口头答应不代表动作已经执行成功。
- **唱歌包含讲话或短哼唱被漏掉：**这是当前明确保留的识别限制。不要把某次局部改善当成整体分段已通过验证。
- **声音学习列表为空：**确认面板地址、用户档案和服务端数据目录；新部署默认没有其他用户的私人数据。
- **模型或资源缺失：**按对应服务文档准备依赖，禁止依靠旧机器的绝对路径推断新机器已经安装。

服务管理脚本的健康检查、模型烟测和 Unity 回归检查覆盖的层次不同。历史延迟测试见 [Spark 语音迁移报告](docs/spark-voice-deployment-20260926.md)，其数值不能直接当作任意设备上“用户说完到角色首音”的保证。

## 资源与发布范围

第三方模型、VRM 角色、字体、音频及工具保留各自许可和来源要求。仓库不对这些资源统一授予额外使用权。私有房间、模型安装、缓存、录音、个人标注、日志及恢复备份已通过 `.gitignore` 排除；请不要用 `git add -f` 将这些本地数据加入公开分支。
