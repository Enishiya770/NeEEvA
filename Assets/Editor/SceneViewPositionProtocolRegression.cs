using System;
using System.Linq;
using Newtonsoft.Json.Linq;

/// <summary>Closed view-position intent and request-local observation-goal grammar. No engine, service or movement.</summary>
public static class SceneViewPositionProtocolRegression
{
    private const string UserText = "为了观察这个目标，请调整一下站位。";
    private static int checks;
    public static JObject LastReport { get; private set; }

    public static int RunChecks()
    {
        checks = 0;
        LastReport = new JObject {
            ["status"] = "running",
            ["scope"] = "Production parser and independent structural schema checks. Current image, entity identity, target direction, collision, actual movement and readability are not proved by this protocol test."
        };
        try
        {
            ShapeAndAcceptedCases(); InvalidCases(); SourceAndLegacyCases(); GoalGrammarCases();
            LastReport["status"] = "passed";
            return checks;
        }
        catch (Exception error)
        { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
        finally { LastReport["checks"] = checks; }
    }

    private static JObject Pixel() => new JObject {
        ["captureId"] = "current-view-37", ["x"] = .45, ["y"] = .55, ["label"] = "当前可见目标"
    };
    private static JObject Entity() => new JObject {
        ["captureId"] = "current-view-37", ["entityId"] = "scene-object-known-37"
    };
    private static JObject Position(string direction = "farther", double distance = .6, string purpose = "see-whole") =>
        new JObject { ["direction"] = direction, ["distance"] = distance, ["purpose"] = purpose };
    private static JObject Decision(bool entity = false, string direction = "farther", double distance = .6, string purpose = "see-whole") =>
        new JObject {
            ["intent"] = "reposition-view", ["origin"] = "user", ["evidence"] = UserText,
            [entity ? "entityTarget" : "visualTarget"] = entity ? Entity() : Pixel(),
            ["viewPosition"] = Position(direction, distance, purpose)
        };
    private static bool Parse(JObject value, out RoomTaskProtocol.Decision result, out string reason,
        string userText = UserText, bool autonomous = false) =>
        RoomTaskProtocol.TryDecision(value.ToString(Newtonsoft.Json.Formatting.None), userText, autonomous, out result, out reason);

    private static void ExpectShape(JObject value, bool expected, string label)
    {
        Require(SceneObjectProtocolRegression.MatchesSchemaShape(JObject.Parse(RoomTaskProtocol.DecisionSchema), value) == expected,
            "Advertised grammar: " + label);
        Require(Parse(value, out var result, out _) == expected && (expected ? result != null : result == null),
            "Runtime parser: " + label);
    }

    private static void ShapeAndAcceptedCases()
    {
        var schema = JObject.Parse(RoomTaskProtocol.DecisionSchema);
        var definition = (JObject)schema["properties"]["viewPosition"];
        Require((bool)definition["additionalProperties"] == false && ((JObject)definition["properties"]).Count == 3 &&
            definition["required"].Values<string>().OrderBy(x => x).SequenceEqual(new[] { "direction", "distance", "purpose" }),
            "View position has exactly three required fields, no world coordinates or capability grants.");
        var branches = ((JArray)schema["oneOf"]).OfType<JObject>().Where(x =>
            x["properties"]["intent"]["enum"].Values<string>().Contains("reposition-view")).ToArray();
        Require(branches.Length == 2 && branches.All(x => x["required"].Values<string>().Contains("viewPosition") &&
            x["properties"]["observationGoalId"] == null && x["properties"]["targetSelection"] != null),
            "Both target representations require the same position shape and forbid observation-goal inheritance.");
        foreach (bool entity in new[] { false, true })
        foreach (string direction in new[] { "farther", "closer", "left", "right" })
        foreach (string purpose in new[] { "see-whole", "read-detail", "avoid-occlusion" })
        foreach (double distance in new[] { .2, .65, 1.2 })
        {
            var value = Decision(entity, direction, distance, purpose);
            ExpectShape(value, true, direction + "/" + purpose + "/" + distance);
            Parse(value, out var parsed, out _);
            Require(parsed.IsSpatial && parsed.IsVisual && parsed.IsViewPosition && !parsed.IsViewChange &&
                !parsed.IsObjectState && !parsed.IsObservationApproach && !parsed.ContinuesObservationGoal &&
                parsed.IsEntityReference == entity && parsed.viewChange == null && parsed.objectState == null &&
                parsed.viewPosition.direction == direction && parsed.viewPosition.purpose == purpose &&
                Math.Abs(parsed.viewPosition.distance - distance) < .000001,
                "Parsing preserves the proposed direction, distance and purpose without converting them into execution facts.");
        }
        var correction = Decision(true); correction["targetSelection"] = "reselect";
        ExpectShape(correction, true, "Explicit referent correction remains available.");
        Parse(correction, out var reselected, out _);
        Require(reselected.RequestsTargetReselection, "Reselection is retained, not treated as a new capability.");
    }

    private static void InvalidCases()
    {
        foreach (bool entity in new[] { false, true })
        {
            string key = entity ? "entityTarget" : "visualTarget";
            var missing = Decision(entity); missing.Remove(key); ExpectShape(missing, false, "A confirmed target proposal is required.");
            missing = Decision(entity); missing[key] = JValue.CreateNull(); ExpectShape(missing, false, "A null target is not a reference.");
            missing = Decision(entity); missing.Remove("viewPosition"); ExpectShape(missing, false, "Position intent cannot omit position fields.");
            missing = Decision(entity); missing["viewPosition"] = JValue.CreateNull(); ExpectShape(missing, false, "Null position is forbidden.");
            foreach (var extra in new JObject {
                [entity ? "visualTarget" : "entityTarget"] = entity ? Pixel() : Entity(),
                ["viewChange"] = new JObject { ["captureId"] = "current-view-37", ["yaw"] = 10, ["pitch"] = 0 },
                ["targetPurpose"] = "observe-closer", ["objectState"] = "on", ["observationGoalId"] = "observation-goal-old",
                ["worldPosition"] = new JArray(1, 2, 3), ["capability"] = "model-granted"
            }.Properties())
            {
                var value = Decision(entity); value[extra.Name] = extra.Value.DeepClone();
                ExpectShape(value, false, "No additional target, separate turn, goal inheritance or mutation: " + extra.Name);
                value[extra.Name] = JValue.CreateNull(); ExpectShape(value, false, "Forbidden fields remain forbidden when null.");
            }
        }
        foreach (string name in new[] { "direction", "distance", "purpose" })
        {
            var value = Decision(); ((JObject)value["viewPosition"]).Remove(name);
            ExpectShape(value, false, "Missing viewPosition member: " + name);
            value = Decision(); value["viewPosition"][name] = JValue.CreateNull(); ExpectShape(value, false, "Null member: " + name);
        }
        foreach (JToken bad in new JToken[] { -.1, 0, .199999, 1.200001, 2, "0.6", true, new JObject(), new JArray() })
        {
            var value = Decision(); value["viewPosition"]["distance"] = bad;
            ExpectShape(value, false, "Distance is finite numeric metres in the closed supported interval.");
        }
        foreach (double bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var value = Decision(); value["viewPosition"]["distance"] = bad;
            Require(!Parse(value, out var result, out _) && result == null, "Nonfinite values cannot gain movement authority.");
        }
        foreach (string bad in new[] { "backward", "forward", "Farther", " left", "right ", "world-x", "" })
        { var value = Decision(); value["viewPosition"]["direction"] = bad; ExpectShape(value, false, "No direction alias/coercion."); }
        foreach (string bad in new[] { "see-clear", "read", "See-whole", "avoid-occlusion ", "", "grab" })
        { var value = Decision(); value["viewPosition"]["purpose"] = bad; ExpectShape(value, false, "Closed observation purpose, no operation request."); }
        foreach (JToken bad in new JToken[] { true, 1, new JArray(), new JObject() })
        foreach (string name in new[] { "direction", "purpose" })
        { var value = Decision(); value["viewPosition"][name] = bad.DeepClone(); ExpectShape(value, false, "Enums require actual strings."); }
        var extraField = Decision(); extraField["viewPosition"]["yaw"] = 180;
        ExpectShape(extraField, false, "Nested position has no free head/whole-body rotation.");
        var staleGoal = Decision(); staleGoal["observationGoalId"] = "observation-goal-even-if-known";
        Require(!Parse(staleGoal, out _, out string failure) && failure == "invalid-observation-goal",
            "Old goal references are explicitly rejected, never removed and executed.");
        string duplicate = Decision().ToString(Newtonsoft.Json.Formatting.None).Replace("\"direction\":\"farther\"", "\"direction\":\"farther\",\"direction\":\"closer\"");
        Require(!RoomTaskProtocol.TryDecision(duplicate, UserText, false, out _, out _), "Duplicate position members are rejected.");
    }

    private static void SourceAndLegacyCases()
    {
        var value = Decision(true);
        Require(!Parse(value, out _, out string failure, "只说明你在哪里，不要移动。") && failure == "evidence-not-in-current-user-text",
            "A valid movement schema cannot replay an older user request.");
        Require(!Parse(value, out _, out failure, "", true) && failure == "user-origin-in-autonomous-round",
            "No autonomous tick gains an old user's authority.");
        value["origin"] = "autonomous"; value["evidence"] = "我想看清这个目标，考虑向侧面调整站位。";
        Require(Parse(value, out var autonomous, out _, "", true) && autonomous.IsViewPosition,
            "Purposeful autonomous view placement uses the same bounded capability without requiring a user command.");
        Require(!Parse(value, out _, out _, UserText, false), "A non-autonomous turn cannot assert autonomous ownership.");
        foreach (string intent in JObject.Parse(RoomTaskProtocol.DecisionSchema)["properties"]["intent"]["enum"].Values<string>().Where(x => x != "reposition-view"))
        {
            var other = new JObject { ["intent"] = intent, ["origin"] = "user", ["evidence"] = UserText };
            if (new[] { "look-at", "approach-object", "inspect-object", "set-object-state" }.Contains(intent)) other["visualTarget"] = Pixel();
            if (intent == "set-object-state") other["objectState"] = "on";
            if (intent == "orient-view" || intent == "turn-in-place") other["viewChange"] = new JObject { ["captureId"] = "current-view-37", ["yaw"] = 15, ["pitch"] = 0 };
            ExpectShape(other, true, "Existing intent without viewPosition remains valid: " + intent);
            other["viewPosition"] = Position(); ExpectShape(other, false, "Other intent cannot add position side effect: " + intent);
            other["viewPosition"] = JValue.CreateNull(); ExpectShape(other, false, "Null position cannot bypass another intent's closed shape.");
        }
    }

    private static void GoalGrammarCases()
    {
        string original = RoomTaskProtocol.DecisionSchema;
        var empty = JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null));
        var narrowed = JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(new[] { "goal-A", "goal-B", "goal-A", "", " ", null, new string('x',129) }));
        Require(empty["properties"]["observationGoalId"] == null && ((JArray)empty["oneOf"]).All(x => x["properties"]["observationGoalId"] == null),
            "No available observation goal removes the field from every emitted grammar branch.");
        Require(narrowed["properties"]["observationGoalId"]["enum"].Values<string>().SequenceEqual(new[] { "goal-A", "goal-B" }),
            "Only exact distinct valid offered IDs are advertised, without invented or trimmed replacements.");
        foreach (string intent in new[] { "look-at", "approach-object", "inspect-object", "set-object-state" })
        {
            var value = new JObject { ["intent"] = intent, ["origin"] = "user", ["evidence"] = UserText, ["visualTarget"] = Pixel() };
            if (intent == "set-object-state") value["objectState"] = "off";
            Require(SceneObjectProtocolRegression.MatchesSchemaShape(empty, value) && SceneObjectProtocolRegression.MatchesSchemaShape(narrowed, value),
                "Goal continuation remains optional; a direct object reference needs no goal.");
            foreach (string id in new[] { "goal-A", "goal-B", "goal-C", "scene-object-known-37", " goal-A" })
            {
                value["observationGoalId"] = id;
                Require(!SceneObjectProtocolRegression.MatchesSchemaShape(empty, value), "No-goal grammar rejects even an otherwise valid ID.");
                Require(SceneObjectProtocolRegression.MatchesSchemaShape(narrowed, value) == (id == "goal-A" || id == "goal-B"),
                    "Per-request goal enum narrows all existing supported object branches exactly.");
            }
        }
        foreach (var schema in new[] { empty, narrowed })
        {
            var value = Decision(true); Require(SceneObjectProtocolRegression.MatchesSchemaShape(schema, value), "Goal narrowing preserves view reposition.");
            value["observationGoalId"] = "goal-A"; Require(!SceneObjectProtocolRegression.MatchesSchemaShape(schema, value),
                "Even an offered goal cannot be attached to reposition-view.");
        }
        Require(RoomTaskProtocol.DecisionSchema == original && JObject.Parse(original)["properties"]["observationGoalId"]["enum"] == null,
            "Per-request schemas cannot mutate the shared base or leak another payload's goal IDs.");
        var next = JObject.Parse(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(new[] { "goal-next" }));
        Require(next["properties"]["observationGoalId"]["enum"].Values<string>().SequenceEqual(new[] { "goal-next" }),
            "A subsequent payload advertises only its own goal snapshot; execution must still reject later-invalidated ownership.");
    }

    private static void Require(bool condition, string reason)
    {
        ++checks;
        if (!condition) throw new InvalidOperationException("View-position protocol: " + reason);
    }
}
