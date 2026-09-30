using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

public partial class ChatQW
{
    private static void CompleteRecordingCapabilityRoute(RecordingSingingDecisionContext context,JObject review)
    {
        // Audio operations still belong to the same adopted recording task.
        // Keep its detached intent through feedback and independent source review,
        // even if the short-lived FastLoop turn has already been retired.
        if((string)review?["operation"]=="catalog_search") context.OnLegacyHandoff?.Invoke();
    }
    private string BuildRecordingCapabilityRequestJson(string proposal, RecordingSingingDecisionContext context)
    {
        var sb=new StringBuilder(4096); sb.Append("{\"model\":"); AppendJsonString(sb,CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":384,\"response_format\":")
            .Append(RecordingCapabilityReview.Format(context).ToString(Formatting.None)).Append(",\"messages\":[");
        AppendMessage(sb,new SendData("system",RecordingCapabilityReview.Contract));sb.Append(',');
        AppendMessage(sb,new SendData("user",RecordingCapabilityReview.Facts(proposal,context).ToString(Formatting.None)));
        sb.Append(']');AppendReasoningControls(sb,false);AppendSlot(sb,k_SlotAuxiliary);sb.Append('}');return sb.ToString();
    }

    private IEnumerator RequestRecordingCapability(string proposal, RecordingSingingDecisionContext context, int generation,
        Action<JObject,string> complete,ConversationTrace trace)
    {
        if(generation!=m_StreamRequestGeneration) yield break;
        string wire=BuildRecordingCapabilityRequestJson(proposal,context);
        using(var request=new UnityWebRequest(url,"POST")) {
            m_ActiveStreamRequest=request;
            request.uploadHandler=new UploadHandlerRaw(Encoding.UTF8.GetBytes(wire));request.downloadHandler=new DownloadHandlerBuffer();request.timeout=20;
            request.SetRequestHeader("Content-Type","application/json");request.SetRequestHeader("Authorization","Bearer "+(string.IsNullOrEmpty(api_key)?"ollama":api_key));
            RaiseRequestDiagnostic(wire);
            if(generation!=m_StreamRequestGeneration) yield break;
            trace?.Mark("recording_capability.send",new JObject{["requestBytes"]=Encoding.UTF8.GetByteCount(wire),["scope"]="independent boundary only; no permission or execution"});
            yield return request.SendWebRequest();
            if(ReferenceEquals(m_ActiveStreamRequest,request)) m_ActiveStreamRequest=null;
            if(generation!=m_StreamRequestGeneration) yield break;
            JObject reviewed=null;string reason="capability-review-transport-failed";
            if(request.result==UnityWebRequest.Result.Success && request.responseCode==200 &&
                TryReadActorIntentEnvelope(request.downloadHandler?.text??"",out string raw,out _,out reason))
                RecordingCapabilityReview.TryRead(raw,proposal,context,out reviewed,out reason);
            trace?.Mark("recording_capability.review",new JObject{["accepted"]=reviewed!=null,["reason"]=reason,["decision"]=reviewed});
            if(generation==m_StreamRequestGeneration) complete(reviewed,reason);
        }
    }

    private IEnumerator ResolveRecordingCapability(string user,int generation,Action<string> onDelta,Action<string> onComplete,
        bool recordHistory,string transient,Action<SpeechText> onSpeech,string feedback,string image,
        string proposal,RecordingSingingDecisionContext context,ConversationTrace trace)
    {
        if(context.CapabilityReviewAttempted) {
            RaiseOutputFormatError("RecordingSingingDecision 表外移交已核验一次但仍未落实；本轮没有公开承诺、没有提交动作。请据完整当前目标作具体澄清，不能重新走General猜选素材。");
            if(generation==m_StreamRequestGeneration) onComplete?.Invoke("<silent/>");yield break;
        }
        context.CapabilityReviewAttempted=true;
        JObject review=null;string failure=null;
        yield return RequestRecordingCapability(proposal,context,generation,(r,e)=>{review=r;failure=e;},trace);
        if(generation!=m_StreamRequestGeneration) yield break;
        if(review==null || (string)review["verdict"]=="uncertain") {
            RaiseOutputFormatError("RecordingSingingDecision 能力边界尚未确认："+(failure??"uncertain")+"；未公开承诺、未提交动作，保留完整角色目标和音源证据。");
            if(generation==m_StreamRequestGeneration) onComplete?.Invoke("<silent/>");yield break;
        }
        if((string)review["verdict"]=="supported_here") {
            context.CapabilityReviewFacts=review;RecordingDecisionContext=context;
            trace?.Mark("recording_capability.return_to_domain");
            yield return RequestStageStreamWithObservation(user,generation,onDelta,onComplete,recordHistory,transient,onSpeech,feedback,image,FormalResponseStage.RecordingSingingDecision);
            yield break;
        }
        string candidate=null;
        string adapter="\n[已独立核验的能力候选；不公开、不执行]\n"+review.ToString(Formatting.None)+
            "\n只为该operation生成现有工具候选。不得公开台词或承诺，不改完整角色目标、sources顺序或当前输入。catalog_search仅song_search/song_catalog只读查询；其它只生成sing_goal+sing及必要clip_revise，源确认仍交执行器。所有录音goal必须携原current_input的input_turn/input_capture/input_clip、source_relation、source_evidence；自定义坐标仍需独立目标审核，不杜撰范围。";
        // Buffer every channel. General's vocabulary remains available, but its
        // unreviewed words and actions never reach the user or scene callbacks.
        yield return RequestStageStreamWithObservation(user,generation,null,value=>candidate=value,false,
            transient+RecordingSingingDecisionCompiler.HandoffFacts(proposal,context)+adapter,null,feedback,image,FormalResponseStage.General);
        if(generation!=m_StreamRequestGeneration) yield break;
        bool valid=RecordingCapabilityReview.TryConstrainCandidate(candidate??"",review,context,out string compiled,out string reason);
        trace?.Mark("recording_capability.candidate",new JObject{["accepted"]=valid,["reason"]=reason,["operation"]=review["operation"],["scope"]="private candidate; independent goal and source checks still required"});
        if(!valid) {
            RaiseOutputFormatError("RecordingSingingDecision 表外候选未通过能力/音源约束："+reason+"；本次未公开承诺、未提交动作。");
            if(generation==m_StreamRequestGeneration) onComplete?.Invoke("<silent/>");yield break;
        }
        CompleteRecordingCapabilityRoute(context,review);
        if(generation!=m_StreamRequestGeneration) yield break;
        if(recordHistory) m_DataList.Add(new SendData("assistant",compiled));
        onComplete?.Invoke(compiled);
    }
}
