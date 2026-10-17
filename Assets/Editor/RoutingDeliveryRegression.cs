using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class RoutingDeliveryRegression
{
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static readonly JArray cases=new JArray();static JObject fixture,failureForReplay;static string directory;
    static object Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,Flags).Invoke(o,args);
    static object Get(object o,string name){for(Type t=o.GetType();t!=null;t=t.BaseType){var f=t.GetField(name,Flags);if(f!=null)return f.GetValue(o);}throw new Exception(name);}
    static void Set(object o,string name,object value){for(Type t=o.GetType();t!=null;t=t.BaseType){var f=t.GetField(name,Flags);if(f!=null){f.SetValue(o,value);return;}}throw new Exception(name);}
    static IEnumerable<JObject> Events(string kind)=>((JArray)fixture["events"]).OfType<JObject>().Where(e=>(string)e["kind"]==kind);
    static JObject Data(int line)=>(JObject)((JArray)fixture["events"]).First(e=>(int)e["line"]==line)["data"].DeepClone();
    static void Save(bool done=false)=>File.WriteAllText(Path.Combine(directory,"routing-delivery.json"),new JObject{
        ["completed"]=done,["passed"]=done&&cases.All(c=>(bool)c["passed"]),["sha256"]=fixture["sha256"],
        ["actualDialogue"]=fixture["dialogue"],["cases"]=cases}.ToString());
    static void Check(string name,bool pass,JToken evidence=null){cases.Add(new JObject{["name"]=name,["passed"]=pass,["evidence"]=evidence});Save();}
    static List<LLM.SendData> History(int before)=>((JArray)fixture["dialogue"]).OfType<JObject>().Where(r=>(int)r["line"]<before)
        .Select(r=>new LLM.SendData((string)r["role"],(string)r["raw"]??(string)r["text"])).ToList();
    static JObject PriorFailure()=>(JObject)failureForReplay.DeepClone();
    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string output)
    {
        directory=output;cases.Clear();fixture=ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/RoutingDelivery.20261016-2111.json"));
        var host=new GameObject("Routing delivery native replay");host.SetActive(false);
        var model=host.AddComponent<ChatQW>();model.enabled=false;model.m_PrewarmPrefixOnStart=false;
        model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";Set(model,"url",model.m_LocalUrl);host.SetActive(true);
        try {
            FailureLifecycle(chat);Protocols(model);
            foreach(var entry in Events("LLM/FirstReplyUnadmittedFacts")) {
                var packet=(JObject)entry["expanded"].DeepClone();
                int userLine=(int)((JArray)fixture["dialogue"]).OfType<JObject>().Last(r=>(string)r["role"]=="user"&&(int)r["line"]<(int)entry["line"])["line"];
                yield return Route(model,packet,History(userLine),"recorded-unadmitted-"+entry["line"],"tools");
            }
            var current=((JArray)fixture["dialogue"]).OfType<JObject>().Single(r=>(int)r["line"]==180432);
            var initial=new JObject{["latestUserText"]=current["raw"],["currentUtterance"]=current["text"],
                ["runtimeContext"]=new JObject{["humanInput"]=new JObject{["primaryUtterance"]=current["text"]},
                    ["recordedPriorRecall"]=new JArray(Events("SceneRecall/Result").Where(e=>(int)e["line"]<180432).Select(e=>e["data"].DeepClone())),
                    ["scope"]="Reconstructed prefix from recorded dialogue and prior recall, not the unavailable exact gen11 wire; no later outcome included."}};
            foreach(int line in new[]{180750,180782}) {
                string rejected=(string)Data(line)["rawResponse"];
                Check("original-rejected-"+line,!FirstReplyProtocol.TryGroundedReview(rejected,(string)current["text"],out _,out _,out _,out _,out _,out _));
                var errors=FirstReplyProtocol.ReviewGroundingErrors(rejected,(string)current["text"]);
                Check("scene-field-diagnosed-"+line,errors.OfType<JObject>().Any(e=>(string)e["field"]=="sceneConstraintQuote"),errors);
                yield return Route(model,initial,History(180432),"original-gen11-bounded-repair-"+line,"tools",rejected);
            }
            foreach(var variant in new[]{
                new[]{"打开刚才那份文档，检索错误后修改并保存。","zh"},
                new[]{"Generate a summary of the document we found, then save it.","en"},
                new[]{"さっき見た棚をもう一度観察して、何が変わったか答えて。","ja"},
                new[]{"把刚才关闭的灯重新打开，不要动旁边的另一盏。","zh"},
                new[]{"Turn the same lamp back on and return to me.","en"},
                new[]{"先ほど消した同じランプをつけて、私のところに戻って。","ja"}}) {
                var packet=(JObject)Events("LLM/FirstReplyUnadmittedFacts").Last()["expanded"].DeepClone();
                packet["latestUserText"]=variant[0];packet["currentUtterance"]=variant[0];
                packet["runtimeContext"]["humanInput"]=new JObject{["primaryUtterance"]=variant[0],["fullInputEvidence"]=variant[0]};
                yield return Route(model,packet,History(196247),"general-route-"+variant[1]+"-"+variant[0],"tools");
            }
            foreach(var row in new[]{
                new[]{"あの、さっきのことはよくわからないんですけれど……大丈夫ですよ。ふふっ。さて、小灯りはちゃんと消しました。これで、無事にあなたの元へ戻りますね。","approach","user","小灯りはちゃんと消しました。","戻ります"},
                new[]{"I found the document. I will save it now. Thank you for waiting.","save","document-a","I found the document.","I will save it now."},
                new[]{"图已经生成好了。现在把它交付给你。谢谢你等我。","deliver","artifact-a","图已经生成好了。","现在把它交付给你。"},
                new[]{"観察は終わったわ。これから質問に答えるね。待ってくれてありがとう。","answer","question-a","観察は終わったわ。","これから質問に答えるね。"}})
                yield return Partition(model,row);
            foreach(string question in new[]{"刚才为什么没开始？只解释，不要操作。","Why didn't that earlier request start? Explain only; don't execute anything.","さっきの依頼を始められなかった理由だけ教えて。操作はしないで。"}) {
                var packet=(JObject)Events("LLM/FirstReplyUnadmittedFacts").Last()["expanded"].DeepClone();
                packet["latestUserText"]=question;packet["currentUtterance"]=question;
                packet["runtimeContext"]["humanInput"]=new JObject{["primaryUtterance"]=question};
                packet["runtimeContext"]["previousRequestFailure"]=PriorFailure();
                yield return Route(model,packet,History(196247),"independent-failure-question-"+question,"evidence");
            }
            // These were wrongly judged against an unrelated old action. Replay
            // their original frames and original words, with actual old results.
            foreach(string id in new[]{"speech:11:14","speech:13:15","speech:17:21"}) {
                var row=Events("Activity/MonitorFrame").FirstOrDefault(e=>(e["data"]["speech"] as JArray??new JArray()).OfType<JObject>().Any(s=>(string)s["id"]==id));
                Check("recorded-monitor-present-"+id,row!=null);
                if(row!=null)yield return Monitor(model,(JObject)row["data"].DeepClone(),"recorded-unrelated-result-"+id);
            }
            Save(true);if(cases.Any(c=>!(bool)c["passed"]))throw new InvalidOperationException("Routing/delivery replay failed; inspect routing-delivery.json.");
        }finally{model.CancelActiveResponse();model.CancelActivityMonitor();UnityEngine.Object.Destroy(host);}
    }
    static void Protocols(ChatQW model)
    {
        foreach(var row in Events("LLM/FirstReplyUnadmittedFacts")) {
            var raw=(JObject)row["expanded"].DeepClone();var source=(JObject)raw["runtimeContext"];var before=source.DeepClone();
            raw["runtimeContext"]=TaskContinuationContextPolicy.FirstReply(source);
            string input=SceneFactContextCompactor.CompactJson(raw.ToString(Formatting.None),out _);
            Check("recorded-final-routing-budget-"+row["line"],input.Length<=16000,new JObject{["characters"]=input.Length,["runtimeFields"]=TaskContinuationContextPolicy.Sizes((JObject)raw["runtimeContext"])});
            Check("canonical-history-unchanged-"+row["line"],JToken.DeepEquals(source,before));
            Check("routing-idempotent-"+row["line"],JToken.DeepEquals(raw["runtimeContext"],TaskContinuationContextPolicy.FirstReply((JObject)raw["runtimeContext"])));
            source["previousUnfinishedWork"]["customRestriction"]=new JObject{["stage"]="retain exactly",["generation"]=91,["neverOverwrite"]="all originals"};
            var projection=TaskContinuationContextPolicy.FirstReply(source);
            Check("custom-restriction-kept-"+row["line"],JToken.DeepEquals(source.SelectToken("previousUnfinishedWork.customRestriction"),projection.SelectToken("previousUnfinishedWork.customRestriction")));
            ((JObject)raw["runtimeContext"])["previousRequestFailure"]=PriorFailure();
            string withFailure=SceneFactContextCompactor.CompactJson(raw.ToString(Formatting.None),out _);
            Check("recorded-routing-with-failure-receipt-"+row["line"],withFailure.Length<=16000,new JObject{["characters"]=withFailure.Length});
        }
        foreach(var entry in Events("Activity/MonitorFrame").Concat(Events("Activity/MonitorUnadmittedFrame"))) {
            var original=(JObject)entry["data"].DeepClone();int index=0;
            foreach(var frame in (string)original["purpose"]=="speech"?ActivityEvidencePolicy.SpeechOperations(original):new[]{original}) {
                var focus=ActivityEvidencePolicy.Focus(frame);string input=model.PrepareActivityMonitorFrame(focus.ToString(Formatting.None),out string diagnostics);
                Check("actual-monitor-complete-budget-"+entry["line"]+"-"+index++,input!=null,JObject.Parse(diagnostics));
                if(input!=null) {
                    var bounded=JObject.Parse(input);var before=(focus["speech"] as JArray)?.OfType<JObject>().FirstOrDefault();var after=(bounded["speech"] as JArray)?.OfType<JObject>().FirstOrDefault();
                    if(before?["observedAtSpeechStart"] is JObject frozen)
                        Check("frozen-body-retained-"+entry["line"]+"-"+index,ActivityEvidencePolicy.Equal(frozen["body"],ActivityEvidencePolicy.FrozenSpeech(after,bounded)?["body"]));
                    if(bounded.SelectToken("facts.speech-start") is JObject snapshot) {
                        var liveFacts=(JObject)bounded["facts"].DeepClone();liveFacts.Remove("speech-start");
                        var live=ActivityEvidencePolicy.WithLiveFacts(bounded,liveFacts,(JObject)bounded["evidenceScopes"]);
                        live=ActivityMonitorContextPolicy.Project(live,(int?)bounded.SelectToken("projection.pressure")??0);
                        Check("frozen-source-survives-live-recheck-"+entry["line"]+"-"+index,
                            ActivityEvidencePolicy.Equal(snapshot,live.SelectToken("facts.speech-start")));
                    }
                }
            }
            Check("monitor-source-unchanged-"+entry["line"],JToken.DeepEquals(original,entry["data"]));
        }
        foreach(string language in new[]{"zh","en","ja"}) {
            string text=(string)typeof(ChatSample).GetMethod("ContinuousFollowText",Flags).Invoke(null,new object[]{language});
            Check("continuous-follow-report-"+language,!string.IsNullOrEmpty(text)&&!text.Contains("最终")&&!text.Contains("最終")&&!text.Contains("final result"),new JValue(text));
        }
        foreach(var entry in Events("Activity/MonitorFrame").Where(e=>(e["data"]["speech"] as JArray??new JArray()).OfType<JObject>()
            .Any(s=>new[]{"speech:11:14","speech:17:21"}.Contains((string)s["id"])))) {
            var frame=(JObject)entry["data"].DeepClone();var failure=(JObject)PriorFailure()["receipt"];
            frame["facts"]["planningFailure"]=failure;
            frame["evidenceScopes"]["planningFailure"]=new JObject{["domain"]="execution",["issues"]=new JArray("mistake","uncertainty"),
                ["binding"]=ActivityEvidencePolicy.Binding(failure,"request-processing",null)};
            int index=0;
            foreach(var operation in ActivityEvidencePolicy.SpeechOperations(frame)) {
                string input=model.PrepareActivityMonitorFrame(ActivityEvidencePolicy.Focus(operation).ToString(Formatting.None),out string diagnostics);
                Check("monitor-with-produced-failure-"+entry["line"]+"-"+index++,input!=null,JObject.Parse(diagnostics));
            }
        }
        var oversized=new JObject{["humanInput"]=new JObject{["primaryUtterance"]="Only explain."},["customConstraints"]=new string('限',20000)};
        string overWire=(string)Call(model,"BuildFirstReplyReviewWire",new LLM.SendData("user","Only explain."),new List<LLM.SendData>(),oversized.ToString(),new FirstReplyCompletion());
        Check("unknown-large-restrictions-still-fail-closed",overWire==null);
    }
    static void FailureLifecycle(ChatSample chat)
    {
        int generation=(int)Get(chat,"m_FormalResponseGeneration"),epoch=(int)Get(chat,"m_WorkEpoch");
        object old=Get(chat,"m_FirstReplyFailure");
        try {
            Call(chat,"RecordFirstReplyFailure",generation,epoch,"first-reply-review-input-budget");
            var failure=(JObject)Call(chat,"ReadRoomTaskDecisionFailure");
            Check("failed-entry-is-shared-current-fact",failure!=null&&(string)failure["stage"]=="classification-preflight"&&(bool?)failure["newActionSubmittedByThisDecision"]==false,failure);
            Check("failed-entry-is-recallable",Call(chat,"ReadPreviousRoomPlanningFailure") is JObject);
            failureForReplay=(JObject)Call(chat,"ReadPreviousRoomPlanningFailure");
            failureForReplay["receipt"]["request"]=((JArray)fixture["dialogue"]).OfType<JObject>().Single(r=>(int)r["line"]==180432)["text"].DeepClone();
            foreach(string language in new[]{"zh","en","ja"}) {
                string message=(string)typeof(ChatSample).GetMethod("FirstReplyFailureText",Flags).Invoke(null,new object[]{failure,language});
                Check("failure-says-not-started-"+language,!string.IsNullOrEmpty(message),new JValue(message));
            }
            Set(chat,"m_FormalResponseGeneration",generation+1);
            Check("old-failure-does-not-own-new-request",Call(chat,"ReadRoomTaskDecisionFailure")==null);
            var snapshot=Get(chat,"m_FirstReplyFailure");Call(chat,"RecordFirstReplyFailure",generation,epoch,"stale");
            Check("stale-failure-cannot-replace-current",ReferenceEquals(snapshot,Get(chat,"m_FirstReplyFailure")));
        }finally{Set(chat,"m_FormalResponseGeneration",generation);Set(chat,"m_FirstReplyFailure",old);}
    }
    static IEnumerator Route(ChatQW model,JObject packet,List<LLM.SendData> history,string name,string expected,string rejected=null)
    {
        var current=new LLM.SendData("user",(string)packet["latestUserText"]);
        string wire=(string)Call(model,"BuildFirstReplyReviewWire",current,history,packet["runtimeContext"].ToString(Formatting.None),new FirstReplyCompletion());
        string route=null,status=null,kind=null;JObject proof=null;
        if(wire!=null) {
            if(rejected!=null)wire=FirstReplyProtocol.BuildGroundingRepairWire(wire,rejected);
            Action<string,JObject,string,bool?,string,string,JObject,string> callback=(r,q,k,s,d,t,p,f)=>{route=r;kind=k;status=t;proof=p;};
            yield return (IEnumerator)Call(model,"RequestFirstReplyReview",wire,(int)Get(model,"m_StreamRequestGeneration"),callback,rejected==null?0:1,false);
        }
        bool meaningful=!name.StartsWith("original-gen11-") || ((string)proof?["actionQuote"]??"").Contains("关掉") || ((string)proof?["actionQuote"]??"").Contains("回到");
        Check(name,route==expected&&meaningful,new JObject{["route"]=route,["kind"]=kind,["status"]=status,["proof"]=proof,["fullHistoryRows"]=history.Count,
            ["inputCharacters"]=wire==null?-1:((string)JObject.Parse(wire)["messages"][1]["content"]).Length});
    }
    static IEnumerator Partition(ChatQW model,string[] row)
    {
        var input=StepSpeechPartitionProtocol.Input("Recorded result with a concurrent successor",row[0],new JObject{["actionId"]="current-successor",["intent"]=row[1],["target"]=row[2],["actor"]="CHARACTER"});
        bool done=false,ok=false;string raw="",status=null;
        model.PostRoomPreparationMessage(input.ToString(Formatting.None),(a,b,c)=>{done=true;ok=a;raw=b;status=c;});
        float until=Time.realtimeSinceStartup+5;while(!done&&Time.realtimeSinceStartup<until)yield return null;
        string prefix=null,reason=null;bool parsed=ok&&StepSpeechPartitionProtocol.Resolve(input,raw,out prefix,out reason);
        Check("native-step-partition-"+row[1],parsed&&prefix.Contains(row[3])&&!prefix.Contains(row[4]),new JObject{["raw"]=raw,["status"]=status,["reason"]=reason,["prefix"]=prefix,["original"]=row[0]});
    }
    static IEnumerator Monitor(ChatQW model,JObject source,string name)
    {
        var frame=ActivityEvidencePolicy.Focus(source);var tries=new ActivityMonitorAttempts();string key=ActivityEvidencePolicy.SemanticKey(frame);JObject verdict=null;string raw="",reason=null;
        for(int i=0;i<3;i++) {
            if(tries.Feedback(key)!=null)frame["previousAttempt"]=tries.Feedback(key);
            string input=model.PrepareActivityMonitorFrame(frame.ToString(Formatting.None),out var diagnostics);if(input==null)break;
            bool done=false,ok=false;string status=null;
            model.PostActivityMonitor(input,(a,b,c)=>{done=true;ok=a;raw=b;status=c;});
            float until=Time.realtimeSinceStartup+12;while(!done&&Time.realtimeSinceStartup<until)yield return null;
            if(ok&&ActivityMonitorProtocol.TryRead(raw,JObject.Parse(input),out verdict,out reason))break;
            tries.Failed(key,Time.realtimeSinceStartup,reason??status,raw,false);
        }
        Check(name,verdict!=null&&(string)verdict["speech"]["issue"]=="none",new JObject{["raw"]=raw,["reason"]=reason});
    }
}
