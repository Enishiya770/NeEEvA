using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RoomDecisionBudgetRegression
{
    private const string CognitionMarker = "\n[物件与自身方位的观察记录；回答当前对话可直接参考，无需先发起寻物]\n";

    private static JObject RealProducerInput() => (JObject)Call(null, "ReadRoomTaskRecoveryJson",
        File.ReadAllText("Assets/Editor/Fixtures/RoomDecisionBudget.real-producer.json"));

    private static void CheckRealProducerProjection(ChatQW provider)
    {
        var source = RealProducerInput();
        string original = source.ToString(Formatting.None);
        var cognition = (JObject)Call(null, "ReadRoomTaskRecoveryJson", ((string)source["visualEvidence"]).Split(new[] { CognitionMarker }, StringSplitOptions.None)[1]);
        var canonical = (JArray)source.SelectToken("visibleObjectCatalog.current.candidates");
        var first = ((JArray)cognition["currentObjects"]).OfType<JObject>().First(row => canonical.OfType<JObject>().Any(candidate =>
            (string)row["entityId"] == (string)candidate["entityId"]));
        var paired = canonical.OfType<JObject>().First(row => (string)row["entityId"] == (string)first["entityId"]);
        Require(!JToken.DeepEquals(first["imagePoint"], paired["imagePoint"]) &&
            !JToken.DeepEquals(first["pointAtCapture"], paired["pointAtCapture"]),
            "Saved complete real producer contains actual JsonUtility/JObject numeric spelling differences; fixture does not manufacture identical floats.");
        Require((bool)Call(null, "RoomDecisionRowRepresented", first, canonical, true),
            "Same capture/entity real coordinate serializations resolve to identical float32 geometry.");
        var projected = (JObject)Call(null, "ProjectRoomDecisionInput", source, 0);
        Require(((JArray)projected["sceneCognitionSummary"]["currentObjects"]).Count == 1,
            "Real producer's six duplicated current rows disappear and its seventh extra-page row remains.");
        Require(((JArray)projected["sceneCognitionSummary"]["historicalObjects"]).Count == 0,
            "Equivalent historical cognition follows catalog selection, not the already emptied catalog.");
        var retainedHistory = ((JArray)projected["visibleObjectCatalog"]["memory"]).OfType<JObject>().ToArray();
        foreach (var row in retainedHistory)
        {
            var prior = ((JArray)cognition["historicalObjects"]).OfType<JObject>().First(value =>
                (string)value["entityId"] == (string)row["entityId"] && (string)value["sourceCaptureId"] == (string)row["sourceCaptureId"]);
            var originalRow = ((JArray)source["visibleObjectCatalog"]["memory"]).OfType<JObject>().First(value =>
                (string)value["entityId"] == (string)row["entityId"] && (string)value["sourceCaptureId"] == (string)row["sourceCaptureId"]);
            Require(JToken.DeepEquals(row["ageSecondsAtCognitionRead"], prior["ageSeconds"]) &&
                JToken.DeepEquals(row["ageSeconds"], originalRow["ageSeconds"]), "Distinct age readings retain both exact values after equivalent history is joined.");
        }
        Require(original == source.ToString(Formatting.None), "Real source/engine memory remains byte-identical after request projection.");

        var differing = (JObject)first.DeepClone();
        differing["imagePoint"]["x"] = (double)differing["imagePoint"]["x"] + .0001;
        Require(!(bool)Call(null, "RoomDecisionRowRepresented", differing, canonical, true), "Different float32 pixel samples are not merged by a geometric distance tolerance.");
        differing = (JObject)first.DeepClone(); differing["pointAtCapture"]["futureNumericConstraint"] = .00000001;
        Require(!(bool)Call(null, "RoomDecisionRowRepresented", differing, canonical, true), "Unknown numeric coordinate-child constraints stay protected.");
        differing = (JObject)first.DeepClone(); differing["sourceCaptureId"] = "different-capture";
        Require(!(bool)Call(null, "RoomDecisionRowRepresented", differing, canonical, true), "Identical geometry in a different capture is not duplicate evidence.");
        differing = (JObject)first.DeepClone(); differing.Remove("entityId"); differing.Remove("sourceCaptureId");
        Require(!(bool)Call(null, "RoomDecisionRowRepresented", differing, new JArray(differing.DeepClone()), true), "Unscoped geometry cannot prove identity equivalence.");
        differing = (JObject)first.DeepClone(); differing["actionAuthorization"] = true;
        Require(!(bool)Call(null, "RoomDecisionRowRepresented", differing, canonical, true), "Numeric normalization cannot erase an authorization conflict.");
        differing = (JObject)first.DeepClone(); differing["bearing"]["bodyYawDegreesRight"] = (double)differing["bearing"]["bodyYawDegreesRight"] + .00000001;
        Require(!(bool)Call(null, "RoomDecisionRowRepresented", differing, canonical, true), "Body yaw remains exact; this change only reconciles capture coordinate serialization.");

        // Historical-only special constraints cannot be discarded simply because
        // the corresponding canonical row became an optional omission.
        var guardedSource = (JObject)source.DeepClone();
        var guardedCognition = (JObject)cognition.DeepClone();
        guardedCognition["historicalObjects"][0]["actionAuthorization"] = true;
        guardedCognition["currentObjects"][0]["futureConstraint"] = new JObject { ["allow"] = false };
        guardedSource["visualEvidence"] = ((string)source["visualEvidence"]).Split(new[] { CognitionMarker }, StringSplitOptions.None)[0] + CognitionMarker + guardedCognition.ToString(Formatting.None);
        var guardedProjection = (JObject)Call(null, "ProjectRoomDecisionInput", guardedSource, 0);
        Require(((JArray)guardedProjection["sceneCognitionSummary"]["historicalObjects"]).Count == 1 &&
            ((JArray)guardedProjection["sceneCognitionSummary"]["currentObjects"]).Count == 2,
            "Conflicting historical authority and unknown current constraints survive unified selection.");

        var progress = new JArray(); var views = new JArray(); var failures = new JArray();
        for (int i = 0; i < 6; ++i)
        {
            progress.Add(new JObject { ["actionId"] = "closed-action-" + i, ["intent"] = "look-direction", ["phase"] = "failed",
                ["reason"] = "Candidate not independently confirmed; do not treat this step as operation permission.", ["captureId"] = "previous-capture-" + i,
                ["entityId"] = "candidate-" + i, ["targetPurpose"] = "device-operation", ["observedUtc"] = "2026-10-09T14:00:00Z",
                ["actualRoot"] = new JObject { ["x"] = .123, ["y"] = 0, ["z"] = -2.456 }, ["actualViewChangeDegrees"] = 15.0, ["mainGoalSatisfied"] = false });
            failures.Add(new JObject { ["actionId"] = "closed-action-" + i, ["entityId"] = "candidate-" + i, ["captureId"] = "previous-capture-" + i,
                ["verdict"] = "uncertain", ["reason"] = "No current semantic match", ["observedUtc"] = "2026-10-09T14:00:00Z",
                ["scope"] = "Failure of this candidate/step in this frame; not a global exclusion or missing/broken device proof." });
        }
        foreach (string goalName in new[] { "spatialGoal", "standingGoal" })
        {
            var withGoal = (JObject)source.DeepClone();
            withGoal[goalName] = new JObject { ["status"] = "waiting_clarification", ["originalRequest"] = "请把电视柜上的灯关掉。", ["requestQuote"] = "请把电视柜上的灯关掉。",
                ["desiredState"] = "off", ["ownerCurrent"] = true, ["boundEntityId"] = "authorized-object", ["unknownPermission"] = new JObject { ["mustConfirm"] = true },
                ["searchProgress"] = new JObject { ["completedSteps"] = progress.DeepClone(), ["observedViews"] = views.DeepClone(), ["stepCount"] = 6 },
                ["failedCandidates"] = failures.DeepClone(), ["userUpdates"] = new JArray(new JObject { ["text"] = "只看看，不操作。", ["actionAuthorization"] = false }) };
            var compact = (JObject)Call(null, "ProjectRoomDecisionInput", withGoal, 0);
            var actual = (JObject)compact[goalName]; var expected = (JObject)withGoal[goalName];
            Require(JToken.DeepEquals(progress, ExpandProgressTable(actual["searchProgress"]["completedSteps"])) &&
                JToken.DeepEquals(failures, ExpandProgressTable(actual["failedCandidates"])), "Unfinished " + goalName + " trace encoding reconstructs every failure/pose/reason exactly.");
            foreach (var property in expected.Properties().Where(p => p.Name != "searchProgress" && p.Name != "failedCandidates"))
                Require(JToken.DeepEquals(property.Value, actual[property.Name]), "Goal authority/user/unknown fields remain exact: " + goalName + "." + property.Name);
            ((JArray)expected["searchProgress"]["completedSteps"])[0]["futurePolicy"] = new JObject { ["canProceed"] = false };
            compact = (JObject)Call(null, "ProjectRoomDecisionInput", withGoal, 0);
            Require(JToken.DeepEquals(expected["searchProgress"]["completedSteps"], compact[goalName]["searchProgress"]["completedSteps"]), "Unknown progress row shape stays literal and cannot be silently summarized.");
        }
        LastReport["realProducer"] = new JObject { ["fixture"] = "Assets/Editor/Fixtures/RoomDecisionBudget.real-producer.json",
            ["provenance"] = "Complete earlier Unity producer from ordinary-followup; not latest Play replay. Captured coordinate serialization is preserved verbatim.",
            ["sourceCurrent"] = canonical.Count, ["sourceCognitionCurrent"] = ((JArray)cognition["currentObjects"]).Count,
            ["projectedCognitionCurrent"] = ((JArray)projected["sceneCognitionSummary"]["currentObjects"]).Count,
            ["projectedCognitionHistory"] = ((JArray)projected["sceneCognitionSummary"]["historicalObjects"]).Count };
        CheckPostSearchChain(provider);
    }

    private static void CheckPostSearchChain(ChatQW provider)
    {
        var source = (JObject)Call(null, "ReadRoomTaskRecoveryJson", File.ReadAllText("Assets/Editor/Fixtures/RoomDecisionBudget.post-search-chain.json"));
        var reports = new JArray();
        // Exact production serializer accepts its own DTO; the offline pure
        // compiler's transport stub has public fields instead of the real ctor.
        Type imageType = null;
        for (Type type = typeof(ChatQW); type != null && imageType == null; type = type.BaseType)
            imageType = type.GetNestedType("RoomObservationImage", Flags);
        if (imageType == null) throw new InvalidOperationException("Missing production observation-image DTO.");
        var historical = (JObject)source["sceneSpatialMemory"]["dialogueViews"][0]["visualObservation"];
        string historyId = (string)historical["captureId"];
        var constructor = imageType.GetConstructor(new[] { typeof(string), typeof(string), typeof(string) });
        object image = constructor != null ? constructor.Invoke(new object[] { historyId, historical.ToString(Formatting.None), "data:image/png;base64,HISTORICAL_BUDGET_ONLY" }) : Activator.CreateInstance(imageType, true);
        if (constructor == null)
        {
            imageType.GetField("CaptureId").SetValue(image, historyId);
            imageType.GetField("MetadataJson").SetValue(image, historical.ToString(Formatting.None));
            imageType.GetField("ImageDataUrl").SetValue(image, "data:image/png;base64,HISTORICAL_BUDGET_ONLY");
        }
        var images = Array.CreateInstance(imageType, 1); images.SetValue(image, 0);
        int turnIndex = 0;
        foreach (string utterance in new[] { "刚才为什么没有关灯？", "向右转身90度。", "我说的是让你把身体朝右转九十度，先做这个转身动作。之前那盏灯暂时不要再操作，也不用重新寻找；这次我只要求你转身。" })
        {
            var input = (JObject)source.DeepClone(); input["currentUserText"] = utterance; input["latestUserText"] = utterance;
            input["currentRequestSemantics"]["requestKind"] = turnIndex == 0 ? "answer-only" : "action";
            input["currentRequestSemantics"]["requiresActionOrControl"] = turnIndex != 0;
            input["currentRequestSemantics"]["effectiveRoute"] = turnIndex == 0 ? "evidence" : "tools";
            input["answerRequired"] = turnIndex++ == 0;
            // Full native grammar is the most expensive observed shape. This is
            // a serializer/admission boundary, not an assertion that pure QA
            // should reach the planner through current routing.
            input["currentRequestOnly"] = false;
            string wire = (string)Call(provider, "BuildRoomTaskRequestJsonWithObservationHistory", input.ToString(Formatting.None), "data:image/png;base64,CURRENT_BUDGET_ONLY", images, false);
            var result = Budget(wire, false); reports.Add(result["diagnostic"].DeepClone());
            Require((bool)result["ok"] && (int)result["diagnostic"]["final"]["schemaUtf8Bytes"] == 19437 &&
                (long)result["diagnostic"]["original"]["estimatedUnits"] > 61440 &&
                (long)result["diagnostic"]["final"]["estimatedUnits"] <= 61440,
                "Post-search full native grammar + six current/six historical objects + two image reserves fit unchanged ceilings after projection.");
            var after = Input((string)result["wire"]); Call(null, "RestoreRoomDecisionSharedEvidence", after);
            Require((string)after["currentUserText"] == utterance && (string)after["latestUserText"] == utterance &&
                JToken.DeepEquals(after["currentRequestSemantics"], input["currentRequestSemantics"]) &&
                ((JArray)after["visibleObjectCatalog"]["current"]["candidates"]).Count == 6 &&
                ((JArray)after["visibleObjectCatalog"]["memory"]).Count == 6,
                "Protected words, compact request receipt and all twelve real-capture object records remain in the admitted chain request.");
            Require(JToken.DeepEquals(JObject.Parse(wire)["response_format"], JObject.Parse((string)result["wire"])["response_format"]) &&
                JObject.Parse((string)result["wire"]).SelectTokens("$..image_url.url").Count() == 2,
                "Both image attachments and full native schema survive chain projection unchanged.");
            Require(JToken.DeepEquals(input["standingGoal"]["searchProgress"]["observedViews"],
                ExpandProgressTable(after["standingGoal"]["searchProgress"]["observedViews"])) &&
                JToken.DeepEquals(input["standingGoal"]["originalRequest"], after["standingGoal"]["originalRequest"]) &&
                JToken.DeepEquals(input["pendingDeviceRequest"], after["pendingDeviceRequest"]),
                "Pending original authority and all historical view poses/observations reconstruct exactly from goal projection.");
        }
        LastReport["postSearchChain"] = new JObject { ["cases"] = reports,
            ["provenance"] = "Complete earlier same-run real producer/current and historical object geometry; controlled paused-goal/view receipts and dialogue. Pure image reserves use labelled placeholders; separate model calibration used both paired real JPEGs. Not latest Play exact replay." };
    }

    private static JToken ExpandProgressTable(JToken token)
    {
        if (!(token is JObject table) || table["columns"] == null || table["rows"] == null) return token;
        var columns = (JArray)table["columns"];
        return new JArray(((JArray)table["rows"]).OfType<JArray>().Select(row =>
        {
            var value = new JObject();
            for (int i = 0; i < columns.Count; ++i) value[(string)columns[i]] = row[i].DeepClone();
            return value;
        }));
    }
}
