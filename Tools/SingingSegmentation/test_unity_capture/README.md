# Original-recording capture regression

Run from the repository root, with the installed .NET 10 SDK:

```powershell
dotnet run --project Tools/SingingSegmentation/test_unity_capture/CaptureChecks.csproj --configuration Release
```

No NuGet package source, network request, microphone access, audio playback, or Unity Editor launch is used. The harness compiles the **actual current production** `PersonalSingingLearningCapture.cs`, `PersonalSingingLearningProtocol.cs`, `PersonalSingingLearningRecorder.cs`, and `VoiceInputs.cs`; `UnityStub.cs` supplies only a deterministic in-memory subset of Unity's clip and microphone API. Newtonsoft.Json is referenced from the repository's existing plugin.

The 32 assertions cover interleaved stereo PCM and WAV frame counts; invalid numeric PCM rejection; one immutable publication per capture; parent and multi-region lineage preservation; short/noise recordings retained without ASR filtering; cleanup after missing/failed/overlong clips; disabled capture rejection; per-subscriber exception isolation; and legacy push-to-talk sealing at the actual microphone cursor, excluding an unknown cursor's padded fallback from learning while preserving its old ASR callback.

This is a recorder and event-bus regression. It does **not** validate Unity's native microphone implementation, real-time EOU scheduling, browser/WebGL recording, HTTP uploads, the settings panel, or service training. Those need their separate integration checks.

Verified on 2026-09-29: `PASS 32 checks`. The unused `VoiceInputs.signalManager` field warning is expected when compiling the non-WebGL path.
