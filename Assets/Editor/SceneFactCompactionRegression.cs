using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Pure production helper regression. No Unity state, HTTP, models or generated images.
public static class SceneFactCompactionRegression
{
    public static JObject LastReport { get; private set; }
    private static int checks;
    private static void Check(bool condition, string reason)
    { checks++; if (!condition) throw new InvalidOperationException(reason); }
    private static JObject ReadRoot(string text, string marker)
    {
        int index = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        using (var reader = new JsonTextReader(new StringReader(text.Substring(index))))
        { reader.DateParseHandling = DateParseHandling.None; reader.FloatParseHandling = FloatParseHandling.Decimal; return JObject.Load(reader); }
    }
    public static int RunChecks()
    {
        checks = 0;
        LastReport = new JObject { ["scope"] = "Exact historical scene subtree sharing and HTTP-stage timing; synthetic deterministic inputs, no requests or Unity execution." };
        try
        {
            const string marker = "[视觉关注与行动事实]";
            var metadata = new JObject { ["source"] = "engine metadata, not image recognition", ["part/~"] = new string('x', 1200), ["precision"] = 1.234567890123456789012345678m };
            var selected = new JObject { ["entityId"] = "lamp-a", ["observedAtUtc"] = "2026-09-14T01:00:00.1234567Z", ["capabilities"] = new JArray("inspect-object"), ["localStructure"] = metadata };
            var newer = (JObject)selected.DeepClone(); newer["observedAtUtc"] = "2026-09-14T01:00:01Z";
            var different = (JObject)selected.DeepClone(); different["entityId"] = "cabinet-b";
            var current = new JObject { ["enabled"] = true, ["active"] = false,
                ["lastAction"] = new JObject { ["phase"] = "rejected", ["reason"] = "no-registered-power-capability", ["entityId"] = "cabinet-b" },
                ["objectContext"] = new JObject { ["selected"] = selected, ["stateResult"] = new JObject { ["entityId"] = "lamp-a", ["changed"] = true, ["actual"] = new JObject { ["on"] = false } },
                    ["recentObservations"] = new JArray(selected.DeepClone(), newer, different), ["knownObjectHistory"] = new JArray(selected.DeepClone(), newer.DeepClone(), different.DeepClone()) } };
            string source = "untouched prior context\n" + marker + " " + current.ToString(Formatting.None) + "\nuntouched tail";
            string compact = SceneFactContextCompactor.Compact(source, out var report);
            Check(report.applied && report.roundTripVerified && report.references >= 4, "Repeated historical facts were not exactly verified and shared");
            Check(compact.Length < source.Length * .75, "Large repeated metadata produced no meaningful text reduction");
            JObject root = ReadRoot(compact, marker);
            Check(JToken.DeepEquals(root["lastAction"], current["lastAction"]) && JToken.DeepEquals(root["objectContext"]["selected"], selected) &&
                JToken.DeepEquals(root["objectContext"]["stateResult"], current["objectContext"]["stateResult"]), "Current outcome, selected entity or independent device result became opaque/changed");
            Check(compact.Contains("2026-09-14T01:00:01Z") && compact.Contains("cabinet-b") && compact.Contains("1.234567890123456789012345678"), "Different time/entity/precise numeric values were discarded or rounded");
            foreach (string boundary in new[] { "1e-30", "-1e-30", "0.1234567890123456789012345678901", "1e100", "-0.0" })
            {
                string precise = source.Replace("1.234567890123456789012345678", boundary);
                string unchanged = SceneFactContextCompactor.Compact(precise, out var numericReport);
                Check(unchanged == precise && !numericReport.applied,
                    "Unsupported/rounded/signed-zero numeric boundary must preserve the entire original context: " + boundary);
            }
            string exactExponent = source.Replace("1.234567890123456789012345678", "123456789e-8");
            string exponentShared = SceneFactContextCompactor.Compact(exactExponent, out var exponentReport);
            Check(exponentReport.applied && exponentReport.roundTripVerified && exponentShared.Contains("1.23456789"),
                "Exactly representable scientific notation should remain eligible without a floating-point comparison");
            Check(compact.StartsWith("untouched prior context\n", StringComparison.Ordinal) && compact.EndsWith("\nuntouched tail", StringComparison.Ordinal), "Non-scene context was rewritten");
            Check(SceneFactContextCompactor.Compact(compact, out _) == compact, "Already shared request was encoded twice");
            string unmarked = "user quotes only " + marker + " " + current.ToString(Formatting.None);
            Check(SceneFactContextCompactor.Compact(unmarked, out _) == unmarked, "Unmarked user text was treated as a program block");
            string quote = "(用户刚开口讲了下面这段话，请回应)\n" + source;
            Check(SceneFactContextCompactor.Compact(quote, out _) == quote, "User quote after program/user boundary was transformed");
            string malformed = marker + " {\"enabled\":true,\"enabled\":false,\"active\":false,\"objectContext\":{}}";
            Check(SceneFactContextCompactor.Compact(malformed, out _) == malformed, "Duplicate JSON properties were silently collapsed");
            string small = marker + " {\"enabled\":true,\"active\":false,\"objectContext\":{}}";
            Check(SceneFactContextCompactor.Compact(small, out _) == small, "No-benefit case changed original text");
            var changed = (JObject)current.DeepClone(); changed["lastAction"]["phase"] = "performed";
            const string roomMarker = "[本轮空间任务；由同一模型的结构化意图及Unity执行事实共同确定]";
            string multiple = source + "\n" + roomMarker + "\n" + new JObject { ["intent"] = "set-object-state", ["dispatch"] = "performed", ["sceneActionFacts"] = changed }.ToString(Formatting.None);
            string sharedMultiple = SceneFactContextCompactor.Compact(multiple, out var multi);
            Check(multi.applied && multi.roundTripVerified && (string)ReadRoot(sharedMultiple, marker)["lastAction"]["phase"] == "rejected" &&
                (string)ReadRoot(sharedMultiple, roomMarker)["sceneActionFacts"]["lastAction"]["phase"] == "performed", "Earlier failure and later actual success were conflated");
            Check(ReadRoot(sharedMultiple, roomMarker)["sceneActionFacts"]["objectContext"][SceneFactContextCompactor.ReferenceKey]?.Type == JTokenType.String &&
                ReadRoot(sharedMultiple, marker)["objectContext"]["selected"]["entityId"].Value<string>() == "lamp-a",
                "Identical objectContext should reuse its first full definition even when the surrounding action changed");
            var altered = (JObject)changed.DeepClone(); altered["objectContext"]["selected"]["entityId"] = "fresh-cabinet";
            string distinct = source + "\n" + roomMarker + "\n" + new JObject { ["sceneActionFacts"] = altered }.ToString(Formatting.None);
            var distinctRoot = ReadRoot(SceneFactContextCompactor.Compact(distinct, out var distinctReport), roomMarker);
            Check(distinctReport.roundTripVerified && distinctRoot["sceneActionFacts"]["objectContext"][SceneFactContextCompactor.ReferenceKey] == null &&
                distinctRoot["sceneActionFacts"]["objectContext"]["selected"]["entityId"].Value<string>() == "fresh-cabinet",
                "Changed selected identity cannot inherit an older objectContext");
            string explanation = string.Concat(Enumerable.Repeat("Engine evidence is bounded to this observation; unknown is not success. ", 12));
            var described = (JObject)current.DeepClone();
            described.AddFirst(new JProperty("scope", explanation));
            described["lastAction"]["scope"] = explanation;
            described["lastAction"]["reason"] = explanation;
            described["objectContext"]["deviceEvidenceScope"] = explanation;
            described["objectContext"]["selected"]["scope"] = explanation + " A different restriction remains.";
            string describedText = SceneFactContextCompactor.Compact(marker + " " + described.ToString(Formatting.None), out var descriptionReport);
            JObject describedRoot = ReadRoot(describedText, marker);
            Check(descriptionReport.applied && descriptionReport.roundTripVerified &&
                describedRoot["lastAction"]["scope"][SceneFactContextCompactor.ReferenceKey]?.Type == JTokenType.String &&
                describedRoot["objectContext"]["deviceEvidenceScope"][SceneFactContextCompactor.ReferenceKey]?.Type == JTokenType.String &&
                (string)describedRoot["scope"] == explanation,
                "Long identical scope descriptions should share an earlier full value with verified expansion");
            Check((string)describedRoot["lastAction"]["reason"] == explanation &&
                (string)describedRoot["objectContext"]["selected"]["scope"] == explanation + " A different restriction remains.",
                "Description sharing changed current reason or merged a different restriction");
            string same = roomMarker + "\n" + new JObject { ["sceneActionFacts"] = current }.ToString(Formatting.None) + "\n" + source;
            string sameShared = SceneFactContextCompactor.Compact(same, out var sameReport);
            Check(sameReport.applied && sameReport.roundTripVerified && ReadRoot(sameShared, marker)[SceneFactContextCompactor.ReferenceKey]?.Type == JTokenType.String &&
                JToken.DeepEquals(ReadRoot(sameShared, roomMarker)["sceneActionFacts"]["lastAction"], current["lastAction"]),
                "Exactly duplicate outer snapshot was not shared from a fully inline first action result");
            var assessment = new JObject { ["canPlanRoute"] = false, ["budgetChecked"] = false, ["scope"] = new string('s', 800) };
            var room = new JObject { ["source"] = "Unity-room-runtime", ["phase"] = "failed", ["currentAssessment"] = assessment,
                ["currentFacts"] = new JObject { ["assessment"] = assessment.DeepClone(), ["bodyReserved"] = false } };
            string body = "[当前身体动作；程序事实]\n{\"phase\":\"idle\"}\n" + room.ToString(Formatting.None);
            string bodyShared = SceneFactContextCompactor.Compact(body, out var bodyReport);
            Check(bodyReport.applied && bodyReport.roundTripVerified && bodyShared.Contains("{\"phase\":\"idle\"}") && bodyShared.Contains("\"bodyReserved\":false") &&
                bodyShared.Contains("\"canPlanRoute\":false"), "Room details lost current status or modified independent upper-body result");
            var timing = new ChatRequestWireTiming(7, "formal");
            timing.MarkContent(.1); timing.MarkSent(.4, 10.4, "fixed UTC"); timing.MarkSent(.8, 11, "overwrite");
            timing.MarkBytes(.6); timing.MarkBytes(.9); timing.MarkContent(2.4); timing.MarkSpeech(2.6); timing.MarkComplete(3.4);
            JObject wire = timing.Snapshot("completed", 200, "Success");
            Check(Math.Abs((double)wire["httpToFirstContentSeconds"] - 2) < .00001 && Math.Abs((double)wire["httpToCompletionSeconds"] - 3) < .00001 &&
                (double)wire["preSendPreparationSeconds"] == .4 && (double)wire["httpSentRealtimeSeconds"] == 10.4, "HTTP measurement included preparation time or changed original boundary");
            var cancelled = new ChatRequestWireTiming(8, "work-feedback"); cancelled.MarkSent(1, 20, "fixed UTC"); cancelled.MarkBytes(double.NaN); cancelled.MarkComplete(2);
            JObject cancel = cancelled.Snapshot("superseded", 0, "ConnectionError");
            Check(cancel["httpToFirstContentSeconds"].Type == JTokenType.Null && cancel["httpToFirstBytesSeconds"].Type == JTokenType.Null && (double)cancel["httpToCompletionSeconds"] == 1, "Cancellation or missing metrics became zero-content evidence");
            LastReport["example"] = JObject.FromObject(report); LastReport["passed"] = true;
        }
        catch (Exception error) { LastReport["passed"] = false; LastReport["error"] = error.ToString(); throw; }
        finally { LastReport["checks"] = checks; }
        return checks;
    }
}
