using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private JObject m_DeliveryDeferredIntent;
    private LLM m_DeliveryDeferredModel;
    private int m_DeliveryDeferredProbe = -1;

    private void RememberAfterDeliveryIntent(AutonomyIntentDecision decision)
    {
        if(decision?.next_intent_source!="autonomous" || !decision.proceed || string.IsNullOrWhiteSpace(decision.intent))return;
        m_DeliveryDeferredIntent=new JObject{["workEpoch"]=m_WorkEpoch,["userTurn"]=m_ActionConstraints.CurrentUserTurn,
            ["intent"]=decision.intent,["planningFocus"]=decision.planning_focus,["actionAuthorized"]=false,
            ["scope"]="Optional earlier wish; freely reconsider against current facts/limits. Not completed work or action authority."};
        m_DeliveryDeferredModel=m_ChatSettings?.m_ChatModel;m_DeliveryDeferredProbe=-1;
    }

    private JObject ReadAfterDeliveryIntent()
    {
        if(m_DeliveryDeferredIntent==null)return null;
        if((int?)m_DeliveryDeferredIntent["workEpoch"]!=m_WorkEpoch ||
            (int?)m_DeliveryDeferredIntent["userTurn"]!=m_ActionConstraints.CurrentUserTurn ||
            m_DeliveryDeferredModel!=m_ChatSettings?.m_ChatModel ||
            m_DeliveryDeferredProbe>=0&&m_DeliveryDeferredProbe!=m_AutonomyProbeGeneration) {
            m_DeliveryDeferredIntent=null;m_DeliveryDeferredModel=null;return null;
        }
        if(m_WorkReviewOpen)return null;
        // Both capture and send rebuild the same private frame. Offer the wish
        // to that one new probe, never repeatedly relaunch it on later ticks.
        m_DeliveryDeferredProbe=m_AutonomyProbeGeneration;
        return (JObject)m_DeliveryDeferredIntent.DeepClone();
    }
}
