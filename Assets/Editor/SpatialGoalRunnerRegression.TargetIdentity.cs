using System;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class SpatialGoalRunnerRegression
{
    private static void CheckTargetIdentityReuse(ChatSample chat)
    {
        var lease = Get(chat,"m_SceneTargetIdentity");
        if (lease == null) return;
        var observed = (SceneObservedObject)Get(lease,"observed");
        var observation = (SceneVisionObservation)typeof(SceneObservedObject).GetField("observation",Flags).GetValue(observed);
        var request = new JObject{["intent"]="inspect-object",["origin"]="user",["evidence"]=(string)Get(lease,"request"),
            ["entityTarget"]=new JObject{["captureId"]=observed.CaptureId,["entityId"]=observed.EntityId}};
        if (!RoomTaskProtocol.TryDecision(request.ToString(),(string)Get(lease,"request"),false,out var decision)) return;
        var method=typeof(ChatSample).GetMethod("TryReuseSceneTargetIdentity",Flags);
        bool Reuse() => (bool)method.Invoke(chat,new[]{(object)decision,observation,observed,Activator.CreateInstance(method.GetParameters()[3].ParameterType,true)});
        if (!Reuse()) return; // The actual current image/goal must still qualify.
        int count=0;
        foreach(string field in new[]{"userTurn","session","request","model","environment","avatar"}) {
            object original=Get(lease,field);
            try {
                Set(lease,field,field=="userTurn"?(object)((int)original+1):field=="session"?(float)original+1:
                    field=="request"?"different complete request":null);
                Require(!Reuse(),"Semantic identity cannot cross changed owner: "+field);count++;
            } finally {Set(lease,field,original);}
        }
        var goal=(JObject)Get(lease,"goal");
        foreach(string field in new[]{"goalId","originalRequest","desiredState"}) {
            var original=goal[field].DeepClone();
            try {goal[field]="changed";Require(!Reuse(),"Semantic identity cannot cross changed goal: "+field);count++;}
            finally {goal[field]=original;}
        }
        string entity=decision.entityTarget.entityId,capture=decision.entityTarget.captureId;
        try {
            decision.entityTarget.entityId="different-entity";Require(!Reuse(),"Another entity cannot borrow the semantic match.");count++;
            decision.entityTarget.entityId=entity;decision.entityTarget.captureId="old-capture";
            Require(!Reuse(),"A stale capture reference cannot borrow the semantic match.");count++;
            decision.entityTarget.captureId=capture;decision.targetSelection="reselect";
            Require(!Reuse(),"Explicit reselection revokes semantic identity reuse.");count++;
        } finally {decision.entityTarget.entityId=entity;decision.entityTarget.captureId=capture;decision.targetSelection=null;}
        Vector3 root=observed.Root.position;
        try {observed.Root.position+=Vector3.right*.5f;Require(!Reuse(),"Moved geometry must be independently observed again.");count++;}
        finally {observed.Root.position=root;}
        Require(Reuse(),"Restoring the exact current owner and actual geometry retains semantic identity.");
        LastReport["targetIdentityReuse"]=new JObject{["passed"]=true,["invalidations"]=count,["captureId"]=capture,
            ["scope"]="Real captured opaque object and controlled independent semantic approval. Direct production reuse guard exercised; no device write or new movement is performed by these checks."};
    }
}
