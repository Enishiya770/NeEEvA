# DiffSinger 日语歌声渲染器（外部 SVS 后端）

给 `Server/SVS` 的语种路由用的**日语**渲染后端。配置好后，日语歌声不再走 SoulX 的
内置假名适配（那条路把假名映射到 SoulX 的**英语**音素表，听感是英语口音），而是用
原生日语音素集的 DiffSinger 音源生成，角色音色再由 Unity 侧的 svc-post-polish
（9882 的 RVC）负责。

> 状态：**功能可用，音质待调**。全链路已跑通（DiffSinger 5.4s + RVC 转音色），
> 日语发音经人工确认正常，音色也确实变成角色本人；但转换后仍有偏沙哑的残留，
> 见文末「已知问题：沙哑」。
> ⚠️ onnxruntime 必须锁 1.23.x 且用纯 CPU 包，原因见「运行时版本（关键）」。

## 为什么这样分工

DiffSinger 负责**发音正确的日语演唱**，RVC 负责**角色音色**。所以音源不需要是角色
本人训练的模型——这也是社区常见做法。选音源时质量固然重要，但**许可条款**才是硬约束。

## 选用的音源：波音リツ（Namine Ritsu）DiffSinger Ver.1.0.1

- 官网：<https://www.canon-voice.com/>（音源下载在 `/voicebanks/` 页 `#etcvocal`）
- 条款：<https://www.canon-voice.com/terms/>
- acoustic 模型制作：カノン／variance 模型：ちかの／使用数据库：波音リツ歌声データベース Ver.2

选它的理由是条款在同类角色音源中**异常宽松**，且明确覆盖本项目的用法。官方「要約」原文要点：

- 商用利用可です（可商用）
- 音源の転載、再配布可（可转载再分发）
- 原音を加工しての転載、再配布可（**可加工后再分发**）
- **他キャラクターへの声当て可**（**可用于为其他角色配音**）← 即"生成后用 RVC 转成角色音色"
- 一部音素を他キャラクターへ流用可
- クレジット表記不要（无需署名）
- 第6条：商用・非商用を問わず無償かつ非独占的な使用権

附带约束（务必注意）：

- 「他ソフトウェア上で利用する場合はそのソフトウェアの規約に従う」——本项目还用到
  **nsf_hifigan 声码器**，它来自 OpenVPI 社区，许可为 **CC BY-NC-SA 4.0（非商用）**。
  因此整条链路的实际约束以声码器为准：个人使用没问题，商用需另行解决声码器授权。
- 第8条2：制作者若认为不当并要求停止公开，需配合。

对比过但**未采用**的音源：戯白メリー（明确禁止 AI 学习、商用需事先报告）、
Yamine Renri（条款未公开写明）。

## 安装（约 1.4 GB，均不入库）

```powershell
# 1) 音源
curl -L -o NamineRitsu_DiffSinger.zip https://www.canon-voice.com/voice/NamineRitsu_DiffSinger.zip
# 解压到 Server/DiffSinger/voicebank/

# 2) 声码器 nsf_hifigan（音源不自带）
curl -L -o nsf_hifigan.oudep https://github.com/xunmengshe/OpenUtau/releases/download/0.0.0.0/nsf_hifigan.oudep
# .oudep 就是 zip，解压到 Server/DiffSinger/vocoder/

# 3) 独立 venv（避免与 RVC 的依赖固定冲突）
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

`requirements.txt` 把 onnxruntime 锁在 `1.23.2`，**不要**改成其他版本或 `onnxruntime-gpu`。

## 启用

```powershell
setx NEEEVA_JA_SVS_PYTHON "<项目>\Server\DiffSinger\.venv\Scripts\python.exe"
setx NEEEVA_JA_SVS_RUNNER "<项目>\Server\DiffSinger\neeeva_runner.py"
setx NEEEVA_JA_SVS_MODEL  "<项目>\Server\DiffSinger\voicebank\NamineRitsu_DiffSinger"
```

三者齐全且文件存在时，`svs_server.py` 的 `_japanese_backend_state()` 会返回
`backend: diffsinger-ja`、`experimental: false`，日语自动改走这里；否则维持现状
（降级到 9882 的角色歌声转换）。

## 技术要点

音源是 **OpenUtau 的 ONNX 格式**，不是 PyTorch 训练格式，所以只需 `onnxruntime`，
不必装整个 DiffSinger 仓库。各模型签名（已实测）：

| 模型 | 输入 | 输出 |
|---|---|---|
| `acoustic.onnx` | tokens[1,n] / durations[1,n] / f0[1,frames] / depth / speedup | mel[1,frames,128] |
| `nsf_hifigan.onnx` | mel[1,frames,128] / f0[1,frames] | waveform[1,samples] |
| `linguistic.onnx` | tokens / word_div / word_dur | encoder_out[1,n,256] / x_masks |
| `dsdur/dur.onnx` | encoder_out / x_masks / ph_midi | ph_dur_pred |
| `dspitch/pitch.onnx` | encoder_out / ph_dur / note_midi / note_dur / pitch / retake / speedup | pitch_pred |

runner **有意跳过 dur/pitch 两个 variance 模型**：项目的 `SingingScore` 已经带了真实
音符时值与逐帧 F0，直接用比让模型再预测一遍更贴合原唱。

- 帧率：`hop_size 512 / sample_rate 44100` → 11.61 ms，需把 score 的 10 ms F0 重采样过去
- 音素集：NNSVS/ENUNU 系日语音素（か→`k a`、し→`sh i`、ん→`N`、っ→`cl`、拗音→`ky a` 等）
  `dictionary.txt` 是恒等映射，假名→音素必须由 runner 自己完成（已实现并验证）

## 运行时版本（关键）

一开始 `acoustic.onnx` 推理会在**帧数超过约 200 时触发 access violation**（原生崩溃，
不是 Python 异常），期间还导致过两次系统级崩溃。根因是**运行时版本错配**，与模型和
硬件都无关：

| onnxruntime | 结果 |
|---|---|
| 1.18.1 | 创建 InferenceSession 即崩 |
| 1.22.0 | 同上 |
| 1.28.0 | 能加载，>200 帧崩 |
| **1.23.2（纯 CPU）** | ✅ 511 帧 3.1s |

1.23.x 正是 [OpenUtau 的 `OpenUtau.Core.csproj`](https://github.com/stakira/OpenUtau/blob/master/OpenUtau.Core/OpenUtau.Core.csproj) 锁定的版本
（Windows `Microsoft.ML.OnnxRuntime.DirectML 1.23.0` / 其他平台 `Microsoft.ML.OnnxRuntime 1.23.2`），
也就是这些社区音源实际被验证过的运行时。排查时应当**先对齐参考实现的版本**，而不是
把长序列崩溃当成模型限制去做分段——OpenUtau 本身就是整个 phrase 一次性渲染的。

另外**不要用 `onnxruntime-gpu`**：即使显式传 `providers=["CPUExecutionProvider"]`，
导入时仍会加载 CUDA/TensorRT 的驱动层库，没有必要。纯 CPU 包下
`get_available_providers()` 里不再出现 CUDA/TensorRT。CPU 速度已足够
（约 4s 出 6s 音频），本渲染器也不与占用显存的 LLM/TTS 抢资源。

## 已知问题：沙哑

RVC 转换后音色确实变成角色本人，但仍有偏沙哑的残留。已排查与已确认的部分：

**已修复的一项**：nsf-hifigan 输出电平极低（峰值约 0.17 / RMS 0.03，比 SoulX 输出低约
14dB），而 RVC 的 HuBERT 特征提取与 F0 跟踪在这种电平下会明显劣化。runner 现在做峰值
归一化（默认 0.9，`--target-peak` 可调，设 0 关闭），这一项带来了可听出的改善，但**没有
完全消除**沙哑。

**当前最佳参数**（送入 9882 之前）：

| 项 | 值 | 说明 |
|---|---|---|
| 源采样率 | 降到 32000 | 匹配角色 RVC 模型的训练采样率（DiffSinger 原生输出 44100） |
| `index_rate` | 0.75 | 角色索引权重。0 等于不用 `.index`，沙哑会明显加重 |
| `protect` | 0.38 | 越低对清辅音/气息保护越强（0.5=关闭） |
| `rms_mix_rate` | 0.31 | 与既有回唱路径保持一致 |
| `auto_f0_adjust` / `semitone_shift` | false / 0 | 润色分支不应改变歌曲调性 |

单变量对比（44.1k 原样 / 32k / 24k / protect 0.20 / index 0.90）实测**差异都不明显**，
说明沙哑主要来自 DiffSinger 输出本身的频谱特性，而不是 RVC 侧参数。

**未验证的下一步方向**（按优先级）：

1. **音域**：DiffSinger 输出中位 F0 约 329Hz，角色 RVC 模型中位约 288Hz，高约 2.3 个半音。
   在训练音域边缘运行是紧绷感的常见来源。可生成中位 ~294Hz 的乐谱做对照
   （`scratchpad` 里有现成的双音域样本生成思路：同一旋律 tonic 取 MIDI 62 vs 67）。
2. **源头而非补救**：调 runner 的 `--depth` / `--speedup`（现为 400 / 10，即 100 步），
   或改用音源自带的 `dspitch/pitch.onnx` 生成更自然的 F0 曲线，而不是直接用乐谱 F0。
3. 若仍无改善，考虑换一个日语音源——本项目选 波音リツ 是因为许可宽松，音质并非首要标准。

⚠️ **调优前先确认机器状态**：本机为 13th Gen i9-13900KF，系统日志中有 **47 次 WHEA
Id=19（Processor Core / Corrected Machine Check / Internal parity error）**，且最早的
记录（2026-06-19）远早于本项目的 ONNX 工作。排查期间发生过多次蓝屏，bugcheck 码高度
分散（0x50 / 0x20001 / 0x1e+0xC0000096 非法特权指令 / 0x1e+0xC0000005），而**没有任何
显卡驱动错误记录**——指向 CPU 层面的硬件不稳定，与 Intel 13/14 代高端型号的已知问题
表现一致。持续 AVX 满载的推理负载最容易触发它。继续做音质调优前，建议先更新含新微码的
BIOS 并取消一切超频。
