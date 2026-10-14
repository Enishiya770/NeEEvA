using System;
using System.Collections;
using NeEEvA.Motion;
using UnityEngine;

public static partial class ArdyRoomStreamDelayRegression
{
    private static IEnumerator FollowHandoffScenarios()
    {
        foreach(string mode in new[]{"arrival-wait-resume","pending-arrival-wait-resume","near-arrival-retarget","replace-after-arrival"})
        {
            var wait=Reset();while(wait.MoveNext())yield return wait.Current;
            bool pendingArrival=mode=="pending-arrival-wait-resume";
            BeginCase(mode,pendingArrival?3:-1,pendingArrival?900:0,false);
            userGround=new Vector3(0,0,2.8f);userVelocity=Vector3.zero;
            controller.BeginContinuousFollow(Plan(),userGround,userGround+Vector3.up*1.6f,Vector3.zero,.55f,false);
            current.operation=controller.OperationRevision;
            float deadline=Time.realtimeSinceStartup+25;
            bool retargeted=false;
            while(controller.Stage!="stream-waiting")
            {
                Check(Time.realtimeSinceStartup<deadline && !Failed() && controller.IsContinuousFollowActive,"Arrival failed: "+controller.Status);
                if(mode=="near-arrival-retarget" && !retargeted && controller.Player.IsPlaying &&
                    Horizontal(controller.TargetGround-Ground()).magnitude<.5f) {
                    userGround+=Vector3.right*1.1f;retargeted=true;
                }
                if(pendingArrival && !retargeted && controller.Player.IsPlaying &&
                    controller.StreamTelemetry.pending && controller.StreamStepCount>=3) {
                    userGround=Ground()+Vector3.forward*1.1f;retargeted=true;
                }
                UpdateHandoffTarget(false);yield return null;
            }
            Check(mode!="near-arrival-retarget" || retargeted,"Near-arrival target change was not exercised.");
            current.initialSession=controller.StreamTelemetry.lastHandoff?.sessionId;
            Check(!pendingArrival || retargeted && controller.StreamTelemetry.lastHandoff?.pending==true,
                "Pending arrival did not actually close an in-flight native request.");
            Check(!string.IsNullOrEmpty(current.initialSession),"Arrival lost its pre-close session evidence.");
            float until=Time.realtimeSinceStartup+2.5f;
            while(Time.realtimeSinceStartup<until) {
                Check(controller.IsContinuousFollowActive && !Failed() && controller.OperationRevision==current.operation &&
                    controller.StreamSessionId==null,"Several real waiting frames lost follow ownership or restarted a stationary stream.");
                UpdateHandoffTarget(false);yield return null;
            }
            Check(!controller.Player.HasPoseOwnership,"Arrival did not complete its real Animator handoff.");
            if(mode=="replace-after-arrival") {
                wait=StopAndHold(1.2f);while(wait.MoveNext())yield return wait.Current;
                Check(!controller.IsContinuousFollowActive && controller.ExecutionState==ArdyRoomExecutionState.Cancelled,
                    "Cancelled arrival waiter retained operation authority.");
                userGround=new Vector3(1.6f,0,3f);
                caseRoot=avatar.transform.position; // Measure the new operation's prebuffer from its actual start.
                controller.BeginContinuousFollow(Plan(),userGround,userGround+Vector3.up*1.6f,Vector3.zero,.5f,false);
                Check(controller.OperationRevision!=current.operation,"Replacement reused the retired operation identity.");
                current.operation=controller.OperationRevision;stopped=false;
            } else {
                userGround+=new Vector3(.65f,0,1.7f);UpdateHandoffTarget(true);
            }
            deadline=Time.realtimeSinceStartup+20;
            while(!controller.Player.IsPlaying || string.IsNullOrEmpty(controller.StreamSessionId)) {
                Check(Time.realtimeSinceStartup<deadline && !Failed() && controller.IsContinuousFollowActive,
                    "User movement after arrival failed to resume with fresh rendered history: "+controller.Status);
                UpdateHandoffTarget(true);yield return null;
            }
            current.resumedSession=controller.StreamSessionId;current.observedResume=true;
            Check(current.resumedSession!=current.initialSession && controller.OperationRevision==current.operation,
                "Resumed follow reused a closed session or changed the retained operation.");
            until=Time.realtimeSinceStartup+1f;
            while(Time.realtimeSinceStartup<until) {
                Check(!Failed() && controller.IsContinuousFollowActive,"Resumed follow immediately failed.");
                UpdateHandoffTarget(true);yield return null;
            }
            wait=StopAndHold(1);while(wait.MoveNext())yield return wait.Current;EndCase();
        }
    }
    private static void UpdateHandoffTarget(bool moving)
    {
        if(!controller.IsContinuousFollowActive)return;
        Check(controller.TryUpdateContinuousFollow(Plan(),userGround,userGround+Vector3.up*1.6f,
            Vector3.zero,.55f,moving,out string error),error);
    }
}
