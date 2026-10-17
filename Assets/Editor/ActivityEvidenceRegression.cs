using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class ActivityEvidenceRegression
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    private static int checks;
    private static void Check(bool test,string message) { checks++; if(!test)throw new InvalidOperationException("Activity evidence: "+message); }
    private static JObject Fixture()=>ActivityEvidencePolicy.Parse(File.ReadAllText("Assets/Editor/Fixtures/ActivityMonitor.correction-20261015-2130.json"));
    private static JObject Upgrade(JObject frame,IEnumerable<JObject> results=null)
    {
        frame=(JObject)frame.DeepClone();frame["evidenceVersion"]=2;
        var facts=(JObject)frame["facts"];var scopes=(JObject)frame["evidenceScopes"];
        foreach(var field in facts.Properties())
            if((string)scopes[field.Name]?["domain"]=="execution" && field.Value is JObject obj)
                scopes[field.Name]["binding"]=ActivityEvidencePolicy.Binding(obj,(string)obj["intent"]??(string)obj["kind"]??field.Name,(string)obj["actionId"]);
        var receipts=(results??new JObject[0]).ToArray();
        ActivityEvidencePolicy.AddReceipts(facts,scopes,receipts);
        foreach(var unit in (frame["speech"] as JArray??new JArray()).OfType<JObject>()) {
            var snapshot=unit["observedAtSpeechStart"] as JObject??unit["observedAtCreation"] as JObject;
            if(snapshot==null)unit["observedAtCreation"]=snapshot=new JObject();
            snapshot["completedResultIds"]=new JArray(receipts.Select(r=>(string)r["id"]));
        }
        if(frame["delivery"] is JObject delivery)delivery["playbackRevision"]=ActivityEvidencePolicy.Stamp(delivery["completedPlaybackText"]);
        return frame;
    }
    private static JObject Recorded(JObject fixture)
    {
        var receipt=((JArray)fixture["deliveryFrames"]).OfType<JObject>().First(f=>(int)f["owner"]["generation"]==7)["delivery"] as JObject;
        var allResults=(JArray)((JArray)fixture["events"]).OfType<JObject>().Single(e=>(string)e["kind"]=="Work/ResultDelivery")["data"]["results"];
        var frame=Upgrade((JObject)fixture["falseFrame"],allResults.OfType<JObject>());
        // Return settled after this phrase started; it cannot refute its past tense.
        frame["speech"][0]["observedAtSpeechStart"]["completedResultIds"]=new JArray((string)receipt["id"]);
        // Replay the complete accepted dialogue available before this frame,
        // including opening audio and user turns. Production attention then
        // selects adjacent phrases; this is not a hand-written short fixture.
        var history=new Dictionary<string,JObject>();
        foreach(var e in ((JArray)fixture["events"]).OfType<JObject>().Where(e=>(int)e["line"]<76115)) {
            var eventData=e["data"] as JObject;
            foreach(var row in (eventData?["facts"]?["dialogue"] as JArray??new JArray()).OfType<JObject>()) {
                string identity=(string)row["role"]=="user"?"user:"+(string)row["userTurn"]:(string)row["source"];
                if(!string.IsNullOrEmpty(identity)&&!history.ContainsKey(identity)) {
                    history[identity]=(JObject)row.DeepClone();history[identity]["recordedLine"]=e["line"].DeepClone();
                }
            }
            if((string)e["kind"]=="Activity/SpeechProgress" && (string)e["data"]?["state"]=="completed")
                history[(string)e["data"]["id"]]=new JObject{["role"]="assistant",["text"]=e["data"]["text"],
                    ["source"]=e["data"]["id"],["recordedLine"]=e["line"].DeepClone()};
        }
        frame["facts"]["dialogue"]=new JArray(history.Values.OrderBy(r=>(int)r["recordedLine"]));
        return frame;
    }
    public static int RunChecks(ChatQW model)
    {
        checks=0;var fixture=Fixture();var frame=Recorded(fixture);
        Check(((JArray)fixture["events"]).Count==1811,"Full real session is retained with original audio/ASR and completion order.");
        Check(((JArray)frame["facts"]["dialogue"]).OfType<JObject>().Count(r=>(string)r["role"]=="user")==3,
            "All three accepted user turns before the erroneous correction enter the replay before purpose projection.");
        foreach(string qualifier in new[]{"その瞬間には","当时","at that moment","……","never","没有","見ていなかった"})
            Check(ActivityMonitorProtocol.ScopeOnlyQualifier(qualifier),"Scope/punctuation is not epistemic uncertainty: "+qualifier);
        foreach(string qualifier in new[]{"その瞬間だったかもしれない","当时可能","maybe earlier","I am not sure","might never","可能没有","多分見ていなかった"})
            Check(!ActivityMonitorProtocol.ScopeOnlyQualifier(qualifier),"Actual qualified guesses remain free: "+qualifier);
        var time=new JObject{["observedUtc"]="2026-10-15T12:28:24.4465776Z"};
        Check(!JToken.DeepEquals(time,JObject.Parse(time.ToString())) && ActivityEvidencePolicy.Equal(time,JObject.Parse(time.ToString())),"Date token/string round-trip cannot invent a changed premise.");
        Check(ActivityEvidencePolicy.Equal(new JValue((string)null),JValue.CreateNull()),"Runtime null-string values normalize without crashing the monitor tick.");
        var changed=(JObject)time.DeepClone();changed["observedUtc"]="2026-10-15T12:28:25.4465776Z";
        Check(!ActivityEvidencePolicy.Equal(time,changed)&&ActivityEvidencePolicy.Differences(time,changed).Count==1,"Real timestamps still invalidate with field diagnostics.");
        Check((string)WorkResultDeliveryProtocol.Result("tool","completed",time)["id"]==(string)WorkResultDeliveryProtocol.Result("tool","completed",JObject.Parse(time.ToString()))["id"],"Result version identity ignores timestamp token representation.");
        Check((string)WorkResultDeliveryProtocol.Result("tool","completed",time)["id"]!=(string)WorkResultDeliveryProtocol.Result("tool","completed",changed)["id"],"Real result timestamp change still creates a new version.");
        string key=((JObject)frame["facts"]).Properties().First(p=>p.Name.StartsWith("result:")).Name;
        foreach(int pressure in new[]{0,1,2}) {
            var projected=ActivityMonitorContextPolicy.Project(frame,pressure);
            Check(projected["facts"][key]!=null && (bool)projected["facts"][key]["facts"]["outcome"]["completionEvidenceValid"],"Completed substep proof survives pressure "+pressure);
        }
        Check(!ActivityMonitorProtocol.TryRead(fixture["falseDecision"].ToString(),frame,out _,out _),"Actual wrong reassurance-to-device-denial verdict is rejected.");
        var unavailable=(JObject)frame.DeepClone();
        unavailable["evidenceScopes"]["observation"]=ActivityMonitorContextPolicy.Scope("visual");
        var malformed=(JObject)fixture["falseDecision"].DeepClone();malformed.Remove("grounding");
        malformed["claimDomain"]="visual";malformed["speechAct"]="qualified-guess";malformed["qualificationQuote"]="not in the utterance";
        malformed["speech"]["issue"]="uncertainty";malformed["speech"]["evidenceIds"]=new JArray("request");
        Check(!ActivityMonitorProtocol.TryRead(malformed.ToString(),unavailable,out _,out var nonfactualReason) &&
            nonfactualReason=="speech-act-not-factual" && !ActivityMonitorContextPolicy.NeedsNewSource(nonfactualReason,malformed.ToString(),unavailable),
            "A proposed correction of a declared guess first requires semantic repair, never automatic visual acquisition.");
        malformed["speechAct"]="factual-claim";malformed["claimQuote"]=frame["speech"][0]["text"].DeepClone();
        Check(!ActivityMonitorProtocol.TryRead(malformed.ToString(),unavailable,out _,out var unavailableReason) &&
            unavailableReason=="speech-evidence-domain-or-coverage-mismatch" &&
            ActivityMonitorContextPolicy.NeedsNewSource(unavailableReason,malformed.ToString(),unavailable),
            "A literal factual claim with unavailable evidence requires a new source before correction.");
        var crossed=(JObject)fixture["falseDecision"].DeepClone();crossed["claimBinding"]=frame["evidenceScopes"][key]["binding"].DeepClone();
        crossed["speech"]["proof"]=new JObject{["evidenceId"]="execution",["evidenceQuote"]="arrived",["scope"]="same-explicit-proposition"};
        Check(!ActivityMonitorProtocol.TryRead(crossed.ToString(),frame,out _,out _),"Returning to the user cannot refute another operation on another entity.");
        var frames=new List<JObject>{frame};frames.AddRange(((JArray)fixture["parallelFrames"]).OfType<JObject>().Select(f=>Upgrade(f)));
        frames.AddRange(((JArray)fixture["deliveryFrames"]).OfType<JObject>().Select(f=>Upgrade(f)));
        foreach(var source in frames)foreach(var lane in ActivityEvidencePolicy.AttentionFrames(source)) {
            string input=model.PrepareActivityMonitorFrame(lane.ToString(),out var diagnostics);
            Check(input!=null,"Complete purpose-specific wire must fit unchanged limit: "+diagnostics);
            var parsed=ActivityEvidencePolicy.Parse(input);
            Check(((JArray)parsed["speech"]).Count==0 || ((JArray)parsed["actions"]).Count==0,"Concurrent actions and speech use independent schemas.");
            Check(ActivityEvidencePolicy.Equal(source["facts"]["constraints"],parsed["facts"]["constraints"]),"Original constraints survive admission.");
        }
        var failed=new ActivityMonitorAttempts();
        failed.Failed("same-work",0,"speech-truth-operation-entity-mismatch",fixture["falseDecision"].ToString());
        foreach(var retryFrame in ActivityEvidencePolicy.AttentionFrames(frame)) {
            retryFrame["previousAttempt"]=failed.Feedback("same-work");
            Check(model.PrepareActivityMonitorFrame(retryFrame.ToString(),out var retryBudget)!=null,"A real rejected verdict plus all completed receipts fits each operation's bounded retry projection: "+retryBudget);
        }
        var parallel=Upgrade((JObject)fixture["parallelFrames"][0]);
        var actionLane=ActivityEvidencePolicy.AttentionFrames(parallel).First();
        actionLane["facts"]["execution"]["body"]["execution"]["phase"]="moving";
        var noise=(JObject)actionLane.DeepClone();noise["facts"]["execution"]["body"]["execution"]["reason"]="new telemetry sentence";
        Check(ActivityEvidencePolicy.SemanticKey(noise)==ActivityEvidencePolicy.SemanticKey(actionLane),"Sampling reason noise shares bounded attempts.");
        noise["owner"]["generation"]=999;
        Check(ActivityEvidencePolicy.SemanticKey(noise)!=ActivityEvidencePolicy.SemanticKey(actionLane),"New owner remains a separate semantic work item.");
        var fault=(JObject)actionLane.DeepClone();fault["facts"]["execution"]["body"]["execution"]["phase"]="failed";
        var newFault=(JObject)fault.DeepClone();newFault["facts"]["execution"]["body"]["execution"]["reason"]="different real failure";
        Check(ActivityEvidencePolicy.SemanticKey(fault)!=ActivityEvidencePolicy.SemanticKey(newFault),"Real failure reasons remain semantic changes.");
        var delivery=(JObject)((JArray)fixture["deliveryFrames"]).OfType<JObject>().First(f=>(int)f["owner"]["generation"]==7)["delivery"].DeepClone();
        delivery["playbackRevision"]=ActivityEvidencePolicy.Stamp(delivery["completedPlaybackText"]);
        var report=new WorkResultDeliveryProtocol.Report{id=(string)delivery["id"],coverage="reported",quote="ちゃんと消したわ。"};
        Check(WorkResultDeliveryProtocol.CoverageRejection(delivery,report,(string)delivery["completedPlaybackText"])=="playback-revision-not-audited","Earlier positive substring alone cannot settle later retraction.");
        report.playedRevision=(string)delivery["playbackRevision"];report.lastRelevantQuote="まだ灯りは消していません。";report.finalDisposition="retracted";
        Check(!WorkResultDeliveryProtocol.Covers(delivery,report,(string)delivery["completedPlaybackText"]),"Actual negative correction keeps delivery open.");
        delivery["latestCorrectionStart"]=((string)delivery["completedPlaybackText"]).IndexOf("えっと、ごめんなさい",StringComparison.Ordinal);
        report.finalDisposition="consistent";report.lastRelevantQuote=report.quote;
        Check(WorkResultDeliveryProtocol.CoverageRejection(delivery,report,(string)delivery["completedPlaybackText"])=="playback-quote-precedes-result-correction",
            "Even a mistaken coverage verdict cannot use a quote before the actual scoped correction.");
        delivery["completedPlaybackText"]=(string)delivery["completedPlaybackText"]+"確認すると、ランプは消せています。";
        delivery["playbackRevision"]=ActivityEvidencePolicy.Stamp(delivery["completedPlaybackText"]);
        Check(!WorkResultDeliveryProtocol.Covers(delivery,report,(string)delivery["completedPlaybackText"]),"New playback invalidates older audit.");
        report.playedRevision=(string)delivery["playbackRevision"];report.quote=report.lastRelevantQuote="確認すると、ランプは消せています。";report.finalDisposition="consistent";
        Check(WorkResultDeliveryProtocol.Covers(delivery,report,(string)delivery["completedPlaybackText"]),"Actual final correction can settle without redoing execution.");
        var actionFact=new JObject{["id"]="job",["version"]="1",["kind"]="edit",["phase"]="running",["cancellable"]=true};
        var actionFrame=new JObject{["speech"]=new JArray(),["actions"]=new JArray(actionFact.DeepClone()),
            ["facts"]=new JObject{["action:job"]=actionFact,["request"]=new JObject{["text"]="Do not edit the original."}},
            ["evidenceScopes"]=new JObject{["request"]=ActivityMonitorContextPolicy.Scope("request","mistake")}};
        Check(((JArray)JObject.Parse(ActivityMonitorProtocol.SchemaForFrame(actionFrame)).SelectToken("properties.action.anyOf"))
            .OfType<JObject>().Select(branch=>(int?)branch.SelectToken("properties.reason.maxLength")).Distinct().Count()==1,
            "A longer reason cannot grammatically force a continue decision into a stop branch.");
        var stop=new JObject{["action"]=new JObject{["operation"]="stop",["id"]="job",["version"]="1",["reason"]="Original edit conflicts with explicit constraint.",
            ["evidenceIds"]=new JArray("action:job"),["conflict"]=new JObject{["basis"]="constraint-conflict",["requirementEvidenceId"]="request",
                ["requirementQuote"]="Do not edit the original.",["observedQuote"]="edit",["assessment"]="conflict-even-considering-prerequisites"}}};
        Check(ActivityMonitorProtocol.TryRead(stop.ToString(),actionFrame,out var normalized,out _) &&
            ((JArray)normalized["action"]["evidenceIds"]).Values<string>().Contains("request"),"Explicit proof references enter canonical citations even without redundant list entry.");
        var newFacts=(JObject)actionFrame["facts"].DeepClone();newFacts["request"]["text"]="Edit the original.";
        Check(!ActivityMonitorProtocol.EvidenceCurrent(normalized["action"] as JObject,actionFrame["facts"] as JObject,newFacts),"Derived proof citation is still checked against live constraints.");
        stop["action"]["conflict"]["requirementQuote"]="Invented restriction";
        Check(!ActivityMonitorProtocol.TryRead(stop.ToString(),actionFrame,out _,out _),"Canonical citation does not bypass actual quotation validation.");
        var unknownFrame=new JObject{["evidenceVersion"]=2,["facts"]=new JObject{["history"]=new JObject{["events"]=new JArray()},["request"]="Tell me what happened."},
            ["evidenceScopes"]=new JObject{["history"]=ActivityMonitorContextPolicy.Scope("history","uncertainty"),["request"]=ActivityMonitorContextPolicy.Scope("request","mistake")}};
        var unknownVerdict=new JObject{["claimDomain"]="history",["speech"]=new JObject{["issue"]="uncertainty",["evidenceIds"]=new JArray("history","request"),
            ["proof"]=new JObject{["evidenceId"]="history",["evidencePath"]="events",["evidenceQuote"]="[]",["scope"]="same-explicit-proposition"}}};
        Check(ActivityMonitorContextPolicy.SpeechEvidenceRejection(unknownVerdict,unknownFrame)==null,"Explicit unknown-only empty history supports narrow uncertainty.");
        unknownVerdict["speech"]["issue"]="mistake";
        Check(ActivityMonitorContextPolicy.SpeechEvidenceRejection(unknownVerdict,unknownFrame)!=null,"Empty records never become a negative event proof.");
        unknownVerdict["speech"]["issue"]="uncertainty";unknownVerdict["speech"]["proof"]["evidenceId"]="request";
        unknownVerdict["speech"]["proof"]["evidencePath"]="";unknownVerdict["speech"]["proof"]["evidenceQuote"]="Tell me";
        Check(ActivityMonitorContextPolicy.SpeechEvidenceRejection(unknownVerdict,unknownFrame)!=null,"A separate qualified citation cannot lend authority to a request quotation.");
        var distanceSpeech=(JObject)actionLane.DeepClone();distanceSpeech["purpose"]="speech";
        distanceSpeech["facts"]["execution"]["body"]["actualUserDistance"]=1.0;
        var movedSpeech=(JObject)distanceSpeech.DeepClone();movedSpeech["facts"]["execution"]["body"]["actualUserDistance"]=4.0;
        Check(ActivityEvidencePolicy.SemanticKey(distanceSpeech)!=ActivityEvidencePolicy.SemanticKey(movedSpeech),"A changed distance can reopen current-state speech monitoring.");
        return checks;
    }
    private static JObject GeneralFrame(string request,string speech,string operation,string following,string status,string language)
    {
        var result=WorkResultDeliveryProtocol.Result("step:1",status,new JObject{["operation"]=operation,["actionId"]="first-operation",["entityId"]="target-A",["actor"]="character",["status"]=status});
        var frame=new JObject{["owner"]=new JObject{["generation"]=1,["workEpoch"]=1},["facts"]=new JObject{
            ["request"]=new JObject{["text"]=request},["execution"]=new JObject{["actionId"]="second-operation",["intent"]=following,["body"]=new JObject{["phase"]="running"}}},
            ["evidenceScopes"]=new JObject{["request"]=ActivityMonitorContextPolicy.Scope("request","mistake"),["execution"]=ActivityMonitorContextPolicy.Scope("execution","mistake","world-change","uncertainty")},
            ["speech"]=new JArray(new ActivityMonitorProtocol.SpeechUnit{Id="speech:1",Text=speech,Language=language,State="playing",ObservedAtSpeechStart=new JObject()}.Snapshot()),["actions"]=new JArray()};
        frame["speech"][0]["observedAtSpeechStart"]=frame["facts"]["execution"].DeepClone();
        return Upgrade(frame,new[]{result});
    }
    public static IEnumerator RunNative(string output)
    {
        var host=new GameObject("Bound evidence native regression");host.SetActive(false);
        var model=host.AddComponent<ChatQW>();model.enabled=false;model.m_PrewarmPrefixOnStart=false;
        model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";
        for(Type t=typeof(ChatQW);t!=null;t=t.BaseType){var f=t.GetField("url",Flags);if(f!=null){f.SetValue(model,model.m_LocalUrl);break;}}
        host.SetActive(true);var rows=new JArray();bool all=true;
        try {
            int pure=RunChecks(model);var fixture=Fixture();var cases=new List<(string name,JObject frame,string expected)>();
            var recorded=Recorded(fixture);cases.Add(("recorded-reassurance-after-completion",recorded,"none"));
            var completed=(JObject)recorded.DeepClone();completed["speech"][0]["state"]="completed";cases.Add(("late-reassurance",completed,"none"));
            var negative=(JObject)recorded.DeepClone();negative["speech"][0]["text"]="まだ灯りは消していません。";cases.Add(("actual-false-negative",negative,"mistake"));
            foreach(var item in new[]{
                new[]{"save-read","Save the draft, then read it.","The draft is saved. Now I'm reading it.","save","read","completed","en","none"},
                new[]{"edit-preview","コピーを編集してからプレビューして。","コピーの編集は完了したわ。","edit","preview","completed","ja","none"},
                new[]{"retrieve-summarize","检索资料然后总结。","资料已经找到了。","retrieve","summarize","completed","zh","none"},
                new[]{"generate-display","Generate an image and show it.","The image is ready.","generate","display","completed","en","none"},
                new[]{"observe-return","先看看窗户再回来。","窗户已经看过了。","observe","return","completed","zh","none"},
                new[]{"sing-prepare","Sing the selected song then prepare another.","I finished that song.","sing","prepare-next-song","completed","en","none"},
                new[]{"save-failure","Save the draft then read it.","The draft was saved successfully.","save","read","failed","en","mistake"},
                new[]{"save-failure-ja","保存してから読んで。","保存できたわ。","save","read","failed","ja","mistake"},
                new[]{"save-failure-zh","保存录音再检查它。","录音已经保存好了。","save","inspect","failed","zh","mistake"},
                new[]{"subjective-freedom","把文件保存。","接下来我想写一首诗。","save","read","completed","zh","none"},
                new[]{"invitation-freedom","Save the draft.","Would you like to hear a song?","save","read","completed","en","none"}})
                cases.Add((item[0],GeneralFrame(item[1],item[2],item[3],item[4],item[5],item[6]),item[7]));
            int n=0;
            foreach(var original in ((JArray)fixture["deliveryFrames"]).OfType<JObject>()) {
                var delivery=Upgrade(original);cases.Add(("recorded-delivery-"+(n++),delivery,
                    (int)delivery["owner"]["generation"]==14?"covered":"supplement"));
            }
            foreach(string status in new[]{"completed","failed"}) {
                var chain=GeneralFrame("Only preview after saving succeeds. Keep the original unchanged.","","save","preview",status,"en");
                chain["speech"]=new JArray();
                var action=new JObject{["id"]="preview-job",["version"]="1",["kind"]="preview",["phase"]="running",["cancellable"]=true,
                    ["facts"]=new JObject{["operation"]="preview",["target"]="target-A"},["task"]=new JObject{["relationship"]="task-step",["purpose"]="Preview after successful save."}};
                chain["actions"]=new JArray(action);chain["facts"]["action:preview-job"]=action.DeepClone();
                chain["evidenceScopes"]["action:preview-job"]=ActivityMonitorContextPolicy.Scope("execution","mistake");
                cases.Add(("action-after-"+status+"-step",chain,status=="completed"?"action:continue":"action:stop"));
            }
            foreach(var history in new[]{new[]{"ja","その瞬間にはテレビ台の灯りを見ていなかったの。"},
                new[]{"en","You never moved that document earlier."},new[]{"zh","你之前没有保存过这份笔记。"}}) {
                var historyFrame=new JObject{["facts"]=new JObject(),["speech"]=new JArray(new ActivityMonitorProtocol.SpeechUnit{
                    Id="speech:history",Text=history[1],Language=history[0],State="playing"}.Snapshot()),["actions"]=new JArray()};
                typeof(ActivityMonitorPlayRegression).GetMethod("BuildCurrentSessionFrame",Flags).Invoke(null,new object[]{historyFrame,"current-history-"+history[0]});
                cases.Add(("historical-uncertainty-"+history[0],historyFrame,"uncertainty"));
            }
            foreach(var item in cases) {
                var source=item.frame;
                var attention=ActivityEvidencePolicy.AttentionFrames(source).ToArray();
                var selected=new List<JObject>(attention.Where(f=>(string)f["purpose"]!="speech"));
                var interpretations=new JArray();bool understood=true;
                foreach(var speechFrame in attention.Where(f=>(string)f["purpose"]=="speech").GroupBy(ActivitySpeechMeaningPolicy.Key).Select(g=>g.First())) {
                    var input=ActivitySpeechMeaningPolicy.Input(speechFrame);JObject meaning=null;string rawMeaning=null;bool valid=false;
                    for(int i=0;i<2&&!valid;i++) {
                        string ready=model.PrepareActivityMonitorFrame(input.ToString(),out string readBudget);bool done=false,ok=false;
                        if(ready!=null){model.PostActivityMonitor(ready,(a,b,c)=>{done=true;ok=a;rawMeaning=b;});float until=Time.realtimeSinceStartup+11;while(!done&&Time.realtimeSinceStartup<until)yield return null;}
                        valid=ok&&ActivitySpeechMeaningPolicy.TryRead(rawMeaning,input,out meaning);
                        interpretations.Add(new JObject{["raw"]=rawMeaning,["valid"]=valid,["budget"]=JObject.Parse(readBudget)});
                        if(!valid)input["previousAttempt"]=new JObject{["rejectedVerdict"]=rawMeaning,["fieldRepair"]=ActivitySpeechMeaningPolicy.Rejection(rawMeaning,input)};
                    }
                    if(valid)selected.AddRange(ActivitySpeechMeaningPolicy.Frames(speechFrame,meaning));else understood=false;
                }
                if(!understood) {
                    all=false;rows.Add(new JObject{["case"]=item.name,["passed"]=false,["interpretations"]=interpretations});
                    File.WriteAllText(Path.Combine(output,"activity-evidence-native.json"),new JObject{["passed"]=all,["pureChecks"]=pure,["cases"]=rows}.ToString());
                    continue;
                }
                foreach(var selectedFrame in selected) {
                var frame=selectedFrame;
                bool parsed=false;JObject verdict=null;string why=null,raw=null,budget=null;var attempts=new JArray();var retries=new ActivityMonitorAttempts();
                for(int attempt=0;attempt<3&&!parsed;attempt++) {
                    string input=model.PrepareActivityMonitorFrame(frame.ToString(),out budget);
                    Check(input!=null,"Native complete payload exceeds limit: "+item.name+" "+budget);frame=ActivityEvidencePolicy.Parse(input);
                    frame["monitorRequestId"]=Guid.NewGuid().ToString("N");input=frame.ToString(Newtonsoft.Json.Formatting.None);
                    bool done=false,ok=false;model.PostActivityMonitor(input,(success,text,status)=>{done=true;ok=success;raw=text;why=status;});
                    float start=Time.realtimeSinceStartup;while(!done&&Time.realtimeSinceStartup-start<11f)yield return null;
                    parsed=done&&ok&&ActivityMonitorProtocol.TryRead(raw,frame,out verdict,out why);
                    attempts.Add(new JObject{["raw"]=raw,["parsed"]=parsed,["reason"]=why});
                    if(!parsed) {retries.Failed(item.name,Time.realtimeSinceStartup,why,raw);frame["previousAttempt"]=retries.Feedback(item.name);}
                }
                string actual=(string)verdict?["delivery"]?["operation"]??(string)verdict?["speech"]?["issue"];
                if(item.expected.StartsWith("action:")) {
                    actual="action:"+(string)verdict?["action"]?["operation"];
                    if(actual=="action:reconsider")actual="action:stop";
                }
                bool semanticPassed=parsed&&actual==item.expected;
                bool protectedAbstention=!parsed && item.expected=="none" &&
                    new[]{"speech-uncertainty-with-definite-outcome","speech-phase-not-entity-state-evidence"}.Contains(why);
                if(protectedAbstention)Check(verdict==null && !retries.CanAttempt(item.name,Time.realtimeSinceStartup+100) &&
                    ActivityEvidencePolicy.Equal(frame["speech"]?[0]?["text"],source["speech"]?[0]?["text"]),
                    "Protected abstention applies no verdict, retains the original speech and exhausts the same-work retry budget.");
                bool passed=semanticPassed||protectedAbstention;all&=passed;
                rows.Add(new JObject{["case"]=item.name,["claimIndex"]=frame["claimIndex"],["interpretations"]=interpretations,["expected"]=item.expected,["source"]=source,["sent"]=frame,["attempts"]=attempts,
                    ["modelSemanticPassed"]=semanticPassed,["protectedAbstention"]=protectedAbstention,["reviewed"]=parsed,
                    ["passed"]=passed,["budget"]=JObject.Parse(budget)});
                File.WriteAllText(Path.Combine(output,"activity-evidence-native.json"),new JObject{["passed"]=all,["pureChecks"]=pure,["cases"]=rows}.ToString());
                }
            }
            Check(all,"Native decisions or explicit protected abstentions must preserve actual events, final audio order and autonomy; inspect modelSemanticPassed separately.");
        } finally {model.CancelActivityMonitor();UnityEngine.Object.Destroy(host);}
    }
}
