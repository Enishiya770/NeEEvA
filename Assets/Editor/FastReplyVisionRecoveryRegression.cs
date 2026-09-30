#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Actual capture and repair dispatch; controlled provider never sends HTTP.</summary>
public static class FastReplyVisionRecoveryRegression
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    public static JObject LastReport { get; private set; }
    static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        // The enclosing Editor runner calls MoveNext rather than StartCoroutine.
        // Expand nested Capture/Wait enumerators here, instead of yielding an
        // IEnumerator that that runner treats as a one-frame opaque value.
        var stack = new Stack<IEnumerator>();
        stack.Push(RunCore(chat, bridge));
        try
        {
            while (stack.Count > 0)
            {
                var current = stack.Peek();
                if (!current.MoveNext()) { (current as IDisposable)?.Dispose(); stack.Pop(); continue; }
                if (current.Current is IEnumerator nested) { stack.Push(nested); continue; }
                yield return current.Current;
            }
        }
        finally { while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose(); }
    }

    static IEnumerator RunCore(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use the isolated graphics-enabled Play fixture, never the main scene.");
        checks = 0;
        LastReport = new JObject { ["passed"] = false, ["scope"] = "Actual VRM view captures and production repair request callbacks. No model HTTP, TTS or locomotion generation; fixture root is repositioned only to exercise the unchanged view gate." };
        var fields = new[] { "m_ChatSettings", "m_EnableScreenVision", "m_AgentEyesOpen", "m_ContinuousSceneVision", "m_SceneVisionAvatar", "m_SceneVisionCapture", "m_SceneVisionApplicationPaused", "m_UserSpeechActiveForAutonomy", "m_FormalResponseGeneration", "m_WorkEpoch", "m_CurrentSpeechRoundId", "m_FastLoop", "m_FastLoopTurn", "m_EnableActorIntentCompiler", "m_EnableFastTaskLoop", "m_FormalSceneObservation", "m_FormalSceneObservationGeneration", "m_FastLoopVisualFacts", "m_FastLoopVisualFactsGeneration" };
        var saved = fields.ToDictionary(n => n, n => Get(chat, n));
        Vector3 position = bridge.avatar.transform.position;
        var model = chat.gameObject.AddComponent<FastReplyVisionRecoveryModel>();
        var eyes = bridge.avatar.GetComponent<NevaEyeTracking>();
        bool addedEyes = eyes == null;
        if (addedEyes) eyes = bridge.avatar.gameObject.AddComponent<NevaEyeTracking>();
        var capture = chat.GetComponent<NevaSceneVisionCapture>();
        bool addedCapture = capture == null;
        if (addedCapture) capture = chat.gameObject.AddComponent<NevaSceneVisionCapture>();
        try
        {
            Check(!bridge.ReservesBody && !bridge.Controller.IsMoving, "Vision recovery fixture begins after all physical tasks are idle.");
            eyes.Configure(bridge.avatar); capture.Configure(bridge.avatar, eyes);
            Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model });
            foreach (string name in new[] { "m_EnableScreenVision", "m_AgentEyesOpen", "m_EnableActorIntentCompiler", "m_EnableFastTaskLoop" }) Set(chat, name, true);
            foreach (string name in new[] { "m_ContinuousSceneVision", "m_SceneVisionApplicationPaused", "m_UserSpeechActiveForAutonomy" }) Set(chat, name, false);
            Set(chat, "m_SceneVisionAvatar", bridge.avatar); Set(chat, "m_SceneVisionCapture", capture);
            Set(chat, "m_FastLoop", new FastLoopRuntime());
            Set(chat, "m_FormalResponseGeneration", 24001); Set(chat, "m_WorkEpoch", 24002); Set(chat, "m_CurrentSpeechRoundId", 24003);
            SceneVisionObservation original = null, latest = null;
            yield return Capture(chat, value => original = value);
            bridge.avatar.transform.position += Vector3.right * .40f;
            yield return Capture(chat, value => latest = value);
            Check(original != null && latest != null && original.CaptureId != latest.CaptureId,
                "Both source and moved-view captures actually completed as distinct frames.");
            var originalView = capture.DescribeViewCompatibility(original, .35f, 25f);
            var latestView = capture.DescribeViewCompatibility(latest, .35f, 25f);
            LastReport["firstViewShift"] = new JObject {
                ["sourceCaptureId"] = original.CaptureId, ["latestCaptureId"] = latest.CaptureId,
                ["sourceFrame"] = original.Frame, ["latestFrame"] = latest.Frame,
                ["actualCaptureTranslationMeters"] = Vector3.Distance(original.CameraPosition, latest.CameraPosition),
                ["sourceCompatibility"] = JObject.Parse(JsonUtility.ToJson(originalView)),
                ["latestCompatibility"] = JObject.Parse(JsonUtility.ToJson(latestView)),
                ["sourceFreshnessReason"] = (string)Call(chat, "FastReplyVisionUnavailableReason", original),
                ["latestFreshnessReason"] = (string)Call(chat, "FastReplyVisionUnavailableReason", latest)
            };
            Debug.Log("[FastReplyVisionRegression] firstViewShift=" + LastReport["firstViewShift"].ToString(Newtonsoft.Json.Formatting.None));
            Check(!(bool)Call(chat, "IsFreshSceneObservation", original) && (bool)Call(chat, "IsFreshSceneObservation", latest),
                "The real .40m view shift invalidates old pixels while current pixels remain usable: " +
                LastReport["firstViewShift"].ToString(Newtonsoft.Json.Formatting.None));
            Call(chat, "BindFormalSceneObservation", original, 24001);
            var turn = Turn(chat, model, original.ImageDataUrl);
            Check((bool)Call(chat, "TryRepairDeclinedFastControl", turn, "zh", "走路的时候你能看到什么？", null, 0), "Production control repair accepted.");
            Check(model.Requests.Count == 1 && model.Requests[0].Image == latest.ImageDataUrl,
                "Repair reuses latest compatible pixels, not the old turn's viewpoint.");
            var vision = model.Requests[0].Facts["vision"];
            Check((string)vision["captureId"] == latest.CaptureId && (string)vision["oldUnavailableReason"] == "translation-exceeded" &&
                (bool)vision["imageAttached"] && (string)vision["acquisition"] == "latest-compatible-frame",
                "Model input identifies the exact attached new frame and why the old frame was unused.");
            Check((string)Get(turn, "Image") == latest.ImageDataUrl && ReferenceEquals(Call(chat, "GetFormalSceneObservation", 24001), latest),
                "Same-owner planner handoff inherits exactly the image seen during repair.");

            bridge.avatar.transform.position += Vector3.right * .40f;
            turn = Turn(chat, model, latest.ImageDataUrl);
            int requests = model.Requests.Count;
            Call(chat, "TryRepairDeclinedFastControl", turn, "zh", "继续聊天，可以看到什么？", null, 0);
            Check(model.Requests.Count == requests, "Incompatible latest frame waits for a new capture before sending the repair.");
            yield return Wait(() => model.Requests.Count > requests, 5f);
            var fresh = model.Requests.Last();
            Check(fresh.Image != null && fresh.Image == (string)Get(turn, "Image") && (string)fresh.Facts["vision"]["acquisition"] == "requested-new-frame",
                "A requested actual GPU frame releases one repair request and updates its handoff identity.");
            Check((string)fresh.Facts["vision"]["captureId"] != latest.CaptureId && (bool)fresh.Facts["vision"]["freshAtDispatch"],
                "Fresh repair never relabels the old image's capture id or timestamp.");

            foreach (string invalidation in new[] { "generation", "output-revision", "turn-identity" })
            {
                yield return Capture(chat, value => latest = value);
                Call(chat, "BindFormalSceneObservation", latest, 24001);
                bridge.avatar.transform.position += Vector3.right * .40f;
                turn = Turn(chat, model, latest.ImageDataUrl);
                requests = model.Requests.Count;
                Call(chat, "TryRepairDeclinedFastControl", turn, "zh", "现在呢？", null, 0);
                Check(capture.IsCapturePending, "Real image acquisition is pending before owner invalidation.");
                if (invalidation == "generation") Set(chat, "m_FormalResponseGeneration", 24099);
                else if (invalidation == "output-revision") ((FastLoopRuntime)Get(chat, "m_FastLoop")).BeginTurn();
                else Set(chat, "m_FastLoopTurn", null);
                yield return Wait(() => !capture.IsCapturePending, 5f);
                yield return null;
                Check(model.Requests.Count == requests && (string)Get(turn, "Image") == latest.ImageDataUrl,
                    "A late capture cannot send a repair or overwrite an invalidated turn: " + invalidation);
                Set(chat, "m_FormalResponseGeneration", 24001);
            }
            Set(chat, "m_AgentEyesOpen", false);
            turn = Turn(chat, model, latest.ImageDataUrl);
            Call(chat, "TryRepairDeclinedFastControl", turn, "zh", "还看得到吗？", null, 0);
            var unavailable = model.Requests.Last();
            Check(unavailable.Image == null && !(bool)unavailable.Facts["vision"]["imageAttached"] &&
                (string)unavailable.Facts["vision"]["reason"] == "eyes-closed-or-paused",
                "Revoked visual permission sends no pixels and identifies the actual request-level cause.");
            Check(((string)unavailable.Facts["vision"]["scope"]).Contains("does not prove darkness") && Get(turn, "Image") == null,
                "No attachment cannot be promoted to global blindness, nor reused by later handoff.");
            LastReport["passed"] = true;
        }
        finally
        {
            capture.SetObserving(false);
            bridge.avatar.transform.position = position;
            foreach (var pair in saved) Set(chat, pair.Key, pair.Value);
            if (addedCapture) UnityEngine.Object.Destroy(capture);
            if (addedEyes) UnityEngine.Object.Destroy(eyes);
            UnityEngine.Object.Destroy(model);
            LastReport["checks"] = checks;
        }
    }
    static object Turn(ChatSample chat, LLM model, string image)
    {
        var type = typeof(ChatSample).GetNestedType("FastLoopTurnContext", BindingFlags.NonPublic);
        var value = Activator.CreateInstance(type, Flags, null, new object[] { model, "走路的时候你能看到什么？", image, null,
            24001, 24002, 24003, ((FastLoopRuntime)Get(chat, "m_FastLoop")).OutputRevision, false }, null);
        Set(chat, "m_FastLoopTurn", value);
        return value;
    }
    static IEnumerator Capture(ChatSample chat, Action<SceneVisionObservation> done)
    {
        SceneVisionObservation value = null;
        Call(chat, "RequestFreshSceneObservation", "repair-vision-fixture", new Action<SceneVisionObservation>(v => value = v));
        yield return Wait(() => value != null, 5f);
        Check(value.HasImage, "Real capture has RGB pixels."); done(value);
    }
    static IEnumerator Wait(Func<bool> done, float seconds)
    { float deadline = Time.realtimeSinceStartup + seconds; while (!done()) { if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Repair vision actual capture timeout"); yield return null; } }
    static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Flags).Invoke(target, args);
}

public sealed class FastReplyVisionRecoveryModel : LLM, IFastLoopDecisionBackend, IActorIntentBackend
{
    public sealed class Request { public JObject Facts; public string Image; }
    public readonly List<Request> Requests = new List<Request>();
    public override bool SupportsVisualDecisionMessages => true;
    public bool SupportsFastLoopDecisions => true;
    public bool SupportsActorIntents => true;
    public void PostFastLoopDecision(string context, string currentUser, Action<FastLoopDecision, string> callback, ConversationTrace trace = null, string imageDataUrl = null)
        => Requests.Add(new Request { Facts = JObject.Parse(context), Image = imageDataUrl });
    public void CancelFastLoopDecision() { }
    public void PostActorIntent(string context, string draft, string user, Action<ActorIntentDecision, string> callback, ConversationTrace trace = null) => throw new InvalidOperationException("Fixture does not authorize actor classification.");
    public void PostActorConditionSignal(string context, string user, Action<string, string> callback, ConversationTrace trace = null) => throw new InvalidOperationException("Fixture does not authorize condition classification.");
    public void CancelActorIntent() { }
}
#endif
