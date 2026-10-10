using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Actual captured lamps and production matching, with controlled semantic
/// replies. Verifies standing-goal references independently of model accuracy.</summary>
public static class SceneGoalReferentRegression
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private const string OffRequest="请把刚才看到的左边这盏小灯关掉。",OnRequest="把刚才关掉的那盏灯重新打开。",Reminder="继续啊，怎么不动啊？";
    private static int checks;
    private static string directory;
    private static GameObject fixture;
    private static readonly List<Material> materials=new List<Material>();
    public static JObject LastReport { get; private set; }
    public static JObject LastRecentVisualSearchReport { get; private set; }
    private sealed class MatchRun
    {
        public bool done;
        public object fact;
        public SceneObservedObject selected;
        public string failure;
        public NevaSceneVisionIntegrationModel.Request request;
    }

    public static IEnumerator RunChecks(ChatSample chat,ArdyRoomDialogueBridge bridge,NevaSceneVisionIntegrationModel model,string output)
        => RunChecksCore(chat,bridge,model,output,false);

    public static IEnumerator RunDialogueRoutingChecks(ChatSample chat,ArdyRoomDialogueBridge bridge,NevaSceneVisionIntegrationModel model,string output)
        => RunChecksCore(chat,bridge,model,output,true);

    public static IEnumerator RunRecentVisualSearchChecks(ChatSample chat,ArdyRoomDialogueBridge bridge,NevaSceneVisionIntegrationModel model,string output)
    {
        try { yield return RunChecksCore(chat,bridge,model,output,false,true); }
        finally { LastRecentVisualSearchReport=LastReport?.DeepClone() as JObject; }
    }

    private static IEnumerator RunChecksCore(ChatSample chat,ArdyRoomDialogueBridge bridge,NevaSceneVisionIntegrationModel model,string output,bool dialogueOnly,bool recentVisualOnly=false)
    {
        if(!Application.isBatchMode||!Application.isPlaying||Application.dataPath.IndexOf("unity-naturalness-validation",StringComparison.OrdinalIgnoreCase)<0)
            throw new InvalidOperationException("Goal-referent checks require the isolated real-room Play fixture.");
        checks=0;materials.Clear();directory=Path.Combine(output,recentVisualOnly?"recent-visual-search":dialogueOnly?"scene-dialogue-routing":"scene-goal-referent");Directory.CreateDirectory(directory);
        LastReport=new JObject { ["status"]="running",["cases"]=new JArray(),
            ["scope"]="Production matching and actual source-linked RGB/ID lamps. A real off write/fresh receipt establishes prior identity; matcher and wrong-bound-output rejection probes never write. Actual RoomTask retained-goal and pending-proposal clarification routes independently review new answers before real same-device on/off operations and fresh readbacks, with one audited short result per outcome and no step narration. The prohibition case never registers or executes a goal. Semantic replies are controlled; no real-model accuracy evaluation or HTTP/audio synthesis." };
        string[] fields={"m_EnableSceneActions","m_EnableSceneObjectAwareness","m_EnableSceneObjectTargetMatching","m_EnableScreenVision","m_AgentEyesOpen",
            "m_AgentRunning","m_FormalResponseInFlight","m_AgentRoundInFlight","m_WorkEpoch","m_WorkStatus","m_WorkReviewOpen","m_SpatialDeviceGoal","m_SpatialGoalLatestReview","m_PendingDeviceClarification","m_PendingDeviceClarificationReviewRequest","m_IndependentSpatialRequestScope",
            "m_SelectedSceneObject","m_SelectedSceneDevice","m_SceneResultDevice","m_SceneResultEnvironment","m_SceneResultAvatar","m_SceneObjectStateResult",
            "m_SceneObjectMatchReceipt","m_SceneObservationGoal","m_SceneObjectCandidateView","m_SceneMatchEnvironment","m_SceneMatchAvatar","m_SceneMatchUserTurn",
            "m_SceneActionFact","m_SpatialGoalTerminalReplyGeneration","m_SpatialGoalTerminalProof",
            "m_EnableFirstReplyRouting","m_AgentCurrentRoundIsTick","m_UserSpeechActiveForAutonomy","m_StreamComplete","m_TTSSenderDone",
            "m_ConsecutiveAITurns","m_WorkNoProgress","m_UserTurnAwaitingReplySince","m_LastUserMsg","m_LastUserEvidence",
            "m_AgentGracefulShutdownPending","m_MonologueBackstopTurns","m_MaxConsecutiveAITurns","m_SceneAutonomyIntention",
            "m_AnswerThenResumeObservation","m_AnswerThenResumeGoalId","m_AnswerThenResumeUserTurn","m_ReviewedGoalAnswerObligation","m_RoomTaskSpatialAnswer"};
        var saved=fields.Select(name=>Get(chat,name)).ToArray();
        var ledger=Get(chat,"m_ActionConstraints");string[] ledgerFields={"currentText","revision","<CurrentUserTurn>k__BackingField"};
        var ledgerSaved=ledgerFields.Select(name=>Get(ledger,name)).ToArray();
        string[] collectionFields={"m_VerifiedSpatialDeviceHistory","m_VerifiedObservedDeviceHistory","m_KnownSceneObjects","m_RecentSceneObjects","m_SceneDeviceChangedAt","m_SpatialGoalActions","m_SpatialGoalActionOrder","m_SceneSpatialViewSamples","m_SceneViewStarts"};
        var collections=collectionFields.Select(name=>Get(chat,name)).ToArray();var collectionSaved=collections.Select(SnapshotCollection).ToArray();
        var stack=new Stack<IEnumerator>();
        try
        {
            // A running visual session is a real authorization prerequisite.
            // Retire the preceding runner's deferred transaction, then reserve
            // round/formal ownership to suppress private ticks during held replies.
            typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags|BindingFlags.Static).Invoke(null,new object[]{chat});
            Call(chat,"ClearPendingDeviceClarification","goal-referent-fixture-new-scope");
            for(int i=0;i<5;i++)Set(chat,fields[i],true);
            Set(chat,"m_AgentRunning",true);Set(chat,"m_AgentRoundInFlight",true);Set(chat,"m_FormalResponseInFlight",true);
            Set(chat,"m_SpatialDeviceGoal",null);Set(chat,"m_IndependentSpatialRequestScope",null);Set(chat,"m_WorkEpoch",(int)Get(chat,"m_WorkEpoch")+1);
            ((IList)Get(chat,"m_VerifiedSpatialDeviceHistory")).Clear();((IList)Get(chat,"m_VerifiedObservedDeviceHistory")).Clear();
            Check(chat.SceneEyesOpen,"Actual running visual session stays open while held round/formal ownership prevents autonomous ticks.");
            Check(bridge!=null&&bridge.IsConnected&&!bridge.ReservesBody&&!bridge.IsFollowing,"Actual room bridge is idle.");
            Call(chat,"EnsureSceneObjectCatalog");var capture=(NevaSceneVisionCapture)Call(chat,"EnsureSceneVisionCapture");capture.SetObserving(true);
            if(dialogueOnly) LastReport["scope"]="Actual RoomTask review/planning/formal speech requests with controlled semantic responses and actual RGB capture. No model accuracy, device operation or audible playback is claimed; response/TTS completion is held. New proposal and question routing preserve owner/cancellation checks.";
            if(recentVisualOnly) LastReport["scope"]="Production empty-source request projection/strict response normalization, current RoomTask goal registration and search handoff, rejection fallback and cancellation. Controlled semantic replies and real RGB; no live model, device state write, full search execution or audible speech is claimed.";
            stack.Push(recentVisualOnly?ExerciseRecentVisualSearch(chat,bridge,model,capture):dialogueOnly?ExerciseDialogueRouting(chat,bridge,model,capture):Exercise(chat,bridge,model,capture));
            while(stack.Count>0)
            {
                object next=null;
                try
                {
                    while(stack.Count>0)
                    {
                        var routine=stack.Peek();
                        if(!routine.MoveNext()){(routine as IDisposable)?.Dispose();stack.Pop();continue;}
                        if(routine.Current is IEnumerator nested){stack.Push(nested);continue;}
                        next=routine.Current;break;
                    }
                }
                catch(Exception error){LastReport["status"]="failed";LastReport["error"]=error.ToString();throw;}
                if(stack.Count>0)yield return next;
            }
            LastReport["status"]="passed";
        }
        finally
        {
            while(stack.Count>0)(stack.Pop() as IDisposable)?.Dispose();
            Call(chat,"CancelSceneObjectTargetMatch");Call(chat,"CancelSpatialGoalRunner","goal-referent-fixture-finished");
            Call(chat,"CancelSceneAction","goal-referent-fixture-finished");Call(chat,"CancelSpatialGoal","goal-referent-fixture-finished");
            Call(chat,"InvalidateFormalResponse","goal-referent-fixture-finished");chat.StopAllCoroutines();Call(chat,"ClearScheduledAgentWake");
            if(fixture!=null)UnityEngine.Object.DestroyImmediate(fixture);fixture=null;
            foreach(var material in materials)if(material!=null)UnityEngine.Object.DestroyImmediate(material);materials.Clear();
            for(int i=0;i<collections.Length;i++)RestoreCollection(collections[i],collectionSaved[i]);
            for(int i=0;i<fields.Length;i++)Set(chat,fields[i],saved[i]);
            for(int i=0;i<ledgerFields.Length;i++)Set(ledger,ledgerFields[i],ledgerSaved[i]);
            LastReport["checks"]=checks;File.WriteAllText(Path.Combine(directory,"report.json"),LastReport.ToString());
        }
    }

    private static IEnumerator ExerciseRecentVisualSearch(ChatSample chat,ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model,NevaSceneVisionCapture capture)
    {
        void Reset(string text)
        {
            Call(chat,"CancelSpatialGoalRunner","recent-visual-case");Call(chat,"CancelSpatialGoal","recent-visual-case");
            Call(chat,"CancelSceneAction","recent-visual-case");Call(chat,"ClearPendingDeviceClarification","recent-visual-case");
            typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags|BindingFlags.Static).Invoke(null,new object[]{chat});
            Set(chat,"m_EnableFirstReplyRouting",false);Set(chat,"m_SpatialDeviceGoal",null);Set(chat,"m_IndependentSpatialRequestScope",null);
            Set(chat,"m_SpatialGoalLatestReview",null);Call(chat,"RecordAcceptedMotionUserTurn",text);Call(chat,"ResetWorkReview",true);
            ((IList)Get(chat,"m_VerifiedSpatialDeviceHistory")).Clear();((IList)Get(chat,"m_VerifiedObservedDeviceHistory")).Clear();
            Set(chat,"m_AgentRoundInFlight",true);Set(chat,"m_FormalResponseInFlight",true);
        }
        JObject Proposal(string text,string state="off") => new JObject {
            ["intent"]="none",["replyScope"]="ordinary",["origin"]="user",["evidence"]=text,
            ["deviceGoal"]=new JObject { ["operation"]="set",["requestQuote"]=text,["targetDescription"]=text,["desiredState"]=state } };
        string Normalize(string input,string assessment,string verdict)
        {
            var args=new object[]{input,new JObject { ["operationRequest"]=assessment=="not-requested"?"none":assessment=="uncertain"?"uncertain":"affirmative",
                ["targetReference"]=assessment=="target-unclear"?"ambiguous":"described",
                ["proposalMatches"]=verdict=="approved"?"yes":assessment=="requested"?"no":"uncertain",
                ["reason"]="Controlled independent semantic verdict; no identity or execution claim." }.ToString(),null,null};
            Check((bool)typeof(ChatQW).GetMethod("TryNormalizeDeviceGoalReviewOutput",Flags|BindingFlags.Static).Invoke(null,args),
                "Production normalizer accepts the closed semantic verdict.");
            Check(RoomTaskProtocol.TryDeviceGoalProposalReview((string)args[2],out var review)&&review.referentKind=="unbound"&&review.referentSourceId=="none"&&
                review.referentGoalId==null&&review.referentEntityId==null,"Only the engine supplies the fixed unbound pair; no entity can be invented.");
            return (string)args[2];
        }
        foreach(string text in new[] {"去把刚才你看到的那盏灯关掉。","Turn off the reading light you saw near the bookcase.","さっきテレビ台の上に見えた扇風機を止めて。","把它关掉。"})
        {
            Reset(text);SceneVisionObservation source=null;yield return Capture(chat,"recent-visual-source",value=>source=value);
            var antecedent=new LLM.SendData("assistant","电视柜上有一盏装饰灯。");
            if(text=="把它关掉。")model.m_DataList.Add(antecedent);
            int rooms=model.Decisions.Count,reviews=model.DeviceGoalReviews.Count,searches=model.SceneSearchRequests.Count;
            int matches=model.Matches.Count,operation=bridge.Controller.OperationRevision;Quaternion rotation=bridge.avatar.transform.rotation;
            Call(chat,"StartStreaming",text,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Current remembered-object command reaches planning");
            model.Decisions.Last().Complete(true,Proposal(text).ToString(),"controlled-recent-object-proposal");
            yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,8,"New operation receives independent review");
            var request=model.DeviceGoalReviews.Last();var input=JObject.Parse(request.Input);var wire=JObject.Parse(request.Wire);
            var human=JObject.Parse((string)wire["messages"][1]["content"]);
            Check(((JArray)input["referentSources"]).Count==0&&human.Count==3&&(string)human["currentUserText"]==text&&
                human["proposal"] is JObject&&human["referentSources"]==null&&human["recentDialogue"]==null&&
                human["referenceContext"] is JObject&&
                JToken.DeepEquals(wire["response_format"]["schema"],JObject.Parse(RoomTaskProtocol.BuildUnboundDeviceGoalReviewSchema())),
                "Actual production wire asks only about human semantics; empty source identity is not a model decision.");
            foreach(var malformed in new[] {
                "{\"verdict\":\"approved\",\"requestAssessment\":\"not-requested\",\"reason\":\"bad\"}",
                "{\"verdict\":\"approved\",\"requestAssessment\":\"requested\",\"reason\":\"bad\",\"referentSourceId\":\"observed-device:invented\"}",
                "{\"verdict\":\"approved\",\"verdict\":\"approved\",\"requestAssessment\":\"requested\",\"reason\":\"bad\"}"})
                Check(!(bool)typeof(ChatQW).GetMethod("TryNormalizeDeviceGoalReviewOutput",Flags|BindingFlags.Static).Invoke(null,new object[]{request.Input,malformed,null,null}),
                    "Malformed, contradictory or forged-source semantic responses cannot authorize a task.");
            request.Complete(true,Normalize(request.Input,"requested","approved"),"controlled-unbound-operation-approved");
            yield return Wait(()=>model.SceneSearchRequests.Count==searches+1,8,"Approved unbound goal reaches bounded search without a repeated user command");
            var goal=Goal(chat);var search=JObject.Parse(model.SceneSearchRequests.Last().Input);
            Check((string)goal["originalRequest"]==text&&(string)goal["targetDescription"]==text&&
                string.IsNullOrEmpty((string)goal["boundEntityId"])&&Call(chat,"GetActiveSpatialGoalId")!=null&&
                model.Matches.Count==matches&&bridge.Controller.OperationRevision==operation&&Quaternion.Angle(rotation,bridge.avatar.transform.rotation)<.1f,
                "Search retains the complete human description and remains unbound; neither dialogue nor approval moves the body or writes a device.");
            Check(JToken.DeepEquals(goal["referenceContext"],human["referenceContext"])&&
                JToken.DeepEquals(search["task"]?["referenceContext"],human["referenceContext"]),
                "The same bounded dialogue reference snapshot reaches the registered goal and search without becoming entity evidence.");
            if(text=="把它关掉。")
            {
                Check(human["referenceContext"].ToString().Contains(antecedent.content),"A pronoun keeps its actual dialogue antecedent as a descriptive search clue.");
                model.m_DataList.Remove(antecedent);
                Check(JToken.DeepEquals(Goal(chat)["referenceContext"],human["referenceContext"]),"Later dialogue changes cannot silently replace the frozen target reference.");
            }
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="approved-description-search",["userText"]=text,["goal"]=goal,["searchInput"]=search,
                ["wireHumanInput"]=human,["independentReviews"]=model.DeviceGoalReviews.Count-reviews });
        }
        foreach(var item in new[] {
            new {Text="Look at the lamp you saw earlier; leave its power unchanged.",Assessment="not-requested",Verdict="rejected"},
            new {Text="さっき見えた扇風機は止めないで。",Assessment="not-requested",Verdict="rejected"},
            new {Text="把它关掉。",Assessment="target-unclear",Verdict="uncertain"},
            new {Text="There are two identical lamps. Turn off the one I mean.",Assessment="target-unclear",Verdict="uncertain"}})
        {
            Reset(item.Text);var input=new JObject { ["mode"]="new-proposal",["currentUserText"]=item.Text,
                ["proposal"]=Proposal(item.Text)["deviceGoal"].DeepClone(),["referentSources"]=new JArray() };
            string normalized=Normalize(input.ToString(),item.Assessment,item.Verdict);
            Check(RoomTaskProtocol.TryDecision(Proposal(item.Text).ToString(),item.Text,false,out var proposal)&&
                RoomTaskProtocol.TryDeviceGoalProposalReview(normalized,out var review)&&
                !(bool)Call(chat,"TryRegisterReviewedSpatialGoal",new object[]{proposal,item.Text,review,null})&&Call(chat,"GetActiveSpatialGoalId")==null,
                "Negative, observation-only and genuinely unspecified targets never become operation authority merely because no sources exist.");
        }
        {
            const string text="Turn the television on.";Reset(text);SceneVisionObservation source=null;
            yield return Capture(chat,"rejected-command-source",value=>source=value);
            int rooms=model.Decisions.Count,reviews=model.DeviceGoalReviews.Count,speech=model.Speech.Count,searches=model.SceneSearchRequests.Count;
            int operation=bridge.Controller.OperationRevision;
            Call(chat,"StartStreaming",text,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            for(int attempt=0;attempt<2;attempt++)
            {
                int index=attempt;
                yield return Wait(()=>model.Decisions.Count==rooms+index+1,8,"Bounded rejected proposal planner");
                model.Decisions.Last().Complete(true,Proposal(text).ToString(),"controlled-wrong-off-state");
                yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+index+1,8,"Wrong state receives independent rejection");
                var request=model.DeviceGoalReviews.Last();request.Complete(true,Normalize(request.Input,"requested","rejected"),"controlled-wrong-state-rejected");
            }
            yield return Wait(()=>model.Decisions.Count==rooms+3,8,"Two rejected interpretations exhaust the existing bounded proposal route");
            model.Decisions.Last().Complete(true,"{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\",\"replyScope\":\"spatial\"}","controlled-no-executable-plan");
            yield return Wait(()=>model.Speech.Count==speech+1,8,"Rejected action request receives one truthful response");
            int generation=(int)Get(chat,"m_FormalResponseGeneration");var reply=model.Speech.Last();
            Check((bool)Call(chat,"CurrentGenerationHasRejectedDeviceRequest",generation)&&
                !(bool)Call(chat,"CurrentGenerationRequiresReviewedGoalAnswer",generation)&&reply.Context.Contains("requestedDevicePlanRejected"),
                "A rejected command remains a command in writer facts, not an inferred direction question.");
            string fallback=(string)Call(chat,"RoomTaskFallback",bridge);
            Check(fallback=="这次没有得到可执行的动作计划，没有启动新的动作。","Command fallback says what actually failed and does not blame user understanding.");
            foreach(string language in new[]{"zh","ja","en"})
            {
                string localized=(string)typeof(ChatSample).GetMethod("LocalizeRoomFallback",Flags|BindingFlags.Static).Invoke(null,new object[]{fallback,language});
                Check(!string.IsNullOrWhiteSpace(localized)&&!localized.Contains("question")&&!localized.Contains("質問")&&!localized.Contains("方位"),
                    "Rejected command localization does not revert to a question/direction answer.");
            }
            const string falseSuccess="I turned the television on.";
            reply.SpeechCallback(new SpeechText(falseSuccess,"en",actionDependency:SpeechActionDependency.Independent));reply.SpeechCompleteCallback(falseSuccess);
            yield return Wait(()=>model.Decisions.Count==rooms+4,8,"False completion remains held for current speech review");
            model.Decisions.Last().Complete(true,"{\"verdict\":\"inconsistent\",\"reason\":\"No action was dispatched.\",\"factScope\":\"scene-action\"}","controlled-no-action-proof");
            yield return null;string published=model.m_DataList.Last().content;
            Check(published=="I didn't obtain an executable action plan, so I haven't started a new action."&&
                model.SceneSearchRequests.Count==searches&&bridge.Controller.OperationRevision==operation,
                "Actual completed reply path releases action-plan failure rather than false completion or a question fallback.");
            Set(chat,"m_FormalResponseGeneration",generation+1);
            Check(!(bool)Call(chat,"CurrentGenerationHasRejectedDeviceRequest",generation+1),"Old rejection cannot contaminate the next generation.");
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="rejected-command-public-fallback",["publishedText"]=published });
        }
        {
            const string text="Turn off the light you just saw.";Reset(text);SceneVisionObservation source=null;
            yield return Capture(chat,"cancelled-review-source",value=>source=value);
            int rooms=model.Decisions.Count,reviews=model.DeviceGoalReviews.Count,searches=model.SceneSearchRequests.Count;
            Call(chat,"StartStreaming",text,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Cancellation case reaches planner");model.Decisions.Last().Complete(true,Proposal(text).ToString(),"controlled-held-proposal");
            yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,8,"Cancellation holds current independent review");var request=model.DeviceGoalReviews.Last();
            Reset("Do not operate that light.");request.Complete(true,Normalize(request.Input,"requested","approved"),"controlled-stale-approval");yield return null;
            Check(Call(chat,"GetActiveSpatialGoalId")==null&&model.SceneSearchRequests.Count==searches,
                "A late approved unbound review cannot revive a cancelled or replaced user turn.");
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="cancelled-review-cannot-create-search" });
        }
    }

    private static IEnumerator ExerciseDialogueRouting(ChatSample chat,ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model,NevaSceneVisionCapture capture)
    {
        void Reset(string text)
        {
            Call(chat,"CancelSpatialGoalRunner","dialogue-routing-case");Call(chat,"CancelSpatialGoal","dialogue-routing-case");
            Call(chat,"CancelSceneAction","dialogue-routing-case");Call(chat,"ClearPendingDeviceClarification","dialogue-routing-case");
            typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags|BindingFlags.Static).Invoke(null,new object[]{chat});
            Set(chat,"m_EnableFirstReplyRouting",false);Set(chat,"m_SpatialDeviceGoal",null);Set(chat,"m_IndependentSpatialRequestScope",null);
            Set(chat,"m_SpatialGoalLatestReview",null);Call(chat,"RecordAcceptedMotionUserTurn",text);Call(chat,"ResetWorkReview",true);
            Set(chat,"m_AgentRoundInFlight",true);Set(chat,"m_FormalResponseInFlight",true);
        }
        var spatialAnswerProbes=new JArray();
        foreach(var questionCase in new[] {
            new {Id="zh-recalled-clock",Text="你刚才在书架旁看到那个钟了吗？为什么朝另一边转？",Cancel=false},
            new {Id="en-cabinet-direction",Text="Which direction is the cabinet you saw earlier? Answer from what you remember; do not move or start searching.",Cancel=false},
            new {Id="ja-projector-memory",Text="さっき見たプロジェクターの位置を覚えている？今は動かず、覚えていることを教えて。",Cancel=true}
        })
        {
            string question=questionCase.Text;bool cancel=questionCase.Cancel;
            Reset(question);SceneVisionObservation source=null;yield return Capture(chat,"spatial-answer-question",value=>source=value);
            int rooms=model.Decisions.Count,speech=model.Speech.Count,searches=model.SceneSearchRequests.Count,operation=bridge.Controller.OperationRevision;
            Call(chat,"StartStreaming",question,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Spatial question reaches the existing RoomTask planner");
            string probeDirectory=Path.Combine(directory,"spatial-answer-probes");Directory.CreateDirectory(probeDirectory);
            var planner=model.Decisions.Last();
            File.WriteAllText(Path.Combine(probeDirectory,questionCase.Id+".input.json"),planner.Input);
            File.WriteAllText(Path.Combine(probeDirectory,questionCase.Id+".request.json"),planner.Wire);
            File.WriteAllText(Path.Combine(probeDirectory,questionCase.Id+".source.metadata.json"),source.MetadataJson);
            spatialAnswerProbes.Add(new JObject { ["id"]=questionCase.Id,["inputFile"]=questionCase.Id+".input.json",
                ["requestFile"]=questionCase.Id+".request.json",["sourceMetadata"]=questionCase.Id+".source.metadata.json",
                ["expected"]=new JObject { ["intent"]="none",["replyScope"]="spatial",["deviceGoalAbsent"]=true },
                ["semanticRequirement"]="Answer the actual memory/direction/reason question from available evidence or state uncertainty. No new search, turn, device task, or invented room-layout direction." });
            File.WriteAllText(Path.Combine(probeDirectory,"manifest.json"),new JObject { ["cases"]=spatialAnswerProbes,
                ["scope"]="Untouched actual RoomTask provider inputs/wires over real Unity RGB. User questions and expected routing are controlled; no real-model requests, ASR or model accuracy claims. Frozen wires may include separately marked historical images." }.ToString());
            // Deliberately omit replyScope: legacy/partial no-action output must not
            // create the former unguarded free-motion reply route.
            model.Decisions.Last().Complete(true,"{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}","controlled-omitted-reply-scope");
            yield return Wait(()=>model.Speech.Count==speech+1,8,"Spatial no-action reply is prepared without search");
            int generation=(int)Get(chat,"m_FormalResponseGeneration");var reply=model.Speech.Last();
            Check((bool)Call(chat,"ShouldGuardRoomSpeech",generation)&&reply.Context.Contains("spatialReplyFacts")&&
                reply.Context.Contains("sceneSpatialMemory")&&reply.Context.Contains("spatialEvidenceIncludedInVisualContext")&&
                model.SceneSearchRequests.Count==searches&&bridge.Controller.OperationRevision==operation,
                "Even omitted-scope none receives source-linked spatial facts and submits no motion or search.");
            const string unsupported="我现在转回来，再找找书架旁的钟。";
            int history=model.m_DataList.Count;reply.SpeechCallback(new SpeechText(unsupported,"zh",actionDependency:SpeechActionDependency.Independent));
            Check(model.m_DataList.Count==history&&bridge.Controller.OperationRevision==operation,
                "An unreviewed claim of turning stays held and cannot cause an action.");
            reply.SpeechCompleteCallback(unsupported);
            yield return Wait(()=>model.Decisions.Count==rooms+2,8,"No-action spatial answer receives actual speech review");
            var review=model.Decisions.Last();var facts=JObject.Parse(review.Input);
            Check(review.Kind=="room-review"&&(bool?)facts["spatialReplyFacts"]?["newSpatialActionSubmitted"]==false&&
                (string)facts["intendedAction"]=="none","Reviewer sees that no new spatial action was submitted.");
            if(cancel)
            {
                Reset("继续聊刚才的话题，不用移动。");int newHistory=model.m_DataList.Count;
                review.Complete(true,"{\"verdict\":\"consistent\",\"reason\":\"Late stale callback\",\"factScope\":\"none\"}","controlled-late-review");
                yield return null;
                Check(model.m_DataList.Count==newHistory&&bridge.Controller.OperationRevision==operation,
                    "A new input revokes the held spatial-answer review; its late callback releases no text or action.");
            }
            else
            {
                review.Complete(true,"{\"verdict\":\"inconsistent\",\"reason\":\"No new turn or search was submitted.\",\"factScope\":\"scene-action\"}","controlled-unsupported-new-turn");
                yield return null;string published=model.m_DataList.Last().content;
                Check(published!=unsupported&&published.Contains("没有执行新的移动或搜索")&&
                    model.SceneSearchRequests.Count==searches&&bridge.Controller.OperationRevision==operation,
                    "The actual public fallback reports uncertainty and no new action instead of publishing a false turn promise.");
                LastReport["ordinarySpatialAnswerGuard"]=new JObject { ["reviewInput"]=facts,["publishedText"]=published };
            }
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"]=(cancel?"ordinary-spatial-answer-new-input-revokes-held-review-":"ordinary-spatial-answer-omitted-scope-still-guarded-")+questionCase.Id });
        }
        {
            const string chatText="今天想聊音乐，不需要动作。";Reset(chatText);
            SceneVisionObservation source=null;yield return Capture(chat,"ordinary-reply-scope",value=>source=value);
            int rooms=model.Decisions.Count,speech=model.Speech.Count,operation=bridge.Controller.OperationRevision;
            Call(chat,"StartStreaming",chatText,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Ordinary conversation planner");
            model.Decisions.Last().Complete(true,"{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\",\"replyScope\":\"ordinary\"}","controlled-ordinary-reply-scope");
            yield return Wait(()=>model.Speech.Count==speech+1,8,"Ordinary conversation formal reply");
            Check(!(bool)Call(chat,"ShouldGuardRoomSpeech",(int)Get(chat,"m_FormalResponseGeneration"))&&bridge.Controller.OperationRevision==operation,
                "Explicit ordinary conversation is not forced into a spatial action or extra review.");
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="explicit-ordinary-reply-retains-conversation-route" });
        }
        {
            const string request="请找一下书架旁的钟。";Reset(request);
            SceneVisionObservation source=null;yield return Capture(chat,"search-overall-result",value=>source=value);
            int rooms=model.Decisions.Count,speech=model.Speech.Count,searches=model.SceneSearchRequests.Count,operation=bridge.Controller.OperationRevision;
            Call(chat,"StartStreaming",request,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Independent find planner");
            model.Decisions.Last().Complete(true,new JObject { ["intent"]="search-scene",["origin"]="user",["evidence"]=request,
                ["searchTask"]=new JObject { ["mode"]="find" } }.ToString(),"controlled-search-entry");
            yield return Wait(()=>model.SceneSearchRequests.Count>searches,8,"Actual bounded search request");
            int serviced=searches;float searchDeadline=Time.realtimeSinceStartup+10;
            while(model.Speech.Count==speech)
            {
                if(Time.realtimeSinceStartup>searchDeadline)throw new TimeoutException("Bounded historical inspection before search done");
                if(model.SceneSearchRequests.Count>serviced)
                {
                    var search=model.SceneSearchRequests[serviced++];
                    if ((bool?)JObject.Parse(search.Input)["recovery"]?["onlyNewView"] == true)
                        search.Complete(false,"","controlled-recovery-provider-unavailable");
                    else
                    search.Complete(true,"{\"choice\":\"done\",\"selectionRole\":\"exploration\",\"referenceId\":\"\",\"reason\":\"Cannot confirm the complete target from this checked page.\"}","controlled-page-only-search-incomplete");
                }
                yield return null;
            }
            yield return Wait(()=>model.Speech.Count==speech+1,8,"Incomplete search result formal reply");
            var reply=model.Speech.Last();const string partial="我已经看过这个方向了。";
            reply.SpeechCallback(new SpeechText(partial,"zh",actionDependency:SpeechActionDependency.Independent));reply.SpeechCompleteCallback(partial);
            yield return Wait(()=>model.Decisions.Count==rooms+2,8,"Whole-search result public review");
            var review=model.Decisions.Last();var facts=JObject.Parse(review.Input);
            Check((bool?)facts["sceneSearchReportRequirement"]?["required"]==true&&
                (string)facts["sceneSearchReportRequirement"]?["expectedReport"]=="incomplete"&&
                (bool?)facts["sceneSearchReportRequirement"]?["searchAttempted"]==true,
                "A checked page with zero body motion is still an attempted but incomplete search, not a last-turn result.");
            review.Complete(true,"{\"verdict\":\"consistent\",\"reason\":\"Only reports the viewed direction.\",\"factScope\":\"scene-action\",\"resultReport\":\"not-applicable\"}","controlled-omitted-overall-result");
            yield return null;string published=model.m_DataList.Last().content;
            Check(published!=partial&&published.Contains("搜索还没有确认")&&bridge.Controller.OperationRevision==operation,
                "The runtime rejects an otherwise consistent substep-only answer and publishes the true incomplete search outcome.");
            LastReport["wholeSearchResultGuard"]=new JObject { ["reviewInput"]=facts,["publishedText"]=published };
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="page-only-search-incomplete-requires-overall-result" });
        }
        var proposalCases=new[] {
            new {Id="ordinary-followup",Text="还有什么吗？",Assessment="not-requested",Verdict="rejected",Target="空气净化器"},
            new {Id="english-quoted-operation",Text="The sign says turn off the projector. What does it mean?",Assessment="not-requested",Verdict="rejected",Target="projector"},
            new {Id="japanese-negated-operation",Text="扇風機は消さないで。ほかに何が見える？",Assessment="not-requested",Verdict="rejected",Target="扇風機"},
            new {Id="wrong-object-proposal",Text="Turn the television on.",Assessment="requested",Verdict="rejected",Target="fan"},
            new {Id="wrong-object-proposal-budget",Text="Turn the television on.",Assessment="requested",Verdict="rejected",Target="fan"},
            new {Id="reviewer-uncertain",Text="What did you understand about that device?",Assessment="uncertain",Verdict="uncertain",Target="that device"}
        };
        foreach(var item in proposalCases)
        {
            Reset(item.Text);SceneVisionObservation source=null;yield return Capture(chat,"dialogue-routing-"+item.Id,value=>source=value);
            int rooms=model.Decisions.Count,reviews=model.DeviceGoalReviews.Count,speech=model.Speech.Count,matches=model.Matches.Count,searches=model.SceneSearchRequests.Count;
            int operation=bridge.Controller.OperationRevision;Quaternion body=bridge.avatar.transform.rotation;
            Call(chat,"StartStreaming",item.Text,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Actual current request reaches proposal planner");
            model.Decisions.Last().Complete(true,new JObject { ["intent"]="none",["replyScope"]="ordinary",["origin"]="user",["evidence"]=item.Text,
                ["deviceGoal"]=new JObject { ["operation"]="set",["requestQuote"]=item.Text,["targetDescription"]=item.Target,["desiredState"]="off" } }.ToString(),"controlled-wrong-device-interpretation");
            yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,8,"Wrong device interpretation receives independent semantic review");
            model.DeviceGoalReviews.Last().Complete(true,new JObject { ["verdict"]=item.Verdict,["requestAssessment"]=item.Assessment,
                ["relation"]="new",["referentKind"]="unbound",["referentSourceId"]="none",["reason"]="Controlled complete-utterance classification; draft is not an approved task." }.ToString(),"controlled-classified-proposal-rejection");
            yield return Wait(()=>model.Decisions.Count==rooms+2,8,"Rejected non-pending interpretation returns to the actual current request");
            if(item.Assessment=="requested")
            {
                var correction=model.Decisions.Last();var correctedInput=JObject.Parse(correction.Input);
                Check((int?)correctedInput["proposalInterpretationCorrection"]?["maximumAttempts"]==1&&
                    (bool?)correctedInput["currentRequestOnly"]!=true&&Call(chat,"GetActiveSpatialGoalId")==null,
                    "A real operation request with a wrong proposal receives exactly one independent reinterpretation opportunity.");
                correction.Complete(true,new JObject { ["intent"]="none",["replyScope"]="ordinary",["origin"]="user",["evidence"]=item.Text,
                    ["deviceGoal"]=new JObject { ["operation"]="set",["requestQuote"]=item.Text,
                        ["targetDescription"]=item.Id.EndsWith("budget")?"fan":"television",["desiredState"]=item.Id.EndsWith("budget")?"off":"on" } }.ToString(),"controlled-single-reinterpretation");
                yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+2,8,"Corrected proposal receives its own independent review");
                model.DeviceGoalReviews.Last().Complete(true,new JObject { ["verdict"]=item.Id.EndsWith("budget")?"rejected":"approved",
                    ["requestAssessment"]="requested",["relation"]="new",["referentKind"]="unbound",["referentSourceId"]="none",
                    ["reason"]="Controlled independent review of the second interpretation" }.ToString(),"controlled-second-proposal-review");
                if(!item.Id.EndsWith("budget"))
                {
                    yield return Wait(()=>model.SceneSearchRequests.Count==searches+1,8,"Approved corrected target enters the unified search task");
                    var correctedGoal=Goal(chat);
                    LastReport["correctedProposalHandoff"]=new JObject {
                        ["goal"]=correctedGoal.DeepClone(),["roomPlannerRequests"]=model.Decisions.Count-rooms,
                        ["independentReviews"]=model.DeviceGoalReviews.Count-reviews,["speechRequests"]=model.Speech.Count-speech,
                        ["matchRequests"]=model.Matches.Count-matches,["searchRequests"]=model.SceneSearchRequests.Count-searches,
                        ["bodyOperationChanged"]=bridge.Controller.OperationRevision!=operation,
                        ["bodyAngleChanged"]=Quaternion.Angle(body,bridge.avatar.transform.rotation),
                        ["sceneActionActive"]=(bool)Get(chat,"m_SceneActionActive") };
                    Check((string)correctedGoal["desiredState"]=="on"&&(string)correctedGoal["originalRequest"]==item.Text&&
                        (string)correctedGoal["targetDescription"]==item.Text&&
                        ((string)correctedGoal["targetDescriptionSource"]??"").StartsWith("original-user-request",StringComparison.Ordinal),
                        "Corrected proposal retains the complete actual user target constraint, rather than either the rejected fan description or a shortened proposal label.");
                    Check(model.Decisions.Count==rooms+2&&model.DeviceGoalReviews.Count==reviews+2&&model.Speech.Count==speech&&
                        model.Matches.Count==matches&&model.SceneSearchRequests.Count==searches+1,
                        "Exactly one reinterpretation and its independent review hand the approved objective to unified search without extra planning or target matching.");
                    Check(bridge.Controller.OperationRevision==operation&&Quaternion.Angle(body,bridge.avatar.transform.rotation)<.1f&&
                        !(bool)Get(chat,"m_SceneActionActive"),
                        "Before any search choice returns, no body step or device action is submitted for the rejected or corrected proposal.");
                    ((JArray)LastReport["cases"]).Add(new JObject { ["name"]=item.Id,["goal"]=Goal(chat),["proposalReviews"]=2,["reinterpretations"]=1 });
                    continue;
                }
                yield return Wait(()=>model.Decisions.Count==rooms+3,8,"A second rejected interpretation leaves the bounded device proposal route");
                ++rooms;++reviews;
            }
            var current=model.Decisions.Last();var input=JObject.Parse(current.Input);var receipt=(JObject)Get(chat,"m_SpatialGoalLatestReview");
            Check((bool?)input["currentRequestOnly"]==true&&(string)input["currentUserText"]==item.Text&&
                (bool?)receipt["proposalDiscarded"]==true&&(bool?)receipt["pendingRetained"]==false&&
                (string)((JObject)Call(chat,"BuildPendingDeviceClarificationContext"))["status"]=="none"&&Call(chat,"GetActiveSpatialGoalId")==null,
                "Only a real unresolved target may become pending; rejected interpretation returns to current dialogue: "+item.Id);
            current.Complete(true,"{\"intent\":\"none\",\"replyScope\":\"spatial\",\"origin\":\"none\",\"evidence\":\"\"}","controlled-current-question-needs-no-action");
            yield return Wait(()=>model.Speech.Count==speech+1,8,"Current question reaches ordinary formal speech instead of device clarification");
            var reply=model.Speech.Last();
            Check(reply.Stage!=FormalResponseStage.SpatialGoalResult&&reply.Context.Contains("proposalDiscarded")&&
                reply.Context.Contains(item.Assessment)&&model.Decisions.Count==rooms+2&&model.DeviceGoalReviews.Count==reviews+1&&
                model.Matches.Count==matches&&model.SceneSearchRequests.Count==searches&&bridge.Controller.OperationRevision==operation&&
                Quaternion.Angle(body,bridge.avatar.transform.rotation)<.1f&&(bool)Get(chat,"m_SceneActionActive")==false,
                "Ordinary reply carries rejection facts without forced device clarification, search, body turn or write: "+item.Id);
            File.WriteAllText(Path.Combine(directory,item.Id+".planner-input.json"),current.Input);
            File.WriteAllText(Path.Combine(directory,item.Id+".speech-request.json"),reply.Wire);
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"]=item.Id,["review"]=receipt.DeepClone(),["speechStage"]=reply.Stage.ToString(),["actionSubmitted"]=false });
        }
        foreach(var item in new[] {
            new {Text="为什么停下了？",Relation="unrelated"},
            new {Text="Which direction is the device?",Relation="unrelated"},
            new {Text="さっきの同じ機器だよ。まだ覚えている？",Relation="clarify"},
            new {Text="Why did you stop? Cancel the projector task and turn the fan off instead.",Relation="new"}
        })
        {
            const string original="Please turn the projector on.";Reset(original);
            var proposal=new RoomTaskProtocol.Decision {intent="none",origin="user",evidence=original,
                deviceGoal=new RoomTaskProtocol.DeviceGoalProposal {operation="set",requestQuote=original,targetDescription="the projector",desiredState="on"}};
            Check((bool)Call(chat,"TryRegisterReviewedSpatialGoal",new object[]{proposal,original,new RoomTaskProtocol.DeviceGoalReview {
                verdict="approved",requestAssessment="requested",relation="new",referentKind="unbound",referentSourceId="none",reason="Controlled prior explicit authorization"},null}),
                "Question case starts with a genuinely approved unbound device goal.");
            string goalId=(string)Goal(chat)["goalId"];
            Call(chat,"PauseSpatialGoal","actual-new-user-question");Call(chat,"RecordAcceptedMotionUserTurn",item.Text);Call(chat,"ResetWorkReview",true);
            SceneVisionObservation source=null;yield return Capture(chat,"dialogue-question-source",value=>source=value);
            int rooms=model.Decisions.Count,reviews=model.DeviceGoalReviews.Count,speech=model.Speech.Count,matches=model.Matches.Count,searches=model.SceneSearchRequests.Count;
            int operation=bridge.Controller.OperationRevision;
            Call(chat,"StartStreaming",item.Text,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,8,"Current question is independently classified against the old goal");
            model.DeviceGoalReviews.Last().Complete(true,new JObject { ["verdict"]="approved",["relation"]=item.Relation,["responseMode"]="answer",
                ["referentGoalId"]=item.Relation=="clarify"||item.Relation=="new"?goalId:"",["referentEntityId"]="",["reason"]="Controlled clear question requires an answer, not immediate continuation." }.ToString(),"controlled-question-relation-and-answer-obligation");
            yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Question reaches an independent no-device planner pass");
            var current=model.Decisions.Last();var input=JObject.Parse(current.Input);
            Check((bool?)input["answerRequired"]==true&&((bool?)input["currentRequestOnly"]==true||item.Relation=="new")&&
                (string)input["currentUserText"]==item.Text&&(string)Goal(chat)["goalId"]==goalId&&Call(chat,"GetActiveSpatialGoalId")==null,
                "Even a clarifying question preserves identity/authorization while requiring an answer before any new action.");
            if(item.Relation=="new")
            {
                Check((string)Goal(chat)["status"]=="cancelled","Replacement relation applies only the old goal cancellation before any new authority exists.");
                current.Complete(true,new JObject { ["intent"]="none",["replyScope"]="ordinary",["origin"]="user",["evidence"]="turn the fan off",
                    ["deviceGoal"]=new JObject { ["operation"]="set",["requestQuote"]="turn the fan off",["targetDescription"]="the fan",["desiredState"]="off" } }.ToString(),"controlled-replacement-proposal-with-answer-obligation");
                yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+2,8,"A mixed replacement is independently reviewed after the old goal was cancelled");
                model.DeviceGoalReviews.Last().Complete(true,new JObject { ["verdict"]="approved",["requestAssessment"]="requested",["relation"]="new",
                    ["referentKind"]="unbound",["referentSourceId"]="none",["reason"]="Controlled exact new operation authorization" }.ToString(),"controlled-new-mixed-goal-approved");
                Check((string)Goal(chat)["goalId"]!=goalId&&(string)Goal(chat)["desiredState"]=="off"&&(string)Goal(chat)["status"]=="suspended"&&
                    (string)Goal(chat)["originalRequest"]==item.Text&&(string)Goal(chat)["targetDescription"]==item.Text,
                    "New approved goal preserves the complete compound request and is registered but held until the required answer finishes.");
                goalId=(string)Goal(chat)["goalId"];
            }
            else current.Complete(true,"{\"intent\":\"none\",\"replyScope\":\"spatial\",\"origin\":\"none\",\"evidence\":\"\"}","controlled-question-only-no-spatial-action");
            yield return Wait(()=>model.Speech.Count==speech+1,8,"Question receives a fact-backed ordinary formal reply request");
            var reply=model.Speech.Last();
            Check(reply.Stage!=FormalResponseStage.SpatialGoalResult&&reply.Context.Contains("answerRequired")&&
                reply.Context.Contains("responseMode")&&(string)Goal(chat)["goalId"]==goalId&&
                !(bool)Call(chat,"TryResumeSpatialGoalAfterDialogue")&&model.Matches.Count==matches&&model.SceneSearchRequests.Count==searches&&
                bridge.Controller.OperationRevision==operation&&(bool)Get(chat,"m_SceneActionActive")==false,
                "Answer-only mode does not dispatch search/turn/write or swallow the current question into a device terminal report.");
            if(item.Text=="Which direction is the device?")
            {
                const string unsupported="It is definitely behind the refrigerator.";
                int reviewCount=model.Decisions.Count(request=>request.Kind=="room-review");
                reply.SpeechCallback(new SpeechText(unsupported,"en",actionDependency:SpeechActionDependency.Independent));
                reply.SpeechCompleteCallback(unsupported);
                yield return Wait(()=>model.Decisions.Count(request=>request.Kind=="room-review")==reviewCount+1,8,"Question answer receives the real public-speech consistency review");
                var speechReview=model.Decisions.Last(request=>request.Kind=="room-review");
                speechReview.Complete(true,"{\"verdict\":\"inconsistent\",\"reason\":\"No verified direction supports that answer.\",\"factScope\":\"scene-action\",\"resultReport\":\"not-applicable\"}","controlled-unsupported-direction-answer");
                yield return null;
                string published=model.m_DataList.Last().content;
                Check(published!=unsupported&&published.Contains("haven't confirmed the answer")&&!published.Contains("approach")&&
                    !(bool)Call(chat,"TryResumeSpatialGoalAfterDialogue")&&model.SceneSearchRequests.Count==searches,
                    "A rejected question answer releases a language-correct answer fallback, never the unrelated user-approach fallback or a device restart.");
                LastReport["answerFallback"]=new JObject { ["rejectedText"]=unsupported,["publishedText"]=published,["reviewInput"]=JObject.Parse(speechReview.Input) };
            }
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="question-"+item.Relation,["userText"]=item.Text,["plannerInput"]=input,
                ["goal"]=Goal(chat),["speechStage"]=reply.Stage.ToString(),["audioCompletionHeld"]=true });
        }
    }

    private static IEnumerator Exercise(ChatSample chat,ArdyRoomDialogueBridge bridge,NevaSceneVisionIntegrationModel model,NevaSceneVisionCapture capture)
    {
        Check(capture.TryGetCurrentCameraPose(out Pose pose),"Actual current camera pose is available.");
        fixture=new GameObject("Isolated device referent candidates");fixture.transform.SetParent(bridge.environmentRoot,false);fixture.transform.SetPositionAndRotation(pose.position,pose.rotation);
        var lamp=Lamp("previously operated lamp",new Vector3(-.12f,-.02f,.6f));var reference=Lamp("other visible observation reference",new Vector3(.12f,-.02f,.6f));
        Vector3 root=bridge.avatar.transform.position;Quaternion body=bridge.avatar.transform.rotation;int operation=bridge.Controller.OperationRevision;
        const string seenRequest="你有看到左边这盏发光的小灯吗？";
        Call(chat,"RecordAcceptedMotionUserTurn",seenRequest);
        SceneVisionObservation source=null;yield return Capture(chat,"referent-seen-never-operated-source",value=>source=value);
        Check(capture.TryFindObservedObject(source,lamp.EntityId,out var seen,out string reason),"Previously seen target begins with genuine current opaque lamp pixels: "+reason);
        var neverOperatedBefore=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));bool seenDone=false;int seenMatches=model.Matches.Count;
        Call(chat,"BeginSceneAction",Inspect(source,seen,seenRequest),source,(int)Get(chat,"m_FormalResponseGeneration"),new Action(()=>seenDone=true));
        yield return Wait(()=>model.Matches.Count>seenMatches||seenDone,5,"Previously seen lamp's actual semantic review");
        Check(model.Matches.Count==seenMatches+1,"A plain lamp observation has its own semantic review before any device goal exists.");
        var seenCandidate=chat.LastSceneObjectCandidateView.Candidates.Single(value=>value.entityId==lamp.EntityId);
        model.Matches.Last().Complete(true,Reply("matched",seenCandidate.candidateId),"controlled-observed-never-operated-lamp");
        yield return Wait(()=>seenDone,8,"Previously seen lamp's fresh confirmation");
        var seenAction=JObject.Parse(JsonUtility.ToJson(Get(chat,"m_SceneActionFact")));var seenObject=(JObject)Get(chat,"m_SelectedSceneObject");
        Check((string)seenAction["phase"]=="observed"&&(bool?)seenAction["semanticTargetConfirmed"]==true&&
            (bool?)seenAction["objectConfirmed"]==true&&!string.IsNullOrEmpty((string)seenAction["confirmedCaptureId"])&&
            (string)seenAction["confirmedCaptureId"]!=source.CaptureId&&(string)seenAction["entityId"]==lamp.EntityId&&
            (bool?)seenAction["stateChanged"]==false&&JToken.DeepEquals(neverOperatedBefore,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
            "A real inspected lamp has a semantic association and fresh confirmation, while its device state remains untouched.");
        Check(((JArray)Call(chat,"BuildVerifiedDeviceReferentHistory")).Count==0&&
            ((JArray)seenObject["capabilities"]).Values<string>().Contains("set-object-state:on|off"),
            "An actually observed registered lamp supplies device capability but no completed device-goal operation history.");
        var observedHistory=(JArray)Call(chat,"BuildVerifiedObservedDeviceReferentHistory");
        Check(observedHistory.Count==1&&(string)observedHistory[0]["entityId"]==lamp.EntityId&&
            (string)observedHistory[0]["actionId"]==(string)seenAction["actionId"]&&observedHistory[0]["goalId"]==null&&
            (string)observedHistory[0]["confirmedCaptureId"]==(string)seenAction["confirmedCaptureId"]&&
            (bool?)observedHistory[0]["semanticTargetConfirmedAtObservation"]==true,
            "Real matched/fresh-confirmed observation enters the separate typed seen-device history without pretending to be an operation-goal receipt.");
        LastReport["seenButNeverOperated"]=new JObject { ["request"]=seenRequest,["actualAction"]=seenAction.DeepClone(),
            ["observedObject"]=seenObject.DeepClone(),["verifiedDeviceHistory"]=new JArray(),
            ["verifiedObservedDeviceHistory"]=observedHistory.DeepClone(),["actualState"]=neverOperatedBefore.DeepClone() };
        observedHistory[0]["entityId"]="untrusted-returned-hint-edit";
        Check((string)((JArray)Call(chat,"BuildVerifiedObservedDeviceReferentHistory"))[0]["entityId"]==lamp.EntityId,
            "Returned observation history is a deep copy and cannot mutate authority by altering prompt metadata.");
        var observedSources=(JArray)Call(chat,"BuildSpatialGoalReferentSources");
        var observedSource=observedSources.OfType<JObject>().Single(item=>(string)item["entityId"]==lamp.EntityId);
        Check((string)observedSource["kind"]=="observed-device"&&
            (string)observedSource["sourceId"]=="observed-device:"+(string)seenAction["actionId"],
            "The engine lists a typed real observation source before there is any operated-goal history.");
        CheckTypedReferentBoundaries(chat,bridge,observedSource,lamp);
        ExportTypedReviewProbe(chat,model,"observed-off",OffRequest,"off",observedSource,"approved");
        const string prohibition="不要关掉刚才看到的那盏小灯。";
        ExportTypedReviewProbe(chat,model,"negative",prohibition,"off",observedSource,"rejected");
        Call(chat,"RecordAcceptedMotionUserTurn",prohibition);
        Check(!RegisterTyped(chat,prohibition,"off",observedSource,"rejected")&&Call(chat,"GetActiveSpatialGoalId")==null&&lamp.ReadState().on,
            "A true observed identity plus independently rejected negative wording never creates write authority.");

        // Two real observations make the ambiguity probe meaningful; the second
        // object is not created as a prompt-only or hidden inventory candidate.
        const string secondSeenRequest="也看一下右边这盏发光的小灯。";
        Call(chat,"RecordAcceptedMotionUserTurn",secondSeenRequest);
        yield return Capture(chat,"referent-second-observed-source",value=>source=value);
        Check(capture.TryFindObservedObject(source,reference.EntityId,out var secondSeen,out reason),"Second ambiguity candidate has actual opaque pixels: "+reason);
        bool secondSeenDone=false;int secondSeenMatches=model.Matches.Count;
        Call(chat,"BeginSceneAction",Inspect(source,secondSeen,secondSeenRequest),source,(int)Get(chat,"m_FormalResponseGeneration"),new Action(()=>secondSeenDone=true));
        yield return Wait(()=>model.Matches.Count>secondSeenMatches||secondSeenDone,5,"Second observed source semantic check");
        Check(model.Matches.Count==secondSeenMatches+1,"The other observed source requires its own real semantic review.");
        model.Matches.Last().Complete(true,Reply("matched",chat.LastSceneObjectCandidateView.Candidates.Single(value=>value.entityId==reference.EntityId).candidateId),"controlled-second-observed-source");
        yield return Wait(()=>secondSeenDone,8,"Second source fresh confirmation");
        Check(((JArray)Call(chat,"BuildSpatialGoalReferentSources")).OfType<JObject>().Count(item=>(string)item["kind"]=="observed-device")==2&&reference.ReadState().on,
            "Both ambiguity candidates now have genuine observation records, with neither record granting a device write.");
        ExportTypedReviewProbe(chat,model,"ambiguous","把之前看到的那盏小灯关掉。","off",null,"uncertain");
        Call(chat,"RecordAcceptedMotionUserTurn",OffRequest);
        Check(RegisterTyped(chat,OffRequest,"off",observedSource,"approved"),"The current off request independently selects the actual observed source.");
        Check((string)Goal(chat)["boundEntityId"]==lamp.EntityId&&(string)Goal(chat)["referentKind"]=="observed-device"&&lamp.ReadState().on,
            "A reviewed observed source retains the exact same target without granting execution, current visibility or completion.");
        string offGoal=(string)Goal(chat)["goalId"];
        yield return Capture(chat,"referent-off-source",value=>source=value);
        Check(capture.TryFindObservedObject(source,lamp.EntityId,out seen,out reason),"Actual target lamp has opaque source pixels: "+reason);
        var off=new RoomTaskProtocol.Decision { intent="set-object-state",origin="user",evidence=OffRequest,objectState="off",
            entityTarget=new RoomTaskProtocol.EntityTarget {captureId=source.CaptureId,entityId=lamp.EntityId} };
        Set(chat,"m_FormalResponseInFlight",true);Set(chat,"m_AgentRoundInFlight",true);bool actionDone=false;int before=model.Matches.Count;
        Call(chat,"BeginSceneAction",off,source,(int)Get(chat,"m_FormalResponseGeneration"),new Action(()=>actionDone=true));
        yield return Wait(()=>model.Matches.Count>before||actionDone,5,"Actual off candidate review");
        Check(model.Matches.Count==before+1,"A real device write first issues one production candidate review.");
        var offered=chat.LastSceneObjectCandidateView.Candidates.FirstOrDefault(value=>value.entityId==lamp.EntityId);
        Check(offered!=null,"Actual lamp is among this frame's candidates.");
        model.Matches.Last().Complete(true,Reply("matched",offered.candidateId),"controlled-current-lamp-match");
        yield return Wait(()=>actionDone,7,"Actual off device execution and reobservation");
        var performed=JObject.Parse(JsonUtility.ToJson(Get(chat,"m_SceneActionFact")));
        Check((string)performed["phase"]=="performed"&&!string.IsNullOrEmpty((string)performed["resultCaptureId"])&&
            (string)performed["resultCaptureId"]!=source.CaptureId&&!lamp.ReadState().on&&lamp.ReadState().registered,
            "Production execution actually switched the isolated target off and captured a new result view.");
        Check((string)Goal(chat)["status"]=="satisfied"&&(string)Goal(chat)["boundEntityId"]==lamp.EntityId,"Fresh same-target device evidence completed the previous goal.");
        Check((string)Call(chat,"BuildSpatialGoalReviewFallback","zh")=="已确认这个设备现在已经关闭。",
            "An audited fallback preserves valid actual completion evidence instead of denying an already verified result or inventing another operation.");
        var history=(JArray)Call(chat,"BuildVerifiedDeviceReferentHistory");
        var receipt=history.OfType<JObject>().LastOrDefault(item=>(string)item["goalId"]==offGoal&&(string)item["entityId"]==lamp.EntityId);
        Check(receipt!=null&&(bool?)receipt["actualOperationPerformed"]==true&&(string)receipt["desiredState"]=="off"&&
            (bool?)receipt["completionEvidence"]?["actual"]?["on"]==false&&
            receipt.ToString().Contains((string)performed["resultCaptureId"]),"Verified referent history comes from the actual off operation's fresh completion capture.");
        LastReport["priorActualOperation"]=performed;LastReport["verifiedPriorReferent"]=receipt.DeepClone();
        receipt["entityId"]="mutated-export";
        Check(((JArray)Call(chat,"BuildVerifiedDeviceReferentHistory")).OfType<JObject>().Any(item=>(string)item["entityId"]==lamp.EntityId),"Exported history cannot mutate authoritative identity.");

        Call(chat,"RecordAcceptedMotionUserTurn",OnRequest);
        var operatedSource=((JArray)Call(chat,"BuildSpatialGoalReferentSources")).OfType<JObject>().Single(item=>(string)item["sourceId"]=="operated-goal:"+offGoal);
        ExportTypedReviewProbe(chat,model,"operated-on",OnRequest,"on",operatedSource,"approved");
        Check(RegisterTyped(chat,OnRequest,"on",operatedSource,"approved"),"The independently reviewed reverse operation selects the actual operated-goal source.");
        string main=(string)Goal(chat)["goalId"];
        Check(main!=offGoal&&(string)Goal(chat)["boundEntityId"]==lamp.EntityId,"Opposite state is a new goal for the exact previously operated entity.");
        var replacementFacts=(JObject)Call(chat,"BuildSpatialGoalExecutionFacts");
        Check(replacementFacts["lastAction"].Type==JTokenType.Null&&replacementFacts["objectContext"]["stateResult"].Type==JTokenType.Null&&
            SpatialGoalResultContextPolicy.RequiredReport(replacementFacts)=="incomplete",
            "A newly registered replacement with no steps cannot import the prior goal's actual performed action or device result.");
        // Negative receipt injection: preserve the real retired action's owner,
        // but present it as a stale distance rejection to the reporting boundary.
        // No device state or execution receipt is fabricated for the new goal.
        var retiredFact=Get(chat,"m_SceneActionFact");var retiredPhase=Get(retiredFact,"phase");var retiredReason=Get(retiredFact,"reason");
        try
        {
            Set(retiredFact,"phase","rejected");Set(retiredFact,"reason","device-outside-nearby-operation-distance");
            var isolated=(JObject)Call(chat,"BuildSpatialGoalExecutionFacts");
            Check(isolated["lastAction"].Type==JTokenType.Null&&isolated["objectContext"]["stateResult"].Type==JTokenType.Null&&
                isolated["goal"]["searchSummary"]["latestFailure"].Type==JTokenType.Null&&
                SpatialGoalResultContextPolicy.RequiredReport(isolated)=="incomplete"&&
                !SpatialGoalResultContextPolicy.BuildTrustedReport(isolated,"en").Contains("too far"),
                "An old goal's distance rejection cannot become the current cause or public fallback for an untouched replacement.");
            LastReport["replacementResultOwnerIsolation"]=isolated;
        }
        finally {Set(retiredFact,"phase",retiredPhase);Set(retiredFact,"reason",retiredReason);}
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="replacement-result-excludes-retired-action-and-state-receipt" });
        Call(chat,"PauseSpatialGoal","controlled-new-user-speech");Call(chat,"RecordAcceptedMotionUserTurn",Reminder);
        Check((string)((JObject)Call(chat,"CurrentSceneObjectSpatialGoal"))["originalRequest"]==OnRequest&&
            (string)Call(chat,"SceneObjectGoal",new RoomTaskProtocol.Decision {origin="user"})==Reminder&&
            (bool?)Goal(chat)["executionSuspended"]==true,"A suspended logical goal remains background while independent current matching uses the latest request.");
        Call(chat,"SetSpatialGoalAwaitingClarification","controlled-current-identity-question");
        Check((string)((JObject)Call(chat,"CurrentSceneObjectSpatialGoal"))["originalRequest"]==OnRequest&&
            (string)Call(chat,"SceneObjectGoal",new RoomTaskProtocol.Decision {origin="user"})==Reminder&&
            (string)Goal(chat)["status"]=="waiting_clarification","Waiting for identity clarification preserves logical background without authorizing execution or replacing an independent purpose.");
        Call(chat,"RememberFirstReplyRequestSemantics",new FirstReplyCompletion {Disposition=FirstReplyDisposition.Tools,RequestKind="control"},
            (int)Get(chat,"m_FormalResponseGeneration"),model);
        var update=new RoomTaskProtocol.DeviceGoalReview {verdict="approved",reason="Controlled explicit continuation",relation="continue",responseMode="answer",referentGoalId=main,referentEntityId=lamp.EntityId};
        var updateArgs=new object[]{update,Reminder,false,null};
        Check((bool)Call(chat,"TryApplySpatialGoalUserUpdate",updateArgs)&&(bool)updateArgs[2]&&update.responseMode=="act"&&(string)Goal(chat)["goalId"]==main,"A reviewed reminder resumes the existing goal rather than replacing its original request.");
        yield return Capture(chat,"referent-reminder-source",value=>source=value);
        Check(capture.TryFindObservedObject(source,lamp.EntityId,out seen,out reason),"The switched-off target is genuinely visible in the current frame: "+reason);
        var inspect=Inspect(source,seen,Reminder);MatchRun run=null;
        yield return StartMatch(chat,model,source,seen,inspect,value=>run=value);
        var input=JObject.Parse(run.request.Input);
        Check((string)input["originalGoal"]==OnRequest&&(string)input["currentRequest"]==Reminder&&(string)input["latestUserRequest"]==Reminder,
            "Production matcher keeps the authorized on objective separate from the latest reminder.");
        Check((string)input["spatialGoal"]["goalId"]==main&&(string)input["spatialGoal"]["boundEntityId"]==lamp.EntityId&&
            ((JArray)input["verifiedDeviceReferentHistory"]).OfType<JObject>().Any(item=>(string)item["goalId"]==offGoal&&(string)item["entityId"]==lamp.EntityId),
            "Exact verified old entity and stable current goal reach the actual candidate-matching input.");
        ExportActualModelProbe(run.request,chat.LastSceneObjectCandidateView,source,lamp.EntityId,main);
        var offState=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
        run.request.Complete(true,Reply("uncertain","none"),"controlled-uncertain-even-with-history");
        yield return Wait(()=>run.done,3,"Uncertain matched-history result");
        Check(run.selected==null&&run.failure.Contains("uncertain")&&(bool?)JObject.Parse(JsonUtility.ToJson(run.fact))["semanticTargetConfirmed"]!=true,
            "Verified history cannot bypass a current uncertain semantic match.");
        Check(JToken.DeepEquals(offState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState())))&&(string)Goal(chat)["status"]!="satisfied","Uncertain matching performs no write and cannot satisfy the on objective.");
        AddCase("reminder-with-verified-history",input);

        // Failed transports can retain a typed string null in the authoritative
        // receipt. The next production request must preserve that diagnostic
        // absence without passing it to Regex.Replace or inventing a match.
        var receiptBeforeNullRecovery=(JObject)Get(chat,"m_SceneObjectMatchReceipt");
        try
        {
            var nullReceipt=(JObject)receiptBeforeNullRecovery.DeepClone();nullReceipt["modelVerdict"]=new JValue((string)null);
            nullReceipt["reason"]="";nullReceipt["modelReason"]=new JValue((string)null);
            Set(chat,"m_SceneObjectMatchReceipt",nullReceipt);
            Check(nullReceipt["modelReason"].Type==JTokenType.String&&(string)nullReceipt["modelReason"]==null,
                "Failed receipt uses the actual in-memory typed string-null edge, rather than a reparsed JSON-null approximation.");
            yield return Capture(chat,"null-review-recovery-source",value=>source=value);
            Check(capture.TryFindObservedObject(source,lamp.EntityId,out seen,out reason),"Typed-null recovery starts from genuine current target pixels: "+reason);
            yield return StartMatch(chat,model,source,seen,Inspect(source,seen,Reminder),value=>run=value);
            var recoveryInput=JObject.Parse(run.request.Input);var failedHistory=(JObject)recoveryInput["previousTargetReview"];
            Check(failedHistory["modelVerdict"].Type==JTokenType.Null&&(string)failedHistory["reason"]==""&&
                failedHistory["modelReason"].Type==JTokenType.Null&&run.request.Image==chat.LastSceneObjectCandidateView.ImageDataUrl,
                "Production matcher serializes a new real candidate request after an empty/typed-null failure reason without treating the failed receipt as authority.");
            run.request.Complete(true,Reply("uncertain","none"),"controlled-null-reason-recovery-uncertain");
            yield return Wait(()=>run.done,3,"Typed-null failure receipt recovery");
            Check(run.selected==null&&run.failure.Contains("uncertain")&&JToken.DeepEquals(offState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState())))&&
                (string)Goal(chat)["goalId"]==main&&(string)Goal(chat)["status"]!="satisfied",
                "A failure-receipt recovery still requires a successful current semantic review and cannot write or complete the goal.");
            AddCase("typed-null-failure-receipt-recovery",recoveryInput);
        }
        finally { Set(chat,"m_SceneObjectMatchReceipt",receiptBeforeNullRecovery); }

        JObject wrongReview=null;string wrongCandidateId=null,correctCandidateId=null;
        var wrongCases=new JArray();LastReport["wrongBoundMatchedCandidates"]=wrongCases;
        foreach(string wrongIntent in new[]{"set-object-state","inspect-object"})
        {
        // The preceding actual off action retained a smoothing head overlay.
        // Return it through the real attention API before measuring whether this
        // rejected request acquires any new physical attention ownership.
        var attentionBefore=(NevaSceneAttention)Get(chat,"m_SceneAttention");
        attentionBefore?.Cancel("wrong-bound-fixture-release-prior-view");
        yield return Wait(()=>attentionBefore==null||!attentionBefore.IsBusy,5,"Prior real retained head view fully returns before wrong-output measurement");
        yield return Capture(chat,"wrong-bound-match-source-"+wrongIntent,value=>source=value);
        Check(capture.TryFindObservedObject(source,lamp.EntityId,out seen,out reason),"Wrong-output case still starts from real bound-lamp pixels: "+reason);
        Check(capture.TryGetCurrentCameraPose(out Pose beforeWrongFocus),"Actual pre-match camera pose is available.");
        string attentionOwner=attentionBefore?.OwnerId,attentionLastOwner=attentionBefore?.LastOwnerId,attentionState=attentionBefore?.State;
        Vector3 attentionTarget=attentionBefore!=null?attentionBefore.TargetPoint:Vector3.zero;
        var beforeDevice=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));var beforeGoal=Goal(chat);
        Vector3 beforeBodyPosition=bridge.avatar.transform.position;Quaternion beforeBodyRotation=bridge.avatar.transform.rotation;
        int beforeBodyOperation=bridge.Controller.OperationRevision;bool beforeBodyReserved=bridge.ReservesBody,beforeControllerBusy=bridge.Controller.IsBusy;
        var wrongBound=Inspect(source,seen,Reminder);wrongBound.intent=wrongIntent;
        if(wrongIntent=="set-object-state")wrongBound.objectState="on";
        else
        {
            wrongBound.entityTarget=null;
            wrongBound.visualTarget=new RoomTaskProtocol.VisualTarget { captureId=source.CaptureId,x=seen.ImagePoint.x,y=seen.ImagePoint.y,label="当前实际图像中的已绑定左侧小灯" };
        }
        int wrongMatches=model.Matches.Count;bool wrongDone=false;
        Call(chat,"BeginSceneAction",wrongBound,source,(int)Get(chat,"m_FormalResponseGeneration"),new Action(()=>wrongDone=true));
        yield return Wait(()=>model.Matches.Count==wrongMatches+1||wrongDone,5,"Actual device action awaits the current bound-target matcher");
        Check(model.Matches.Count==wrongMatches+1,"Normal scene-action gates reach one semantic review before device mutation.");
        var wrongCandidate=chat.LastSceneObjectCandidateView.Candidates.Single(value=>value.entityId==reference.EntityId);
        var correctCandidate=chat.LastSceneObjectCandidateView.Candidates.Single(value=>value.entityId==lamp.EntityId);
        wrongCandidateId=wrongCandidate.candidateId;correctCandidateId=correctCandidate.candidateId;
        model.Matches.Last().Complete(true,new JObject { ["verdict"]="matched", ["candidateRole"]="target",["candidateId"]=wrongCandidate.candidateId,
            ["reason"]="Controlled contradictory output: "+wrongCandidate.candidateId+" is the different target; "+correctCandidate.candidateId+" is the correct bound lamp." }.ToString(),"controlled-contradictory-wrong-bound-candidate");
        yield return Wait(()=>wrongDone,3,"Wrong bound candidate rejected before focus");
        var wrongFact=JObject.Parse(JsonUtility.ToJson(Get(chat,"m_SceneActionFact")));
        // Typed string nulls can retain JTokenType.String in an in-memory
        // DeepClone. Validate the exposed JSON null, not that storage detail.
        wrongReview=JObject.Parse(((JObject)Call(chat,"SceneObjectTargetContext")).ToString());
        var wrongCase=new JObject { ["intent"]=wrongIntent,["offeredWrongCandidateId"]=wrongCandidate.candidateId,
            ["correctCandidateId"]=correctCandidate.candidateId,["actualAction"]=wrongFact,["currentReview"]=wrongReview };
        wrongCases.Add(wrongCase);
        Check((string)wrongFact["phase"]=="rejected"&&(string)wrongFact["targetMatchVerdict"]=="uncertain"&&
            (bool?)wrongFact["semanticTargetConfirmed"]==false&&(bool?)wrongFact["hasIntentFocus"]==false&&
            string.IsNullOrEmpty((string)wrongFact["confirmedCaptureId"])&&string.IsNullOrEmpty((string)wrongFact["resultCaptureId"])&&
            (bool?)wrongReview["boundGoalEntityConflict"]==true&&(string)wrongReview["modelVerdict"]=="matched"&&
            (bool?)wrongReview["associationCurrent"]==false&&wrongReview["matchedEntityId"].Type==JTokenType.Null,
            "A syntactically valid wrong bound candidate is uncertain before any semantic association, physical focus or confirmation capture; model evidence remains diagnostic.");
        bool afterPoseAvailable=capture.TryGetCurrentCameraPose(out Pose afterWrongFocus);
        var afterDevice=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));var afterGoal=Goal(chat);
        var attentionAfter=(NevaSceneAttention)Get(chat,"m_SceneAttention");
        bool attentionUnchanged=attentionBefore==attentionAfter&&(attentionAfter==null||
            (attentionAfter.OwnerId==attentionOwner&&attentionAfter.LastOwnerId==attentionLastOwner&&
             attentionAfter.State==attentionState&&attentionAfter.TargetPoint==attentionTarget));
        float poseDistance=Vector3.Distance(beforeWrongFocus.position,afterWrongFocus.position);
        float poseAngle=Quaternion.Angle(beforeWrongFocus.rotation,afterWrongFocus.rotation);
        bool sameCapture=capture.LatestObservation.CaptureId==source.CaptureId;
        bool sameDevice=JToken.DeepEquals(offState,afterDevice)&&JToken.DeepEquals(beforeDevice,afterDevice)&&reference.ReadState().on;
        bool sameGoal=(string)beforeGoal["goalId"]==(string)afterGoal["goalId"]&&(string)afterGoal["boundEntityId"]==lamp.EntityId&&
            (string)afterGoal["status"]!="satisfied";
        string rejectedActionId=(string)wrongFact["actionId"];
        bool rejectedActionNeverOwnedAttention=!string.IsNullOrEmpty(rejectedActionId)&&(attentionAfter==null||
            (attentionAfter.OwnerId!=rejectedActionId&&attentionAfter.LastOwnerId!=rejectedActionId));
        bool sameBodyOperation=bridge.Controller.OperationRevision==beforeBodyOperation&&bridge.ReservesBody==beforeBodyReserved&&
            bridge.Controller.IsBusy==beforeControllerBusy&&bridge.avatar.transform.position==beforeBodyPosition&&
            bridge.avatar.transform.rotation==beforeBodyRotation;
        wrongCase["physicalDiagnostics"]=new JObject { ["beforePose"]=JObject.Parse(JsonUtility.ToJson(beforeWrongFocus)),
            ["afterPose"]=JObject.Parse(JsonUtility.ToJson(afterWrongFocus)),["afterPoseAvailable"]=afterPoseAvailable,
            ["positionDelta"]=poseDistance,["rotationDeltaDegrees"]=poseAngle,["beforeCaptureId"]=source.CaptureId,
            ["afterCaptureId"]=capture.LatestObservation.CaptureId,["sameCapture"]=sameCapture,["beforeDevice"]=beforeDevice,
            ["afterDevice"]=afterDevice,["sameDevice"]=sameDevice,["referenceLampOn"]=reference.ReadState().on,
            ["beforeGoal"]=beforeGoal,["afterGoal"]=afterGoal,["sameGoal"]=sameGoal,["attentionUnchanged"]=attentionUnchanged,
            ["rejectedActionNeverOwnedAttention"]=rejectedActionNeverOwnedAttention,["sameBodyOperation"]=sameBodyOperation,
            ["beforeBody"]=new JObject { ["position"]=JObject.Parse(JsonUtility.ToJson(beforeBodyPosition)),
                ["rotation"]=JObject.Parse(JsonUtility.ToJson(beforeBodyRotation)),["operationRevision"]=beforeBodyOperation,
                ["reserved"]=beforeBodyReserved,["controllerBusy"]=beforeControllerBusy },
            ["afterBody"]=new JObject { ["position"]=JObject.Parse(JsonUtility.ToJson(bridge.avatar.transform.position)),
                ["rotation"]=JObject.Parse(JsonUtility.ToJson(bridge.avatar.transform.rotation)),["operationRevision"]=bridge.Controller.OperationRevision,
                ["reserved"]=bridge.ReservesBody,["controllerBusy"]=bridge.Controller.IsBusy },
            ["beforeAttention"]=new JObject { ["owner"]=attentionOwner,["lastOwner"]=attentionLastOwner,["state"]=attentionState,
                ["target"]=JObject.Parse(JsonUtility.ToJson(attentionTarget)) },
            ["afterAttention"]=new JObject { ["owner"]=attentionAfter?.OwnerId,["lastOwner"]=attentionAfter?.LastOwnerId,["state"]=attentionAfter?.State,
                ["target"]=attentionAfter==null?null:JObject.Parse(JsonUtility.ToJson(attentionAfter.TargetPoint)) } };
        LastReport["wrongBoundMatchedCandidate"]=wrongCase.DeepClone();
        // Background head animation may change the camera without acquiring
        // attention or issuing an action. Measure focus through its real owner
        // and command state; retain camera deltas as diagnostics only.
        Check(afterPoseAvailable&&sameCapture&&sameDevice&&sameGoal&&attentionUnchanged&&rejectedActionNeverOwnedAttention&&
            sameBodyOperation&&!(bool)Get(chat,"m_SceneActionActive"),
            "Wrong matched candidate cannot focus/reobserve, change either device, replace the bound target or complete its on objective.");
        }
        yield return Capture(chat,"same-parent-reference-source",value=>source=value);
        Check(capture.TryFindObservedObject(source,reference.EntityId,out var referenceSeen,out reason),"A separate actual observation reference is visible: "+reason);
        var closer=Inspect(source,referenceSeen,Reminder);closer.intent="approach-object";closer.targetPurpose="observe-closer";
        yield return StartMatch(chat,model,source,referenceSeen,closer,value=>run=value);
        var previous=(JObject)JObject.Parse(run.request.Input)["previousTargetReview"];
        Check(previous["proposedCandidateId"]==null&&previous["proposedImagePoint"]==null&&(bool?)previous["historical"]==true&&
            (bool?)previous["localCandidateIdsApplicableToCurrentView"]==false&&(string)previous["captureId"]==(string)wrongReview["captureId"]&&
            (string)previous["proposedEntityId"]==reference.EntityId&&
            ((string)previous["modelReason"]).Contains("[former-image-local-candidate]")&&
            !((string)previous["modelReason"]).Contains(wrongCandidateId)&&!((string)previous["modelReason"]).Contains(correctCandidateId)&&
            ((string)wrongReview["modelReason"]).Contains(wrongCandidateId),
            "Historical prompt projection retains its source and stable entity while masking old local IDs even in quoted reasoning; the original diagnostic receipt is preserved.");
        offered=chat.LastSceneObjectCandidateView.Candidates.First(value=>value.entityId==reference.EntityId);
        run.request.Complete(true,Reply("matched",offered.candidateId),"controlled-visible-reference");yield return Wait(()=>run.done,3,"Reference semantic result");
        var pending=(JObject)Get(chat,"m_SceneObservationGoal");
        Check(run.selected?.EntityId==reference.EntityId&&(string)pending["parentSpatialGoalId"]==main&&(string)pending["originalGoal"]==OnRequest,
            "A current visible reference creates an observation child of the retained on objective.");
        Check((string)pending["referenceEntityId"]==reference.EntityId&&(string)Goal(chat)["boundEntityId"]==lamp.EntityId,
            "The observation reference identity remains separate from the main bound device.");
        const string independent="请看看右边这个物体。";
        Call(chat,"PauseSpatialGoal","controlled-independent-spatial-request");Call(chat,"RecordAcceptedMotionUserTurn",independent);
        Call(chat,"RememberFirstReplyRequestSemantics",new FirstReplyCompletion {Disposition=FirstReplyDisposition.Tools,RequestKind="action"},
            (int)Get(chat,"m_FormalResponseGeneration"),model);
        var unrelated=new RoomTaskProtocol.DeviceGoalReview {verdict="approved",reason="Controlled unrelated inspection",relation="unrelated",responseMode="answer",referentGoalId="",referentEntityId=""};
        var unrelatedArgs=new object[]{unrelated,independent,false,null};
        Check((bool)Call(chat,"TryApplySpatialGoalUserUpdate",unrelatedArgs)&&!(bool)unrelatedArgs[2]&&unrelated.responseMode=="act"&&
            !(bool)Get(Get(chat,"m_SpatialDeviceGoal"),"resumeAfterDialogue")&&(bool?)Goal(chat)["executionCurrent"]==false,
            "Independent inspection preserves the standing goal while suspending its execution lease.");
        var pausedContinuation=Inspect(source,referenceSeen,independent);pausedContinuation.observationGoalId=(string)pending["goalId"];
        var pausedArgs=new object[]{pausedContinuation,null,null};
        Check(!(bool)Call(chat,"TryContinueSceneObservationGoal",pausedArgs)&&(string)pausedArgs[2]=="observation-goal-parent-execution-not-current",
            "A child cannot acquire a suspended main goal's authority during an independent observation.");
        yield return Capture(chat,"independent-purpose-source",value=>source=value);
        Check(capture.TryFindObservedObject(source,reference.EntityId,out referenceSeen,out reason),"Independent inspection still has actual current candidate pixels: "+reason);
        var independentDecision=Inspect(source,referenceSeen,independent);
        yield return StartMatch(chat,model,source,referenceSeen,independentDecision,value=>run=value);
        var independentInput=JObject.Parse(run.request.Input);
        Check((string)independentInput["originalGoal"]==independent&&(string)independentInput["latestUserRequest"]==independent&&
            independentInput["spatialGoal"].Type==JTokenType.Null&&independentInput["pendingObservationGoal"].Type==JTokenType.Null&&
            (string)independentInput["standingGoal"]["goalId"]==main&&
            (bool?)independentInput["standingGoal"]["executionCurrent"]==false,
            "Unrelated inspection matches the latest purpose and includes the on goal only as nonauthorizing background.");
        offered=chat.LastSceneObjectCandidateView.Candidates.First(value=>value.entityId==reference.EntityId);
        run.request.Complete(true,Reply("matched",offered.candidateId),"controlled-independent-current-reference");yield return Wait(()=>run.done,3,"Independent matching result");
        Check(run.selected?.EntityId==reference.EntityId&&(string)Goal(chat)["boundEntityId"]==lamp.EntityId&&(bool?)Goal(chat)["executionCurrent"]==false&&
            JToken.DeepEquals(offState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
            "Independent matching cannot bind another device to the standing goal, resume execution or perform its write.");
        AddCase("unrelated-inspection-preserves-background",independentInput);

        // A new observation reference belongs to this independent user request,
        // even while an older device goal remains suspended in the background.
        var storedChild=Get(chat,"m_SceneObservationGoal");
        var storedReview=Get(chat,"m_SceneObjectMatchReceipt");
        yield return Capture(chat,"independent-reference-source",value=>source=value);
        Check(capture.TryFindObservedObject(source,reference.EntityId,out referenceSeen,out reason),"Current independent reference has captured pixels: "+reason);
        var independentCloser=Inspect(source,referenceSeen,independent);
        independentCloser.intent="approach-object";independentCloser.targetPurpose="observe-closer";
        yield return StartMatch(chat,model,source,referenceSeen,independentCloser,value=>run=value);
        offered=chat.LastSceneObjectCandidateView.Candidates.First(value=>value.entityId==reference.EntityId);
        run.request.Complete(true,Reply("matched",offered.candidateId),"controlled-new-independent-observation-reference");
        yield return Wait(()=>run.done,3,"Independent reference match creates its own observation goal");
        var independentChild=(JObject)Get(chat,"m_SceneObservationGoal");
        string independentId=(string)independentChild["goalId"];
        Check(string.IsNullOrEmpty((string)independentChild["parentSpatialGoalId"])&&
            (string)independentChild["originalGoal"]==independent&&(int?)independentChild["userTurnAtCreation"]==
                (int)Get(Get(chat,"m_ActionConstraints"),"<CurrentUserTurn>k__BackingField"),
            "The current request creates an unparented observation objective, not a child of the old device task.");
        var independentContext=(JObject)Call(chat,"SceneObjectTargetContext");
        Check((string)independentContext["observationGoal"]?["status"]=="pending",
            "The inactive standing device goal does not supersede the current independent observation.");
        yield return Capture(chat,"independent-continuation-source",value=>source=value);
        Check(capture.TryFindObservedObject(source,reference.EntityId,out referenceSeen,out reason),"Independent continuation has a new current capture: "+reason);
        var independentContinuation=Inspect(source,referenceSeen,independent);independentContinuation.observationGoalId=independentId;
        yield return StartMatch(chat,model,source,referenceSeen,independentContinuation,value=>run=value);
        var childInput=JObject.Parse(run.request.Input);
        Check((string)childInput["pendingObservationGoal"]?["goalId"]==independentId&&
            (string)childInput["originalGoal"]==independent&&childInput["spatialGoal"].Type==JTokenType.Null,
            "Current independent observation continuation reaches the real matcher without borrowing device authority.");
        offered=chat.LastSceneObjectCandidateView.Candidates.First(value=>value.entityId==reference.EntityId);
        run.request.Complete(true,Reply("matched",offered.candidateId),"controlled-independent-observation-continuation");
        yield return Wait(()=>run.done,3,"Independent observation continuation completes matching");
        Check(run.selected?.EntityId==reference.EntityId&&(bool?)Goal(chat)["executionCurrent"]==false&&
            JToken.DeepEquals(offState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
            "Independent observation matching leaves the background device goal and its state untouched.");
        AddCase("current-independent-observation-child",childInput);
        Call(chat,"RecordAcceptedMotionUserTurn","看看其他东西。");
        var staleChildArgs=new object[]{independentContinuation,null,null};
        Check(!(bool)Call(chat,"TryContinueSceneObservationGoal",staleChildArgs),
            "A later user turn cannot inherit this independent observation while the other standing goal is present.");
        Set(chat,"m_SceneObservationGoal",storedChild);Set(chat,"m_SceneObjectMatchReceipt",storedReview);
        Call(chat,"PauseSpatialGoal","controlled-second-reminder");Call(chat,"RecordAcceptedMotionUserTurn",Reminder);
        updateArgs=new object[]{update,Reminder,false,null};
        Check((bool)Call(chat,"TryApplySpatialGoalUserUpdate",updateArgs)&&(bool)updateArgs[2],"A later independently reviewed reminder preserves the parent goal.");
        Check((string)((JObject)Call(chat,"SceneObjectTargetContext"))["observationGoal"]["status"]=="pending",
            "A child of that same logical goal remains pending across a new user turn.");
        string observationId=(string)pending["goalId"];var continuing=Inspect(source,referenceSeen,Reminder);continuing.observationGoalId=observationId;
        var continueArgs=new object[]{continuing,null,null};
        Check((bool)Call(chat,"TryContinueSceneObservationGoal",continueArgs),"Only the same-parent child remains explicitly continuable.");
        var originalPending=(JObject)pending.DeepClone();
        foreach(string wrongParent in new[]{offGoal,""})
        {
            var stale=(JObject)originalPending.DeepClone();stale["parentSpatialGoalId"]=wrongParent;stale["originalGoal"]=OffRequest;
            Set(chat,"m_SceneObservationGoal",stale);int count=model.Matches.Count;
            yield return StartMatch(chat,model,source,referenceSeen,continuing,value=>run=value,false);
            Check(run.done&&run.selected==null&&run.failure=="observation-goal-parent-spatial-goal-mismatch"&&model.Matches.Count==count,
                "An old/unparented off observation is refused before candidate review under the on main goal.");
            var exposed=(JObject)((JObject)Call(chat,"SceneObjectTargetContext"))["observationGoal"];
            Check((string)exposed["status"]=="superseded"&&(bool?)exposed["needsOriginalTargetRecheck"]==false,"Superseded child is exposed as unusable planning history.");
        }
        Set(chat,"m_SceneObservationGoal",originalPending);originalPending["originalGoal"]=OffRequest;
        yield return Capture(chat,"same-parent-purpose-source",value=>source=value);
        Check(capture.TryFindObservedObject(source,lamp.EntityId,out seen,out reason),"Current target pixels available for same-parent purpose check: "+reason);
        continuing=Inspect(source,seen,Reminder);continuing.observationGoalId=observationId;
        yield return StartMatch(chat,model,source,seen,continuing,value=>run=value);
        input=JObject.Parse(run.request.Input);
        Check((string)input["originalGoal"]==OnRequest,"Even a stale child-purpose field cannot overwrite the main on objective.");
        Call(chat,"CancelSpatialGoal","controlled-logical-goal-replacement");Call(chat,"RecordAcceptedMotionUserTurn",OffRequest);
        Check(Register(chat,OffRequest,"off"),"A separate reviewed replacement owns a new logical goal.");
        string replacement=(string)Goal(chat)["goalId"];
        offered=chat.LastSceneObjectCandidateView.Candidates.First(value=>value.entityId==lamp.EntityId);
        run.request.Complete(true,Reply("matched",offered.candidateId),"controlled-late-old-goal-match");yield return Wait(()=>run.done,3,"Old logical goal callback rejection");
        Check(run.selected==null&&run.failure=="object-target-main-goal-changed-during-match"&&(string)Goal(chat)["goalId"]==replacement,
            "An otherwise current callback cannot attach an old purpose to a replacement logical goal.");
        Check(JToken.DeepEquals(offState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState())))&&reference.ReadState().on&&
            bridge.avatar.transform.position==root&&bridge.avatar.transform.rotation==body&&bridge.Controller.OperationRevision==operation,
            "All follow-up matching checks preserve actual device states and character body operation.");
        AddCase("same-parent-purpose-and-replaced-owner",input);
        yield return ExerciseRoomClarificationContinuation(chat,bridge,model,capture,lamp,reference,offGoal);
        yield return ExercisePendingClarificationFailureIsolation(chat,bridge,model,capture,lamp);
        yield return ExercisePendingProposalClarification(chat,bridge,model,capture,lamp,reference,false);
        yield return ExercisePendingProposalClarification(chat,bridge,model,capture,lamp,reference,true);
        yield return ExerciseApprovedIndependentTurnWithoutFirstReply(chat,bridge,model,capture,lamp);
        yield return ExerciseUnapprovedGoalUpdateIndependentRequest(chat,bridge,model,capture,lamp);
        yield return ExerciseApprovedGoalUpdateRouting(chat,bridge,model);
        yield return ExercisePendingDialogueRecovery(chat,bridge,model,capture,lamp);
        yield return ExerciseNewlyApprovedLatestTurn(chat,bridge,model,capture,lamp);
        yield return ExerciseAutonomousExplanationRepair(chat,bridge,model,lamp);
    }

    private static IEnumerator ExerciseAutonomousExplanationRepair(ChatSample chat,ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model,SceneObjectLightSwitch lamp)
    {
        const string oldHuman="你还记得那盏灯的方向吗？你转身的时候得往那个方向看去吧。";
        const string intention="向用户说明当前视野内没有发现灯具，并询问灯具的具体位置。";
        var results=new JArray();
        foreach(bool escalates in new[]{false,true})
        {
            Call(chat,"CancelSpatialGoalRunner","autonomous-explanation-fixture");Call(chat,"CancelSpatialGoal","autonomous-explanation-fixture");
            Call(chat,"CancelSceneAction","autonomous-explanation-fixture");Call(chat,"ClearPendingDeviceClarification","autonomous-explanation-fixture");
            typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags|BindingFlags.Static).Invoke(null,new object[]{chat});
            Set(chat,"m_EnableFirstReplyRouting",false);Call(chat,"RecordAcceptedMotionUserTurn",oldHuman);
            Set(chat,"m_SpatialDeviceGoal",null);Call(chat,"RestrictSpatialPlanningToCurrentRequest");
            Set(chat,"m_AgentCurrentRoundIsTick",true);Set(chat,"m_SceneAutonomyIntention",intention);
            SceneVisionObservation source=null;yield return Capture(chat,"autonomous-explanation-source",value=>source=value);
            int rooms=model.Decisions.Count,speech=model.Speech.Count,matches=model.Matches.Count,operation=bridge.Controller.OperationRevision;
            var body=bridge.avatar.transform.rotation;var state=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
            Call(chat,"StartStreaming",intention,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Same-user-turn autonomous explanation receives its own room decision");
            var initial=model.Decisions.Last();var input=JObject.Parse(initial.Input);
            Check((bool?)input["autonomous"]==true&&(bool?)input["currentRequestOnly"]==true&&
                (string)input["currentUserText"]==""&&(string)input["latestUserText"]==""&&
                (string)input["autonomousIntention"]==intention&&input["latestGoalUpdate"].Type==JTokenType.Null,
                "Old device-write restrictions persist without promoting the old human text or cancellation into a new autonomous command.");
            initial.Complete(true,"{\"intent\":\"none\",\"origin\":\"autonomous\",\"evidence\":\"\"}","controlled-real-log-malformed-autonomous-none");
            yield return Wait(()=>model.Decisions.Count==rooms+2,8,"Malformed no-action receives exactly one structure repair");
            var repair=model.Decisions.Last();var repairInput=JObject.Parse(repair.Input);
            Check((bool?)repairInput["decisionRepair"]?["preserveNoAction"]==true&&model.Speech.Count==speech,
                "Repair preserves the no-action choice before any speech or motion is released.");
            var actualDecision=escalates?new JObject { ["intent"]="turn-in-place",["origin"]="autonomous",["evidence"]=intention,
                ["viewChange"]=new JObject { ["captureId"]=repairInput["visualObservation"]?["captureId"]?.DeepClone(),["yaw"]=180,["pitch"]=0 } }:
                new JObject { ["intent"]="none",["replyScope"]="spatial",["origin"]="none",["evidence"]="" };
            repair.Complete(true,actualDecision.ToString(),"controlled-autonomous-no-action-repair");
            yield return Wait(()=>model.Speech.Count==speech+1,8,"No-action or rejected escalation proceeds to explanation without a body turn");
            Check(model.Decisions.Count==rooms+2&&model.Matches.Count==matches&&bridge.Controller.OperationRevision==operation&&
                Quaternion.Angle(body,bridge.avatar.transform.rotation)<.1f&&
                JToken.DeepEquals(state,JObject.Parse(JsonUtility.ToJson(lamp.ReadState())))&&
                (bool)Get(chat,"m_SceneActionActive")==false&&Call(chat,"GetActiveSpatialGoalId")==null,
                "An explanation-only tick and its repair cannot perform the repeated half-turn, match a target or change the lamp.");
            string export=Path.Combine(directory,"autonomous-explanation-planner");Directory.CreateDirectory(export);
            if(!escalates)
                foreach(var item in new[]{new{Id="initial-explanation",Request=initial},new{Id="repair-remains-none",Request=repair}})
                {
                    File.WriteAllText(Path.Combine(export,item.Id+".input.json"),item.Request.Input);
                    File.WriteAllText(Path.Combine(export,item.Id+".request.json"),item.Request.Wire);
                    File.WriteAllText(Path.Combine(export,item.Id+".image-data-url.txt"),item.Request.Image);
                    File.WriteAllText(Path.Combine(export,item.Id+".history.json"),PendingReplayHistory(item.Request).ToString());
                }
            results.Add(new JObject { ["escalationProbe"]=escalates,["initialInput"]=input,["repairInput"]=repairInput,
                ["submittedRepair"]=actualDecision,["bodyOperationUnchanged"]=bridge.Controller.OperationRevision==operation });
        }
        Set(chat,"m_AgentCurrentRoundIsTick",false);Set(chat,"m_SceneAutonomyIntention","");
        LastReport["autonomousExplanationRepair"]=results;
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="autonomous-explanation-repair-cannot-repeat-old-human-turn" });
    }

    private static IEnumerator ExerciseApprovedIndependentTurnWithoutFirstReply(ChatSample chat,ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model,NevaSceneVisionCapture capture,SceneObjectLightSwitch lamp)
    {
        Call(chat,"CancelSpatialGoalRunner","unrelated-no-first-reply-start");Call(chat,"CancelSpatialGoal","unrelated-no-first-reply-start");
        Call(chat,"CancelSceneAction","unrelated-no-first-reply-start");Call(chat,"ClearPendingDeviceClarification","unrelated-no-first-reply-start");
        yield return Wait(()=>!(Get(chat,"m_SceneAttention") is NevaSceneAttention attention)||!attention.IsBusy,5,"Prior head ownership releases before independent turn");
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags|BindingFlags.Static).Invoke(null,new object[]{chat});
        var savedSemantics=Get(chat,"m_FirstReplyRequestSemantics");var savedSemanticsModel=Get(chat,"m_FirstReplyRequestSemanticsModel");
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");var oldTts=settings.m_TextToSpeech;
        var audio=(AudioSource)Get(chat,"m_AudioSource");float oldVolume=audio.volume;
        var audioHost=new GameObject("Independent turn reply playback fixture");var tts=audioHost.AddComponent<UnifiedDeviceAnswerPlaybackTTS>();
        settings.m_TextToSpeech=tts;audio.volume=0;
        try
        {
            Set(chat,"m_EnableFirstReplyRouting",false);Set(chat,"m_FirstReplyRequestSemantics",null);Set(chat,"m_FirstReplyRequestSemanticsModel",null);
            Set(chat,"m_IndependentSpatialRequestScope",null);Call(chat,"RecordAcceptedMotionUserTurn",OnRequest);
            Check(Register(chat,OnRequest,"on"),"The independent-turn case starts with a genuinely reviewed old device goal.");
            Call(chat,"BindSpatialGoalExecution",Get(chat,"m_SpatialDeviceGoal"));
            string goalId=(string)Goal(chat)["goalId"];
            Check((string)Goal(chat)["status"]=="searching"&&(bool?)Goal(chat)["executionCurrent"]==true,
                "The old goal is searchable, not blocked or waiting for clarification, so those states cannot hide an accidental resume lease.");
            bool native=Array.IndexOf(Environment.GetCommandLineArgs(),"-nevaSpatialGoalRunnerNative")>=0;
            string latest=native?"现在只向左转身30度。":"现在只向左看30度。";
            Call(chat,"RecordAcceptedMotionUserTurn",latest);Set(chat,"m_LastUserMsg",latest);Set(chat,"m_LastUserEvidence",latest);
            Call(chat,"MarkUserTurnAwaitingReply");Call(chat,"ResetWorkReview",true);
            SceneVisionObservation source=null;yield return Capture(chat,"unrelated-no-first-reply-source",value=>source=value);
            int rooms=model.Decisions.Count(request=>request.Kind=="room"),reviews=model.DeviceGoalReviews.Count,speeches=model.Speech.Count;
            int searches=model.SceneSearchRequests.Count,matches=model.Matches.Count,operation=bridge.Controller.OperationRevision;
            var deviceState=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));Quaternion body=bridge.avatar.transform.rotation;
            Call(chat,"StartStreaming",latest,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,8,"Independent current turn reaches the actual old-goal relation request");
            int generation=(int)Get(chat,"m_FormalResponseGeneration");
            Check(Call(chat,"ReadCompactCurrentRequestSemantics",generation)==null,
                "FirstReply is disabled and no stale current-action receipt supplies this case's protection.");
            model.DeviceGoalReviews.Last().Complete(true,new JObject { ["verdict"]="approved",["relation"]="unrelated",["responseMode"]="act",
                ["referentGoalId"]="",["referentEntityId"]="",["reason"]="The complete current request is an independent turn, not continuation of the device task." }.ToString(),"controlled-approved-unrelated-without-first-reply");
            yield return Wait(()=>model.Decisions.Count(request=>request.Kind=="room")==rooms+1,8,"Approved unrelated action reaches current-only planning without a FirstReply receipt");
            var planner=model.Decisions.Last(request=>request.Kind=="room");var input=JObject.Parse(planner.Input);
            Check((bool?)input["currentRequestOnly"]==true&&input["spatialGoal"].Type==JTokenType.Null&&
                (string)input["currentUserText"]==latest&&(string)input["standingGoal"]?["goalId"]==goalId&&
                (bool?)input["decisionContractFacts"]?["deviceGoalAlreadyReviewed"]==false&&
                !(bool)Get(Get(chat,"m_SpatialDeviceGoal"),"resumeAfterDialogue")&&Call(chat,"GetActiveSpatialGoalId")==null,
                "Approved unrelated act independently fences device authority and cannot grant an after-dialogue resume lease.");
            // A formal restart in the same accepted human turn must preserve the
            // actual relation result, rather than requiring another FirstReply.
            var retiredPlanner=planner;int userTurn=(int)Get(Get(chat,"m_ActionConstraints"),"<CurrentUserTurn>k__BackingField");
            Call(chat,"StartStreaming",latest,false,null,false,false,false,null,false,capture.LatestObservation,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.Decisions.Count(request=>request.Kind=="room")==rooms+2,8,"Same-turn restart retains the independently reviewed current-action fence");
            planner=model.Decisions.Last(request=>request.Kind=="room");var restarted=JObject.Parse(planner.Input);
            Check((int)Get(Get(chat,"m_ActionConstraints"),"<CurrentUserTurn>k__BackingField")==userTurn&&model.DeviceGoalReviews.Count==reviews+1&&
                (bool?)restarted["currentRequestOnly"]==true&&restarted["spatialGoal"].Type==JTokenType.Null&&
                Call(chat,"ReadCompactCurrentRequestSemantics",(int)Get(chat,"m_FormalResponseGeneration"))==null,
                "Same-user restart needs no new semantic review and cannot inherit the old device execution.");
            retiredPlanner.Complete(true,new JObject { ["intent"]="none",["origin"]="user",["evidence"]=latest,
                ["deviceGoal"]=new JObject { ["operation"]="set",["requestQuote"]=latest,["targetDescription"]="左边这盏灯",["desiredState"]="on" } }.ToString(),"controlled-late-retired-unrelated-planner");
            yield return null;
            Check(model.DeviceGoalReviews.Count==reviews+1&&model.SceneSearchRequests.Count==searches&&model.Matches.Count==matches&&
                model.Speech.Count==speeches&&Get(chat,"m_SpatialGoalRunner")==null&&bridge.Controller.OperationRevision==operation,
                "The retired callback cannot reopen a proposal or submit any operation under the new formal owner.");
            string intent=native?"turn-in-place":"orient-view";
            var decision=new JObject { ["intent"]=intent,["origin"]="user",["evidence"]=latest,
                ["viewChange"]=new JObject { ["captureId"]=capture.LatestObservation.CaptureId,["yaw"]=-30,["pitch"]=0 } };
            planner.Complete(true,decision.ToString(),"controlled-current-independent-left-turn");
            yield return Wait(()=>model.Speech.Count==speeches+1,20,"The real independent turn completes before its reply");
            var fact=JObject.Parse(JsonUtility.ToJson(Get(chat,"m_SceneActionFact")));
            float bodyYaw=Vector3.SignedAngle(body*Vector3.forward,bridge.avatar.transform.forward,Vector3.up);
            Check((string)fact["intent"]==intent&&(string)fact["phase"]=="observed"&&
                (native?bodyYaw < -10&&bridge.Controller.OperationRevision>operation:Quaternion.Angle(body,bridge.avatar.transform.rotation)<.1f)&&
                model.SceneSearchRequests.Count==searches&&model.Matches.Count==matches&&Get(chat,"m_SpatialGoalRunner")==null&&
                JToken.DeepEquals(deviceState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
                "The left view/body action really executes without starting a device search, match or write.");
            var speech=model.Speech.Last();Check(speech.Stage!=FormalResponseStage.SpatialGoalResult,
                "The current turn gets its own result, not the unrelated old device terminal reply.");
            const string answer="已经向左转过来了。";int history=model.m_DataList.Count,spokenReviews=model.Decisions.Count(request=>request.Kind=="room-review");
            speech.SpeechCallback(new SpeechText(answer,"zh",actionDependency:SpeechActionDependency.Independent));speech.SpeechCompleteCallback(answer);
            yield return Wait(()=>model.Decisions.Count(request=>request.Kind=="room-review")==spokenReviews+1,8,"The actual independent-action reply reaches public consistency review");
            model.Decisions.Last(request=>request.Kind=="room-review").Complete(true,new JObject { ["verdict"]="consistent",["factScope"]="scene-action",
                ["resultReport"]="not-applicable",["reason"]="The reply describes only the completed current left turn." }.ToString(),"controlled-independent-turn-reply-approved");
            Check(model.m_DataList.Count==history+1&&model.m_DataList.Last().content==answer,
                "Only the audited current-action answer is published once.");
            yield return Wait(()=>tts.Started&&chat.IsVoiceOutputPlaying&&chat.IsAISpeaking&&audio.timeSamples>0,8,"The audited reply plays real muted PCM through the production output gate");
            Check(!(bool)Call(chat,"TryResumeSpatialGoalAfterDialogue")&&Get(chat,"m_SpatialGoalRunner")==null,
                "The unrelated old device cannot resume while the independent reply is actually playing.");
            tts.ReleasePlayback=true;
            yield return Wait(()=>tts.Completed&&!chat.IsVoiceOutputPlaying&&!chat.IsAISpeaking&&
                (!(bool)Get(chat,"m_FormalResponseInFlight")&&!(bool)Get(chat,"m_AgentRoundInFlight")||Get(chat,"m_SpatialGoalRunner")!=null),8,
                "Natural PCM drain reaches the production dialogue-complete boundary");
            Call(chat,"ClearScheduledAgentWake");
            int completedGeneration=(int)Get(chat,"m_FormalResponseGeneration"),completedOperation=bridge.Controller.OperationRevision;
            Check(!(bool)Call(chat,"TryResumeSpatialGoalAfterDialogue"),"Completed independent speech cannot acquire the old device goal's execution lease.");
            yield return null;yield return null;yield return null;
            Check((int)Get(chat,"m_FormalResponseGeneration")==completedGeneration&&Get(chat,"m_SpatialGoalRunner")==null&&
                model.SceneSearchRequests.Count==searches&&model.Matches.Count==matches&&bridge.Controller.OperationRevision==completedOperation&&
                (string)Goal(chat)["goalId"]==goalId&&(string)Goal(chat)["originalRequest"]==OnRequest&&
                (bool?)Goal(chat)["executionCurrent"]==false&&!(bool)Get(Get(chat,"m_SpatialDeviceGoal"),"resumeAfterDialogue")&&
                JToken.DeepEquals(deviceState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
                "After actual reply playback and idle frames, no search, new turn or device write resumes; the unchanged old objective stays paused.");
            LastReport["approvedUnrelatedWithoutFirstReply"]=new JObject { ["plannerInput"]=input,["restartedPlannerInput"]=restarted,
                ["actualAction"]=fact,["bodyYawDegrees"]=bodyYaw,["nativeBodyTurn"]=native,["publishedReply"]=answer,["pcmCompleted"]=tts.Completed,
                ["goalAfterPlayback"]=Goal(chat),["scope"]="Real StartStreaming, controlled approved-unrelated relation, current-only planner, same-user restart/stale callback, real body/head action, formal speech review and muted PCM playback. No live semantic model or external TTS; no old device task is executed." };
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="approved-unrelated-act-without-first-reply-never-resumes-after-actual-playback" });
        }
        finally
        {
            tts.CancelStreaming();settings.m_TextToSpeech=oldTts;audio.volume=oldVolume;UnityEngine.Object.Destroy(audioHost);
            Set(chat,"m_FirstReplyRequestSemantics",savedSemantics);Set(chat,"m_FirstReplyRequestSemanticsModel",savedSemanticsModel);
        }
    }

    private static IEnumerator ExerciseUnapprovedGoalUpdateIndependentRequest(ChatSample chat,ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model,NevaSceneVisionCapture capture,SceneObjectLightSwitch lamp)
    {
        var cases=new JArray();
        foreach(string bypass in new[]{"state-write","new-proposal","cancel-proposal"})
        {
            Call(chat,"CancelSpatialGoalRunner","current-request-only-next");Call(chat,"CancelSpatialGoal","current-request-only-next");
            Call(chat,"CancelSceneAction","current-request-only-next");Call(chat,"ClearPendingDeviceClarification","current-request-only-next");
            yield return Wait(()=>!(Get(chat,"m_SceneAttention") is NevaSceneAttention attention)||!attention.IsBusy,5,"Independent request starts after head ownership releases");
            typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags|BindingFlags.Static).Invoke(null,new object[]{chat});
            Set(chat,"m_EnableFirstReplyRouting",false);
            Call(chat,"RecordAcceptedMotionUserTurn",OnRequest);
            Check(Register(chat,OnRequest,"on"),"An approved old on goal exists before the independent request.");
            string main=(string)Goal(chat)["goalId"];
            Call(chat,"SetSpatialGoalAwaitingClarification","controlled-old-goal-cannot-resolve-target");
            bool turning=bypass=="cancel-proposal";
            bool nativeTurn=turning&&Array.IndexOf(Environment.GetCommandLineArgs(),"-nevaSpatialGoalRunnerNative")>=0;
            string latest=turning?(nativeTurn?"你转身，向左转30度。":"请向左看30度。") : "你先看看左边这盏灯，不要开关它。";
            Call(chat,"RecordAcceptedMotionUserTurn",latest);Call(chat,"ResetWorkReview",true);
            SceneVisionObservation source=null;yield return Capture(chat,"current-request-only-source",value=>source=value);
            int reviews=model.DeviceGoalReviews.Count,rooms=model.Decisions.Count,matches=model.Matches.Count,speech=model.Speech.Count;
            int operation=bridge.Controller.OperationRevision;Quaternion body=bridge.avatar.transform.rotation;
            var state=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
            Call(chat,"StartStreaming",latest,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,8,"Old goal update is reviewed before planning the new independent request");
            var update=model.DeviceGoalReviews.Last();
            update.Complete(true,new JObject { ["verdict"]="uncertain",["relation"]="unrelated",["responseMode"]="act",["referentGoalId"]="",["referentEntityId"]="",
                ["reason"]="Controlled old goal review cannot approve continuation; latest request is independent." }.ToString(),"controlled-unapproved-old-goal-update");
            yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Unapproved old goal update still reaches the current-request planner");
            var planner=model.Decisions.Last();var input=JObject.Parse(planner.Input);
            Check((bool?)input["currentRequestOnly"]==true&&(bool?)input["quietSpatialGoalStep"]==false&&
                (string)input["currentUserText"]==latest&&(string)input["latestUserText"]==latest&&input["spatialGoal"].Type==JTokenType.Null&&
                (string)input["standingGoal"]?["goalId"]==main&&(bool?)input["decisionContractFacts"]?["deviceGoalAlreadyReviewed"]==false&&
                (string)Goal(chat)["status"]=="waiting_clarification"&&Call(chat,"GetActiveSpatialGoalId")==null&&model.Speech.Count==speech,
                "Only the new utterance reaches planning; the old request survives as paused background without a terminal reply swallowing the new task.");
            if(turning)
            {
                string replay=Path.Combine(directory,"current-request-only-turn");Directory.CreateDirectory(replay);
                File.WriteAllText(Path.Combine(replay,"input.json"),planner.Input);
                File.WriteAllText(Path.Combine(replay,"image-data-url.txt"),planner.Image);
                File.WriteAllText(Path.Combine(replay,"request.json"),planner.Wire);
            }
            var injected=new JObject { ["intent"]="none",["origin"]="user",["evidence"]=latest };
            if(bypass=="state-write")
            {
                injected["intent"]="set-object-state";injected["objectState"]="on";
                injected["entityTarget"]=new JObject { ["captureId"]=source.CaptureId,["entityId"]=lamp.EntityId };
            }
            else injected["deviceGoal"]=bypass=="cancel-proposal" ?
                new JObject { ["operation"]="cancel",["requestQuote"]=latest } :
                new JObject { ["operation"]="set",["requestQuote"]=latest,["targetDescription"]="左边这盏灯",["desiredState"]="on" };
            Check(RoomTaskProtocol.TryDecision(injected.ToString(),latest,false,out _),"The controlled bypass is structurally valid and must be stopped by authority, not syntax: "+bypass);
            if(bypass=="state-write")
            {
                var supersededPlanner=planner;
                var ledger=Get(chat,"m_ActionConstraints");int sameUserTurn=(int)Get(ledger,"<CurrentUserTurn>k__BackingField");
                // Retry the formal response, not the user's request: no accepted
                // turn is added and no successful relation review is supplied.
                source=capture.LatestObservation;
                Call(chat,"StartStreaming",latest,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
                yield return Wait(()=>model.Decisions.Count==rooms+2,8,"Same-user-turn formal restart retains current-only authority");
                planner=model.Decisions.Last();input=JObject.Parse(planner.Input);
                Check((int)Get(ledger,"<CurrentUserTurn>k__BackingField")==sameUserTurn&&model.DeviceGoalReviews.Count==reviews+1&&
                    (bool?)input["currentRequestOnly"]==true&&(string)input["currentUserText"]==latest&&input["spatialGoal"].Type==JTokenType.Null&&
                    (bool?)input["decisionContractFacts"]?["deviceGoalAlreadyReviewed"]==false&&
                    (string)Goal(chat)["goalId"]==main&&(string)Goal(chat)["status"]=="waiting_clarification",
                    "A new formal generation in the same user turn cannot forget the failed relation review or inherit its old device authority.");
                injected["entityTarget"]["captureId"]=capture.LatestObservation.CaptureId;
                supersededPlanner.Complete(true,injected.ToString(),"controlled-superseded-current-only-state-write");
                yield return null;
                Check(model.Decisions.Count==rooms+2&&model.DeviceGoalReviews.Count==reviews+1&&model.Matches.Count==matches&&
                    model.Speech.Count==speech&&Get(chat,"m_SpatialGoalRunner")==null&&
                    JToken.DeepEquals(state,JObject.Parse(JsonUtility.ToJson(lamp.ReadState())))&&bridge.Controller.OperationRevision==operation,
                    "A late callback from the replaced planner cannot write, reopen review, match or publish during the restarted generation.");
                ++rooms; // The independent repair assertions below now own the restarted planner.
                ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="same-user-turn-formal-restart-preserves-current-only-device-fence" });
            }
            planner.Complete(true,injected.ToString(),"controlled-current-only-authority-bypass");
            yield return Wait(()=>model.Decisions.Count==rooms+2,8,"Unauthorized device operation receives one bounded current-request repair");
            var repair=model.Decisions.Last();var repairedInput=JObject.Parse(repair.Input);
            Check((bool?)repairedInput["currentRequestOnly"]==true&&(string)repairedInput["currentUserText"]==latest&&
                ((string)repairedInput["decisionRepair"]?["rejectionReason"]).StartsWith("invalid-current-request-only-device-authority",StringComparison.Ordinal)&&
                model.DeviceGoalReviews.Count==reviews+1&&model.Matches.Count==matches&&model.Speech.Count==speech&&
                Get(chat,"m_SpatialGoalRunner")==null&&(string)Goal(chat)["goalId"]==main&&(string)Goal(chat)["status"]=="waiting_clarification"&&
                JToken.DeepEquals(state,JObject.Parse(JsonUtility.ToJson(lamp.ReadState())))&&bridge.Controller.OperationRevision==operation,
                "Repair retains the current-only fence; old state write, new proposal and cancellation cannot reauthorize, replace, cancel, match or mutate the paused device goal: "+bypass);
            var current=capture.LatestObservation;
            JObject valid;
            if(turning) valid=new JObject { ["intent"]=nativeTurn?"turn-in-place":"orient-view",["origin"]="user",["evidence"]=latest,
                ["viewChange"]=new JObject { ["captureId"]=current.CaptureId,["yaw"]=-30,["pitch"]=0 } };
            else
            {
                Check(capture.TryFindObservedObject(current,lamp.EntityId,out var seen,out string why)&&seen.VisibleConfirmed,"Independent inspection has actual current target pixels: "+why);
                // The public wire contains only the parser's allowed fields.
                valid=new JObject { ["intent"]="inspect-object",["origin"]="user",["evidence"]=latest,
                    ["entityTarget"]=new JObject { ["captureId"]=current.CaptureId,["entityId"]=lamp.EntityId } };
            }
            Check(RoomTaskProtocol.TryDecision(valid.ToString(),latest,false,out _,out string parserFailure),"The replacement independent action passes the production parser: "+parserFailure);
            repair.Complete(true,valid.ToString(),"controlled-independent-action-after-old-goal-review-failure");
            if(!turning)
            {
                yield return Wait(()=>model.Matches.Count==matches+1,8,"Independent current request reaches normal semantic inspection");
                var match=model.Matches.Last();var matchInput=JObject.Parse(match.Input);
                Check((string)matchInput["originalGoal"]==latest&&matchInput["spatialGoal"].Type==JTokenType.Null,
                    "The matcher inspects the user's current purpose without inheriting the paused on objective.");
                var offered=chat.LastSceneObjectCandidateView.Candidates.Single(value=>value.entityId==lamp.EntityId);
                match.Complete(true,Reply("matched",offered.candidateId),"controlled-independent-current-inspection");
            }
            yield return Wait(()=>model.Speech.Count==speech+1,20,"Independent action completes and reaches its own reply");
            var actual=JObject.Parse(JsonUtility.ToJson(Get(chat,"m_SceneActionFact")));
            Check((string)actual["intent"]==(turning?(nativeTurn?"turn-in-place":"orient-view"):"inspect-object")&&(string)actual["phase"]=="observed"&&
                model.Speech.Last().Stage!=FormalResponseStage.SpatialGoalResult&&(string)Goal(chat)["goalId"]==main&&
                (string)Goal(chat)["status"]=="waiting_clarification"&&Call(chat,"GetActiveSpatialGoalId")==null&&
                JToken.DeepEquals(state,JObject.Parse(JsonUtility.ToJson(lamp.ReadState())))&&
                (!turning||(nativeTurn?(bridge.Controller.OperationRevision>operation&&Quaternion.Angle(body,bridge.avatar.transform.rotation)>10):
                    Quaternion.Angle(body,bridge.avatar.transform.rotation)<.1f)),
                "The requested inspection/turn really executes, while the old device goal remains unresolved and the lamp state unchanged.");
            cases.Add(new JObject { ["bypass"]=bypass,["plannerInput"]=input,["repairInput"]=repairedInput,["actualAction"]=actual });
        }
        LastReport["unapprovedGoalUpdateIndependentRequests"]=cases;
    }

    private static IEnumerator ExerciseApprovedGoalUpdateRouting(ChatSample chat,ArdyRoomDialogueBridge bridge,NevaSceneVisionIntegrationModel model)
    {
        foreach(string relation in new[]{"cancel","new"})
        {
            Call(chat,"CancelSpatialGoalRunner","approved-update-routing-next");Call(chat,"CancelSpatialGoal","approved-update-routing-next");
            Call(chat,"CancelSceneAction","approved-update-routing-next");Call(chat,"ClearPendingDeviceClarification","approved-update-routing-next");
            typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags|BindingFlags.Static).Invoke(null,new object[]{chat});
            Set(chat,"m_EnableFirstReplyRouting",false);Call(chat,"RecordAcceptedMotionUserTurn",OnRequest);
            Check(Register(chat,OnRequest,"on"),"Approved routing control owns an unfinished device goal.");
            string main=(string)Goal(chat)["goalId"];Call(chat,"SetSpatialGoalAwaitingClarification","controlled-approved-update-routing");
            string latest=relation=="cancel"?"取消刚才的灯任务。":"取消刚才的灯任务，现在看看面前。";
            Call(chat,"RecordAcceptedMotionUserTurn",latest);Call(chat,"ResetWorkReview",true);
            SceneVisionObservation source=null;yield return Capture(chat,"approved-update-routing-source",value=>source=value);
            int reviews=model.DeviceGoalReviews.Count,rooms=model.Decisions.Count,speech=model.Speech.Count,matches=model.Matches.Count;
            Call(chat,"StartStreaming",latest,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,8,"Explicit cancellation/replacement retains its normal independent update review");
            model.DeviceGoalReviews.Last().Complete(true,new JObject { ["verdict"]="approved",["relation"]=relation,["responseMode"]="act",["referentGoalId"]=main,
                ["referentEntityId"]="",["reason"]="Controlled explicit cancellation or replacement." }.ToString(),"controlled-approved-update-routing");
            yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Approved update plans every remaining instruction in the actual latest utterance");
            Check((string)Goal(chat)["status"]=="cancelled"&&Call(chat,"GetActiveSpatialGoalId")==null,
                "An approved explicit cancellation/replacement still retires the old objective.");
            if(relation=="cancel")
            {
                var input=JObject.Parse(model.Decisions.Last().Input);
                Check((string)input["currentUserText"]==latest&&(bool?)input["currentRequestOnly"]==true&&
                    model.Speech.Count==speech&&(bool?)((JObject)Get(chat,"m_SpatialGoalLatestReview"))["cancellationApplied"]==true,
                    "Approved cancellation clears old authority and still plans the latest utterance; only the planner can decide whether a same-sentence action remains.");
                var superseded=model.Decisions.Last();int userTurn=(int)Get(Get(chat,"m_ActionConstraints"),"<CurrentUserTurn>k__BackingField");
                Call(chat,"StartStreaming",latest,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
                yield return Wait(()=>model.Decisions.Count==rooms+2,8,"Approved cancellation survives a formal restart before its final none decision");
                input=JObject.Parse(model.Decisions.Last().Input);
                Check((bool?)input["currentRequestOnly"]==true&&(bool?)input["latestGoalUpdate"]?["cancellationApplied"]==true&&
                    (int?)input["latestGoalUpdate"]?["generation"]==(int)Get(chat,"m_FormalResponseGeneration")&&
                    (int)Get(Get(chat,"m_ActionConstraints"),"<CurrentUserTurn>k__BackingField")==userTurn&&
                    model.DeviceGoalReviews.Count==reviews+1&&model.Speech.Count==speech,
                    "Same-turn recovery preserves the actual cancellation receipt with current-generation ownership and never repeats its review.");
                superseded.Complete(true,new JObject { ["intent"]="none",["replyScope"]="ordinary",["origin"]="none",["evidence"]="" }.ToString(),"controlled-late-pre-recovery-cancel-none");
                Check(model.Speech.Count==speech,"A superseded cancellation planner cannot report or complete the recovered round.");
                model.Decisions.Last().Complete(true,new JObject { ["intent"]="none",["replyScope"]="ordinary",["origin"]="none",["evidence"]="" }.ToString(),"controlled-pure-cancellation-no-follow-up-action");
                yield return Wait(()=>model.Speech.Count==speech+1,8,"Pure cancellation planner chooses no extra action and reaches one reply");
                bool readCancellation=SpatialGoalResultContextPolicy.TryReadContext(model.Speech.Last().Context,out _,out var cancellationFacts);
                Check(model.Matches.Count==matches&&Call(chat,"GetActiveSpatialGoalId")==null&&model.Speech.Last().Stage==FormalResponseStage.SpatialGoalResult,
                    "A pure cancellation keeps the retired objective inactive after the explicit none decision.");
                Check(readCancellation&&SpatialGoalResultContextPolicy.RequiredReport(cancellationFacts)=="cancelled"&&
                    (bool?)cancellationFacts["latestGoalReview"]?["cancellationApplied"]==true,
                    "The recovered pure cancellation reports actual cancellation, not an unresolved or silently forgotten update.");
            }
            else
            {
                var input=JObject.Parse(model.Decisions.Last().Input);
                Check((string)input["currentUserText"]==latest&&(bool?)input["currentRequestOnly"]!=true&&model.Speech.Count==speech,
                    "Approved replacement plans the actual new request under the normal proposal-review path, without the failed-update restriction.");
            }
        }
    }

    private static IEnumerator ExercisePendingDialogueRecovery(ChatSample chat,ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model,NevaSceneVisionCapture capture,SceneObjectLightSwitch lamp)
    {
        const string original="现在去把你刚才看到的那盏小灯关掉。";
        Call(chat,"CancelSpatialGoalRunner","pending-dialogue-new-scope");Call(chat,"CancelSpatialGoal","pending-dialogue-new-scope");
        Call(chat,"CancelSceneAction","pending-dialogue-new-scope");Call(chat,"ClearPendingDeviceClarification","pending-dialogue-new-scope");
        yield return Wait(()=>!(Get(chat,"m_SceneAttention") is NevaSceneAttention attention)||!attention.IsBusy,5,"Dialogue replay releases preceding head ownership");
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags|BindingFlags.Static).Invoke(null,new object[]{chat});
        Set(chat,"m_EnableFirstReplyRouting",false);Set(chat,"m_SpatialDeviceGoal",null);
        ((IList)Get(chat,"m_VerifiedSpatialDeviceHistory")).Clear();((IList)Get(chat,"m_VerifiedObservedDeviceHistory")).Clear();
        var initialState=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
        int matches=model.Matches.Count,rooms=model.Decisions.Count,reviews=model.DeviceGoalReviews.Count,speech=model.Speech.Count;
        Call(chat,"RecordAcceptedMotionUserTurn",original);Call(chat,"ResetWorkReview",true);
        SceneVisionObservation source=null;yield return Capture(chat,"pending-dialogue-empty-history",value=>source=value);
        Call(chat,"StartStreaming",original,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
        yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Historical lamp request reaches the real Room planner");
        var initialDecision=new JObject { ["intent"]="none",["replyScope"]="ordinary",["origin"]="user",["evidence"]=original,
            ["deviceGoal"]=new JObject { ["operation"]="set",["requestQuote"]=original,
                ["targetDescription"]="刚才描述的电视柜上的小灯",["desiredState"]="off" } };
        Check(RoomTaskProtocol.TryDecision(initialDecision.ToString(),original,false,out var parsedInitial)&&
            parsedInitial.intent=="none"&&parsedInitial.origin=="user"&&parsedInitial.evidence==original&&parsedInitial.deviceGoal!=null,
            "A valid no-action device proposal preserves its checked user provenance so unresolved identity can become a draft without granting action authority.");
        model.Decisions.Last().Complete(true,initialDecision.ToString(),"controlled-dialogue-empty-history-off-proposal");
        yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,8,"Empty-history request receives independent semantic review");
        var initialReview=model.DeviceGoalReviews.Last();var initialReviewInput=JObject.Parse(initialReview.Input);
        Check(((JArray)initialReviewInput["referentSources"]).Count==0&&model.Matches.Count==matches,
            "No verified observed or operated source is invented from the conversational description.");
        // This fixture starts from genuinely unresolved user target semantics. An
        // empty verified-source catalog alone would not justify clarification.
        var ambiguityReview=new JObject { ["verdict"]="uncertain",["requestAssessment"]="target-unclear",
            ["relation"]="new",["referentKind"]="unbound",["referentSourceId"]="none",
            ["reason"]="Controlled target ambiguity requires the user's missing distinguishing condition; no intended lamp has been selected." };
        Check(RoomTaskProtocol.TryDeviceGoalProposalReview(ambiguityReview.ToString(),out var ambiguityVerdict)&&
            RoomTaskProtocol.RetainsPendingDeviceProposal(ambiguityVerdict),"Only a valid semantic target ambiguity can establish this dialogue-recovery draft.");
        initialReview.Complete(true,ambiguityReview.ToString(),"controlled-dialogue-target-unclear");
        yield return Wait(()=>model.Speech.Count==speech+1,8,"Initial unresolved identity reaches one result reply");
        var pending=(JObject)Call(chat,"BuildPendingDeviceClarificationContext");string pendingId=(string)pending["pendingId"];
        var initialReply=model.Speech.Last();
        bool hasNoSearchContext=SpatialGoalResultContextPolicy.TryReadContext(initialReply.Context,out _,out var noSearchFacts);
        Check(!string.IsNullOrEmpty(pendingId)&&hasNoSearchContext&&
            noSearchFacts["lastAction"]?.Type==JTokenType.Null&&Call(chat,"GetActiveSpatialGoalId")==null,
            "Unapproved identity is not a completed search or a registered executable lamp goal.");
        const string unsupportedSearch="我已经四处找过了，没有找到那盏小灯。";
        int beforeBadSpeech=model.m_DataList.Count,beforeBadReview=model.Decisions.Count;
        initialReply.SpeechCallback(new SpeechText(unsupportedSearch,"zh",actionDependency:SpeechActionDependency.Independent));
        initialReply.SpeechCompleteCallback(unsupportedSearch);
        yield return Wait(()=>model.Decisions.Count==beforeBadReview+1,8,"Unsupported searched/not-found claim is independently audited before publication");
        var searchReviewInput=JObject.Parse(model.Decisions.Last().Input);
        Check(model.m_DataList.Count==beforeBadSpeech&&searchReviewInput["sceneActionFacts"]?["lastAction"]?.Type==JTokenType.Null,
            "The candidate is still private and its real review input contains no search action receipt.");
        model.Decisions.Last().Complete(true,new JObject { ["verdict"]="inconsistent",["factScope"]="scene-action",
            ["resultReport"]="clarification",["reason"]="No search was performed; an unresolved identity does not prove not found." }.ToString(),"controlled-dialogue-unsearched-claim-rejected");
        Check(model.m_DataList.Count==beforeBadSpeech+1&&model.m_DataList.Last().content!=unsupportedSearch&&
            !model.m_DataList.Last().content.Contains("没找到")&&!model.m_DataList.Last().content.Contains("没有找到"),
            "The actual released fallback does not report a failed search that never ran.");
        var sequence=new JArray();
        bool native=Array.IndexOf(Environment.GetCommandLineArgs(),"-nevaSpatialGoalRunnerNative")>=0;
        var latestTexts=new[]{"你自己找找呀。","转身90度，向左转90度。","先不要管那个灯了，你先左转90度。",
            "取消那个灯的任务，然后向左转90度。","好，再向左转90度。"};
        for(int step=0;step<latestTexts.Length;step++)
        {
            Call(chat,"CancelSceneAction","pending-dialogue-next-user-turn");
            yield return Wait(()=>!(Get(chat,"m_SceneAttention") is NevaSceneAttention attention)||!attention.IsBusy,5,"Next dialogue command starts after previous view ownership releases");
            string latest=latestTexts[step];bool approvedCancel=step==3,rejectedCancel=step==2,needsReview=step<4;
            Call(chat,"RecordAcceptedMotionUserTurn",latest);Call(chat,"ResetWorkReview",true);
            yield return Capture(chat,"pending-dialogue-turn-"+step,value=>source=value);
            rooms=model.Decisions.Count;reviews=model.DeviceGoalReviews.Count;speech=model.Speech.Count;
            int operation=bridge.Controller.OperationRevision;Quaternion body=bridge.avatar.transform.rotation;
            Call(chat,"StartStreaming",latest,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            NevaSceneVisionIntegrationModel.Request reviewed=null;JObject updateInput=null;
            if(needsReview)
            {
                yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,8,"Next real utterance is reviewed against the same quoted unapproved draft");
                reviewed=model.DeviceGoalReviews.Last();updateInput=JObject.Parse(reviewed.Input);
                Check((string)updateInput["pendingId"]==pendingId&&(string)updateInput["originalUserRequest"]==original&&
                    (string)updateInput["latestUserText"]==latest&&model.Decisions.Count==rooms&&model.Speech.Count==speech,
                    "Held relation review cannot perform, narrate or replace the user's exact current command.");
                reviewed.Complete(true,new JObject { ["verdict"]=approvedCancel?"approved":"rejected",
                    ["relation"]=approvedCancel||rejectedCancel?"cancel":"continue",["pendingClarificationId"]=pendingId,
                    ["referentKind"]="unbound",["referentSourceId"]="none",
                    ["reason"]="Controlled recovery branch; draft operation remains unapproved unless the relation explicitly cancels it." }.ToString(),"controlled-dialogue-relation-"+step);
            }
            yield return Wait(()=>model.Decisions.Count==rooms+1,8,"The current search/turn reaches its own planner despite the old pending proposal");
            var planner=model.Decisions.Last();var plannerInput=JObject.Parse(planner.Input);
            var pendingNow=(JObject)Call(chat,"BuildPendingDeviceClarificationContext");
            var latestReview=(JObject)Get(chat,"m_SpatialGoalLatestReview");
            var routingChecks=new JObject { ["currentUserTextPreserved"]=(string)plannerInput["currentUserText"]==latest,
                ["latestUserTextPreserved"]=(string)plannerInput["latestUserText"]==latest,
                // The ordinary new-user path omits this optional field. Only
                // true means goal continuation; the production tail maps missing
                // to false. Current-only requests additionally require explicit false.
                ["notQuietGoalContinuation"]=(bool?)plannerInput["quietSpatialGoalStep"]!=true,
                ["currentOnlyExplicitFalseWhenRequired"]=!needsReview||(bool?)plannerInput["quietSpatialGoalStep"]==false,
                ["noPrematureSpeech"]=model.Speech.Count==speech,["noMatcher"]=model.Matches.Count==matches,
                ["noActiveDeviceGoal"]=Call(chat,"GetActiveSpatialGoalId")==null,["noDeviceRunner"]=Get(chat,"m_SpatialGoalRunner")==null,
                ["deviceUnchanged"]=JToken.DeepEquals(initialState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))) };
            LastReport["pendingDialogueLatestRoutingCheck"]=new JObject { ["step"]=step,["userText"]=latest,["checks"]=routingChecks,
                ["quietSpatialGoalStepField"]=plannerInput["quietSpatialGoalStep"]?.DeepClone(),["plannerInput"]=plannerInput.DeepClone() };
            Check(routingChecks.Properties().All(property=>(bool)property.Value),
                "Planning uses the complete latest utterance without a premature old-goal reply, matcher or device write.");
            if(needsReview)
                Check((bool?)plannerInput["currentRequestOnly"]==true&&plannerInput["spatialGoal"].Type==JTokenType.Null&&
                    (bool?)latestReview["reviewApplied"]==approvedCancel&&(bool?)latestReview["cancellationApplied"]==approvedCancel&&
                    (approvedCancel?(string)pendingNow["status"]=="none":(string)pendingNow["pendingId"]==pendingId),
                    "Rejected cancellation is not reported as applied; an approved cancellation clears only the draft and still plans the same-sentence turn.");
            else Check(model.DeviceGoalReviews.Count==reviews&&(string)pendingNow["status"]=="none",
                "The following left-turn request is not swallowed by a cancelled draft or sent through another pending review.");
            if(step==0)
            {
                // Same accepted user turn, new formal generation: the fence must survive.
                var superseded=planner;int userTurn=(int)Get(Get(chat,"m_ActionConstraints"),"<CurrentUserTurn>k__BackingField");
                Call(chat,"StartStreaming",latest,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
                yield return Wait(()=>model.Decisions.Count==rooms+2,8,"Same-turn retry preserves the failed pending-review restriction");
                planner=model.Decisions.Last();plannerInput=JObject.Parse(planner.Input);
                Check((bool?)plannerInput["currentRequestOnly"]==true&&model.DeviceGoalReviews.Count==reviews+1&&
                    (int)Get(Get(chat,"m_ActionConstraints"),"<CurrentUserTurn>k__BackingField")==userTurn,
                    "A formal retry cannot treat the pending original off request as authorized current work.");
                superseded.Complete(true,new JObject { ["intent"]="none",["origin"]="user",["evidence"]=latest,
                    ["deviceGoal"]=new JObject { ["operation"]="cancel",["requestQuote"]=latest } }.ToString(),"controlled-late-pending-planner-cancel");
                reviewed.Complete(true,new JObject { ["verdict"]="approved",["relation"]="clarify",["pendingClarificationId"]=pendingId,
                    ["referentKind"]="unbound",["referentSourceId"]="none",["reason"]="Late contradictory duplicate must not regain authority." }.ToString(),"controlled-late-pending-review-approval");
                yield return null;
                Check(model.Decisions.Count==rooms+2&&model.DeviceGoalReviews.Count==reviews+1&&model.Matches.Count==matches&&
                    model.Speech.Count==speech&&(string)((JObject)Call(chat,"BuildPendingDeviceClarificationContext"))["pendingId"]==pendingId&&
                    Call(chat,"GetActiveSpatialGoalId")==null&&JToken.DeepEquals(initialState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
                    "Superseded planner and contradictory review callbacks cannot cancel/register/write or publish during retry.");
            }
            if(rejectedCancel)
            {
                var noActionDecision=new JObject { ["intent"]="none",["replyScope"]="ordinary",["origin"]="none",["evidence"]="" };
                Check(RoomTaskProtocol.TryDecision(noActionDecision.ToString(),latest,false,out var parsedNoAction)&&
                    parsedNoAction.intent=="none"&&parsedNoAction.replyScope=="ordinary",
                    "The controlled no-action reply uses the current explicit ordinary scope, not the legacy omitted-scope spatial-question default.");
                planner.Complete(true,noActionDecision.ToString(),"controlled-rejected-cancel-planner-none");
                yield return Wait(()=>model.Speech.Count==speech+1,8,"A no-action current-only decision reaches an independently guarded reply");
                var noActionReply=model.Speech.Last();const string falseCancellation="已经取消那个灯的任务了。";
                int history=model.m_DataList.Count,reviewRequests=model.Decisions.Count;
                Check(noActionReply.Stage==FormalResponseStage.CommittedSpatialReply&&!noActionReply.RecordAssistantHistory,
                    "Current-only none uses the guarded committed reply instead of the ordinary streaming route.");
                noActionReply.SpeechCallback(new SpeechText(falseCancellation,"zh",actionDependency:SpeechActionDependency.Independent));
                Check(model.m_DataList.Count==history&&((SpeechTextBuffer)Get(chat,"m_SentenceBuffer")).Length==0,
                    "Rejected-cancel prose cannot enter assistant history or the public sentence buffer before review.");
                noActionReply.SpeechCompleteCallback(falseCancellation);
                yield return Wait(()=>model.Decisions.Count==reviewRequests+1,8,"None/rejected-cancel speech submits a real consistency review");
                var noActionReview=model.Decisions.Last();var noActionInput=JObject.Parse(noActionReview.Input);
                Check(noActionReview.Kind=="room-review"&&(bool?)noActionInput["deviceGoalFacts"]?["latestGoalReview"]?["cancellationApplied"]==false&&
                    (bool?)noActionInput["deviceGoalFacts"]?["currentRequestSearchAttempted"]==false&&
                    (string)((JObject)Call(chat,"BuildPendingDeviceClarificationContext"))["pendingId"]==pendingId&&model.m_DataList.Count==history,
                    "The reviewer sees unapplied cancellation and no current search while the old draft remains unresolved.");
                noActionReview.Complete(true,new JObject { ["verdict"]="inconsistent",["factScope"]="scene-action",["resultReport"]="not-applicable",
                    ["reason"]="The engine retained this draft; a rejected cancel relation is not an applied cancellation." }.ToString(),"controlled-rejected-cancel-none-speech-verdict");
                LastReport["rejectedCancellationNoneSpeechGuard"]=new JObject { ["candidate"]=falseCancellation,["reviewInput"]=noActionInput,
                    ["plannerDecision"]=noActionDecision,["publishedText"]=model.m_DataList.Last().content,
                    ["historyEntriesAdded"]=model.m_DataList.Count-history,
                    ["pendingIdAfterReply"]=((JObject)Call(chat,"BuildPendingDeviceClarificationContext"))["pendingId"]?.DeepClone() };
                Check(model.m_DataList.Count==history+1&&model.m_DataList.Last().content=="这次还没有执行新的动作。"&&
                    (string)((JObject)Call(chat,"BuildPendingDeviceClarificationContext"))["pendingId"]==pendingId,
                    "The final published text reports no new action, without claiming cancellation, a failed search or an unrelated user-approach attempt, and keeps the pending request.");
                // Continue the same accepted command after that no-action response,
                // keeping its pending-review restriction and actual outcome receipt.
                rooms=model.Decisions.Count;speech=model.Speech.Count;
                yield return Capture(chat,"pending-dialogue-rejected-cancel-recovery",value=>source=value);
                Call(chat,"StartStreaming",latest,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
                yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Rejected-cancel none recovery still plans the original independent turn");
                planner=model.Decisions.Last();plannerInput=JObject.Parse(planner.Input);
                Check((bool?)plannerInput["currentRequestOnly"]==true&&(bool?)plannerInput["latestGoalUpdate"]?["cancellationApplied"]==false&&
                    model.DeviceGoalReviews.Count==reviews+1&&model.Speech.Count==speech,
                    "No-action speech completion and same-turn recovery neither authorize nor repeat the failed device update.");
            }
            if(step==1||approvedCancel)
                ExportPendingDialoguePlanner(planner,capture.LatestObservation,step==1?"failed-pending-left-90":"applied-cancel-left-90",approvedCancel,native);
            string intent=step>0&&native?"turn-in-place":"orient-view";
            var current=capture.LatestObservation;
            var decision=new JObject { ["intent"]=intent,["origin"]="user",["evidence"]=latest,
                ["viewChange"]=new JObject { ["captureId"]=current.CaptureId,["yaw"]=intent=="turn-in-place"?-90:-30,["pitch"]=0 } };
            Check(RoomTaskProtocol.TryDecision(decision.ToString(),latest,false,out _,out string parseReason),"Current independent movement has a valid production decision: "+parseReason);
            planner.Complete(true,decision.ToString(),"controlled-dialogue-current-action-"+step);
            yield return Wait(()=>model.Speech.Count==speech+1,35,"Actual independent view/turn completes and reaches its own reply");
            var actual=JObject.Parse(JsonUtility.ToJson(Get(chat,"m_SceneActionFact")));
            Check((string)actual["intent"]==intent&&(string)actual["phase"]=="observed"&&
                ((double?)actual["actualViewChangeDegrees"]??0)>10&&model.Speech.Last().Stage!=FormalResponseStage.SpatialGoalResult&&
                model.Matches.Count==matches&&Call(chat,"GetActiveSpatialGoalId")==null&&
                JToken.DeepEquals(initialState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState())))&&
                (intent=="turn-in-place"?(bridge.Controller.OperationRevision>operation&&Quaternion.Angle(body,bridge.avatar.transform.rotation)>60):
                    Quaternion.Angle(body,bridge.avatar.transform.rotation)<.1f),
                "The independent action changes the actual view/body; old off authority never returns and the lamp remains unchanged.");
            sequence.Add(new JObject { ["userText"]=latest,["reviewInput"]=updateInput,["plannerInput"]=plannerInput,
                ["reviewFacts"]=latestReview?.DeepClone(),["pendingAfterReview"]=pendingNow,["actualAction"]=actual });
        }
        LastReport["pendingDialogueRecovery"]=new JObject { ["initialReviewInput"]=initialReviewInput,["initialResultFacts"]=noSearchFacts,
            ["unsupportedSearchCandidate"]=unsupportedSearch,["searchReviewInput"]=searchReviewInput,["steps"]=sequence,
            ["scope"]="Controlled semantic responses with actual scene view actions. Native focus additionally verifies four real 90-degree body turns; controlled focus verifies the same routing/authority through view orientation without motion-service HTTP." };
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="empty-history-pending-search-turn-rejected-cancel-approved-cancel-next-turn" });
        CheckPendingAuthorizationHistoryBounds(chat,model,lamp);
    }

    private static void ExportPendingDialoguePlanner(NevaSceneVisionIntegrationModel.Request request,
        SceneVisionObservation source,string id,bool cancellationApplied,bool native)
    {
        var input=JObject.Parse(request.Input);
        int historyCount=request.ObservationHistory?.Count??0;
        Check(request.Image==source.ImageDataUrl&&(string)input["visualObservation"]?["captureId"]==source.CaptureId&&
            (bool?)input["currentRequestOnly"]==true,
            "Replay exports preserve the exact current planner image/capture and carry the entire request-local historical image list.");
        string export=Path.Combine(directory,"pending-dialogue-planner");Directory.CreateDirectory(export);
        File.WriteAllText(Path.Combine(export,id+".input.json"),request.Input);
        File.WriteAllText(Path.Combine(export,id+".image-data-url.txt"),request.Image);
        File.WriteAllText(Path.Combine(export,id+".request.json"),request.Wire);
        File.WriteAllText(Path.Combine(export,id+".history.json"),PendingReplayHistory(request).ToString());
        string manifestPath=Path.Combine(export,"manifest.json");
        var manifest=File.Exists(manifestPath)?JObject.Parse(File.ReadAllText(manifestPath)):new JObject {
            ["scope"]="Actual production planner requests and frozen Unity images after controlled preceding review outcomes; expected labels are sidecar-only, not model input or proof of real-model execution.",
            ["cases"]=new JArray() };
        ((JArray)manifest["cases"]).Add(new JObject { ["id"]=id,["mode"]="current-only",["requestOrigin"]="actual-unmodified-planner-request",
            ["input"]=id+".input.json",["image"]=id+".image-data-url.txt",
            ["request"]=id+".request.json",["observationHistory"]=id+".history.json",["historyCount"]=historyCount,
            ["fixtureFocus"]=native?"native":"controlled",["sourceCaptureId"]=source.CaptureId,
            ["expected"]=new JObject { ["intent"]="turn-in-place",["yaw"]=-90,["pitch"]=0,["cancellationApplied"]=cancellationApplied } });
        File.WriteAllText(manifestPath,manifest.ToString());
    }

    private static JArray PendingReplayHistory(NevaSceneVisionIntegrationModel.Request request)
        =>new JArray((request.ObservationHistory??Array.Empty<LLM.RoomObservationImage>()).Select(frame=>new JObject {
            ["captureId"]=frame.CaptureId,["metadataJson"]=frame.MetadataJson,["imageDataUrl"]=frame.ImageDataUrl }));

    private static void ExportDerivedApprovedPendingTurn(NevaSceneVisionIntegrationModel model,
        NevaSceneVisionIntegrationModel.Request request,SceneVisionObservation source)
    {
        const string id="approved-clarify-turn-first",latest="你先转身啊。";
        var input=JObject.Parse(request.Input);string originalLatest=(string)input["latestUserText"];
        Check((bool?)input["quietSpatialGoalStep"]==true&&(bool?)input["decisionContractFacts"]?["deviceGoalAlreadyReviewed"]==true&&
            (string)input["spatialGoal"]?["desiredState"]=="off"&&request.Image==source.ImageDataUrl,
            "Derived turn-first probe begins with the real post-approval off planner and its exact fresh picture.");
        input["latestUserText"]=latest;
        var goal=(JObject)input["spatialGoal"];goal["latestUserText"]=latest;
        if(goal["userUpdates"] is JArray updates)
            foreach(var update in updates.OfType<JObject>())
                if((string)update["text"]==originalLatest)
                {
                    update["text"]=latest;
                    update["reason"]="Derived semantic fixture: original off operation is approved and the latest user specifically asks to turn first.";
                }
        if(input["recentDialogue"] is JArray dialogue)
            foreach(var entry in dialogue.OfType<JObject>())
                if((string)entry["role"]=="user"&&(string)entry["quotedText"]==originalLatest)entry["quotedText"]=latest;
        var serializer=Get(model,"serializer");
        string derivedWire=(string)Call(serializer,"BuildRoomTaskRequestJsonWithObservationHistory",input.ToString(),request.Image,request.ObservationHistory,false);
        string export=Path.Combine(directory,"pending-dialogue-planner");Directory.CreateDirectory(export);
        File.WriteAllText(Path.Combine(export,id+".input.json"),input.ToString());
        File.WriteAllText(Path.Combine(export,id+".image-data-url.txt"),request.Image);
        File.WriteAllText(Path.Combine(export,id+".request.json"),derivedWire);
        File.WriteAllText(Path.Combine(export,id+".history.json"),PendingReplayHistory(request).ToString());
        File.WriteAllText(Path.Combine(export,id+".original-input.json"),request.Input);
        File.WriteAllText(Path.Combine(export,id+".original-request.json"),request.Wire);
        string manifestPath=Path.Combine(export,"manifest.json");
        var manifest=File.Exists(manifestPath)?JObject.Parse(File.ReadAllText(manifestPath)):new JObject {
            ["scope"]="Actual planner requests plus a separately labeled derived approved-goal turn-first probe. Expected labels never enter the model request.",["cases"]=new JArray() };
        ((JArray)manifest["cases"]).Add(new JObject { ["id"]=id,["mode"]="reviewed-pending-turn",
            ["requestOrigin"]="derived-production-payload-from-real-approved-goal-and-capture",
            ["input"]=id+".input.json",["image"]=id+".image-data-url.txt",["request"]=id+".request.json",
            ["observationHistory"]=id+".history.json",["historyCount"]=request.ObservationHistory?.Count??0,
            ["originalInput"]=id+".original-input.json",["originalRequest"]=id+".original-request.json",
            ["modifiedFields"]=new JArray("latestUserText","spatialGoal.latestUserText","matching spatialGoal.userUpdates text/reason","matching recentDialogue user quotedText"),
            ["fixtureFocus"]=Array.IndexOf(Environment.GetCommandLineArgs(),"-nevaSpatialGoalRunnerNative")>=0?"native":"controlled",
            ["sourceCaptureId"]=source.CaptureId,["expected"]=new JObject { ["intent"]="turn-in-place",["anyNonzeroBodyTurn"]=true,["pitch"]=0 },
            ["scope"]="Derived semantic probe, not an untouched conversation or an observed real-model result. Preserve originalRequest/off/real entity binding/current RGB; replace only the latest identity-answer semantics with turn first. A nonzero legal body turn verifies action selection, not search completion or a unique correct angle." });
        File.WriteAllText(manifestPath,manifest.ToString());
    }

    private static void CheckPendingAuthorizationHistoryBounds(ChatSample chat,NevaSceneVisionIntegrationModel model,SceneObjectLightSwitch lamp)
    {
        const string original="请把那盏灯关掉。";
        int requests=model.Decisions.Count+model.DeviceGoalReviews.Count+model.Matches.Count+model.Speech.Count;
        var state=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
        Call(chat,"ClearPendingDeviceClarification","pending-history-boundary-fixture");Call(chat,"RecordAcceptedMotionUserTurn",original);
        var decision=new RoomTaskProtocol.Decision {intent="none",origin="user",evidence=original,
            deviceGoal=new RoomTaskProtocol.DeviceGoalProposal {operation="set",requestQuote=original,targetDescription="那盏灯",desiredState="off"}};
        var rejected=new RoomTaskProtocol.DeviceGoalReview {verdict="uncertain",requestAssessment="target-unclear",reason="Controlled missing target distinction",relation="new",referentKind="unbound",referentSourceId="none"};
        Call(chat,"RememberPendingDeviceClarification",decision,original,rejected,(int)Get(chat,"m_FormalResponseGeneration"));
        var pending=Get(chat,"m_PendingDeviceClarification");
        Check(pending!=null,"Pure bounded-history fixture owns a quoted draft without creating a goal.");
        for(int i=0;i<7;i++)
        {
            string latest=i==0?"取消关灯，不要再关了。":"先观察一下，编号"+i+"。";
            Call(chat,"RecordAcceptedMotionUserTurn",latest);Call(chat,"RememberPendingDeviceUserUpdate",pending,latest);
            var context=(JObject)Call(chat,"BuildPendingDeviceClarificationContext");
            Check(((JArray)context["actualUserUpdates"]).Count==Math.Min(i+1,6)&&
                (bool?)context["authorizationHistoryComplete"]==(i<6),
                "The first discarded user update marks authorization history incomplete instead of pretending the retained six are the entire history.");
        }
        var overflow=(JObject)Call(chat,"BuildPendingDeviceClarificationContext");
        Check(!((JArray)overflow["actualUserUpdates"]).OfType<JObject>().Any(item=>((string)item["text"]).Contains("取消关灯"))&&
            (bool?)overflow["authorizationHistoryComplete"]==false,"Discarding an earlier actual cancellation leaves an explicit incomplete-history marker.");
        Call(chat,"ClearPendingDeviceClarification","pending-history-long-update-fixture");Call(chat,"RecordAcceptedMotionUserTurn",original);
        Call(chat,"RememberPendingDeviceClarification",decision,original,rejected,(int)Get(chat,"m_FormalResponseGeneration"));
        pending=Get(chat,"m_PendingDeviceClarification");string longUpdate=new string('观',601)+"，不要关灯。";
        Call(chat,"RecordAcceptedMotionUserTurn",longUpdate);Call(chat,"RememberPendingDeviceUserUpdate",pending,longUpdate);
        var truncated=(JObject)Call(chat,"BuildPendingDeviceClarificationContext");
        Check((bool?)truncated["authorizationHistoryComplete"]==false&&(bool?)truncated["actualUserUpdates"]?[0]?["textTruncated"]==true&&
            (int?)truncated["actualUserUpdates"]?[0]?["fullTextLength"]==longUpdate.Length&&
            ((string)truncated["actualUserUpdates"]?[0]?["text"]).Length<=600,
            "A truncated user turn immediately marks incomplete authorization history, including when a negation falls beyond the retained prefix.");
        Check(model.Decisions.Count+model.DeviceGoalReviews.Count+model.Matches.Count+model.Speech.Count==requests&&
            Call(chat,"GetActiveSpatialGoalId")==null&&JToken.DeepEquals(state,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
            "Bounded-history checks issue no planner/review/action request and never authorize or write the lamp.");
        LastReport["pendingAuthorizationHistoryBounds"]=new JObject { ["overflow"]=overflow,["truncated"]=truncated };
        Call(chat,"ClearPendingDeviceClarification","pending-history-boundary-fixture-finished");
    }

    private static IEnumerator ExerciseNewlyApprovedLatestTurn(ChatSample chat,ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model,NevaSceneVisionCapture capture,SceneObjectLightSwitch lamp)
    {
        const string original="请把那盏小灯关掉。",latest="你先转身啊。";
        Call(chat,"CancelSpatialGoalRunner","reviewed-latest-turn-new-scope");Call(chat,"CancelSpatialGoal","reviewed-latest-turn-new-scope");
        Call(chat,"CancelSceneAction","reviewed-latest-turn-new-scope");Call(chat,"ClearPendingDeviceClarification","reviewed-latest-turn-new-scope");
        yield return Wait(()=>!(Get(chat,"m_SceneAttention") is NevaSceneAttention attention)||!attention.IsBusy,5,"Fresh reviewed-goal fixture releases prior view ownership");
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags|BindingFlags.Static).Invoke(null,new object[]{chat});
        Set(chat,"m_EnableFirstReplyRouting",false);Call(chat,"RecordAcceptedMotionUserTurn",original);
        var draft=new RoomTaskProtocol.Decision {intent="none",origin="user",evidence=original,
            deviceGoal=new RoomTaskProtocol.DeviceGoalProposal {operation="set",requestQuote=original,targetDescription="那盏小灯",desiredState="off"}};
        Call(chat,"RememberPendingDeviceClarification",draft,original,new RoomTaskProtocol.DeviceGoalReview {
            verdict="uncertain",requestAssessment="target-unclear",reason="Controlled earlier missing target distinction",relation="new",referentKind="unbound",referentSourceId="none"},
            (int)Get(chat,"m_FormalResponseGeneration"));
        string pendingId=(string)((JObject)Call(chat,"BuildPendingDeviceClarificationContext"))["pendingId"];
        Check(!string.IsNullOrEmpty(pendingId),"A controlled earlier unapproved off request exists before the real latest-turn review route.");
        Call(chat,"RecordAcceptedMotionUserTurn",latest);Call(chat,"ResetWorkReview",true);
        SceneVisionObservation beforeApproval=null;yield return Capture(chat,"reviewed-latest-turn-before-approval",value=>beforeApproval=value);
        int reviews=model.DeviceGoalReviews.Count,rooms=model.Decisions.Count,matches=model.Matches.Count;
        var initialState=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));Quaternion body=bridge.avatar.transform.rotation;
        int operation=bridge.Controller.OperationRevision;
        Call(chat,"StartStreaming",latest,false,null,false,false,false,null,false,beforeApproval,true);Set(chat,"m_AgentRoundInFlight",true);
        yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,8,"Actual latest turn request receives the pending combined review");
        model.DeviceGoalReviews.Last().Complete(true,new JObject { ["verdict"]="approved",["relation"]="clarify",["pendingClarificationId"]=pendingId,
            ["referentKind"]="unbound",["referentSourceId"]="none",["reason"]="Controlled combined authorization preserves original off objective and latest instruction to turn first." }.ToString(),"controlled-approved-goal-latest-turn");
        yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Freshly registered goal captures a new image and plans the original objective with the latest turn instruction");
        var planner=model.Decisions.Last();var input=JObject.Parse(planner.Input);var goal=Goal(chat);var current=capture.LatestObservation;
        Check((string)input["currentUserText"]==original&&(string)input["latestUserText"]==latest&&
            (bool?)input["quietSpatialGoalStep"]==true&&(bool?)input["decisionContractFacts"]?["deviceGoalAlreadyReviewed"]==true&&
            (bool?)goal["executionCurrent"]==true&&(string)goal["originalRequest"]==original&&(string)goal["latestUserText"]==latest&&
            (string)goal["desiredState"]=="off"&&current.CaptureId!=beforeApproval.CaptureId&&
            (string)input["visualObservation"]?["captureId"]==current.CaptureId,
            "The original off objective and newest human turn instruction are distinct checked sources on a newly registered current goal.");
        bool native=Array.IndexOf(Environment.GetCommandLineArgs(),"-nevaSpatialGoalRunnerNative")>=0;
        string intent=native?"turn-in-place":"orient-view";
        var decision=new JObject { ["intent"]=intent,["origin"]="user",["evidence"]=latest,
            ["viewChange"]=new JObject { ["captureId"]=current.CaptureId,["yaw"]=native?180:-30,["pitch"]=0 } };
        Check(!RoomTaskProtocol.TryDecision(decision.ToString(),original,false,out _)&&
            RoomTaskProtocol.TryReviewedGoalStepDecision(decision.ToString(),original,latest,out var parsed,out string parseReason)&&
            parsed.evidence==latest&&parsed.deviceGoal==null,
            "The reviewed-goal parser accepts actual latest-user evidence that the original-only parser correctly cannot cite.");
        planner.Complete(true,decision.ToString(),"controlled-fresh-goal-step-cites-latest-turn");
        yield return Wait(()=>Get(chat,"m_SceneActionFact") is object action&&
            (string)Get(action,"intent")==intent&&(string)Get(action,"phase")=="observed"&&
            (string)Get(action,"captureId")==current.CaptureId,40,"Actual newly approved goal step with latest-user evidence changes the view");
        var actual=JObject.Parse(JsonUtility.ToJson(Get(chat,"m_SceneActionFact")));goal=Goal(chat);
        Check(((double?)actual["actualViewChangeDegrees"]??0)>10&&(string)actual["resultCaptureId"]!=current.CaptureId&&
            (string)goal["originalRequest"]==original&&(string)goal["desiredState"]=="off"&&(bool?)goal["completionEvidenceValid"]!=true&&
            model.Matches.Count==matches&&JToken.DeepEquals(initialState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState())))&&
            (native?(bridge.Controller.OperationRevision>operation&&Quaternion.Angle(body,bridge.avatar.transform.rotation)>100):
                Quaternion.Angle(body,bridge.avatar.transform.rotation)<.1f),
            "Latest-user evidence dispatches the real turn/view without changing the original goal, granting device success, matching or writing the lamp.");
        LastReport["freshApprovedGoalLatestTurnEvidence"]=new JObject { ["plannerInput"]=input,["decision"]=decision,["actualAction"]=actual,
            ["goalAfterStep"]=goal,["scope"]=native?"Actual 180-degree body turn for latest human evidence on a newly approved off goal.":
                "Controlled mode verifies the same latest-evidence dispatch path through actual head orientation without a motion-service request; it does not claim a body turn satisfies the semantic user request." };
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="fresh-approved-pending-goal-step-accepts-latest-human-turn-evidence" });
        Call(chat,"CancelSpatialGoalRunner","reviewed-latest-turn-fixture-finished");
        Call(chat,"CancelSpatialGoal","reviewed-latest-turn-fixture-finished");
    }

    private static IEnumerator ExerciseRoomClarificationContinuation(ChatSample chat,ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model,NevaSceneVisionCapture capture,SceneObjectLightSwitch lamp,
        SceneObjectLightSwitch reference,string verifiedOffGoal)
    {
        const string answer="对，就是刚才那盏。";
        Call(chat,"CancelSpatialGoalRunner","referent-room-case-next");Call(chat,"CancelSpatialGoal","referent-room-case-next");
        Call(chat,"CancelSceneAction","referent-room-case-next");
        yield return Wait(()=>!(Get(chat,"m_SceneAttention") is NevaSceneAttention attention)||!attention.IsBusy,5,"Previous actual head ownership releases");
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags|BindingFlags.Static).Invoke(null,new object[]{chat});
        Set(chat,"m_EnableFirstReplyRouting",false);
        Check(!lamp.ReadState().on&&reference.ReadState().on,"Full Room continuation starts from the actually switched-off target without fixture power mutation.");
        Call(chat,"RecordAcceptedMotionUserTurn",OnRequest);
        Check(Register(chat,OnRequest,"on",verifiedOffGoal,lamp.EntityId),"A separately approved on goal inherits the actual prior off identity.");
        string main=(string)Goal(chat)["goalId"];
        Call(chat,"SetSpatialGoalAwaitingClarification","controlled-requested-identity-clarification");
        int previousTurn=(int)Get(Get(chat,"m_ActionConstraints"),"<CurrentUserTurn>k__BackingField");
        Call(chat,"RecordAcceptedMotionUserTurn",answer);Call(chat,"ResetWorkReview",true);
        Check((int)Get(Get(chat,"m_ActionConstraints"),"<CurrentUserTurn>k__BackingField")>previousTurn&&
            (string)Goal(chat)["goalId"]==main&&(string)Goal(chat)["status"]=="waiting_clarification"&&
            (bool?)Goal(chat)["executionCurrent"]==false,"A real new user turn/work reset preserves the waiting logical goal while fencing its old execution.");
        SceneVisionObservation source=null;yield return Capture(chat,"referent-room-clarification-source",value=>source=value);
        Check(capture.TryFindObservedObject(source,lamp.EntityId,out var seen,out string reason)&&seen.VisibleConfirmed,
            "Clarification begins with actual current opaque target pixels: "+reason);
        int goals=model.DeviceGoalReviews.Count,rooms=model.Decisions.Count,matches=model.Matches.Count,speech=model.Speech.Count,probes=model.Probes.Count;
        int operation=bridge.Controller.OperationRevision;Vector3 root=bridge.avatar.transform.position;Quaternion body=bridge.avatar.transform.rotation;
        Set(chat,"m_AgentCurrentRoundIsTick",false);
        Call(chat,"StartStreaming",answer,false,null,false,false,false,null,false,source,true);
        Set(chat,"m_AgentRoundInFlight",true);
        int generation=(int)Get(chat,"m_FormalResponseGeneration");
        yield return Wait(()=>model.DeviceGoalReviews.Count==goals+1,8,"Actual RoomTask independently reviews the latest clarification first");
        var review=model.DeviceGoalReviews.Last();var reviewInput=JObject.Parse(review.Input);
        var decodedReviewSchema=JObject.Parse(review.Wire)["response_format"]?["schema"];
        Check((string)reviewInput["mode"]=="goal-user-update"&&(string)reviewInput["currentUserText"]==answer&&
            (string)reviewInput["latestUserText"]==answer&&(string)reviewInput["existingGoal"]?["goalId"]==main&&
            (string)reviewInput["existingGoal"]?["originalRequest"]==OnRequest&&
            ((JArray)reviewInput["verifiedDeviceHistory"]).OfType<JObject>().Any(item=>(string)item["goalId"]==verifiedOffGoal&&(string)item["entityId"]==lamp.EntityId),
            "The actual update-review input separates the latest answer, retained original on request, same logical goal and real prior off receipt.");
        Check(JToken.DeepEquals(decodedReviewSchema,JObject.Parse(RoomTaskProtocol.DeviceGoalUserUpdateReviewSchema))&&
            ((JArray)decodedReviewSchema?["required"]).Values<string>().OrderBy(value=>value).SequenceEqual(
                new[]{"verdict","reason","relation","responseMode","referentGoalId","referentEntityId"}.OrderBy(value=>value)),
            "Decoded actual provider wire requires the complete strict user-update review, including the exact goal/entity referent pair.");
        Check(model.Decisions.Count==rooms&&model.Matches.Count==matches&&model.Speech.Count==speech&&model.Probes.Count==probes&&
            Get(chat,"m_SpatialGoalRunner")==null&&!lamp.ReadState().on,
            "Pending clarification review authorizes no new planner, matcher, device write, intermediate speech or autonomy probe.");
        review.Complete(true,new JObject { ["verdict"]="approved",["reason"]="Controlled explicit answer confirms the already bound previously operated lamp.",
            ["relation"]="clarify",["responseMode"]="act",["referentGoalId"]=main,["referentEntityId"]=lamp.EntityId }.ToString(),"controlled-room-user-clarification-approved");
        yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Approved clarification resumes the original goal through the actual planner callback");
        var planner=model.Decisions.Last();var plannerInput=JObject.Parse(planner.Input);
        Check(planner.Kind=="room"&&(string)plannerInput["currentUserText"]==OnRequest&&(string)plannerInput["latestUserText"]==answer&&
            (bool?)plannerInput["quietSpatialGoalStep"]==true&&(bool?)plannerInput["autonomous"]==false&&
            (string)plannerInput["spatialGoal"]?["goalId"]==main&&(string)plannerInput["spatialGoal"]?["boundEntityId"]==lamp.EntityId&&
            (bool?)plannerInput["spatialGoal"]?["executionCurrent"]==true&&planner.Image==source.ImageDataUrl&&
            (string)plannerInput["visualObservation"]?["captureId"]==source.CaptureId,
            "Actual continuation planning keeps the original on authorization, latest answer and same logical goal on a current image; it is a quiet user step.");
        Check(model.DeviceGoalReviews.Count==goals+1&&model.Matches.Count==matches&&model.Speech.Count==speech&&
            (int)Get(chat,"m_FormalResponseGeneration")==generation,"Approval neither re-proposes/re-reviews the original device goal nor emits a step narration.");
        var decision=new JObject { ["intent"]="set-object-state",["origin"]="user",["evidence"]=OnRequest,["objectState"]="on",
            ["entityTarget"]=new JObject { ["captureId"]=source.CaptureId,["entityId"]=lamp.EntityId } };
        Check(RoomTaskProtocol.TryDecision(decision.ToString(),OnRequest,false,out var parsed)&&parsed.deviceGoal==null,
            "The continuation uses original-request evidence and passes the production parser without a replacement device-goal proposal.");
        planner.Complete(true,decision.ToString(),"controlled-room-original-on-step");
        yield return Wait(()=>model.Matches.Count==matches+1,8,"Actual RoomTask dispatch reaches the normal current candidate matcher");
        var matching=model.Matches.Last();var matchInput=JObject.Parse(matching.Input);
        Check((string)matchInput["originalGoal"]==OnRequest&&(string)matchInput["latestUserRequest"]==answer&&
            (string)matchInput["spatialGoal"]?["goalId"]==main&&(string)matchInput["spatialGoal"]?["boundEntityId"]==lamp.EntityId&&
            !string.IsNullOrEmpty(matching.Image)&&model.Speech.Count==speech&&Get(chat,"m_SpatialGoalRunner")!=null,
            "The actual matcher receives current image plus the on objective, latest clarification and bound target before any write.");
        var offered=chat.LastSceneObjectCandidateView.Candidates.Single(value=>value.entityId==lamp.EntityId);
        matching.Complete(true,Reply("matched",offered.candidateId),"controlled-room-current-off-lamp-match");
        yield return Wait(()=>model.Speech.Count==speech+1,16,"Real same-device on write/fresh readback produces the single terminal result");
        var actual=JObject.Parse(JsonUtility.ToJson(Get(chat,"m_SceneActionFact")));var goal=Goal(chat);
        Check(lamp.ReadState().on&&lamp.ReadState().registered&&reference.ReadState().on&&(string)actual["phase"]=="performed"&&
            (string)actual["entityId"]==lamp.EntityId&&(string)actual["requestedState"]=="on"&&(bool?)actual["stateChanged"]==true&&
            (bool?)actual["semanticTargetConfirmed"]==true&&(string)actual["resultCaptureId"]!=source.CaptureId&&
            !string.IsNullOrEmpty((string)actual["resultCaptureId"])&&(string)goal["goalId"]==main&&(string)goal["status"]=="satisfied"&&
            (bool?)goal["completionEvidenceValid"]==true&&(bool?)goal["completionEvidence"]?["actual"]?["on"]==true,
            "Only a normal semantic-confirmed actual on write and a different fresh completion capture satisfy the retained same-lamp objective.");
        var terminal=model.Speech.Last();
        bool valid=SpatialGoalResultContextPolicy.TryReadContext(terminal.Context,out string request,out var facts);
        var terminalWire=JObject.Parse(terminal.Wire);
        Check(terminal.Stage==FormalResponseStage.SpatialGoalResult&&!terminal.RecordAssistantHistory&&string.IsNullOrEmpty(terminal.Input)&&
            valid&&request==OnRequest&&(string)facts?["goal"]?["goalId"]==main&&(string)facts?["goal"]?["latestUserText"]==answer&&
            (string)facts?["goal"]?["status"]=="satisfied"&&(string)facts?["latestGoalReview"]?["relation"]=="clarify"&&
            terminalWire["messages"].OfType<JObject>().Any(message=>(string)message["role"]=="system"&&
                ((string)message["content"]).Contains(SpatialGoalResultContextPolicy.OutputContract))&&
            (int?)terminalWire["max_tokens"]==256&&model.DeviceGoalReviews.Count==goals+1&&model.Decisions.Count==rooms+1&&model.Probes.Count==probes,
            "Exactly one short dedicated result receives decoded public-output contract and actual same-goal on proof; no intermediate formal/review/probe was requested.");
        int history=model.m_DataList.Count;const string result="見つけたわ。さあ、行こう。";
        terminal.SpeechCallback(new SpeechText(result,"ja",actionDependency:SpeechActionDependency.Independent));
        Check(model.m_DataList.Count==history&&((SpeechTextBuffer)Get(chat,"m_SentenceBuffer")).Length==0,
            "Even the single successful result remains private until normal speech consistency review.");
        terminal.SpeechCompleteCallback(result);
        yield return Wait(()=>model.Decisions.Count==rooms+2,8,"Actual terminal result enters the normal independent speech consistency review");
        var speechReview=model.Decisions.Last();
        Check(speechReview.Kind=="room-review"&&model.Speech.Count==speech+1,"The only added review audits the single final result, not an intermediate narration.");
        var reportReview=JObject.Parse(speechReview.Input);
        Check((string)reportReview["publicSpeech"]==result&&(string)reportReview["sceneActionFacts"]?["resultReportRequirement"]?["expectedReport"]=="on",
            "The actual semantic reviewer receives the generic candidate together with the mandatory current on result.");
        speechReview.Complete(true,new JObject { ["verdict"]="consistent",["factScope"]="none",["resultReport"]="omitted",
            ["reason"]="The greeting is not itself contradictory but omits the completed on result." }.ToString(),"controlled-room-on-result-omitted");
        string publishedOn=SpatialGoalResultContextPolicy.BuildTrustedReport(facts,"ja");
        Check(publishedOn=="この機器が今オンになっていることを確認したわ。"&&model.m_DataList.Count==history+1&&
            model.m_DataList.Last().role=="assistant"&&model.m_DataList.Last().content==publishedOn&&!model.m_DataList.Last().content.Contains(result)&&
            !(bool)Get(chat,"m_FormalResponseInFlight")&&(string)Get(chat,"m_WorkStatus")=="closed"&&Get(chat,"m_SpatialGoalRunner")==null,
            "Even a consistent generic greeting cannot replace the actual result: one localized on fallback is committed and all final ownership releases.");
        Check(bridge.avatar.transform.position==root&&bridge.avatar.transform.rotation==body&&bridge.Controller.OperationRevision==operation,
            "Referent continuation performs the authorized device operation without assigning a body transform or moving the character.");
        LastReport["actualRoomClarificationContinuation"]=new JObject { ["goalId"]=main,["reviewInput"]=reviewInput,
            ["decodedReviewSchema"]=decodedReviewSchema.DeepClone(),["plannerInput"]=plannerInput,["matchInput"]=matchInput,
            ["actualOperation"]=actual,["terminalFacts"]=facts,["decodedTerminalMaxTokens"]=terminalWire["max_tokens"],
            ["omittedCandidate"]=result,["publishedTerminalResult"]=publishedOn,
            ["intermediateSpeechRequests"]=0,["terminalSpeechRequests"]=1,["auditedAssistantHistoryEntries"]=1 };
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="actual-room-new-user-clarification-original-on-goal-single-audited-result" });
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="actual-on-proof-generic-result-replaced-by-localized-state-report" });
    }

    private static IEnumerator ExercisePendingClarificationFailureIsolation(ChatSample chat,ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model,NevaSceneVisionCapture capture,SceneObjectLightSwitch lamp)
    {
        const string request="请把那盏灯关掉。";
        const string budgetFailure="room-task-http-context-budget;attempts=0;recovery=preflight-budget";
        const string httpFailure="room-task-http-server-error;attempts=2;recovery=exhausted";
        var results=new JArray();string oldGoalId=(string)Goal(chat)["goalId"];
        var deviceState=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));
        foreach(string kind in new[]{"planner-budget","planner-http","review-format","review-unlisted-reference"})
        {
            Call(chat,"CancelSpatialGoalRunner","pending-failure-isolation");Call(chat,"CancelSceneAction","pending-failure-isolation");
            Call(chat,"ClearPendingDeviceClarification","pending-failure-isolation");
            typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags|BindingFlags.Static).Invoke(null,new object[]{chat});
            Set(chat,"m_EnableFirstReplyRouting",false);Call(chat,"RecordAcceptedMotionUserTurn",request);Call(chat,"ResetWorkReview",true);
            SceneVisionObservation source=null;yield return Capture(chat,"pending-failure-"+kind,value=>source=value);
            int rooms=model.Decisions.Count,reviews=model.DeviceGoalReviews.Count,matches=model.Matches.Count,
                searches=model.SceneSearchRequests.Count,speech=model.Speech.Count,operation=bridge.Controller.OperationRevision;
            Call(chat,"StartStreaming",request,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
            yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Failure isolation obtains its actual current planner: "+kind);
            if(kind.StartsWith("review-",StringComparison.Ordinal))
            {
                var proposal=new JObject { ["intent"]="none",["replyScope"]="ordinary",["origin"]="user",["evidence"]=request,
                    ["deviceGoal"]=new JObject { ["operation"]="set",["requestQuote"]=request,
                        ["targetDescription"]="那盏灯",["desiredState"]="off" } };
                Check(RoomTaskProtocol.TryDecision(proposal.ToString(),request,false,out _),"Negative review fixture has a valid proposal before its independent review.");
                model.Decisions.Last().Complete(true,proposal.ToString(),"controlled-pending-failure-proposal");
                yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,8,"Negative proposal reaches actual review: "+kind);
                var review=model.DeviceGoalReviews.Last();var reviewInput=JObject.Parse(review.Input);
                var response=new JObject { ["verdict"]="uncertain",["reason"]="Controlled invalid review cannot establish missing target identity." };
                if(kind=="review-unlisted-reference")
                {
                    response["requestAssessment"]="target-unclear";response["relation"]="new";
                    response["referentKind"]="operated-goal";response["referentSourceId"]="operated-goal:controlled-missing-source";
                    Check(RoomTaskProtocol.TryDeviceGoalProposalReview(response.ToString(),out var parsed)&&
                        !RoomTaskProtocol.TryValidateListedDeviceReferent(parsed,reviewInput["referentSources"] as JArray,out _),
                        "The negative reference has valid six-field syntax but is absent from this request's authoritative source list.");
                }
                else Check(!RoomTaskProtocol.TryDeviceGoalProposalReview(response.ToString(),out _),
                    "The legacy two-field uncertain response remains a strict-format rejection.");
                review.Complete(true,response.ToString(),"controlled-"+kind);
                yield return Wait(()=>model.Decisions.Count==rooms+2,8,"Invalid review receives one bounded current-only planning pass: "+kind);
                Check((string)((JObject)Call(chat,"BuildPendingDeviceClarificationContext"))["status"]=="none"&&
                    (bool?)JObject.Parse(model.Decisions.Last().Input)["currentRequestOnly"]==true&&
                    (string)JObject.Parse(model.Decisions.Last().Input)["currentUserText"]==request&&model.Speech.Count==speech,
                    "Malformed/unlisted review is not saved as pending semantic evidence and cannot emit a clarification result.");
                var late=new JObject { ["verdict"]="uncertain",["requestAssessment"]="target-unclear",["relation"]="new",
                    ["referentKind"]="unbound",["referentSourceId"]="none",["reason"]="Late valid-shaped response cannot replace the settled invalid review." };
                review.Complete(true,late.ToString(),"controlled-late-target-unclear-after-invalid-review");
            }
            string failureStatus=kind=="planner-budget"?budgetFailure:httpFailure;
            model.Decisions.Last().Complete(false,"",failureStatus);
            yield return Wait(()=>model.Speech.Count==speech+1,8,"Real planner failure retains its guarded failure answer: "+kind);
            var failure=Call(chat,"ReadRoomTaskDecisionFailure") as JObject;var goal=Goal(chat);
            Check(failure!=null&&(string)failure["kind"]==(kind=="planner-budget"?"input-budget-exceeded":"service-request-failed")&&
                (string)Get(chat,"m_RoomTaskDispatch")=="decision-unavailable"&&
                model.Speech.Last().Stage!=FormalResponseStage.SpatialGoalResult&&
                (string)((JObject)Call(chat,"BuildPendingDeviceClarificationContext"))["status"]=="none"&&
                (string)goal["goalId"]==oldGoalId&&(string)goal["status"]=="satisfied"&&(string)goal["desiredState"]=="on"&&
                Call(chat,"GetActiveSpatialGoalId")==null&&Get(chat,"m_SpatialGoalRunner")==null&&
                model.Matches.Count==matches&&model.SceneSearchRequests.Count==searches&&bridge.Controller.OperationRevision==operation&&
                JToken.DeepEquals(deviceState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
                "HTTP/budget and invalid-review fallback failures remain failures, never pending clarification, inherited old success or device authority: "+kind);
            results.Add(new JObject { ["case"]=kind,["failure"]=failure.DeepClone(),["stage"]=model.Speech.Last().Stage.ToString(),
                ["currentOnlyRecoveryPlans"]=kind.StartsWith("review-",StringComparison.Ordinal)?1:0,["pendingRetained"]=false,["oldGoalUnchanged"]=true });
        }
        LastReport["pendingClarificationFailureIsolation"]=results;
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="only-strict-current-target-ambiguity-can-use-pending-clarification-result" });
    }

    private static IEnumerator ExercisePendingProposalClarification(ChatSample chat,ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model,NevaSceneVisionCapture capture,SceneObjectLightSwitch lamp,
        SceneObjectLightSwitch reference,bool negative)
    {
        string original=negative?"不要关灯。":"请把那盏灯关掉。";
        string quote=negative?"关灯":"灯关掉";const string answer="对，左边那盏。";
        var previousGoal=Goal(chat);string previousGoalId=(string)previousGoal["goalId"];
        Check(negative||((string)previousGoal["status"]=="satisfied"&&(string)previousGoal["desiredState"]=="on"&&
            (bool?)previousGoal["completionEvidenceValid"]==true),"The positive pending case begins with a real prior satisfied on goal, retaining its completion history.");
        Call(chat,"CancelSpatialGoalRunner","pending-referent-case-next");
        if(negative)Call(chat,"CancelSpatialGoal","pending-referent-case-next");
        Call(chat,"CancelSceneAction","pending-referent-case-next");Call(chat,"ClearPendingDeviceClarification","pending-referent-case-next");
        yield return Wait(()=>!(Get(chat,"m_SceneAttention") is NevaSceneAttention attention)||!attention.IsBusy,5,"Pending case starts after previous head ownership releases");
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags|BindingFlags.Static).Invoke(null,new object[]{chat});
        Set(chat,"m_EnableFirstReplyRouting",false);
        Check(negative||lamp.ReadState().on,"The initial ambiguous off request starts from the actually reopened lamp, without a fixture power write.");
        var initialState=JObject.Parse(JsonUtility.ToJson(lamp.ReadState()));int operation=bridge.Controller.OperationRevision;
        Call(chat,"RecordAcceptedMotionUserTurn",original);Call(chat,"ResetWorkReview",true);
        SceneVisionObservation source=null;yield return Capture(chat,"pending-original-source",value=>source=value);
        Check(capture.TryFindObservedObject(source,lamp.EntityId,out var seen,out string reason)&&seen.VisibleConfirmed,
            "Unapproved initial proposal still has actual current opaque pixels: "+reason);
        int rooms=model.Decisions.Count,reviews=model.DeviceGoalReviews.Count,matches=model.Matches.Count,speech=model.Speech.Count;
        Call(chat,"StartStreaming",original,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
        yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Actual first Room planner proposes the ambiguous/unapproved device task");
        var proposal=new JObject { ["operation"]="set",["requestQuote"]=quote,["targetDescription"]="左边那盏灯",["desiredState"]="off" };
        var proposedDecision=new JObject { ["intent"]="inspect-object",["origin"]="user",["evidence"]=quote,["deviceGoal"]=proposal,
            ["visualTarget"]=new JObject { ["captureId"]=source.CaptureId,["x"]=seen.ImagePoint.x,["y"]=seen.ImagePoint.y,["label"]="当前实际图像中的左侧小灯" } };
        Check(RoomTaskProtocol.TryDecision(proposedDecision.ToString(),original,false,out _),
            "Initial proposal passes format/quote parsing; semantic permission still requires the independent complete-request review.");
        model.Decisions.Last().Complete(true,proposedDecision.ToString(),"controlled-unapproved-original-proposal");
        yield return Wait(()=>model.DeviceGoalReviews.Count==reviews+1,8,"Initial original-request proposal receives independent authorization review");
        var initialReview=model.DeviceGoalReviews.Last();var initialReviewInput=JObject.Parse(initialReview.Input);
        Check((string)initialReviewInput["mode"]=="new-proposal"&&(string)initialReviewInput["currentUserText"]==original&&
            model.Matches.Count==matches&&model.Speech.Count==speech&&Call(chat,"GetActiveSpatialGoalId")==null,
            "The unapproved interpretation and any real image/known lamp cannot authorize a matcher, goal or device action.");
        var proposalReview=new JObject { ["verdict"]=negative?"rejected":"uncertain",
            ["requestAssessment"]=negative?"not-requested":"target-unclear",
            ["reason"]=negative?"完整原话禁止关灯，off提案没有执行授权。":"完整原话要求关灯，但尚未明确两盏中的哪一盏。",
            ["relation"]="new",["referentKind"]="unbound",["referentSourceId"]="none" };
        Check(RoomTaskProtocol.TryDeviceGoalProposalReview(proposalReview.ToString(),out var proposalVerdict)&&
            RoomTaskProtocol.RetainsPendingDeviceProposal(proposalVerdict)==!negative,
            "Strict proposal semantics retain only real target ambiguity; a prohibited operation never becomes a pending draft.");
        initialReview.Complete(true,proposalReview.ToString(),"controlled-original-proposal-unapproved");
        if(negative)
        {
            yield return Wait(()=>model.Decisions.Count==rooms+2,8,"A prohibited operation returns to current-only planning without a clarification draft");
            var deniedPlanner=model.Decisions.Last();var deniedInput=JObject.Parse(deniedPlanner.Input);
            var rejectedReceipt=(JObject)Get(chat,"m_SpatialGoalLatestReview");
            Check((string)((JObject)Call(chat,"BuildPendingDeviceClarificationContext"))["status"]=="none"&&
                (bool?)rejectedReceipt["pendingRetained"]==false&&(bool?)rejectedReceipt["proposalDiscarded"]==true&&
                (string)rejectedReceipt["requestAssessment"]=="not-requested"&&(string)rejectedReceipt["verdict"]=="rejected"&&
                (bool?)deniedInput["currentRequestOnly"]==true&&(string)deniedInput["currentUserText"]==original&&
                deniedInput["spatialGoal"].Type==JTokenType.Null&&Call(chat,"GetActiveSpatialGoalId")==null&&
                Get(chat,"m_SpatialGoalRunner")==null&&model.Matches.Count==matches&&model.Speech.Count==speech&&
                model.DeviceGoalReviews.Count==reviews+1&&bridge.Controller.OperationRevision==operation&&
                JToken.DeepEquals(initialState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState())))&&reference.ReadState().on,
                "The complete prohibition discards the off proposal with no pending identity question, device authority, matcher, body operation or write.");
            var lateApproval=(JObject)proposalReview.DeepClone();lateApproval["verdict"]="approved";lateApproval["requestAssessment"]="requested";
            lateApproval["reason"]="Controlled contradictory duplicate cannot replace the already settled prohibition review.";
            initialReview.Complete(true,lateApproval.ToString(),"controlled-late-approved-after-proposal-rejection");
            yield return null;yield return null;
            Check(model.Decisions.Count==rooms+2&&model.DeviceGoalReviews.Count==reviews+1&&model.Speech.Count==speech&&
                model.Matches.Count==matches&&Call(chat,"GetActiveSpatialGoalId")==null&&Get(chat,"m_SpatialGoalRunner")==null&&
                (string)((JObject)Call(chat,"BuildPendingDeviceClarificationContext"))["status"]=="none"&&
                JToken.DeepEquals(initialState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
                "A late contradictory approval cannot turn the discarded prohibited operation into an executable or pending goal.");
            deniedPlanner.Complete(true,"{\"intent\":\"none\",\"replyScope\":\"ordinary\",\"origin\":\"none\",\"evidence\":\"\"}","controlled-prohibition-no-device-action");
            yield return Wait(()=>model.Speech.Count==speech+1,8,"The actual prohibited-operation turn receives an ordinary reply without forced clarification");
            var deniedReply=model.Speech.Last();
            Check(deniedReply.Stage!=FormalResponseStage.SpatialGoalResult&&deniedReply.Context.Contains("proposalDiscarded")&&
                model.Matches.Count==matches&&Call(chat,"GetActiveSpatialGoalId")==null&&Get(chat,"m_SpatialGoalRunner")==null&&
                bridge.Controller.OperationRevision==operation&&JToken.DeepEquals(initialState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
                "A rejected prohibition reaches current dialogue without a device clarification result or any device action.");
            LastReport["prohibitedOriginalNoPendingClarification"]=new JObject { ["originalReviewInput"]=initialReviewInput,
                ["reviewReply"]=proposalReview,["reviewReceipt"]=rejectedReceipt.DeepClone(),["currentOnlyPlannerInput"]=deniedInput,
                ["speechStage"]=deniedReply.Stage.ToString(),["deviceState"]=JObject.Parse(JsonUtility.ToJson(lamp.ReadState())),["deviceMatcherRequests"]=0 };
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="actual-prohibited-operation-never-creates-pending-or-device-authority" });
            yield break;
        }
        yield return Wait(()=>model.Speech.Count==speech+1,8,"Unregistered proposal produces one short grounded clarification request");
        var pending=(JObject)Call(chat,"BuildPendingDeviceClarificationContext");string pendingId=(string)pending["pendingId"];
        var initialTerminal=model.Speech.Last();
        bool initialContext=SpatialGoalResultContextPolicy.TryReadContext(initialTerminal.Context,out string initialRequest,out var initialFacts);
        Check(initialTerminal.Stage==FormalResponseStage.SpatialGoalResult&&Call(chat,"ReadRoomTaskDecisionFailure")==null&&
            (string)Get(chat,"m_RoomTaskDispatch")=="goal-awaiting-clarification"&&
            !string.IsNullOrEmpty(pendingId)&&(bool?)pending["registered"]==false&&(string)pending["originalUserRequest"]==original&&
            (string)pending["previousReview"]?["verdict"]=="uncertain"&&initialContext&&initialRequest==original&&
            (string)initialFacts?["goal"]?["status"]=="waiting_clarification"&&(bool?)initialFacts?["goal"]?["registered"]==false&&
            (string)initialFacts?["goal"]?["originalRequest"]==original&&model.Matches.Count==matches&&Call(chat,"GetActiveSpatialGoalId")==null&&
            initialFacts?["lastAction"]?.Type==JTokenType.Null&&
            (initialFacts?["objectContext"]?["stateResult"]==null||initialFacts["objectContext"]["stateResult"].Type==JTokenType.Null)&&
            ((string)Goal(chat)["goalId"]==previousGoalId&&(string)Goal(chat)["status"]=="satisfied")&&
            JToken.DeepEquals(initialState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
            "Pending clarification preserves the complete original request/provenance with no new goal/action/state result; a previous real satisfied on goal cannot leak as current off-task success.");
        yield return AuditShortResult(chat,model,initialTerminal,"你指的是左边那盏灯吗？");

        Call(chat,"RecordAcceptedMotionUserTurn",answer);Call(chat,"ResetWorkReview",true);
        yield return Capture(chat,"pending-latest-answer-source",value=>source=value);
        int nextRooms=model.Decisions.Count,nextReviews=model.DeviceGoalReviews.Count,nextSpeech=model.Speech.Count,probes=model.Probes.Count;
        Call(chat,"StartStreaming",answer,false,null,false,false,false,null,false,source,true);Set(chat,"m_AgentRoundInFlight",true);
        yield return Wait(()=>model.DeviceGoalReviews.Count==nextReviews+1,8,"Latest identity answer is jointly reviewed with the unapproved original request before planning");
        var combined=model.DeviceGoalReviews.Last();var combinedInput=JObject.Parse(combined.Input);
        var schema=JObject.Parse(combined.Wire)["response_format"]?["schema"];
        Check((string)combinedInput["mode"]=="pending-proposal-update"&&(string)combinedInput["pendingId"]==pendingId&&
            (string)combinedInput["originalUserRequest"]==original&&(string)combinedInput["latestUserText"]==answer&&
            (string)combinedInput["currentUserText"]==answer&&(bool?)combinedInput["registered"]==false&&
            (string)combinedInput["previousReview"]?["verdict"]=="uncertain"&&
            JToken.DeepEquals(combinedInput["proposal"],proposal)&&JToken.DeepEquals(schema,JObject.Parse(
                RoomTaskProtocol.BuildTypedDeviceGoalReviewSchema(true,combinedInput["referentSources"] as JArray)))&&
            ((JArray)schema?["required"]).Count==6,
            "Decoded combined-review wire carries both actual utterances, same unapproved draft/proposal and the separate six-field pending/reference schema.");
        Check(model.Decisions.Count==nextRooms&&model.Matches.Count==matches&&model.Speech.Count==nextSpeech&&model.Probes.Count==probes&&
            Call(chat,"GetActiveSpatialGoalId")==null&&Get(chat,"m_SpatialGoalRunner")==null&&
            JToken.DeepEquals(initialState,JObject.Parse(JsonUtility.ToJson(lamp.ReadState()))),
            "An identity answer alone cannot start a planner/matcher, register a goal, narrate a step or mutate the device while combined approval is pending.");
        var pendingSource=((JArray)combinedInput["referentSources"]).OfType<JObject>().First(item=>
            (string)item["entityId"]==lamp.EntityId&&(string)item["kind"]=="operated-goal");
        var reviewReply=new JObject { ["verdict"]="approved",["relation"]="clarify",
            ["reason"]="原话明确要求关灯，最新回答补清左侧同一目标，现在可批准该原提案。",
            ["pendingClarificationId"]=pendingId,["referentKind"]=(string)pendingSource["kind"],["referentSourceId"]=(string)pendingSource["sourceId"] };
        Check(RoomTaskProtocol.TryPendingDeviceClarificationReview(reviewReply.ToString(),out _),"Controlled combined verdict passes the actual strict pending-review parser.");
        combined.Complete(true,reviewReply.ToString(),"controlled-combined-original-and-identity-answer");
        yield return Wait(()=>model.Decisions.Count==nextRooms+1,8,"Combined approval first captures a newly authorized current view, then plans the original request");
        var planner=model.Decisions.Last();var plannerInput=JObject.Parse(planner.Input);var approvedView=capture.LatestObservation;var goal=Goal(chat);
        Check(approvedView.CaptureId!=source.CaptureId&&(string)plannerInput["visualObservation"]?["captureId"]==approvedView.CaptureId&&
            planner.Image==approvedView.ImageDataUrl&&(string)plannerInput["currentUserText"]==original&&(string)plannerInput["latestUserText"]==answer&&
            (bool?)plannerInput["quietSpatialGoalStep"]==true&&(string)goal["originalRequest"]==original&&(string)goal["latestUserText"]==answer&&
            (string)goal["goalId"]!=previousGoalId&&
            (bool?)goal["executionCurrent"]==true&&(string)goal["boundEntityId"]==lamp.EntityId&&
            (string)goal["referentSourceId"]==(string)pendingSource["sourceId"]&&
            (string)((JObject)Call(chat,"BuildPendingDeviceClarificationContext"))["status"]=="none"&&model.Matches.Count==matches&&model.Speech.Count==nextSpeech,
            "Only approved combined semantics create the original goal with its reviewed same-entity source; a new post-approval capture still supplies the first quiet planner and no device action has run.");
        Check(capture.TryFindObservedObject(approvedView,lamp.EntityId,out seen,out reason)&&seen.VisibleConfirmed,
            "Newly approved device action still needs genuine current opaque target pixels: "+reason);
        ExportDerivedApprovedPendingTurn(model,planner,approvedView);
        var deviceDecision=new JObject { ["intent"]="set-object-state",["origin"]="user",["evidence"]=original,["objectState"]="off",
            ["visualTarget"]=new JObject { ["captureId"]=approvedView.CaptureId,["x"]=seen.ImagePoint.x,["y"]=seen.ImagePoint.y,["label"]="当前实际图像中的左侧小灯" } };
        Check(RoomTaskProtocol.TryDecision(deviceDecision.ToString(),original,false,out var parsed)&&parsed.deviceGoal==null,
            "The approved original goal's next action passes the parser with original evidence and no duplicate proposal.");
        planner.Complete(true,deviceDecision.ToString(),"controlled-combined-approved-current-off-step");
        yield return Wait(()=>model.Matches.Count==matches+1,8,"Approved pending request reaches the independent actual current target matcher");
        var offered=chat.LastSceneObjectCandidateView.Candidates.Single(value=>value.entityId==lamp.EntityId);
        model.Matches.Last().Complete(true,Reply("matched",offered.candidateId),"controlled-pending-current-lamp-match");
        yield return Wait(()=>model.Speech.Count==nextSpeech+1,16,"Actual off write plus fresh receipt produces one successful result for the jointly approved original request");
        var fact=JObject.Parse(JsonUtility.ToJson(Get(chat,"m_SceneActionFact")));goal=Goal(chat);var terminal=model.Speech.Last();
        bool resultContext=SpatialGoalResultContextPolicy.TryReadContext(terminal.Context,out string resultRequest,out var resultFacts);
        Check(!lamp.ReadState().on&&reference.ReadState().on&&(string)fact["phase"]=="performed"&&(bool?)fact["stateChanged"]==true&&
            (string)fact["entityId"]==lamp.EntityId&&(bool?)fact["semanticTargetConfirmed"]==true&&!string.IsNullOrEmpty((string)fact["resultCaptureId"])&&
            (string)fact["resultCaptureId"]!=approvedView.CaptureId&&(string)goal["status"]=="satisfied"&&(bool?)goal["completionEvidenceValid"]==true&&
            (bool?)goal["completionEvidence"]?["actual"]?["on"]==false&&resultContext&&resultRequest==original&&
            (string)resultFacts?["goal"]?["latestUserText"]==answer&&model.Decisions.Count==nextRooms+1&&model.DeviceGoalReviews.Count==nextReviews+1&&
            model.Speech.Count==nextSpeech+1&&bridge.Controller.OperationRevision==operation,
            "Current-image semantic match, actual same-target off write and a distinct fresh readback satisfy the original objective; one result and no intermediate speech/re-proposal are generated.");
        int silentHistory=model.m_DataList.Count,silentReviews=model.Decisions.Count;
        var languageField=typeof(LLM).GetField("lan",Flags);var previousLanguage=languageField.GetValue(model);
        try
        {
            languageField.SetValue(model,"日语");
            // A real completed transaction returns no typed speech and no language
            // tag. The public result must still be spoken in the configured language.
            terminal.SpeechCompleteCallback("<silent/>");
        }
        finally { languageField.SetValue(model,previousLanguage); }
        string publishedOff=SpatialGoalResultContextPolicy.BuildTrustedReport(resultFacts,"ja");
        Check(publishedOff=="この機器が今オフになっていることを確認したわ。"&&model.m_DataList.Count==silentHistory+1&&
            model.m_DataList.Last().role=="assistant"&&model.m_DataList.Last().content==publishedOff&&
            model.Decisions.Count==silentReviews&&!(bool)Get(chat,"m_FormalResponseInFlight")&&
            (string)Get(chat,"m_WorkStatus")=="closed"&&model.Speech.Count==nextSpeech+1&&bridge.Controller.OperationRevision==operation,
            "A silent result over actual off proof produces one configured-language state report without another model review, action or terminal request.");
        int settledRooms=model.Decisions.Count,settledSpeech=model.Speech.Count,settledMatches=model.Matches.Count;
        string satisfiedGoalId=(string)Goal(chat)["goalId"];
        combined.Complete(true,reviewReply.ToString(),"controlled-late-duplicate-pending-approval-after-success");
        yield return null;yield return null;
        Check(model.Decisions.Count==settledRooms&&model.Speech.Count==settledSpeech&&model.Matches.Count==settledMatches&&
            (string)Goal(chat)["goalId"]==satisfiedGoalId&&(string)Goal(chat)["status"]=="satisfied"&&
            (bool?)Goal(chat)["completionEvidenceValid"]==true&&!lamp.ReadState().on&&reference.ReadState().on&&
            Get(chat,"m_SpatialGoalRunner")==null,
            "Duplicate approval after the actual audited off result cannot clear success, issue a second result or restart planner/matcher/device execution.");
        LastReport["approvedOriginalPendingClarification"]=new JObject { ["combinedReviewInput"]=combinedInput,["decodedReviewSchema"]=schema.DeepClone(),
            ["reviewReply"]=reviewReply,["plannerInput"]=plannerInput,["actualOperation"]=fact,["terminalFacts"]=resultFacts,
            ["silentCandidate"]="<silent/>",["publishedTerminalResult"]=publishedOff,
            ["intermediateSpeechRequests"]=0,["terminalSpeechRequests"]=1 };
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="actual-pending-ambiguous-original-identity-answer-independent-approval-real-off-result" });
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"]="actual-off-proof-silent-result-replaced-by-configured-language-report" });
    }

    private static IEnumerator AuditShortResult(ChatSample chat,NevaSceneVisionIntegrationModel model,
        NevaSceneVisionIntegrationModel.Request terminal,string text)
    {
        Check(terminal.Stage==FormalResponseStage.SpatialGoalResult&&!terminal.RecordAssistantHistory&&string.IsNullOrEmpty(terminal.Input)&&
            text.Length<=SpatialGoalResultContextPolicy.MaximumSpeechCharacters,"Each pending clarification/result uses one bounded independent short-result stage.");
        int history=model.m_DataList.Count,rooms=model.Decisions.Count;
        terminal.SpeechCallback(new SpeechText(text,"zh",actionDependency:SpeechActionDependency.Independent));
        Check(model.m_DataList.Count==history&&((SpeechTextBuffer)Get(chat,"m_SentenceBuffer")).Length==0,"Pending/result candidate remains private until its normal independent consistency audit.");
        terminal.SpeechCompleteCallback(text);
        yield return Wait(()=>model.Decisions.Count==rooms+1,8,"Normal pending/result speech consistency review");
        Check(model.Decisions.Last().Kind=="room-review","The added request audits the result; it cannot schedule another device step.");
        Check(SpatialGoalResultContextPolicy.TryReadContext(terminal.Context,out _,out var terminalFacts),"Result review is grounded in its exact terminal facts.");
        model.Decisions.Last().Complete(true,new JObject { ["verdict"]="consistent",["factScope"]="scene-action",
            ["resultReport"]=SpatialGoalResultContextPolicy.RequiredReport(terminalFacts),
            ["reason"]="Controlled short statement accurately reflects the supplied actual approval/registered-state/execution facts." }.ToString(),"controlled-pending-result-consistency");
        Check(model.m_DataList.Count==history+1&&model.m_DataList.Last().role=="assistant"&&model.m_DataList.Last().content.Contains(text)&&
            !(bool)Get(chat,"m_FormalResponseInFlight"),"Normal audit publishes one exact result and releases formal ownership before another real user turn.");
    }

    private static IEnumerator StartMatch(ChatSample chat,NevaSceneVisionIntegrationModel model,SceneVisionObservation source,SceneObservedObject initial,
        RoomTaskProtocol.Decision decision,Action<MatchRun> complete,bool expectRequest=true)
    {
        var value=new MatchRun {fact=Activator.CreateInstance(typeof(ChatSample).GetNestedType("SceneActionFact",Flags),true)};
        Set(value.fact,"actionId","goal-referent-match-"+Guid.NewGuid().ToString("N"));Set(value.fact,"intent",decision.intent);
        int before=model.Matches.Count;
        chat.StartCoroutine((IEnumerator)Call(chat,"MatchSceneObjectTarget",decision,source,initial,value.fact,new Func<bool>(()=>true),
            new Action<SceneObservedObject,string>((selected,failure)=>{value.selected=selected;value.failure=failure;value.done=true;})));
        yield return Wait(()=>value.done||model.Matches.Count>before,5,"Production goal-referent matcher");
        if(expectRequest)
        {
            Check(!value.done&&model.Matches.Count==before+1,"One source-linked candidate request is pending.");value.request=model.Matches.Last();
            Check(!string.IsNullOrEmpty(value.request.Image)&&!string.IsNullOrEmpty(value.request.Wire),"The matcher still receives the actual current candidate image and serialized request.");
        }
        complete(value);
    }
    private static bool Register(ChatSample chat,string text,string state,string referentGoal="",string referentEntity="")
        => RegisterReviewed(chat,text,state,new RoomTaskProtocol.DeviceGoalReview {verdict="approved",requestAssessment="requested",reason="Controlled independent user authorization",
            relation="new",referentGoalId=referentGoal,referentEntityId=referentEntity});
    private static bool RegisterTyped(ChatSample chat,string text,string state,JObject source,string verdict)
        => RegisterReviewed(chat,text,state,new RoomTaskProtocol.DeviceGoalReview {verdict=verdict,requestAssessment="requested",
            reason="Controlled independent authorization and explicit engine-listed source selection",relation="new",
            referentKind=source==null?"unbound":(string)source["kind"],referentSourceId=source==null?"none":(string)source["sourceId"]});
    private static bool RegisterReviewed(ChatSample chat,string text,string state,RoomTaskProtocol.DeviceGoalReview review)
    {
        var decision=new RoomTaskProtocol.Decision {intent="none",origin="user",evidence=text,
            deviceGoal=new RoomTaskProtocol.DeviceGoalProposal {operation="set",requestQuote=text,targetDescription="左边这盏灯",desiredState=state}};
        var args=new object[]{decision,text,review,null};bool registered=(bool)Call(chat,"TryRegisterReviewedSpatialGoal",args);
        if(!(LastReport["registrationAttempts"] is JArray attempts))LastReport["registrationAttempts"]=attempts=new JArray();
        attempts.Add(new JObject { ["request"]=text,["actualCurrentUserText"]=(string)Get(Get(chat,"m_ActionConstraints"),"currentText"),
            ["registered"]=registered,["reason"]=args[3]?.ToString(),["sceneEyesOpen"]=chat.SceneEyesOpen,
            ["agentRunning"]=(bool)Get(chat,"m_AgentRunning"),["goalStatus"]=Goal(chat)["status"] });
        return registered;
    }

    private static void CheckTypedReferentBoundaries(ChatSample chat,ArdyRoomDialogueBridge bridge,JObject source,SceneObjectLightSwitch lamp)
    {
        var review=new RoomTaskProtocol.DeviceGoalReview {verdict="approved",reason="Controlled same observed target",relation="new",
            referentKind=(string)source["kind"],referentSourceId=(string)source["sourceId"]};
        var resolved=new object[]{review,null,null,null};
        Check((bool)Call(chat,"TryResolveReviewedSpatialReferent",resolved)&&(string)resolved[1]==lamp.EntityId&&
            ReferenceEquals(resolved[2],lamp.transform),"The typed source resolves only through the current authoritative observation record.");
        var sourceCopy=(JObject)source.DeepClone();sourceCopy["entityId"]="forged-entity-in-prompt-copy";
        resolved=new object[]{review,null,null,null};
        Check(RoomTaskProtocol.TryValidateListedDeviceReferent(review,new JArray(sourceCopy),out _)&&
            (bool)Call(chat,"TryResolveReviewedSpatialReferent",resolved)&&(string)resolved[1]==lamp.EntityId,
            "Modifying a returned source's entity metadata cannot change the engine-owned referent.");
        Check(!RoomTaskProtocol.TryValidateListedDeviceReferent(review,new JArray(),out _),
            "A current authoritative source still cannot be borrowed by a request which did not offer it.");
        var forged=new RoomTaskProtocol.DeviceGoalReview {verdict="approved",relation="new",referentKind="observed-device",referentSourceId="observed-device:invented-action"};
        Check(!(bool)Call(chat,"TryResolveReviewedSpatialReferent",new object[]{forged,null,null,null}),
            "An arbitrary well-shaped source ID cannot bind a lamp.");
        var originalEnvironment=bridge.environmentRoot;var otherEnvironment=new GameObject("Isolated stale-source environment");
        try
        {
            bridge.environmentRoot=otherEnvironment.transform;
            Check(((JArray)Call(chat,"BuildSpatialGoalReferentSources")).Count==0&&
                !(bool)Call(chat,"TryResolveReviewedSpatialReferent",new object[]{review,null,null,null}),
                "Changing the active environment invalidates the old observed source even when its device still exists.");
        }
        finally {bridge.environmentRoot=originalEnvironment;UnityEngine.Object.DestroyImmediate(otherEnvironment);}
        Check(lamp.ReadState().on&&Call(chat,"GetActiveSpatialGoalId")==null,
            "Source validation and environment rejection do not create a goal or write the observed device.");
    }

    private static void ExportTypedReviewProbe(ChatSample chat,NevaSceneVisionIntegrationModel model,string id,
        string text,string state,JObject expectedSource,string verdict)
    {
        // These are explicit controlled semantic probes using real receipt data,
        // not a claim that the room planner naturally emitted these four cases.
        var input=new JObject {
            ["mode"]="new-proposal",["currentUserText"]=text,
            ["proposal"]=new JObject { ["operation"]="set",["requestQuote"]=text,["targetDescription"]=text,["desiredState"]=state },
            ["existingGoal"]=(JObject)Call(chat,"BuildSpatialGoalContext"),
            ["recentDialogue"]=JArray.Parse((string)Call(chat,"RecentRoomDialogue")),
            ["verifiedDeviceHistory"]=(JArray)Call(chat,"BuildVerifiedDeviceReferentHistory"),
            ["verifiedObservedDeviceHistory"]=(JArray)Call(chat,"BuildVerifiedObservedDeviceReferentHistory"),
            ["referentSources"]=(JArray)Call(chat,"BuildSpatialGoalReferentSources"),
            ["scope"]="Independently compare the complete current user request and proposed device task. Historical sources constrain identity only; no action is authorized by this proposal."
        };
        model.PostRoomDeviceGoalReviewMessage(input.ToString(),(ok,output,status)=>{});
        var request=model.DeviceGoalReviews.Last();
        var wire=JObject.Parse(request.Wire);
        Check(JToken.DeepEquals(wire["response_format"]?["schema"],JObject.Parse(
                ((JArray)input["referentSources"]).Count==0 ? RoomTaskProtocol.BuildUnboundDeviceGoalReviewSchema() :
                    RoomTaskProtocol.BuildTypedDeviceGoalReviewSchema(false,input["referentSources"] as JArray)))&&
            string.IsNullOrEmpty(request.Image),"Typed semantic probe uses the actual production request-local schema and text-only reviewer payload.");
        string path=Path.Combine(directory,"typed-referent-probes");Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path,id+".input.json"),request.Input);
        File.WriteAllText(Path.Combine(path,id+".request.json"),request.Wire);
        if(!(LastReport["typedReferentProbes"] is JObject manifest))
            LastReport["typedReferentProbes"]=manifest=new JObject {
                ["scope"]="Controlled four-case semantic replay inputs using actual isolated observation/operation receipts and the production provider payload. Expected results are sidecar-only. This export performs no HTTP or device action.",
                ["relativeDirectory"]="scene-goal-referent/typed-referent-probes",["cases"]=new JArray() };
        ((JArray)manifest["cases"]).Add(new JObject { ["id"]=id,["input"]=id+".input.json",["request"]=id+".request.json",
            ["expected"]=new JObject { ["verdict"]=verdict,["relation"]="new",
                ["referentKind"]=expectedSource==null?"unbound":(string)expectedSource["kind"],
                ["referentSourceId"]=expectedSource==null?"none":(string)expectedSource["sourceId"] } });
        File.WriteAllText(Path.Combine(path,"manifest.json"),manifest.ToString());
    }
    private static RoomTaskProtocol.Decision Inspect(SceneVisionObservation source,SceneObservedObject seen,string text)=>new RoomTaskProtocol.Decision {
        intent="inspect-object",origin="user",evidence=text,entityTarget=new RoomTaskProtocol.EntityTarget{captureId=source.CaptureId,entityId=seen.EntityId} };
    private static string Reply(string verdict,string candidate)=>new JObject { ["verdict"]=verdict, ["candidateRole"]="target",["candidateId"]=candidate,["reason"]="Controlled candidate semantic reply; no execution claim." }.ToString();
    private static JObject Goal(ChatSample chat)=>(JObject)Call(chat,"BuildSpatialGoalContext");
    private static void AddCase(string name,JObject input)=>((JArray)LastReport["cases"]).Add(new JObject{["name"]=name,["matchInput"]=input.DeepClone()});
    private static void ExportActualModelProbe(NevaSceneVisionIntegrationModel.Request request,SceneObjectCandidateView view,
        SceneVisionObservation source,string expectedEntity,string goalId)
    {
        var expected=view.Candidates.Single(value=>value.entityId==expectedEntity);
        string path=Path.Combine(directory,"live-probe");Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path,"on-previously-closed-lamp.request.json"),request.Wire);
        File.WriteAllText(Path.Combine(path,"on-previously-closed-lamp.input.json"),request.Input);
        File.WriteAllText(Path.Combine(path,"candidates.metadata.json"),view.MetadataJson);
        File.WriteAllBytes(Path.Combine(path,"candidates.png"),Convert.FromBase64String(request.Image.Substring(request.Image.IndexOf(',')+1)));
        File.WriteAllText(Path.Combine(path,"source.metadata.json"),source.MetadataJson);
        File.WriteAllBytes(Path.Combine(path,"source.jpg"),Convert.FromBase64String(source.ImageDataUrl.Substring(source.ImageDataUrl.IndexOf(',')+1)));
        var manifest=new JObject {
            ["scope"]="Untouched actual ChatQW.BuildSceneObjectMatchRequestJson wire with the new current candidate image and production typed history of an actual prior off write/fresh readback. Expected result is separate evaluation metadata, never injected into wire. No HTTP or extra device write performed by this export.",
            ["requestFile"]="on-previously-closed-lamp.request.json",["inputFile"]="on-previously-closed-lamp.input.json",
            ["candidateImage"]="candidates.png",["sourceImage"]="source.jpg",["sourceCaptureId"]=source.CaptureId,
            ["capturedAtUtc"]=source.CapturedAtUtc,["ageSecondsAtExport"]=Time.realtimeSinceStartup-source.CapturedAtRealtime,
            ["mainGoalId"]=goalId,["expectedEntityId"]=expectedEntity,["expectedCandidateId"]=expected.candidateId,["expectedVerdict"]="matched"
        };
        File.WriteAllText(Path.Combine(path,"manifest.json"),manifest.ToString());
        LastReport["actualModelProbe"]=manifest.DeepClone();LastReport["actualModelProbe"]["relativeDirectory"]="scene-goal-referent/live-probe";
        Check(JObject.Parse(request.Wire)["messages"]!=null&&view.CaptureId==source.CaptureId,"Export retains the actual serialized model wire and matching source capture.");
    }
    private static IEnumerator Capture(ChatSample chat,string purpose,Action<SceneVisionObservation> completed)
    {
        SceneVisionObservation observation=null;Call(chat,"RequestSceneObservation",purpose,new Action<SceneVisionObservation>(value=>observation=value));
        yield return Wait(()=>observation!=null,5,"Goal-referent RGB/ID capture");
        Check(observation.HasImage&&observation.HasSurfaceGrounding,"A fresh real RGB/ID source is available.");
        Call(chat,"BindFormalSceneObservation",observation,(int)Get(chat,"m_FormalResponseGeneration"));completed(observation);
    }
    private static SceneObjectLightSwitch Lamp(string name,Vector3 position)
    {
        var root=new GameObject(name);root.transform.SetParent(fixture.transform,false);root.transform.localPosition=position;
        root.AddComponent<SceneObjectIdentity>().Configure(SceneObjectLampCatalog.Label,SceneObjectLampCatalog.AuthoredKey);
        var panel=GameObject.CreatePrimitive(PrimitiveType.Quad);panel.transform.SetParent(root.transform,false);panel.transform.localScale=new Vector3(.10f,.10f,1);UnityEngine.Object.DestroyImmediate(panel.GetComponent<Collider>());
        var material=new Material(Shader.Find("Standard"));material.color=Color.white;material.EnableKeyword("_EMISSION");material.SetColor("_EmissionColor",new Color(.4f,.2f,.1f));materials.Add(material);
        var renderer=panel.GetComponent<Renderer>();renderer.sharedMaterial=material;
        var lightObject=new GameObject("registered light");lightObject.transform.SetParent(root.transform,false);var light=lightObject.AddComponent<Light>();light.enabled=true;light.intensity=.2f;light.range=.25f;
        var device=root.AddComponent<SceneObjectLightSwitch>();Check(device.TryBind(root.transform,renderer,light,out string reason),"Real fixture mesh/material/Light registration: "+reason);return device;
    }
    private static object[] SnapshotCollection(object collection)
    {
        if(!(collection is IDictionary dictionary))return((IEnumerable)collection).Cast<object>().ToArray();
        var saved=new List<object>();var entries=dictionary.GetEnumerator();while(entries.MoveNext())saved.Add(entries.Entry);return saved.ToArray();
    }
    private static void RestoreCollection(object collection,object[] saved)
    {
        if(collection is IDictionary dictionary){dictionary.Clear();foreach(DictionaryEntry entry in saved)dictionary.Add(entry.Key,entry.Value);return;}
        collection.GetType().GetMethod("Clear").Invoke(collection,null);
        var add=collection.GetType().GetMethod(collection is IList?"Add":"Enqueue");foreach(var item in saved)add.Invoke(collection,new[]{item});
    }
    private static IEnumerator Wait(Func<bool> ready,float seconds,string label)
    {float until=Time.realtimeSinceStartup+seconds;while(!ready()){if(Time.realtimeSinceStartup>until)throw new TimeoutException(label);yield return null;}}
    private static void Check(bool value,string reason){checks++;if(!value)throw new InvalidOperationException(reason);}
    private static object Get(object target,string field)=>target.GetType().GetField(field,Flags).GetValue(target);
    private static void Set(object target,string field,object value)=>target.GetType().GetField(field,Flags).SetValue(target,value);
    private static object Call(object target,string name,params object[] args)
    {try{return target.GetType().GetMethods(Flags).Single(value=>value.Name==name&&value.GetParameters().Length==args.Length).Invoke(target,args);}catch(TargetInvocationException error){throw error.InnerException??error;}}
}
