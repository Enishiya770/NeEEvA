using System;

/// <summary>Interprets unpublished character drafts and separate new user condition events.</summary>
public interface IActorIntentBackend
{
    bool SupportsActorIntents { get; }
    void PostActorIntent(string context, string draft, string currentUser,
        Action<ActorIntentDecision, string> callback, ConversationTrace trace = null);
    void PostActorConditionSignal(string context, string currentUser,
        Action<string, string> callback, ConversationTrace trace = null);
    void CancelActorIntent();
}

/// <summary>Optional one-shot semantic review. Its result is not an execution receipt.</summary>
public interface IActorIntentReviewBackend
{
    void PostActorIntentReview(string context, string draft, string currentUser,
        Action<ActorIntentReviewDecision, string> callback, ConversationTrace trace = null);
}
