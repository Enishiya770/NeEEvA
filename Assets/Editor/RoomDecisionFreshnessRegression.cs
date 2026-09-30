using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Actual TryPlanRoomTask entry and capture ownership; room requests are recorded, never answered.</summary>
public static class RoomDecisionFreshnessRegression
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string Greeting = "你好，今天感觉怎么样？";
    private static int checks;
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use the isolated real-room Play fixture only.");
        checks = 0;
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(),
            ["scope"] = "Real GPU frames, natural elapsed time and actual TryPlanRoomTask entry. In-process transport only records room requests; no decision reply, model HTTP, TTS or motion execution." };
        bool continuous = (bool)Get(chat, "m_ContinuousSceneVision");
        var environment = bridge.environmentRoot;
        var stack = new Stack<IEnumerator>();
        try
        {
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
            bridge.environmentRoot = environment;
            Call(chat, "InvalidateFormalResponse", "room-decision-freshness-finished");
            Call(chat, "ClearScheduledAgentWake");
            Set(chat, "m_ContinuousSceneVision", continuous);
            chat.SceneVisionCapture?.SetContinuousObservation(continuous);
            LastReport["checks"] = checks;
            if ((string)LastReport["status"] == "running") LastReport["status"] = "failed";
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "room-decision-freshness.json"), LastReport.ToString());
        }
    }

    private static IEnumerator Exercise(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        Vector3 root = bridge.avatar.transform.position;
        Quaternion rotation = bridge.avatar.transform.rotation;
        int operation = bridge.Controller.OperationRevision;
        int speech = model.Speech.Count, matches = model.Matches.Count;
        SceneVisionObservation original = null;

        yield return Prepare(chat);
        yield return CaptureAndBind(chat, "room-initial-fresh-source", value => original = value);
        int generation = (int)Get(chat, "m_FormalResponseGeneration");
        int requests = model.Decisions.Count;
        Check(Time.realtimeSinceStartup - original.CapturedAtRealtime < 1f, "Fresh-case picture is genuinely younger than one second.");
        Begin(chat, original, generation);
        Check(model.Decisions.Count == requests + 1, "Fresh initial planning reuses its picture without a second capture wait.");
        var request = model.Decisions.Last();
        Check(request.Kind == "room" && request.Image == original.ImageDataUrl &&
            (string)JObject.Parse(request.Input)["visualObservation"]["captureId"] == original.CaptureId,
            "The first actual room request carries the same fresh RGB and capture ID.");
        Check((int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1, "Synchronous fresh planning leaves no pending capture flag.");
        Case("just-captured-initial-frame-is-reused", original.CaptureId, original.CaptureId);
        Call(chat, "InvalidateFormalResponse", "freshness-cancel-unanswered-room-request");

        yield return Prepare(chat);
        yield return CaptureAndBind(chat, "room-initial-aged-source", value => original = value);
        generation = (int)Get(chat, "m_FormalResponseGeneration");
        while (Time.realtimeSinceStartup - original.CapturedAtRealtime < 1.2f) yield return null;
        requests = model.Decisions.Count;
        float requestedAt = Time.realtimeSinceStartup;
        Begin(chat, original, generation);
        Check(model.Decisions.Count == requests && (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == generation,
            "Aged initial picture waits for renewal before the first room request.");
        yield return Wait(() => model.Decisions.Count > requests, 5f, "First room request after actual image renewal");
        var bound = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", generation);
        request = model.Decisions.Last();
        Check(model.Decisions.Count == requests + 1 && bound != null && bound.HasImage &&
            bound.CaptureId != original.CaptureId && bound.CapturedAtRealtime >= requestedAt,
            "Exactly one room request is issued after a genuinely new post-request frame is bound.");
        Check(request.Image == bound.ImageDataUrl && (string)JObject.Parse(request.Input)["visualObservation"]["captureId"] == bound.CaptureId,
            "Planning image bytes, formal binding and decision metadata agree on the renewed capture.");
        Check((int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1, "Successful renewal releases the pending capture flag.");
        Case("serial-route-aged-frame-renewed-before-planning", original.CaptureId, bound.CaptureId);
        Call(chat, "InvalidateFormalResponse", "freshness-cancel-unanswered-renewed-request");

        yield return Prepare(chat);
        yield return CaptureAndBind(chat, "room-initial-generation-source", value => original = value);
        generation = (int)Get(chat, "m_FormalResponseGeneration");
        while (Time.realtimeSinceStartup - original.CapturedAtRealtime < 1.2f) yield return null;
        requests = model.Decisions.Count;
        Begin(chat, original, generation);
        Check(model.Decisions.Count == requests && (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == generation,
            "Generation cancellation starts with a real pending initial capture.");
        Call(chat, "InvalidateFormalResponse", "new-user-turn");
        int replacementGeneration = (int)Get(chat, "m_FormalResponseGeneration");
        Check(replacementGeneration != generation, "Production invalidation advances the formal generation.");
        SceneVisionObservation afterCancellation = null;
        yield return Capture(chat, "room-initial-after-generation-cancel", value => afterCancellation = value);
        Check(model.Decisions.Count == requests && (int)Get(chat, "m_FormalResponseGeneration") == replacementGeneration,
            "Completing a later real GPU frame cannot revive the cancelled owner's room request or advance its replacement.");
        Check((int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1 && !(bool)Get(chat, "m_FormalResponseInFlight"),
            "New-generation cancellation leaves neither capture nor formal-request flags stuck.");
        Case("new-generation-revokes-pending-initial-capture", original.CaptureId, afterCancellation.CaptureId);

        yield return Prepare(chat);
        yield return CaptureAndBind(chat, "room-initial-binding-source", value => original = value);
        generation = (int)Get(chat, "m_FormalResponseGeneration");
        while (Time.realtimeSinceStartup - original.CapturedAtRealtime < 1.2f) yield return null;
        requests = model.Decisions.Count;
        Begin(chat, original, generation);
        Check(model.Decisions.Count == requests && (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == generation,
            "Binding cancellation starts with the real capture pending, before any room request.");
        var environment = bridge.environmentRoot;
        try
        {
            // A changed bridge binding does not itself cancel the capture. Its real
            // callback must detect the mismatch and end this still-current owner.
            bridge.environmentRoot = null;
            yield return Wait(() => (int)Get(chat, "m_FormalResponseGeneration") != generation, 5f,
                "Pending capture detects changed room binding");
            Check(model.Decisions.Count == requests && (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1 &&
                !(bool)Get(chat, "m_FormalResponseInFlight"),
                "Changed binding cancels the owner and releases flags without dispatching a room decision.");
        }
        finally { bridge.environmentRoot = environment; }
        yield return Capture(chat, "room-initial-after-binding-cancel", value => afterCancellation = value);
        Check(model.Decisions.Count == requests, "Restoring the room and obtaining another frame cannot replay the invalidated request.");
        Case("binding-change-revokes-pending-initial-capture", original.CaptureId, afterCancellation.CaptureId);

        Check(bridge.avatar.transform.position == root && bridge.avatar.transform.rotation == rotation &&
            bridge.Controller.OperationRevision == operation && !bridge.ReservesBody &&
            model.Speech.Count == speech && model.Matches.Count == matches,
            "All initial-planning cases stop at the intercepted request: no speech, matching, controller operation or movement.");
    }

    private static IEnumerator Prepare(ChatSample chat)
    {
        yield return (IEnumerator)typeof(SceneObjectReferenceRegression).GetMethod("Prepare", Flags).Invoke(null, new object[] { chat });
        Set(chat, "m_ContinuousSceneVision", true);
        Call(chat, "RecordAcceptedMotionUserTurn", Greeting);
    }
    private static void Begin(ChatSample chat, SceneVisionObservation source, int generation)
        => Check((bool)Call(chat, "TryPlanRoomTask", Greeting, source.ImageDataUrl, generation, false, null),
            "Actual TryPlanRoomTask entry accepts the greeting and owns initial planning.");
    private static IEnumerator CaptureAndBind(ChatSample chat, string purpose, Action<SceneVisionObservation> completed)
    {
        SceneVisionObservation observed = null;
        yield return Capture(chat, purpose, value => observed = value);
        Call(chat, "BindFormalSceneObservation", observed, (int)Get(chat, "m_FormalResponseGeneration"));
        completed(observed);
    }
    private static IEnumerator Capture(ChatSample chat, string purpose, Action<SceneVisionObservation> completed)
    {
        SceneVisionObservation observed = null;
        Call(chat, "RequestFreshSceneObservation", purpose, new Action<SceneVisionObservation>(value => observed = value));
        yield return Wait(() => observed != null, 5f, "Real new frame: " + purpose);
        Check(observed.HasImage, "Capture produced actual RGB: " + purpose);
        completed(observed);
    }
    private static IEnumerator Wait(Func<bool> ready, float seconds, string message)
    { float deadline = Time.realtimeSinceStartup + seconds; while (!ready()) { if (Time.realtimeSinceStartup > deadline) throw new TimeoutException(message); yield return null; } }
    private static void Case(string name, string original, string current) => ((JArray)LastReport["cases"]).Add(
        new JObject { ["name"] = name, ["originalCaptureId"] = original, ["currentCaptureId"] = current });
    private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] args)
    {
        try { return target.GetType().GetMethods(Flags).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
}
