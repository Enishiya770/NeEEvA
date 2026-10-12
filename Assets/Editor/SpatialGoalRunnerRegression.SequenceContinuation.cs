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
        try {
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
        finally { model.ControlReviewsEnabled = previous; Call(chat, "CancelSequenceContinuation", "fixture-complete"); }
        LastReport["sequenceContinuation"] = new JObject { ["cases"] = cases,
            ["scope"] = "Real device readback and production bounded continuation, controlled semantic judgments and boundaries; actual bridge submission. No live-model or played-speech timing claim." };
    }
}
