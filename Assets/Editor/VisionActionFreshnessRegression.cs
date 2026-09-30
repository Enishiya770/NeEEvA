using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Real RGB/ID frames and controlled matcher callbacks; no model or motion service requests.</summary>
public static class VisionActionFreshnessRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use the isolated real-room Play fixture, never the user's scene.");
        checks = 0;
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(),
            ["scope"] = "Production capture, moving-focus sampling and semantic matching with actual room RGB/IDs. Controlled in-process matcher replies only; no model HTTP, locomotion request or timestamp fabrication." };
        bool continuous = (bool)Get(chat, "m_ContinuousSceneVision");
        bool awareness = (bool)Get(chat, "m_EnableSceneObjectAwareness");
        bool matching = (bool)Get(chat, "m_EnableSceneObjectTargetMatching");
        float maximumAge = (float)Get(chat, "m_SceneObservationMaxAgeSeconds");
        var stack = new Stack<IEnumerator>();
        try
        {
            yield return (IEnumerator)typeof(SceneObjectReferenceRegression).GetMethod("Prepare", Flags).Invoke(null, new object[] { chat });
            Set(chat, "m_ContinuousSceneVision", true);
            Set(chat, "m_SceneObservationMaxAgeSeconds", 12f);
            Set(chat, "m_EnableSceneObjectTargetMatching", true);
            Check(bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing, "Real room bridge is connected and idle.");
            stack.Push(Exercise(chat, bridge, model));
            while (stack.Count > 0)
            {
                object value = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var routine = stack.Peek();
                        if (!routine.MoveNext()) { (routine as IDisposable)?.Dispose(); stack.Pop(); continue; }
                        if (routine.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        value = routine.Current; break;
                    }
                }
                catch (Exception error) { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
                if (stack.Count > 0) yield return value;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            Call(chat, "CancelSceneAction", "freshness-regression-finished");
            Call(chat, "ClearScheduledAgentWake");
            Set(chat, "m_ContinuousSceneVision", continuous);
            Set(chat, "m_EnableSceneObjectAwareness", awareness);
            Set(chat, "m_EnableSceneObjectTargetMatching", matching);
            Set(chat, "m_SceneObservationMaxAgeSeconds", maximumAge);
            chat.SceneVisionCapture?.SetContinuousObservation(continuous);
            LastReport["checks"] = checks;
            if ((string)LastReport["status"] == "running") LastReport["status"] = "failed";
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "vision-action-freshness.json"), LastReport.ToString());
        }
    }

    private static IEnumerator Exercise(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        Vector3 root = bridge.avatar.transform.position;
        Quaternion rotation = bridge.avatar.transform.rotation;
        int operation = bridge.Controller.OperationRevision;
        SceneVisionObservation source = null;
        yield return Capture(chat, "freshness-source", value => source = value);
        SceneVisionObservation cached = null;
        Call(chat, "RequestSceneObservation", "freshness-cached-control", new Action<SceneVisionObservation>(value => cached = value));
        Check(cached != null && cached.CaptureId == source.CaptureId,
            "Ordinary continuous-observation consumers still reuse the completed fresh frame synchronously.");
        SceneVisionObservation requested = null;
        float requestedAt = Time.realtimeSinceStartup;
        Call(chat, "RequestFreshSceneObservation", "freshness-after-request", new Action<SceneVisionObservation>(value => requested = value));
        Check(requested == null, "A required-new-frame request cannot return the pre-request cache synchronously.");
        yield return Wait(() => requested != null, 5f, "Requested new GPU frame");
        Check(requested.HasImage && requested.CaptureId != cached.CaptureId && requested.CapturedAtRealtime >= requestedAt,
            "Required-new-frame result is a distinct real capture taken at or after the request.");
        Case("continuous-cache-versus-requested-new-frame");

        // Exercise the actual moving-focus call site while the controller is idle.
        // This samples visibility but cannot create a native pose lease or move the actor.
        var selected = FindVisible(chat, requested);
        var anchorType = typeof(ChatSample).GetNestedType("SceneObjectApproachAnchor", Flags);
        var anchor = Activator.CreateInstance(anchorType, Flags, null, new object[] { selected, bridge }, null);
        Set(anchor, "focusRegion", selected.WorldPoint);
        var focus = Activator.CreateInstance(typeof(ChatSample).GetNestedType("SceneObjectApproachFocus", Flags), true);
        Set(focus, "point", selected.WorldPoint);
        Set(focus, "lastSeen", requested.CapturedAtRealtime);
        object fact = NewFact("freshness-moving-focus");
        Set(fact, "entityId", selected.EntityId);
        Set(fact, "trackingFocusWorldPoint", selected.WorldPoint);
        bool active = true;
        Call(chat, "TickSceneObjectApproachFocus", fact, anchor, operation, focus, new Func<bool>(() => active));
        Check((bool)Get(focus, "pending"), "Moving-focus waits for a new image instead of rejecting a reused cache immediately.");
        yield return Wait(() => !(bool)Get(focus, "pending"), 5f, "Moving-focus actual capture callback");
        active = false;
        Check((string)Get(fact, "trackingVisibility") == "visible" && (int)Get(fact, "trackingConfirmedImages") == 1,
            "Production moving-focus confirms the same entity from the requested new frame.");
        Check((int)Get(fact, "trackingMovingConfirmedImages") == 0 && !bridge.Controller.IsMoving,
            "Sampling while idle does not claim movement or a native gaze application.");
        Case("moving-focus-continuous-cache-does-not-withdraw-visibility");

        // Let a real picture age as it does during FirstReply plus room planning.
        yield return Capture(chat, "freshness-before-serial-planning", value => source = value);
        selected = FindVisible(chat, source);
        while (Time.realtimeSinceStartup - source.CapturedAtRealtime < 7.05f) yield return null;
        Check((bool)Call(chat, "NeedsSceneObjectMatchRefresh", source) && selected.TryValidate(out _),
            "A genuinely aged but still valid source needs renewal before the matcher.");
        var decision = Decision(source, selected);
        Call(chat, "RecordAcceptedMotionUserTurn", decision.evidence);
        fact = NewFact("freshness-aged-match");
        bool done = false, current = true;
        SceneObservedObject matched = null; string failure = null;
        int count = model.Matches.Count;
        var matcher = chat.StartCoroutine((IEnumerator)Call(chat, "MatchSceneObjectTarget", decision, source, selected, fact,
            new Func<bool>(() => current), new Action<SceneObservedObject, string>((value, why) => { matched = value; failure = why; done = true; })));
        try
        {
            yield return Wait(() => model.Matches.Count > count || done, 5f, "Aged-source renewal and matcher submission");
            Check(!done && model.Matches.Count == count + 1, "Renewal issues exactly one semantic review, without a speculative action.");
            var input = JObject.Parse(model.Matches.Last().Input);
            var view = chat.LastSceneObjectCandidateView;
            var candidate = view.Candidates.Single(value => value.entityId == selected.EntityId);
            Check(view.CaptureId != source.CaptureId && (string)input["candidates"]["captureId"] == view.CaptureId &&
                (string)input["initialGeometryEntityId"] == selected.EntityId,
                "Matcher's RGB/ID metadata use the new capture while retaining the exact entity identity.");
            var currentPoint = input["candidates"]["focusImagePoint"].ToObject<Vector2>();
            Check((currentPoint - candidate.imagePoint).sqrMagnitude < .000001f,
                "Renewed matching focuses the same entity's new-image pixels, not old visualTarget coordinates.");
            model.Matches.Last().Complete(true, Reply(candidate.candidateId), "controlled-freshness");
            yield return Wait(() => done, 3f, "Renewed match completion");
            Check(failure == null && matched != null && matched.EntityId == selected.EntityId && matched.Root == selected.Root &&
                matched.CaptureId == view.CaptureId && matched.TryValidate(out _),
                "A successful current semantic reply returns the same entity bound to its real new frame.");
            Case("aged-plan-renews-same-entity-before-one-match");
        }
        finally { current = false; chat.StopCoroutine(matcher); }

        // A target disappearing after a renewal request cannot be replaced by the old
        // pixel's background, nor can a late callback outlive its current owner.
        yield return Capture(chat, "freshness-hidden-source", value => source = value);
        selected = FindVisible(chat, source);
        bool renewed = false; SceneObservedObject replacement = null; string renewalFailure = null;
        current = true;
        var renewal = chat.StartCoroutine((IEnumerator)Call(chat, "RefreshSceneObjectMatchSource", source, selected,
            new Func<bool>(() => current), new Action<SceneVisionObservation, SceneObservedObject, string>((value, entity, why) => {
                renewed = true; replacement = entity; renewalFailure = why;
            })));
        bool wasActive = selected.Root.gameObject.activeSelf;
        try
        {
            selected.Root.gameObject.SetActive(false);
            yield return Wait(() => renewed, 5f, "Hidden entity renewal refusal");
            Check(replacement == null && !string.IsNullOrEmpty(renewalFailure),
                "Entity lost from the new frame is refused; old pixels cannot select another visible object.");
            Case("same-entity-missing-refuses-renewal");
        }
        finally { current = false; selected.Root.gameObject.SetActive(wasActive); chat.StopCoroutine(renewal); }

        yield return Capture(chat, "freshness-cancel-source", value => source = value);
        selected = FindVisible(chat, source); renewed = false; current = true;
        renewal = chat.StartCoroutine((IEnumerator)Call(chat, "RefreshSceneObjectMatchSource", source, selected,
            new Func<bool>(() => current), new Action<SceneVisionObservation, SceneObservedObject, string>((value, entity, why) => renewed = true)));
        current = false;
        yield return null; yield return null;
        yield return Capture(chat, "freshness-after-cancel", value => requested = value);
        Check(!renewed, "An old refresh callback cannot complete after its owner is cancelled.");
        chat.StopCoroutine(renewal);
        Case("cancelled-renewal-cannot-complete");

        yield return Capture(chat, "freshness-rebind-source", value => source = value);
        selected = FindVisible(chat, source); renewed = false; replacement = null; renewalFailure = null; current = true;
        renewal = chat.StartCoroutine((IEnumerator)Call(chat, "RefreshSceneObjectMatchSource", source, selected,
            new Func<bool>(() => current), new Action<SceneVisionObservation, SceneObservedObject, string>((value, entity, why) => {
                renewed = true; replacement = entity; renewalFailure = why;
            })));
        try
        {
            chat.SceneVisionCapture.SetObserving(false);
            yield return Wait(() => renewed, 5f, "Binding invalidation during renewal");
            Check(replacement == null && renewalFailure == "source-binding-changed-during-refresh",
                "Closing/rebinding capture while renewal is pending cannot transfer an old entity to the new binding.");
            Case("binding-invalidation-refuses-renewal");
        }
        finally { current = false; chat.SceneVisionCapture.SetObserving(true); chat.StopCoroutine(renewal); }

        yield return Capture(chat, "freshness-no-identity-source", value => source = value);
        renewed = false; replacement = null; renewalFailure = null;
        yield return (IEnumerator)Call(chat, "RefreshSceneObjectMatchSource", source, null, new Func<bool>(() => true),
            new Action<SceneVisionObservation, SceneObservedObject, string>((value, entity, why) => {
                renewed = true; replacement = entity; renewalFailure = why;
            }));
        Check(renewed && replacement == null && renewalFailure == "same-visible-entity-required-for-refresh",
            "An unresolved old image region requires a new decision; it cannot reuse pixels on a fresh picture.");
        Case("unresolved-region-cannot-blindly-renew-pixels");

        // Keep the original 12-second guard: deliberately delay the one matcher
        // response until the picture expires, without mutating any timestamp.
        yield return Capture(chat, "freshness-late-match-source", value => source = value);
        selected = FindVisible(chat, source); decision = Decision(source, selected);
        fact = NewFact("freshness-late-match"); done = false; current = true; matched = null; failure = null;
        count = model.Matches.Count;
        matcher = chat.StartCoroutine((IEnumerator)Call(chat, "MatchSceneObjectTarget", decision, source, selected, fact,
            new Func<bool>(() => current), new Action<SceneObservedObject, string>((value, why) => { done = true; matched = value; failure = why; })));
        try
        {
            yield return Wait(() => model.Matches.Count > count, 3f, "Fresh matcher request");
            var candidate = chat.LastSceneObjectCandidateView.Candidates.Single(value => value.entityId == selected.EntityId);
            while (Time.realtimeSinceStartup - source.CapturedAtRealtime < 12.05f) yield return null;
            model.Matches.Last().Complete(true, Reply(candidate.candidateId), "controlled-late-freshness");
            yield return Wait(() => done, 2f, "Expired semantic reply refusal");
            Check(matched == null && (string)Get(fact, "targetMatchVerdict") == "expired" &&
                (failure ?? "").StartsWith("object-target-expired", StringComparison.Ordinal) && model.Matches.Count == count + 1,
                "Expired response authorizes no object and creates no automatic retry or extra matcher request.");
            Case("matcher-expiry-is-still-enforced");
        }
        finally { current = false; chat.StopCoroutine(matcher); }
        Check(bridge.avatar.transform.position == root && bridge.avatar.transform.rotation == rotation &&
            bridge.Controller.OperationRevision == operation && !bridge.ReservesBody,
            "All freshness cases leave the real actor and locomotion controller untouched.");
    }

    private static RoomTaskProtocol.Decision Decision(SceneVisionObservation source, SceneObservedObject selected)
    {
        const string text = "请看一下当前这个物件。";
        var wire = new JObject { ["intent"] = "inspect-object", ["origin"] = "user", ["evidence"] = text,
            ["visualTarget"] = new JObject { ["captureId"] = source.CaptureId,
                ["x"] = selected.ImagePoint.x, ["y"] = selected.ImagePoint.y, ["label"] = "当前物件" } };
        Check(RoomTaskProtocol.TryDecision(wire.ToString(), text, false, out var decision), "Fixture decision uses the production parser.");
        return decision;
    }
    private static SceneObservedObject FindVisible(ChatSample chat, SceneVisionObservation source)
    {
        for (int y = 30; y <= 70; y += 5) for (int x = 30; x <= 70; x += 5)
            if (chat.SceneVisionCapture.TryResolveObservedObject(source, new Vector2(x / 100f, y / 100f), out var value, out _) &&
                value.VisibleConfirmed && value.Root != null && value.Root.GetComponent<Renderer>() != null && value.TryValidate(out _))
                return value;
        throw new InvalidOperationException("Real fixture needs a visible opaque renderer in the central view.");
    }
    private static object NewFact(string action)
    {
        var fact = Activator.CreateInstance(typeof(ChatSample).GetNestedType("SceneActionFact", Flags), true);
        Set(fact, "actionId", action); Set(fact, "intent", "inspect-object"); return fact;
    }
    private static string Reply(string candidate) => new JObject {
        ["verdict"] = "matched", ["candidateId"] = candidate, ["reason"] = "Controlled response matches the offered current entity." }.ToString();
    private static IEnumerator Capture(ChatSample chat, string purpose, Action<SceneVisionObservation> completed)
    {
        SceneVisionObservation observed = null;
        Call(chat, "RequestFreshSceneObservation", purpose, new Action<SceneVisionObservation>(value => observed = value));
        yield return Wait(() => observed != null, 5f, "Fresh actual capture: " + purpose);
        Check(observed.HasImage && observed.HasSurfaceGrounding, "Real current RGB and ID frame: " + purpose);
        completed(observed);
    }
    private static IEnumerator Wait(Func<bool> ready, float seconds, string label)
    { float deadline = Time.realtimeSinceStartup + seconds; while (!ready()) { if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(label); yield return null; } }
    private static void Case(string name) => ((JArray)LastReport["cases"]).Add(name);
    private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethods(Flags).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
}
