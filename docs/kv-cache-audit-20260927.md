# Spark KV cache、内存与延迟核查（2026-09-27）

结论：KV cache 与跨请求 prefix caching 均已启用并有实际复用。约 100 GiB 进程 GPU 分配主要来自约 75.86 GiB 缓存池预留，而不是 100 GiB 模型权重。当前值得改善的是可复用前缀布局、混合模型缓存粒度和调度，而非继续扩大池子。缓存可减少输入预填充耗时，不能修复 Unity 已裁掉的当前意图、错误历史引文或审核理解错误。

所有检查均为只读服务查询和 8 次无执行的诊断推理；未重启、重配、清空缓存或修改 Unity Assets。原始输出位于 [审计目录](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/kv-cache-audit-20260927)。

## 服务与内存实测

采集 UTC 2026-09-27 01:11 左右。服务 `qwen36-vllm.service` 是启动容器的 oneshot，systemd 显示 active/exited；实际 `qwen36-35b-nvfp4` 容器 running，开始于 2026-09-25 04:10:45 UTC，OOMKilled=false。

- 容器镜像：`vllm/vllm-openai:nightly-aarch64`；实际版本 `0.29.1rc1.dev187+gaf1c01499`。
- 模型目录：`Qwen3.6-35B-A3B-NVFP4-Fast`；served name `qwen3.6-35b-a3b`。
- 当前配置：`gpu-memory-utilization=0.84`、`max-model-len=262144`、`max-num-seqs=1`、`max-num-batched-tokens=512`、`enable-prefix-caching`、`kv-cache-dtype=fp8`、`enforce-eager`、MTP 3 token。
- 实际 cache config：Mamba `align`，attention/mamba block=2144 token，fine-grained prefix cache=false，prefix_match_unit=None，Mamba SSM dtype=float32。FP8 参数不意味着所有混合状态都是 FP8。
- 有两个只读覆盖的 GDN/Qwen3Next Python 补丁文件，服务调优需保留版本和补丁兼容性，不能用浮动 nightly 替换来假定结果等价。

| 测量口径 | 值 |
|---|---:|
| 最新启动日志的 model loading | 22.15 GiB |
| 启动时 weights + non-torch consumed | 24.67 GiB |
| 初始化缓存池 | 75.86 GiB |
| 启动目标 executor 预算 | 100.53 GiB |
| 本轮 nvidia-smi EngineCore used_gpu_memory | 102369 MiB = 99.97 GiB |
| 系统 MemAvailable | 约 5.59 GiB |
| swap | 未配置，pswpin/pswpout=0 |
| 当前 memory pressure | avg10/60/300 均 0；oom_kill=0 |

启动与当前统计口径/时刻不同，不将各项强行相加为精确当前值。Spark 的 CPU/GPU 使用同一物理统一内存池，不能把进程 GPU 使用与系统已用再相加。[NVIDIA 硬件说明](https://docs.nvidia.com/dgx/dgx-spark/hardware.html)

`gpu-memory-utilization=.84` 按设备预算为模型执行器预留，扣除权重等后大部用于 cache。262144 是最大单请求上下文；当前池还报告 group-aware 6,405,431 token 容量与 24.43x 长请求容量估算，但实际调度限制仍是 max-num-seqs=1，不能把容量估算叫作实际并发。只降低 max-model-len 不保证明显降低由利用率决定的预留池；需要显式内存预算或利用率调优。[vLLM cache 配置](https://docs.vllm.ai/en/latest/api/vllm/config/cache/)

活动请求结束时 `kv_cache_usage_perc=0`；近期场景日志活动占用约 0.x%。这反映活动块，不等于池内没有可复用、可回收的旧前缀，也不能单凭此值承诺无损缩池。

## 缓存是否真正用上

基线 `/metrics`：

- prefix lookup hits 5,055,552 / queries 16,867,516，约 29.97%。
- 实际缓存 prompt token 3,098,080 / total prompt token 12,075,476，约 25.66%。
- num_preemptions_total=0。

两种指标定义不同，不混用分母；均为服务累计值，包含历史对话、测试及其他请求，不能冒充最新单场 Unity 命中率。它们足以证明不是“仅开了参数但完全没用”。Unity 记录的 cachedTokensReported=null 是服务器 response 没报该字段，不等于 0 命中。现有 LAN OpenAICompatible 路径未纳入 Unity 的 /metrics 采样。

## 当前服务上的只读冷暖测试

固定独特前缀制造冷样例，不调用 reset cache。各请求前后抓服务指标，8 个样本每个区间都恰好新增 1 个成功请求，发送前后服务均无其他活动/排队请求。前两个家族取项目既有 Actor/Fast 诊断请求，加无动作诊断 namespace；长前缀使用无动作参考行，输出仅 OK。都不是完整场景延迟基准。

| 样例 | 输入 token | 实际缓存 token 增量 | 首个文本内容 | 预填充 |
|---|---:|---:|---:|---:|
| 短 Actor 冷 | 1093 | 0 | 0.395s | 0.371s |
| 短 Actor 原样重发 | 1093 | 0 | 0.386s | 0.365s |
| Fast 样例冷 | 3256 | 0 | 1.033s | 1.007s |
| Fast 样例原样重发 | 3256 | 0 | 1.048s | 1.006s |
| 长前缀 A 冷 | 7626 | 0 | 2.349s | 2.320s |
| 长前缀 A，只改最后问题 | 7626 | 4288 | 1.102s | 1.069s |
| 改动开头为前缀 B | 7626 | 0 | 2.342s | 2.315s |
| 前缀 B 原样重发 | 7626 | 4288 | 1.103s | 1.074s |

长样例复用两个 2144-token 块，首内容约减少 1.25s。短请求即使完全一样也没复用，不能承诺所有路径都靠缓存提速。实测与当前粗块对齐、Mamba align、MTP 命中时回退重算末块机制一致；本轮没有关闭 MTP 或切换粒度，因此不把单个配置的贡献写成已完成因果消融。已保存实际安装代码的相关片段。MTP 还可能加快输出，不能只看它对前缀命中的影响就关闭。

APC 加速输入预填充，不直接减少输出解码时间；缓存 token 仍属于逻辑上下文，不是免费的额外记忆，也不代替每轮正确构造请求。[vLLM APC 机制与限制](https://docs.vllm.ai/en/v0.30.0/features/automatic_prefix_caching/)

## 应用侧确定的可优化点

源码审计详见 [prompt-layout-audit.md](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/kv-cache-audit-20260927/prompt-layout-audit.md)。

- Fast/Actor/独立歌唱审核有各自固定 system，可各自复用，不应为了共享缓存合并角色与审核职责。
- Fast 的新图位于 user 文本前；无图时 user 也较早放当前头部角度、任务 ID、时间和 capture ID，后面的固定素材/技能文字不能按相同后缀复用。
- Recording PromptFacts 先放 current user/current input，后放大素材表。同一素材表随新输入变化，无法按相同前缀复用。
- 启动预热取原始完整 system，与 Fast/Actor/Recording 压缩协议等并不相同。出现预热成功日志不代表这些关键路径都已暖好。
- 当前使用 vLLM OpenAICompatible，不走 llama.cpp 的 `id_slot`。不发 slot 不等于没有 APC。

建议把固定契约与版本化、确定排序的原始素材事实放在可复用前部，把当前输入、修订、变化状态和新图放在清晰的当前上下文部分。图文绑定、最新纠正优先和事实新鲜度必须通过回放验证；不能冻结状态或用旧图来制造命中，不能添加无用填充强迫短请求跨越缓存块。

## 对当前两类问题的作用

1. **延迟：有直接价值。** 长输入重复计算已经实测能减少约 1.25s/该诊断请求。应测真实路径冷暖前缀、预填充、输出、排队，而不是拿全服务累计 hit ratio 代替体验。
2. **上下文正确性：只有间接价值。** 可降低保留相关稳定证据的代价；不能补回发请求前裁掉的 ActorProposal，不能阻止旧目标替换新目标。服务已有 262144 token 上限，上一场裁剪发生于 Unity 自己的预算/字段优先级，不是缓存池容量不足。
3. **内存：有缩减预留的空间值得测试。** 大池有助保留更多不同前缀，但当前不是越大越快；缩池先测容量、淘汰与命中，不能把释放内存直接承诺成性能提升。当前没有 swap/OOM 证据支持“因内存交换导致本场慢”。
4. **调度：也值得纳入。** max-num-batched-tokens=512 偏向小预填充分块，应与更大候选对照；max-num-seqs=1 意味着应用层多个 LLM HTTP 并发也不能当成模型同时执行。粗块/细粒度、MTP 和现有补丁的兼容需一起测，不能直接套最新版命令。[vLLM 分块预填充调优](https://docs.vllm.ai/en/latest/configuration/optimization/)

推荐顺序：保存当前基线 → 修复当前意图受保护交接并整理稳定前缀 → 在维护窗口对比缓存粒度/MTP/预填充预算 → 在保持命中与长请求容量的前提下缩减缓存预留。正确性回放与首音测量同批进行。
