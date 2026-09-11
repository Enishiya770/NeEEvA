# Qwen → adapter → ARDY 试验与 Unity 接入

实时对话动作入口现已实现：**Tools → NeEEvA → ARDY Dialogue Motion**。使用同一个 Qwen server 的独立短特征 context，加本机 adapter/ARDY；新窗口从角色上身历史开始，支持三窗续接、取消和回待机。启动与验证范围见 [对话动作说明](../../docs/ardy-dialogue-motion.md) 和 [HTTP 协议](../../docs/ardy-live-protocol.md)。下面保留离线试验背景，固定资源预览窗口仍用于复查已验收手势。

固定资源预览：打开 **Tools → NeEEvA → ARDY Motion Preview**，在运行中的场景绑定角色即可播放、切换或回待机。左手使用六秒 ARDY 离线生成动作，右手使用其明确标注的镜像，点头/摇头使用短受控交流曲线。单臂动作使用胸部参照，并保留原始失败样本及三个新增原生右手候选；最终资源已通过两模型编辑模式和专项回归，播放器通过实际 Animator、ControlRig、嘴型和眨眼检查。详见 [使用与验证说明](../../docs/ardy-unity-preview.md)。此预览窗口的按钮继续读取固定资源。

这组工具验证 NeEEvA 的单份 Qwen 权重复用和动作条件迁移。运行环境为本机 4090、SSH 别名 `neeeva-5090` 对应的 5090。Qwen 特征出口已从早期 C API 探针扩展为共享 server 的 HTTP 路由，保留视觉与三个聊天槽。HTTP 特征与训练缓存数值、实际聊天 KV 和视觉切换已验证；长上下文满载并发仍未验收。

## 已验证（2026-09-10）

- 真实部署的 Qwen3.6 GGUF，仅调用一次模型加载，创建独立的聊天和动作特征 context。
- 2000 条固定描述已导出 `[2000,2048]` 特征；聊天的短贪心生成交错测试一致，A/B/A 动作特征重置测试差为 0。
- ARDY Core40 在 `text_encoder=False` 下成功生成 40 帧、27 关节的有限值动作；仅为无文本条件的运行测试。
- 原版教师 2000 条特征、线性/MLP 各两个种子的训练及两种适配器各 10 组教师/学生动作对比已完成。验证集选择 `runtime/mlp-seed1.pt`，测试特征余弦相似度 0.98838；这不是动作准确率。

完整结果与动作预览见 [第一批离线报告](reports/pilot-results.md)。

版本固定在 `upstream-lock.json`，模型权重、特征和环境均留在 Git 忽略目录。Qwen 编码契约绑定 GGUF 与 DLL 哈希、固定输入模板、raw last-token pooling；更换部署或特征规则后必须重新验证与训练。

## 安装与 Qwen 导出

以下命令从项目根目录运行。需 Python 3.11+、可用 CUDA PyTorch、MSVC C++ 工具链，以及既有 SSH 主机配置。当前 ARDY 环境以 `--system-site-packages` 只读复用本机 torch；它不是可搬到另一台机器的独立发行环境。安装脚本把需要覆盖的依赖安装在项目 `.venv` 内。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/MotionAdapter/bootstrap_sources.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Server/ARDY/install_core.ps1
python -m Tools.MotionAdapter.generate_prompts --output Tools/MotionAdapter/runtime/prompts.jsonl
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/MotionAdapter/build_probe.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/MotionAdapter/run_remote_probe.ps1 -Action start -Count 2000
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/MotionAdapter/run_remote_probe.ps1 -Action fetch
python -m Tools.MotionAdapter.import_probe --directory Tools/MotionAdapter/runtime/remote-probe --output Tools/MotionAdapter/runtime/qwen-2000.npz
```

`start` 会保持 SSH 会话到导出结束；程序退出后卸载 Qwen。如果生产 Qwen 已在运行，探针会拒绝启动，避免额外加载第二份权重。不要通过停止用户正在使用的服务强行运行。脚本只写远端 `D:\NeEEvA\motion-adapter-pilot` 实验目录，借用既有运行时 DLL 和 GGUF。

`fetch` 必须在 `start` 完成后运行。输入和输出哈希清单会拒绝混用不同轮次的缓存；本地 `dataset.jsonl` 必须与该轮输入对应。远端每轮使用同一个实验目录，重跑前先归档需要保留的本地结果。

## 原版教师与训练

教师使用 ARDY 自带的 LLM2Vec wrapper、Llama 3 8B Instruct 和两个 McGill 适配器。需先用获批账号在本机运行 `hf auth login`；账号获批与本机登录是两步。token 不写入项目或聊天。仅离线导出阶段加载教师，训练和后续动作推理不加载它。

后续新数据集可使用 [完整 CUDA 导出入口](teacher-full-cuda.md)。它从固定本机缓存加载官方 BF16 教师，先验证两条原缓存控制，再导出新 JSONL；支持独立输出、资源保护和审计报告。六条真实诊断及通用 CLI 回归均与原控制向量逐元素一致，内部 batch size 保持 1。

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.export_teacher --dataset Tools/MotionAdapter/runtime/prompts.jsonl --output Tools/MotionAdapter/runtime/teacher-2000.npz

& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.train --dataset Tools/MotionAdapter/runtime/prompts.jsonl --qwen Tools/MotionAdapter/runtime/qwen-2000.npz --teacher Tools/MotionAdapter/runtime/teacher-2000.npz --architecture linear --output Tools/MotionAdapter/runtime/linear.pt
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.train --dataset Tools/MotionAdapter/runtime/prompts.jsonl --qwen Tools/MotionAdapter/runtime/qwen-2000.npz --teacher Tools/MotionAdapter/runtime/teacher-2000.npz --architecture mlp --output Tools/MotionAdapter/runtime/mlp.pt
```

每次训练另存 `.report.json`。用验证集选结构与超参数，最终测试集仅用于报告；默认 seed 0 的单次结果不代表稳定性。MLP 有 12,593,152 个参数，纯 BF16 权重约 24 MiB；当前训练保存 FP32 权重和教师统计量。

可一次运行预先固定的两种结构、seed 0/1 四组试验，并只依据验证集选择用于动作比较的权重：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/MotionAdapter/train_pilot.ps1
```

该入口输出 `reports/adapter-pilot.json`，包含每组指标和恒定输出训练集教师均值的参考结果。最终测试集不参与模型选择。

当前文本是 144 个动作/左右/幅度/速度组合的模板改写，共 2000 条；同一组合的改写不跨分组。它适合管线和初步泛化试验，覆盖面有限，不能代替自然描述、真实动作标注与人工动作评价。

## 动作验证

无需教师即可检查 ARDY 的运行路径，零特征仅用于这一明确标记的无条件测试：

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.baseline_ardy --smoke-unconditioned --output Server/ARDY/runtime/core-smoke
```

得到教师缓存与训练权重后，生成同一描述、同一种子、无历史冷启动的教师/学生动作：

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.baseline_ardy --dataset Tools/MotionAdapter/runtime/prompts.jsonl --teacher Tools/MotionAdapter/runtime/teacher-2000.npz --qwen Tools/MotionAdapter/runtime/qwen-2000.npz --adapter Tools/MotionAdapter/runtime/mlp-seed1.pt --count 10 --output Server/ARDY/runtime/mlp-comparison
```

输出 `.npz` 使用原版查看器的 `[T,J,...]` 约定，包含根位置、局部/全局旋转、关节位置和足部接触；报告记录模型修订、种子、10 步采样参数、耗时与 PyTorch 分配显存峰值。该显存数字不含全部驱动/框架开销，不等于进程或整卡峰值。

比较数值差异并生成指定样本的骨架 GIF / PNG：

```powershell
python -m Tools.MotionAdapter.review_motion --directory Server/ARDY/runtime/mlp-comparison --preview-id motion-00005
```

预览依赖 matplotlib 和 Pillow。左右骨链分别标为橙色和蓝色；两边保持同一坐标范围。根位置、对齐根后的关节位置、旋转与足部接触差异衡量的是对教师的模仿程度，不是动作自然度或指令遵循率。

上述原始动作基线没有编译或启用可选 C++ 动作校正，原始 NPZ 保持不变。Unity 原型已加入离线历史续接样例、旋转重定向、动作切换和停止；当前头部按钮另外采用下述受控曲线。实际角色历史输入、在线流式生成及完整对话并发另行验收。

## Unity 资源与受控头部示意

左右挥手各为 120 帧 / 6 秒，使用三次 ARDY 窗口、自身最后 16 帧历史续接。单臂遮罩按源胸部相对运动映到当前目标胸部：旧右挥手源动作约有 29° 转胸，直接套角色全局手臂旋转而保留目标待机胸部，会使手臂与胸部参照不一致。此次修正播放器参照，不修改左右挥手源文件。

点头与摇头原始候选已被用户否定。先前的 pitch/yaw 幅度筛选没有发现点头伴随约 14° 侧歪、摇头 roll 达约 30.5° 等问题，不能用“可见”代表自然。`conversational_head.py` 当前构造一次温和下俯 8° 的点头，以及左右各 6° 的摇头；分段 quintic minimum-jerk 曲线 C2 连续、开始即响应、末尾回正。20 FPS 下播放分别为 37 帧 / 1.85 秒、41 帧 / 2.05 秒。Neck/Head 局部贡献为 20%/80%，通过 `additive-local` 叠加当前姿态。

**这些头部动作是显式受控交流曲线，不是 ARDY 原生生成改进或新训练结果。** 原始 NPZ、生成报告、候选失败记录和数据划分均保留；资源用 `text` / `displayIntent` 描述当前曲线，`source.rawText` 保存原提示词，原模型/条件 SHA 可追溯。`rawMetrics` 只描述已否定的原始候选，`profileStatistics` 才描述受控曲线；两者分开呈现。

已提供可直接播放的 JSON 资源，无需先生成模型缓存。若要从缓存复现资源：

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.generate_preview_clips
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.generate_preview_clips --head-candidates
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.build_unity_preview_assets --verify-only
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.build_unity_preview_assets
```

前两步生成原始审计数据，需要项目 ARDY 环境和缓存特征。后两步只用 CPU 校验/导出，不加载语言模型；已有原始缓存时无需重跑前两步。构建脚本校验十份源 NPZ 的 hash、记录、seed 与历史协议，导出挥手并应用受控头部曲线，同时保留 [原始生成报告](reports/unity-preview-generation.json)、[全部头部候选](reports/unity-head-candidates.json) 和 [资源来源/哈希清单](reports/unity-preview-assets.json)。

## 检查

```powershell
python -m unittest Tools.MotionAdapter.test_adapter -v
python -m unittest Tools.MotionAdapter.test_unity_export Tools.MotionAdapter.test_conversational_head -v
```

测试覆盖数据错配、文本变更、分组泄漏、非有限值、特征版本、教师尺度还原、训练统计隔离及梯度学习；合成数值只验证代码，不代表 Qwen→ARDY 的真实效果。

本轮导出与曲线两组检查共 11 项已通过，覆盖坐标/旋转协议、单位四元数、回正与主轴、零侧歪、Neck/Head 的 20%/80% 局部组合、C2 连续性及数值导数。它们不替代本轮 Unity 隔离验证与实际角色上的自然度验收。
