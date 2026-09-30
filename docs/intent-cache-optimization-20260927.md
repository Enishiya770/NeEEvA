# 当前意图保护与 Spark 缓存调优（2026-09-27）

已完成当前录音意图的受保护交接、历史请求恢复绑定、录音材料及 Actor 前缀整理，并部署实测选定的 Spark 配置。2026-09-27 02:03:57 UTC 复核：原 `qwen36-vllm.service` 已指向同名生产容器，`192.168.114.175:8888/health` 与 `/v1/models` 均 HTTP 200，运行和排队请求均 0，OOMKilled=false。主 Unity 场景未保存或改动。

本轮解决工程上已证实的目标丢失和旧请求授权漏洞；没有把模型的全部理解、复读问题宣称为已解决。所有资料位于 [工作目录](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/intent-cache-optimization-20260927)。

## 最终生效配置与内存

沿用实际安装的 vLLM `0.29.1rc1.dev187+gaf1c01499`、Qwen3.6-35B-A3B-NVFP4 权重及两个既有 GDN/Qwen 补丁；容器固定到原镜像 ID，没有拉取浮动 nightly。

| 项目 | 修改前 | 最终配置 / 实测 |
|---|---|---|
| KV pool | 自动预留 75.86 GiB | 显式 24 GiB（25769803776 bytes） |
| max-num-batched-tokens | 512 | 2048 |
| 前缀匹配 | 粗块，fine-grained=false | prefix-match-unit=32，fine-grained=true |
| 物理 attention / Mamba 块 | 2144 token | 2144 token；未把物理块改成 32 |
| Mamba cache mode | align | align |
| MTP | 3 speculative tokens | 保留 3 speculative tokens |
| KV dtype | fp8 | fp8；Mamba SSM 仍 float32 |
| max-model-len | 262144 | 262144 |
| max-num-seqs | 1 | 1 |
| EngineCore GPU 分配 | 102369 MiB ≈ 99.97 GiB | 最终 49503 MiB ≈ 48.34 GiB；实验阶段约 48.3–48.7 GiB |
| 主机 MemAvailable | 约 5.59 GiB | 最终约 60.22 GiB |

`gpu-memory-utilization=.84` 参数仍在，但显式 `kv-cache-memory-bytes` 决定缓存预算。统一内存中的进程 GPU 分配和系统已用不能相加。最终内存压力 avg10/60/300 均 0，未配置 swap，当前服务 preemptions=0。

服务报告 group-aware cache token capacity 2,026,867，长上下文容量比估算 7.73；这是池容量估算，不是运行并发，实际仍限制 1 个序列。约 36,850-token 输入已实际完成正确性烟雾测试，未声称穷尽验证 262144-token 全长或多小时不同前缀淘汰行为。

最终参数增量：

```text
--kv-cache-memory-bytes 25769803776
--max-num-batched-tokens 2048
--prefix-match-unit 32
--enable-mamba-fine-grained-prefix-cache
```

镜像 ID：`sha256:cf41fa0cc731193cd646cac16294a403951fd802b4da3030ad9a34207849ff56`。部署身份、完整参数、主机内存、服务状态及 Prometheus 数据保存在 `promotion.json`、`final-remote-state.json`；原始配置在 `remote-baseline.json`，远端另保存权限为 0600 的完整 Docker inspect。

## 性能对照与选择依据

依次测试原配置、仅缩池、缩池+2048、再加细粒度、关闭 MTP；候选每次沿用同一固定镜像/权重/补丁。只运行一个服务实例，先做排除在统计外的 schema/kernel 预热，再发送相同基准请求。每次前后采指标，56 个计时区间全部恰好新增 1 个成功请求，且结束时服务空闲。没有调用 reset cache；冷请求通过独特前缀产生。

下表为首次完整对照的**客户端首个文本内容时间**，不是 TTS 首音，也不是整轮完成时间。最终配置重启后又以两个新 namespace 复测；数值方向一致，原始请求、流响应、指标均保存。

| 样例 | 原配置 | 仅缩池 24 GiB | +2048 | +细粒度（选定） | 关闭 MTP |
|---|---:|---:|---:|---:|---:|
| Actor 1094 token，冷 | .402s | .402s | .265s | .335s | .240s |
| Actor，原样重发 | .384s | .394s | .278s | .119s | .262s |
| Fast 3257 token，冷 | 1.035s | 1.047s | .755s | .810s | .682s |
| Fast，原样重发 | 1.057s | 1.043s | .741s | .124s | .264s |
| 长输入 7627 token，冷 | 2.357s | 2.349s | 1.734s | 1.817s | 1.605s |
| 长输入，同前缀、换末尾问题 | 1.099s | 1.111s | .800s | .939s | .309s |
| 换过早期前缀后原样重发 | 1.102s | 1.101s | .798s | .127s | .309s |

- 仅缩池的延迟和命中接近基线：释放内存本身没有明显提速。
- 2048 降低了本批冷请求预填充耗时。
- 细粒度使短 Actor / Fast 原样重发实际缓存分别达到 1056 / 3200 token，长输入原样重发达到 7584 token。原粗块配置对应为 0 / 0 / 4288。
- 更换末尾问题的首次长请求仍只复用 4288 token；细粒度并非所有输入都更快，不能用原样重发成绩承诺每轮体验。
- 关闭 MTP 虽部分首 token 更快，但输出变慢：同为 82 输出 token 的 Actor 冷请求，选定配置总耗时 1.127s、decode .794s；关闭 MTP 总耗时 2.936s、decode 2.697s。Fast 两组均输出 80 token 时，总耗时约 1.753s 对 3.310s，因此保留 MTP。
- 其他样本输出长度有差异，即使 temperature=0，也没有假设字节级确定性；不把所有总耗时差归因于缓存。
- 最终重启复测：Actor 原样重发首内容 .116–.118s，Fast .124–.125s；冷 Fast .798–.814s，冷长输入 1.768–1.773s。新 namespace 的 token 数有 1 token 左右变化，复测用于稳定性检查，不冒充逐字相同的最初对照。

汇总：[benchmark-summary.json](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/intent-cache-optimization-20260927/benchmark-summary.json)。

## 已保留的代码修复

1. **受保护的当前意图。** 录音决策上下文携带完整、脱离可变所有者的 `current_intent`：ActorProposal、完整目标、输入身份、user turn / work epoch / generation。普通背景被裁光时它仍保留；同轮修复换 generation 不丢原提案，新轮不能继承旧提案。独立审核收到同一份当前意图，它并不自动构成执行许可。
2. **历史引文只能恢复明确绑定的未完成任务。** 新请求用本轮原话。旧引文只有绑定到已审核、尚未完成的活动目标，核对完整素材顺序、范围、成功条件与版本后才可继续。已完成旧任务或任意存在于历史的引文不能批准新演唱。
3. **合法恢复及修改仍能进行。** 原始请求离开普通 16 轮历史后，保留活动目标审核时核定的精确原引文，明确其录音身份未知，不补造历史。改变未完成目标时，简表交给现有 revise/replace 规划器，并携带完整当前目标和提案；原样续接则显式生成 continue/base_version。角色可以拒绝、撤回或澄清。
4. **录音材料与 Actor 布局。** 完整录音素材行置于会增长的数量信息及当前输入之前；必要时列定义先于无损列值。Actor 只重排当前快照中的顶层字段，不冻结事实、不改数组顺序或信任层级。
5. **异步服务区间指标。** 为配置中的私有 LAN OpenAI-compatible 地址开启现有正式请求的 `/metrics` 区间采样，补充实际 cached/total prompt、decode、success、preemption 计数。仍是全服务区间，不冒充单请求归因，不阻塞会话。Fast/Actor 独立请求尚未各自接入这一 interval probe；响应 usage 未报告 cached_tokens 仍表示未知。

主要代码：`ChatSample.SemanticClosure.cs`、`ChatSample.RecordingInput.cs`、`ChatSample.SingingGoal.cs`、`RecordingSingingDecision.cs`、`ActorIntentProtocol.cs`、`ProviderTimingInterval.cs`、`ChatQW.ProviderMetrics.cs`。

## 未保留的 Fast 排序实验

Fast 字段排序实验可以增加序列化公共前缀，但真实模型配对案例出现候选语义退步：三段合并从 legacy 交规划变为 reply，并额外吸收旧调音条件和准备询问。每种布局仅一个样本，不能证明所有重排都有害，也不能证明稳定因果；因收益尚未实测确立，本轮保守撤回该实验。

最终 `FastLoopPrompt.cs` 与本轮修改前逐字节一致，SHA256 `FF15B8DD86AF79EC321209BB6D37A04263D85B9635CCB615F4F4773311A5CAC9`。实验文件在 `withheld-fast-layout/`；`layout-live-fixtures/*after*` 指保存的实验版，不是最终 Fast 源码。保留了原先过时 v1 测试夹具的修正。没有修改通用图片 `[image,text]` 次序或旧启动预热逻辑。

## 验证与真实模型限制

- 最终保留的源码重新编译：372 个 C# 文件成功，所有编译哈希与最终源码一致；隔离 Unity 14 组、1406 条断言全部通过，其中新的目标保护/历史淘汰场景 31 条。
- 纯测试：Recording 166、Fast context 830、Actor 95 个语义夹具 / 645 条断言、non-action 196、Fast transport 93、TaskControl 51、Actor transport 81、DecisionClosure 81 均通过。夹具不是模型准确率。
- 主场景和 QualitySettings 哈希未变；主 Unity PID 15620 始终保留。隔离项目退出，夹具 DLL 已恢复。
- 服务缓存烟雾测试 13/13：短输入、约 36,850-token 输入、改变问题、改变前部事实、红/蓝/红当前图片均答对。这是检索及图片新鲜度检查，不等于复杂多模态理解评估。
- 真实 Qwen 的 26 素材重建案例，5/5 通过编译、决策类型、有序素材和来源关系子集。三段选为 10→19→25，单独第三段选 25，库存查询未发演唱，角色拒唱未发演唱，绑定恢复选原 10→19。没有执行独立目标审核、音频转换或播放；这些不是原 Unity 完整 wire 的精确回放。
- **仍有理解问题。** 拒唱案例虽然不发动作，却把角色不想唱错说成用户不想唱；Fast 库存问答在原布局和实验布局都误解“不用重新唱、只问有没有”为不想再连唱，并用“记在心里”替代准确事实。Actor 三段任务也在两边都混入旧调音条件。不能把 5/5 子集或所有 JSON 可解析写成整体理解已解决。
- 下一批应保护当前待回答事项、用户已否定的解释、角色自身选择及已失效旧目标的边界；不应仅继续扩大缓存、增加历史或更换措辞。

验证详情：[integration/review.md](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/intent-cache-optimization-20260927/integration/review.md)；模型细读：[model-evaluation.md](E:/NeEEvA/NeEEvA-fix-singing-material-version-and-public-boundary/work/intent-cache-optimization-20260927/model-evaluation.md)。

## 回退与下一场观察

原生产容器完整保留为 `qwen36-35b-nvfp4-baseline-20260927`（已停，restart=no）。选定容器接回原名 `qwen36-35b-nvfp4`，restart=unless-stopped，原 systemd 的启停命令和一键启动入口继续使用原名。其他候选均停止，不占用推理内存。

如需回退，以下命令核对容器 ID、拒绝在服务有活动/排队请求时切换，并恢复原配置；之后需要等待原模型重新加载：

```powershell
ssh.exe -o BatchMode=yes -o IdentitiesOnly=yes -i C:/Users/33830/.ssh/id_ed25519_dgx_spark spark_root_001@192.168.114.175 "python3 /home/spark_root_001/neeeva-vllm-tuning-20260927/remote-tuning.py rollback"
```

下一场正常 Unity 对话需要重新测实际停说/采集关闭到 AudioSource 首个播放进度，分别看普通回复、首次新图和三段连接；同时检查当前意图是否进入 IR/Review、三段实际播放数、区间缓存指标及恢复行为。本轮的首文本基准不能承诺端到端首音同比例下降。24 GiB 池保留旧前缀的空间小于原来，多小时会话下的命中/淘汰仍需观测。
