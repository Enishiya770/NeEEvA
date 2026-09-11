# Public motion-feedback placement comparison

Exactly two requests were reserved and executed once: A followed by B. No retries, model starts, body execution, TTS, private scene prompt, private conversation, or holdout data were used.

The fixed public request was: “双臂向前平伸，用双手跟我打招呼，掌心朝我。” Its synthetic preceding assistant response used the invalid `palm="partner"` compose attribute. The current production C# parser confirmed that rejection before either request.

Both requests retained the complete production `GeneratedOutputContract`, `MotionFeedbackOutputContract`, and `SpeechText.OutputContract` before the user. The exact same short rejection fact and correction request were moved between two positions. The same rejected assistant response remained in both histories.

| Fixed variant | Message order after base system | Actual result |
| --- | --- | --- |
| A | ordinary contracts → short feedback → original user → rejected assistant | HTTP 200, finish `stop`; no public speech or action; production projection `<silent/>` |
| B | ordinary contracts → original user → rejected assistant → short feedback | HTTP 200, finish `stop`; public correction and a valid compose tag |

B public speech: “抱歉，刚才的指令有误。我现在重新执行：双臂前伸，掌心朝向你。”

B unchanged model tag:

```xml
<motion name="compose" left="forward" right="forward" leftPalm="partner" rightPalm="partner" joint="wrists" axis="palm-normal" amplitude="8" cycles="2" seconds="3.2" end="idle"/>
```

The actual C# parser accepted B; both sides resolve `BendAuto=true`. This verifies protocol correction only. No avatar motion was executed, and the wording is not evidence that a physical target was reached.

Both requests used temperature 0, seed 0, thinking disabled, maximum 700 tokens, SSE, and existing shared-Qwen slot 1. Before each request, all three Qwen slots and the ARDY queue were idle. Request latency was approximately 0.578 and 0.607 seconds respectively. The model hash was `071ee2a008ec51372f990d8efbea92ec9dd0137974110ef68fbfde429c8c6dd4`.

This fixed counterfactual supports a feedback-placement mechanism. It does not establish an overall repair rate, prove the cause of all five private-session failures, or distinguish a literal model `<silent/>` token from another empty public-channel response. The report preserves the public executable projection; it does not persist reasoning content.

Evidence:

- `ab-public.json` SHA256: `7874ac5ef8ed3ff1a0802f6a85d2fc834ff7a4185cf51f3bd509830315b16d9d`.
- A exact request SHA256: `cd6d1ed8fc16eaf2fbf93e2756f8cb1fb0adfa9ea97d597e6ba6b1ce64518009`.
- B exact request SHA256: `408e56ba71cf0597b09891960da221193f5f8483f50c6b2438c3b9419beb1372`.
- Once reservation and per-request attempt records: `Server/ARDY/runtime/feedback-placement-v1/`.
- Reproducible source, guarded against re-execution: `Tools/MotionAdapter/probe_motion_feedback_placement.py`.

The narrow implementation recommendation is a separate feedback-continuation API: retain ordinary context placement and put only the newly arrived execution fact after prior assistant messages. Do not globally move memory, skills, or normal continuation context.
