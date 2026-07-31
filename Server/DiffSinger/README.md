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
| `protect` | 0.38 | **无效，见下**。保留默认值即可 |
| `rms_mix_rate` | 0.85 | 低于 ~0.6 时听感"有气无力"，见下 |
| `auto_f0_adjust` / `semitone_shift` | false / 0 | 润色分支不应改变歌曲调性 |

单变量对比（44.1k 原样 / 32k / 24k / protect 0.20 / index 0.90）实测**差异都不明显**，
说明沙哑主要来自 DiffSinger 输出本身的频谱特性，而不是 RVC 侧参数。

### 已解决：音量偏弱（"有气无力"）

`rms_mix_rate` 决定输出音量包络多大程度上采用角色模型自己的包络。原先 Unity 侧
`CreateHumPerformanceProfile` 把它随机在 **0.19–0.33**，输出因此跟随合成源那条几乎无
起伏的包络。实测阶梯（同一音源，仅变此参数）：

| rms_mix_rate | 0.31 | 0.60 | 0.85 | 1.00 | 角色参考音 |
|---|---|---|---|---|---|
| 有声 RMS (dBFS) | −15.5 | −14.7 | −13.9 | −13.5 | −14.9 |
| 波峰因数 | 3.40 | 3.38 | 3.36 | 3.38 | 5.53 |

0.85 起主观听感恢复正常。随机区间已改为 **0.80–0.92**，服务端与 runner 默认值同步抬到
0.85。注意波峰因数全程不变——RVC 会把源的动态（5.87）压到 3.4 左右，这个参数只调整体
响度，不恢复动态起伏。

### 已修复：实时跟唱的 mora/note 契约冲突

`neeeva_runner.py` 要求乐谱严格一 mora 一音符，但 Unity 实时跟唱送来的乐谱永远不满足：
音符来自对歌声的**声学切分**（音高稳定段，实测约每 0.19s 一个），mora 来自 ASR 转写的
**歌词文本**（约每 0.47s 一个），实测 22 mora vs 54 音符、31 mora vs 57 音符。二者比例
常达 1:2.5 且不可能自然相等，因此**日语实时跟唱在此之前从未成功过**——手写乐谱的离线
测试天然满足契约，所以一直没暴露。

`svs_server.py` 新增 `_align_score_to_mora()`：反复合并代价最小的相邻音符对（代价 =
半音差 + 间隔惩罚，优先合并音高接近且时间连续的段，即同一音节被误切开的情形），合并后
取时值较长那段的音高而非平均值，避免产生原曲没有的走音；音符不足时拆分最长音符。帧级
`f0_hz` 不动，它来自真实歌声，仍是最准确的旋律依据。

另加降级：DiffSinger 失败时若 SoulX 可用则改用 SoulX 唱同一份乐谱（不受一 mora 一音符
约束），宁可音质降级也不要整次演唱没有声音。

### 已修复：RVC 偶发 `FileNotFoundError: [WinError 2]`

`vendor/rvc/infer/audio.py` 在**模块导入时**二选一：CUDA 可用则用 torchaudio 解码，否则
回落到 ffmpeg。而 `rvc_convert.py` 每次转换都是新进程，这个选择每次重做——CUDA 初始化
偶发失败时就走 ffmpeg 分支，它以裸名 `"ffmpeg"` 调 `subprocess.Popen`，不在 PATH 上便
直接失败。**这是随机复现的故障**，正常情况下走 torchaudio 完全正常，所以很难发现。

修复：`rvc_convert.py` 启动时把 `vendor/rvc` 加入 PATH——该目录本就自带 `ffmpeg.exe`
（n4.3.2），无需额外下载。

### 两条已排除的死路（不要再调）

- **`protect` 对本链路完全无效**。参数确实传到了底层（`last_conversion/metadata.json`
  可验证），但 `vendor/rvc/infer/vc/pipeline.py` 中 `pitchff[pitchf < 1] = protect` 只作用
  于 rmvpe 判定为无声的帧。DiffSinger 渲染的音频里无声帧只有乐句间静音，本就没有声音，
  所以改这个参数的输出 RMS 一致到小数点后四位。
- **乐谱中的 `energy` 数组被音源忽略**。`voicebank/*/dsconfig.yaml` 里
  `use_energy_embed: false`、`use_breathiness_embed: false`，声学模型没有能量输入通道，
  给乐句写强弱曲线不会产生任何影响。

### 已修复：休止符 MIDI

休止符原先填 `note_midi = 0`，但 MIDI 0 ≈ 8.18Hz。本音源 `dspitch/dsconfig.yaml` 为
`use_note_rest: false`，模型没有"休止"概念，会把它当成真实极低音渲染，在每个休止处生成
荒谬的音高过渡并污染整条 F0 曲线（此前观察到的 6–16Hz 输出即来源于此）。现改为沿用相邻
实音的音高，使曲线在休止处保持连续。此问题只影响 `dspitch` 音高模型路径。

**未验证的下一步方向**（按优先级）：

1. **音域**：角色 RVC 模型中位约 288Hz。乐谱应让演唱中位落在 ~294Hz 附近；实测中位 392Hz
   的版本明显差于 294Hz 版本。`scratchpad/make_sample3.py` 给出了按乐句写旋律并对齐音域的
   做法（tonic 取 MIDI 60，中位 294Hz）。
2. **动态恢复**：RVC 把波峰因数从 5.87 压到 3.4。若仍觉得平，可在 RVC 之后做一次动态扩展，
   而不是继续调 RVC 入参。
3. **源头而非补救**：调 runner 的 `--depth` / `--speedup`（现为 400 / 10，即 100 步）。
   `dspitch/pitch.onnx` 路径（`--no-pitch-model` 关闭）在修复休止符后仍需重新评估：主观
   对比中乐谱 F0 一路始终更好听。
4. 若仍无改善，考虑换一个日语音源——本项目选 波音リツ 是因为许可宽松，音质并非首要标准。

⚠️ **调优前先确认机器状态**：本机为 13th Gen i9-13900KF，系统日志中有 **47 次 WHEA
Id=19（Processor Core / Corrected Machine Check / Internal parity error）**，且最早的
记录（2026-06-19）远早于本项目的 ONNX 工作。排查期间发生过多次蓝屏，bugcheck 码高度
分散（0x50 / 0x20001 / 0x1e+0xC0000096 非法特权指令 / 0x1e+0xC0000005），而**没有任何
显卡驱动错误记录**——指向 CPU 层面的硬件不稳定，与 Intel 13/14 代高端型号的已知问题
表现一致。持续 AVX 满载的推理负载最容易触发它。继续做音质调优前，建议先更新含新微码的
BIOS 并取消一切超频。
