# ASR route audit — 2026-09-26

The route script is prepared but no scene or `voice_routes.json` switch has been
performed by the ASR preparation task. Main scene:
`Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity`.

The one SenseVoice component has fileID `501756907`, script GUID
`e74eb068227898240b54f1ba047b8f2c`. Both nested
`ChatSettings.m_SpeechToText` and `RTSpeechHandler.m_NeuralVadClient` point to that
component. Its current ASR origin is `http://127.0.0.1:9881`; streaming preview is
enabled and its explicit `m_StreamingPreviewURL` is empty.

## Required saved-scene changes

`Tools/ASR/set-asr-route.ps1` changes only these two fields inside the verified
SenseVoice YAML block:

| Field | Local profile | Spark profile |
|---|---|---|
| `m_ServerSetting` | `http://127.0.0.1:9881` | `http://192.168.114.163:9881` |
| `m_StreamingPreviewURL` | empty string | empty string |

Keeping the WS override empty is intentional: `BuildStreamingPreviewURL()` at
`SenseVoiceSpeechToText.cs:1282` derives `ws://<same-origin>/stream/asr` from
`m_ServerSetting`. This also repairs any old explicit WS override that could
otherwise continue sending previews to Windows while final ASR goes to Spark.
The inherited serialized `m_SpeechRecognizeURL` is overwritten by `Awake()` and
does not need an independent edit. Language, VAD thresholds, window timing,
streaming enablement and singing-analysis enablement stay unchanged.

The script updates only the `asr` member of `voice_routes.json`, retaining `tts`,
`vc` and other members. Its route contains mode, HTTP origin, health URL and the
derived WS URL. The shared launcher subsequently uses
`manage-spark-asr.ps1 -Action start -SshTarget ...` for a remote selection.

## All ASR-dependent endpoints

| Call path | How it follows the selected origin | Code evidence |
|---|---|---|
| Formal `/asr` | Assigned from `m_ServerSetting` during Awake | `SenseVoiceSpeechToText.cs:141` |
| Neural `/vad` | Same origin; RT handler holds the same component | `SenseVoiceSpeechToText.cs:142` |
| `/asr/cancel`, `/asr/promote` | Uses `m_ServerSetting` at dispatch | `SenseVoiceSpeechToText.cs:869` |
| WS `/stream/asr` | Derived unless explicitly overridden; script clears override | `SenseVoiceSpeechToText.cs:1282` |
| `/speakers/manage`, `/speakers/rename`, `/speakers/reset-owner` | Same origin | `SenseVoiceSpeechToText.cs:3682`, `:3726`, `:3766` |
| `/songs/search`, `/songs/catalog` and remember/rename/forget/sing | Assigned from same origin during Awake | `SenseVoiceSpeechToText.cs:143` through `:148` |
| Generated-singing pitch verification `/singing/score` | Unity uploads rendered WAV to the same SenseVoice instance | `SenseVoiceSpeechToText.cs:6905` |

The singing verification callback is **Unity → SenseVoice**, not a hardcoded
SeedVC/SVS → Windows ASR callback. `ChatSample.cs:18170` invokes
`senseVoice.AnalyzeRenderedSingingPitch`; that method derives the selected ASR URL.
No ASR/9881 HTTP endpoint was found in the active SeedVC or SVS server scripts.

Remembered-song playback also works across machines without a shared filesystem:
`sensevoice_server.py:2714` returns composed WAV as `audio_base64`, stripping the
internal absolute paths; `SenseVoiceSpeechToText.cs:6734` decodes those bytes.
Speaker identity JSON, song catalogue and managed song WAVs belong on the selected
ASR server, so their synchronized data snapshot remains part of cutover acceptance.

## What stays on Windows in the ASR phase

- Unity microphone capture, audio buffer ownership, local audio playback, rendered
  character and recording/UI state remain on the desktop.
- `ChatSample.m_HumSVCURL` currently remains `http://127.0.0.1:9882/convert`.
- `ChatSample.m_SVSURL` currently remains `http://127.0.0.1:9883/synthesize`.
- SVC/SVS prompt/reference file paths and their existing start flags are untouched.
  Their audio/score inputs already come through Unity; no ASR-origin mirror needs
  to be added to those services.
- The ASR change does not alter the selected TTS origin or its reference path,
  nor Qwen, ARDY, memories, or any credentials in the scene.

## Prepared route command and safeguards

```powershell
./Tools/ASR/set-asr-route.ps1 -Profile local -CheckOnly
# After remote ASR acceptance and with this project's Editor closed:
./Tools/ASR/set-asr-route.ps1 -Profile spark -CheckOnly
./Tools/ASR/set-asr-route.ps1 -Profile spark
# Roll back the endpoint selection while retaining other service routes:
./Tools/ASR/set-asr-route.ps1 -Profile local
```

Spark CheckOnly and apply both require the actual remote CUDA ASR/VAD/speaker,
streaming/dispatch/segmentation, singing analysis, song API and final-window health
contract. The script does not start a service to make validation pass. It refuses
scene writes while this project's Unity Editor is open (or an Editor's project
cannot be determined), rechecks just before committing, and rejects files changed
while preparation was running. It makes byte-preserving backups before writing
and restores both files on write failure. It never prints the complete scene or
component blocks, which may contain unrelated secrets.

PowerShell AST and real-project **local CheckOnly** passed. The scene and routes
hashes before/after that read-only run matched. Remote route health and actual
scene switching are reserved for the coordinator after ASR deployment acceptance.
