using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Actual input-candidate interruption and empty final ASR, real current RGB,
/// production Room planner and public audit. Only model/TTS transport is controlled.</summary>
public static class InputRecoveryRoomRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Request = "请把当前看到的小灯关掉。";
    private const string FalseSuccess = "あなたの言う通りに、ちゃんと消したから。";
    private const string None = "{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}";
    private static int checks;
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        checks = 0;
        string directory = Path.Combine(output, "input-recovery-room"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["status"]="running", ["cases"]=new JArray(),
            ["scope"]="Production NotifyUserStartedSpeaking and DealingTextCallback(empty,false), current character RGB, original accepted user turn, Room planning for unapproved requests, direct unified runner/search recovery for already approved goals, and public audit. Controlled verdicts and counting TTS transport; no model-quality or audible playback claim." };
        var settings = (ChatSetting)Get(chat, "m_ChatSettings");
        TTS originalTts = settings.m_TextToSpeech;
        var tts = chat.gameObject.AddComponent<InputRecoveryRoomFixtureTTS>();
        string[] fields = { "m_SpatialDeviceGoal", "m_SpatialGoalLatestReview", "m_SpatialGoalTerminalReplyGeneration",
            "m_SpatialGoalTerminalProof", "m_PendingDeviceClarification", "m_PendingDeviceClarificationReviewRequest",
            "m_InterruptedUserQuestion", "m_InterruptedHeardText", "m_InterruptedAgentWasRunning", "m_InterruptedUserTurn", "m_InterruptedWorkEpoch",
            "m_EnableSceneObjectAwareness", "m_EnableSceneActions", "m_SceneTimePaused", "m_LastUserMsg", "m_LastUserEvidence" };
        object[] saved = fields.Select(name => Get(chat, name)).ToArray();
        settings.m_TextToSpeech = tts;
        try
        {
            // The log's unapproved draft and cancelled review coexist before the
            // empty-ASR recovery. Neither may become device-action authority.
            SceneVisionObservation oldView = null;
            yield return Prepare(chat, model, view => oldView = view);
            var oldSpeech = model.Speech.Last();
            oldSpeech.SpeechCallback(new SpeechText("まだ対象を確認しているわ。", "ja", actionDependency: SpeechActionDependency.Independent));
            oldSpeech.SpeechCompleteCallback("まだ対象を確認しているわ。");
            var oldReview = model.Decisions.Last();
            Require(oldReview.Kind == "room-review", "The interrupted candidate was awaiting the actual public room audit.");
            Call(chat, "RememberPendingDeviceClarification", Proposal(), Request,
                new RoomTaskProtocol.DeviceGoalReview { verdict="uncertain", requestAssessment="target-unclear", reason="controlled missing target distinction",
                    relation="new", referentKind="unbound", referentSourceId="none" },
                (int)Get(chat, "m_FormalResponseGeneration"));
            string pendingId = (string)((JObject)Call(chat, "BuildPendingDeviceClarificationContext"))["pendingId"];
            Require(!string.IsNullOrEmpty(pendingId) && Get(chat, "m_SpatialDeviceGoal") == null,
                "A real unapproved proposal has a pending ID and has no registered device goal.");
            int history = model.m_DataList.Count, users = model.m_DataList.Count(item => item.role == "user");
            int turn = CurrentTurn(chat), epoch = (int)Get(chat, "m_WorkEpoch"), speechCount = model.Speech.Count,
                decisionCount = model.Decisions.Count, goalReviews = model.DeviceGoalReviews.Count, actionSequence = (int)Get(chat, "m_MotionSequence");
            Call(chat, "NotifyUserStartedSpeaking");
            Require((string)Get(chat, "m_InterruptedUserQuestion") == Request && NoPublicCandidate(chat, tts),
                "The actual input-start boundary preserves the unfinished original question and cancels all unheard output.");
            Call(chat, "DealingTextCallback", "", false);
            yield return Wait(() => model.Decisions.Count > decisionCount, "Empty-ASR recovery enters current-image Room planner");
            var recoveredPlan = model.Decisions.Last();
            var planInput = JObject.Parse(recoveredPlan.Input);
            SceneVisionObservation newView = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", (int)Get(chat, "m_FormalResponseGeneration"));
            Require(recoveredPlan.Kind == "room" && newView?.HasImage == true && newView.CaptureId != oldView.CaptureId &&
                recoveredPlan.Image == newView.ImageDataUrl && newView.CapturedAtRealtime >= oldView.CapturedAtRealtime,
                "Recovery plans from a newly captured actual view, rather than the old generation or a null image.");
            Require((string)planInput["currentUserText"] == Request && (bool?)planInput["autonomous"] == false &&
                turn == CurrentTurn(chat) && epoch == (int)Get(chat, "m_WorkEpoch"),
                "The planner receives the accepted original request with its existing real-user/work owner.");
            Require(model.Speech.Count == speechCount && model.m_DataList.Count == history &&
                model.m_DataList.Count(item => item.role == "user") == users && model.DeviceGoalReviews.Count == goalReviews &&
                Get(chat, "m_SpatialDeviceGoal") == null &&
                (string)((JObject)Call(chat, "BuildPendingDeviceClarificationContext"))["pendingId"] == pendingId,
                "Empty ASR emits no generic reply or duplicate user and cannot approve, re-register or discard the unapproved draft.");
            oldReview.Complete(true, Verdict("consistent"), "late-cancelled-old-review");
            oldSpeech.SpeechCallback(new SpeechText(FalseSuccess, "ja", actionDependency: SpeechActionDependency.Independent));
            oldSpeech.SpeechCompleteCallback(FalseSuccess);
            Require(NoPublicCandidate(chat, tts) && model.m_DataList.Count == history && model.Speech.Count == speechCount,
                "Cancelled generation speech and review handles cannot publish into the recovered round.");
            recoveredPlan.Complete(true, None, "controlled-recovery-no-device-action");
            yield return Wait(() => model.Speech.Count == speechCount + 1, "Recovered no-action decision produces an audited candidate");
            var candidate = model.Speech.Last();
            Require(candidate.Kind == "work-feedback" && candidate.Stage == FormalResponseStage.CommittedSpatialReply &&
                !candidate.RecordAssistantHistory && candidate.Image == newView.ImageDataUrl,
                "Even intent=none recovery uses the forced buffered spatial reply and cannot fall through to raw General speech.");
            candidate.SpeechCallback(new SpeechText(FalseSuccess, "ja", actionDependency: SpeechActionDependency.Independent));
            yield return null;
            Require(NoPublicCandidate(chat, tts) && model.m_DataList.Count == history,
                "The exact false-success phrase remains private before candidate completion: no sentence, TTS chunk, clip, playback or history.");
            int beforeReview = model.Decisions.Count;
            candidate.SpeechCompleteCallback(FalseSuccess);
            yield return Wait(() => model.Decisions.Count == beforeReview + 1, "False recovery claim reaches the actual public reviewer");
            var review = model.Decisions.Last();
            Require(review.Kind == "room-review" && !string.IsNullOrEmpty(review.Image) &&
                (string)JObject.Parse(review.Input)["publicSpeech"] == FalseSuccess &&
                NoPublicCandidate(chat, tts) && model.m_DataList.Count == history &&
                (int)Get(chat, "m_MotionSequence") == actionSequence && Get(chat, "m_SpatialDeviceGoal") == null,
                "The completed false-success claim is independently audited against current facts before any public or device effect.");
            File.WriteAllText(Path.Combine(directory, "false-success-review.request.json"), review.Wire);
            review.Complete(true, Verdict("inconsistent"), "controlled-no-actual-device-off");
            Require(model.m_DataList.Count == history + 1 && model.m_DataList.Last().role == "assistant" &&
                model.m_DataList.Last().content != FalseSuccess && !model.m_DataList.Last().content.Contains("ちゃんと消した") &&
                tts.RequestTexts.All(text => !text.Contains("ちゃんと消した")),
                "The rejected completion phrase is replaced by a factual audited fallback and never reaches TTS or assistant history.");
            int finishedHistory = model.m_DataList.Count;
            review.Complete(true, Verdict("consistent"), "late-duplicate-approval");
            candidate.SpeechCallback(new SpeechText(FalseSuccess, "ja")); candidate.SpeechCompleteCallback(FalseSuccess);
            Require(model.m_DataList.Count == finishedHistory && model.m_DataList.Count(item => item.role == "user") == users &&
                !(bool)Get(chat, "m_FormalResponseInFlight") && tts.RequestTexts.All(text => !text.Contains("ちゃんと消した")),
                "Late approvals and fragments cannot revive the rejected phrase or leak a formal owner.");
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="actual-no-speech-recovery-unapproved-draft-false-success",
                ["oldCaptureId"]=oldView.CaptureId, ["recoveryCaptureId"]=newView.CaptureId, ["pendingId"]=pendingId,
                ["currentUserTurn"]=turn, ["duplicateUserMessages"]=0, ["falseSuccessTtsRequests"]=0,
                ["publicFallback"]=model.m_DataList.Last().content });

            // A previously approved lease can be resumed after this same-turn
            // interruption; the original authorization does not need a new quote.
            yield return Prepare(chat, model, view => oldView = view);
            var approvedReview = new RoomTaskProtocol.DeviceGoalReview { verdict="approved", requestAssessment="requested",
                relation="new", referentKind="unbound", referentSourceId="none", reason="controlled independently approved original request" };
            var approvedWire = new JObject { ["verdict"]=approvedReview.verdict, ["requestAssessment"]=approvedReview.requestAssessment,
                ["relation"]=approvedReview.relation, ["referentKind"]=approvedReview.referentKind,
                ["referentSourceId"]=approvedReview.referentSourceId, ["reason"]=approvedReview.reason };
            Require(RoomTaskProtocol.TryDeviceGoalProposalReview(approvedWire.ToString(),out var strictApproved) &&
                strictApproved.IsApproved && strictApproved.HasTypedReferent &&
                !RoomTaskProtocol.TryDeviceGoalProposalReview("{\"verdict\":\"approved\",\"reason\":\"Legacy approval cannot be a new proposal response.\"}",out _),
                "The registered fixture uses current typed authorization; a bare legacy approval remains invalid as a new proposal response.");
            object[] register = { Proposal(), Request, strictApproved, null };
            Require((bool)Call(chat, "TryRegisterReviewedSpatialGoal", register), "The second case starts with a genuinely registered approved goal.");
            string approvedId = (string)((JObject)Call(chat, "BuildSpatialGoalContext"))["goalId"];
            int oldGeneration = (int)Get(chat, "m_FormalResponseGeneration");
            var interruptedCandidate = model.Speech.Last();var settledOriginalPlan = model.Decisions.Last();
            Require((bool)Call(chat, "TryBeginSpatialGoalRunner", approvedId, oldGeneration),
                "The approved request holds a real runner lease that the input boundary must suspend.");
            var oldRunner = Get(chat, "m_SpatialGoalRunner");
            int approvedTurn = CurrentTurn(chat), approvedEpoch = (int)Get(chat, "m_WorkEpoch"), approvedReviews = model.DeviceGoalReviews.Count;
            int approvedSearches = model.SceneSearchRequests.Count, approvedSpeech = model.Speech.Count, approvedMatches = model.Matches.Count;
            int approvedUsers = model.m_DataList.Count(item => item.role == "user"), approvedOperation = bridge.Controller.OperationRevision;
            int approvedMotionSequence = (int)Get(chat, "m_MotionSequence");
            decisionCount = model.Decisions.Count; history = model.m_DataList.Count;
            Call(chat, "NotifyUserStartedSpeaking");
            var pausedGoal = (JObject)Call(chat, "BuildSpatialGoalContext");
            Require((string)pausedGoal["status"] == "suspended" && (bool?)pausedGoal["executionSuspended"] == true &&
                Get(chat, "m_SpatialGoalRunner") == null && Call(chat, "GetActiveSpatialGoalId") == null,
                "The input-start boundary releases the old runner and execution lease without deleting logical authorization.");
            Call(chat, "DealingTextCallback", "", false);
            yield return Wait(() => (string)Call(chat, "GetActiveSpatialGoalId") == approvedId &&
                Get(chat, "m_SpatialGoalRunner") != null && !ReferenceEquals(Get(chat, "m_SpatialGoalRunner"),oldRunner) &&
                model.SceneSearchRequests.Count == approvedSearches + 1,
                "Same-turn recovery reacquires a fresh approved lease and current-image unified search without another Room plan");
            var resumedGoal = (JObject)Call(chat, "BuildSpatialGoalContext");var resumedRunner = Get(chat,"m_SpatialGoalRunner");
            int recoveredGeneration = (int)Get(chat,"m_FormalResponseGeneration");
            var recoveredSearch = model.SceneSearchRequests.Last();var recoveredSearchInput = JObject.Parse(recoveredSearch.Input);
            var recoveredSearchOwner = Get(chat,"m_SceneSearch");
            var approvedView = recoveredSearchOwner == null ? null : (SceneVisionObservation)Get(recoveredSearchOwner,"observation");
            var approvedPanel = chat.LastSceneSearchCandidateView;
            Require(recoveredGeneration != oldGeneration && (string)resumedGoal["goalId"] == approvedId &&
                (bool?)resumedGoal["executionCurrent"] == true && (string)resumedGoal["originalRequest"] == Request &&
                CurrentTurn(chat) == approvedTurn && (int)Get(chat,"m_WorkEpoch") == approvedEpoch &&
                model.DeviceGoalReviews.Count == approvedReviews && model.Decisions.Count == decisionCount &&
                model.m_DataList.Count == history && model.m_DataList.Count(item => item.role == "user") == approvedUsers &&
                model.Speech.Count == approvedSpeech && model.Matches.Count == approvedMatches &&
                bridge.Controller.OperationRevision == approvedOperation && (int)Get(chat,"m_MotionSequence") == approvedMotionSequence &&
                NoPublicCandidate(chat,tts),
                "Only the same reviewed user/request and scene owner bind the existing goal to fresh execution, with no new Room plan, authorization, user history, speech or device action.");
            Require(recoveredSearchOwner != null && (int)Get(recoveredSearchOwner,"generation") == recoveredGeneration &&
                (int)Get(recoveredSearchOwner,"userTurn") == approvedTurn && (int)Get(recoveredSearchOwner,"workEpoch") == approvedEpoch &&
                (string)Get(recoveredSearchOwner,"deviceGoalId") == approvedId &&
                approvedView?.HasImage == true && approvedView.CaptureId != oldView.CaptureId &&
                approvedView.CapturedAtRealtime >= oldView.CapturedAtRealtime && (bool)Call(chat,"IsFreshSceneObservation",approvedView) &&
                approvedPanel != null && !approvedPanel.IsHistorical && approvedPanel.CaptureId == approvedView.CaptureId &&
                ReferenceEquals(Get(approvedPanel,"observation"),approvedView) && recoveredSearch.Image == approvedPanel.ImageDataUrl &&
                (string)recoveredSearchInput["imageRole"] == "current" &&
                (string)recoveredSearchInput["currentCaptureId"] == approvedView.CaptureId &&
                (string)recoveredSearchInput["task"]?["originalUserRequest"] == Request &&
                (string)recoveredSearchInput["task"]?["deviceGoalId"] == approvedId &&
                (int?)recoveredSearchInput["task"]?["generation"] == recoveredGeneration &&
                (int?)recoveredSearchInput["task"]?["userTurn"] == approvedTurn,
                "Recovered unified search sends the exact current candidate panel derived from its own fresh RGB and accepted target/goal/turn, never the interrupted generation's image or a synthetic instruction.");
            interruptedCandidate.SpeechCallback(new SpeechText(FalseSuccess,"ja",actionDependency:SpeechActionDependency.Independent));
            interruptedCandidate.SpeechCompleteCallback(FalseSuccess);
            settledOriginalPlan.Complete(true, None, "late-pre-interruption-room-decision");
            yield return null;yield return null;
            Require(ReferenceEquals(Get(chat,"m_SpatialGoalRunner"),resumedRunner) &&
                (string)Call(chat,"GetActiveSpatialGoalId") == approvedId && (int)Get(chat,"m_FormalResponseGeneration") == recoveredGeneration &&
                model.SceneSearchRequests.Count == approvedSearches + 1 && model.Decisions.Count == decisionCount &&
                model.DeviceGoalReviews.Count == approvedReviews && model.Speech.Count == approvedSpeech && model.Matches.Count == approvedMatches &&
                model.m_DataList.Count == history && model.m_DataList.Count(item => item.role == "user") == approvedUsers &&
                CurrentTurn(chat) == approvedTurn && bridge.Controller.OperationRevision == approvedOperation && NoPublicCandidate(chat,tts),
                "Old candidate fragments/completion and a settled old planner callback cannot publish, replace the recovered lease or add a duplicate search, action or user turn.");
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="same-accepted-turn-approved-goal-resumes-current-execution-lease",
                ["goalId"]=approvedId, ["oldGeneration"]=oldGeneration, ["recoveryGeneration"]=recoveredGeneration,
                ["oldCaptureId"]=oldView.CaptureId, ["recoveryCaptureId"]=approvedView.CaptureId, ["userTurn"]=approvedTurn,
                ["roomPlansAdded"]=0, ["goalReviewsAdded"]=0, ["searchRequestsAdded"]=1, ["duplicateUserMessages"]=0,
                ["staleCallbacksReleasedSpeech"]=false });

            // An interruption record must not survive a different accepted turn
            // even if its text happens to be identical.
            yield return Prepare(chat, model, view => oldView = view);
            Call(chat, "NotifyUserStartedSpeaking");
            Call(chat, "RecordAcceptedMotionUserTurn", Request);
            int changedTurn = CurrentTurn(chat), noRecoveryDecisions = model.Decisions.Count, noRecoverySpeech = model.Speech.Count;
            Call(chat, "DealingTextCallback", "", false);
            yield return null; yield return null;
            Require(CurrentTurn(chat) == changedTurn && model.Decisions.Count == noRecoveryDecisions &&
                model.Speech.Count == noRecoverySpeech && Get(chat, "m_InterruptedUserQuestion") == null && NoPublicCandidate(chat, tts),
                "Matching text in another accepted user turn cannot recover the stale interrupted question or publish old speech.");
            ((JArray)LastReport["cases"]).Add("same-text-new-user-owner-rejects-old-empty-input-recovery");

            // Inject the actual live judge failure without invoking the separate
            // topic's tools: approved/new must neither erase nor approve the draft.
            yield return Prepare(chat, model, view => oldView = view);
            string keptPending = RememberDraft(chat);
            const string independentTopic = "给我唱首歌吧";
            NevaSceneVisionIntegrationModel.Request updateReview = null;
            yield return BeginPendingUpdate(chat, model, independentTopic, value => updateReview=value);
            actionSequence = (int)Get(chat, "m_MotionSequence"); decisionCount = model.Decisions.Count;
            updateReview.Complete(true, PendingVerdict(keptPending, "new"), "injected-real-model-parallel-task-wrong-new");
            yield return Wait(() => model.Decisions.Count > decisionCount, "Wrong-new relation plans only the actual independent latest request");
            var retained = (JObject)Call(chat, "BuildPendingDeviceClarificationContext");
            Require((string)retained["pendingId"] == keptPending && (string)retained["originalUserRequest"] == Request &&
                (string)JObject.Parse(model.Decisions.Last().Input)["currentUserText"] == independentTopic &&
                Get(chat, "m_SpatialDeviceGoal") == null && (int)Get(chat, "m_MotionSequence") == actionSequence,
                "An injected approved/new on the independent topic preserves the original unapproved draft and plans only the latest words, without registering or writing a device goal.");
            Require((string)retained["actualUserUpdates"]?[0]?["text"] == independentTopic &&
                (string)retained["actualUserUpdates"]?[0]?["source"] == "actual-accepted-user-text" &&
                (bool?)retained["registered"] == false,
                "Pending context retains the actual independent user words separately from the incorrect model classification.");
            Require((string)Call(chat,"BuildSpatialGoalReviewFallback","zh") == "我还没有开始寻找目标，可以澄清这次请求中未确定的部分吗？",
                "An unregistered proposal reports that search has not started and leaves its unresolved details open, without claiming a failed search or repeating the old lamp action.");
            const string actualClarification = "对，就是刚才看到那盏小灯，继续关掉。";
            yield return BeginPendingUpdate(chat, model, actualClarification, value => updateReview=value);
            var clarificationInput = JObject.Parse(updateReview.Input);
            Require((string)clarificationInput["pendingId"] == keptPending &&
                (string)clarificationInput["actualUserUpdates"]?[0]?["text"] == independentTopic &&
                (string)clarificationInput["latestUserText"] == actualClarification,
                "The next independent combined review sees the same pending request, actual intervening topic and actual latest clarification.");
            decisionCount = model.Decisions.Count;
            updateReview.Complete(true, PendingVerdict(keptPending, "clarify"), "controlled-real-same-draft-clarification-approved");
            yield return Wait(() => model.Decisions.Count > decisionCount, "Actual clarification approval creates its fresh first Room plan");
            var clarifiedGoal = (JObject)Call(chat, "BuildSpatialGoalContext");
            Require((string)((JObject)Call(chat, "BuildPendingDeviceClarificationContext"))["status"] == "none" &&
                (string)clarifiedGoal["originalRequest"] == Request && (string)clarifiedGoal["latestUserText"] == actualClarification &&
                (bool?)clarifiedGoal["executionCurrent"] == true && (string)clarifiedGoal["boundEntityId"] == null &&
                (int)Get(chat, "m_MotionSequence") == actionSequence,
                "Only the subsequent independently approved clarification registers the original unbound goal; the retained draft never supplied an action or guessed entity.");
            ((JArray)LastReport["cases"]).Add("injected-wrong-new-preserves-unapproved-draft-until-real-clarification-approved");

            yield return Prepare(chat, model, view => oldView = view);
            keptPending = RememberDraft(chat);
            const string replacement = "不要关左边那盏灯，改为打开右边的灯。";
            yield return BeginPendingUpdate(chat, model, replacement, value => updateReview=value);
            decisionCount = model.Decisions.Count;
            updateReview.Complete(true, PendingVerdict(keptPending, "new"), "controlled-real-device-replacement-not-yet-registered");
            yield return Wait(() => model.Decisions.Count > decisionCount, "Replacement relation alone leaves the independent new proposal uncommitted");
            Require(Get(chat, "m_SpatialDeviceGoal") == null &&
                (string)((JObject)Call(chat, "BuildPendingDeviceClarificationContext"))["pendingId"] == keptPending,
                "A real replacement classification retains quoted evidence while no replacement device proposal has passed its independent registration gate.");
            const string identityOnly = "对，就是左边那盏。";
            yield return BeginPendingUpdate(chat, model, identityOnly, value => updateReview=value);
            var identityInput = JObject.Parse(updateReview.Input);
            Require((string)identityInput["originalUserRequest"] == Request && (string)identityInput["latestUserText"] == identityOnly &&
                identityInput["actualUserUpdates"].OfType<JObject>().Any(item => (string)item["text"] == replacement) &&
                identityInput["scope"].ToString().Contains("cancellation") && Get(chat, "m_SpatialDeviceGoal") == null,
                "A later identity-only answer cannot erase the real intervening withdrawal/replacement from the combined-review wire or establish device authority itself.");
            File.WriteAllText(Path.Combine(directory, "replacement-then-identity-review.request.json"), updateReview.Wire);
            Require(JObject.Parse(updateReview.Wire).ToString().Contains(replacement),
                "The production provider serializer actually transmits the exact intervening replacement text, not only its model reason.");
            ((JArray)LastReport["cases"]).Add("actual-replacement-history-survives-into-later-identity-review-wire");

            yield return Prepare(chat, model, view => oldView = view);
            keptPending = RememberDraft(chat);
            const string explicitCancellation = "取消这个待澄清的关灯请求。";
            yield return BeginPendingUpdate(chat, model, explicitCancellation, value => updateReview=value);
            actionSequence = (int)Get(chat, "m_MotionSequence");
            updateReview.Complete(true, PendingVerdict(keptPending, "cancel"), "controlled-explicit-pending-cancellation");
            Require((string)((JObject)Call(chat, "BuildPendingDeviceClarificationContext"))["status"] == "none" &&
                Get(chat, "m_SpatialDeviceGoal") == null && (int)Get(chat, "m_MotionSequence") == actionSequence,
                "Explicit independently reviewed cancellation still clears the pending request without any device action.");
            ((JArray)LastReport["cases"]).Add("explicit-pending-cancel-still-clears-without-device-action");

            yield return Prepare(chat, model, view => oldView = view);
            keptPending = RememberDraft(chat);
            const string newDeviceRequest = "请打开右边那盏灯。";
            yield return BeginPendingUpdate(chat, model, newDeviceRequest, value => updateReview=value);
            decisionCount = model.Decisions.Count;
            updateReview.Complete(true, PendingVerdict(keptPending, "new"), "controlled-new-device-request-classified");
            yield return Wait(() => model.Decisions.Count > decisionCount, "Actual replacement device receives its own planner");
            var newPlan = model.Decisions.Last(); int independentReviews = model.DeviceGoalReviews.Count;
            newPlan.Complete(true, new JObject { ["intent"]="none", ["origin"]="user", ["evidence"]=newDeviceRequest,
                ["deviceGoal"]=new JObject { ["operation"]="set", ["requestQuote"]=newDeviceRequest,
                    ["targetDescription"]="右边那盏灯", ["desiredState"]="on" } }.ToString(), "controlled-new-device-proposal");
            yield return Wait(() => model.DeviceGoalReviews.Count == independentReviews+1, "Replacement proposal receives the independent new-goal review");
            var independentReview = model.DeviceGoalReviews.Last();
            Require((string)JObject.Parse(independentReview.Input)["mode"] == "new-proposal" &&
                (string)JObject.Parse(independentReview.Input)["currentUserText"] == newDeviceRequest &&
                (string)((JObject)Call(chat, "BuildPendingDeviceClarificationContext"))["pendingId"] == keptPending &&
                Get(chat, "m_SpatialDeviceGoal") == null,
                "The old draft remains unapproved while the actual new device request waits for its own full-original-request authorization.");
            independentReview.Complete(true, new JObject { ["verdict"]="approved", ["reason"]="controlled independent new request approval",
                ["requestAssessment"]="requested", ["relation"]="new", ["referentKind"]="unbound", ["referentSourceId"]="none" }.ToString(), "controlled-new-device-independently-approved");
            var replacementGoal = (JObject)Call(chat, "BuildSpatialGoalContext");
            Require((string)((JObject)Call(chat, "BuildPendingDeviceClarificationContext"))["status"] == "none" &&
                (string)replacementGoal["originalRequest"] == newDeviceRequest && (string)replacementGoal["desiredState"] == "on" &&
                (string)replacementGoal["boundEntityId"] == null,
                "Successful independent new-device registration clears the old draft and carries only the newly approved request/state, without inheriting a guessed identity.");
            ((JArray)LastReport["cases"]).Add("new-device-proposal-clears-old-draft-only-after-independent-registration");
            LastReport["status"]="passed";
        }
        finally
        {
            Call(chat, "CancelSpatialGoalRunner", "input-recovery-regression-finished");
            Call(chat, "CancelSpatialGoal", "input-recovery-regression-finished");
            Call(chat, "ClearPendingDeviceClarification", "input-recovery-regression-finished");
            Call(chat, "InvalidateFormalResponse", "input-recovery-regression-finished"); chat.StopAllCoroutines();
            Call(chat, "ClearScheduledAgentWake");
            settings.m_TextToSpeech = originalTts;
            for (int i=0; i<fields.Length; i++) Set(chat, fields[i], saved[i]);
            UnityEngine.Object.DestroyImmediate(tts);
            LastReport["checks"]=checks;
            if ((string)LastReport["status"] == "running") LastReport["status"]="failed";
            File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString()+"\n");
        }
    }

    private static IEnumerator Prepare(ChatSample chat, NevaSceneVisionIntegrationModel model, Action<SceneVisionObservation> ready)
    {
        Call(chat, "CancelSpatialGoalRunner", "input-recovery-fixture-next"); Call(chat, "CancelSpatialGoal", "input-recovery-fixture-next");
        Call(chat, "ClearPendingDeviceClarification", "input-recovery-fixture-next");
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound", Flags).Invoke(null, new object[] { chat });
        ((InputRecoveryRoomFixtureTTS)((ChatSetting)Get(chat, "m_ChatSettings")).m_TextToSpeech).RequestTexts.Clear();
        Set(chat, "m_SpatialDeviceGoal", null); Set(chat, "m_SpatialGoalLatestReview", null);
        Set(chat, "m_SpatialGoalTerminalReplyGeneration", -1); Set(chat, "m_SpatialGoalTerminalProof", null);
        Set(chat, "m_InterruptedUserQuestion", null); Set(chat, "m_SceneTimePaused", false);
        Set(chat, "m_EnableSceneObjectAwareness", true); Set(chat, "m_EnableSceneActions", true);
        Set(chat, "m_LastUserMsg", Request); Set(chat, "m_LastUserEvidence", Request);
        Call(chat, "RecordAcceptedMotionUserTurn", Request);
        SceneVisionObservation source = null;
        Call(chat, "RequestSceneObservation", "input-recovery-fixture-source", new Action<SceneVisionObservation>(view => source=view));
        yield return Wait(() => source?.HasImage == true, "Actual pre-interruption camera image");
        int decisions = model.Decisions.Count, speech = model.Speech.Count;
        Call(chat, "StartStreaming", Request, false, null, false, false, false, null, false, source, true);
        yield return Wait(() => model.Decisions.Count == decisions+1, "Original accepted request enters production Room planner");
        model.Decisions.Last().Complete(true, new JObject { ["intent"]="inspect", ["origin"]="user", ["evidence"]=Request }.ToString(), "controlled-pre-interruption-inspection");
        yield return Wait(() => model.Speech.Count == speech+1, "Original candidate awaits audit before interruption");
        Set(chat, "m_AgentRoundInFlight", true); Set(chat, "m_UserTurnAwaitingReplySince", Time.realtimeSinceStartup);
        ready(source);
    }

    private static RoomTaskProtocol.Decision Proposal() => new RoomTaskProtocol.Decision { intent="none", origin="user", evidence=Request,
        deviceGoal=new RoomTaskProtocol.DeviceGoalProposal { operation="set", requestQuote=Request,
            targetDescription="当前看到的小灯", desiredState="off" } };
    private static string RememberDraft(ChatSample chat)
    {
        Call(chat,"RememberPendingDeviceClarification",Proposal(),Request,
            new RoomTaskProtocol.DeviceGoalReview { verdict="uncertain",requestAssessment="target-unclear",reason="controlled missing original target distinction",
                relation="new",referentKind="unbound",referentSourceId="none" },
            (int)Get(chat,"m_FormalResponseGeneration"));
        string id=(string)((JObject)Call(chat,"BuildPendingDeviceClarificationContext"))["pendingId"];
        Require(!string.IsNullOrEmpty(id) && Get(chat,"m_SpatialDeviceGoal") == null,"Each pending update case starts from the actual unapproved draft and no registered goal.");
        return id;
    }
    private static IEnumerator BeginPendingUpdate(ChatSample chat, NevaSceneVisionIntegrationModel model, string latest,
        Action<NevaSceneVisionIntegrationModel.Request> ready)
    {
        Call(chat,"SetCurrentUserInput",latest,latest); Call(chat,"RecordAcceptedMotionUserTurn",latest);
        Set(chat,"m_UserSpeechActiveForAutonomy",false); Set(chat,"m_AgentCurrentRoundIsTick",false);
        Set(chat,"m_InterruptedUserQuestion",null);
        SceneVisionObservation source=null;
        Call(chat,"RequestSceneObservation","input-recovery-pending-update-source",new Action<SceneVisionObservation>(value=>source=value));
        yield return Wait(()=>source?.HasImage==true,"Current image for the actual pending follow-up request");
        int reviews=model.DeviceGoalReviews.Count;
        Call(chat,"StartStreaming",latest,false,null,false,false,false,null,false,source,true);
        Set(chat,"m_AgentRoundInFlight",true);
        yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,"Production strict pending update review");
        var review=model.DeviceGoalReviews.Last();
        Require((string)JObject.Parse(review.Input)["mode"]=="pending-proposal-update" &&
            (string)JObject.Parse(review.Input)["latestUserText"]==latest,
            "The actual fresh user turn reaches the separate six-field combined pending review, before any planner action.");
        ready(review);
    }
    private static string PendingVerdict(string id,string relation)
    {
        string value = new JObject { ["verdict"]="approved",
            ["reason"]="controlled pending relation verdict; no device permission unless separately clarified/registered",
            ["relation"]=relation, ["pendingClarificationId"]=id, ["referentKind"]="unbound", ["referentSourceId"]="none" }.ToString();
        Require(RoomTaskProtocol.TryPendingDeviceClarificationReview(value,out var review) && review.HasTypedReferent &&
            review.pendingClarificationId == id && review.referentKind == "unbound" && review.referentSourceId == "none",
            "A current pending callback uses the separate typed six-field contract and does not inherit a legacy identity pair.");
        return value;
    }
    private static bool NoPublicCandidate(ChatSample chat, InputRecoveryRoomFixtureTTS tts) =>
        ((SpeechTextBuffer)Get(chat, "m_SentenceBuffer")).Length == 0 && ((ICollection)Get(chat, "m_PendingChunks")).Count == 0 &&
        ((ICollection)Get(chat, "m_PendingClips")).Count == 0 && !chat.IsAISpeaking && !chat.IsVoiceOutputPlaying && tts.RequestTexts.Count == 0;
    private static int CurrentTurn(ChatSample chat) { object constraints=Get(chat,"m_ActionConstraints"); return (int)constraints.GetType().GetProperty("CurrentUserTurn", Flags).GetValue(constraints); }
    private static string Verdict(string verdict) => new JObject { ["verdict"]=verdict,
        ["reason"]="controlled original device request remains unperformed; no successful off readback", ["factScope"]="scene-action" }.ToString();
    private static IEnumerator Wait(Func<bool> ready, string label) { float end=Time.realtimeSinceStartup+8f; while (!ready()) { if (Time.realtimeSinceStartup>end) throw new TimeoutException(label); yield return null; } }
    private static object Call(object target, string name, params object[] args) => NevaSceneVisionIntegrationRegression.Call(target,name,args);
    private static object Get(object target, string name) => target.GetType().GetField(name,Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name,Flags).SetValue(target,value);
    private static void Require(bool value,string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
}

public sealed class InputRecoveryRoomFixtureTTS : TTS
{
    public readonly List<string> RequestTexts = new List<string>();
    public override void Speak(SpeechText speech, Action<AudioClip,string> callback)
    { RequestTexts.Add(speech.Text); callback(null,speech.Text); }
}
