using System.Linq;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private string m_RequestProgressSpeechPrefix;
    private JObject m_RequestProgressSpeechState;
    private int m_RequestProgressSpeechGeneration = -1;
    private int m_ProcessSpeechBindingGeneration=-1;
    private string m_ProcessSpeechBindingSource,m_ProcessSpeechBindingPrefix,m_ProcessSpeechBindingLanguage,m_ProcessSpeechBindingAction;

    private bool TryBindProcessSpeechStep(SpeechTextBuffer buffer,string executable,int generation,
        bool recordHistory,ObservationReceipt observations)
    {
        var step=BuildRoomSpeechProgressOwner(generation);
        string speech=buffer.ToString();
        if(step==null||m_ProcessSpeechBindingGeneration==generation&&m_ProcessSpeechBindingSource==speech&&
            m_ProcessSpeechBindingAction==(string)step["actionId"])return false;
        var model=m_ChatSettings?.m_ChatModel;
        if(model==null)return false;
        m_ProcessSpeechBindingGeneration=generation;m_ProcessSpeechBindingSource=speech;
        m_ProcessSpeechBindingAction=(string)step["actionId"];
        m_ProcessSpeechBindingPrefix=null;m_ProcessSpeechBindingLanguage=buffer.LanguageCode;
        var input=StepSpeechPartitionProtocol.Input(m_ActionConstraints.CurrentUserText,speech,step,
            BuildRoomRequestProgress()?["steps"] as JArray);
        int epoch=m_WorkEpoch,turn=m_ActionConstraints.CurrentUserTurn;
        bool completed=false;
        bool Current()=>generation==m_FormalResponseGeneration&&epoch==m_WorkEpoch&&turn==m_ActionConstraints.CurrentUserTurn&&
            m_ProcessSpeechBindingSource==speech&&m_ProcessSpeechBindingAction==(string)step["actionId"]&&
            model==m_ChatSettings?.m_ChatModel&&!m_UserSpeechActiveForAutonomy&&!m_SceneTimePaused&&isActiveAndEnabled&&
            GetComponent<ArdyRoomDialogueBridge>()?.ActionId==(string)step["actionId"]&&
            RoomRequestProgressOwnerCurrent((string)step["actionId"],generation);
        void Finish(bool ok,string raw,string status) {
            if(completed)return;completed=true;
            if(!Current()) {
                if(generation==m_FormalResponseGeneration&&epoch==m_WorkEpoch&&turn==m_ActionConstraints.CurrentUserTurn&&
                    m_ProcessSpeechBindingAction==(string)step["actionId"]&&model==m_ChatSettings?.m_ChatModel&&
                    !m_UserSpeechActiveForAutonomy&&!m_SceneTimePaused&&isActiveAndEnabled) {
                    m_FormalResponseInFlight=false;
                    m_WorkSubstepReviewPending=true;ScheduleNextTick(1f,"speech-step-owner-changed");
                    UnityEngine.Debug.Log("[Speech/StepBinding] generation="+generation+" status=owner-changed publication=False",this);
                }
                return;
            }
            string prefix=null,failure=null;
            bool bound=ok&&StepSpeechPartitionProtocol.Resolve(input,raw,out prefix,out failure);
            if(bound)m_ProcessSpeechBindingPrefix=prefix;
            UnityEngine.Debug.Log("[Speech/StepBinding] "+new JObject{["generation"]=generation,["step"]=step,
                ["bound"]=bound,["status"]=status,["failure"]=failure,["raw"]=raw,["originalSpeech"]=speech,
                ["independentSpeech"]=prefix,["scope"]="Text ownership only; execution continued. No factual preapproval or action permission."}.ToString(Newtonsoft.Json.Formatting.None),this);
            // A failed classification preserves original words under ongoing
            // monitoring and leaves delivery pending; it never deletes an
            // independent answer or marks that classification as checked.
            TryPublishProcessMonitoredSpeech(buffer,executable,generation,recordHistory,observations);
        }
        try {model.PostRoomPreparationMessage(input.ToString(Newtonsoft.Json.Formatting.None),Finish);}
        catch(System.Exception error) {Finish(false,"","speech-step-binding-exception:"+error.GetType().Name);}
        StartCoroutine(Deadline());
        System.Collections.IEnumerator Deadline() {
            float until=UnityEngine.Time.realtimeSinceStartup+3f;
            while(!completed&&Current()&&UnityEngine.Time.realtimeSinceStartup<until)yield return null;
            if(!completed)Finish(false,"","speech-step-binding-timeout");
        }
        return true;
    }

    private JObject BuildRoomSpeechProgressOwner(int generation)
    {
        if (generation != m_SpatialGoalTerminalReplyGeneration) return null;
        var body = BuildRoomRequestProgress()?["body"] as JObject;
        return body == null ? null : new JObject {
            ["actionId"] = body["actionId"]?.DeepClone(), ["intent"] = body["intent"]?.DeepClone(),
            ["targetKind"] = "user", ["actor"] = "CHARACTER", ["target"] = "USER",
            ["scope"] = "Only this current request's owned body step. Partition its spoken progress separately; this does not authorize another action."
        };
    }

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
                ["currentFacts"] = PickRoomFields(room["currentFacts"] as JObject, "currentlyNearAndFacingUser",
                    "userTrackingValid", "userSupportVerified", "actualUserDistance", "actualFacingErrorDegrees", "desiredUserDistance")
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
        if (intent == "approach" && (string)state["phase"] == "arrived")
            return ArrivalProgressText((string)state["arrivalRelation"], language);
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

    private static string ArrivalProgressText(string relation, string language)
    {
        bool ja = language == "ja", en = language == "en";
        if (relation == "near-facing") return ja ? "あなたのそばに着いたわ。" : en ? "I've reached your side." : "我已经到你身边了。";
        string arrived = ja ? "さっきあなたのそばに着いたわ。" : en ? "I reached your side a moment ago. " : "刚才已经到你身边了。";
        switch (relation)
        {
            case "distance-changed": return arrived + (ja ? "今はお互いの距離が変わっているわ。" :
                en ? "The distance between us has changed since then." : "现在我们之间的距离又变了。");
            case "facing-changed": return arrived + (ja ? "今はあなたの方をまっすぐ向いていないわ。" :
                en ? "I'm not directly facing you now." : "现在我没有正对着你。");
            case "tracking-unavailable": return arrived + (ja ? "ただ、今のあなたの位置は確認できないわ。" :
                en ? "I can't verify your current position now." : "不过现在无法确认你的当前位置。");
            default: return arrived + (ja ? "今もそばにいるかは、改めて確認が必要ね。" :
                en ? "Whether I'm still beside you needs a fresh check." : "此刻是否仍在你身边，还需要重新确认。");
        }
    }
}
