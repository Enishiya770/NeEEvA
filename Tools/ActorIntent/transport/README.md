# ActorIntent transport checks

Run `Tools/ActorIntent/transport/Run-Checks.ps1` with PowerShell and the .NET 10 SDK.
Results default to `Logs/actor-intent/transport/`.

The fixture compiles the production `ChatQW.ActorIntent.cs`, protocol and backend
interface with small Unity/HTTP stubs. It does not send network requests. It checks
strict completion envelopes, draft/user evidence binding, intent and condition
request replacement, stale callbacks, cancellation, transport errors and the
actual production request builder.

These checks do not assess model classification quality, actual Unity scheduling,
audio output or physical head motion; those require their separate fixtures.
