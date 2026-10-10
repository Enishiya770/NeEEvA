using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Real room RGB and action ownership with controlled search decisions; no model HTTP or TTS.</summary>
public static class SceneSearchRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static string directory;
    private static GameObject fixture;
    private static readonly List<Material> materials = new List<Material>();
    private static readonly JArray exports = new JArray();
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, string output, bool native = false)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Search checks require the isolated real-room Play fixture.");
        checks = 0; materials.Clear(); exports.Clear();
        directory = Path.Combine(output, "scene-search"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["status"] = "running", ["native"] = native, ["cases"] = new JArray(),
            ["scope"] = "Actual room/avatar camera RGB and opaque IDs, real head actions and optional native body actions. Search/match choices are controlled callbacks through production interfaces; no claim of live-model understanding, model HTTP, TTS, or exhaustive room absence." };
        string[] fields = { "m_EnableSceneActions", "m_EnableSceneObjectAwareness", "m_EnableSceneObjectTargetMatching",
            "m_EnableScreenVision", "m_AgentEyesOpen", "m_AgentRunning", "m_FormalResponseInFlight", "m_AgentRoundInFlight",
            "m_EnableFirstReplyRouting", "m_UserSpeechActiveForAutonomy", "m_AgentCurrentRoundIsTick", "m_SceneTimePaused",
            "m_SpatialDeviceGoal", "m_PendingDeviceClarification", "m_SpatialGoalLatestReview", "m_IndependentSpatialRequestScope",
            "m_LastUserMsg", "m_LastUserEvidence" };
        object[] saved = fields.Select(name => Get(chat, name)).ToArray();
        var ledger = Get(chat, "m_ActionConstraints");
        string[] ledgerFields = { "currentText", "revision", "<CurrentUserTurn>k__BackingField" };
        object[] savedLedger = ledgerFields.Select(name => Get(ledger, name)).ToArray();
        var stack = new Stack<IEnumerator>();
        try
        {
            Check(bridge != null && bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing,
                "Search begins in an actually connected idle room.");
            Set(chat, "m_SpatialDeviceGoal", null); Set(chat, "m_PendingDeviceClarification", null);
            Set(chat, "m_IndependentSpatialRequestScope", null);
            stack.Push(Exercise(chat, bridge, model, native));
            while (stack.Count > 0)
            {
                object next = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var routine = stack.Peek();
                        if (!routine.MoveNext()) { (routine as IDisposable)?.Dispose(); stack.Pop(); continue; }
                        if (routine.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        next = routine.Current; break;
                    }
                }
                catch (Exception error) { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
                if (stack.Count > 0) yield return next;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            Call(chat, "CancelSceneSearchTask", "search-regression-finished");
            Call(chat, "CancelSceneAction", "search-regression-finished");
            Call(chat, "InvalidateFormalResponse", "search-regression-finished");
            Call(chat, "ClearScheduledAgentWake");
            if (fixture != null) UnityEngine.Object.DestroyImmediate(fixture); fixture = null;
            foreach (var material in materials) if (material != null) UnityEngine.Object.DestroyImmediate(material);
            materials.Clear();
            for (int i = 0; i < fields.Length; i++) Set(chat, fields[i], saved[i]);
            for (int i = 0; i < ledgerFields.Length; i++) Set(ledger, ledgerFields[i], savedLedger[i]);
            LastReport["checks"] = checks;
            File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString());
            File.WriteAllText(Path.Combine(directory, "replay-manifest.json"), new JObject {
                ["scope"] = "Captured production inputs/wires and matching actual Unity images; expectations are sidecar only. Controlled choices do not measure model accuracy.",
                ["cases"] = exports.DeepClone() }.ToString());
        }
    }

    private static IEnumerator Exercise(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, bool native)
    {
        yield return ExerciseCatalogMemory(chat, bridge);
        yield return ExerciseKnownPointAcrossUserTurn(chat, bridge);
        yield return ExerciseSurveyPagesAdvance(chat, bridge, model);
        yield return ExerciseExpiredReviewProgress(chat, bridge, model);
        yield return ExerciseRoomRouting(chat, bridge, model);
        yield return ExerciseRoomRouting(chat, bridge, model, true);
        yield return ExerciseSurvey(chat, bridge, model, native);
        yield return ExerciseHistoricalRecall(chat, bridge, model);
        yield return ExerciseAutomaticHistoricalPointRecall(chat, bridge, model, false);
        yield return ExerciseAutomaticHistoricalPointRecall(chat, bridge, model, true);
        yield return ExerciseAutomaticHistoricalPointRecall(chat, bridge, model, false, true);
        yield return ExerciseCurrentCandidate(chat, bridge, model);
        yield return ExerciseFiniteRejectedTargets(chat, bridge, model);
        yield return ExercisePrematureDoneRecovery(chat, bridge, model);
        yield return ExerciseRecoveryMemory(chat, bridge, model, false);
        yield return ExerciseRecoveryMemory(chat, bridge, model, true);
        yield return ExerciseFrozenMemoryWhilePending(chat, bridge, model, false, false);
        yield return ExerciseFrozenMemoryWhilePending(chat, bridge, model, true, false);
        yield return ExerciseFrozenMemoryWhilePending(chat, bridge, model, true, true);
        yield return ExerciseDeadline(chat, bridge, model);
        yield return ExerciseInvalidReferences(chat, bridge, model);
        yield return ExerciseActiveCancellation(chat, bridge, model, native);
        yield return ExerciseLifecycle(chat, bridge, model);
    }

    private static IEnumerator Prepare(ChatSample chat, ArdyRoomDialogueBridge bridge, string text,
        Action<SceneVisionObservation> completed)
    {
        Call(chat, "CancelSceneSearchTask", "search-regression-next-case");
        Call(chat, "CancelSceneAction", "search-regression-next-case");
        yield return Wait(() => !bridge.ReservesBody && HeadIdle(chat), 8, "previous search action released");
        Call(typeof(SceneActionIntegrationRegression), "ResetRound", chat);
        Set(chat, "m_EnableSceneObjectAwareness", true); Set(chat, "m_EnableSceneObjectTargetMatching", true);
        Set(chat, "m_EnableFirstReplyRouting", false); Set(chat, "m_SceneTimePaused", false);
        Set(chat, "m_FormalResponseInFlight", true); Set(chat, "m_AgentRoundInFlight", true);
        Call(chat, "RecordAcceptedMotionUserTurn", text);
        Set(chat, "m_LastUserMsg", text); Set(chat, "m_LastUserEvidence", text);
        Call(chat, "EnsureSceneObjectCatalog");
        // Independent fixture cases start new memory sessions. Cross-turn cases
        // below deliberately do not call Prepare again within the same case.
        chat.SceneVisionCapture?.InvalidateHistoricalSession("search-regression-independent-case");
        yield return Capture(chat, "search-regression-source", completed);
    }

    private static IEnumerator Capture(ChatSample chat, string purpose, Action<SceneVisionObservation> completed)
    {
        SceneVisionObservation view = null;
        Call(chat, "RequestSceneObservation", purpose, new Action<SceneVisionObservation>(value => view = value));
        yield return Wait(() => view != null, 8, purpose + " actual RGB");
        Check(view.HasImage && view.HasSurfaceGrounding, "Capture supplies actual RGB and opaque object grounding: " + purpose);
        Call(chat, "BindFormalSceneObservation", view, (int)Get(chat, "m_FormalResponseGeneration"));
        completed(view);
    }

    private static void Begin(ChatSample chat, string text, string mode, SceneVisionObservation view, Action completed)
    {
        var value = new JObject { ["intent"] = "search-scene", ["origin"] = "user", ["evidence"] = text,
            ["searchTask"] = new JObject { ["mode"] = mode } };
        Check(RoomTaskProtocol.TryDecision(value.ToString(), text, false, out var decision), "Ordinary search request passes the production room protocol.");
        Call(chat, "BeginSceneSearchTask", decision, view, (int)Get(chat, "m_FormalResponseGeneration"), completed);
    }

    private static IEnumerator ExerciseCatalogMemory(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        SceneVisionObservation first = null;
        yield return Prepare(chat, bridge, "看看桌上的物件。", value => first = value);
        Check(chat.SceneVisionCapture.TryGetCurrentCameraPose(out Pose pose), "Catalog fixture uses the actual calibrated camera pose.");
        fixture = new GameObject("Isolated unregistered search objects"); fixture.transform.SetParent(bridge.environmentRoot, false);
        fixture.transform.SetPositionAndRotation(pose.position, pose.rotation);
        var panel = Panel("visible without authored identity", Vector3.forward * .55f, new Vector2(.18f, .18f));
        var hidden = Panel("never visible behind opaque panel", Vector3.forward * .7f, new Vector2(.10f, .10f));
        yield return Capture(chat, "search-catalog-visible-unregistered", value => first = value);
        Check(panel.GetComponent<SceneObjectIdentity>() == null && hidden.GetComponent<SceneObjectIdentity>() == null,
            "The new-memory case does not pre-register authored object identity.");
        Check(chat.SceneVisionCapture.TryResolveObservedObject(first, new Vector2(.5f, .5f), out var seen, out string reason) &&
            seen.VisibleConfirmed && seen.Root == panel.transform, "Actual opaque center pixels identify the unregistered front object: " + reason);
        Check(chat.SceneVisionCapture.TryBuildVisibleObjectCatalog(first, out var fullCatalog, out reason), "Full visible catalog builds from source-frame pixels: " + reason);
        var entry = fullCatalog.Candidates.Single(value => value.entityId == seen.EntityId);
        JObject current = Catalog(chat, first);
        object[] pageArgs = { first, fullCatalog.CatalogId + ":p" + entry.pageIndex, null, null };
        Check((bool)Call(chat, "TrySelectSceneVisibleCatalogPage", pageArgs), "The visible object can be reached through its engine-issued catalog page.");
        current = Catalog(chat, first);
        Check(FindEntity(current["current"], seen.EntityId) != null, "Visible unregistered object appears in the engine current catalog.");
        Check(!chat.SceneVisionCapture.TryResolveObservedObject(first, new Vector2(float.NaN, .5f), out _, out _), "Invalid coordinates do not create catalog evidence.");
        string currentReference = (string)FindEntity(current["current"], seen.EntityId)["selectionId"];
        Check(!string.IsNullOrEmpty(currentReference), "A current candidate has an engine-issued selection reference.");
        Check(fullCatalog.Candidates.All(item => !fullCatalog.TryGetCandidate(item.referenceId, out var visible, out _) || visible.Root != hidden.transform),
            "An object fully hidden behind an opaque foreground panel has no entry in the full visible catalog, including other pages.");
        Check(fullCatalog.Candidates.All(item => !item.actionAuthorization && !item.semanticIdentityConfirmed),
            "Pixel-discovered identity is neither a semantic target confirmation nor device authorization.");
        LastReport["catalogSource"] = current.DeepClone();
        ExportImage("catalog-source", first);
        var blocker = Panel("temporary opaque occluder", Vector3.forward * .4f, new Vector2(.20f, .20f));
        SceneVisionObservation empty = null;
        yield return Capture(chat, "search-current-occlusion-old-object-history", value => empty = value);
        var historical = Catalog(chat, empty);
        Check(FindEntity(historical["current"], seen.EntityId) == null, "Current pixels no longer list the occluded foreground object.");
        var remembered = FindEntity(historical["memory"], seen.EntityId);
        Check(remembered != null && (bool?)remembered["historical"] == true && (bool?)remembered["actionAuthorization"] == false &&
            (string)remembered["sourceCaptureId"] == first.CaptureId && (string)remembered["currentVisibility"] == "not-established",
            "Earlier observed identity remains explicitly historical with its original source and no current visibility/permission.");
        var originalPoint = remembered["pointAtCapture"].DeepClone();
        Check((bool?)remembered["positionMemoryValid"] == true &&
            Resolve(chat, "historical-object", (string)remembered["referenceId"], empty, out _, out _),
            "An unchanged but occluded object retains a usable measured search bearing without becoming current-visible.");
        Check(!Resolve(chat, "current-candidate", currentReference, empty, out _, out _), "An old capture's candidate ID cannot target the current empty view.");
        Check(first.CaptureId != empty.CaptureId && first.ImageDataUrl != empty.ImageDataUrl,
            "Historical object and current empty view have distinct actual capture identities and pixels.");
        LastReport["catalogAfterRemoval"] = historical.DeepClone(); ExportImage("catalog-current-empty", empty);
        panel.transform.localPosition += Vector3.right * .30f;
        panel.gameObject.SetActive(false); hidden.gameObject.SetActive(false); blocker.gameObject.SetActive(false);
        yield return Capture(chat, "search-moved-hidden-object-position-revoked", value => empty = value);
        var moved = FindEntity(Catalog(chat, empty)["memory"], seen.EntityId);
        Check(moved != null && (bool?)moved["positionMemoryValid"] == false && JToken.DeepEquals(moved["pointAtCapture"], originalPoint) &&
            !Resolve(chat, "historical-object", (string)moved["referenceId"], empty, out _, out _),
            "Moving a hidden object revokes the old bearing but never learns its new position from live transforms.");
        LastReport["movedHiddenMemory"] = moved.DeepClone();
        AddCase("unregistered-visible-object-history-is-not-current-pixels", historical);
        UnityEngine.Object.DestroyImmediate(fixture); fixture = null;
    }

    private static IEnumerator ExerciseKnownPointAcrossUserTurn(ChatSample chat, ArdyRoomDialogueBridge bridge)
    {
        SceneVisionObservation source = null;
        yield return Prepare(chat, bridge, "记住眼前物件的位置。", value => source = value);
        Check(chat.SceneVisionCapture.TryGetCurrentCameraPose(out Pose pose), "Cross-turn point memory starts from the real calibrated camera.");
        fixture = new GameObject("Cross-turn frozen object point"); fixture.transform.SetParent(bridge.environmentRoot, false);
        fixture.transform.SetPositionAndRotation(pose.position, pose.rotation);
        var panel = Panel("previously observed point", Vector3.forward * .55f, new Vector2(.18f, .18f));
        yield return Capture(chat, "known-point-before-user-turn", value => source = value);
        Check(chat.SceneVisionCapture.TryResolveObservedObject(source, new Vector2(.5f, .5f), out var seen, out _) &&
            seen.Root == panel.transform, "The retained point comes from actual visible opaque pixels, not a supplied entity coordinate.");
        Call(chat, "RememberSceneObject", seen, null);
        Vector3 directSamplePoint = seen.WorldPoint;
        Check(chat.SceneVisionCapture.TryBuildVisibleObjectCatalog(source, out var originalCatalog, out _),
            "The actual original capture supplies the catalog's own representative sample.");
        Vector3 frozenPoint = originalCatalog.Candidates.Single(entry => entry.entityId == seen.EntityId).pointAtCapture;
        var stateBefore = ((JObject)Get(chat, "m_SceneObjectStateResult"))?.DeepClone();
        Panel("cross-turn occluder", Vector3.forward * .4f, new Vector2(.20f, .20f));
        int priorEpoch = (int)Get(chat, "m_SceneVisionEpoch");
        Call(chat, "InvalidateFormalResponse", "new-user-turn-known-point-regression");
        SceneVisionObservation current = null;
        yield return Capture(chat, "known-point-after-user-turn-reset", value => current = value);
        var facts = Catalog(chat, current);
        var memory = FindEntity(facts["memory"], seen.EntityId);
        Check((int)Get(chat, "m_SceneVisionEpoch") > priorEpoch && !(bool)Call(chat.SceneVisionCapture, "IsObservationBindingCurrent", source),
            "The real user-turn invalidation still revokes the old visual epoch and capture binding.");
        Check(memory != null && (string)memory["referenceId"] == source.CaptureId + ":memory:" + seen.EntityId &&
            (string)memory["sourceCaptureId"] == source.CaptureId &&
            (bool?)memory["positionMemoryValid"] == true && (string)memory["semanticStatus"] == "unconfirmed" &&
            (bool?)memory["actionAuthorization"] == false &&
            (string)memory["currentVisibility"] == "not-established",
            "The ordinary catalog source survives the new turn in its historical namespace without restoring visibility, semantic identity or authority.");
        string reference = (string)memory["referenceId"];
        Check(Resolve(chat, "historical-object", reference, current, out var resolved, out _) && Vector3.Distance(resolved, frozenPoint) < .00001f &&
            !Resolve(chat, "current-candidate", reference, current, out _, out _) &&
            !Resolve(chat, "historical-view", reference, current, out _, out _) &&
            !Resolve(chat, "historical-object", seen.EntityId, current, out _, out _),
            "Only the offered historical-object reference resolves the exact captured point; it cannot serve as current pixels, an old RGB or a free entity ID.");
        Check(FindEntity(facts["current"], seen.EntityId) == null &&
            ((JArray)facts["historicalViews"]).Any(item => (string)item["sourceCaptureId"] == source.CaptureId),
            "The occluded object is absent from the current page while its actual old RGB remains separately retrievable as history.");
        checks += SceneVisibleCatalogRegression.RunHistoryContinuityChecks(chat, source, current);
        panel.transform.localPosition += Vector3.right * .30f;
        Check(!Resolve(chat, "historical-object", reference, current, out _, out _) &&
            (bool?)FindEntity(Catalog(chat, current)["memory"], seen.EntityId)?["positionMemoryValid"] != true && seen.WorldPoint == directSamplePoint,
            "Moving the hidden object's geometry revokes the offered point without learning its new transform position.");
        var room = bridge.environmentRoot;
        var otherRoom = new GameObject("Unrelated room for point ownership check");
        try
        {
            bridge.environmentRoot = otherRoom.transform;
            Call(chat, "EnsureSceneVisionCapture");
            Check(!Resolve(chat, "historical-object", reference, current, out _, out _),
                "A frozen point cannot cross the current room owner binding.");
        }
        finally { bridge.environmentRoot = room; UnityEngine.Object.DestroyImmediate(otherRoom); }
        yield return Capture(chat, "known-point-room-owner-restored", value => current = value);
        Check(!chat.SceneVisionCapture.IsObservationHistoryBindingCurrent(source) &&
            !((JArray)Catalog(chat,current)["historicalViews"]).Any(item=>(string)item["sourceCaptureId"]==source.CaptureId),
            "Restoring the original room does not revive old historical pixels after a true room binding change.");
        Check(JToken.DeepEquals(stateBefore, (JObject)Get(chat, "m_SceneObjectStateResult")) && Get(chat, "m_SpatialDeviceGoal") == null,
            "Remembering and resolving historical search directions neither writes device state nor creates device authorization.");
        AddCase("known-point-survives-user-turn-without-restoring-pixels-or-permission", facts);
        UnityEngine.Object.DestroyImmediate(fixture); fixture = null;
    }

    private static IEnumerator ExerciseSurveyPagesAdvance(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        const string text = "看看四周还有哪些物件。";
        SceneVisionObservation source = null; yield return Prepare(chat, bridge, text, value => source = value);
        int requests = model.SceneSearchRequests.Count, operation = bridge.Controller.OperationRevision, matches = model.Matches.Count;
        bool completed = false; Begin(chat, text, "survey", source, () => completed = true);
        string firstCapture = null; var pagesSeen = new HashSet<int>();
        for (int i = 0; i < 18; i++)
        {
            int expected = requests + i + 1;
            yield return Wait(() => completed || model.SceneSearchRequests.Count >= expected, 18, "survey pages or actual next direction");
            Check(!completed, "Inspecting the first current view does not falsely complete an all-directions survey.");
            var request = model.SceneSearchRequests.Last(); var input = JObject.Parse(request.Input);
            firstCapture = firstCapture ?? (string)input["currentCaptureId"];
            if ((string)input["currentCaptureId"] != firstCapture)
            {
                Check((int?)State(chat)["steps"] == 1 && Quaternion.Angle(source.CameraRotation, chat.SceneVisionCapture.LatestObservation.CameraRotation) > 10 &&
                    bridge.Controller.OperationRevision == operation && model.Matches.Count == matches,
                    "After reviewing actual current pages, survey performs a real comfortable head turn and captures new RGB; paging itself is never a motion.");
                Check(pagesSeen.Count > 0 && ((JArray)State(chat)["receipts"]).OfType<JObject>().Any(row =>
                    (string)row["choice"] == "survey-unobserved-direction" && (bool?)row["observed"] == true),
                    "The new survey direction has an engine motion/capture receipt instead of a model promise.");
                ExportRequest("survey-after-all-current-pages", request, new JObject { ["mode"] = "survey", ["requiresNewDirection"] = true });
                Call(chat, "CancelSceneSearchTask", "controlled-survey-page-check-finished");
                AddCase("all-reviewed-pages-advance-to-real-new-head-view", State(chat));
                yield break;
            }
            Check((int?)State(chat)["steps"] == 0 && bridge.Controller.OperationRevision == operation,
                "Changing candidate pages has not counted a head/body step or changed the physical body.");
            pagesSeen.Add((int)input["visibleCatalog"]["current"]["pageIndex"]);
            Check(!((JArray)input["requestChoices"]).OfType<JObject>().Any(row => (string)row["choice"] == "current-candidate"),
                "A survey cannot waste a decision selecting one object as a find target.");
            var next = ((JArray)input["visibleCatalog"]["current"]["availablePages"]).OfType<JObject>()
                .FirstOrDefault(page => (bool?)page["selectionAllowed"] == true);
            request.Complete(true, next == null ? Choice("done", "") : Choice("next-page", (string)next["referenceId"]), "controlled-review-current-survey-pages");
        }
        throw new InvalidOperationException("A bounded current catalog did not progress to an actual new view.");
    }

    private static IEnumerator ExerciseExpiredReviewProgress(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        var idle = bridge.avatar.GetComponent<Animator>();
        bool idleEnabled = idle != null && idle.enabled;
        try { yield return ExerciseExpiredReviewProgressCore(chat, bridge, model, idle); }
        finally { if (idle != null) idle.enabled = idleEnabled; }
    }

    private static IEnumerator ExerciseExpiredReviewProgressCore(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, Animator idle)
    {
        const string text = "找一下架子左侧的蓝色盒子，位置和颜色都要符合，先只看。";
        SceneVisionObservation source = null; yield return Prepare(chat, bridge, text, value => source = value);
        int requests = model.SceneSearchRequests.Count, matches = model.Matches.Count, operation = bridge.Controller.OperationRevision;
        bool completed = false; Begin(chat, text, "find", source, () => completed = true);
        yield return Wait(() => model.SceneSearchRequests.Count > requests, 8, "generic spatially constrained search");
        var first = model.SceneSearchRequests.Last(); var chosen = (JObject)CurrentCandidates(JObject.Parse(first.Input))[0];
        string entity = (string)chosen["entityId"];
        first.Complete(true, Choice("current-candidate", (string)chosen["referenceId"]), "controlled-unconfirmed-non-lamp-object");
        yield return Wait(() => model.Matches.Count > matches, 8, "generic object's independent semantic match");
        var match = model.Matches.Last();
        Check((string)JObject.Parse(match.Input)["originalGoal"] == text,
            "The matcher receives the complete generic target including spatial relationship and appearance constraints.");
        // The selection -> fresh matcher capture may contain genuine idle head
        // drift, which must not erase the NEW review. Hold the idle animator only
        // for the subsequent age-only comparison; no transform or capture time
        // is rewritten, and the wrapper restores its original enabled state.
        if (idle != null) idle.enabled = false;
        match.Complete(true, new JObject { ["verdict"] = "different-target", ["candidateRole"] = "other", ["candidateId"] = "none",
            ["reason"] = "The selected current object does not satisfy the requested left-of-shelf relationship and blue-box identity." }.ToString(), "controlled-full-target-rejection");
        yield return Wait(() => model.SceneSearchRequests.Count == requests + 2, 8, "search continues after independent rejection");
        var held = model.SceneSearchRequests.Last(); var before = chat.SceneVisionCapture.LatestObservation;
        var beforeRejected = CurrentCandidates(JObject.Parse(held.Input)).OfType<JObject>()
            .FirstOrDefault(row => (string)row["entityId"] == entity);
        LastReport["ageRefreshBefore"] = new JObject { ["entityId"] = entity, ["captureId"] = before.CaptureId,
            ["candidate"] = beforeRejected?.DeepClone(),
            ["scope"] = "The independent review's fresh frame, before its real 12-second age wait. Idle animator is held at its actual pose only for this age-only fixture; no timestamp, geometry or actor root is changed." };
        Check(beforeRejected != null && (bool?)beforeRejected["independentlyReviewedAtThisView"] == true &&
            (bool?)beforeRejected["selectionAllowed"] == false,
            "Independent rejection is retained against the actual matcher frame, even if selection-to-matcher capture changed the previous pose cache.");
        int noProgress = (int)State(chat)["noProgressSteps"];
        yield return Wait(() => Time.realtimeSinceStartup - before.CapturedAtRealtime > 12.05f, 14, "actual current image ages while a decision is pending");
        held.Complete(true, Choice("done", ""), "controlled-age-only-completion");
        yield return Wait(() => model.SceneSearchRequests.Count == requests + 3 || completed, 8, "expired response refreshes without losing frozen review evidence");
        Check(!completed && (int?)State(chat)["noProgressSteps"] == noProgress && (int?)State(chat)["steps"] == 0,
            "An age-only refresh consumes the total decision budget but does not become a semantic no-progress failure or physical step.");
        var next = model.SceneSearchRequests.Last(); var input = JObject.Parse(next.Input);
        Check((string)input["currentCaptureId"] != before.CaptureId && (string)input["task"]["originalUserRequest"] == text,
            "The retry uses genuinely new pixels while retaining the original complete non-device request.");
        var rejected = CurrentCandidates(input).OfType<JObject>().FirstOrDefault(row => (string)row["entityId"] == entity);
        var fresh = chat.SceneVisionCapture.LatestObservation;
        bool foundFresh = chat.SceneVisionCapture.TryFindObservedObject(fresh, entity, out var freshEntity, out string geometryFailure);
        bool choicePresent = rejected != null && ((JArray)input["requestChoices"]).OfType<JObject>().Any(row =>
            (string)row["choice"] == "current-candidate" && (string)row["referenceId"] == (string)rejected["referenceId"]);
        var diagnostic = new JObject {
            ["entityId"] = entity, ["beforeCaptureId"] = before.CaptureId, ["afterCaptureId"] = fresh?.CaptureId,
            ["currentCaptureMatchesInput"] = fresh?.CaptureId == (string)input["currentCaptureId"],
            ["currentFrameAgeSeconds"] = fresh == null ? -1f : Time.realtimeSinceStartup - fresh.CapturedAtRealtime,
            ["cameraTranslationMeters"] = Vector3.Distance(before.CameraPosition, fresh.CameraPosition),
            ["cameraRotationDegrees"] = Quaternion.Angle(before.CameraRotation, fresh.CameraRotation),
            ["currentOpaqueEntityFound"] = foundFresh, ["currentGeometryValid"] = freshEntity?.GeometryIsCurrent == true,
            ["geometryFailure"] = geometryFailure, ["candidatePresent"] = rejected != null,
            ["independentlyReviewedAtThisView"] = rejected?["independentlyReviewedAtThisView"]?.DeepClone(),
            ["selectionAllowed"] = rejected?["selectionAllowed"]?.DeepClone(), ["requestChoicePresent"] = choicePresent,
            ["noProgressBefore"] = noProgress, ["noProgressAfter"] = State(chat)["noProgressSteps"]?.DeepClone(),
            ["steps"] = State(chat)["steps"]?.DeepClone(), ["matchesAdded"] = model.Matches.Count - matches
        };
        LastReport["ageRefreshAfter"] = diagnostic;
        File.WriteAllText(Path.Combine(directory, "age-refresh-assertion-evidence.json"), diagnostic.ToString());
        Check(foundFresh && freshEntity.GeometryIsCurrent &&
            Vector3.Distance(before.CameraPosition, fresh.CameraPosition) <= .04f &&
            Quaternion.Angle(before.CameraRotation, fresh.CameraRotation) <= 3f,
            "The age-only fixture retains a measured same pose and currently valid opaque geometry after real recapture.");
        Check(rejected != null && (bool?)rejected["independentlyReviewedAtThisView"] == true && (bool?)rejected["selectionAllowed"] == false &&
            !choicePresent,
            "Unchanged rejected geometry remains excluded across pure age recapture; old pixels are not restored as executable current evidence.");
        ExportRequest("generic-find-after-age-refresh", next, new JObject { ["mode"] = "find", ["rejectedEntityId"] = entity });
        int historyStart=model.SceneSearchRequests.Count;
        next.Complete(true, Choice("done", ""), "controlled-generic-target-unconfirmed-stop");
        yield return DeclineAdditionalHistoricalReviews(model,()=>completed,historyStart,"generic find ends after bounded history without matching the rejected entity again");
        Check(model.Matches.Count == matches + 1 && bridge.Controller.OperationRevision == operation && Get(chat, "m_SpatialDeviceGoal") == null,
            "The rejected historical sample neither triggers duplicate matching, movement nor device authority.");
        AddCase("generic-target-frozen-rejection-survives-age-only-refresh", State(chat));
    }

    private static IEnumerator ExerciseSurvey(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, bool native)
    {
        const string text = "四处看看，仔细观察房间。";
        SceneVisionObservation source = null; yield return Prepare(chat, bridge, text, value => source = value);
        int requests = model.SceneSearchRequests.Count, matches = model.Matches.Count, reviews = model.DeviceGoalReviews.Count;
        int operation = bridge.Controller.OperationRevision; Quaternion body = bridge.avatar.transform.rotation;
        bool done = false; Begin(chat, text, "survey", source, () => done = true);
        yield return Wait(() => model.SceneSearchRequests.Count > requests, 8, "first ordinary survey planner request");
        var first = model.SceneSearchRequests.Last();
        ValidateRequest(chat, first, source); ExportRequest("survey-initial", first, new JObject { ["mode"] = "survey" });
        var region = Region(first, native ? 120 : 30);
        first.Complete(true, Choice("explore-region", (string)region["referenceId"]), "controlled-search-region");
        yield return Wait(() => model.SceneSearchRequests.Count > requests + 1 || done, native ? 50 : 12, "fresh view schedules the next search decision");
        Check(!done && model.SceneSearchRequests.Count == requests + 2, "A survey continues from its action result without a new user turn or a monologue tick.");
        var second = model.SceneSearchRequests.Last(); var actual = chat.SceneVisionCapture.LatestObservation;
        ValidateRequest(chat, second, actual);
        Check(actual.CaptureId != source.CaptureId && actual.ImageDataUrl != source.ImageDataUrl &&
            Quaternion.Angle(source.CameraRotation, actual.CameraRotation) > 10,
            "Search's second choice receives a fresh RGB image from a materially changed physical view.");
        Check(model.Matches.Count == matches && model.DeviceGoalReviews.Count == reviews && Get(chat, "m_SpatialDeviceGoal") == null,
            "Survey view changes never invoke device authorization or manufacture an executable device goal.");
        if (native)
            Check(bridge.Controller.OperationRevision > operation && Quaternion.Angle(body, bridge.avatar.transform.rotation) > 30 && !bridge.ReservesBody,
                "Native search physically turns the body and releases its action owner before replanning.");
        else
            Check(bridge.Controller.OperationRevision == operation && Quaternion.Angle(body, bridge.avatar.transform.rotation) < .1f,
                "Controlled head survey does not acquire native movement or rotate the avatar root.");
        ExportRequest(native ? "survey-after-body-turn" : "survey-after-head-turn", second, new JObject { ["mode"] = "survey" });
        var nextRegion = Region(second, native ? 240 : 0);
        second.Complete(true, Choice("explore-region", (string)nextRegion["referenceId"]), "controlled-second-survey-view");
        yield return Wait(() => done || model.SceneSearchRequests.Count > requests + 2, native ? 50 : 12, "second physical search view continues in the same user turn");
        Check(!done && model.SceneSearchRequests.Count == requests + 3, "A plain survey executes multiple observation steps under one request.");
        var third = model.SceneSearchRequests.Last(); var thirdView = chat.SceneVisionCapture.LatestObservation;
        ValidateRequest(chat, third, thirdView);
        Check(thirdView.CaptureId != actual.CaptureId && Quaternion.Angle(actual.CameraRotation, thirdView.CameraRotation) > 10,
            "The second search movement obtains its own distinct settled RGB rather than replaying an earlier turn picture.");
        if (!native) Check(bridge.Controller.OperationRevision == operation && Quaternion.Angle(body, bridge.avatar.transform.rotation) < .1f,
            "Both controlled observation steps use actual head motion without requiring native movement service.");
        Call(chat, "CancelSceneSearchTask", "controlled-two-view-sample-finished");
        yield return null;
        var terminal = State(chat);
        Check(!done && (bool?)terminal["active"] == false && (string)terminal["status"] == "cancelled" && (int?)terminal["steps"] == 2 &&
            (int?)terminal["coveredHorizontalRegions"] < (int?)terminal["horizontalRegionCount"],
            "Explicitly stopping this two-view sample reports cancellation, never a completed whole-direction survey.");
        AddCase(native ? "ordinary-survey-native-body-fresh-rgb-continuation" : "ordinary-survey-head-fresh-rgb-continuation", terminal);
    }

    private static IEnumerator ExerciseRoomRouting(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model, bool delayed = false)
    {
        const string text = "帮我找一下小灯。";
        SceneVisionObservation source = null; yield return Prepare(chat, bridge, text, value => source = value);
        int rooms = model.Decisions.Count, searches = model.SceneSearchRequests.Count, speech = model.Speech.Count;
        Call(chat, "StartStreaming", text, false, null, false, false, false, null, false, source, true);
        Set(chat, "m_AgentRoundInFlight", true);
        yield return Wait(() => model.Decisions.Count > rooms, 8, "ordinary user request reaches the production room planner");
        var initial = model.Decisions.Last();
        ExportRequest(delayed ? "expired-image-room-planner" : "ordinary-room-planner", initial, new JObject { ["intent"] = "search-scene", ["mode"] = "find" });
        if (delayed)
        {
            // Model latency is simulated by withholding the actual callback. The image
            // and its immutable capture timestamp stay untouched throughout this wait.
            while (Time.realtimeSinceStartup - source.CapturedAtRealtime < 12.1f) yield return null;
            Check(!(bool)Call(chat, "IsFreshSceneObservation", source), "The delayed room decision really outlives its original capture freshness window.");
        }
        initial.Complete(true, new JObject { ["intent"] = "search-scene", ["origin"] = "user", ["evidence"] = text,
            ["searchTask"] = new JObject { ["mode"] = "find" } }.ToString(), "controlled-room-search-route");
        yield return Wait(() => model.SceneSearchRequests.Count > searches, 8, "room planner dispatches dedicated bounded search");
        var search = model.SceneSearchRequests.Last(); var input = JObject.Parse(search.Input);
        Check((string)input["task"]?["originalUserRequest"] == text && (string)input["task"]?["mode"] == "find" && model.Speech.Count == speech,
            "Plain find keeps the exact user's words and begins search before any premature spoken result.");
        if (delayed)
        {
            var refreshed = chat.SceneVisionCapture.LatestObservation;
            Check(refreshed.CaptureId != source.CaptureId && (string)input["currentCaptureId"] == refreshed.CaptureId &&
                (bool)Call(chat, "IsFreshSceneObservation", refreshed), "Search refreshes the expired room-planner image before its first visual decision.");
            ValidateRequest(chat, search, refreshed);
        }
        ExportRequest(delayed ? "find-entry-after-expired-image" : "ordinary-user-find-entry", search,
            new JObject { ["mode"] = "find", ["originalUserRequest"] = text });
        int historyStart=model.SceneSearchRequests.Count;
        search.Complete(true, Choice("done", ""), "controlled-no-evidence-stop");
        yield return DeclineAdditionalHistoricalReviews(model,()=>model.Speech.Count>speech,historyStart,"search result reaches the normal guarded speech path");
        var result = State(chat);
        Check((string)result["status"] == "incomplete" && string.IsNullOrEmpty((string)result["foundEntityId"]),
            "Stopping find without current confirmation reports incomplete, never found or target absence.");
        Check(model.Speech.Count == speech + 1 && model.SceneSearchRequests.Count >= searches + 1 && model.SceneSearchRequests.Count <= searches + 4,
            "A stopped ordinary search produces one result after bounded historical reviews and one unavailable recovery.");
        LastReport[delayed ? "expiredImageFindReplyContext" : "ordinaryFindReplyContext"] = model.Speech.Last().Context;
        AddCase(delayed ? "expired-room-planner-image-refreshes-before-search" : "plain-user-find-room-route-incomplete-one-reply", result);
    }

    private static IEnumerator ExerciseCurrentCandidate(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        const string text = "找一下当前画面里的那个物件。";
        SceneVisionObservation source = null; yield return Prepare(chat, bridge, text, value => source = value);
        int requests = model.SceneSearchRequests.Count, matches = model.Matches.Count, operation = bridge.Controller.OperationRevision;
        bool done = false; Begin(chat, text, "find", source, () => done = true);
        yield return Wait(() => model.SceneSearchRequests.Count > requests, 8, "current-candidate search planner");
        var request = model.SceneSearchRequests.Last(); var candidates = CurrentCandidates(JObject.Parse(request.Input));
        Check(candidates.Count > 0, "The actual room frame supplies at least one visible candidate.");
        var chosen = (JObject)candidates[0]; string entity = (string)chosen["entityId"];
        ExportRequest("find-current-candidate", request, new JObject { ["choice"] = "current-candidate", ["referenceId"] = chosen["selectionId"]?.DeepClone() });
        request.Complete(true, Choice("current-candidate", (string)chosen["selectionId"]), "controlled-find-current-object");
        yield return Wait(() => model.Matches.Count > matches || done, 8, "independent semantic match for current candidate");
        Check(!done && model.Matches.Count == matches + 1, "A find choice requires independent current-image target matching before it can report found.");
        var match = model.Matches.Last();
        var matchInput = JObject.Parse(match.Input);
        var matchCandidate = ((JArray)matchInput["candidates"]["candidates"]).OfType<JObject>().Single(value => (string)value["entityId"] == entity);
        string matchCapture = (string)matchInput["candidates"]["captureId"];
        Check(!string.IsNullOrEmpty(matchCapture) && matchCapture != source.CaptureId &&
            (string)matchInput["requestedEntityId"] == entity,
            "Independent matching refreshes the selected geometry into a new numbered image without inheriting old candidate numbering.");
        match.Complete(true, new JObject { ["verdict"] = "matched", ["candidateRole"] = "target", ["candidateId"] = matchCandidate["candidateId"]?.DeepClone(),
            ["reason"] = "Controlled independent confirmation of the requested actual current object." }.ToString(), "controlled-search-match");
        yield return Wait(() => done, 12, "same-entity fresh pixels establish found");
        var result = State(chat);
        Check((string)result["status"] == "found" && (string)result["foundEntityId"] == entity &&
            !string.IsNullOrEmpty((string)result["foundCaptureId"]) && (string)result["foundCaptureId"] != source.CaptureId &&
            (string)result["foundCaptureId"] != matchCapture,
            "Selection, independent matching and final same-entity proof use three distinct real captures.");
        Check(bridge.Controller.OperationRevision == operation && model.SceneSearchRequests.Count == requests + 1,
            "A currently visible found target does not trigger an unnecessary search turn or extra planner cycle.");
        AddCase("current-target-independent-match-fresh-proof-no-search-turn", result);
    }

    private static IEnumerator ExerciseHistoricalRecall(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        const string text = "先向右看30度，再找一下刚才画面里的物件，先只看。";
        SceneVisionObservation historical = null;
        yield return Prepare(chat, bridge, text, value => historical = value);
        Catalog(chat, historical); // Register the real source pixels before the physical turn.
        int operation = bridge.Controller.OperationRevision, matches = model.Matches.Count;
        bool turned = false;
        var turn = new JObject { ["intent"] = "orient-view", ["origin"] = "user", ["evidence"] = text,
            ["viewChange"] = new JObject { ["captureId"] = historical.CaptureId, ["yaw"] = 30, ["pitch"] = 0 } };
        Check(RoomTaskProtocol.TryDecision(turn.ToString(), text, false, out var direction), "Historical recall setup uses an explicit production head-view decision.");
        Call(chat, "BeginSceneViewChange", direction, historical, (int)Get(chat, "m_FormalResponseGeneration"), new Action(() => turned = true));
        yield return Wait(() => turned, 12, "actual head turn before historical recall");
        var current = chat.SceneVisionCapture.LatestObservation;
        Check(current.HasImage && current.CaptureId != historical.CaptureId &&
            Quaternion.Angle(historical.CameraRotation, current.CameraRotation) > 25 && bridge.Controller.OperationRevision == operation,
            "Historical and current source pictures come from distinct actual head views without a body operation.");
        int searches = model.SceneSearchRequests.Count; bool completed = false;
        Begin(chat, text, "find", current, () => completed = true);
        yield return Wait(() => model.SceneSearchRequests.Count > searches, 8, "current planner offers historical recall references");
        var first = model.SceneSearchRequests.Last(); var firstInput = JObject.Parse(first.Input);
        var offered = (firstInput["visibleCatalog"]?["historicalViews"] as JArray)?.OfType<JObject>()
            .SingleOrDefault(item => (string)item["sourceCaptureId"] == historical.CaptureId);
        Check(offered != null && (string)offered["sourceCaptureId"] == historical.CaptureId,
            "The exact earlier capture is an engine-listed historical reference; no invented history ID is supplied.");
        var directionFacts = ((JArray)firstInput["explorationRegions"]).OfType<JObject>().ToArray();
        Check(directionFacts.Any(row => Mathf.Abs(Mathf.DeltaAngle((float)row["yawRelativeToCurrentCameraDegrees"],
                (float)row["yawRelativeToCurrentBodyDegrees"])) > 15f) &&
            directionFacts.All(row => Mathf.Abs((float)row["yawRelativeToCurrentCameraDegrees"]) <= 180f && Mathf.Abs((float)row["yawRelativeToCurrentBodyDegrees"]) <= 180f),
            "After an actual head turn, the same regions expose distinct measured camera/body angles within the shortest +/-180 range.");
        first.Complete(true, Choice("historical-view", (string)offered["referenceId"]), "controlled-select-real-historical-view");
        yield return Wait(() => model.SceneSearchRequests.Count > searches + 1 || completed, 8, "separate historical image review");
        Check(!completed && model.SceneSearchRequests.Count == searches + 2, "A historical choice opens a separate recall decision instead of claiming a current target.");
        var recall = model.SceneSearchRequests.Last(); var recallInput = JObject.Parse(recall.Input);
        object[] historicalPage = { historical, current, 0, null, null, null };
        bool hasHistoricalPage = (bool)Call(chat, "TryGetSceneSearchHistoricalPage", historicalPage);
        Check((string)recallInput["imageRole"] == "historical" &&
            (string)recallInput["historicalCaptureId"] == historical.CaptureId &&
            ((string)recallInput["historicalReferenceId"]).StartsWith((string)recallInput["requestId"] + "-", StringComparison.Ordinal) &&
            recall.Image == (hasHistoricalPage ? (string)historicalPage[3] : historical.ImageDataUrl) && recall.Image != first.Image &&
            recallInput["currentCaptureId"] == null && recallInput["visibleCatalog"] == null,
            "Recall sends this exact historical frame's frozen annotated page (or its raw RGB fallback), never a current page or candidate namespace.");
        ExportRequest("historical-recall-source-image", recall, new JObject { ["imageRole"] = "historical", ["historicalCaptureId"] = historical.CaptureId, ["annotated"] = hasHistoricalPage });
        ExportImage("historical-recall-source", historical); ExportImage("historical-recall-before-return", current);
        recall.Complete(true, Choice("historical-view", (string)recallInput["historicalReferenceId"]), "controlled-recall-selects-same-offered-source");
        yield return Wait(() => model.SceneSearchRequests.Count > searches + 2 || completed, 15, "actual return toward the historical camera direction and new capture");
        Check(!completed && model.SceneSearchRequests.Count == searches + 3, "Accepted recall returns to an actual current-view planner after physical observation.");
        var returned = chat.SceneVisionCapture.LatestObservation; var next = model.SceneSearchRequests.Last();
        ValidateRequest(chat, next, returned);
        Check(returned.CaptureId != historical.CaptureId && returned.CaptureId != current.CaptureId &&
            Quaternion.Angle(returned.CameraRotation, historical.CameraRotation) < 12 &&
            Quaternion.Angle(returned.CameraRotation, current.CameraRotation) > 15 &&
            bridge.Controller.OperationRevision == operation && model.Matches.Count == matches,
            "Historical return physically changes head direction and obtains new current RGB; memory alone neither matches a target nor moves the body.");
        next.Complete(true, Choice("done", ""), "controlled-recall-no-confirmed-target");
        int answered = searches + 3;
        while (!completed)
        {
            yield return Wait(() => completed || model.SceneSearchRequests.Count > answered, 8, "bounded remaining historical review before done");
            if (completed) break;
            var pending=model.SceneSearchRequests[answered++];
            var input=JObject.Parse(pending.Input); bool recovery=(bool?)input["recovery"]?["onlyNewView"]==true;
            Check(((string)input["imageRole"] == "historical" || recovery) && answered <= searches + 5,
                "A final done may review one remaining historical image and request one useful recovery.");
            if(recovery) pending.Complete(false,"","controlled-recovery-provider-unavailable");
            else pending.Complete(true,Choice("done",""),"controlled-decline-remaining-history");
        }
        var result = State(chat);
        Check((string)result["status"] == "incomplete" && string.IsNullOrEmpty((string)result["foundEntityId"]) &&
            (int?)result["steps"] == 1 && model.Matches.Count == matches && (bool?)result["deviceWriteAuthorized"] == false,
            "A successfully revisited historical view remains an incomplete find until independent fresh target confirmation.");
        AddCase("historical-frozen-image-two-stage-head-return-fresh-current", result);
    }

    private static IEnumerator ExerciseAutomaticHistoricalPointRecall(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, bool decline, bool invalidHistory=false)
    {
        const string firstText="Remember the small object in this view.";
        const string text="Look 35 degrees right, then find the orange marker from the previous view. Only observe.";
        SceneVisionObservation historical=null;
        yield return Prepare(chat,bridge,firstText,value=>historical=value);
        Check(chat.SceneVisionCapture.TryGetCurrentCameraPose(out Pose pose),"Automatic recall fixture has a calibrated actual viewpoint.");
        fixture=new GameObject("Generic cross-turn historical marker");fixture.transform.SetParent(bridge.environmentRoot,false);
        fixture.transform.SetPositionAndRotation(pose.position,pose.rotation);
        Panel("opaque background",Vector3.forward*.8f,new Vector2(3,3));
        var marker=Panel("small observed marker",Vector3.forward*.55f,new Vector2(.12f,.12f));
        yield return Capture(chat,"automatic-recall-original",value=>historical=value);
        Check(chat.SceneVisionCapture.TryResolveObservedObject(historical,new Vector2(.5f,.5f),out var observed,out _) &&
            observed.Root==marker.transform,"Historical marker point originates from real opaque pixels of an unregistered generic object.");
        Catalog(chat,historical);
        Call(chat,"ResolveFormalSceneImage",(int)Get(chat,"m_FormalResponseGeneration"));
        int epoch=(int)Get(chat,"m_SceneVisionEpoch");
        chat.NotifyUserStartedSpeaking();
        Call(chat,"RecordAcceptedMotionUserTurn",text);Set(chat,"m_LastUserMsg",text);Set(chat,"m_LastUserEvidence",text);
        Set(chat,"m_UserSpeechActiveForAutonomy",false);Set(chat,"m_FormalResponseInFlight",true);Set(chat,"m_AgentRoundInFlight",true);
        SceneVisionObservation beforeTurn=null;
        yield return Capture(chat,"automatic-recall-new-user-view",value=>beforeTurn=value);
        Check((int)Get(chat,"m_SceneVisionEpoch")>epoch && chat.SceneVisionCapture.IsObservationHistoryBindingCurrent(historical) &&
            !(bool)Call(chat.SceneVisionCapture,"IsObservationBindingCurrent",historical),
            "A real new VAD/user turn preserves the earlier visual history while revoking old action pixels.");
        var turn=new JObject { ["intent"]="orient-view",["origin"]="user",["evidence"]=text,
            ["viewChange"]=new JObject { ["captureId"]=beforeTurn.CaptureId,["yaw"]=35,["pitch"]=0 } };
        Check(RoomTaskProtocol.TryDecision(turn.ToString(),text,false,out var direction),"A current explicit head preface passes normal protocol validation.");
        bool turned=false;int operation=bridge.Controller.OperationRevision;
        Call(chat,"BeginSceneViewChange",direction,beforeTurn,(int)Get(chat,"m_FormalResponseGeneration"),new Action(()=>turned=true));
        yield return Wait(()=>turned,12,"cross-turn setup physical head turn");
        var current=chat.SceneVisionCapture.LatestObservation;
        Check(current.HasImage && Quaternion.Angle(current.CameraRotation,historical.CameraRotation)>25,
            "The new user's actual head view differs from the retained historical image.");
        int searches=model.SceneSearchRequests.Count,matches=model.Matches.Count;bool completed=false;
        Begin(chat,text,"find",current,()=>completed=true);
        yield return Wait(()=>model.SceneSearchRequests.Count>searches,8,"automatic recall initial current planner");
        var first=model.SceneSearchRequests.Last();var firstInput=JObject.Parse(first.Input);
        string initialChoice=decline?"done":"explore-region";
        var offeredChoice=((JArray)firstInput["requestChoices"]).OfType<JObject>()
            .FirstOrDefault(row=>(string)row["choice"]==initialChoice);
        Check(offeredChoice!=null,"Automatic recall starts from an actually offered current-request choice, not an unavailable region heading.");
        ExportRequest(invalidHistory?"automatic-history-invalid-initial":decline?"automatic-history-decline-initial":"automatic-history-before-explore-initial",
            first,new JObject { ["initialChoice"]=initialChoice,["requiresHistoricalReviewBeforeExecution"]=true });
        first.Complete(true,Choice(initialChoice,(string)offeredChoice["referenceId"]),"controlled-current-choice-before-history");
        yield return Wait(()=>model.SceneSearchRequests.Count>searches+1||completed,8,"automatic pre-exploration historical review");
        Check(!completed && model.SceneSearchRequests.Count==searches+2,"The first exploration/done choice is preceded by a separate bounded historical review.");
        var recall=model.SceneSearchRequests.Last();var recallInput=JObject.Parse(recall.Input);
        Check((string)recallInput["imageRole"]=="historical" && (string)recallInput["historicalCaptureId"]==historical.CaptureId &&
            bridge.Controller.OperationRevision==operation && model.Matches.Count==matches && (int?)State(chat)["steps"]==0,
            "The exact pre-user-turn image is reviewed before exploration or any semantic target confirmation.");
        ExportRequest(invalidHistory?"automatic-history-invalid":decline?"automatic-history-decline":"automatic-history-before-explore",recall,new JObject {
            ["imageRole"]="historical",["captureId"]=historical.CaptureId,["notCurrentTargetProof"]=true });
        if(invalidHistory)
        {
            recall.Complete(true,"{}","controlled-malformed-historical-review");
            yield return Wait(()=>completed,8,"invalid historical response terminates instead of blind exploration");
            var result=State(chat);
            Check((string)result["status"]=="incomplete" && (int?)result["steps"]==0 &&
                ((JArray)result["reviewedHistoricalCaptureIds"]).Count==0 &&
                model.SceneSearchRequests.Count==searches+2 && model.Matches.Count==matches && bridge.Controller.OperationRevision==operation,
                "Malformed historical review is not recorded as reviewed and terminates without executing the queued exploratory action.");
            AddCase("invalid-history-review-never-authorizes-blind-exploration",result);
        }
        else if(decline)
        {
            recall.Complete(true,Choice("done",""),"controlled-decline-first-history");
            int answered=searches+2;
            while(!completed)
            {
                yield return Wait(()=>completed||model.SceneSearchRequests.Count>answered,8,"bounded historical decline terminates");
                if(completed)break;
                var pending=model.SceneSearchRequests[answered++];
                var input=JObject.Parse(pending.Input); bool recovery=(bool?)input["recovery"]?["onlyNewView"]==true;
                Check(((string)input["imageRole"]=="historical" || recovery) && answered<=searches+4,
                    "At most two actual old images and one explicit recovery are requested before finite termination.");
                if(recovery) pending.Complete(false,"","controlled-recovery-provider-unavailable");
                else pending.Complete(true,Choice("done",""),"controlled-decline-second-history");
            }
            Check((string)State(chat)["status"]=="incomplete" && (int?)State(chat)["steps"]==0 &&
                model.Matches.Count==matches && bridge.Controller.OperationRevision==operation,
                "Declining bounded history ends as incomplete without a search motion, device operation or fabricated target.");
            AddCase("cross-user-history-decline-is-bounded",State(chat));
        }
        else
        {
            var historicalCandidate=((JArray)recallInput["historicalCandidates"]).OfType<JObject>()
                .Single(row=>(string)row["entityId"]==observed.EntityId);
            Check(historicalCandidate["worldPoint"]!=null && (bool?)historicalCandidate["actionAuthorization"]==false,
                "The historical candidate carries its frozen opaque point with no operation authorization.");
            recall.Complete(true,Choice("historical-object",(string)historicalCandidate["referenceId"]),"controlled-revisit-historical-marker-point");
            yield return Wait(()=>model.SceneSearchRequests.Count>searches+2||completed,15,"physical head return toward historical point");
            Check(!completed && model.SceneSearchRequests.Count==searches+3 && model.Matches.Count==matches &&
                bridge.Controller.OperationRevision==operation && (int?)State(chat)["steps"]==1,
                "Historical point selection only turns the real head and recaptures; it cannot establish found or operate a device.");
            var returned=chat.SceneVisionCapture.LatestObservation;var next=model.SceneSearchRequests.Last();
            ValidateRequest(chat,next,returned);
            Check(returned.CaptureId!=historical.CaptureId && Quaternion.Angle(returned.CameraRotation,current.CameraRotation)>20 &&
                Quaternion.Angle(returned.CameraRotation,historical.CameraRotation)<15,
                "The retained point causes a measured head return and a new current picture, not replayed historical pixels.");
            var selected=CurrentCandidates(JObject.Parse(next.Input)).OfType<JObject>().Single(row=>(string)row["entityId"]==observed.EntityId);
            next.Complete(true,Choice("current-candidate",(string)selected["selectionId"]),"controlled-fresh-marker-selection");
            yield return Wait(()=>model.Matches.Count>matches||completed,8,"fresh independent marker match");
            Check(!completed && model.Matches.Count==matches+1,"Revisited target still requires an independent match on fresh current pixels.");
            var match=model.Matches.Last();var matchInput=JObject.Parse(match.Input);
            var chosen=((JArray)matchInput["candidates"]["candidates"]).OfType<JObject>().Single(row=>(string)row["entityId"]==observed.EntityId);
            Check((string)matchInput["candidates"]["captureId"]!=historical.CaptureId &&
                (string)matchInput["candidates"]["captureId"]!=returned.CaptureId,"The independent matcher gets a newly captured candidate image.");
            match.Complete(true,new JObject { ["verdict"]="matched",["candidateRole"]="target",["candidateId"]=chosen["candidateId"].DeepClone(),
                ["reason"]="Controlled confirmation of the same generic marker in the fresh image." }.ToString(),"controlled-fresh-history-target-match");
            yield return Wait(()=>completed,12,"fresh same-entity proof after historical return");
            Check((string)State(chat)["status"]=="found" && (string)State(chat)["foundEntityId"]==observed.EntityId &&
                (string)State(chat)["foundCaptureId"]!=historical.CaptureId && (bool?)State(chat)["deviceWriteAuthorized"]==false,
                "Found is established only by a fresh independently matched same entity; no device authority follows.");
            AddCase("cross-user-auto-history-point-real-head-return-fresh-match",State(chat));
        }
        UnityEngine.Object.DestroyImmediate(fixture);fixture=null;
    }

    private static IEnumerator ExerciseInvalidReferences(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        foreach (bool devicePayload in new[] { false, true })
        {
            const string text = "找一下房间里的小灯，先只看。";
            SceneVisionObservation source = null; yield return Prepare(chat, bridge, text, value => source = value);
            int requests = model.SceneSearchRequests.Count, matches = model.Matches.Count, reviews = model.DeviceGoalReviews.Count;
            int operation = bridge.Controller.OperationRevision; bool done = false;
            Begin(chat, text, "find", source, () => done = true);
            yield return Wait(() => model.SceneSearchRequests.Count > requests, 8, "negative search request");
            var request = model.SceneSearchRequests.Last();
            var bad = JObject.Parse(Choice("current-candidate", "not-an-engine-listed-source"));
            if (devicePayload)
            {
                bad["choice"] = "done"; bad["referenceId"] = ""; bad["selectionRole"] = "exploration";
                bad["deviceGoal"] = new JObject { ["operation"] = "set", ["desiredState"] = "off", ["targetDescription"] = "小灯", ["requestQuote"] = text };
            }
            request.Complete(true, bad.ToString(), "controlled-invalid-search-reference");
            yield return null;
            Check(model.Matches.Count == matches && model.DeviceGoalReviews.Count == reviews &&
                bridge.Controller.OperationRevision == operation && Get(chat, "m_SpatialDeviceGoal") == null &&
                (string)State(chat)["status"] != "found", "Unlisted references and device payloads cannot obtain match, movement, device authority or found status.");
            AddCase(devicePayload ? "search-choice-cannot-introduce-device-goal" : "search-choice-requires-engine-listed-reference", State(chat));
            Call(chat, "CancelSceneSearchTask", "negative-search-probe-finished");
        }
    }

    private static IEnumerator ExerciseFiniteRejectedTargets(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        const string text = "Find the missing object beside the shelf; do not operate anything.";
        SceneVisionObservation source = null; yield return Prepare(chat, bridge, text, value => source = value);
        int requests = model.SceneSearchRequests.Count, operation = bridge.Controller.OperationRevision, matchCount = model.Matches.Count;
        bool done = false; Begin(chat, text, "find", source, () => done = true);
        var rejected = new HashSet<string>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int expected = requests + attempt + 1;
            yield return Wait(() => done || model.SceneSearchRequests.Count >= expected, 8, "next genuine target rejection");
            Check(!done, "A failed candidate alone does not terminate the whole search.");
            var request = model.SceneSearchRequests.Last();
            var chosen = CurrentCandidates(JObject.Parse(request.Input)).OfType<JObject>().FirstOrDefault(row =>
                (bool?)row["selectionAllowed"] == true && !rejected.Contains((string)row["entityId"]));
            Check(chosen != null, "A different actual offered entity is available for each bounded rejection.");
            rejected.Add((string)chosen["entityId"]); int beforeMatches = model.Matches.Count;
            request.Complete(true, Choice("current-candidate", (string)chosen["referenceId"]), "controlled-distinct-candidate");
            yield return Wait(() => model.Matches.Count > beforeMatches || done, 8, "independent rejection request");
            Check(!done, "Offered candidate reaches the independent matcher rather than invalid-reference rejection.");
            model.Matches.Last().Complete(true, new JObject { ["verdict"] = "different-target", ["candidateRole"] = "other", ["candidateId"] = "none",
                ["reason"] = "This actual entity is not the complete requested object." }.ToString(), "controlled-non-target");
        }
        yield return Wait(() => model.SceneSearchRequests.Count == requests + 4 || done, 8, "same-lease bounded recovery");
        Check(!done, "Three internal failures request useful recovery before terminating.");
        var recovery = model.SceneSearchRequests.Last(); var input = JObject.Parse(recovery.Input);
        Check((bool?)input["recovery"]?["onlyNewView"] == true && (int?)input["task"]["totalNoProgress"] == 3 &&
            (int?)input["task"]["recoveryAttempts"] == 1 && (int?)input["task"]["steps"] == 0 &&
            ((JArray)input["requestChoices"]).OfType<JObject>().All(row => (string)row["choice"] != "current-candidate"),
            "Recovery retains cumulative failures and offers only a new memory/view, never the rejected current candidates.");
        ExportRequest("three-failures-recovery-input", recovery, new JObject { ["requiresNewView"] = true, ["notTargetConfirmation"] = true });
        recovery.Complete(true, "{}", "controlled-invalid-recovery-plan");
        yield return Wait(() => done, 8, "invalid recovery cannot authorize motion or reopen a loop");
        var result = State(chat);
        Check((string)result["status"] == "incomplete" && (int?)result["recoveryAttempts"] == 1 &&
            (int?)result["totalNoProgress"] == 3 && (int?)result["decisions"] == 4 &&
            string.IsNullOrEmpty((string)result["foundEntityId"]) && model.SceneSearchRequests.Count == requests + 4,
            "One invalid recovery ends within the unchanged total budget without inventing a target or absence.");
        Check(model.Matches.Count == matchCount + 3 && bridge.Controller.OperationRevision == operation && Get(chat, "m_SpatialDeviceGoal") == null,
            "Failed candidate and recovery checks create no device goal, state change or native motion.");
        AddCase("three-real-rejections-one-bounded-recovery-no-false-absence", result);
    }

    private static IEnumerator ExerciseRecoveryMemory(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, bool cancel)
    {
        const string text = "Find the small orange marker seen earlier. The broad blue surface is only its background. Only observe.";
        SceneVisionObservation historical = null;
        yield return Prepare(chat, bridge, text, value => historical = value);
        Check(chat.SceneVisionCapture.TryGetCurrentCameraPose(out Pose pose), "Recovery fixture starts from an actual camera pose.");
        fixture = new GameObject("Bounded search recovery geometry"); fixture.transform.SetParent(bridge.environmentRoot, false);
        fixture.transform.SetPositionAndRotation(pose.position, pose.rotation);
        var marker = Panel("small orange marker", Vector3.forward * .55f, new Vector2(.12f, .12f));
        yield return Capture(chat, "recovery-historical-marker", value => historical = value);
        Check(chat.SceneVisionCapture.TryResolveObservedObject(historical, new Vector2(.5f, .5f), out var remembered, out _) &&
            remembered.Root == marker.transform, "Recovery target point is actual earlier opaque geometry.");
        Catalog(chat, historical);
        var blocker = Panel("temporary opaque marker occluder", Vector3.forward * .4f, new Vector2(.18f, .18f));
        blocker.sharedMaterial.color = Color.blue;
        SceneVisionObservation blocked = null; yield return Capture(chat, "recovery-marker-currently-occluded", value => blocked = value);
        var turn = new JObject { ["intent"] = "orient-view", ["origin"] = "user", ["evidence"] = text,
            ["viewChange"] = new JObject { ["captureId"] = blocked.CaptureId, ["yaw"] = 35, ["pitch"] = 0 } };
        Check(RoomTaskProtocol.TryDecision(turn.ToString(), text, false, out var direction), "Bounded observation setup passes the real view protocol.");
        bool turned = false; int operation = bridge.Controller.OperationRevision;
        Call(chat, "BeginSceneViewChange", direction, blocked, (int)Get(chat, "m_FormalResponseGeneration"), new Action(() => turned = true));
        yield return Wait(() => turned, 12, "actual head turn before recovery fixture");
        Check(chat.SceneVisionCapture.TryGetCurrentCameraPose(out Pose changedPose), "The actual changed pose supplies foreground distractor placement.");
        var distractors = new List<Renderer>();
        var background = Panel("broad blue background", Vector3.zero, new Vector2(3, 3));
        background.transform.SetPositionAndRotation(changedPose.position + changedPose.rotation * Vector3.forward * .9f, changedPose.rotation);
        background.sharedMaterial.color = Color.blue;
        for (int i = 0; i < 3; i++)
        {
            var panel = Panel("recovery distractor " + i, Vector3.zero, new Vector2(.18f, .18f));
            panel.transform.SetPositionAndRotation(changedPose.position + changedPose.rotation * new Vector3((i - 1) * .22f, 0, .7f), changedPose.rotation);
            panel.sharedMaterial.color = Color.cyan; distractors.Add(panel);
        }
        SceneVisionObservation current = null; yield return Capture(chat, "recovery-current-distractors", value => current = value);
        var actual = Catalog(chat, current);
        var wrongIds = ((JArray)actual["current"]["candidates"]).OfType<JObject>()
            .Where(row => ((string)row["authoredHint"]?["rendererName"] ?? "").StartsWith("recovery distractor ", StringComparison.Ordinal))
            .Select(row => (string)row["entityId"]).ToArray();
        Check(wrongIds.Length == 3, "Three real current objects provide distinct independent failed candidates.");
        int searches = model.SceneSearchRequests.Count, matches = model.Matches.Count; bool done = false;
        Begin(chat, text, "find", current, () => done = true);
        for (int i = 0; i < 3; i++)
        {
            int expected = searches + i + 1;
            yield return Wait(() => done || model.SceneSearchRequests.Count == expected, 8, "recovery-chain candidate request");
            Check(!done, "Search remains owned through candidate failures.");
            var request = model.SceneSearchRequests.Last(); var input = JObject.Parse(request.Input);
            var selected = CurrentCandidates(input).OfType<JObject>().Single(row => (string)row["entityId"] == wrongIds[i]);
            Check((bool?)selected["selectionAllowed"] == true, "Each failure selects a genuinely offered different entity.");
            request.Complete(true, Choice("current-candidate", (string)selected["referenceId"]), "controlled-recovery-chain-candidate");
            int expectedMatches = matches + i + 1;
            yield return Wait(() => done || model.Matches.Count == expectedMatches, 8, "recovery-chain independent target matcher");
            Check(!done, "Actual new RGB reaches each independent matcher.");
            model.Matches.Last().Complete(true, new JObject { ["verdict"] = "different-target", ["candidateRole"] = "other",
                ["candidateId"] = "none", ["reason"] = "This blue object is not the requested small orange marker." }.ToString(), "controlled-wrong-object");
        }
        yield return Wait(() => model.SceneSearchRequests.Count == searches + 4 || done, 8, "recovery with earlier target memory still available");
        Check(!done, "The unresolved target still gets one budgeted recovery rather than a clarification demand.");
        var recovery = model.SceneSearchRequests.Last(); var recoveryInput = JObject.Parse(recovery.Input);
        var memory = ((JArray)recoveryInput["visibleCatalog"]["memory"]).OfType<JObject>().Single(row =>
            (string)row["entityId"] == remembered.EntityId);
        Check((bool?)recoveryInput["recovery"]?["onlyNewView"] == true && (bool?)memory["positionMemoryValid"] == true &&
            (bool?)memory["selectionAllowed"] == true && (bool?)memory["actionAuthorization"] == false &&
            (int?)recoveryInput["task"]["totalNoProgress"] == 3,
            "Unbound earlier target identity remains a usable direction clue while the three rejected entities are unavailable as targets.");
        ExportRequest(cancel ? "recovery-memory-cancelled" : "recovery-memory-target-return", recovery,
            new JObject { ["expectedChoice"] = "historical-object", ["expectedRole"] = "target", ["targetReference"] = memory["referenceId"]?.DeepClone(),
                ["targetIdentityIsOracleOnly"] = true, ["noDeviceAuthority"] = true });
        if (cancel)
        {
            int sequence = (int)Get(chat, "m_MotionSequence"), count = model.SceneSearchRequests.Count;
            chat.NotifyUserStartedSpeaking();
            Call(chat, "RecordAcceptedMotionUserTurn", "Stop searching. Stay here.");
            Set(chat, "m_UserSpeechActiveForAutonomy", false);
            recovery.Complete(true, Choice("historical-object", (string)memory["referenceId"]), "controlled-late-recovery-choice");
            yield return null; yield return null;
            Check(!done && Get(chat, "m_SceneSearch") == null && (int)Get(chat, "m_MotionSequence") == sequence &&
                model.SceneSearchRequests.Count == count && bridge.Controller.OperationRevision == operation,
                "A late recovery choice after a new user turn cannot move, match, reopen search or complete the replaced turn.");
            AddCase("bounded-recovery-new-user-revokes-late-memory-return", (JObject)Call(chat, "BuildSceneSearchContext"));
        }
        else
        {
            // Remove only the temporary occluder; the remembered target's frozen
            // geometry does not move. Its return is proved by newly rendered RGB.
            blocker.gameObject.SetActive(false);
            recovery.Complete(true, Choice("historical-object", (string)memory["referenceId"]), "controlled-unvisited-target-memory");
            yield return Wait(() => model.SceneSearchRequests.Count == searches + 5 || done, 15, "real head returns toward the retained target point");
            Check(!done && (int?)State(chat)["steps"] == 1 && (int?)State(chat)["recoveryAttempts"] == 1 &&
                (int?)State(chat)["totalNoProgress"] == 3 && bridge.Controller.OperationRevision == operation,
                "Recovery performs one real head observation without resetting total failure/motion budgets or starting a body/device operation.");
            var returned = chat.SceneVisionCapture.LatestObservation;
            Check(returned.CaptureId != historical.CaptureId && returned.CaptureId != current.CaptureId &&
                Quaternion.Angle(current.CameraRotation, returned.CameraRotation) > 15,
                "Recovery creates a measured new view instead of reusing historical pixels.");
            var request = model.SceneSearchRequests.Last(); var selected = CurrentCandidates(JObject.Parse(request.Input)).OfType<JObject>()
                .Single(row => (string)row["entityId"] == remembered.EntityId);
            request.Complete(true, Choice("current-candidate", (string)selected["referenceId"]), "controlled-recovered-current-target");
            yield return Wait(() => model.Matches.Count == matches + 4 || done, 8, "recovered target still requires fresh independent matching");
            Check(!done, "Memory itself did not complete the search.");
            var match = model.Matches.Last(); var matchInput = JObject.Parse(match.Input);
            var selectedMatch = ((JArray)matchInput["candidates"]["candidates"]).OfType<JObject>()
                .Single(row => (string)row["entityId"] == remembered.EntityId);
            match.Complete(true, new JObject { ["verdict"] = "matched", ["candidateRole"] = "target",
                ["candidateId"] = selectedMatch["candidateId"]?.DeepClone(), ["reason"] = "Fresh pixels show the requested orange marker itself." }.ToString(), "controlled-fresh-recovery-target");
            yield return Wait(() => done, 12, "same-entity fresh proof completes the bounded recovery");
            var result = State(chat);
            Check((string)result["status"] == "found" && (string)result["foundEntityId"] == remembered.EntityId &&
                (int?)result["recoveryAttempts"] == 1 && (int?)result["decisions"] == 5 &&
                (bool?)result["deviceWriteAuthorized"] == false && Get(chat, "m_SpatialDeviceGoal") == null,
                "Recovered semantic identity requires fresh proof; the bounded search still creates no goal or device permission.");
            AddCase("three-failed-entities-unvisited-memory-real-head-return-fresh-match", result);
        }
        UnityEngine.Object.DestroyImmediate(fixture); fixture = null;
    }

    private static IEnumerator ExerciseFrozenMemoryWhilePending(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, bool recovery, bool cancel)
    {
        const string text = "Find the small orange marker I saw earlier. Only look; do not operate anything.";
        SceneVisionObservation historical = null;
        yield return Prepare(chat, bridge, text, value => historical = value);
        Check(chat.SceneVisionCapture.TryGetCurrentCameraPose(out Pose pose), "Frozen-offer fixture starts at an actual camera pose.");
        fixture = new GameObject("Frozen historical offer during planner wait"); fixture.transform.SetParent(bridge.environmentRoot, false);
        fixture.transform.SetPositionAndRotation(pose.position, pose.rotation);
        var marker = Panel("orange marker quoted from old capture", Vector3.forward * .55f, new Vector2(.12f, .12f));
        yield return Capture(chat, "frozen-offer-original-marker", value => historical = value);
        Check(chat.SceneVisionCapture.TryResolveObservedObject(historical, new Vector2(.5f, .5f), out var remembered, out _) &&
            remembered.Root == marker.transform, "Quoted memory starts with actual earlier marker pixels and frozen geometry.");
        Catalog(chat, historical);
        var blocker = Panel("temporary marker occluder during model wait", Vector3.forward * .4f, new Vector2(.18f, .18f));
        blocker.sharedMaterial.color = Color.blue;
        SceneVisionObservation blocked = null;
        yield return Capture(chat, "frozen-offer-hidden-marker", value => blocked = value);
        Catalog(chat, blocked);
        int searches = model.SceneSearchRequests.Count, matches = model.Matches.Count;
        int sequence = (int)Get(chat, "m_MotionSequence"), operation = bridge.Controller.OperationRevision;
        bool done = false; Begin(chat, text, "find", blocked, () => done = true);
        yield return Wait(() => model.SceneSearchRequests.Count > searches, 8, "frozen historical offer pending request");
        if (recovery)
        {
            int answered = searches;
            while (JObject.Parse(model.SceneSearchRequests.Last().Input)["recovery"] == null)
            {
                Check(!done && answered - searches <= 2, "Premature done considers at most the existing two historical frames before the single recovery.");
                model.SceneSearchRequests.Last().Complete(true, Choice("done", ""), "controlled-no-target-before-frozen-offer-recovery");
                answered++;
                yield return Wait(() => done || model.SceneSearchRequests.Count > answered, 8, "bounded historical reviews reach frozen-offer recovery");
                Check(!done, "A useful historical point keeps the one recovery available.");
            }
        }
        var request = model.SceneSearchRequests.Last(); var input = JObject.Parse(request.Input);
        var offer = ((JArray)input["visibleCatalog"]["memory"]).OfType<JObject>().Single(row =>
            (string)row["entityId"] == remembered.EntityId);
        Check((bool?)offer["selectionAllowed"] == true && (string)offer["sourceCaptureId"] == historical.CaptureId &&
            (bool?)offer["actionAuthorization"] == false,
            "The pending model was offered one exact old point, not an authorized or matched target.");
        var pendingObservation = chat.SceneVisionCapture.LatestObservation;
        blocker.gameObject.SetActive(false);
        SceneVisionObservation newlyVisible = null;
        yield return Capture(chat, "frozen-offer-new-pixels-while-model-pending", value => newlyVisible = value);
        var updated = Catalog(chat, newlyVisible);
        Check(((JArray)updated["current"]["candidates"]).OfType<JObject>().Any(row => (string)row["entityId"] == remembered.EntityId) &&
            !((JArray)updated["memory"]).OfType<JObject>().Any(row => (string)row["entityId"] == remembered.EntityId &&
                (string)row["sourceCaptureId"] == historical.CaptureId),
            "A real intervening capture replaces the offered old memory entry with current same-entity pixels while the model is pending.");
        if (cancel)
        {
            int count = model.SceneSearchRequests.Count;
            chat.NotifyUserStartedSpeaking(); Call(chat, "RecordAcceptedMotionUserTurn", "Stop searching and remain here.");
            Set(chat, "m_UserSpeechActiveForAutonomy", false);
            request.Complete(true, Choice("historical-object", (string)offer["referenceId"]), "controlled-late-frozen-offer-after-user-cancel");
            yield return null; yield return null;
            Check(!done && Get(chat, "m_SceneSearch") == null && model.Matches.Count == matches &&
                model.SceneSearchRequests.Count == count && (int)Get(chat, "m_MotionSequence") == sequence &&
                bridge.Controller.OperationRevision == operation,
                "A frozen point cannot survive the old search owner: no late matching, motion or completion after a new user turn.");
            AddCase("pending-memory-replaced-new-user-revokes-frozen-offer", State(chat));
        }
        else
        {
            yield return Wait(() => Time.realtimeSinceStartup - pendingObservation.CapturedAtRealtime > 12.05f, 14,
                "pending original pixels really expire before the frozen memory answer");
            request.Complete(true, Choice("historical-object", (string)offer["referenceId"]), "controlled-exact-quoted-old-point");
            yield return Wait(() => model.Matches.Count > matches || done, 8, "quoted old point survives a real freshness capture and reaches independent matching");
            Check(!done && model.Matches.Count == matches + 1 && (int)Get(chat, "m_MotionSequence") == sequence &&
                bridge.Controller.OperationRevision == operation && (int?)State(chat)["steps"] == 0,
                "Already reobserved same entity goes to independent matching without an unnecessary turn or premature completion.");
            var receipt = ((JArray)State(chat)["receipts"]).OfType<JObject>().Single(row => (string)row["stage"] == "quoted-memory-reobserved");
            Check((string)receipt["sourceCaptureId"] == historical.CaptureId &&
                JToken.DeepEquals(JObject.Parse(receipt.ToString())["quotedPointAtCapture"], offer["pointAtCapture"]) &&
                (string)receipt["resultCaptureId"] != historical.CaptureId &&
                (bool?)receipt["semanticIdentityConfirmed"] == false && (bool?)receipt["deviceWriteAuthorized"] == false,
                "The old source and exact numeric point remain quoted; newly captured identity alone is not semantic proof or device permission.");
            var match = model.Matches.Last(); var matchInput = JObject.Parse(match.Input);
            var selected = ((JArray)matchInput["candidates"]["candidates"]).OfType<JObject>().Single(row =>
                (string)row["entityId"] == remembered.EntityId);
            match.Complete(true, new JObject { ["verdict"] = "matched", ["candidateRole"] = "target",
                ["candidateId"] = selected["candidateId"]?.DeepClone(), ["reason"] = "New pixels independently show the requested orange marker." }.ToString(),
                "controlled-independent-frozen-offer-match");
            yield return Wait(() => done, 12, "fresh same-entity recapture completes the frozen-offer target proof");
            var result = State(chat);
            Check((string)result["status"] == "found" && (string)result["foundEntityId"] == remembered.EntityId &&
                (int?)result["steps"] == 0 && (int?)result["recoveryAttempts"] == (recovery ? 1 : 0) &&
                (bool?)result["deviceWriteAuthorized"] == false && Get(chat, "m_SpatialDeviceGoal") == null,
                "A replaced memory entry still yields bounded fresh target proof, with no motion or new device authorization.");
            AddCase(recovery ? "recovery-pending-memory-replaced-frozen-source-fresh-match" :
                "ordinary-pending-memory-replaced-frozen-source-fresh-match", result);
        }
        UnityEngine.Object.DestroyImmediate(fixture); fixture = null;
    }

    private static IEnumerator ExercisePrematureDoneRecovery(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model)
    {
        const string text = "Find the previously seen object; a checked patch of floor is not a whole-room search.";
        SceneVisionObservation current = null; yield return Prepare(chat, bridge, text, value => current = value);
        int requests = model.SceneSearchRequests.Count; bool done = false;
        Begin(chat, text, "find", current, () => done = true);
        yield return Wait(() => model.SceneSearchRequests.Count > requests, 8, "premature done initial request");
        model.SceneSearchRequests.Last().Complete(true, Choice("done", ""), "controlled-premature-done");
        int answered = requests + 1; NevaSceneVisionIntegrationModel.Request recovery = null;
        while (recovery == null)
        {
            yield return Wait(() => done || model.SceneSearchRequests.Count > answered, 8, "premature done must consider remaining evidence");
            Check(!done, "A premature done cannot bypass available memory/direction recovery.");
            var request = model.SceneSearchRequests[answered++]; var input = JObject.Parse(request.Input);
            if ((bool?)input["recovery"]?["onlyNewView"] == true) { recovery = request; break; }
            Check((string)input["imageRole"] == "historical" && answered <= requests + 3,
                "Only the existing bounded historical review may precede the single done recovery.");
            request.Complete(true, Choice("done", ""), "controlled-no-target-in-old-picture");
        }
        var recoveryInput = JObject.Parse(recovery.Input);
        Check((string)recoveryInput["recovery"]["trigger"] == "premature-done" &&
            (int?)recoveryInput["task"]["noProgressSteps"] == 0 && (int?)recoveryInput["task"]["recoveryAttempts"] == 1,
            "Done shares the same one-shot recovery budget even without three candidate failures.");
        recovery.Complete(false, "", "controlled-recovery-provider-unavailable");
        yield return Wait(() => done, 8, "unavailable recovery ends finitely");
        Check((string)State(chat)["status"] == "incomplete" && (int?)State(chat)["recoveryAttempts"] == 1 &&
            (int?)State(chat)["steps"] == 0 && model.SceneSearchRequests.Count == answered,
            "Failed recovery is not retried recursively or described as target absence.");
        AddCase("premature-done-still-gets-one-existing-budget-recovery", State(chat));
    }

    private static IEnumerator ExerciseLifecycle(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        foreach (string mode in new[] { "new-user", "new-generation", "work-reset", "application-pause", "eyes-closed", "scene-reset", "room-rebound" })
        {
            const string text = "继续四处看看。";
            SceneVisionObservation source = null; yield return Prepare(chat, bridge, text, value => source = value);
            int requests = model.SceneSearchRequests.Count; bool completed = false;
            Begin(chat, text, "survey", source, () => completed = true);
            yield return Wait(() => model.SceneSearchRequests.Count > requests, 8, "held search planner before " + mode);
            var held = model.SceneSearchRequests.Last(); var region = Region(held, 30);
            int operation = bridge.Controller.OperationRevision, matches = model.Matches.Count;
            var environment = bridge.environmentRoot;
            if (mode == "new-user") Call(chat, "RecordAcceptedMotionUserTurn", "先停下，我要问你别的事情。");
            else if (mode == "new-generation") Call(chat, "BeginFormalResponseGeneration");
            else if (mode == "work-reset") Call(chat, "ResetWorkReview", true);
            else if (mode == "application-pause") Call(chat, "OnApplicationPause", true);
            else if (mode == "eyes-closed") { Set(chat, "m_AgentEyesOpen", false); Call(chat, "ResetSceneVision", "eyes-closed", true); }
            else if (mode == "scene-reset") Call(chat, "ResetSceneVision", "search-regression-scene-reset", false);
            else bridge.environmentRoot = null;
            try
            {
                yield return Wait(() => (bool?)State(chat)["active"] != true, 3, "pending search cancels on " + mode);
                held.Complete(true, Choice("explore-region", (string)region["referenceId"]), "late-superseded-search-result");
                yield return null; yield return null;
                var cancellation = Get(chat, "m_SceneSearchLastReport") as JObject;
                Check(!completed && model.SceneSearchRequests.Count == requests + 1 && model.Matches.Count == matches &&
                    bridge.Controller.OperationRevision == operation && !(bool)Get(chat, "m_SceneActionActive") &&
                    (string)cancellation?["status"] == "cancelled", "Cancellation while awaiting a planner suppresses late actions, callbacks and replanning: " + mode);
                if (mode == "new-generation") Check(State(chat)["status"] == null,
                    "A new response generation does not inherit the cancelled prior search's terminal result.");
                AddCase("held-planner-cancelled-" + mode, cancellation);
            }
            finally
            {
                bridge.environmentRoot = environment;
                if (mode == "application-pause") Call(chat, "OnApplicationPause", false);
                Set(chat, "m_AgentEyesOpen", true); Call(chat, "ClearScheduledAgentWake");
            }
        }
    }

    private static IEnumerator ExerciseDeadline(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model)
    {
        const string text = "四处看看。";
        SceneVisionObservation source = null; yield return Prepare(chat, bridge, text, value => source = value);
        int requests = model.SceneSearchRequests.Count, operation = bridge.Controller.OperationRevision, completed = 0;
        Begin(chat, text, "survey", source, () => completed++);
        yield return Wait(() => model.SceneSearchRequests.Count > requests, 8, "held planner before deadline");
        var held = model.SceneSearchRequests.Last(); var lease = Get(chat, "m_SceneSearch");
        Check(lease != null, "The deadline fixture holds a real production search lease.");
        // Advance the lease's elapsed-time input, not wall time or actor pose. This exercises
        // the production deadline branch without imposing a three-minute suite delay.
        Set(lease, "startedAt", Time.realtimeSinceStartup - 181f);
        yield return Wait(() => completed == 1, 3, "production search watchdog reaches its duration limit");
        held.Complete(true, Choice("explore-region", (string)Region(held, 30)["referenceId"]), "late-after-search-duration-limit");
        yield return null; yield return null;
        var result = State(chat);
        Check((string)result["status"] == "incomplete" && (string)result["reason"] == "search-time-budget" &&
            (int?)result["steps"] == 0 && completed == 1 && model.SceneSearchRequests.Count == requests + 1 &&
            bridge.Controller.OperationRevision == operation, "Time budget settles once with zero fictitious actions and rejects late planner output.");
        LastReport["deadlineTestScope"] = "Real pending production request and watchdog; startedAt is advanced by 181 seconds to test elapsed-time handling. No claim of a 180-second wall-clock run.";
        AddCase("held-planner-elapsed-time-budget-settles-once", result);
    }

    private static IEnumerator ExerciseActiveCancellation(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model, bool native)
    {
        const string text = "先四处看看。";
        SceneVisionObservation source = null; yield return Prepare(chat, bridge, text, value => source = value);
        int requests = model.SceneSearchRequests.Count; bool completed = false;
        Begin(chat, text, "survey", source, () => completed = true);
        yield return Wait(() => model.SceneSearchRequests.Count > requests, 8, "search decision before active cancellation");
        var request = model.SceneSearchRequests.Last();
        request.Complete(true, Choice("explore-region", (string)Region(request, native ? 120 : 30)["referenceId"]), "controlled-search-active-cancel");
        yield return Wait(() => (bool)Get(chat, "m_SceneActionActive") &&
            (native ? bridge.IsOrientingScene : Get(chat, "m_SceneAttention") is NevaSceneAttention a && a.IsBusy),
            native ? 45 : 8, "search really acquires its head/body action");
        string action = (string)JObject.Parse(chat.SceneActionContextJson)["lastAction"]?["actionId"];
        Call(chat, "RecordAcceptedMotionUserTurn", "停止观察，先听我说。");
        yield return Wait(() => !(bool)Get(chat, "m_SceneActionActive") && !bridge.ReservesBody && HeadIdle(chat), 8,
            "new user releases the actual search action and retained head owner");
        yield return null; yield return null;
        var result = Get(chat, "m_SceneSearchLastReport") as JObject;
        Check(!string.IsNullOrEmpty(action) && !completed && model.SceneSearchRequests.Count == requests + 1 &&
            (string)result?["status"] == "cancelled" && (bool?)result?["deviceWriteAuthorized"] == false,
            "Interrupting an active search action releases exact ownership and cannot continue from its late result frame.");
        AddCase(native ? "new-user-cancels-active-native-search" : "new-user-cancels-active-head-search", result);
    }

    private static JObject Region(NevaSceneVisionIntegrationModel.Request request, float desired)
    {
        var regions = (JArray)JObject.Parse(request.Input)["explorationRegions"];
        Check(regions != null && regions.Count > 0, "Planner receives bounded engine-authored exploration regions.");
        return regions.OfType<JObject>().Where(value => (bool?)value["selectionAllowed"] != false)
            .OrderBy(value => Mathf.Abs((float)value["yawFromStartDegrees"] - desired)).First();
    }
    private static IEnumerator DeclineAdditionalHistoricalReviews(NevaSceneVisionIntegrationModel model,Func<bool> completed,int alreadyRequested,string label)
    {
        int answered=alreadyRequested;
        while(!completed())
        {
            yield return Wait(()=>completed()||model.SceneSearchRequests.Count>answered,8,label);
            if(completed())yield break;
            var pending=model.SceneSearchRequests[answered++];
            var input=JObject.Parse(pending.Input);
            bool recovery=(bool?)input["recovery"]?["onlyNewView"]==true;
            Check(answered<=alreadyRequested+3 && ((string)input["imageRole"]=="historical" || recovery),
                "At most two historical reviews and one explicit recovery may precede incomplete completion.");
            if(recovery) pending.Complete(false,"","controlled-recovery-provider-unavailable");
            else pending.Complete(true,Choice("done",""),"controlled-decline-nonmatching-history");
        }
    }
    private static string Choice(string choice, string reference) => new JObject {
        ["choice"] = choice, ["referenceId"] = reference, ["selectionRole"] = choice == "current-candidate" || choice == "historical-object" ? "target" : "exploration", ["reason"] = "Controlled selection from this exact production search request." }.ToString();
    private static JObject State(ChatSample chat) => (JObject)Call(chat, "BuildSceneSearchContext");
    private static JObject Catalog(ChatSample chat, SceneVisionObservation view) => (JObject)Call(chat, "BuildSceneVisibleCatalogContext", view);
    private static JArray CurrentCandidates(JObject input) => input["visibleCatalog"]?["current"]?["candidates"] as JArray ?? new JArray();
    private static JObject FindEntity(JToken value, string entity)
    {
        if (value is JObject item && (string)item["entityId"] == entity) return item;
        if (value is JContainer container) foreach (var child in container.Children()) { var found = FindEntity(child, entity); if (found != null) return found; }
        return null;
    }
    private static bool Resolve(ChatSample chat, string kind, string id, SceneVisionObservation current, out Vector3 point, out string reason)
    {
        object[] args = { kind, id, current, Vector3.zero, null };
        bool resolved = (bool)Call(chat, "TryResolveSceneSearchSelection", args); point = (Vector3)args[3]; reason = args[4] as string; return resolved;
    }
    private static Renderer Panel(string name, Vector3 position, Vector2 size)
    {
        var panel = GameObject.CreatePrimitive(PrimitiveType.Quad); panel.name = name; panel.transform.SetParent(fixture.transform, false);
        panel.transform.localPosition = position; panel.transform.localScale = new Vector3(size.x, size.y, 1);
        UnityEngine.Object.DestroyImmediate(panel.GetComponent<Collider>());
        var material = new Material(Shader.Find("Unlit/Color")) { color = new Color(.9f, .3f, .15f, 1) }; materials.Add(material);
        var renderer = panel.GetComponent<Renderer>(); renderer.sharedMaterial = material; return renderer;
    }
    private static bool HeadIdle(ChatSample chat) => !(Get(chat, "m_SceneAttention") is NevaSceneAttention attention) || !attention.IsBusy;
    private static void ValidateRequest(ChatSample chat, NevaSceneVisionIntegrationModel.Request request, SceneVisionObservation current)
    {
        var input = JObject.Parse(request.Input);
        int pageIndex = (int?)input["visibleCatalog"]?["current"]?["pageIndex"] ?? 0;
        object[] args = { current, pageIndex, null, null };
        bool hasPage = (bool)Call(chat, "TryGetSceneVisibleCatalogPage", args);
        var page = args[2] as SceneObjectCandidateView;
        Check(request.Kind == "scene-search" && request.Image == (hasPage ? page.ImageDataUrl : current.ImageDataUrl) &&
            (string)input["currentCaptureId"] == current.CaptureId && (!hasPage || page.CaptureId == current.CaptureId),
            "Production search request uses the numbered page generated from this exact current RGB capture, never an old view.");
        Check((string)input["imageRole"] == "current" && JObject.Parse(request.Wire)["messages"] != null,
            "Captured production wire labels current pixels rather than silently treating historical pixels as current.");
    }
    private static void ExportRequest(string id, NevaSceneVisionIntegrationModel.Request request, JObject expected)
    {
        File.WriteAllText(Path.Combine(directory, id + ".input.json"), request.Input);
        File.WriteAllText(Path.Combine(directory, id + ".request.json"), request.Wire);
        File.WriteAllText(Path.Combine(directory, id + ".image-data-url.txt"), request.Image ?? "");
        exports.Add(new JObject { ["id"] = id, ["input"] = id + ".input.json", ["request"] = id + ".request.json",
            ["image"] = id + ".image-data-url.txt", ["expected"] = expected.DeepClone() });
    }
    private static void ExportImage(string id, SceneVisionObservation view)
    {
        byte[] bytes = Convert.FromBase64String(view.ImageDataUrl.Substring(view.ImageDataUrl.IndexOf(',') + 1));
        File.WriteAllBytes(Path.Combine(directory, id + ".jpg"), bytes);
        File.WriteAllText(Path.Combine(directory, id + ".metadata.json"), view.MetadataJson);
        using (var hash = SHA256.Create()) LastReport[id + "ImageSha256"] = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    private static void AddCase(string name, JObject facts) => ((JArray)LastReport["cases"]).Add(new JObject { ["name"] = name, ["facts"] = facts.DeepClone() });
    private static IEnumerator Wait(Func<bool> ready, float seconds, string label)
    { float until = Time.realtimeSinceStartup + seconds; while (!ready()) { if (Time.realtimeSinceStartup > until) throw new TimeoutException(label); yield return null; } }
    private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
    private static object Get(object target, string field) => target.GetType().GetField(field, Flags).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] args)
    {
        Type type = target as Type ?? target.GetType();
        try { return type.GetMethods(Flags).Single(value => value.Name == name && value.GetParameters().Length == args.Length).Invoke(target is Type ? null : target, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
}
