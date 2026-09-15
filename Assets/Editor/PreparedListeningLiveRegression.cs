using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Real Qwen, synthetic text and recorder evidence. This does not measure microphone EOU.
public static class PreparedListeningLiveRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(JObject roleConfig, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Live listening checks require the isolated project.");
        string directory = Path.Combine(output, "listening-live"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(),
            ["scope"] = "Actual role prompt files and shared Qwen. Synthetic complete/incomplete utterances and recorder facts; no microphone, no audible draft, no actions or persistent user history. Lease reuse is controlled, not a real Play hit rate." };
        var host = new GameObject("Isolated live listening model");
        var model = host.AddComponent<ChatQW>();
        try
        {
            foreach (var item in roleConfig.Properties())
            {
                var field = FindField(item.Name); if (field == null) continue;
                object value = item.Name == "m_PromptFiles" || item.Name == "m_SkillFiles"
                    ? ((JArray)item.Value).Select(x => AssetDatabase.LoadAssetAtPath<TextAsset>((string)x["path"])).ToArray()
                    : item.Value.ToObject(field.FieldType);
                field.SetValue(model, value);
            }
            model.m_Backend = ChatQW.BackendType.Local;
            FindField("url").SetValue(model, model.m_LocalUrl);
            model.InitSystemMessage(); model.SetActiveSkills("singing"); model.m_LogRequestStats = true;
            if (!model.HasPromptFiles) throw new InvalidOperationException("Actual role prompts are required.");
            string[] inputs = { "晚上好。", "我刚才说错了，不要", "先别唱歌，只聊天。", "停下。",
                "没事了，谢谢你。", "等一下，我想让你", "それはやめてください。", "If I change my mind," };
            bool[] expectedClosed = { true, false, true, true, true, false, true, false };
            for (int i = 0; i < inputs.Length; i++)
            {
                string prompt = PreparedListeningProtocol.BuildPrompt(inputs[i]);
                string wire = (string)typeof(ChatQW).GetMethod("BuildListeningPreparationRequestJson", Flags).Invoke(model, new object[] { prompt });
                File.WriteAllText(Path.Combine(directory, "case-" + i + ".request.json"), wire);
                string response = null; float started = Time.realtimeSinceStartup;
                var source = Evidence(inputs[i], i + 1, started);
                model.ListeningPreparationRequestId = "live-fixture-" + i;
                model.PostListeningPreparationMsg(prompt, value => response = value ?? "");
                while (response == null && Time.realtimeSinceStartup - started < 12f) yield return null;
                float elapsed = Time.realtimeSinceStartup - started;
                if (response == null) model.CancelEphemeralMsg();
                bool parsed = false; JObject result = null;
                try { result = JObject.Parse(response ?? ""); parsed = true; } catch { }
                var boundary = result?["quiet_boundary"] as JObject;
                bool closed = boundary != null && (string)boundary["turn_state"] == "closed" &&
                    new[] { "complete", "take_turn", "ask_user" }.Contains((string)boundary["action"]);
                bool expected = expectedClosed[i] ? closed : boundary != null &&
                    (string)boundary["action"] == "continue" && (string)boundary["turn_state"] == "open";
                PreparedTurnBoundary candidate; string reason;
                bool ready = PreparedTurnBoundary.TryCreate(response ?? "", source, 1, started,
                    Time.realtimeSinceStartup, out candidate, out reason, "live-fixture-" + i);
                bool reusable = false;
                if (ready)
                {
                    var mature = source; mature.SampledAt = Time.realtimeSinceStartup;
                    mature.QuietSeconds = Math.Max(0.75f, elapsed + 0.05f);
                    reusable = candidate.CanReuse(mature, 1, Time.realtimeSinceStartup, out reason);
                }
                ((JArray)LastReport["cases"]).Add(new JObject { ["input"] = inputs[i], ["seconds"] = elapsed,
                    ["response"] = response, ["parsed"] = parsed, ["expectedClosed"] = expectedClosed[i], ["expectedSemanticBoundary"] = expected,
                    ["controlledLeaseReady"] = ready, ["controlledLeaseReusable"] = reusable,
                    ["leaseReason"] = reason, ["timeout"] = response == null });
                File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString());
                if (response == null) break;
            }
            LastReport["status"] = "completed";
            LastReport["passed"] = ((JArray)LastReport["cases"]).Count == inputs.Length &&
                ((JArray)LastReport["cases"]).All(x => (bool)x["parsed"] && (bool)x["expectedSemanticBoundary"]);
        }
        finally
        {
            model.CancelEphemeralMsg(); Object.DestroyImmediate(host);
            File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString());
        }
    }

    private static PreparedTurnBoundaryEvidence Evidence(string text, int capture, float now) =>
        new PreparedTurnBoundaryEvidence { CaptureSerial = capture, TextRevision = 1, ActivityRevision = 1,
            AudioMs = 2000, Transcript = text, SampledAt = now, LatestSoundAt = now - 0.05f,
            QuietSeconds = 0.05f, MinimumQuietSeconds = 0.7f, TailAge = 0.02f, MinimumConfidence = 0.78f,
            Recording = true, AsrHealthy = true, FreshCoverage = true, CoversQuietTail = true, TailAvailable = true };

    private static FieldInfo FindField(string name)
    {
        for (Type type = typeof(ChatQW); type != null; type = type.BaseType)
        { var field = type.GetField(name, Flags); if (field != null) return field; }
        return null;
    }
}
