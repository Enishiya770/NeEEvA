# Spark ASR migration preparation

Prepared on 2026-09-26. **TTS acceptance is the phase boundary: these scripts have
not uploaded assets, installed packages, started ASR or changed Unity routing.**
Only the small inventory and local archive/health-contract tests were run.

## Current Windows baseline

`Server/RuntimeLogs/asr_runtime.json` selects
`Server/SenseVoice/runtime/.venv-cuda/Scripts/python.exe`, `cuda:0`,
`requireCuda=true`, no extra arguments. Latest startup logs confirm actual model
tensors: SenseVoice CUDA; FSMN-VAD and CAM++ CPU. Python is 3.12.10, FunASR
1.3.14, Torch/TorchAudio 2.11.0+cu126. The production code is
`Server/SenseVoice/sensevoice_server.py` plus its adjacent Python modules.

The first preparation check found no 9881 listener; the coordinator subsequently
started the Windows baseline for comparison. This folder never starts or stops it.

The service also supplies VAD, streaming previews, speaker identities, song
catalogue and singing perception; moving only SenseVoice weights would lose part
of the contract. `preparation-inventory.json` records paths/sizes and versions,
without identity names/embeddings or recorded speech content:

| Asset | Size / state |
|---|---:|
| `iic/SenseVoiceSmall`, cached master snapshot | 940,019,376 bytes |
| `iic/speech_fsmn_vad_zh-cn-16k-common-pytorch` | 4,033,603 bytes |
| `iic/speech_campplus_sv_zh-cn_16k-common` | 28,857,970 bytes |
| Existing speaker database | 958,082 bytes; 10 identities / 23 voiceprints |
| OpenJTalk 1.11 UTF-8 dictionary | 107,304,813 bytes |
| Song catalogue/library | Current catalogue absent; startup log says zero songs |
| Character reference | `Assets/Model/41041.wav`, 258,176 bytes |

The three models are in `~/.cache/modelscope/models/<owner--name>/snapshots/master`,
not the old `~/.cache/modelscope/hub` layout. A full offline bundle is about 1.082 GB
before tar padding. It excludes diagnostic audio dumps, Windows venv and unrelated
models. The speaker JSON contains its existing operations and identity structure.
The Japanese dictionary must move too, preventing a surprise first-use download.

## ARM64 dependency feasibility and remaining gate

The target is `.163`, ARM64 Python 3.12.3, GB10 capability 12.1, driver 580.173.02,
CUDA toolkit 13.0. Its existing ARDY environment and `/neeeva-voice/tts` are not
modified or reused. ASR's proposed root is
`/home/spark_root_001/neeeva-voice/asr`, with its own `.venv`.

Official indexes were read from Windows, without downloading wheels:

- [Torch CUDA 13 index](https://download.pytorch.org/whl/cu130/torch/) and
  [TorchAudio CUDA 13 index](https://download.pytorch.org/whl/cu130/torchaudio/)
  contain **2.11.0+cu130, cp312, manylinux_2_28_aarch64**. Keeping the 2.11 pair
  limits changes from the Windows baseline while adding the Spark CUDA build.
- [FunASR 1.3.14](https://pypi.org/project/funasr/1.3.14/) has a pure Python wheel.
  NumPy 1.26.4, SciPy 1.17.1, Numba 0.66.0, llvmlite 0.48.0 and SoundFile 0.14.0
  have matching ARM64/Python 3.12 wheels in their PyPI release metadata.
- [pyopenjtalk 0.4.1](https://pypi.org/project/pyopenjtalk/0.4.1/) supplies a source
  distribution, requiring a local build. `.163` has gcc, g++, cmake and make.
  A successful ARM64 build/import and Japanese reading parity are still required.
- `torchcrepe` is absent from the actual Windows runtime. The candidate launcher
  explicitly keeps the current FFT fallback; this migration does not enable a new
  pitch algorithm. VAD, speaker and pitch remain CPU for the first comparison.
- At preparation time `.163` could not resolve `pypi.org`,
  `files.pythonhosted.org` or `download.pytorch.org`. Its pip wheel cache contained
  only `antlr4_python3_runtime-4.9.3-py3-none-any.whl`; no ASR model cache was found.
  System DNS was not changed. Online setup must wait for a working task network,
  or use a complete transferred ARM64 wheelhouse including source-build needs.
  The TTS preparation subsequently established a temporary SSH reverse proxy at
  `.163:127.0.0.1:18880`; ASR may reuse it for its explicit installation step once
  the coordinator releases this phase. No global proxy/DNS change is needed.

Availability of wheels is **not** a passed installation or inference test. Setup
must pass `pip check`, imports, required CUDA warmup, the health contract and
same-audio comparison. The launcher wraps only the production `AutoModel` factory
to map its three fixed IDs onto explicit local directories; it keeps production
ASR logic unchanged and does not download new model revisions. The service's
existing `--require-cuda` refuses a silent CPU fallback.

## Commands, only after the coordinator releases the ASR phase

Inventory is safe before the phase boundary:

```powershell
$py = 'Server/SenseVoice/runtime/.venv-cuda/Scripts/python.exe'
& $py Tools/ASR/prepare_spark_bundle.py --output Tools/ASR/preparation-inventory.json
```

After TTS passes, take a fresh identity/song snapshot and create a NEW local tar:

```powershell
& $py Tools/ASR/prepare_spark_bundle.py --output work/asr-migration/inventory.json --bundle work/asr-migration/asr.tar
./Tools/ASR/manage_spark.ps1 -Action upload -Bundle work/asr-migration/asr.tar
./Tools/ASR/manage_spark.ps1 -Action setup
# When the temporary TTS download tunnel is still running:
# ./Tools/ASR/manage_spark.ps1 -Action setup -InstallProxy http://127.0.0.1:18880
# Offline alternative, only once a complete wheelhouse has been staged:
# ./Tools/ASR/manage_spark.ps1 -Action setup -RemoteWheelhouse /home/spark_root_001/neeeva-voice/asr/wheelhouse
./Tools/ASR/manage_spark.ps1 -Action start
./Tools/ASR/manage_spark.ps1 -Action health
```

Upload refuses an existing ASR root; extraction checks every SHA256 and refuses
unsafe paths/links or overwrites. The installed root is user-only. Upload records
`Server/RuntimeLogs/spark_asr.state.json` with `uploaded-not-started`; this state
does not claim setup/health/acceptance. Setup refuses an existing venv rather than
upgrading another environment. Investigate a partial installation before retrying.
Start/stop operate only on the recorded PID whose command line matches this
ASR root. They never stop another process occupying 9881. A running PID is not
acceptance; use the contract probe next. No script changes `voice_routes.json`.
`-InstallProxy` sets HTTP_PROXY/HTTPS_PROXY only for the remote setup command and
its child pip processes; it is not stored in shell configuration or service state.

The unified voice launcher uses the compatible entry
`Tools/ASR/manage-spark-asr.ps1 -Action start -SshTarget spark_root_001@192.168.114.163`.
This delegates the same idempotent process start, then waits up to 240 seconds for
actual CUDA ASR/VAD/speaker readiness and the existing streaming/segmentation
health contract. It does not treat a PID, listening socket or HTTP 200 alone as
readiness. `-WaitSeconds`, `-IdentityFile` and `-RemoteRoot` are available overrides.

Validate the same explicit 16 kHz mono PCM16 WAV on local and remote endpoints:

```powershell
& $py Tools/ASR/validate_endpoint.py --url http://127.0.0.1:9881 --mode all --wav work/asr-migration/speech.wav --output work/asr-migration/local.json
& $py Tools/ASR/validate_endpoint.py --url http://192.168.114.163:9881 --mode all --wav work/asr-migration/speech.wav --output work/asr-migration/spark.json
```

`all` performs a final upload with `learn_speaker=false`, paced real-time streaming,
WS cancellation, then a successor final upload. It records exact input SHA256,
transcripts, phase timing, first partial, lag and coalescing. Health asserts the
existing schema, final speech window policy and actual CUDA device. HTTP 200 alone
does not count as recognition accuracy or latency acceptance. Compare Chinese and
Japanese normal speech, long utterances, speaker identity and singing perception;
then test simultaneous TTS + ASR + ARDY before the coordinator changes the route.
Keep the Windows runtime and databases for rollback. Final cutover must avoid
independent writes to both identity databases; take the accepted data snapshot at
the coordinated switchover, rather than overwriting a live remote database.

Read-only/isolated validation already passed: Python AST for new scripts,
PowerShell parsing, and 3 local tests covering valid install, existing-state
protection, four unsafe/corrupt archives, and rejection of CPU/insufficient health.

On 2026-09-26 the coordinator authorized deployment after TTS passed its Unity
acceptance. The isolated ARM environment on `.163` passed `pip check`, CUDA model
placement, the same 14.875-second synthetic Haruka Japanese fixture over HTTP and
paced WS, cancellation, and a successor request. Final text matched the Windows
baseline exactly and the speaker database SHA256 stayed unchanged. Evidence is in
`work/spark-voice-migration-20260926/asr/comparison.json` and
`spark-post-validation.json`. This is one controlled synthetic speech comparison,
not a claim about all microphones, languages, singing or simultaneous workloads.
The actual Unity SenseVoiceSpeechToText transport regression subsequently passed
all 113 checks (`Logs/asr/unity-transport-spark-verified.json`). The coordinator then
switched the main scene and `voice_routes.json` ASR route to `.163`, stopped the
Windows ASR process and verified that the mixed service launcher reused remote
PID 44057. The remote ASR route is now active; the local runtime remains available
for an explicit rollback. No physical-microphone or simultaneous-workload
acceptance is inferred from this synthetic transport test.

The dependency constraints also pin `transformers`, `tokenizers`, `modelscope-hub`,
`pydantic` and `starlette` to the active Windows versions. The first installation
resolver inherited a host mirror and was stopped before installing those packages;
the final dependency pass used explicit official PyPI. The Torch/CUDA pass had
already completed from the official PyTorch CUDA 13.0 index. The two post-bundle
deployment script/constraint updates are hashed in `deployment-overrides.json`;
model snapshots, production ASR source and identity data were unchanged.
