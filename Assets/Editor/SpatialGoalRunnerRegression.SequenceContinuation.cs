using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using NeEEvA.Perception;

public static partial class SpatialGoalRunnerRegression
{
    private static IEnumerator SequenceContinuationChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, SceneObjectLightSwitch lamp, Run run)
    {
        bool previous = model.ControlReviewsEnabled; model.ControlReviewsEnabled = true;
        var cases = new JArray();
        var source = (JObject)((JObject)Get(chat, "m_RoomTaskSequence")).DeepClone();
        // Reuse the real completed predecessor; reset only the synthetic pending
        // successor for independently controlled cancellation-boundary cases.
        var successor = (JObject)source.SelectToken("steps[1]"); successor["status"] = "pending";
        successor.Remove("actionId"); successor.Remove("receipt"); successor.Remove("generation"); successor.Remove("intent");
        int generation = (int)Get(chat, "m_FormalResponseGeneration");
        string reportAction = (string)Get(chat, "m_RoomTaskActionId");
        object savedPreparation = Get(chat, "m_RoomSpeechPreparation");
        try {
            foreach (string reservation in new[] { "pending", "completed-unconsumed", "consumed-transport-owned" }) {
                var prepared = Activator.CreateInstance(typeof(ChatSample).GetNestedType("RoomSpeechPreparation", Flags), true);
                Set(prepared, "completed", reservation != "pending");
                Set(prepared, "consumed", reservation == "consumed-transport-owned");
                Set(prepared, "ownsTransport", reservation != "completed-unconsumed");
                Set(chat, "m_RoomSpeechPreparation", prepared);
                Set(chat, "m_RoomTaskSequence", source.DeepClone());
                int reviews = model.ControlReviews.Count;
                Require(!(bool)Call(chat, "TryStartRoomSequenceContinuation", generation, new Action(() => { })) && model.ControlReviews.Count == reviews,
                    "An unconsumed preparation or active transport still reserves the slot: " + reservation);
                cases.Add(new JObject { ["id"] = reservation, ["passed"] = true });
            }
            // Simulate a historical consumed record independently of the normal
            // detach path: its existence alone must no longer reserve execution.
            var consumedPreparation = Activator.CreateInstance(typeof(ChatSample).GetNestedType("RoomSpeechPreparation", Flags), true);
            Set(consumedPreparation, "completed", true); Set(consumedPreparation, "consumed", true);
            Set(consumedPreparation, "ownsTransport", false);
            Set(chat, "m_RoomSpeechPreparation", consumedPreparation);
            foreach (string boundary in new[] { "denied", "timeout", "generation", "pause", "vision", "input", "body-owner", "failed-predecessor", "duplicate" }) {
                Call(chat, "CancelSequenceContinuation", "fixture-next");
                Set(chat, "m_RoomTaskSequence", source.DeepClone());
                ((HashSet<string>)Get(chat, "m_SequenceBodyAttempts")).Clear();
                int reports = 0, controls = model.ControlReviews.Count, speech = model.Speech.Count, plans = RoomPlans(model);
                string beforeAction = bridge.ActionId;
                Require((bool)Call(chat, "TryStartRoomSequenceContinuation", generation, new Action(() => ++reports)),
                    "Actual completed device receipt admits one bounded successor review: " + boundary);
                Require(model.ControlReviews.Count == controls + 1 && reports == 0 && bridge.ActionId == beforeAction && model.Speech.Count == speech,
                    "Pending review starts neither movement nor result speech.");
                var review = model.ControlReviews.Last(); Export("continuation-" + boundary, review);
                var input = JObject.Parse(review.Input);
                Require((string)input["currentUserText"] == run.text && (string)input.SelectToken("steps[0].status") == "completed" &&
                    (bool?)input.SelectToken("steps[0].receipt.completionEvidenceValid") == true,
                    "Continuation consumes intact user intent and real same-device completion proof.");
                string approval = new JObject { ["target"] = "current-user", ["readiness"] = "satisfied", ["stepId"] = "s2", ["evidence"] = "回到我身边", ["reason"] = "Explicit immediate return." }.ToString();
                string changedField = boundary == "generation" ? "m_FormalResponseGeneration" : boundary == "vision" ? "m_SceneVisionEpoch" :
                    boundary == "input" ? "m_InputAudioRevision" : boundary == "pause" ? "m_SceneTimePaused" : null;
                object original = changedField == null ? null : Get(chat, changedField);
                if (changedField != null) Set(chat, changedField, original is bool ? (object)true : (int)original + 1);
                if (boundary == "body-owner") StageOwnedFollow(bridge, "continuation-other-follow");
                if (boundary == "failed-predecessor") ((JObject)Get(chat, "m_RoomTaskSequence"))["steps"][0]["status"] = "failed";
                string expectedAction = bridge.ActionId;
                if (boundary == "timeout") yield return Wait(() => reports == 1, 4, "finite continuation timeout resumes reporting");
                review.Complete(true, boundary == "denied" ? "{\"target\":\"current-user\",\"readiness\":\"waiting-speech\",\"stepId\":\"s2\",\"evidence\":\"\",\"reason\":\"Required prior speech.\"}" : approval, "controlled-continuation");
                if (boundary == "duplicate") {
                    string submitted = bridge.ActionId;
                    Require(submitted != beforeAction && (string)((JObject)Get(chat, "m_RoomTaskSequence")).SelectToken("steps[1].actionId") == submitted,
                        "Approved successor submits actual current-target approach while result speech is still held.");
                    Require((string)Get(chat, "m_RoomTaskActionId") == reportAction && (bool?)Goal(chat)["completionEvidenceValid"] == true && !lamp.ReadState().on,
                        "Concurrent body action does not steal device report ownership or invalidate actual completion proof.");
                    CheckRequestProgressPlayback(chat, bridge, model, generation, submitted);
                    review.Complete(true, approval, "controlled-duplicate");
                    Require(bridge.ActionId == submitted && reports == 1, "Duplicate approval cannot resubmit or duplicate report continuation.");
                } else Require(bridge.ActionId == expectedAction && reports == (changedField == null ? 1 : 0),
                    "Denied/timed-out/stale/busy lease never moves; only current owner may resume reporting: " + boundary);
                Call(chat, "CancelSequenceContinuation", "fixture-boundary-complete");
                if (changedField != null) Set(chat, changedField, original);
                if (boundary == "body-owner" || boundary == "duplicate") bridge.StopMovement("continuation-fixture-end", "End fixture movement");
                Require(model.Speech.Count == speech && RoomPlans(model) == plans, "Continuation uses no large replanner or extra speech request.");
                cases.Add(new JObject { ["id"] = boundary, ["reports"] = reports, ["passed"] = true });
            }
            Set(chat, "m_RoomTaskSequence", source.DeepClone());
            ((JObject)Get(chat, "m_RoomTaskSequence"))["steps"][0]["status"] = "failed";
            ((HashSet<string>)Get(chat, "m_SequenceBodyAttempts")).Clear();
            int failedReviews = model.ControlReviews.Count;
            Require(!(bool)Call(chat, "TryStartRoomSequenceContinuation", generation, new Action(() => throw new Exception("Invalid continuation"))) &&
                model.ControlReviews.Count == failedReviews, "Failed predecessor is rejected by the engine before any model request, regardless of model opinion.");
            cases.Add(new JObject { ["id"] = "failed-predecessor-before-request", ["passed"] = true });
        }
        finally { Set(chat, "m_RoomSpeechPreparation", savedPreparation); model.ControlReviewsEnabled = previous; Call(chat, "CancelSequenceContinuation", "fixture-complete"); }
        LastReport["sequenceContinuation"] = new JObject { ["cases"] = cases,
            ["scope"] = "Real device readback and production bounded continuation, controlled semantic judgments and boundaries; actual bridge submission. No live-model or played-speech timing claim." };
    }

    private static void CheckRequestProgressPlayback(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, int generation, string action)
    {
        string candidate = "The device is off. I'll come back to you now. Your separate answer is retained. Its final sentence survives too.";
        var ownershipInput = new JObject { ["publicSpeech"] = candidate,
            ["speechProgressOwner"] = (JObject)Call(chat, "BuildRoomSpeechProgressOwner", generation),
            ["independentSpeechClaim"] = new JObject { ["evidence"] = "The device is off.", ["otherClaims"] = new JArray() } };
        var reviewedParts = new JObject { ["bodyProgressParts"] = new JArray("p1"), ["actualLanguage"] = "en" };
        Require(RoomSpeechPartsProtocol.Required(ownershipInput) && RoomSpeechPartsProtocol.Resolve(ownershipInput, reviewedParts, out _) &&
            RoomSpeechPartsProtocol.TryRead(reviewedParts, candidate, out _, out _), "Actual current return owner admits only its audited speech fragments.");
        RoomSpeechPartsProtocol.TryRead(reviewedParts, candidate, out string prefix, out _);
        Require(prefix == "The device is off. Your separate answer is retained. Its final sentence survives too.",
            "Return promise is excluded before installing an immutable compound prefix; every independent sentence survives.");
        var progress = (JObject)Call(chat, "BuildRoomRequestProgress");
        Require((string)progress?.SelectToken("body.actionId") == action &&
            (string)progress.SelectToken("steps[0].status") == "completed", "Compound progress binds real predecessor and current successor receipts.");
        string combined = (string)Call(chat, "AppendRoomRequestProgress", prefix, "en", generation);
        Require(combined.StartsWith(prefix) && combined.Length > prefix.Length,
            "Current movement progress is appended to the audited outcome/answer without dropping it.");
        var history = new LLM.SendData("assistant", combined); model.m_DataList.Add(history);
        Call(chat, "BindRoomSpeechPlayback", generation, "en", bridge, model, history);
        var lease = Get(chat, "m_RoomSpeechPlayback");
        Require(lease != null && (bool)Get(lease, "compound"), "The compound result installs the same playback gate as movement replies.");
        var type = typeof(ChatSample).GetNestedType("PendingSpeechChunk", Flags);
        var chunk = Activator.CreateInstance(type, true);
        Set(chunk, "RoomState", lease); Set(chunk, "RoomStateVersion", (int)Get(lease, "version"));
        Set(chunk, "Text", combined); Set(chunk, "LanguageCode", "en"); Set(chunk, "RoundId", (int)Get(chat, "m_CurrentSpeechRoundId"));
        object phase = Get(bridge, "phase"), reason = Get(bridge, "reason"), diagnostic = Get(bridge, "latestDiagnostic");
        int revision = bridge.Controller.OperationRevision;
        int assessments = (int)Get(bridge, "assessmentBuildCount");
        try {
            // Exercise the actual sentence queue before any audio starts. The
            // result/answer must stay separate from a fast-changing successor.
            Call(Get(chat, "m_PendingChunks"), "Clear");
            var sentenceBuffer = (SpeechTextBuffer)Get(chat, "m_SentenceBuffer");
            sentenceBuffer.Length = 0;
            sentenceBuffer.Append(new SpeechText(combined, "en"));
            Call(chat, "FlushCompleteSentences", true);
            var queue = Get(chat, "m_PendingChunks");
            var queued = ((System.Collections.IEnumerable)queue).Cast<object>().ToArray();
            LastReport["continuationQueueDiagnostic"] = new JArray(queued.Select(c=>new JObject {
                ["text"]=(string)Get(c,"Text"), ["prefixOnly"]=(bool)Get(c,"RoomPrefixOnly"),
                ["round"]=(int)Get(c,"RoundId"), ["hasLease"]=Get(c,"RoomState")!=null }));
            string WithoutSpacing(string value) => new string(value.Where(c=>!char.IsWhiteSpace(c)).ToArray());
            Require(queued.Length >= 2 && (bool)Get(queued[0], "RoomPrefixOnly") &&
                (string)Get(queued[0], "Text") == prefix && queued.Skip(1).All(c=>!(bool)Get(c,"RoomPrefixOnly")) &&
                WithoutSpacing(string.Concat(queued.Select(c=>(string)Get(c,"Text")))) == WithoutSpacing(combined),
                "Actual queue separates the entire audited independent answer from every dynamic progress sentence without losing text.");
            Call(bridge, "SetFact", "arrived", "Controlled arrival while the independent answer plays");
            Require(Call(chat, "RefreshRoomSpeechForPlayback", queued[0], generation).ToString() == "Current",
                "Arrival does not replace or resynthesize the stable result segment.");
            Call(chat, "RecordRoomSpeechPlayed", lease, prefix);
            Require(Call(chat, "RefreshRoomSpeechForPlayback", queued[1], generation).ToString() == "Replaced" &&
                !((string)Get(queued[1], "Text")).Contains("device is off") &&
                ((string)Get(queued[1], "Text")).Contains("reached"),
                "Arrival replaces only the unplayed progress, without replaying the independent result.");
            Call(queue, "Clear");
            // Restore the independent partial-playback scenario below.
            Call(bridge, "SetFact", (string)phase, (string)reason);
            Call(chat, "BindRoomSpeechPlayback", generation, "en", bridge, model, history);
            lease = Get(chat, "m_RoomSpeechPlayback");
            Set(chunk, "RoomState", lease); Set(chunk, "RoomStateVersion", (int)Get(lease, "version"));
            Call(bridge, "SetFact", "follow-paused", "Controlled successor pause before playback");
            Require(Call(chat, "RefreshRoomSpeechForPlayback", chunk, generation).ToString() == "Replaced" &&
                ((string)Get(chunk, "Text")).StartsWith(prefix) && ((string)Get(chunk, "Text")).Contains("paused"),
                "A paused successor refreshes only its progress; unplayed device result and independent answer survive.");
            Call(chat, "RecordRoomSpeechPlayed", lease, "The device is off.");
            Call(chat, "RecordRoomSpeechPlayed", lease, "Your separate answer is retained.");
            Call(bridge, "SetFact", "moving", "Controlled successor resumed");
            Require(Call(chat, "RefreshRoomSpeechForPlayback", chunk, generation).ToString() == "Replaced" &&
                ((string)Get(chunk, "Text")).StartsWith("Its final sentence survives too.") &&
                !((string)Get(chunk, "Text")).Contains("The device is off.") &&
                bridge.Controller.OperationRevision == revision && (int)Get(bridge, "assessmentBuildCount") == assessments &&
                (bool?)Goal(chat)["completionEvidenceValid"] == true,
                "Trimmed sentence boundaries retain the unplayed independent answer; refresh cannot resubmit a body/device operation.");
            LastReport["requestProgressPlayback"] = new JObject { ["status"] = "passed", ["originalCandidate"] = candidate,
                ["retainedPrefix"] = prefix, ["published"] = combined,
                ["afterResume"] = history.content, ["scope"] = "Real owned goal/sequence and actual playback gate; controlled body phases, no audible playback claim." };
        }
        finally {
            Set(bridge, "phase", phase); Set(bridge, "reason", reason); Set(bridge, "latestDiagnostic", diagnostic);
            Call(chat, "FinishRoomSpeechPlaybackHistory", "");
            Set(chat, "m_RequestProgressSpeechPrefix", null); Set(chat, "m_RequestProgressSpeechState", null);
            Set(chat, "m_RequestProgressSpeechGeneration", -1);
        }
    }
}
