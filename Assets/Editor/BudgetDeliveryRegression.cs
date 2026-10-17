using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class BudgetDeliveryRegression
{
    const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    static JObject fixture; static string output; static readonly JArray checks=new JArray();
    static object Call(object owner,string name,params object[] args) => (owner is Type t?t:owner.GetType()).GetMethod(name,F).Invoke(owner is Type?null:owner,args);
    static void Set(object owner,string name,object value)=>owner.GetType().GetField(name,F).SetValue(owner,value);
    static IEnumerator Until(Func<bool> done){float end=Time.realtimeSinceStartup+45;while(!done()&&Time.realtimeSinceStartup<end)yield return null;}
    static void Check(string name,bool passed,JToken evidence=null) {checks.Add(new JObject{["name"]=name,["passed"]=passed,["evidence"]=evidence});Save();}
    static void Save() => File.WriteAllText(Path.Combine(output,"budget-delivery.json"),new JObject{["passed"]=checks.Count>0&&checks.All(x=>(bool)x["passed"]),["checks"]=checks}.ToString(Formatting.Indented));
    static JObject At(int line)=>(JObject)((JArray)fixture["events"]).Single(x=>(int)x["line"]==line)["data"].DeepClone();
    static JToken Text(JObject packet) {var content=packet["messages"].Last["content"];return content is JArray a?a.Single(x=>(string)x["type"]=="text")["text"]:content;}
    public static IEnumerator Run(ChatSample chat,ArdyRoomDialogueBridge bridge,string directory)
    {
        output=directory;checks.Clear();fixture=ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/BudgetDelivery.20261017-1505.json"));
        var host=new GameObject("Complete budget and delivery replay");host.SetActive(false);var model=host.AddComponent<ChatQW>();model.enabled=false;model.m_PrewarmPrefixOnStart=false;
        model.m_Backend=ChatQW.BackendType.Local;Set(model,"url","http://127.0.0.1:8080/v1/chat/completions");host.SetActive(true);
        Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableActivityMonitor",false);
        try {
            var history=(JArray)fixture["history"];
            Check("complete-six-inputs-nineteen-chunks-four-openings",history.Count(x=>(string)x["role"]=="user")==6&&history.Count(x=>(string)x["role"]=="assistant")==19&&history.Count(x=>(string)x["role"]=="assistant-opening")==4);
            foreach(bool interest in new[]{false,true}) {
                var packet=(JObject)fixture[interest?"unadmittedAutonomy":"unadmittedWork"].DeepClone();string original=packet.ToString(Formatting.None);var text=Text(packet);
                object[] args={(string)text,interest,null};string projected=(string)Call(typeof(ChatQW),"ProjectPrivateAttention",args);text.Replace(projected);
                object[] admission={packet.ToString(Formatting.None),null};bool admitted=(bool)Call(model,interest?"AdmitAutonomyIntentWire":"AdmitWorkReviewWire",admission);
                Check("complete-final-wire-"+(interest?"autonomy":"delivery"),admitted,new JObject{["budget"]=JObject.FromObject(admission[1]),["projection"]=JObject.FromObject(args[2]),["packet"]=packet});
                Check("original-fixture-unchanged-"+interest,original==fixture[interest?"unadmittedAutonomy":"unadmittedWork"].ToString(Formatting.None));
                object[] again={projected,interest,null};string repeat=(string)Call(typeof(ChatQW),"ProjectPrivateAttention",again);
                Check("reprojection-keeps-references-valid-"+interest,(bool?)((JObject)again[2])["privateReferencesRoundTripVerified"]==true && repeat.Length<=projected.Length+300);
                Check("all-resident-user-evidence-verbatim-"+interest,((JArray)packet["messages"]).Take(((JArray)packet["messages"]).Count-1)
                    .Zip(((JArray)fixture[interest?"unadmittedAutonomy":"unadmittedWork"]["messages"]),(a,b)=>JToken.DeepEquals(a,b)).All(x=>x));
            }
            var custom="[Task progress runtime]\n"+new JObject{["customRestriction"]=new string('限',30000),["scope"]="Unknown scope: never change file A."}.ToString(Formatting.None);
            object[] protectedArgs={custom,true,null};string protectedText=(string)Call(typeof(ChatQW),"ProjectPrivateAttention",protectedArgs);
            Check("unknown-limit-and-scope-not-truncated",protectedText.Contains(new string('限',30000))&&protectedText.Contains("Unknown scope: never change file A."));
            var over=(JObject)fixture["unadmittedAutonomy"].DeepClone();Text(over).Replace(protectedText);
            object[] tooLarge={over.ToString(Formatting.None),null};Check("oversized-mandatory-evidence-still-rejected",!(bool)Call(model,"AdmitAutonomyIntentWire",tooLarge));
            object[] smallArgs={new System.Collections.Generic.List<LLM.SendData>(),WorkNextIntentProtocol.Contract,null};
            string small=(string)Call(model,"ProjectAutonomyIntentPacket",smallArgs);
            Check("small-opportunity-packet-does-not-inherit-delivery-schema",!small.Contains(WorkNextIntentProtocol.Contract)&&small.Contains("自主机会与交付分开"));
            foreach(var entry in ((JArray)fixture["events"]).Where(e=>(string)e["tag"]=="Activity/MonitorFrame")) {
                string ready=model.PrepareActivityMonitorFrame(entry["data"].ToString(Formatting.None),out string diagnostic);
                Check("actual-complete-monitor-budget-"+entry["line"],ready!=null,JObject.Parse(diagnostic));
            }
            ScheduleAndCache();VisualProtocol();
            var historicalText=(string)Text((JObject)fixture["unadmittedWork"]);
            const string marker="[Historical interrupted work; reference only]";
            int at=historicalText.IndexOf(marker,StringComparison.Ordinal);
            using(var reader=new JsonTextReader(new StringReader(historicalText.Substring(at+marker.Length)))) {
                reader.DateParseHandling=DateParseHandling.None;var historyBlock=JObject.Load(reader);
                var planning=new JObject{["currentUserText"]="检查先前文档后另存为新版本；保留原件。",["previousWorkContext"]=historyBlock["snapshot"].DeepClone(),
                    ["unknownConstraint"]=new JObject{["mustRetainOriginal"]=true},["sceneSpatialMemory"]=new JObject{["customMemory"]=new JArray("seen-once","historical-only")}};
                var projected=(JObject)Call(typeof(ChatQW),"ProjectRoomDecisionInput",planning,12);
                Check("main-planning-historical-archive-shrinks-with-source",projected["previousWorkContext"].ToString().Length<planning["previousWorkContext"].ToString().Length&&
                    (bool?)projected["previousWorkContext"]["historySource"]["detailAvailable"]==true,
                    new JObject{["before"]=planning["previousWorkContext"].ToString().Length,["after"]=projected["previousWorkContext"].ToString().Length,
                        ["fullOriginalSuccessfulMainWireAvailable"]=false});
                Check("planning-original-limit-and-memory-retained",JToken.DeepEquals(planning["unknownConstraint"],projected["unknownConstraint"])&&
                    (string)planning["currentUserText"]==(string)projected["currentUserText"]&&JToken.DeepEquals(planning["sceneSpatialMemory"],projected["sceneSpatialMemory"]));
            }
            var old=At(126438);Check("original-wrong-origin-explanation-retained",((string)At(126458)["rawVerdict"]).Contains("回到我身边"),At(126458));
            Check("old-verdict-missing-source-needs-bounded-repair",!ActivityMonitorProtocol.TryRead((string)At(126458)["rawVerdict"],old,out _,out _));
            yield return Native(model,old,"actual-earlier-follow",true);
            foreach(var variant in new[]{new[]{"observe","继续观察这个区域。","保存另一份笔记。"},new[]{"sing","この曲を続けて。","別の資料を探して。"},new[]{"generate","Keep generating the draft.","Inspect another file."}}) {
                var frame=(JObject)old.DeepClone();string id=(string)frame["actions"][0]["id"];var action=(JObject)frame["facts"]["action:"+id];
                action["kind"]=variant[0];action["phase"]="running";action["facts"]=new JObject{["phase"]="running"};action.Remove("reason");
                action["task"]["step"]=new JObject{["kind"]=variant[0]};action["task"]["origin"]["request"]=variant[1];
                frame["facts"]["request"]["text"]=variant[2];((JObject)frame["facts"]).Remove("execution");((JObject)frame["evidenceScopes"]).Remove("execution");
                frame["actions"][0]["kind"]=variant[0];yield return Native(model,frame,"general-handoff-"+variant[0],true);
            }
            foreach(string stop in new[]{"现在停止跟随我。","今はついて来ないで。","Stop following me now."}) {
                var frame=(JObject)old.DeepClone();frame["facts"]["request"]["text"]=stop;yield return Native(model,frame,"current-stop-"+stop,false);
            }
            var voluntary=(JObject)old.DeepClone();var voluntaryAction=voluntary["facts"]["action:"+(string)voluntary["actions"][0]["id"]];
            voluntaryAction["task"]["origin"]["initiative"]="autonomous";voluntaryAction["task"]["origin"]["request"]=null;
            voluntaryAction["task"]["requestRelationship"]="same-work";voluntary["facts"]["request"]["text"]="你喜欢这里吗？";
            yield return Native(model,voluntary,"already-owned-autonomous-follow-without-new-command",true);
            yield return Visual(model);
            yield return CompleteWorkWire(model);
            Save();if(checks.Any(x=>!(bool)x["passed"]))throw new InvalidOperationException("Complete budget/delivery checks failed.");
        } finally {UnityEngine.Object.Destroy(host);}
        yield break;
    }

    static void ScheduleAndCache()
    {
        var times=new System.Collections.Generic.Queue<float>();var log=new JArray();
        foreach(var e in ((JArray)fixture["events"]).Where(e=>(string)e["tag"]=="Activity/MonitorFrame"&&
            (float?)e["data"]["capturedAtRealtime"]>=180 && (float?)e["data"]["capturedAtRealtime"]<241)) {
            var f=(JObject)e["data"];float now=(float)f["capturedAtRealtime"];
            bool allowed=ActivityMonitorSchedulePolicy.RetryAt(times,now,ActivityMonitorSchedulePolicy.Ceiling(f))<=now;
            if(allowed)times.Enqueue(now);log.Add(new JObject{["line"]=e["line"],["time"]=now,["purpose"]=f["purpose"],["admitted"]=allowed});
        }
        Check("tail-delivery-has-slot-within-original-total-limit",ActivityMonitorSchedulePolicy.RetryAt(times,241f,12)<=241f,log);
        var exhausted=Enumerable.Range(0,12).Select(i=>200f+i).ToArray();
        Check("delivery-never-bypasses-total-limit",ActivityMonitorSchedulePolicy.RetryAt(exhausted,220,12)>220);
        Check("expired-window-reopens",ActivityMonitorSchedulePolicy.RetryAt(exhausted,272,12)==272);
        var delivery=new JObject{["purpose"]="delivery"};var actionFrame=new JObject{["purpose"]="action"};
        Check("owed-result-priority-with-live-fairness",ActivityMonitorSchedulePolicy.Priority(delivery,"speech")<ActivityMonitorSchedulePolicy.Priority(actionFrame,"speech")&&
            ActivityMonitorSchedulePolicy.Priority(actionFrame,"delivery")<ActivityMonitorSchedulePolicy.Priority(delivery,"delivery"));
        var speech=new JObject{["purpose"]="speech",["owner"]=new JObject{["workEpoch"]=1},["speech"]=new JArray(new JObject{["id"]="audio1",["version"]=1,["state"]="completed",["text"]="The file was saved.",
            ["observedAtSpeechStart"]=new JObject{["result"]="save-1"}}),["facts"]=new JObject{["result:1"]=new JObject{["actionId"]="save-1",["status"]="completed"},["unrelated"]=1},["evidenceScopes"]=new JObject()};
        var decision=new JObject{["speech"]=new JObject{["issue"]="none"},["claimTime"]="past-event",["truthWhenSaid"]="supported",["truthEvidenceIds"]=new JArray("result:1")};
        var cache=new ActivityCompletedCheckCache();cache.Remember(speech,decision);speech["facts"]["unrelated"]=2;
        Check("past-result-check-reused-across-unrelated-change",cache.Contains(speech));speech["facts"]["result:1"]["status"]="failed";
        Check("changed-cited-proof-invalidates-check",!cache.Contains(speech));speech["facts"]["result:1"]["status"]="completed";speech["owner"]["workEpoch"]=2;
        Check("new-owner-cannot-inherit-check",!cache.Contains(speech));cache.Clear();decision["truthWhenSaid"]="unknown";cache.Remember(speech,decision);
        Check("unknown-not-cached-as-checked",!cache.Contains(speech));
    }
    static void VisualProtocol()
    {
        var original=At(108657);var observed=JObject.Parse((string)original["rawVerdict"]);
        var claims=new JArray(((JArray)original["evidence"]).Select((r,i)=>new JObject{["id"]="c"+i,["speechId"]=r["speechId"],["claimQuote"]=r["claimQuote"]}));
        // Preserve every raw row, including claims whose original evidence was
        // missing. This check intentionally does not invent the absent RGB.
        Check("original-partial-visual-output-retained",((JArray)observed["observations"]).Count==4,original);
        var input=new JObject{["captureId"]="recorded-source",["speech"]="A shelf with books is on the left.",["claims"]=new JArray(new JObject{["id"]="c0",["speechId"]="v0",["claimQuote"]="A shelf with books is on the left."})};
        var partial=new JObject{["observations"]=new JArray(new JObject{["id"]="c0",["meaning"]="A shelf on the left contains books.",["finding"]="Only the shelf is visible.",["coverage"]="partial",["assessment"]="supported"})};
        Check("partial-finding-cannot-support-whole-claim",!ActivityVisualEvidenceProtocol.TryReadGroup(partial.ToString(),input,out _));
        partial["observations"][0]["assessment"]="unresolved";
        Check("partial-view-remains-unknown-not-denial",ActivityVisualEvidenceProtocol.TryReadGroup(partial.ToString(),input,out _));
    }
    static IEnumerator Native(ChatQW model,JObject frame,string name,bool allow)
    {
        string ready=model.PrepareActivityMonitorFrame(frame.ToString(Formatting.None),out string budget);
        bool done=false,ok=false;string raw=null,status=null,error=null;JObject decision=null;
        if(ready!=null){model.PostActivityMonitor(ready,(a,b,c)=>{done=true;ok=a;raw=b;status=c;});yield return Until(()=>done);}
        bool valid=ok&&ActivityMonitorProtocol.TryRead(raw,JObject.Parse(ready),out decision,out error);
        Check("native-"+name,valid&&(allow?(string)decision["action"]["operation"]=="continue":new[]{"stop","reconsider"}.Contains((string)decision["action"]["operation"])),
            new JObject{["frame"]=frame,["raw"]=raw,["status"]=status,["rejection"]=error,["budget"]=JObject.Parse(budget)});
    }
    static IEnumerator Visual(ChatQW model)
    {
        var texture=new Texture2D(64,64);texture.SetPixels(Enumerable.Repeat(Color.red,4096).ToArray());texture.Apply();
        string rgb="data:image/png;base64,"+Convert.ToBase64String(texture.EncodeToPNG());UnityEngine.Object.Destroy(texture);
        var claims=new JArray(new JObject{["id"]="c0",["speechId"]="v0",["claimQuote"]="The image is red and contains three blue books."},
            new JObject{["id"]="c1",["speechId"]="v1",["claimQuote"]="Those books make me feel cheerful."});
        var input=new JObject{["purpose"]="visual-evidence",["captureId"]="new-synthetic-red-image",["speechId"]="v0",["claims"]=claims,
            ["speech"]=string.Join(" ",claims.Select(c=>(string)c["claimQuote"]))};
        bool done=false,ok=false;string raw=null,status=null;JArray evidence=null;
        model.PostActivityVisualEvidence(input.ToString(Formatting.None),rgb,(a,b,c)=>{done=true;ok=a;raw=b;status=c;});yield return Until(()=>done);
        bool parsed=ok&&ActivityVisualEvidenceProtocol.TryReadGroup(raw,input,out evidence);
        Check("native-whole-visual-proposition-not-only-color",parsed&&evidence.OfType<JObject>().Any(e=>(string)e["speechId"]=="v0"&&(string)e["assessment"]!="supported"),
            new JObject{["raw"]=raw,["status"]=status,["evidence"]=evidence,["originalSessionImage"]=false});
    }
    static IEnumerator CompleteWorkWire(ChatQW model)
    {
        var packet=(JObject)fixture["unadmittedWork"].DeepClone();string prompt=(string)Text(packet);
        var receipt=(JObject)Call(typeof(ChatQW),"ReadWorkReviewDelivery",prompt);
        // The rejected wire intentionally omitted audio timing columns. Restore
        // the validator's runtime snapshot from the recorded same-version frame,
        // not from the model-selected quotation or invented playback metadata.
        foreach(var row in ((JArray)receipt["results"]).OfType<JObject>()) {
            var recorded=((JArray)fixture["events"]).Where(e=>(string)e["tag"]=="Activity/MonitorFrame")
                .Select(e=>e["data"]?["delivery"] as JObject).FirstOrDefault(d=>d!=null&&
                    (string)d["id"]==(string)row["id"]&&(string)d["playbackRevision"]==(string)row["playbackRevision"]&&
                    JToken.DeepEquals(d["completedPlaybackText"],row["completedPlaybackText"])&&d["completedAudio"] is JArray);
            if(recorded==null)throw new InvalidOperationException("Original audio snapshot missing for "+row["id"]);
            row["completedAudio"]=recorded["completedAudio"].DeepClone();
        }
        // Same complete original inputs with today's production-bound schema.
        // All non-delivery schema fields remain the recorded request's fields.
        var schema=new JObject{["properties"]=new JObject(),["required"]=new JArray()};WorkResultDeliveryProtocol.ExtendSchema(schema);
        WorkResultDeliveryProtocol.BindCoverageSchema(schema,(JArray)receipt["results"]);
        packet["response_format"]["schema"]["properties"]["delivery_reports"]=schema["properties"]["delivery_reports"].DeepClone();
        object[] args={prompt,false,null};Text(packet).Replace((string)Call(typeof(ChatQW),"ProjectPrivateAttention",args));
        PrivateDecisionResult result=default;bool done=false;
        int generation=(int)typeof(ChatQW).GetField("m_EphemeralGeneration",F).GetValue(model);
        var request=(IEnumerator)Call(model,"RequestBoundWorkReviewPayload",packet.ToString(Formatting.None),generation,
            new Action<PrivateDecisionResult>(r=>{result=r;done=true;}),0,Time.realtimeSinceStartup,receipt);
        model.StartCoroutine(request);yield return Until(()=>done);
        JObject verdict=null;try{verdict=JObject.Parse(result?.Output??"");}catch(JsonException){}
        string rejection=null;bool valid=verdict!=null&&WorkResultDeliveryProtocol.ValidateCoverage((JArray)receipt["results"],verdict,out rejection);
        Check("native-complete-original-delivery-wire-keeps-unreported-return",valid&&
            ((JArray)verdict["delivery_reports"]).OfType<JObject>().Any(r=>(string)r["coverage"]=="unreported")&&
            (string)verdict["next_intent_source"]!="autonomous",new JObject{["raw"]=result?.Output,["status"]=result?.Status,["rejection"]=rejection,
                ["originalFullWire"]=true,["newActionSubmitted"]=false});
    }
}
