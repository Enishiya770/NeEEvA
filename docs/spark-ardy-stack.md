# Spark LLM 与 native ARDY

当前 Unity 聊天使用 Spark 的 OpenAI-compatible Qwen：

```text
聊天： http://192.168.114.175:8888/v1/chat/completions
模型： qwen3.6-35b-a3b
动作： http://192.168.114.163:8093
特征： Spark 上已有的 http://127.0.0.1:8890/neeeva/motion-features
```

`192.168.114.163:8890` 返回的是 ARDY 所需的 2048 维原始 Qwen 特征，当前响应明确标记 `shared_model:false`。这表示动作特征模型独立运行；它仍必须匹配固定的 feature contract、Qwen SHA256、模板、维度和 raw-last pooling。启动 native ARDY 时，一键脚本显式传入 `--allow-independent-feature-model`，不会把普通 embedding 或旧 Gradio 文件输出当作动作特征。

旧的 `192.168.114.163:9550` 只有 Gradio `DemoWrapper` 文件下载接口，不能给 Unity 的 `/v1/motion/generate` 使用。

2026-09-27 的 LLM 调优已接回原 `qwen36-vllm.service` / `qwen36-35b-nvfp4`：24 GiB 显式 KV pool、2048 预填充预算、32-token 细粒度前缀匹配，保留 MTP 3-token 和 262144 上下文上限。固定原镜像与补丁，当前进程分配约 48.3 GiB。配置对照、验证范围与原容器回退命令见 [当前意图与缓存调优报告](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/docs/intent-cache-optimization-20260927.md)。

## 一键命令

在项目根目录执行：

```powershell
.\start_spark_ardy.cmd
status_spark_ardy.cmd
stop_spark_ardy.cmd
```

三个 `.cmd` 都会在执行结束后暂停窗口，适合直接双击运行；按任意键才会关闭窗口。启动成功后关闭本地窗口不会停止 Spark 上的远程服务。

`start_spark_ardy.cmd` 会：

1. 检查 Spark LLM 的 `/v1/models`；已有 LLM 进程直接复用。
2. 检查并在需要时启动 `.175` 上的 `qwen36-vllm.service`，聊天入口仍是 `192.168.114.175:8888`。
3. 将 native ARDY 源码和启动入口上传到 `.163:/home/spark_root_001/ardy-qwen36`，复用现有 vendor、Python/CUDA 环境和特征服务。
4. 在 `.163` 上绑定 `0.0.0.0:8093`，并把特征 URL 指向 Spark 本机 `127.0.0.1:8890`。
5. 等待 `GET http://192.168.114.163:8093/health` 返回完整 native ARDY 健康合同。
6. 将本次启动状态写入 `Server/RuntimeLogs/spark_ardy_stack.state.json`，停止时只按这个状态和远端 ARDY 命令行校验后的 PID 停止；一键停止入口还会明确停止 `.175` 上的 `qwen36-vllm.service`。

`.163` 上的 ARDY Core 权重和 adapter checkpoint 需要先准备一次。当前项目提供的准备流程会从 `.163:8890` 导出 2048 维 Qwen 特征，并在 Spark 上训练与当前特征合同匹配的 MLP adapter：

```powershell
# 只需在缺少 runtime/qwen-2000.npz 或 runtime/mlp-seed1.pt 时执行
scp Tools\prepare_spark_ardy_features.py spark_root_001@192.168.114.163:/home/spark_root_001/ardy-qwen36/
scp Tools\MotionAdapter\__init__.py Tools\MotionAdapter\adapter.py Tools\MotionAdapter\data.py Tools\MotionAdapter\train.py spark_root_001@192.168.114.163:/home/spark_root_001/ardy-qwen36/Tools/MotionAdapter/
ssh spark_root_001@192.168.114.163 "cd /home/spark_root_001/ardy-qwen36 && /home/spark_root_001/ardy-qwen36/venv/bin/python prepare_spark_ardy_features.py --prompts runtime/prompts.jsonl --output runtime/qwen-2000.npz --feature-url http://127.0.0.1:8890"
ssh spark_root_001@192.168.114.163 "cd /home/spark_root_001/ardy-qwen36 && /home/spark_root_001/ardy-qwen36/venv/bin/python -m Tools.MotionAdapter.train --dataset runtime/prompts.jsonl --qwen runtime/qwen-2000.npz --teacher runtime/teacher-2000.npz --architecture mlp --seed 1 --epochs 100 --output runtime/mlp-seed1.pt"
```

Core40 权重放在脚本状态中的 `remoteCoreDir`（默认 `/home/spark_root_001/ardy-qwen36/checkpoints/ARDY-Core-RP-20FPS-Horizon40`）。缺少任一项时，启动会失败并把详细错误留在 Spark 的：

```text
/home/spark_root_001/ardy-qwen36/.runtime/ardy.err.log
```

### `tar` 时间戳警告导致启动失败

Windows 与 Spark 时钟有少量偏差时，解压上传包可能出现 `time stamp ... is ... s in the future` 警告。旧启动器在 Windows PowerShell 5.1 中使用 `2>&1` 配合 `ErrorActionPreference=Stop`，会把原生命令的 stderr 警告升级为异常，即使命令退出码为 0，也会在部署阶段报 `[stack] FAILED`。

启动器现在统一收集 SSH、SCP 和本机打包命令的输出，按实际退出码判断成功或失败；部署警告保留在窗口中，远端 `tar` 使用英文诊断以避免中文编码乱码。远端解压、权限设置和链接操作的真实失败会立即中止，不再被最后的清理命令掩盖。两台机器的时间仍建议保持同步，但小幅时钟偏差不会再单独阻止启动。

更新后正常运行 `start_spark_ardy.cmd` 即可。若只是重启已经部署过的远端版本，可临时执行 `start_spark_ardy.cmd -SkipDeploy`；该选项不会上传本机源码更新。回归脚本覆盖 stderr 警告、非零退出、未来时间戳解压和损坏压缩包，并仅在隔离的 `/tmp` 目录中测试。

### 显示 `reused`，但 ARDY 一直未就绪

2026-09-22 确认过一次 Spark 重启后的残留 PID 问题：`.runtime/ardy.pid` 还在，但对应进程已经不存在，8093 也没有监听。旧启动逻辑只在 PID 存活时校验命令行，没有清除已退出进程的记录，因此误报 `reused`，等待 180 秒后失败。

启动器现在会检查 PID 内容、进程存活状态和完整端口参数；失效、空白或不匹配的记录只清除 PID 文件，再启动 ARDY。不会因为残留 PID 指向其他进程而结束那个进程。正常运行的 ARDY 仍会复用。启动后若进程提前退出，会立即带出远端 stderr；健康等待超时也会显示最近错误日志，并把本机状态标记为 `failed`。SSH 命令使用 UTF-8 编码传递，避免 Windows PowerShell 5.1 损坏 Bash 的引号。

遇到这种情况，更新脚本后重新运行 `start_spark_ardy.cmd` 即可。本机缺少 `mlp-seed1.pt` 的上传警告不代表远端也缺少权重；包装脚本使用的是 Spark 上的 `runtime/mlp-seed1.pt`。

启动控制器回归可运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File Tools\test_spark_ardy_stack.ps1`。它通过 SSH 在唯一的 `/tmp` 目录中使用模拟进程验证 PID 恢复、正常复用、端口匹配和错误日志，不加载模型或启停正式服务。

如果 LLM 没有自动运行，可以显式提供已由你确认过的远程启动命令，例如：

```powershell
.\Tools\neeeva_spark_ardy_stack.ps1 start `
  -RemoteLlmStartCommand 'cd /home/spark_root_001/llm && ./start-qwen.sh'
```

脚本不会猜测或 `pkill` 一个未知的 LLM 进程。`stop_spark_ardy.cmd` 会停止本次记录的 native ARDY，并停止 `.175` 上已知的 `qwen36-vllm.service`，因此它是关闭本套 LLM+ARDY 的一键入口。它不会停止 `.163` 上的 8890 特征服务；该服务是独立的动作特征 worker。直接调用 PowerShell 的 `-Action stop` 时，只有加上 `-StopLlm` 才会停止 LLM。

包装脚本默认传入 `-AllowUnverifiedAdapter`，因为 Spark 上训练出的 adapter 是针对当前独立 Qwen 特征 worker 的部署 artifact，健康响应会明确标记 `adapterMode=external-unverified`；它不冒充 Windows 项目中缺失的历史 `mlp-seed1.pt` 验收文件。

## Unity 配置

当前场景 [NeEEvA_SailingMoon_Day_Chat.unity](../Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity) 已设置：

```yaml
m_Backend: 2
m_OpenAICompatibleUrl: http://192.168.114.175:8888/v1/chat/completions
m_OpenAICompatibleModelName: qwen3.6-35b-a3b
m_ArdyServiceUrl: http://192.168.114.163:8093
```

`ChatQW.Awake()` 会把这个地址同步到场景中的 `ArdyDialogueMotionBridge` 和 `ArdyRoomDialogueBridge`。进入 Play 前先确认 `status_spark_ardy.cmd` 显示 LLM 与 ARDY 都是 `ready=True`。首次真实动作请求还会验证 Spark 特征响应；验证失败时 Unity 不会把错误数据播放成动作。

如果 ARDY 部署到了另一台 Spark，只需把启动命令的 `-Spark`、`-ArdyHost` 和场景 `m_ArdyServiceUrl` 一起改成同一台机器；不要把 `:8888`、`:8890` 或 `:9550` 填到 `m_ArdyServiceUrl`。

## 服务 ready，但 Unity 返回 502 或没有语音转写

启动窗口的 `LLM ready` 只说明 `/v1/models` 返回了预期模型名，并未执行聊天推理；`ARDY ready` 检查的是动作服务 `/health`。这两项都不覆盖本机 ASR/VAD 服务、麦克风采集或 Unity 自身的 HTTP 路径。实时转写需要另外检查本机语音服务，不能由两台 Spark 的状态推断。

2026-09-21 的本机对照检查发现，同一个 `http://192.168.114.175:8888/v1/models` 直连返回 `200`（`Server: uvicorn`），强制经过本机 `http://127.0.0.1:7897` 代理则约 5 秒后返回 `502`、`Content-Length: 0`，与 Unity 截图表现吻合。因此应优先检查 Unity 的代理路径，而不能把这类错误直接归因于 Spark 未启动、提示词过长或显存不足。该对照定位的是代理路径故障；是否修复当前 Unity 进程，仍以场景内实际请求结果为准。现有 SSE 日志本身也不会保留一般 HTTP 错误正文，单看日志中的“响应体为空”不能证明服务器真的没有错误详情。

隔离 Unity 2022.3.22f1 的真实 `UnityWebRequest` 对照进一步确认：默认环境访问两台 Spark 均为 502，本机 ASR 为 200；普通 `NO_PROXY` 和 `UNITY_NOPROXY` 地址名单仍无法修复。最终有效设置是在新进程的环境块中明确保留 `UNITY_PROXYSERVER` 空字符串：LLM、ARDY、ASR 分别返回 200，约耗时 60、42、20 毫秒。不能用 Windows PowerShell 5.1 的 `$env:UNITY_PROXYSERVER = ''` 替代，因为它会删除变量。

项目启动器通过 `ProcessStartInfo` 传入这个设置，只让新 Unity 进程的原生网络请求直接联网，包括该进程的外网请求；系统代理、其他应用、场景地址和已有 Unity 进程均不修改。

最终启动器已用 Windows PowerShell 5.1 实测：复用脚本生成的进程环境启动隔离 Unity，三个健康接口均返回 200；最小流式聊天 POST 也返回 200，并收到 SSE 数据与 `[DONE]`。

1. 在 Unity 中保存当前工作，关闭这个项目的 Editor。
2. 双击项目根目录的 `start_unity_direct_services.cmd`，再进入场景测试。
3. 如果仍无转写，检查本机 ASR/VAD 是否运行及麦克风输入；如果仍报 502，记录 Unity 启动时的实际聊天 URL 与本次 HTTP 响应信息。

启动器按 `ProjectSettings/ProjectVersion.txt` 查找匹配的 Editor，依次检查注册表、常见安装目录和运行中的 Editor 路径。也可以显式指定：

```powershell
.\Tools\start_unity_direct_services.ps1 -UnityPath 'E:\Unity_Data\2022.3.22f1\Editor\Unity.exe'

# 只显示将使用的 Editor 和子进程直连配置，不打开 GUI 或修改当前环境
.\Tools\start_unity_direct_services.ps1 -CheckOnly
```

若同一个项目已经打开，启动器会要求先保存并关闭，不会强制结束进程。环境变量只传给新进程，已运行的 Unity 不会因为另开启动脚本而自动更新。Unity 官方工程师曾说明 Windows UnityWebRequest 的 [`UNITY_PROXYSERVER` 覆盖入口](https://discussions.unity.com/t/unitywebrequest-proxy-environment-variables-overridden-by-system-proxy-settings/690979)；本修复以本机实际版本的隔离请求结果为依据，而非仅据通用 Editor 代理文档推断。

### 桌面实时模式没有转写，但手动录音能识别

本次同时确认本机 `http://127.0.0.1:9881/health` 的 ASR、VAD 和流式预览均正常，Unity 日志中的手动录音也成功识别了“你好。”。原来的实时录音在设备名留空时无条件优先选择 PICO 输入，而 Windows 上仍注册着多个 PICO 虚拟麦克风，因此桌面模式和手动录音使用了不同输入。

`RTSpeechHandler` 的麦克风名称留空时，桌面和 XR 都使用 Unity 的系统默认输入（`null`）；Inspector 中显式指定的麦克风仍优先。设备选择会记录为 `[RTSpeech/Microphone]`；正在采集的一轮不会中途切换设备。停止实时录音只释放自身持有的输入，避免之后反复停止同样使用默认设备的手动录音。

2026-09-22 的 VR 实测进一步确认：可用头显输入显示为 `Capture Input terminal` / `AC Interface`，名称不含 PICO；同时 Windows 仍列出多个 PICO 虚拟输入。原先 XR 自动取第一个含 `pico` 的设备，实际选中了没有有效人声的 `PicoStreamingMicrophone`，导致实时模式与共享同一输入的 VR 按住说话均失败。现在应在进入 Play Mode 前把实测可用输入设为 Windows 默认，保持 `M Microphone Name` 留空；无需按品牌名猜测或硬编码这台机器的设备名称。

完整生产代码编译通过，隔离 Unity 的 `RealtimeMicrophoneRegression.RunBatch` 通过 24 项设备选择、VR 按住说话和释放检查，包含多个 PICO 端点与不含 PICO 名称的 AC Interface 输入、枚举顺序变化、显式设备选择及模式切换。该回归使用模拟麦克风边界；用户实际说话和场景内字幕仍需在重新进入 Play Mode 后验收。本次报告：`work/vr-mic-fix-20260922/realtime-microphone-regression.json`。

### 本机 ASR 使用已验收的 CUDA 环境

`Tools/neeeva_services.ps1` 不会自动发现新建的虚拟环境。只有完成 CUDA 与 ASR 实测后，才启用 `Server/RuntimeLogs/asr_runtime.json`（已 gitignore）：

```json
{
  "python": "E:/项目目录/Server/SenseVoice/runtime/.venv-cuda/Scripts/python.exe",
  "device": "cuda:0",
  "requireCuda": true,
  "extraArgs": []
}
```

`python` 填已验收解释器的实际路径。`NEEEVA_ASR_PYTHON` 若已设置则优先覆盖解释器，JSON 中的设备和 CUDA 要求仍生效；`extraArgs` 只接受字符串数组，不允许覆盖 `device`、`host`、`port` 或 `requireCuda`。CUDA 预检会执行一个 GPU 张量运算，失败或解释器不存在时明确报错，且在 `restart` 停止旧进程之前检查。`requireCuda: true` 还会传入 `--require-cuda`，由服务在模型加载后再次确认实际设备；本次启动的健康检查要求 `asr_device` 为 CUDA 且 `require_cuda` 为 true。新 ASR 使用 `-u` 输出即时日志，选中的解释器与设备记录到 `Server/RuntimeLogs/asr.launch.log`。

启用或撤销配置后，用 `.\Tools\neeeva_services.ps1 restart -Only asr` 才会影响正在运行的 ASR。回退时仅撤销本次添加的 `asr_runtime.json`（如果原来已有配置，恢复其备份），以及本次设置的 `NEEEVA_ASR_PYTHON`；不要删除或覆盖已有用户配置。没有 active 配置与显式覆盖时，继续使用原来的 PATH `python` 和服务默认设备行为；在原 CPU-only PyTorch 环境下仍按原逻辑回退 CPU。
