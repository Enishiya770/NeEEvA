# Frozen native motion final audit, 2026-09-10

The selected adapter improves some required geometric targets on the frozen unseen descriptions, while retaining a substantial gap to the verified teacher condition. The results do not establish reliable completion, prompt compliance or naturalness for arbitrary gestures. No checkpoint or generation parameter is changed from these results.

The audit completed all 384 trajectories once: 64 supported descriptions from 32 semantic groups, seeds 0 and 1, verified teacher / old adapter / selected adapter, identical captured grounded16 initial history and 16-frame continuation history. Each trajectory contains 120 frames in three 40-frame windows at 20 FPS. All 384 clip / NPZ / per-frame measurement triplets are present. The sixteen boundary descriptions were excluded from native generation.

| Necessary world-Y height observations | Old adapter | Selected adapter | Verified teacher |
|---|---:|---:|---:|
| At least one qualifying frame | 16 / 36 | 21 / 36 | 32 / 36 |
| First qualification within 1 second | 6 / 36 | 4 / 36 | 4 / 36 |
| First qualification within 2 seconds | 6 / 36 | 5 / 36 | 9 / 36 |
| Median first qualification, reached only | 3.40 s | 4.05 s | 3.25 s |
| Median longest continuous qualification, reached only | 1.85 s | 1.15 s | 2.00 s |

The denominator is 36 explicitly labeled height observations per condition, rather than the 128 trajectories. Most descriptions have other observations or require manual review. Bilateral gates require both wrists to satisfy the condition in the same frame. A one-frame event can meet this necessary observation without completing a held or repeated action. First-reach and hold medians exclude unreached observations, so they cannot establish whole-set responsiveness.

Compared pairwise with the old adapter, the candidate newly reaches nine required height observations but loses four that the old adapter reached. Among twelve observations reached by both, the median first-reach difference is zero seconds. Compared with the teacher, the candidate gains two observations and misses thirteen teacher-reached observations; among nineteen common-reached observations, its first-reach difference has median +0.15 seconds. All individual cases, including these regressions, remain in the JSON report.

The candidate's offline ARDY generation timing is 109.53 ms median per 40-frame window (p95 124.58 ms), or 328.84 ms median for all three windows (p95 360.78 ms). The complete audit took 177.84 seconds. These timings exclude Qwen feature encoding, network/service queues, Unity playback and speech. They measure motion computation, while the several-second target times above measure progression within the generated motion. Condition execution order was fixed, so the per-condition timings are descriptive rather than a controlled speed comparison.

The locked checkpoint SHA256 is `c1006ee8280f8314472acc3b19302ea7823de55dae3947e0b0fa34b6d7dfbeab`; backend SHA256 is `8c6f1eef11cf3dcece283f382234b0ca4c46c1ade3efb02d0915f0135c1ca642`. Plan SHA256 is `fb1677b5fe2a9d8287b6e3f791598109dfb24a9b8a72c77742f7ab6472d931f1`. Frozen source hashes were unchanged after execution.

Evidence:

- `native-final-supported-audit-v1.json`: condition/group summaries, paired gains/losses, onset/hold and generation times.
- `native-motion-final-test-bad7ec319c961c07.json`: one-use audit marker, status complete with 384 trajectories.
- `Server/ARDY/runtime/generalization-final-supported-v1/report.json`: complete per-trajectory results and input provenance.
- `Server/ARDY/runtime/generalization-final-supported-v1/render-manifest.json`: every final native clip, available for review without regeneration.

Palm orientation, exact direction/extension, event count/order, naturalness and actual target-avatar collision remain manual. No aggregate semantic or naturalness success rate is reported. The audit loaded no language model, started no service, and released its GPU process after completion.
