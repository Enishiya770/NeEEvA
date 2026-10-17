using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

public static class ActivityMonitorRegression
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool value,string why){checks++;if(!value)throw new InvalidOperationException("Activity monitor: "+why);}
    private static object Call(object value,string name,params object[] args)=>value.GetType().GetMethod(name,Flags).Invoke(value,args);
    private static void Set(object value,string name,object field)=>value.GetType().GetField(name,Flags).SetValue(value,field);
    private static object Get(object value,string name)=>value.GetType().GetField(name,Flags).GetValue(value);
    private static JObject Decision(string issue="none",string operation="continue")=>new JObject{
        ["comparison"]="Actual receipt contradicts the assertion at speech start.",
        ["speechAct"]=issue=="none"?"no-speech":"factual-claim",["claimQuote"]=issue=="none"?"":"Saved.",
        ["claimTime"]="current-state",["truthWhenSaid"]=issue=="world-change"?"supported":issue=="uncertainty"?"unknown":"contradicted",
        ["claimDomain"]="execution",
        ["truthEvidenceIds"]=new JArray("receipt"),
        ["speech"]=new JObject{["id"]=issue=="none"?"":"speech:1",["version"]=0,["issue"]=issue,
            ["groundedUpdate"]=issue=="none"?"":"The save failed.",["correctionLeadIn"]=issue=="none"?"":"Sorry, ",
            ["evidenceIds"]=issue=="none"?new JArray():new JArray("receipt"),["reason"]="Actual receipt differs from the assertion.",
            ["replacement"]=issue=="none"?"":"The save failed.",["correction"]=issue=="none"?"":"Sorry, the save actually failed."},
        ["action"]=new JObject{["id"]=operation=="continue"?"":"save-job",["version"]="v1",["operation"]=operation,
            ["evidenceIds"]=operation=="continue"?new JArray():new JArray("request","action:save-job"),
            ["conflict"]=new JObject{["basis"]="constraint-conflict",["requirementEvidenceId"]="request",["requirementQuote"]="Keep the original unchanged.",["observedQuote"]="overwrite-original",["assessment"]="conflict-even-considering-prerequisites"},["reason"]="Do not overwrite the original.",
            ["nextIntent"]=operation=="reconsider"?"Consider creating a separate copy while preserving the original.":""}
    };
    private static JObject Frame()=>new JObject{
        ["evidenceScopes"]=new JObject{["receipt"]=ActivityMonitorContextPolicy.Scope("execution","mistake","world-change","uncertainty"),
            ["request"]=ActivityMonitorContextPolicy.Scope("request","mistake","uncertainty")},
        ["facts"]=new JObject{["receipt"]=new JObject{["operation"]="save",["status"]="failed"},
            ["request"]="Keep the original unchanged.",
            ["action:save-job"]=new JObject{["id"]="save-job",["version"]="v1",["kind"]="overwrite-original"}},
        ["speech"]=new JArray(new ActivityMonitorProtocol.SpeechUnit{Id="speech:1",Text="Saved.",Language="en"}.Snapshot()),
        ["actions"]=new JArray(new JObject{["id"]="save-job",["version"]="v1",["cancellable"]=true,["adapter"]="document"})
    };
    public static int RunChecks()
    {
        checks=0;
        var sourceRepair=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/ActivityMonitor.routing-repair-20261016.json"));
        foreach(var original in ((JArray)sourceRepair["cases"]).OfType<JObject>()) {
            var historicalFrame=(JObject)original["input"].DeepClone();
            string raw=(string)original["attempts"][0]["raw"];
            Check(!ActivityMonitorProtocol.TryRead(raw,historicalFrame,out _,out string rejection),
                "Original false historical denial or wrong-source correction cannot be cached as reviewed.");
            var retries=new ActivityMonitorAttempts();retries.Failed("history",0,rejection,raw);
            historicalFrame["previousAttempt"]=retries.Feedback("history");
            Check((bool?)historicalFrame["previousAttempt"]["historicalObservation"]==true &&
                (bool?)historicalFrame["previousAttempt"]["requireHistoricalUnknownConsistency"]==true,
                "Bounded repair binds the declared historical uncertainty to history capabilities.");
            var shape=JObject.Parse(ActivityMonitorProtocol.SchemaForFrame(historicalFrame));
            Check(shape.SelectTokens("$..enum").OfType<JArray>().All(a=>a.Count>0),
                "Combined history repair constraints never send an empty enum to the provider.");
            var nativeShape=JObject.Parse(ActivityMonitorProtocol.CompactSchema(shape.ToString()));
            Check(nativeShape.SelectTokens("$..properties").OfType<JObject>().All(p=>p.Properties().All(f=>f.Value is JObject)),
                "Compaction preserves properties maps as maps, never replaces a schema container with $ref.");
            var proofSources=shape.SelectTokens("$..evidenceId.enum").OfType<JArray>()
                .SelectMany(a=>a.Values<string>()).ToArray();
            Check(proofSources.Length>0 && proofSources.All(id=>(string)historicalFrame["evidenceScopes"][id]["domain"]=="history"),
                "A user question cannot supply a history correction proof on bounded repair.");
        }
        var frame=Frame();var decision=Decision("mistake","reconsider");
        var failures=new ActivityMonitorAttempts();
        Check(failures.CanAttempt("frame",0),"New frame is eligible.");
        failures.Failed("frame",0);
        Check(!failures.CanAttempt("frame",1)&&failures.CanAttempt("frame",2),"Failed frame gets a bounded delayed retry, not reviewed status.");
        failures.Failed("frame",2);failures.Failed("frame",7);
        Check(!failures.CanAttempt("frame",100)&&failures.CanAttempt("new-evidence",100),"Exhausted retry does not block new evidence.");
        var mismatch=Decision("mistake");mismatch["claimDomain"]="visual";mismatch["speech"]["evidenceIds"]=new JArray("request");
        Check(!ActivityMonitorProtocol.TryRead(mismatch.ToString(),frame,out _,out _),
            "A user request cannot support a visual correction.");
        var visual=(JObject)frame.DeepClone();visual["facts"]["observation"]=new JObject{["observedObjects"]=new JArray(),["monitorPixelsAvailable"]=false};
        visual["evidenceScopes"]["observation"]=ActivityMonitorContextPolicy.Scope("visual");
        mismatch["speech"]["evidenceIds"]=new JArray("observation");
        Check(!ActivityMonitorProtocol.TryRead(mismatch.ToString(),visual,out _,out _),"Empty descriptors are not contradictory pixels.");
        var projected=ActivityMonitorContextPolicy.Project(visual,2);
        Check(JToken.DeepEquals(frame["facts"]["request"],projected["facts"]["request"]),"Attention reduction preserves complete request.");
        var boundedVisual=(JObject)visual.DeepClone();boundedVisual["evidenceVersion"]=2;
        boundedVisual=ActivityMonitorContextPolicy.Project(boundedVisual,2);
        Check(boundedVisual["facts"]["observation"]!=null && boundedVisual["evidenceScopes"]["observation"]==null &&
            !ActivityMonitorProtocol.TryRead(mismatch.ToString(),boundedVisual,out _,out var missingVisualReason) &&
            ActivityMonitorContextPolicy.NeedsNewSource(missingVisualReason,mismatch.ToString(),boundedVisual),
            "Removing zero-authority annotations retains the observation and still requires actual visual evidence before correction.");
        var owed=new JObject{["id"]="version1",["status"]="failed",["hasCompletedPlaybackAfterResult"]=false,["completedPlaybackText"]=""};
        var report=new JObject{["comparison"]="Report the actual failure.",["operation"]="covered",["id"]="version1",["quote"]="It failed.",["text"]=""};
        Check(ActivityDeliveryProtocol.Reject(report,owed)=="no-completed-playback-after-result","Generated words cannot settle delivery.");
        owed["hasCompletedPlaybackAfterResult"]=true;owed["completedPlaybackText"]="It failed.";
        Check(ActivityDeliveryProtocol.Reject(report,owed)==null,"Failed execution may be delivered by actual complete playback.");
        report["id"]="version2";
        Check(ActivityDeliveryProtocol.Reject(report,owed)=="delivery-version-mismatch","Future result cannot borrow older words.");
        Check(ActivityMonitorProtocol.TryRead(decision.ToString(),frame,out _,out _),"General speech and non-movement action can share process feedback.");
        foreach(string state in new[]{"queued","starting","playing","completed","interrupted"}) {
            var unit=new ActivityMonitorProtocol.SpeechUnit{Id="speech:1",Text="Saved.",Language="en",State=state};
            string result=ActivityMonitorProtocol.Revise(unit,(JObject)decision["speech"],out string continuation);
            Check(result==(state=="queued"?"replace-unplayed":"correct-after-exposure"),"Respect actual playback state: "+state);
            Check(unit.Text==(state=="queued"?"The save failed.":"Saved."),"Previously exposed words stay immutable: "+state);
            Check((continuation==null)==(state=="queued"),"Only exposed words need an audible correction: "+state);
            Check(ActivityMonitorProtocol.Revise(unit,(JObject)decision["speech"],out _) == "stale","Late duplicate cannot overwrite a newer revision.");
        }
        foreach(string issue in new[]{"mistake","world-change","uncertainty"}) {
            var candidate=Decision(issue);
            Check(ActivityMonitorProtocol.TryRead(candidate.ToString(),frame,out _,out _),"Preserve distinct reasons: "+issue);
        }
        var bad=(JObject)decision.DeepClone();bad["speech"]["evidenceIds"]=new JArray("invented");
        Check(!ActivityMonitorProtocol.TryRead(bad.ToString(),frame,out _,out _),"Invented evidence cannot control speech.");
        var misclassified=Decision("uncertainty");misclassified["qualificationQuote"]="might be";
        var guessFrame=(JObject)frame.DeepClone();guessFrame["speech"][0]["text"]="That might be a book, but I cannot tell from here.";
        Check(!ActivityMonitorProtocol.TryRead(misclassified.ToString(),guessFrame,out _,out var wrongQuoteReason) &&
            wrongQuoteReason=="speech-claim-not-in-current-unit","An adjacent old assertion cannot replace the current guess's quoted words.");
        // Isolate the next defect: a non-factual declaration is rejected before
        // the quote guard, so it must not mask the preceding wrong-quote probe.
        misclassified["speechAct"]="qualified-guess";
        misclassified["claimQuote"]="That might be a book";
        Check(!ActivityMonitorProtocol.TryRead(misclassified.ToString(),guessFrame,out _,out var classificationReason) &&
            classificationReason=="speech-act-not-factual" &&
            !ActivityMonitorContextPolicy.NeedsNewSource(classificationReason,misclassified.ToString(),guessFrame),
            "Contradictory correction of a classified guess receives bounded semantic repair, not a wait for unrelated evidence.");
        var sequentialRepair=(JObject)guessFrame.DeepClone();sequentialRepair["evidenceVersion"]=2;
        sequentialRepair["facts"]["historyAnswers"]=new JObject{["availability"]="unknown"};
        sequentialRepair["evidenceScopes"]["historyAnswers"]=ActivityMonitorContextPolicy.Scope("history","uncertainty");
        sequentialRepair["previousAttempt"]=new JObject{["rejection"]="speech-act-not-factual",["requireHistoricalUnknownConsistency"]=true};
        var sequentialSchema=JObject.Parse(ActivityMonitorProtocol.SchemaForFrame(sequentialRepair));
        Check(sequentialSchema.Descendants().OfType<JProperty>().Where(p=>p.Name=="anyOf").All(p=>((JArray)p.Value).Count>0),
            "Consecutive historical and speech-act repairs compose without losing prior constraints or generating an empty grammar branch.");
        bad=(JObject)decision.DeepClone();bad["action"]["id"]="new-job";
        Check(!ActivityMonitorProtocol.TryRead(bad.ToString(),frame,out _,out _),"A verdict cannot target an unseen action.");
        bad=(JObject)decision.DeepClone();bad["action"]["version"]="v2";
        Check(!ActivityMonitorProtocol.TryRead(bad.ToString(),frame,out _,out _),"No guessed action revision.");
        var uncancellable=(JObject)frame.DeepClone();uncancellable["actions"][0]["cancellable"]=false;
        Check(!ActivityMonitorProtocol.TryRead(decision.ToString(),uncancellable,out _,out _),"No fabricated rollback/cancel capability.");
        foreach(string injection in new[]{"<motion name=\"stop\"/>","<song_sing/>","<silent/>"}) {
            bad=(JObject)decision.DeepClone();bad["speech"]["groundedUpdate"]=injection;
            Check(!ActivityMonitorProtocol.TryRead(bad.ToString(),frame,out _,out _),"Feedback text is never an action channel.");
        }
        var facts=(JObject)frame["facts"];
        Check(ActivityMonitorProtocol.EvidenceCurrent((JObject)decision["speech"],facts,(JObject)facts.DeepClone()),"Same evidence remains usable while playback advances.");
        var newer=(JObject)facts.DeepClone();newer["receipt"]["status"]="completed";
        Check(!ActivityMonitorProtocol.EvidenceCurrent((JObject)decision["speech"],facts,newer),"Changed premise invalidates a late correction.");
        Check(ActivityMonitorProtocol.EvidenceCurrent((JObject)decision["action"],facts,newer),"Unrelated changed evidence does not revoke a valid action conflict.");
        var schema=JObject.Parse(ActivityMonitorProtocol.SchemaForFrame(frame));
        Check(schema["properties"]["action"]!=null && schema["properties"]["speech"]!=null,"Same transport supports both domains.");
        Check((int)schema.SelectToken("properties.speech.anyOf[1].properties.version.enum[0]")==0,"Native grammar cannot invent the next speech version.");
        var empty=(JObject)frame.DeepClone();empty["actions"]=new JArray();
        Check(((JArray)JObject.Parse(ActivityMonitorProtocol.SchemaForFrame(empty)).SelectToken("properties.action.anyOf")).Count==1,
            "No active action yields only the continue branch, never a hallucinated execution owner.");

        var host=new GameObject("Activity monitor regression");host.SetActive(false);
        try {
            var chat=host.AddComponent<ChatSample>();var model=host.AddComponent<ChatQW>();
            Set(chat,"m_ChatSettings",new ChatSetting{m_ChatModel=model});
            var unit=new ActivityMonitorProtocol.SpeechUnit{Id="speech:1",Text="Saved.",Language="en"};
            var type=typeof(ChatSample).GetNestedType("PendingSpeechChunk",BindingFlags.NonPublic);
            var chunk=Activator.CreateInstance(type,true);Set(chunk,"ProcessUnit",unit);Set(chunk,"Text",unit.Text);Set(chunk,"LanguageCode","en");
            ActivityMonitorProtocol.Revise(unit,(JObject)decision["speech"],out _);
            Check(Call(chat,"RefreshRoomSpeechForPlayback",chunk,0).ToString()=="Replaced","Actual player boundary replaces queued content.");
            Check((string)Get(chunk,"Text")=="The save failed.","TTS receives corrected text.");
            Check(Call(chat,"RefreshRoomSpeechForPlayback",chunk,0).ToString()=="Current","Same revision does not endlessly re-synthesize.");
            var clipType=typeof(ChatSample).GetNestedType("PendingSpeechClip",BindingFlags.NonPublic);
            var staleClip=Activator.CreateInstance(clipType,true);Set(staleClip,"ProcessUnit",unit);Set(staleClip,"Text","Saved.");Set(staleClip,"LanguageCode","en");
            Check(!(bool)typeof(ChatSample).GetMethod("MatchesPreparedSpeech",Flags).Invoke(null,new[]{staleClip,chunk,(object)"The save failed."}),"Old prefetched WAV cannot survive a semantic revision.");
            var ledger=(List<ActivityMonitorProtocol.SpeechUnit>)Get(chat,"m_ActivitySpeech");ledger.Add(unit);
            unit.State="playing";
            Call(chat,"RecordActivityInterruption","The whole candidate was not necessarily heard.","regression");
            var interrupted=(JObject)Get(chat,"m_LastInterruptedSpeechEvidence");
            Check((bool)interrupted["exactSpokenPrefixKnown"]==false && unit.State=="interrupted","Interruption never invents a word-aligned prefix.");

            int stopped=0;var adapters=new Dictionary<string,ChatSample.MonitoredActivity>{["document"]=new ChatSample.MonitoredActivity{
                Read=()=> (JObject)frame["actions"][0].DeepClone(),Stop=(version,reason)=>{stopped++;return version=="v1";}}};
            var onlyAction=Decision("none","stop");
            Call(chat,"ApplyActivityDecision",frame,onlyAction,facts,(JArray)frame["actions"],adapters);
            Check(stopped==1,"A registered non-device/non-movement executor receives its exact-version stop.");
            var live=(JArray)frame["actions"].DeepClone();live[0]["version"]="v2";
            Call(chat,"ApplyActivityDecision",frame,onlyAction,facts,live,adapters);
            Check(stopped==1,"Late monitor cannot stop a replacement action.");
            live=(JArray)frame["actions"].DeepClone();live[0]["cancellable"]=false;
            Call(chat,"ApplyActivityDecision",frame,onlyAction,facts,live,adapters);
            Check(stopped==1,"A completed/uncancellable side effect is not rolled back.");
            var wire=(string)Call(model,"BuildActivityMonitorRequest",frame.ToString());
            checks+=ActivityEvidenceRegression.RunChecks(model);
            Check(!string.IsNullOrEmpty(wire)&&JObject.Parse(wire)["id_slot"]==null,"Concurrent monitor does not seize a pinned foreground slot.");
            Check((bool)((JObject)typeof(ChatQW).GetMethod("MeasureActivityMonitorBudget",Flags).Invoke(null,new object[]{wire}))["allowed"],
                "Final complete wire, including native schema and completion reserve, fits the independent monitor budget.");
            var session=JObject.Parse(File.ReadAllText("Assets/Editor/Fixtures/ActivityMonitor.latest-20261015-1410.json"));
            var stress=(JObject)frame.DeepClone();
            stress["facts"]["dialogue"]=session["dialogue"].DeepClone();
            stress["facts"]["execution"]=new JObject{["priorResults"]=session["delivery"].DeepClone(),["body"]=new JObject{["phase"]="arrived"}};
            stress["facts"]["constraints"]=new JArray("Keep the original, never overwrite it.");
            string admitted=model.PrepareActivityMonitorFrame(stress.ToString(),out string projection);
            Check(admitted!=null,"Complete real dialogue/receipt workload is admitted by projection, without raising a ceiling. "+projection);
            var reduced=JObject.Parse(admitted);
            Check(JToken.DeepEquals(reduced["facts"]["constraints"],stress["facts"]["constraints"]),"Budget reduction never strips user limits.");
            Check(session["dialogue"]["completedAudio"].Count()==23,"The full actual audio history remains in the fixture after projection.");
            var oversized=(JObject)frame.DeepClone();oversized["facts"]["request"]=new string('中',20000);
            Check(model.PrepareActivityMonitorFrame(oversized.ToString(),out _) == null,"Cannot force admission by truncating an oversized authoritative request.");
            foreach(var rejected in ((JArray)session["interventions"]).OfType<JObject>()) {
                var actual=(JObject)rejected["data"]["decision"].DeepClone();actual["claimDomain"]="visual";
                var recordedFrame=((JArray)session["frames"]).OfType<JObject>().Where(x=>(int)x["line"]<(int)rejected["line"])
                    .Last(x=>((JArray)x["data"]["speech"]).OfType<JObject>().Any(u=>(string)u["id"]==(string)actual["speech"]["id"]));
                var replay=(JObject)recordedFrame["data"].DeepClone();replay["facts"]=visual["facts"].DeepClone();
                replay["evidenceScopes"]=visual["evidenceScopes"].DeepClone();
                Check(!ActivityMonitorProtocol.TryRead(actual.ToString(),replay,out _,out _),"The actual recorded false visual correction cannot apply.");
            }
            Check(Call(model,"BuildActivityMonitorRequest",new string('x',ActivityMonitorProtocol.MaximumInputCharacters+1))==null,"Oversized context makes zero network requests.");
            bad=(JObject)decision.DeepClone();bad["speechAct"]="personal-expression";
            Check(!ActivityMonitorProtocol.TryRead(bad.ToString(),frame,out _,out _),"An expression assessment cannot become an intervention.");
            bad=(JObject)decision.DeepClone();bad["claimQuote"]="The original was deleted.";
            Check(!ActivityMonitorProtocol.TryRead(bad.ToString(),frame,out _,out _),"An invented assertion cannot justify altering speech.");
            var autonomous=new JObject{["autonomous"]=true,["ongoingActivityMonitoring"]=true,["autonomousIntention"]="Explore the shelf out of curiosity."};
            Check(!AutonomyPremiseProtocol.Required(autonomous.ToString()),"Live process monitoring replaces the autonomous premise preapproval gate.");
            autonomous["ongoingActivityMonitoring"]=false;
            Check(AutonomyPremiseProtocol.Required(autonomous.ToString()),"Unsupported providers retain the previous bounded premise path.");
        } finally {UnityEngine.Object.DestroyImmediate(host);}
        return checks;
    }
    public static void RunBatch()
    {
        if(!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation",StringComparison.OrdinalIgnoreCase)<0)
            throw new InvalidOperationException("Use isolated validation project.");
        string output=Environment.GetCommandLineArgs().SkipWhile(x=>x!="-activityMonitorOutput").Skip(1).FirstOrDefault();
        if(string.IsNullOrEmpty(output))throw new InvalidOperationException("Output required.");
        Directory.CreateDirectory(output);
        try {int count=RunChecks();File.WriteAllText(Path.Combine(output,"regression.json"),new JObject{["passed"]=true,["checks"]=count}.ToString());EditorApplication.Exit(0);}
        catch(Exception e){File.WriteAllText(Path.Combine(output,"regression.json"),new JObject{["passed"]=false,["error"]=e.ToString()}.ToString());Debug.LogException(e);EditorApplication.Exit(1);}
    }
}
