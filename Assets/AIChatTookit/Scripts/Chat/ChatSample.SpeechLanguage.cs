using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private System.Collections.IEnumerator AwaitSpeechLanguage(PendingSpeechChunk chunk,int generation)
    {
        var unit=chunk.ProcessUnit;
        while(unit?.LanguageCheckPending==true && Time.realtimeSinceStartup-unit.LanguageCheckStartedAt<3.25f) {
            if(generation!=m_FormalResponseGeneration || m_SilencedSpeechRoundIds.Contains(chunk.RoundId))yield break;
            yield return null;
        }
        // A timeout settles this metadata lease. A later callback cannot change
        // the tag after the TTS provider has begun preparing the same words.
        if(unit!=null)unit.LanguageCheckPending=false;
    }

    private void BeginSpeechLanguageCheck(ActivityMonitorProtocol.SpeechUnit unit)
    {
        var model=m_ChatSettings?.m_ChatModel;
        if(model?.SupportsSpeechLanguageCheck!=true || !SpeechLanguagePolicy.NeedsCheck(unit.Text,unit.Language))return;
        int generation=m_FormalResponseGeneration,version=unit.Version;string text=unit.Text,declared=unit.Language;
        unit.LanguageCheckPending=true;unit.LanguageCheckStartedAt=Time.realtimeSinceStartup;
        try { model.PostSpeechLanguage(text,(ok,actual,status)=>{
            bool current=generation==m_FormalResponseGeneration && version==unit.Version && text==unit.Text &&
                model==m_ChatSettings?.m_ChatModel && !m_UserSpeechActiveForAutonomy && isActiveAndEnabled && unit.LanguageCheckPending;
            if(!current)return;
            unit.LanguageCheckPending=false;
            bool applied=ok && SpeechText.NormalizeLanguage(actual)!=null && unit.State=="queued";
            if(applied)unit.Language=actual;
            Debug.Log("[Speech/LanguageBinding] "+new JObject{["speechId"]=unit.Id,["declared"]=declared,["actual"]=actual,
                ["applied"]=applied,["status"]=status,["scope"]="Metadata only; mixed/unknown retain original span; text and action permission unchanged."}.ToString(Formatting.None),this);
        }); } catch(System.Exception error) {
            unit.LanguageCheckPending=false;
            Debug.LogWarning("[Speech/LanguageBinding] failed="+error.GetType().Name+" speechId="+unit.Id,this);
        }
    }
}
