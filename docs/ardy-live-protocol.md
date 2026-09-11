# ARDY 实时动作协议

此接口服务于站立交流时的上身动作。Unity 的四种已验收基础手势继续使用本地资源；`generate` 必须调用共享 Qwen 的实时特征出口和 ARDY，失败时回待机，不使用缓存特征冒充生成。

## 数据链

`ChatSample 完成回复 → motion 独立通道 → ArdyLiveMotionController → 本地 ARDY HTTP → 同一 Qwen server 内的特征 context → MLP → ARDY → Unity`

动作在回复完成时触发；语音沿原有流程输出，尚未按 TTS 音节同步。Qwen 保留三个聊天槽和视觉能力，动作 context 为 512 token，与聊天共享同一份模型权重。ARDY 服务只加载 adapter 和动作网络。

## ARDY HTTP

本地默认 `http://127.0.0.1:8093`。不将未鉴权服务暴露到外网。

- `GET /health`：模型、特征来源、队列和服务状态。
- `POST /v1/motion/generate`：每次生成 40 个新帧，20 FPS，共三个窗口。
- `POST /v1/motion/cancel`：以角色、轮次、revision 取消；GPU 已运行的计算可继续，但结果不会提交或复活动作。

generate 请求为 camelCase：`characterId, turnId, revision, requestId, chunkIndex, description, mask="UpperBody", seed, timeoutMs=20000, maxChunks=3, initialHistory`。窗口索引为 0–2，描述最多 240 字符，随机种子为非负整数。每角色 revision 单调递增；同一 revision 不得改变描述、种子和历史。requestId 用于响应绑定及重试幂等。

随机种子直接采用请求的 `seed`，不再混入角色、轮次或 revision。同一模型、特征、历史和种子在相同运行环境中可跨身份重现；续窗沿用该 revision 独立的 CPU/CUDA 随机状态，不干扰其他动作。

首窗可带 `initialHistory={kind:"unity-upper-body-projection-v1",fps:20,frames:[{globalRotations:[27 个 xyzw 四元数]}]}`，最多 16 帧。可选 `conditioningFrames` 只接受整数 4、8、16，省略时仍为 16；这是显式实验参数，默认场景与控制器保持 16。服务取最近 N 帧，不足 N 时前填最早保留帧；不会将短历史暗中补成 16。Unity 仍保留最多 16 帧的实际采样环，默认序列化省略新参数以兼容旧服务。Unity 从当前实际渲染的上身骨骼逆映射到 Core27，去掉角色根朝向。缺失 Spine1 插值，手部端点跟随手腕，根与腿为单位旋转。服务执行 ARDY 官方运动表示的正向变换，计算归一化历史。这是上身投影，**不代表真实腿部、平移或足部接触捕获**。后续窗口只使用该 revision 已提交生成结果的最后 16 帧，响应始终只包含新帧。

Core27 的参考关节位置相对 Hips，不能把其 Hips 零向量直接当作世界位置。服务根据中立骨架最低的脚/脚趾推导站立高度，本版本合成 Hips 世界 Y 为 `0.954412825 m`，使最低脚趾位于地面。此高度仅用于动作网络的历史输入，不驱动 Unity 根位移。`provenance.initialHistoryProjection` 来源记录包含合成根位置及足部高度，便于复现。

响应回显全部身份字段，并带 `startFrame=40*chunkIndex,newFrames=40,historyFrames=0|4|8|16,fps=20,final`、`clip`、`provenance`、`timings`。无初历史的首窗为 0，有初历史的首窗按请求的 N 如实返回，续窗固定 16。`initialHistoryProjection` 的 `suppliedFrames,usedFrames,discardedFrames,paddedFrames,conditioningFrames` 区分输入、保留、裁剪、补齐及实际条件长度。clip 沿用 schema 1，固定 Core27 名称/父索引及单位参考旋转，Unity LH / Y-up / Z-forward，仅旋转；动态窗口不带 additive-local 头部配置。

provenance 的 `mode` 为 `dynamic-ardy-native`，首窗 `featureSource=live-qwen`，续窗为 `live-condition-reused`。`historySource` 明确起始投影或生成历史；timings 包含 `queueMs,featureMs,generationMs,totalMs`。

cancel 请求携带 `characterId,turnId,revision,requestId,reason`。取消形成 tombstone；迟到请求和响应被丢弃。Unity 本地同时中止 HTTP、停止续窗、平滑释放骨骼控制。未收到续窗时最多短暂等待 0.15 秒，然后停止，而不是重复循环最后一窗。

## 动作生命周期与诊断

身体动作独立于后续自主说话：新用户输入、用户开口、明确新动作、`none`、停止或断开会取消旧动作；只有自动续说而没有新动作时保留播放。同一回复链中名称和描述完全相同的动作只提交一次。当前动作名称、描述和生成/播放阶段作为程序事实提供给后续回复，降低重复发送动作标签的概率。

Unity 默认保留最近八份动作诊断 JSON。Editor 位于项目 `Logs/ardy-live`，构建程序位于 `Application.persistentDataPath/ardy-live`。记录动作描述、种子、精确初始历史、三个请求与响应、HTTP 状态和结束原因，不记录完整聊天或音频；单个消息正文上限 512 Ki 字符。可在 `ArdyLiveMotionController.recordMotionDiagnostics` 关闭。诊断用于重现新失败，旧截图没有完整历史，不能当作已精确重放。

## 共享 Qwen 特征

`POST /neeeva/motion-features` 使用 snake_case 请求 `text,request_id,feature_contract`。模板固定为 `Motion description: {text}\nRepresentation:`，输出 2048 维原始最后输入 token，不做归一化。

- Feature contract：`f30f7b62ee39bfe3c930b44e1d0654b291442653c310d715ad6ae3784eee31a0`
- Qwen GGUF SHA256：`071ee2a008ec51372f990d8efbea92ec9dd0137974110ef68fbfde429c8c6dd4`
- Protocol：`qwen-motion-raw-last-v1`
- Pooling：`last_input_token_raw`

响应还必须声明 `shared_model=true,feature_source=live-qwen,context_tokens=512`，并回显文本和请求 ID。runtime 构建来源单独记录，不替换训练特征契约。特征请求仅在聊天槽空闲时执行，不清除聊天 KV；排队有界，超长输入拒绝。

验证失败、过期、队列满或服务不可用均返回明确 HTTP 错误。Unity 验证身份、窗口顺序、契约、模型、骨架和单位四元数后才播放。测试 fixture 的来源必须明确标为 fixture，Unity 实时入口拒绝此来源。
