using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class ConversationRepairRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }
    public static int RunChecks()
    {
        int checks = 0;
        void Check(bool ok, string name) { ++checks; if (!ok) throw new InvalidOperationException("Conversation repair: " + name); }
        ClosedSpeechEvidence Evidence(string text) => new ClosedSpeechEvidence { CaptureSessionSerial = 4,
            Text = text, NormalizedText = ClosedSpeechEvidence.Normalize(text), Source = "closed-stream-comparison",
            ClosedAt = 3f, AudioMs = 2100, SubmittedAudioMs = 3010, StableForSeconds = 1.5f };
        JObject Compare(string a, string b) => TranscriptDisagreementPolicy.Compare(Evidence(a), 4, 4, "full-preview", b, 4f);
        var actual = Compare("跟着我走。", "근데워서.");
        Check(actual != null && actual["selectedTranscript"].Type == JTokenType.Null && !(bool)actual["actionAuthorized"], "actual gen8 cannot silently select either command");
        foreach (var pair in new[] { new[]{"开门。","别开门。"},new[]{"play the recording","delete the recording"},
            new[]{"私について来て","あなたについて行く"},new[]{"音量二十","音量九十"},new[]{"关闭后检查","检查后关闭"} })
            Check(Compare(pair[0], pair[1]) != null, "changed actor/negation/resource/number/order requires adjudication");
        foreach (string text in new[]{"请用英语回答。","日本語で答えて。","Please answer in English.","播放那段录音。"})
            Check(Compare(text,text)==null, "genuine language switches and non-device tasks pass unchanged");
        Check(Compare("Hello!"," hello ")==null, "formatting-only difference needs no model");
        Check(TranscriptDisagreementPolicy.Compare(Evidence("跟着我走"),5,4,"full-preview","근데워서",4f)==null, "old capture ignored");
        Check(TranscriptDisagreementPolicy.Compare(Evidence("跟着我走"),4,4,"rejected","근데워서",4f)==null, "rejected final is not rescued");
        Check(TranscriptDisagreementPolicy.Compare(Evidence("跟着我走"),4,4,"full-preview","근데워서",30f)==null, "expired comparison ignored");
        Check(!TranscriptDisagreementPolicy.Equivalent(actual,"{\"relation\":\"equivalent\",\"streamText\":\"wrong\",\"fullText\":\"근데워서.\"}"), "review must bind both exact hypotheses");
        var tracker = new ClosedSpeechEvidenceTracker(); tracker.Begin(4,0);
        tracker.Observe(4,"跟着我。","",1170,1.17f,true);
        tracker.Observe(4,"跟着我走。","",2100,2.1f,true);
        Check(tracker.TryComparison(4,3.5f,3010,true,true,false,true,out _), "latest session stable extension survives the normal EOU wait");
        Check(!tracker.TryComparison(4,3.5f,6000,true,false,false,true,out _), "uncovered later speech cannot be adjudicated from an old partial");
        Check(!tracker.TryComparison(4,3.5f,3010,true,true,true,true,out _), "pending negation/late revision prevents comparison");
        string feedback=CommittedSpatialReplyProtocol.BuildReviewRepairFeedback("刚才关掉的那盏灯，已经重新打开了哦。",
            new JObject { ["verdict"]="inconsistent",["actualLanguage"]="zh" },"speech-language-mismatch","ja",new JObject(),"",false);
        string projected=CommittedSpatialReplyProtocol.RepairWireFeedback(feedback);
        Check(!projected.Contains("刚才关掉的那盏灯") && projected.Contains("actualLanguage"), "language repair keeps diagnosis without echo anchor");
        Check(CommittedSpatialReplyProtocol.RepairWritingInstruction(projected).Contains("日本語"), "repair remains the final writing task");
        Check(CommittedSpatialReplyProtocol.RepairWritingInstruction("regular tool feedback")=="", "ordinary feedback does not become repair");
        string verdict="{\"explicitStopNow\":false,\"bodyDirective\":\"other-task\",\"force\":\"command-now\",\"reason\":\"New task replaces current body task.\"}";
        var before=RoomControlReviewProtocol.BodyInput("播放录音，然后过来。","body-1",4,"moving",true,false);
        var after=RoomControlReviewProtocol.BodyInput("播放录音，然后过来。","body-1",4,"moving",true,true);
        Check(RoomControlReviewProtocol.SameBodyEvidence(before.ToString(),after.ToString(),verdict), "identical evidence reuses meaning, not execution permission");
        Check(RoomControlReviewProtocol.TryBodyDecision(verdict,"播放录音，然后过来。",false,out var early,out _) && early.intent=="none" &&
            RoomControlReviewProtocol.TryBodyDecision(verdict,"播放录音，然后过来。",true,out var approved,out _) && approved.intent=="stop-moving", "reuse preserves separate replacement permission");
        foreach(string field in new[]{"actionId","operationRevision","phase","following"}) {
            var changed=(JObject)after.DeepClone();changed["activeBody"][field]="changed";
            Check(!RoomControlReviewProtocol.SameBodyEvidence(before.ToString(),changed.ToString(),verdict), "changed body evidence invalidates reuse: "+field);
        }
        after["currentUserText"]="不要播放录音，继续跟随。";
        Check(!RoomControlReviewProtocol.SameBodyEvidence(before.ToString(),after.ToString(),verdict), "later correction cannot borrow prior approval");
        var host=new GameObject("Delivery history regression");host.SetActive(false);
        try {
            var chat=host.AddComponent<ChatSample>();var model=host.AddComponent<ChatQW>();
            var history=new LLM.SendData("assistant","第一句。后半句没有完整播出。");model.m_DataList.Add(history);
            typeof(ChatSample).GetField("m_FormalResponseGeneration",Flags).SetValue(chat,2);
            typeof(ChatSample).GetMethod("BindRoomSpeechPlayback",Flags).Invoke(chat,new object[]{2,"zh",null,model,history});
            typeof(ChatSample).GetMethod("RecordSpeechDeliveryFailure",Flags).Invoke(chat,new object[]{2,"后半句没有完整播出。"});
            typeof(ChatSample).GetMethod("FinishRoomSpeechPlaybackHistory",Flags).Invoke(chat,new object[]{"第一句。"});
            Check(history.content=="第一句。", "read-only reply history contains only completed sentence after TTS failure");
        } finally { UnityEngine.Object.DestroyImmediate(host); }
        return checks + DialogueSemanticsRegression.RunChecks() + SessionRepairRegression.RunChecks() + ContinuityPolicyRegression.RunChecks() + IdentityAttentionRegression.RunChecks() + TaskSemanticsRegression.RunChecks();
    }

    public static IEnumerator RunNative(string fixturePath, string output)
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation",StringComparison.OrdinalIgnoreCase)<0)
            throw new InvalidOperationException("Isolated project required.");
        Directory.CreateDirectory(output);
        yield return ConversationTtsTransportRegression.Run(output);
        var fixture=JObject.Parse(File.ReadAllText(fixturePath)); var rows=new JArray();
        LastReport=new JObject { ["passed"]=false,["pureChecks"]=RunChecks(),["cases"]=rows,
            ["fixtureSource"]=fixture["source"], ["scope"]="Production request builders and actual local model. Recorded dialogue retained. Runtime fact packets reconstructed from recorded receipts; original HTTP bodies were not recorded. No actions executed." };
        LastReport["identityAttention"] = IdentityAttentionRegression.LastReport.DeepClone();
        LastReport["taskSemantics"] = TaskSemanticsRegression.LastReport.DeepClone();
        var host=new GameObject("Conversation repair live");host.SetActive(false);var model=host.AddComponent<ChatQW>();model.enabled=false;
        foreach(var item in ((JObject)fixture["roleConfiguration"]).Properties()) {
            var field=typeof(ChatQW).GetField(item.Name,Flags);
            object value=item.Name=="m_PromptFiles"||item.Name=="m_SkillFiles" ?
                ((JArray)item.Value).Select(x=>UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>((string)x["path"])).ToArray() : item.Value.ToObject(field.FieldType);
            field.SetValue(model,value);
        }
        model.m_PrewarmPrefixOnStart=false;model.m_LogRequestStats=true;model.InitSystemMessage();host.SetActive(true);
        model.m_Backend=ChatQW.BackendType.Local;model.m_LocalUrl="http://127.0.0.1:8080/v1/chat/completions";
        var systemHistory=model.m_DataList.Where(m=>m.role=="system").ToList();
        for(Type type=typeof(ChatQW);type!=null;type=type.BaseType) {var f=type.GetField("url",Flags);if(f!=null){f.SetValue(model,model.m_LocalUrl);break;}}
        try {
            if (fixture["taskSemantics"] is JObject semantics)
                yield return TaskSemanticsRegression.RunNative(model, semantics, output);
            if (fixture["identityAttention"] is JObject identity)
                yield return IdentityAttentionRegression.RunNative(model, identity, output);
            yield return TranscriptComparisonTransportRegression.Run(model,output);
            foreach(JObject item in fixture["workReviews"] as JArray ?? new JArray()) {
                model.m_DataList.Clear();model.m_DataList.AddRange(systemHistory);
                foreach(JObject message in item["history"]??fixture["history"])
                    model.m_DataList.Add(new LLM.SendData((string)message["role"],(string)message["content"]));
                string prompt=(string)item["prompt"];
                if ((bool?)item["productionPrompt"] != true) prompt+=WorkNextIntentProtocol.Contract+
                    (string)typeof(ChatSample).GetField("ScenePlaybackHandoffContract",Flags).GetValue(null);
                string wire=(string)typeof(ChatQW).GetMethod("BuildWorkReviewRequestJson",Flags).Invoke(model,new object[]{prompt});
                File.WriteAllText(Path.Combine(output,(string)item["name"]+"-request.json"),wire);
                PrivateDecisionResult response=null;float began=Time.realtimeSinceStartup;
                model.PostWorkReviewResult(prompt,null,value=>response=value);
                while(response==null&&Time.realtimeSinceStartup-began<30)yield return null;
                JObject decision=null;try{decision=JObject.Parse(response?.Output??"");}catch{}
                var rawDecision = decision?.DeepClone();
                if (decision != null && item["delivery"] is JObject delivery && delivery["results"] is JArray recordedResults)
                {
                    var results = (JArray)recordedResults.DeepClone();
                    foreach (var report in (decision["delivery_reports"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var result = results.OfType<JObject>().FirstOrDefault(r => (string)r["id"] == (string)report["id"]);
                        if (WorkResultDeliveryProtocol.Covers(result, new WorkResultDeliveryProtocol.Report {
                            id = (string)report["id"], coverage = (string)report["coverage"], quote = (string)report["quote"] }, (string)delivery["completedPlaybackText"])) result["delivered"] = true;
                    }
                    if (WorkResultDeliveryProtocol.IsSettledActionQuote((string)decision["remaining_request_quote"], item["sequence"] as JObject,
                        results.OfType<JObject>().Where(r => (bool?)r["delivered"] == true).Select(r => (string)r["source"]))) decision["remaining_request_quote"] = "";
                    if ((string)decision["next_intent_source"] != "autonomous" &&
                        WorkResultDeliveryProtocol.CanClose(results, item["sequence"] as JObject, (string)decision["remaining_request_quote"])) decision["work_status"] = "closed";
                    else if (WorkResultDeliveryProtocol.MustContinue((string)decision["work_status"], results,
                        (string)decision["remaining_request_quote"], (string)(item["history"] as JArray)?.OfType<JObject>().LastOrDefault(m => (string)m["role"] == "user")?["content"]))
                    {
                        // Same production refusal of premature closure: version
                        // validity is not delegated to the language model.
                        decision["work_status"] = "continue"; decision["proceed"] = true;
                        decision["next_intent_source"] = "current-work";
                        decision["next_step_id"] = ""; decision["planning_focus"] = "other";
                    }
                }
                // Apply the same continuation admission as the runtime after
                // coverage. A proposal to repeat an ended step is reply-only.
                if ((string)decision?["work_status"] == "continue" && WorkNextIntentProtocol.ReplyOnly(
                    item["sequence"] as JObject, (string)decision["next_intent_source"], (string)decision["next_step_id"]))
                {
                    decision["next_step_id"] = ""; decision["next_intent_source"] = "current-work";
                    decision["planning_focus"] = "other";
                }
                bool passed=response?.Succeeded==true&&(string)decision?["work_status"]==(string)item["expectedStatus"];
                if((string)item["expectedStatus"]=="continue") passed &= (string)decision?["next_intent_source"]=="current-work";
                if(item["expectedPlanningFocus"]!=null) passed &= (string)decision?["planning_focus"]==(string)item["expectedPlanningFocus"];
                if((bool?)item["expectedNoActionStep"]==true) passed &= string.IsNullOrEmpty((string)decision?["next_step_id"]);
                if (item["expectedDeliveredIds"] is JArray expectedIds)
                    passed &= expectedIds.Values<string>().All(id => (decision?["delivery_reports"] as JArray)?.OfType<JObject>().Any(r =>
                        (string)r["id"] == id && (string)r["coverage"] == "reported" && !string.IsNullOrWhiteSpace((string)r["quote"]) &&
                        ((string)item["completedPlaybackText"] ?? "").Contains((string)r["quote"])) == true);
                if ((bool?)item["expectedRemainingQuestion"] == true)
                    passed &= !string.IsNullOrWhiteSpace((string)decision?["remaining_request_quote"]);
                rows.Add(new JObject{["name"]=item["name"],["passed"]=passed,["rawDecision"]=rawDecision,["response"]=decision,["status"]=response?.Status,
                    ["seconds"]=Time.realtimeSinceStartup-began,["actionsExecuted"]=false});
                File.WriteAllText(Path.Combine(output,"conversation-repair.json"),LastReport.ToString());
            }
            foreach(JObject item in fixture["comparisons"]) {
                var packet=new JObject { ["capture"]=4,["stream"]=new JObject{["text"]=item["stream"]},["full"]=new JObject{["text"]=item["full"]} };
                bool equivalent=false;float began=Time.realtimeSinceStartup;
                yield return model.ReviewTranscriptComparison(packet,()=>true,value=>equivalent=value);
                rows.Add(new JObject{["name"]=item["name"],["equivalent"]=equivalent,["passed"]=equivalent==(bool)item["equivalent"],["seconds"]=Time.realtimeSinceStartup-began});
            }
            foreach(JObject item in fixture["eventReviews"] as JArray ?? new JArray()) {
                model.m_DataList.Clear();model.m_DataList.AddRange(systemHistory);
                foreach(JObject message in item["history"]??fixture["history"])model.m_DataList.Add(new LLM.SendData((string)message["role"],(string)message["content"]));
                var input=(JObject)item["input"].DeepClone();
                bool done=false,ok=false;string response=null,status=null;float began=Time.realtimeSinceStartup;
                model.PostRoomTaskMessage(input.ToString(),true,(success,value,detail)=>{ok=success;response=value;status=detail;done=true;});
                while(!done&&Time.realtimeSinceStartup-began<30)yield return null;
                if(!done)model.CancelRoomTaskPlanningMessage();
                bool allowed=ok&&RoomTaskProtocol.ReviewAllows(response);
                bool expectedState=item["expectedState"]==null || ok&&(string)JObject.Parse(response)["resultReport"]==(string)item["expectedState"];
                rows.Add(new JObject{["name"]=item["name"],["allowed"]=allowed,["review"]=response,["status"]=status,
                    ["passed"]=done&&ok&&expectedState&&allowed==(bool)item["allowed"],["seconds"]=Time.realtimeSinceStartup-began});
                File.WriteAllText(Path.Combine(output,"conversation-repair.json"),LastReport.ToString());
            }
            foreach(JObject item in fixture["repairs"]) {
                model.m_DataList.Clear();
                model.m_DataList.AddRange(systemHistory);
                foreach(JObject message in item["history"]??fixture["history"]) model.m_DataList.Add(new LLM.SendData((string)message["role"],(string)message["content"]));
                int count=model.m_DataList.Count;
                string historyBefore=Newtonsoft.Json.JsonConvert.SerializeObject(model.m_DataList);
                string context=(string)item["context"], language=(string)item["language"];
                string baseline=(string)typeof(ChatSample).GetMethod("CurrentReplyRepairBaseline",Flags).Invoke(null,new object[]{context,language});
                string feedback=(bool)item["terminal"] ? SpatialGoalResultContextPolicy.BuildRepairFeedback((string)item["rejected"],(JObject)item["review"],(string)item["reason"],language,baseline) :
                    CommittedSpatialReplyProtocol.BuildReviewRepairFeedback((string)item["rejected"],(JObject)item["review"],(string)item["reason"],language,(JObject)item["humanInput"],context,true,baseline);
                var stage=(bool)item["terminal"]?FormalResponseStage.SpatialGoalResult:FormalResponseStage.CommittedSpatialReply;
                string wire=(string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages",Flags,null,new[]{typeof(System.Collections.Generic.List<LLM.SendData>),typeof(bool),typeof(string),typeof(string),typeof(FormalResponseStage)},null)
                    .Invoke(model,new object[]{model.m_DataList,false,context,feedback,stage});
                File.WriteAllText(Path.Combine(output,(string)item["name"]+"-request.json"),wire);
                var buffer=new SpeechTextBuffer();bool done=false;string raw=null;float began=Time.realtimeSinceStartup;
                model.PostSpeechFeedbackStreamForStage(context,feedback,part=>buffer.Append(part),value=>{raw=value;done=true;},null,false,stage);
                while(!done && Time.realtimeSinceStartup-began<30)yield return null;
                if(!done)model.CancelActiveResponse();
                string text=buffer.ToString();
                bool changed=text.Trim()!=(string)item["rejected"],envelope=done&&RoomTaskProtocol.IsPlainSpeech(text)&&buffer.LanguageCode==language;
                var row=new JObject{["name"]=item["name"],["raw"]=raw,["text"]=text,["changed"]=changed,["envelopeValid"]=envelope,
                    ["historyUnchanged"]=model.m_DataList.Count==count&&Newtonsoft.Json.JsonConvert.SerializeObject(model.m_DataList)==historyBefore,
                    ["seconds"]=Time.realtimeSinceStartup-began,["factualBaseline"]=baseline};
                // Independently inspect the actual returned words with the production classifier.
                string response=null,status=null;bool reviewed=false,ok=false;
                var reviewInput=(JObject)item["reviewInput"].DeepClone(); reviewInput["publicSpeech"]=text;reviewInput["publicSpeechLanguage"]=buffer.LanguageCode;
                if ((string)reviewInput["intendedAction"]=="follow") reviewInput["movementReportRequirement"]=RoomMovementResultPolicy.Requirement(
                    (JObject)reviewInput["currentRoomFacts"],"follow",(string)reviewInput["actionId"]);
                model.PostRoomTaskMessage(reviewInput.ToString(),true,(success,value,detail)=>{ok=success;response=value;status=detail;reviewed=true;});
                float reviewAt=Time.realtimeSinceStartup;while(!reviewed&&Time.realtimeSinceStartup-reviewAt<30)yield return null;
                if(!reviewed)model.CancelRoomTaskPlanningMessage();
                row["review"]=response;row["reviewStatus"]=status;
                row["passed"]=envelope&&changed&&(bool)row["historyUnchanged"]&&ok&&RoomTaskProtocol.ReviewAllows(response);
                rows.Add(row);
                File.WriteAllText(Path.Combine(output,"conversation-repair.json"),LastReport.ToString());
            }
            if (fixture["continuity"] is JObject continuity)
                yield return ContinuityLiveRegression.Run(model, continuity, output);
            LastReport["passed"]=rows.All(x=>(bool)x["passed"]);
            File.WriteAllText(Path.Combine(output,"conversation-repair.json"),LastReport.ToString());
            if(!(bool)LastReport["passed"])throw new InvalidOperationException("Native conversation repair cases failed; see report.");
        } finally { UnityEngine.Object.Destroy(host); }
    }
}
