# Generated-motion diagnosis: input height and remaining bilateral semantics

The input-height error has a controlled reproduction and correction. The Core27 reference `joints.p` stores positions relative to Hips; its Hips is `[0,0,0]`, while the heel and toe joints are about 0.896 m and 0.954 m below it. The original projection code used that zero vector as the world pelvis position. Its historical frames therefore placed both feet underground. A quaternion roundtrip can remain exact while this world-space conditioning is wrong.

The correction derives source pelvis height from the lowest neutral foot/toe relative to Hips. It gives Y=0.954412825 m for this pinned skeleton, with heels at Y=0.058474925 m and toes at Y=0. No target-avatar leg contacts or root translation are claimed: the lower body remains a synthetic stationary projection.

The controlled comparison uses the same measured Qwen feature, same effective seed, same actual Unity Animator upper-body capture and three 40-frame windows. Only the support-plane placement changes. The captured Animator was `hugshoulder_00`, sampled in actual Unity PlayMode; it was not a T-pose fixture.

| Actual prompt | Seed | Spine3 maximum tilt, original → grounded | Hips maximum tilt, original → grounded |
|---|---:|---:|---:|
| Both arms overhead | 0 | 24.99° → 1.50° | 39.90° → 3.69° |
| Both arms overhead | 1 | 74.11° → 4.12° | 19.40° → 2.64° |
| Both forearms forward, palms up | 0 | 24.59° → 1.54° | 55.70° → 2.81° |
| Both forearms forward, palms up | 1 | 32.52° → 4.11° | 21.24° → 1.79° |

The projected root's maximum absolute normalized value falls from 5.227 to 0.412. The real motion tokenizer's mean global-rotation reconstruction error falls from 12.43° to 3.46° (maximum 37.35° to 16.13°). Its remaining reconstruction error is distinct from the near-zero representation/FK roundtrip and should not be hidden by that simpler check. The initial heading is zero because the hips and lower-body projection are neutral; canonicalizing this input changes the normalized values by less than 1.2e-6, so a missing heading canonicalization is not the cause of the large tilt in these cases.

The isolated real-avatar rendering confirms this effect after the existing Unity mapping. For seed 1, maximum torso tilt falls from 48.01° to 3.03° for the overhead prompt and from 15.99° to 2.12° for the explanatory prompt. A separate latest live HTTP/Unity regression on the corrected service completes three windows / 120 frames, consumes the 16-frame actual Animator history, rejects late responses and returns exactly to Animator after completion and cancellation. Its first-window latency is 1.374 s and all-window receipt is 1.751 s in that run. These validate posture integration and service behavior, not semantic naturalness.

The visible posture correction does **not** prove that the overhead command now works. For the actual overhead prompt at both tested seeds, the fraction of generated frames with both wrists above the head stays zero. It is also zero for the same overhead command under cold start and original ARDY history. In the separate four-text actual/canonical-style comparison, `raise-actual` and `raise-style` likewise produce no simultaneous above-head result with the current Qwen adapter. The pilot adapter corpus contains single-arm raise/lower and extend templates; it has no equivalent bilateral-overhead or bilateral explanation templates. The direct-teacher experiment below now provides evidence that the conditioned feature path is a material bottleneck for these overhead descriptions.

The explanatory prompts are evaluated separately: wrists above the head is **not** their success criterion. The reports include each wrist's chest-local forward position and displacement from its first generated frame, but do not convert those values into a semantic pass/fail. Both-forearm posture and palm-up orientation still require appropriate visual evaluation. The posture-tilt improvement above is valid for these prompts without implying that their complete meaning has been followed or failed.

The direct-teacher comparison has now completed after the user stopped the NeEEvA services. The full-CUDA official teacher exported four diagnostic vectors and two cached controls; both controls matched the original teacher cache exactly, element by element. The export process exited before the isolated ARDY generation began. No service was restarted by this experiment.

The same four texts, seeds 0/1, cold/actual-grounded histories and pinned ARDY produced sixteen teacher-conditioned clips. The eight overhead cases produced the following fraction of frames with both wrists above the head:

| Prompt | History | Seed | Current Qwen + adapter | Direct teacher |
|---|---|---:|---:|---:|
| Actual overhead | Cold | 0 | 0% | 92.5% |
| Actual overhead | Cold | 1 | 0% | 92.5% |
| Actual overhead | Grounded Animator 16 | 0 | 0% | 26.7% |
| Actual overhead | Grounded Animator 16 | 1 | 0% | 0% |
| Canonical-style overhead | Cold | 0 | 0% | 54.2% |
| Canonical-style overhead | Cold | 1 | 0% | 87.5% |
| Canonical-style overhead | Grounded Animator 16 | 0 | 0% | 65.0% |
| Canonical-style overhead | Grounded Animator 16 | 1 | 0% | 30.0% |

This intervention changes only the conditioning vector. It demonstrates that direct teacher conditioning unlocks an overhead capability missed by the current Qwen-to-adapter output. The actual overhead vector has cosine 0.682 and relative L2 error 0.771 against the verified teacher; its canonical-style counterpart has cosine 0.792 and relative L2 0.629. This supports improving the conditioned feature path and its training coverage. It does not isolate the MLP architecture from its training data or Qwen representation, and one grounded teacher case still fails, so history and stochastic generation remain relevant. The height event is a necessary observation, not full palm-orientation or naturalness acceptance. Explanatory motions have descriptive forearm-direction and elbow-flexion measurements only; they have no automatic semantic pass/fail.

Earlier capped/offload failures and the unsuccessful 18-GiB pause window remain historical diagnostic evidence; they did not establish physical hardware impossibility. The successful export and exact controls are recorded in the current `teacher-motion-diagnostic.json`.

Explicit seed semantics are also corrected. The prior implementation hashed character, turn and revision into the seed. The fixed implementation uses the request's seed directly while retaining separate history and CPU/CUDA RNG state per revision. The real HTTP regression verifies exact matching motion for matching inputs and seed across different character/revision identities, plus isolation under interleaving and cancellation.

Evidence:

- `generated-motion-history-ablation.json`: 24 original-height/cold/native-history trajectories, including actual-pose and Hips-relative captures.
- `generated-motion-ground-height-ablation.json`: 16 grounded projection trajectories; paired features were frozen from the first live-Qwen extraction.
- `generated-motion-qwen-condition-ablation.json`: actual/canonical text styles, cold/grounded history, seeds 0 and 1.
- `generated-motion-teacher-condition-ablation.json`: the matching sixteen direct-teacher trajectories, verified feature metadata and exact cached controls.
- `generated-motion-paired-condition-comparison.json`: paired height events, onset/hold durations, forearm/elbow observations, input hashes and CPU adapter-feature comparison.
- `ardy-motion-grounded-history-regression.json`: 31 real HTTP assertions for grounded history, explicit seed reproducibility, streaming and cancellation.
- `unity-live-playmode.json`: current real Unity/Animator/HTTP integration, including complete request/response traces and cancellation checks.
- `ardy-motion-grounded-service-running.json`: historical local-service readiness and exact loaded Python module hashes after the earlier integration test; the user subsequently stopped the services before the successful teacher experiment.
- `Server/ARDY/diagnose_generated_motion.py` retains an explicit local reproduction of the old Y=0 bug so the comparison remains reproducible after production is fixed.

These are bounded diagnostics for the reported commands, not a broad motion-quality evaluation. The old live service did not save exact request/history/response payloads, so they are controlled reconstructions rather than exact replay of the user's earlier screenshots. The newly added Unity trace supplies those payloads for subsequent failures. Accepted local baseline clips are unchanged.
