using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Controlled execution facts and reviewer verdicts, actual Chat feedback buffering and scene RGB.
/// This verifies publication/ownership boundaries, not Qwen semantic accuracy or actual lamp operation.</summary>
public static class WorkSceneFeedbackRegression
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int checks;
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        checks = 0;
        LastReport = new JObject { ["status"]="running", ["cases"]=new JArray(),
            ["scope"]="Actual Chat work-feedback transport/buffering/review/publication and fresh real character images, with controlled spatial rejection facts and explicit reviewer verdicts. No remote model, semantic-review accuracy, audible TTS or physical lamp-switch evidence is claimed." };
        string reviewDirectory = Path.Combine(output, "review-live-inputs");
        Directory.CreateDirectory(reviewDirectory);
        var exportedReviews = new JObject { ["scope"]="Controlled rejection facts; real room RGB. Exact production pending reviewer request wires before any controlled verdict is delivered. Export only; no model HTTP is performed by this regression.", ["cases"]=new JArray() };
        object oldSelected = Get(chat, "m_SelectedSceneObject"), oldStateResult = Get(chat, "m_SceneObjectStateResult");
        try
        {
            foreach (string reason in new[] { "visible-object-not-confirmed: fixture pixel has no confirmed entity", "selected-object-has-no-registered-power-capability" })
            {
                yield return Prepare(chat, bridge, model, reason);
                int generation = (int)Get(chat, "m_FormalResponseGeneration"), history = model.m_DataList.Count;
                int reviewCount = model.Decisions.Count;
                Require(!(bool)Call(chat, "ShouldGuardRoomSpeech", generation) && (bool)Call(chat, "ShouldGuardWorkSceneFeedback", generation),
                    "A new formal generation remains guarded by the same user-work spatial result, not the expired original generation.");
                Call(chat, "DispatchWorkFeedback", "说明刚才未能重新开灯的实际结果。", null, generation);
                var request = model.Speech.Last();
                Require(request.Kind == "work-feedback" && !request.RecordAssistantHistory && !string.IsNullOrEmpty(request.Image),
                    "Guarded feedback requests an unrecorded candidate with this round's real image.");
                Require(request.Input.Contains("same-user-work-spatial-result-feedback") && request.Input.Contains(reason) &&
                    request.Input.Contains("newSpatialActionSubmittedByThisFeedback") && request.Input.Contains("physical mains power"),
                    "Feedback receives actual spatial rejection scope and separates scene-device evidence from imagined power wiring.");
                const string falseCause = "The lamp is unplugged, so it cannot turn on.";
                request.SpeechCallback(new SpeechText(falseCause, "en", actionDependency: SpeechActionDependency.Independent));
                Require(model.m_DataList.Count == history && model.Decisions.Count == reviewCount,
                    "A streaming candidate is neither remembered nor prematurely reviewed as a completed reply.");
                Require(((SpeechTextBuffer)Get(chat, "m_SentenceBuffer")).Length == 0 &&
                    ((ICollection)Get(chat, "m_PendingChunks")).Count == 0,
                    "Unreviewed scene feedback has not entered the public TTS sentence/clip pipeline.");
                request.SpeechCompleteCallback(falseCause);
                Require(model.Decisions.Count == reviewCount + 1 && model.m_DataList.Count == history,
                    "Completed work feedback enters the actual room speech reviewer before any assistant history write.");
                var review = model.Decisions.Last();
                var reviewInput = JObject.Parse(review.Input);
                Require(review.Kind == "room-review" && (string)reviewInput["intendedAction"] == "set-object-state" &&
                    (string)reviewInput["sceneActionFacts"]?["lastAction"]?["reason"] == reason,
                    "Reviewer sees device/selected-target facts instead of treating the reply as a generic user-route failure.");
                if (reason.StartsWith("visible-object-not-confirmed", StringComparison.Ordinal))
                    ExportReview(reviewDirectory, exportedReviews, review, "unsupported-physical-cause", falseCause, "inconsistent");
                review.Complete(true, Verdict("inconsistent", "scene-action"), "controlled-unsupported-physical-cause");
                Require(model.m_DataList.Count == history + 1 && model.m_DataList.Last().content != falseCause &&
                    model.m_DataList.Last().content != "<silent/>",
                    "An unsupported physical explanation is replaced by a factual public fallback even on a user-work tick.");
                Require(!(bool)Get(chat, "m_FormalResponseInFlight") && (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1,
                    "Audited work feedback releases its formal review owner.");
                review.Complete(true, Verdict("consistent", "none"), "late-duplicate-approval");
                Require(model.m_DataList.Count == history + 1, "A duplicate approval cannot remember or publish the rejected candidate later.");
                ((JArray)LastReport["cases"]).Add(new JObject { ["reason"]=reason, ["candidate"]=falseCause,
                    ["releasedHistory"]=model.m_DataList.Last().content, ["newSpatialActionSubmitted"]=false });
            }

            yield return Prepare(chat, bridge, model, "visible-object-not-confirmed: fixture pixel has no confirmed entity");
            int cancelledGeneration = (int)Get(chat, "m_FormalResponseGeneration"), before = model.m_DataList.Count;
            Call(chat, "DispatchWorkFeedback", "说明真实限制。", null, cancelledGeneration);
            var cancelled = model.Speech.Last();
            const string truthful = "I could not confirm the intended object, so I have not turned the lamp on.";
            cancelled.SpeechCallback(new SpeechText(truthful, "en", actionDependency: SpeechActionDependency.Independent));
            cancelled.SpeechCompleteCallback(truthful);
            var pendingReview = model.Decisions.Last();
            ExportReview(reviewDirectory, exportedReviews, pendingReview, "supported-target-unconfirmed", truthful, "consistent");
            Call(chat, "InvalidateFormalResponse", "work-feedback-user-cancel");
            pendingReview.Complete(true, Verdict("consistent", "scene-action"), "late-cancelled-feedback-review");
            cancelled.SpeechCallback(new SpeechText("late fragment", "en"));
            cancelled.SpeechCompleteCallback("late completion");
            Require(model.m_DataList.Count == before && !(bool)Get(chat, "m_FormalResponseInFlight") &&
                (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1,
                "Cancellation and delayed feedback/reviewer callbacks cannot revive a public reply or stale formal owner.");
            ((JArray)LastReport["cases"]).Add("cancelled-work-feedback-review-and-late-fragments");

            yield return Prepare(chat, bridge, model, "selected-object-has-no-registered-power-capability");
            cancelledGeneration = (int)Get(chat, "m_FormalResponseGeneration"); before = model.m_DataList.Count;
            int reviewsBeforeClose = model.Decisions.Count;
            Call(chat, "DispatchWorkFeedback", "解释已观察到的限制。", null, cancelledGeneration);
            cancelled = model.Speech.Last();
            Require((int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == cancelledGeneration,
                "The unspoken feedback candidate itself owns a cancelable formal reservation before review starts.");
            Call(chat, "ResetSceneVision", "work-feedback-close-before-candidate", true);
            cancelled.SpeechCallback(new SpeechText(truthful, "en", actionDependency: SpeechActionDependency.Independent));
            cancelled.SpeechCompleteCallback(truthful);
            Require(model.m_DataList.Count == before && model.Decisions.Count == reviewsBeforeClose &&
                !(bool)Get(chat, "m_FormalResponseInFlight") && !(bool)Get(chat, "m_AgentRoundInFlight") &&
                (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1,
                "Closing the scene source before the candidate returns releases ownership without a late review, history write or audio.");
            ((JArray)LastReport["cases"]).Add("source-close-during-feedback-candidate-releases-owner");

            yield return Prepare(chat, bridge, model, "visible-object-not-confirmed: old user target");
            Require((bool)Call(chat, "ShouldGuardWorkSceneFeedback", (int)Get(chat, "m_FormalResponseGeneration")),
                "The original user-work context is currently associated.");
            Set(chat, "m_WorkEpoch", (int)Get(chat, "m_WorkEpoch") + 1);
            Require(!(bool)Call(chat, "ShouldGuardWorkSceneFeedback", (int)Get(chat, "m_FormalResponseGeneration")),
                "A different user-work epoch cannot inherit an old lamp result as its own spatial task.");
            ((JArray)LastReport["cases"]).Add("new-user-work-epoch-does-not-inherit-device-guard");
            LastReport["status"]="passed";
        }
        finally
        {
            Call(chat, "InvalidateFormalResponse", "work-feedback-regression-finished"); chat.StopAllCoroutines();
            Call(chat, "ClearScheduledAgentWake");
            Set(chat, "m_SelectedSceneObject", oldSelected); Set(chat, "m_SceneObjectStateResult", oldStateResult);
            LastReport["checks"]=checks;
            LastReport["exportedReviewManifest"] = Path.Combine("review-live-inputs", "manifest.json");
            if ((string)LastReport["status"] == "running") LastReport["status"]="failed";
            File.WriteAllText(Path.Combine(output, "work-scene-feedback.json"), LastReport.ToString()+"\n");
        }
    }

    private static void ExportReview(string directory, JObject manifest, NevaSceneVisionIntegrationModel.Request review,
        string name, string candidate, string expectedVerdict)
    {
        // The wire is already serialized by the production Qwen request builder. Keep it byte-for-text
        // identical: no added model instruction, altered generation parameter, example verdict or image substitution.
        string file = name + ".request.json";
        File.WriteAllText(Path.Combine(directory, file), review.Wire);
        var input = JObject.Parse(review.Input);
        ((JArray)manifest["cases"]).Add(new JObject {
            ["name"]=name, ["requestFile"]=file, ["candidate"]=candidate, ["expectedVerdict"]=expectedVerdict,
            ["captureId"]=(string)input["visualObservation"]?["captureId"],
            ["controlledFailureReason"]=(string)input["sceneActionFacts"]?["lastAction"]?["reason"],
            ["source"]="actual pending RoomSpeechReview wire from controlled work-feedback fixture; exact current scene image"
        });
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifest.ToString()+"\n");
    }

    private static IEnumerator Prepare(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model, string reason)
    {
        var existing = (IEnumerator)typeof(RoomSpeechFactScopeRegression).GetMethod("Prepare", Flags)
            .Invoke(null, new object[] { chat, bridge, model, "inspect" });
        yield return existing;
        int originalGeneration = (int)Get(chat, "m_FormalResponseGeneration");
        var observation = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", originalGeneration);
        Require(observation?.HasImage == true, "Feedback fixture has a real current source image.");
        Set(chat, "m_RoomTaskIntent", "set-object-state"); Set(chat, "m_RoomTaskActionId", "controlled-work-device-result");
        Set(chat, "m_RoomTaskDispatch", "rejected: " + reason);
        var factType = typeof(ChatSample).GetNestedType("SceneActionFact", Flags);
        bool confirmedSelection = !reason.StartsWith("visible-object-not-confirmed", StringComparison.Ordinal);
        var fact = new JObject { ["actionId"]="controlled-work-device-result", ["intent"]="set-object-state",
            ["origin"]="user", ["captureId"]=observation.CaptureId, ["phase"]="rejected", ["reason"]=reason,
            ["entityId"]=confirmedSelection ? "controlled-selected-renderer-without-capability" : "",
            ["objectConfirmed"]=confirmedSelection, ["requestedState"]="on", ["stateChanged"]=false };
        Set(chat, "m_SceneActionFact", JsonUtility.FromJson(fact.ToString(), factType));
        Set(chat, "m_SelectedSceneObject", null); Set(chat, "m_SceneObjectStateResult", null);
        Call(chat, "RecordWorkRoomTaskContext");
        // Work continuations keep the existing user epoch but receive a new formal response identity.
        int generation = originalGeneration + 1;
        Set(chat, "m_FormalResponseGeneration", generation);
        Call(chat, "BindFormalSceneObservation", observation, generation);
        Set(chat, "m_AgentCurrentRoundIsTick", true);
        Set(chat, "m_RoomTaskFormatError", false);
    }

    private static string Verdict(string verdict, string scope) => new JObject { ["verdict"]=verdict,
        ["reason"]="controlled review of selected-target evidence; no power diagnosis observed", ["factScope"]=scope }.ToString();
    private static object Call(object target, string method, params object[] args) => NevaSceneVisionIntegrationRegression.Call(target, method, args);
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static void Require(bool valid, string reason) { checks++; if (!valid) throw new InvalidOperationException(reason); }
}
