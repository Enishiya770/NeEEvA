# Mixed-turn independent transcription — 2026-09-06

The final semantic transcript now uses independently decoded timestamped spans.
The first whole-wave ASR pass is retained as an acoustic-analysis hint and a
fallback/comparison observation; this change does not remove that initial pass.

## Recognition and evidence

- Split at clean/recovery boundaries and internal melodic phrase transitions.
  Internal transitions are exposed before playback island filtering/merging, so
  an English phrase followed by a Japanese phrase can be decoded independently.
- Include the complete post-VAD content, including speech outside the expanded
  playback crop. Keep decoded raw and content WAVs for local diagnostic replay.
- A whole-turn `speech` verdict does not disable these ASR observations. When
  boundaries are missing, recordings over 8 seconds still receive bounded
  windows near quiet points. Short homogeneous recordings retain the old path.
- Each core window uses a fresh ASR cache and `language=auto`. A second window
  adds 0.4 seconds of context at each edge. Its differing text stays an explicit
  alternative, never concatenated as an additional user utterance.
- The acoustic types are hypotheses (`singing_candidate` / `uncertain`). Text
  recognition does not confirm a candidate, change playback boundaries, or
  decide user intent. The role can ask about uncertain observations.
- Missing/failed spans remain visible. No calibrated ASR confidence is available;
  `confidence_available=false` says so explicitly. `turn_segments_complete`
  means every core decoder returned text, not that all words are correct.
- Preserve the fluent whole text when every independent core text occurs inside
  it in order and all segment languages agree. Otherwise use ordered core text.
  This literal corroboration preserves boundary words in ordinary speech.

`/asr` returns `turn_segments_schema=1`, `turn_segments`, `whole_text`,
`segmented_text`, `transcript_source`, completeness and review flags. Each span
has content-relative times, region, type source, text, language, status, and
overlap alternatives with their own times. Original-wave times are content
times plus `audio_content_start_seconds`.

Unity reads the selected primary text for user history and tool-source checks;
the same request receives all timestamped alternatives and whole-text evidence.
An older streaming draft cannot replace this final segmented transcript.
Existing exact-crop singing lyrics remain separate from full-turn semantics.

## Local diagnostics

`Server/SenseVoice/mixed_dumps/` is gitignored. Each eligible final recording
produces `.raw.wav`, `.content.wav` and response `.json`, deduplicated by raw
content hash. Decoded raw audio is before VAD trimming. The default cap is 80
recordings; reaching it skips new dumps and retains all old files. Set
`NEEEVA_MIXED_DUMP=0` to disable recording diagnostics. `reference_segments` is
reserved for human transcription and is never filled from ASR as ground truth.

Offline replay, using an already cached local model directory:

```powershell
cd Server/SenseVoice
python replay_mixed_asr.py band_dumps/TAKE.json --model LOCAL_MODEL_DIR --device cuda:0 --fresh-analysis --output ../../Logs/comparison.json
python -m unittest test_mixed_turn_asr test_singing_score test_asr_dispatch test_asr_scheduler test_asr_timing test_streaming_activity test_streaming_window
```

## Verified results and limits

53 Python tests and Unity `SkillRoutingRegression.RunBatch` passed. Four actual
recordings were replayed with fresh acoustic analysis and local SenseVoice:

| Recording | Whole-wave result | Independent result |
|---|---|---|
| 15:22, 14.60 s | “这次没有问题，然后我继续唱轮唱我了。” | Recovered English “Can you give me one last kiss”, Japanese “忘れたくないこと”, and the trailing request |
| 10:27, 17.11 s | Preface, “can you”, and request; most lyrics absent | Recovered the separate English and Japanese phrases plus the request |
| 10:03, 20.22 s | “唱。” | Recovered preface, Japanese lyric hypotheses, and “现在唱的这一段唱给我听吗” |
| 15:20, 12.98 s ordinary speech | Complete request | Kept the same full request after corroboration by the independent spans |

Machine-readable observations: `Logs/mixed_asr_replay_final.json`. Segment ASR
including overlap alternatives took 0.59–0.82 s on the local GPU in this replay;
this excludes acoustic analysis and is not an end-to-end latency delta. Shared
model inference remains serialized because the model instance is not guaranteed
thread-safe. Some Japanese lyric words remain wrong, and no corpus-level error
rate is claimed without human reference annotations. Live role behavior still
requires the next conversational test.

The local 9881 ASR service was restarted and `/health` reports
`turn_segments_schema=1`. A real `/asr` multipart request for the 14.60-second
recording also returned the recovered English/Japanese/tail content. Server
pipeline time was 4.21 s, including 2.49 s full pitch analysis and 0.87 s segment
ASR. The diagnostic raw/content/JSON snapshot was successfully created.

## 2026-09-06 follow-up: context and completed-capture ownership

- Clean boundaries still preserve a short spoken tail. Optional inner/recovery
  cuts now require at least 2.8 seconds of context on both sides; no audio is
  discarded. This avoids decoding two-second syllable fragments as unrelated
  languages. Useful recovery boundaries (English before Japanese) remain.
- Ordered near-equivalent whole/segment observations retain the fluent whole
  primary with an explicit disagreement flag. Substantial segment-only content
  still selects the segmented primary. Both hypotheses remain visible to the
  role; string similarity is not semantic certainty or a confidence score.
- Weak acoustic islands remain transcript hypotheses, but are no longer promoted
  unconditionally into playback `singing_text`.
- Completed final/promoted ASR jobs survive new speech. Cancellation detaches the
  old reply; the late result is archived independently as a pending candidate,
  never published as current `LastText`, speaker state, or recent-turn audio.
  Capture-specific scheduler channels prevent out-of-order promotion from losing
  the old upload. Pending analysis is visible as a fact, not “nothing recorded”.
- Confirmed practice material is committed before building the user perception
  frame. Source validation is recomputed after a song_sing/hum_back reroute while
  preserving musical parameters and unrelated validation errors.
- Continue chains receive factual singing-job status. No new forced-action,
  keyword-based promise detector, or mandatory-clarification policy was added.

Final fresh-analysis replays: `Logs/mixed-asr-sept06-verified.json`. The 15:22
English/Japanese/tail example remains complete; the 16:53 Japanese fragment no
longer switches to phonetic Chinese/English; the 16:54 mixed turn retains the
complete trailing playback request. Japanese word substitutions still occur.
Segment-only time for these three offline replays was 0.51–0.82 seconds, excluding
full acoustic analysis; this is not an end-to-end first-audio latency measurement.

Unity compilation and `SkillRoutingRegression.RunBatch` passed in
`Logs/unity-sept06-capture-asr-final.log`, including independent late-capture
archival, deduplication, explicit confirmation, and stale-source validation.
The final Python suite passed all 57 tests (including a regression preserving
the English recovery phrase and cross-capture scheduler ownership).
Live conversational behavior requires a new user test. ASR health exposes
`turn_segments_revision=2026-09-06-context-preserving` to identify the loaded code.

## 2026-09-07: explicit material identity and short-capture recovery

- `recent_turn` and implicit song saving no longer expose an older recording
  when a newer singing candidate (or newer confirmed stable recording) exists.
  The older recording is retained; the role can explicitly select `stable:N`.
  Song saving accepts `source_ref="stable:N|recent_turn"`, distinct from the
  destination library song ID. Unknown references fail without substitution.
- Pending candidates retain an independent raw/timeline/boundary snapshot.
  `practice_confirm` can explicitly choose `capture="clean|expanded"` plus
  capture-local head/tail trims. Automatic qualification still uses three
  seconds; explicit recovery validates a voiced window of at least one second.
  This does not auto-confirm, auto-play, or erase original clean/expanded audio.
- Up to two earlier ASR observations from the same recording remain available
  as conflicting/complementary hypotheses; beginning another capture clears
  them. Final text is not blindly replaced by earlier text.
- Short clean islands can get `recovery_context` and `recovery_tail` alternative
  ASR observations. These do not modify playback boundaries. In the 10:37 real
  recording they recovered the later Japanese lyric hypothesis and the request
  “好，轮到你唱了” (`Logs/mixed-asr-sept07-context.json`). Some lyric errors remain.
- Single-phrase practice now executes the already computed semitone plan via
  the same renderer as multiple phrases (`auto_f0_adjust=false`), instead of
  silently reverting to backend auto-F0. Its conversion can overlap the spoken
  prelude; playback still waits for that prelude. Actual perceived latency and
  pitch accuracy require a new live test; this does not claim a faster formal
  spoken reply or perfect tuning.

The known mismatched library entry `c04b007c9699` was not deleted or rewritten.
This change prevents implicit old-audio substitution; it does not automatically
repair historical library content. Health identifies this ASR build as
`turn_segments_revision=2026-09-07-recovery-context`.
