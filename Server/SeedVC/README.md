# NeEEvA Seed-VC bridge

This local service converts the user's latest real singing/humming performance into
the character voice.  It preserves the source timing, pitch, lyrics, breathing and
micro-variation instead of synthesising a melody from oscillator grains.

NeEEvA enables automatic F0-range matching by default: the melody intervals and
timing stay intact while the whole performance moves into the character's natural
register.  The scene keeps `Assets/Model/41041.wav` as its diagnostic/reference
audio and requests 20 diffusion steps when the Seed-VC fallback is used.

When `Server/RVC/models/neeeva_character.pth` exists, this bridge automatically
uses the dedicated RVC v2 singing voice first.  The currently deployed model was
selected at epoch 75 after training on 12.80 minutes of character speech and
humming-like vocalisations.  It uses a fixed inference seed and no retrieval mix
by default, which made held-out pure humming more stable on this corpus.  Seed-VC
remains the zero-shot fallback before training finishes or when the dedicated
model is unavailable.

Run `install_seedvc.ps1` once. Unity now probes `http://127.0.0.1:9882/health`
when the scene starts and launches `start_seedvc_server.ps1` in a hidden Windows
process when the bridge is absent. It verifies health again immediately before
every hum-back request, so a service stopped during play is restarted on demand.
When the on-demand `singing` Skill is loaded, Unity also sends a background
`POST /warmup`. This loads the dedicated RVC worker while the LLM is still
deciding what to say; it does not submit fake audio and does not block dialogue.
Manual startup remains available by running `start_seedvc_server.ps1` directly.
Automatic-start output is written to `runtime/seedvc_server.log`.

The visible process chain is `powershell.exe` running this script and the
resolved Python interpreter running `seedvc_server.py --port 9882`, with
`Server/SeedVC` as its working directory. Resolution checks
`NEEEVA_SEEDVC_PYTHON`, the isolated `Server/SVS/.venv` runtime,
`NEEEVA_PYTHON_EXE`, `NEEEVA_GPT_SOVITS_ROOT`, the project-local
`GPT-SoVITS/runtime/python.exe`, and finally the Python launcher. Candidates
that cannot import `soundfile`, `uvicorn`, `fastapi`, and multipart support are
skipped instead of starting a bridge that immediately exits.
The first Seed-VC fallback conversion can still download the official singing
checkpoint and take several minutes; the preferred dedicated RVC path reuses its
local trained model.

The service uses port `9882`. The dedicated RVC path uses the configured free-VRAM
gate (1000 MiB by default), safely falls back to CPU after recoverable CUDA errors,
and keeps one worker alive for 90 idle seconds so streamed blocks do not repeatedly
pay model-import cost. A warmed worker's existing device is reused even though its
own allocation lowers the later free-VRAM reading. The Seed-VC fallback remains an
isolated process and releases its model/VRAM after completion.

Only the isolated model process uses `https://hf-mirror.com`, because direct access
to Hugging Face times out on the target machine.  Downloaded weights stay under
`vendor/seed-vc/checkpoints` and are ignored by Git.

The most recent successful conversion is copied to the private, Git-ignored
`last_conversion` folder as `source.wav`, `target_reference.wav`, `output.wav` and
`metadata.json` for direct A/B diagnosis.  Each new conversion replaces this set.
