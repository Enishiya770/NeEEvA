using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Actual same-camera opaque pixels exercise historical location memory;
/// fixture panels never become device operations, locomotion or model requests.</summary>
public static class SceneSpatialMemoryRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static int checks;
    private static string directory;
    private static GameObject fixture;
    private static readonly List<Material> materials = new List<Material>();
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Spatial memory regression requires the isolated real-room Play fixture.");
        checks = 0; materials.Clear();
        directory = Path.Combine(output, "scene-spatial-memory"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(),
            ["scope"] = "Actual RGB/opaque ID samples of temporary room-bound panels. Production memory helpers preserve positions across selection, retain unobserved points without current-visibility claims, invalidate moved geometry, update only known entities, keep capture/body bearings separate, and accept real earlier frames after physical head motion. No model HTTP, actor locomotion, device state read or device operation." };
        string[] names = { "m_EnableSceneObjectAwareness", "m_EnableSceneActions", "m_AgentRunning", "m_EnableScreenVision", "m_AgentEyesOpen" };
        var settings = names.Select(name => Get(chat, name)).ToArray();
        var stack = new Stack<IEnumerator>();
        NevaSceneAttention attention = null;
        try
        {
            foreach (string name in names) Set(chat, name, true);
            Check(bridge != null && bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing, "Bound room is idle for read-only memory checks.");
            var capture = (NevaSceneVisionCapture)Call(chat, "EnsureSceneVisionCapture");
            capture.SetObserving(true);
            attention = chat.GetComponent<NevaSceneAttention>() ?? chat.gameObject.AddComponent<NevaSceneAttention>();
            attention.Configure(capture.Target.GetComponent<NeEEvA.Motion.NevaEyeTracking>());
            stack.Push(Exercise(chat, bridge, capture, attention));
            while (stack.Count > 0)
            {
                object current = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var routine = stack.Peek();
                        if (!routine.MoveNext()) { (routine as IDisposable)?.Dispose(); stack.Pop(); continue; }
                        if (routine.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        current = routine.Current; break;
                    }
                }
                catch (Exception error) { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
                if (stack.Count > 0) yield return current;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            attention?.Release("spatial-memory-view-turn", "memory regression finished");
            if (fixture != null) UnityEngine.Object.DestroyImmediate(fixture);
            foreach (var material in materials) if (material != null) UnityEngine.Object.DestroyImmediate(material);
            materials.Clear(); fixture = null;
            for (int i = 0; i < names.Length; i++) Set(chat, names[i], settings[i]);
            LastReport["checks"] = checks;
            File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString());
        }
    }

    private static IEnumerator Exercise(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionCapture capture, NevaSceneAttention attention)
    {
        Check(capture.TryGetCurrentCameraPose(out Pose pose), "Calibrated room view exists.");
        fixture = new GameObject("Isolated spatial memory panels");
        fixture.transform.SetParent(bridge.environmentRoot, false);
        fixture.transform.SetPositionAndRotation(pose.position, pose.rotation);
        var first = Panel("first known", new Vector3(-.12f, -.04f, .60f), new Vector2(.10f, .10f), true);
        var other = Panel("other known", new Vector3(.12f, -.04f, .60f), new Vector2(.10f, .10f), true);
        var unknown = Panel("visible never identified", new Vector3(0, .12f, .60f), new Vector2(.08f, .08f), true);
        string firstId = first.GetComponent<SceneObjectIdentity>().EntityId;
        string otherId = other.GetComponent<SceneObjectIdentity>().EntityId;
        string unknownId = unknown.GetComponent<SceneObjectIdentity>().EntityId;
        SceneVisionObservation source = null;
        yield return Capture(capture, "initial", value => source = value);
        Check(capture.TryFindObservedObject(source, firstId, out var seenFirst, out string reason), "Actual first known opaque entity exists: " + reason);
        Check(capture.TryFindObservedObject(source, otherId, out var seenOther, out reason), "Actual other known opaque entity exists: " + reason);
        Check(capture.TryFindObservedObject(source, unknownId, out _, out reason), "Unknown fixture is genuinely visible, not missing from RGB: " + reason);
        Call(chat, "RememberSceneObject", seenFirst, null);
        var firstPoint = History(chat, firstId)["pointAtCapture"].DeepClone();
        Call(chat, "RememberSceneObject", seenOther, null);
        Check(JToken.DeepEquals(History(chat, firstId)["pointAtCapture"], firstPoint), "Selecting another object retains the first object's actual captured world point.");
        Check((string)ObjectContext(chat)["selected"]["entityId"] == otherId, "The public selected dossier belongs to the actual second selection.");
        Check(History(chat, firstId)["cameraPositionAtCapture"] != null && History(chat, firstId)["cameraRotationAtCapture"] != null &&
            History(chat, firstId)["rootPositionAtCapture"] != null && History(chat, firstId)["rootRotationAtCapture"] != null && History(chat, firstId)["rootScaleAtCapture"] != null,
            "Historical position retains its exact camera view and geometry-checked root snapshot.");
        var selectedBefore = ObjectContext(chat)["selected"].DeepClone();
        var stateBefore = ObjectContext(chat)["stateResult"]?.DeepClone();
        var confirmations = Observe(chat, source);
        Check(confirmations.OfType<JObject>().Any(x => (string)x["entityId"] == firstId) && confirmations.OfType<JObject>().Any(x => (string)x["entityId"] == otherId), "Sampling confirms the actual known opaque entities.");
        Check(!confirmations.OfType<JObject>().Any(x => (string)x["entityId"] == unknownId) && History(chat, unknownId) == null, "Visible unknown objects are not learned through scene inventory.");
        Check(JToken.DeepEquals(ObjectContext(chat)["selected"], selectedBefore) && JToken.DeepEquals(ObjectContext(chat)["stateResult"], stateBefore), "Sampling neither replaces selection nor mutates the device result.");
        var remembered = Memory(chat, source);
        Check((bool?)Object(remembered, firstId)["positionMemoryValid"] == true && Object(remembered, firstId)["relativeToCurrentView"] is JObject && Object(remembered, firstId)["relativeToCurrentBody"] is JObject,
            "Valid historical geometry produces separate camera and body search bearings.");
        Check((bool?)Object(remembered, firstId)["requiresReobservation"] == true && (bool?)Object(remembered, firstId)["actionAuthorization"] == false,
            "Historical bearings cannot authorize action or bypass reobservation.");
        int viewCount = ((JArray)remembered["viewSamples"]).Count;
        Check(JToken.DeepEquals(Observe(chat, source), confirmations) && ((JArray)Memory(chat, source)["viewSamples"]).Count == viewCount, "The same capture callback is idempotent across capture and sweep hooks.");
        AddCase("selection-preservation", remembered);

        var blocker = Panel("unidentified opaque occluder", new Vector3(-.09f, -.03f, .45f), new Vector2(.14f, .14f), false);
        SceneVisionObservation hidden = null;
        yield return Capture(capture, "occluded", value => hidden = value);
        Check(!capture.TryFindObservedObject(hidden, firstId, out _, out _), "A real closer opaque panel occludes the remembered entity.");
        var hiddenConfirmations = Observe(chat, hidden);
        var hiddenMemory = Memory(chat, hidden);
        Check(!hiddenConfirmations.OfType<JObject>().Any(x => (string)x["entityId"] == firstId) && JToken.DeepEquals(Object(hiddenMemory, firstId)["pointAtCapture"], firstPoint),
            "Occlusion preserves the observed historical point without inventing a new observation.");
        Check((string)Object(hiddenMemory, firstId)["currentVisibility"] == "not-established", "A hidden remembered object never becomes currently visible by transform lookup.");
        var hiddenSample = ((JArray)hiddenMemory["viewSamples"]).OfType<JObject>().Single(x => (string)x["captureId"] == hidden.CaptureId);
        Check(((JArray)hiddenSample["notVisibleKnownEntityIds"]).Values<string>().Contains(firstId) &&
            !((JArray)hiddenSample["notVisibleKnownEntityIds"]).Values<string>().Contains(unknownId), "Negative evidence is scoped to this actual frame and known identities only.");
        AddCase("occluded-history", hiddenMemory);

        first.transform.localPosition += Vector3.right * .035f;
        var movedMemory = Memory(chat, hidden);
        Check((bool?)Object(movedMemory, firstId)["positionMemoryValid"] == false && Object(movedMemory, firstId)["relativeToCurrentView"].Type == JTokenType.Null &&
            Object(movedMemory, firstId)["relativeToCurrentBody"].Type == JTokenType.Null, "Hidden movement revokes both bearings instead of reading a new hidden position.");
        Check(JToken.DeepEquals(Object(movedMemory, firstId)["pointAtCapture"], firstPoint), "Moved hidden transform does not overwrite captured historical coordinates.");
        UnityEngine.Object.DestroyImmediate(blocker.gameObject);
        SceneVisionObservation renewed = null;
        yield return Capture(capture, "renewed", value => renewed = value);
        Observe(chat, renewed);
        var renewedMemory = Memory(chat, renewed);
        Check((bool?)Object(renewedMemory, firstId)["positionMemoryValid"] == true && !JToken.DeepEquals(Object(renewedMemory, firstId)["pointAtCapture"], firstPoint),
            "Only new real opaque pixels replace a moved object's old location and restore bearings.");
        var rotation = first.transform.rotation;
        first.transform.rotation *= Quaternion.Euler(0, 8, 0);
        Check((bool?)Object(Memory(chat, renewed), firstId)["positionMemoryValid"] == false, "Root rotation after capture invalidates the captured geometric location.");
        first.transform.rotation = rotation;
        Check((bool?)Object(Memory(chat, renewed), firstId)["positionMemoryValid"] == false, "Returning the transform does not revive an already invalidated sample.");
        SceneVisionObservation rotated = null;
        yield return Capture(capture, "after-rotation", value => rotated = value);
        Observe(chat, rotated);
        first.transform.localScale *= 1.1f;
        Check((bool?)Object(Memory(chat, rotated), firstId)["positionMemoryValid"] == false, "Root scale after capture invalidates the captured location.");
        AddCase("moved-invalidated", movedMemory);

        SceneVisionObservation earlier = null;
        yield return Capture(capture, "before-head-turn", value => earlier = value);
        int visualGeneration = (int)Get(chat, "m_FormalResponseGeneration");
        Call(chat, "BindFormalSceneObservation", earlier, visualGeneration);
        Check((string)Call(chat, "ResolveFormalSceneImage", visualGeneration) == earlier.ImageDataUrl,
            "The formal visual-consumer image path retains exactly the RGB actually supplied, without extracting assistant descriptions.");
        Check(attention.TryOrientView("spatial-memory-view-turn", 40, 0, out reason), "Physical calibrated head turn starts: " + reason);
        yield return Wait(() => attention.IsSettled, 5, "Physical memory head turn");
        Check(!capture.IsCurrentViewCompatible(earlier, .35f, 25f), "The genuine earlier frame now differs from live head orientation beyond the ordinary action gate.");
        var earlierConfirmations = Observe(chat, earlier);
        Check(earlierConfirmations.OfType<JObject>().Any(x => (string)x["entityId"] == firstId), "Earlier genuine sweep pixels still refresh memory when binding/time are valid after the head has moved.");
        var separateBearings = Memory(chat, earlier);
        Check(((JArray)separateBearings["objects"]).Count == 0,
            "A still-young capture with an incompatible live head pose cannot produce current-view or body bearing suggestions.");
        SceneVisionObservation turned = null;
        yield return Capture(capture, "turned-head", value => turned = value);
        var turnedMemory = Memory(chat, turned);
        float viewYaw = (float)Object(turnedMemory, firstId)["relativeToCurrentView"]["yawDegreesRight"];
        float bodyYaw = (float)Object(turnedMemory, firstId)["relativeToCurrentBody"]["yawDegrees"];
        Check(Mathf.Abs(Mathf.DeltaAngle(viewYaw, bodyYaw)) > 25, "A real head turn changes view-relative yaw while body-relative yaw remains distinct.");
        Check(Object(turnedMemory, firstId)["relativeToCurrentView"] is JObject,
            "A new actual capture at the turned pose restores usable view-relative bearings.");
        var dialogue = ((JArray)turnedMemory["dialogueViews"]).OfType<JObject>().Single(x => (string)x["captureId"] == earlier.CaptureId);
        var historyImages = (IReadOnlyList<SceneVisionObservation>)Call(chat, "GetScenePlanningHistoryImages", turned);
        Check((bool?)dialogue["imageAttachedToThisPlanner"] == true && (bool?)dialogue["verifiedEntity"] == false &&
            (bool?)dialogue["actionAuthorization"] == false && historyImages.Any(x => ReferenceEquals(x, earlier)) &&
            historyImages.All(x => x.CaptureId != turned.CaptureId) && History(chat, unknownId) == null,
            "The earlier dialogue RGB becomes an explicitly unverified search image, never a remembered unknown entity or a current target.");
        Check(Mathf.Abs((float)dialogue["returnDirection"]["yawRelativeToCurrentCameraDegrees"]) > 25 &&
            (string)dialogue["returnDirection"]["relativeToCurrentCaptureId"] == turned.CaptureId,
            "Real post-turn camera pose gives a measured return direction to the actual earlier dialogue image.");
        File.WriteAllText(Path.Combine(directory, "dialogue-return-memory.json"), turnedMemory.ToString());
        File.WriteAllText(Path.Combine(directory, "dialogue-return-history-image-data-url.txt"), earlier.ImageDataUrl);
        File.WriteAllBytes(Path.Combine(directory, "dialogue-return-history.jpg"), Convert.FromBase64String(earlier.ImageDataUrl.Substring(earlier.ImageDataUrl.IndexOf(',') + 1)));
        File.WriteAllBytes(Path.Combine(directory, "dialogue-return-current.jpg"), Convert.FromBase64String(turned.ImageDataUrl.Substring(turned.ImageDataUrl.IndexOf(',') + 1)));
        Check((bool)Call(chat, "SceneDialogueViewAgeValid", 10f, 10f + SceneVisionObservation.MaximumHistoryAgeSeconds) &&
            !(bool)Call(chat, "SceneDialogueViewAgeValid", 10f, 10.1f + SceneVisionObservation.MaximumHistoryAgeSeconds) &&
            !(bool)Call(chat, "SceneDialogueViewAgeValid", 10f, 9f),
            "Dialogue history uses the shared bounded 30-minute age while rejecting future timestamps; current pixel freshness is unchanged.");
        var actualSweepInputs = new[] { source, hidden, renewed, rotated };
        var merged = (IReadOnlyList<SceneVisionObservation>)Call(chat, "MergeScenePlanningHistoryImages", actualSweepInputs, earlier);
        Check(merged.Count == 4 && merged.Distinct().Count() == 4 && merged.Last() == earlier &&
            merged.First() == source && merged[2] == rotated,
            "Four actual sweep frames plus an earlier dialogue frame merge into four attachments with the first/last sweep directions retained.");
        // Search coverage and repeated candidate/page decisions now belong to
        // SceneSearchRegression. This fixture retains the real historical RGB,
        // bearing, expiry and attachment checks above, without the retired
        // device runner's half-turn planning predicate.
        AddCase("earlier-frame-after-turn", separateBearings);
        AddCase("new-frame-after-turn", turnedMemory);
        for (int i = 0; i < 4; i++)
        {
            SceneVisionObservation extra = null;
            yield return Capture(capture, "bounded-view-" + i, value => extra = value);
            Observe(chat, extra); turned = extra;
            Call(chat, "BindFormalSceneObservation", extra, visualGeneration);
            Call(chat, "ResolveFormalSceneImage", visualGeneration);
        }
        var retainedDialogue = (JArray)Memory(chat, turned)["dialogueViews"];
        Check(retainedDialogue.OfType<JObject>().Any(x => (string)x["captureId"] == earlier.CaptureId) &&
            ((IReadOnlyList<SceneVisionObservation>)Call(chat, "GetScenePlanningHistoryImages", turned)).Any(x => x.CaptureId == earlier.CaptureId),
            "Repeated real same-heading formal frames refresh that viewpoint instead of evicting the distinct earlier dialogue view; selection prefers that return direction.");
        var boundedViews = (JArray)Memory(chat, turned)["viewSamples"];
        Check(boundedViews.Count == 6 && boundedViews.OfType<JObject>().All(x => (string)x["captureId"] != source.CaptureId),
            "View history retains at most the six latest real captures and evicts earlier samples.");
        var avatar = bridge.avatar;
        try
        {
            bridge.avatar = null;
            Check(((JArray)Memory(chat, turned)["objects"]).Count == 0 && ((JArray)Memory(chat, turned)["dialogueViews"]).Count == 0 &&
                ((IReadOnlyList<SceneVisionObservation>)Call(chat, "GetScenePlanningHistoryImages", turned)).Count == 0 &&
                ((JArray)ObjectContext(chat)["knownObjectHistory"]).Count == 0 && Observe(chat, turned).Count == 0,
                "A changed owner binding exposes no old identity positions or frame confirmations.");
        }
        finally { bridge.avatar = avatar; }
        var environment = bridge.environmentRoot;
        try
        {
            bridge.environmentRoot = null;
            Check(((JArray)Memory(chat, turned)["viewSamples"]).Count == 0 && Observe(chat, turned).Count == 0, "Changed room binding exposes no old view history.");
        }
        finally { bridge.environmentRoot = environment; }
        var pointBeforeReset = History(chat, firstId)["pointAtCapture"].DeepClone();
        string firstCaptureBeforeReset = (string)History(chat, firstId)["captureId"];
        capture.SetObserving(false);
        Check(History(chat, firstId) != null && JToken.DeepEquals(History(chat, firstId)["pointAtCapture"], pointBeforeReset),
            "Closing the same visual source retains the known room identity and its frozen historical point.");
        capture.SetObserving(true);
        Check(Observe(chat, earlier).Count == 0 && ((JArray)Memory(chat, turned)["objects"]).Count == 0,
            "A reset visual binding cannot consume an old image or use that image as the current bearing frame.");
        Check(History(chat, firstId) != null && JToken.DeepEquals(History(chat, firstId)["pointAtCapture"], pointBeforeReset),
            "Reopening the same room/avatar/source preserves identity history without reacquiring old pixels.");
        int operationBefore = bridge.Controller.OperationRevision;
        string command = "请观察已记住的物件。";
        var oldWire = new JObject { ["intent"] = "inspect-object", ["origin"] = "user", ["evidence"] = command,
            ["entityTarget"] = new JObject { ["captureId"] = earlier.CaptureId, ["entityId"] = firstId } };
        Check(RoomTaskProtocol.TryDecision(oldWire.ToString(), command, false, out var oldDecision), "The old frame test supplies an otherwise valid same-entity request.");
        Check((bool)chat.GetType().GetProperty("RoomMotionOutputEnabled", Flags).GetValue(chat), "The stale-frame refusal is tested with spatial output enabled.");
        bool rejected = false;
        Call(chat, "BeginSceneObjectAction", oldDecision, earlier, (int)Get(chat, "m_FormalResponseGeneration"), new Action(() => rejected = true));
        var refusal = JObject.Parse(chat.SceneActionContextJson)["lastAction"];
        Check(rejected && (string)refusal["phase"] == "rejected" && (string)refusal["reason"] == "visual-reference-not-current" &&
            bridge.Controller.OperationRevision == operationBefore, "Remembered identity never lets an old capture execute after a camera reset.");
        Call(chat, "ClearScheduledAgentWake");
        SceneVisionObservation newView = null;
        yield return Capture(capture, "new-binding-current-view", value => newView = value);
        var retainedMemory = Memory(chat, newView);
        Check(((JArray)retainedMemory["dialogueViews"]).OfType<JObject>().Any(x => (string)x["captureId"] == earlier.CaptureId) &&
            capture.IsObservationHistoryBindingCurrent(earlier) && !capture.IsCurrentViewCompatible(earlier),
            "A refreshed current frame may reattach historical dialogue RGB after eye/capture reset, without reviving its current action binding.");
        Check((string)Object(retainedMemory, firstId)["captureId"] == firstCaptureBeforeReset &&
            JToken.DeepEquals(Object(retainedMemory, firstId)["pointAtCapture"], pointBeforeReset) &&
            (string)Object(retainedMemory, firstId)["currentVisibility"] == "not-established" &&
            (bool?)Object(retainedMemory, firstId)["requiresReobservation"] == true &&
            (bool?)Object(retainedMemory, firstId)["actionAuthorization"] == false,
            "A new current view can retrieve frozen identity/location history, which still cannot claim current visibility or authorize an action.");
        AddCase("identity-survives-source-reset", retainedMemory);
        attention.Release("spatial-memory-view-turn", "return for actual reobservation");
        yield return Wait(() => !attention.IsBusy, 5, "Spatial memory head return");
        var selectedBeforeReobserve = ObjectContext(chat)["selected"].DeepClone();
        SceneVisionObservation reobserved = null;
        Call(chat, "RequestSceneObservation", "spatial-memory-new-binding-reobserve", new Action<SceneVisionObservation>(value => reobserved = value));
        yield return Wait(() => reobserved != null, 5, "New-binding automatic known-object reobservation");
        Check(reobserved.HasImage && reobserved.HasSurfaceGrounding && capture.TryFindObservedObject(reobserved, firstId, out _, out _),
            "The new source binding obtains actual opaque pixels of the retained identity.");
        Check((string)History(chat, firstId)["captureId"] == reobserved.CaptureId &&
            (bool?)History(chat, firstId)["positionMemoryValid"] == true &&
            JToken.DeepEquals(ObjectContext(chat)["selected"], selectedBeforeReobserve),
            "The normal capture hook automatically refreshes retained identity location from new pixels without replacing selection.");
        Check(Observe(chat, earlier).Count == 0 && !capture.TryFindObservedObject(earlier, firstId, out _, out _),
            "New reobservation never reactivates the earlier image's visual binding.");
        AddCase("fresh-reobservation-after-source-reset", Memory(chat, reobserved));
        yield return ExerciseObservationWithoutActions(chat, bridge, firstId);
        yield return ExerciseTurnContinuity(chat, bridge, capture, attention, firstId);
    }

    private static IEnumerator ExerciseObservationWithoutActions(ChatSample chat, ArdyRoomDialogueBridge bridge, string entityId)
    {
        bool enabled=(bool)Get(chat,"m_EnableSceneActions");
        object previousFact=Get(chat,"m_SceneActionFact"),previousDispatch=Get(chat,"m_RoomTaskDispatch");
        try
        {
            Set(chat,"m_EnableSceneActions",false);
            SceneVisionObservation observation=null;
            Call(chat,"RequestSceneObservation","memory-with-actions-disabled",new Action<SceneVisionObservation>(value=>observation=value));
            yield return Wait(()=>observation!=null,5,"Read-only capture while scene actions are disabled");
            Check(observation.HasImage && observation.HasSurfaceGrounding && observation.HasBodyPose &&
                (string)History(chat,entityId)["captureId"]==observation.CaptureId &&
                Memory(chat,observation)["selfPose"] is JObject,
                "Disabling actions preserves actual observation, automatic known-location refresh and measured self pose.");
            const string text="Look slightly to the right.";
            var json=new JObject { ["intent"]="orient-view",["origin"]="user",["evidence"]=text,
                ["viewChange"]=new JObject { ["captureId"]=observation.CaptureId,["yaw"]=15,["pitch"]=0 } };
            Check(RoomTaskProtocol.TryDecision(json.ToString(),text,false,out var decision),"Disabled-action probe has a valid current view request.");
            int operation=bridge.Controller.OperationRevision;bool completed=false;
            Call(chat,"BeginSceneViewChange",decision,observation,(int)Get(chat,"m_FormalResponseGeneration"),new Action(()=>completed=true));
            Check(completed && !(bool)Get(chat,"m_SceneActionActive") && bridge.Controller.OperationRevision==operation &&
                ((string)Get(chat,"m_RoomTaskDispatch")).Contains("scene-action-disabled"),
                "Read-only memory cannot bypass the disabled action gate or submit a controller operation.");
            AddCase("perception-continues-with-actions-disabled",Memory(chat,observation));
        }
        finally
        {
            Set(chat,"m_EnableSceneActions",enabled);Set(chat,"m_SceneActionFact",previousFact);Set(chat,"m_RoomTaskDispatch",previousDispatch);
        }
    }

    private static IEnumerator ExerciseTurnContinuity(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionCapture capture, NevaSceneAttention attention, string entityId)
    {
        var avatar=bridge.avatar.transform;Vector3 originalPosition=avatar.position;Quaternion originalRotation=avatar.rotation;
        var ledger=(NeEEvA.Motion.ArdyActionConstraintLedger)Get(chat,"m_ActionConstraints");
        string[] ledgerFields={"currentText","revision","<CurrentUserTurn>k__BackingField"};
        var ledgerSaved=ledgerFields.Select(name=>Get(ledger,name)).ToArray();
        string[] fields={"m_UserSpeechActiveForAutonomy","m_InputAudioRevision","m_InterruptedUserQuestion","m_InterruptedUserTurn","m_InterruptedWorkEpoch",
            "m_InterruptedAgentWasRunning","m_InterruptedHeardText","m_UserTurnAwaitingReplySince","m_LastUserMsg","m_LastUserEvidence"};
        var saved=fields.Select(name=>Get(chat,name)).ToArray();
        Transform originalRoom=bridge.environmentRoot;GameObject otherRoom=null;
        try
        {
            SceneVisionObservation remembered=null;
            yield return Capture(capture,"continuity-original",value=>remembered=value);
            Observe(chat,remembered);
            Call(chat,"BindFormalSceneObservation",remembered,(int)Get(chat,"m_FormalResponseGeneration"));
            Call(chat,"ResolveFormalSceneImage",(int)Get(chat,"m_FormalResponseGeneration"));
            var frozenPoint=History(chat,entityId)["pointAtCapture"].DeepClone();
            Check(remembered.HasBodyPose && Vector3.Distance(remembered.BodyPosition,avatar.position)<.01f,
                "The actual RGB frame carries its same-render measured body pose.");
            int oldEpoch=(int)Get(chat,"m_SceneVisionEpoch");int oldTurn=ledger.CurrentUserTurn;
            chat.NotifyUserStartedSpeaking();
            ledger.RecordUserTurn("Where is the object I just saw?");
            Check((int)Get(chat,"m_SceneVisionEpoch")>oldEpoch && ledger.CurrentUserTurn>oldTurn &&
                capture.IsObservationHistoryBindingCurrent(remembered) && !capture.IsCurrentViewCompatible(remembered) &&
                !capture.TryFindObservedObject(remembered,entityId,out _,out _),
                "Actual VAD invalidation plus a newly accepted user turn retains historical ownership but rejects old current pixels.");
            Set(chat,"m_UserSpeechActiveForAutonomy",false);
            attention.Release("spatial-memory-view-turn","continuity root displacement");
            yield return Wait(()=>!attention.IsBusy,5,"Head settled before continuity body pose");
            // Controlled physical transform change tests perception geometry, not a locomotion controller or route.
            avatar.SetPositionAndRotation(originalPosition+originalRotation*new Vector3(.45f,0,0),Quaternion.AngleAxis(80,Vector3.up)*originalRotation);
            yield return null;yield return null;
            SceneVisionObservation moved=null;
            yield return Capture(capture,"continuity-body-moved",value=>moved=value);
            var memory=Memory(chat,moved);var target=Object(memory,entityId);
            Check(JToken.DeepEquals(target["pointAtCapture"],frozenPoint) &&
                (string)target["captureId"]==remembered.CaptureId &&
                ((JArray)memory["dialogueViews"]).OfType<JObject>().Any(x=>(string)x["captureId"]==remembered.CaptureId),
                "Translation/body turn after VAD preserves the frozen object's world point and actual historical dialogue image.");
            var delta=target["selfPose"]?["sinceHistoricalCapture"];
            Check(delta!=null && Mathf.Abs((float)delta["bodyTranslationMeters"]-.45f)<.025f &&
                Mathf.Abs((float)delta["bodyYawChangeDegreesRight"]-80f)<2f,
                "Memory reports measured self translation and body rotation between the two real frames.");
            Vector3 point=new Vector3((float)frozenPoint["x"],(float)frozenPoint["y"],(float)frozenPoint["z"]);
            Vector3 bodyDelta=point-moved.BodyPosition;
            float expectedBodyYaw=Vector3.SignedAngle(Vector3.ProjectOnPlane(moved.BodyRotation*Vector3.forward,Vector3.up),Vector3.ProjectOnPlane(bodyDelta,Vector3.up),Vector3.up);
            Check(Mathf.Abs(Mathf.DeltaAngle((float)target["relativeToCurrentBody"]["yawDegrees"],expectedBodyYaw))<.05f &&
                Mathf.Abs((float)target["relativeToCurrentBody"]["distanceMeters"]-bodyDelta.magnitude)<.001f,
                "Body-relative direction and distance are recomputed from measured current body pose and frozen source coordinates.");
            Check(attention.TryOrientView("spatial-memory-view-turn",-35,0,out string reason),"Continuity real calibrated head turn starts: "+reason);
            yield return Wait(()=>attention.IsSettled,5,"Continuity head-only observation");
            SceneVisionObservation headed=null;
            yield return Capture(capture,"continuity-head-turned",value=>headed=value);
            var headedTarget=Object(Memory(chat,headed),entityId);
            Check(Mathf.Abs(Mathf.DeltaAngle((float)headedTarget["relativeToCurrentBody"]["yawDegrees"],(float)target["relativeToCurrentBody"]["yawDegrees"]))<2f &&
                Mathf.Abs(Mathf.DeltaAngle((float)headedTarget["relativeToCurrentView"]["yawDegreesRight"],(float)target["relativeToCurrentView"]["yawDegreesRight"]))>20f,
                "A real head turn changes camera bearing while measured body bearing remains stable.");
            Check((bool?)headedTarget["actionAuthorization"]==false && (string)headedTarget["currentVisibility"]=="not-established",
                "Cross-turn and pose-relative historical hints remain explicitly non-authorizing and not current visibility.");
            AddCase("new-user-vad-body-head-continuity",Memory(chat,headed));
            otherRoom=new GameObject("isolated replacement room binding");bridge.environmentRoot=otherRoom.transform;
            Call(chat,"EnsureSceneVisionCapture");
            Check(!capture.IsObservationHistoryBindingCurrent(remembered) && ((JArray)Memory(chat,headed)["dialogueViews"]).Count==0,
                "A real room-root rebind invalidates the historical source session, including its old RGB.");
            bridge.environmentRoot=originalRoom;Call(chat,"EnsureSceneVisionCapture");
            Check(!capture.IsObservationHistoryBindingCurrent(remembered),"Restoring the old room cannot revive a revoked historical session.");
            LastReport["continuityScope"]="Actual rendered frames, production NotifyUserStartedSpeaking invalidation, actual user-turn ledger, controlled avatar-root displacement/body rotation and real NevaSceneAttention head motion. This fixture does not claim native locomotion or semantic model accuracy.";
        }
        finally
        {
            attention.Release("spatial-memory-view-turn","continuity fixture cleanup");
            avatar.SetPositionAndRotation(originalPosition,originalRotation);bridge.environmentRoot=originalRoom;
            if(otherRoom!=null)UnityEngine.Object.DestroyImmediate(otherRoom);
            for(int i=0;i<fields.Length;i++)Set(chat,fields[i],saved[i]);
            for(int i=0;i<ledgerFields.Length;i++)Set(ledger,ledgerFields[i],ledgerSaved[i]);
            Call(chat,"ClearScheduledAgentWake");
        }
    }

    private static Renderer Panel(string name, Vector3 position, Vector2 size, bool identity)
    {
        var panel = GameObject.CreatePrimitive(PrimitiveType.Quad); panel.name = name;
        panel.transform.SetParent(fixture.transform, false); panel.transform.localPosition = position;
        panel.transform.localScale = new Vector3(size.x, size.y, 1);
        UnityEngine.Object.DestroyImmediate(panel.GetComponent<Collider>());
        var material = new Material(Shader.Find("Unlit/Color")) { color = new Color(.8f, .3f, .1f, 1) }; materials.Add(material);
        var renderer = panel.GetComponent<Renderer>(); renderer.sharedMaterial = material;
        if (identity) panel.AddComponent<SceneObjectIdentity>().Configure("空间记忆测试物件");
        return renderer;
    }
    private static IEnumerator Capture(NevaSceneVisionCapture capture, string name, Action<SceneVisionObservation> complete)
    {
        SceneVisionObservation value = null; capture.RequestCapture(result => value = result);
        yield return Wait(() => value != null, 5, "Spatial memory RGB capture " + name);
        Check(value.HasImage && value.HasSurfaceGrounding, "Actual RGB and ID geometry exist: " + name);
        File.WriteAllBytes(Path.Combine(directory, name + ".jpg"), Convert.FromBase64String(value.ImageDataUrl.Substring(value.ImageDataUrl.IndexOf(',') + 1)));
        File.WriteAllText(Path.Combine(directory, name + ".metadata.json"), value.MetadataJson); complete(value);
    }
    private static JObject ObjectContext(ChatSample chat) => (JObject)Call(chat, "BuildSceneObjectFacts");
    private static JObject History(ChatSample chat, string id) => ((JArray)ObjectContext(chat)["knownObjectHistory"]).OfType<JObject>().FirstOrDefault(x => (string)x["entityId"] == id);
    private static JObject Memory(ChatSample chat, SceneVisionObservation observation) => (JObject)Call(chat, "BuildSceneSpatialMemory", observation);
    private static JArray Observe(ChatSample chat, SceneVisionObservation observation) => (JArray)Call(chat, "ObserveKnownSceneObjects", observation, "spatial-memory-regression");
    private static JObject Object(JObject memory, string id) => ((JArray)memory["objects"]).OfType<JObject>().Single(x => (string)x["entityId"] == id);
    private static void AddCase(string name, JObject memory) => ((JArray)LastReport["cases"]).Add(new JObject { ["name"] = name, ["memory"] = memory });
    private static IEnumerator Wait(Func<bool> ready, float seconds, string label)
    { float until = Time.realtimeSinceStartup + seconds; while (!ready()) { if (Time.realtimeSinceStartup > until) throw new TimeoutException(label); yield return null; } }
    private static void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    private static object Get(object target, string field) => target.GetType().GetField(field, Flags).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags).SetValue(target, value);
    private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags).Invoke(target, args);
}
