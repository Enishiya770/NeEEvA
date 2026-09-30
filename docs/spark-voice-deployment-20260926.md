# Spark voice deployment and visual input repair — 2026-09-26

This records deployment and acceptance work following the latest log review.
The current acceptance state is recorded below; preparation files alone do not
mean a service was migrated.

**Final state at 11:10 JST: TTS, ASR and character RVC are all deployed,
validated and production-routed to Spark `.163`.** The unified launcher passed
with all three remote routes and no local voice services. Unity, microphone
capture and audio playback remain on the PC; Qwen remains on Spark `.175`.
SoulX/9883 is excluded at the user's request.

## Topology and isolation

- Qwen remains on `192.168.114.175:8888`. Its model and vLLM configuration are unchanged.
- TTS is deployed on `192.168.114.163:9880` in the dedicated
  `neeeva-spark-tts` container under `/home/spark_root_001/neeeva-voice/tts`.
  Existing ARDY/motion services and their environments are unchanged.
- ASR is deployed on `.163:9881` in its separate native environment; character
  RVC is deployed on `.163:9882` in the dedicated `neeeva-spark-vc` container.
- TTS uses the current Windows GPT-SoVITS source, weights, reference recording and
  language resources, with a separate ARM/CUDA dependency environment. Both
  acoustic models were verified as CUDA FP32. This is not a model/voice replacement.
- Deployment-time package access used a temporary SSH tunnel to the PC's existing
  proxy because Spark DNS could not resolve the package repositories. Production
  inference does not depend on that tunnel. The installation tunnel was closed
  and remote port 18880 no longer listened before the final launcher/health check.
- The PC–Spark link currently negotiates at 100 Mbps. Benchmark figures include
  that link. Model transfer speed and live PCM bandwidth are different constraints.

## TTS measurements

`Tools/TTS/benchmark_http.py --extended` warms Japanese, Chinese and English,
then sends four short utterances and two longer utterances. The reference,
streaming mode 3, minimum chunk 12, seed 42 and cut5 splitter are the same for
the direct HTTP comparison. These figures exclude Unity playback, ASR and LLM.

| Direct HTTP measurement | Windows | Spark |
|---|---:|---:|
| Short utterance first PCM range | 0.438–0.665 s | 0.081–0.102 s |
| Long Chinese generation / audio | 12.027 / 11.054 s | 2.022 / 11.248 s |
| Long Japanese generation / audio | 11.107 / 12.395 s | 1.836 / 12.130 s |
| Long Chinese real-time factor | 1.088 | 0.180 |
| Long Japanese real-time factor | 0.896 | 0.151 |

A real-time factor below 1 means generation is faster than playback. Different
CUDA/PyTorch execution paths need not produce bit-identical waveforms or durations.
Valid WAV headers, PCM length and nonzero RMS are verified, but they do not by
themselves establish perceived voice quality or pronunciation accuracy.

The main-scene fixture uses the actual rendered scene, production Unity TTS
transport and muted AudioSource sample progression. It disables microphone,
LLM and memory writes. It measures the TTS request-to-AudioSource interval,
not user speech-end-to-first-audio latency or physical speaker audibility.
It also checks interruption, the immediate successor request, and absence of
late audio restart. Saved scene and quality hashes must remain unchanged.

Windows baseline, with CUDA ASR resident and TTS sharing the 6 GB GTX 1660 Ti
with Unity: four short utterances averaged **3.031 s** to AudioSource progress.
Long Chinese/Japanese real-time factors were **1.842 / 1.777**; each long
utterance rebuffered three times, accumulating **6.368 / 6.797 s** of wait.
The complete six-utterance sample accumulated **15.559 s** rebuffer wait and
**2.224 s** inserted silence. Cancellation and successor playback passed.

Spark main-scene comparison passed, with the local TTS process stopped and the
local CUDA ASR service still resident. Four short utterances averaged **0.229 s**
to AudioSource progress. Long Chinese/Japanese real-time factors were
**0.170 / 0.154**. All six complete comparison utterances had **zero rebuffer
episodes / zero rebuffer wait**; total inserted silence was **0.083 s**, confined
to the first short Chinese utterance. Both long utterances had zero inserted
silence. Interruption, immediate successor playback and no late restart passed;
the saved scene and quality settings were unchanged by the trial.

| Same main-scene trial | Local TTS | Spark TTS |
|---|---:|---:|
| Four short utterances: mean TTS-to-AudioSource | 3.031 s | 0.229 s |
| Long Chinese / Japanese RTF | 1.842 / 1.777 | 0.170 / 0.154 |
| Six utterances: total rebuffer wait | 15.559 s | 0 s |
| Six utterances: inserted silence | 2.224 s | 0.083 s |
| Cancellation + successor + no late restart | Passed | Passed |

These are small descriptive samples with identical texts and client settings,
not a guarantee of end-to-end conversation latency. The formal production TTS
route was switched to Spark after these checks on 2026-09-26 at 10:44 JST.

Evidence:

- `Logs/spark-voice/local-http-extended.json`
- `Logs/spark-voice/spark-http-extended.json`
- `Logs/tts/main-scene-latency-local-extended.json`
- `Logs/tts/main-scene-latency-spark-extended.json`
- `work/spark-voice-migration-20260926/trial-summary.json`
- `Server/TTS/runtime/smoke.json` — real PCM, active disconnect, queued successors.
- `work/spark-voice-migration-20260926/tts-owner-release-review.json` — an independent
  isolated check of exception/cancellation/disconnect cleanup, not an HTTP test.

## Visual input and intent classification

FastLoop now sends the current generation's actual image when it matches the
bound scene observation and passes existing eye-state/freshness/viewpoint checks.
No-image requests remain text-only. The trace records attachment state and
observation identity/age, without dumping image data. There is no additional
model round trip for this fix.

Unity shared-entry integration passed **78 cases / 499 checks**, including PTT
and realtime image binding. Actual serializer/observation transport passed
**197 checks**. Two stale test assumptions were updated to match the existing
production contract: only current observation pixels go onto a continuation
request, and the work-review method now accepts an optional trace parameter.
The tests still verify prior text, execution facts, observation identity and
that serialization does not mutate source history.

The intent prompt clarifies physical head posture versus visual/perceptual
description. Real Qwen still misclassified one Japanese perception denial as
`claim/head.hold`. Two further prompt candidates fixed some cases while causing
other domain/condition regressions; neither candidate was installed. The
remaining semantic limitation is explicit, not hidden by relaxing execution
checks or by claiming perfect classification. See
`work/spark-voice-migration-20260926/actor-intent-candidate-assessment.md`.

Live Qwen image verification passed **3/3** with exported production request
bodies: red-left/blue-right, swapped colors, and no-image control. Responses
correctly tracked the pixels or acknowledged no attached evidence. Request
times were **1.172 / 1.039 / 1.272 s**; these are complete LLM request durations,
not first audio. The no-image response used Japanese despite a Chinese-language
request, which is recorded separately rather than counted as an image-recognition
failure. The 256×128 synthetic inputs do not characterize larger scene image cost.
Evidence is in `work/spark-voice-migration-20260926/vision-live/assessment.md`.

## Routing and rollback

The selected voice routes live in ignored local configuration
`Server/RuntimeLogs/voice_routes.json`. `start_voice.cmd` reads those choices,
starts the selected Spark services idempotently, and starts only the remaining
local services. An absent route file preserves the previous all-local behavior.
Invalid routing fails before starting local services. Five isolated launcher
tests passed under Windows PowerShell 5. The TTS switcher additionally requires
ready CUDA FP32 health, the accepted release hash, and the exact reference path
before writing the remote route.

Close this project in Unity before changing its saved route:

```powershell
# Current production routes already point to Spark. To roll all voices back:
Tools/TTS/set-tts-route.ps1 -Profile local
Tools/ASR/set-asr-route.ps1 -Profile local
Tools/VC/set-vc-route.ps1 -Profile local
./start_voice.cmd
```

Each switcher can also change one service independently and supports `-CheckOnly`.
The production TTS and VC references are distinct absolute Linux paths under
their own releases. VC auto-start in ChatSample is disabled for the remote route,
so a remote failure does not accidentally launch the old PC service. The Windows
installations, models and CPU-thread setting remain available for rollback.
Backups of the saved scene and routing file are under
`work/spark-voice-migration-20260926/{routes-*,asr-routes-*,vc-routes-*}`.

Standalone Spark TTS management:

```powershell
Tools/TTS/manage-spark-tts.ps1 -Action status
Tools/TTS/manage-spark-tts.ps1 -Action start
Tools/TTS/manage-spark-tts.ps1 -Action logs
Tools/TTS/manage-spark-tts.ps1 -Action stop
```

The health endpoint's disconnect/lifecycle counters are transport diagnostics;
normal response shutdown also contributes, so they are not counts of successful
utterances or user interruptions. Use the explicit trial evidence for those.

## ASR acceptance and production route

ASR preparation preserves the existing SenseVoice/FSMN-VAD/CAM++ configuration,
final utterance policy, speaker database and Japanese dictionary. An offline
1.082 GB archive has 85 individually hash-verified members. Preparation and
management tools are under `Tools/ASR`; the deployed environment is independently
installed with ARM64 PyTorch 2.11/CUDA 13.0 and the aligned FunASR dependencies.
No Windows virtual environment is copied to ARM.

ASR is deployed and production-routed to `192.168.114.163:9881`, with WebSocket
streaming at `ws://192.168.114.163:9881/stream/asr`. The actual SenseVoice model
is on CUDA; FSMN-VAD, speaker identification and pitch retain their existing CPU
policy. A fixed
14.875 s Japanese synthetic fixture uses the OS Haruka voice (not the character
voice), avoiding self-echo rejection and use of private microphone recordings.
HTTP final text and successor text matched the Windows results exactly, including
punctuation. This validates migration consistency, not improved recognition accuracy.

| ASR measurement, same synthetic input | Windows | Spark |
|---|---:|---:|
| Fresh final request, direct HTTP | 1.352 s | 0.554 s |
| Median streaming recognition computation | 262.7 ms | 57.4 ms |
| Maximum measured streaming lag | 225 ms | 28.4 ms |
| Fresh final request, actual Unity component | 1.896 s | 0.517 s |

The direct streaming run produced 16 partials on each service. First partial
arrived at 1.157 / 0.956 s, including the approximately 0.8 s collection gate.
The Unity trial used different chunk timing and produced 14 partials on each;
it passed **113 checks per endpoint**, including HTTP final recognition, paced
WebSocket input, cancellation, absence of stale partial callbacks and a usable
successor. Repeated/successor requests can hit the recognizer cache and are kept
separate from fresh-request timings. No speaker learning, microphone capture,
memory writes or saved-scene changes occurred in these trials.

The speaker database remained byte-identical (10 profiles, 23 enrolled voiceprints);
the song library remained empty. The production route was changed at 10:59 JST,
then the previous local ASR listener was stopped. The unified launcher reused
the already healthy Spark ASR process. Rollback is
`Tools/ASR/set-asr-route.ps1 -Profile local`, followed by `start_voice.cmd`.

Evidence: `Logs/asr/unity-transport-local-baseline.json`,
`Logs/asr/unity-transport-spark-verified.json`, and
`work/spark-voice-migration-20260926/asr/comparison.json`.

## RVC and excluded SoulX service

The user confirmed that SoulX-Singer SVS is not currently wanted. Port 9883 is
excluded from this migration; its code/assets were not removed. The main scene
already has `m_EnableSingingVoiceSynthesis=0` and `m_AutoStartSVS=0`.

The existing role RVC conversion backend is deployed at `192.168.114.163:9882`.
Its actual vendor source, character checkpoint/index, HuBERT
and RMVPE were found. Seed-VC fallback assets were not found, so readiness must
specifically verify the working RVC backend rather than imply all fallback
backends exist. Remote reference paths are updated separately from TTS.

RVC Unity acceptance and final route switching are complete. An existing persistent-worker
cancellation gap was reproduced and fixed before packaging: request identity is
now registered before queueing, cancellation is scoped to that identity, cancelled
jobs cannot enter fallback conversion or publish an old result, and killed workers
are reaped. Independent review and real cancellation/next-request checks are gates
before changing the saved Unity route. All 24 focused cancellation regression tests
passed. Real HTTP tests verified wrong-ID cancellation has no effect, correct-ID
cancellation returns true, the old request terminates with HTTP 499 (0.428 s),
and a subsequent request returns valid audio. RVC, HuBERT and RMVPE actual parameters
are all CUDA FP16, with no CPU fallback in these conversions.

Same-source conversion measured **1.180 → 0.379 s** for a 4 s excerpt and
**1.554 → 0.536 s** for the complete 12.19 s source, Windows → Spark. The output
durations were 3.98 / 12.18 s on both. These inputs were existing project vocal
material, used only between the PC and the user's Spark. Numeric PCM validation
does not establish listening quality. The successor after killing an active worker
took **3.518 s**, including a fresh worker load; it is not a warm-inference result.
The installed idle-worker timeout remains **90 s**, so an idle service can incur
a cold start. The local Windows installation remains available for rollback.

Evidence: `Server/VC/runtime/spark/report.json`,
`Server/VC/runtime/local/report.json`, `Server/VC/deployment-lock.json`.

The Unity transport trial passed all six stages: conversion, decoded AudioSource
natural completion, playback cancellation, in-flight conversion cancellation,
successor conversion and successor playback. The independent synthetic Haruka
input produced 14.86 s of non-silent audio; the initial conversion took 0.760 s
and the successor after cancellation took 3.780 s including cold loading. Saved
scene and quality hashes remained unchanged during the trial. This tests actual
Unity WAV decoding/playback transport, **not the full ChatSample singing goal,
LLM authorization, autonomous new-song generation or human listening quality**.
Evidence: `Logs/vc/unity-transport-spark.json`.

The VC route was changed at 11:08 JST, including its separate Linux reference
path and disabled local auto-start. The task-started local VC listener was then
stopped after verifying its identity and absence of worker children. The final
unified startup reused all three remote services and started no local services.

A proposed change to extend only Spark's worker idle timeout to one hour was
not deployed: an upload/restart/test combination was rejected by automatic
approval with only `blocked by policy` as its reason. It was not retried. The
unapplied local proposal was reverted to match the verified 90 s deployment.

## Coexistence and final verification

One simultaneous request to each service passed with public synthetic input:
TTS first PCM 0.141 s / total 1.506 s for 7.794 s audio; ASR final 0.575 s;
RVC total 0.754 s for 7.98 s audio, without CPU fallback. These are a coexistence
smoke check, not a load-capacity test or a guaranteed full conversational latency.
See `Logs/spark-voice/coexistence.json`.

The final read-only snapshot confirmed TTS, ASR, VC, existing ARDY and Qwen
endpoints all returned HTTP 200; no local 9880/9881/9882 listener remained.
With Unity closed, the PC GPU reported 0 MiB used / 6144 MiB total. This is an
idle snapshot, not the memory footprint while playing the scene. Spark `.163`
reported approximately 68.4 GiB available system memory. The saved quality
settings hash remained unchanged throughout; the main scene changed only through
the explicit service route setters. Evidence: `Logs/spark-voice/final-deployment.json`.

## Disabled embedding configuration

The main scene has semantic recall disabled (`m_EnableSemanticRecall=0`). Its
retained `bge-m3` embedding URL at `127.0.0.1:8090` has no local listener and is
not an enabled service left behind by this voice migration. Neither Spark has
that BGE service on 8090; the Qwen embedding-capable service on 8891 is a different
model and was not substituted. Re-enabling semantic recall requires a separate
decision about the model and vector cache: the current cache identity includes
the endpoint URL, so even a same-model address change can invalidate that cache.
No memory index was rebuilt. See the white-listed read-only inventory at
`work/spark-voice-migration-20260926/asr/embedding-inventory.json`.
