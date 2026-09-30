using System;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // Repair belongs to the original accepted turn, not to its original camera
    // pose. Reuse a current compatible rolling frame, otherwise acquire once.
    // The ordinary .35m / 25deg / age and source-binding gates remain unchanged.
    private void PrepareFastReplyVision(FastLoopTurnContext turn, JObject facts, string purpose,
        ConversationTrace trace, Action<string> ready)
    {
        if (!FastLoopTurnCurrent(turn)) return;
        int visionEpoch = m_SceneVisionEpoch;
        var capture = m_SceneVisionCapture;
        var target = capture?.Target;
        var old = GetFormalSceneObservation(turn.Generation);
        string oldReason = FastReplyVisionUnavailableReason(old);
        var latest = capture?.LatestObservation;
        bool completed = false;
        void Complete(SceneVisionObservation observation, string acquisition)
        {
            if (completed) return;
            completed = true;
            if (!FastLoopTurnCurrent(turn)) return;
            bool sameBinding = visionEpoch == m_SceneVisionEpoch &&
                (capture == null || ReferenceEquals(capture, m_SceneVisionCapture) && ReferenceEquals(target, capture.Target));
            string reason = sameBinding ? FastReplyVisionUnavailableReason(observation) : "capture-binding-changed-during-repair";
            bool attached = reason == "ready";
            var view = observation == null ? null : m_SceneVisionCapture?.DescribeViewCompatibility(observation, .35f, 25f);
            var evidence = new JObject {
                ["source"] = "character-scene-rgb", ["imageAttached"] = attached,
                ["freshAtDispatch"] = attached, ["state"] = attached ? "ready" : "request-image-unavailable",
                ["reason"] = reason, ["acquisition"] = acquisition,
                ["captureId"] = attached ? observation.CaptureId : "",
                ["ageSeconds"] = observation?.HasImage == true ? Time.realtimeSinceStartup - observation.CapturedAtRealtime : -1f,
                ["translationMeters"] = view?.translationMeters ?? -1f,
                ["rotationDegrees"] = view?.rotationDegrees ?? -1f,
                ["oldCaptureId"] = old?.CaptureId ?? "", ["oldUnavailableReason"] = oldReason,
                ["scope"] = "Only this request's attached, identified frame is visual evidence. A missing attachment means this request lacks current evidence; it does not prove darkness, global blindness, network failure, or that moving/changing images disable sight. Do not infer the cause beyond reason."
            };
            facts["vision"] = evidence.DeepClone();
            m_FastLoopVisualFactsGeneration = turn.Generation;
            m_FastLoopVisualFacts = evidence;
            // The later legacy/Room handoff must carry the same frame the role
            // just used. No newer turn can inherit an old capture callback.
            turn.Image = attached ? observation.ImageDataUrl : null;
            BindFormalSceneObservation(attached ? observation : null, turn.Generation);
            trace?.Mark("fast_loop.repair_vision", new JObject {
                ["generation"] = turn.Generation, ["outputRevision"] = turn.OutputRevision,
                ["purpose"] = purpose, ["vision"] = evidence.DeepClone() });
            if (FastLoopTurnCurrent(turn)) ready(attached ? observation.ImageDataUrl : null);
        }
        if (!SceneEyesOpen || turn.Model?.SupportsVisualDecisionMessages != true)
        { Complete(null, "not-requested"); return; }
        if (IsFreshSceneObservation(latest)) { Complete(latest, "latest-compatible-frame"); return; }
        if (IsFreshSceneObservation(old)) { Complete(old, "bound-compatible-frame"); return; }
        RequestFreshSceneObservation(purpose, observation => Complete(observation, "requested-new-frame"));
        // The capture's own epoch fence can intentionally suppress a callback.
        // Still settle this owner (without pixels) if only vision was revoked.
        if (!completed)
            StartCoroutine(SceneObservationDeadline(() => completed || !FastLoopTurnCurrent(turn),
                () => Complete(null, "repair-capture-deadline")));
    }

    private string FastReplyVisionUnavailableReason(SceneVisionObservation observation)
    {
        if (!SceneEyesOpen) return !m_EnableScreenVision ? "vision-disabled" : "eyes-closed-or-paused";
        if (m_ChatSettings?.m_ChatModel?.SupportsVisualDecisionMessages != true) return "provider-image-unsupported";
        if (observation?.HasImage != true) return "no-completed-frame";
        float age = Time.realtimeSinceStartup - observation.CapturedAtRealtime;
        if (age < 0 || age > m_SceneObservationMaxAgeSeconds) return "frame-age-out-of-range";
        if (m_SceneVisionCapture == null || !m_SceneVisionCapture.IsObserving) return "capture-not-observing";
        var view = m_SceneVisionCapture.DescribeViewCompatibility(observation, .35f, 25f);
        return view.compatible ? "ready" : view.reason;
    }
}
