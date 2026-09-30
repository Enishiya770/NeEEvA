using System;

/// <summary>A swappable decision source; accepting a proposal does not execute it.</summary>
public interface IFastLoopDecisionBackend
{
    bool SupportsFastLoopDecisions { get; }
    void PostFastLoopDecision(string context, string currentUser,
        Action<FastLoopDecision, string> callback, ConversationTrace trace = null, string imageDataUrl = null);
    void CancelFastLoopDecision();
}

/// <summary>Optional history ownership for providers shared with the legacy
/// planner. An accepted input survives cancellation and is claimed once on handoff.</summary>
public interface IFastLoopHistoryBackend
{
    void BeginFastLoopHistory(string prompt, string imageUrl, bool commitHistory);
    void CompleteFastLoopHistory();
}

/// <summary>Rebind the exact previously accepted user object after an invalid
/// interrupt. Text equality alone cannot authorize history reuse.</summary>
public interface IFastLoopRecoveryHistoryBackend
{
    bool ResumeFastLoopHistory(LLM.SendData acceptedUser, string freshImageUrl);
}

/// <summary>Independent present-tense body-task termination judgment. A result
/// never owns or cancels a controller; callers must still bind its task identity.</summary>
public interface IFastTaskControlBackend
{
    void PostFastTaskControlDecision(string context, string currentUser,
        Action<string, string> callback, ConversationTrace trace = null);
}
