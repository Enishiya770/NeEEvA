using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NeEEvA.Motion;
using NeEEvA.Perception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Explicitly launched live model check inside the existing isolated room fixture; never auto-runs.</summary>
public static class SceneActionQwenRegression
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private const string Endpoint = "http://127.0.0.1:8080/v1/chat/completions";
    private sealed class Completion { public string content, status; public JObject body; public bool valid; }
    public static JObject LastReport { get; private set; }
    private static string directory;
    private static ChatSample ownedChat;
    private static float started, deadline;
    private static int httpCalls;

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output)
    {
        if (!Application.isBatchMode || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("The explicit live probe only runs in the existing isolated validation project.");
        directory = Path.Combine(output, "scene-qwen-live"); Directory.CreateDirectory(directory);
        ownedChat = chat; httpCalls = 0; started = Time.realtimeSinceStartup; deadline = started + 60f;
        LastReport = new JObject { ["status"] = "running", ["endpoint"] = Endpoint,
            ["scope"] = "One actual-role fresh-image autonomy preprobe, one actual production room decision, and at most one production-requested fresh-image target correction, submitted to the existing shared Qwen. Actual Chat and Unity attention/navigation execute only the returned accepted intent. Formal speech transport is captured in process; no speech HTTP, ASR or TTS.",
            ["limits"] = "At most3 explicit model POSTs,60seconds total,10,000characters per new input text. Preserved role system prompts are outside the new-input cap. Preprobe keeps original128output tokens; room keeps production240. Only one production-owned target reobservation correction is allowed; no transport retry, forced desire, target substitution or model restart.",
            ["httpRequests"] = new JArray(), ["phases"] = new JArray() };
        Save();
        Require(chat != null && model != null && bridge != null && bridge.IsConnected, "Connected existing fixture is required.");
        Require(bridge.serviceUrl.TrimEnd('/') == "http://127.0.0.1:8093", "Live navigation fixture must explicitly bind its existing local ARDY service.");
        string templatePath = Path.Combine(output, "real-role-autonomy-request.json");
        Require(File.Exists(templatePath), "This same integration run must export the actual role request first.");
        string templateText = File.ReadAllText(templatePath);
        var template = JObject.Parse(templateText);
        Require((int?)template["max_tokens"] == 128 && (bool?)template["stream"] == false &&
            (bool?)template["enable_thinking"] == false && (bool?)template["chat_template_kwargs"]?["enable_thinking"] == false,
            "Actual role request must retain its original bounded nonthinking parameters.");
        LastReport["templateSha256"] = Hash(Encoding.UTF8.GetBytes(templateText));
        ResetRound(chat);
        // Earlier controlled cases must not impersonate the live character's prior desires.
        ((Dictionary<string, float>)Get(chat, "m_SceneTargetRevisitAt")).Clear();
        Set(chat, "m_SceneActionFact", null); Set(chat, "m_SceneAutonomyIntention", "");
        model.m_DataList.Clear();
        yield return Wait(() => IdleHead(chat) && !bridge.ReservesBody && !bridge.Controller.IsBusy, 5f, "prior isolated ownership release");
        int probesBefore = model.Probes.Count, decisionsBefore = model.Decisions.Count, speechBefore = model.Speech.Count;
        Vector3 rootBefore = bridge.avatar.transform.position;
        Quaternion rootRotationBefore = bridge.avatar.transform.rotation;
        int operationBefore = bridge.Controller.OperationRevision;
        Call(chat, "BeginAutonomyIntentProbe", "new-scene-eyes-and-physical-attention-capability");
        yield return Wait(() => model.Probes.Count == probesBefore + 1, 5f, "production fresh scene preprobe");
        var pendingProbe = model.Probes.Last();
        SceneVisionObservation source = chat.SceneVisionCapture.LatestObservation;
        Require(source.HasImage && pendingProbe.Image == source.ImageDataUrl && pendingProbe.Input.Contains(source.CaptureId),
            "Production preprobe must contain this fresh physical camera image and its exact captureId.");
        Require(pendingProbe.Input.Length <= 10000, "New preprobe text exceeds its bounded live input limit.");
        Require(Fresh(chat, source), "Source image expired or changed before first HTTP dispatch.");
        LastReport["source"] = ExportCapture("source", source);
        var payload = (JObject)template.DeepClone();
        var messages = payload["messages"] as JArray;
        Require(messages != null && messages.Count >= 2 && (string)messages.Last["role"] == "user", "Actual role template must end in its one current user observation.");
        var originalMessages = (JArray)template["messages"];
        for (int i = 0; i < messages.Count - 1; i++)
            Require(!messages[i].SelectTokens("$..image_url.url").Any(), "Earlier template messages must not contain old image pixels.");
        // Preserve the actual transport's image provenance wrapper as well as the new prompt.
        // Only role/system context comes from the real configured provider template.
        var currentProbeWire = JObject.Parse(pendingProbe.Wire);
        messages[messages.Count - 1] = currentProbeWire["messages"].Last.DeepClone();
        Require(template.Properties().Where(x => x.Name != "messages").All(x => JToken.DeepEquals(x.Value, payload[x.Name])) &&
            Enumerable.Range(0, messages.Count - 1).All(i => JToken.DeepEquals(originalMessages[i], messages[i])),
            "Every original role/system message and generation parameter must be preserved.");
        Require(Images(payload).SequenceEqual(new[] { source.ImageDataUrl }), "Old source pixels cannot survive current-observation replacement.");
        LastReport["roleSystemMessagesPreserved"] = true;
        Completion probeResult = null;
        yield return Post("autonomy", payload, source, value => probeResult = value);
        Require(probeResult.valid, "Actual preprobe envelope is incomplete or invalid: " + probeResult.status);
        JObject intent = ParseAutonomy(probeResult.content);
        Require(intent["proceed"]?.Type == JTokenType.Boolean, "Actual preprobe lacks an explicit boolean proceed decision.");
        LastReport["autonomyDecision"] = intent;
        pendingProbe.Complete(probeResult.content); // The real production callback owns whether to dispatch.
        yield return null;
        if ((bool)intent["proceed"] == false)
        {
            Require(model.Decisions.Count == decisionsBefore && model.Speech.Count == speechBefore &&
                bridge.Controller.OperationRevision == operationBefore && Vector3.Distance(rootBefore, bridge.avatar.transform.position) < .0001f,
                "proceed=false must remain a legitimate quiet no-action result.");
            LastReport["outcome"] = "model-chose-no-action"; Complete(); yield break;
        }
        if (model.Decisions.Count == decisionsBefore)
        {
            LastReport["outcome"] = "production-declined-preprobe-handoff";
            LastReport["reason"] = "No new room decision was dispatched by the actual callback; no action was forced.";
            Complete(); yield break;
        }
        Require(model.Decisions.Count == decisionsBefore + 1, "Live autonomy unexpectedly created more than one room decision.");
        var pendingRoom = model.Decisions.Last();
        Require(pendingRoom.Kind == "room" && pendingRoom.Input.Length <= 10000 && pendingRoom.Image == source.ImageDataUrl &&
            pendingRoom.Input.Contains(source.CaptureId), "Actual room decision must preserve current source pixels and bounded text.");
        Require(Fresh(chat, source), "Source image expired before the room model request; no stale request is submitted.");
        var roomPayload = JObject.Parse(pendingRoom.Wire);
        Require((int?)roomPayload["max_tokens"] == 640 && Images(roomPayload).SequenceEqual(new[] { source.ImageDataUrl }),
            "Room payload retains actual production240token cap and this one current image.");
        File.WriteAllText(Path.Combine(directory, "room-input.json"), pendingRoom.Input + "\n");
        Completion roomResult = null;
        yield return Post("room", roomPayload, source, value => roomResult = value, pendingRoom.Wire);
        Require(roomResult.valid && RoomTaskProtocol.TryDecision(roomResult.content, "", true, out _),
            "Actual room result failed production completion/schema validation: " + roomResult.status);
        RoomTaskProtocol.TryDecision(roomResult.content, "", true, out var decision);
        LastReport["roomDecision"] = JObject.Parse(roomResult.content);
        LastReport["imageAgeAtRoomCompletionSeconds"] = Time.realtimeSinceStartup - source.CapturedAtRealtime;
        int handledDecisions = model.Decisions.Count;
        int responseGeneration = (int)Get(chat, "m_FormalResponseGeneration");
        bool correctionForwarded = false;
        SceneVisionObservation actionSource = source;
        pendingRoom.Complete(true, roomResult.content, "actual-shared-qwen");
        string actionId = (string)Get(chat, "m_RoomTaskActionId");
        LastReport["actionId"] = actionId;
        LastReport["phaseAtDispatch"] = bridge.Phase;
        string lastPhase = null;
        while (true)
        {
            if (Time.realtimeSinceStartup > deadline) Fail("Live model/action case exceeded its60second total bound.");
            JObject fact = Fact(chat);
            // Only production may request another decision. Never fabricate a repair or
            // reuse the first image, and consume the new pending before checking terminal facts.
            if (model.Decisions.Count > handledDecisions)
            {
                Require(!correctionForwarded && model.Decisions.Count == handledDecisions + 1,
                    "Only one production target-correction decision may be forwarded.");
                Require((int)Get(chat, "m_FormalResponseGeneration") == responseGeneration,
                    "An invalidated response generation cannot dispatch a target correction.");
                var pendingCorrection = model.Decisions[handledDecisions];
                var correctionInput = JObject.Parse(pendingCorrection.Input);
                var correction = correctionInput["visualCorrection"] as JObject;
                Require(pendingCorrection.Kind == "room" && pendingCorrection.Input.Length <= 10000 &&
                    correction != null && (int?)correction["attempt"] == 1 && (int?)correction["maximumAttempts"] == 1,
                    "The third request must be the bounded production room visualCorrection, not a fixture retry.");
                Require(fact != null && (string)fact["phase"] == "rejected" && (bool?)fact["canReobserveTarget"] == true &&
                    (string)fact["captureId"] == source.CaptureId &&
                    (string)correction["measuredFailure"] == (string)fact["reason"] &&
                    JToken.DeepEquals(correction["previousDecision"], JObject.FromObject(new { decision.intent, decision.origin, decision.evidence, decision.visualTarget })),
                    "Production correction must retain the actual rejected decision and its measured failure.");
                Require(!(bool)Get(chat, "m_SceneActionActive") && IdleHead(chat) && !bridge.ReservesBody &&
                    !bridge.IsFollowing && bridge.Controller.OperationRevision == operationBefore &&
                    Vector3.Distance(rootBefore, bridge.avatar.transform.position) < .0001f &&
                    Quaternion.Angle(rootRotationBefore, bridge.avatar.transform.rotation) < .001f && model.Speech.Count == speechBefore,
                    "Pixel correction must begin before any head, root, native or formal-speech side effect.");
                SceneVisionObservation correctionSource = chat.SceneVisionCapture.LatestObservation;
                Require(correctionSource != null && correctionSource.HasImage && correctionSource.CaptureId != source.CaptureId &&
                    pendingCorrection.Image == correctionSource.ImageDataUrl &&
                    (string)correctionInput["visualObservation"]?["captureId"] == correctionSource.CaptureId && Fresh(chat, correctionSource),
                    "Production correction must bind a new, current physical camera capture.");
                var correctionPayload = JObject.Parse(pendingCorrection.Wire);
                Require((int?)correctionPayload["max_tokens"] == 640 &&
                    Images(correctionPayload).SequenceEqual(new[] { correctionSource.ImageDataUrl }),
                    "Correction payload must retain production240token cap and only its fresh current image.");
                LastReport["rejectedBeforeCorrection"] = fact.DeepClone();
                if ((string)fact["phase"] != lastPhase) ((JArray)LastReport["phases"]).Add(fact.DeepClone());
                LastReport["visualCorrection"] = correction.DeepClone();
                LastReport["correctionSource"] = ExportCapture("correction-source", correctionSource);
                File.WriteAllText(Path.Combine(directory, "room-correction-input.json"), pendingCorrection.Input + "\n");
                correctionForwarded = true; handledDecisions++;
                Completion correctionResult = null;
                yield return Post("room-correction", correctionPayload, correctionSource,
                    value => correctionResult = value, pendingCorrection.Wire);
                Require(correctionResult.valid && RoomTaskProtocol.TryDecision(correctionResult.content, "", true, out _),
                    "Actual correction result failed production completion/schema validation: " + correctionResult.status);
                RoomTaskProtocol.TryDecision(correctionResult.content, "", true, out decision);
                LastReport["correctionDecision"] = JObject.Parse(correctionResult.content);
                LastReport["imageAgeAtCorrectionCompletionSeconds"] = Time.realtimeSinceStartup - correctionSource.CapturedAtRealtime;
                actionSource = correctionSource;
                pendingCorrection.Complete(true, correctionResult.content, "actual-shared-qwen");
                actionId = (string)Get(chat, "m_RoomTaskActionId");
                LastReport["actionId"] = actionId;
                LastReport["phaseAtCorrectionDispatch"] = bridge.Phase;
                lastPhase = null;
                Save();
                continue;
            }
            // Re-observation acquisition keeps the initial rejection visible until its
            // callback creates the new request. It is not yet the final action outcome.
            bool pendingCapture = (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") ==
                (int)Get(chat, "m_FormalResponseGeneration");
            if (decision.IsVisual && fact != null && (string)fact["captureId"] == actionSource.CaptureId &&
                ((string)fact["actionId"] == actionId || string.IsNullOrEmpty(actionId)))
            {
                string phase = (string)fact["phase"];
                if (phase != lastPhase)
                {
                    ((JArray)LastReport["phases"]).Add(fact.DeepClone()); lastPhase = phase;
                    var observation = chat.SceneVisionCapture.LatestObservation;
                    if (observation.HasImage && observation.CaptureId != actionSource.CaptureId)
                        LastReport[phase + "Observation"] = ExportCapture("view-" + ((JArray)LastReport["phases"]).Count, observation);
                    Save();
                }
                var heldView = Get(chat, "m_SceneAttention") as NevaSceneAttention;
                bool viewFinished = IdleHead(chat) || (heldView != null && heldView.IsSettled && heldView.OwnerId == actionId);
                if (!pendingCapture && !(bool)Get(chat, "m_SceneActionActive") && viewFinished && !bridge.ReservesBody)
                {
                    // Await the actual result observation when navigation has completed.
                    if (decision.intent == "approach-object" && (bool?)fact["reached"] == true && string.IsNullOrEmpty((string)fact["resultCaptureId"]))
                    { yield return null; continue; }
                    LastReport["actualSceneAction"] = fact;
                    var native = bridge.LastSceneTargetResult;
                    if (native != null && native.actionId == actionId) LastReport["actualNativeResult"] = JObject.Parse(JsonUtility.ToJson(native));
                    var nativeTurn = bridge.LastSceneOrientResult;
                    if (nativeTurn != null && nativeTurn.actionId == actionId) LastReport["actualNativeTurnResult"] = JObject.Parse(JsonUtility.ToJson(nativeTurn));
                    LastReport["outcome"] = (string)fact["phase"];
                    break;
                }
            }
            else if (!decision.IsVisual)
            {
                if (decision.intent == "follow" && bridge.ActionId == actionId && bridge.IsFollowing)
                {
                    // A continuous task has no natural terminal event in this bounded isolated probe.
                    LastReport["outcome"] = "nonvisual-follow-submitted-bounded-probe-ended"; break;
                }
                if (!pendingCapture && !bridge.ReservesBody && !bridge.Controller.IsBusy && model.Speech.Count > speechBefore)
                { LastReport["outcome"] = "nonvisual-" + decision.intent; LastReport["actualRoomPhase"] = bridge.Phase; break; }
            }
            yield return null;
        }
        LastReport["targetCorrectionForwarded"] = correctionForwarded;
        LastReport["effectiveRoomDecision"] = JObject.FromObject(decision);
        LastReport["effectiveSourceCaptureId"] = actionSource.CaptureId;
        LastReport["formalRequestsCaptured"] = model.Speech.Count - speechBefore;
        if (model.Speech.Count > speechBefore)
        {
            var formal = model.Speech.Last(); File.WriteAllText(Path.Combine(directory, "formal-request-captured-only.json"), formal.Wire + "\n");
            LastReport["formalModelHttpSent"] = false;
        }
        LastReport["actualRootTravelMetres"] = Vector3.Distance(rootBefore, bridge.avatar.transform.position);
        LastReport["nativeOperationChanged"] = bridge.Controller.OperationRevision != operationBefore;
        Complete();
    }

    private static IEnumerator Post(string name, JObject payload, SceneVisionObservation source, Action<Completion> complete, string exactWire = null)
    {
        Require(++httpCalls <= 3, "Explicit model HTTP request budget exceeded.");
        Require(exactWire == null || JToken.DeepEquals(JObject.Parse(exactWire), payload),
            "Exact production wire must match the inspected model request.");
        string json = exactWire ?? payload.ToString(Formatting.None);
        File.WriteAllText(Path.Combine(directory, name + "-request.json"), json + "\n");
        float submitted = Time.realtimeSinceStartup;
        var record = new JObject { ["kind"] = name, ["submittedUtc"] = DateTime.UtcNow.ToString("o"),
            ["sourceCaptureId"] = source.CaptureId, ["sourceAgeSeconds"] = submitted - source.CapturedAtRealtime,
            ["maxTokens"] = (int)payload["max_tokens"], ["productionWireUnmodified"] = exactWire != null,
            ["requestSha256"] = Hash(Encoding.UTF8.GetBytes(json)) };
        ((JArray)LastReport["httpRequests"]).Add(record); Save();
        using (var request = new UnityWebRequest(Endpoint, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 18; request.redirectLimit = 0;
            request.SetRequestHeader("Content-Type", "application/json");
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                if (Time.realtimeSinceStartup > deadline || Time.realtimeSinceStartup - submitted > 19f)
                { request.Abort(); Fail(name + " model request exceeded bounded timeout."); }
                yield return null;
            }
            string envelope = request.downloadHandler.text;
            record["httpStatus"] = request.responseCode; record["elapsedSeconds"] = Time.realtimeSinceStartup - submitted;
            Require(envelope != null && envelope.Length <= 200000, "Model response exceeded the bounded envelope size.");
            File.WriteAllText(Path.Combine(directory, name + "-response.json"), envelope + "\n");
            Require(request.result == UnityWebRequest.Result.Success && request.responseCode == 200, name + " model HTTP request failed.");
            object[] args = { envelope, null, null };
            bool valid = (bool)typeof(ChatQW).GetMethod("TryReadRoomTaskCompletion", Flags).Invoke(null, args);
            var body = JObject.Parse(envelope);
            record["completionValid"] = valid; record["completionStatus"] = (string)args[2];
            record["usage"] = body["usage"]?.DeepClone(); record["timings"] = body["timings"]?.DeepClone(); Save();
            complete(new Completion { valid = valid, content = (string)args[1], status = (string)args[2], body = body });
        }
    }

    private static void ResetRound(ChatSample chat)
    {
        Call(chat, "CancelAutonomyIntentProbe"); Call(chat, "InvalidateFormalResponse", "scene-qwen-live-fixture-next"); chat.StopAllCoroutines(); Call(chat, "ClearScheduledAgentWake");
        foreach (string field in new[] { "m_AudioSource", "m_HumStreamSecondaryAudioSource" })
        { var audio = (AudioSource)Get(chat, field); if (audio != null) { audio.playOnAwake = false; audio.Stop(); audio.clip = null; } }
        Set(chat, "m_PendingTickCo", null); Set(chat, "m_AgentRoundInFlight", false); Set(chat, "m_FormalResponseInFlight", false);
        Set(chat, "m_AgentCurrentRoundIsTick", false); Set(chat, "m_UserSpeechActiveForAutonomy", false); Set(chat, "m_StreamComplete", true);
        Set(chat, "m_ConsecutiveAITurns", 0); Set(chat, "m_AgentRunning", true); Set(chat, "m_AgentEyesOpen", true); Set(chat, "m_EnableScreenVision", true);
        Set(chat, "m_EnableSceneActions", true); Set(chat, "m_WorkReviewOpen", false); Set(chat, "m_WorkNoProgress", false);
        Set(chat, "m_UserTurnAwaitingReplySince", -1f); Set(chat, "m_LastUserMsg", ""); Set(chat, "m_LastUserEvidence", "");
        Set(chat, "m_AgentGracefulShutdownPending", false); Set(chat, "m_MonologueBackstopTurns", 40); Set(chat, "m_MaxConsecutiveAITurns", 8);
    }
    private static bool Fresh(ChatSample chat, SceneVisionObservation source) => (bool)Call(chat, "IsFreshSceneObservation", source);
    private static JObject ParseAutonomy(string content)
    {
        try { return JObject.Parse(content); }
        catch (Exception) { Fail("Actual preprobe did not return a complete JSON decision; no malformed-output fallback is executed by this probe."); return null; }
    }
    private static bool IdleHead(ChatSample chat) { var head = (NevaSceneAttention)Get(chat, "m_SceneAttention"); return head == null || !head.IsBusy; }
    private static JObject Fact(ChatSample chat) { object fact = Get(chat, "m_SceneActionFact"); return fact == null ? null : JObject.Parse(JsonUtility.ToJson(fact)); }
    private static IEnumerable<string> Images(JObject payload) => payload.SelectTokens("$..image_url.url").Select(x => (string)x);
    private static JObject ExportCapture(string name, SceneVisionObservation source)
    {
        byte[] image = Convert.FromBase64String(source.ImageDataUrl.Substring(source.ImageDataUrl.IndexOf(',') + 1));
        File.WriteAllBytes(Path.Combine(directory, name + ".jpg"), image);
        File.WriteAllText(Path.Combine(directory, name + ".metadata.json"), source.MetadataJson + "\n");
        return new JObject { ["captureId"] = source.CaptureId, ["image"] = name + ".jpg", ["metadata"] = name + ".metadata.json", ["sha256"] = Hash(image) };
    }
    private static IEnumerator Wait(Func<bool> ready, float seconds, string label)
    { float until = Mathf.Min(deadline, Time.realtimeSinceStartup + seconds); while (!ready()) { if (Time.realtimeSinceStartup > until) Fail("Timed out: " + label); yield return null; } }
    private static void Cleanup()
    {
        if (ownedChat == null) return;
        Call(ownedChat, "CancelAutonomyIntentProbe"); Call(ownedChat, "InvalidateFormalResponse", "scene-qwen-live-fixture-finished");
        ownedChat.StopAllCoroutines(); Call(ownedChat, "ClearScheduledAgentWake"); Set(ownedChat, "m_PendingTickCo", null);
    }
    private static void Complete() { LastReport["status"] = "passed"; LastReport["elapsedSeconds"] = Time.realtimeSinceStartup - started; Save(); Cleanup(); }
    private static void Require(bool condition, string message) { if (!condition) Fail(message); }
    private static void Fail(string message) { LastReport["status"] = "failed"; LastReport["error"] = message; Save(); Cleanup(); throw new InvalidOperationException(message); }
    private static void Save() { LastReport["httpCalls"] = httpCalls; File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString() + "\n"); }
    private static string Hash(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    private static object Call(object target, string name, params object[] args) => NevaSceneVisionIntegrationRegression.Call(target, name, args);
    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
}
