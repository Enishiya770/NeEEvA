using System.Linq;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    // A compact index of engine evidence shared by writer and reviewer. No new
    // action in this reply does not erase an earlier execution event.
    private JObject BuildReplyEventContext(int generation)
    {
        var previous = ReadPreviousFormalReplyReceipt();
        var room = ReadCompactRoomTaskFacts(GetComponent<ArdyRoomDialogueBridge>());
        var execution = PickRoomFields(room?["execution"] as JObject,
            "actionId", "intent", "targetKind", "sceneTargetId", "phase", "observedUtc");
        var devices = ReadHistoricalDeviceEvents();
        var recentDeviceOutcomes = new JArray();
        // Archive order is chronological ingestion order. Keep identity in the
        // key: a newer event for another entity cannot supersede this one.
        foreach (var group in devices.OfType<JObject>().Select((d, i) => new { d, i, entity =
            (string)d.SelectToken("lastAction.entityId") ?? (string)d.SelectToken("goal.boundEntityId") })
            .Where(x => !string.IsNullOrEmpty(x.entity)).GroupBy(x => x.entity))
        {
            var latest = group.Last();
            recentDeviceOutcomes.Add(new JObject {
                ["entityId"] = group.Key, ["historicalDeviceEventIndex"] = latest.i,
                ["recordedGeneration"] = latest.d["recordedGeneration"]?.DeepClone(),
                ["observedUtc"] = latest.d.SelectToken("lastAction.observedUtc")?.DeepClone(),
                ["desiredState"] = (latest.d.SelectToken("goal.desiredState") ?? latest.d.SelectToken("lastAction.requestedState"))?.DeepClone(),
                ["phase"] = latest.d.SelectToken("lastAction.phase")?.DeepClone(), ["historicalOnly"] = true
            });
        }
        return new JObject {
            ["source"] = "owner-validated-engine-event-index", ["replyGeneration"] = generation,
            ["newActionIdForThisReply"] = m_RoomTaskActionId,
            ["latestExecutionEvent"] = execution,
            ["currentUserRelation"] = PickRoomFields(room?["currentFacts"] as JObject,
                "currentTargetRevision", "observedAtRealtime", "currentlyNearAndFacingUser", "actualUserDistance", "desiredUserDistance", "actualFacingErrorDegrees"),
            ["executionBelongsToThisReply"] = !string.IsNullOrEmpty(m_RoomTaskActionId) &&
                (string)execution?["actionId"] == m_RoomTaskActionId,
            ["previousReply"] = previous == null ? null : PickRoomFields(previous,
                "generation", "userTurn", "recordedAtUtc", "completedPlaybackText"),
            ["latestDeviceEventByEntity"] = recentDeviceOutcomes,
            ["scope"] = "Past completion stays true without new action authority. currentUserRelation is the current measurement; a past arrival cannot establish present proximity. " +
                "Use the latest event for a latest-task report. Failure grants no retry; only executor receipts establish recovery. Playback does not prove hearing."
        };
    }
}
