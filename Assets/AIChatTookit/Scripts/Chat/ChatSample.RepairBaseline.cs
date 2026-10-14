using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    // The same engine-owned sentences used by the final fallback can ground a
    // repair before generation. Never derive this baseline from a rejected
    // candidate, user imperative or the review's hypothetical alternative.
    internal static string CurrentReplyRepairBaseline(string context, string language)
    {
        if (SpatialGoalResultContextPolicy.TryReadContext(context, out _, out var terminal))
            return SpatialGoalResultContextPolicy.BuildTrustedReport(terminal, language);
        var facts = CommittedSpatialReplyProtocol.ReadReplyFacts(context);
        if ((string)facts?.SelectToken("replyPurpose.kind") == "clarify-current-input")
            return TranscriptDisagreementPolicy.Clarification(language);
        var receipt = facts?["currentExecution"] as JObject ?? facts?["currentActionReceipt"] as JObject;
        if (receipt == null) return null;
        string action = (string)receipt["actionId"] ?? (string)receipt["requestedActionId"];
        string intent = (string)receipt["intent"];
        var room = receipt["currentRoomFacts"] as JObject ?? facts?["currentRoomFacts"] as JObject ??
            new JObject { ["execution"] = receipt["execution"]?.DeepClone() };
        string report = RoomMovementResultPolicy.Fallback(room, intent, action);
        return report == null ? null : LocalizeRoomFallback(report, language);
    }
}
