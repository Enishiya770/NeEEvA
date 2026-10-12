using System.Linq;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private string m_RequestProgressSpeechPrefix;
    private JObject m_RequestProgressSpeechState;
    private int m_RequestProgressSpeechGeneration = -1;

    private bool RoomRequestProgressOwnerCurrent(string actionId, int generation) =>
        RoomTaskSequenceCurrent() && m_RoomTaskSequence["steps"] is JArray steps &&
        steps.OfType<JObject>().Any(step => (string)step["actionId"] == actionId &&
            (int?)step["generation"] == generation &&
            new[] { "approach", "follow", "stop-moving" }.Contains((string)step["kind"]));

    private JObject BuildRoomRequestProgress()
    {
        var sequence = ReadRoomTaskSequence();
        if (!(sequence?["steps"] is JArray steps) || steps.Count < 2) return null;
        var room = ReadCompactRoomTaskFacts(GetComponent<ArdyRoomDialogueBridge>());
        var execution = room?["execution"] as JObject;
        var body = steps.OfType<JObject>().LastOrDefault(s =>
            new[] { "approach", "follow", "stop-moving" }.Contains((string)s["kind"]) &&
            !string.IsNullOrEmpty((string)s["actionId"]) && (string)s["actionId"] == (string)execution?["actionId"] &&
            (int?)s["generation"] == m_FormalResponseGeneration && (string)execution?["targetKind"] == "user");
        return new JObject {
            ["workEpoch"] = m_WorkEpoch, ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
            ["steps"] = new JArray(steps.OfType<JObject>().Select(s => {
                var summary = PickRoomFields(s, "id", "quote", "kind", "after", "status", "desiredState", "actionId", "goalId");
                if (s["receipt"] is JObject receipt)
                    summary["result"] = receipt["goal"] is JObject goal ?
                        PickRoomFields(goal, "goalId", "status", "desiredState", "completionEvidenceValid", "reason") :
                        PickRoomFields(receipt, "actionId", "phase", "reason", "observedUtc");
                return summary;
            })),
            ["body"] = body == null ? null : new JObject {
                ["stepId"] = body["id"].DeepClone(), ["intent"] = body["kind"].DeepClone(),
                ["actionId"] = body["actionId"].DeepClone(),
                ["execution"] = PickRoomFields(execution, "actionId", "targetKind", "phase", "reason", "operationRevision", "observedUtc"),
                ["currentFacts"] = PickRoomFields(room["currentFacts"] as JObject, "currentlyNearAndFacingUser")
            },
            ["scope"] = "Current request progress, not new action authority. Completed device steps do not complete pending questions or movements. Body status is reportable only for this step's current owned action; old completed receipts are historical."
        };
    }

    private bool UseTerminalResultReviewPacket(int generation)
    {
        string kind = (string)ReadCompactCurrentRequestSemantics(generation)?["requestKind"];
        return generation == m_SpatialGoalTerminalReplyGeneration && (kind == "action" || kind == "control") &&
            ReadRoomEvidenceReplyFacts(generation) == null;
    }

    private string AppendRoomRequestProgress(string text, string language, int generation)
    {
        m_RequestProgressSpeechPrefix = null; m_RequestProgressSpeechState = null; m_RequestProgressSpeechGeneration = -1;
        if (generation != m_SpatialGoalTerminalReplyGeneration) return text;
        var progress = BuildRoomRequestProgress();
        if (progress == null) return text;
        string report = null;
        if (progress["body"] is JObject body)
        {
            report = MovementProgressText(body, (string)body["intent"], (string)body["actionId"], language, false);
            m_RequestProgressSpeechState = MovementPlaybackStamp(body, (string)body["intent"], (string)body["actionId"]);
        }
        else if ((progress["steps"] as JArray)?.OfType<JObject>().Any(s => (string)s["status"] != "completed") == true)
            report = language == "ja" ? "依頼の続きはまだ完了していないわ。" :
                language == "en" ? "The remaining steps are not finished yet." : "后续步骤还没有完成。";
        if (string.IsNullOrEmpty(report)) return text;
        m_RequestProgressSpeechPrefix = text;
        m_RequestProgressSpeechGeneration = generation;
        return text + (language == "en" ? " " : "") + report;
    }

    private string MovementProgressText(JObject room, string intent, string actionId, string language, bool transition)
    {
        var state = MovementPlaybackStamp(room, intent, actionId);
        if (state == null) return null;
        bool ja = language == "ja", en = language == "en";
        string expected = (string)state["expectedReport"];
        if (intent == "stop-moving" && expected != "stopped")
            return ja ? "移動を止められたかは、まだ確認できないわ。" :
                en ? "I can't confirm that movement has stopped yet." : "我还不能确认移动已经停下。";
        switch (expected)
        {
            case "moving": return intent == "follow" ?
                (ja ? (transition ? "今はあなたについて歩いているわ。" : "あなたについて歩いているわ。") :
                 en ? (transition ? "I'm walking with you now." : "I'm following you.") :
                 transition ? "现在已经开始跟着你走了。" : "我正在跟着你走。") :
                ja ? "今、あなたのところへ向かっているわ。" : en ? "I'm walking over to you now." : "我正在往你身边走。";
            case "arrived": return ja ? "あなたのそばに着いたわ。" : en ? "I've reached your side." : "我已经到你身边了。";
            case "waiting": return ja ? "今はここで待っているわ。あなたが動いたら、またついていくわね。" :
                en ? "I'm waiting here now, ready to follow when you move." : "现在我先在这里等着，你移动时我会继续跟上。";
            case "stopped": return ja ? "今は移動を止めているわ。" : en ? "I've stopped moving now." : "现在已经停下来了。";
            case "preparing" when transition: return ja ? "今は歩く準備中で、まだ動き出していないわ。" :
                en ? "I'm preparing the next movement now, and haven't started it yet." : "现在正在准备下一段行走，暂时还没迈步。";
            default:
                string fallback = RoomMovementResultPolicy.Fallback(room, intent, actionId);
                if (fallback == null)
                    return ja ? "今あなたのそばにいるかどうかは、まだ確認できないわ。" :
                        en ? "I can't confirm that I'm beside you right now." : "我还不能确认现在已经到你身边。";
                string result = LocalizeRoomFallback(fallback, language);
                return !transition ? result : (ja ? "ただ、今は状況が変わったわ。" : en ? "The situation has changed. " : "不过，现在情况变了。") + result;
        }
    }
}
