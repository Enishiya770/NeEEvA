using System.Linq;
using Newtonsoft.Json.Linq;

// Report-only view. Never used to authorize an action or mutate resident memory.
public static class ResultDeliveryContextPolicy
{
    public static JObject Execution(JObject source)
    {
        if(source==null)return null;
        var result=RoomTaskContextPolicy.DeliveryReceipt(source);
        var room=result["currentRoomFacts"] as JObject;
        if(room?["currentFacts"] is JObject current)
            foreach(string key in new[]{"actualRoot","userHeadWorld"})
                if(RoomTaskContextPolicy.NumericDiagnostic(current[key]))current.Remove(key);
        // Interface availability is a planning concern. Unknown additions must
        // survive this report projection, including constraints inside that node.
        if(room?["controlEntryPoints"] is JObject entry && entry.Properties().All(p=>
            new[]{"spokenTaskPlannerAvailable","sceneActionsAvailable","sameRoomControllerAsEditorButtons","scope"}.Contains(p.Name)))room.Remove("controlEntryPoints");
        return result;
    }

    public static bool PreferReceiptReview(JObject semantics,JArray receipts,bool answerRequired) =>
        !answerRequired && (string)semantics?["requestKind"]=="action" &&
        (string)semantics?.SelectToken("questionUnderstanding.needsEvidence")=="memory-or-direction" &&
        receipts!=null && receipts.OfType<JObject>().Any();
}
