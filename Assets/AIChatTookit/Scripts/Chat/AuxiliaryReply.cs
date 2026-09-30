using System;

/// <summary>Request-scoped diagnostic result. Text keeps the existing provider callback semantics.</summary>
public sealed class AuxiliaryReply
{
    public readonly string Text, Status;
    public AuxiliaryReply(string text, string status) { Text = text ?? ""; Status = status; }

    public static string TransportStatus(long code, string result, string error)
    {
        if (result == "Success" && code == 200) return "received";
        if (code >= 400) return "http_error";
        string message = error ?? "";
        if (message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
            message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0) return "transport_timeout";
        return "transport_error";
    }

    public static string OutputStatus(string transport, bool validEnvelope, string finish, string text)
    {
        if (transport != "received") return transport;
        if (!validEnvelope) return "invalid_envelope";
        if (finish == "length") return "output_truncated";
        return string.IsNullOrWhiteSpace(text) ? "empty_output" : "received";
    }

    public string InvalidDecisionStatus => Status == "received" ? "invalid_decision" : Status;
}
