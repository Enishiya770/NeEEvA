# Native motion development validation, 2026-09-10

The selected finetune-seed0 adapter improves necessary geometric observations across the fixed development set. Its checkpoint remains locked; these observations do not establish complete semantic compliance or natural motion for every description.

The fixed plan selected all 40 validation configurations, one preselected template each, covering 29 families. Each condition used seeds 0 and 1 with either no initial history or the same captured 16-frame Animator history projected onto the source skeleton. Teacher, old adapter, and selected adapter produced 480 six-second trajectories. The exact conditions, sources and output paths are in `Server/ARDY/runtime/generalization-validation-v1/report.json`.

| Condition | Cold required height observations reached | Grounded16 required height observations reached | Cold first reach median | Grounded16 first reach median |
|---|---:|---:|---:|---:|
| Old adapter | 16 / 28 | 15 / 28 | 0.30 s | 0.60 s |
| Selected adapter | 27 / 28 | 23 / 28 | 0.50 s | 1.75 s |
| Verified teacher | 27 / 28 | 23 / 28 | 0.50 s | 1.25 s |

These denominators count explicit required height observations, including separately labeled alternating-side targets. They do not count complete successful gestures. First-reach medians exclude unreached targets: the old adapter's smaller timing median must not be interpreted as better general responsiveness. Palms, exact repetitions, direction, pauses and naturalness remain manual. See `native-val-height-summary-v1.json`.

An independent validation ablation generated 160 additional trajectories using the selected adapter and only the last 4 or 8 projected initial frames. Candidate condition bytes matched the original validation exactly, the two seeds were unchanged, and continuation histories stayed 16. The corresponding original 80 grounded16 trajectories were reused.

| Initial history | Required heights reached | Reached within 1 s | Median first reach, reached only | Median initial maximum global bone jump | P95 trajectory maximum window-seam jump |
|---|---:|---:|---:|---:|---:|
| 4 frames | 25 / 28 | 6 / 28 | 1.80 s | 12.35 deg | 30.83 deg |
| 8 frames | 24 / 28 | 8 / 28 | 1.75 s | 11.95 deg | 28.84 deg |
| 16 frames | 23 / 28 | 9 / 28 | 1.75 s | 11.41 deg | 26.05 deg |

Across the same 23 targets reached at both lengths, the paired first-reach difference has median zero for either shorter length. Shorter history provides no demonstrated general responsiveness gain; production keeps the default 16. The continuity figures are unblended source-space proxies, not avatar comfort ratings. `history-length-validation-v1.json` retains every trajectory, gained/missed observation, paired timing difference and speed measurement. All 19 CPU label/measurement/continuity tests passed.

A separate seen-diagnostic recheck generated 16 candidate trajectories for the previously investigated four descriptions. Seven of eight overhead cases had at least one simultaneous both-wrists-above-head event, versus zero of eight for the old adapter and seven of eight for the original teacher. These descriptions were already studied and must not be presented as unseen test generalization. Some events were brief or delayed. The explanatory descriptions use appropriate descriptive geometry and manual review, with no overhead success gate.

Ten actual NEVA VRM clips were rendered using the unchanged production mapping, identical captured Animator baseline, and a single camera fitted to their combined bounds. Every frame used BakeMesh. They cover old/candidate actual overhead and explanation at seeds 0/1, plus candidate validation 00904 at both seeds. All original native clip hashes were unchanged. Five paired contact sheets and 6.5-second GIFs are in `Logs/ardy-generalization`; `candidate-vrm-review-v1.json` and its artifact manifest record the evidence. The candidate visibly improves bilateral movement in some clips, while another explanatory sample remains near idle and an overhead sample remains asymmetric. No full-generalization claim follows from this review.

The final supported test is separately fixed to all 64 supported upper-body descriptions, seeds 0/1, teacher/old/selected candidate, and grounded16 history: 384 trajectories. The one authorized final audit has since completed all 384 trajectories; see `native-final-supported-audit-v1.json` and `native-final-supported-audit-v1.md`. The sixteen boundary descriptions are excluded from native generation, and no final result is used to tune this locked candidate.
