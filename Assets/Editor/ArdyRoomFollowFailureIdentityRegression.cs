using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Bridge response to already completed controller events; no fake generated motion or HTTP.</summary>
public static class ArdyRoomFollowFailureIdentityRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;
    [Serializable] private sealed class Report
    {
        public bool passed;
        public int checks;
        public string error;
        public string scope = "Production connected bridge consumes injected controller terminal/recovery observations. Checks event identity, detached diagnostic snapshots, nonplanning monitor reads and explicit-stop task lifetime. Does not claim native stream recovery or motion quality; those are separate runtime tests.";
        public List<string> cases = new List<string>();
    }

    public static int RunChecks(ArdyRoomDialogueBridge bridge)
    {
        checks = 0; var report = new Report();
        Check(Application.isPlaying && Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) >= 0 &&
            bridge != null && bridge.IsConnected && !bridge.IsFollowing && !bridge.ReservesBody &&
            !bridge.Controller.IsContinuousFollowActive && !bridge.Controller.IsBusy && !bridge.Controller.Player.HasPoseOwnership &&
            string.IsNullOrEmpty(bridge.Controller.StreamSessionId),
            "Use a dormant bridge in the isolated connected room fixture.");
        var controller = bridge.Controller;
        Check(GetField(controller, "pending") == null && GetField(controller, "work") == null,
            "Identity injection cannot run alongside a real pending controller worker.");
        var properties = new Dictionary<string, object>();
        var fields = new Dictionary<string, object>();
        foreach (string name in new[] { "ExecutionState", "ExecutionReason", "Stage", "Status", "IsBusy", "IsContinuousFollowActive", "LastErrorResponseJson" })
            properties[name] = GetProperty(controller, name);
        foreach (string name in new[] { "revision", "streamRecovering", "streamEventName", "streamLastFailureCategory", "streamLastFailureReason", "streamBackend" })
            fields[name] = GetField(controller, name);
        var bridgeFields = SnapshotFields(bridge);
        var reports = (Queue<string>)GetField(bridge, "actionReports");
        string[] savedReports = reports.ToArray();
        var velocity = GetField(bridge, "followVelocity");
        var velocityFields = SnapshotFields(velocity);
        object savedBlockedReason = GetProperty(controller.Player, "LastBlockedReason");
        Vector3 savedUser = bridge.userHead.position;
        Vector3 savedRoot = bridge.avatar.transform.position;
        int operation = controller.OperationRevision;
        try
        {
            ServiceErrorClassificationChecks(controller);
            report.cases.Add("service-quality-code-is-exact-and-stale-error-body-cannot-reclassify-later-failures");
            NavigationRecoveryChecks(bridge, report);
            foreach (var item in new[] {
                (ArdyRoomExecutionState.Failed, ArdyRoomExecutionReason.GenerationFailed, "motion-supply", "fixture: next short prefix arrived too late", false),
                (ArdyRoomExecutionState.Blocked, ArdyRoomExecutionReason.GeneratedPathRejected, "user-clearance", "fixture: native candidate entered user clearance", true),
                (ArdyRoomExecutionState.Failed, ArdyRoomExecutionReason.OwnershipLost, "ownership", "fixture: player acquired by a different owner", false) })
            {
                ArmFollow(bridge, "failure-identity-" + item.Item3, false);
                SetField(controller, "streamLastFailureCategory", item.Item3);
                SetField(controller, "streamLastFailureReason", item.Item4);
                SetField(controller, "streamBackend", .24f);
                SetProperty(controller, "Stage", "stream-step");
                Call(controller, "SetStatus", item.Item4, item.Item1, item.Item2);
                int before = controller.Revision;
                // A changed view is a later observation. It must not conceal the completed
                // controller event before the bridge consumes its exact terminal reason.
                bridge.userHead.position = savedUser + Vector3.up * 10f;
                Call(bridge, "TickFollow", Time.realtimeSinceStartup);
                bridge.userHead.position = savedUser;
                Check(controller.Revision == before && controller.OperationRevision == operation &&
                    controller.ExecutionState == item.Item1 && controller.ExecutionReason == item.Item2 && controller.Stage == "stream-step",
                    "Consuming a controller result cancelled it again or changed its failure identity: " + item.Item3);
                Check(bridge.IsFollowing == item.Item5 && bridge.Phase == (item.Item5 ? "follow-paused" : "failed"),
                    "Terminal failure did not close follow, or a blocked follow lost its paused authorization.");
                var json = JObject.Parse(bridge.DescribeRoomContext());
                var diagnostic = json["latestExecutionObservation"];
                Check((string)diagnostic["executionReason"] == item.Item2.ToString() &&
                    (string)diagnostic["streamTelemetry"]["lastFailureCategory"] == item.Item3 &&
                    Mathf.Abs((float)diagnostic["streamTelemetry"]["backendSeconds"] - .24f) < .0001f,
                    "RoomTrace/current speech context lost the original reason or stream timing snapshot.");
                if (item.Item5)
                    Check(bridge.PeekCurrentTargetEvidence().activeBlockingCategory == "user-clearance",
                        "Current paused category did not identify the actual user-clearance rejection.");
                bridge.StopMovement();
                Check(!bridge.IsFollowing && bridge.PeekCurrentTargetEvidence().activeBlockingCategory == "" && controller.Revision == before,
                    "An explicit stop of an already ended/paused controller fabricated a second cancellation or retained a pause.");
                report.cases.Add("preserve-" + item.Item3 + "-controller-and-trace-identity");
            }

            foreach (string category in new[] { "navigation", "user-clearance" })
            {
                ArmFollow(bridge, "new-live-guard-" + category, true);
                SetField(controller, "streamLastFailureCategory", "motion-supply");
                SetField(controller, "streamLastFailureReason", "fixture: old underrun was already recovered");
                SetProperty(controller, "Stage", "live-guard-blocked");
                string currentReason = category == "user-clearance"
                    ? "fixture: 用户已进入当前路线的身体净距范围，已停止移动。"
                    : "fixture: a new door blocked this actual checked step";
                SetProperty(controller.Player, "LastBlockedReason", currentReason);
                if (category == "user-clearance")
                    Call(bridge, "RecordRejection", "user-clearance-check", category, currentReason, true, savedRoot + Vector3.forward * .2f);
                Call(controller, "Fail", currentReason, ArdyRoomExecutionReason.GeneratedPathRejected);
                int failedRevision = controller.Revision;
                Call(bridge, "ObserveExecution");
                var blocked = bridge.PeekCurrentTargetEvidence();
                Check(bridge.IsFollowing && bridge.Phase == "follow-paused" && controller.Revision == failedRevision &&
                    blocked.activeBlockingCategory == category && blocked.streamTelemetry.lastFailureCategory == category &&
                    blocked.streamTelemetry.lastFailureReason == currentReason,
                    "The current live " + category + " guard inherited a historical recovered motion-supply category.");
                bridge.StopMovement();
                report.cases.Add("new-live-" + category + "-replaces-historical-underrun-category");
            }

            ArmFollow(bridge, "recovery-parent-task", true);
            SetField(controller, "streamRecovering", true);
            SetProperty(controller, "Stage", "stream-recovering");
            Call(controller, "SetStatus", "fixture: bounded refill after underrun", ArdyRoomExecutionState.Returning, ArdyRoomExecutionReason.None);
            int recoveringRevision = controller.Revision;
            var live = bridge.avatar.GetComponent<ArdyLiveMotionController>();
            long gestureRevision = live != null ? live.Revision : 0;
            Call(bridge, "ObserveExecution");
            Check(bridge.IsFollowing && bridge.ActiveTaskId == "recovery-parent-task" && bridge.Phase == "follow-recovering" &&
                controller.IsContinuousFollowActive && controller.OperationRevision == operation && controller.Revision == recoveringRevision &&
                bridge.ReservesBody && (live == null || live.Revision == gestureRevision),
                "Bounded recovery lost its parent task, cancelled a body owner or released the reservation prematurely.");
            var recoveringFacts = bridge.PeekCurrentTargetEvidence();
            Check(recoveringFacts.streamTelemetry.recovering && recoveringFacts.streamTelemetry.stage == "stream-recovering" &&
                recoveringFacts.activeBlockingCategory == "", "Recovery was reported as a terminal/paused failure.");
            SetProperty(controller, "Stage", "stream-history");
            Call(bridge, "PrepareWaitingFollowResume", true, 0f);
            Call(bridge, "ObserveExecution");
            Check(bridge.Phase == "follow-recovering" && bridge.IsFollowing && controller.StreamIsRecovering &&
                controller.Revision == recoveringRevision && (live == null || live.Revision == gestureRevision),
                "Recovery history preparation lost its recovery identity or acquired a gesture owner.");
            SetProperty(controller, "Stage", "stream-recovering");
            recoveringFacts.streamTelemetry.lastFailureCategory = "mutated-consumer-copy";
            Check(bridge.PeekCurrentTargetEvidence().streamTelemetry.lastFailureCategory != "mutated-consumer-copy",
                "A diagnostic consumer mutated the controller's stream telemetry.");
            Check(bridge.StopForTaskIfCurrent("recovery-explicit-stop", "recovery-parent-task", operation, out string failure, true), failure);
            Check(!bridge.IsFollowing && !controller.IsContinuousFollowActive && controller.Revision == recoveringRevision + 1 &&
                controller.ExecutionState == ArdyRoomExecutionState.Cancelled && controller.ExecutionReason == ArdyRoomExecutionReason.CancelledByCaller &&
                bridge.Phase == "stopped", "Explicit stop during recovery did not cancel the authorized operation exactly once.");
            for (int i = 0; i < 10; i++) Call(bridge, "TickFollow", Time.realtimeSinceStartup + 100 + i);
            Check(!bridge.IsFollowing && !controller.IsBusy && controller.Revision == recoveringRevision + 1 &&
                Vector3.Distance(savedRoot, bridge.avatar.transform.position) < .0001f,
                "Follow resumed or moved the root after an explicit recovery stop.");
            report.cases.Add("recovery-retains-task-without-body-acquisition-and-stop-prevents-resume");

            bridge.InvalidateCurrentTargetAssessment();
            int builds = (int)GetField(bridge, "assessmentBuildCount");
            for (int i = 0; i < 5; i++)
            {
                var current = bridge.PeekCurrentTargetEvidence();
                Check(current.assessment.state == "unchecked" && !current.assessment.canPlanRoute &&
                    current.currentUserSupport != null && current.currentUserSupport.verified && current.userAnchorValid &&
                    Mathf.Abs(current.currentUserClearanceRadius - (bridge.Navigation.AgentRadius + ArdyRoomApproachPlanner.UserBodyRadius + ArdyRoomApproachPlanner.UserClearance)) < .0001f,
                    "Nonplanning monitor omitted current support/radius or invented a fresh route.");
            }
            Check((int)GetField(bridge, "assessmentBuildCount") == builds, "Monitor repaint triggered route planning.");
            var refreshed = bridge.ReadCurrentTargetEvidence(true);
            Check((int)GetField(bridge, "assessmentBuildCount") == builds + 1 && refreshed.assessment.state != "unchecked",
                "The explicit route-check action no longer performs its bounded current assessment.");
            report.cases.Add("live-monitor-is-nonplanning-and-explicit-refresh-still-checks-route");

            Vector3 rejectedPoint = savedRoot + Vector3.forward * .3f;
            Call(bridge, "RecordRejection", "fixture-actual-check", "user-clearance", "fixture recorded checked candidate", true, rejectedPoint);
            var observed = bridge.PeekCurrentTargetEvidence();
            Check(observed.lastObservedRejection != null && observed.lastObservedRejection.appliesToCurrentTarget &&
                observed.lastObservedRejection.hasPoint && observed.lastObservedRejection.point == rejectedPoint,
                "A recorded check point was lost or relabelled as an inferred coordinate.");
            observed.lastObservedRejection.point = Vector3.zero;
            Check(bridge.PeekCurrentTargetEvidence().lastObservedRejection.point == rejectedPoint,
                "An inspector mutated the retained rejection record.");
            bridge.userHead.position += Vector3.right * .1f;
            Check(!bridge.PeekCurrentTargetEvidence().lastObservedRejection.appliesToCurrentTarget,
                "A prior rejection point remained current after the user target moved.");
            report.cases.Add("rejected-check-point-is-detached-and-becomes-historical-after-target-change");
            report.passed = true;
        }
        catch (Exception error) { report.error = error.ToString(); throw; }
        finally
        {
            bridge.StopMovement();
            bridge.userHead.position = savedUser;
            foreach (var item in properties) SetProperty(controller, item.Key, item.Value);
            foreach (var item in fields) SetField(controller, item.Key, item.Value);
            SetProperty(controller.Player, "LastBlockedReason", savedBlockedReason);
            RestoreFields(bridge, bridgeFields); RestoreFields(velocity, velocityFields);
            reports.Clear(); foreach (string value in savedReports) reports.Enqueue(value);
            report.checks = checks;
            string output = Path.GetFullPath("Tools/MotionAdapter/reports/room-follow-failure-identity-regression.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, JsonUtility.ToJson(report, true));
        }
        return checks;
    }

    private static void ServiceErrorClassificationChecks(ArdyRoomLocomotionController controller)
    {
        const string httpMessage = "ARDY 服务请求失败（422）：HTTP/1.1 422 Unprocessable Entity";
        const string quality = "{\"error\":{\"code\":\"stream_quality_rejected\",\"message\":\"No valid candidate remained\",\"details\":{\"attempts\":3}}}";
        Check(Classify(NewException("ServiceRequestException", httpMessage, quality)) == "motion-quality",
            "The exact backend quality-rejection code was reported as a transport/service failure.");
        const string unrelated = "{\"error\":{\"code\":\"generation_unavailable\",\"message\":\"stream_quality_rejected appears only in prose\"}}";
        Check(Classify(NewException("ServiceRequestException", httpMessage, unrelated)) == "service-request",
            "A different machine code was classified from unstructured error-body prose.");
        Check(Classify(NewException("ServiceRequestException", httpMessage,
            "<html><body>502 Bad Gateway: stream_quality_rejected</body></html>")) == "service-request",
            "Malformed proxy HTML was accepted as a structured quality rejection or escaped the bounded parser.");
        Check(Classify(NewException("ServiceRequestException", httpMessage, "")) == "service-request",
            "An empty transport error body was treated as a quality event.");
        Check(Classify(NewException("ServiceRequestException", httpMessage,
            "{\"error\":{\"code\":\"stream_quality_rejected_later\"}}")) == "service-request",
            "A similar machine code bypassed exact quality-code matching.");

        SetProperty(controller, "LastErrorResponseJson", quality);
        Check(Classify(NewException("ExecutionException", "fixture: a different player acquired the body", ArdyRoomExecutionReason.OwnershipLost)) == "ownership",
            "A retained old HTTP quality body contaminated a later ownership exception.");
        Check(Classify(NewException("ExecutionException", "fixture: no verified route to the changed target", ArdyRoomExecutionReason.NavigationRejected)) == "navigation",
            "A retained old HTTP quality body contaminated a later navigation exception.");
        Check(Classify(NewException("ServiceRequestException", "fixture: connection refused", "")) == "service-request",
            "A retained old quality body contaminated a later request with no response body.");

        Exception NewException(string nestedName, params object[] arguments)
        {
            var type = typeof(ArdyRoomLocomotionController).GetNestedType(nestedName, BindingFlags.NonPublic)
                ?? throw new MissingMemberException(nameof(ArdyRoomLocomotionController), nestedName);
            return (Exception)Activator.CreateInstance(type, Flags, null, arguments, null);
        }
        string Classify(Exception error)
        {
            var method = typeof(ArdyRoomLocomotionController).GetMethod("StreamFailureCategory", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(nameof(ArdyRoomLocomotionController), "StreamFailureCategory");
            try { return (string)method.Invoke(null, new object[] { error }); }
            catch (TargetInvocationException exception) { throw exception.InnerException ?? exception; }
        }
    }

    private static void NavigationRecoveryChecks(ArdyRoomDialogueBridge bridge, Report report)
    {
        var controller = bridge.Controller;
        Vector3 root = bridge.avatar.transform.position;
        int operation = controller.OperationRevision;
        var boundary = new ArdyRoomApproachObservation {
            preflightFailure = new ArdyRoomRouteDiagnostic {
                stage = "start", code = "navmesh-boundary", hasSample = true, sample = root,
                reason = "fixture: role start failed normal NavMesh admission" } };
        ArmFollow(bridge, "boundary-classification", false);
        Call(controller, "SetStatus", "fixture: dormant", ArdyRoomExecutionState.Arrived, ArdyRoomExecutionReason.None);
        foreach (var item in new[] { ("target", "navmesh-boundary"), ("start", "support-missing"), ("path", "body-sweep-blocked") })
        {
            var other = new ArdyRoomApproachObservation { preflightFailure = new ArdyRoomRouteDiagnostic {
                stage = item.Item1, code = item.Item2, hasSample = true, sample = root } };
            int revision = controller.Revision;
            Check(!(bool)Call(bridge, "TryRecoverPlanStart", other, "fixture: other failure") &&
                !(bool)GetField(bridge, "navigationRecoveryAttempted") && controller.Revision == revision,
                "A target, support or route failure consumed navigation recovery or dispatched body work.");
        }
        report.cases.Add("navigation-recovery-only-accepts-precise-start-navmesh-boundary");

        var mixed = new ArdyRoomApproachObservation();
        mixed.candidates.Add(new ArdyRoomApproachCandidateObservation { navigation = new ArdyRoomRouteDiagnostic {
            stage = "target", code = "body-overlap", hasSample = true, sample = root + Vector3.right * .8f } });
        mixed.candidates.Add(new ArdyRoomApproachCandidateObservation { navigation = boundary.preflightFailure });
        Call(bridge, "RecordPlanningRejection", mixed, "fixture: common invalid start with mixed candidates");
        var evidence = bridge.PeekCurrentTargetEvidence();
        Check(evidence.lastObservedRejection.hasPoint && evidence.lastObservedRejection.point == root,
            "A preceding target collision concealed the actual common start-boundary rejection point.");

        ArmRecovery("navigation-recovery-parent");
        Call(bridge, "ObserveExecution");
        evidence = bridge.PeekCurrentTargetEvidence();
        Check(bridge.IsFollowing && bridge.ActiveTaskId == "navigation-recovery-parent" && bridge.Phase == "nav-recovering" &&
            evidence.navigationRecoveryActive && evidence.navigationRecoveryAttempted && bridge.ReservesBody,
            "Navigation recovery lost the authorized parent task or was reported as a user-target pause.");
        float radius = (float)GetField(bridge, "navigationRecoveryUserClearanceRadius");
        Vector3 userGround = (Vector3)GetField(bridge, "navigationRecoveryUserGround");
        Check(Call(bridge, "GuardUser", userGround + Vector3.right * (radius + .2f), userGround + Vector3.right * .1f) != null,
            "A recovery without an ordinary approach plan bypassed the user's body-clearance guard.");
        int before = controller.Revision;
        Call(controller, "SetStatus", "fixture: executed endpoint verified and pose returned", ArdyRoomExecutionState.Arrived, ArdyRoomExecutionReason.None);
        Call(bridge, "ObserveExecution");
        evidence = bridge.PeekCurrentTargetEvidence();
        Check(bridge.IsFollowing && bridge.ActiveTaskId == "navigation-recovery-parent" && bridge.Phase == "follow-replanning" &&
            !evidence.navigationRecoveryActive && evidence.navigationRecoveryAttempted &&
            !(bool)GetField(bridge, "followNeedsNewTarget") && controller.OperationRevision == operation && controller.Revision == before &&
            ArdyRoomFollowPolicy.MayDispatch(Time.realtimeSinceStartup, (float)GetField(bridge, "followLastDispatchAt"), false, 0f),
            "A successful recovery required the user to move again, reset its budget, or changed parent identity.");
        // Actual displacement/resumed native generation is covered by the separate runtime recovery test.
        Check((bool)Call(bridge, "TryRecoverPlanStart", boundary, "fixture: repeated invalid start") && !bridge.IsFollowing &&
            bridge.Phase == "blocked" && controller.Revision == before && bridge.avatar.transform.position == root,
            "A repeated start failure reset the once-per-parent budget, retried generation or warped the root.");
        report.cases.Add("recovery-keeps-user-guard-and-resumes-same-parent-without-new-target-gate");
        report.cases.Add("second-boundary-failure-ends-parent-without-retry-or-root-write");

        foreach (var state in new[] { ArdyRoomExecutionState.Failed, ArdyRoomExecutionState.Blocked })
        {
            ArmRecovery("navigation-recovery-async-" + state);
            Call(controller, "SetStatus", "fixture: native recovery candidate failed quality or terrain", state,
                state == ArdyRoomExecutionState.Failed ? ArdyRoomExecutionReason.GenerationFailed : ArdyRoomExecutionReason.GeneratedPathRejected);
            before = controller.Revision;
            Call(bridge, "ObserveExecution");
            for (int i = 0; i < 5; i++) Call(bridge, "TickFollow", Time.realtimeSinceStartup + 30 + i);
            Check(!bridge.IsFollowing && !bridge.PeekCurrentTargetEvidence().navigationRecoveryActive &&
                controller.ExecutionState == state && controller.Revision == before && bridge.avatar.transform.position == root,
                "An asynchronous recovery failure started ordinary follow, cancelled the failure again or moved the root.");
        }
        report.cases.Add("async-native-recovery-failure-does-not-fall-through-to-follow-or-retry");

        ArmRecovery("navigation-recovery-stop-parent");
        before = controller.Revision;
        Check(bridge.StopForTaskIfCurrent("navigation-recovery-explicit-stop", "navigation-recovery-stop-parent", operation,
            out string failure, true), failure);
        for (int i = 0; i < 5; i++) Call(bridge, "TickFollow", Time.realtimeSinceStartup + 60 + i);
        Check(!bridge.IsFollowing && !bridge.PeekCurrentTargetEvidence().navigationRecoveryActive &&
            controller.ExecutionState == ArdyRoomExecutionState.Cancelled && controller.Revision == before + 1 &&
            bridge.Phase == "stopped" && bridge.avatar.transform.position == root,
            "Explicit stop during navigation recovery did not cancel exactly once or later revived its follow task.");
        report.cases.Add("explicit-stop-cancels-navigation-recovery-once-and-never-resumes");

        void ArmRecovery(string id)
        {
            ArmFollow(bridge, id, false);
            Check(ArdyRoomApproachPlanner.TryGetUserGround(bridge.Navigation, bridge.userHead, out Vector3 ground, out string supportFailure), supportFailure);
            SetField(bridge, "plan", null);
            SetField(bridge, "navigationRecoveryActive", true);
            SetField(bridge, "navigationRecoveryAttempted", true);
            SetField(bridge, "navigationRecoveryUserGround", ground);
            SetField(bridge, "navigationRecoveryUserHead", bridge.userHead.position);
            SetField(bridge, "navigationRecoveryUserClearanceRadius", bridge.Navigation.AgentRadius +
                ArdyRoomApproachPlanner.UserBodyRadius + ArdyRoomApproachPlanner.UserClearance);
            SetProperty(controller, "Stage", "navigation-recovery-generating");
            Call(controller, "SetStatus", "fixture: native bounded recovery in progress", ArdyRoomExecutionState.Generating, ArdyRoomExecutionReason.None);
        }
    }

    private static void ArmFollow(ArdyRoomDialogueBridge bridge, string id, bool continuous)
    {
        bridge.StopMovement();
        Call(bridge, "ResetNavigationRecovery");
        SetField(bridge, "activeAction", true); SetField(bridge, "followActive", true);
        SetField(bridge, "hasControllerOperation", true); SetField(bridge, "operationRevision", bridge.Controller.OperationRevision);
        SetField(bridge, "actionId", id); SetField(bridge, "lastAttemptActionId", id);
        SetField(bridge, "followUser", bridge.userHead); SetField(bridge, "boundUser", bridge.userHead);
        SetProperty(bridge.Controller, "IsContinuousFollowActive", continuous);
        SetField(bridge.Controller, "streamRecovering", false);
    }
    private static object GetField(object target, string name) =>
        (target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().Name, name)).GetValue(target);
    private static void SetField(object target, string name, object value) =>
        (target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().Name, name)).SetValue(target, value);
    private static object GetProperty(object target, string name) =>
        (target.GetType().GetProperty(name, Flags) ?? throw new MissingMemberException(target.GetType().Name, name)).GetValue(target);
    private static void SetProperty(object target, string name, object value) =>
        (target.GetType().GetProperty(name, Flags) ?? throw new MissingMemberException(target.GetType().Name, name)).SetValue(target, value);
    private static Dictionary<FieldInfo, object> SnapshotFields(object target)
    {
        var result = new Dictionary<FieldInfo, object>();
        foreach (var field in target.GetType().GetFields(Flags))
            if (!field.IsInitOnly && !field.IsPublic) result[field] = field.GetValue(target);
        return result;
    }
    private static void RestoreFields(object target, Dictionary<FieldInfo, object> values)
    { foreach (var item in values) item.Key.SetValue(target, item.Value); }
    private static object Call(object target, string name, params object[] arguments)
    {
        try { return (target.GetType().GetMethod(name, Flags) ?? throw new MissingMethodException(target.GetType().Name, name)).Invoke(target, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
}
