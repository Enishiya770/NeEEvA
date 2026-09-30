using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

public static class FormalPromptBudgetRegression
{
    [MenuItem("Tools/NeEEvA/Run Formal Prompt Budget Regression")]
    public static void RunInteractive()
    {
        RunOrThrow();
        Debug.Log("[FormalPromptBudgetRegression] all cases passed");
    }

    public static void RunBatch()
    {
        try
        {
            RunOrThrow();
            Debug.Log("[FormalPromptBudgetRegression] all cases passed");
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            EditorApplication.Exit(1);
        }
    }

    private static void RunOrThrow()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
        var host = new GameObject("FormalPromptBudgetRegression");
        host.SetActive(false);
        try
        {
            var provider = host.AddComponent<ChatQW>();
            provider.m_LogRequestStats = false;
            provider.m_MaxPromptTokens = 10000;
            provider.m_FormalPromptTokens = 10000;
            provider.m_FormalHistoryLimit = 16;

            var messageForRequest = typeof(ChatQW).GetMethod("MessageForRequest", flags);
            var oldThought = "<thought>" + new string('x', 600) + "</thought><speech mode=\"independent\"><say>hello</say>";
            var oldAssistant = new LLM.SendData("assistant", oldThought);
            var historyWithThought = new List<LLM.SendData> {
                new LLM.SendData("system", "stable persona"),
                oldAssistant,
                new LLM.SendData("user", "later question")
            };
            var compacted = (LLM.SendData)messageForRequest.Invoke(null, new object[] { historyWithThought, 1 });
            Require(compacted != oldAssistant, "Historical assistant projection must be request-local.");
            Require(compacted.content.Length < oldThought.Length && compacted.content.Contains("<thought>"),
                "Historical thought was not bounded in the request projection.");
            Require(oldAssistant.content == oldThought, "Historical assistant content was mutated in place.");

            var current = new LLM.SendData("user", "current user input");
            var continuation = new LLM.SendData("assistant", "tool continuation");
            var view = new List<LLM.SendData> {
                new LLM.SendData("system", new string('s', 1800))
            };
            for (int i = 0; i < 18; i++)
            {
                view.Add(new LLM.SendData("user", new string('u', 900)));
                view.Add(new LLM.SendData("assistant", new string('a', 900)));
            }
            view.Add(current);
            view.Add(continuation);
            int originalCount = view.Count;
            var trim = typeof(ChatQW).GetMethod("TrimFormalHistoryToWireBudget", flags);
            var trimmed = (List<LLM.SendData>)trim.Invoke(provider, new object[] {
                view, "current facts", null, "active skill", null,
                provider.m_FormalPromptTokens, provider.m_FormalHistoryLimit
            });
            Require(trimmed.Contains(current) && trimmed.Contains(continuation),
                "Formal budget trimming removed the current turn or its continuation.");
            Require(trimmed.Count < originalCount, "Formal budget regression did not remove old history.");
            Require(view.Count == trimmed.Count, "Trim result was not applied to the request-local view.");

            var reportMethod = typeof(ChatQW).GetMethod("BuildPromptBudgetReport", flags);
            var report = reportMethod.Invoke(provider, new object[] {
                trimmed, "current facts", null, "active skill", null
            });
            int estimated = (int)report.GetType().GetField("estimatedTotal", flags).GetValue(report);
            Require(estimated <= provider.m_FormalPromptTokens,
                "Final formal wire estimate still exceeds the configured budget.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
