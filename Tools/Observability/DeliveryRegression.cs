using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

// Minimal host for the actual ChatSample.Delivery.cs. No microphone/server/Unity simulation.
public partial class ChatSample
{
    private ConversationTrace m_ResponseTrace;
    private bool pending, policy, deferred, CurrentMotionResponseRejected, m_ToolCorrectionExhaustedThisRound;
    private bool m_SingingSpeechWithheld, m_RoundSilencedForRepeat;
    private int m_SingingRejectedSpeechRound = -1, m_CurrentSpeechRoundId = 1;
    private bool HasPendingSingingGoalReview() => pending;
    private bool HasPendingSingingPolicyReview() => policy;
    private bool IsSingingPolicyActionDeferredThisRound() => deferred;
    private void ClearUserTurnAwaitingReply(string reason) { Released = true; ReleaseReason = reason; }
    public bool Released; public string ReleaseReason;
    public readonly List<string> Events = new List<string>();
    public string Close(bool played=false, bool awaiting=false, bool failed=false, bool withheld=false,
        bool quiet=false, bool repeat=false, bool pendingPolicy=false)
    {
        pending=awaiting;policy=pendingPolicy;deferred=false;CurrentMotionResponseRejected=false;
        m_ToolCorrectionExhaustedThisRound=failed;m_SingingSpeechWithheld=withheld;m_RoundSilencedForRepeat=repeat;
        Released=false;m_ResponseTrace=new ConversationTrace("test",Events.Add);m_DeliveryTrace=m_ResponseTrace.Child("delivery");
        ObserveDeliveryEnd("fixture",quiet,played);
        return (string)JObject.Parse(Events.Last())["fields"]["outcome"];
    }
    public bool OldReviewCannotClearNew()
    {
        var old=m_ResponseTrace.Child("old");
        var next=m_ResponseTrace.Child("new");
        m_GoalReviewTrace=next;
        EndGoalReviewTrace(old,"superseded");
        return ReferenceEquals(m_GoalReviewTrace,next);
    }
}

public static class DeliveryRegression
{
    private static int count;
    private static void Check(bool value,string label) { count++;if(!value)throw new Exception(label); }
    public static void Main()
    {
        ObservabilityRegression.Main();
        ClosureRegression.Run();
        var host=new ChatSample();
        Check(host.Close()=="Undelivered"&&!host.Released,"empty queue is not delivered");
        Check(host.Close(withheld:true)=="Undelivered"&&!host.Released,"withheld public text stays unanswered");
        Check(host.Close(awaiting:true,withheld:true,quiet:true)=="AwaitingValidation"&&!host.Released,"withheld empty response must not masquerade as chosen silence");
        Check(host.Close(failed:true,quiet:true)=="Rejected"&&!host.Released,"invalid protocol plus silent marker cannot clear wait");
        Check(host.Close(quiet:true)=="Quiet"&&host.Released&&host.ReleaseReason=="delivery-quiet","intentional silence ends wait explicitly");
        Check(host.Close(repeat:true,quiet:true)=="Suppressed"&&host.Released,"existing repeat suppression still terminates without claiming playback");
        Check(host.Close(played:true)=="Played"&&host.Released,"successful formal audio releases wait");
        Check(host.Close(played:true,awaiting:true)=="AwaitingValidation"&&!host.Released,"earlier audio does not complete a pending validation");
        Check(host.Close(pendingPolicy:true,quiet:true)=="AwaitingValidation"&&!host.Released,"pending policy does not masquerade as quiet");
        Check(host.OldReviewCannotClearNew(),"late old review cannot clear a newer diagnostic owner");
        Check(DeliveryDispositionRules.AllowsPendingGoalReview(true,"singing-goal-review"),"pending review can progress while user still awaits delivery");
        Check(!DeliveryDispositionRules.AllowsPendingGoalReview(true,"llm-requested"),"unrelated autonomous tick does not bypass wait");
        Check(!DeliveryDispositionRules.AllowsPendingGoalReview(false,"singing-goal-review"),"stale review reason cannot bypass wait");
        foreach(var error in new[]{"Request timeout","Operation timed out"})
            Check(AuxiliaryReply.TransportStatus(0,"ConnectionError",error)=="transport_timeout","explicit network timeout");
        Check(AuxiliaryReply.TransportStatus(0,"ConnectionError","Connection reset")=="transport_error","connection failure is not guessed to be a timeout");
        Check(AuxiliaryReply.TransportStatus(503,"ProtocolError","Service Unavailable")=="http_error","503 not model-format error");
        Check(AuxiliaryReply.OutputStatus("received",false,"","")=="invalid_envelope","bad envelope distinct from decision schema");
        Check(AuxiliaryReply.OutputStatus("received",true,"length","partial")=="output_truncated","truncated output distinct from empty");
        Check(AuxiliaryReply.OutputStatus("received",true,"stop","")=="empty_output","successful empty response");
        Check(AuxiliaryReply.OutputStatus("transport_timeout",false,"","")=="transport_timeout","parse failure cannot overwrite transport cause");
        Check(new AuxiliaryReply("bad JSON","received").InvalidDecisionStatus=="invalid_decision","only received output gets decision-format attribution");
        Check(new AuxiliaryReply("","transport_timeout").InvalidDecisionStatus=="transport_timeout","timeout survives consumer classification");
        Console.WriteLine("PASS: "+count+" delivery-state/real-helper/review-isolation/transport-cause checks");
    }
}
