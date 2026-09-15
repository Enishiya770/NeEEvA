using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NeEEvA.Motion;
using UnityEngine;

/// <summary>Bounded lifecycle checks in an initialized, idle, real boundary fixture. No generated candidate.</summary>
public static class ArdyRoomBoundaryRecoveryOwnershipRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    [Serializable] private sealed class Report
    {
        public bool passed;
        public int checks;
        public string error;
        public string scope = "Real configured controller and boundary geometry; same-frame cancellation of a localhost:1 capability request, plus injected ownership serial observations passed to production checks. Does not generate or play a native motion candidate, simulate the old pose return, or claim visual naturalness.";
        public List<string> cases = new List<string>();
    }
    public static string LastReportJson { get; private set; }

    public static int RunChecks(ArdyRoomLocomotionController controller)
    {
        var report = new Report();
        GameObject fixtureOwners = null;
        Snapshot originalController = null, originalPlayer = null;
        ArdyRoomNavigation navigation = null;
        object savedRoutePlane = null, savedHasRoutePlane = null;
        Vector3 originalRoot = default;
        try
        {
            Check(Application.isPlaying && Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) >= 0 &&
                controller != null && controller.Avatar != null && controller.Player != null && controller.isActiveAndEnabled,
                "Use the isolated initialized boundary Play fixture.");
            Check(!controller.IsBusy && !controller.IsContinuousFollowActive && !controller.IsNavigationRecoveryActive &&
                !controller.Player.HasPoseOwnership && Field(controller, "work") == null && Field(controller, "pending") == null,
                "Ownership checks require a dormant controller, without a live request or body owner.");
            originalRoot = controller.Avatar.transform.position;
            originalController = new Snapshot(controller);
            originalPlayer = new Snapshot(controller.Player);
            navigation = (ArdyRoomNavigation)Field(controller, "navigation");
            savedRoutePlane = Field(navigation, "routeGroundY"); savedHasRoutePlane = Field(navigation, "hasRoutePlane");
            Vector3 ground = (Vector3)Call(controller, "AvatarGround");
            Check(navigation.TryPlanBoundaryRecovery(ground, out var certificate, out string reason),
                "Fixture must retain a real safely recoverable invalid start: " + reason);

            fixtureOwners = new GameObject("ARDY isolated recovery ownership probes") { hideFlags = HideFlags.DontSave };
            fixtureOwners.transform.SetParent(controller.Avatar.transform, false);
            var gesture = fixtureOwners.AddComponent<ArdyMotionPlayer>();
            var producer = fixtureOwners.AddComponent<ArdyLiveMotionController>();
            var gestureSnapshot = new Snapshot(gesture);
            var producerSnapshot = new Snapshot(producer);

            Case("accepted-recovery-same-frame-cancel-invalidates-token-and-worker", () => {
                controller.Endpoint = "http://127.0.0.1:1";
                int previous = controller.Revision;
                Check(controller.TryBeginNavigationRecovery(originalRoot + controller.Avatar.transform.forward, out string failure), failure);
                int token = controller.OperationRevision;
                Check(token > previous && controller.IsNavigationRecoveryActive && Field(controller, "pending") != null &&
                    string.IsNullOrEmpty(controller.LastRequestJson),
                    "Expected only the capability request to start before cancellation; native generation is forbidden here.");
                IEnumerator stale = (IEnumerator)Call(controller, "GenerateBoundaryRecovery", token);
                controller.Cancel();
                int cancelled = controller.Revision;
                Check(cancelled > token && !(bool)Call(controller, "Current", token) && !controller.IsNavigationRecoveryActive &&
                    !controller.IsBusy && Field(controller, "pending") == null && Field(controller, "work") == null &&
                    controller.ExecutionState == ArdyRoomExecutionState.Cancelled && controller.ExecutionReason == ArdyRoomExecutionReason.CancelledByCaller,
                    "Cancel failed to revoke the operation, dispose the request or clear recovery authority.");
                Check(!stale.MoveNext() && controller.Revision == cancelled && Field(controller, "pending") == null &&
                    !controller.Player.HasPoseOwnership && controller.Avatar.transform.position == originalRoot,
                    "A cancelled recovery iterator resumed work or changed the actual root.");
                (stale as IDisposable)?.Dispose();
            });

            Case("initial-foreign-full-body-owner-is-rejected-without-cancellation", () => {
                SetProperty(controller.Player, "HasPoseOwnership", true);
                SetField(controller, "ownedClip", null);
                int revision = controller.Revision, operation = controller.OperationRevision;
                Check(!controller.TryBeginNavigationRecovery(originalRoot + Vector3.forward, out string failure) && !string.IsNullOrEmpty(failure) &&
                    controller.Revision == revision && controller.OperationRevision == operation && controller.Player.HasPoseOwnership &&
                    Field(controller, "pending") == null && Field(controller, "work") == null,
                    "Initial foreign ownership was cancelled, replaced or allowed to dispatch a request.");
            });

            Case("waiting-recovery-rejects-full-body-revision-change-even-after-release", () => {
                ArmWaiting();
                SetProperty(controller.Player, "PlaybackRevision", controller.Player.PlaybackRevision + 1);
                OwnershipLost("A changed full-body revision was accepted merely because no pose remained owned.");
            });
            Case("waiting-recovery-rejects-new-active-full-body-owner", () => {
                ArmWaiting();
                SetProperty(controller.Player, "HasPoseOwnership", true);
                OwnershipLost("A new active body owner was accepted during recovery generation.");
            });
            Case("waiting-recovery-rejects-gesture-serial-change-after-gesture-ended", () => {
                ArmWaiting();
                SetProperty(gesture, "MotionPlaybackSerial", gesture.MotionPlaybackSerial + 1);
                Check(!gesture.IsPlaying, "The serial case must detect an already-ended gesture, not only an active clip.");
                OwnershipLost("A replaced gesture serial was ignored once its clip had ended.");
            });
            Case("waiting-recovery-rejects-producer-revision-change-before-clip-exists", () => {
                ArmWaiting();
                SetField(producer, "revision", producer.Revision + 1);
                Check(string.IsNullOrEmpty(producer.DescribeActiveMotion()), "The producer case must isolate its revision check.");
                OwnershipLost("A newly revised gesture producer was ignored before an active clip existed.");
            });
            report.passed = true;

            void ArmWaiting()
            {
                SetField(controller, "boundaryRecovery", certificate);
                SetField(controller, "boundaryRecoveryWaitingForRelease", false);
                SetField(controller, "boundaryRecoveryIdlePlayerRevision", controller.Player.PlaybackRevision);
                Call(controller, "CheckBoundaryGestureOwners", true);
                Call(controller, "CheckBoundaryRecoveryWaitingOwner");
            }
            void OwnershipLost(string message)
            {
                int revision = controller.Revision;
                Exception rejected = null;
                try { Call(controller, "CheckBoundaryRecoveryWaitingOwner"); }
                catch (Exception error) { rejected = error; }
                Check(rejected != null && rejected.GetType().Name == "ExecutionException" &&
                    (ArdyRoomExecutionReason)Field(rejected, "Reason") == ArdyRoomExecutionReason.OwnershipLost,
                    message + " Actual error: " + rejected);
                Check(controller.Revision == revision && Field(controller, "pending") == null && Field(controller, "work") == null &&
                    controller.Avatar.transform.position == originalRoot,
                    "The ownership check itself cancelled an owner, dispatched work or wrote the root.");
            }
            void Case(string name, Action body)
            {
                var controllerSnapshot = new Snapshot(controller);
                var playerSnapshot = new Snapshot(controller.Player);
                try { body(); report.cases.Add(name); }
                finally
                {
                    // Restore injected owner observations before any worker cleanup; no real
                    // foreign playback is cancelled or fabricated by these isolated probes.
                    playerSnapshot.Restore(); gestureSnapshot.Restore(); producerSnapshot.Restore();
                    if (Field(controller, "pending") != null || Field(controller, "work") != null) controller.Cancel();
                    controllerSnapshot.Restore();
                }
                Check(controller.Avatar.transform.position == originalRoot &&
                    !controller.Player.HasPoseOwnership && Field(controller, "pending") == null && Field(controller, "work") == null,
                    "Case failed to restore the dormant native fixture: " + name);
            }
        }
        catch (Exception error) { report.error = error.ToString(); throw; }
        finally
        {
            originalPlayer?.Restore();
            if (originalController != null && controller != null && (Field(controller, "pending") != null || Field(controller, "work") != null)) controller.Cancel();
            originalController?.Restore();
            if (navigation != null)
            { SetField(navigation, "routeGroundY", savedRoutePlane); SetField(navigation, "hasRoutePlane", savedHasRoutePlane); }
            if (fixtureOwners != null) UnityEngine.Object.DestroyImmediate(fixtureOwners);
            LastReportJson = JsonUtility.ToJson(report, true);
        }
        return report.checks;

        void Check(bool condition, string message) { report.checks++; if (!condition) throw new InvalidOperationException(message); }
    }

    private sealed class Snapshot
    {
        private readonly object target;
        private readonly Dictionary<FieldInfo, object> fields = new Dictionary<FieldInfo, object>();
        private readonly List<(IList target, object[] values)> lists = new List<(IList, object[])>();
        private readonly List<(IDictionary target, List<DictionaryEntry> values)> dictionaries = new List<(IDictionary, List<DictionaryEntry>)>();
        public Snapshot(object value)
        {
            target = value;
            foreach (var field in value.GetType().GetFields(Flags))
            {
                object item = field.GetValue(value);
                if (!field.IsInitOnly) fields[field] = item;
                if (item is IDictionary map && !map.IsReadOnly && !map.IsFixedSize)
                {
                    var entries = new List<DictionaryEntry>();
                    foreach (DictionaryEntry entry in map) entries.Add(entry);
                    dictionaries.Add((map, entries));
                }
                else if (item is IList list && !list.IsReadOnly && !list.IsFixedSize)
                { var values = new object[list.Count]; list.CopyTo(values, 0); lists.Add((list, values)); }
            }
        }
        public void Restore()
        {
            foreach (var item in fields) item.Key.SetValue(target, item.Value);
            foreach (var item in lists) { item.target.Clear(); foreach (var value in item.values) item.target.Add(value); }
            foreach (var item in dictionaries) { item.target.Clear(); foreach (var value in item.values) item.target.Add(value.Key, value.Value); }
        }
    }
    private static object Field(object target, string name) =>
        (target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().Name, name)).GetValue(target);
    private static void SetField(object target, string name, object value) =>
        (target.GetType().GetField(name, Flags) ?? throw new MissingFieldException(target.GetType().Name, name)).SetValue(target, value);
    private static void SetProperty(object target, string name, object value) =>
        (target.GetType().GetProperty(name, Flags) ?? throw new MissingMemberException(target.GetType().Name, name)).SetValue(target, value);
    private static object Call(object target, string name, params object[] arguments)
    {
        try { return (target.GetType().GetMethod(name, Flags) ?? throw new MissingMethodException(target.GetType().Name, name)).Invoke(target, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
}
