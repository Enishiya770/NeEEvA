# Spark TTS：固定本机模型的独立部署

2026-09-26 已在 `192.168.114.163` 的独立容器中完成 GPT-SoVITS CUDA FP32 部署，并通过真实 HTTP 合成、PCM 校验和中断后的连续请求测试。本目录记录部署与服务层验证；Unity 主场景及完整对话延迟由项目的主场景测试报告另外记录。

| 项目 | 当前部署 |
| --- | --- |
| 合成接口 | `http://192.168.114.163:9880/tts`，支持原协议 GET / POST |
| 健康接口 | `http://192.168.114.163:9880/health` |
| 远端目录 | `/home/spark_root_001/neeeva-voice/tts` |
| 容器 / 镜像标签 | `neeeva-spark-tts` / `neeeva-spark-tts:local-frozen-v1` |
| 参考音频 | `/home/spark_root_001/neeeva-voice/tts/release/refs/41041.wav` |
| 模型运行状态 | GPT-SoVITS v2；两个模型实际参数均在 `cuda:0`、`torch.float32` |
| 运行环境 | Linux ARM64，GB10，PyTorch `2.11.0+cu130`，CUDA 13.0 |
| CPU 线程 | `OMP_NUM_THREADS=1`、`MKL_NUM_THREADS=1`，健康检查实测为 1 |

## 来源与隔离

源代码来自本机实际安装 `E:/so-vits/GPT-SoVITS-v2pro-20250604`，没有拉取或升级上游分支。固定了实际 v2 GPT / SoVITS 权重、BERT / HuBERT、语言识别资源、NLTK 数据、日本语字典及项目的 `Assets/Model/41041.wav`。源目录名含 v2pro，但实际配置与所加载权重是 v2。

`manifest.json` 对 337 个文件逐一记录大小及 SHA-256，合计 2,113,597,471 字节；部署时已经在 Spark 验证全部文件。发布标识是 `05e901197489fc462dfbe1624914034fa00d34a10bf47897ec20cf994c30a8e1`。完整的本机归档是 `Server/TTS/runtime/package/release.tar`，大小 2,114,273,280 字节。首次上传后另补了固定的语言识别资源，因此远端首次上传的旧 `release.tar` 不应当作为完整备份；以已验证的 `release/` 和本机完整归档为准。

CUDA / PyTorch 复用 Spark 已缓存镜像的不可变层，未修改 ARDY、vLLM 或宿主机的 Python 环境。基础镜像身份和测试镜像身份记录在 `deployment-lock.json`，`manage.sh build` 会核对基础镜像 ID。容器中的独立 venv 继承该基础镜像的系统包，并覆盖 TTS 所需依赖；没有在此容器运行 vLLM。安装过程中存在与基础镜像中未使用的 vLLM 依赖之间的版本冲突提示，不能将本次结果描述为所有基础镜像包的兼容性验收。

`requirements-lock.txt` 是已通过测试的 venv 本地包版本快照。NumPy 固定为 1.26.4；NLTK 固定为原本机使用的 3.8.1，以匹配已打包的英文词性标注资源。初次英语测试暴露的新版 NLTK 资源不兼容已经修复。源码、角色权重和参考音色未因此改变。现有 Dockerfile 已引用该快照约束后续安装；增加约束文件后的重新构建未另行运行，当前在线服务仍是记录中的已测试镜像。

容器以 `unless-stopped` 自动恢复，只挂载自己的 TTS 目录；`release/` 额外只读挂载。上游初始化时写配置的行为被引导到 `runtime/tts_infer.yaml`。生产运行不依赖互联网或安装代理。没有停止或替换 Spark 上的其他服务。

## 管理与部署

在项目根目录运行以下入口。`start` 对已存在的本服务容器执行幂等启动，并等待实际 CUDA FP32 与发布标识匹配；不重装、不强制重启健康实例。

```powershell
./Tools/TTS/manage-spark-tts.ps1 -Action status
./Tools/TTS/manage-spark-tts.ps1 -Action start
./Tools/TTS/manage-spark-tts.ps1 -Action logs
./Tools/TTS/manage-spark-tts.ps1 -Action stop
```

`stop` 只操作名称和所有权标签均匹配的 TTS 容器。端口 9880 被其他进程占用时，创建入口会拒绝操作，不会停止端口拥有者。远程 SSH 地址、密钥文件和部署根路径可以通过脚本参数指定；文件中不存储私钥内容。

首次部署 / 在相同发布上重新准备文件使用：

```powershell
./Tools/TTS/deploy-spark-tts.ps1
# 已完成本机打包时：
./Tools/TTS/deploy-spark-tts.ps1 -ReusePackage
```

脚本拒绝覆盖没有本工具所有权标记的目录，也拒绝直接替换不同发布的现有 `release/`。构建新镜像不会自动替换已有容器，因此该入口不应被当成自动升级命令；更新已有服务需要单独审查并安排该服务的切换窗口。

本次安装因 Spark 外网 DNS / 路由不可用，使用了仅限安装进程的临时反向代理 `http://127.0.0.1:18880`。可在明确已建立该隧道后向部署脚本传 `-BuildProxy http://127.0.0.1:18880`。没有改宿主机 DNS、全局代理或生产容器的代理环境。下载缓存保留在 `/home/spark_root_001/neeeva-voice/tts/wheel-cache`，可供后续独立环境只读复用；不应共享可变 venv。临时隧道在 ASR 安装交接完成后可关闭。

## 流式协议与请求所有权

`serve.py` 保留原 `/tts` 请求字段、默认参数和流式 WAV / PCM 输出，调用固定的上游合成实现。没有增加更换权重的 HTTP 入口。单进程、单模型所有权锁覆盖排队后的执行、流式输出及生成器清理；旧客户端断开后，必须等待正在执行的模型工作和生成器关闭完成，才允许后继请求使用共享模型。排队上限为 120 秒，超时返回 503。

`/health` 的 `ready` 表示模型已加载，包含实际设备、精度、线程数、权重哈希和发布摘要；不表示每种语言都已经完成首次预热。`busy` 表示当前模型所有权被占用。三个累计计数的含义需要特别区分：

- `disconnected`：服务收到 ASGI 客户端断开事件；正常响应结束后的连接关闭也可能计入。它不是“用户取消次数”。
- `completed`：响应处理生命周期正常返回；断连导致流结束也可能正常返回。它不是“完整音频合成成功次数”。
- `failed`：流式响应生命周期抛出的异常次数；不覆盖所有输入错误、排队超时或上游以错误响应返回的情况。

因此不能用 `completed == disconnected` 推断所有请求被取消，也不能只用 `failed == 0` 推断每次合成都成功。取消验证依靠主动中止请求、后继音频结果与锁恢复的独立证据。

## 已完成的服务层验证

`runtime/smoke.json` 保留真实网络 / GPU 合成的原始结果。有效音频检查包含 RIFF / WAVE、32 kHz、单声道 PCM16、非零且有限的 RMS；音频保存在同目录。以下时间是客户端收到首段 PCM 的时间，不是扬声器首音、Unity 首音，也不包含 LLM 判定或生成。

| 样本 | 首 PCM（秒） | 请求耗时（秒） | 音频长度（秒） | RTF |
| --- | ---: | ---: | ---: | ---: |
| 冷启动首个日语请求 | 4.860 | 5.211 | 1.499 | 3.475 |
| 首个中文请求 | 1.147 | 1.604 | 2.552 | 0.629 |
| 首个英文请求 | 1.313 | 1.647 | 1.652 | 0.997 |
| 已预热的日语请求 | 0.087 | 0.608 | 3.077 | 0.198 |
| 中止后的中文后继 | 0.356 | 0.911 | 3.236 | 0.281 |
| 同时提交、排队的日语后继 | 1.003 | 1.403 | 2.211 | 0.635 |

另一个长中文请求在首 PCM 到达后主动关闭连接，随后并发提交的两个请求均产生有效完整音频，最终 `busy=false`。这些结果证明了此次具体中止 / 排队场景的恢复，不能替代长时间并发稳定性或听感质量评测。独立的无模型回归还覆盖了取锁后断连检查抛普通异常、抛取消异常及直接返回断连三种路径，均只释放一次所有权，证据在 `work/spark-voice-migration-20260926/tts-owner-release-review.json`。

主场景对照期间不要同时执行此烟测、重建镜像或向相同 GPU 添加新部署负载。服务层暖机短句结果不能外推为完整互动延迟。

## 回滚

本机 GPT-SoVITS 安装、模型和本机 TTS 配置未被本部署修改。关闭此项目的 Unity 编辑器后，可通过项目路由工具恢复本机地址和本机参考文件，然后使用现有启动器启动本机服务：

```powershell
./Tools/TTS/set-tts-route.ps1 -Profile local
# 再运行项目现有的 start_voice.cmd。
# 确认不再需要远端 TTS 后，仅停止本部署：
./Tools/TTS/manage-spark-tts.ps1 -Action stop
```

恢复远端路由使用 `set-tts-route.ps1 -Profile spark`，其前提是远端健康检查通过。路由工具同时维护主场景和 `Server/RuntimeLogs/voice_routes.json`，并备份修改前的文件。TTS 的远端参考路径与本机 9882 变声服务的参考文件是不同服务的输入，不能将 9882 的本机文件路径改成这个 Linux 路径。
