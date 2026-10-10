using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Actual room pixels and production planner/serializer with controlled callbacks.
/// Never completes a current reposition matcher: native trajectories have a separate fixture.</summary>
public static class SceneViewPositionIntegrationRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string LampSeedText = "你有看到电视机柜上的小灯吗？";
    public static JObject LastReport { get; private set; }

    private sealed class Context
    {
        public ChatSample chat; public ArdyRoomDialogueBridge bridge; public NevaSceneVisionIntegrationModel model;
        public List<LLM.SendData> history; public SceneObjectLightSwitch lamp;
        public string directory; public int checks; public Vector3 initialRoot; public int initialOperation;
        public void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
        public void Save()
        {
            LastReport["checks"] = checks; LastReport["lastFact"] = Fact(chat);
            LastReport["lastDispatch"] = (string)Get(chat, "m_RoomTaskDispatch");
            File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString() + "\n");
        }
        public void NoMovement(string label)
        {
            Check(bridge.Controller.OperationRevision == initialOperation &&
                Vector3.Distance(initialRoot, bridge.avatar.transform.position) < .0001f && !bridge.ReservesBody,
                label + ": no controller operation, root displacement or reserved body.");
        }
    }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("View-position checks require the existing isolated real-room Play fixture.");
        var c = new Context { chat=chat, bridge=bridge, model=model, history=model.m_DataList,
            directory=Path.Combine(output, "scene-view-position-integration"),
            initialRoot=bridge.avatar.transform.position, initialOperation=bridge.Controller.OperationRevision };
        Directory.CreateDirectory(c.directory);
        var savedNonSystem = c.history.Select((message,index) => new {message,index})
            .Where(x => x.message == null || x.message.role != "system").ToArray();
        var savedSystem = c.history.Where(x => x != null && x.role == "system").ToArray();
        string[] flagNames = {"m_EnableSceneObjectAwareness", "m_EnableSceneObjectTargetMatching", "m_RoomMotionOutputEnabled"};
        var flags = flagNames.ToDictionary(x => x, x => Get(chat,x));
        object oldGoal=Get(chat,"m_SceneObservationGoal"), oldMatch=Get(chat,"m_SceneObjectMatchReceipt");
        LastReport = new JObject { ["status"]="running", ["cases"]=new JArray(), ["frozenRequests"]=new JArray(),
            ["modelHttpRequests"]=0, ["nativeRequestsEnabled"]=false,
            ["scope"]="Original room rendered from the actual avatar head. Production planner, image serializer and object matcher dispatch are exercised with controlled callback values; movement is cancelled before native submission.",
            ["limitations"]="This suite does not prove Qwen reliability, real walking, readability, whole-object framing or a live desktop feed. Actual native trajectories and live frozen-request probes are separate tests.",
            ["dialogueIsolation"]="Only the accepted seed question backed by the completed physical lamp look is quoted. No assistant reply is invented; original message objects and system messages are restored." };
        var stack=new Stack<IEnumerator>();
        try
        {
            ClearHistory(c.history);
            c.Check(bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing,"Real room starts idle.");
            c.Check(SceneObjectLampCatalog.EnsureRegistered(bridge.environmentRoot,out c.lamp,out string reason) &&
                c.lamp.TryValidate(out reason),"Original authored lamp is available: "+reason);
            Set(chat,"m_SceneObservationGoal",null); Set(chat,"m_SceneObjectMatchReceipt",null);
            stack.Push(Exercise(c));
            while (stack.Count>0)
            {
                object yielded=null;
                try
                {
                    while (stack.Count>0)
                    {
                        var routine=stack.Peek();
                        if (!routine.MoveNext()) { (routine as IDisposable)?.Dispose(); stack.Pop(); continue; }
                        if (routine.Current is IEnumerator nested) {stack.Push(nested);continue;}
                        yielded=routine.Current; break;
                    }
                }
                catch(Exception error) {LastReport["error"]=error.ToString();throw;}
                if(stack.Count>0) yield return yielded;
            }
            LastReport["status"]="passed";
        }
        finally
        {
            try
            {
                while(stack.Count>0) (stack.Pop() as IDisposable)?.Dispose();
                Call(chat,"InvalidateFormalResponse","view-position-fixture-finished");
                Call(chat,"CancelSceneAction","view-position-fixture-finished"); Call(chat,"ClearScheduledAgentWake");
                foreach(var flag in flags) Set(chat,flag.Key,flag.Value);
                Set(chat,"m_SceneObservationGoal",oldGoal);Set(chat,"m_SceneObjectMatchReceipt",oldMatch);
            }
            finally
            {
                ClearHistory(c.history);
                foreach(var entry in savedNonSystem) c.history.Insert(Math.Min(entry.index,c.history.Count),entry.message);
                bool restored=ReferenceEquals(model.m_DataList,c.history) && savedNonSystem.All(x=>x.index<c.history.Count && ReferenceEquals(c.history[x.index],x.message));
                bool systems=c.history.Where(x=>x!=null && x.role=="system").SequenceEqual(savedSystem);
                LastReport["originalHistoryRestored"]=restored;LastReport["systemMessagesUnchanged"]=systems;
                if((string)LastReport["status"]=="running" || !restored || !systems) LastReport["status"]="failed";
                c.Save();
                if(!restored || !systems) throw new InvalidOperationException("Fixture history ownership was not restored.");
            }
        }
    }

    private static IEnumerator Exercise(Context c)
    {
        var powerBefore=JObject.Parse(JsonUtility.ToJson(c.lamp.ReadState()));
        yield return Prepare(c);
        SceneVisionObservation seed=null;
        yield return Capture(c,"view-position-seed",x=>seed=x);
        c.Check(c.chat.SceneVisionCapture.TryFindObservedObject(seed,c.lamp.EntityId,out var lamp,out string reason),"Lamp is actually visible: "+reason);
        Call(c.chat,"RecordAcceptedMotionUserTurn",LampSeedText);
        var seedWire=new JObject { ["intent"]="look-at",["origin"]="user",["evidence"]=LampSeedText,
            ["visualTarget"]=PixelTarget(lamp,"电视机柜上的小灯") };
        c.Check(RoomTaskProtocol.TryDecision(seedWire.ToString(),LampSeedText,false,out var seedDecision),"Production parser accepts seed look.");
        bool done=false;int before=c.model.Matches.Count;
        Set(c.chat,"m_FormalResponseInFlight",true);Set(c.chat,"m_AgentRoundInFlight",true);
        Call(c.chat,"BeginSceneAction",seedDecision,seed,(int)Get(c.chat,"m_FormalResponseGeneration"),new Action(()=>done=true));
        yield return Wait(()=>c.model.Matches.Count==before+1,5,"seed matcher");
        string matched=MatchReply(c,c.lamp.EntityId);
        c.model.Matches.Last().Complete(true,matched,"controlled-actual-lamp-match");
        yield return Wait(()=>done,12,"actual lamp head focus and new image");
        c.Check((string)Fact(c.chat)["phase"]=="observed" && (string)Fact(c.chat)["entityId"]==c.lamp.EntityId,
            "Physical seed confirms the same lamp in a new rendered view.");
        LastReport["seedFact"]=Fact(c.chat);c.NoMovement("Seed head look");c.Save();
        ExportFormalContextPair(c);

        string[][] cases={
            new[]{"lamp-farther","请离刚才看到的小灯稍微远一点，让我看看你能不能把它看全。","farther","see-whole"},
            new[]{"lamp-closer","请靠近刚才看到的小灯一点，仔细看它的细节。","closer","read-detail"},
            new[]{"lamp-side","请到刚才看到的小灯侧面换一个观察角度，试着避开遮挡。","left","avoid-occlusion"}
        };
        foreach(var item in cases)
        {
            yield return Prepare(c);SeedHistory(c);
            SceneVisionObservation source=null;
            yield return BeginPlanner(c,item[1],x=>source=x);
            c.Check(c.chat.SceneVisionCapture.TryFindObservedObject(source,c.lamp.EntityId,out var seen,out reason),item[0]+" lamp visible in current decision image: "+reason);
            var request=c.model.Decisions.Last();var input=JObject.Parse(request.Input);
            var sample=CheckSample(c,source,seen);
            Export(c,item[0],request,seen,sample,"reposition-view",item[2]=="left"?new[]{"left","right"}:new[]{item[2]},item[3]);
            yield return RouteThenCancel(c,request,ViewDecision(input,seen,item[2],item[3],true),seen.EntityId,item[0]);
        }

        // The referent is a real opaque TV/frame pixel, not the lamp relabelled as a screen.
        yield return Prepare(c);ClearHistory(c.history);
        SceneVisionObservation screenSource=null;
        yield return BeginPlanner(c,"请离你眼前的电视屏幕稍微远一点，试着让整个屏幕进入视野。",x=>screenSource=x);
        c.Check(TryFindActualScreen(c,screenSource,out var screen),"A real tv_frame/Desktop Screen opaque surface must be visible; never substitute a fabricated screen pixel.");
        var screenRequest=c.model.Decisions.Last();var screenInput=JObject.Parse(screenRequest.Input);
        LastReport["actualScreenFixture"]=new JObject { ["literalRootName"]=screen.Root.name,["entityId"]=screen.EntityId,
            ["captureId"]=screen.CaptureId,["scope"]="Fixture identifies the original TV/frame by its literal renderer hierarchy name, then verifies current opaque pixels. No desktop text or semantic readability is asserted." };
        Export(c,"screen-farther",screenRequest,screen,CheckSample(c,screenSource,screen),"reposition-view",new[]{"farther"},"see-whole");
        yield return RouteThenCancel(c,screenRequest,ViewDecision(screenInput,screen,"farther","see-whole",false),screen.EntityId,"screen-farther");

        yield return Prepare(c);SeedHistory(c);
        SceneVisionObservation questionSource=null;
        yield return BeginPlanner(c,"先不要移动，只解释一下为什么后退一点可能更容易看全物件。",x=>questionSource=x);
        var question=c.model.Decisions.Last();int questionMatches=c.model.Matches.Count,formal=c.model.Speech.Count;
        Export(c,"no-move-query",question,null,null,"none",Array.Empty<string>(),null);
        question.Complete(true,None(),"controlled-no-move-explanation");
        yield return Wait(()=>c.model.Speech.Count>formal,5,"no-move formal response preparation");
        c.Check(c.model.Matches.Count==questionMatches && (string)Get(c.chat,"m_RoomTaskIntent")=="none","Explanation query bypasses object matching and movement.");
        c.NoMovement("No-move query");Case(c,"no-move-query");

        yield return GuardCases(c);
        c.Check(JToken.DeepEquals(powerBefore,JObject.Parse(JsonUtility.ToJson(c.lamp.ReadState()))),"No lamp power or material mutation occurred.");
        c.NoMovement("Complete suite");
    }

    private static IEnumerator GuardCases(Context c)
    {
        yield return Prepare(c);SeedHistory(c);
        SceneVisionObservation source=null;
        yield return BeginPlanner(c,"请离刚才的小灯远一点看看。",x=>source=x);
        var initial=c.model.Decisions.Last();var input=JObject.Parse(initial.Input);
        c.Check(c.chat.SceneVisionCapture.TryFindObservedObject(source,c.lamp.EntityId,out var seen,out string reason),"Guard target is current: "+reason);
        var valid=ViewDecision(input,seen,"farther","see-whole",true);
        var stale=(JObject)valid.DeepClone();stale["observationGoalId"]=c.lamp.EntityId;
        int count=c.model.Decisions.Count,matches=c.model.Matches.Count;
        initial.Complete(true,stale.ToString(),"controlled-illegal-goal-on-view-position");
        yield return Wait(()=>c.model.Decisions.Count==count+1,6,"one fresh decision repair");
        var repair=c.model.Decisions.Last();var repairInput=JObject.Parse(repair.Input);
        c.Check((int?)repairInput["decisionRepair"]?["attempt"]==1 && (int?)repairInput["decisionRepair"]?["maximumAttempts"]==1 &&
            ((string)repairInput["decisionRepair"]?["rejectionReason"]??"").Contains("observation-goal"),"Illegal goal carries exact rejection into the one permitted repair.");
        c.Check((string)repairInput["visualObservation"]?["captureId"]!=source.CaptureId,"Repair captures a distinct real image.");
        var schema=JObject.Parse(repair.Wire)["response_format"]?["schema"] as JObject;
        c.Check(schema!=null && schema["properties"]?["observationGoalId"]==null &&
            !schema.Descendants().OfType<JProperty>().Any(x=>x.Name=="observationGoalId"),"Actual production repair wire removes every unavailable goal field from dynamic schema.");
        c.Check(c.model.Matches.Count==matches,"Invalid goal has not reached a matcher.");c.NoMovement("Invalid goal");
        Export(c,"stale-goal-repair",repair,null,null,"reposition-view",new[]{"farther"},"see-whole");
        initial.Complete(true,valid.ToString(),"late-original-decision");yield return null;
        c.Check(c.model.Matches.Count==matches,"Old callback cannot supersede the repair owner.");
        Call(c.chat,"InvalidateFormalResponse","view-position-cancel-repair");Call(c.chat,"CancelSceneAction","view-position-cancel-repair");
        var repaired=(JObject)valid.DeepClone();repaired["entityTarget"]["captureId"]=(string)repairInput["visualObservation"]["captureId"];
        repair.Complete(true,repaired.ToString(),"late-cancelled-repair");yield return null;yield return null;
        c.Check(c.model.Matches.Count==matches && c.model.Decisions.Count==count+1,"Cancelled repair cannot dispatch or request another repair.");
        c.NoMovement("Late callbacks");Case(c,"one-repair-and-cancelled-old-callbacks");

        yield return Prepare(c);SeedHistory(c);
        SceneVisionObservation firstSource=null;
        yield return BeginPlanner(c,"请靠近小灯一点观察。",x=>firstSource=x);
        var replaced=c.model.Decisions.Last();var replacedInput=JObject.Parse(replaced.Input);
        c.Check(c.chat.SceneVisionCapture.TryFindObservedObject(firstSource,c.lamp.EntityId,out var firstSeen,out reason),"Replacement first reference exists: "+reason);
        var replacementWire=ViewDecision(replacedInput,firstSeen,"closer","read-detail",true);
        yield return Prepare(c);SeedHistory(c);
        SceneVisionObservation secondSource=null;
        yield return BeginPlanner(c,"现在不要移动，只回答我。",x=>secondSource=x);
        var latest=c.model.Decisions.Last();matches=c.model.Matches.Count;
        replaced.Complete(true,replacementWire.ToString(),"late-replaced-user-decision");yield return null;yield return null;
        c.Check(c.model.Matches.Count==matches,"New user generation fences the older movement callback.");
        latest.Complete(true,None(),"controlled-current-no-move");yield return null;
        c.NoMovement("Replaced user");Case(c,"new-user-replaces-pending-move");

        yield return Prepare(c);SeedHistory(c);
        SceneVisionObservation disabledSource=null;
        yield return Capture(c,"disabled-view-position",x=>disabledSource=x);
        c.Check(c.chat.SceneVisionCapture.TryFindObservedObject(disabledSource,c.lamp.EntityId,out var disabledSeen,out reason),"Disabled case uses actual visible lamp: "+reason);
        const string disabledText="请退后一点看小灯。";
        Call(c.chat,"RecordAcceptedMotionUserTurn",disabledText);
        var disabledInput=new JObject { ["currentUserText"]=disabledText,["visualObservation"]=new JObject{["captureId"]=disabledSource.CaptureId} };
        c.Check(RoomTaskProtocol.TryDecision(ViewDecision(disabledInput,disabledSeen,"farther","see-whole",true).ToString(),disabledText,false,out var decision),"Disabled case decision remains protocol-valid.");
        SceneVisionObservation newerSource=null;
        yield return Capture(c,"view-position-replaces-old-capture",x=>newerSource=x);
        matches=c.model.Matches.Count;bool staleCompleted=false;
        Call(c.chat,"BeginSceneAction",decision,newerSource,(int)Get(c.chat,"m_FormalResponseGeneration"),new Action(()=>staleCompleted=true));
        c.Check(staleCompleted && c.model.Matches.Count==matches && (string)Fact(c.chat)["reason"]=="visual-reference-not-current",
            "A syntactically valid old capture reference is rejected at the action gate before candidate repair or matching.");
        c.NoMovement("Stale image reference");Case(c,"stale-capture-gate");
        Set(c.chat,"m_RoomMotionOutputEnabled",false);matches=c.model.Matches.Count;bool completed=false;
        Call(c.chat,"BeginSceneAction",decision,disabledSource,(int)Get(c.chat,"m_FormalResponseGeneration"),new Action(()=>completed=true));
        c.Check(completed && c.model.Matches.Count==matches && ((string)Fact(c.chat)["reason"]??"").Contains("disabled"),"Disabled output fails at the production action gate before any matcher or native request.");
        c.NoMovement("Disabled output");Set(c.chat,"m_RoomMotionOutputEnabled",true);Case(c,"disabled-output-gate");
    }

    private static IEnumerator RouteThenCancel(Context c,NevaSceneVisionIntegrationModel.Request request,JObject wire,string entity,string label)
    {
        string text=(string)JObject.Parse(request.Input)["currentUserText"];
        c.Check(RoomTaskProtocol.TryDecision(wire.ToString(),text,false,out var parsed) && parsed.IsViewPosition && parsed.IsVisual,"Production parser classifies "+label);
        int matches=c.model.Matches.Count;
        request.Complete(true,wire.ToString(),"controlled-view-position");
        yield return Wait(()=>c.model.Matches.Count==matches+1,5,label+" production matcher dispatch");
        var matcher=c.model.Matches.Last();var matchInput=JObject.Parse(matcher.Input);var fact=Fact(c.chat);
        c.Check((string)matchInput["intent"]=="reposition-view" && (string)matchInput["initialGeometryEntityId"]==entity,
            label+" reaches actual same-entity matcher through BeginSceneAction.");
        c.Check((string)fact["viewDirection"]==(string)wire["viewPosition"]["direction"] &&
            (string)fact["viewPurpose"]==(string)wire["viewPosition"]["purpose"] && Math.Abs(((float?)fact["requestedViewDistance"] ?? -1f)-.4f)<.0001f,
            label+" preserves direction, purpose and distance in action facts.");
        string lateMatch=MatchReply(c,entity);
        Call(c.chat,"InvalidateFormalResponse","view-position-before-native");Call(c.chat,"CancelSceneAction","view-position-before-native");
        matcher.Complete(true,lateMatch,"controlled-late-match-after-cancel");yield return null;yield return null;
        c.NoMovement(label);Case(c,label);
    }

    private static SceneObjectViewSample CheckSample(Context c,SceneVisionObservation observation,SceneObservedObject target)
    {
        var sample=SceneObjectViewSample.Measure(observation,target);
        c.Check(sample!=null && sample.entityId==target.EntityId && sample.captureId==observation.CaptureId &&
            sample.frame==observation.Frame && sample.width==observation.Width && sample.height==observation.Height,"View sample retains actual same-frame object ownership.");
        c.Check(sample.visibleOpaquePixels>0 && sample.visiblePixelFraction>0 && sample.visiblePixelFraction<=1 &&
            sample.cameraDistanceToObservedPoint>0 && sample.normalizedVisibleBounds.x>=0 && sample.normalizedVisibleBounds.y>=0 &&
            sample.normalizedVisibleBounds.z>0 && sample.normalizedVisibleBounds.w>0 &&
            sample.normalizedVisibleBounds.x+sample.normalizedVisibleBounds.z<=1.001f &&
            sample.normalizedVisibleBounds.y+sample.normalizedVisibleBounds.w<=1.001f,"Visible bounds are bounded actual opaque pixels with a measured distance.");
        c.Check(Math.Abs(sample.visiblePixelFraction-(float)sample.visibleOpaquePixels/(sample.width*sample.height))<.000001f &&
            sample.scope.Contains("does not prove") && SceneObjectViewSample.Measure(null,target)==null &&
            SceneObjectViewSample.Measure(observation,null)==null,"Sample fraction is consistent and explicitly does not prove whole visibility or readability.");
        return sample;
    }

    private static void ExportFormalContextPair(Context c)
    {
        int generation=(int)Get(c.chat,"m_FormalResponseGeneration");
        object oldRoomGeneration=Get(c.chat,"m_RoomTaskGeneration"),oldIntent=Get(c.chat,"m_RoomTaskIntent");
        try
        {
            // The fixture dispatched the real look directly, so supply that actual transaction's
            // planner bookkeeping while retaining its unmodified observed action/result facts.
            Set(c.chat,"m_RoomTaskGeneration",generation);Set(c.chat,"m_RoomTaskIntent","look-at");
            var observation=(SceneVisionObservation)Call(c.chat,"GetFormalSceneObservation",generation);
            c.Check(observation!=null && observation.HasImage && (string)Fact(c.chat)["resultCaptureId"]==observation.CaptureId,
                "Formal context pair uses the real completed look's current result image.");
            string transient=(string)Call(c.chat,"BuildRoomTaskReplyFacts");
            string baseContext=(string)Call(c.chat,"BuildFormalObservationContext",transient);
            string before=(string)Call(c.chat,"BuildMotionRequestContext",baseContext);
            string after=(string)Call(c.chat,"BuildFormalResponseRequestContext",baseContext,generation,true);
            const string menu="一次 motion 会独立执行";
            c.Check(before.Contains(menu) && !after.Contains(menu) && after.StartsWith(baseContext,StringComparison.Ordinal) &&
                after.Contains((string)Fact(c.chat)["actionId"]),"Scoped spatial reply retains actual action facts and removes the unrelated free-motion menu.");
            c.Check(((string)Call(c.chat,"BuildFormalResponseRequestContext",baseContext,generation,false)).Contains(menu) &&
                ((string)Call(c.chat,"BuildFormalResponseRequestContext",baseContext,generation+1,true)).Contains(menu),
                "Unprepared and stale generation retain the normal motion capability contract.");
            Set(c.chat,"m_RoomTaskIntent","none");
            c.Check(((string)Call(c.chat,"BuildFormalResponseRequestContext",baseContext,generation,true)).Contains(menu),
                "Ordinary intent=none retains free body expression capabilities.");
            Set(c.chat,"m_RoomTaskIntent","look-at");
            var history=c.history.Where(x=>x!=null && x.role=="system").ToList();
            history.Add(new LLM.SendData("user",LampSeedText){imageDataUrl=observation.ImageDataUrl});
            var serializer=Get(c.model,"serializer");
            string beforeWire=(string)Call(serializer,"BuildRequestJsonForMessages",history,true,before);
            string afterWire=(string)Call(serializer,"BuildRequestJsonForMessages",history,true,after);
            var beforeMessages=(JArray)JObject.Parse(beforeWire)["messages"];
            var afterMessages=(JArray)JObject.Parse(afterWire)["messages"];
            c.Check(JToken.DeepEquals(new JArray(beforeMessages.Where(x=>(string)x["role"]!="system")),
                new JArray(afterMessages.Where(x=>(string)x["role"]!="system"))) &&
                beforeWire.Contains(observation.ImageDataUrl) && afterWire.Contains(observation.ImageDataUrl),
                "Paired production wires keep identical real user/history/image; only appended response context differs.");
            string directory=Path.Combine(c.directory,"latency-paired");Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,"before.request.json"),beforeWire+"\n");
            File.WriteAllText(Path.Combine(directory,"after.request.json"),afterWire+"\n");
            File.WriteAllText(Path.Combine(directory,"before.context.txt"),before);
            File.WriteAllText(Path.Combine(directory,"after.context.txt"),after);
            File.WriteAllText(Path.Combine(directory,"base.context.txt"),baseContext);
            var manifest=new JObject { ["scope"]="Same actual base facts; production old full motion context vs scoped spatial response context. A real completed lamp look, not fabricated native movement.",
                ["captureId"]=observation.CaptureId,["sourceGeneration"]=generation,
                ["beforeRequestFile"]="before.request.json",["afterRequestFile"]="after.request.json",
                ["beforeContextCharacters"]=before.Length,["afterContextCharacters"]=after.Length,
                ["nativeMovementClaimed"]=false,["productionContextHelper"]="BuildFormalResponseRequestContext",
                ["roomTaskPrepared"]=true,["roomTaskIntent"]="look-at",["actualFact"]=Fact(c.chat) };
            File.WriteAllText(Path.Combine(directory,"manifest.json"),manifest.ToString()+"\n");
            LastReport["formalContextPair"]=manifest;Case(c,"formal-context-scope-and-paired-export");
        }
        finally {Set(c.chat,"m_RoomTaskGeneration",oldRoomGeneration);Set(c.chat,"m_RoomTaskIntent",oldIntent);}
    }

    private static bool TryFindActualScreen(Context c,SceneVisionObservation observation,out SceneObservedObject found)
    {
        found=null;
        // Fixture-only scene selection locates a literal original screen renderer. Every return
        // still requires a real same-frame opaque pixel of that renderer/root, not projected bounds.
        var renderers=c.bridge.environmentRoot.GetComponentsInChildren<MeshRenderer>(true)
            .Where(x=>x!=null && x.enabled && x.gameObject.activeInHierarchy && ScreenNamed(x.transform)).ToArray();
        foreach(var renderer in renderers)
        {
            var bounds=renderer.bounds;
            foreach(float x in new[]{0f,-.25f,.25f,-.45f,.45f})
                foreach(float y in new[]{0f,-.25f,.25f,-.45f,.45f})
                    foreach(float z in new[]{0f,-.45f,.45f})
                    {
                        Vector3 point=bounds.center+Vector3.Scale(bounds.size,new Vector3(x,y,z));
                        Vector3 local=Quaternion.Inverse(observation.CameraRotation)*(point-observation.CameraPosition);
                        if(local.z<=.025f)continue;
                        float tangent=Mathf.Tan(observation.VerticalFov*Mathf.Deg2Rad*.5f);
                        var uv=new Vector2(.5f+local.x/(2*local.z*tangent*observation.Width/observation.Height),.5f-local.y/(2*local.z*tangent));
                        if(uv.x<.01f || uv.x>.99f || uv.y<.01f || uv.y>.99f)continue;
                        if(!c.chat.SceneVisionCapture.TryResolveObservedObject(observation,uv,out var value,out _) ||
                            !value.VisibleConfirmed || value.Root==null || !value.TryValidate(out _))continue;
                        if(renderer.transform!=value.Root && !renderer.transform.IsChildOf(value.Root))continue;
                        if(!ScreenNamed(value.Root))continue;
                        found=value;return true;
                    }
        }
        return false;
    }
    private static bool ScreenNamed(Transform value)
    {
        for(var current=value;current!=null;current=current.parent)
            if(string.Equals(current.name,"tv_frame",StringComparison.OrdinalIgnoreCase) ||
                string.Equals(current.name,"Desktop Screen",StringComparison.OrdinalIgnoreCase))return true;
        return false;
    }

    private static void Export(Context c,string name,NevaSceneVisionIntegrationModel.Request request,SceneObservedObject target,
        SceneObjectViewSample sample,string intent,string[] directions,string purpose)
    {
        c.Check(request.Kind=="room" && !string.IsNullOrEmpty(request.Image) &&
            JObject.Parse(request.Wire)["messages"].ToString().Contains(request.Image),"Frozen request is the actual production serializer output with the actual captured image.");
        File.WriteAllText(Path.Combine(c.directory,name+".request.json"),request.Wire+"\n");
        File.WriteAllText(Path.Combine(c.directory,name+".input.json"),request.Input+"\n");
        File.WriteAllBytes(Path.Combine(c.directory,name+".jpg"),Convert.FromBase64String(request.Image.Substring(request.Image.IndexOf(',')+1)));
        if(target==null && intent=="reposition-view")
        {
            var current=(SceneVisionObservation)Call(c.chat,"GetFormalSceneObservation",(int)Get(c.chat,"m_FormalResponseGeneration"));
            c.Check(current!=null && current.CaptureId==(string)JObject.Parse(request.Input)["visualObservation"]?["captureId"] &&
                c.chat.SceneVisionCapture.TryFindObservedObject(current,c.lamp.EntityId,out target,out _),
                "Repair evaluation target is confirmed in its own new image.");
            sample=CheckSample(c,current,target);
        }
        JObject mask=target==null?null:ExportVisibleMask(c,name,target);
        ((JArray)LastReport["frozenRequests"]).Add(new JObject { ["case"]=name,["request"]=name+".request.json",
            ["input"]=name+".input.json",["image"]=name+".jpg",["actualEntityId"]=target?.EntityId ?? c.lamp.EntityId,
            ["captureId"]=(string)JObject.Parse(request.Input)["visualObservation"]?["captureId"],
            ["expectedIntent"]=intent,["allowedDirections"]=new JArray(directions),["expectedPurpose"]=purpose,
            ["actualImagePoint"]=target==null?null:new JObject{["x"]=target.ImagePoint.x,["y"]=target.ImagePoint.y},
            ["visibleEntityMask"]=mask,
            ["visibleSample"]=sample==null?null:JObject.Parse(JsonUtility.ToJson(sample)),
            ["scope"]="Expected outcome is fixture evaluation metadata only, not injected into the frozen model request." });
        c.Save();
    }
    private static JObject ExportVisibleMask(Context c,string name,SceneObservedObject target)
    {
        var observation=(SceneVisionObservation)Get(target,"observation");
        object frame=typeof(SceneVisionObservation).GetProperty("Grounding",Flags).GetValue(observation);
        var depth=(float[])Get(frame,"depthIds");var entries=(Array)Get(frame,"entries");
        int width=(int)Get(frame,"width"),height=(int)Get(frame,"height");bool top=(bool)Get(frame,"rowsStartAtTop");
        c.Check(target.VisibleConfirmed && target.TryValidate(out _) && width==observation.Width && height==observation.Height &&
            width<=1280 && height<=960 && depth.Length==width*height*2,"Evaluation mask uses the exact bounded opaque companion frame.");
        var decode=typeof(SceneVisionGrounding).GetMethod("ObjectEntryId",Flags);
        var eligible=new Dictionary<int,bool>();
        // Decode each distinct ID once using the production implementation, never once per pixel.
        for(int i=1;i<depth.Length;i+=2)
        {
            int id=Mathf.RoundToInt(depth[i]);if(eligible.ContainsKey(id))continue;
            object[] args={id,entries.Length,0,false};bool accepted=(bool)decode.Invoke(null,args);
            object entry=accepted?entries.GetValue((int)args[2]):null;
            eligible[id]=accepted && (bool)args[3] && entry!=null && (string)Get(entry,"entityId")==target.EntityId &&
                (Transform)Get(entry,"identityRoot")==target.Root && (bool)Get(entry,"observedRigid") &&
                (bool)entry.GetType().GetMethod("TryValidateObserved",Flags).Invoke(entry,new object[]{false,null});
        }
        var mask=new byte[width*height];int count=0;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int index=(y*width+x)*2,id=Mathf.RoundToInt(depth[index+1]);float z=depth[index];
            if(!eligible[id] || float.IsNaN(z) || float.IsInfinity(z) || z<=.025f || z>60f)continue;
            bool consistent=true;
            for(int dy=-1;dy<=1 && consistent;dy++)for(int dx=-1;dx<=1;dx++)
            {
                int neighbor=(Mathf.Clamp(y+dy,0,height-1)*width+Mathf.Clamp(x+dx,0,width-1))*2;
                float other=depth[neighbor];
                if(Mathf.RoundToInt(depth[neighbor+1])!=id || float.IsNaN(other) || float.IsInfinity(other) ||
                    Mathf.Abs(other-z)>Mathf.Max(.03f,z*.025f)){consistent=false;break;}
            }
            if(consistent){mask[(top?y:height-1-y)*width+x]=1;count++;}
        }
        c.Check(count>0,"The selected entity has actual stable opaque pixels in its evaluation mask.");
        string filename=name+".visible-entity-mask.bin";File.WriteAllBytes(Path.Combine(c.directory,filename),mask);
        return new JObject { ["file"]=filename,["captureId"]=target.CaptureId,["entityId"]=target.EntityId,
            ["width"]=width,["height"]=height,["markedPixels"]=count,["encoding"]="row-major-top-left-u8-0-or-1",
            ["scope"]="Evaluation-only same-frame opaque ID mask with the production 3x3 ID/depth consistency rule. Not sent to the model; no reachability, current-session freshness or text readability asserted." };
    }
    private static JObject ViewDecision(JObject input,SceneObservedObject seen,string direction,string purpose,bool entity)
    {
        var result=new JObject { ["intent"]="reposition-view",["origin"]="user",["evidence"]=(string)input["currentUserText"],
            ["viewPosition"]=new JObject { ["direction"]=direction,["distance"]=.4,["purpose"]=purpose } };
        if(entity)result["entityTarget"]=new JObject { ["captureId"]=(string)input["visualObservation"]["captureId"],["entityId"]=seen.EntityId };
        else result["visualTarget"]=PixelTarget(seen,"电视屏幕与外框区域");
        return result;
    }
    private static JObject PixelTarget(SceneObservedObject seen,string label)=>new JObject {
        ["captureId"]=seen.CaptureId,["x"]=seen.ImagePoint.x,["y"]=seen.ImagePoint.y,["label"]=label };
    private static string MatchReply(Context c,string entity)
    {
        var candidate=c.chat.LastSceneObjectCandidateView.Candidates.FirstOrDefault(x=>x.entityId==entity);
        c.Check(candidate!=null,"Actual candidate view contains the exact selected entity.");
        return new JObject { ["verdict"]="matched", ["candidateRole"]="target",["candidateId"]=candidate.candidateId,
            ["reason"]="Controlled fixture reply for the same actual visible target; no assertion of readability or whole-object fit." }.ToString();
    }
    private static string None()=>new JObject { ["intent"]="none",["origin"]="none",["evidence"]="" }.ToString();
    private static IEnumerator Prepare(Context c)
    {
        Call(c.chat,"CancelSceneAction","view-position-next");Call(c.chat,"ClearScheduledAgentWake");
        yield return Wait(()=>!((Get(c.chat,"m_SceneAttention") as NevaSceneAttention)?.IsBusy??false),5,"head release");
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags).Invoke(null,new object[]{c.chat});
        Set(c.chat,"m_EnableSceneObjectAwareness",true);Set(c.chat,"m_EnableSceneObjectTargetMatching",true);
        Set(c.chat,"m_RoomMotionOutputEnabled",true);
    }
    private static void SeedHistory(Context c) {ClearHistory(c.history);c.history.Add(new LLM.SendData("user",LampSeedText));}
    private static void ClearHistory(List<LLM.SendData> history) {for(int i=history.Count-1;i>=0;i--)if(history[i]==null || history[i].role!="system")history.RemoveAt(i);}
    private static IEnumerator BeginPlanner(Context c,string text,Action<SceneVisionObservation> captured)
    {
        Call(c.chat,"RecordAcceptedMotionUserTurn",text);
        SceneVisionObservation observation=null;yield return Capture(c,"view-position-decision",x=>observation=x);captured(observation);
        int before=c.model.Decisions.Count;
        Call(c.chat,"StartStreaming",text,false,null,false,false,false,null,false,observation,true);
        yield return Wait(()=>c.model.Decisions.Count==before+1,5,"production view-position planner");
    }
    private static IEnumerator Capture(Context c,string purpose,Action<SceneVisionObservation> completed)
    {
        SceneVisionObservation value=null;Call(c.chat,"RequestSceneObservation",purpose,new Action<SceneVisionObservation>(x=>value=x));
        yield return Wait(()=>value!=null,5,"actual RGB capture: "+purpose);
        c.Check(value.HasImage && value.HasSurfaceGrounding,"Current actual RGB and companion surface frame available.");completed(value);
    }
    private static IEnumerator Wait(Func<bool> ready,float seconds,string label)
    {float deadline=Time.realtimeSinceStartup+seconds;while(!ready()){if(Time.realtimeSinceStartup>deadline)throw new TimeoutException(label);yield return null;}}
    private static void Case(Context c,string label) {((JArray)LastReport["cases"]).Add(label);c.Save();}
    private static JObject Fact(ChatSample chat)=>Get(chat,"m_SceneActionFact") is object fact?JObject.Parse(JsonUtility.ToJson(fact)):new JObject();
    private static object Call(object target,string name,params object[] args)=>NevaSceneVisionIntegrationRegression.Call(target,name,args);
    private static object Get(object target,string name)=>target.GetType().GetField(name,Flags).GetValue(target);
    private static void Set(object target,string name,object value)=>target.GetType().GetField(name,Flags).SetValue(target,value);
}
