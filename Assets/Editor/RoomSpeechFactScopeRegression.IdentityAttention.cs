using System.Collections;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class RoomSpeechFactScopeRegression
{
    private static IEnumerator CheckStableArrivalRefresh(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        bool automatic=model.CompleteControlledEngineFallback;model.CompleteControlledEngineFallback=false;
        try {
        foreach(bool cancel in new[]{false,true}) {
            yield return PrepareRoomPlanning(chat,bridge,"来我身边。");
            int generation=(int)Get(chat,"m_FormalResponseGeneration");
            model.Decisions.Last().Complete(true,"{\"intent\":\"none\",\"origin\":\"none\",\"evidence\":\"\"}","arrival-refresh-source");
            Call(chat,"RememberFirstReplyRequestSemantics",new FirstReplyCompletion { Disposition=FirstReplyDisposition.Tools,RequestKind="action" },generation,model);
            string action="arrival-refresh-owned";
            Set(bridge,"actionId",action);Set(bridge,"activeAction",false);Set(bridge,"followActive",false);Set(bridge,"executionTargetKind","user");
            Call(bridge,"SetFact","moving","controlled initial movement");
            Set(chat,"m_RoomTaskIntent","approach");Set(chat,"m_RoomTaskActionId",action);Set(chat,"m_RoomTaskDispatch","approach-submitted");
            int history=model.m_DataList.Count, operations=bridge.Controller.OperationRevision;
            const string words="今すぐ行きますね。";
            int count=model.Decisions.Count;Call(chat,"CompleteRoomSpeech",Buffer(words),words,generation,true,null);
            yield return Wait(()=>model.Decisions.Count>count,"First movement statement review");
            var first=model.Decisions.Last();int fallbacks=model.EngineFallbacks.Count;
            Call(bridge,"SetFact","returning","controlled posture return during first review");
            first.Complete(true,"{\"verdict\":\"consistent\",\"reason\":\"Earlier moving receipt\",\"factScope\":\"user-target\",\"resultReport\":\"moving\"}","movement-stale");
            yield return Wait(()=>model.EngineFallbacks.Count>fallbacks,"Transitional fallback gets its own review");
            var transition=model.EngineFallbacks.Last();
            Call(bridge,"SetFact","arrived","controlled completed arrival during fallback review");
            if(cancel) { Call(chat,"InvalidateFormalResponse","arrival-refresh-owner-cancelled");transition.Complete(true,"{\"verdict\":\"consistent\",\"reason\":\"Late returning\",\"factScope\":\"user-target\",\"resultReport\":\"returning\"}","late-transition");
                Require(model.m_DataList.Count==history,"Cancelled reply cannot use the terminal refresh to publish.");continue; }
            transition.Complete(true,"{\"verdict\":\"consistent\",\"reason\":\"Earlier returning receipt\",\"factScope\":\"user-target\",\"resultReport\":\"returning\"}","returning-stale");
            yield return Wait(()=>model.EngineFallbacks.Count==fallbacks+2,"One stable arrival supersedes stale transitional fallback");
            var completed=model.EngineFallbacks.Last();var input=JObject.Parse(completed.Input);
            Require((string)input.SelectToken("movementReportRequirement.expectedReport")=="arrived" && model.m_DataList.Count==history,
                "Arrival refresh is independently reviewed before any release.");
            completed.Complete(true,"{\"verdict\":\"consistent\",\"reason\":\"Current owned completion\",\"factScope\":\"user-target\",\"resultReport\":\"arrived\"}","completion-approved");
            Require(model.m_DataList.Count==history+1 && model.m_DataList.Last().content==(string)input["publicSpeech"] && bridge.Controller.OperationRevision==operations,
                "Stable completion is delivered once in this generation without restarting movement.");
            int reviews=model.EngineFallbacks.Count;completed.Complete(true,"{}","late-duplicate");
            Require(model.EngineFallbacks.Count==reviews&&model.m_DataList.Count==history+1,"Duplicate terminal review cannot refresh again.");
        }
        ((JArray)LastDecisionFailureReport["cases"]).Add("moving-returning-arrived-one-terminal-refresh-and-owner-cancellation");
        Call(chat,"InvalidateFormalResponse","arrival-refresh-complete");
        } finally { model.CompleteControlledEngineFallback=automatic; }
    }

    private static IEnumerator CheckReconciledBodyHandoff(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        const string request="为什么没执行？请打开那台设备，然后回来。";
        bool previous=model.ControlReviewsEnabled;model.ControlReviewsEnabled=true;
        try {
            foreach(string outcome in new[]{"approved","denied","owner-replaced"}) {
                Call(chat,"CancelSpatialGoal","handoff-regression-next");
                yield return PrepareRoomPlanning(chat,bridge,request,beforePlanner:()=>{
                    Set(bridge,"followActive",true);Set(bridge,"actionId","old-follow");Set(bridge,"phase","follow-waiting");
                });
                int generation=(int)Get(chat,"m_FormalResponseGeneration");
                Call(chat,"BeginSceneVisionStopPreparation",generation);
                var early=model.ControlReviews.Last();
                early.Complete(true,"{\"explicitStopNow\":false,\"bodyDirective\":\"continue-current\",\"force\":\"question\",\"reason\":\"Controlled actual prior misclassification\"}","actual-negative-cache");
                int before=model.ControlReviews.Count, operation=bridge.Controller.OperationRevision;
                var proposal=new JObject { ["intent"]="none",["origin"]="user",["evidence"]="打开那台设备",["taskStep"]="s1",
                    ["deviceGoal"]=new JObject { ["operation"]="set",["requestQuote"]="打开那台设备",["targetDescription"]=request,["desiredState"]="on" },
                    ["taskPlan"]=new JArray(new JObject { ["id"]="s1",["quote"]="打开那台设备",["kind"]="device-state",["desiredState"]="on",["after"]=new JArray() },
                        new JObject { ["id"]="s2",["quote"]="回来",["kind"]="approach",["desiredState"]=null,["after"]=new JArray("s1") }),
                    ["taskCoverage"]=new JArray(new JObject { ["quote"]="为什么没执行？",["kind"]="answer",["stepIds"]=new JArray() },
                        new JObject { ["quote"]="请打开那台设备，然后回来。",["kind"]="action",["stepIds"]=new JArray("s1","s2") }) };
                int reviews=model.DeviceGoalReviews.Count;
                model.Decisions.Last(r=>r.Kind=="room").Complete(true,proposal.ToString(Formatting.None),"current-replacement-proposal");
                yield return Wait(()=>model.DeviceGoalReviews.Count>reviews,"Independent current task review");
                model.DeviceGoalReviews.Last().Complete(true,"{\"verdict\":\"approved\",\"reason\":\"Current request authorizes the new device task.\",\"relation\":\"new\",\"referentKind\":\"unbound\",\"referentSourceId\":\"none\",\"requestAssessment\":\"requested\"}","current-task-approved");
                yield return Wait(()=>model.ControlReviews.Count>before,"Cached negative must get one independent handoff reconciliation");
                var reconcile=model.ControlReviews.Last();var input=JObject.Parse(reconcile.Input);
                Require(input["replacementTransaction"]!=null && bridge.IsFollowing && bridge.Controller.OperationRevision==operation,
                    "Goal approval does not itself stop the old body task.");
                if(outcome=="owner-replaced")Set(bridge,"actionId","replacement-follow");
                string directive=outcome=="denied"?"continue-current":"other-task";
                reconcile.Complete(true,"{\"explicitStopNow\":false,\"bodyDirective\":\""+directive+"\",\"force\":\"command-now\",\"reason\":\"Controlled whole-turn reconciliation\"}","reconciled");
                Require(model.ControlReviews.Count==before+1 && (outcome=="approved" ? !bridge.IsFollowing : bridge.IsFollowing),
                    "Only approved current owner hands off; denial and stale ownership remain blocked without repeat: "+outcome);
                reconcile.Complete(true,"{}","late-duplicate");
                Require(model.ControlReviews.Count==before+1,"Duplicate callback cannot request another handoff.");
                Call(chat,"InvalidateFormalResponse","handoff-case-complete");Call(chat,"CancelSpatialGoal","handoff-case-complete");
                bridge.StopMovement("handoff-regression-end","End controlled follow");
            }
            ((JArray)LastDecisionFailureReport["cases"]).Add("cached-negative-handoff-approval-denial-owner-replacement");
        } finally { model.ControlReviewsEnabled=previous;Call(chat,"CancelSpatialGoal","handoff-regression-cleanup");bridge.StopMovement("handoff-cleanup","End controlled fixture"); }
    }

    private static void CheckOngoingSpeechDependencies(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        var head = bridge.userHead; var position = head.position;
        string action = (string)Get(bridge, "actionId"), phase = (string)Get(bridge, "phase");
        int operation = bridge.Controller.OperationRevision;
        void Operation(int value) => bridge.Controller.GetType().GetProperty("OperationRevision",Flags).SetValue(bridge.Controller,value);
        var verdict = new JObject { ["verdict"]="consistent", ["resultReport"]="moving", ["movementDependencies"]=new JObject {
            ["source"]="independent-public-words", ["currentGeometryRequired"]=false,
            ["currentGeometryQuote"]="", ["ongoingActivityQuote"]="正在跟随你" } };
        try
        {
            Set(bridge,"actionId","activity-owner"); Set(bridge,"phase","following");
            var activity = Call(chat,"ResolveReviewedRoomSpeechDependencies","follow","user-target",verdict);
            var geometry = Call(chat,"ResolveRoomSpeechDependencies","follow","user-target");
            var snapshot = Call(chat,"CaptureRoomSpeechSnapshot",bridge,null);
            string[] Changes(object scope) => ((System.Collections.Generic.List<string>)Call(chat,"ChangedRoomSpeechFacts",snapshot,scope)).ToArray();
            head.position += Vector3.right * 0.7f; bridge.ReadCurrentTargetEvidence();
            Require(!Changes(activity).Contains("userTargetRevision") && Changes(geometry).Contains("userTargetRevision"),
                "Actual target movement invalidates current geometry but not independently classified ongoing activity.");
            foreach (string terminal in new[] { "stopped", "failed", "follow-paused", "arrived" })
            {
                Set(bridge,"phase",terminal);
                Require(Changes(activity).Contains("userTaskPhase"),"Activity cannot survive a real phase change: "+terminal);
            }
            Set(bridge,"phase","following"); Set(bridge,"actionId","replacement-owner");
            Require(Changes(activity).Contains("userTaskActionId"),"Activity cannot survive action replacement.");
            Set(bridge,"actionId","activity-owner");
            Operation(operation+1);
            Require(Changes(activity).Contains("userTaskOperation"),"Activity cannot survive controller takeover.");
            Operation(operation);
            bridge.userHead=null;
            Require(Changes(activity).Contains("userTaskBinding"),"Activity cannot survive losing the tracked user.");
            ((JArray)LastDecisionFailureReport["cases"]).Add("ongoing-speech-coordinate-vs-owner-and-terminal-dependencies");
        }
        finally { bridge.userHead=head;head.position=position;Set(bridge,"actionId",action);Set(bridge,"phase",phase);Operation(operation); }
    }

    private static IEnumerator CheckBoundedRejectedProposal(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        const string request="请重新打开刚才关闭的设备，然后过来。";
        yield return PrepareRoomPlanning(chat,bridge,request);
        int generation=(int)Get(chat,"m_FormalResponseGeneration"), decisions=model.Decisions.Count, operation=bridge.Controller.OperationRevision;
        var proposal=new JObject { ["intent"]="none",["origin"]="user",["evidence"]="重新打开刚才关闭的设备",["taskStep"]="s1",
            ["deviceGoal"]=new JObject { ["operation"]="set",["requestQuote"]="重新打开刚才关闭的设备",["targetDescription"]="刚才关闭的设备",["desiredState"]="off" },
            ["taskPlan"]=new JArray(new JObject { ["id"]="s1",["quote"]="重新打开刚才关闭的设备",["kind"]="device-state",["desiredState"]="off",["after"]=new JArray() },
                new JObject { ["id"]="s2",["quote"]="过来",["kind"]="approach",["desiredState"]=null,["after"]=new JArray("s1") }),
            ["taskCoverage"]=new JArray(new JObject { ["quote"]=request,["kind"]="action",["stepIds"]=new JArray("s1","s2") }) };
        for(int attempt=0;attempt<2;attempt++)
        {
            int reviews=model.DeviceGoalReviews.Count;
            model.Decisions.Last().Complete(true,proposal.ToString(Formatting.None),"controlled-wrong-new-operation");
            yield return Wait(()=>model.DeviceGoalReviews.Count>reviews,"Current operation independent review");
            model.DeviceGoalReviews.Last().Complete(true,
                "{\"verdict\":\"rejected\",\"reason\":\"The new requested state is on, but the proposal requests off.\",\"relation\":\"new\",\"referentKind\":\"unbound\",\"referentSourceId\":\"none\",\"requestAssessment\":\"requested\"}","controlled-current-operation-mismatch");
            if(attempt==0)yield return Wait(()=>model.Decisions.Count>decisions,"Single bounded reinterpretation");
        }
        var failure=(JObject)Call(chat,"ReadRoomTaskDecisionFailure");
        Require(model.Decisions.Count==decisions+1 && bridge.Controller.OperationRevision==operation,
            "Two rejected interpretations end without a third planner request or any physical action.");
        Require((string)failure?["validationReason"]=="device-goal-not-independently-approved" &&
            (string)failure.SelectToken("proposalReview.requestAssessment")=="requested" &&
            (string)failure.SelectToken("proposalReview.proposal.desiredState")=="off",
            "Original current proposal rejection remains attributable; no none/taskStep schema error replaces it.");
        Require(!(bool)Call(chat,"CurrentGenerationRequiresReviewedGoalAnswer",generation) &&
            ((string)Call(chat,"RoomTaskFallback",bridge)).Contains("核验没有通过"),
            "A rejected interpretation is explained without inventing a human refusal or a device execution failure.");
        ((JArray)LastDecisionFailureReport["cases"]).Add("bounded-rejected-proposal-keeps-current-cause");
        Call(chat,"InvalidateFormalResponse","bounded-proposal-regression-end");
    }
}
