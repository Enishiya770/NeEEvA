using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RoomDecisionBudgetRegression
{
    private static void CheckSharedSceneAttention(ChatQW provider)
    {
        // Latest Play's trigger was 16 extra current cognition rows alongside a
        // six-entry offered page. Reproduce that density, then exceed it. These
        // are controlled copies of real producer rows, not an exact HTTP replay.
        var source = WorkingMemorySource();
        var expanded = (JObject)Call(null, "ProjectRoomDecisionInput", source, int.MaxValue);
        var template = (JObject)expanded["sceneCognitionSummary"]["currentObjects"][0];
        var cognition = (JObject)expanded["sceneCognitionSummary"].DeepClone();
        var rows = new JArray();
        for (int i = 0; i < 24; i++)
        {
            var row = (JObject)template.DeepClone();
            row["entityId"] = "controlled-visible-" + i;
            if (i == 0) row["authoredHint"] = JValue.CreateNull();
            rows.Add(row);
        }
        cognition["currentObjects"] = rows;
        source["visualEvidence"] = "\n[物件与自身方位的观察记录；回答当前对话可直接参考，无需先发起寻物]\n" + cognition.ToString(Formatting.None);
        string before = source.ToString(Formatting.None);
        var projected = (JObject)Call(null, "ProjectRoomWorkingMemory", source);
        var kept = (JArray)projected["sceneCognitionSummary"]["currentObjects"];
        Require(kept.Count < 16 && kept.Count <= 6, "Dense current cognition shares the offered page's finite attention window.");
        Require(before == source.ToString(Formatting.None), "Attention does not change retained runtime observations.");
        Require(JToken.DeepEquals(source["currentUserText"], projected["currentUserText"]) &&
            ((JArray)projected["visibleObjectCatalog"]["current"]["candidates"]).Count == 6,
            "Exact human request and every current selectable candidate survive attention selection.");
        var result = Budget(Wire(provider, source.ToString(Formatting.None)), false);
        Require((bool)result["ok"], "Dense current view after a completed task fits the unchanged full planning budget.");

        var dialogueState = new SceneAttentionState();
        dialogueState.Update("attention-fixture", "talk", "recent question", false, false, true, Array.Empty<JObject>(), null, 10);
        var attendedSource = (JObject)source.DeepClone();
        attendedSource["attentionState"] = dialogueState.Snapshot("planner", 10);
        var dialogueProjection = (JObject)Call(null, "ProjectRoomWorkingMemory", attendedSource);
        dialogueState.Update("attention-fixture", "task-failed", "find another observed candidate", true, true, false, Array.Empty<JObject>(), null, 11);
        attendedSource["attentionState"] = dialogueState.Snapshot("planner", 11);
        var investigationProjection = (JObject)Call(null, "ProjectRoomWorkingMemory", attendedSource);
        Require(((JArray)dialogueProjection["sceneCognitionSummary"]["currentObjects"]).Count <
            ((JArray)investigationProjection["sceneCognitionSummary"]["currentObjects"]).Count,
            "Actual planner projection expands relevant evidence for investigation and shrinks ordinary conversation.");
        Require(((JArray)dialogueProjection.SelectToken("visibleObjectCatalog.current.candidates")).Count == 6 &&
            (string)dialogueProjection["currentUserText"] == (string)source["currentUserText"],
            "Adaptive budgets never hide offered selectable candidates or the current user request.");

        var guarded = (JObject)source.DeepClone();
        var protectedRows = (JArray)cognition["currentObjects"];
        protectedRows[23]["futureConstraint"] = new JObject { ["allow"] = false };
        guarded["visualEvidence"] = "\n[物件与自身方位的观察记录；回答当前对话可直接参考，无需先发起寻物]\n" + cognition.ToString(Formatting.None);
        var protectedProjection = (JObject)Call(null, "ProjectRoomWorkingMemory", guarded);
        Require(((JArray)protectedProjection["sceneCognitionSummary"]["currentObjects"]).OfType<JObject>().Any(row => row["futureConstraint"] != null),
            "Unknown current-object constraints remain protected under dense attention.");

        var review = new JObject { ["requestedUserText"] = "重新打开刚才的小灯", ["publicSpeech"] = "现在过去。",
            ["visualEvidence"] = source["visualEvidence"].DeepClone(),
            ["sceneSpatialMemory"] = source["sceneSpatialMemory"].DeepClone() };
        var boundedReview = JObject.Parse((string)Call(null, "ApplySceneAttentionToInput", review.ToString(Formatting.None)));
        Require((string)boundedReview["requestedUserText"] == (string)review["requestedUserText"] &&
            (string)boundedReview["publicSpeech"] == (string)review["publicSpeech"] && boundedReview.ToString().Length < review.ToString().Length,
            "Reviewer shares attention without rewriting the request or speech being judged.");
        string text = (string)Call(null, "ProjectSceneAttentionText", (string)source["visualEvidence"], "重新打开刚才的小灯", null);
        string twice = (string)Call(null, "ProjectSceneAttentionText", text, "重新打开刚才的小灯", null);
        Require(text == twice && text.Length < ((string)source["visualEvidence"]).Length,
            "Formal reply attention is bounded and idempotent across repeated serialization.");

        var room = new JObject { ["currentFacts"] = new JObject { ["currentlyNearAndFacingUser"] = true },
            ["execution"] = new JObject { ["actionId"] = "current", ["targetKind"] = "user", ["phase"] = "arrived" } };
        var requirement = RoomMovementResultPolicy.Requirement(room, "approach", "current");
        Require((bool)requirement["required"] && !RoomMovementResultPolicy.ReviewReports("{\"resultReport\":\"omitted\"}", requirement) &&
            RoomMovementResultPolicy.ReviewReports("{\"resultReport\":\"arrived\"}", requirement), "Arrival requires an actual spoken result, not an old future promise.");
        Require(!(bool)RoomMovementResultPolicy.Requirement(room, "approach", "another-action")["required"],
            "Another action's arrival cannot satisfy this reply.");
        room["currentFacts"]["currentlyNearAndFacingUser"] = false;
        room["currentFacts"]["actualUserDistance"] = 3.2;
        room["currentFacts"]["desiredUserDistance"] = 1.2;
        var changedRelation = RoomMovementResultPolicy.Requirement(room, "approach", "current");
        Require((string)changedRelation["expectedReport"] == "arrived" && (string)changedRelation["arrivalRelation"] == "distance-changed",
            "Completed arrival remains reportable while live distance explicitly disallows claiming current proximity.");
        room["currentFacts"] = JValue.CreateNull();
        var unknownRelation = RoomMovementResultPolicy.Requirement(room, "approach", "current");
        Require((string)unknownRelation["expectedReport"] == "arrived" && (string)unknownRelation["arrivalRelation"] == "current-unverified",
            "Missing live proximity evidence cannot erase committed arrival or establish present proximity.");
        var schema = JObject.Parse(RoomTaskProtocol.BuildSpeechReviewSchemaForInput(new JObject { ["movementReportRequirement"] = requirement }.ToString()));
        Require(((JArray)schema["required"]).Values<string>().Contains("resultReport") &&
            ((JArray)schema["properties"]["resultReport"]["enum"]).Values<string>().Contains("arrived"), "Arrival evidence requires semantic result classification on the real review wire.");
        LastReport["sharedAttention"] = new JObject { ["status"] = "passed", ["currentRowsBefore"] = 24,
            ["currentRowsAfter"] = kept.Count, ["budget"] = result["diagnostic"]["final"].DeepClone(),
            ["provenance"] = "Latest Play density regression using controlled copies of real producer records; not an exact serialized failed request." };
    }
}
