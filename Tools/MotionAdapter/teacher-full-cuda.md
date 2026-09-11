# 完整 CUDA 原教师导出

入口 `export_teacher_full_cuda.py` 用于新动作描述集的离线教师特征导出。
它复用已验证的官方 `LLM2Vec.from_pretrained`、原 MNTP 合并与监督适配器、
BF16 和官方 `encode(batch_size=1)`，输出未经归一化的 4096 维向量。
它不修改原导出脚本、2000 条缓存、模型权重或 vendor 源码。

从项目根目录执行，替换下面两个路径为新数据集和新输出路径：

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.export_teacher_full_cuda `
  --dataset Tools/MotionAdapter/runtime/new-motion-descriptions.jsonl `
  --output Tools/MotionAdapter/runtime/new-teacher-features.npz `
  --batch-size 1
```

JSONL 每行至少包含 `id`、`semantic_group`、`split`、`text`。
ID 必须唯一；同一语义组不能跨 train/val/test。示例：

```json
{"id":"both-arms-up-001","semantic_group":"both-arms-up","split":"train","text":"While standing in place, a person raises both arms straight upward until both hands are above the head."}
```

输出保留输入行序，包括 `.npz` 中的 `ids`、`features[N,4096]` 与 `metadata`。
旁边的 `.report.json` 保存模型版本、构造方式、控制样本数值对照、耗时与资源峰值；
`.resources.json` 在正常退出和异常退出时都保存资源观测。可用 `--report` 指定单独报告路径。
已有输出或报告会被拒绝覆盖。

每轮先用当前加载的真实教师重新编码原 `motion-00004` 与 `motion-00053`，
核验它们与固定 `teacher-2000.npz` 的余弦和误差。
原缓存的 IDs、描述清单哈希与教师合同也会先校验。
两条都满足 cosine ≥ 0.9999、relative L2 ≤ 0.005 后才编码新数据，
报告同时记录 max absolute error。2026-09-10 的六条完整 CUDA 验证中，两条控制逐元素误差均为 0。

`--batch-size` 当前仅接受 `1`。这是官方 ARDY wrapper 为保持数值可重复而固定的内部批大小；
增大批量需要另行验证，不能直接混入原教师契约。

模型全部来自固定本机缓存，网络离线。启动要求显卡空闲至少 18 GiB；
CUDA allocator 上限为 `min(18 GiB, 当前实际空闲−2 GiB)`。
默认进程 RSS 上限 8 GiB，持续保留至少 2 GiB 系统可用 RAM 和 2 GiB 全卡空闲显存。
CUDA allocator 与 `nvidia-smi` 指标分别保存，避免混用 Windows WDDM 的不同统计口径。
加载时逐张量释放 safetensors 源映射，并关闭多余 allocator warmup；模型数值与编码流程保持不变。

脚本不会启动、停止或恢复任何其它服务；GPU 使用窗口由调用方协调。
结束后进程退出并释放教师。后续 adapter 训练与 ARDY 动作推理仍使用离线特征，不加载教师。

成功的六条诊断见 [teacher-motion-diagnostic.json](reports/teacher-motion-diagnostic.json)。
