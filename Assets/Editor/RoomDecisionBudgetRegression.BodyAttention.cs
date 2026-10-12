using System.Linq;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RoomDecisionBudgetRegression
{
    private static void CheckInitialBodyAttention(ChatQW provider)
    {
        var source = WorkingMemorySource();
        source["currentUserText"] = "跟着我走，保持两米距离。";
        source["latestUserText"] = source["currentUserText"].DeepClone();
        source["autonomous"] = false;
        source["currentRequestSemantics"] = new JObject {
            ["source"] = "independent-first-reply-current-request-review", ["requestKind"] = "action",
            ["actionAuthorization"] = false, ["planningFocus"] = new JObject {
                ["kind"] = "current-user-body", ["currentUserText"] = source["currentUserText"].DeepClone() }
        };
        string untouched = source.ToString(Formatting.None);
        string focusedWire = Wire(provider, untouched);
        var focused = Input(focusedWire);
        Require(focused["stepAttention"] != null &&
            !((JArray)focused.SelectToken("visibleObjectCatalog.current.candidates")).Any(),
            "Current-turn pure user movement removes optional object rows before the initial HTTP.");
        Require(untouched == source.ToString(Formatting.None) &&
            (string)focused["currentUserText"] == (string)source["currentUserText"],
            "Attention preserves the complete distance constraint and runtime records.");
        foreach (string field in new[] { "currentFacts", "currentSupport", "decisionContractFacts" })
            Require(JToken.DeepEquals(source[field], focused[field]), "Body attention preserves current execution/route/support: " + field);
        var general = (JObject)source.DeepClone(); general["currentRequestSemantics"]["planningFocus"]["kind"] = "general";
        string generalWire = Wire(provider, general.ToString(Formatting.None));
        Require(JToken.DeepEquals(JObject.Parse(generalWire)["response_format"], JObject.Parse(focusedWire)["response_format"]),
            "Semantic attention cannot narrow the initial native action menu or confer permission.");
        foreach (string guard in new[] { "mixed", "general", "stale", "autonomous", "authorization" })
        {
            var protectedInput = (JObject)source.DeepClone();
            if (guard == "mixed") protectedInput["currentRequestSemantics"]["requestKind"] = "mixed";
            if (guard == "general") protectedInput["currentRequestSemantics"]["planningFocus"]["kind"] = "general";
            if (guard == "stale") protectedInput["currentUserText"] = "先找杯子，再来我身边。";
            if (guard == "autonomous") protectedInput["autonomous"] = true;
            if (guard == "authorization") protectedInput["currentRequestSemantics"]["actionAuthorization"] = true;
            Require(!RoomTaskContextPolicy.InitialUserBodyAttention(protectedInput) &&
                Input(Wire(provider, protectedInput.ToString(Formatting.None)))["stepAttention"] == null,
                "Invalid/inapplicable attention cannot omit object evidence: " + guard);
        }
        var guarded = (JObject)source.DeepClone();
        var candidate = (JObject)guarded.SelectToken("visibleObjectCatalog.current.candidates[0]");
        candidate["futureConstraint"] = new JObject { ["allow"] = false, ["reason"] = "keep exact" };
        var guardedProjection = Input(Wire(provider, guarded.ToString(Formatting.None)));
        Require(((JArray)guardedProjection.SelectToken("visibleObjectCatalog.current.candidates")).OfType<JObject>()
            .Any(row => JToken.DeepEquals(row["futureConstraint"], candidate["futureConstraint"])),
            "Unknown restrictions survive body attention even for ordinary observed objects.");
        var budget = Budget(focusedWire, false);
        Require((bool)budget["ok"], "The complete initial body-planning request fits unchanged limits.");
        LastReport["initialBodyAttention"] = new JObject { ["status"] = "passed",
            ["general"] = Call(null, "BuildRoomTaskRequestDiagnostic", generalWire) as JObject,
            ["focused"] = budget["diagnostic"], ["scope"] = "Real serializer; controlled semantic attention, unchanged menu and protected constraints." };
        CheckLatestAccumulatedAttention(provider);
        CheckLatestAccumulatedAttention(provider, "TaskBudget.latest-2123-20261012.json", "latest2123Attention");
    }

    private static void CheckLatestAccumulatedAttention(ChatQW provider, string fixtureName = "TaskBudget.latest-1945-20261012.json", string reportName = "latestAccumulatedAttention")
    {
        var fixture = JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/" + fixtureName));
        var reports = new JArray();
        foreach (JObject row in fixture["cases"])
        {
            var input = (JObject)row["planningInput"].DeepClone();
            string original = input.ToString(Formatting.None);
            string raw = (string)Call(provider, "BuildRoomTaskRequestJsonWithImage", original, "data:image/png;base64,AA==", false);
            var budget = Budget(raw, false);
            reports.Add(new JObject { ["name"] = row["name"], ["recordedOriginalBudget"] = row["recordedBudget"],
                ["reconstructedBudget"] = budget["diagnostic"] });
            LastReport[reportName] = new JObject { ["scope"] = fixture["scope"], ["turns"] = reports };
            Require((bool)budget["ok"], "Latest complete five-turn cross-task stress remains admitted: " + row["name"]);
            var sent = Input((string)budget["wire"]); Call(null, "RestoreRoomDecisionSharedEvidence", sent);
            Require((string)sent["currentUserText"] == (string)input["currentUserText"] && original == input.ToString(Formatting.None),
                "Latest dialogue stays intact and attention never changes the retained source.");
            Require((sent["stepAttention"] != null) == ((string)input.SelectToken("currentRequestSemantics.planningFocus.kind") == "current-user-body"),
                "Only the pure approach/follow turns use body attention in the accumulated load.");
            if ((string)row["name"] == "latest-2123-gen8")
            {
                Require(((JArray)sent.SelectToken("visibleObjectCatalog.historicalViews")).Count == 0,
                    "New follow request does not preload historical object viewpoint directions.");
                var canonical = (JObject)Call(null, "ReadRoomTaskRecoveryJson", original);
                Require(JToken.DeepEquals(sent["currentFacts"], canonical["currentFacts"]), "Current body facts survive historical-detail projection.");
                Require(JToken.DeepEquals(sent["execution"], canonical["execution"]), "Current execution survives historical-detail projection.");
                Require(JToken.DeepEquals(sent.SelectToken("workingMemory.completedTasks.spatialGoal.completionEvidence"), canonical.SelectToken("spatialGoal.completionEvidence")),
                    "Exact completed device proof survives historical-detail projection.");
                var unknown = (JObject)input.DeepClone();
                unknown["visibleObjectCatalog"]["historicalViews"][0]["returnDirection"]["futureConstraint"] = "preserve exactly";
                unknown["sceneActionFacts"]["lastAction"]["viewBefore"] = new JObject { ["captureId"] = "old-view", ["futureConstraint"] = "preserve exactly" };
                string protectedWire = Wire(provider, unknown.ToString(Formatting.None));
                var protectedInput = Input(protectedWire);
                Require((string)protectedInput.SelectToken("visibleObjectCatalog.historicalViews[0].returnDirection.futureConstraint") == "preserve exactly" &&
                    (string)protectedInput.SelectToken("sceneActionFacts.lastAction.viewBefore.futureConstraint") == "preserve exactly",
                    "Unknown nested history/geometry constraints remain intact.");
                var continuation = (JObject)input.DeepClone(); continuation["taskSequence"] = new JObject { ["steps"] = new JArray() };
                var continuationInput = Input(Wire(provider, continuation.ToString(Formatting.None)));
                Require(((JArray)continuationInput.SelectToken("visibleObjectCatalog.historicalViews")).Count > 0 &&
                    JToken.DeepEquals(continuationInput.SelectToken("sceneActionFacts.lastAction"), canonical.SelectToken("sceneActionFacts.lastAction")),
                    "A compound continuation cannot discard predecessor action detail through initial attention.");
            }
        }
        var results = new JArray();
        foreach (JObject row in fixture["terminals"])
        {
            var facts = (JObject)row["facts"];
            string original = (string)facts.SelectToken("goal.originalRequest");
            string wire = (string)Call(provider, "BuildSpatialGoalResultRequestJsonForMessages", provider.m_DataList, false,
                SpatialGoalResultContextPolicy.BuildContext(original, facts.ToString(Formatting.None)), null, null);
            Require(wire != null, "Exact logged latest terminal facts still fit the unchanged result budget.");
            var projected = SpatialGoalResultContextPolicy.ProjectResultFacts(facts);
            Require(SpatialGoalResultContextPolicy.RequiredReport(projected) == SpatialGoalResultContextPolicy.RequiredReport(facts) &&
                JToken.DeepEquals(projected.SelectToken("goal.completionEvidence"), facts.SelectToken("goal.completionEvidence")),
                "Reusing the device result packet keeps exact logged proof and reporting obligation.");
            results.Add(new JObject { ["line"] = row["sourceLine"], ["requiredReport"] = SpatialGoalResultContextPolicy.RequiredReport(projected),
                ["wireCharacters"] = wire.Length });
        }
        LastReport["latestResultFacts"] = results;
    }
}
