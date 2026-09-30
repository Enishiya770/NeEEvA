/// <summary>Delivery facts only; neither an empty queue nor a filler proves a formal reply.</summary>
public enum DeliveryDisposition { Played, Quiet, Suppressed, AwaitingValidation, Rejected, Undelivered }

public static class DeliveryDispositionRules
{
    public static DeliveryDisposition Classify(bool formalPlaybackCompleted, bool awaitingValidation,
        bool rejected, bool withheld, bool intentionalQuiet, bool repeatSuppressed)
    {
        if (awaitingValidation) return DeliveryDisposition.AwaitingValidation;
        if (rejected) return DeliveryDisposition.Rejected;
        if (withheld) return DeliveryDisposition.Undelivered;
        if (formalPlaybackCompleted) return DeliveryDisposition.Played;
        if (repeatSuppressed) return DeliveryDisposition.Suppressed;
        if (intentionalQuiet) return DeliveryDisposition.Quiet;
        return DeliveryDisposition.Undelivered;
    }

    // Quiet/suppressed closes scheduling deliberately, without claiming anything was heard.
    public static bool ReleasesWait(DeliveryDisposition value) =>
        value == DeliveryDisposition.Played || value == DeliveryDisposition.Quiet || value == DeliveryDisposition.Suppressed;

    public static bool AllowsPendingGoalReview(bool pending, string scheduledReason) =>
        pending && (scheduledReason == "singing-goal-review" || scheduledReason == "singing-goal-review-retry");
}
