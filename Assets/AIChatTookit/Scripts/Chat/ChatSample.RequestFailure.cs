using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // Independent of the body lease: a routing failure must neither cancel an
    // earlier action nor borrow that action's successful outcome.
    private JObject m_FirstReplyFailure;

    private void RecordFirstReplyFailure(int generation,int epoch,string reason)
    {
        if(generation!=m_FormalResponseGeneration||epoch!=m_WorkEpoch)return;
        bool budget=(reason??"").IndexOf("budget",StringComparison.Ordinal)>=0;
        m_FirstReplyFailure=new JObject {
            ["generation"]=generation,["workEpoch"]=epoch,["userTurn"]=m_ActionConstraints.CurrentUserTurn,
            ["request"]=m_ActionConstraints.CurrentUserText,["observedUtc"]=DateTime.UtcNow.ToString("o"),
            ["intent"]="request-processing",["entityId"]="request:"+m_ActionConstraints.CurrentUserTurn,
            ["stage"]=budget?"classification-preflight":"request-classification",
            ["kind"]=budget?"input-budget-exceeded":"classification-unresolved",["reason"]=reason,
            ["classificationCompleted"]=false,["scenePlannerDispatched"]=false,
            ["newActionSubmittedByThisDecision"]=false,
            ["scope"]="This request could not pass classification. No new action was dispatched by it; earlier actions and results remain separate. Not an unclear-user verdict, executor failure, network diagnosis, or retry permission."
        };
        RememberRoomPlanningFailure(m_FirstReplyFailure);
        Debug.Log("[Request/Failure] "+m_FirstReplyFailure.ToString(Newtonsoft.Json.Formatting.None),this);
    }

    private static string FirstReplyFailureText(JObject failure,string language)
    {
        bool budget=(string)failure?["kind"]=="input-budget-exceeded";
        if(language=="ja")return budget
            ?"今回の依頼は、処理する情報量が上限を超えてしまったの。新しい操作はまだ始めていないわ。"
            :"今回の依頼の処理を確定できなかったの。新しい操作はまだ始めていないわ。";
        if(language=="en")return budget
            ?"I couldn't process this request within the information limit. I haven't started any new operation."
            :"I couldn't resolve how to process this request. I haven't started any new operation.";
        return budget?"处理这次请求时，信息量超出了处理范围。我还没有开始新的操作。"
            :"我没能确认这次请求的处理方式，还没有开始新的操作。";
    }
}
