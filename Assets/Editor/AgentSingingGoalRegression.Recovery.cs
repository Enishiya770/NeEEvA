using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

// Intercepts every formal request at the LLM boundary. It deliberately consumes
// snapshots just like ChatQW, so a continuation cannot pass by reusing a property.
public sealed class RecordingRecoveryFixtureModel : LLM
{
    public readonly List<RecordingSingingDecisionContext> Snapshots = new List<RecordingSingingDecisionContext>();
    public readonly List<FormalResponseStage> Stages = new List<FormalResponseStage>();
    public override bool SupportsRecordingSingingDecision => true;
    private void Capture(FormalResponseStage stage)
    {
        Stages.Add(stage); Snapshots.Add(RecordingDecisionContext); RecordingDecisionContext = null;
    }
    public override void PostSpeechStreamForStage(string prompt, Action<SpeechText> part, Action<string> done,
        string image, bool history, FormalResponseStage stage) => Capture(stage);
    public override void PostSpeechFeedbackStream(string context, string feedback, Action<SpeechText> part,
        Action<string> done, string image = null) => Capture(RecordingDecisionContext != null
            ? FormalResponseStage.RecordingSingingDecision : FormalResponseStage.General);
    public override void PostSpeechFeedbackStream(string context, string feedback, Action<SpeechText> part,
        Action<string> done, string image, bool history) => PostSpeechFeedbackStream(context, feedback, part, done, image);
}

public static partial class AgentSingingGoalRegression
{
    // Existing real retained-audio fixture, no network/TTS/action callbacks. Run
    // synchronously before yielding so ChatSample.Start cannot bootstrap services.
    public static void RunRecordingRecoveryChecks(string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying)
            throw new InvalidOperationException("Use an isolated batch Play fixture; this check does not start Unity.");
        int before = checks;
        using (var f = new Fixture())
        {
            f.chat.enabled = false; f.sense.enabled = false;
            var model = f.host.AddComponent<RecordingRecoveryFixtureModel>(); model.enabled = false;
            var settings = (ChatSetting)Get(f.chat, "m_ChatSettings"); settings.m_ChatModel = model;
            var text = new GameObject("RecoveryFixtureText", typeof(RectTransform), typeof(Text)); text.transform.SetParent(f.host.transform);
            var button = new GameObject("RecoveryFixtureButton", typeof(RectTransform), typeof(Button)); button.transform.SetParent(f.host.transform);
            var input = new GameObject("RecoveryFixtureInput", typeof(RectTransform), typeof(InputField)); input.transform.SetParent(f.host.transform);
            Set(f.chat, "m_TextBack", text.GetComponent<Text>()); Set(f.chat, "m_CommitMsgBtn", button.GetComponent<Button>());
            Set(f.chat, "m_InputWord", input.GetComponent<InputField>());
            foreach (string name in new[] { "m_PersistSubtitleSettings", "m_EnableSubtitleTranslation", "m_PrewarmHumSVCOnSingingSkillLoad",
                "m_AutoStartHumSVC", "m_AutoStartSVS", "m_EnableScreenVision", "m_EnableMemoryRecall" }) Set(f.chat, name, false);
            f.host.SetActive(true); f.chat.enabled = true;
            Set(f.chat, "m_SceneTimePaused", true); Set(f.chat, "m_UserSpeechActiveForAutonomy", false);
            f.Begin("请把刚才录下的这一段唱给我听。");
            Set(f.chat, "m_UserTurnAwaitingReplySince", 1f); Set(f.chat, "m_FormalResponseGeneration", 71);
            Call(f.chat, "DispatchFormalStream", f.user, null, 71, true, "", true, true, true);
            Check(model.Stages.SequenceEqual(new[] { FormalResponseStage.RecordingSingingDecision }) &&
                model.Snapshots[0] != null && model.RecordingDecisionContext == null,
                "Initial real dispatch did not bind/consume a recording snapshot.");
            var old = model.Snapshots[0];
            string oldRevision = old.Materials.Single(m => m.ClipRef == f.first).Revision;
            Call(f.chat, "PreservePendingReplyForInputCandidate");
            Call(f.chat, "PreservePendingReplyForInputCandidate");
            Call(f.chat, "InvalidateFormalResponse", "regression-empty-input-candidate");
            Check(f.sense.TryPrepareSingingClip(f.first, false, "expanded", out _, out _), "Could not revise retained range.");
            string recovery = (string)Call(f.chat, "ConsumeInterruptedReplyContext");
            Check(recovery != null && recovery.Contains(f.user) && Call(f.chat, "ConsumeInterruptedReplyContext") == null,
                "Empty input lost the original question or allowed duplicate recovery.");
            Set(f.chat, "m_FormalResponseGeneration", 72);
            Call(f.chat, "DispatchFormalContinuation", recovery, null, 72);
            var fresh = model.Snapshots.Last();
            Check(model.Stages.Last() == FormalResponseStage.RecordingSingingDecision && !ReferenceEquals(old, fresh) &&
                fresh.Materials.Single(m => m.ClipRef == f.first).Revision != oldRevision,
                "Recovery fell back to General or reused a consumed/stale material revision.");
            Check(Get(f.chat, "m_SingingGoal") == null && Get(f.chat, "m_DeferredSingingRequest") == null &&
                !(bool)Get(f.chat, "m_SingingRequestSubmittedSinceUserTurn"), "Route recovery granted an action or review approval.");

            // A real same-generation correction rebuilds facts; an unrelated tick
            // cannot borrow this route just because the latest user text matches.
            Call(f.chat, "DispatchWorkFeedback", "真实校验失败，重新核对素材。", null, 72);
            Check(model.Stages.Last() == FormalResponseStage.RecordingSingingDecision &&
                !ReferenceEquals(fresh, model.Snapshots.Last()), "Correction lost the route or reused the old context.");
            fresh.OnLegacyHandoff();
            Check((bool)Call(f.chat, "HasCurrentRecordingDecisionRoute"), "Stale same-generation snapshot changed correction routing.");
            Check(!(bool)Call(f.chat, "ConsumeReviewedRecordingFeedback", 73, false), "Unreviewed later tick inherited the route.");
            Check(!(bool)Call(f.chat, "ConsumeReviewedRecordingFeedback", 72, true), "Recording route bypassed scene speech guard.");
            var selected = fresh.Materials.Single(m => m.ClipRef == f.first);
            JObject Plan(string key) => new JObject {
                ["decision"] = "perform", ["materials"] = new JArray(key), ["range"] = "expanded", ["intent"] = "perform",
                ["expected"] = "回唱指定的已有录音", ["user_evidence"] = f.user, ["thought"] = "",
                ["source_evidence"] = f.user,
                ["speech"] = new JObject { ["text"] = "", ["language"] = "zh" } };
            Check(CompileRecordingFixture(Plan(selected.Key), fresh, out string compiled, out _) &&
                compiled.Contains("refs=\"" + f.first + "\"") && compiled.Contains("revisions=\"" + selected.Revision + "\""),
                "Alias selection did not compile the fresh real reference/version.");
            Check(!CompileRecordingFixture(Plan("clip:49"), fresh, out _, out _),
                "Invented ref was silently substituted for a real recording.");

            // A validated unsupported-operation handoff can leave this stage;
            // after rebinding a newer owner, an older callback cannot clear it.
            var handoff = model.Snapshots.Last(); handoff.OnLegacyHandoff();
            Check(!(bool)Call(f.chat, "HasCurrentRecordingDecisionRoute"), "Legitimate handoff was forced back into recording IR.");
            Call(f.chat, "BindRecordingDecisionContext", true, 72);
            handoff.OnLegacyHandoff();
            Check((bool)Call(f.chat, "HasCurrentRecordingDecisionRoute"), "Stale handoff cleared a newer route owner.");
            Check(!JObject.FromObject(settings.m_ChatModel.RecordingDecisionContext).Properties().Any(p => p.Name == "OnLegacyHandoff"),
                "Runtime route callback leaked into serialized material facts.");

            foreach (string boundary in new[] { "new-input-same-text", "work-replaced", "model-replaced", "stop", "role-cancel", "session-stop" })
            {
                settings.m_ChatModel = model; f.Begin("请唱这一段，先不要继续其它事情。");
                Set(f.chat, "m_AgentRunning", true); Set(f.chat, "m_UserTurnAwaitingReplySince", 1f);
                Call(f.chat, "BindRecordingDecisionContext", true, 72);
                Call(f.chat, "PreservePendingReplyForInputCandidate");
                if (boundary == "new-input-same-text") Call(f.chat, "SetCurrentUserInput", f.user, f.user);
                if (boundary == "work-replaced") Call(f.chat, "ResetWorkReview", true);
                if (boundary == "model-replaced") settings.m_ChatModel = f.host.AddComponent<RecordingRecoveryFixtureModel>();
                if (boundary == "stop") Call(f.chat, "CancelSkillRuntimeActivity", "singing", "singing-stop-tool");
                if (boundary == "role-cancel") Check((bool)Call(f.chat, "ApplyRoleWorkCancellation", "先不要继续其它事情"), "Grounded cancellation failed.");
                if (boundary == "session-stop") Call(f.chat, "ResetWorkReviewForLoopBoundary");
                Check(!(bool)Call(f.chat, "HasCurrentRecordingDecisionRoute") && Call(f.chat, "ConsumeInterruptedReplyContext") == null,
                    "Obsolete recording recovery survived boundary=" + boundary);
            }
            f.chat.StopAllCoroutines(); f.chat.enabled = false;
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "recording-recovery-regression.json"), new JObject {
            ["passed"] = true, ["checks"] = checks - before,
            ["scope"] = "Production formal/continuation/correction dispatch, real retained material revisions, cancellation boundaries; fake LLM consumes snapshots; no HTTP, TTS or singing conversion." }.ToString());
    }
}
