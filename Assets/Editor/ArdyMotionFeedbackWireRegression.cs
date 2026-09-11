using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Actual Qwen JSON construction, synthetic public history, no network requests.</summary>
public static class ArdyMotionFeedbackWireRegression
{
    private static int checks;
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void RunBatch()
    {
        var report = new JObject { ["scope"] = "Actual ChatQW serialization with public synthetic messages; no model or private scene." };
        GameObject host = null;
        try
        {
            host = new GameObject("InactiveFeedbackWireFixture"); host.SetActive(false);
            var model = host.AddComponent<ChatQW>(); model.m_Backend = ChatQW.BackendType.Local;
            model.ActiveSkillContext = "ordinary skill"; model.TrailingContext = "ordinary memory";
            model.SpokenPrefix = "already spoken prefix";
            var history = new List<LLM.SendData> {
                new LLM.SendData("system", "public role"),
                new LLM.SendData("user", "Wave both hands toward me."),
                new LLM.SendData("assistant", "I will try. <motion name=\"compose\" palm=\"partner\"/>") };
            const string context = "ordinary motion and speech contract";
            const string feedback = "Actual execution rejected palm; return one correction or an audible explanation.";
            var legacy = Build(model, "BuildRequestJsonForMessages", history, true, context);
            var messages = (JArray)legacy["messages"];
            Check(messages.Count == 7, "Legacy message count changed");
            Check((string)messages[1]["content"] == model.ActiveSkillContext &&
                (string)messages[2]["content"] == model.TrailingContext && (string)messages[3]["content"] == context,
                "Ordinary memory/contracts moved away from before-user position");
            Check((string)messages[4]["role"] == "user" && (string)messages[6]["content"] == model.SpokenPrefix,
                "Normal user/prefix behavior changed");
            var revised = Build(model, "BuildRequestJsonForMessagesWithFeedback", history, true, context, feedback);
            var revisedMessages = (JArray)revised["messages"];
            Check(revisedMessages.Count == 7 && (string)revisedMessages[6]["role"] == "system" &&
                (string)revisedMessages[6]["content"] == feedback, "Execution feedback is not last");
            Check((string)revisedMessages[5]["content"] == history[2].content, "Rejected reply was removed or rewritten");
            for (int i = 0; i < 6; i++) Check(JToken.DeepEquals(messages[i], revisedMessages[i]), "Ordinary history/context changed");
            Check((int)revised["id_slot"] == 0 && !(bool)revised["chat_template_kwargs"]["enable_thinking"],
                "Feedback changed local slot/thinking policy");
            Check(history.Count == 3 && history[2].content.Contains("palm="), "Transient feedback mutated stored conversation");
            var noFeedback = Build(model, "BuildRequestJsonForMessagesWithFeedback", history, true, context, null);
            Check(JToken.DeepEquals(legacy, noFeedback), "Empty feedback changed the legacy payload");
            var noUser = Build(model, "BuildRequestJsonForMessagesWithFeedback",
                new List<LLM.SendData> { new LLM.SendData("system", "public role") }, true, context, feedback);
            var noUserMessages = (JArray)noUser["messages"];
            Check((string)noUserMessages[noUserMessages.Count - 1]["content"] == feedback, "No-user fallback lost feedback");
            Check(noUserMessages.SelectTokens("$[?(@.role == 'user')]").GetEnumerator().MoveNext() == false,
                "Feedback invented a user message");
            report["passed"] = true;
        }
        catch (Exception error)
        {
            report["passed"] = false; report["error"] = error.ToString(); Debug.LogException(error);
        }
        finally
        {
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            report["checks"] = checks; report["checkedAtUtc"] = DateTime.UtcNow.ToString("o");
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Tools/MotionAdapter/reports/motion-feedback-wire-regression.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report.ToString() + "\n");
        }
        Debug.Log("[ArdyMotionFeedbackWireRegression] " + report["passed"] + " checks=" + checks);
        EditorApplication.Exit((bool)report["passed"] ? 0 : 1);
    }

    private static JObject Build(ChatQW model, string method, params object[] args)
        => JObject.Parse((string)typeof(ChatQW).GetMethod(method, Flags).Invoke(model, args));
    private static void Check(bool passed, string message)
    {
        checks++;
        if (!passed) throw new InvalidOperationException(message);
    }
}
