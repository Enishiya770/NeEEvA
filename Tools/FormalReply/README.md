# Formal reply prompt and Semantic IR checks

`Run-Checks.ps1` runs pure .NET 10 regressions without starting Unity, using the project's Newtonsoft.Json assembly. It checks split-packet JSON records, silent thought, language transitions, rejection, truncation, cancellation, exact prompt projection and preservation of current user/image/facts. `ProjectionRegression.cs` supplies a small host seam; native TextAsset loading and Unity PlayMode are not simulated.

```powershell
./Tools/FormalReply/Run-Checks.ps1
```

`Run-Benchmark.ps1` is an explicit inference-only experiment against `127.0.0.1:18080`, model `qwen3.6-35b-a3b`. It runs eight synthetic text requests, reverses pair order, retains all results, and never dispatches actions or TTS. Do not interpret its first public candidate as audible onset. Leading system sections are merged for this history-free fixture because the current service rejects multiple system messages; this is not production message layout. Shared server load/cache state is uncontrolled.

```powershell
./Tools/FormalReply/Run-Benchmark.ps1
```

Runtime changes:

- `ChatQW.FormalPrompt.cs` projects only the reviewed three-file persona/language/behavior configuration, and only at the formal streaming boundary. Original prompt assets and stored history remain intact. Unknown/modified behavior or language rules keep the legacy prompt. The SHA-256 checks normalize only line endings and outer whitespace; update their expected values only after reviewing and updating the compact rules.
- General formal/tool/continuation replies use concise common/transport rules. Tool permissions, identity selection, singing rules, memory evidence, scheduling and asynchronous-result contracts remain; obsolete fenced examples of naked prose are omitted. Active skills, current user, current image, memories, pending work and feedback use their existing paths.
- `CommittedSpatialReply` uses `RoleDecision SpeechStream v1`: `{"segments":[{"kind":"thought","text":"..."},{"kind":"speech","language":"zh","text":"..."}]}`. The server receives a JSON Schema. Each complete record is validated and compiled to the existing thought/lang/speech/say envelope; the compiler cannot issue tools. The whole JSON root and transport must finish before completion is accepted. Empty `segments` means deliberate silence.
- Thought is a non-TTS speech segment. Brief psychological activity is encouraged; it is neither removed nor promoted to public speech. No runtime truncation silently discards it.
- Existing room fact review, speech gates, cancellation and receipt tracking remain authoritative. Early record decoding does not bypass review or guarantee early AudioSource playback.
- The old `m_EnableSemanticRoleDecision` experiment remains separate and disabled as before. `m_CompactFormalRules` turns off both new projections; `m_StreamCommittedRoleDecision` disables only the new spatial speech format. Both new switches default to enabled for the reviewed profile.

Observe `[LLM/FormalPromptProfile]` for applied/skipped profile, system estimate and request bytes; `[LLM/Performance]` retains actual provider token usage and timing. `decision.first_speech_record` marks complete record validation, not playback acceptance. Existing TTS/AudioSource traces remain necessary for first-audio measurement.

The current local endpoint's multi-system HTTP 400 must be distinguished from protocol/output problems. The latest read Unity log still showed earlier successful formal responses. This change does not reorder production feedback or deploy a remote chat template; end-to-end Unity timing requires a working service configuration and a new Play session.
