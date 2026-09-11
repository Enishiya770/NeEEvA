# 新动作域的 adapter 候选训练

这组入口只读旧 2000 条描述、两个特征缓存和已验收 MLP checkpoint。
新旧向量合并到独立目录；四个候选写入另一个新目录，运行服务的批准 SHA 与旧权重保持不变。

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.merge_extension_bundles `
  --new-dataset <经过复核的新JSONL> --new-qwen <新QwenNPZ> --new-teacher <新教师NPZ> `
  --output-directory <新的合并目录>

& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.train_extension select `
  --merged-directory <新的合并目录> --output-directory <新的候选目录> --device cuda
```

合并前核验记录哈希、ID/语义组划分、Qwen 合同、旧 checkpoint 的来源及每个源文件 SHA。
新教师必须给出两条原控制向量的 **exact match**，并绑定原教师合同、原描述清单哈希、
相同模型/软件版本与官方 batch-size-1 构造。
旧 JSONL 的完整字节前缀和原向量保留；合并教师使用明确的组合合同，包含两份来源 metadata 与兼容证据。
输出是 `dataset.jsonl`、`qwen.npz`、`teacher.npz`、`merge.json`。

选型预注册四个候选：scratch/finetune × seed 0/1。
scratch 学习率 1e-4，finetune 5e-5，最多 100 epochs、patience 10、batch 256。
两种模式都冻结旧 checkpoint 的 `target_mean` 和 `target_std`。
目标仍为标准化 residual MSE + 0.1 × cosine loss；每个 batch 的 old/new 各占一半损失。

每个 epoch 分开评估 old/new validation，使用旧 std 计算全部留存指标。
四项资格门槛是：

- 旧 val MSE 不超过旧模型的 1.05 倍，cosine 不下降超过 0.001。
- 新 val MSE 至少改善 10%，cosine 不低于旧模型。
- 按新 val 的 semantic group 做配对 bootstrap，MSE 改善的 95% 区间下界必须大于 0。
- 所有数值有限。

合格 epoch/候选按 old/new 各半的相对 baseline MSE 评分选择。
早停也使用这个等权评分。没有合格候选时，`selection.json` 会明确 `selected=false`；不会自动放宽门槛。
bootstrap 是 validation 选型证据，不能解释为最终 test 结论。

新语料刻意保留宽覆盖，会与旧 holdout 的挥手、举手、点头等动作语义重叠。
因此旧 val/test 明确称为 **retention audit**，不声称是未见动作泛化。
新域的划分与另行冻结的 80 条都是配置/表达审计，允许共享动作原子；冻结 80 条不参与选模。

`select` 不计算 test 指标。它先写 `preregistered-policy.json`，随后保存四份独立候选和报告，
最后把胜者路径、checkpoint SHA、合并清单 SHA 锁入 `selection.json`。
锁文件同时记录模型结构、Qwen/teacher/data 合同及预注册策略文件 SHA，供隔离 HTTP 测试严格验真。
需要显式授权的最终测试审计时，另外调用：

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m Tools.MotionAdapter.train_extension audit `
  --merged-directory <同一合并目录> --selection <候选目录/selection.json> `
  --output <新的test审计报告.json> --device cuda
```

`audit` 只评估已经锁定且合格的单个候选，验证其 checkpoint/数据 SHA 未变，分别报告 old/new test。
特征门槛通过后仍需动作泛化与播放验证；这些工具不会替换线上 adapter。

隔离 HTTP 入口是 `python -m Server.ARDY.motion_service.app --port 8095 --feature-url http://127.0.0.1:8080 --adapter-selection <候选目录/selection.json>`。
它拒绝未锁定、未通过门槛、SHA/合同/MLP 结构不符的候选，且健康信息标记 `candidate=true`、`approvedForRuntime=false`。
该模式禁止使用正式端口 8093 或缓存特征测试模式，不修改默认启动器和旧模型。

CPU 数学检查：`python -m Tools.MotionAdapter.test_extension_policy`。
它使用小型合成网络验证归一化冻结、域权重、门槛和语义组 bootstrap，并用 NaN 毒化合成 test 目标确认选型训练不访问 test。
