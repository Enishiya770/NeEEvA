# 局部录音边界回放

`replay_boundary.py` 对固定录音中的一句头部/尾部口语做局部边界实验。默认只显示真实 PCM 低能间隙；加 `--endpoint` 才串行调用现有 ASR，不启动或部署服务，不播放录音，不写声纹或歌曲记忆。

输入必须是本地 mono 16k WAV。manifest 包含 `schema=1`、`request_id`、`source_id`、`source_revision`、`source_offset_seconds`、`current_start_seconds`、`current_end_seconds`、`edge`（lead/tail）、`exclude_text`、`keep_text`、`language`。额外的 `observed_transcript` 用来验证两个逐字锚点均来自当时可见文本。脚本为实际上传的 PCM16 WAV 字节重新计算 SHA256。

```powershell
python -X utf8 Tools/IncrementalListening/replay_boundary.py `
  --wav 'Server/SenseVoice/practice_dumps/20260928_165353_seg1_p2_接下来唱第二段喽.wav' `
  --manifest work/incremental-listening-20260928/server/c24-boundary-manifest.json `
  --output work/incremental-listening-20260928/server/c24-boundary-dry.json
```

真实服务比较可加 `--endpoint http://192.168.114.163:9881 --max-remote-probes 8`。已有运行报告可用 `--reuse-report` 复用同一个 PCM SHA256 的实际探针结果，避免重复占用服务。输出保留每个输入 WAV、hash、服务原始 JSON 和耗时。缓存不能跨输入音频或前缀复用。

`--prefix-seconds` 物理截断上传与分析输入。此模式要求 manifest 额外给出 `observed_at_audio_seconds <= prefix-seconds`，且文本必须是该快照已观察到的 partial；脚本不能凭最终文字为更早的 prefix 提供语义先验。现有 c24 manifest 是完整回唱素材的事后纠错例子，不能直接用于录音中提前路由评估。

旧 `/asr` HTTP 回放包含 VAD 和完整识别后处理；新 `/singing/refine-boundary` 生产接口使用同一个 SenseVoice 模型的直接局部识别。因此 HTTP 回放证明这些真实音频切片可被现有模型转写支持，不能替代部署后的新接口验证。报告不把旧转写当人工真值。

新服务接口为 multipart：`audio_file` 固定来源的完整保留 WAV，`manifest` 为上述 JSON（正式接口不需要 observed_transcript）。还要求 `audio_sha256` 精确等于上传 WAV 字节的 SHA256。可选 `played_revision`、`pcm_lineage_hash` 原样回显，但服务不宣称自行校验外部 lineage 数据。

响应只在 `ok=true,status=resolved,quality=local-asr-acoustic-gap` 时返回 `range.start_seconds/end_seconds`。只收缩指定一端，另一端保持请求值。`word_timestamps=false`、`mode_verified=false` 明确表示它不是词级强制对齐，也不能证明剩余整段全是歌声；它只核验请求排除的短语位于删除侧、相邻保留锚点在另一侧完整可识别。客户端仍须验证请求归属、source/revision/hash、取消和音频版本。

最多搜索边缘 8 秒、8 个静音候选、16 次局部识别；候选 ASR 每次让出低优先队列，普通 final 优先。整个请求 30 秒、单次排队加推理 12 秒上限，同时最多 2 个边界请求。使用 `/asr/cancel` 的相同 request_id 取消整个分析；取消不会返回可执行范围。

相近候选只有在首尾距离不超过 0.65 秒、桥接音频每个 20 ms RMS 窗仍处于低能范围时，才作为同一次转场的等价切点，选择删除最少的那一个并报告完整不确定区间。较远多解、可听活动夹层、没有声学间隙、锚点不匹配或预算耗尽均明确 unresolved；不按文字长度分配时间。

health 增加 `singing_boundary_schema=1`、`singing_boundary_algorithm=local-gap-asr-v1`、`singing_boundary_ready`。无新运行依赖、模型或部署配置。
