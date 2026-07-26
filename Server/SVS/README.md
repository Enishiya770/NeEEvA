# NeEEvA Singing Voice Synthesis

Port `9883` is the independent singing renderer. It is not the Seed-VC/RVC
service on port `9882`.

The request path is:

1. SenseVoice extracts `SingingScore` (lyrics, timed notes, continuous F0,
   energy, breaths and vibrato hints).
2. The bridge routes Mandarin/English/Cantonese scores to SoulX-Singer. For
   Japanese, SenseVoice's transcript is normalised to hiragana and mora, then
   passed through NeEEvA's experimental kana-to-SoulX-phone adapter.
3. The selected backend synthesizes a new waveform from lyrics and melody
   metadata.
4. Unity accepts the result only when `X-SVS-Complete: 1` is present. Otherwise
   it can explicitly fall back to the existing port-9882 conversion path.

Install the optional backend:

```powershell
.\install_soulx.cmd
.\start_svs_server.cmd
```

The `.cmd` launchers use `ExecutionPolicy Bypass` only for their child
PowerShell process. They do not weaken or permanently change the machine-wide
PowerShell policy. Direct `.ps1` invocation remains available on systems that
already permit local scripts.

The installer requires Python 3.10 because that is the version used by the
official backend. If the Windows Python launcher is present but 3.10 is
missing, the installer runs `py install 3.10` and creates an isolated `.venv`.
On an NVIDIA system it installs the official PyTorch 2.4.1 CUDA 12.1 wheels and
verifies that CUDA is visible; set `NEEEVA_SVS_CPU_ONLY=1` only when an explicit
CPU-only installation is wanted.
It first tries a shallow Git clone; if GitHub resets that connection, it retries
through GitHub's official source ZIP. Model downloads use `hf_xet`, an extended
network timeout and Hugging Face's resumable cache. The installer also refuses
to start a second model download while one is already active.

The official model files are several gigabytes. They are not downloaded or
committed automatically. SoulX-Singer officially supports Mandarin, English
and Cantonese. NeEEvA additionally advertises Japanese as
`soulx-ja-phone-adapter-experimental` when SoulX is installed. This is an
explicitly labelled approximation, not a claim of upstream Japanese support.
The port-9882 voice-conversion fallback is never labelled as independent
synthesis.

## Japanese score backend

SenseVoice remains the sole lyric recogniser. `SingingScore` preserves its
original `lyrics` for dialogue understanding and adds:

- `lyrics_reading`: hiragana used for pronunciation
- `lyrics_mora`: timing units such as `きょ`, `う`, `っ`, `て`
- `lyrics_reading_source` and `lyrics_reading_complete`: provenance/readiness

Katakana is converted locally. If SenseVoice emits kanji, its contextual
OpenJTalk front end converts it to kana (for example, lyric `君` is read as
`きみ`, not the title suffix `くん`). The Japanese renderer is never allowed to
consume unresolved kanji and does not carry a second dictionary. Explicit
Japanese lyric corrections invalidate the old reading and must therefore be
written in kana.

The built-in adapter maps each kana mora directly to phones in SoulX's existing
English inventory. It does not run English G2P and handles contextual `っ`,
`ん`, `ー`, contracted kana such as `きゃ`, and common foreign-sound kana. An
unhandled unit fails validation instead of falling back to source-audio
repetition. The response backend is always
`soulx-ja-phone-adapter-experimental`.

An external character-specific Japanese renderer remains an optional future
replacement. When all three variables below are valid it takes precedence:

```powershell
$env:NEEEVA_JA_SVS_PYTHON = "D:\DiffSinger\venv\Scripts\python.exe"
$env:NEEEVA_JA_SVS_RUNNER = "D:\DiffSinger\neeeva_runner.py"
$env:NEEEVA_JA_SVS_MODEL = "D:\DiffSinger\checkpoints\character-ja"
.\start_svs_server.cmd
```

The runner contract is:

```text
python neeeva_runner.py
  --model <model-file-or-directory>
  --score <SingingScore-json>
  --output <generated-wav>
  --seed <uint32>
  --request-id <id>
```

It must read `renderer_lyrics`/`renderer_lyric_units`, notes, F0 and expression
from the score and write a complete WAV. The adapter deliberately does not pass
the user's source WAV to the runner, so this path cannot silently become a
voice converter. The source is retained only in the local diagnostic capture.

Unity plays the independent result directly by default. Enabling
`Enable SVSRVC Post Polish` sends that generated result—not the user's
recording—through the character RVC once more. Logs and the LLM result fact use
`svc-post-polish`; if polishing fails, Unity plays the unpolished independent
result.

Unity now uses the dedicated 3.04-second Mandarin character prompt
`prompts/41041_svs_zh_short.wav` and its checked phoneme sidecar by default.
This avoids treating the older Japanese dialogue reference as English and also
keeps prompt/target sequence length small enough for a 6 GB GPU. Override it
with `NEEEVA_SVS_PROMPT_WAV`; set its real transcription language with
`NEEEVA_SVS_PROMPT_LANGUAGE=en|zh|yue`.

On a 6 GB GPU the model may not coexist with dialogue TTS. The bridge therefore
preloads a persistent FP16 renderer into **system RAM**, moves it to CUDA only
while a song is being rendered, then moves it back to CPU and releases CUDA
memory. This removes repeated checkpoint loading without reserving GPU memory
between songs. Set `NEEEVA_SVS_PERSISTENT_WORKER=0` to restore the isolated
one-shot fallback.

The character prompt metadata is content-addressed under
`runtime/prompt_cache`, and final-ASR-aligned targets skip repeated lyric, note
and F0 transcription. Before rendering, isolated octave/harmonic spikes are
corrected, sub-80 ms note fragments are merged, and lyrics are divided only at
existing stable note boundaries. The renderer preserves the user's original
key rather than automatically moving the whole melody to the prompt range.

For Mandarin and Cantonese, a separate CPU-resident singing-ASR worker now
extracts real per-syllable timestamps. It first splits the performance at
acoustic vocal gaps, so sung homophones and long phrases are more reliable than
ordinary dialogue ASR. Stable pitch changes inside a timestamped syllable can
produce at most one `note_type=3` melisma continuation; vibrato is not allowed
to repeat the consonant. Timestamp results are cached by source-audio hash.
Set `NEEEVA_SVS_ACOUSTIC_LYRIC_ALIGNMENT=0` to use the older note-boundary
heuristic. An explicit `lyrics_override` in SingingScore replaces recognized
characters while retaining these acoustic timestamps.

Inference uses 12 flow-matching steps by default (the upstream preset is 32);
`NEEEVA_SVS_INFERENCE_STEPS=4..32` can trade latency for quality. Response
headers and Unity logs expose prompt-cache, metadata, checkpoint-transfer and
inference timings. If a persistent worker dies during inference, the bridge
replaces it and retries once before using the isolated one-shot fallback.

Every successful request is retained under
`runtime/captures/<timestamp>_<request-id>/` with source WAV, SingingScore,
prompt/target metadata, generated WAV and timing/result JSON. The newest 20
captures are kept. Set `NEEEVA_SVS_SAVE_CAPTURES=0` to disable this diagnostic
retention. `quality_metrics.py` can compare a captured target contour with one
or more generated WAV files using the bundled RMVPE extractor.

For reverse-ASR diagnostics, call SenseVoice `/asr` with
`learn_speaker=false`. This keeps generated test audio from enrolling or
updating a speaker profile.

Official backend:

- https://github.com/Soul-AILab/SoulX-Singer
