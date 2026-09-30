# FastLoop replay and live semantic checks

`cases.jsonl` contains 73 named cases: the original 32 semantic expectations, 25 task-control/recording-route cases (five actual latest-session inputs plus 20 contrasts), six rejected protocol messages, and ten task/output lifecycle scenarios. Chinese, Japanese, English, negation, quoted commands, ambiguous stop scope, compound requests, correction, voice-only interruption, ownership yield and late callbacks are represented. The original eight extra cases carry `group=heldout_after_prompt_freeze`; that describes their historical origin, not an untouched holdout for the current v2 prompt. This is a development regression set.

FastLoop v2 adds required `task_control`, `control_target_id` and `control_evidence` fields plus the `recording_singing` route. Control values are `none`, `stop_follow`, `stop_movement`; `none` requires empty identity/evidence. A stop needs a bounded identity and exact current-user evidence. The application must additionally match that ID to the current controller owner and verify real cancellation. The protocol parser cannot prove a stop request is semantically correct or that a controller stopped. Recording routing selects the existing decision stage only; it does not choose a clip/range, grant source approval, or execute singing. The old v1 wire is rejected; existing C# constructors get explicit v2/no-control defaults unless they override them.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/FastLoop/run-regression.ps1
```

The standalone harness compiles the **production** `FastLoopProtocol`, `FastLoopRuntime` and prompt factory against the project's Newtonsoft.Json DLL. No Unity instance, GPU, model or network is needed. It also rejects partial JSON, duplicate keys, JavaScript-only JSON extensions, unsupported languages, encoded tags and programmatically constructed invalid proposals. Build outputs and results go to `Logs/fast-loop-regression`.

The 57 semantic fixtures supply expected decisions to check protocol compatibility. Passing those fixture checks **does not measure model semantic accuracy**. The runtime cases likewise prove state and callback rules, not physical bone movement or TTS cancellation. Success publication is an authority check rather than an exactly-once message queue; caller-side receipt deduplication remains the caller's responsibility.

To obtain real model evidence, explicitly run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/FastLoop/test-model.ps1 -BaseUrl http://192.168.114.175:8888 -Model qwen3.6-35b-a3b
```

This sends the 57 cases sequentially to the supplied OpenAI-compatible endpoint. It does not execute Unity actions or start, stop or configure any service. It first exports `FastLoopProtocol.ResponseFormat()` plus messages built by `FastLoopPrompt.System()` / `User()`, using the project's persona and language assets; it does not maintain a second handwritten classification prompt. `-DefaultLanguage` defaults to `ja` and may be changed to match the scene configuration. Fixture context is synthetic and follows the runtime context shape; extra test facts such as voice activity are explicit in the exported requests.

Each request, raw response, transport error, token usage and wall-clock request duration is saved under a timestamped `Logs/fast-loop-model-*` directory. The same production parser then checks all responses, and the harness reports protocol validity, exact route/candidate and task-control/target agreement and p50/p95 request duration, with the historical fixture groups reported separately. Evaluation uses the expectations captured in that run's `production-protocol.json`, so adding fixtures does not silently change an older run's denominator. Different evidence substrings are accepted if the production parser verifies them against the current user input. Generated reply wording is not compared to the illustrative fixture wording and must be reviewed manually for deliberate refusal versus missed intent. A route-only pass is not sufficient for control cases. `parsed-results.jsonl` preserves these dimensions separately. Windows PowerShell responses are decoded from original UTF-8 bytes. `-ProtocolExport` can pin an already exported cohort; `-SkipEvaluation` supports read-only comparison against an older pinned parser. Do not regrade v1 responses using the v2 parser.

This is a small targeted regression suite, not a general capability benchmark. Request duration is complete response time for this bounded decision, not audio first-sound latency. Physical head motion, persistent pose ownership, real audio interruption and stale-callback behavior in Unity still require the scene test.

The independent body-stop gate is tested by `transport/Run-Checks.ps1`, which compiles the actual `FastTaskControlProtocol` and `ChatQW.FastTaskControl` source alongside the main transport. It checks strict one-field JSON, the restricted current-body-facts input, shared cancellation, and synchronous diagnostic/trace reentry. To export the same fixed users for an explicitly authorized live gate evaluation:

```powershell
Tools/FastLoop/transport/Run-Checks.ps1 -ControlCohort work/control-async-repair-20260926/v2-protocol.json -ExportControl work/control-async-repair-20260926/focused-control-protocol.json
Tools/FastLoop/test-control-model.ps1 -ProtocolExport work/control-async-repair-20260926/focused-control-protocol.json -OutputDirectory Logs/fast-task-control-new-run
```

That runner uses 32 output tokens and the actual factory's system/user/schema, with no character persona, previous reply, or main decision supplied to the gate. A gate `none` or failed response cannot authorize stopping. Runtime additionally requires a main termination proposal for the same current task and a gate result matching the actual controller mode; the gate does not introduce stopping when the main proposal is `none`. The fixed regression result and the failed prior main-prompt experiments are documented in `work/control-async-repair-20260926/focused-control-analysis.md` and `comparison-analysis.md`. Their recording-inventory fixtures are abbreviated controlled facts, not exact main-scene request captures.

To re-evaluate saved model responses without further requests:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/FastLoop/run-regression.ps1 -ModelResults Logs/fast-loop-model-YYYYMMDD-HHMMSS/responses.jsonl
```
