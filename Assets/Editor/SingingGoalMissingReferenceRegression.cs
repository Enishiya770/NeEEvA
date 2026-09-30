using System;
using System.Collections;
using System.Reflection;
using Newtonsoft.Json.Linq;

public static class SingingGoalMissingReferenceRegression
{
    public static JObject RunChecks()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var fixtureType = typeof(AgentSingingGoalRegression).GetNestedType("Fixture", BindingFlags.NonPublic);
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
        foreach (string version in new[] { "", " revisions=\"pending\"" })
        using (var fixture = (IDisposable)Activator.CreateInstance(fixtureType, true))
        {
            fixtureType.GetMethod("Begin", flags).Invoke(fixture, new object[] { "请把刚才的录音唱出来。" });
            var chat = (ChatSample)fixtureType.GetField("chat", flags).GetValue(fixture);
            var sense = (SenseVoiceSpeechToText)fixtureType.GetField("sense", flags).GetValue(fixture);
            string first = (string)fixtureType.GetField("first", flags).GetValue(fixture);
            string second = (string)fixtureType.GetField("second", flags).GetValue(fixture);
            var before = JArray.FromObject(sense.DescribePracticePhrases());
            const string invalid = "clip:5226615a080f";
            object[] args = { "<sing_goal refs=\"" + invalid + "\"" + version + " expected=\"回唱刚才的录音\"/>", true };
            typeof(ChatSample).GetMethod("ExtractSingingGoalTags", flags).Invoke(chat, args);
            string failure = (string)typeof(ChatSample).GetField("m_LastPracticeEditResult", flags).GetValue(chat);
            Check(failure.Contains("录音素材不存在") && failure.Contains(invalid) &&
                failure.Contains("说话人编号不是录音ref"), "Unknown recording was misreported as a revision formatting error.");
            Check(failure.Contains(first) && failure.Contains(second) && failure.Contains("不代表"),
                "Correction lacked real candidate identities or promoted availability into singing approval.");
            Check(typeof(ChatSample).GetField("m_SingingGoal", flags).GetValue(chat) == null,
                "Unknown recording created a pending goal.");
            // AgoSeconds is elapsed observation time, so compare stable identity/version/approval only.
            var after = JArray.FromObject(sense.DescribePracticePhrases());
            Check(before.Count == after.Count, "Invalid reference prepared or dropped a material.");
            for (int i = 0; i < before.Count; i++)
                Check(JToken.DeepEquals(before[i]["ClipRef"], after[i]["ClipRef"]) &&
                    JToken.DeepEquals(before[i]["Revision"], after[i]["Revision"]) &&
                    JToken.DeepEquals(before[i]["PendingConfirmation"], after[i]["PendingConfirmation"]),
                    "Invalid reference changed material identity, version or source approval.");
        }
        return new JObject { ["passed"] = true, ["checks"] = checks };
    }
}
