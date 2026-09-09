# Unified singing material protocol

## Responsibility

The role chooses material, source attribution, boundaries, musical targets and
whether to ask. Unity validates concrete references and executes the choice;
it never substitutes the last successful recording or an acoustically similar
library item. These interfaces do not authorize automatic deletion.

## Model-facing calls

```xml
<sing refs="clip:exact-identity"/>
<sing refs="clip:first,clip:second" pitch_plan="keep,A3"/>
<sing refs="clip:pending" range="expanded"/>
<sing refs="clip:exact-identity" start_seconds="3.2" end_seconds="12.1"/>
<clip_confirm ref="clip:exact-identity"/>
<clip_revise ref="clip:exact-identity" range="clean"/>
<clip_drop ref="clip:exact-identity"/>
<song_remember source_ref="clip:exact-identity" title=""/>
<sing refs="song:123456abcdef" lyrics="selected stored passage"/>
```

Examples contain placeholders, not usable references. Clip IDs are opaque GUIDs,
created when a retained recording becomes a candidate/phrase. Confirmation and
revision keep the same ID; a same-recording continuation preserves it too.
They are session material IDs, not a new on-disk song-library database. Library
IDs remain separate, and saving does not change the in-session clip identity.

`sing` does not accept `source`, `mode` or `order`. Its ordered `refs` are resolved
to concrete stable renderer IDs before asynchronous generation. Unknown refs
reject the request, including unknown entries in a multi-selection. Repeating a
ref explicitly repeats that clip. Only one singing request executes per reply.

`current` is the default range: use the current edited audio, not the original
clean source. Explicit `clean`/`expanded` selects an original range as the new
current version. Explicit start/end seconds share the **original recording**
clock, must occur together, and cannot be combined with a named range. Current
implementation permits these cuts within the retained expanded range for which
audio/pitch evidence exists. Original recordings and source variants survive.
Multi-clip requests use current versions; edit individual ranges first.

Source confirmation and playability are separate. A pending source is not
confirmed merely by selection or completed playback. Singing and revision do
not require source confirmation. Legacy `confirm_user=true` remains an explicit
attribution for the selected refs. Evidence-only material needs an explicit
range before it can play; otherwise it returns actionable facts. A standalone
`clip_confirm` can confirm provenance without making audio playable or playing
anything. Range preparation can persist even if a later rendering/validation
step fails; failures must not claim playback. In a multi-selection, any earlier
completed preparation remains and is reported if later preparation fails.

The existing SVC, per-segment pitch plan, streaming, interruption and output-F0
measurement implementations are reused. Legacy tags remain executable adapters,
but new prompting uses sing/clip calls. Error codes are not tool names.

## Current limits

- Library sing accepts one `song:ID`, lyrics and reason. No mixing library songs
  with session clips, and no library musical/range parameters: unsupported
  parameters fail rather than disappearing. Omitting lyrics selects all stored
  segments of that entry, not necessarily a complete real-world song.
- Continuous follow-along remains the separate `hum_back mode="follow"` action.
- The internal candidate/stable containers and library persistence format are
  retained behind the adapter. Historical bad catalog entries are not repaired
  or deleted by this migration.
- A failed singing/material operation keeps the skill's repair context available
  for up to ten minutes; this is contextual information, not an automatic retry
  or a decision to sing. Explicit access restrictions remain effective.
- No recognizer/SVC service change or restart is required. This change does not
  establish improved ASR accuracy, pitch accuracy or conversational first-audio
  latency; these require a fresh live test.

## Regression coverage

### 2026-09-08: speech channels, per-clip pitch and boundary facts

- Initial implementation (superseded by the plain-speech revision below):
  primary ChatQW replies explicitly addressed audible text with `<say>...</say>`.
  Unmarked prose and optional `<thought>...</thought>` stayed off the TTS/subtitle
  path. Top-level actions remain independent. Quoted-code action examples and
  actions inside thought/speech blocks are not dispatched. Only primary reply
  callbacks are routed; private listening/tick classifiers retain their own
  interfaces. Raw assistant history retains the role's chosen channel markers.
- Routing is request-local and supports tags split across arbitrary chunks. It
  streams say text immediately, without waiting for the full reply or classifying
  its words. A missing speech marker does not fall back to reading analysis.
  `finish_reason=length` produces a system error notice and a factual history
  entry; truncated responses dispatch no tool actions. Already spoken words
  cannot be revoked. No automatic retry or forced singing is introduced.
- Pitch state no longer clears when the next selected set includes a new clip.
  Only actually played entries update; clip-specific drop/revision invalidation
  still applies. Existing integer-semitone execution and F0 verification remain.
- Clip facts show current version and original-time bounds, plus exactly how
  much expanded adds at the head/tail. Mixed-turn evidence explicitly describes
  a completed recording rather than a future singing plan. These are information
  changes, not keyword-based selection or mandatory confirmation.
- ASR exact-window reuse is bounded to 128 results/90 seconds, keyed by model,
  language, shape/dtype and SHA-256 of all input bytes. It reuses only identical
  recognizer work and returns independent result copies. No audio is shortened,
  no pitch precision/threshold is reduced, and no full semantic decision is
  released early. Health exposes `asr_window_cache_schema=1` after ASR restart.
  The multi-second full-F0 bottleneck is not eliminated by this limited cache.

Validation: `Logs/unity-output-channels-pitch-final.log` completed the full Unity
regression suite, exit 0. Tests include every streaming split size, private/tool
isolation, truncation metadata, real pitch-state commits for A then B then B,
unheard-C rejection, and the 4.40s head-only expansion fact. The 35 Python tests
in window cache, scheduler, timing, dispatch and mixed-turn suites passed.

Small read-only live LLM probes returned channel-marked greetings and a two-clip
sing call. An initial ambiguous-current probe spent its output budget on private
analysis; this prompted truncation reporting and explicit current-range facts.
With current=clean and head-only expansion stated, a follow-up probe returned
an explicit source confirmation and current-clip sing request. These probes did
not execute audio/tools, and do not establish production conversation latency or
guarantee every future semantic choice. Re-Play Unity for client changes; restart
ASR for the cache. LLM/TTS service restarts are not required.

### 2026-09-08: short-clean recording admission

Before the final role perception is built, normal confirmed captures still use
the existing commit path. Unrepresented current recordings with acoustic
singing/uncertain evidence (p >= 0.52) or semantic/review support are retained as
pending `clip` evidence independently of that commit's playback-length gate or
an early speech veto. This does not classify the recording as user singing,
select expanded audio, or schedule playback. The role may select a range,
confirm its source, ask, or decline. Ordinary low-band speech without semantic
support is not automatically registered by this fallback.

The fallback reads the current recording snapshot, never the recent playable
cache, which can belong to an older recording. Existing clip identities are
preserved; publication is scoped to the recording and does not resurrect a
dropped clip. Unknown clip requests return actual current references and explain
that a library ID is not a recording ID, without automatic substitution.

`Logs/unity-clip-evidence-admission-verified.log` passed the complete suite with
Unity exit code 0. The added test uses synthetic WAV/pitch data with the actual
9/8 dimensions (19.12s raw, 2.74s clean, 10.04s expanded), runs the real
capture/cache and pre-perception entry points without a pre-created candidate,
and verifies both ordinary and speech-veto paths. It checks visible evidence,
explicit sing recovery into composition, unchanged identity/original audio,
no implicit range choice, invalid library-as-clip references, duplicate
publication, new-recording isolation and rejection/drop behavior. It does not
test live SVC audio or guarantee the LLM's next semantic decision. The existing
parallel speech/singing classifier is unchanged in this patch.

`SkillRoutingRegression.RunBatch` exercises the actual sing parser and resolver,
source uncertainty, explicit recovery, fixed identity, original-time selection,
current-version replay/save, rejected contradictory parameters, exact library
selection, and prevalidated multiple clip deletion. Existing legacy regressions
are retained; prompt assertions now describe the new protocol instead of the
retired parameter combinations.

Verified with Unity 2022.3.22f1: `Logs/unity-sing-protocol-complete.log` reports
the unified protocol tests and the complete regression suite passed; process
exit code was 0, with no C# compilation errors. The selection test also feeds
the selected audio into the existing composition builder and verifies that an
A3 target reaches the actual pitch planner. This is not a live acoustic or
conversational-quality evaluation.

### Raw silent-response diagnostics (2026-09-08)

ChatQW now publishes completed response content through a diagnostic-only
`OnRawResponse` event before think stripping and speech/tool projection, for
both streaming (including continuations) and non-streaming requests. ChatSample
logs this as `[LLM原文]` under the existing `m_LogRawLLMOutput` switch. This
preserves silent/thought prose for Console inspection without feeding it into
TTS, tool dispatch or additional history writes. The projected stream-completion
log is renamed `[LLM执行层输入]`; it may correctly contain only `<silent/>`.
Canceled/stale streaming requests do not publish a completed raw response.
The raw log is response `content`, not a separate provider reasoning field.

The regression covers silent prose, thought-only and mixed speech/action
responses, the logging switch, event removal and unchanged channel projection.
Validation log: `Logs/unity-raw-silent-diagnostics.log`.

### Plain speech by default (2026-09-08, supersedes mandatory say)

The latest live log contained five ordinary replies without a say wrapper that
were incorrectly treated as silence. Ordinary response content now streams as
speech immediately; no wrapper, semantic classifier or extra LLM request is
required. `<thought>`/`<think>` blocks and `<silent/>` tails remain private.
Closing a thought block restores the preceding mode; silent lasts for the rest
of the reply, including any later compatibility `<say>` wrapper. Closing say
does not mute subsequent ordinary prose. Top-level tools remain independent,
including during silent, while code examples and tools inside thought/say
blocks are not executed. Raw response diagnostics remain unchanged.

Behavior and singing prompts now agree on plain speech. The redundant C#
speech instruction was removed: it was incorrectly attached to memory-claim
decomposition, not primary chat, and made that request's JSON invalid by
concatenating two messages without a comma. That auxiliary request again
contains only its JSON-task prompt. Main chat uses the existing prompt-file
pipeline; no second, conflicting channel prompt is injected.

Tradeoff: unmarked model analysis is indistinguishable from unmarked dialogue
and will be spoken. The program does not guess which prose is private. Explicit
private markers and provider `reasoning_content` remain isolated; incomplete
private blocks do not leak. No silence/asking decision is imposed on the role.

Regression includes all five failed log replies at every chunk size, immediate
plain-text emission, silent and thought transitions, say compatibility, tools,
code examples, byte-split UTF-8 SSE with provider reasoning, actual prompt-file
assembly and primary request JSON with/without singing in both streaming modes,
and valid isolated auxiliary JSON. The actual router portion of the regression
was also compiled and executed standalone via PowerShell Add-Type: all cases
passed. Unity's open editor compiled the updated runtime/editor assemblies
without C# errors. Full interactive regression is pending exit from the user's
active Play session at that point. The subsequent complete batch runs
`Logs/unity-multi-range-playback-facts.log` and
`Logs/unity-multi-range-playback-facts-final.log` include these channel and
request-construction regressions.

### Multi-clip range selection and heard-order facts (2026-09-08)

`sing refs="clip:A,clip:B" range="expanded" confirm_user="true"` now prepares
each selected clip's own expanded range, including pending evidence when the
model explicitly confirms its source. Multi-clip clean works the same way;
current preserves each existing version. Original audio is retained. One pair
of absolute start/end coordinates is still limited to one clip, with a precise
error rather than the old false claim that the ranges were conflicting.
Unknown identities and invalid range values are checked before preparation;
later preparation failures identify the failing clip and disclose any already
prepared clips. No partial selection is played as a substitute.

Model-facing facts begin with latest retained recording, capture chronology,
and a separate numbered snapshot of the last fully played practice composition.
The latter freezes clip identities, source revision/range and short lyric hints
at preparation, and publishes them only after successful playback completion.
New preparations, interruption/failure, later revision and deletion cannot
rewrite that completed record. Latest completed/incomplete outcome is reported
separately; this is not a claim that an interrupted song was fully heard, nor
a reconstruction of the exact partially heard prefix. Existing pitch evidence
is unchanged. Runtime verification can look for `[Sing/Playback]`.

Singing instructions explain the difference between capture and playback
positions and require chosen order/range to be expressed in actual parameters,
not only in private prose or reason. They invite concise direct actions without
requiring reasoning or forbidding questions. Repeated per-clip boundary advice
was removed while measured boundary facts remain. No automatic material
selection, semantic override, extra LLM request or service restart is added.

### Repeated ranges and malformed tool feedback (2026-09-08)

Selecting the same source coordinates, audio and pitch timeline is now a no-op:
the current buffers, revision and measured pitch remain intact. Equality checks
include decoded samples before PCM16 quantization as well as encoded output;
different WAV headers or initial re-encoding must not erase pitch. A later
failure in a multi-clip request cannot invalidate an unchanged earlier clip.
Actual range changes still invalidate that clip's pitch evidence.

When recorded clean and expanded start/end coordinates are identical, immutable
clean audio/timeline can provide the missing expanded buffers. Matching only
duration is insufficient; genuinely different missing expanded material still
returns a factual failure without substituting another recording.

Streaming channel routing holds a possible malformed book-bracket tool until
a known tool name and attribute assignment are established, e.g.
`《sing refs="clip:..."/>`. Such syntax is withheld from speech, never repaired
or executed automatically, and prevents sibling tools from executing in that
response. Ordinary titles such as `《One Last Kiss》` remain spoken. Raw output
remains available through the existing logging switch. The main model receives
the actual format error; a waiting real user can trigger the existing bounded
ToolCorrection flow. Autonomous errors remain facts for the next natural turn,
not forced interruptions. Ordinary prose still needs no `say` wrapper.

Sticky failures are explicitly historical events, not assertions that the latest
source/playback state is still pending. Missing source confirmation and missing
playable range are reported together, allowing the model to express both in
one request or ask the user. No automatic confirmation or range choice is added.

Unity batch regression covers high-amplitude repeated selection, clean/expanded
aliases, true missing expanded audio, partial batch failure preserving pitch,
malformed syntax at every stream chunk size, ordinary titles, error-event delivery,
real-user correction, autonomous isolation and the existing retry budget.
See `Logs/unity-range-idempotency-format-guard-verified.log` for the passing run.
Live conversation/SVC replay remains to be checked in the next Play test.

### Actual request evidence and decision/output boundaries (2026-09-08)

A compact inventory now lists all retained recordings in capture order, including
pending candidates alongside confirmed clips, with exact clip identity, lyric
hint, independent source/playback statuses and duration. Last completed playback
is a separate record of the refs actually played. Unloading detailed singing
instructions no longer hides this read-only overview; it does not load the skill,
confirm a source or select audio automatically.

`[Sing/Execution]` reports whether a singing request has been submitted since the
latest user turn and the current runtime state (idle, queued, preparing, buffered
waiting for speech, playing, library lookup or other active work). A rejected
request counts as submitted, not as successful/active. These are objective facts,
not a semantic promise detector or an automatic retry/selection gate. Existing
no-work continuation feedback remains. User-turn boundaries reset the submission
fact; completed playback history is not substituted for current work.

The existing raw-LLM logging switch also enables `[LLM请求/歌唱事实]`. Both main
streaming and non-streaming dispatch paths emit diagnostics from the final JSON
payload. Only marked singing fact lines from the last user message are logged;
image content, unrelated memory, older user frames and spoken prefixes are not
dumped. Missing facts are explicitly reported rather than filled from old history.
The event does not modify the request, dialogue, speech or actions.

Behavior/singing instructions distinguish a thought about asking from an audible
question, with a thought-then-plain-speech example. The conflicting recommendation
to continue after every sentence has been removed. Silence, asking, acting or
changing one's mind remain model choices; ordinary speech still needs no say tag,
and neither look nor continue is described as starting audio preparation.

Regression covers actual serialized plain/multimodal requests in both modes,
missing-current-frame versus stale history, logging switch/event delivery,
pending+confirmed inventory with skill unloaded, idle/rejected/queued/text-drain
states and effective prompt assembly. The complete passing run is
`Logs/unity-singing-request-facts-verified.log`.
These checks verify plumbing and instructions, not that the live LLM will always
make the intended decision; the next conversation test must verify that behavior.

## 2026-09-09: Separate playback, attribution and full recording evidence

The source-confirmation gate on ordinary `sing`/`clip_revise` was removed.
Explicitly selecting ready pending audio admits exactly that clip, without changing
its provenance. Completion does not promote pending sources either. `clip_confirm`
can independently confirm a candidate or an already selected stable clip; legacy
`confirm_user=true` remains supported. No extra LLM classifier call was added.

Each retained clip now keeps its current audio evidence and the latest complete
recording evidence separately. Capture publication and duplicate/archive updates
refresh evidence even when the final acoustic score is weaker. A late shorter
preview cannot erase the longer recording. Current playback stays unchanged;
explicit range/raw-coordinate selection uses the latest evidence. The same rule
applies before and after admission. Failed range validation keeps the current
version, and dropped recordings are not resurrected by late results.

`[Sing/LatestRecording]` distinguishes the full transcript/boundaries from current
audio and appears in the actual-request diagnostic summary. Boundary windows use
raw-recording coordinates. The fixed recording sequence is reserved at capture
start, shared by candidate/practice representations and never renumbered by
confirmation, revision or deletion. It is separate from playback order.

Only the related singing subsection and one behavior tool-catalog line changed;
voice metadata, memory/persona instructions and output-channel rules were kept.
Regression includes 4.93-second current audio alongside an 11.17-second weaker
final recording, independent source confirmation, completion, explicit recovery,
invalid/speech-conflicting bounds, out-of-order analysis completion and deletion.
