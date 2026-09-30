# Spark 9882 角色 RVC 部署

2026-09-26 已将项目现有角色 RVC 部署到 `192.168.114.163:9882`，并完成真实 HTTP 音频、CUDA 参数、中断与后继请求验证。本次没有恢复、安装或迁移 9883 SoulX-Singer；也没有部署缺失的 Seed-VC 回退模型。

| 配置 | 已部署值 |
| --- | --- |
| 服务 / 容器 | `http://192.168.114.163:9882` / `neeeva-spark-vc` |
| 目录 | `/home/spark_root_001/neeeva-voice/vc` |
| 参考音频 | `/home/spark_root_001/neeeva-voice/vc/release/refs/41041.wav` |
| 模型 | 现有 epoch-75 角色 RVC v2 f0 32 kHz，原 HuBERT / RMVPE / index |
| 实际模型参数 | RVC、HuBERT、RMVPE 均为 `cuda:0 / torch.float16` |
| 环境 | ARM64 / GB10，PyTorch `2.11.0+cu130`；专用 `/opt/neeeva-vc-venv` |
| 线程 | OMP / MKL 各 1 |
| worker 空闲回收 | **90 秒**，与本机默认一致；没有应用拟议的 3600 秒配置 |
| Faiss | `faiss-cpu==1.10.0`，服务专属 `FAISS_OPT_LEVEL=generic` |

`manifest.json` 固定 73 个源码、角色权重、索引、HuBERT、RMVPE 及参考文件，共 552,938,467 字节。发布 SHA-256 为 `a27dc2a78d608a0ff66a4dd0e727bd1140add8f12e92316b8c7494d7000329c9`，远端逐文件验证已通过。没有更新上游 vendor 或重新训练角色。

`deployment-lock.json` 记录基础镜像、实际测试镜像、支持脚本及依赖快照的身份。`requirements-lock.txt` 是已通过测试的专用 venv 的 `pip freeze --local`，继承部分由基础镜像 ID 固定。当前 Dockerfile 使用顶层依赖版本和缓存构建；这份本地包快照用于审计，不宣称未来重新解析全部传递依赖必然产生相同镜像。复用已验证镜像身份是当前严格重现的方法。

## 平台适配与边界

新环境在独立镜像及容器内创建，没有混装到 TTS、ASR、ARDY 或 vLLM 环境。镜像复用已有 CUDA / PyTorch 不可变基础层，`praat-parselmouth` 已在 ARM 内编译成功，Faiss 使用 ARM wheel。基础镜像中未使用的 vLLM 等包与 TTS/VC 所需版本有冲突提示；它们不在本容器运行，不能据本次验收宣称整个基础镜像的所有应用都兼容。

Faiss 1.10 的 ARM 指令集自动探测在 Python 3.12 下导入了不存在的 `numpy.distutils`，首次实际预热因此失败。专用服务 wrapper 指定 Faiss 支持的 generic 实现绕过这段探测，检索索引文件和检索比例不变。模型推理仍使用 CUDA。

Spark 的 `nvidia-smi memory.free` 在此部署返回不可用，因此健康检查中的 `will_use` 只是冷启动预测。验收使用 worker 回报的三组实际模型参数，不能只检查 `cuda_available` 或 `will_use`。

完整归档保留在本机 `Server/VC/runtime/package/release.tar`。首次上传后，对审查修正的少量源码及 manifest 做过增量同步；远端初次上传的旧 `release.tar` 不是最终完整发布备份，应使用本机最终归档或逐文件验证后的 `release/`。

## 控制与回滚

项目根目录下运行：

```powershell
./Tools/VC/manage-spark-vc.ps1 -Action status
./Tools/VC/manage-spark-vc.ps1 -Action start
./Tools/VC/manage-spark-vc.ps1 -Action logs
./Tools/VC/manage-spark-vc.ps1 -Action stop
```

`start` 幂等复用已有容器，并在必要时调用 `/warmup`，等待真实 RVC / HuBERT / RMVPE 参数全部在 CUDA 上。预热会执行模型工作，不应与性能测量同时调用。`stop` 仅停止名称和所有权标签匹配的 VC 容器，不停止其他服务。容器使用 `unless-stopped`；90 秒无转换后 worker 会释放，下次请求会重新加载。

首次准备新部署时，先运行 `package_local.py`，再执行 `Tools/VC/deploy-spark-vc.ps1`。支持 `-TransferOnly`、`-SkipStart` 和仅安装期间的 `-BuildProxy`。不同发布或无本工具所有权的既有目录会被拒绝覆盖。构建镜像不自动替换已有容器，不能把该脚本当成无条件升级入口。生产运行不依赖安装代理。

路由由父级迁移工具管理。Unity 关闭后，`Tools/VC/set-vc-route.ps1 -Profile local` 恢复本机 URL、参考文件和自动启动；`-Profile spark` 要求远端真实模型就绪。再运行现有 `start_voice.cmd` 启动选定服务。回退不要求删除远端模型；不再使用时可单独停止远端 VC。本机 9882 在本次对照时没有被本部署重启。

## 请求与取消契约

- `GET /health`：服务、后端、忙碌、worker 状态、实际模型参数、发布 hash 和参考绝对路径。
- `POST /warmup`：加载并预热角色 worker，不返回可播放音频。
- `POST /convert`：multipart `source_audio` 上传源 WAV，`target_path` 指向服务器上的参考文件，`request_id` 标识请求；其余音高、音色参数保持原协议。返回完整 WAV，必须检查 `X-SVC-Complete: true` 和实际后端。
- `POST /cancel/{request_id}`：取消已登记的匹配请求。错误 ID 返回 `cancelled=false`，不会杀掉当前工作。

已修复持久 worker 原先未登记活动请求、取消后可能退回一次性重试的问题。请求在等待转换锁之前注册取消状态；排队取消只撤销该请求，不杀当前请求。重复活动 ID 返回 409，待处理登记上限为 8，并在结束时清理。取消返回 499，不发布旧音频、不启动回退重试；持有该 worker 的线程负责回收子进程。尚未绑定的取消不会杀掉无关的健康预热 worker。旧一次性 RVC / Seed-VC 子进程仍使用同一取消契约。

`Server/SeedVC/test_request_cancellation.py` 与现有测试合计 **24 项通过**，不调用模型、GPU、服务或真实子进程。原故障复现保留在 `runtime/cancel-before.json`，修复审计在 `runtime/cancel-regression.json`，并经过独立代理对请求所有权和取消边界的复核。

## 实际音频结果

输入是项目已有本地测试录唱的 `last_conversion/source.wav` 副本，保存为 `runtime/source.wav`：16 kHz、单声道、12.19 秒、RMS 0.0353。它不是公开合成语料，也不是本次生成的纯正弦或静音。仅在用户本机与用户 Spark 之间发送，没有向外部服务提交录音。

同源、seed 1234、index rate 0.75、自动全曲音高匹配，其他参数使用原接口默认值。以下耗时包括 HTTP 完整转换和接收，不是首音、Unity 播放延迟或完整 LLM 对话延迟。

| 请求 | 输出长度 | 本机 CUDA | Spark CUDA |
| --- | ---: | ---: | ---: |
| 前 4 秒录唱 | 3.98 s | 1.180 s | 0.379 s |
| 完整 12.19 秒录唱 | 12.18 s | 1.554 s | 0.536 s |
| 主动取消后重新加载 worker 的 4 秒后继 | 3.98 s | 未测试本机已知旧取消路径 | 3.518 s |

两个普通请求都为 `rvc-character-v2`、实际 CUDA、无 CPU 回退；WAV 完整、有限且非静音。Spark 上长请求开始后，错 ID 取消返回 false，正确 ID 返回 true，旧请求在 0.428 秒返回 499，随后新请求成功。后继耗时包含模型重新加载，不能与前两项暖机耗时混为一谈。

原始证据和输出 WAV 在 `runtime/local/report.json`、`runtime/spark/report.json` 及同目录音频。本次是少量服务层样本，未进行物理听音或长时间压测；也未证明歌词清晰度、歌唱艺术质量或角色自主生成新歌。

父级随后运行的 Unity 运输层验收也已通过 6 个阶段，记录在 `Logs/vc/unity-transport-spark.json`。该测试使用另一份 Haruka 合成语音，真实转换耗时 0.760 秒，解码音频为 14.86 秒；转换取消被服务确认，取消后的后继耗时 3.780 秒（含冷加载），静音 AudioSource 的播放、完成及取消无旧音频复播，场景与质量设置 hash 未改变。它调用真实 Unity 下载/解码/播放器，但没有经过 ChatSample 完整歌唱授权流程、LLM 或麦克风，不能当作自主歌唱验收。

安装结束后，本次专属 SSH 反向隧道 session 57546 已关闭，远端 18880 监听消失；TTS / ASR / VC 生产服务不依赖该安装代理。

## 未执行的配置尝试

计划将 Spark 专属 worker 空闲回收时间延长为 3600 秒时，一条“上传两个支持脚本 → 停止自身 VC → 启动自身 VC → 烟测”的组合执行被自动审批拒绝，工具只给出 `rejected: blocked by policy`，没有更具体理由。该组合没有执行，也没有重试这些更新/重启动作。随后获准对已经健康的服务单独执行烟测，结果如上。未部署的 3600 秒本地改动已撤回，支持脚本 hash 与远端一致，交付配置仍为 90 秒。
