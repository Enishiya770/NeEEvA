# NeEEvA

基于 Unity 的 AI 虚拟角色语音伴侣。VRM 虚拟形象「アントネーワ（安托涅瓦，出自《永远的7日之都》）」在 3D 房间场景中与用户进行实时日语语音对话，支持流式回复、语音打断、口型同步与图谱式长期记忆。

## 主要特性

- **VRM 1.0 虚拟形象**：基于 UniVRM 0.129.1，实时口型同步（OVRLipSync）、自动眨眼、姿态动画切换
- **实时语音对话链路**：麦克风 VAD 检测 → 本地 SenseVoice 语音识别（附带情绪 / 音频事件标签）→ LLM 流式生成 → 按句切分排队 TTS → 边合成边播放边显示
- **低延迟交互**：支持对话打断（barge-in，约 0.3s 响应）、试探性断句（tentative EOU，0.6s 静默即预判句尾）以减少等待；`<continue/>` 无缝续写、`<silent/>` 内心独白（只记录不朗读）
- **实时屏幕视觉**：主推的 qwen3.6 多模态模型可以"看"用户的电脑屏幕——角色自主输出 `<look/>` 睁眼后，每一帧感知都附最新桌面截图，能实时感知画面变化，`<unlook/>` 闭眼（详见下文「启用 Qwen3.6 与实时视觉」）
- **图谱式长期记忆**：带权重的记忆节点 + 语义边构成知识网络（`seed_memory.json` 含 21 个种子节点），运行时持久化到本地，每轮对话将 Top-30 节点注入感知帧
- **多 LLM 后端可插拔**：DeepSeek、通义千问（DashScope 云端 / llama-server、Ollama 本地双后端）、OpenAI、智谱 ChatGLM、讯飞星火、RWKV
- **多语音服务可选**：本地 SenseVoice ASR、GPT-SoVITS 声音克隆 TTS，以及 OpenAI / Azure / 讯飞 云端 TTS & STT
- **Qwen-Omni 多模态**：文本 + 语音 + 摄像头 / 截图输入，流式文本与音频输出（`qwen-omni-turbo`）
- **语音唤醒（WOV）**：通过语音触发激活对话

## 环境要求

- Unity **2022.3.22f1**
- 可选的本地服务：
  - 本地语音识别：Python 3.10+，见 `Server/SenseVoice`（FunASR SenseVoiceSmall，支持 CUDA / CPU）
  - 本地声音克隆 TTS：GPT-SoVITS（默认 `127.0.0.1:9880`）
  - 本地 LLM：llama-server / Ollama（默认 `127.0.0.1:8080`）

## 快速开始

1. 用 Unity Hub 打开本项目（首次导入会重新生成 `Library/`，耗时较长）。
2. 打开主对话场景 `Assets/AIChatTookit/Scene/chatSample.unity`（包含 NeEEvA 形象与完整对话栈；`Assets/Scenes/NeEEvARoom.unity` 是由编辑器菜单 `NeEEvA > Build Room (White-box)` 生成的白盒房间场景）。
3. 在场景内对应组件的 Inspector 中填入自己的 API Key（**仓库不包含任何密钥**）：
   - LLM：`ChatQW`（主推，见下节）/ `ChatDeepSeek` 等组件的 `api_key`
   - Qwen-Omni：`QwenOmni` 组件的 `api_key`（阿里云百炼 DashScope）
4. （可选）启动本地语音识别服务：

   ```bash
   cd Server/SenseVoice
   pip install -r requirements.txt
   python sensevoice_server.py              # 默认 cuda:0，监听 127.0.0.1:9881
   python sensevoice_server.py --device cpu # CPU 模式
   ```

5. 运行场景，开始对话。

## 启用 Qwen3.6 与实时视觉（屏幕感知）

项目主推的 LLM 是 **qwen3.6-35b-a3b**（多模态，支持视觉输入）。视觉附图只在 `ChatQW` 后端实现（`ChatQW.PostMsgStream` 的 `imageDataUrl` 重载），其他 LLM 后端不支持——想用实时视觉就必须把对话模型切到 Qwen。

### 1. 选择 Qwen 作为对话模型

在 `chatSample.unity` 中找到挂有 `ChatSetting` 的对象，把 `m_ChatModel` 指向场景里的 `ChatQW` 组件。

### 2. 配置 ChatQW 后端（二选一）

`ChatQW` 组件的 `m_Backend` 支持两种后端：

| 字段 | 云端（Cloud，阿里云百炼） | 本地（Local，llama-server / Ollama） |
|---|---|---|
| `m_Backend` | `Cloud` | `Local` |
| 模型名 | `m_ChatModelName`，如 `qwen3.6-35b-a3b`（需选支持视觉的版本，以百炼接口文档为准） | `m_LocalModelName`，填服务端加载的模型名/别名 |
| `api_key` | 必填（百炼平台申请） | 留空即可（本地服务不校验） |
| 地址 | 自动使用 DashScope 兼容模式接口 | `m_LocalUrl`，默认 `http://127.0.0.1:8080/v1/chat/completions` |

本地部署提示：llama-server 需以多模态方式启动（加载模型权重的同时用 `--mmproj` 加载对应的视觉投影文件），否则带图请求会被服务端拒绝。

其他相关字段：

- `m_EnableThinking`：Qwen3 / 3.6 思考模式开关，默认关闭（可大幅缩短首 token 延迟），云端/本地均生效
- `m_KeepRecentImages`：多模态历史滑窗，默认 `2`——只保留最近 N 条带图消息的图片，更早的自动剥图只留文字，防止视觉 token 撑爆上下文

### 3. 配置 ChatSample 的视觉参数

`ChatSample` 组件 Inspector 的「视觉(屏幕感知)」区：

- `m_EnableScreenVision`：视觉总开关（默认开）。关闭后角色永远闭眼，`<look/>` 也不生效
- `m_CaptureMode`：截屏范围——`ActiveWindow` 跟随当前前台窗口所在显示器（推荐，自动跟随你的注意力）/ `Primary` 主屏 / `Specific` 按 `m_MonitorIndex` 指定显示器
- `m_CaptureMaxDimension`：截图最长边像素，默认 `1280`（等比缩放，平衡画质与 token）
- `m_CaptureJpegQuality`：JPEG 质量，默认 `80`

### 4. 运行时如何"看见"

视觉由角色在 Agent Loop 中**自主控制**，不需要手动按键：

1. 启动会话后角色默认闭眼，感知帧会提示她「闭眼(用 `<look/>` 可以睁眼)」
2. 当你提到屏幕内容（比如"看看这段代码""这个网页怎么样"），她会在回复末尾输出 `<look/>` 睁眼
3. 睁眼状态是**持久的**：之后每一帧感知都会自动附上最新的桌面截图（约 50–150ms 一次 GDI 截屏 → JPEG → base64，随对话节奏发送），所以她能实时感知画面变化——切换窗口、滚动页面、播放视频
4. 她认为不需要再看时会输出 `<unlook/>` 闭眼，停止图像输入以节省视觉 token

限制：仅支持 Windows 平台（GDI P/Invoke 截屏）；只能看到屏幕，没有摄像头通道（单次的摄像头/截图问答请用 `QwenOmni` 模块）。

## 项目结构

```
Assets/
  AIChatTookit/       对话框架
    Scripts/LLM/        各 LLM 接入（DeepSeek / QW / chatGPT / chatGLM / SparkAI / RWKV / Ollama）
    Scripts/TTS&&STT/   语音合成与识别（SenseVoice / GPT-SoVITS / OpenAI / Azure / Xunfei / Whisper）
    Scripts/Chat/       对话编排（流式管线、实时语音、打断处理、DesktopCapture 桌面截屏视觉）
    Scripts/Memory/     图谱式长期记忆（MemoryHub / MemoryStore / MemoryRecall）
    Scripts/Expression/ 口型同步、眨眼等表情控制
    Scripts/WOV/        语音唤醒
    QwenOmni/           通义千问 Omni 多模态接入
    Scene/              chatSample 主对话场景（NeEEvA 形象 + 完整对话栈）
    Prompts/            角色人设 / 行为 / 语言提示词
    MemoryData/         种子记忆 seed_memory.json
  Model/              NeEEvA.vrm 虚拟形象及贴图
  Scenes/             NeEEvARoom 白盒房间场景（由 NeEEvA > Build Room 编辑器菜单生成）
  VRM10/ UniGLTF/     UniVRM 0.129.1（VRM 1.0 运行时，内嵌源码）
Server/
  SenseVoice/         本地语音识别服务（FastAPI + FunASR）
```

## 致谢 / 第三方

- [AIChatToolkit](https://github.com/zhangliwei7758/unity-AI-Chat-Toolkit) — Unity AI 对话框架，本项目在其基础上扩展
- [UniVRM / UniGLTF](https://github.com/vrm-c/UniVRM) v0.129.1 — VRM Consortium
- Oculus OVRLipSync — 口型同步
- [SenseVoice / FunASR](https://github.com/FunAudioLLM/SenseVoice) — 阿里巴巴语音识别模型
- [GPT-SoVITS](https://github.com/RVC-Boss/GPT-SoVITS) — 声音克隆 TTS

## 注意事项

- 所有 API 密钥均不包含在仓库中，需自行申请并在 Inspector 中填写。
- `Library/`、`Logs/`、`UserSettings/` 等 Unity 生成目录已被 `.gitignore` 排除，克隆后首次打开 Unity 会自动重新生成。
- 角色与相关设定仅用于学习交流。
