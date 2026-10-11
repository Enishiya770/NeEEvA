/// <summary>Transport-owned outcome, separate from model-authored decision JSON.</summary>
public sealed class PrivateDecisionResult
{
    public string Output { get; }
    public string Status { get; }
    public bool HttpRequestSent { get; }
    public bool Succeeded => Status == "received" && !string.IsNullOrWhiteSpace(Output);
    public PrivateDecisionResult(string output, string status, bool httpRequestSent)
    { Output = output ?? ""; Status = status; HttpRequestSent = httpRequestSent; }
    public static PrivateDecisionResult FromLegacy(string output) => new PrivateDecisionResult(output,
        string.IsNullOrWhiteSpace(output) ? "provider-no-decision" : "received", true);
}
