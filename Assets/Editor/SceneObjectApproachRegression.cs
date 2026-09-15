using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Same-entity approach in the existing real-room isolation. No actor relocation or synthetic arrival.
/// The native entry is deliberately separate: only an explicit runner flag may use the ARDY service.</summary>
public static class SceneObjectApproachRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }
    private sealed class Context
    {
        public ChatSample chat; public ArdyRoomDialogueBridge bridge; public NevaEyeTracking tracking;
        public string output; public int checks; public bool native; public int recordedRegion = -1;
        public GameObject occluder; public Material material;
        public readonly List<ArdyRoomDialogueBridge.SceneTargetResult> results = new List<ArdyRoomDialogueBridge.SceneTargetResult>();
        public Action<ArdyRoomDialogueBridge.SceneTargetResult> arrived;
        public void Check(bool valid, string description)
        { checks++; if (!valid) throw new InvalidOperationException(description); }
        public void OnResult(ArdyRoomDialogueBridge.SceneTargetResult value)
        { if (value != null) { results.Add(value); arrived?.Invoke(value); } }
        public void Save()
        {
            LastReport["checks"] = checks;
            if (chat != null) LastReport["lastActionFact"] = Fact(chat);
            if (bridge != null && bridge.avatar != null && bridge.importedReference != null)
            {
                Vector3 root = bridge.avatar.transform.position;
                float offset = bridge.importedReference.groundY * bridge.avatar.transform.lossyScale.y;
                LastReport["lastAvatarRoot"] = JObject.Parse(JsonUtility.ToJson(root));
                LastReport["importedFootOffset"] = offset;
                LastReport["lastReferenceGround"] = JObject.Parse(JsonUtility.ToJson(root + Vector3.up * offset));
                LastReport["placementScope"] = "Avatar root and imported-reference foot ground are different quantities; no floor snap is performed by this fixture.";
            }
            LastReport["nativeTerminalEvents"] = new JArray(results.Select(x => JObject.Parse(JsonUtility.ToJson(x))));
            File.WriteAllText(Path.Combine(output, "report.json"), LastReport.ToString() + "\n");
        }
        public void HideView()
        {
            Check(chat.SceneVisionCapture.TryGetCurrentCameraPose(out Pose pose), "Real current view supplies temporary visual occlusion.");
            occluder = GameObject.CreatePrimitive(PrimitiveType.Quad);
            occluder.name = "Isolated arrival reobservation occluder";
            occluder.transform.SetParent(bridge.environmentRoot, false);
            occluder.transform.SetPositionAndRotation(pose.position + pose.rotation * new Vector3(0, 0, .4f), pose.rotation);
            occluder.transform.localScale = new Vector3(3f / bridge.environmentRoot.lossyScale.x,
                3f / bridge.environmentRoot.lossyScale.y, 1f / bridge.environmentRoot.lossyScale.z);
            UnityEngine.Object.DestroyImmediate(occluder.GetComponent<Collider>());
            material = new Material(Shader.Find("Unlit/Color")) { color = new Color(.4f, .4f, .4f, 1f) };
            occluder.GetComponent<Renderer>().sharedMaterial = material;
        }
        public void ClearOccluder()
        {
            if (occluder != null) UnityEngine.Object.DestroyImmediate(occluder);
            if (material != null) UnityEngine.Object.DestroyImmediate(material);
            occluder = null; material = null;
        }
    }

    public static IEnumerator RunGuardChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, string output)
        => Run(chat, bridge, null, output, false);
    public static IEnumerator RunNativeChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output) => Run(chat, bridge, model, output, true);

    public static IEnumerator RunRecordedLowFurnitureChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output, int regionIndex)
    {
        if (regionIndex < 0 || regionIndex >= RecordedRegions.Length) throw new ArgumentOutOfRangeException(nameof(regionIndex));
        return Run(chat, bridge, model, output, true, regionIndex);
    }

    private static readonly Vector3[] RecordedRegions = {
        new Vector3(-.5717926f, -.3993156f, -5.7215543f),
        new Vector3(-.4307084f, -.3645766f, -5.7900224f),
        new Vector3(-.4113269f, -.1195828f, -4.5481033f),
        new Vector3(-.2753994f, -.3645822f, -5.8236108f),
        new Vector3(-.2546899f, -.3645759f, -5.8133540f),
        new Vector3(-.4318275f, -.3645761f, -5.7892766f),
        new Vector3(-.3928502f, -.3645715f, -6.1357908f) };
    private static readonly string[] RecordedActions = { "2d0b:19:12", "2d0b:22:15", "2836:3:2", "2836:8:6", "2836:11:8", "2836:17:13", "2836:47:34" };

    private static IEnumerator Run(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output, bool native, int recordedRegion = -1)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Entity approach regression requires the existing isolated actual-room Play fixture.");
        var c = new Context { chat=chat, bridge=bridge, native=native, recordedRegion=recordedRegion,
            tracking=bridge?.avatar?.GetComponent<NevaEyeTracking>(),
            output=Path.Combine(output, recordedRegion >= 0 ? "scene-object-approach-low-" + recordedRegion :
                native ? "scene-object-approach-native" : "scene-object-approach-guards") };
        Directory.CreateDirectory(c.output);
        LastReport = new JObject { ["status"]="running", ["cases"]=new JArray(), ["modelHttpRequests"]=0,
            ["nativeRequestsEnabled"]=native,
            ["scope"]=native ? "Real scene-image entity, actual head look and second image, then the production confirmed-approach handoff through native ARDY, actual ground arrival and same-entity post-arrival RGB/ID confirmation. Second attempt physically occludes the arrived view. The selected target and semantic agreement are controlled preconditions, not a model-understanding claim." :
                "Real room capture establishes the exact entity commitment. Tests distinguish stale image permission from committed target lifetime, root/room/geometry validity and current visibility. No movement submission or model request.",
            ["limitations"]="No physical touching, device mutation, actor/user teleport, stairs, added navigation world, or model semantic reliability claim." };
        bool awareness=(bool)Get(chat,"m_EnableSceneObjectAwareness");
        bool matching=(bool)Get(chat,"m_EnableSceneObjectTargetMatching");
        bool formal=(bool)Get(chat,"m_FormalResponseInFlight"), round=(bool)Get(chat,"m_AgentRoundInFlight");
        int initialOperation=bridge.Controller.OperationRevision, modelWires=model?.Wires.Count ?? 0;
        var stack = new Stack<IEnumerator>();
        try
        {
            c.Check(bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing, "Existing room fixture is idle and connected.");
            c.Check(c.tracking != null && c.tracking.IsInitialized && c.tracking.isActiveAndEnabled,
                "Actual edit-calibrated head and eyes are available.");
            bridge.SceneTargetCompleted += c.OnResult;
            if (native) Set(chat,"m_EnableSceneObjectTargetMatching",false);
            stack.Push(native ? NativeCases(c) : GuardCases(c));
            while (stack.Count > 0)
            {
                object yielded=null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var routine=stack.Peek();
                        if (!routine.MoveNext()) { (routine as IDisposable)?.Dispose(); stack.Pop(); continue; }
                        if (routine.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        yielded=routine.Current; break;
                    }
                }
                catch (Exception error) { LastReport["error"]=error.ToString(); throw; }
                if (stack.Count > 0) yield return yielded;
            }
            c.Check(native || bridge.Controller.OperationRevision == initialOperation, "Guard suite never starts a native body operation.");
            c.Check(model == null || model.Wires.Count == modelWires, "Confirmed-handoff fixture issues no language-model requests.");
            LastReport["status"]="passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            bridge.SceneTargetCompleted -= c.OnResult; c.arrived=null;
            Call(chat,"CancelSceneAction","entity-approach-fixture-finished"); Call(chat,"ClearScheduledAgentWake");
            c.ClearOccluder(); Set(chat,"m_EnableSceneObjectAwareness",awareness);
            Set(chat,"m_EnableSceneObjectTargetMatching",matching);
            Set(chat,"m_FormalResponseInFlight",formal); Set(chat,"m_AgentRoundInFlight",round);
            if ((string)LastReport["status"] == "running") LastReport["status"]="failed";
            c.Save();
        }
    }

    private static IEnumerator GuardCases(Context c)
    {
        yield return Prepare(c);
        CheckRecordedStationGeometry(c);
        SceneVisionObservation observation=null;
        yield return Capture(c,"committed-source",x=>observation=x);
        var selected=Choose(c,observation,false,out _);
        c.Check(selected != null && selected.VisibleConfirmed && selected.GeometryIsCurrent,"A real stable opaque room entity can be committed.");
        object anchor=NewAnchor(selected,c.bridge);
        c.Check(Validate(anchor)==null,"Committed entity is initially valid.");
        LastReport["entityId"]=selected.EntityId; LastReport["rootName"]=selected.Root.name;
        LastReport["sourceCaptureId"]=selected.CaptureId;
        Vector3 position=selected.Root.position; Quaternion rotation=selected.Root.rotation;
        Vector3 scale=selected.Root.localScale; Transform parent=selected.Root.parent;
        try
        {
            selected.Root.position=position+Vector3.right*.08f;
            c.Check((Validate(anchor)??"").Contains("target-moved"),"An 8 cm target displacement invalidates its committed waypoint.");
            selected.Root.position=position; selected.Root.rotation=rotation*Quaternion.Euler(0,8,0);
            c.Check((Validate(anchor)??"").Contains("target-moved"),"A changed target frame cannot keep its previous approach point.");
            selected.Root.rotation=rotation; selected.Root.localScale=scale*1.04f;
            c.Check((Validate(anchor)??"").Contains("target-moved"),"Target scale changes invalidate the committed geometry.");
        }
        finally { selected.Root.SetPositionAndRotation(position,rotation); selected.Root.localScale=scale; }
        c.Check(Validate(anchor)==null,"Restoring the exact target preserves the original commitment.");
        var avatar=c.bridge.avatar;
        try { c.bridge.avatar=null; c.Check(Validate(anchor)!=null,"A different room actor cannot inherit the committed reference."); }
        finally { c.bridge.avatar=avatar; }
        var environment=c.bridge.environmentRoot;
        try { c.bridge.environmentRoot=null; c.Check(Validate(anchor)!=null,"A changed environment invalidates the committed reference."); }
        finally { c.bridge.environmentRoot=environment; }
        try
        {
            selected.Root.gameObject.SetActive(false);
            c.Check(Validate(anchor)!=null,"A deactivated target cannot remain an executable approach reference.");
        }
        finally { selected.Root.gameObject.SetActive(true); }
        c.Check(selected.Root.parent==parent && Validate(anchor)==null,"All temporary target/binding changes are restored before any frame advances.");

        // This is an elapsed-time test, not a timestamp mutation. A route may legitimately outlive
        // its original picture while its exact physical commitment remains unchanged.
        while (Time.realtimeSinceStartup-observation.CapturedAtRealtime<12.4f) yield return null;
        c.Check(!selected.TryValidate(out _),"The actual source image expires and cannot authorize another action.");
        c.Check(Validate(anchor)==null,"Image age alone does not cancel a still-valid committed walk target.");
        yield return Capture(c,"fresh-same-entity",x=>observation=x);
        c.Check(c.chat.SceneVisionCapture.TryFindObservedObject(observation,selected.EntityId,out var current,out string reason) &&
            current.Root==selected.Root && current.VisibleConfirmed,"A real new image reidentifies the exact entity: "+reason);
        c.HideView();
        try
        {
            yield return Capture(c,"occluded-same-entity",x=>observation=x);
            c.Check(Validate(anchor)==null,"Temporary occlusion does not turn an unchanged commitment into continuous x-ray sight.");
            c.Check(!c.chat.SceneVisionCapture.TryFindObservedObject(observation,selected.EntityId,out _,out _),
                "The exact entity cannot be declared reobserved through a real foreground occluder.");
        }
        finally { c.ClearOccluder(); }
        c.Save();
    }

    private static IEnumerator NativeCases(Context c)
    {
        yield return Prepare(c);
        CheckRecordedStationGeometry(c);
        if (c.recordedRegion >= 0)
        {
            SceneVisionObservation lowSource = null;
            yield return Capture(c, "recorded-low-source", x => lowSource = x);
            var actual = ResolveRecordedRegion(c, lowSource, RecordedRegions[c.recordedRegion]);
            c.Check(actual != null && actual.VisibleConfirmed, "Recorded low furniture region must be actually visible from the initialized real room actor; no hidden target or substituted tall cabinet.");
            LastReport["recordedRegionIndex"] = c.recordedRegion;
            LastReport["recordedAction"] = RecordedActions[c.recordedRegion];
            LastReport["recordedRegion"] = JObject.Parse(JsonUtility.ToJson(RecordedRegions[c.recordedRegion]));
            LastReport["realSourcePoint"] = JObject.Parse(JsonUtility.ToJson(actual.WorldPoint));
            Vector3 rootBefore = c.bridge.avatar.transform.position;
            LastReport["initialRoot"] = JObject.Parse(JsonUtility.ToJson(rootBefore));
            yield return NativeAttempt(c, actual, lowSource, "recorded-low-furniture-arrival", false, true);
            c.Check(Horizontal(c.bridge.avatar.transform.position - rootBefore) > .35f,
                "Recorded low furniture test actually walks from the initialized logged start, without runtime relocation.");
            yield break;
        }
        SceneVisionObservation observation=null;
        yield return Capture(c,"native-source",x=>observation=x);
        SceneObservedObject selected=Choose(c,observation,true,out var plan);
        c.Check(selected!=null && plan!=null,"A real visible stable referent has a legal nearby >0.5 m route in the existing room.");
        LastReport["initialRouteLength"]=plan.Route.Length; LastReport["entityId"]=selected.EntityId;
        Vector3 initialRoot=c.bridge.avatar.transform.position;
        yield return NativeAttempt(c,selected,observation,"same-entity-arrival",false,true);
        c.Check(Horizontal(c.bridge.avatar.transform.position-initialRoot)>.4f,"First attempt actually moves the avatar to the object floor station.");
        yield return Prepare(c);
        yield return Capture(c,"occlusion-attempt-source",x=>observation=x);
        c.Check(c.chat.SceneVisionCapture.TryFindObservedObject(observation,selected.EntityId,out selected,out string reason),
            "Same real entity is visible before a second bounded attempt: "+reason);
        yield return NativeAttempt(c,selected,observation,"ground-arrived-but-hidden",true,false);
    }

    private static IEnumerator NativeAttempt(Context c,SceneObservedObject selected,SceneVisionObservation source,
        string name,bool hideOnArrival,bool expectVisible)
    {
        string actionId="entity-approach-regression-"+Guid.NewGuid().ToString("N");
        var attention=(NevaSceneAttention)Get(c.chat,"m_SceneAttention");
        if (attention==null) { attention=c.chat.GetComponent<NevaSceneAttention>()??c.chat.gameObject.AddComponent<NevaSceneAttention>(); Set(c.chat,"m_SceneAttention",attention); }
        if (attention.Target!=c.tracking.Target) attention.Configure(c.tracking);
        c.Check(attention.TryLookAt(actionId,selected.WorldPoint,out string reason,5),"Real preparatory head look: "+reason);
        yield return Wait(c,()=>attention.IsSettled,5,"actual head settles before confirming the entity");
        SceneVisionObservation confirmed=null;
        yield return Capture(c,name+"-confirmed",x=>confirmed=x);
        bool visible=c.chat.SceneVisionCapture.TryFindObservedObject(confirmed,selected.EntityId,out var actual,out reason);
        c.Check(confirmed.CaptureId!=source.CaptureId && visible && actual.Root==selected.Root,
            "Prepared new image confirms this same real entity: "+reason);
        int epoch=(int)Get(c.chat,"m_SceneActionEpoch")+1,generation=(int)Get(c.chat,"m_FormalResponseGeneration");
        object fact=Activator.CreateInstance(typeof(ChatSample).GetNestedType("SceneActionFact",Flags),true);
        Set(fact,"actionId",actionId); Set(fact,"intent","approach-object"); Set(fact,"origin","user");
        Set(fact,"captureId",source.CaptureId); Set(fact,"confirmedCaptureId",confirmed.CaptureId);
        Set(fact,"entityId",actual.EntityId); Set(fact,"objectConfirmed",true);
        Set(fact,"intentFocusWorldPoint",selected.WorldPoint); Set(fact,"hasIntentFocus",true);
        Set(fact,"targetPurpose","observe-closer"); Set(fact,"targetDescription","本测试从实际视图选出的稳定参照表面");
        Set(fact,"semanticTargetConfirmed",true); Set(fact,"targetMatchVerdict","match");
        Set(fact,"targetMatchReason","Controlled precondition for navigation regression, not a model semantic result.");
        Set(c.chat,"m_SceneActionFact",fact); Set(c.chat,"m_SceneActionEpoch",epoch); Set(c.chat,"m_SceneActionActive",true);
        Call(c.chat,"RecordSceneAction",fact,"confirmed","controlled semantic precondition; actual same-entity capture acquired");
        int previousOperation=c.bridge.Controller.OperationRevision;
        var decision=new RoomTaskProtocol.Decision { intent="approach-object",origin="user",evidence="靠近所选参照观察。" };
        bool completed=false;
        if (hideOnArrival) c.arrived=value=> { if (value.actionId==actionId && value.reached && value.status=="arrived") c.HideView(); };
        yield return (IEnumerator)Call(c.chat,"SubmitConfirmedSceneObjectApproach",decision,actual,confirmed,fact,c.bridge,generation,epoch,new Action(()=>completed=true));
        c.Check(completed && c.bridge.Controller.OperationRevision>previousOperation,"Confirmed approach hands off to an actual native controller operation.");
        Vector3 prior=c.bridge.avatar.transform.position; float priorAt=Time.realtimeSinceStartup,deadline=priorAt+65;
        float maxRootStep=0, maximumWalkingHeadFocus=0, maximumActualAppliedDegrees=0, lastGazeSample=-100;
        int walkingAppliedFrames=0; var walkingGaze=new JArray();
        while ((bool)Get(c.chat,"m_SceneActionActive"))
        {
            c.Check(Time.realtimeSinceStartup<deadline,"Actual entity approach did not complete: "+Fact(c.chat));
            Vector3 current=c.bridge.avatar.transform.position; float dt=Mathf.Max(.001f,Time.realtimeSinceStartup-priorAt);
            float step=Vector3.Distance(current,prior); maxRootStep=Mathf.Max(step,maxRootStep);
            c.Check(step<Mathf.Max(.15f,dt*2.5f),"Native body root remains continuous, without teleporting to a nominal arrival.");
            if (c.bridge.Controller.IsMoving && c.bridge.Controller.Player != null && c.bridge.Controller.Player.IsPlaying)
            {
                bool focusActive=(bool)attention.GetType().GetProperty("LocomotionFocusActive").GetValue(attention);
                int lastApplied=(int)attention.GetType().GetProperty("LocomotionFocusLastAppliedFrame").GetValue(attention);
                int appliedFrames=(int)attention.GetType().GetProperty("LocomotionFocusAppliedFrames").GetValue(attention);
                if (focusActive && lastApplied>=Time.frameCount-2 && appliedFrames>0)
                {
                    c.Check(Mathf.Abs(attention.CurrentHeadAngles.x) <= attention.maximumYawDegrees + .05f &&
                        attention.CurrentHeadAngles.y >= -attention.maximumDownDegrees - .05f &&
                        attention.CurrentHeadAngles.y <= attention.maximumUpDegrees + .05f,
                        "Native moving focus preserves original yaw/up/down bone limits, even with an off-centre visible region.");
                    walkingAppliedFrames++; maximumWalkingHeadFocus=Mathf.Max(maximumWalkingHeadFocus,attention.CurrentHeadAngles.magnitude);
                    maximumActualAppliedDegrees=Mathf.Max(maximumActualAppliedDegrees,
                        (float)attention.GetType().GetProperty("LocomotionFocusAppliedDegrees").GetValue(attention));
                }
                if (Time.realtimeSinceStartup-lastGazeSample>.07f)
                {
                    lastGazeSample=Time.realtimeSinceStartup;
                    walkingGaze.Add(new JObject { ["frame"]=Time.frameCount,["focusActive"]=focusActive,
                        ["lastAppliedFrame"]=lastApplied,["appliedFrames"]=appliedFrames,
                        ["actualHeadFrameRotation"]=JObject.Parse(JsonUtility.ToJson(c.tracking.HeadFrameRotation)),
                        ["headAngles"]=JObject.Parse(JsonUtility.ToJson(attention.CurrentHeadAngles)),
                        ["root"]=JObject.Parse(JsonUtility.ToJson(current)),["captureId"]=Fact(c.chat)["trackingCaptureId"],
                        ["visibility"]=Fact(c.chat)["trackingVisibility"] });
                }
            }
            prior=current; priorAt=Time.realtimeSinceStartup; yield return null;
        }
        c.arrived=null;
        var final=Fact(c.chat); var events=c.results.Where(value=>value.actionId==actionId).ToArray();
        LastReport["lastAttempt"] = new JObject { ["name"] = name, ["actionId"] = actionId,
            ["fact"] = final.DeepClone(), ["terminalEvents"] = new JArray(events.Select(x => JObject.Parse(JsonUtility.ToJson(x)))),
            ["walkingAppliedFrames"] = walkingAppliedFrames, ["maximumRootStep"] = maxRootStep };
        c.Save();
        c.Check(events.Length==1 && events[0].reached && events[0].status=="arrived" &&
            events[0].targetId==actual.EntityId && events[0].captureId==confirmed.CaptureId,
            "Exact controller attempt publishes one actual floor-arrival receipt for this entity.");
        c.Check((bool?)final["groundArrived"]==true && (bool?)final["reached"]==true &&
            (bool?)final["objectReobserved"]==expectVisible &&
            (string)final["phase"]==(expectVisible?"arrived":"arrived-object-unconfirmed"),
            "Ground arrival and actual same-entity reobservation have distinct truthful outcomes: "+final);
        c.Check((string)final["entityId"]==actual.EntityId && (string)final["resultCaptureId"]!=confirmed.CaptureId &&
            !string.IsNullOrEmpty((string)final["resultCaptureId"]),"Post-arrival result uses a distinct real image and retains the exact target identity.");
        c.Check((bool?)final["stateChanged"]!=true && (string)final["targetPurpose"]=="observe-closer",
            "Closer observation of this referent does not claim a device operation or identification of a smaller nearby object.");
        c.Check(Horizontal((Vector3)Get(fact,"measuredSurface")-(Vector3)Get(fact,"intentFocusWorldPoint"))<=.5001f,
            "The actual navigation focus stays within 0.5 m XZ of the intended object region.");
        if (expectVisible)
        {
            c.Check(walkingAppliedFrames>5 && maximumWalkingHeadFocus>.05f && maximumActualAppliedDegrees>.05f,
                "Walking applies actual bounded native head focus across multiple rendered frames, not only a lease field.");
            c.Check((int?)final["trackingMovingConfirmedImages"]>=2,
                "Multiple actual local images confirm the same target while the body is moving.");
        }
        if (!expectVisible) c.Check((string)final["postArrivalObservationStatus"]=="same-entity-not-visible",
            "Hidden target outcome comes from actual new-image visibility, not an unrelated failure.");
        ((JArray)LastReport["cases"]).Add(new JObject { ["name"]=name,["fact"]=final,["maximumRootStep"]=maxRootStep,
            ["terminal"]=JObject.Parse(JsonUtility.ToJson(events[0])),["walkingAppliedFrames"]=walkingAppliedFrames,
            ["maximumWalkingHeadFocusDegrees"]=maximumWalkingHeadFocus,["maximumActualAppliedDegrees"]=maximumActualAppliedDegrees,["walkingGaze"]=walkingGaze });
        var latest=c.chat.SceneVisionCapture.LatestObservation;
        c.Check(latest.CaptureId==(string)final["resultCaptureId"],"Exported arrival image matches the receipt capture exactly.");
        SaveCapture(c,name+"-arrival",latest); c.ClearOccluder(); c.Save();
        yield return Wait(c,()=>!c.bridge.ReservesBody,5,"actual native body releases");
    }

    private static void CheckRecordedStationGeometry(Context c)
    {
        Vector3 neutral = (Vector3)Call(c.chat, "SceneObjectNeutralEyeOffset", c.bridge, c.tracking);
        Quaternion original = c.tracking.Head.localRotation;
        try
        {
            // Within the same test frame, prove the planning reference does not inherit an
            // already turned head. Restore before advancing/rendering; this is not a gaze test.
            c.tracking.Head.localRotation = original * Quaternion.Euler(18f, 32f, 0f);
            Vector3 turned = (Vector3)Call(c.chat, "SceneObjectNeutralEyeOffset", c.bridge, c.tracking);
            c.Check(Vector3.Distance(neutral, turned) < .0002f,
                "Neutral planning eye is invariant to the current look-at head offset.");
        }
        finally { c.tracking.Head.localRotation = original; }
        float slack = (float)Call(c.chat, "SceneObjectViewSlack", 56f);
        c.Check(slack <= 8f && (float)Call(c.chat, "SceneObjectViewSlack", 12f) == 0f,
            "Inner view allowance never widens the camera and vanishes for a narrow actual field of view.");
        var rows = new JArray();
        for (int i = 0; i < RecordedRegions.Length; i++)
        {
            // Logs label -.69721955 as avatarGround, not root. The original root stayed -.72;
            // its imported foot offset is +.02278045. Reusing ground as root invents 2.28 cm height.
            float rootY = -.72f;
            float groundY = rootY + c.bridge.importedReference.groundY * c.bridge.avatar.transform.lossyScale.y;
            var point = RecordedRegions[i];
            float required = (float)Call(c.chat, "SceneObjectObservationDistance", point, groundY, neutral,
                25f, 30f, slack, c.bridge.Controller.ArrivalTolerance);
            float previous = Mathf.Abs(point.y - groundY - neutral.y) / Mathf.Tan(23f * Mathf.Deg2Rad) +
                Horizontal(neutral) + c.bridge.Controller.ArrivalTolerance + .05f;
            c.Check(required <= 2f, "Recorded low furniture " + RecordedActions[i] + " fits the bounded camera view without widening head rotation or 2 m search.");
            Vector3 ground = new Vector3(point.x, groundY, point.z - required);
            float pitch = (float)Call(c.chat, "SceneObjectStationPitch", point, ground, neutral, c.bridge.Controller.ArrivalTolerance);
            c.Check(Mathf.Abs(pitch) <= 27f + slack + .01f, "Predicted station retains the focus in the inner camera view with native 30 degree head limit.");
            rows.Add(new JObject { ["action"] = RecordedActions[i], ["point"] = JObject.Parse(JsonUtility.ToJson(point)),
                ["oldRequiredDistance"] = previous, ["newRequiredDistance"] = required, ["predictedPitch"] = pitch,
                ["oldGeometryWouldReject"] = previous > 2f, ["scope"] = "Recorded-world-point geometry with this actual imported rig; not a current-visibility or route claim." });
        }
        LastReport["recordedStationGeometry"] = rows;
        LastReport["neutralEyeOffset"] = JObject.Parse(JsonUtility.ToJson(neutral));
        c.Check((float)Call(c.chat, "SceneObjectObservationDistance", new Vector3(0, -4f, 0), -.72f, neutral,
            25f, 30f, slack, c.bridge.Controller.ArrivalTolerance) > 2f,
            "A genuinely excessive vertical disparity is still rejected by the bounded station geometry.");
    }

    private static SceneObservedObject ResolveRecordedRegion(Context c, SceneVisionObservation observation, Vector3 recorded)
    {
        Vector3 local = Quaternion.Inverse(observation.CameraRotation) * (recorded - observation.CameraPosition);
        if (local.z <= .025f) return null;
        float tangent = Mathf.Tan(observation.VerticalFov * Mathf.Deg2Rad * .5f);
        Vector2 pixel = new Vector2(.5f + local.x / (2f * local.z * tangent * observation.Width / observation.Height),
            .5f - local.y / (2f * local.z * tangent));
        SceneObservedObject best = null; float nearest = .25f;
        // The historical point selects a small search window only. Every admitted point must
        // be actually opaque and same-frame; recorded coordinates never create a visual target.
        for (int y = -8; y <= 8; y++) for (int x = -8; x <= 8; x++)
        {
            Vector2 candidate = pixel + new Vector2(x * .005f, y * .005f);
            if (!c.chat.SceneVisionCapture.TryResolveObservedObject(observation, candidate, out var seen, out _) ||
                !seen.VisibleConfirmed || !seen.GeometryIsCurrent) continue;
            float distance = Vector3.Distance(seen.WorldPoint, recorded);
            if (distance >= nearest) continue;
            nearest = distance; best = seen;
        }
        return best;
    }

    private static SceneObservedObject Choose(Context c,SceneVisionObservation observation,bool requireRoute,out ArdyRoomObjectApproachPlan chosenPlan)
    {
        chosenPlan=null;
        Vector3 ground=c.bridge.avatar.transform.position+Vector3.up*c.bridge.importedReference.groundY*c.bridge.avatar.transform.lossyScale.y;
        var tried=new List<SceneObservedObject>();
        foreach(float y in new[]{.50f,.42f,.58f,.35f,.65f}) foreach(float x in new[]{.62f,.38f,.70f,.30f,.50f,.78f,.22f})
        {
            if (!c.chat.SceneVisionCapture.TryResolveObservedObject(observation,new Vector2(x,y),out var candidate,out _) ||
                !candidate.VisibleConfirmed || !candidate.GeometryIsCurrent || candidate.Root==null ||
                candidate.Root.GetComponentInParent<Animator>()!=null || candidate.Root.GetComponentInChildren<Animator>()!=null ||
                candidate.Root==c.bridge.environmentRoot) continue;
            if (!requireRoute) return candidate;
            Vector3 offset=candidate.WorldPoint-c.tracking.EyeOrigin,local=Quaternion.Inverse(c.tracking.HeadFrameRotation)*offset;
            float yaw=Mathf.Atan2(local.x,local.z)*Mathf.Rad2Deg;
            float pitch=Mathf.Atan2(local.y,new Vector2(local.x,local.z).magnitude)*Mathf.Rad2Deg;
            if (candidate.WorldPoint.y<ground.y+.6f || candidate.WorldPoint.y>ground.y+2.1f ||
                offset.magnitude<1.15f || offset.magnitude>5 || Mathf.Abs(yaw)>38 || pitch< -20 || pitch>18) continue;
            if (tried.Any(v=>v.EntityId==candidate.EntityId&&Vector3.Distance(v.WorldPoint,candidate.WorldPoint)<.35f)) continue;
            tried.Add(candidate); if (tried.Count>16) return null;
            if (!ArdyRoomObjectApproachPlanner.TryPlan(c.bridge.Navigation,ground,candidate.WorldPoint,c.bridge.userHead,.8f,
                out var plan,out _,out _) || plan.Route.Length<.5f || plan.Route.Length>3.5f ||
                Horizontal(plan.GroundTarget-ground)<.45f) continue;
            chosenPlan=plan; return candidate;
        }
        return null;
    }
    private static object NewAnchor(SceneObservedObject target,ArdyRoomDialogueBridge bridge)
        => Activator.CreateInstance(typeof(ChatSample).GetNestedType("SceneObjectApproachAnchor",Flags),Flags,null,new object[]{target,bridge},null);
    private static string Validate(object anchor)=>(string)Call(anchor,"Validate");
    private static IEnumerator Prepare(Context c)
    {
        Call(c.chat,"CancelSceneAction","entity-approach-next-case"); Call(c.chat,"ClearScheduledAgentWake");
        yield return Wait(c,()=>!((NevaSceneAttention)Get(c.chat,"m_SceneAttention") is NevaSceneAttention a)||!a.IsBusy,5,"previous attention releases");
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound",Flags).Invoke(null,new object[]{c.chat});
        Set(c.chat,"m_EnableSceneObjectAwareness",true);
        Set(c.chat,"m_FormalResponseInFlight",true); Set(c.chat,"m_AgentRoundInFlight",true);
    }
    private static IEnumerator Capture(Context c,string name,Action<SceneVisionObservation> completed)
    {
        SceneVisionObservation observation=null;
        Call(c.chat,"RequestSceneObservation",name,new Action<SceneVisionObservation>(value=>observation=value));
        yield return Wait(c,()=>observation!=null,5,"actual scene RGB/ID image: "+name);
        c.Check(observation.HasImage&&observation.HasSurfaceGrounding,"Capture has actual RGB with same-frame ID geometry.");
        SaveCapture(c,name,observation); completed(observation);
    }
    private static void SaveCapture(Context c,string name,SceneVisionObservation observation)
    {
        File.WriteAllBytes(Path.Combine(c.output,name+".jpg"),Convert.FromBase64String(observation.ImageDataUrl.Substring(observation.ImageDataUrl.IndexOf(',')+1)));
        File.WriteAllText(Path.Combine(c.output,name+".metadata.json"),observation.MetadataJson);
    }
    private static IEnumerator Wait(Context c,Func<bool> ready,float seconds,string description)
    { float until=Time.realtimeSinceStartup+seconds; while(!ready()) { c.Check(Time.realtimeSinceStartup<until,"Timeout: "+description); yield return null; } }
    private static float Horizontal(Vector3 value)=>new Vector2(value.x,value.z).magnitude;
    private static JObject Fact(ChatSample chat)=>Get(chat,"m_SceneActionFact") is object value?JObject.Parse(JsonUtility.ToJson(value)):new JObject();
    private static object Call(object target,string name,params object[] args)=>NevaSceneVisionIntegrationRegression.Call(target,name,args);
    private static object Get(object target,string name)=>target.GetType().GetField(name,Flags).GetValue(target);
    private static void Set(object target,string name,object value)=>target.GetType().GetField(name,Flags).SetValue(target,value);
}
