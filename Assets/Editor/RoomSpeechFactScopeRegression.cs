using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Real room camera + actual pending public-review callbacks. No model HTTP or synthesized audio.</summary>
public static class RoomSpeechFactScopeRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        checks = 0;
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(),
            ["scope"] = "Real character RGB and real Chat room speech-review pipeline with controlled verdict callbacks. User camera transforms change while review is pending. No model semantic-quality, native movement or audible TTS claim." };
        Vector3 oldUserPosition = bridge.userHead.position;
        float oldObservationAge = (float)Get(chat, "m_SceneObservationMaxAgeSeconds");
        try
        {
            // Reproduce the misclassified inspect case from the user's bookcase log.
            yield return Prepare(chat, bridge, model, "inspect");
            int generation = (int)Get(chat, "m_FormalResponseGeneration"), history = model.m_DataList.Count;
            const string visualComment = "この棚は空いているわね。";
            var buffer = Buffer(visualComment);
            Call(chat, "CompleteRoomSpeech", buffer, visualComment, generation, true, null);
            var review = model.Decisions.Last();
            Require(review.Kind == "room-review" && !string.IsNullOrEmpty(review.Image), "Visual review receives actual current pixels.");
            int targetBefore = bridge.ReadCurrentTargetEvidence().currentTargetRevision;
            bridge.userHead.position += Vector3.right * .2f;
            Require(bridge.ReadCurrentTargetEvidence().currentTargetRevision != targetBefore, "A real camera translation changes the user target revision.");
            review.Complete(true, Verdict("consistent", "visual-observation"), "controlled-current-image-comment");
            Require(model.m_DataList.Count == history + 1 && model.m_DataList.Last().content == visualComment,
                "Unrelated user movement must not rewrite a pure scene comment as failed locomotion.");
            review.Complete(true, Verdict("consistent", "visual-observation"), "late-duplicate");
            Require(model.m_DataList.Count == history + 1, "Duplicate review cannot publish the statement twice.");
            ((JArray)LastReport["cases"]).Add("inspect-image-comment-survives-real-user-camera-movement");

            // Stage only the bridge's accepted-task receipt; no native movement
            // or HTTP request is issued. Run synchronously between Unity frames,
            // and restore every staged field before yielding again.
            foreach (string scenario in new[] { "target-and-phase-change", "paused", "failed", "replaced", "rejected", "live-claim" })
            {
                bridge.userHead.position = oldUserPosition;
                yield return Prepare(chat, bridge, model, "none");
                string oldAction = (string)Get(bridge, "actionId"), oldPhase = (string)Get(bridge, "phase");
                bool oldFollow = (bool)Get(bridge, "followActive"), oldActive = (bool)Get(bridge, "activeAction");
                try
                {
                    const string receiptAction = "speech-review-receipt-regression";
                    Set(bridge, "actionId", receiptAction); Set(bridge, "phase", "follow-waiting");
                    Set(bridge, "followActive", true); Set(bridge, "activeAction", false);
                    Set(chat, "m_RoomTaskIntent", "follow"); Set(chat, "m_RoomTaskDispatch", "follow-submitted");
                    Set(chat, "m_RoomTaskActionId", receiptAction);
                    generation = (int)Get(chat, "m_FormalResponseGeneration"); history = model.m_DataList.Count;
                    const string acknowledgement = "うん、ついていけるようにしてみるね。";
                    Call(chat, "CompleteRoomSpeech", Buffer(acknowledgement), acknowledgement, generation, true, null);
                    review = model.Decisions.Last(); int beforeCount = model.Decisions.Count;
                    Require((bool)JObject.Parse(review.Input)["acceptedRequest"]["available"], "The request carries the exact staged active receipt.");
                    bridge.userHead.position += Vector3.right * .2f;
                    Set(bridge, "phase", scenario == "paused" ? "follow-paused" : scenario == "failed" ? "failed" : "moving");
                    if (scenario == "replaced") Set(bridge, "actionId", "another-task");
                    var verdict = JObject.Parse(Verdict(scenario == "rejected" ? "inconsistent" : "consistent", "user-target"));
                    if (scenario != "rejected" && scenario != "live-claim") verdict["claimBasis"] = "accepted-request";
                    review.Complete(true, verdict.ToString(), "controlled-receipt-" + scenario);
                    if (scenario == "live-claim")
                    {
                        Require(model.Decisions.Count == beforeCount + 1 && model.m_DataList.Count == history,
                            "Live movement claims retain their target fence and one factual retry.");
                        model.Decisions.Last().Complete(true, Verdict("inconsistent", "user-target"), "controlled-live-claim-rejected");
                    }
                    else
                        Require(model.Decisions.Count == beforeCount, "Receipt/rejection handling never issues a redundant second model review.");
                    Require(model.m_DataList.Count == history + 1 &&
                        (model.m_DataList.Last().content == acknowledgement) == (scenario == "target-and-phase-change"),
                        "Only the still-healthy same-task receipt retains the role's original acknowledgement.");
                    review.Complete(true, verdict.ToString(), "duplicate-receipt");
                    Require(model.m_DataList.Count == history + 1 && (int)Get(chat, "m_RoomSpeechGuardGeneration") == -1,
                        "Duplicate receipt callbacks cannot republish or retain the owner.");
                    ((JArray)LastReport["cases"]).Add("receipt-review-" + scenario);
                }
                finally
                {
                    Set(bridge, "actionId", oldAction); Set(bridge, "phase", oldPhase);
                    Set(bridge, "followActive", oldFollow); Set(bridge, "activeAction", oldActive);
                }
            }

            // Movement language retains user dependencies even if an auxiliary
            // reviewer incorrectly tries to classify it as fact-free.
            bridge.userHead.position = oldUserPosition;
            yield return Prepare(chat, bridge, model, "none");
            Set(chat, "m_RoomTaskIntent", "approach"); Set(chat, "m_RoomTaskDispatch", "not-submitted");
            generation = (int)Get(chat, "m_FormalResponseGeneration"); history = model.m_DataList.Count;
            const string arrivalClaim = "あなたのそばに着いたわ。";
            Call(chat, "CompleteRoomSpeech", Buffer(arrivalClaim), arrivalClaim, generation, true, null);
            review = model.Decisions.Last(); int reviewCount = model.Decisions.Count;
            bridge.userHead.position += Vector3.right * .3f;
            review.Complete(true, Verdict("consistent", "none"), "controlled-stale-movement-claim");
            Require(model.Decisions.Count == reviewCount + 1 && model.m_DataList.Count == history,
                "A stale movement claim cannot drop its required target revision; it receives one new factual review.");
            var revised = model.Decisions.Last();
            Require((int)JObject.Parse(revised.Input)["reviewAttempt"] == 1, "The retry is explicit and bounded.");
            revised.Complete(true, Verdict("inconsistent", "user-target"), "controlled-no-arrival");
            Require(model.m_DataList.Count == history + 1 && model.m_DataList.Last().content != arrivalClaim &&
                !model.m_DataList.Last().content.Contains("路线"), "Rejected arrival is not remembered, and Japanese fallback does not switch to Chinese.");
            ((JArray)LastReport["cases"]).Add("user-arrival-keeps-required-target-fence-and-language");

            // Repeated real changes cannot create an unbounded review loop.
            bridge.userHead.position = oldUserPosition;
            yield return Prepare(chat, bridge, model, "inspect");
            generation = (int)Get(chat, "m_FormalResponseGeneration"); history = model.m_DataList.Count;
            Set(chat, "m_AgentCurrentRoundIsTick", true);
            const string routeComment = "近くへ行ける経路を確認したわ。";
            Call(chat, "CompleteRoomSpeech", Buffer(routeComment), routeComment, generation, true, null);
            review = model.Decisions.Last(); reviewCount = model.Decisions.Count;
            bridge.userHead.position += Vector3.right * .2f;
            review.Complete(true, Verdict("consistent", "user-target"), "controlled-first-route-change");
            Require(model.Decisions.Count == reviewCount + 1, "First relevant change re-reviews once.");
            revised = model.Decisions.Last(); bridge.userHead.position += Vector3.right * .2f;
            revised.Complete(true, Verdict("consistent", "user-target"), "controlled-second-route-change");
            Require(model.Decisions.Count == reviewCount + 1 && model.m_DataList.Count == history + 1 &&
                model.m_DataList.Last().content == "<silent/>", "Second relevant change ends with silent autonomy, not another review or fabricated route feedback.");
            Require(!(bool)Get(chat, "m_FormalResponseInFlight"), "Bounded review releases the formal owner.");
            ((JArray)LastReport["cases"]).Add("second-relevant-change-is-bounded-and-silent");

            bridge.userHead.position = oldUserPosition;
            yield return Prepare(chat, bridge, model, "inspect");
            generation = (int)Get(chat, "m_FormalResponseGeneration"); history = model.m_DataList.Count;
            Call(chat, "CompleteRoomSpeech", Buffer(visualComment), visualComment, generation, true, null);
            review = model.Decisions.Last();
            Call(chat, "InvalidateFormalResponse", "scope-review-user-cancel");
            review.Complete(true, Verdict("consistent", "visual-observation"), "controlled-late-cancelled-review");
            Require(model.m_DataList.Count == history, "A cancelled review does not publish or append assistant history.");
            ((JArray)LastReport["cases"]).Add("cancelled-scoped-review-cannot-publish");

            yield return Prepare(chat, bridge, model, "inspect");
            generation = (int)Get(chat, "m_FormalResponseGeneration"); history = model.m_DataList.Count;
            Call(chat, "CompleteRoomSpeech", Buffer(visualComment), visualComment, generation, true, null);
            review = model.Decisions.Last(); reviewCount = model.Decisions.Count;
            bridge.userHead.position += Vector3.right * .2f;
            // User movement alone does not expire the character's RGB. With
            // pre-audit preparation it correctly reuses a current frame, so this
            // cancellation case must actually need a new image.
            var expiring = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", generation);
            Set(chat, "m_SceneObservationMaxAgeSeconds", 2f);
            yield return Wait(() => !(bool)Call(chat, "IsFreshSceneObservation", expiring), "Actual pre-refresh image expiration");
            review.Complete(true, Verdict("consistent", "combined"), "controlled-refresh-before-close");
            Require((int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == generation,
                "A review requesting refreshed pixels retains an explicit formal owner.");
            Call(chat, "ResetSceneVision", "scope-review-close-during-refresh", true);
            for (int i = 0; i < 5; i++) yield return null;
            review.Complete(true, Verdict("consistent", "combined"), "late-pre-refresh-result");
            Require(model.m_DataList.Count == history && model.Decisions.Count == reviewCount &&
                !(bool)Get(chat, "m_FormalResponseInFlight") && (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1,
                "Closing vision while GPU refresh is pending releases ownership and cannot submit a stale second review.");
            ((JArray)LastReport["cases"]).Add("vision-close-during-review-refresh-releases-owner");
            Set(chat, "m_SceneObservationMaxAgeSeconds", oldObservationAge);

            yield return Prepare(chat, bridge, model, "inspect");
            generation = (int)Get(chat, "m_FormalResponseGeneration"); history = model.m_DataList.Count;
            Call(chat, "CompleteRoomSpeech", Buffer(visualComment), visualComment, generation, true, null);
            review = model.Decisions.Last();
            Call(chat, "OnApplicationPause", true);
            review.Complete(true, Verdict("consistent", "visual-observation"), "late-paused-review");
            Require(model.m_DataList.Count == history && !(bool)Get(chat, "m_FormalResponseInFlight") &&
                (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1,
                "Application pause cancels the pending review and releases the exact formal owner.");
            Call(chat, "OnApplicationPause", false); Call(chat, "ClearScheduledAgentWake");
            ((JArray)LastReport["cases"]).Add("application-pause-cancels-review-without-late-publication");
            LastReport["status"] = "passed";
        }
        finally
        {
            Call(chat, "OnApplicationPause", false);
            bridge.userHead.position = oldUserPosition;
            Set(chat, "m_SceneObservationMaxAgeSeconds", oldObservationAge);
            Call(chat, "InvalidateFormalResponse", "scope-review-finished"); chat.StopAllCoroutines(); Call(chat, "ClearScheduledAgentWake");
            LastReport["checks"] = checks;
            File.WriteAllText(Path.Combine(output, "room-speech-scope.json"), LastReport.ToString() + "\n");
        }
    }

    private static IEnumerator Prepare(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model, string intent)
    {
        Call(chat, "InvalidateFormalResponse", "scope-review-fixture-next"); chat.StopAllCoroutines(); Call(chat, "ClearScheduledAgentWake");
        Set(chat, "m_AgentRunning", true); Set(chat, "m_AgentCurrentRoundIsTick", false);
        Set(chat, "m_AgentRoundInFlight", false); Set(chat, "m_FormalResponseInFlight", false);
        Set(chat, "m_UserSpeechActiveForAutonomy", false); Set(chat, "m_AgentEyesOpen", true);
        Set(chat, "m_EnableScreenVision", true); Set(chat, "m_AgentGracefulShutdownPending", false);
        Set(chat, "m_WorkReviewOpen", false); Set(chat, "m_StreamComplete", true);
        Set(chat, "m_RoomTaskFormatError", false); Set(chat, "m_PendingTickCo", null);
        foreach (string field in new[] { "m_AudioSource", "m_HumStreamSecondaryAudioSource" })
        { var source = (AudioSource)Get(chat, field); if (source != null) { source.Stop(); source.clip = null; } }
        const string request = "请看看当前情况。";
        Call(chat, "RecordAcceptedMotionUserTurn", request);
        SceneVisionObservation observation = null;
        Call(chat, "RequestSceneObservation", "scope-review-source", new Action<SceneVisionObservation>(x => observation = x));
        yield return Wait(() => observation != null, "Real scoped review source");
        Call(chat, "StartStreaming", request, false, null, false, false, false, null, false, observation, true);
        var decision = model.Decisions.Last();
        decision.Complete(true, new JObject { ["intent"] = intent, ["origin"] = intent == "none" ? "none" : "user",
            ["evidence"] = intent == "none" ? "" : request }.ToString(), "controlled-review-scope-fixture");
        Require(model.Speech.Last().Image == observation.ImageDataUrl && !bridge.ReservesBody,
            "Review starts from actual formal dispatch without claiming a performed movement.");
    }
    private static SpeechTextBuffer Buffer(string text) { var result = new SpeechTextBuffer(); result.Append(new SpeechText(text, "ja", actionDependency: SpeechActionDependency.Independent)); return result; }
    private static string Verdict(string verdict, string scope) => new JObject { ["verdict"] = verdict, ["reason"] = "受控事实范围回归", ["factScope"] = scope }.ToString();
    private static IEnumerator Wait(Func<bool> ready, string label) { float end = Time.realtimeSinceStartup + 8f; while (!ready()) { if (Time.realtimeSinceStartup > end) throw new TimeoutException(label); yield return null; } }
    private static object Call(object target, string method, params object[] args) => NevaSceneVisionIntegrationRegression.Call(target, method, args);
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static void Require(bool valid, string message) { checks++; if (!valid) throw new InvalidOperationException(message); }
}
