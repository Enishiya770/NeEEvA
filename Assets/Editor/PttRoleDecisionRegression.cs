using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
#endif

// Protocol checks are pure; the optional Unity section uses the existing
// isolated, zero-network model fixture and actual room/vision controllers.
public static class PttRoleDecisionRegression
{
    private static int checks;
    private static void Check(bool value,string reason) { checks++; if(!value) throw new Exception(reason); }
    public static JObject Decision(string task="none",string route="reply",string attention="unchanged",string speech="",string evidence="") =>
        new JObject { ["version"]=3,["task"]=task,["attention"]=attention,["route"]=route,
            ["user_evidence"]=evidence,["thought"]="",["speech"]=new JObject{["text"]=speech,["language"]="zh"} };
    public static int RunChecks()
    {
        checks=0;
        foreach(string task in new[]{"approach","follow","stop_moving","other"})
        {
            var value=Decision(task,"reply",speech:"我会一直跟着你。");
            Check(DecisionCompiler.TryParse(value.ToString(),out var d,out _)&&d.Version==3&&d.Task==task&&
                d.Route=="tools"&&d.HasRouteIntent&&d.Speech=="","Task proposal escaped as ordinary public promise: "+task);
            Check(d.Intents.Single().Type==(task=="other"?"tool.request":"room.request"),"Task proposal lost its planner type.");
        }
        foreach(string attention in new[]{"visual_on","visual_off"})
        {
            var value=Decision(attention:attention,speech:"已经看清了。",evidence:"睁开眼");
            Check(DecisionCompiler.TryParse(value.ToString(),out var d,out _)&&d.Attention==attention&&
                d.HasExecutionIntent&&d.Route=="tools"&&d.Speech=="","Attention leaked pre-observation speech.");
            Check(d.HasBoundAttentionEvidence("请睁开眼。")&&!d.HasBoundAttentionEvidence("只是聊天。"),"Attention quote failed exact current-input binding.");
            value["user_evidence"]="";Check(!DecisionCompiler.TryParse(value.ToString(),out _,out _),"Attention without current quote accepted.");
        }
        foreach(string text in new[]{"你好。","我只解释这句话。","好的，不启动跟随。"})
        {
            Check(DecisionCompiler.TryParse(Decision(speech:text).ToString(),out var d,out _)&&!d.HasExecutionIntent&&
                d.Speech==text&&d.Task=="none"&&d.Attention=="unchanged","Ordinary/negation/quotation gained action authority.");
        }
        const string explore="你自己在房间随便逛逛，目的地由你决定。";
        Check(DecisionCompiler.TryParse(Decision("other","tools",evidence:explore).ToString(),out var exploring,out _)&&
            exploring.UserEvidence==explore&&exploring.Intents.Single().Type=="tool.request"&&exploring.Speech=="",
            "Delegated exploration must hand the complete request to the planner without inventing a user-approach action.");
        Check(DecisionCompiler.TryCompile(Decision("other","reply",speech:"我已经走遍房间。",evidence:explore).ToString(),out var exploreOutput,out _)&&
            !exploreOutput.Contains("我已经")&&!exploreOutput.Contains("<motion"),
            "Exploration route proposal gained execution tags or leaked an unexecuted movement claim.");
        Check(DecisionCompiler.TryParse(Decision(route:"recording_singing",speech:"先唱起来。").ToString(),out var song,out _)&&
            song.Route=="recording_singing"&&song.Speech==""&&song.Intents.Single().Type=="singing.request","Recording handoff changed or spoke.");
        Check(DecisionCompiler.TryParse(Decision(route:"cancel_work",evidence:"测试结束",speech:"再见。").ToString(),out var cancel,out _)&&
            !cancel.HasExecutionIntent&&cancel.Attention=="unchanged","Work cancellation invented visual shutdown.");
        Check(!DecisionCompiler.TryParse(Decision(route:"cancel_work").ToString(),out _,out _),"Empty work cancellation accepted.");
        foreach(string field in new[]{"task","attention"})
        {
            var bad=Decision();bad[field]="made_up";Check(!DecisionCompiler.TryParse(bad.ToString(),out _,out _),"Unknown semantic enum accepted.");
            bad=Decision();bad.Remove(field);Check(!DecisionCompiler.TryParse(bad.ToString(),out _,out _),"Missing v3 field accepted.");
            bad=Decision();bad[field]=true;Check(!DecisionCompiler.TryParse(bad.ToString(),out _,out _),"Boolean semantic enum accepted.");
        }
        var extended=Decision();extended["execute_now"]=true;Check(!DecisionCompiler.TryParse(extended.ToString(),out _,out _),"Extended authority accepted.");
        Check(!DecisionCompiler.TryParse(Decision().ToString()+"{}",out _,out _),"Trailing object accepted.");
        var branches=(JArray)DecisionCompiler.FastLaneResponseFormat()["json_schema"]["schema"]["anyOf"];
        Check(branches.Count==5&&branches.All(b=>(int)b["properties"]["version"]["enum"][0]==3),"Production schema is not the explicit v3 protocol.");
        Check(branches.Where(b=>((JArray)b["properties"]["route"]["enum"]).Any(r=>(string)r=="tools"|| (string)r=="recording_singing"))
            .All(b=>(int)b["properties"]["speech"]["properties"]["text"]["maxLength"]==0),"Action schema permits public promises.");
        return checks;
    }

#if UNITY_EDITOR
    const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object Get(object target,string name) => target.GetType().GetField(name,Flags).GetValue(target);
    static void Set(object target,string name,object value) => target.GetType().GetField(name,Flags).SetValue(target,value);
    static object Call(object target,string name,params object[] supplied)
    {
        Type type=target as Type??target.GetType();var method=type.GetMethod(name,Flags);var args=method.GetParameters();
        return method.Invoke(target is Type?null:target,args.Select((p,i)=>i<supplied.Length?supplied[i]:p.DefaultValue).ToArray());
    }
    public static IEnumerator RunIntegrationChecks(ChatSample chat,ArdyRoomDialogueBridge bridge,string output)
    {
        if(!Application.isBatchMode||!Application.isPlaying||chat==null||bridge==null||!bridge.IsConnected)
            throw new Exception("Use an isolated connected batch Play fixture.");
        int pure=RunChecks();var report=new JObject{["protocolChecks"]=pure,["scope"]="Actual non-realtime dispatcher, strict provider decoder, planner callbacks and near-user follow controller; fake LLM, no remote inference/TTS/motion."};
        string directory=Path.Combine(output,"ptt-role-decision");Directory.CreateDirectory(directory);
        var fixtureType=typeof(FirstReplyChatIntegrationRegression).GetNestedType("Fixture",BindingFlags.NonPublic);
        var fixture=(IDisposable)Activator.CreateInstance(fixtureType,new object[]{chat,bridge});
        var model=(FirstReplyChatFixtureModel)Get(fixture,"Model");
        Transform user=(Transform)Get(bridge,"userHead");Vector3 originalUser=user.position;
        try
        {
            void Arm(string text)
            {
                Call(fixture,"Arm",text);Set(chat,"m_BypassFirstReplyRoutingInRealtime",false);
                Set(chat,"m_RealtimeFormalTurnGeneration",-1);Set(chat,"m_AgentRunning",false);
                Set(chat,"m_PendingSceneFormalCaptureGeneration",-1);
            }
            void Dispatch() => Check((bool)Call(chat,"TryDispatchFirstReply",(string)Get(fixture,"Prompt"),null,
                (int)Get(fixture,"Generation"),true,null)&&model.Roles.Count==1&&model.First.Count==0,"PTT did not enter one JSON role decision.");
            string Queued() => (string)Call(fixture,"QueuedText");
            var avatar=(Component)Get(bridge,"avatar");
            user.position=new Vector3(avatar.transform.position.x+1f,originalUser.y,avatar.transform.position.z);
            int operation=bridge.Controller.OperationRevision;
            const string follow="请一直跟在我后面。";
            Arm(follow);Dispatch();var first=model.Roles[0];
            model.AnswerRole(first,Decision("follow","reply",speech:"我会一直跟着你。",evidence:follow));
            model.AnswerRole(first,Decision("follow","reply",speech:"重复回调不能出声。",evidence:follow));
            Check(model.Room.Count==1&&Queued()==""&&model.RoleCalls==1,"Follow/reply conflict or duplicate callback escaped the single planner handoff.");
            Check((string)JObject.Parse(model.Room[0])["currentUserText"]==follow,"Planner lost the original follow request.");
            model.RoomCallbacks[0](true,new JObject{["intent"]="follow",["origin"]="user",["evidence"]=follow}.ToString(),"fixture");
            Check(bridge.IsFollowing&&bridge.Phase=="follow-waiting"&&bridge.ReadCurrentTargetEvidence().followLegCount==0,
                "Actual follow task was not submitted as a persistent near-user wait.");
            Check((string)Get(chat,"m_RoomTaskDispatch")=="follow-submitted"&&bridge.Controller.OperationRevision==operation,
                "Follow did not reach the controller or started an external motion leg.");
            yield return null;
            Check(bridge.IsFollowing&&bridge.ReadCurrentTargetEvidence().followLegCount==0,"Near-user follow was mistaken for a completed one-shot approach.");

            const string stop="别再跟着我了。";
            Arm(stop);Dispatch();model.AnswerRole(model.Roles[0],Decision("stop_moving","tools",evidence:stop));
            Check(model.Room.Count==1&&Queued()=="","Stop was spoken instead of routed.");
            model.RoomCallbacks[0](true,new JObject{["intent"]="stop-moving",["origin"]="user",["evidence"]=stop}.ToString(),"fixture");
            Check(!bridge.IsFollowing&&(string)Get(chat,"m_RoomTaskDispatch")=="stop-submitted","Actual follow did not stop.");
            const string explore="你自己在房间随便逛逛，目的地由你决定。";
            int explorationOperation=bridge.Controller.OperationRevision;
            Arm(explore);Dispatch();var exploration=model.Roles[0];
            model.AnswerRole(exploration,Decision("other","tools",evidence:explore));
            model.AnswerRole(exploration,Decision("other","tools",evidence:explore));
            Check(model.Room.Count==1&&Queued()==""&&model.RoleCalls==1,
                "Exploration did not reach exactly one structured planner without a public promise.");
            Check((string)JObject.Parse(model.Room[0])["currentUserText"]==explore&&!bridge.IsFollowing&&
                bridge.Controller.OperationRevision==explorationOperation,
                "An exploration routing proposal lost the delegated purpose or executed motion before the room decision.");
            foreach(string text in new[]{"解释‘跟着我’这句话，不要真的移动。","只聊聊天，不要跟随。",
                "解释‘自己在房间随便逛逛’这句话，不要真的动。","我们只讨论探索，你现在不要开始走动。"})
            {
                Arm(text);Dispatch();model.AnswerRole(model.Roles[0],Decision(speech:"好的，只讨论这句话。"));
                Check(model.Room.Count==0&&!bridge.IsFollowing&&Queued().Contains("只讨论"),"Quotation/negation ordinary path triggered movement.");
            }
            Arm("照刚才的录音唱一遍。");Dispatch();model.AnswerRole(model.Roles[0],Decision(route:"recording_singing"));
            Check(model.Room.Count==0&&model.FormalCalls==1&&model.LastFormalStage==FormalResponseStage.RecordingSingingDecision&&Queued()=="",
                "PTT recording route lost its dedicated planner or spoke prematurely.");

            Arm("请睁开眼看看现在面前有什么。");
            Set(chat,"m_EnableScreenVision",true);Call(chat,"ApplySceneVisionAttention",false);Dispatch();
            var pending=model.Roles[0];model.AnswerRole(pending,Decision(route:"tools",attention:"visual_on",evidence:"睁开眼"));
            float deadline=Time.realtimeSinceStartup+5f;
            while(model.Roles.Count<2&&Time.realtimeSinceStartup<deadline)yield return null;
            Check(model.Roles.Count==2&&model.Roles[1].Image!=null&&Queued()==""&&model.Room.Count==0,
                "Visual attention did not await a real same-turn image before reconsideration.");
            Check(!(bool)Get(chat,"m_AgentRunning"),"Opening eyes started Agent autonomy.");
            model.AnswerRole(model.Roles[1],Decision(speech:"我依据这张新图回答。"));
            Check(Queued().Contains("依据这张新图")&&model.Room.Count==0,"Post-observation ordinary reply did not complete directly.");

            Arm("闭上眼睛。");Dispatch();model.AnswerRole(model.Roles[0],Decision(route:"tools",attention:"visual_off",evidence:"闭上眼睛"));
            Check(model.Roles.Count==2&&model.Roles[1].Image==null&&!(bool)Call(chat,"get_SceneEyesOpen")&&Queued()=="",
                "Closing eyes reused an old image or spoke its pre-operation draft.");
            model.AnswerRole(model.Roles[1],Decision(route:"tools",attention:"visual_off",evidence:"闭上眼睛"));
            Check(model.Roles.Count==2&&model.Room.Count==1&&Queued()=="","Repeated attention caused an unbounded redecision loop.");

            Arm("聊聊天。");Dispatch();model.AnswerRole(model.Roles[0],Decision(route:"tools",attention:"visual_on",evidence:"并不存在的原话"));
            Check(!(bool)Call(chat,"get_SceneEyesOpen")&&model.Roles.Count==1&&model.Room.Count==1&&Queued()=="",
                "Unbound attention quote changed visual state or escaped publicly.");

            Arm("睁开眼。");Dispatch();var stale=model.Roles[0];int staleGeneration=(int)Get(chat,"m_FormalResponseGeneration");
            Set(chat,"m_FormalResponseGeneration",staleGeneration+1);
            model.AnswerRole(stale,Decision(route:"tools",attention:"visual_on",evidence:"睁开眼"));
            Check(!(bool)Call(chat,"get_SceneEyesOpen")&&model.Roles.Count==1&&model.Room.Count==0&&Queued()=="",
                "A stale decision changed attention or started another request.");

            Arm("看看现在的场景。");Dispatch();var beforePause=model.Roles[0];
            int beforePauseGeneration=(int)Get(chat,"m_FormalResponseGeneration");
            Call(chat,"SetSceneVisionApplicationPaused",true);
            Check((int)Get(chat,"m_RoleDecisionInFlightGeneration")==-1&&
                (int)Get(chat,"m_FormalResponseGeneration")>beforePauseGeneration&&
                !(bool)Get(chat,"m_FormalResponseInFlight")&&(bool)Get(chat,"m_StreamComplete"),
                "Application pause did not release the in-flight role owner and busy state.");
            model.AnswerRole(beforePause,Decision(speech:"旧画面仍然可见。"));
            Check(Queued()==""&&model.Room.Count==0&&model.Roles.Count==1,
                "A paused visual owner published an old decision.");
            Call(chat,"SetSceneVisionApplicationPaused",false);
            report["passed"]=true;report["checks"]=checks;report["networkRequests"]=0;
        }
        finally
        {
            bridge.StopMovement("fixture-cleanup","PTT regression complete.");user.position=originalUser;
            fixture.Dispose();File.WriteAllText(Path.Combine(directory,"report.json"),report.ToString());
        }
    }

    public static void ExportLiveRequests(ChatSample chat,string output)
    {
        Directory.CreateDirectory(output);
        var source=new DirectoryInfo(Path.GetFullPath(output));
        while(source!=null&&!Directory.Exists(Path.Combine(source.FullName,"Assets/AIChatTookit/Prompts")))source=source.Parent;
        if(source==null)throw new Exception("Production prompt source root was not found above the export directory.");
        var bridge=chat.GetComponent<ArdyRoomDialogueBridge>();
        var fixtureType=typeof(FirstReplyChatIntegrationRegression).GetNestedType("Fixture",BindingFlags.NonPublic);
        var fixture=(IDisposable)Activator.CreateInstance(fixtureType,new object[]{chat,bridge});
        var host=new GameObject("PTT production-wire exporter");host.SetActive(false);
        var model=host.AddComponent<ChatQW>();model.m_Backend=ChatQW.BackendType.OpenAICompatible;
        var assets=new[]{"persona","language","behavior"}.Select(name=>new TextAsset(File.ReadAllText(
            Path.Combine(source.FullName,"Assets/AIChatTookit/Prompts",name+".txt"))){name=name}).ToArray();
        typeof(LLM).GetField("m_PromptFiles",Flags).SetValue(model,assets);
        var settings=(ChatSetting)Get(chat,"m_ChatSettings");
        var cases=new[]{
            new[]{"greeting","你好。"},new[]{"approach","请走到我身边。"},
            new[]{"follow","请一直跟在我后面。"},new[]{"stop","别再跟着我了。"},
            new[]{"explore","你自己在房间随便逛逛，目的地由你决定。"},
            new[]{"explore_indirect","不需要我逐个告诉你去哪儿，你自己挑感兴趣的地方走走看看。"},
            new[]{"explore_stop","别再四处逛了，停在这里。"},
            new[]{"explore_negation","我们只讨论探索，你现在不要开始走动。"},
            new[]{"explore_quotation","解释‘自己在房间随便逛逛’这句话，不要真的动。"},
            new[]{"negation","我们只聊天，不要跟着我走。"},new[]{"quotation","解释‘一直跟着我’这句话，不要真的移动。"},
            new[]{"eyes_on","请睁开眼。"},new[]{"eyes_off","请闭上眼。"},new[]{"visual_question","你现在实际能看到什么？"},
            new[]{"recording","请照着刚才的录音再唱一遍。"},new[]{"no_recording","唱一首歌给我听，我没有录过。"}};
        try
        {
            foreach(var row in cases)
            {
                Call(fixture,"Arm",row[1]);settings.m_ChatModel=model;
                Set(chat,"m_AgentRunning",false);Set(chat,"m_EnableScreenVision",true);
                Set(chat,"m_SceneVisionApplicationPaused",false);Set(chat,"m_AgentEyesOpen",row[0]=="eyes_off");
                Call(chat,"SetCurrentUserInput",row[1],row[1]);
                string prompt=(string)Call(chat,"BuildFirstReplyContext","")+"\n[本轮用户原话；必须完整保留其否定、指代和尾句]\n"+row[1];
                File.WriteAllText(Path.Combine(output,row[0]+".request.json"),(string)Call(model,"BuildRoleDecisionRequestJson",prompt,null));
            }
            File.WriteAllText(Path.Combine(output,"export-scope.json"),new JObject{
                ["sourceRoot"]=source.FullName,["productionBuilder"]="ChatQW.BuildRoleDecisionRequestJson",
                ["contextBuilder"]="ChatSample.BuildFirstReplyContext",["promptFiles"]=new JArray(assets.Select(a=>a.name)),
                ["cases"]=new JArray(cases.Select(c=>c[0])),["imageAttached"]=false,["agentRunning"]=false,
                ["scope"]="Read-only proposals from real production wire; isolated actual room state, no execution or fabricated recording evidence."
            }.ToString());
        }
        finally
        {
            fixture.Dispose();foreach(var asset in assets)UnityEngine.Object.DestroyImmediate(asset);
            UnityEngine.Object.DestroyImmediate(host);
        }
    }
#endif
}
