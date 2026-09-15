using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Motion;
using NeEEvA.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

/// <summary>Isolated real Physics/NavMesh station checks; never opens or saves the user's room.</summary>
public static class ArdyRoomObjectApproachRegression
{
    [Serializable] private sealed class Report
    {
        public string status = "running", error, unityVersion;
        public int checks;
        public List<string> fixtures = new List<string>();
        public string scope = "Real Unity Physics/NavMesh object-adjacent floor planning plus bridge rejection-event contract. No live model, ARDY playback, grounding recognition, gaze, or fresh visual confirmation assertion.";
    }

    private sealed class Fixture : IDisposable
    {
        public readonly GameObject Root, Avatar, User;
        public readonly ArdyRoomNavigation Navigation;
        public Fixture(string name)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Root = new GameObject("ObjectApproachFixture_" + name);
            Avatar = new GameObject("ExplicitAvatar");
            User = new GameObject("ExplicitUserHead");
            User.transform.position = new Vector3(3f, 1.6f, 3f);
            Navigation = Root.AddComponent<ArdyRoomNavigation>();
            Box("Floor", new Vector3(0, -.1f, 0), new Vector3(12, .2f, 12));
        }
        public BoxCollider Box(string name, Vector3 center, Vector3 size)
        {
            var item = new GameObject(name); item.transform.SetParent(Root.transform, false);
            item.transform.position = center;
            var box = item.AddComponent<BoxCollider>(); box.size = size; return box;
        }
        public void Build() => Check(Navigation.Build(Root.transform, Avatar.transform, out var reason), "Build: " + reason);
        public bool Plan(Vector3 start, Vector3 surface, out ArdyRoomObjectApproachPlan plan, out string reason,
            out ArdyRoomObjectApproachObservation observation)
            => ArdyRoomObjectApproachPlanner.TryPlan(Navigation, start, surface, User.transform, .8f, out plan, out reason, out observation);
        public void Dispose()
        {
            Navigation.Clear(); Object.DestroyImmediate(Root); Object.DestroyImmediate(Avatar); Object.DestroyImmediate(User);
        }
    }

    private static int checks;
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public static void RunBatch()
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Run only in the existing isolated unity-naturalness-validation project, in batch mode.");
        string output = Path.GetFullPath(Argument("-ardyRoomObjectApproachOutput") ?? Path.Combine(Application.dataPath, "../Logs/object-approach"));
        Directory.CreateDirectory(output);
        checks = 0;
        var report = new Report { unityVersion = Application.unityVersion };
        int navSettings = NavMesh.GetSettingsCount();
        try
        {
            ExerciseSurfaceHeight(report);
            ExerciseUserClearance(report);
            ExerciseUserSupportNotGoal(report);
            ExerciseOtherHeightAndDisconnected(report);
            ExerciseInvalidAndChangedGeometry(report);
            ExerciseRejectedCallback(report);
            Check(NavMesh.GetSettingsCount() == navSettings, "Owned navigation settings are released.");
            report.status = "passed";
        }
        catch (Exception error) { report.status = "failed"; report.error = error.ToString(); Debug.LogException(error); }
        report.checks = checks;
        File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
        Debug.Log("[ArdyRoomObjectApproachRegression] " + report.status + "; checks=" + checks);
        EditorApplication.Exit(report.status == "passed" ? 0 : 1);
    }

    private static void ExerciseSurfaceHeight(Report report)
    {
        using (var f = new Fixture("cabinet"))
        {
            f.Box("Cabinet", new Vector3(0, .75f, 0), new Vector3(1, 1.5f, 1)); f.Build();
            Vector3 start = new Vector3(-3, 0, 0), surface = new Vector3(-.5f, 1.2f, 0);
            Check(f.Plan(start, surface, out var plan, out string reason, out var evidence), "Cabinet floor station: " + reason);
            ValidatePlan(f, plan);
            Check(Mathf.Abs(plan.GroundTarget.y) < .005f && plan.FacePoint == surface, "Target Y is floor; gaze Y remains visible surface height.");
            Check(plan.GroundTarget.x < -.8f, "Visible cabinet face is approached from the available floor side.");
            Check(evidence.candidateCount <= 24 && evidence.acceptedCount > 0, "Bounded observed candidates, not unlimited reachability claim.");
            Check(f.Plan(plan.GroundTarget, surface, out var near, out reason, out _), "Already-near plan: " + reason);
            Check(Vector3.Distance(near.GroundTarget, plan.GroundTarget) < .001f, "Already comfortably near does not introduce a gratuitous root step.");
            Check(f.Plan(start, new Vector3(0, 1.5f, 0), out var top, out reason, out _), "Elevated visible surface has a floor station: " + reason);
            Check(Mathf.Abs(top.GroundTarget.y) < .005f, "Top of cabinet is never passed to locomotion as foot height.");
            report.fixtures.Add("elevated cabinet face/top, floor station and already-near facing");
        }
    }

    private static void ExerciseUserClearance(Report report)
    {
        using (var f = new Fixture("user-on-direct-route"))
        {
            f.User.transform.position = new Vector3(-1.7f, 1.6f, 0); f.Build();
            bool accepted = f.Plan(new Vector3(-3, 0, 0), new Vector3(0, 1, 0), out var plan, out _, out var evidence);
            Check(evidence.userClearanceRejected > 0, "Direct candidates record the real user's reserved body disk.");
            if (accepted) ValidatePlan(f, plan);
            Check(!accepted || plan.GroundTarget.x > f.User.transform.position.x, "User safety does not substitute user position as the object destination.");
            report.fixtures.Add("current user body clearance during object approach");
        }
    }

    private static void ExerciseOtherHeightAndDisconnected(Report report)
    {
        using (var f = new Fixture("raised-platform"))
        {
            f.Box("RaisedPlatform", new Vector3(0, .3f, 0), new Vector3(4, .6f, 4)); f.Build();
            Check(!f.Plan(new Vector3(-4, 0, 0), new Vector3(0, 1.5f, 0), out _, out string reason, out var evidence), "Raised stations must not become same-level foot destinations.");
            Check(evidence.code == "bounded-candidates-rejected" && !string.IsNullOrEmpty(reason), "Failure stays bounded and actionable.");
        }
        using (var f = new Fixture("separated-floor"))
        {
            f.Box("FullDivider", new Vector3(0, 1.5f, 0), new Vector3(.4f, 3, 12)); f.Build();
            Check(!f.Plan(new Vector3(-3, 0, 0), new Vector3(3, 1.2f, 0), out _, out _, out _), "Seen point across impassable divider cannot teleport the actor.");
        }
        report.fixtures.Add("raised surface and separated NavMesh paths are rejected");
    }

    private static void ExerciseUserSupportNotGoal(Report report)
    {
        using (var f = new Fixture("user-over-sofa"))
        {
            f.Box("SofaUnderUser", new Vector3(3, .35f, 3), new Vector3(1.4f, .7f, 1.4f)); f.Build();
            Vector3 start = new Vector3(-3, 0, 0), surface = new Vector3(0, 1.2f, 0);
            Check(f.Plan(start, surface, out var sofaPlan, out string reason, out var evidence),
                "User over furniture does not prevent a clear object-directed route: " + reason);
            ValidatePlan(f, sofaPlan);
            Check(evidence.userClearance.valid && evidence.userClearance.center.y == sofaPlan.GroundTarget.y &&
                evidence.userClearance.trackedHead == f.User.transform.position,
                "User clearance is explicitly an XZ disk on the actor plane, not certified feet.");
            Check(!ArdyRoomApproachPlanner.TryPlan(f.Navigation, start, f.User.transform, 1f, out _, out _),
                "Approach-user retains its own floor/same-level requirement over the sofa.");

            f.User.transform.position = new Vector3(25, 7, 25); Physics.SyncTransforms();
            Check(!ArdyRoomApproachPlanner.TryGetUserGround(f.Navigation, f.User.transform, out _, out _),
                "Far tracked view has no floor support in the fixture.");
            Check(f.Plan(start, surface, out var airbornePlan, out reason, out _),
                "Tracked user far outside the floor does not prevent object approach: " + reason);
            ValidatePlan(f, airbornePlan);
            Check(!ArdyRoomApproachPlanner.TryPlan(f.Navigation, start, f.User.transform, 1f, out _, out _),
                "Approach-user still rejects the unsupported user destination.");

            f.User.transform.position = new Vector3(-1.7f, 9, 0);
            bool accepted = f.Plan(start, surface, out var blockedPlan, out _, out var blocked);
            Check(blocked.userClearanceRejected > 0,
                "Unsupported high user anchor still reserves XZ space in object routes.");
            if (accepted) ValidatePlan(f, blockedPlan);
            Check(ArdyRoomObjectApproachPlanner.TryGetUserClearanceCenter(f.User.transform, 0, out var center, out _) &&
                !ArdyRoomApproachPlanner.StepKeepsUserClearance(start, Vector3.zero, center, sofaPlan.UserClearanceRadius),
                "A generated segment through that user's disk is rejected regardless of user height.");
            report.fixtures.Add("sofa/airborne user support is irrelevant to object goals; user-goal and XZ safety checks remain");
        }
    }

    private static void ExerciseInvalidAndChangedGeometry(Report report)
    {
        using (var f = new Fixture("dynamic-obstruction"))
        {
            f.Build();
            Check(!f.Plan(new Vector3(float.NaN, 0, 0), Vector3.one, out _, out _, out _), "Non-finite root rejected.");
            Check(!f.Plan(new Vector3(-3, 0, 0), new Vector3(0, float.PositiveInfinity, 0), out _, out _, out _), "Non-finite visual point rejected.");
            Check(!ArdyRoomObjectApproachPlanner.TryPlan(f.Navigation, new Vector3(-3, 0, 0), Vector3.one, null, .8f, out _, out _, out _), "Missing true user anchor cannot silently disable user safety.");
            Check(f.Plan(new Vector3(-3, 0, 0), new Vector3(0, 1, 0), out var plan, out var reason, out _), "Initial open route: " + reason);
            var blocker = f.Box("NewObstacleAtStation", plan.GroundTarget + Vector3.up * .6f, new Vector3(1, 1.2f, 1));
            Physics.SyncTransforms();
            Check(!f.Navigation.TryPlan(new Vector3(-3, 0, 0), plan.GroundTarget, out _, out _), "Fresh Physics check rejects a new obstacle despite old NavMesh.");
            Object.DestroyImmediate(blocker.gameObject);
            f.User.SetActive(false);
            Check(!f.Plan(new Vector3(-3, 0, 0), Vector3.one, out _, out _, out _), "Disabled user tracking rejects subsequent station planning.");
            report.fixtures.Add("invalid inputs, explicit user ownership, changed scene geometry");
        }
    }

    private static void ExerciseRejectedCallback(Report report)
    {
        var host = new GameObject("DisconnectedBridgeContract");
        try
        {
            var bridge = host.AddComponent<ArdyRoomDialogueBridge>();
            bridge.connectOnStart = false;
            int delivered = 0;
            ArdyRoomDialogueBridge.SceneTargetResult deliveredResult = null;
            bridge.SceneTargetCompleted += value => { delivered++; deliveredResult = value; value.reason = "subscriber-local edit"; };
            Check(!bridge.ApproachSceneTarget("object-action", "capture-a", "surface-a", Vector3.one, () => null, out string reason), "Disconnected bridge rejects action before execution.");
            Check(delivered == 1 && deliveredResult.actionId == "object-action" && deliveredResult.targetKind == "scene-object" && deliveredResult.terminal && !deliveredResult.reached,
                "One terminal result retains object/action/image identity.");
            Check(bridge.LastSceneTargetResult.reason == reason, "Subscriber cannot mutate stored action evidence.");
            bridge.StopMovement();
            Check(delivered == 1, "Later generic stop does not repeat a terminal object result.");
            Check(!bridge.StopForTaskIfCurrent("late-stop", "object-action", 0, out _), "Unowned stale object stop cannot affect later tasks.");
            report.fixtures.Add("bridge rejection identity, exactly-once callback, defensive evidence and stale stop");
        }
        finally { Object.DestroyImmediate(host); }
    }

    private static void ValidatePlan(Fixture fixture, ArdyRoomObjectApproachPlan plan)
    {
        Check(plan != null && plan.Route != null && plan.Route.Corners.Length > 0, "Real planned route exists.");
        Check(ArdyRoomApproachPlanner.RouteKeepsUserClearance(plan.Route, plan.UserClearanceCenter, plan.UserClearanceRadius), "Whole route retains user clearance.");
        Check(fixture.Navigation.ValidateStep(plan.GroundTarget, plan.GroundTarget, out float y, out var reason), "Standpoint footprint is currently clear: " + reason);
        Check(Mathf.Abs(y - plan.GroundTarget.y) <= fixture.Navigation.MaxRouteHeightVariation, "Standpoint is on the verified current floor.");
    }

    /// <summary>
    /// Run synchronously in a dormant real connected isolated Play fixture. Injects only an owned
    /// task snapshot to exercise the exact per-step guard; it does not submit motion or claim playback.
    /// </summary>
    public static int RunRuntimeChecks(ArdyRoomDialogueBridge bridge, string outputDirectory)
    {
        checks = 0;
        var report = new Report { unityVersion = Application.unityVersion,
            scope = "Production connected scene-object per-step and binding guards under controlled task snapshots. Real user-anchor and tracker validity; no generated motion, smooth-stop playback, service or model requests." };
        Check(Application.isPlaying && Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) >= 0 &&
            bridge != null && bridge.IsConnected && !bridge.IsFollowing && !bridge.ReservesBody &&
            !bridge.IsApproachingSceneTarget && !bridge.Controller.IsBusy && !bridge.Controller.IsMoving,
            "Run in the dormant connected isolated room fixture only.");
        var savedFields = new Dictionary<FieldInfo, object>();
        foreach (var field in bridge.GetType().GetFields(Flags))
            if (!field.IsInitOnly && !field.IsPublic) savedFields[field] = field.GetValue(bridge);
        var reports = (Queue<string>)Get(bridge, "actionReports");
        string[] savedReports = reports.ToArray();
        Transform user = bridge.userHead, environment = bridge.environmentRoot;
        Vector3 userPosition = user.position;
        GameObject trackingProbe = null, trackingOrigin = null;
        try
        {
            Vector3 ground = bridge.avatar.transform.position + Vector3.up * bridge.importedReference.groundY * bridge.avatar.transform.lossyScale.y;
            Vector3 stepEnd = ground + Vector3.right * .05f;
            float radius = bridge.Navigation.AgentRadius + ArdyRoomApproachPlanner.UserBodyRadius + ArdyRoomApproachPlanner.UserClearance;
            var scenePlan = new ArdyRoomObjectApproachPlan();
            typeof(ArdyRoomObjectApproachPlan).GetProperty("GroundTarget").SetValue(scenePlan, stepEnd);
            typeof(ArdyRoomObjectApproachPlan).GetProperty("UserClearanceRadius").SetValue(scenePlan, radius);
            Set(bridge, "sceneTargetActive", true); Set(bridge, "activeAction", true);
            Set(bridge, "sceneTargetPlan", scenePlan); Set(bridge, "sceneTargetValidator", (Func<string>)(() => null));
            Set(bridge, "boundUser", user); Set(bridge, "hasControllerOperation", true);
            Set(bridge, "operationRevision", bridge.Controller.OperationRevision);
            Set(bridge, "executionTargetKind", "scene-object");
            Set(bridge, "sceneTargetActionId", "clearance-guard-fixture"); Set(bridge, "actionId", "clearance-guard-fixture");

            user.position = ground + new Vector3(4, 1.6f, 4);
            Check(Guard(bridge, ground, stepEnd) == null, "Far valid user does not block the object step.");
            user.position += Vector3.up * 8f; Physics.SyncTransforms();
            Check(!ArdyRoomApproachPlanner.TryGetUserGround(bridge.Navigation, user, out _, out _),
                "Changed head height has no support within the user-ground validation ray.");
            Check(Guard(bridge, ground, stepEnd) == null,
                "Losing user floor support with unchanged XZ does not falsely stop a committed object step.");
            Check(bridge.IsApproachingSceneTarget && bridge.Controller.OperationRevision == (int)Get(bridge, "operationRevision"),
                "Support-only change preserves owned scene-target identity.");

            user.position = stepEnd + Vector3.up * 9f;
            Check(Guard(bridge, ground, stepEnd) != null,
                "Moving the unsupported user's XZ into a segment still rejects the moving step.");
            user.position = ground + new Vector3(4, 9.6f, 4);
            Check(Guard(bridge, ground, stepEnd) == null, "A fresh guard no longer reports overlap after the user leaves the disk.");

            trackingProbe = new GameObject("ObjectApproachInvalidTrackingProbe");
            var tracking = trackingProbe.AddComponent<PlayerCameraController>();
            trackingOrigin = trackingProbe.transform.parent != null ? trackingProbe.transform.parent.gameObject : null;
            trackingProbe.transform.position = user.position;
            bridge.userHead = trackingProbe.transform;
            Check(Guard(bridge, ground, stepEnd) != null, "Changing user anchor binding invalidates the committed object task.");
            Set(bridge, "boundUser", trackingProbe.transform);
            tracking.enabled = false;
            Check(Guard(bridge, ground, stepEnd) != null,
                "An active head GameObject with invalid tracking does not silently disable user clearance.");
            trackingProbe.SetActive(false);
            Check(Guard(bridge, ground, stepEnd) != null, "Inactive user anchor invalidates the object task.");
            bridge.userHead = user; Set(bridge, "boundUser", user);
            Check(Guard(bridge, ground, stepEnd) == null, "Restored valid binding is a positive control.");

            bridge.environmentRoot = trackingProbe.transform;
            Check(Guard(bridge, ground, stepEnd) != null, "Changed room binding still rejects the object step.");
            bridge.environmentRoot = environment;
            Set(bridge, "sceneTargetValidator", (Func<string>)(() => "fixture: target geometry changed"));
            Check(Guard(bridge, ground, stepEnd) == "fixture: target geometry changed", "Target geometry validity remains mandatory.");
            Set(bridge, "operationRevision", bridge.Controller.OperationRevision - 1);
            Check(Guard(bridge, ground, stepEnd) == null, "An old object task does not audit or cancel a foreign controller operation.");
            report.fixtures.Add("committed per-step guard: support-only change, current XZ overlap, tracking and binding loss, target geometry and operation ownership");
            report.status = "passed";
        }
        catch (Exception error) { report.status = "failed"; report.error = error.ToString(); throw; }
        finally
        {
            bridge.userHead = user; bridge.environmentRoot = environment; user.position = userPosition;
            foreach (var value in savedFields) value.Key.SetValue(bridge, value.Value);
            reports.Clear(); foreach (string item in savedReports) reports.Enqueue(item);
            if (trackingOrigin != null) Object.DestroyImmediate(trackingOrigin);
            else if (trackingProbe != null) Object.DestroyImmediate(trackingProbe);
            Physics.SyncTransforms();
            report.checks = checks;
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(Path.Combine(outputDirectory, "object-approach-user-clearance-runtime.json"), JsonUtility.ToJson(report, true));
        }
        Debug.Log("[ArdyRoomObjectApproachRuntimeRegression] passed " + checks + " checks.");
        return checks;
    }

    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static string Guard(ArdyRoomDialogueBridge bridge, Vector3 from, Vector3 to)
    {
        try { return (string)bridge.GetType().GetMethod("GuardSceneTargetStep", Flags).Invoke(bridge, new object[] { from, to }); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }

    private static string Argument(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
        return null;
    }
    private static void Check(bool valid, string message) { checks++; if (!valid) throw new InvalidOperationException(message); }
}
