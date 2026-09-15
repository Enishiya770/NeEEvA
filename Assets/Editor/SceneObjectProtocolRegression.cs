using System;
using System.Linq;
using Newtonsoft.Json.Linq;

/// <summary>Closed object-inspection/light-state protocol. No model HTTP, scene enumeration or device execution.</summary>
public static class SceneObjectProtocolRegression
{
    private const string UserText = "请检查这盏灯，必要时打开或者关闭它，也可以靠近电视柜看清这盏小灯。";
    private static int checks;
    public static JObject LastReport { get; private set; }

    public static int RunChecks()
    {
        checks = 0;
        LastReport = new JObject { ["status"] = "running",
            ["scope"] = "Production JSON parser and advertised schema only. Capture/source freshness, local entity identity, capability, actual light change and result image require separate runtime tests." };
        try
        {
            SchemaCases(); IntentShapeSchemaCases(); AcceptedCases(); RejectionCases(); LegacyIsolationCases(); EntityReferenceCases();
            TargetSelectionCases(); ObservationApproachCases(); ObservationGoalCases(); ObjectMatchCases();
            LastReport["status"] = "passed";
            return checks;
        }
        catch (Exception error)
        { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
        finally { LastReport["checks"] = checks; }
    }

    private static JObject Target() => new JObject { ["captureId"] = "current-capture-17",
        ["x"] = .47, ["y"] = .62, ["label"] = "桌上的亮处" };

    private static JObject EntityTarget() => new JObject { ["captureId"] = "current-capture-17",
        ["entityId"] = "scene-object-known-lamp-17" };

    private static JObject ObjectDecision(string intent, string state = null)
    {
        var value = new JObject { ["intent"] = intent, ["origin"] = "user",
            ["evidence"] = intent == "inspect-object" ? "检查这盏灯" : intent == "approach-object" ? "靠近电视柜" : state == "off" ? "关闭" : "打开",
            ["visualTarget"] = Target() };
        if (state != null) value["objectState"] = state;
        return value;
    }

    private static bool Parse(JObject value, out RoomTaskProtocol.Decision decision,
        bool autonomous = false, string userText = UserText) =>
        RoomTaskProtocol.TryDecision(value.ToString(Newtonsoft.Json.Formatting.None), userText, autonomous, out decision);

    private static void SchemaCases()
    {
        JObject schema = JObject.Parse(RoomTaskProtocol.DecisionSchema);
        Require((bool)schema["additionalProperties"] == false, "Top-level schema stays closed.");
        var intents = (JArray)schema["properties"]["intent"]["enum"];
        Require(intents.Values<string>().Contains("inspect-object") && intents.Values<string>().Contains("set-object-state"),
            "Both new intents are advertised to the actual structured-output provider.");
        Require(schema["properties"]["objectState"]["enum"].Values<string>().SequenceEqual(new[] { "on", "off" }),
            "Only explicit on/off states are advertised; toggle/arbitrary properties are absent.");
        Require((bool)schema["properties"]["visualTarget"]["additionalProperties"] == false &&
            schema["properties"]["visualTarget"]["required"].Values<string>().OrderBy(x => x)
                .SequenceEqual(new[] { "captureId", "label", "x", "y" }),
            "Object actions reuse the closed pixel/capture identity shape, without world coordinates or model-granted capability.");
        var entity = (JObject)schema["properties"]["entityTarget"];
        Require((bool)entity["additionalProperties"] == false &&
            entity["required"].Values<string>().OrderBy(x => x).SequenceEqual(new[] { "captureId", "entityId" }) &&
            ((JObject)entity["properties"]).Count == 2,
            "Known-entity references advertise only captureId and entityId, never names, selectors or positions.");
        Require((string)entity["properties"]["entityId"]["type"] == "string" &&
            (int)entity["properties"]["entityId"]["minLength"] == 1 &&
            (int)entity["properties"]["entityId"]["maxLength"] == 128 &&
            (int)entity["properties"]["captureId"]["maxLength"] == 128,
            "Entity IDs are bounded opaque strings and retain the existing capture-ID bound.");
        Require(schema["properties"]["targetSelection"]["enum"].Values<string>().SequenceEqual(new[] { "reselect" }) &&
            schema["properties"]["targetPurpose"]["enum"].Values<string>().SequenceEqual(new[] { "observe-closer" }),
            "Selection correction and closer-observation purpose advertise only their exact bounded values.");
        Require((string)schema["properties"]["observationGoalId"]["type"] == "string" &&
            (int)schema["properties"]["observationGoalId"]["minLength"] == 1 &&
            (int)schema["properties"]["observationGoalId"]["maxLength"] == 128 &&
            !schema["required"].Values<string>().Contains("observationGoalId"),
            "Observation continuation advertises a bounded optional goal reference, not an implicit inherited goal.");
        JObject matcher = JObject.Parse(RoomTaskProtocol.ObjectMatchSchema);
        Require((bool)matcher["additionalProperties"] == false && ((JObject)matcher["properties"]).Count == 3 &&
            matcher["required"].Values<string>().OrderBy(x => x).SequenceEqual(new[] { "candidateId", "reason", "verdict" }),
            "Object semantic matching has its own closed three-field schema.");
        Require(matcher["properties"]["verdict"]["enum"].Values<string>().SequenceEqual(new[] { "matched", "different-target", "uncertain" }) &&
            matcher["properties"]["candidateId"]["enum"].Values<string>().SequenceEqual(new[] { "none", "c1", "c2", "c3", "c4", "c5", "c6" }) &&
            (int)matcher["properties"]["reason"]["maxLength"] == 160,
            "Matcher outputs use only six short current candidates or none; capture/session UUIDs are absent.");
    }

    private static void IntentShapeSchemaCases()
    {
        int start = checks;
        JObject schema = JObject.Parse(RoomTaskProtocol.DecisionSchema);
        var branches = (JArray)schema["oneOf"];
        Require(branches != null && branches.Count == 12, "Decision grammar advertises twelve explicit intent/target shapes.");
        Require(branches.OfType<JObject>().All(branch => (bool)branch["additionalProperties"] == false &&
            branch["required"].Values<string>().Contains("intent") && branch["required"].Values<string>().Contains("origin") &&
            branch["required"].Values<string>().Contains("evidence")), "Every intent branch independently closes its fields.");
        foreach (string intent in schema["properties"]["intent"]["enum"].Values<string>())
        {
            int expected = new[] { "approach-object", "inspect-object", "set-object-state", "reposition-view" }.Contains(intent) ? 2 : 1;
            Require(branches.Count(branch => branch["properties"]["intent"]["enum"].Values<string>().Contains(intent)) == expected,
                "The grammar has exactly the supported target alternatives for " + intent + ".");
        }
        JObject Basic(string intent) => new JObject { ["intent"] = intent, ["origin"] = "autonomous", ["evidence"] = "本轮明确的观察或接近目的" };
        JObject View() => new JObject { ["captureId"] = "current-capture-17", ["yaw"] = 20, ["pitch"] = 0 };
        void Expect(JObject value, bool expected, string reason)
        {
            string text = value.ToString(Newtonsoft.Json.Formatting.None);
            Require(MatchesSchemaShape(schema, value) == expected, "Advertised schema: " + reason + " :: " + text);
            Require(RoomTaskProtocol.TryDecision(text, "", true, out _) == expected,
                "Production parser: " + reason + " :: " + text);
        }
        foreach (string intent in new[] { "none", "approach", "follow", "stop-moving", "inspect" })
        {
            var value = Basic(intent); Expect(value, true, "Plain intent retains its three-field form.");
            var extras = new JObject { ["visualTarget"] = Target(), ["entityTarget"] = EntityTarget(), ["viewChange"] = View(),
                ["objectState"] = "on", ["targetSelection"] = "reselect", ["targetPurpose"] = "observe-closer", ["observationGoalId"] = "goal-17" };
            foreach (var extra in extras.Properties())
            {
                var wrong = (JObject)value.DeepClone(); wrong[extra.Name] = extra.Value.DeepClone();
                Expect(wrong, false, intent + " cannot carry object or view fields, including approach plus a lamp pixel.");
                wrong[extra.Name] = JValue.CreateNull(); Expect(wrong, false, "Null does not make a forbidden plain-intent field absent.");
            }
        }
        foreach (string intent in new[] { "look-at", "approach-object", "inspect-object", "set-object-state" })
        foreach (string targetKey in intent == "look-at" ? new[] { "visualTarget" } : new[] { "visualTarget", "entityTarget" })
        {
            var value = Basic(intent); value[targetKey] = targetKey == "visualTarget" ? Target() : EntityTarget();
            if (intent == "set-object-state") value["objectState"] = "on";
            Expect(value, true, intent + " accepts its exact " + targetKey + " shape.");
            var changed = (JObject)value.DeepClone(); changed.Remove(targetKey);
            Expect(changed, false, "Object/attention intent cannot omit its target.");
            changed = (JObject)value.DeepClone(); changed[targetKey] = JValue.CreateNull();
            Expect(changed, false, "Object/attention target cannot be null.");
            changed = (JObject)value.DeepClone(); string other = targetKey == "visualTarget" ? "entityTarget" : "visualTarget";
            changed[other] = other == "visualTarget" ? Target() : EntityTarget();
            Expect(changed, false, "Exactly one target representation is allowed.");
            changed[other] = JValue.CreateNull(); Expect(changed, false, "A null second target still violates exclusivity.");
            changed = (JObject)value.DeepClone(); changed["viewChange"] = View();
            Expect(changed, false, "An object target cannot silently add a head or body turn.");
            changed = (JObject)value.DeepClone(); changed["targetSelection"] = "reselect"; changed["observationGoalId"] = "goal-17";
            Expect(changed, true, "Reselection and explicit goal continuation remain optional object fields.");
            changed["targetPurpose"] = "observe-closer";
            Expect(changed, intent == "approach-object", "Only approach-object can declare closer observation as a subgoal.");
            changed = (JObject)value.DeepClone(); changed["capability"] = "model-granted";
            Expect(changed, false, "Closed branch rejects a model-granted capability.");
            changed = (JObject)value.DeepClone(); changed[targetKey]["worldPosition"] = new JArray(0, 1, 2);
            Expect(changed, false, "Closed nested target rejects world coordinates.");
            if (intent == "set-object-state")
            {
                changed = (JObject)value.DeepClone(); changed.Remove("objectState");
                Expect(changed, false, "Device state is required in both target branches.");
                foreach (JToken state in new JToken[] { JValue.CreateNull(), "toggle", true, 1 })
                { changed["objectState"] = state; Expect(changed, false, "No null, toggle or coercion can replace on/off."); }
                changed["objectState"] = "off"; Expect(changed, true, "Explicit off is supported alongside on.");
            }
            else
            {
                changed = (JObject)value.DeepClone(); changed["objectState"] = "on";
                Expect(changed, false, "Read-only or movement intents cannot smuggle a device change.");
            }
        }
        var lookEntity = Basic("look-at"); lookEntity["entityTarget"] = EntityTarget();
        Expect(lookEntity, false, "Look-at still requires an image point, not an entity-only branch.");
        foreach (string intent in new[] { "orient-view", "turn-in-place" })
        {
            var value = Basic(intent); value["viewChange"] = View();
            float limit = intent == "orient-view" ? 50 : 180;
            foreach (float yaw in new[] { -limit, limit })
            { value["viewChange"]["yaw"] = yaw; Expect(value, true, "Current directional yaw boundary remains executable."); }
            foreach (float yaw in new[] { -limit - .01f, limit + .01f })
            { value["viewChange"]["yaw"] = yaw; Expect(value, false, "Directional branch rejects a yaw beyond its own limit."); }
            value["viewChange"]["yaw"] = 20;
            foreach (float pitch in new[] { -25f, 25f })
            { value["viewChange"]["pitch"] = pitch; Expect(value, intent == "orient-view", "Head pitch never becomes whole-body pitch."); }
            value["viewChange"]["pitch"] = 25.01f; Expect(value, false, "No branch exceeds the existing head pitch maximum.");
            value["viewChange"]["pitch"] = 0;
            foreach (string key in new[] { "captureId", "yaw", "pitch" })
            { var changed = (JObject)value.DeepClone(); ((JObject)changed["viewChange"]).Remove(key); Expect(changed, false, "Each viewChange member is required."); }
            foreach (var extra in new JObject { ["visualTarget"] = Target(), ["entityTarget"] = EntityTarget(), ["objectState"] = "on",
                ["targetSelection"] = "reselect", ["targetPurpose"] = "observe-closer", ["observationGoalId"] = "goal-17" }.Properties())
            { var changed = (JObject)value.DeepClone(); changed[extra.Name] = extra.Value.DeepClone(); Expect(changed, false, "Directional branches own no object fields."); }
            value.Remove("viewChange"); Expect(value, false, "A directional intent cannot omit its direction.");
        }
        LastReport["intentShapeSchemaChecks"] = checks - start;
        LastReport["intentShapeSchemaScope"] = "Twelve production schema branches; existing shapes retain their structural examples. Reposition-view and request-local goal narrowing have a separate protocol regression. This bounded schema-keyword evaluator is not a provider grammar test; parser-only source authority, whitespace and nonzero/minimum-turn guards remain stricter than the schema.";
    }

    // Independent structural evaluation of precisely the JSON Schema keywords used
    // by DecisionSchema. Unknown keywords fail the test instead of being ignored.
    internal static bool MatchesSchemaShape(JObject schema, JToken value)
    {
        string[] supported = { "type", "additionalProperties", "required", "properties", "enum", "oneOf", "minimum", "maximum", "minLength", "maxLength" };
        foreach (var property in schema.Properties())
            if (!supported.Contains(property.Name)) throw new InvalidOperationException("Structural schema test needs support for: " + property.Name);
        if (schema["enum"] is JArray choices && !choices.Any(choice => JToken.DeepEquals(choice, value))) return false;
        string type = (string)schema["type"];
        if (type == "object")
        {
            if (!(value is JObject obj)) return false;
            if (schema["required"] is JArray required && required.Values<string>().Any(key => obj.Property(key) == null)) return false;
            var properties = schema["properties"] as JObject;
            foreach (var property in obj.Properties())
            {
                var child = properties?[property.Name] as JObject;
                if (child == null) { if ((bool?)schema["additionalProperties"] == false) return false; }
                else if (!MatchesSchemaShape(child, property.Value)) return false;
            }
        }
        else if (type == "string")
        {
            if (value?.Type != JTokenType.String) return false;
            int length = ((string)value).Length;
            if (schema["minLength"] != null && length < (int)schema["minLength"] ||
                schema["maxLength"] != null && length > (int)schema["maxLength"]) return false;
        }
        else if (type == "number")
        {
            if (value == null || value.Type != JTokenType.Integer && value.Type != JTokenType.Float) return false;
            double number = (double)value;
            if (double.IsNaN(number) || double.IsInfinity(number) ||
                schema["minimum"] != null && number < (double)schema["minimum"] ||
                schema["maximum"] != null && number > (double)schema["maximum"]) return false;
        }
        else if (type != null) throw new InvalidOperationException("Structural schema test needs support for type: " + type);
        return !(schema["oneOf"] is JArray alternatives) || alternatives.OfType<JObject>().Count(branch => MatchesSchemaShape(branch, value)) == 1;
    }

    private static void AcceptedCases()
    {
        Require(Parse(ObjectDecision("inspect-object"), out var inspect) && inspect.IsVisual && inspect.IsSpatial &&
            !inspect.IsViewChange && !inspect.IsObjectState && !inspect.IsEntityReference && inspect.entityTarget == null && inspect.objectState == null,
            "Read-only object inspection is visual but grants no state-change instruction.");
        foreach (string state in new[] { "on", "off" })
        {
            JObject value = ObjectDecision("set-object-state", state);
            Require(Parse(value, out var decision) && decision.IsObjectState && decision.IsVisual && decision.IsSpatial &&
                !decision.IsViewChange && decision.objectState == state && decision.visualTarget.captureId == "current-capture-17",
                "Exact " + state + " action survives parsing with its current pixel identity.");
            Require(decision.visualTarget.label == "桌上的亮处" && Math.Abs(decision.visualTarget.x - .47f) < .0001f,
                "The parser preserves the model's pixel interpretation without converting it to verified object identity.");
            value["origin"] = "autonomous"; value["evidence"] = "我想调整眼前这盏已声明可控的灯。";
            Require(Parse(value, out decision, true, "") && decision.origin == "autonomous" && decision.objectState == state,
                "An explicit autonomous local light intention uses the same closed state protocol.");
        }
        JObject edge = ObjectDecision("inspect-object"); edge["visualTarget"]["x"] = 0; edge["visualTarget"]["y"] = 1;
        Require(Parse(edge, out _), "Image boundary pixels remain legal inputs for later runtime visibility checks.");
    }

    private static void RejectionCases()
    {
        Require(!Parse(ObjectDecision("set-object-state"), out _), "State changes require objectState.");
        foreach (JToken state in new JToken[] { JValue.CreateNull(), new JValue(true), new JValue(1), new JValue("ON"),
            new JValue("toggle"), new JValue("dim"), new JValue("light-on"), new JValue(" on "), new JObject { ["enabled"] = true } })
        {
            JObject value = ObjectDecision("set-object-state", "on"); value["objectState"] = state;
            Require(!Parse(value, out _), "Unsupported/coerced objectState is rejected: " + state.ToString(Newtonsoft.Json.Formatting.None));
        }
        foreach (string intent in new[] { "inspect-object", "set-object-state", "approach-object" })
        {
            JObject valid = ObjectDecision(intent, intent == "set-object-state" ? "on" : null);
            JObject missing = (JObject)valid.DeepClone(); missing.Remove("visualTarget");
            Require(!Parse(missing, out _), intent + " cannot omit current image pixel identity.");
            JObject invalid = (JObject)valid.DeepClone(); invalid["visualTarget"]["captureId"] = "";
            Require(!Parse(invalid, out _), intent + " cannot submit an empty observation identity.");
            invalid = (JObject)valid.DeepClone(); invalid["visualTarget"]["worldPosition"] = new JArray(0, 1, 2);
            Require(!Parse(invalid, out _), intent + " cannot inject world coordinates.");
            invalid = (JObject)valid.DeepClone(); invalid["capability"] = "light-on";
            Require(!Parse(invalid, out _), intent + " cannot grant its own device capability.");
            foreach (JToken x in new JToken[] { new JValue(-.01), new JValue(1.01), new JValue("0.5"),
                new JValue(double.NaN), new JValue(double.PositiveInfinity), JValue.CreateNull() })
            {
                invalid = (JObject)valid.DeepClone(); invalid["visualTarget"]["x"] = x;
                Require(!Parse(invalid, out _), intent + " rejects non-finite, coerced, missing or out-of-image coordinates.");
            }
            invalid = (JObject)valid.DeepClone(); invalid["viewChange"] = new JObject { ["captureId"] = "current-capture-17", ["yaw"] = 20, ["pitch"] = 0 };
            Require(!Parse(invalid, out _), intent + " cannot silently combine a second view-change instruction.");
            Require(!Parse(valid, out _, true, ""), "An autonomous tick cannot replay a cited user object command.");
            Require(!Parse(valid, out _, false, "刚才我说的是另一件事"), "Historical object evidence is not current user authority.");
            invalid = (JObject)valid.DeepClone(); invalid["origin"] = "none"; invalid["evidence"] = "";
            Require(!Parse(invalid, out _), "No-origin object instruction cannot execute.");
        }
        string duplicate = ObjectDecision("set-object-state", "on").ToString(Newtonsoft.Json.Formatting.None)
            .Replace("\"objectState\":\"on\"", "\"objectState\":\"on\",\"objectState\":\"off\"");
        Require(!RoomTaskProtocol.TryDecision(duplicate, UserText, false, out _), "Duplicate contradictory state fields are rejected, not last-value-wins.");
    }

    private static void LegacyIsolationCases()
    {
        foreach (string intent in new[] { "none", "approach", "follow", "stop-moving", "inspect", "look-at", "approach-object", "orient-view", "turn-in-place", "inspect-object" })
        {
            var value = new JObject { ["intent"] = intent, ["origin"] = "user", ["evidence"] = "检查这盏灯" };
            if (intent == "look-at" || intent == "approach-object" || intent == "inspect-object") value["visualTarget"] = Target();
            if (intent == "orient-view" || intent == "turn-in-place")
                value["viewChange"] = new JObject { ["captureId"] = "current-capture-17", ["yaw"] = 20, ["pitch"] = 0 };
            Require(Parse(value, out var original) && !original.IsObjectState, "Existing/read-only intent still parses independently: " + intent);
            value["objectState"] = "off";
            Require(!Parse(value, out _), "Only the explicit state intent may carry an off instruction: " + intent);
            value["objectState"] = JValue.CreateNull();
            Require(!Parse(value, out _), "Null objectState is not an allowed extra field: " + intent);
        }
    }

    private static void EntityReferenceCases()
    {
        JObject ReferenceDecision(string intent, string state = null)
        {
            var value = ObjectDecision(intent, state); value.Remove("visualTarget"); value["entityTarget"] = EntityTarget();
            return value;
        }
        foreach (string intent in new[] { "inspect-object", "set-object-state", "approach-object" })
        {
            string state = intent == "set-object-state" ? "on" : null;
            JObject valid = ReferenceDecision(intent, state);
            Require(Parse(valid, out var decision) && decision.IsEntityReference && decision.IsVisual &&
                decision.visualTarget == null && decision.viewChange == null && decision.objectState == state &&
                decision.entityTarget.captureId == "current-capture-17" && decision.entityTarget.entityId == "scene-object-known-lamp-17",
                intent + " preserves an exact entity reference without inventing a pixel or view-change command.");
            JObject autonomous = (JObject)valid.DeepClone(); autonomous["origin"] = "autonomous";
            autonomous["evidence"] = "再检查刚才明确观察过的同一盏灯。";
            Require(Parse(autonomous, out decision, true, "") && decision.origin == "autonomous" && decision.IsEntityReference,
                "Autonomous entity references use the same closed representation and origin rules.");
            Require(!Parse(valid, out _, true, ""), "An autonomous round cannot replay a user's entity-referenced request.");
            Require(!Parse(valid, out _, false, "另一轮对话"), "Entity identity does not bypass current-user evidence checks.");
            foreach (JToken invalidTarget in new JToken[] { JValue.CreateNull(), new JValue(0), new JValue("刚才那盏灯"),
                new JArray(0, 1, 2), new JObject(), new JObject { ["captureId"]="current-capture-17", ["name"]="桌上的灯" },
                new JObject { ["captureId"]="current-capture-17", ["path"]="1階家具/standlight (4)" } })
            {
                var value = (JObject)valid.DeepClone(); value["entityTarget"] = invalidTarget;
                Require(!Parse(value, out _), intent + " rejects null/coerced/empty/vector/name/path references.");
            }
            foreach (string field in new[] { "captureId", "entityId" })
            {
                foreach (JToken invalidId in new JToken[] { JValue.CreateNull(), new JValue(0), new JValue(false), new JValue(""),
                    new JValue(" \t\n"), new JValue(new string('a', 129)), new JArray(1, 2, 3),
                    new JObject { ["contains"]="灯" } })
                {
                    var value = (JObject)valid.DeepClone(); value["entityTarget"][field] = invalidId;
                    Require(!Parse(value, out _), intent + " rejects malformed, empty, oversized or fuzzy " + field + ".");
                }
                var missing = (JObject)valid.DeepClone(); ((JObject)missing["entityTarget"]).Remove(field);
                Require(!Parse(missing, out _), intent + " cannot omit " + field + ".");
            }
            foreach (string extra in new[] { "label", "name", "match", "x", "worldPosition", "capability", "objectState" })
            {
                var value = (JObject)valid.DeepClone(); value["entityTarget"][extra] = extra == "worldPosition" ?
                    (JToken)new JArray(0, 1, 2) : new JValue("forbidden");
                Require(!Parse(value, out _), intent + " rejects extra entity-target field " + extra + ".");
            }
            foreach (string extra in new[] { "entityId", "worldPosition", "targetName", "capability" })
            {
                var value = (JObject)valid.DeepClone(); value[extra] = "forbidden";
                Require(!Parse(value, out _), intent + " rejects extra top-level field " + extra + ".");
            }
            foreach (JToken pixel in new JToken[] { Target(), JValue.CreateNull() })
            {
                var value = (JObject)valid.DeepClone(); value["visualTarget"] = pixel;
                Require(!Parse(value, out _), "Entity and pixel selection are exclusive, including a null pixel field.");
            }
            foreach (JToken view in new JToken[] { new JObject { ["captureId"]="current-capture-17", ["yaw"]=20, ["pitch"]=0 }, JValue.CreateNull() })
            {
                var value = (JObject)valid.DeepClone(); value["viewChange"] = view;
                Require(!Parse(value, out _), "Entity selection cannot include a second view-change instruction, including null.");
            }
            JObject boundary = (JObject)valid.DeepClone(); boundary["entityTarget"]["captureId"] = new string('c', 128);
            boundary["entityTarget"]["entityId"] = new string('e', 128);
            Require(Parse(boundary, out decision) && decision.entityTarget.entityId.Length == 128,
                "Bounded 128-character IDs remain syntax-valid for exact runtime lookup.");
            // Parsing never resolves a name or guesses membership in the engine's known-object set.
            // The runtime must reject an unknown ID by exact equality, even when its syntax is valid.
            JObject opaque = (JObject)valid.DeepClone(); opaque["entityTarget"]["entityId"] = "UNKNOWN-Exact-ID";
            Require(Parse(opaque, out decision) && decision.entityTarget.entityId == "UNKNOWN-Exact-ID",
                "Opaque ID spelling is preserved; parser acceptance supplies neither fuzzy matching nor execution authority.");
            string duplicate = valid.ToString(Newtonsoft.Json.Formatting.None).Replace("\"entityId\":\"scene-object-known-lamp-17\"",
                "\"entityId\":\"scene-object-known-lamp-17\",\"entityId\":\"another-entity\"");
            Require(!RoomTaskProtocol.TryDecision(duplicate, UserText, false, out _), "Duplicate nested entity IDs are rejected.");
        }
        Require(Parse(ReferenceDecision("set-object-state", "off"), out var off) && off.objectState == "off" && off.IsEntityReference,
            "The known-entity protocol also preserves an explicit Off instruction.");
        Require(!Parse(ReferenceDecision("set-object-state"), out _), "A known identity does not supply a missing power state.");
        JObject readOnly = ReferenceDecision("inspect-object"); readOnly["objectState"] = JValue.CreateNull();
        Require(!Parse(readOnly, out _), "Entity inspection remains read-only even if objectState is null.");
        foreach (string intent in new[] { "none", "approach", "follow", "stop-moving", "inspect", "look-at", "approach-object", "orient-view", "turn-in-place" })
        {
            var value = new JObject { ["intent"]=intent, ["origin"]="user", ["evidence"]="检查这盏灯" };
            if (intent == "look-at" || intent == "approach-object") value["visualTarget"] = Target();
            if (intent == "orient-view" || intent == "turn-in-place")
                value["viewChange"] = new JObject { ["captureId"]="current-capture-17", ["yaw"]=20, ["pitch"]=0 };
            Require(Parse(value, out var legacy) && !legacy.IsEntityReference, "Legacy intent remains valid without entityTarget: " + intent);
            value["entityTarget"] = EntityTarget(); Require(!Parse(value, out _), intent + " rejects an entityTarget field.");
            value["entityTarget"] = JValue.CreateNull(); Require(!Parse(value, out _), intent + " rejects a null entityTarget field.");
        }
    }

    private static void TargetSelectionCases()
    {
        foreach (string intent in new[] { "look-at", "approach-object", "inspect-object", "set-object-state" })
        {
            var value = ObjectDecision(intent, intent == "set-object-state" ? "on" : null);
            Require(Parse(value, out var original) && original.targetSelection == null && !original.RequestsTargetReselection,
                "An ordinary object intent does not implicitly revoke an old semantic association: " + intent);
            value["targetSelection"] = "reselect";
            Require(Parse(value, out var correction) && correction.RequestsTargetReselection && correction.targetSelection == "reselect" &&
                correction.intent == intent && correction.visualTarget != null && correction.objectState == original.objectState,
                "Explicit reselection preserves the requested action and pixel rather than authorizing a different action: " + intent);
            foreach (JToken invalid in new JToken[] { JValue.CreateNull(), new JValue(""), new JValue("RESELECT"), new JValue(" reselect"),
                new JValue("forget"), new JValue("clear-history"), new JValue(true), new JValue(1), new JObject { ["reselect"]=true } })
            {
                value["targetSelection"] = invalid;
                Require(!Parse(value, out _), "Reselection rejects null/coercion/deletion/unsupported semantics: " + intent);
            }
            value["targetSelection"] = "reselect";
            value["forgetHistory"] = true;
            Require(!Parse(value, out _), "Reselection cannot add a scene-memory deletion instruction.");
            value.Remove("forgetHistory");
            if (intent != "look-at")
            {
                value.Remove("visualTarget"); value["entityTarget"] = EntityTarget();
                Require(Parse(value, out correction) && correction.RequestsTargetReselection && correction.IsEntityReference,
                    "Reselection with an exact entity remains only an intended semantic correction: " + intent);
                value["visualTarget"] = JValue.CreateNull();
                Require(!Parse(value, out _), "Reselection cannot bypass entity/pixel exclusivity even with a null pixel.");
            }
        }
        foreach (string intent in new[] { "none", "approach", "follow", "stop-moving", "inspect", "orient-view", "turn-in-place" })
        {
            var value = new JObject { ["intent"]=intent, ["origin"]="user", ["evidence"]="检查这盏灯" };
            if (intent == "orient-view" || intent == "turn-in-place")
                value["viewChange"] = new JObject { ["captureId"]="current-capture-17", ["yaw"]=20, ["pitch"]=0 };
            Require(Parse(value, out _), "Baseline non-object intent remains valid: " + intent);
            foreach (JToken selection in new JToken[] { new JValue("reselect"), JValue.CreateNull() })
            { value["targetSelection"]=selection; Require(!Parse(value, out _), "Non-object intent refuses targetSelection, including null: " + intent); }
        }
        var duplicate = ObjectDecision("inspect-object"); duplicate["targetSelection"]="reselect";
        string repeated = duplicate.ToString(Newtonsoft.Json.Formatting.None).Replace("\"targetSelection\":\"reselect\"",
            "\"targetSelection\":\"reselect\",\"targetSelection\":\"reselect\"");
        Require(!RoomTaskProtocol.TryDecision(repeated, UserText, false, out _), "Duplicate targetSelection fields are rejected.");
    }

    private static void ObservationApproachCases()
    {
        foreach (bool entity in new[] { false, true })
        {
            var value = ObjectDecision("approach-object");
            if (entity) { value.Remove("visualTarget"); value["entityTarget"]=EntityTarget(); }
            Require(Parse(value, out var baseline) && !baseline.IsObservationApproach && baseline.targetPurpose == null,
                "Ordinary object approach retains its original target purpose.");
            value["targetPurpose"]="observe-closer";
            Require(Parse(value, out var observation) && observation.IsObservationApproach && observation.intent == "approach-object" &&
                observation.targetPurpose == "observe-closer" && observation.IsEntityReference == entity && !observation.IsObjectState,
                "Approaching a larger visible reference for a closer view is explicit and grants no switch instruction.");
            value["targetSelection"]="reselect";
            Require(Parse(value, out observation) && observation.RequestsTargetReselection && observation.IsObservationApproach,
                "The observation subgoal can explicitly correct the previous semantic association without losing its purpose.");
            value["origin"]="autonomous"; value["evidence"]="先靠近电视柜，看清原先想观察的小灯。";
            Require(Parse(value, out observation, true, "") && observation.IsObservationApproach && observation.origin == "autonomous",
                "Autonomy can choose a closer-observation subgoal under the same origin rules.");
            Require(!Parse(value, out _, false), "Autonomous closer observation cannot impersonate current user authority.");
            value["origin"]="user"; value["evidence"]="靠近电视柜";
            Require(!Parse(value, out _, true, ""), "An autonomous round cannot replay the old user's observation approach.");
            foreach (JToken invalid in new JToken[] { JValue.CreateNull(), new JValue(""), new JValue("OBSERVE-CLOSER"), new JValue(" observe-closer"),
                new JValue("operate"), new JValue(true), new JValue(1), new JObject { ["move"]=true } })
            { value["targetPurpose"]=invalid; Require(!Parse(value, out _), "Observation purpose rejects null/coercion or implicit operation authority."); }
            value["targetPurpose"]="observe-closer"; value["objectState"]="on";
            Require(!Parse(value, out _), "A closer observation approach cannot also open its larger reference object.");
            value.Remove("objectState"); value["originalTarget"]="unverified secret world position";
            Require(!Parse(value, out _), "The observation purpose cannot smuggle an arbitrary extra target field.");
        }
        foreach (string intent in new[] { "none", "approach", "follow", "stop-moving", "inspect", "look-at", "inspect-object", "set-object-state", "orient-view", "turn-in-place" })
        {
            var value = new JObject { ["intent"]=intent, ["origin"]="user", ["evidence"]="检查这盏灯" };
            if (intent == "look-at" || intent == "inspect-object" || intent == "set-object-state") value["visualTarget"]=Target();
            if (intent == "set-object-state") value["objectState"]="on";
            if (intent == "orient-view" || intent == "turn-in-place")
                value["viewChange"]=new JObject { ["captureId"]="current-capture-17", ["yaw"]=20, ["pitch"]=0 };
            Require(Parse(value, out _), "Unmodified other intent remains valid before purpose injection: " + intent);
            foreach (JToken purpose in new JToken[] { new JValue("observe-closer"), JValue.CreateNull() })
            { value["targetPurpose"]=purpose; Require(!Parse(value, out _), "Only approach-object permits an observation-purpose field: " + intent); }
        }
    }

    private static void ObjectMatchCases()
    {
        string[] offered = { "c1", "c3", "c6" };
        JObject Result(string verdict, string id) => new JObject { ["verdict"]=verdict, ["candidateId"]=id, ["reason"]="当前图中可核对的描述关系" };
        bool ParseMatch(JObject value, out RoomTaskProtocol.ObjectMatch match) =>
            RoomTaskProtocol.TryObjectMatch(value.ToString(Newtonsoft.Json.Formatting.None), offered, out match);
        foreach (string id in offered)
        {
            Require(ParseMatch(Result("matched", id), out var match) && match.IsMatched && match.candidateId == id,
                "A matched current candidate preserves its exact short identity, including a correct alternative to an earlier wrong selection.");
            foreach (string verdict in new[] { "different-target", "uncertain" })
                Require(ParseMatch(Result(verdict, id), out match) && !match.IsMatched && match.verdict == verdict,
                    "A proposed replacement or uncertain candidate is not a positive semantic match.");
        }
        foreach (string verdict in new[] { "different-target", "uncertain" })
        {
            var value = Result(verdict, "none");
            Require(ParseMatch(value, out var match) && !match.IsMatched, "Failure or uncertainty may honestly have no identifiable alternative.");
            Require(RoomTaskProtocol.TryObjectMatch(value.ToString(), null, out _), "An empty candidate view still permits a no-target uncertain/rejection result.");
        }
        Require(!ParseMatch(Result("matched", "none"), out _), "Matched cannot claim success without an actual offered candidate.");
        Require(!RoomTaskProtocol.TryObjectMatch(Result("matched", "c1").ToString(), null, out _), "No offered candidate set means no positive match.");
        foreach (JToken id in new JToken[] { JValue.CreateNull(), new JValue(0), new JValue(true), new JValue(""), new JValue(" c1"),
            new JValue("C1"), new JValue("c2"), new JValue("c0"), new JValue("c7"), new JValue("scene-object-known-lamp-17"),
            new JValue("current-capture-17"), new JValue("7d9de4e777ed41bd9e48caa8d67f331a"), new JValue(new string('a', 129)),
            new JObject { ["entityId"]="c1" }, new JArray(0, 1, 2) })
        {
            var value = Result("matched", "c1"); value["candidateId"]=id;
            Require(!ParseMatch(value, out _), "Matched refuses unoffered IDs, UUIDs, entity IDs, coercions, vectors and fuzzy references.");
        }
        Require(!RoomTaskProtocol.TryObjectMatch(Result("matched", "c1").ToString(), new[] { "C1" }, out _),
            "Offered-candidate membership uses exact ordinal equality, not case folding.");
        Require(!RoomTaskProtocol.TryObjectMatch(Result("matched", "7d9de4e777ed41bd9e48caa8d67f331a").ToString(),
            new[] { "7d9de4e777ed41bd9e48caa8d67f331a" }, out _), "Even a malformed offered list cannot make a session UUID a valid candidate number.");
        foreach (string key in new[] { "verdict", "candidateId", "reason" })
        {
            var missing = Result("matched", "c1"); missing.Remove(key);
            Require(!ParseMatch(missing, out _), "Object matcher requires every advertised field: " + key);
            var invalid = Result("matched", "c1"); invalid[key]=JValue.CreateNull();
            Require(!ParseMatch(invalid, out _), "Matcher required fields cannot be null: " + key);
        }
        foreach (string verdict in new[] { "MATCHED", "consistent", "accepted", " matched", "" })
            Require(!ParseMatch(Result(verdict, "c1"), out _), "Speech approval/unknown verdicts cannot impersonate object matching.");
        foreach (string field in new[] { "entityId", "captureId", "worldPosition", "capability", "objectState", "targetSelection", "targetPurpose", "observationGoalId" })
        {
            var extra = Result("matched", "c1"); extra[field]="forbidden";
            Require(!ParseMatch(extra, out _), "Matching cannot smuggle execution parameters: " + field);
        }
        var bounded = Result("matched", "c1"); bounded["reason"]=new string('a', 160);
        Require(ParseMatch(bounded, out _), "Matcher supports the exact bounded explanation length.");
        bounded["reason"]=new string('a', 161); Require(!ParseMatch(bounded, out _), "Oversized matching explanations fail closed.");
        string duplicate = Result("matched", "c1").ToString(Newtonsoft.Json.Formatting.None)
            .Replace("\"candidateId\":\"c1\"", "\"candidateId\":\"c1\",\"candidateId\":\"c3\"");
        Require(!RoomTaskProtocol.TryObjectMatch(duplicate, offered, out _), "Duplicate contradictory matcher candidate IDs fail closed.");
    }

    private static void ObservationGoalCases()
    {
        const string goal = "observation-goal-current-17";
        foreach (string intent in new[] { "look-at", "inspect-object", "approach-object", "set-object-state" })
        foreach (bool entity in new[] { false, true })
        {
            if (entity && intent == "look-at") continue;
            var value = ObjectDecision(intent, intent == "set-object-state" ? "on" : null);
            if (entity) { value.Remove("visualTarget"); value["entityTarget"]=EntityTarget(); }
            Require(Parse(value, out var baseline) && baseline.observationGoalId == null && !baseline.ContinuesObservationGoal,
                "Object actions without goalId do not implicitly continue the old observation objective: " + intent);
            value["observationGoalId"]=goal;
            Require(Parse(value, out var continuation) && continuation.ContinuesObservationGoal && continuation.observationGoalId == goal &&
                continuation.intent == intent && continuation.IsEntityReference == entity && continuation.objectState == baseline.objectState,
                "Explicit observation continuation preserves the selected object action, representation and state.");
            value["targetSelection"]="reselect";
            if (intent == "approach-object") value["targetPurpose"]="observe-closer";
            Require(Parse(value, out continuation) && continuation.ContinuesObservationGoal && continuation.RequestsTargetReselection &&
                continuation.IsObservationApproach == (intent == "approach-object"),
                "Goal continuation composes with reselection and a new reference approach without losing its exact goal ID.");
            value["origin"]="autonomous"; value["evidence"]="继续明确绑定的观察目标，重新看清原来那盏小灯。";
            Require(Parse(value, out continuation, true, "") && continuation.ContinuesObservationGoal && continuation.origin == "autonomous",
                "Autonomous continuation has explicit syntax; live goal/turn ownership is still an engine check.");
            Require(!Parse(value, out _, false), "A goal reference cannot bypass autonomous-origin rules.");
            value["origin"]="user"; value["evidence"]=baseline.evidence;
            Require(!Parse(value, out _, true, "") && !Parse(value, out _, false, "与先前观察无关的新要求"),
                "A goal reference cannot replay previous user authority or replace current evidence.");
            foreach (JToken invalid in new JToken[] { JValue.CreateNull(), new JValue(""), new JValue(" \t\n"),
                new JValue(0), new JValue(false), new JValue(new string('g',129)), new JObject { ["goalId"]=goal }, new JArray(goal) })
            { value["observationGoalId"]=invalid; Require(!Parse(value, out _), "Continuation ID rejects null, coercion, blank, oversized and nested references."); }
            value["observationGoalId"]=new string('g',128);
            Require(Parse(value, out continuation) && continuation.observationGoalId.Length == 128, "The exact goal-ID length boundary remains syntax-valid.");
            value["observationGoalId"]="unknown-goal-for-exact-engine-check";
            Require(Parse(value, out continuation) && continuation.observationGoalId == "unknown-goal-for-exact-engine-check",
                "Parser preserves opaque spelling; accepted syntax does not prove an active, historical or current-room goal binding.");
            value["goalAuthorization"]="allowed";
            Require(!Parse(value, out _), "Optional goal fields cannot open the closed schema to model-granted authorization.");
        }
        foreach (string intent in new[] { "none", "approach", "follow", "stop-moving", "inspect", "orient-view", "turn-in-place" })
        {
            var value=new JObject { ["intent"]=intent, ["origin"]="user", ["evidence"]="检查这盏灯" };
            if (intent == "orient-view" || intent == "turn-in-place")
                value["viewChange"]=new JObject { ["captureId"]="current-capture-17", ["yaw"]=20, ["pitch"]=0 };
            Require(Parse(value, out _), "Unmodified non-object intent still parses before goal injection: " + intent);
            foreach (JToken invalid in new JToken[] { new JValue(goal), JValue.CreateNull() })
            { value["observationGoalId"]=invalid; Require(!Parse(value, out _), "Non-object intents reject observationGoalId, including null: " + intent); }
        }
        var duplicate=ObjectDecision("approach-object"); duplicate["observationGoalId"]=goal;
        duplicate["targetSelection"]="reselect"; duplicate["targetPurpose"]="observe-closer";
        Require(duplicate.Count == 7 && Parse(duplicate, out _), "Maximum seven-field object continuation is supported without opening unknown fields.");
        string repeated=duplicate.ToString(Newtonsoft.Json.Formatting.None).Replace("\"observationGoalId\":\""+goal+"\"",
            "\"observationGoalId\":\""+goal+"\",\"observationGoalId\":\"other-goal\"");
        Require(!RoomTaskProtocol.TryDecision(repeated,UserText,false,out _), "Duplicate contradictory observation goal IDs are rejected.");
    }

    private static void Require(bool condition, string reason)
    { checks++; if (!condition) throw new InvalidOperationException(reason); }
}
