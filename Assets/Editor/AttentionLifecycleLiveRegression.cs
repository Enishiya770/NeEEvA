using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Explicitly selected local-model probe. It creates no ChatSample, executor,
// microphone or audio source. Use production public planning/review entrypoints.
public static class AttentionLifecycleLiveRegression
{
    public static IEnumerator Run(string source, string output)
    {
        var spec=JObject.Parse(File.ReadAllText(source));
        var report=new JObject { ["passed"]=false, ["scope"]="Recorded production facts and complete dialogue, controlled completed Unity state; real local model and full words-only plus factual-review pipeline. No physical action or audible playback.", ["cases"]=new JArray() };
        var host=new GameObject("LifecycleLiveModel");host.SetActive(false);
        var model=host.AddComponent<ChatQW>();model.enabled=false;
        model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";
        model.m_LocalModelName=(string)spec["model"];model.m_PrewarmPrefixOnStart=false;model.m_LogRequestStats=true;
        host.SetActive(true);
        typeof(LLM).GetField("url",BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public).SetValue(model,model.m_LocalUrl);
        try {
            foreach(JObject row in spec["cases"]) {
                bool done=false,ok=false;string text=null,status=null;float start=Time.realtimeSinceStartup;
                string input=row["input"].ToString();
                var flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
                string method=(string)row["kind"]=="control"?"BuildRoomPreparationRequestJson":"BuildRoomTaskRequestJson";
                object[] arguments=(string)row["kind"]=="control"?new object[]{input}:new object[]{input,(string)row["kind"]=="review"};
                string wire=(string)typeof(ChatQW).GetMethod(method,flags).Invoke(model,arguments);
                File.WriteAllText(Path.Combine(output,(string)row["name"]+".request.json"),wire);
                Action<bool,string,string> callback=(a,b,c)=>{ok=a;text=b;status=c;done=true;};
                if((string)row["kind"]=="control")model.PostRoomPreparationMessage(input,callback);
                else model.PostRoomTaskMessage(input,(string)row["kind"]=="review",callback);
                while(!done && Time.realtimeSinceStartup-start<45)yield return null;
                JObject value=null;try{if(!string.IsNullOrEmpty(text))value=JObject.Parse(text);}catch(Exception){}
                bool passed=done && ok==((bool?)row["expectedAccepted"]??true);
                if(row["expectedStatus"]!=null)passed &= status==(string)row["expectedStatus"];
                if(row["expectedVerdict"]!=null)passed &= (string)value?["verdict"]==(string)row["expectedVerdict"];
                if(row["expectedExplicitStop"]!=null)passed &= (bool?)value?["explicitStopNow"]==(bool)row["expectedExplicitStop"];
                if((string)row["kind"]=="review")passed &= status!="speech-claim-state-mismatch" &&
                    RoomSpeechClaimProtocol.Requirement((JObject)row["input"],out _)==null;
                ((JArray)report["cases"]).Add(new JObject { ["name"]=row["name"], ["passed"]=passed, ["accepted"]=ok,
                    ["status"]=status,["seconds"]=Time.realtimeSinceStartup-start,["output"]=value ?? (JToken)new JValue(text) });
                File.WriteAllText(Path.Combine(output,"lifecycle-live.json"),report.ToString());
            }
            bool all=true;foreach(JObject row in report["cases"])all &= (bool)row["passed"];
            report["passed"]=all;File.WriteAllText(Path.Combine(output,"lifecycle-live.json"),report.ToString());
            if(!all)throw new InvalidOperationException("Lifecycle local-model expectations failed; inspect lifecycle-live.json.");
        } finally {model.CancelRoomTaskPlanningMessage();model.CancelRoomPreparationMessage();UnityEngine.Object.DestroyImmediate(host);}
    }
}
