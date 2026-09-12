using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using UnityEditor;
using UnityEngine;

/// <summary>Actual inactive ChatSample; no model calls, network, scene edits or locomotion claims.</summary>
public static class ArdyRoomDialogueProtocolRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    [Serializable] private sealed class Report
    {
        public bool passed;
        public int checks;
        public string failure, checkedAtUtc = DateTime.UtcNow.ToString("o"), unityVersion = Application.unityVersion;
        public string scope = "Production room protocol stream/quotation/schema cases and actual inactive ChatSample opt-in, identity, stale dispatch, pending upper-body repair, facts and cancellation; no model semantics or physical movement claims.";
    }
    private sealed class Fixture : IDisposable
    {
        public readonly GameObject host = new GameObject("InactiveRoomDialogueProtocolFixture");
        public readonly ChatSample chat;
        public readonly List<DialogueMotionIntent> actions = new List<DialogueMotionIntent>();
        public Fixture()
        {
            host.SetActive(false); chat = host.AddComponent<ChatSample>();
            Set(chat, "m_LogAgentLoop", false); Set(chat, "m_LogStreamTimings", false);
            Set(chat, "m_SystemNoticeMode", SystemNoticeMode.Hidden);
            chat.MotionIntentRequested += action => actions.Add(action);
            chat.ConfigureMotionOutput(true, true);
            Call(chat, "RecordAcceptedMotionUserTurn", "请走到我身边。");
            Call(chat, "BeginFormalResponseGeneration");
        }
        public void Submit(string tag)
        {
            var intent = (DialogueMotionIntent?)Call(chat, "ExtractDialogueMotion", new object[] { tag });
            Call(chat, "DispatchDialogueMotion", intent, false);
        }
        public string Context => (string)Call(chat, "BuildMotionRequestContext", "public synthetic context");
        public object Pending => Get(chat, "m_PendingMotionRepair");
        public void Dispose() => UnityEngine.Object.DestroyImmediate(host);
    }
    public static void RunInteractive()
    {
        checks = 0; var report = new Report();
        try
        {
            checks += ArdyRoomDialogueProtocolCases.Run();
            OptInAndIdentity(); IndependentFactsAndCancellation(); UpperBodyRepairIsolation(); ExistingArmConstraints(); SpeechPhaseIsolation();
            report.passed = true;
        }
        catch (Exception error) { report.failure = error.ToString(); throw; }
        finally
        {
            report.checks = checks;
            string path = Path.GetFullPath("Tools/MotionAdapter/reports/room-dialogue-protocol-regression.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, JsonUtility.ToJson(report, true) + "\n");
        }
        Debug.Log("[ArdyRoomDialogueProtocolRegression] passed " + checks + " checks.");
    }
    public static void RunBatch()
    {
        try { RunInteractive(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }
    private static void OptInAndIdentity()
    {
        using (var f = new Fixture())
        {
            Check(!f.chat.RoomMotionOutputEnabled && !f.Context.Contains(DialogueMotionProtocol.RoomOutputContract), "Unbound room capability advertised");
            f.Submit("<motion name=\"approach\"/>");
            Check(f.actions.Count == 0 && f.Pending == null, "Disabled room action executed or queued arm repair");
            f.chat.ConfigureRoomMotionOutput(true); Call(f.chat, "BeginFormalResponseGeneration");
            Check(f.chat.RoomMotionOutputEnabled && f.Context.Contains(DialogueMotionProtocol.RoomOutputContract), "Runtime opt-in missing");
            f.Submit("<motion name=\"approach\"/>"); f.Submit("<motion name=\"approach\"/>");
            Check(f.actions.Count == 1 && f.actions[0].IsRoomMotion && f.actions[0].ActionId.Length > 32 &&
                f.actions[0].ParentActionId == "" && f.actions[0].RepairAttempt == 0, "Room identity or deduplication broken");
            f.Submit("<motion name=\"stop-moving\"/>"); f.Submit("<motion name=\"approach\"/>");
            Check(f.actions.Count == 3 && f.actions[1].Name == "stop-moving" && f.actions[2].ActionId != f.actions[0].ActionId,
                "Explicit stop did not permit a fresh same-chain approach");
            var stale = f.actions[2]; Call(f.chat, "BeginFormalResponseGeneration");
            Call(f.chat, "DispatchDialogueMotion", (DialogueMotionIntent?)stale, false);
            Check(f.actions.Count == 3, "Stale speech generation dispatched room motion");
            var current = (DialogueMotionIntent?)Call(f.chat, "ExtractDialogueMotion", new object[] { "<motion name=\"approach\"/>" });
            Call(f.chat, "DispatchDialogueMotion", current, true);
            Check(f.actions.Count == 3, "Repeated response dispatched room motion");
            f.chat.ConfigureRoomMotionOutput(false); Call(f.chat, "DispatchDialogueMotion", current, false);
            Check(f.actions.Count == 3 && f.Pending == null && !f.Context.Contains(DialogueMotionProtocol.RoomOutputContract),
                "Room opt-out failed second dispatch gate or entered arm repair");
        }
        using (var f = new Fixture())
        {
            f.chat.ConfigureRoomMotionOutput(true);
            f.Submit("<motion name=\"approach\"/>"); f.Submit("<motion name=\"none\"/>"); f.Submit("<motion name=\"approach\"/>");
            Check(f.actions.Count == 2, "Upper-body stop cleared a running room command's deduplication");
            f.Submit("<motion name=\"nod\"/>"); f.Submit("<motion name=\"stop-moving\"/>"); f.Submit("<motion name=\"nod\"/>");
            Check(f.actions.Count == 4, "Room stop cleared an independent upper-body command's deduplication");
        }
    }
    private static void IndependentFactsAndCancellation()
    {
        using (var f = new Fixture())
        {
            f.chat.ConfigureRoomMotionOutput(true);
            const string facts = "{\"source\":\"room-motion\",\"status\":\"playing\",\"actionId\":\"test-room-action\"}";
            f.chat.MotionStateContextRequested += () => facts;
            var reasons = new List<string>(); f.chat.MotionCancelled += (_, reason) => reasons.Add(reason);
            foreach (string reason in new[] { "new-user-turn", "barge-in", "user-started-speaking" })
            {
                Call(f.chat, "CancelDialogueMotion", reason); Call(f.chat, "BeginFormalResponseGeneration");
                Check(reasons[reasons.Count - 1] == reason && f.Context.Contains(facts), "Ordinary chat lost room facts or cancellation reason");
            }
            f.Submit("<motion name=\"approach\"/>"); var intent = f.actions[0];
            f.chat.NotifyMotionFeedback(new ArdyMotionExecutionFeedback { actionId = intent.ActionId, name = intent.Name,
                parentActionId = "", responseGeneration = intent.ResponseGeneration, status = "rejected", canRepair = true });
            Check(f.Pending == null && Get(f.chat, "m_LastMotionExecutionFeedback") == null,
                "Room result entered upper-body feedback loop");
        }
    }
    private static void UpperBodyRepairIsolation()
    {
        using (var f = new Fixture())
        {
            f.chat.ConfigureRoomMotionOutput(true);
            f.Submit("<motion name=\"generate\" text=\"Raise the right hand beside the head.\" rightGoal=\"near-head\"/>");
            var arm = f.actions[0];
            f.chat.NotifyMotionFeedback(new ArdyMotionExecutionFeedback { actionId = arm.ActionId, name = arm.Name,
                parentActionId = "", responseGeneration = arm.ResponseGeneration, status = "rejected", canRepair = true,
                reason = "Synthetic measured executor rejection" });
            Check(f.Pending != null, "Fixture failed to queue original upper-body repair");
            f.Submit("<motion name=\"approach\"/>");
            Check(f.actions.Count == 2 && f.Pending == null && f.actions[1].Goal == null &&
                f.actions[1].ParentActionId == "" && f.actions[1].RepairAttempt == 0, "New approach inherited upper-body repair target or pending retry");
        }
        using (var f = new Fixture())
        {
            f.chat.ConfigureRoomMotionOutput(true);
            f.Submit("<motion name=\"generate\" text=\"Raise the right hand beside the head.\"/>"); var arm = f.actions[0];
            f.chat.NotifyMotionFeedback(new ArdyMotionExecutionFeedback { actionId = arm.ActionId, name = arm.Name,
                parentActionId = "", responseGeneration = arm.ResponseGeneration, status = "rejected", canRepair = true });
            var repair = f.Pending; Check(repair != null, "Missing callback repair fixture");
            Set(f.chat, "m_ActiveMotionRepair", repair); Set(f.chat, "m_PendingMotionRepair", null); Set(repair, "callbackStarted", true);
            f.Submit("<motion name=\"approach\"/>");
            Check(f.actions.Count == 1 && f.Pending == null, "Upper-body repair escaped into locomotion");
        }
    }
    private static void ExistingArmConstraints()
    {
        using (var f = new Fixture())
        {
            f.chat.ConfigureSemanticMotionPlanning(true); f.chat.ConfigureRoomMotionOutput(true);
            Call(f.chat, "RecordAcceptedMotionUserTurn", "右臂向外侧伸着。"); Call(f.chat, "BeginFormalResponseGeneration");
            var ledger = (ArdyActionConstraintLedger)Get(f.chat, "m_ActionConstraints");
            f.Submit("<motion name=\"plan\" purpose=\"display\" mode=\"hold\" scope=\"new\" right=\"outward\" end=\"hold\" lock=\"right\" userTurn=\"" +
                ledger.CurrentUserTurn + "\" evidence=\"右臂向外侧伸着。\"/>");
            Check(ledger.HasConstraints && f.actions.Count == 1, "Fixture failed to lock arm pose");
            f.Submit("<motion name=\"approach\"/>"); f.Submit("<motion name=\"stop-moving\"/>");
            Check(f.actions.Count == 3 && ledger.HasConstraints, "Room motion was blocked by or erased upper-body constraints");
        }
    }
    private static void SpeechPhaseIsolation()
    {
        using (var f = new Fixture())
        {
            f.chat.ConfigureRoomMotionOutput(true);
            var model = f.host.AddComponent<ChatQW>();
            var listener = (Action<string, string>)Delegate.CreateDelegate(typeof(Action<string, string>), f.chat,
                typeof(ChatSample).GetMethod("HandleLLMSpeechPhaseError", Flags));
            model.OnSpeechPhaseError += listener;
            int malformedEvents = 0, cancellations = 0;
            model.OnOutputFormatError += _ => malformedEvents++;
            f.chat.MotionCancelled += (_, __) => cancellations++;
            Set(f.chat, "m_AgentRunning", true); Set(f.chat, "m_AgentRoundInFlight", true);
            Set(f.chat, "m_UserTurnAwaitingReplySince", 1f);
            const string invalidRoom = "<lang code=\"zh\"/><speech mode=\"after_action\"/>我试着走近你。<motion name=\"approach\"/>";
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                Set(f.chat, "m_CurrentSpeechRoundId", attempt);
                Set(f.chat, "m_ToolCorrectionContinuationPending", false);
                var channels = RoleOutputChannels.Parse(invalidRoom);
                Call(model, "ReportMalformedRoleTool", channels);
                string frame = (string)Get(f.chat, "m_PendingToolCorrectionFrame");
                Check(frame.Contains("tool=speech") && frame.Contains("code=speech_phase_conflict") &&
                    frame.Contains("scope=speech_phase_only") && !frame.Contains("同轮必须提交可验证的工具请求"),
                    "Speech-only recovery told the role that its independently accepted movement failed or must be repeated");
                Check(model.m_DataList[model.m_DataList.Count - 1].content.Contains("不能使用 after_action") &&
                    malformedEvents == 0, "Speech phase lost precise correction or entered generic malformed-tool routing");
                object[] executable = { channels.ToExecutableText(true, true) };
                Call(f.chat, "ObserveSingingSpeechCompletion", executable);
                object[] spoken = { "我试着走近你。" };
                Call(f.chat, "ResolveSingingSpeech", spoken);
                Check((string)spoken[0] == "" && (bool)Get(f.chat, "m_SingingSpeechWithheld"),
                    "Recovery released room-only after_action speech through the singing gate");
                f.Submit((string)executable[0]);
            }
            Check(f.actions.Count == 1 && f.actions[0].Name == "approach" && cancellations == 0,
                "Speech correction duplicated or cancelled an eligible room command");
            string notice = (string)Get(f.chat, "m_PendingNonCharacterToolNotice");
            Check((bool)Get(f.chat, "m_ToolCorrectionExhaustedThisRound") && notice.Contains("发声阶段格式") &&
                notice.Contains("移动结果请查看房间移动状态") && !notice.Contains("未能完成本轮工具调用"),
                "Exhausted speech correction falsely reported locomotion failure");
            Set(f.chat, "m_CurrentSpeechRoundId", 3);
            object[] corrected = { RoleOutputChannels.Parse("<lang code=\"zh\"/><speech mode=\"independent\"/>我正在尝试靠近。").ToExecutableText(true, true) };
            Call(f.chat, "ObserveSingingSpeechCompletion", corrected);
            object[] correctionSpeech = { "我正在尝试靠近。" };
            Call(f.chat, "ResolveSingingSpeech", correctionSpeech);
            Check((string)correctionSpeech[0] == "我正在尝试靠近。" && !(bool)Get(f.chat, "m_SingingSpeechWithheld"),
                "A fresh independent explanation stayed blocked by the earlier phase failure");
            model.OnSpeechPhaseError -= listener;
        }
    }
    private static object Get(object value, string field) => value.GetType().GetField(field, Flags).GetValue(value);
    private static void Set(object value, string field, object data) => value.GetType().GetField(field, Flags).SetValue(value, data);
    private static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags).Invoke(value, args);
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
}
