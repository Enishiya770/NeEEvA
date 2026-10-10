using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RoomDecisionBudgetRegression
{
    private static JObject WorkingMemorySource()
    {
        var source = RealProducerInput();
        source["planningContextVersion"] = "working-memory-v1";
        source["spatialGoal"] = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/RoomWorkingMemory.completed-task.json"));
        source["standingGoal"] = new JObject { ["status"] = "none" };
        source["pendingDeviceRequest"] = new JObject { ["status"] = "none", ["registered"] = false };
        source["currentRequestOnly"] = false;
        source["currentUserText"] = source["latestUserText"] = "来我身边。";
        source["currentRequestSemantics"] = new JObject { ["requestKind"] = "action", ["requiresActionOrControl"] = true,
            ["effectiveRoute"] = "tools", ["actionAuthorization"] = false };
        return source;
    }

    private static JArray ReadWorkingMemoryIndex(JObject input)
    {
        var token = input.SelectToken("workingMemory.recallIndex");
        if (token is JArray array) return array;
        var table = (JObject)token;
        var columns = (JArray)table["columns"];
        return new JArray(((JArray)table["rows"]).OfType<JArray>().Select(row =>
            new JObject(columns.Select((column, index) => new JProperty((string)column, row[index].DeepClone())))));
    }

    private static void CheckWorkingMemory(ChatQW provider)
    {
        var source = WorkingMemorySource();
        string before = source.ToString(Formatting.None);
        var working = (JObject)Call(null, "ProjectRoomWorkingMemory", source);
        Require(before == source.ToString(Formatting.None), "Working memory does not delete runtime task/object/history records.");
        var completed = working["workingMemory"]["completedTasks"]["spatialGoal"];
        Require(completed["searchProgress"] == null && completed["referenceContext"] == null &&
            (bool?)completed["archivedDetail"]["searchProgressArchived"] == true &&
            working["spatialGoal"]["originalRequest"] == null &&
            (string)working["spatialGoal"]["archivedContextPath"] == "workingMemory.completedTasks.spatialGoal",
            "The latest actual completed task sheds its trace and frozen dialogue from the next planning turn.");
        foreach (string name in new[] { "originalRequest", "boundEntityId", "status", "completionEvidence", "completionEvidenceValid", "desiredState" })
            Require(JToken.DeepEquals(source["spatialGoal"][name], completed[name]), "Terminal identity/result remains exact: " + name);
        foreach (string name in new[] { "currentUserText", "latestUserText", "currentRequestSemantics", "currentFacts", "currentSupport", "decisionContractFacts", "visualObservation" })
            Require(JToken.DeepEquals(source[name], working[name]), "Current authoritative input remains exact: " + name);
        Require(((JArray)working["visibleObjectCatalog"]["current"]["candidates"]).Count == 6 &&
            ((JArray)working["planningHistoryImageCaptureIds"]).Count == 0, "Current visible candidates survive; terminal task images are not eagerly reattached.");
        Require(((JArray)working["sceneSpatialMemory"]["viewSamples"]).Count == 2, "Self-motion samples stay bounded while object/view recall indexes remain.");

        var reports = new JArray();
        foreach (string status in new[] { "satisfied", "cancelled", "suspended", "waiting_clarification", "searching" })
        {
            var state = (JObject)source.DeepClone(); state["spatialGoal"]["status"] = status;
            var projected = (JObject)Call(null, "ProjectRoomWorkingMemory", state);
            if (status != "satisfied" && status != "cancelled")
            {
                foreach (string field in new[] { "referenceContext", "originalRequest", "desiredState", "completionPredicate", "userUpdates", "executionRevision" })
                    Require(JToken.DeepEquals(state["spatialGoal"][field], projected["spatialGoal"][field]), "Unfinished task recovery field remains exact: " + status + "." + field);
                foreach (string field in new[] { "completedSteps", "observedViews" })
                    Require(JToken.DeepEquals(state["spatialGoal"]["searchProgress"][field], ExpandProgressTable(projected["spatialGoal"]["searchProgress"][field])),
                        "Unfinished coverage/failures remain recoverable: " + status + "." + field);
            }
            var result = Budget(Wire(provider, state.ToString(Formatting.None)), false);
            Require((bool)result["ok"], "Cross-task working memory fits unchanged budget for " + status);
            reports.Add(new JObject { ["status"] = status, ["final"] = result["diagnostic"]["final"].DeepClone() });
        }

        var growth = (JObject)source.DeepClone();
        var oldRows = (JArray)source["visibleObjectCatalog"]["memory"];
        var memories = new JArray();
        for (int i = 0; i < 48; ++i)
        {
            var item = (JObject)oldRows[i % oldRows.Count].DeepClone();
            string previousEntity = (string)item["entityId"];
            item = JObject.Parse(item.ToString(Formatting.None).Replace(previousEntity, "controlled-observed-object-" + i));
            item["ageSeconds"] = 0; item["observationCount"] = 1;
            memories.Add(item);
        }
        growth["visibleObjectCatalog"]["memory"] = memories;
        var growthResult = Budget(Wire(provider, growth.ToString(Formatting.None)), false);
        Require((bool)growthResult["ok"], "48 retained observations do not become 48 detailed planning records.");
        var admitted = Input((string)growthResult["wire"]);
        Require((bool)Call(null, "RestoreRoomDecisionSharedEvidence", admitted), "Interned capture references expand without nested/cyclic evidence references.");
        Require(ReadWorkingMemoryIndex(admitted).Count <= 12, "Ordinary recall has a shared 12-entry attention bound across collections.");
        reports.Add(new JObject { ["status"] = "48-fresh-observations", ["final"] = growthResult["diagnostic"]["final"].DeepClone() });

        var memory = (JObject)oldRows[0].DeepClone();
        memory["modelDescription"] = null; memory["matchedOriginalTarget"] = null; memory["observationCount"] = 1;
        memory["ageSeconds"] = 0;
        var clear = (JObject)Call(null, "RoomWorkingMemoryRecallIndex", memory);
        memory["ageSeconds"] = 120;
        var fading = (JObject)Call(null, "RoomWorkingMemoryRecallIndex", memory);
        memory["ageSeconds"] = 270;
        var weak = (JObject)Call(null, "RoomWorkingMemoryRecallIndex", memory);
        Require(clear["pointAtCapture"] != null && fading["pointAtCapture"] == null && fading["coarseBodyDirection"] != null &&
            weak["pointAtCapture"] == null && weak["coarseBodyDirection"] == null,
            "Aging changes clear coordinates into coarse direction and then identity/time only; it never jitters or invents a point.");
        double once = (double)Call(null, "RoomWorkingMemoryRecallStrength", memory);
        memory["observationCount"] = 5;
        Require((double)Call(null, "RoomWorkingMemoryRecallStrength", memory) > once, "Repeated observed attention slows retention decay without claiming truth confidence.");
        memory["observationCount"] = 1; memory["ageSeconds"] = 900;
        var faded = (JObject)source.DeepClone(); faded["visibleObjectCatalog"]["memory"] = new JArray(memory);
        faded["sceneSpatialMemory"]["objects"] = new JArray();
        var forgotten = (JObject)Call(null, "ProjectRoomWorkingMemory", faded);
        Require(ReadWorkingMemoryIndex(forgotten).Count == 0, "Old unattended observations leave ordinary planning recall.");
        faded["spatialGoal"]["status"] = "suspended"; faded["spatialGoal"]["boundEntityId"] = memory["entityId"].DeepClone();
        var pinned = (JObject)Call(null, "ProjectRoomWorkingMemory", faded);
        Require(ReadWorkingMemoryIndex(pinned).Count == 1 && ((JArray)pinned["visibleObjectCatalog"]["memory"]).Count == 1,
            "An unfinished target remains recoverable even when its ordinary recall weight decays.");
        string activeCapture = (string)memory["sourceCaptureId"];
        Require(((JArray)pinned["planningHistoryImageCaptureIds"]).Count == 1 &&
            (bool)Call(null, "RoomWorkingMemoryAllowsImage", pinned, activeCapture) &&
            !(bool)Call(null, "RoomWorkingMemoryAllowsImage", pinned, "unrelated-old-frame") &&
            !(bool)Call(null, "RoomWorkingMemoryAllowsImage", working, activeCapture),
            "Only the active target's historical image is selected; a later ordinary request attaches none.");

        var unknown = (JObject)source.DeepClone(); unknown["spatialGoal"]["searchProgress"]["futureConstraint"] = "ask-before-resuming";
        unknown["visibleObjectCatalog"]["memory"][0]["futureConstraint"] = new JObject { ["allow"] = false };
        var guarded = (JObject)Call(null, "ProjectRoomWorkingMemory", unknown);
        Require((string)guarded["workingMemory"]["completedTasks"]["spatialGoal"]["searchProgress"]["futureConstraint"] == "ask-before-resuming" &&
            ((JArray)guarded["visibleObjectCatalog"]["memory"]).OfType<JObject>().Any(row => row["futureConstraint"] != null),
            "Unknown task/observation constraints cannot be silently archived or evicted.");
        var nested = (JObject)source.DeepClone();
        nested["sceneSpatialMemory"]["viewSamples"][0]["cameraPosition"]["futureConstraint"] = false;
        nested["spatialGoal"]["referenceContext"]["quotedDialogue"] = new JObject { ["futureConstraint"] = false };
        nested["spatialGoal"]["searchProgress"]["completedSteps"][0]["actualRoot"] = new JObject { ["x"] = 0, ["futureConstraint"] = false };
        var nestedGuarded = (JObject)Call(null, "ProjectRoomWorkingMemory", nested);
        Require(JToken.DeepEquals(nested["sceneSpatialMemory"]["viewSamples"], nestedGuarded["sceneSpatialMemory"]["viewSamples"]) &&
            nestedGuarded["workingMemory"]["completedTasks"]["spatialGoal"]["referenceContext"] != null &&
            nestedGuarded["workingMemory"]["completedTasks"]["spatialGoal"]["searchProgress"] != null,
            "Unknown nested view/terminal-history constraints protect their containing records.");

        // A current picture is still required; memory indexes never manufacture a
        // frame, action or permission. The current words are preserved in the wire.
        var chain = new JArray();
        foreach (string utterance in new[] { "来我身边。", "向右转身九十度。", "再找找刚才电视柜上的那盏灯。", "把它打开。" })
        {
            var turn = (JObject)source.DeepClone(); turn["currentUserText"] = turn["latestUserText"] = utterance;
            var result = Budget(Wire(provider, turn.ToString(Formatting.None)), false);
            var current = Input((string)result["wire"]); Call(null, "RestoreRoomDecisionSharedEvidence", current);
            Require((bool)result["ok"] && (string)current["currentUserText"] == utterance &&
                (string)current["spatialGoal"]["status"] == "satisfied", "New turn fits and cannot resurrect a completed task: " + utterance);
            chain.Add(new JObject { ["utterance"] = utterance, ["totalEstimatedUnits"] = result["diagnostic"]["final"]["estimatedUnits"].DeepClone() });
        }
        LastReport["workingMemory"] = new JObject { ["status"] = "passed", ["cases"] = reports, ["chain"] = chain,
            ["provenance"] = "Actual latest completed-goal log + earlier complete producer observation fixture. Composite controlled cases, not an exact full latest-turn replay or live movement test." };
    }
}
