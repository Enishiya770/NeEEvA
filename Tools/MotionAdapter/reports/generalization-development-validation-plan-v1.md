# Fixed development validation plan

Bound to `extension-v1.jsonl`, records SHA256 `484a4c8b7d493e763ba26f4070caa3541c4232ae38c7ad8de50fe4b29e03d291`. This is a validation plan; no new motion has been generated.

Select template 0 from every validation configuration: **40 descriptions, 40 semantic groups and all 29 families**. Each condition uses seeds 0/1 and both cold start and the recorded grounded Animator history: 160 trajectories per condition, or 480 for teacher + old adapter + one candidate. Each trajectory is three 40-frame windows at 20 FPS.

The old checkpoint, source manifest and initial history hashes are stored in the JSON plan. Candidate checkpoint hashes and feature bundles must be fixed before execution. The final test set is not used in this plan.

Explicit wrist-height observations are necessary checks only. Face level uses the head-joint proxy; waist targets use a weaker below-chest observation and require manual confirmation. Both-hand criteria require the same frame, while alternating targets are measured independently. Head/torso excursions are descriptive. Palm direction, event order, exact repetitions, holds, contacts and actual-avatar naturalness remain manual; no automatic semantic success rate is produced.

Preserve all fixed seeds and failures, compare every family, and choose candidates from development validation alone. Do not substitute a favorable seed or consult final-test motion results. The previous four diagnostic descriptions remain seen examples.

| ID | Family | Side | Structured configuration |
|---|---|---|---|
| expanded-v1-00040 | raise | left | action=raise, target=chest, direction=diagonal, ending=repeat |
| expanded-v1-00064 | wave | left | action=wave, target=waist, count=1 |
| expanded-v1-00128 | wave | left | action=wave, target=high, count=4 |
| expanded-v1-00272 | circle | left | action=circle, direction=outward, count=3 |
| expanded-v1-00320 | forearm_rotation | left | action=forearm_rotation, orientation=down |
| expanded-v1-00336 | beckon | left | action=beckon, target=gesture_specific |
| expanded-v1-00360 | offer | left | action=offer, target=gesture_specific |
| expanded-v1-00368 | stop | left | action=stop, target=gesture_specific |
| expanded-v1-00408 | relax | left | action=relax, target=gesture_specific |
| expanded-v1-00520 | wave | right | action=wave, target=chest, count=4 |
| expanded-v1-00592 | point | right | action=point, direction=forward, tempo=briskly, target=chest |
| expanded-v1-00624 | point | right | action=point, direction=across, tempo=briskly, target=chest |
| expanded-v1-00632 | circle | right | action=circle, direction=forward, count=2 |
| expanded-v1-00648 | circle | right | action=circle, direction=backward, count=2 |
| expanded-v1-00800 | figure-eight | right | action=figure-eight, target=gesture_specific |
| expanded-v1-00808 | emphasize | right | action=emphasize, target=gesture_specific |
| expanded-v1-00832 | raise | both | action=raise, target=chest, direction=forward, ending=repeat |
| expanded-v1-00904 | raise | both | action=raise, target=overhead, direction=diagonal, ending=hold |
| expanded-v1-01048 | point | both | action=point, direction=outward, tempo=briskly, target=chest |
| expanded-v1-01064 | point | both | action=point, direction=across, tempo=briskly, target=chest |
| expanded-v1-01136 | elbow_bend | both | action=elbow_bend, target=waist, count=2 |
| expanded-v1-01200 | dismiss | both | action=dismiss, target=gesture_specific |
| expanded-v1-01208 | present | both | action=present, target=gesture_specific |
| expanded-v1-01232 | low-sweep | both | action=low-sweep, target=gesture_specific |
| expanded-v1-01256 | self-indicate | both | action=self-indicate, target=gesture_specific |
| expanded-v1-01288 | bilateral_spread | both | action=spread, target=low, count=1 |
| expanded-v1-01344 | alternating_arms | both | action=alternating_raise, target=face, count=3, order=right-first |
| expanded-v1-01376 | nod | none | action=nod, count=3, pace=slow |
| expanded-v1-01424 | shake | both | action=shake, count=2, amplitude=small |
| expanded-v1-01472 | head_tilt | left | action=tilt, count=1, amplitude=small |
| expanded-v1-01528 | head_look | up | action=look, ending=hold |
| expanded-v1-01624 | torso_lean | backward | action=lean, count=2, amplitude=small |
| expanded-v1-01648 | torso_twist | left | action=twist, count=3 |
| expanded-v1-01688 | bow | none | action=bow, amplitude=deep |
| expanded-v1-01704 | shrug | both | action=shrug, count=2 |
| expanded-v1-01728 | shoulder_roll | both | action=roll, count=2, direction=forward |
| expanded-v1-01832 | combination | mixed | action=combination, temporal=simultaneous, configuration=right-wave-left-offer |
| expanded-v1-01856 | combination | mixed | action=combination, temporal=simultaneous, configuration=both-elbows-head-up |
| expanded-v1-01888 | sequence | mixed | action=sequence, temporal=ordered_sequence, configuration=shrug-then-right-offer |
| expanded-v1-01928 | sequence | mixed | action=sequence, temporal=ordered_sequence, configuration=left-offer-then-right |

CPU verification: 15 fixture tests passed; all 40 exported assessments exactly match `assessment_for`. No GPU run, held-out motion generation or service startup was performed.
