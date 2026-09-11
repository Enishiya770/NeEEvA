# Actual Unity palm acceptance

**passed** — 42 fixture cases, 3,262,379 numeric checks and 6,304 actual sampled-loop frames; 308 verified PNGs. Unity 2022.3.22f1.

Two real VRM assets run with real Animator updates: disabled-VRM Humanoid and actual ControlRig, root yaw25/65deg and scales0.8/1/1.2. No manual Tick, network service, audio playback or saved production scene.

| Positive actual metric | Maximum |
|---|---:|
| Arm direction error | 0.019782 deg |
| Elbow bend error | 0.000366 deg |
| Independent palm tracking error | 0.249446 deg |
| Actual wrist swing | 79.739006 deg |
| Saved preparing-phase wrist swing | 79.689018 deg |
| Return-to-Animator error | 0.000000 deg |

The unchanged first public Qwen XML is re-parsed and sent through the real Chat event → Bridge → Controller → Player. Omitted elbows are solved at entry, while explicit angles remain fixed. Every public positive fixture must complete its measured curve.

Coverage includes side/elevated partner targets, bound-target priority, start-time target snapshot, Camera and explicitly labelled character-forward fallback, current/keep, up/down and inward/outward palms, legacy keep, missing-finger/scale rejection, fixed-angle limits, retained failure feedback and Bind clearing.

| Fixture | Avatar / branch | Cases | Uncontrolled bone error | Expression error |
|---|---|---:|---:|---:|
| fixture-0 | NEVA.vrm / Humanoid with disabled VRM and real Animator | 12 | 0.000000 deg | 0.000000 |
| fixture-1 | NeEEvA.vrm / Humanoid with disabled VRM and real Animator | 10 | 0.000000 deg | 0.000000 |
| fixture-2 | NEVA.vrm / Real ControlRig with real Animator and VRM LateUpdate | 10 | 0.000000 deg | 0.000000 |
| fixture-3 | NeEEvA.vrm / Real ControlRig with real Animator and VRM LateUpdate | 10 | 0.000000 deg | 0.000000 |

Fixed8 is a measured reachable low-margin positive; fixed0 is an explicit unreachable negative. The first failed run incorrectly assumed fixed8 could not reach. The second exposed a production JSON-null bug; it is fixed and directly verified. Both complete failed archives remain intact.

Full raw report: `Server/ARDY/runtime/palm-execution-v1/full-pass-v1/constraint-regression.json`  
SHA256: `ae60a49f995314bb558e0dfbf4cf96ee50c9f2356ce2c71daa67d679a6a0c63a`

`acceptance.json` retains every case metric, saved phase ranges, exact plan/feedback, source hashes, and archive paths plus hashes for each PNG and pose stamp. Large sample arrays remain in the hashed raw report.

This is geometric and lifecycle evidence. It does not automatically establish naturalness, comfort, collision-free clothing/body motion or broad Qwen planning success. Preview image coverage is separate from actual bone measurements.
