using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    // The same engine-owned sentences used by the final fallback can ground a
    // repair before generation. Never derive this baseline from a rejected
    // candidate, user imperative or the review's hypothetical alternative.
    internal static string CurrentReplyRepairBaseline(string context, string language)
    {
        if (SpatialGoalResultContextPolicy.TryReadContext(context, out _, out var terminal))
            return ReplyAnswerObligationProtocol.Read(terminal).Count > 0 ? null :
                SpatialGoalResultContextPolicy.BuildTrustedReport(terminal, language);
        var facts = CommittedSpatialReplyProtocol.ReadReplyFacts(context);
        if (ReplyAnswerObligationProtocol.Read(facts).Count > 0) return null;
        if ((string)facts?.SelectToken("replyPurpose.kind") == "clarify-current-input")
            return TranscriptDisagreementPolicy.Clarification(language);
        var receipt = facts?["currentExecution"] as JObject ?? facts?["currentActionReceipt"] as JObject;
        if (receipt == null) return null;
        if ((bool?)facts?["workResultOnly"] == true) return WorkResultMovementReport(receipt, language);
        string action = (string)receipt["actionId"] ?? (string)receipt["requestedActionId"];
        string intent = (string)receipt["intent"];
        var room = receipt["currentRoomFacts"] as JObject ?? facts?["currentRoomFacts"] as JObject ??
            new JObject { ["execution"] = receipt["execution"]?.DeepClone() };
        string report = RoomMovementResultPolicy.Fallback(room, intent, action);
        return report == null ? null : LocalizeRoomFallback(report, language);
    }

    private static string WorkResultMovementReport(JObject receipt, string language)
    {
        var room = receipt?["currentRoomFacts"] as JObject;
        string intent = (string)receipt?["intent"], action = (string)receipt?["actionId"];
        var required = RoomMovementResultPolicy.Requirement(room, intent, action);
        if ((bool?)required["required"] != true) return null;
        if ((string)required["expectedReport"] == "arrived")
            return ArrivalProgressText((string)required["arrivalRelation"], language);
        var report = RoomMovementResultPolicy.Fallback(room, intent, action);
        return report == null ? null : LocalizeRoomFallback(report, language);
    }
}
