using System;
using System.Collections;
using System.Linq;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Production transport policy, lossless context recovery and callback ownership.
/// No endpoint calls; does not claim a recovered live model decision.</summary>
public static partial class RoomDecisionRecoveryRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static JObject LastReport { get; private set; }

    public static int RunChecks()
    {
        checks = 0;
        LastReport = new JObject { ["status"] = "running", ["httpRequests"] = 0,
            ["scope"] = "Provider policy, exact evidence preservation and owner cancellation; no live backend recovery or semantic-action success claim." };
        var host = new GameObject("InactiveRoomDecisionRecoveryFixture"); host.SetActive(false);
        try
        {
            CheckFailureClassification();
            var provider = host.AddComponent<ChatQW>();
            provider.m_Backend = ChatQW.BackendType.Local;
            var source = BuildSource();
            string input = source.ToString(Formatting.None);
            string wire = (string)Call(provider, "BuildRoomTaskRequestJsonWithImage", input,
                "data:image/png;base64,CURRENT_PIXELS_CANARY", false);
            object[] compactArgs = { wire, null };
            Require((bool)Call(null, "TryCompactRoomTaskRequest", compactArgs), "Repeated captured geometry permits a meaningful lossless reduction.");
            string compact = (string)compactArgs[1];
            var before = JObject.Parse(wire); var after = JObject.Parse(compact);
            Require(JToken.DeepEquals(before["response_format"], after["response_format"]) &&
                JToken.DeepEquals(before["messages"][0], after["messages"][0]) &&
                JToken.DeepEquals(((JArray)before["messages"]).Last, ((JArray)after["messages"]).Last),
                "Native grammar, system contract and latest-turn authority remain byte-equivalent JSON tokens.");
            Require(before.SelectTokens("$..image_url.url").Select(x => (string)x).SequenceEqual(after.SelectTokens("$..image_url.url").Select(x => (string)x)),
                "Current and historical image attachments retain their order and identity.");
            var recovered = MainInput(after);
            var table = (JObject)recovered["requestEvidenceTable"];
            Require(table != null && table.Count > 0, "Compacted input explicitly carries its complete evidence dictionary.");
            Expand(recovered, table);
            recovered.Remove("requestEvidenceTable"); recovered.Remove("requestEvidenceEncoding");
            var sentSource = MainInput(before);
            Require(source.ToString(Formatting.None) == input, "Purpose-scoped serialization does not mutate stored source evidence.");
            Require(JToken.DeepEquals(sentSource, recovered),
                "Expanding recovery references reconstructs every field of the original purpose-scoped HTTP packet exactly. " + FirstDifference(sentSource, recovered, "$"));
            Require((bool?)recovered["visibleObjectCatalog"]["memory"][0]["actionAuthorization"] == false &&
                (bool?)recovered["visibleObjectCatalog"]["memory"][0]["historical"] == true,
                "Lossless context retry never upgrades remembered coordinates into current visibility or permission.");

            foreach (string category in new[] { "connection", "timeout", "rate-limit", "server-error" })
            {
                var plan = Retry(false, 1, true, 18, category, null, wire);
                Require((bool)plan["retry"] && (string)plan["json"] == wire, "Transient first failure retries the identical immutable decision input: " + category);
                Require(!(bool)Retry(false, 2, true, 18, category, null, wire)["retry"], "A second failure is terminal: " + category);
            }
            Require((bool)Retry(false, 1, true, 18, "context-limit", null, wire)["retry"] &&
                (string)Retry(false, 1, true, 18, "context-limit", null, wire)["json"] == compact,
                "A proven context limit uses the reduced valid request exactly once.");
            foreach (string category in new[] { "invalid-request", "authentication", "unknown" })
                Require(!(bool)Retry(false, 1, true, 18, category, null, wire)["retry"], "A permanent or unexplained provider rejection is never retried blindly: " + category);
            Require(!(bool)Retry(false, 1, false, 18, "connection", null, wire)["retry"] &&
                !(bool)Retry(true, 1, true, 18, "connection", null, wire)["retry"] &&
                !(bool)Retry(false, 1, true, 1, "connection", null, wire)["retry"],
                "Cancellation, review ownership and the total latency budget independently block retry.");
            Require(!(bool)Retry(false, 1, true, 18, "rate-limit", "120", wire)["retry"] &&
                !(bool)Retry(false, 1, true, 18, "rate-limit", "NaN", wire)["retry"] &&
                (double)Retry(false, 1, true, 18, "rate-limit", "1.5", wire)["delay"] == 1.5,
                "Long or malformed Retry-After is not ignored; bounded provider backoff is honored.");
            string tiny = (string)Call(provider, "BuildRoomTaskRequestJsonWithImage", "{\"currentUserText\":\"来我身边。\",\"autonomous\":false}", null, false);
            Require(!(bool)Retry(false, 1, true, 18, "context-limit", null, tiny)["retry"] &&
                (string)Retry(false, 1, true, 18, "context-limit", null, tiny)["reason"] == "context-not-reducible",
                "A context failure with nothing safely reducible fails truthfully, rather than dropping authority or truncating JSON.");
            var diagnostic = (JObject)Call(null, "BuildRoomTaskRequestDiagnostic", wire);
            Require((int?)diagnostic["imageCount"] == 1 && ((string)diagnostic["schemaSha256"]).Length == 64 &&
                (int?)diagnostic["inputFieldCharacters"]["currentUserText"] > 0 && !diagnostic.ToString().Contains("CANARY") &&
                !diagnostic.ToString().Contains("来我身边"), "Request diagnostics expose sizes and stable hashes, never prompts or image bytes.");
            Require(ChatQW.TryReadRoomTaskFailureStatus("room-task-http-invalid-request;attempts=1;recovery=not-eligible", out var stopped) &&
                (int?)stopped["attempts"] == 1 && (bool?)stopped["retryAttempted"] == false && (bool?)stopped["actionDispatched"] == false,
                "Unknown HTTP400 is distinguishable from retries and from action execution.");
            Require(ChatQW.TryReadRoomTaskFailureStatus("room-task-http-server-error;attempts=2;recovery=exhausted", out var exhausted) &&
                (bool?)exhausted["retryAttempted"] == true && (string)exhausted["recovery"] == "exhausted" &&
                ChatQW.TryReadRoomTaskFailureStatus("room-task-http-failure", out var legacy) && legacy["attempts"].Type == JTokenType.Null,
                "Only actual attempt counts become recovery facts; older unstructured failures remain unknown.");
            int generation = (int)typeof(ChatQW).GetField("m_RoomTaskRequestGeneration", Flags).GetValue(provider);
            int calls = 0;
            Action<bool, string, string> callback = (ok, text, status) => { calls++; Require(!ok && text == "", "Transport failure cannot release executable partial output."); };
            Call(provider, "PublishRoomTaskFailure", generation, 400L, false, 0f,
                "room-task-http-invalid-request;attempts=1;recovery=not-eligible", callback);
            Require(calls == 1, "A terminal current transport failure settles its caller once.");
            IEnumerator stale = (IEnumerator)Call(provider, "RequestRoomTaskPayload", wire, false, generation, callback);
            provider.CancelActiveResponse();
            Require(!stale.MoveNext(), "An invalidated owner never enters even the initial HTTP attempt.");
            Call(provider, "PublishRoomTaskFailure", generation, 503L, false, 0f,
                "room-task-http-server-error;attempts=2;recovery=exhausted", callback);
            Require(calls == 1, "Cancelled late failure cannot produce fallback speech or start another action.");
            LastReport["originalCharacters"] = wire.Length;
            LastReport["compactedCharacters"] = compact.Length;
            LastReport["status"] = "passed";
            return checks;
        }
        catch (Exception error) { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
        finally { LastReport["checks"] = checks; UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckFailureClassification()
    {
        foreach (var item in new[] {
            new { Code = 400L, Message = "invalid json schema", Expected = "invalid-request" },
            new { Code = 400L, Message = "request (81899 tokens) exceeds the available context size (65536 tokens)", Expected = "context-limit" },
            new { Code = 400L, Message = "maximum context length is 65536 tokens", Expected = "context-limit" },
            new { Code = 401L, Message = "maximum context length is 65536 tokens", Expected = "authentication" },
            new { Code = 413L, Message = "request payload too large", Expected = "invalid-request" },
            new { Code = 429L, Message = "too many requests", Expected = "rate-limit" },
            new { Code = 503L, Message = "no slots available", Expected = "server-error" },
            new { Code = 408L, Message = "request timeout", Expected = "timeout" },
            new { Code = 0L, Message = "", Expected = "connection" }
        })
        {
            var body = new JObject { ["error"] = new JObject { ["message"] = item.Message }, ["messages"] = "PRIVATE_PROMPT_CANARY" };
            var facts = (JObject)Call(null, "BuildRoomTaskHttpFailure", item.Code, body.ToString(), "");
            Require((string)facts["category"] == item.Expected && !facts.ToString().Contains("PRIVATE_PROMPT_CANARY"),
                "Service evidence classifies failure without copying unrelated envelope payload: " + item.Code + " / " + item.Expected);
        }
        string message = "invalid input data:image/png;base64,PRIVATE_IMAGE_CANARY Authorization: Bearer SECRET_CREDENTIAL_CANARY \"PRIVATE_QUOTE_CANARY\" " + new string('x', 2000);
        var secret = (JObject)Call(null, "BuildRoomTaskHttpFailure", 400L,
            new JObject { ["error"] = new JObject { ["message"] = message } }.ToString(), "");
        Require(!secret.ToString().Contains("CANARY") && ((string)secret["message"]).Length <= 779 &&
            (int?)secret["bodyCharacters"] > 2000 && ((string)secret["bodySha256"]).Length == 64,
            "Bounded error diagnostics redact image data, credentials, quoted echoes and long opaque payloads while preserving a grouping hash.");
        foreach (string quotedEcho in new[] {
            "invalid request value: \"" + string.Concat(Enumerable.Repeat("私密中文CANARY不应出现在日志中", 700)) + "\" end",
            "invalid request value: \"PRIVATE_CANARY before \\\"ESCAPED_QUOTE_CANARY\\\" " + new string('密', 9000) + "\" end",
            "invalid request value: 'PRIVATE_CANARY before \\'ESCAPED_QUOTE_CANARY\\' " + new string('密', 9000) + "' end",
            "invalid request value: \"PRIVATE_CANARY with missing close and dangling escape " + new string('密', 8113) + "\\"
        })
        {
            var boundedEcho = (JObject)Call(null, "BuildRoomTaskHttpFailure", 400L,
                new JObject { ["error"] = new JObject { ["message"] = quotedEcho } }.ToString(), "");
            string safe = (string)boundedEcho["message"];
            Require(!safe.Contains("CANARY") && !safe.Contains("密") && safe.Contains("[redacted-quoted-value]") && safe.Length <= 779,
                "Oversized quoted provider echoes remain entirely redacted when the size limit removes closing quotes or splits escaped quotes/backslashes.");
        }
    }

    private static JObject BuildSource()
    {
        var pose = new JObject { ["cameraPosition"] = new JObject { ["x"] = 1.25, ["y"] = 2.3, ["z"] = -3.4 },
            ["bodyRotation"] = new JObject { ["x"] = 0, ["y"] = .7, ["z"] = 0, ["w"] = .7 },
            ["capturedAtUtc"] = "2026-10-09T09:48:35Z", ["sourceCaptureId"] = "current-capture",
            ["scope"] = "Frozen measured body and camera pose; historical geometry never authorizes execution. " + new string('s', 360) };
        var memory = new JArray();
        for (int i = 0; i < 18; ++i) memory.Add(new JObject { ["entityId"] = "entity-" + i,
            ["sourceCaptureId"] = "past-capture-" + i, ["historical"] = true, ["actionAuthorization"] = false,
            ["pointAtCapture"] = new JObject { ["x"] = i, ["y"] = 2, ["z"] = -i }, ["selfPose"] = pose.DeepClone() });
        return new JObject { ["currentUserText"] = "重来一遍，来我身边。\n不要再操作灯。😀", ["latestUserText"] = "重来一遍，来我身边。\n不要再操作灯。😀",
            ["autonomous"] = false, ["currentRequestOnly"] = true, ["spatialGoal"] = null,
            ["currentFacts"] = new JObject { ["userAnchorValid"] = true, ["actualUserDistance"] = 3.2, ["bodyReserved"] = false },
            ["visualObservation"] = new JObject { ["captureId"] = "current-capture", ["state"] = "ready", ["hasImage"] = true },
            ["visibleObjectCatalog"] = new JObject { ["memory"] = memory, ["current"] = new JObject { ["captureId"] = "current-capture", ["selfPose"] = pose.DeepClone() } },
            ["sceneSpatialMemory"] = new JObject { ["selfPose"] = pose.DeepClone(), ["actionAuthorization"] = false },
            ["decisionContractFacts"] = new JObject { ["allowedObservationGoalIds"] = new JArray("goal-current") },
            ["recentDialogue"] = new JArray(new JObject { ["role"] = "user", ["quotedText"] = "之前的灯已经关了。" }) };
    }

    private static JObject MainInput(JObject wire)
    {
        foreach (var message in ((JArray)wire["messages"]).OfType<JObject>())
        {
            if ((string)message["role"] != "user") continue;
            var content = message["content"];
            string text = content.Type == JTokenType.String ? (string)content : (string)((JArray)content).OfType<JObject>().First(x => (string)x["type"] == "text")["text"];
            int start = text.IndexOf('{');
            if (start < 0) continue;
            try
            {
                JObject input;
                using (var reader = new JsonTextReader(new StringReader(text.Substring(start))) { DateParseHandling = DateParseHandling.None })
                    input = JObject.Load(reader);
                if (input["currentUserText"]?.Type == JTokenType.String && input["autonomous"]?.Type == JTokenType.Boolean) return input;
            }
            catch (JsonException) { }
        }
        throw new InvalidOperationException("No decision input in fixture request.");
    }

    private static void Expand(JToken token, JObject table)
    {
        foreach (var child in (token as JContainer)?.Children().ToArray() ?? Array.Empty<JToken>())
        {
            if (child is JObject reference && reference.Count == 1 && reference["requestEvidenceRef"]?.Type == JTokenType.String)
                child.Replace(table[(string)reference["requestEvidenceRef"]].DeepClone());
            else Expand(child, table);
        }
    }

    private static string FirstDifference(JToken expected, JToken actual, string path)
    {
        if (JToken.DeepEquals(expected, actual)) return "";
        if (expected?.Type != actual?.Type) return path + " expectedType=" + expected?.Type + " actualType=" + actual?.Type;
        if (expected is JObject leftObject && actual is JObject rightObject)
        {
            if (leftObject.Count != rightObject.Count) return path + " object-count " + leftObject.Count + " != " + rightObject.Count;
            foreach (var property in leftObject.Properties())
            {
                string difference = FirstDifference(property.Value, rightObject[property.Name], path + "." + property.Name);
                if (difference.Length > 0) return difference;
            }
        }
        if (expected is JArray leftArray && actual is JArray rightArray)
        {
            if (leftArray.Count != rightArray.Count) return path + " array-count " + leftArray.Count + " != " + rightArray.Count;
            for (int index = 0; index < leftArray.Count; ++index)
            {
                string difference = FirstDifference(leftArray[index], rightArray[index], path + "[" + index + "]");
                if (difference.Length > 0) return difference;
            }
        }
        return path + " unequal scalar values";
    }

    private static JObject Retry(bool review, int attempts, bool current, float remaining, string category, string retryAfter, string json)
    {
        object[] args = { review, attempts, current, remaining, category, retryAfter, json, null, 0f, null };
        bool retry = (bool)Call(null, "TryPlanRoomTaskTransportRetry", args);
        return new JObject { ["retry"] = retry, ["json"] = (string)args[7], ["delay"] = (float)args[8], ["reason"] = (string)args[9] };
    }
    private static object Call(object model, string method, params object[] args) => typeof(ChatQW).GetMethod(method, Flags).Invoke(model, args);
    private static void Require(bool condition, string reason) { checks++; if (!condition) throw new InvalidOperationException(reason); }
}
