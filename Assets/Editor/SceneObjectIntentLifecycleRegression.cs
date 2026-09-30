using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Model callbacks are controlled. RGB/ID frames and same-entity renewal are real;
// this tests program boundaries, not semantic accuracy or a completed device action.
public static class SceneObjectIntentLifecycleRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static JObject LastReport { get; private set; }

    public static int RunPureChecks()
    {
        int count = 0;
        var host = new GameObject("scene-object-source-regression"); host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            var ledger = (ArdyActionConstraintLedger)Get(chat, "m_ActionConstraints");
            string user = "不是显示器，是我刚才指定的小装饰灯。" + new string('长', 1300) + "末尾：不要关闭屏幕。";
            ledger.RecordUserTurn(user);
            var decision = new RoomTaskProtocol.Decision { origin = "actor_draft", intent = "inspect-object",
                visualTarget = new RoomTaskProtocol.VisualTarget { label = "发光的屏幕" } };
            typeof(RoomTaskProtocol.Decision).GetProperty("SourceText", Flags).SetValue(decision, "我去关掉发光的屏幕。", null);
            var facts = (JObject)Call(chat, "SceneObjectGoalSources", decision);
            Require((string)facts["currentUserContext"] == user, "Complete user source including final correction was truncated."); count++;
            Require((string)facts["roleDecision"] == decision.SourceText && (string)facts["plannerSelectionDescription"] == "发光的屏幕",
                "Role decision and planner hypothesis were conflated with user intent."); count++;
            string combined = (string)Call(chat, "SceneObjectGoal", decision);
            Require(combined.Contains("Current user target context") && combined.Contains("Role decision") &&
                combined.Contains("不是显示器"), "Actor draft replaced the actual target source."); count++;
            typeof(RoomTaskProtocol.Decision).GetProperty("SourceText", Flags).SetValue(decision, "我现在不去关灯，我决定只看一下旁边的书。", null);
            facts = (JObject)Call(chat, "SceneObjectGoalSources", decision);
            Require((string)facts["roleDecision"] == decision.SourceText && (string)facts["currentUserContext"] == user,
                "User context silently rewrote the role's refusal or separate chosen action."); count++;
            decision.origin = "autonomous"; Set(chat, "m_SceneAutonomyIntention", "我自主想观察书脊。");
            Require((string)Call(chat, "SceneObjectGoal", decision) == "我自主想观察书脊。", "Autonomous goal was replaced by unrelated prior user text."); count++;
            decision.origin = "user";
            facts = (JObject)Call(chat, "SceneObjectGoalSources", decision);
            Require((string)facts["roleDecision"] == "" && (string)facts["currentUserContext"] == user,
                "User-origin decision invented a role adoption from an old source string."); count++;
            object[] arguments = { null, null, null, null };
            Require(!(bool)CallStatic(typeof(ChatSample), "TryGetSceneObjectPowerCapability", arguments), "Missing identity exposed device control."); count++;
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        return count;
    }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Run only in the existing isolated validation Play fixture.");
        checks = 0;
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(),
            ["scope"] = "Actual room RGB/ID frames and production matcher/capability entry with controlled semantic replies. No model HTTP, navigation service or power operation. Temporary avatar offsets are test-only and restored; no scene saved." };
        Vector3 position = bridge.avatar.transform.position; Quaternion rotation = bridge.avatar.transform.rotation;
        bool awareness = (bool)Get(chat, "m_EnableSceneObjectAwareness"), matching = (bool)Get(chat, "m_EnableSceneObjectTargetMatching");
        var stack = new System.Collections.Generic.Stack<IEnumerator>();
        try
        {
            stack.Push(Exercise(chat, bridge, model));
            while (stack.Count > 0)
            {
                object next = null;
                while (stack.Count > 0)
                {
                    var current = stack.Peek();
                    if (!current.MoveNext()) { (current as IDisposable)?.Dispose(); stack.Pop(); continue; }
                    if (current.Current is IEnumerator nested) { stack.Push(nested); continue; }
                    next = current.Current; break;
                }
                if (stack.Count > 0) yield return next;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            Call(chat, "CancelSceneObjectTargetMatch"); Call(chat, "CancelSceneAction", "intent-lifecycle-regression-finished");
            bridge.avatar.transform.SetPositionAndRotation(position, rotation);
            Set(chat, "m_EnableSceneObjectAwareness", awareness); Set(chat, "m_EnableSceneObjectTargetMatching", matching);
            LastReport["checks"] = checks;
            if ((string)LastReport["status"] == "running") LastReport["status"] = "failed";
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "vision-intent-lifecycle.json"), LastReport.ToString());
        }
    }

    private static IEnumerator Exercise(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        yield return (IEnumerator)typeof(SceneObjectReferenceRegression).GetMethod("Prepare", Flags).Invoke(null, new object[] { chat });
        Set(chat, "m_EnableSceneObjectAwareness", true); Set(chat, "m_EnableSceneObjectTargetMatching", true);
        Check(bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing, "Connected idle room required.");
        var source = chat.SceneVisionCapture;
        SceneVisionObservation image = null;
        yield return Capture(chat, value => image = value);
        var ordinary = FindOrdinary(source, image);
        Call(chat, "RememberSceneObject", ordinary, null);
        object[] capability = { ordinary, bridge.environmentRoot, null, null };
        Check(!(bool)CallStatic(typeof(ChatSample), "TryGetSceneObjectPowerCapability", capability) &&
            (string)capability[3] == "selected-object-has-no-registered-power-capability", "Visible ordinary renderer acquired power capability.");
        Check(source.TryBuildObjectCandidateView(image, ordinary.ImagePoint, ordinary.EntityId, out var view, out string why), "Candidate metadata unavailable: " + why);
        var offered = view.Candidates.Single(c => c.entityId == ordinary.EntityId);
        Check(offered.registeredCapabilities.SequenceEqual(new[] { "inspect-object", "look-at" }), "Ordinary entity advertised an unregistered state operation.");

        const string request = "请关闭这个已经观察到的物体。";
        Call(chat, "RecordAcceptedMotionUserTurn", request);
        var decision = new RoomTaskProtocol.Decision { intent = "set-object-state", origin = "user", evidence = request,
            objectState = "off", entityTarget = new RoomTaskProtocol.EntityTarget { captureId = image.CaptureId, entityId = ordinary.EntityId } };
        int before = model.Matches.Count; bool complete = false;
        Call(chat, "BeginSceneObjectAction", decision, image, (int)Get(chat, "m_FormalResponseGeneration"), new Action(() => complete = true));
        Check(complete && model.Matches.Count == before && ((string)Get(chat, "m_RoomTaskDispatch")).Contains("no-registered-power-capability"),
            "Known entity without power interface spent a matcher request or began head motion.");
        Case("known-entity-capability-fails-before-matcher");

        yield return Capture(chat, value => image = value);
        ordinary = FindOrdinary(source, image);
        Call(chat, "RecordAcceptedMotionUserTurn", request);
        decision = new RoomTaskProtocol.Decision { intent = "set-object-state", origin = "user", evidence = request,
            objectState = "off", visualTarget = new RoomTaskProtocol.VisualTarget { captureId = image.CaptureId,
                x = ordinary.ImagePoint.x, y = ordinary.ImagePoint.y, label = "规划器误称可关闭的设备" } };
        before = model.Matches.Count; complete = false;
        Call(chat, "BeginSceneObjectAction", decision, image, (int)Get(chat, "m_FormalResponseGeneration"), new Action(() => complete = true));
        yield return Wait(() => complete || model.Matches.Count > before, 5f, "Pixel selection semantic review");
        Check(!complete && model.Matches.Count == before + 1, "Pixel hypothesis should reach one semantic review before capability verification.");
        var ordinaryCandidate = chat.LastSceneObjectCandidateView.Candidates.Single(c => c.entityId == ordinary.EntityId);
        model.Matches.Last().Complete(true, new JObject { ["verdict"] = "matched", ["candidateId"] = ordinaryCandidate.candidateId,
            ["reason"] = "Controlled incorrect semantic answer calls this ordinary renderer the requested device." }.ToString(), "controlled-nondevice");
        yield return Wait(() => complete, 3f, "Matched nondevice capability rejection");
        var rejectedFact = Get(chat, "m_SceneActionFact");
        Check((string)Get(rejectedFact, "phase") == "rejected" &&
            ((string)Get(rejectedFact, "reason")).Contains("no-registered-power-capability"),
            "A model's matched label granted power control to an ordinary renderer.");
        Check(string.IsNullOrEmpty((string)Get(rejectedFact, "confirmedCaptureId")) && !(bool)Get(rejectedFact, "stateChanged") &&
            model.Matches.Count == before + 1, "Invalid capability reached head confirmation, device operation or a repeated matcher request.");
        Case("matched-nondevice-fails-before-head-confirmation");

        yield return Capture(chat, value => image = value);
        ordinary = FindOrdinary(source, image);
        Check(source.TryBuildObjectCandidateView(image, ordinary.ImagePoint, ordinary.EntityId, out view, out why), "Fresh diagnostic candidate view unavailable: " + why);
        offered = view.Candidates.Single(c => c.entityId == ordinary.EntityId);

        Vector3 originalPosition = bridge.avatar.transform.position; Quaternion originalRotation = bridge.avatar.transform.rotation;
        try
        {
            bridge.avatar.transform.position = originalPosition - image.CameraRotation * Vector3.forward * .40f;
            var translation = source.DescribeViewCompatibility(image, .35f, 25f);
            Check(!translation.compatible && translation.bindingCurrent && translation.poseAvailable && translation.translationMeters > .35f &&
                translation.maximumTranslationMeters == .35f, "Translation failure lacks actual pose delta or weakened its threshold.");
            Check(!view.TryGetCandidate(offered.candidateId, out _, out _), "Old candidate became executable despite translation drift.");
            object[] historical = { offered.candidateId, null, null };
            Check((bool)Call(view, "TryGetHistoricalCandidateForReobservation", historical) && ((SceneObservedObject)historical[1]).EntityId == ordinary.EntityId,
                "Historical identity needed for reobservation was confused with a current-view permit.");
            bridge.avatar.transform.position = originalPosition;
            bridge.avatar.transform.rotation = Quaternion.AngleAxis(31f, Vector3.up) * originalRotation;
            var angle = source.DescribeViewCompatibility(image, .35f, 25f);
            Check(!angle.compatible && angle.rotationDegrees > 25f && angle.maximumRotationDegrees == 25f,
                "Rotation drift lacks diagnostics or weakened the 25 degree gate.");
            LastReport["translationDiagnostic"] = JObject.FromObject(translation);
            LastReport["rotationDiagnostic"] = JObject.FromObject(angle);
        }
        finally { bridge.avatar.transform.SetPositionAndRotation(originalPosition, originalRotation); }
        Case("view-delta-diagnostics-do-not-relax-current-gate");

        yield return Capture(chat, value => image = value);
        ordinary = FindOrdinary(source, image);
        const string inspect = "请检查当前选中的物体。";
        Call(chat, "RecordAcceptedMotionUserTurn", inspect);
        decision = new RoomTaskProtocol.Decision { origin = "user", intent = "inspect-object", evidence = inspect,
            visualTarget = new RoomTaskProtocol.VisualTarget { captureId = image.CaptureId, x = ordinary.ImagePoint.x, y = ordinary.ImagePoint.y, label = "当前物体" } };
        var fact = Activator.CreateInstance(typeof(ChatSample).GetNestedType("SceneActionFact", Flags), true);
        Set(fact, "actionId", "intent-lifecycle-drift"); Set(fact, "intent", "inspect-object");
        bool active = true, done = false; SceneObservedObject matched = null; string failure = null;
        before = model.Matches.Count;
        var routine = chat.StartCoroutine((IEnumerator)Call(chat, "MatchSceneObjectTarget", decision, image, ordinary, fact,
            new Func<bool>(() => active), new Action<SceneObservedObject, string>((value, reason) => { matched = value; failure = reason; done = true; })));
        try
        {
            yield return Wait(() => model.Matches.Count > before || done, 5f, "Candidate matcher request");
            Check(!done && model.Matches.Count == before + 1, "Expected one controlled matcher request.");
            var candidate = chat.LastSceneObjectCandidateView.Candidates.Single(c => c.entityId == ordinary.EntityId);
            bridge.avatar.transform.position = originalPosition - image.CameraRotation * Vector3.forward * .40f;
            Check(!source.IsCurrentViewCompatible(image, .35f, 25f), "Controlled drift failed to cross the actual gate.");
            model.Matches.Last().Complete(true, new JObject { ["verdict"] = "matched", ["candidateId"] = candidate.candidateId,
                ["reason"] = "Controlled semantic interpretation names this exact original-image entity." }.ToString(), "controlled-drift");
            yield return Wait(() => done, 6f, "One same-entity new-frame renewal");
            Check(failure == null && matched != null && matched.Root == ordinary.Root && matched.EntityId == ordinary.EntityId && matched.CaptureId != image.CaptureId,
                "Drift renewal failed or silently substituted another entity: " + failure);
            Check(source.TryFindObservedObject(source.LatestObservation, ordinary.EntityId, out _, out _) && model.Matches.Count == before + 1,
                "Renewal reused stale pixels or added another model retry.");
            var receipt = (JObject)Call(chat, "SceneObjectTargetContext");
            Check((bool?)receipt["reobservedAfterMatch"] == true && (string)receipt["captureId"] == image.CaptureId &&
                (string)receipt["acceptedCaptureId"] == matched.CaptureId, "Original semantic image and new accepted observation lost separate provenance.");
        }
        finally { active = false; chat.StopCoroutine(routine); bridge.avatar.transform.SetPositionAndRotation(originalPosition, originalRotation); }
        Case("matcher-pose-drift-renews-same-identity-once");
        // Existing VisionActionFreshnessRegression separately exercises genuine 12s expiry,
        // disappearance in the new frame, cancelled callbacks and source rebind rejection.
    }

    private static SceneObservedObject FindOrdinary(NevaSceneVisionCapture source, SceneVisionObservation image)
    {
        for (int y = 30; y <= 70; y += 5) for (int x = 30; x <= 70; x += 5)
            if (source.TryResolveObservedObject(image, new Vector2(x / 100f, y / 100f), out var value, out _) &&
                value.VisibleConfirmed && value.Root != null && value.Root.GetComponent<Renderer>() != null &&
                value.Root.GetComponent<SceneObjectLightSwitch>() == null && value.TryValidate(out _)) return value;
        throw new InvalidOperationException("Fixture needs a visible ordinary opaque renderer.");
    }
    private static IEnumerator Capture(ChatSample chat, Action<SceneVisionObservation> completed)
    {
        SceneVisionObservation image = null;
        Call(chat, "RequestFreshSceneObservation", "intent-lifecycle-regression", new Action<SceneVisionObservation>(value => image = value));
        yield return Wait(() => image != null, 5f, "New real RGB/ID capture");
        Check(image.HasImage && image.HasSurfaceGrounding, "Capture must include actual same-frame geometry."); completed(image);
    }
    private static IEnumerator Wait(Func<bool> done, float seconds, string label)
    { float deadline = Time.realtimeSinceStartup + seconds; while (!done()) { if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(label); yield return null; } }
    private static void Case(string name) => ((JArray)LastReport["cases"]).Add(name);
    private static void Check(bool value, string reason) { checks++; Require(value, reason); }
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] args) => Invoke(target.GetType(), target, name, args);
    private static object CallStatic(Type type, string name, params object[] args) => Invoke(type, null, name, args);
    private static object Invoke(Type type, object target, string name, object[] args)
    {
        try { return type.GetMethods(Flags).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
}
