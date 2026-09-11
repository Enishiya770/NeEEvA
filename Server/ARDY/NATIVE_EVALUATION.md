# Offline native motion evaluation

`evaluate_native_motion.py` compares the verified teacher condition with named old/new adapters using the same record IDs, seeds and initial histories. It reads saved feature bundles, loads ARDY without a text encoder, and starts no service. Every trajectory generates three 40-frame windows at 20 FPS, using its own last 16 generated frames for continuation.

Validation example (substitute a prepared validation manifest, bundles, checkpoints and exact validation IDs):

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m Server.ARDY.evaluate_native_motion `
  --dataset path/to/training-with-val.jsonl `
  --qwen-bundle path/to/qwen.npz --teacher-bundle path/to/teacher.npz `
  --adapter old=Tools/MotionAdapter/runtime/mlp-seed1.pt `
  --adapter new=path/to/candidate.pt `
  --record-ids validation-id-1,validation-id-2 --seeds 0 1 `
  --history cold `
  --history grounded=Server/ARDY/runtime/generate-diagnostics/animator-idle-history.json `
  --split val --output Server/ARDY/runtime/native-eval-validation
```

Teacher and Qwen bundles must bind the complete input dataset, including its exact texts, IDs, splits and labels. Filtering to fixed IDs happens only after those manifest checks. Adapter checkpoints must match the Qwen feature contract. `--plan-only` verifies the bundle/manifest relationship and records the immutable plan without loading ARDY or reserving final-test execution. A later run can use the same directory only while it contains that identical plan alone.

The frozen `generalization-eval-v1.jsonl` is entirely test data. It is not validation data. Test execution requires both `--split test` and `--final-test`. Before generation the tool creates an exclusive audit marker keyed by the dataset hash in `Tools/MotionAdapter/reports`; existing markers or output results are not silently overwritten. Choose the candidate using independent validation first, then perform one fixed final audit. The earlier four-text comparison remains a seen diagnostic and must not be represented as a new held-out result.

Outputs include each trajectory's full NPZ and Unity clip, a separate `*.measurements.json` with every frame's measurements, a render manifest, input/checkpoint hashes and grouped observations. Conditions are identified as offline teacher or offline frozen-Qwen-plus-adapter; none is described as a fresh live request. Timing records cover ARDY generation/decode and CPU adapter projection separately. They omit live Qwen, networking, Unity and TTS, so they are not end-to-end latency claims.

Structured `assessment.machine_observations` are interpreted without reading meaning from prompt text or free-form target strings. Wrist height uses world +Y against the named head/chest/shoulder reference; shoulder means the Core27 `Arm` humeral pivot, not the medial clavicle. Left/right values retain explicit side order. A both-hands height condition must hold simultaneously in the same frame. Wrist excursion is measured in chest coordinates, and shoulder height change in Hips coordinates. Head/torso Euler conventions and possible gimbal warnings are recorded explicitly.

Only explicit supported height bands or thresholds yield `thresholdSatisfied`. This is a necessary geometric observation, not overall semantic success. Low-activity labels without numerical thresholds remain descriptive. Repetition, event order, palm orientation, precise contact and target-avatar naturalness remain manual checks. `semanticPass` is always unknown, and boundary cases marked `include_in_success_summary=false` are excluded from semantic success denominators. The tool performs no automated candidate ranking or selection.

CPU checks:

```powershell
& Server/ARDY/.venv/Scripts/python.exe -m unittest Server.ARDY.test_native_motion_eval -v
```

The fixtures exercise same-frame bilateral criteria, world-vs-chest coordinate distinctions, side and event timing, manual/unknown behavior, immutable audit records and split/text/label binding. They do not generate any held-out test motion.
