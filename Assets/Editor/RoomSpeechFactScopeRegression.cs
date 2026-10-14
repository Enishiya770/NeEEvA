using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Real room camera + actual pending public-review callbacks. No model HTTP or synthesized audio.</summary>
public static partial class RoomSpeechFactScopeRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static JObject LastReport { get; private set; }
    public static JObject LastDecisionFailureReport { get; private set; }

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
            yield return RunDecisionFailureChecks(chat, bridge, model, output);
            LastReport["decisionFailureEvidence"] = LastDecisionFailureReport;
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
        yield return PrepareRoomPlanning(chat, bridge);
        var observation = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", (int)Get(chat, "m_FormalResponseGeneration"));
        const string request = "请看看当前情况。";
        var decision = model.Decisions.Last();
        decision.Complete(true, new JObject { ["intent"] = intent, ["origin"] = intent == "none" ? "none" : "user",
            ["evidence"] = intent == "none" ? "" : request }.ToString(), "controlled-review-scope-fixture");
        Require(model.Speech.Last().Image == observation.ImageDataUrl && !bridge.ReservesBody,
            "Review starts from actual formal dispatch without claiming a performed movement.");
    }

    private static IEnumerator PrepareRoomPlanning(ChatSample chat, ArdyRoomDialogueBridge bridge, string request = "请看看当前情况。", string evidence = null, Action beforePlanner = null)
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
        // Match both accepted-input operations from the real dialogue entry.
        // Disable only background work review for this controlled callback fixture;
        // keep its real work epoch, primary utterance and full evidence distinct.
        string fullInput = evidence ?? request;
        Call(chat, "SetCurrentUserInput", request, fullInput);
        Set(chat, "m_WorkReviewOpen", false);
        Call(chat, "RecordAcceptedMotionUserTurn", request);
        SceneVisionObservation observation = null;
        Call(chat, "RequestSceneObservation", "scope-review-source", new Action<SceneVisionObservation>(x => observation = x));
        yield return Wait(() => observation != null, "Real scoped review source");
        beforePlanner?.Invoke();
        Call(chat, "StartStreaming", fullInput, false, null, false, false, false, null, false, observation, true);
    }

    public static IEnumerator RunDecisionFailureChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        int beforeChecks = checks;
        bool previousAwareness = (bool)Get(chat, "m_EnableSceneObjectAwareness");
        // The live-input budget cases require actual candidate production even
        // when the caller is the older awareness-disabled baseline suite.
        Set(chat, "m_EnableSceneObjectAwareness", true);
        LastDecisionFailureReport = new JObject { ["status"] = "running", ["cases"] = new JArray(),
            ["scope"] = "Production planner callback to writer facts, speech review and localized fallback with controlled provider failures. No HTTP retry, model semantic-quality or audible TTS claim." };
        try
        {
            CheckOngoingSpeechDependencies(chat, bridge);
            foreach (string status in new[] {
                "room-task-http-invalid-request;attempts=1;recovery=not-eligible",
                "room-task-http-context-limit;attempts=2;recovery=exhausted" })
            {
                yield return PrepareRoomPlanning(chat, bridge);
                int generation = (int)Get(chat, "m_FormalResponseGeneration"), operations = bridge.Controller.OperationRevision;
                if (!Directory.Exists(Path.Combine(output, "live-producer-initial")))
                {
                    var captured = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", generation);
                    var audit = RoomDecisionBudgetRegression.AuditScene(chat, bridge, captured, "请看看当前情况。",
                        Path.Combine(output, "live-producer-initial"));
                    LastDecisionFailureReport["liveProducerInitialBudget"] = audit;
                    Require((int)audit["currentCandidateCount"] > 0,
                        "Live budget audit must include actual current object candidates, not an awareness-disabled empty catalog.");
                    Require((bool)audit["admitted"], "Complete live producer input and its actual scene image must pass the initial planning budget.");
                }
                model.Decisions.Last().Complete(false, "", status);
                var facts = (JObject)Call(chat, "ReadRoomTaskDecisionFailure");
                Require((string)facts?["kind"] == "service-request-failed" && (string)facts["wireStatus"] == status &&
                    (bool?)facts["newActionSubmittedByThisDecision"] == false && bridge.Controller.OperationRevision == operations,
                    "A failed provider decision records a current service failure without dispatching an action.");
                Require((int?)facts["providerDiagnostic"]["attempts"] == (status.Contains("attempts=2") ? 2 : 1),
                    "Provider retry receipt is preserved rather than inferred from the user's repeated request.");
                Require(model.Speech.Last().Context.Contains("service-request-failed") &&
                    model.Speech.Last().Context.Contains(status) && model.Speech.Last().Context.Contains("不把请求失败归咎于用户表达"),
                    "The first reply writer already receives the failure and cannot frame it as unclear user intent.");
                var observation = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", generation);
                var spatial = (JObject)Call(chat, "BuildSpatialAnswerFacts", observation);
                Require(JToken.DeepEquals(spatial["decisionFailure"], facts) &&
                    ((string)Call(chat, "BuildRoomTaskReplyFacts")).Contains(status),
                    "Ordinary and spatial answer facts retain the same current failed-decision receipt.");
                int history = model.m_DataList.Count;
                bool mistakenApproval = status.Contains("attempts=2");
                string falsePromise = mistakenApproval ? "接続が切れて、身体を動かせないの。" : "すぐに行くわ。";
                Call(chat, "CompleteRoomSpeech", Buffer(falsePromise), falsePromise, generation, true, null);
                var review = model.Decisions.Last();
                Require(JToken.DeepEquals(JObject.Parse(review.Input)["decisionFailure"], facts),
                    "Speech reviewer receives the action-planning failure rather than only old movement facts.");
                review.Complete(true, Verdict(mistakenApproval ? "consistent" : "inconsistent", "user-target"),
                    mistakenApproval ? "controlled-reviewer-incorrectly-approves-invented-cause" : "controlled-no-dispatched-action");
                Require(model.m_DataList.Count == history + 1 &&
                    model.m_DataList.Last().content == "動作を計画するサービスへのリクエストが失敗したので、新しい動作は始めていないわ。",
                    "The exact diagnosed service-failure report replaces a false promise or invented connection/body cause even if the reviewer wrongly approves it.");
                Set(chat, "m_FormalResponseGeneration", generation + 1);
                Require(Call(chat, "ReadRoomTaskDecisionFailure") == null, "A later generation cannot reuse the previous planning failure.");
                Set(chat, "m_FormalResponseGeneration", generation);
                Call(chat, "RecordAcceptedMotionUserTurn", "新的请求。");
                Require(Call(chat, "ReadRoomTaskDecisionFailure") == null, "A new user turn revokes the old planning failure even before a new formal generation.");
                ((JArray)LastDecisionFailureReport["cases"]).Add(status);
            }
            yield return PrepareRoomPlanning(chat, bridge);
            model.Decisions.Last().Complete(false, "", "room-task-http-context-limit;attempts=2;recovery=exhausted");
            int answerGeneration = (int)Get(chat, "m_FormalResponseGeneration");
            object previousGoalReview = Get(chat, "m_SpatialGoalLatestReview");
            try
            {
                Set(chat, "m_SpatialGoalLatestReview", new JObject { ["generation"] = answerGeneration,
                    ["answerRequired"] = true, ["responseMode"] = "answer" });
                const string establishedAnswer = "今の画像には棚が見えるわ。";
                Require((bool)Call(chat, "CurrentGenerationRequiresReviewedGoalAnswer", answerGeneration),
                    "A previously established current answer obligation remains distinct from a planner failure.");
                Call(chat, "CompleteRoomSpeech", Buffer(establishedAnswer), establishedAnswer, answerGeneration, true, null);
                model.Decisions.Last().Complete(true, Verdict("consistent", "visual-observation", "not-mentioned"), "controlled-established-answer-stays-answer");
                Require(model.m_DataList.Last().content == establishedAnswer,
                    "A correctly reviewed established question answer is not overwritten by the unavailable-planner report.");
                ((JArray)LastDecisionFailureReport["cases"]).Add("diagnosed-failure-preserves-established-answer");
            }
            finally { Set(chat, "m_SpatialGoalLatestReview", previousGoalReview); }
            foreach (string requestKind in new[] { "action", "control", "mixed" })
            {
                yield return PrepareRoomPlanning(chat, bridge, "为什么没完成？重新来一遍，向左转九十度。");
                int currentGeneration=(int)Get(chat, "m_FormalResponseGeneration");
                Call(chat, "RememberFirstReplyRequestSemantics", new FirstReplyCompletion {
                    Disposition=FirstReplyDisposition.Tools, RequestKind=requestKind }, currentGeneration, model);
                model.Decisions.Last().Complete(false, "", "room-task-http-context-budget;attempts=0;recovery=preflight-budget");
                object oldReview=Get(chat, "m_SpatialGoalLatestReview");
                object oldTerminal=Get(chat, "m_SpatialGoalTerminalReplyGeneration");
                try
                {
                    Set(chat, "m_SpatialGoalLatestReview", new JObject { ["generation"]=currentGeneration,
                        ["answerRequired"]=true, ["responseMode"]="answer" });
                    if (requestKind == "control") Set(chat, "m_SpatialGoalTerminalReplyGeneration", currentGeneration);
                    const string oldAnswer="探してみたけれど、まだ見つからないわ。";
                    int historyBefore=model.m_DataList.Count;
                    int reviewsBefore=model.Decisions.Count;
                    Call(chat, "CompleteRoomSpeech", Buffer(oldAnswer), oldAnswer, currentGeneration, true, null);
                    if (requestKind == "mixed")
                    {
                        var currentReview=model.Decisions.Last();
                        var inputFacts=JObject.Parse(currentReview.Input);
                        Require(model.Decisions.Count == reviewsBefore+1 &&
                            (string)inputFacts["currentRequestSemantics"]?["requestKind"] == requestKind,
                            "Mixed request evidence still reaches the actual speech reviewer unchanged.");
                        // Reproduce the reviewer masking an unexecuted current
                        // command with a coherent but answer-only old result.
                        currentReview.Complete(true, Verdict("consistent", "scene-action", "not-mentioned"), "controlled-old-goal-mask");
                    }
                    else Require(model.Decisions.Count == reviewsBefore,
                        "Pure action/control failure uses the local receipt without another model review.");
                    Require(model.m_DataList.Count == historyBefore+1 &&
                        model.m_DataList.Last().content.Contains("情報が多すぎて") &&
                        model.m_DataList.Last().content != oldAnswer,
                        "A current budget failure beats both old answer obligations and old terminal results.");
                    Require((bool)Call(chat, "CurrentRequestRequiresActionOrControl", currentGeneration),
                        "Failure reporting never rewrites the user's action classification.");
                }
                finally
                {
                    Set(chat, "m_SpatialGoalLatestReview", oldReview);
                    Set(chat, "m_SpatialGoalTerminalReplyGeneration", oldTerminal);
                }
                ((JArray)LastDecisionFailureReport["cases"]).Add("current-"+requestKind+"-failure-beats-old-goal");
            }
            yield return PrepareRoomPlanning(chat, bridge, "重新转一下身。");
            int naturalFailureGeneration=(int)Get(chat, "m_FormalResponseGeneration");
            Call(chat, "RememberFirstReplyRequestSemantics", new FirstReplyCompletion {
                Disposition=FirstReplyDisposition.Tools, RequestKind="action" }, naturalFailureGeneration, model);
            model.Decisions.Last().Complete(false, "", "room-task-http-context-budget;attempts=0;recovery=preflight-budget");
            const string naturalFailure="頭の中を整理したら、ちゃんと灯りを点けるね。";
            object naturalOldTerminal=Get(chat, "m_SpatialGoalTerminalReplyGeneration");
            Set(chat, "m_SpatialGoalTerminalReplyGeneration", naturalFailureGeneration);
            int reviewsBeforeLocalReceipt = model.Decisions.Count;
            Call(chat, "CompleteRoomSpeech", Buffer(naturalFailure), naturalFailure, naturalFailureGeneration, true, null);
            Set(chat, "m_SpatialGoalTerminalReplyGeneration", naturalOldTerminal);
            Require(model.m_DataList.Last().content == "動作を考えるための情報が多すぎて計画を作れなかったので、まだ新しい動作は始めていないわ。" &&
                model.Decisions.Count == reviewsBeforeLocalReceipt,
                "Pure action failure reports the engine receipt locally, with no review and no invented promise, even with an old terminal obligation.");
            ((JArray)LastDecisionFailureReport["cases"]).Add("pure-action-local-receipt-no-model-promise-or-review");
            foreach (string scope in new[] { "visual-observation", "none", "scene-action", "user-target", "combined" })
            {
                yield return PrepareRoomPlanning(chat, bridge, scope == "none" ? "我只是想和你聊天。" : "现在你能看到什么？");
                model.Decisions.Last().Complete(false, "", "room-task-http-context-budget;attempts=0;recovery=preflight-budget");
                int freshAnswerGeneration = (int)Get(chat, "m_FormalResponseGeneration"), operation = bridge.Controller.OperationRevision;
                const string freshAnswer = "我能根据当前画面回答你的问题。";
                Require(!(bool)Get(chat, "m_RoomTaskSpatialAnswer") &&
                    !(bool)Call(chat, "CurrentGenerationRequiresReviewedGoalAnswer", freshAnswerGeneration),
                    "This fresh question has no pre-established planner/goal answer exemption.");
                Call(chat, "CompleteRoomSpeech", Buffer(freshAnswer), freshAnswer, freshAnswerGeneration, true, null);
                var review = model.Decisions.Last();
                var schema = JObject.Parse(RoomTaskProtocol.BuildSpeechReviewSchemaForInput(review.Input));
                Require(((JArray)schema["required"]).Values<string>().Contains("requestDisposition"),
                    "A failed planner requires independent classification of the complete real question.");
                var verdict = JObject.Parse(Verdict("consistent", scope)); verdict["requestDisposition"] = "answer-only"; verdict["planningFailureReport"] = "not-mentioned";
                review.Complete(true, verdict.ToString(), "fresh-answer-only-after-planner-failure");
                Require(model.m_DataList.Last().content == freshAnswer && bridge.Controller.OperationRevision == operation,
                    "An audited fresh visual/chat answer survives budget failure without movement or a pre-created answer flag.");
                ((JArray)LastDecisionFailureReport["cases"]).Add("fresh-" + scope + "-survives-planning-failure");
            }
            foreach (string disposition in new[] { "action-request", "uncertain" })
            {
                yield return PrepareRoomPlanning(chat, bridge, "告诉我看到了什么，然后来我身边。");
                model.Decisions.Last().Complete(false, "", "room-task-http-context-budget;attempts=0;recovery=preflight-budget");
                int mixedGeneration = (int)Get(chat, "m_FormalResponseGeneration");
                const string incompleteAnswer = "画面里有架子。";
                Call(chat, "CompleteRoomSpeech", Buffer(incompleteAnswer), incompleteAnswer, mixedGeneration, true, null);
                var verdict = JObject.Parse(Verdict("consistent", "visual-observation")); verdict["requestDisposition"] = disposition;
                model.Decisions.Last().Complete(true, verdict.ToString(), "mixed-action-not-hidden-by-visual-claim");
                Require(model.m_DataList.Last().content != incompleteAnswer && model.m_DataList.Last().content.Contains("計画"),
                    "Visual-only candidate cannot hide a failed mixed/action request or uncertain request classification.");
                ((JArray)LastDecisionFailureReport["cases"]).Add(disposition + "-keeps-diagnosed-failure");
            }
            LastDecisionFailureReport["degradationScope"] = "New evidence-only requests bypass planning. On legacy/failed planner routes, an independently reviewed answer-only request with consistent visual/general claims survives. Mixed, action and uncertain requests retain exact diagnosed failure; no action authority comes from a speech review.";
            yield return PrepareRoomPlanning(chat, bridge);
            const string budgetStatus = "room-task-http-context-budget;attempts=0;recovery=preflight-budget";
            int budgetOperations = bridge.Controller.OperationRevision;
            model.Decisions.Last().Complete(false, "", budgetStatus);
            var budgetFacts = (JObject)Call(chat, "ReadRoomTaskDecisionFailure");
            Require((string)budgetFacts?["kind"] == "input-budget-exceeded" &&
                (string)budgetFacts["stage"] == "decision-preflight" &&
                (int?)budgetFacts["providerDiagnostic"]["attempts"] == 0 &&
                (bool?)budgetFacts["providerDiagnostic"]["httpRequestSent"] == false,
                "A local oversized essential-input refusal records zero HTTP attempts, not a provider failure.");
            Require(model.Speech.Last().Context.Contains("input-budget-exceeded") &&
                model.Speech.Last().Context.Contains("未发送请求"),
                "The result writer receives the local cause before it can invent a service outage.");
            int budgetGeneration = (int)Get(chat, "m_FormalResponseGeneration");
            const string falseBudgetCause = "ネットワークが切れたから動けないわ。";
            Call(chat, "CompleteRoomSpeech", Buffer(falseBudgetCause), falseBudgetCause, budgetGeneration, true, null);
            model.Decisions.Last().Complete(true, Verdict("inconsistent", "user-target"), "controlled-local-budget-no-network-outage");
            Require(model.m_DataList.Last().content == "動作を考えるための情報が多すぎて計画を作れなかったので、まだ新しい動作は始めていないわ。" &&
                bridge.Controller.OperationRevision == budgetOperations,
                "Rejected network excuse falls back to the actual local input failure without dispatching an action.");
            ((JArray)LastDecisionFailureReport["cases"]).Add("local-input-budget-no-http-request");
            bool previousFirstReplySetting = (bool)Get(chat, "m_EnableFirstReplyRouting");
            try
            {
                Set(chat, "m_EnableFirstReplyRouting", true);
                model.EnableFirstReply = true;
                foreach (string question in new[] { "现在你能看到什么？", "什么情况啊？我就是单纯在跟你聊天，问你能看到什么。", "你刚才为什么说信息太多？" })
                {
                    int decisionsBefore = model.Decisions.Count, speechBefore = model.Speech.Count;
                    yield return PrepareRoomPlanning(chat, bridge, question);
                    int generation = (int)Get(chat, "m_FormalResponseGeneration"), operations = bridge.Controller.OperationRevision;
                    Require(model.FirstReplies.Last().Input.Contains(question) && model.Decisions.Count == decisionsBefore,
                        "The complete fresh user question enters production StartStreaming/FirstReply without pre-creating a planner answer.");
                    model.FirstReplies.Last().FirstReplyCallback(new FirstReplyCompletion { Disposition = FirstReplyDisposition.Evidence });
                    yield return Wait(() => model.Speech.Count == speechBefore + 1, "Evidence-only writer dispatch");
                    var speech = model.Speech.Last();
                    var evidenceFacts = (JObject)Call(chat, "ReadRoomEvidenceReplyFacts", generation);
                    Require(model.Decisions.Count == decisionsBefore && speech.Stage == FormalResponseStage.CommittedSpatialReply &&
                        !string.IsNullOrEmpty(speech.Image) && (bool?)evidenceFacts?["actionAuthorization"] == false &&
                        Call(chat, "ReadRoomTaskDecisionFailure") == null && bridge.Controller.OperationRevision == operations,
                        "A fresh evidence question sends its actual image directly to the closed writer with no planner, action or current planning failure.");
                    Require(evidenceFacts["previousPlanningFailure"]?["historical"]?.Value<bool>() == true &&
                        (string)evidenceFacts["previousPlanningFailure"]["receipt"]["kind"] == "input-budget-exceeded",
                        "The previous failure remains a dated historical receipt for why-questions, never a current answer failure.");
                    const string answer = "当前画面里的内容可以直接说明。";
                    bool historical = question == "你刚才为什么说信息太多？";
                    string actualAnswer = historical ? "刚才那次规划在本地因输入预算不足而中止，并不是网络断开。" : answer;
                    float maxAge = (float)Get(chat, "m_SceneObservationMaxAgeSeconds");
                    try
                    {
                        // Keep eyes open but make every current frame unavailable to
                        // review. A historical diagnostic must not depend on pixels.
                        if (historical) Set(chat, "m_SceneObservationMaxAgeSeconds", -1f);
                        speech.SpeechCallback(new SpeechText(actualAnswer, "zh", actionDependency: SpeechActionDependency.Independent));
                        speech.SpeechCompleteCallback(actualAnswer);
                        yield return Wait(() => model.Decisions.Count == decisionsBefore + 1, "Evidence speech review");
                        var review = model.Decisions.Last();
                        Require(review.Kind == "room-review" && JObject.Parse(review.Input)["readOnlyEvidenceReply"] != null,
                            "The only room-model request is factual speech review, with read-only route and historical failure provenance.");
                        Require(!historical || (chat.SceneEyesOpen && string.IsNullOrEmpty(review.Image) &&
                            JObject.Parse(review.Input)["visualObservation"].Type == JTokenType.Null),
                            "Historical diagnostics can be reviewed while eyes are open but a current image is unavailable; stale pixels are not passed.");
                        review.Complete(true, Verdict("consistent", historical ? "scene-action" : "visual-observation",
                            historical ? "input-budget-exceeded" : "not-mentioned"), "evidence-question-reviewed");
                        Require(model.m_DataList.Last().content == actualAnswer && bridge.Controller.OperationRevision == operations &&
                            (string)Get(chat, "m_RoomTaskDispatch") == "read-only-evidence",
                            "Actual writer completion and speech release preserve the answer without issuing room commands, including historical cause without current pixels.");
                    }
                    finally { Set(chat, "m_SceneObservationMaxAgeSeconds", maxAge); }
                    ((JArray)LastDecisionFailureReport["cases"]).Add("evidence-route:" + question);
                }
                yield return RunTypedEvidenceInputChecks(chat, bridge, model);
                yield return RunEvidenceCaptureOwnerChecks(chat, bridge, model);
                yield return RunEvidenceRepairChecks(chat, bridge, model);
            }
            finally { model.EnableFirstReply = false; Set(chat, "m_EnableFirstReplyRouting", previousFirstReplySetting); }
            yield return PrepareRoomPlanning(chat, bridge, "来我身边。");
            var approachObservation = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", (int)Get(chat, "m_FormalResponseGeneration"));
            var approachAudit = RoomDecisionBudgetRegression.AuditScene(chat, bridge, approachObservation, "来我身边。",
                Path.Combine(output, "live-producer-approach"));
            LastDecisionFailureReport["liveProducerApproachBudget"] = approachAudit;
            Require((bool)approachAudit["admitted"], "The complete live action-request producer also fits admission; no avatar action is dispatched by this audit.");
            foreach (string invalidResponse in new[] { "room-task-invalid-envelope", "room-task-empty-output" })
            {
                yield return PrepareRoomPlanning(chat, bridge);
                model.Decisions.Last().Complete(false, "", invalidResponse);
                var invalidFacts = (JObject)Call(chat, "ReadRoomTaskDecisionFailure");
                Require((string)invalidFacts?["kind"] == "decision-not-executable" && invalidFacts["providerDiagnostic"] == null &&
                    (string)Call(chat, "RoomTaskFallback", bridge) == "这次没有得到可执行的动作计划，没有启动新的动作。",
                    "An HTTP200 invalid/empty envelope must not be presented as a transport outage or unclear user intent.");
                ((JArray)LastDecisionFailureReport["cases"]).Add(invalidResponse);
            }
            yield return PrepareRoomPlanning(chat, bridge);
            model.Decisions.Last().Complete(false, "", "room-task-http-invalid-request;attempts=1;recovery=not-eligible");
            int timeoutGeneration = (int)Get(chat, "m_FormalResponseGeneration"), timeoutOperations = bridge.Controller.OperationRevision;
            string previousLanguage = model.DialogueLanguage;
            typeof(LLM).GetField("lan", Flags).SetValue(model, "日语");
            try
            {
                Call(chat, "HandleResponseGenerationFailure", "no-public-output-deadline");
                Call(chat, "CompleteRoomSpeech", new SpeechTextBuffer(), "", timeoutGeneration, true, null);
                Require(model.m_DataList.Last().content == "動作を計画するサービスへのリクエストが失敗したので、新しい動作は始めていないわ。返事の生成が時間切れになってしまったわ。" &&
                    bridge.Controller.OperationRevision == timeoutOperations,
                    "No-public-speech timeout uses the configured role language, reports both distinct failures, and does not dispatch another action.");
            }
            finally { typeof(LLM).GetField("lan", Flags).SetValue(model, previousLanguage); }
            ((JArray)LastDecisionFailureReport["cases"]).Add("empty-timeout-keeps-role-language-and-action-facts");
            foreach (string incomplete in new[] { "http-failure", "incomplete-output", "context-budget", "invalid-result-context" })
            {
                yield return PrepareRoomPlanning(chat, bridge);
                model.Decisions.Last().Complete(false, "", "room-task-http-invalid-request;attempts=1;recovery=not-eligible");
                int incompleteGeneration = (int)Get(chat, "m_FormalResponseGeneration");
                int reviewCount = model.Decisions.Count, operationCount = bridge.Controller.OperationRevision;
                Call(chat, "HandleResponseGenerationFailure", incomplete);
                Call(chat, "CompleteRoomSpeech", Buffer("すぐに行くわ。"), "", incompleteGeneration, true, null);
                Require(model.Decisions.Count == reviewCount && bridge.Controller.OperationRevision == operationCount &&
                    model.m_DataList.Last().content == "動作を計画するサービスへのリクエストが失敗したので、新しい動作は始めていないわ。" + (incomplete == "http-failure" ? "返事を生成するサービスへのリクエストが失敗したわ。" : incomplete == "context-budget" ? "情報量が返事の処理枠を超えたわ。" : "返事を最後まで生成できなかったわ。"),
                    "A broken/truncated stream discards its buffered promise without a semantic review or new action.");
                var replyFailure = ((JObject)Call(chat, "ReadPreviousFormalReplyReceipt"))["replyFailure"];
                Require((string)replyFailure["reason"] == incomplete && (bool?)replyFailure["httpRequestSent"] ==
                    (incomplete != "context-budget" && incomplete != "invalid-result-context"), "Final receipt retains typed local versus HTTP reply failures independently of planning.");
                ((JArray)LastDecisionFailureReport["cases"]).Add(incomplete + "-discards-buffered-candidate");
            }
            yield return Prepare(chat, bridge, model, "none");
            Require(Get(chat, "m_RoomTaskDecisionFailure") == null && Call(chat, "ReadRoomTaskDecisionFailure") == null,
                "Starting a successful new planning transaction clears the old transport failure.");
            ((JArray)LastDecisionFailureReport["cases"]).Add("fresh-planning-clears-prior-failure");
            yield return PrepareRoomPlanning(chat, bridge);
            var cancelled = model.Decisions.Last(); int speechCount = model.Speech.Count;
            Call(chat, "InvalidateFormalResponse", "cancelled-decision-failure-fixture");
            cancelled.Complete(false, "", "room-task-http-connection;attempts=1;recovery=not-eligible");
            Require(Call(chat, "ReadRoomTaskDecisionFailure") == null && model.Speech.Count == speechCount,
                "A late failed callback from a cancelled planner cannot publish facts or start a new reply.");
            ((JArray)LastDecisionFailureReport["cases"]).Add("cancelled-failure-callback-stays-revoked");
            yield return RunReplyReceiptChecks(chat, bridge, model, output);
            LastDecisionFailureReport["status"] = "passed";
        }
        finally
        {
            Set(chat, "m_EnableSceneObjectAwareness", previousAwareness);
            LastDecisionFailureReport["checks"] = checks - beforeChecks;
            File.WriteAllText(Path.Combine(output, "room-decision-failure-speech.json"), LastDecisionFailureReport.ToString() + "\n");
        }
    }
    private static IEnumerator RunEvidenceRepairChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model)
    {
        var historyFacts = new JObject { ["route"] = "evidence", ["actionAuthorization"] = false,
            ["previousPlanningFailure"] = new JObject { ["historical"] = true,
                ["receipt"] = new JObject { ["kind"] = "input-budget-exceeded", ["newActionSubmittedByThisDecision"] = false } } };
        var historySchema = JObject.Parse(RoomTaskProtocol.BuildSpeechReviewSchemaForInput(
            new JObject { ["readOnlyEvidenceReply"] = historyFacts }.ToString()));
        Require(((JArray)historySchema["required"]).Values<string>().Contains("planningFailureReport"),
            "A historical diagnostic requires an independent candidate cause classification even with no current failure.");
        Require(((JObject)historySchema["properties"]).Properties().Take(2).Select(p => p.Name)
            .SequenceEqual(new[] { "requestDisposition", "planningFailureReport" }) &&
            ((JArray)historySchema["required"]).Values<string>().Take(2)
                .SequenceEqual(new[] { "requestDisposition", "planningFailureReport" }),
            "Native schema generates independent request and spoken-cause classifications before the verdict.");
        foreach (string report in new[] { "other-cause", "uncertain", "service-request-failed", "decision-not-executable", null })
            Require(RoomTaskProtocol.ReviewPlanningFailureRejection(Verdict("consistent", "scene-action", report), null, historyFacts) != null,
                "A consistent verdict cannot override missing, uncertain or conflicting historical cause classifications.");
        foreach (string report in new[] { "input-budget-exceeded", "not-mentioned" })
            Require(RoomTaskProtocol.ReviewPlanningFailureRejection(Verdict("consistent", "scene-action", report), null, historyFacts) == null,
                "Correct historical cause and ordinary visual answers both survive without creating a current failure.");
        foreach (string mode in new[] { "accepted", "exhausted", "guard-accepted", "guard-exhausted", "empty-repair", "invalid-repair", "review-http-failure",
            "review-invalid-json", "interrupted", "new-turn", "scene-unbound", "cancelled-second-review" })
        {
            const string question = "你刚才为什么说信息太多？";
            string original = mode.StartsWith("guard-", StringComparison.Ordinal) ? "刚才是因为我犯困了。" : "我看到了桌子和窗户。";
            const string corrected = "刚才是规划输入超出了本地处理预算，并没有向动作服务发送请求。";
            int decisions = model.Decisions.Count, speechCount = model.Speech.Count;
            yield return PrepareRoomPlanning(chat, bridge, question);
            model.FirstReplies.Last().FirstReplyCallback(new FirstReplyCompletion { Disposition = FirstReplyDisposition.Evidence });
            yield return Wait(() => model.Speech.Count == speechCount + 1, "Repair fixture evidence writer");
            var writer = model.Speech.Last();
            int history = model.m_DataList.Count, generation = (int)Get(chat, "m_FormalResponseGeneration");
            int operations = bridge.Controller.OperationRevision;
            writer.SpeechCallback(new SpeechText(original, "zh", actionDependency: SpeechActionDependency.Independent));
            writer.SpeechCompleteCallback(original);
            yield return Wait(() => model.Decisions.Count == decisions + 1, "Repair fixture first review");
            var firstReview = model.Decisions.Last();
            string rejection = new JObject { ["verdict"] = mode.StartsWith("guard-", StringComparison.Ordinal) ? "consistent" : "inconsistent", ["factScope"] = "scene-action",
                ["actualLanguage"] = "zh",
                ["reason"] = "候选只讲画面，没有回答用户追问的上一轮规划失败原因。",
                ["requestDisposition"] = "answer-only", ["planningFailureReport"] =
                    mode.StartsWith("guard-", StringComparison.Ordinal) ? "other-cause" : "not-mentioned" }.ToString();
            if (mode == "review-http-failure" || mode == "review-invalid-json")
            {
                firstReview.Complete(mode != "review-http-failure", mode == "review-invalid-json" ? "{}" : "", mode);
                Require(model.Speech.Count == speechCount + 1 && model.m_DataList.Count == history + 1 &&
                    model.m_DataList.Last().content != original,
                    "Transport and invalid reviewer envelopes fail closed without semantic-repair requests.");
                ((JArray)LastDecisionFailureReport["cases"]).Add("evidence-repair:" + mode);
                continue;
            }
            firstReview.Complete(true, rejection, "actual-semantic-rejection");
            yield return Wait(() => model.Speech.Count == speechCount + 2, "One evidence correction dispatch");
            var repair = model.Speech.Last();
            firstReview.Complete(true, rejection, "duplicate-old-review");
            Require(repair.Kind == "work-feedback" && repair.Stage == FormalResponseStage.CommittedSpatialReply &&
                !repair.RecordAssistantHistory && model.Speech.Count == speechCount + 2 && model.m_DataList.Count == history &&
                model.Decisions.Count == decisions + 1 && bridge.Controller.OperationRevision == operations,
                "One consumed-before-dispatch closed continuation records neither rejected candidate nor another user and cannot plan/move.");
            var feedback = JObject.Parse(repair.Input.Substring(repair.Input.IndexOf('\n') + 1));
            Require((int)feedback["attempt"] == 1 && (int)feedback["maximumAttempts"] == 1 &&
                (string)feedback["rejectedPublicSpeech"] == original &&
                JToken.DeepEquals(feedback["actualReview"], JObject.Parse(rejection)) &&
                (string)feedback["humanInput"]["primaryUtterance"] == question &&
                feedback["boundReplyFacts"] == null && !string.IsNullOrEmpty((string)feedback["factsSource"]) &&
                (bool?)CommittedSpatialReplyProtocol.ReadReplyFacts(repair.Context)?.SelectToken("readOnlyEvidenceReply.actionAuthorization") == false,
                "Correction receives the real rejection, original complete human question and bound evidence, without handwritten desired answer or authority.");
            Require(mode.StartsWith("guard-", StringComparison.Ordinal)
                    ? (string)feedback["actualReview"]["verdict"] == "consistent" && (string)feedback["guardRejectedReason"] == "planning-failure-cause-mismatch"
                    : feedback["guardRejectedReason"].Type == JTokenType.Null,
                "Deterministic cause mismatch is separate feedback and never falsifies the independent review verdict.");
            if (mode == "interrupted" || mode == "new-turn" || mode == "scene-unbound")
            {
                var environment = bridge.environmentRoot;
                if (mode == "interrupted") Set(chat, "m_UserSpeechActiveForAutonomy", true);
                if (mode == "new-turn") Call(chat, "RecordAcceptedMotionUserTurn", "这是下一轮，不要继续上一条。");
                if (mode == "scene-unbound") bridge.environmentRoot = null;
                repair.SpeechCallback(new SpeechText(corrected, "zh")); repair.SpeechCompleteCallback(corrected);
                firstReview.Complete(true, rejection, "late-old-review");
                yield return null;
                Require(model.m_DataList.Count == history && model.Decisions.Count == decisions + 1 &&
                    model.Speech.Count == speechCount + 2 && bridge.Controller.OperationRevision == operations,
                    "Interrupted/new-turn/unbound repair callbacks cannot publish, re-review or execute.");
                bridge.environmentRoot = environment; Set(chat, "m_UserSpeechActiveForAutonomy", false);
                ((JArray)LastDecisionFailureReport["cases"]).Add("evidence-repair:" + mode);
                continue;
            }
            if (mode != "empty-repair")
                repair.SpeechCallback(new SpeechText(corrected, "zh", actionDependency:
                    mode == "invalid-repair" ? SpeechActionDependency.AfterAction : SpeechActionDependency.Independent));
            Require(model.m_DataList.Count == history && (bool)Get(chat, "m_FormalResponseInFlight"),
                "Repaired streaming text remains private until its independent second review.");
            repair.SpeechCompleteCallback(mode == "empty-repair" ? "" : corrected);
            if (mode == "empty-repair" || mode == "invalid-repair")
            {
                Require(model.m_DataList.Count == history + 1 && model.Decisions.Count == decisions + 1 &&
                    model.Speech.Count == speechCount + 2 && model.m_DataList.Last().content != corrected,
                    "Empty/invalid continuation cannot be re-repaired or released as speech.");
                ((JArray)LastDecisionFailureReport["cases"]).Add("evidence-repair:" + mode);
                continue;
            }
            yield return Wait(() => model.Decisions.Count == decisions + 2, "Corrected public speech review");
            var secondReview = model.Decisions.Last();
            var secondInput = JObject.Parse(secondReview.Input);
            Require((int)secondInput["repairAttempts"] == 1 && (string)secondInput["publicSpeech"] == corrected &&
                (string)secondInput["publicSpeechLanguage"] == "zh" &&
                model.m_DataList.Count == history && bridge.Controller.OperationRevision == operations,
                "Second review judges the private revised candidate with an explicit consumed repair allowance.");
            if (mode == "cancelled-second-review")
            {
                Call(chat, "InvalidateFormalResponse", "cancelled-evidence-repair-review");
                secondReview.Complete(true, Verdict("consistent", "scene-action", "input-budget-exceeded"), "late-second-review");
                Require(model.m_DataList.Count == history, "Cancelled second review cannot release repaired speech.");
            }
            else
            {
                bool accept = mode == "accepted" || mode == "guard-accepted";
                secondReview.Complete(true, Verdict(mode == "guard-exhausted" || accept ? "consistent" : "inconsistent", "scene-action",
                    mode == "guard-exhausted" ? "other-cause" : "input-budget-exceeded"), "second-review");
                Require(model.m_DataList.Count == history + 1 && model.Speech.Count == speechCount + 2 &&
                    model.Decisions.Count == decisions + 2 &&
                    (accept ? model.m_DataList.Last().content == corrected : model.m_DataList.Last().content != corrected),
                    "A second consistency verdict releases exactly once; rejection exhausts recovery and uses factual fallback.");
            }
            int finishedHistory = model.m_DataList.Count;
            repair.SpeechCallback(new SpeechText("重复的旧流。", "zh")); repair.SpeechCompleteCallback(corrected);
            secondReview.Complete(true, Verdict("consistent", "scene-action", "input-budget-exceeded"), "duplicate-second-review");
            firstReview.Complete(true, rejection, "duplicate-first-review");
            Require(model.m_DataList.Count == finishedHistory && model.Speech.Count == speechCount + 2 &&
                model.Decisions.Count == decisions + 2 && bridge.Controller.OperationRevision == operations,
                "All late/duplicate callbacks remain revoked after final release, exhaustion or cancellation.");
            ((JArray)LastDecisionFailureReport["cases"]).Add("evidence-repair:" + mode);
        }
    }

    private static IEnumerator RunTypedEvidenceInputChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model)
    {
        // Exact logged gen4 words/wrapper, Editor.snapshot.log LF 45847-45852.
        // RGB and callbacks are controlled current-scene fixtures, not a replay of that Play.
        const string primary = "意思啊，什么意思啊，什么叫要是什么什么什么要考叫什么叫要考虑的东西太多了，巴拉巴拉的。";
        const string evidence = "[说话人:ユウ; speaker_id:owner; 类型:owner; 可信度:0.76] [听觉测量（不是整轮模态或行动结论）; 歌唱概率:0.14; 语言:zh; 音域:A2～A5; 音高稳定度:0.48; 旋律:D5-E3-B2-A2-G#2-G#3-C#3-F#3-G3-C#3-E5-G#2-C#5-E3-F#3-F3-D3-C#3-…; 歌词是ASR推测，长音与一字多音处可能不准确; 曲库里没有旋律接近的段落] 意思啊，什么意思啊，什么叫要是什么什么什么要考叫什么叫要考虑的东西太多了，巴拉巴拉的。 [同轮分轨观测；以下为同一录音按时间独立ASR的主语义，时间基于去除首尾静音后的内容音频；这是已经录完的一轮，不是接下来才会发生的计划。请同时理解唱前约定、实际歌声和唱后请求；较早流式预判可能只听到开头，不能代替下面完整时序。是否回应、行动或询问仍由你判断。type仅是声学候选，uncertain不等于没有唱歌。尾段可能包含新的用户请求，请结合完整时序理解。alternatives为边界重叠窗口的备选，不是重复发言，不要直接拼接；空文本/failed表示未识别，不能推断没有声音。confidence_available=false表示ASR没有提供可校准置信度。存在歧义时可以先询问，也可以结合语境自行判断。\n{\"id\":1,\"start_seconds\":0.0,\"end_seconds\":4.829999923706055,\"region\":\"head\",\"type\":\"uncertain\",\"type_source\":\"acoustic_boundary_hypothesis\",\"text\":\"意思啊，什么意思啊，什么叫要。\",\"language\":\"zh\",\"status\":\"ok\",\"error\":\"\",\"confidence_available\":false,\"review_required\":true,\"alternatives\":[{\"source\":\"boundary_overlap\",\"text\":\"意思啊，什么意思啊，什么叫要是。\",\"language\":\"zh\",\"audio_event\":\"Speech\",\"error\":\"\",\"start_seconds\":0.0,\"end_seconds\":5.230000019073486}]}\n{\"id\":2,\"start_seconds\":4.829999923706055,\"end_seconds\":9.8100004196167,\"region\":\"island\",\"type\":\"singing_candidate\",\"type_source\":\"acoustic_boundary_hypothesis\",\"text\":\"什么什么什么要考叫什么叫要考虑的东西太多了，巴拉巴拉的。\",\"language\":\"zh\",\"status\":\"ok\",\"error\":\"\",\"confidence_available\":false,\"review_required\":true,\"alternatives\":[{\"source\":\"boundary_overlap\",\"text\":\"要是什么什么什么要考叫什么叫要考虑的东西太多了，巴拉巴拉的。\",\"language\":\"zh\",\"audio_event\":\"Speech\",\"error\":\"\",\"start_seconds\":4.429999828338623,\"end_seconds\":9.8100004196167}]}\n整轮ASR仅作补漏/冲突参考，不能覆盖分段内容：{\"text\":\"意思啊，什么意思啊，什么叫要是什么什么什么要考叫什么叫要考虑的东西太多了，巴拉巴拉的。\"}]\n[同一录音的较早 ASR 观测：不是额外发言，也不表示最终结果必然更准确。可用于补漏和判断冲突，不要重复拼接；有歧义时可以询问。\n覆盖录音前 1.44s：意思啊。]";
        Require(primary.Length == 43 && evidence.Length == 1657 && evidence != primary,
            "The logged complete primary and full ASR wrapper are separate, unchanged fixture sources.");
        int decisionsBefore = model.Decisions.Count, speechBefore = model.Speech.Count;
        yield return PrepareRoomPlanning(chat, bridge, primary, evidence);
        int generation = (int)Get(chat, "m_FormalResponseGeneration"), epoch = (int)Get(chat, "m_WorkEpoch");
        var ledger = (NeEEvA.Motion.ArdyActionConstraintLedger)Get(chat, "m_ActionConstraints");
        int userTurn = ledger.CurrentUserTurn, operations = bridge.Controller.OperationRevision;
        Require(ledger.CurrentUserText == primary && (string)Get(chat, "m_LastUserMsg") == primary &&
            (string)Get(chat, "m_LastUserEvidence") == evidence && model.FirstReplies.Last().Input == evidence &&
            model.Decisions.Count == decisionsBefore,
            "The real accepted-input setter/ledger preserve primary versus acoustic evidence before any route verdict or planner call.");
        model.FirstReplies.Last().FirstReplyCallback(new FirstReplyCompletion { Disposition = FirstReplyDisposition.Evidence });
        yield return Wait(() => model.Speech.Count == speechBefore + 1, "Typed evidence writer dispatch");
        var speech = model.Speech.Last();
        var facts = CommittedSpatialReplyProtocol.ReadReplyFacts(speech.Context);
        var human = facts?["humanInput"] as JObject;
        Require(speech.Input == evidence && speech.Stage == FormalResponseStage.CommittedSpatialReply &&
            (bool?)human?["currentHumanTurnPresent"] == true && (string)human?["primaryUtterance"] == primary &&
            (string)human?["fullInputEvidence"] == evidence && (int?)human?["generation"] == generation &&
            (int?)human?["workEpoch"] == epoch && (int?)human?["userTurn"] == userTurn,
            "The actual closed writer receives complete typed primary/evidence bound to its generation, work epoch and accepted user turn.");
        var historical = facts?["readOnlyEvidenceReply"]?["previousPlanningFailure"];
        Require((string)facts?["readOnlyEvidenceReply"]?["currentUserText"] == primary &&
            (bool?)facts?["readOnlyEvidenceReply"]?["actionAuthorization"] == false &&
            (bool?)historical?["historical"] == true && (int?)historical?["receipt"]?["userTurn"] != userTurn &&
            facts?["decisionFailure"]?.Type == JTokenType.Null &&
            model.Decisions.Count == decisionsBefore && bridge.Controller.OperationRevision == operations,
            "Current utterance, historical failure and current visual evidence keep distinct sources; an old receipt supplies no new planning authority.");
        var wire = JObject.Parse(speech.Wire);
        var wireTexts = wire.SelectTokens("$.messages[*].content").OfType<JValue>()
            .Concat(wire.SelectTokens("$..text").OfType<JValue>())
            .Where(value => value.Type == JTokenType.String).Select(value => (string)value);
        Require(wireTexts.Any(text => JToken.DeepEquals(CommittedSpatialReplyProtocol.ReadReplyFacts(text)?["humanInput"], human)),
            "The production serialized writer request retains the same typed human input, rather than only the test model's context property.");
        // String-typed JValue(null) serializes to JSON null. Check the actual
        // wire representation, not its pre-serialization CLR token type.
        var stale = JObject.Parse(((JObject)Call(chat, "BuildCommittedReplyHumanInput", generation - 1)).ToString());
        Require((bool?)stale["currentHumanTurnPresent"] == false && stale["primaryUtterance"].Type == JTokenType.Null &&
            stale["fullInputEvidence"].Type == JTokenType.Null,
            "A stale generation cannot bind this turn's human input.");
        bool wasTick = (bool)Get(chat, "m_AgentCurrentRoundIsTick");
        try
        {
            Set(chat, "m_AgentCurrentRoundIsTick", true);
            var autonomous = JObject.Parse(((JObject)Call(chat, "BuildCommittedReplyHumanInput", generation)).ToString());
            Require((bool?)autonomous["currentHumanTurnPresent"] == false &&
                autonomous["primaryUtterance"].Type == JTokenType.Null && autonomous["fullInputEvidence"].Type == JTokenType.Null,
                "An autonomous/result round cannot promote the stored user words into a fresh human request.");
        }
        finally { Set(chat, "m_AgentCurrentRoundIsTick", wasTick); }
        const string answer = "刚才说信息太多，是因为上轮动作规划的输入超过了本地预算，当时没有发出服务请求。这不影响现在直接聊天。";
        speech.SpeechCallback(new SpeechText(answer, "zh", actionDependency: SpeechActionDependency.Independent));
        speech.SpeechCompleteCallback(answer);
        yield return Wait(() => model.Decisions.Count == decisionsBefore + 1, "Typed evidence speech review");
        var review = model.Decisions.Last();
        // Match the writer fact reader: ISO timestamps are source strings, not
        // CLR dates. Automatic date promotion changes DeepEquals despite equal wire data.
        JObject reviewInput;
        using (var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(review.Input)))
        {
            reader.DateParseHandling = Newtonsoft.Json.DateParseHandling.None;
            reviewInput = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            Require(!reader.Read(), "The complete typed reviewer input contains exactly one JSON object.");
        }
        Require(review.Kind == "room-review" && JToken.DeepEquals(reviewInput["humanInput"], human) &&
            (string)reviewInput["requestedUserText"] == primary &&
            (string)reviewInput["readOnlyEvidenceReply"]?["currentUserText"] == primary &&
            JToken.DeepEquals(reviewInput["readOnlyEvidenceReply"]?["previousPlanningFailure"], historical),
            "The actual reviewer receives the same exact primary/full wrapper and owner identity, separately from historical failure and image facts.");
        review.Complete(true, Verdict("consistent", "scene-action", "input-budget-exceeded"), "controlled-complete-gen4-evidence-answer");
        Require(model.m_DataList.Last().content == answer && bridge.Controller.OperationRevision == operations &&
            model.Decisions.Count == decisionsBefore + 1 && (string)Get(chat, "m_RoomTaskDispatch") == "read-only-evidence" &&
            string.IsNullOrEmpty((string)Get(chat, "m_RoomTaskActionId")) && Call(chat, "ReadRoomTaskDecisionFailure") == null,
            "The reviewed complete-question answer is published with one speech-review request and no planner or new action.");
        LastDecisionFailureReport["typedHumanInput"] = new JObject {
            ["source"] = "observed-gen4; snapshot LF 45847-45852; logged primary 43 chars and full evidence 1657 chars",
            ["primaryCharacters"] = primary.Length, ["fullEvidenceCharacters"] = evidence.Length,
            ["generation"] = generation, ["workEpoch"] = epoch, ["userTurn"] = userTurn,
            ["scope"] = "Actual accepted setter, FirstReply/Evidence dispatch, production writer wire and reviewer input; controlled route/writer/reviewer callbacks and fresh fixture RGB. Input preservation/ownership test, not model semantic-quality or original-Play replay."
        };
        ((JArray)LastDecisionFailureReport["cases"]).Add("evidence-route-complete-typed-gen4-input");
    }

    private static IEnumerator RunEvidenceCaptureOwnerChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model)
    {
        foreach (string mode in new[] { "binding-without-callback", "epoch-preserves-goal", "replacement-owner" })
        {
            yield return PrepareRoomPlanning(chat, bridge, "只回答当前观察，不开始动作。");
            int generation = (int)Get(chat, "m_FormalResponseGeneration"), epoch = (int)Get(chat, "m_WorkEpoch");
            var capture = (NevaSceneVisionCapture)Get(chat, "m_SceneVisionCapture");
            float oldAge = (float)Get(chat, "m_SceneObservationMaxAgeSeconds"), oldNextCapture = (float)Get(capture, "nextCaptureAt");
            var environment = bridge.environmentRoot;
            object oldGoal = Get(chat, "m_SpatialDeviceGoal"), retainedGoal = null;
            int speech = model.Speech.Count, history = model.m_DataList.Count, decisions = model.Decisions.Count;
            int operation = bridge.Controller.OperationRevision;
            Action<SceneVisionObservation> lateCapture = null;
            try
            {
                // Hold the actual capture queue before LateUpdate, then remove its
                // callback to exercise owner cleanup without a capture completion.
                Set(chat, "m_SceneObservationMaxAgeSeconds", -1f);
                Set(capture, "nextCaptureAt", Time.realtimeSinceStartup + 60f);
                model.FirstReplies.Last().FirstReplyCallback(new FirstReplyCompletion { Disposition = FirstReplyDisposition.Evidence });
                var waiting = (System.Collections.Generic.List<Action<SceneVisionObservation>>)Get(capture, "waiting");
                Require(waiting.Count == 1 && model.Speech.Count == speech &&
                    (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == generation,
                    "Evidence handoff waits on an actual deferred capture before any writer or action.");
                lateCapture = waiting[0]; waiting.Clear();
                if (mode == "binding-without-callback") bridge.environmentRoot = null;
                else if (mode == "epoch-preserves-goal")
                {
                    retainedGoal = Activator.CreateInstance(typeof(ChatSample).GetField("m_SpatialDeviceGoal", Flags).FieldType, true);
                    Set(retainedGoal, "id", "read-only-capture-retained-goal");
                    Set(retainedGoal, "environment", environment); Set(retainedGoal, "avatar", bridge.avatar);
                    Set(retainedGoal, "source", capture); Set(retainedGoal, "model", model);
                    Set(retainedGoal, "status", "active"); Set(retainedGoal, "reason", "retained-existing-goal");
                    Set(retainedGoal, "executionRevision", 17); Set(retainedGoal, "executionSuspended", false);
                    Set(retainedGoal, "resumeAfterDialogue", true);
                    Set(chat, "m_SpatialDeviceGoal", retainedGoal);
                    Set(chat, "m_WorkEpoch", epoch + 1);
                }
                else
                {
                    // Install a replacement owner without stopping the old watcher.
                    Set(chat, "m_FormalResponseGeneration", generation + 1);
                    Set(chat, "m_RoomSpeechReviewOwnerRevision", (int)Get(chat, "m_RoomSpeechReviewOwnerRevision") + 1);
                    Set(chat, "m_RoomSpeechGuardGeneration", generation + 1);
                    Set(chat, "m_PendingSceneFormalCaptureGeneration", generation + 1);
                    Set(chat, "m_FormalResponseInFlight", true);
                }
                yield return null;
                yield return null;
                if (mode == "replacement-owner")
                    Require((int)Get(chat, "m_FormalResponseGeneration") == generation + 1 &&
                        (bool)Get(chat, "m_FormalResponseInFlight") &&
                        (int)Get(chat, "m_RoomSpeechGuardGeneration") == generation + 1 &&
                        (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == generation + 1,
                        "The old capture watcher cannot release or cancel a replacement generation's owner.");
                else
                    Require((int)Get(chat, "m_FormalResponseGeneration") != generation &&
                        !(bool)Get(chat, "m_FormalResponseInFlight") && (bool)Get(chat, "m_StreamComplete") &&
                        (bool)Get(chat, "m_TTSSenderDone") && (int)Get(chat, "m_RoomSpeechGuardGeneration") == -1 &&
                        (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1,
                        "A capture owner invalidated by binding/epoch changes settles without a callback or a stuck formal round.");
                if (retainedGoal != null)
                    Require(ReferenceEquals(Get(chat, "m_SpatialDeviceGoal"), retainedGoal) &&
                        (string)Get(retainedGoal, "status") == "active" && (string)Get(retainedGoal, "reason") == "retained-existing-goal" &&
                        (int)Get(retainedGoal, "executionRevision") == 17 && !(bool)Get(retainedGoal, "executionSuspended") &&
                        (bool)Get(retainedGoal, "resumeAfterDialogue"),
                        "Cancelling only a read-only capture must not pause, cancel, revise or grant resumption of the existing goal.");
                int afterGeneration = (int)Get(chat, "m_FormalResponseGeneration");
                bool afterInFlight = (bool)Get(chat, "m_FormalResponseInFlight");
                lateCapture(null); lateCapture(null);
                Require(model.Speech.Count == speech && model.Decisions.Count == decisions && model.m_DataList.Count == history &&
                    bridge.Controller.OperationRevision == operation &&
                    (int)Get(chat, "m_FormalResponseGeneration") == afterGeneration && (bool)Get(chat, "m_FormalResponseInFlight") == afterInFlight,
                    "Late/duplicate capture completions cannot publish, plan, move or alter the replacement owner.");
                ((JArray)LastDecisionFailureReport["cases"]).Add("evidence-capture-owner:" + mode);
            }
            finally
            {
                bridge.environmentRoot = environment;
                Set(chat, "m_WorkEpoch", epoch); Set(chat, "m_SpatialDeviceGoal", oldGoal);
                Set(chat, "m_SceneObservationMaxAgeSeconds", oldAge); Set(capture, "nextCaptureAt", oldNextCapture);
            }
        }
    }

    private static SpeechTextBuffer Buffer(string text) { var result = new SpeechTextBuffer(); result.Append(new SpeechText(text, "ja", actionDependency: SpeechActionDependency.Independent)); return result; }
    private static string Verdict(string verdict, string scope, string planningReport = null)
    {
        var result = new JObject { ["verdict"] = verdict, ["reason"] = "受控事实范围回归", ["factScope"] = scope };
        if (planningReport != null) { result["requestDisposition"] = "answer-only"; result["planningFailureReport"] = planningReport; }
        return result.ToString();
    }
    private static IEnumerator Wait(Func<bool> ready, string label) { float end = Time.realtimeSinceStartup + 8f; while (!ready()) { if (Time.realtimeSinceStartup > end) throw new TimeoutException(label); yield return null; } }
    private static object Call(object target, string method, params object[] args) => NevaSceneVisionIntegrationRegression.Call(target, method, args);
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static void Require(bool valid, string message) { checks++; if (!valid) throw new InvalidOperationException(message); }
}
