using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Real imported humanoid/control rig, controlled synthetic pose and owner adoption.
/// No ARDY/Qwen request or navigation claim. Native scene-approach checks exercise real motion separately.</summary>
public static class NevaLocomotionFocusRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static JObject LastReport { get; private set; }
    private sealed class Context : IDisposable
    {
        public GameObject host, actor;
        public Vrm10Instance avatar;
        public ArdyLocomotionPlayer player;
        public ArdyRoomLocomotionController controller;
        public NevaEyeTracking eyes;
        public NevaSceneAttention attention;
        public ArdyLocomotionClip clip;
        public int checks;
        public readonly JArray evidence = new JArray();
        public void Check(bool valid, string message)
        { checks++; if (!valid) throw new InvalidOperationException(message); }
        public Vector3 Point(float yaw, float pitch = 0f) => eyes.EyeOrigin +
            avatar.transform.rotation * Quaternion.Euler(-pitch, yaw, 0) * Vector3.forward * 3f;
        public void Adopt(int operation)
        {
            Set(controller, "revision", operation); Set(controller, "<OperationRevision>k__BackingField", operation);
            Set(controller, "ownedClip", player.Clip); Set(controller, "ownedPlayerRevision", player.PlaybackRevision);
        }
        public void Dispose()
        {
            if (attention != null) attention.Cancel("regression cleanup");
            if (player != null) player.Stop();
            if (host != null) Object.DestroyImmediate(host);
        }
    }

    public static IEnumerator RunChecks(string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Locomotion-focus checks only run in the existing isolated Play fixture.");
        string directory = Path.Combine(output, "locomotion-focus"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["status"]="running", ["checks"]=0, ["modelHttpRequests"]=0, ["nativeHttpRequests"]=0,
            ["scope"]="A temporary copy of the real imported NeEEvA VRM and the actual head transform bound by the production player (direct Humanoid or normalized ControlRig, recorded separately), with scheduled Unity frames. A synthetic absolute body pose and explicitly controlled controller adoption test head-layer ownership, limits, real head/eye transfer, expiration and stopping. This does not certify target visibility, model semantics or generated gait quality." };
        var c = new Context(); var stack = new Stack<IEnumerator>();
        try
        {
            Build(c); stack.Push(Cases(c));
            while (stack.Count > 0)
            {
                object yielded = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var top = stack.Peek();
                        if (!top.MoveNext()) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                        if (top.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        yielded = top.Current; break;
                    }
                }
                catch (Exception error)
                { LastReport["status"]="failed"; LastReport["error"]=error.ToString(); throw; }
                if (stack.Count > 0) yield return yielded;
            }
            LastReport["status"]="passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            LastReport["checks"]=c.checks; LastReport["evidence"]=c.evidence;
            File.WriteAllText(Path.Combine(directory,"report.json"), LastReport.ToString()+"\n"); c.Dispose();
        }
    }

    private static void Build(Context c)
    {
        const string path="Assets/Model/NeEEvA.vrm";
        var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);
        c.Check(prefab!=null,"Actual imported avatar exists.");
        var reference=ArdyLocomotionAvatarReference.FromImportedPrefab(prefab.GetComponentInChildren<Vrm10Instance>(true),path);
        c.host=new GameObject("Isolated controlled locomotion-focus rig"); c.host.SetActive(false);
        c.actor=Object.Instantiate(prefab,c.host.transform);
        foreach (var behaviour in c.actor.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled=false;
        c.avatar=c.actor.GetComponentInChildren<Vrm10Instance>(true);
        c.avatar.transform.SetPositionAndRotation(new Vector3(17f,0f,13f),Quaternion.Euler(0f,37f,0f));
        c.avatar.UpdateType=Vrm10Instance.UpdateTypes.LateUpdate;
        c.eyes=c.avatar.GetComponent<NevaEyeTracking>()??c.avatar.gameObject.AddComponent<NevaEyeTracking>();
        c.eyes.Configure(c.avatar); c.eyes.enabled=true;
        c.attention=c.avatar.GetComponent<NevaSceneAttention>()??c.avatar.gameObject.AddComponent<NevaSceneAttention>();
        c.attention.Configure(c.eyes); c.attention.enabled=true;
        c.controller=c.avatar.gameObject.AddComponent<ArdyRoomLocomotionController>();
        c.player=c.avatar.gameObject.AddComponent<ArdyLocomotionPlayer>();
        c.avatar.enabled=true; c.host.SetActive(true); c.player.Bind(c.avatar,reference);
        var headBinding=c.player.GetPoseRecordingBindings().Single(value=>value.human==HumanBodyBones.Head);
        LastReport["headBindingMode"]=headBinding.drivesControlRig?"normalized-ControlRig":"direct-Humanoid";
        LastReport["boundHeadTransform"]=headBinding.drivenTransform;
        Set(c.controller,"avatar",c.avatar); Set(c.controller,"player",c.player);
        Set(c.controller,"reference",reference); Set(c.controller,"boundVrmEnabled",true);
        c.clip=ControlledPose();
    }

    private static IEnumerator Cases(Context c)
    {
        c.Check(!c.attention.TryLookAt("stationary-with-vrm",c.Point(20),out _),"Ordinary stationary attention still yields to full VRM.");
        c.Check(!c.attention.TryBeginLocomotionFocus("gaze",c.Point(20),c.controller,1,out _),"A controller without an owned moving clip cannot grant focus.");
        c.player.Play(c.clip); c.Adopt(1);
        c.Check(c.controller.OwnsActivePlayerPose,"Controlled fixture adopts the exact player/clip/revision.");
        c.Check(!c.attention.TryBeginLocomotionFocus("gaze",c.Point(20),c.controller,2,out _),"Wrong operation cannot grant gaze.");
        c.Check(!c.attention.TryBeginLocomotionFocus("gaze",new Vector3(float.NaN,0,0),c.controller,1,out _),"Nonfinite gaze rejected.");
        c.Check(!c.eyes.TrySetLocomotionAttentionTarget("forged",c.Point(20),c.player),"An eye request cannot manufacture a player lease.");
        Vector3 userPoint=c.Point(0); var user=new GameObject("Unchanged authored gaze"); user.transform.SetParent(c.host.transform);
        user.transform.position=userPoint; c.eyes.gazeTarget=user.transform;
        Vector3 focus=c.Point(30,8); Quaternion root=c.avatar.transform.rotation;
        c.Check(c.attention.TryBeginLocomotionFocus("gaze",focus,c.controller,1,out string reason),"Exact focus starts: "+reason);
        c.Check(!c.attention.ReleaseLocomotionFocus("old"),"Old action cannot release current focus.");
        float deadline=Time.realtimeSinceStartup+.9f; Vector2 previous=Vector2.zero; int applied=c.player.SceneFocusAppliedFrames;
        float largestActualHead=0, largestStep=0;
        Quaternion actualStart=c.eyes.Head.rotation;
        while(Time.realtimeSinceStartup<deadline)
        {
            yield return new WaitForEndOfFrame();
            c.Check(c.attention.LocomotionFocusActive,"Lease remains active during bounded acquisition.");
            if(c.player.SceneFocusLastAppliedFrame!=Time.frameCount) continue;
            float step=Vector2.Distance(previous,c.player.SceneFocusAngles); largestStep=Mathf.Max(largestStep,step);
            c.Check(step<=c.attention.maximumDegreesPerSecond*Mathf.Clamp(Time.unscaledDeltaTime,0,.05f)+.15f,
                "Gaze angular speed is bounded per rendered frame.");
            previous=c.player.SceneFocusAngles;
            largestActualHead=Mathf.Max(largestActualHead,Quaternion.Angle(actualStart,c.eyes.Head.rotation));
            c.Check(Quaternion.Angle(root,c.avatar.transform.rotation)<.05f,"Gaze never rotates the root.");
        }
        c.Check(c.player.SceneFocusAppliedFrames-applied>=3 && largestActualHead>10f,"Multiple actual rig overlays reach the displayed humanoid head.");
        c.Check(c.attention.AngularErrorDegrees<5f,"Calibrated rendered head aims at the real target, not only lease metadata.");
        c.Check(c.eyes.gazeTarget==user.transform && c.eyes.AttentionOwnerId=="gaze","Eye lease preserves authored gaze target.");
        c.evidence.Add(new JObject{["case"]="real-rig-transfer",["actualHeadChange"]=largestActualHead,["maximumLayerStep"]=largestStep,
            ["targetError"]=c.attention.AngularErrorDegrees,["appliedFrames"]=c.player.SceneFocusAppliedFrames-applied});

        c.Check(c.attention.UpdateLocomotionFocusPoint("gaze",c.Point(130,70),c.controller,1,out reason),"Current-owner point updates: "+reason);
        yield return Wait(.6f);
        c.Check(Mathf.Abs(c.player.SceneFocusAngles.x)<=55.01f && c.player.SceneFocusAngles.y<=30.01f,"Behind/high targets never force unbounded head motion.");
        c.Check(!c.attention.UpdateLocomotionFocusPoint("old",focus,c.controller,1,out _),"Old owner cannot update gaze.");
        c.Check(!c.attention.UpdateLocomotionFocusPoint("gaze",focus,c.controller,2,out _),"Wrong operation cannot renew the lease.");
        c.Check(c.attention.ReleaseLocomotionFocus("gaze","target not visible"),"Visibility loss immediately releases target attention.");
        c.Check(!c.attention.LocomotionFocusActive && c.eyes.AttentionOwnerId==null,"Released target is no longer reported actively watched.");
        c.Check(!c.attention.TryBeginLocomotionFocus("foreign-tail-owner",focus,c.controller,1,out _),
            "An unrelated action cannot take over the old operation's still-returning head layer.");
        c.Check(c.attention.TryBeginLocomotionFocus("gaze",focus,c.controller,1,out reason),
            "Fresh confirmation for the same action can reacquire during its smooth return: "+reason);
        c.Check(c.attention.ReleaseLocomotionFocus("gaze","controlled second visibility loss"),"Same action can release the reacquired layer.");
        yield return WaitForFocusReturn(c,"explicit-release");

        c.Check(c.attention.TryBeginLocomotionFocus("expiry",focus,c.controller,1,out reason),"Fresh observation can reacquire: "+reason);
        yield return Wait(1.7f);
        c.Check(!c.attention.LocomotionFocusActive,"A caller cannot sustain focus indefinitely without visibility refresh.");
        c.Check(!c.attention.UpdateLocomotionFocusPoint("expiry",focus,c.controller,1,out _),"Expired point update cannot silently revive old permission.");
        yield return WaitForFocusReturn(c,"lease-expiry");

        c.Check(c.attention.TryBeginLocomotionFocus("cancel",focus,c.controller,1,out reason),"Fresh lease before a real smooth stop: "+reason);
        yield return Wait(.6f);
        yield return new WaitForEndOfFrame();
        // Scene-instantiated VRM prefabs may legitimately have no ControlRig. Inspect the
        // exact transform the player owns instead of creating or assuming a different rig.
        var ownedHead=((IEnumerable)Get(c.player,"bones")).Cast<object>()
            .Single(value=>(HumanBodyBones)Get(value,"human")==HumanBodyBones.Head);
        var rigHead=(Transform)Get(ownedHead,"transform");
        c.Check(rigHead!=null,"The production player has an actual bound head transform.");
        Quaternion lastRig=rigHead.localRotation, lastActual=c.eyes.Head.rotation;
        c.Check(c.player.SceneFocusLastAppliedDegrees>5f,"Stop begins from an actually rotated gaze pose.");
        c.Check(c.player.RequestSmoothStop(.4f),"The production body smooth-stop path starts.");
        c.Check(Quaternion.Angle(lastRig,rigHead.localRotation)<.05f && Quaternion.Angle(lastActual,c.eyes.Head.rotation)<.05f,
            "Stopping does not reset either rig or rendered gaze before handoff.");
        var from=(IEnumerable)Get(c.player,"returnFrom"); bool found=false;
        foreach(var pose in from)
            if((Transform)Get(pose,"transform")==rigHead)
            { found=true; c.Check(Quaternion.Angle((Quaternion)Get(pose,"rotation"),lastRig)<.05f,"Return captures the actual last gaze rig pose."); }
        c.Check(found,"Head is included in the body handoff.");
        c.Check(!c.player.SceneFocusActive && !c.eyes.TrySetLocomotionAttentionTarget("cancel",focus,c.player),"A stopping player no longer authorizes active eyes.");
        yield return Wait(.65f);
        c.Check(!c.player.HasPoseOwnership && c.player.ReturnedToAnimation,"Existing body return completes without a second focus overlay.");

        c.player.Play(c.clip); c.Adopt(7);
        c.Check(c.attention.TryBeginLocomotionFocus("replace",focus,c.controller,7,out reason),"Another exact operation can acquire focus: "+reason);
        yield return Wait(.2f);
        Set(c.controller,"revision",8);
        c.Check(!c.player.SceneFocusActive,"Controller cancellation immediately invalidates the lease.");
        yield return new WaitForEndOfFrame();
        c.Check(c.eyes.AttentionOwnerId==null && !c.attention.LocomotionFocusActive,"Cancelled operation cannot keep eye/head attention ownership.");
        c.Adopt(9); c.Check(c.attention.TryBeginLocomotionFocus("revision",focus,c.controller,9,out reason),"New operation can start with fresh permission: "+reason);
        Set(c.controller,"ownedPlayerRevision",c.player.PlaybackRevision-1);
        c.Check(!c.player.SceneFocusActive,"A replaced clip revision invalidates focus even on the same player.");
        yield return new WaitForEndOfFrame();
        c.Check(!c.attention.LocomotionFocusActive && c.eyes.gazeTarget==user.transform,"Ownership loss preserves authored user gaze.");
    }

    private static IEnumerator Wait(float seconds)
    { float until=Time.realtimeSinceStartup+seconds; while(Time.realtimeSinceStartup<until) yield return null; }
    private static IEnumerator WaitForFocusReturn(Context c,string phase)
    {
        float started=Time.realtimeSinceStartup, deadline=started+2f;
        while(!string.IsNullOrEmpty(c.player.SceneFocusOwnerId) && Time.realtimeSinceStartup<deadline) yield return null;
        c.Check(string.IsNullOrEmpty(c.player.SceneFocusOwnerId) && c.player.SceneFocusAngles.magnitude<.08f,
            "The actual player-owned return completes before a different action is admitted: "+phase);
        c.Check(c.player.IsPlaying && !c.attention.LocomotionFocusActive && c.eyes.AttentionOwnerId==null,
            "Returning attention releases only its layer while the body continues: "+phase);
        c.evidence.Add(new JObject{["case"]="focus-layer-return",["trigger"]=phase,
            ["waitForActualReleaseSeconds"]=Time.realtimeSinceStartup-started,["finalAngles"]=c.player.SceneFocusAngles.magnitude});
    }
    private static ArdyLocomotionClip ControlledPose()
    {
        const int count=301;
        var clip=new ArdyLocomotionClip{schema=1,id="controlled-focus-pose",sourceRootHeight=1,
            frames=new ArdyLocomotionFrame[count],targets=new ArdyLocomotionTarget[count],
            source=new ArdyLocomotionSource{coordinateSystem="unity-lh-y-up-z-forward",generation="synthetic-regression-only"},
            rotationClip=new ArdyMotionClip{schema=1,id="controlled-focus-pose",text="Controlled pose, no model inference.",fps=20,
                jointNames=(string[])ArdyLiveProtocol.JointNames.Clone(),jointParents=(int[])ArdyLiveProtocol.JointParents.Clone(),
                restGlobalRotations=Enumerable.Repeat(Quaternion.identity,27).ToArray(),frames=new ArdyMotionFrame[count],
                source=new ArdyMotionSource{coordinateSystem="unity-lh-y-up-z-forward",rootTranslationApplied=false}}};
        for(int i=0;i<count;i++)
        {
            clip.frames[i]=new ArdyLocomotionFrame{rootPosition=new Vector3(0,1,0),leftFootContact=1,rightFootContact=1};
            clip.targets[i]=new ArdyLocomotionTarget{rootPosition=Vector3.zero};
            clip.rotationClip.frames[i]=new ArdyMotionFrame{globalRotations=Enumerable.Repeat(Quaternion.identity,27).ToArray()};
        }
        clip.Validate(); return clip;
    }
    private static object Get(object target,string name)=>target.GetType().GetField(name,Flags).GetValue(target);
    private static void Set(object target,string name,object value)=>target.GetType().GetField(name,Flags).SetValue(target,value);
}
