using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private JObject BuildFastLoopTurnContext(FastLoopTurnContext turn)
    {
        var facts = JObject.Parse(BuildFastLoopContext());
        if (turn != null && m_RecoveredInputGeneration == turn.Generation)
            facts["inputRecovery"] = BuildCompactInputRecoveryFacts(turn);
        return facts;
    }

    // A similarity observation can request a fresh decision, never create an
    // action or override a deliberate refusal/repetition requested by the user.
    private bool TryReviewRepeatedFastDraft(FastLoopTurnContext turn, FastLoopDecision candidate,
        string currentUser, string taskId, int taskRevision)
    {
        if (!FastLoopTurnCurrent(turn) || turn.DialogueRepairRequested ||
            !(turn.Model is IFastLoopDecisionBackend backend) || !backend.SupportsFastLoopDecisions) return false;
        string prior = RepeatedCompletedFastSpeech(turn, candidate.Speech);
        if (prior == null) return false;
        turn.DialogueRepairRequested = true;
        var facts = BuildRepeatedFastDraftRecoveryFacts(turn);
        facts["dialogueRepair"] = new JObject {
            ["reason"] = "long-repeated-draft", ["previouslySpoken"] = prior,
            ["maxAttempts"] = 1,
            ["scope"] = "Text similarity only, not an intention verdict. Reconsider the complete current input and real task/material facts. " +
                "Reply to the current correction, question, remaining goal or end of test. The old wording is not a suggested reply. " +
                "Select reply_basis=requested_repetition only for a current request to repeat/quote, with that exact request in reply_evidence. " +
                "A complaint about repetition is not such a request. Otherwise select current_input and quote the current focus. No action has been accepted or executed."
        };
        bool consumed = false;
        var trace = m_ResponseTrace?.Child("dialogue-repair");
        trace?.Mark("fast_loop.dialogue_repair_requested", new JObject {
            ["generation"] = turn.Generation, ["draftCharacters"] = candidate.Speech.Length,
            ["previousCharacters"] = prior.Length, ["attempt"] = 1, ["published"] = false });
        void Resolve(FastLoopDecision answer, string status) {
            if (consumed || !FastLoopTurnCurrent(turn)) return;
            consumed = true;
            bool valid = status == "ok" && answer != null && FastLoopProtocol.Validate(answer, currentUser, out _);
            bool repeated = valid && answer.Route == "reply" && RepeatedCompletedFastSpeech(turn, answer.Speech) != null;
            bool requested = repeated && answer.ReplyBasis == "requested_repetition" &&
                !string.IsNullOrWhiteSpace(answer.ReplyEvidence) &&
                currentUser.IndexOf(answer.ReplyEvidence, StringComparison.Ordinal) >= 0;
            trace?.Mark("fast_loop.dialogue_repair_completed", new JObject {
                ["status"] = status, ["route"] = answer?.Route,
                ["stillSimilar"] = repeated, ["replyBasis"] = answer?.ReplyBasis,
                ["accepted"] = valid && (!repeated || requested),
                ["scope"] = "One fresh role choice; usual intention, target and execution gates still apply." });
            if (!FastLoopTurnCurrent(turn)) return;
            if (!valid || repeated && !requested)
            {
                CompleteFastReplyRecoveryFailure(turn, answer?.Language ?? candidate.Language,
                    valid ? "dialogue-repair-still-repeating" : "dialogue-repair-unavailable");
                return;
            }
            ApplyFastLoopAnswer(turn, answer, currentUser, taskId, taskRevision, trace);
        }
        PrepareFastReplyVision(turn, facts, "dialogue-repair", trace, image => {
            if (!FastLoopTurnCurrent(turn) || consumed) return;
            try { backend.PostFastLoopDecision(facts.ToString(Formatting.None), currentUser, Resolve, trace, image); }
            catch (Exception error) { if (consumed) throw; Resolve(null, "dialogue-repair-exception:" + error.GetType().Name); }
        });
        return true;
    }

    private string RepeatedCompletedFastSpeech(FastLoopTurnContext turn, string speech)
    {
        string result = null;
        foreach (var completed in m_CompletedRoleReplies)
        {
            if ((int?)completed["generation"] == turn.Generation && (int?)completed["speechRound"] == turn.Round) continue;
            string text = (string)completed["text"];
            if (FastLoopDialogueRepair.IsLongRepeat(speech, text)) result = text;
        }
        return result;
    }

    private JObject BuildFastReplyRecoveryFacts(FastLoopTurnContext turn)
    {
        // A denied control/invalid decision does not invalidate the conversation.
        // In particular, short answers need the last actually spoken question.
        // Retain completed speech, its user-turn binding and unfinished goals.
        return BuildFastLoopTurnContext(turn);
    }

    private JObject BuildRepeatedFastDraftRecoveryFacts(FastLoopTurnContext turn)
    {
        var facts = BuildFastReplyRecoveryFacts(turn);
        // Keep user history and executor facts. Repeated assistant wording is
        // supplied once, as labelled evidence, rather than several demonstrations.
        if (facts["recentDialogue"] is JObject dialogue && dialogue["turns"] is JArray turns)
            foreach (var row in turns)
                if (row is JObject value)
                { value.Remove("completedAssistantSpeech"); value["assistantWordingOmittedForRepair"] = true; }
        return facts;
    }

    private void CompleteFastReplyRecoveryFailure(FastLoopTurnContext turn, string language, string reason)
    {
        if (!FastLoopTurnCurrent(turn)) return;
        m_PendingToolCorrectionFrame = "[本轮回答恢复未完成] " + reason + "；没有提交新动作，不能描述为动作执行失败；后续输入保留真实任务状态。";
        m_ResponseTrace?.Mark("fast_loop.reply_recovery_failed", new JObject { ["generation"] = turn.Generation, ["reason"] = reason });
        if (!FastLoopTurnCurrent(turn)) return;
        CompleteOwnedReplyFailure(turn.Model, turn.Prompt, language, turn.Generation, turn.RecordHistory,
            "fast-reply-recovery:" + reason, false);
    }
}

public static class FastLoopDialogueRepair
{
    public static bool IsLongRepeat(string candidate, string completed)
    {
        string left = Normalize(candidate), right = Normalize(completed);
        if (left.Length < 32 || right.Length < 32) return false;
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;
        // Multiset bigrams tolerate small apology/punctuation changes. This is
        // only a review trigger: it does not assert equal meaning or reject text.
        var pairs = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 1; i < left.Length; i++)
        {
            string pair = left.Substring(i - 1, 2);
            pairs.TryGetValue(pair, out int count); pairs[pair] = count + 1;
        }
        int shared = 0;
        for (int i = 1; i < right.Length; i++)
        {
            string pair = right.Substring(i - 1, 2);
            if (pairs.TryGetValue(pair, out int count) && count > 0)
            { shared++; pairs[pair] = count - 1; }
        }
        return 2.0 * shared / (left.Length + right.Length - 2) >= .92;
    }

    private static string Normalize(string text)
    {
        var result = new StringBuilder();
        foreach (char value in text ?? "")
            if (char.IsLetterOrDigit(value)) result.Append(char.ToLowerInvariant(value));
        return result.ToString();
    }
}
