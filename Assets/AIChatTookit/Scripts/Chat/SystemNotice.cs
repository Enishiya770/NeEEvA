using System;

/// <summary>
/// Non-character information shown by the application.  These records never enter
/// dialogue/TTS history: they describe the runtime, not something the character said.
/// </summary>
public enum SystemNoticeSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

public enum SystemNoticeMode
{
    Hidden = 0,
    ErrorsOnly = 1,
    All = 2,
}

[Serializable]
public sealed class SystemNotice
{
    public string Code;
    public SystemNoticeSeverity Severity;
    public string Message;
    public string Detail;
    public string Source;
    public bool Retryable;
    public string TimestampUtc;

    public SystemNotice(
        string code,
        SystemNoticeSeverity severity,
        string message,
        string detail = "",
        string source = "",
        bool retryable = false)
    {
        Code = (code ?? "").Trim();
        Severity = severity;
        Message = (message ?? "").Trim();
        Detail = (detail ?? "").Trim();
        Source = (source ?? "").Trim();
        Retryable = retryable;
        TimestampUtc = DateTime.UtcNow.ToString("o");
    }
}
