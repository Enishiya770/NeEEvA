# Tempo execution acceptance — draft, awaiting schema freeze

This is a test design only. No production file or existing Unity harness was changed, and no Unity/GPU/service run was started. Schema fields, tempo values and engineering derivative limits remain to be supplied by the implementation owner.

## Existing evidence to reuse

- `Server/ARDY/runtime/palm-execution-v1/full-pass-v1/constraint-regression.json`, SHA256 `ae60a49f995314bb558e0dfbf4cf96ee50c9f2356ce2c71daa67d679a6a0c63a`: 42 real fixture cases, source hashes, actual partner-palm wrist curves and preserved Animator poses.
- The exact slow reference is amplitude ±10 degrees, two cycles in 3.2 seconds, nominal frequency 0.625 Hz. The user rejected its pace. It remains a comparison baseline, not an accepted naturalness target.
- The preceding constraint archive covers actual head curves, stop/disable/hold/TTL, observers, root rotation/scale, untouched bones and expressions. The four already accepted local resources remain `left-wave.json`, `right-wave.json`, `nod.json` and `shake-head.json`; their bytes and provenance must remain unchanged.
- The current reference curve is sinusoidal with multiplicative SmoothStep envelopes over the first/last 15% of the action. For the un-enveloped interior, ±10 degrees at 0.625 Hz implies an analytic peak speed of 39.270 degrees/s and acceleration of 154.213 degrees/s². These are reference calculations, not measured performance or medical safety limits. Envelope sections have different derivatives.

## Smallest new harness extension

Proposed new file: `Assets/Editor/ArdyConstraintTempoRegression.cs`, a partial companion to `ArdyConstraintRegression`, with a new `RunTempoBatch` entry. Reuse the existing four real fixtures and isolated unsaved scene: NEVA/NeEEvA, disabled-VRM Humanoid and actual ControlRig, yaw25/65 degrees and scale0.8/1/1.2. Continue actual Animator → Player → VRM → production observer12000 → independent observer12500. No manual Tick, Animator.Update or VRM.Process.

The exact integration hooks will be agreed after schema freeze. Existing palm/constraint entries must retain their previous behavior. Reuse root-owned synchronization/run scripts; never start another editor against the production project.

## Fixed comparisons after schema freeze

1. Both forward arms with explicit partner palms: keep identical partner, pose, resolved bend, amplitude and cycle count while comparing the slow reference and each selected high-level tempo. Measure actual wrist motion in the requested axis, including palm-normal when applicable.
2. A head curve with the arms held unchanged: compare the same tempo selections using a fixed smaller head amplitude. Test one pitch and one yaw direction where supported. Head movement must not change the held palms or arms.
3. Rhythm transitions: idle → preparation → curve → idle; held pose → new curve → hold; interrupt an active curve between extrema; switch tempo while an active request is being replaced. Keep pre/post samples and cancellation reason. Never silently reset the arms to idle to make a transition pass.
4. Intent binding: one fixed public planning output, frozen before playback, must pass the real parser/Bridge/Controller into the selected rhythm. Preserve original output and resolved plan; no hand-edited timing presented as model output. Invalid plans or infeasible constraints retain truthful feedback.

Use the same fixture/pose/target for each pair. Do not select the smoothest seed or reroll the plan. This route is procedural execution; no new language model or ARDY trajectory is required for the cadence comparison.

## Independent measurements

- Read actual bone quaternions and positions after VRM. For wrists, measure hand rotation relative to the actual lower arm, with an independent anatomical axis and captured neutral frame. For the head, report head-relative-parent motion and character-space motion separately, so retained Animator chest/neck motion is distinguishable. The solver's commanded angle is a diagnostic reference, never the measured answer.
- Preserve actual game-time and wall-time stamps. Unwrap the small signed angle and report same-direction zero-crossing intervals, extrema, active duration, measured frequency, amplitude and completed cycles. Do not estimate frequency from `cycles / seconds` alone.
- Compute finite-difference angular speed and acceleration using actual nonuniform intervals. Record interval distribution and angular precision; tiny intervals can amplify floating-point noise. Keep raw range, percentiles, and explicitly labelled fixed-window derivative estimates if needed. Freeze that analysis window before comparison; do not smooth or retime the executed poses.
- Inspect preparation/acting, acting/returning, stop and replacement boundaries separately: adjacent rotation change, speed and acceleration before/after, endpoint residual and latency. A completed curve should be measured back at its intended neutral/held offset. New continuity limits require the implementation contract; do not invent a naturalness pass threshold.
- Continue every-frame numeric gates with saved samples capped at 80 Hz plus phase transitions. Report samples per cycle and longest gap. A render-heavy run with insufficient temporal resolution must be labelled inadequate for derivative acceptance rather than passed on a reconstructed smooth curve. If needed, use separate actual-loop measurement and rendering runs with identical fixed plans; neither uses manual simulation.

## Preserved constraints and bounds

Keep existing actual direction ≤5 degrees, elbow error ≤4 degrees, palm tracking ≤5 degrees and engineered wrist-swing bound. Tempo may change wrist/head motion but must not silently change explicit arm/bend/palm constraints. Explicit palm forearm roll remains allowed; test shoulder/elbow/wrist geometry, not an obsolete zero-forearm-rotation condition. Compare uncontrolled root, torso, legs, shoulders and expression channels with the synchronized real Animator control.

Check geometry through the full faster curve: feasible endpoints alone do not establish feasible intermediate positions. If frequency and amplitude violate a frozen derivative or geometry bound, expect explicit rejection/limited-plan feedback according to the new contract, not hidden pose substitution.

## Avoid redundant reruns

Do not repeat the entire missing-finger, scale, partner-fallback, 30-second TTL or full legacy head/resource suite if those paths and source files remain unchanged. Retain their hashed archives and run focused regression where the rhythm change touches preparation, curve integration, return or replacement. Verify unchanged local resource hashes and a small real playback smoke check when the common Player path changes.

## Output

Proposed archive: `Server/ARDY/runtime/tempo-execution-v1/`; compact acceptance under `Tools/MotionAdapter/reports/tempo-execution-v1/`. Store each fixed original intent/resolved plan, source/model/resource hashes, per-case actual phase metrics, bounded samples and failure facts. Render only the principal wrist reference/new pair and one head comparison as real timed PNG/GIF previews. Rendering and numerical acceptance stay separately identified.

The result can establish that tempo selection materially changes the actual movement while preserving constraints and continuity. It cannot automatically prove that a faster pace is natural, socially appropriate or comfortable; those remain user/visual judgments.
