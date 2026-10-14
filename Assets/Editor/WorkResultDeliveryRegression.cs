using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class WorkResultDeliveryRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    public static int RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        int checks = 0;
        void Check(bool test, string reason) { checks++; if (!test) throw new InvalidOperationException("Result delivery: " + reason); }
        object Get(string field) => typeof(ChatSample).GetField(field, Flags).GetValue(chat);
        var saved = new Dictionary<string, object>();
        void Set(string field, object value) { if (!saved.ContainsKey(field)) saved[field] = Get(field); typeof(ChatSample).GetField(field, Flags).SetValue(chat, value); }
        object Call(string method, params object[] args) => typeof(ChatSample).GetMethod(method, Flags).Invoke(chat, args);
        var oldReports = new Dictionary<string, string>((Dictionary<string, string>)Get("m_WorkDeliveredResults"));
        var oldSpeech = ((List<string>)Get("m_WorkCompletedSpeech")).ToArray();
        var oldVersionSpeech = new Dictionary<string, string>((Dictionary<string, string>)Get("m_WorkResultPlaybackText"));
        try
        {
            var ledger = new NeEEvA.Motion.ArdyActionConstraintLedger();
            ledger.RecordUserTurn("保存录音并报告结果，再告诉我采样率。");
            Set("m_ActionConstraints", ledger); Set("m_WorkEpoch", 8001);
            Set("m_WorkDeliveryEpoch", -1); Set("m_WorkDeliveryTurn", -1); Set("m_WorkDeliverySession", -1f);
            Set("m_LastFormalReplyReceipt", null); Set("m_PendingSelfInspection", "");
            Set("m_SpatialDeviceGoal", null); Set("m_WorkNoProgress", false); Set("m_WorkContinuations", 0);
            var sequence = new JObject { ["workEpoch"] = 8001, ["userTurn"] = ledger.CurrentUserTurn,
                ["request"] = ledger.CurrentUserText, ["steps"] = new JArray(new JObject {
                    ["id"] = "s1", ["kind"] = "other", ["status"] = "failed", ["actionId"] = "save-1",
                    ["after"] = new JArray(), ["receipt"] = new JObject { ["phase"] = "failed", ["reason"] = "output-handle-replaced", ["version"] = 1 }
                }), ["readyStepIds"] = new JArray() };
            Set("m_RoomTaskSequence", sequence); Set("m_RoomTaskSequenceBridge", bridge);
            Set("m_RoomTaskSequenceAvatar", bridge.avatar); Set("m_RoomTaskSequenceEnvironment", bridge.environmentRoot);
            Set("m_RoomTaskSequenceModel", ((ChatSetting)Get("m_ChatSettings")).m_ChatModel);
            var first = (JObject)Call("ReadWorkDelivery"); var result = (JObject)first["results"][0];
            string id = (string)result["id"], spoken = "保存は失敗しました。出力先が変更されたため、ファイルは保存されていません。";
            object Decision(string quote, string remaining = "") => JsonUtility.FromJson(new JObject {
                ["work_status"] = "continue", ["proceed"] = true, ["next_intent_source"] = "current-work", ["next_step_id"] = "",
                ["planning_focus"] = "other", ["intent"] = "Report result", ["remaining_request_quote"] = remaining,
                ["delivery_reports"] = new JArray(new JObject { ["id"] = id, ["quote"] = quote })
            }.ToString(), typeof(ChatSample).GetNestedType("AutonomyIntentDecision", BindingFlags.NonPublic));
            bool Apply(object decision) => (bool)Call("ApplyWorkDelivery", decision, sequence);
            Check(!Apply(Decision(spoken)), "Generated text cannot be counted as completed audio.");
            var premature = Decision(spoken);
            premature.GetType().GetField("work_status").SetValue(premature, "closed");
            Check(!Apply(premature) && (string)premature.GetType().GetField("work_status").GetValue(premature) == "continue",
                "A closure verdict cannot bypass actual playback evidence.");
            Call("RecordWorkCompletedSpeech", "保存は失敗しました。");
            Check(!Apply(Decision(spoken)), "An unfinished last chunk cannot certify the entire explanation.");
            Call("RecordWorkCompletedSpeech", "出力先が変更されたため、ファイルは保存されていません。");
            Check(!Apply(Decision(spoken, "告诉我采样率")), "A distinct unanswered question survives fully reported failure.");
            Check((bool)((JObject)Call("ReadWorkDelivery"))["results"][0]["delivered"], "Delivery is recorded independently of whole-work closure.");
            Check(Apply(Decision(spoken)), "Reported terminal result can settle without converting failure to success.");
            var invalid = Decision(spoken, "not a quote from this request");
            invalid.GetType().GetField("work_status").SetValue(invalid, "closed");
            Check(!Apply(invalid) && (string)invalid.GetType().GetField("work_status").GetValue(invalid) == "continue",
                "Invalid independent-request evidence cannot close the work or authorize an action.");
            Set("m_PendingSelfInspection", "音频采样率是 48000 Hz。");
            Set("m_LastSelfInspection", "音频采样率是 48000 Hz。");
            var tool = ((JArray)((JObject)Call("ReadWorkDelivery"))["results"]).OfType<JObject>().Single(r => (string)r["source"] == "selfInspection");
            string savedId = id; id = (string)tool["id"];
            Check(!Apply(Decision("音频采样率是 48000 Hz。")) && (string)Get("m_PendingSelfInspection") != "",
                "An unplayed tool answer remains pending.");
            Call("RecordWorkCompletedSpeech", "音频采样率是 48000 Hz。");
            Check(Apply(Decision("音频采样率是 48000 Hz。")) && (string)Get("m_PendingSelfInspection") == "" &&
                (string)Get("m_LastSelfInspection") == "音频采样率是 48000 Hz。", "Acknowledging one played tool result preserves its historical facts.");
            id = savedId;
            Check((string)sequence["steps"][0]["status"] == "failed", "Failure must remain failure.");
            sequence["coverage"] = new JArray(new JObject { ["quote"]="保存录音并报告结果",["kind"]="action",["stepIds"]=new JArray("s1") },
                new JObject { ["quote"]="再告诉我采样率",["kind"]="answer",["stepIds"]=new JArray() });
            Check(WorkResultDeliveryProtocol.IsSettledActionQuote("保存录音并报告结果。", sequence, new[]{"step:s1"}) &&
                !WorkResultDeliveryProtocol.IsSettledActionQuote("再告诉我采样率", sequence, new[]{"step:s1"}) &&
                !WorkResultDeliveryProtocol.IsSettledActionQuote("保存录音并报告结果，再告诉我采样率", sequence, new[]{"step:s1"}),
                "Only an exactly covered and delivered action clause is settled; independent/composite questions survive.");
            sequence["steps"][0]["receipt"]["version"] = 2;
            Check(!(bool)((JObject)Call("ReadWorkDelivery"))["results"][0]["delivered"], "A new outcome version must not inherit delivered status.");
            Check(!Apply(Decision(spoken)), "A stale result id cannot settle new facts.");
            id = (string)((JObject)Call("ReadWorkDelivery"))["results"][0]["id"];
            Check(!Apply(Decision(spoken)), "Even the new result id cannot borrow completed speech from before that version existed.");
            Call("RecordWorkCompletedSpeech", spoken);
            Check(Apply(Decision(spoken)), "The new version settles only after its own completed playback.");
            ledger.RecordUserTurn("再解释一下刚才保存为什么失败。");
            Call("EnsureWorkDeliveryOwner");
            Check(((Dictionary<string, string>)Get("m_WorkDeliveredResults")).Count == 0 && ((List<string>)Get("m_WorkCompletedSpeech")).Count == 0,
                "An explicit new question/turn owns a new delivery obligation.");
            return checks;
        }
        finally
        {
            foreach (var value in saved) typeof(ChatSample).GetField(value.Key, Flags).SetValue(chat, value.Value);
            var reports = (Dictionary<string, string>)Get("m_WorkDeliveredResults"); reports.Clear(); foreach (var pair in oldReports) reports[pair.Key] = pair.Value;
            var speech = (List<string>)Get("m_WorkCompletedSpeech"); speech.Clear(); speech.AddRange(oldSpeech);
            var versionSpeech = (Dictionary<string, string>)Get("m_WorkResultPlaybackText"); versionSpeech.Clear(); foreach (var pair in oldVersionSpeech) versionSpeech[pair.Key] = pair.Value;
        }
    }
}
