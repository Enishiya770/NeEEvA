using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Controlled execution facts and reviewer verdicts, actual Chat feedback buffering and scene RGB.
/// This verifies reviewed spatial step dispatch and publication/ownership boundaries,
/// not Qwen semantic accuracy, native arrival or successful lamp operation.</summary>
public static class WorkSceneFeedbackRegression
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int checks;
    private const string LampRequest = "请把眼前的那盏小灯关掉；如果太远就先靠近一点。";
    private const string NextDeviceStep = "继续原请求，确认当前目标和距离后执行关闭小灯操作。";
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        checks = 0;
        LastReport = new JObject { ["status"]="running", ["cases"]=new JArray(),
            ["scope"]="Actual Chat work-review continuation into spatial decision dispatch, work-feedback transport/buffering/review/publication and fresh real character images, with controlled prior spatial facts and explicit reviewer verdicts. A proposed device step deliberately cites an unknown entity; approval must hand the complete original target to unified search without dispatching that entity. Controlled current/historical search outcomes end incomplete and receive one guarded terminal reply. No remote model, semantic-review accuracy, native arrival, audible TTS or successful lamp-switch evidence is claimed." };
        string reviewDirectory = Path.Combine(output, "review-live-inputs");
        Directory.CreateDirectory(reviewDirectory);
        var exportedReviews = new JObject { ["scope"]="Controlled rejection facts; real room RGB. Exact production pending reviewer request wires before any controlled verdict is delivered. Export only; no model HTTP is performed by this regression.", ["cases"]=new JArray() };
        object oldSelected = Get(chat, "m_SelectedSceneObject"), oldStateResult = Get(chat, "m_SceneObjectStateResult");
        bool oldMatching = (bool)Get(chat, "m_EnableSceneObjectTargetMatching");
        bool oldAwareness = (bool)Get(chat, "m_EnableSceneObjectAwareness");
        bool oldSceneActions = (bool)Get(chat, "m_EnableSceneActions");
        string[] goalFields = { "m_SpatialDeviceGoal", "m_SpatialGoalLatestReview", "m_PendingDeviceClarification",
            "m_PendingDeviceClarificationReviewRequest", "m_SpatialGoalTerminalReplyGeneration", "m_SpatialGoalTerminalProof",
            "m_SpatialGoalRunnerLastReport" };
        var oldGoalState = goalFields.Select(name => Get(chat, name)).ToArray();
        Require(Get(chat, "m_SpatialGoalRunner") == null,
            "Independent work-feedback cases begin with no executing prior spatial runner.");
        try
        {
            // Preserve the surrounding suite's logical memory without letting
            // this independent fixture cancel or update that saved objective.
            Set(chat, "m_SpatialDeviceGoal", null); Set(chat, "m_PendingDeviceClarification", null);
            Set(chat, "m_PendingDeviceClarificationReviewRequest", null);
            Set(chat, "m_EnableSceneObjectAwareness", true); Set(chat, "m_EnableSceneActions", true);
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
                Require(request.Stage == FormalResponseStage.CommittedSpatialReply && model.Decisions.Count == reviewCount,
                    "Reporting an existing spatial result uses the committed reply grammar and never chooses another spatial operation.");
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
                RequireRuntimeControlReview(chat, review);
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

            foreach (var sample in new[] {
                new { name="unsupported-no-physical-hand", verdict="inconsistent",
                    text="I have no hands, so I cannot turn that lamp off. Press the switch or unplug it yourself." },
                new { name="unsupported-approval-means-no-capability", verdict="inconsistent",
                    text="I have not received approval, so I have no ability to control lamps. Turn it off yourself." },
                new { name="supported-runtime-control-with-unconfirmed-target", verdict="consistent",
                    text="I can control a registered lamp without pressing its switch, but I have not confirmed your lamp in this view and have not operated it. I need a new view to identify the same target." }
            })
            {
                yield return Prepare(chat, bridge, model, "visible-object-not-confirmed: current target needs a new confirmation");
                int generation = (int)Get(chat, "m_FormalResponseGeneration"), history = model.m_DataList.Count;
                int operation = bridge.Controller.OperationRevision;
                string sceneBefore = chat.SceneActionContextJson;
                Call(chat, "DispatchWorkFeedback", "说明当前目标未确认这一具体限制，区分已连接的设备接口与尚未执行。", null, generation);
                var candidate = model.Speech.Last();
                Require(candidate.Input.Contains("runtimeDeviceControl") && candidate.Input.Contains("physicalHandRequired") &&
                    !candidate.RecordAssistantHistory && candidate.Stage == FormalResponseStage.CommittedSpatialReply,
                    "The actual work-feedback prompt retains software capability facts while target identity and result remain unresolved.");
                candidate.SpeechCallback(new SpeechText(sample.text, "en", actionDependency: SpeechActionDependency.Independent));
                candidate.SpeechCompleteCallback(sample.text);
                var review = model.Decisions.Last();
                Require((string)JObject.Parse(review.Input)["publicSpeech"] == sample.text &&
                    model.m_DataList.Count == history && ((SpeechTextBuffer)Get(chat, "m_SentenceBuffer")).Length == 0 &&
                    ((ICollection)Get(chat, "m_PendingChunks")).Count == 0,
                    "Capability-denial and factual limitation candidates reach the real audit before history or public audio buffering.");
                RequireRuntimeControlReview(chat, review, requireUnresolved: true);
                ExportReview(reviewDirectory, exportedReviews, review, sample.name, sample.text, sample.verdict);
                review.Complete(true, Verdict(sample.verdict, "scene-action"), "controlled-runtime-capability-boundary");
                Require(model.m_DataList.Count == history + 1 &&
                    (sample.verdict == "consistent" ? model.m_DataList.Last().content == sample.text : model.m_DataList.Last().content != sample.text) &&
                    bridge.Controller.OperationRevision == operation && chat.SceneActionContextJson == sceneBefore,
                    "Only the controlled factual verdict permits its candidate; reporting does not submit body or device operations or change the rejection facts.");
                ((JArray)LastReport["cases"]).Add(new JObject { ["case"]=sample.name, ["expectedVerdict"]=sample.verdict,
                    ["candidateReleased"]=sample.verdict == "consistent", ["newSpatialActionSubmitted"]=false });
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

            foreach (string priorIntent in new[] { "inspect-object", "approach-object" })
            {
                yield return PrepareContinuation(chat, bridge, model, priorIntent);
                int decisionsBefore = model.Decisions.Count, speechBefore = model.Speech.Count;
                int userMessages = model.m_DataList.Count(x => x.role == "user");
                // CurrentUserTurn belongs to the accepted real request; the timer and reviewer do not create another one.
                int userTurn = CurrentUserTurn(chat);
                NevaSceneVisionIntegrationModel.Request workReview = null;
                yield return BeginWorkReview(chat, model, request => workReview = request);
                Require(workReview.Kind == "work-review" && workReview.Input.Contains(LampRequest) &&
                    model.Decisions.Count == decisionsBefore && model.Speech.Count == speechBefore,
                    "The independent unfinished-work review sees the original request and cannot execute or publish by itself.");
                workReview.Complete(WorkVerdict("continue", NextDeviceStep));
                Require(model.Decisions.Count == decisionsBefore + 1 && model.Decisions.Last().Kind == "room" &&
                    model.Speech.Count == speechBefore,
                    "Reviewed unfinished spatial work enters a fresh room decision before any result-report candidate.");
                var step = model.Decisions.Last();
                var input = JObject.Parse(step.Input);
                var continuation = input["workContinuation"] as JObject;
                Require((string)input["currentUserText"] == LampRequest && (bool)input["autonomous"] == false &&
                    (int)input["decisionContractFacts"]["currentUserTurn"] == userTurn &&
                    continuation != null && (string)continuation["originalRequest"] == LampRequest &&
                    (string)continuation["nextStepIntention"] == NextDeviceStep &&
                    (string)continuation["priorIntent"] == priorIntent &&
                    (string)continuation["priorActionId"] == "controlled-prior-user-lamp-step",
                    "The new step retains the actual accepted user request/turn and the independently reviewed next step; a tick supplies no user authority.");
                Require(model.m_DataList.Count(x => x.role == "user") == userMessages &&
                    (bool)Get(chat, "m_AgentCurrentRoundIsTick") && CurrentUserTurn(chat) == userTurn,
                    "Spatial work planning neither writes a synthetic user message nor changes the global tick or accepted user turn.");
                string currentCapture = (string)input["visualObservation"]?["captureId"];
                Require(!string.IsNullOrEmpty(currentCapture) && !string.IsNullOrEmpty(step.Image),
                    "The next spatial decision uses this continuation's real current image rather than the old arrival image alone.");
                int goalReviewsBefore = model.DeviceGoalReviews.Count;
                string beforeGoalApprovalFact = chat.SceneActionContextJson;
                step.Complete(true, DeviceDecision(currentCapture), "controlled-new-device-step");
                yield return Wait(() => model.DeviceGoalReviews.Count == goalReviewsBefore + 1, "Independent device goal authorization for reviewed work continuation");
                var goalReview = model.DeviceGoalReviews.Last();
                Require((string)JObject.Parse(goalReview.Input)["currentUserText"] == LampRequest &&
                    model.Speech.Count == speechBefore && chat.SceneActionContextJson == beforeGoalApprovalFact &&
                    Get(chat, "m_SpatialGoalRunner") == null,
                    "A continued device proposal still needs independent approval of the full real user request before a runner, execution or speech.");
                int decisionsBeforeApprovedStep = model.Decisions.Count;
                string approvedGoal = ApprovedNewGoalReview();
                Require(RoomTaskProtocol.TryDeviceGoalProposalReview(approvedGoal, out var approvedProposal) &&
                    approvedProposal.IsApproved && approvedProposal.HasTypedReferent && approvedProposal.requestAssessment == "requested" &&
                    approvedProposal.relation == "new" && approvedProposal.referentKind == "unbound" && approvedProposal.referentSourceId == "none" &&
                    !RoomTaskProtocol.TryDeviceGoalProposalReview("{\"verdict\":\"approved\",\"reason\":\"Legacy approval has no current request assessment or referent.\"}", out _),
                    "The continuation fixture uses the current complete new-goal contract; a legacy approval cannot supply authorization.");
                int searchesBefore = model.SceneSearchRequests.Count, matchesBefore = model.Matches.Count;
                int operationBefore = bridge.Controller.OperationRevision;
                var priorAction = JObject.Parse(beforeGoalApprovalFact)["lastAction"].DeepClone();
                var devices = UnityEngine.Object.FindObjectsOfType<SceneObjectLightSwitch>(true);
                var deviceStates = devices.Select(device => JsonUtility.ToJson(device.ReadState())).ToArray();
                goalReview.Complete(true, approvedGoal, "controlled-continued-goal-approved");
                yield return Wait(() => model.SceneSearchRequests.Count == searchesBefore + 1,
                    "Approved complete target enters unified search instead of dispatching the proposal's unknown entity");
                var approvedContext = (JObject)Call(chat, "BuildSpatialGoalContext");
                string goalId = (string)approvedContext["goalId"];
                Require(!string.IsNullOrEmpty(goalId) && (string)approvedContext["originalRequest"] == LampRequest &&
                    (string)approvedContext["targetDescription"] == LampRequest && (string)approvedContext["desiredState"] == "off" &&
                    string.IsNullOrEmpty((string)approvedContext["boundEntityId"]) && (string)approvedContext["referentKind"] == "unbound" &&
                    (string)approvedContext["referentSourceId"] == "none" && Get(chat, "m_SpatialGoalRunner") != null &&
                    model.Decisions.Count == decisionsBeforeApprovedStep && model.Matches.Count == matchesBefore &&
                    model.Speech.Count == speechBefore && JToken.DeepEquals(priorAction, JObject.Parse(chat.SceneActionContextJson)["lastAction"]),
                    "Approval preserves the complete user target and no entity identity; the draft's unknown entity is never an action or a quiet replan.");
                int serviced = searchesBefore;
                var historicalCaptures = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                var searchReceipts = new JArray();
                while (model.Speech.Count == speechBefore)
                {
                    yield return Wait(() => model.SceneSearchRequests.Count > serviced || model.Speech.Count > speechBefore,
                        "Bounded current/history search review advances to one terminal result");
                    if (model.Speech.Count > speechBefore) break;
                    var searchRequest = model.SceneSearchRequests[serviced++];
                    var searchInput = JObject.Parse(searchRequest.Input);
                    bool historical = (string)searchInput["imageRole"] == "historical";
                    bool recovery = (bool?)searchInput["recovery"]?["onlyNewView"] == true;
                    Require(serviced - searchesBefore <= 4 &&
                        (serviced == searchesBefore + 1 ? !historical && !recovery : historical || recovery) &&
                        (string)searchInput["task"]?["originalUserRequest"] == LampRequest && !string.IsNullOrEmpty(searchRequest.Image) &&
                        string.IsNullOrEmpty((string)searchInput["boundEntityId"]) &&
                        model.Decisions.Count == decisionsBeforeApprovedStep && model.Matches.Count == matchesBefore &&
                        bridge.Controller.OperationRevision == operationBefore && CurrentUserTurn(chat) == userTurn &&
                        model.m_DataList.Count(message => message.role == "user") == userMessages &&
                        JToken.DeepEquals(priorAction, JObject.Parse(chat.SceneActionContextJson)["lastAction"]),
                        "Unified search keeps the accepted target/turn and actual labelled images, without draft entity execution, quiet Room replans or synthetic users.");
                    if (historical)
                        Require(!string.IsNullOrEmpty((string)searchInput["historicalCaptureId"]) &&
                            historicalCaptures.Add((string)searchInput["historicalCaptureId"]),
                            "A historical image is reviewed once under its own capture identity, never promoted to current evidence.");
                    searchReceipts.Add(new JObject { ["imageRole"] = searchInput["imageRole"]?.DeepClone(),
                        ["captureId"] = searchInput["captureId"]?.DeepClone(),
                        ["historicalCaptureId"] = searchInput["historicalCaptureId"]?.DeepClone(), ["recovery"] = recovery });
                    if (recovery) searchRequest.Complete(false, "", "controlled-work-search-recovery-provider-unavailable");
                    else searchRequest.Complete(true, new JObject { ["choice"] = "done", ["selectionRole"] = "exploration",
                        ["referenceId"] = "", ["reason"] = "This actual image does not establish a useful hint for the complete requested target." }.ToString(),
                        "controlled-work-search-no-confirmed-target");
                }
                var searchResult = (JObject)Call(chat, "BuildSceneSearchContext");
                Require(model.Speech.Count == speechBefore + 1 && model.Decisions.Count == decisionsBeforeApprovedStep &&
                    model.SceneSearchRequests.Count == serviced && model.Matches.Count == matchesBefore &&
                    (string)searchResult["reason"] == "search-stopped-without-target-or-complete-survey" &&
                    (searchResult["reviewedHistoricalCaptureIds"] as JArray)?.Values<string>().OrderBy(value => value)
                        .SequenceEqual(historicalCaptures.OrderBy(value => value)) == true &&
                    bridge.Controller.OperationRevision == operationBefore &&
                    devices.Select(device => JsonUtility.ToJson(device.ReadState())).SequenceEqual(deviceStates),
                    "Bounded incomplete search terminates once without a match, device write or body operation; all reviewed history remains labelled.");
                var candidate = model.Speech.Last();
                Require(candidate.Kind == "work-feedback" && candidate.Stage == FormalResponseStage.SpatialGoalResult &&
                    !candidate.RecordAssistantHistory && model.m_DataList.Count(x => x.role == "user") == userMessages,
                    "The blocked device goal produces one unrecorded short terminal candidate without promoting the timer into chat user history.");
                Require(SpatialGoalResultContextPolicy.TryReadContext(candidate.Context, out var terminalOriginal, out var terminalFacts) &&
                    terminalOriginal == LampRequest && (string)terminalFacts["goal"]?["goalId"] == goalId &&
                    (string)terminalFacts["goal"]?["status"] == "blocked" && terminalFacts["goal"]?["completionEvidence"]?.Type != JTokenType.Object &&
                    (bool?)terminalFacts["goal"]?["searchSummary"]?["searchAttempted"] == true &&
                    (int?)terminalFacts["goal"]?["searchSummary"]?["latestSearchDecisions"] == serviced - searchesBefore &&
                    (int?)terminalFacts["goal"]?["searchSummary"]?["latestSearchMotionSteps"] == 0 &&
                    SpatialGoalResultContextPolicy.RequiredReport(terminalFacts) == "incomplete" &&
                    (string)(terminalFacts["lastAction"] as JObject)?["requestedEntityId"] != "controlled-unknown-work-lamp" &&
                    (string)(terminalFacts["lastAction"] as JObject)?["intent"] != "set-object-state" &&
                    JToken.DeepEquals(priorAction, JObject.Parse(chat.SceneActionContextJson)["lastAction"]),
                    "Terminal facts retain the original goal and genuine incomplete search, not a fabricated unknown-entity rejection or completed device operation.");
                int assistantHistory = model.m_DataList.Count;
                const string falseSuccess = "The lamp is now off; I have switched it off.";
                candidate.SpeechCallback(new SpeechText(falseSuccess, "en", actionDependency: SpeechActionDependency.Independent));
                Require(model.m_DataList.Count == assistantHistory && model.Decisions.Count == decisionsBeforeApprovedStep,
                    "A false completion candidate remains private before its independent final review.");
                candidate.SpeechCompleteCallback(falseSuccess);
                yield return Wait(() => model.Decisions.Count == decisionsBeforeApprovedStep + 1,
                    "The single terminal candidate receives one public consistency review");
                var publicReview = model.Decisions.Last();var publicInput = JObject.Parse(publicReview.Input);
                var publicFacts = publicInput["sceneActionFacts"] as JObject;
                Require(publicReview.Kind == "room-review" && model.m_DataList.Count == assistantHistory &&
                    (string)publicFacts?["goal"]?["status"] == "blocked" &&
                    (bool?)publicFacts?["goal"]?["completionEvidenceValid"] != true &&
                    SpatialGoalResultContextPolicy.RequiredReport(publicFacts) == "incomplete" &&
                    (string)(publicFacts?["lastAction"] as JObject)?["requestedEntityId"] != "controlled-unknown-work-lamp" &&
                    (string)(publicFacts?["lastAction"] as JObject)?["intent"] != "set-object-state",
                    "The independent audit receives blocked incomplete search facts without inventing a device attempt or accepting the draft's entity.");
                var rejection = new JObject { ["verdict"] = "inconsistent", ["factScope"] = "scene-action", ["resultReport"] = "omitted",
                    ["reason"] = "The search is incomplete and no device operation or completed off-state proof exists." };
                publicReview.Complete(true, rejection.ToString(), "controlled-false-device-success");
                string trusted = SpatialGoalResultContextPolicy.BuildTrustedReport(terminalFacts, "en");
                Require(!string.IsNullOrEmpty(trusted) && model.m_DataList.Count == assistantHistory + 1 &&
                    model.m_DataList.Last().role == "assistant" && model.m_DataList.Last().content == trusted && trusted != falseSuccess &&
                    CurrentUserTurn(chat) == userTurn && model.m_DataList.Count(message => message.role == "user") == userMessages &&
                    model.Speech.Count == speechBefore + 1 && Get(chat, "m_SpatialGoalRunner") == null &&
                    !(bool)Get(chat, "m_FormalResponseInFlight"),
                    "One trusted incomplete-search report replaces false success; no new user, duplicate terminal, device step or execution lease is created.");
                var lateApproval = (JObject)rejection.DeepClone();lateApproval["verdict"] = "consistent";lateApproval["resultReport"] = "incomplete";
                publicReview.Complete(true, lateApproval.ToString(), "late-contradictory-work-search-terminal-approval");
                candidate.SpeechCompleteCallback(falseSuccess);yield return null;yield return null;
                Require(model.m_DataList.Count == assistantHistory + 1 && model.m_DataList.Last().content == trusted &&
                    model.Speech.Count == speechBefore + 1 && model.Decisions.Count == decisionsBeforeApprovedStep + 1 &&
                    model.SceneSearchRequests.Count == serviced && model.Matches.Count == matchesBefore &&
                    bridge.Controller.OperationRevision == operationBefore && CurrentUserTurn(chat) == userTurn &&
                    devices.Select(device => JsonUtility.ToJson(device.ReadState())).SequenceEqual(deviceStates),
                    "Late reviewer approval and duplicate completion cannot republish false success, restart the search, write a device or pollute user history.");
                ((JArray)LastReport["cases"]).Add(new JObject { ["case"] = priorIntent + "-reviewed-continue-enters-unified-search",
                    ["actualDeviceAttempt"] = "unknown-proposal-entity-never-dispatched", ["searchOutcome"] = "bounded-incomplete",
                    ["searchReviews"] = searchReceipts, ["terminalFacts"] = terminalFacts.DeepClone(),
                    ["publishedText"] = trusted, ["terminalReplyCount"] = 1, ["falseSuccessReleased"] = false });
            }

            foreach (string status in new[] { "closed", "waiting_user" })
            {
                yield return PrepareContinuation(chat, bridge, model, "inspect-object");
                int decisionsBefore = model.Decisions.Count, speechBefore = model.Speech.Count;
                NevaSceneVisionIntegrationModel.Request workReview = null;
                yield return BeginWorkReview(chat, model, request => workReview = request);
                workReview.Complete(WorkVerdict(status, ""));
                Require(!(bool)Get(chat, "m_WorkReviewOpen") && model.Decisions.Count == decisionsBefore && model.Speech.Count == speechBefore,
                    "Completed or clarification-waiting user work creates no new spatial step or feedback round.");
                workReview.Complete(WorkVerdict("continue", NextDeviceStep));
                Require(model.Decisions.Count == decisionsBefore && model.Speech.Count == speechBefore,
                    "A late duplicate continue cannot reopen work already closed or waiting for user input.");
                ((JArray)LastReport["cases"]).Add(status + "-cannot-reopen-spatial-work");
            }

            yield return PrepareContinuation(chat, bridge, model, "approach-object");
            int busyDecisions = model.Decisions.Count, busySpeech = model.Speech.Count;
            NevaSceneVisionIntegrationModel.Request busyReview = null;
            yield return BeginWorkReview(chat, model, request => busyReview = request);
            Set(chat, "m_SongSearchInFlight", true);
            try
            {
                busyReview.Complete(WorkVerdict("continue", NextDeviceStep));
                Require((string)Get(chat, "m_WorkStatus") == "waiting_tool" && model.Decisions.Count == busyDecisions && model.Speech.Count == busySpeech,
                    "An actual in-flight tool blocks a reviewed next spatial step instead of concurrently starting a device operation.");
            }
            finally { Set(chat, "m_SongSearchInFlight", false); }
            ((JArray)LastReport["cases"]).Add("busy-tool-prevents-new-spatial-work-step");

            yield return PrepareContinuation(chat, bridge, model, "approach-object");
            NevaSceneVisionIntegrationModel.Request activeSceneReview = null;
            yield return BeginWorkReview(chat, model, request => activeSceneReview = request);
            int activeSceneDecisions = model.Decisions.Count;
            Set(chat, "m_SceneActionActive", true);
            string activeSceneFact = chat.SceneActionContextJson;
            try
            {
                activeSceneReview.Complete(WorkVerdict("continue", NextDeviceStep));
                Require(model.Decisions.Count == activeSceneDecisions && chat.SceneActionContextJson == activeSceneFact,
                    "A scene action that became active during independent review keeps its ownership; no competing next-step room decision or device fact replaces it.");
            }
            finally { Set(chat, "m_SceneActionActive", false); }
            ((JArray)LastReport["cases"]).Add("active-scene-operation-prevents-competing-spatial-step");

            yield return PrepareContinuation(chat, bridge, model, "inspect-object");
            NevaSceneVisionIntegrationModel.Request exhaustedReview = null;
            yield return BeginWorkReview(chat, model, request => exhaustedReview = request);
            int exhaustedDecisions = model.Decisions.Count, exhaustedSpeech = model.Speech.Count;
            Set(chat, "m_WorkContinuations", 3);
            exhaustedReview.Complete(WorkVerdict("continue", NextDeviceStep));
            Require((string)Get(chat, "m_WorkStatus") == "blocked" && !(bool)Get(chat, "m_WorkReviewOpen") &&
                model.Decisions.Count == exhaustedDecisions && model.Speech.Count == exhaustedSpeech,
                "An exhausted existing continuation budget stays unfinished and blocked, without executing another spatial step or claiming completion.");
            ((JArray)LastReport["cases"]).Add("continuation-budget-still-bounds-spatial-steps");

            yield return PrepareContinuation(chat, bridge, model, "inspect-object");
            int cancelledDecisions = model.Decisions.Count, cancelledSpeech = model.Speech.Count;
            NevaSceneVisionIntegrationModel.Request staleWorkReview = null;
            yield return BeginWorkReview(chat, model, request => staleWorkReview = request);
            Call(chat, "ResetWorkReview", false);
            staleWorkReview.Complete(WorkVerdict("continue", NextDeviceStep));
            Require(model.Decisions.Count == cancelledDecisions && model.Speech.Count == cancelledSpeech && !(bool)Get(chat, "m_WorkReviewOpen"),
                "A cancelled user-work epoch revokes an in-flight independent review's authority to create a new spatial step.");
            ((JArray)LastReport["cases"]).Add("cancelled-work-review-cannot-create-device-step");

            yield return PrepareContinuation(chat, bridge, model, "approach-object");
            NevaSceneVisionIntegrationModel.Request lastWorkReview = null;
            yield return BeginWorkReview(chat, model, request => lastWorkReview = request);
            lastWorkReview.Complete(WorkVerdict("continue", NextDeviceStep));
            var staleStep = model.Decisions.Last();
            Require(staleStep.Kind == "room", "The stale-callback fixture holds a real pending next-step room request.");
            int staleSpeech = model.Speech.Count, staleHistory = model.m_DataList.Count;
            string staleFact = chat.SceneActionContextJson;
            Call(chat, "ResetWorkReview", false);
            Call(chat, "InvalidateFormalResponse", "new-user-cancels-work-room-step");
            staleStep.Complete(true, DeviceDecision((string)JObject.Parse(staleStep.Input)["visualObservation"]?["captureId"]), "late-cancelled-device-decision");
            Require(model.Speech.Count == staleSpeech && model.m_DataList.Count == staleHistory && chat.SceneActionContextJson == staleFact,
                "A late cancelled planner result cannot execute, replace the prior action fact, publish or write history.");
            ((JArray)LastReport["cases"]).Add("cancelled-next-step-planner-callback-has-no-authority");

            yield return PrepareContinuation(chat, bridge, model, "approach-object");
            NevaSceneVisionIntegrationModel.Request pauseWorkReview = null;
            yield return BeginWorkReview(chat, model, request => pauseWorkReview = request);
            pauseWorkReview.Complete(WorkVerdict("continue", NextDeviceStep));
            var pausedStep = model.Decisions.Last();
            Require(pausedStep.Kind == "room" && (bool)Get(chat, "m_FormalResponseInFlight"),
                "Pause fixture holds a real room planner request before any public feedback candidate exists.");
            int pauseSpeech = model.Speech.Count, pauseHistory = model.m_DataList.Count;
            Call(chat, "OnApplicationPause", true);
            string pauseFact = chat.SceneActionContextJson;
            pausedStep.Complete(true, DeviceDecision((string)JObject.Parse(pausedStep.Input)["visualObservation"]?["captureId"]), "late-paused-device-decision");
            Require(model.Speech.Count == pauseSpeech && model.m_DataList.Count == pauseHistory && chat.SceneActionContextJson == pauseFact,
                "A delayed planner decision after pause cannot execute a device step or publish/remember a reply.");
            Call(chat, "OnApplicationPause", false); Call(chat, "ClearScheduledAgentWake");
            Require(!(bool)Get(chat, "m_FormalResponseInFlight") && !(bool)Get(chat, "m_AgentRoundInFlight") &&
                (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1 && (int)Get(chat, "m_RoomSpeechGuardGeneration") == -1,
                "Resuming after a cancelled planner restores idle formal/round ownership instead of leaving the next round permanently blocked.");
            ((JArray)LastReport["cases"]).Add("application-pause-releases-pending-work-planner-owner");

            yield return PrepareContinuation(chat, bridge, model, "inspect-object");
            NevaSceneVisionIntegrationModel.Request closingWorkReview = null;
            yield return BeginWorkReview(chat, model, request => closingWorkReview = request);
            closingWorkReview.Complete(WorkVerdict("continue", NextDeviceStep));
            var closingStep = model.Decisions.Last();
            Require(closingStep.Kind == "room", "Closure fixture holds a real pending same-generation work planner.");
            int closingGeneration = (int)Get(chat, "m_FormalResponseGeneration");
            int closingSpeech = model.Speech.Count, closingHistory = model.m_DataList.Count;
            string closingFact = chat.SceneActionContextJson;
            Set(chat, "m_WorkReviewOpen", false); Set(chat, "m_WorkStatus", "closed");
            Require((int)Get(chat, "m_FormalResponseGeneration") == closingGeneration,
                "The fixture closes work without invalidating the formal generation or calling a provider callback.");
            yield return null; yield return null;
            Require(!(bool)Get(chat, "m_FormalResponseInFlight") && !(bool)Get(chat, "m_AgentRoundInFlight") &&
                (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1 && (int)Get(chat, "m_RoomSpeechGuardGeneration") == -1,
                "Planner ownership is released when work closes, even while a provider never responds.");
            closingStep.Complete(true, DeviceDecision((string)JObject.Parse(closingStep.Input)["visualObservation"]?["captureId"]), "late-closed-device-decision");
            Require(model.Speech.Count == closingSpeech && model.m_DataList.Count == closingHistory && chat.SceneActionContextJson == closingFact,
                "A late same-work planner result cannot revive closed work or replace its prior action fact.");
            ((JArray)LastReport["cases"]).Add("same-generation-work-close-releases-planner-without-provider-callback");

            yield return PrepareContinuation(chat, bridge, model, "inspect-object");
            Set(chat, "m_WorkContinuations", 2);
            NevaSceneVisionIntegrationModel.Request finalBudgetReview = null;
            yield return BeginWorkReview(chat, model, request => finalBudgetReview = request);
            int finalBudgetDecisions = model.Decisions.Count, finalBudgetSpeech = model.Speech.Count;
            finalBudgetReview.Complete(WorkVerdict("continue", NextDeviceStep));
            var finalBudgetStep = model.Decisions.Last();
            Require(model.Decisions.Count == finalBudgetDecisions + 1 && finalBudgetStep.Kind == "room" &&
                (int)Get(chat, "m_WorkContinuations") == 3 &&
                (int)JObject.Parse(finalBudgetStep.Input)["workContinuation"]["continuationOrdinal"] == 3,
                "The third permitted continuation receives a valid room-step ticket; reaching the limit does not revoke that already-authorized final step.");
            finalBudgetStep.Complete(true, new JObject { ["intent"]="none", ["origin"]="none", ["evidence"]="" }.ToString(), "controlled-final-allowed-no-action-step");
            Require(model.Speech.Count == finalBudgetSpeech + 1 && model.Speech.Last().Kind == "work-feedback" &&
                model.Speech.Last().Stage == FormalResponseStage.CommittedSpatialReply,
                "The last allowed planner result can settle into guarded feedback without executing a new action or being stranded by its ordinal fence.");
            ((JArray)LastReport["cases"]).Add("third-allowed-work-step-remains-current-until-feedback-dispatch");
            LastReport["status"]="passed";
        }
        finally
        {
            Call(chat, "OnApplicationPause", false);
            Call(chat, "CancelSpatialGoalRunner", "work-feedback-regression-finished");
            Call(chat, "CancelSpatialGoal", "work-feedback-regression-finished");
            Call(chat, "ClearPendingDeviceClarification", "work-feedback-regression-finished");
            Call(chat, "InvalidateFormalResponse", "work-feedback-regression-finished"); chat.StopAllCoroutines();
            Call(chat, "ClearScheduledAgentWake");
            Set(chat, "m_SelectedSceneObject", oldSelected); Set(chat, "m_SceneObjectStateResult", oldStateResult);
            Set(chat, "m_EnableSceneObjectTargetMatching", oldMatching);
            Set(chat, "m_EnableSceneObjectAwareness", oldAwareness);
            Set(chat, "m_EnableSceneActions", oldSceneActions);
            for (int i = 0; i < goalFields.Length; i++) Set(chat, goalFields[i], oldGoalState[i]);
            LastReport["checks"]=checks;
            LastReport["exportedReviewManifest"] = Path.Combine("review-live-inputs", "manifest.json");
            if ((string)LastReport["status"] == "running") LastReport["status"]="failed";
            File.WriteAllText(Path.Combine(output, "work-scene-feedback.json"), LastReport.ToString()+"\n");
        }
    }

    private static void RequireRuntimeControlReview(ChatSample chat, NevaSceneVisionIntegrationModel.Request review, bool requireUnresolved = false)
    {
        // Compare wire values with the same JSON representation of engine facts:
        // captured geometry is stored as Single, while parsed JSON uses Double.
        var currentObjects = JObject.Parse(((JObject)Call(chat, "BuildSceneObjectFacts")).ToString());
        var input = JObject.Parse(review.Input);
        var objects = input["sceneActionFacts"]?["objectContext"] as JObject;
        var control = objects?["runtimeDeviceControl"] as JObject;
        Require(control != null && (bool?)control["implementationConnected"] == true &&
            (bool?)control["featureEnabled"] == true && (bool?)control["physicalHandRequired"] == false &&
            (bool?)control["requiresCurrentTargetConfirmation"] == true &&
            (bool?)control["requiresIndependentUserAuthorization"] == true &&
            JToken.DeepEquals(control, currentObjects["runtimeDeviceControl"]),
            "The compiled audit input receives the connected enabled runtime interface independently of target confirmation and user approval.");
        var capabilityFields = new[] { "implementationConnected", "featureEnabled", "supportedOperation", "physicalHandRequired",
            "requiresCurrentTargetConfirmation", "requiresIndependentUserAuthorization", "scope" };
        Require(control.Properties().All(property => capabilityFields.Contains(property.Name)) &&
            JToken.DeepEquals(objects["selected"], currentObjects["selected"]) &&
            JToken.DeepEquals(objects["stateResult"], currentObjects["stateResult"]) &&
            JToken.DeepEquals(objects["knownObjectHistory"], currentObjects["knownObjectHistory"]),
            "The capability object adds no target inventory or current state; separately supplied selected/state/history remain exactly the actual serialized engine facts.");
        if (requireUnresolved)
            Require(objects["selected"]?.Type == JTokenType.Null && objects["stateResult"]?.Type == JTokenType.Null &&
                (string)((JObject)Call(chat, "BuildSpatialGoalContext"))["status"] == "none",
                "Each new unresolved capability fixture exposes neither a selected device nor a current state result and has no approved goal.");
        var payload = JObject.Parse(review.Wire);
        var messages = payload["messages"] as JArray;
        var userContent = messages?.Last?["content"];
        string wireInput = userContent?.Type == JTokenType.String ? (string)userContent :
            (string)(userContent as JArray)?.FirstOrDefault(part => (string)part["type"] == "text")?["text"];
        string imageNote = (string)typeof(ChatQW).GetField("k_AttachedImageNote", Flags).GetRawConstantValue();
        Require(review.Kind == "room-review" && messages?.Count == 2 &&
            (string)messages[0]["role"] == "system" &&
            (string)messages[0]["content"] == RoomTaskProtocol.ReviewContract + "\n" +
                (payload.SelectToken("response_format.schema") != null ? RoomTaskProtocol.CompactDecisionFieldGuide(JObject.Parse(RoomTaskProtocol.ReviewSchema)) : RoomTaskProtocol.ReviewSchema) +
                "\n" + SpeechActScopeProtocol.ReviewContract &&
            (string)messages[1]["role"] == "user" && wireInput == imageNote + review.Input &&
            payload.SelectTokens("$..image_url.url").Select(token => (string)token).SequenceEqual(new[] { review.Image }),
            "The actual compiled provider wire pairs the current factual audit contract with its unchanged input and exact current RGB, without general persona or singing history.");
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

    private static IEnumerator PrepareContinuation(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string priorIntent)
    {
        Call(chat, "CancelAutonomyIntentProbe");
        yield return Prepare(chat, bridge, model, "visible-object-not-confirmed: fixture for reviewed next step");
        var observation = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", (int)Get(chat, "m_FormalResponseGeneration"));
        Call(chat, "InvalidateFormalResponse", "work-next-step-fixture-idle");
        chat.StopAllCoroutines(); Call(chat, "ClearScheduledAgentWake");
        Call(chat, "ResetWorkReview", true);
        Call(chat, "RecordAcceptedMotionUserTurn", LampRequest);
        Set(chat, "m_LastUserMsg", LampRequest);
        // A controlled accepted user input is fixture data. Production continuations below must not add another user.
        model.m_DataList.Add(new LLM.SendData("user", LampRequest));
        Set(chat, "m_WorkStatus", "unreviewed"); Set(chat, "m_WorkNoProgress", false);
        Set(chat, "m_AgentRoundInFlight", false); Set(chat, "m_FormalResponseInFlight", false);
        Set(chat, "m_AgentCurrentRoundIsTick", true); Set(chat, "m_StreamComplete", true);
        Set(chat, "m_SceneActionActive", false); Set(chat, "m_EnableSceneObjectTargetMatching", false);
        Set(chat, "m_EnableSceneObjectAwareness", true); Set(chat, "m_EnableSceneActions", true);
        Set(chat, "m_UserSpeechActiveForAutonomy", false); Set(chat, "m_SceneTimePaused", false);
        Set(chat, "m_RoomTaskIntent", priorIntent); Set(chat, "m_RoomTaskActionId", "controlled-prior-user-lamp-step");
        Set(chat, "m_RoomTaskDispatch", priorIntent == "approach-object" ? "arrived" : "observed");
        var factType = typeof(ChatSample).GetNestedType("SceneActionFact", Flags);
        var fact = new JObject { ["actionId"]="controlled-prior-user-lamp-step", ["intent"]=priorIntent,
            ["origin"]="user", ["captureId"]=observation.CaptureId, ["resultCaptureId"]=observation.CaptureId,
            ["phase"]=priorIntent == "approach-object" ? "arrived" : "observed", ["reason"]="controlled prior user-task step only",
            ["entityId"]="controlled-prior-visible-lamp", ["objectConfirmed"]=true, ["stateChanged"]=false,
            ["reached"]=priorIntent == "approach-object", ["groundArrived"]=priorIntent == "approach-object",
            ["objectReobserved"]=priorIntent == "approach-object", ["active"]=false };
        Set(chat, "m_SceneActionFact", JsonUtility.FromJson(fact.ToString(), factType));
        Call(chat, "RecordWorkRoomTaskContext");
        Call(chat, "RecordWorkResponse", priorIntent == "approach-object" ? "已经到达目标附近，尚未执行关灯。" : "已经检查目标，尚未执行关灯。");
        Call(chat, "BindFormalSceneObservation", observation, (int)Get(chat, "m_FormalResponseGeneration"));
        Require((bool)Call(chat, "ShouldReviewUserWork") && !bridge.ReservesBody && !bridge.IsFollowing,
            "A controlled completed inspection/arrival leaves an open same-user lamp task with an idle actual body.");
    }

    private static IEnumerator BeginWorkReview(ChatSample chat, NevaSceneVisionIntegrationModel model,
        Action<NevaSceneVisionIntegrationModel.Request> receive)
    {
        int before = model.Probes.Count;
        Call(chat, "BeginAutonomyIntentProbe", "work-spatial-continuation-regression");
        yield return Wait(() => model.Probes.Count == before + 1, "Independent user-work review with real RGB");
        receive(model.Probes.Last());
    }

    private static int CurrentUserTurn(ChatSample chat)
    {
        var constraints = Get(chat, "m_ActionConstraints");
        return (int)constraints.GetType().GetProperty("CurrentUserTurn", Flags).GetValue(constraints);
    }

    private static string WorkVerdict(string status, string nextStep) => new JObject {
        ["work_status"]=status, ["work_evidence"]="controlled prior step did not switch the requested lamp",
        ["proceed"]=status == "continue", ["intent"]=nextStep, ["wait_seconds"]=45,
        ["singing_goal_status"]="none", ["singing_goal_evidence"]="no singing task in this fixture"
    }.ToString();

    private static string DeviceDecision(string capture) => new JObject {
        ["intent"]="set-object-state", ["origin"]="user", ["evidence"]="请把眼前的那盏小灯关掉",
        ["entityTarget"]=new JObject { ["captureId"]=capture, ["entityId"]="controlled-unknown-work-lamp" }, ["objectState"]="off",
        ["deviceGoal"] = new JObject { ["operation"]="set", ["requestQuote"]="小灯关掉", ["targetDescription"]="眼前的那盏小灯", ["desiredState"]="off" }
    }.ToString();

    private static string ApprovedNewGoalReview() => new JObject {
        ["verdict"]="approved", ["requestAssessment"]="requested", ["relation"]="new",
        ["referentKind"]="unbound", ["referentSourceId"]="none",
        ["reason"]="Controlled original accepted request explicitly asks this small lamp off; its executable entity remains unbound."
    }.ToString();

    private static IEnumerator Wait(Func<bool> complete, string label)
    {
        float deadline = Time.realtimeSinceStartup + 8f;
        while (!complete()) { if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(label); yield return null; }
    }

    private static IEnumerator Prepare(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model, string reason)
    {
        // Each case constructs a fresh, independent controlled user task. A
        // blocked goal from a preceding case is real retained logical memory,
        // so ResetWorkReview/InvalidateFormalResponse alone cannot clear it.
        Call(chat, "CancelSpatialGoalRunner", "work-feedback-independent-case-next");
        Call(chat, "CancelSpatialGoal", "work-feedback-independent-case-next");
        Call(chat, "ClearPendingDeviceClarification", "work-feedback-independent-case-next");
        Set(chat, "m_SpatialDeviceGoal", null); Set(chat, "m_SpatialGoalLatestReview", null);
        Set(chat, "m_SpatialGoalTerminalReplyGeneration", -1); Set(chat, "m_SpatialGoalTerminalProof", null);
        Set(chat, "m_SpatialGoalRunnerLastReport", null);
        Require(Get(chat, "m_SpatialGoalRunner") == null &&
            (string)((JObject)Call(chat, "BuildSpatialGoalContext"))["status"] == "none" &&
            (string)((JObject)Call(chat, "BuildPendingDeviceClarificationContext"))["status"] == "none",
            "Independent work-feedback setup has no preceding logical goal, pending draft or runner authority.");
        int decisionsBefore = model.Decisions.Count, speechBefore = model.Speech.Count;
        int goalReviewsBefore = model.DeviceGoalReviews.Count;
        var existing = (IEnumerator)typeof(RoomSpeechFactScopeRegression).GetMethod("Prepare", Flags)
            .Invoke(null, new object[] { chat, bridge, model, "inspect" });
        yield return existing;
        var currentPlan = model.Decisions.Last();
        var currentInput = JObject.Parse(currentPlan.Input);
        Require(model.Decisions.Count == decisionsBefore + 1 && currentPlan.Kind == "room" &&
            (string)currentInput["currentUserText"] == "请看看当前情况。" && (bool?)currentInput["autonomous"] == false &&
            model.DeviceGoalReviews.Count == goalReviewsBefore && model.Speech.Count == speechBefore + 1 &&
            model.Speech.Last().Kind == "formal",
            "Independent setup dispatches this accepted request through a new actual room planner and formal reply, without consuming an old goal review or speech.");
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
