# FastLoop transport checks

Run `Tools/FastLoop/transport/Run-Checks.ps1` with PowerShell and the .NET 10 SDK.
Results default to `Logs/fast-loop/transport/`.

The fixture compiles the production `ChatQW.FastLoop.cs`, protocol, prompt factory
and backend interface. It supplies small Unity/HTTP stubs; no model request,
deployment or service restart occurs. The checks exercise response-envelope and
semantic validation, cancellation before send/after send, callback ownership,
and the actual production request builder.

This is a transport/unit fixture. Unity compilation and actual head-controller,
audio-output and model-quality tests remain separate.
