# DiffSinger 日语歌声渲染器（外部 SVS 后端）

给 `Server/SVS` 的语种路由用的**日语**渲染后端。配置好后，日语歌声不再走 SoulX 的
内置假名适配（那条路把假名映射到 SoulX 的**英语**音素表，听感是英语口音），而是用
原生日语音素集的 DiffSinger 音源生成，角色音色再由 Unity 侧的 svc-post-polish
（9882 的 RVC）负责。

> 状态：**未完成**。runner 已写好且假名映射验证通过，但 ONNX 推理存在一个未解决的
> 崩溃问题，见文末「已知问题」。

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
.\.venv\Scripts\python.exe -m pip install onnxruntime numpy soundfile pyyaml
```

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

## 已知问题（阻塞项）

`acoustic.onnx` 推理在**帧数超过约 200 时触发 Windows access violation**（原生崩溃，
非 Python 异常）。已确认：

- 与音素内容无关：单音素 `a` 拉长到 511 帧同样崩；20/50/100/150/200 帧均正常
- 与 onnxruntime 版本强相关：`1.18.1` 和 `1.22.0` 连**加载**都崩；`1.28.0` 能加载并在
  ≤200 帧下正常推理
- 200 帧只有约 2.3 秒，而 OpenUtau 能渲染整首歌，所以这**不像是模型的固有限制**，
  更像特定 onnxruntime 构建的问题

下一步可尝试：改用 CUDA/DirectML 执行提供器绕开 CPU 内核；或换用 OpenUtau 实际使用的
onnxruntime 版本；或分段推理后拼接（每段 ≤200 帧，在静音处切分）。

⚠️ 排查期间本机发生过一次蓝屏（`HYPERVISOR_ERROR 0x00020001`）。该机器的事件日志显示
在此之前也有多次异常关机记录，未必由本工作引起，但继续排查时建议避免长时间满载。
