# 草稿判断与首段 TTS 并行验证

日期：2026-09-25。继续使用现有 Spark Qwen `qwen3.6-35b-a3b` 与本机 GPT-SoVITS。修改范围是实验 FastLoop 的完整 reply 草稿、头部意图发布，以及已有 TTS 消费者的缓存领用。本文记录并行实现阶段，当时主场景尚未启用；后续用户授权的场景接入与真实播放器试跑见 [主场景试跑](fast-loop-main-scene-trial.md)。本轮不接管歌唱、全身移动或任意条件规划。

## 发布管线

1. 完整草稿到达后，启动意图判断，同时为第一个可播放片段静默准备音频。
2. 音频保存在一个私有槽内。判断前不公开草稿、不写助手历史、不进入可播放队列。
3. `none` 原稿或已登记的等待承诺，可在判定通过后发布。即时头部意图须先被实际 `NevaSceneAttention.TryLookAt` 接受并完成旧 owner 交接，随后动作和语音并行。
4. 完成声明、条件触发后的完成回执仍要求对应 task/revision、物理控制权与 settled 事实。
5. 全文、语言、草稿身份、轮次、generation、epoch、输出版本和 provider 均匹配时，原播放器领用已准备的首片段，不发第二次合成请求。后续片段使用已有播放管线。
6. 更正台词、重新规划、用户打断、失去控制权、换轮或过期时撤销相应缓存/剩余音频。准备超时为 25 秒。非流式 provider 的旧回调只销毁自己的 clip，不用全局取消接口破坏新请求。

provider 忙、不支持准备，或存在歌唱准备、旧队列等不兼容状态时，自动回退普通 TTS 路径。预合成可能产生最终未播放的首片段计算，但本实现不会并发启动两个 GPT-SoVITS 合成任务。

关键实现：

- `Assets/AIChatTookit/Scripts/Chat/ChatSample.ActorSpeech.cs`：私有首片段准备、审批、领用和撤销。
- `ChatSample.ActorIntent.cs`：判断与准备并行，实际接受后的意向发布；准备函数同步取消/重入时保留原请求权限，不能复活旧草稿。
- `ChatSample.FastLoop.cs`：区分接受事实与完成事实的语音门，撤销失效任务的剩余语音。
- `ChatSample.cs`：现有 WAV/PCM 消费者领用已批准的首片段，继续执行原播放门与实际播放追踪。

## 真实服务时延对照

运行 `ActorSpeechLatencyPlayRegression.RunBatch`，报告为 `Logs/actor-speech/live-latency.json`，Unity 日志为 `Logs/actor-speech-live-direct.log`。

使用生产 `ChatSample.CompileActorDraft`、实际 Qwen 判定和实际 GPT-SoVITS PCM 请求，参考音频及流式设置与主场景一致。Qwen 位于 `.175:8888`；TTS 位于本机 `9880`，首播缓冲阈值 0.65 秒、streaming mode 3。服务配置未修改。

测量从**完整草稿进入编译器**开始，直到**判定通过且首段 PCM 缓冲可播放**。fixture 没有 AudioSource，不发声，也不测试麦克风、ASR、前一阶段回复生成、扬声器或主场景竞争。串行组先判定后准备 TTS；并行组经生产私有缓存路径准备，并验证消费者取到同一个请求句柄。

共 8 个测量样本，每个样本各发起一次 Qwen 判断和一次 TTS 请求。预先指定首对为预热样本并排除，剩下 3 对中日文样本交替串/并行次序。数据如下：

| 样本 | 串行可播放缓冲 | 并行可播放缓冲 | 差值 |
| --- | ---: | ---: | ---: |
| 这个故事很有趣，后来的发展是什么？ | 2.000 秒 | 1.140 秒 | 0.860 秒 |
| その話は面白いね。（完整草稿还有下一句，仅首片段准备） | 1.963 秒 | 1.072 秒 | 0.891 秒 |
| 听起来你今天经历了不少事情。 | 2.161 秒 | 1.180 秒 | 0.981 秒 |
| 均值 | **2.041 秒** | **1.130 秒** | **0.911 秒，约 45%** |

常态样本的草稿到判断通过为 0.732–0.757 秒，均早于并行 TTS 可播放缓冲，因此本组中判断未在音频准备完成后再增加等待。实际节省包含 TTS 单次波动，不能把 0.911 秒理解为每轮保证的加速；3 对样本也不足以估计尾延迟、并发负载或长期稳定性。

首个预热样本记录到草稿至缓冲可播放 **19.672 秒**，其中 TTS 请求启动后约 **18.701 秒**才达到缓冲阈值；第二个预热样本恢复至 1.292 秒。该首请求异常长等待保留在原始报告中，未混入常态均值。fixture 特意未执行 ChatSample 启动时的自动 TTS 预热，因此不能由此断言主场景每次启动都要等同样长；并行化也不消除这种首次请求成本。

第一次测试进程未带项目已有的 Unity 直连环境设置，Qwen 请求 HTTP 失败，没有完成任何对照样本。失败报告另存为 `Logs/actor-speech/live-latency-default-proxy-failed.json`，日志为 `Logs/actor-speech-live-latency.log`。有效运行使用与 `Tools/start_unity_direct_services.ps1` 一致的 child process 环境：显式存在且为空的 `UNITY_PROXYSERVER`。未改 Windows 系统代理；不能用 PowerShell 5.1 的 `$env:UNITY_PROXYSERVER=''` 代替，因为它会删除变量。

## 正确性验证

Unity 编译成功；`FastLoopIntegrationRegression.RunBatch` **67 个用例、431 项检查全部通过**。使用实际 ChatSample 入口与消费者、可控 backend 和 TTS 替身，覆盖未审批不泄漏、原稿缓存仅领用一次、改口丢弃、全文/语言/provider 匹配、过期版本、打断、准备失败回退、WAV 迟到回调，以及实际编译入口发生同步取消/嵌套新编译。报告见 `Logs/fast-loop/integration.json`，日志为 `Logs/actor-speech-integration-final.log`；不把替身测试称为真实发声测试。

Unity `FastLoopHeadPlayRegression.RunBatch` 在未保存的独立场景导入实际 VRM 并执行生产控制协程，报告 `Logs/fast-loop/head-play.json`，结果 passed。确认：

- `intentPublishedBeforeSettle=true`：真实控制器接受后，动作尚未稳定时原意向已发布。
- `acceptedSpeechRevokedWhileMotionContinues=true`：撤销该轮语音后，独立头部任务继续执行。
- `conditionReceiptWaitedForSettle=true`：等待条件触发后，完成回执在 settled 前保持不可发布。
- 角色草稿恢复的右转约 17.062°，稳定约 0.489 秒；修正向左约 17.062°；第三个新计数事件后抬头约 11.054°。根位移与根旋转均为 0。
- 到达关节上限后再次抬头会失败，不获取新 owner，不把零位移虚报为完成。旧回调、重复事件和旧 owner 释放无效。

物理回归使用 3 次受控 backend 回调、零网络请求，不运行 TTS；它与上述真实 Qwen/TTS 测量分别证明动作时序和服务重叠，不合并宣称完整场景端到端首音或听感已通过。

## 当前运行范围

`Enable Actor Speech Preparation` 默认 true，但仅在 `Enable Fast Task Loop` 和 `Enable Actor Intent Compiler` 已开启时生效。本文所述并行验证阶段没有保存该切换；后续主场景已单独序列化开启三个开关，代码对其他场景的 FastLoop 默认值仍为 false。已有 `Enable First Reply Routing` 与 Qwen RoleDecision fast lane 仍是入口前提。

后续场景验收应观察完整草稿到任务接受、动作起始、AudioSource 实际推进的时间，并试验动作途中打断/换意图；不能只看 PCM 到达。现有 `[ActorSpeech/prepare|approve|adopt|discard]`、`[ActorIntent/Decision]`、`[FastLoop/Head] acceptedSeconds/settleSeconds` 与 ConversationTrace 可分别定位这几段时间。
