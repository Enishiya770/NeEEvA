# Intent, execution and narration validation

`data/intent-motion-consistency-v1.json` contains 12 development and 8 new held-out public fixtures. It covers the reported overlength description, malformed/source-invalid plan, wrong replay target, unsupported separate fingers, unmet near-head goal, measured versus predicted outcome, different initial histories, interruptions and a single corrective response. These cases do not reuse or overwrite the previous semantic holdout.

Scores stay separate: production parser/compiler acceptance; goal and reference coverage; actual post-VRM goal evidence; reviewed narration truthfulness; and user-rated naturalness. A pretty motion, source prompt, teacher MSE, successful HTTP response, or `completed` status is never sufficient for semantic success. `reached` verifies necessary wrist regions only. Without actual samples, motion success is `null`, including every planning-only probe below.

Development v2 completed 12 real shared-Qwen requests after one feedback-contract revision. Seven passed the frozen protocol/goal/reference checks. The remaining five are retained: one description without an action tag, three unsolicited shake-head substitutions, and one omitted pair of above-head observation goals. The explicit review is bound to the report SHA and keeps narration separate; even an accurate refusal with a replacement gesture fails the requested behavior. V1 remains available with its separate Mono-stdin encoding caveat.

The frozen holdout subsequently ran exactly once, after development and production lifecycle checks, using the same contract and the existing idle shared-Qwen service. All eight requests completed; six passed protocol/goal/reference checks. Explicit public-output review also labels six intent matches and six grounded narrations. The two failures are retained in `reports/intent-consistency-holdout-v1.json` and its `-review-labels.json` / `-reviewed.json` companions: the Japanese bunny-ear request still produced a promise and unsupported independent-finger command, which the actual parser rejected; the unknown-observation answer partly admitted uncertainty but first treated the requested flourish as measured execution and added an unsolicited shake-head. Parser enforcement is not successful planning, and it does not retract speech already delivered. All eight actual-motion results remain `null`: these are responses to labelled synthetic facts, not real avatar trajectories or an autonomous correction loop. No holdout answer was used to change the contract or rerun a case.

The production response still streams speech before its complete trailing action is dispatched. These tools do not establish a hard guarantee that already spoken words match later execution. A public-case response using synthetic body facts tests use of those facts, not real automatic repair scheduling or the private scene's full prompt.

## CPU and public Qwen probes

From the project root, using the existing ARDY Python environment:

```powershell
& ./Server/ARDY/.venv/Scripts/python.exe -m unittest discover -s Tools/MotionAdapter -p test_intent_consistency.py -v
& ./Server/ARDY/.venv/Scripts/python.exe Tools/MotionAdapter/validate_intent_consistency.py --freeze
& ./Server/ARDY/.venv/Scripts/python.exe Tools/MotionAdapter/validate_intent_consistency.py
& ./Server/ARDY/.venv/Scripts/python.exe Tools/MotionAdapter/validate_intent_consistency.py --execute-model --report Tools/MotionAdapter/reports/intent-consistency-development-v1.json
```

The default sends the exact production `GeneratedOutputContract` plus `MotionFeedbackOutputContract`. The semantic experiment requires explicit `--semantic-experiment`. Production C# routing/parser/ledger/compiler runs under the installed Unity Mono runtime without launching Unity. Raw model output is passed to this process through stdin; only public speech and executable tags are persisted, never `think`/`thought` contents or private scene data. Synthetic facts are visibly labelled in reports. Expected answers and review rubrics never enter model requests.

The probe requires the existing 8080 shared Qwen and 8093 verified ARDY health, preserves three 65,536-token contexts, and defers if any slot is busy or unknown. It does not start services, extract motion features, generate ARDY motion, or issue an automatic retry. A supplied failure fixture tests one corrective answer; real repair lifecycle must be tested separately through Chat and Controller.

The holdout command is `--split holdout --execute-model --report <new-path>`, but this suite's one run is now consumed. The global `.holdout.once.json` was exclusively created before its first model request; changing output paths does not permit another attempt. Interrupted/failed runs remain visible and may be incomplete. Do not remove this marker or use held-out answers to tune the contract. The earlier `--freeze` command is likewise a one-time operation; the existing frozen suite must be preserved.

## Existing services and isolated Unity

The read-only inventory is `reports/intent-consistency-environment-v1.json`. Process IDs are observations, not values to kill or assumptions for a later run. Full Unity command lines and credentials are not recorded. The existing supported cold-start commands, if the coordinator needs them, are:

```powershell
& ./Tools/neeeva_remote_llm.ps1 -Action start -Mode feature
& ./Server/ARDY/start_motion_service.ps1 -Port 8093 -FeatureUrl http://127.0.0.1:8080
```

An already running shared Qwen is reused; do not start another model. The ARDY launcher remains foreground; background orchestration must use a hidden process and retain its exact PID. No startup was performed by this inventory or probe.

Sync only when the isolated Editor has exited:

```powershell
& ./Server/ARDY/runtime/unity-naturalness-validation/sync-from-source.ps1 -IncludeDialogue
& 'E:/Unity_Data/2022.3.22f1/Editor/Unity.exe' -batchmode -nographics -projectPath 'E:/VRproject/VR20250611/NeEEvA/Server/ARDY/runtime/unity-naturalness-validation' -executeMethod ArdySemanticMotionRegression.RunBatch -logFile 'E:/VRproject/VR20250611/NeEEvA/Server/ARDY/runtime/unity-naturalness-validation/Logs/intent-semantic-regression.log'
```

The harness exits Unity itself: do not add `-quit`. Existing HTTP/Animator regression uses `ArdyLivePlayModeRegression.RunBatch` with `-ardyServiceUrl http://127.0.0.1:8093 -ardyConditioningFrames 16 -ardyLiveReport <new-absolute-report-path>`. The new observation/replay/repair harness must additionally bind the action ID, revision, source clip hash, requested unchanged goal, actual post-VRM samples, and cancellation timeline. A `replay` test must compare original/replayed clip hashes as well as rendered transition behaviour. Keep the user's main Unity project untouched.

For actual generalization, register the complete avatar/history/seed matrix before execution and include every row, including failures. Begin with two supported geometric goals across two real initial histories and seeds 0/1, then run the new held-out combinations after development is frozen. Unsupported fingers are expected explicit capability refusals, not failed animation targets to hide. Narration needs human review against these action-bound actual facts; keyword matching cannot certify entailment.

## Fixed eight-row actual motion matrix

`data/intent-generalization-matrix-v1.json` preregisters the exact two English descriptions, both wrist goals, two histories and seeds0/1 before any execution. `ArdyIntentGeneralizationRegression.RunBatch` uses real NEVA, its original Animator, an enabled ControlRig/VRM, and a recorder after the production observer. The second history begins the next request on the first real frame at or after0.7 seconds of the accepted left-wave clip, not a fabricated pose or source skeleton. The coordinator runs:

```powershell
& 'E:/Unity_Data/2022.3.22f1/Editor/Unity.exe' -batchmode -nographics -projectPath 'E:/VRproject/VR20250611/NeEEvA/Server/ARDY/runtime/unity-naturalness-validation' -executeMethod ArdyIntentGeneralizationRegression.RunBatch -ardyServiceUrl http://127.0.0.1:8093 -ardyIntentReport 'E:/VRproject/VR20250611/NeEEvA/Server/ARDY/runtime/unity-naturalness-validation/Logs/ardy-intent-generalization-v1/report.json' -logFile 'E:/VRproject/VR20250611/NeEEvA/Server/ARDY/runtime/unity-naturalness-validation/Logs/ardy-intent-generalization-v1.log'
```

No `-quit`. The180-second batch writes its plan before entering PlayMode, retains every row, and immediately copies each request/response trace outside the eight-file production retention ring. Each row records the actual initial-history hash, source-frame hash, individual HTTP timings, post-VRM observation sequence, action-bound terminal feedback and necessary-goal status. `not-reached` and `unknown` are ordinary results; no alternative prompt/seed is tried. Full semantic success and naturalness remain `null`. Any technical failure is recorded separately, with unexecuted planned rows still visible. The matrix makes eight motion requests through the controller (three HTTP windows each) and no additional Qwen chat request or automatic correction. A real correction loop is covered by the separate Chat lifecycle harness.

The completed run is `Server/ARDY/runtime/intent-feedback-v1/pass3/ArdyIntentGeneralizationRegression/report.json`: eight complete rows, zero infrastructure failures. Both near-head goals missed in all four histories/seeds. Both forward goals reached in idle/seed0, idle/seed1 and wave/seed1; wave/seed0 missed. Each row contains120 source frames and360–361 actual post-VRM samples covering approximately5.98–6.00 seconds. The three reached rows sustained both necessary regions for0.917,0.850 and0.733 seconds respectively. This proves the specified0.2-second necessary-region test, not complete semantics or sustained final holding. First-window observed latency was0.674–0.790 seconds.

`reports/intent-generalization-native-v1-audit.json` is an independent CPU audit. It reproduces every initial-history/window/source-frame JSON hash with the same serialization library, verifies archived trace and actual-sample file hashes, binds action ID / goal / generation, and independently recomputes goal regions and continuous duration from stored actual shoulder/elbow/wrist/head positions. All eight classifications agree with the production observer. It performs no model or Unity run. Reproduction is read-only and requires a new audit output path:

```powershell
& ./Server/ARDY/.venv/Scripts/python.exe -X utf8 Tools/MotionAdapter/audit_intent_matrix.py --report Server/ARDY/runtime/intent-feedback-v1/pass3/ArdyIntentGeneralizationRegression/report.json --output <new-audit-json>
```

Three provenance details matter when interpreting this run:

- Wave handoff was actually0.7000015–0.7166743 seconds, verified against both player time and a real rendered handoff pose. Sixteen history frames sampled at20Hz span0.75 seconds, so the earliest roughly1–2 frames can include the preceding idle-to-wave entry. The wire format does not contain absolute history-sample timestamps. All eight history hashes differ; this is a fixed history-category/seed matrix, not byte-identical history across seeds.
- `row.responseGeneration` stores the preparation/base generation. The generated request uses that value plus1, and its authoritative `trace.turnId` and `finalFeedback.responseGeneration` agree. For example the first row's base100 becomes generated response101. No generation mismatch was observed.
- Actual compiled harness SHA is `a88d6c3384fd5f18ca0a8f7990d384db66a0496db13b1bf5701e76ce50de8d72`, preserved in `final-source-snapshot/ArdyIntentGeneralizationRegression.compiled.cs`. Current source SHA `2c279d64ac77a1ce440e842e9066d0b46226be989d4b8c80455356d7ba2df10e` only changes `HashFile` to resolve relative paths against the isolated project's `Application.dataPath` parent instead of the process CWD; the audit records the exact one-expression diff. All emitted runtime/asset hashes match the isolated files or preserved compiled snapshots. The Controller snapshot is used because a later UI status-text correction changed its current copy. Absolute report/trace/sample hashes are unaffected. The matrix signature remains `734eec6869611b442890af6c978590c27da65b90da684ffab899f572b1c408d4`; no matrix rerun was made for this provenance correction.

After that audit, final review also added a Controller ownership check immediately before applying an HTTP response, with the same check governing error cleanup. This closes an external-Player replacement race before LateUpdate; it does not change motion geometry or goal thresholds. Final same-frame replay regressions and real HTTP results are recorded separately under `Server/ARDY/runtime/intent-feedback-v1/ownership-final`, with final-versus-matrix source hashes in `reports/intent-feedback-v1/acceptance.json`. The matrix continues to refer to its preserved original compiled Controller.
