using System;
using System.Collections.Generic;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private const int SceneVisibleMemoryLimit = 48;
    private const int SceneVisibleViewLimit = 6;
    private const float SceneVisibleMemorySeconds = SceneHistoricalMemorySeconds;

    private sealed class SceneVisibleCatalogView
    {
        public SceneVisibleObjectCatalog catalog;
        public SceneVisionObservation observation;
        public NevaSceneVisionCapture source;
        public Transform environment;
        public UniVRM10.Vrm10Instance avatar;
        public LLM model;
        public int visionEpoch, activePage;
        public string purpose;
    }
    private sealed class SceneVisibleMemory
    {
        public SceneVisibleCatalogView view;
        public SceneVisibleObjectCatalog.Entry entry;
        public string description, descriptionCaptureId, matchedOriginalTarget, matchCaptureId;
        public bool positionInvalidated;
        public int observationCount = 1;
        public float lastReinforcedAt;
    }
    private readonly List<SceneVisibleCatalogView> m_SceneVisibleCatalogViews = new List<SceneVisibleCatalogView>();
    private readonly List<SceneVisibleMemory> m_SceneVisibleMemory = new List<SceneVisibleMemory>();
    private sealed class SceneVisiblePointValidity { public bool invalidated; }
    // Revocation belongs to the sampled source point, not a particular recall
    // record. Rereading an old image cannot revive geometry that already moved.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SceneVisibleObjectCatalog.Entry,SceneVisiblePointValidity>
        m_SceneVisiblePointValidity=new System.Runtime.CompilerServices.ConditionalWeakTable<SceneVisibleObjectCatalog.Entry,SceneVisiblePointValidity>();
    private SceneVisibleCatalogView m_SceneVisibleCurrent;
    // Derived data lives exactly as long as an already-retained observation. This
    // weak table does not add another image-history owner or grow its frame budget.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SceneVisionObservation, SceneVisibleCatalogView>
        m_SceneVisibleCaptureOwners = new System.Runtime.CompilerServices.ConditionalWeakTable<SceneVisionObservation, SceneVisibleCatalogView>();
    private SceneObjectCandidateView m_SceneSearchCandidatePreview;
    private string m_SceneSearchPreviewPurpose;
    private readonly Dictionary<string, SceneVisibleMemory> m_SceneHistoricalPointOffers = new Dictionary<string, SceneVisibleMemory>(StringComparer.Ordinal);
    public SceneObjectCandidateView LastSceneSearchCandidateView => m_SceneSearchCandidatePreview;
    public string LastSceneSearchPreviewPurpose => m_SceneSearchPreviewPurpose;

    private void SetSceneSearchCandidatePreview(SceneObjectCandidateView view, string purpose)
    { m_SceneSearchCandidatePreview = view; m_SceneSearchPreviewPurpose = purpose ?? "search"; }

    private bool SceneVisibleCatalogHistoryBinding(SceneVisibleCatalogView view) => view != null &&
        SceneHistoricalObservationReadable(view.source, view.observation, view.environment, view.avatar, view.model) &&
        SceneSearchMemoryAgeValid(view.observation.CapturedAtRealtime, Time.realtimeSinceStartup);

    private bool SceneVisibleCatalogBinding(SceneVisibleCatalogView view)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        return view != null && SceneEyesOpen && view.source != null && view.source == m_SceneVisionCapture &&
            view.source.isActiveAndEnabled && bridge != null && view.environment != null && view.environment == bridge.environmentRoot &&
            view.avatar != null && view.avatar == bridge.avatar && view.source.Target == view.avatar &&
            view.model != null && view.model == m_ChatSettings?.m_ChatModel && view.visionEpoch == m_SceneVisionEpoch &&
            view.source.IsObservationBindingCurrent(view.observation) &&
            SceneSearchMemoryAgeValid(view.observation.CapturedAtRealtime, Time.realtimeSinceStartup);
    }

    private static bool SceneSearchMemoryAgeValid(float capturedAt, float now)
    {
        return !float.IsNaN(capturedAt) && !float.IsInfinity(capturedAt) &&
            !float.IsNaN(now) && !float.IsInfinity(now) && now >= capturedAt &&
            now - capturedAt <= SceneVisibleMemorySeconds;
    }

    private void PruneSceneVisibleCatalog()
    {
        m_SceneVisibleCatalogViews.RemoveAll(view => !SceneVisibleCatalogHistoryBinding(view));
        m_SceneVisibleMemory.RemoveAll(sample => !SceneVisibleCatalogHistoryBinding(sample.view));
        if (!SceneVisibleCatalogBinding(m_SceneVisibleCurrent)) m_SceneVisibleCurrent = null;
        var expiredOffers = new List<string>();
        foreach (var offer in m_SceneHistoricalPointOffers)
            if (FindRetainedSceneVisibleCatalog(offer.Value.view.observation) == null) expiredOffers.Add(offer.Key);
        foreach (string reference in expiredOffers) m_SceneHistoricalPointOffers.Remove(reference);
    }

    private void TrimSceneVisibleMemory()
    {
        m_SceneVisibleMemory.Sort((a, b) => {
            int priority = (!string.IsNullOrEmpty(b.matchedOriginalTarget) ? 2 : !string.IsNullOrEmpty(b.description) ? 1 : 0)
                .CompareTo(!string.IsNullOrEmpty(a.matchedOriginalTarget) ? 2 : !string.IsNullOrEmpty(a.description) ? 1 : 0);
            if (priority != 0) return priority;
            int age = b.view.observation.CapturedAtRealtime.CompareTo(a.view.observation.CapturedAtRealtime);
            return age != 0 ? age : string.CompareOrdinal(a.entry.referenceId, b.entry.referenceId);
        });
        // A record retains its frozen RGB/ID source. Bound distinct retained source
        // frames as well as entity count, including frames no longer in the view ring.
        var frames = new HashSet<string>(StringComparer.Ordinal);
        m_SceneVisibleMemory.RemoveAll(item => {
            if (frames.Contains(item.entry.sourceCaptureId)) return false;
            if (frames.Count >= SceneVisibleViewLimit) return true;
            frames.Add(item.entry.sourceCaptureId); return false;
        });
        if (m_SceneVisibleMemory.Count > SceneVisibleMemoryLimit)
            m_SceneVisibleMemory.RemoveRange(SceneVisibleMemoryLimit, m_SceneVisibleMemory.Count - SceneVisibleMemoryLimit);
    }

    // Safe for actual sweep callback frames, even after the camera moves. This is
    // capture-time geometry registration only, with no semantic or device effects.
    private void ObserveSceneVisibleCatalog(SceneVisionObservation observation, string purpose)
    {
        PruneSceneVisibleCatalog();
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (!m_EnableSceneObjectAwareness || !SceneEyesOpen || observation?.HasImage != true ||
            bridge?.environmentRoot == null || bridge.avatar == null || m_SceneVisionCapture == null ||
            m_ChatSettings?.m_ChatModel == null || m_SceneVisionCapture.Target != bridge.avatar ||
            !m_SceneVisionCapture.IsObservationBindingCurrent(observation) || !SceneVisionGrounding.Fresh(observation)) return;
        if (m_SceneVisibleCurrent?.observation.CaptureId == observation.CaptureId) return;
        foreach (var existing in m_SceneVisibleCatalogViews)
            if (existing.observation.CaptureId == observation.CaptureId) { m_SceneVisibleCurrent = existing; return; }
        if (!m_SceneVisionCapture.TryBuildVisibleObjectCatalog(observation, out var catalog, out _)) return;
        var view = new SceneVisibleCatalogView { catalog = catalog, observation = observation, source = m_SceneVisionCapture,
            environment = bridge.environmentRoot, avatar = bridge.avatar, model = m_ChatSettings.m_ChatModel,
            visionEpoch = m_SceneVisionEpoch, purpose = purpose ?? "" };
        observation.VisibleCatalogSnapshot = catalog;
        m_SceneVisibleCaptureOwners.Remove(observation); m_SceneVisibleCaptureOwners.Add(observation, view);
        m_SceneVisibleCurrent = view;
        // Refresh similar viewpoints without crowding out an earlier useful direction.
        bool newerSimilarView = m_SceneVisibleCatalogViews.Exists(old => SceneDialogueSameView(old.observation, observation) &&
            old.observation.CapturedAtRealtime > observation.CapturedAtRealtime);
        if (!newerSimilarView)
        {
            m_SceneVisibleCatalogViews.RemoveAll(old => SceneDialogueSameView(old.observation, observation));
            m_SceneVisibleCatalogViews.Add(view);
        }
        m_SceneVisibleCatalogViews.Sort((a, b) => a.observation.CapturedAtRealtime.CompareTo(b.observation.CapturedAtRealtime));
        while (m_SceneVisibleCatalogViews.Count > SceneVisibleViewLimit) m_SceneVisibleCatalogViews.RemoveAt(0);
        foreach (var entry in catalog.Candidates)
        {
            // ID pixels can include the room/avatar boundary. Only pixels whose actual
            // observed root belongs to this room enter the room's retained catalog.
            if (entry.observed.Root == null || (entry.observed.Root != bridge.environmentRoot &&
                !entry.observed.Root.IsChildOf(bridge.environmentRoot))) continue;
            var old = m_SceneVisibleMemory.Find(item => item.entry.entityId == entry.entityId);
            if (old != null && old.view.observation.CapturedAtRealtime > observation.CapturedAtRealtime) continue;
            var sample = new SceneVisibleMemory { view = view, entry = entry,
                description = old?.description, descriptionCaptureId = old?.descriptionCaptureId,
                matchedOriginalTarget = old?.matchedOriginalTarget, matchCaptureId = old?.matchCaptureId,
                observationCount = old == null ? 1 : Math.Min(5, old.observationCount +
                    (observation.CapturedAtRealtime - old.lastReinforcedAt >= 2f ? 1 : 0)),
                lastReinforcedAt = old == null || observation.CapturedAtRealtime - old.lastReinforcedAt >= 2f
                    ? observation.CapturedAtRealtime : old.lastReinforcedAt };
            if (old != null) m_SceneVisibleMemory.Remove(old);
            m_SceneVisibleMemory.Add(sample);
        }
        // The page ordering is spatially diverse and small-item first. Retain the
        // first entries of the newest capture instead of the last large surfaces.
        TrimSceneVisibleMemory();
    }

    private SceneVisibleCatalogView GetCurrentSceneVisibleCatalog(SceneVisionObservation current)
    {
        if (!m_EnableSceneObjectAwareness || !SceneEyesOpen) return null;
        ObserveSceneVisibleCatalog(current, "current-planner-view");
        return m_SceneVisibleCurrent != null && m_SceneVisibleCurrent.observation.CaptureId == current?.CaptureId &&
            SceneVisibleCatalogBinding(m_SceneVisibleCurrent) && IsFreshSceneObservation(current) &&
            m_SceneVisionCapture.IsCurrentViewCompatible(current, .35f, 25f) ? m_SceneVisibleCurrent : null;
    }

    private bool TryGetSceneVisibleCatalogPage(SceneVisionObservation current, int pageIndex,
        out SceneObjectCandidateView page, out string reason)
    {
        page = null; var view = GetCurrentSceneVisibleCatalog(current);
        if (view == null) { reason = "No fresh current catalog in this room/avatar/visual binding."; return false; }
        return view.catalog.TryBuildPage(pageIndex, out page, out reason);
    }

    private bool TrySelectSceneVisibleCatalogPage(SceneVisionObservation current, string referenceId,
        out SceneObjectCandidateView page, out string reason)
    {
        page = null; var view = GetCurrentSceneVisibleCatalog(current);
        if (view != null)
            for (int index = 0; index < view.catalog.PageCount; index++)
                if (referenceId == view.catalog.CatalogId + ":p" + index)
                {
                    if (!view.catalog.TryBuildPage(index, out page, out reason)) return false;
                    view.activePage = index; return true;
                }
        reason = "Page reference was not offered by the exact current capture catalog."; return false;
    }

    private bool TryGetSceneVisibleCatalogCandidate(SceneVisionObservation current, string referenceId,
        out SceneObservedObject observed, out string reason)
    {
        observed = null; var view = GetCurrentSceneVisibleCatalog(current);
        string prefix = view == null ? null : view.catalog.CatalogId + ":p" + view.activePage + ":";
        if (prefix == null || referenceId == null || !referenceId.StartsWith(prefix, StringComparison.Ordinal))
        { reason = "Candidate was not offered on the current capture page."; return false; }
        return view.catalog.TryGetCandidate(referenceId, out observed, out reason);
    }

    private bool SceneVisibleMemoryPositionValid(SceneVisibleMemory memory)
    {
        var validity=m_SceneVisiblePointValidity.GetValue(memory.entry,_=>new SceneVisiblePointValidity());
        if (memory.positionInvalidated) validity.invalidated=true;
        if (validity.invalidated) { memory.positionInvalidated=true; return false; }
        // Live geometry may revoke a historical point, never silently update it.
        if (!memory.entry.observed.entry.TryValidateObserved(true, out _)) memory.positionInvalidated = validity.invalidated = true;
        return !memory.positionInvalidated;
    }

    private JObject SceneVisiblePointBearing(Vector3 point, SceneVisionObservation current, SceneVisionObservation historical) =>
        BuildSceneRememberedPointBearing(point, current, historical.CaptureId, historical);

    private static JObject SceneVisibleGeometryNeighborJson(SceneVisibleObjectCatalog.GeometryNeighbor value) =>
        value == null ? null : new JObject {
            ["source"] = value.source, ["sourceCaptureId"] = value.sourceCaptureId, ["entityId"] = value.entityId,
            ["observedPointOffset"] = SceneSpatialVector(value.observedPointOffset),
            ["observedPointDistanceMeters"] = value.observedPointDistanceMeters,
            ["verticalBoundsGapMeters"] = value.verticalBoundsGapMeters, ["scope"] = value.scope };

    private JObject BuildSceneVisibleCatalogContext(SceneVisionObservation current)
    {
        var view = GetCurrentSceneVisibleCatalog(current);
        JObject currentFacts = null; var memoryFacts = new JArray(); var history = new JArray();
        if (view != null)
        {
            var pages = new JArray();
            for (int i = 0; i < view.catalog.PageCount; i++) pages.Add(new JObject {
                ["referenceId"] = view.catalog.CatalogId + ":p" + i, ["pageIndex"] = i });
            if (view.catalog.TryBuildPage(view.activePage, out var page, out _))
            {
                currentFacts = JObject.Parse(page.MetadataJson);
                foreach (var candidate in (JArray)currentFacts["candidates"])
                {
                    candidate["referenceId"] = candidate["selectionId"]?.DeepClone();
                    candidate["semanticStatus"] = "unconfirmed";
                    foreach (var entry in view.catalog.Candidates)
                        if (entry.entityId == (string)candidate["entityId"])
                        {
                            candidate["geometryNeighbor"] = SceneVisibleGeometryNeighborJson(entry.geometryNeighbor);
                            candidate["directionFromCurrentPose"] = SceneVisiblePointBearing(entry.pointAtCapture, current, current);
                            break;
                        }
                    var described = m_SceneVisibleMemory.Find(item => item.entry.referenceId == (string)candidate["referenceId"]);
                    if (described != null && !string.IsNullOrEmpty(described.description))
                    {
                        candidate["modelDescription"] = described.description;
                        candidate["descriptionCaptureId"] = described.descriptionCaptureId;
                        candidate["semanticStatus"] = "model-described-target-unconfirmed";
                    }
                    if (described != null && !string.IsNullOrEmpty(described.matchedOriginalTarget))
                    {
                        candidate["matchedOriginalTarget"] = described.matchedOriginalTarget;
                        candidate["targetMatchCaptureId"] = described.matchCaptureId;
                        candidate["semanticStatus"] = described.matchCaptureId == current.CaptureId ?
                            "target-match-confirmed-in-this-capture" : "target-match-confirmed-in-earlier-capture";
                    }
                }
            }
            else currentFacts = new JObject { ["captureId"] = current.CaptureId, ["catalogId"] = view.catalog.CatalogId,
                ["candidates"] = new JArray(), ["pageIndex"] = 0, ["pageCount"] = view.catalog.PageCount,
                ["totalCandidates"] = view.catalog.TotalCandidates, ["coverageComplete"] = false };
            currentFacts["availablePages"] = pages;
            currentFacts["truncated"] = view.catalog.Truncated;
            currentFacts["imageRole"] = "current-candidate-page";
            memoryFacts = BuildSceneSearchMemoryFacts(current);
            foreach (var old in GetSceneVisibleHistoricalViews(current))
                history.Add(new JObject { ["referenceId"] = old.CaptureId, ["sourceCaptureId"] = old.CaptureId,
                    ["imageRole"] = "historical-view-search-hint", ["imageAttachedToCurrentLocalization"] = false,
                    ["cameraPositionAtCapture"] = SceneSpatialVector(old.CameraPosition),
                    ["returnDirection"] = SceneHistoricalViewBearing(old, current),
                    ["semanticIdentityConfirmed"] = false, ["actionAuthorization"] = false,
                    ["scope"] = "Actual separately retrievable historical RGB; camera direction only, not target position. Recall must not place old pixels in the current image." });
        }
        return new JObject { ["current"] = currentFacts, ["memory"] = memoryFacts, ["historicalViews"] = history,
            ["scope"] = "Current numbered page and historical search hints have separate capture namespaces. Inspect additional pages before inferring no offered match. Only current actual pixels can locate the current requested target; historical descriptions/positions never grant operation authority." };
    }

    private JArray BuildSceneSearchMemoryFacts(SceneVisionObservation current)
    {
        var memoryFacts = new JArray();
        var entities = new HashSet<string>(StringComparer.Ordinal);
        var frames = new HashSet<string>(StringComparer.Ordinal);
        bool Reserve(string entity, string capture)
        {
            if (memoryFacts.Count >= SceneVisibleMemoryLimit || entities.Contains(entity) ||
                (!frames.Contains(capture) && frames.Count >= SceneVisibleViewLimit)) return false;
            entities.Add(entity); frames.Add(capture); return true;
        }
        // A new user turn invalidates pixels and active callbacks, not the already
        // observed object's frozen point. Reuse that existing memory only as a
        // direction hint; never resurrect its image binding or device permission.
        var knownPoints = new List<KnownSceneObject>();
        foreach (var item in m_KnownSceneObjects)
            if (KnownSceneObjectBinding(item.Key, out var known) && known.observed?.VisibleConfirmed == true &&
                known.observed.source == known.source && known.observed.Root == known.root &&
                known.observed.EntityId == item.Key && known.observed.CaptureId != current.CaptureId &&
                SceneSearchMemoryAgeValid(known.observed.observation.CapturedAtRealtime, Time.realtimeSinceStartup) &&
                KnownSceneSpatialPositionValid(known, out _) &&
                !m_SceneVisibleMemory.Exists(sample => sample.entry.entityId == item.Key && SceneVisibleMemoryPositionValid(sample)))
                knownPoints.Add(known);
        knownPoints.Sort((a, b) => {
            int order = b.observed.observation.CapturedAtRealtime.CompareTo(a.observed.observation.CapturedAtRealtime);
            return order != 0 ? order : string.CompareOrdinal(a.observed.EntityId, b.observed.EntityId);
        });
        // Preserve the catalog's existing semantic/recency priority before filling
        // remaining capacity with cross-turn points from the known-object memory.
        foreach (var sample in m_SceneVisibleMemory)
        {
            if (sample.view.observation.CaptureId == current.CaptureId) continue;
            if (knownPoints.Exists(known => known.observed.EntityId == sample.entry.entityId)) continue;
            if (!Reserve(sample.entry.entityId, sample.entry.sourceCaptureId)) continue;
            bool valid = SceneVisibleMemoryPositionValid(sample);
            memoryFacts.Add(new JObject { ["referenceId"] = sample.entry.sourceCaptureId + ":memory:" + sample.entry.entityId,
                ["sourceCaptureId"] = sample.entry.sourceCaptureId, ["entityId"] = sample.entry.entityId,
                ["capturedAtUtc"] = sample.view.observation.CapturedAtUtc,
                ["authoredHint"] = sample.entry.authoredHint == null ? null : JObject.FromObject(sample.entry.authoredHint),
                ["observedRendererBoundsCenter"] = SceneSpatialVector(sample.entry.observedRendererBoundsCenter),
                ["observedRendererBoundsSize"] = SceneSpatialVector(sample.entry.observedRendererBoundsSize),
                ["geometryNeighbor"] = SceneVisibleGeometryNeighborJson(sample.entry.geometryNeighbor),
                ["pointAtCapture"] = SceneSpatialVector(sample.entry.pointAtCapture),
                ["imagePointAtCapture"] = new JObject { ["x"] = sample.entry.imagePoint.x, ["y"] = sample.entry.imagePoint.y },
                ["pixelBounds"] = JObject.FromObject(sample.entry.pixelBounds), ["visiblePixelCount"] = sample.entry.visiblePixelCount,
                ["modelDescription"] = sample.description, ["descriptionCaptureId"] = sample.descriptionCaptureId,
                ["semanticStatus"] = !string.IsNullOrEmpty(sample.matchedOriginalTarget) ? "target-match-confirmed-in-earlier-capture" :
                    !string.IsNullOrEmpty(sample.description) ? "model-described-target-unconfirmed" : "unconfirmed",
                ["matchedOriginalTarget"] = sample.matchedOriginalTarget, ["targetMatchCaptureId"] = sample.matchCaptureId,
                ["positionMemoryValid"] = valid, ["relativeToCurrentView"] = valid ? SceneVisiblePointBearing(sample.entry.pointAtCapture, current, sample.view.observation) : null,
                ["ageSeconds"] = Time.realtimeSinceStartup - sample.view.observation.CapturedAtRealtime,
                ["observationCount"] = sample.observationCount,
                ["historical"] = true, ["currentVisibility"] = "not-established", ["actionAuthorization"] = false,
                ["scope"] = "Actual earlier opaque pixel/depth evidence. Description is model-authored; only an independent target match can confirm a requested identity. Reobserve before approaching or operating. A moved point is invalidated, never refreshed from hidden transforms." });
        }
        foreach (var known in knownPoints)
        {
            var observed = known.observed;
            if (!Reserve(observed.EntityId, observed.CaptureId)) continue;
            memoryFacts.Add(new JObject {
                ["referenceId"] = "known-object-point:" + observed.CaptureId + ":" + observed.EntityId,
                ["sourceKind"] = "previously-observed-object-point", ["sourceCaptureId"] = observed.CaptureId,
                ["entityId"] = observed.EntityId, ["sceneLabel"] = observed.SceneLabel,
                ["identitySource"] = observed.IdentitySource,
                ["authoredHint"] = JObject.FromObject(SceneObjectCandidateView.CreateAuthoredHint(observed.CaptureId,
                    "", "", observed.SceneLabel, observed.IdentitySource)),
                ["capturedAtUtc"] = observed.observation.CapturedAtUtc,
                ["pointAtCapture"] = SceneSpatialVector(observed.WorldPoint),
                ["imagePointAtCapture"] = new JObject { ["x"] = observed.ImagePoint.x, ["y"] = observed.ImagePoint.y },
                ["semanticStatus"] = "previously-observed-entity-target-unconfirmed",
                ["positionMemoryValid"] = true, ["relativeToCurrentView"] = SceneVisiblePointBearing(observed.WorldPoint, current, observed.observation),
                ["ageSeconds"] = Time.realtimeSinceStartup - observed.observation.CapturedAtRealtime,
                ["historical"] = true, ["currentVisibility"] = "not-established", ["requiresReobservation"] = true,
                ["semanticIdentityConfirmed"] = false, ["actionAuthorization"] = false,
                ["scope"] = "Existing frozen opaque-pixel object memory, retained across user-turn visual resets. Point is a search direction only; sceneLabel is an annotation, not target recognition. No historical image is attached or made current. Fresh current RGB and independent same-entity target matching are required before any operation; no device state or permission is supplied." });
        }
        return memoryFacts;
    }

    // Only the task's already bound identity may call this deterministic recall.
    // It reuses the existing frozen observations; it never searches hidden scene
    // transforms or supplies a target point for navigation/device execution.
    private bool TryGetSceneSearchBoundPoint(SceneVisionObservation current, string entityId,
        out Vector3 worldPoint, out string referenceId)
    {
        worldPoint = default; referenceId = null;
        if (GetCurrentSceneVisibleCatalog(current) == null || string.IsNullOrEmpty(entityId)) return false;
        if (KnownSceneObjectBinding(entityId, out var known) && known.observed?.VisibleConfirmed == true &&
            known.observed.source == known.source && known.observed.Root == known.root &&
            known.observed.EntityId == entityId && known.observed.CaptureId != current.CaptureId &&
            SceneSearchMemoryAgeValid(known.observed.observation.CapturedAtRealtime, Time.realtimeSinceStartup) &&
            KnownSceneSpatialPositionValid(known, out _))
        {
            worldPoint = known.observed.WorldPoint;
            referenceId = "known-object-point:" + known.observed.CaptureId + ":" + entityId;
            return true;
        }
        foreach (var sample in m_SceneVisibleMemory)
            if (sample.entry.entityId == entityId && sample.entry.sourceCaptureId != current.CaptureId &&
                SceneVisibleCatalogHistoryBinding(sample.view) && SceneVisibleMemoryPositionValid(sample))
            {
                worldPoint = sample.entry.pointAtCapture;
                referenceId = sample.entry.sourceCaptureId + ":memory:" + entityId;
                return true;
            }
        return false;
    }

    private IReadOnlyList<SceneVisionObservation> GetSceneVisibleHistoricalViews(SceneVisionObservation current)
    {
        var result = new List<SceneVisionObservation>();
        // The existing merger reserves the actual earlier dialogue view before
        // supplementary catalog views. Repeated sweep frames cannot evict it here.
        foreach (var image in GetScenePlanningHistoryImages(current))
            if (image.CaptureId != current.CaptureId && !result.Exists(item => item.CaptureId == image.CaptureId)) result.Add(image);
        for (int i = m_SceneVisibleCatalogViews.Count - 1; i >= 0 && result.Count < SceneVisibleViewLimit; i--)
        {
            var view = m_SceneVisibleCatalogViews[i];
            if (SceneVisibleCatalogHistoryBinding(view) && view.observation.CaptureId != current.CaptureId &&
                !result.Exists(item => item.CaptureId == view.observation.CaptureId || SceneDialogueSameView(item, view.observation))) result.Add(view.observation);
        }
        foreach (var sample in m_SceneVisibleMemory)
            if (result.Count < SceneVisibleViewLimit && SceneVisibleCatalogHistoryBinding(sample.view) &&
                sample.view.observation.CaptureId != current.CaptureId &&
                !result.Exists(item => item.CaptureId == sample.view.observation.CaptureId)) result.Add(sample.view.observation);
        return result;
    }

    private bool TryGetSceneSearchHistoricalView(string referenceId, SceneVisionObservation current,
        out SceneVisionObservation observation, out string reason)
    {
        observation = null;
        if (GetCurrentSceneVisibleCatalog(current) != null)
            foreach (var image in GetSceneVisibleHistoricalViews(current))
                if (referenceId == image.CaptureId) { observation = image; reason = ""; return true; }
        reason = "Historical capture was not offered in this room/avatar/visual binding."; return false;
    }

    private SceneVisibleCatalogView FindRetainedSceneVisibleCatalog(SceneVisionObservation historical)
    {
        if (historical == null) return null;
        foreach (var view in m_SceneVisibleCatalogViews)
            if (ReferenceEquals(view.observation, historical) && SceneVisibleCatalogHistoryBinding(view)) return view;
        foreach (var sample in m_SceneVisibleMemory)
            if (ReferenceEquals(sample.view.observation, historical) && SceneVisibleCatalogHistoryBinding(sample.view)) return sample.view;
        bool retainedDialogue = m_SceneDialogueViews.Exists(sample => ReferenceEquals(sample.observation, historical) && SceneDialogueViewCurrent(sample));
        if (retainedDialogue && m_SceneVisibleCaptureOwners.TryGetValue(historical, out var cached) &&
            ReferenceEquals(historical.VisibleCatalogSnapshot, cached.catalog) && SceneVisibleCatalogHistoryBinding(cached)) return cached;
        return null;
    }

    private bool TryGetSceneSearchHistoricalPage(SceneVisionObservation historical, SceneVisionObservation current, int pageIndex,
        out string image, out JArray candidates, out string reason)
    {
        image = null; candidates = new JArray();
        var view = FindRetainedSceneVisibleCatalog(historical);
        if (view == null) { reason = "Historical catalog is not retained in the current room/avatar/session."; return false; }
        if (GetCurrentSceneVisibleCatalog(current) == null)
        { reason = "Historical recall requires a separate fresh current view for return bearings."; return false; }
        int page = pageIndex < 0 ? view.activePage : pageIndex;
        if (!view.catalog.TryBuildHistoricalPage(page, out var preview, out reason)) return false;
        // One serialized recall request owns at most six point offers from one
        // source image. Old callbacks cannot address another request's page.
        m_SceneHistoricalPointOffers.Clear();
        foreach (var entry in view.catalog.Candidates)
        {
            if (entry.pageIndex != page) continue;
            string reference = "historical-point:" + entry.referenceId;
            var memory = m_SceneVisibleMemory.Find(item => item.entry.referenceId == entry.referenceId) ??
                new SceneVisibleMemory { view = view, entry = entry };
            bool valid = SceneVisibleMemoryPositionValid(memory);
            m_SceneHistoricalPointOffers[reference] = memory;
            candidates.Add(new JObject {
                ["referenceId"] = reference, ["candidateId"] = entry.candidateId, ["entityId"] = entry.entityId,
                ["sourceCaptureId"] = historical.CaptureId, ["capturedAtUtc"] = historical.CapturedAtUtc,
                ["authoredHint"] = entry.authoredHint == null ? null : JObject.FromObject(entry.authoredHint),
                ["worldPoint"] = SceneSpatialVector(entry.pointAtCapture),
                ["positionMemoryValid"] = valid, ["selectionAllowed"] = valid,
                ["directionFromCurrentPose"] = valid ? SceneVisiblePointBearing(entry.pointAtCapture, current, historical) : null,
                ["geometryNeighbor"] = SceneVisibleGeometryNeighborJson(entry.geometryNeighbor),
                ["modelDescription"] = memory.description, ["descriptionCaptureId"] = memory.descriptionCaptureId,
                ["matchedOriginalTarget"] = memory.matchedOriginalTarget, ["targetMatchCaptureId"] = memory.matchCaptureId,
                ["semanticIdentityConfirmed"] = false, ["actionAuthorization"] = false,
                ["scope"] = "Number and pixels belong to this historical capture only. A valid frozen point permits looking back, never current object selection, navigation or a device operation. Reobserve and independently match the full target in current pixels." });
        }
        image = preview.ImageDataUrl; SetSceneSearchCandidatePreview(preview, "historical-recall");
        reason = null; return true;
    }

    private bool TryResolveSceneSearchHistoricalPoint(SceneVisionObservation historical, string referenceId,
        out Vector3 point, out string reason)
    {
        point = default;
        if (referenceId == null || !m_SceneHistoricalPointOffers.TryGetValue(referenceId, out var memory) ||
            !ReferenceEquals(memory.view.observation, historical) || FindRetainedSceneVisibleCatalog(historical) == null ||
            !SceneVisibleCatalogHistoryBinding(memory.view) || !SceneVisibleMemoryPositionValid(memory))
        { reason = "Historical point was not offered from this exact retained image or its frozen geometry is no longer valid."; return false; }
        point = memory.entry.pointAtCapture; reason = null; return true;
    }

    private void RememberSceneHistoricalDescription(SceneVisionObservation historical, string referenceId, string description)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > 280 || referenceId == null ||
            !m_SceneHistoricalPointOffers.TryGetValue(referenceId, out var offered) ||
            !ReferenceEquals(offered.view.observation, historical) || FindRetainedSceneVisibleCatalog(historical) == null) return;
        // A description of old pixels stays attached to those pixels. It must not
        // overwrite a newer observation of the same entity or become recognition.
        var memory = m_SceneVisibleMemory.Find(item => item.entry.entityId == offered.entry.entityId);
        if (memory != null && memory.entry.sourceCaptureId != historical.CaptureId) return;
        if (memory == null) { memory = offered; m_SceneVisibleMemory.Add(memory); }
        memory.description = description; memory.descriptionCaptureId = historical.CaptureId;
        TrimSceneVisibleMemory();
    }

    private bool TryResolveSceneSearchSelection(string kind, string referenceId, SceneVisionObservation current,
        out Vector3 worldPoint, out string reason)
    {
        worldPoint = default; var view = GetCurrentSceneVisibleCatalog(current);
        if (view == null) { reason = "Current scene-search observation is unavailable or stale."; return false; }
        if (kind == "current-candidate")
        {
            if (!TryGetSceneVisibleCatalogCandidate(current, referenceId, out var observed, out reason)) return false;
            worldPoint = observed.WorldPoint; return true;
        }
        if (kind == "historical-object")
        {
            foreach (var sample in BuildSceneSearchMemoryFacts(current))
                if (referenceId == (string)sample["referenceId"] && (bool?)sample["positionMemoryValid"] == true)
                {
                    var point = (JObject)sample["pointAtCapture"];
                    worldPoint = new Vector3((float)point["x"], (float)point["y"], (float)point["z"]);
                    reason = ""; return true;
                }
        }
        else if (kind == "historical-view" && TryGetSceneSearchHistoricalView(referenceId, current, out var image, out reason))
        {
            // A point only encodes the measured historical camera DIRECTION. It is
            // deliberately not an object position or a navigation destination.
            worldPoint = current.CameraPosition + image.CameraRotation * Vector3.forward * 3f; return true;
        }
        reason = "Search reference is not an offered valid historical hint or current candidate."; return false;
    }

    private void RememberSceneVisibleDescription(SceneVisionObservation current, string referenceId, string description)
    {
        if (string.IsNullOrWhiteSpace(description) || description.Length > 280 ||
            !TryResolveSceneSearchSelection("current-candidate", referenceId, current, out _, out _)) return;
        var sample = EnsureSceneVisibleMemoryEntry(referenceId);
        if (sample == null) return;
        sample.description = description; sample.descriptionCaptureId = current.CaptureId;
        TrimSceneVisibleMemory();
    }

    private SceneVisibleMemory EnsureSceneVisibleMemoryEntry(string referenceId)
    {
        var existing = m_SceneVisibleMemory.Find(sample => sample.entry.referenceId == referenceId);
        if (existing != null) return existing;
        if (m_SceneVisibleCurrent == null) return null;
        foreach (var entry in m_SceneVisibleCurrent.catalog.Candidates)
            if (entry.referenceId == referenceId && entry.observed.Root != null &&
                (entry.observed.Root == m_SceneVisibleCurrent.environment || entry.observed.Root.IsChildOf(m_SceneVisibleCurrent.environment)))
            {
                var sample = new SceneVisibleMemory { entry = entry, view = m_SceneVisibleCurrent };
                m_SceneVisibleMemory.Add(sample); return sample;
            }
        return null;
    }

    // Call only after the independent original-target matcher has accepted these
    // exact current pixels; a scene label or an ordinary model description is insufficient.
    private void RememberSceneVisibleSemanticMatch(SceneObservedObject observed, string originalTarget, string matchCaptureId)
    {
        if (observed?.VisibleConfirmed != true || string.IsNullOrWhiteSpace(originalTarget) ||
            originalTarget.Length > 1200 || matchCaptureId != observed.CaptureId || observed.source != m_SceneVisionCapture ||
            !observed.TryValidate(out _) || !observed.GeometryIsCurrent) return;
        ObserveSceneVisibleCatalog(observed.observation, "semantic-target-match");
        if (m_SceneVisibleCurrent?.observation.CaptureId != observed.CaptureId) return;
        foreach (var entry in m_SceneVisibleCurrent.catalog.Candidates)
            if (entry.entityId == observed.EntityId && entry.sourceCaptureId == observed.CaptureId)
            {
                var sample = EnsureSceneVisibleMemoryEntry(entry.referenceId);
                if (sample == null) return;
                sample.matchedOriginalTarget = originalTarget; sample.matchCaptureId = matchCaptureId;
                TrimSceneVisibleMemory(); return;
            }
    }

    // Diagnostic metadata only: no catalog construction, image rendering, capture or
    // model request. The editor polls this at most once per second.
    public string GetSceneVisibleCatalogDiagnosticsJson()
    {
        var view = m_SceneVisibleCurrent;
        bool current = view != null && SceneVisibleCatalogBinding(view) && IsFreshSceneObservation(view.observation) &&
            view.source.IsCurrentViewCompatible(view.observation, .35f, 25f);
        var currentEntries = new JArray();
        if (current)
            foreach (var entry in view.catalog.Candidates)
            {
                var memory = m_SceneVisibleMemory.Find(item => item.entry.entityId == entry.entityId);
                currentEntries.Add(new JObject {
                    ["entityId"] = entry.entityId, ["sourceCaptureId"] = entry.sourceCaptureId,
                    ["capturedAtUtc"] = view.observation.CapturedAtUtc, ["pageIndex"] = entry.pageIndex,
                    ["authoredHint"] = entry.authoredHint == null ? null : JObject.FromObject(entry.authoredHint),
                    ["pointAtCapture"] = SceneSpatialVector(entry.pointAtCapture),
                    ["visibleInCurrentCapture"] = true, ["positionMemoryValid"] = entry.observed.GeometryIsCurrent,
                    ["modelDescription"] = memory?.description, ["descriptionCaptureId"] = memory?.descriptionCaptureId,
                    ["matchedOriginalTarget"] = memory?.matchedOriginalTarget, ["targetMatchCaptureId"] = memory?.matchCaptureId,
                    ["semanticStatus"] = !string.IsNullOrEmpty(memory?.matchedOriginalTarget) ? "target-match-recorded" :
                        !string.IsNullOrEmpty(memory?.description) ? "model-described-target-unconfirmed" : "observed-geometry-only",
                    ["directionFromCurrentPose"] = SceneVisiblePointBearing(entry.pointAtCapture, view.observation, view.observation),
                    ["geometryNeighbor"] = SceneVisibleGeometryNeighborJson(entry.geometryNeighbor) });
            }
        var entries = new JArray();
        foreach (var memory in m_SceneVisibleMemory)
        {
            if (!SceneVisibleCatalogHistoryBinding(memory.view)) continue;
            var entry = memory.entry;
            bool valid = SceneVisibleMemoryPositionValid(memory);
            bool inCurrentCapture = current && entry.sourceCaptureId == view.observation.CaptureId;
            entries.Add(new JObject {
                ["entityId"] = entry.entityId, ["sourceCaptureId"] = entry.sourceCaptureId,
                ["capturedAtUtc"] = memory.view.observation.CapturedAtUtc,
                ["ageSeconds"] = Mathf.Max(0, Time.realtimeSinceStartup - memory.view.observation.CapturedAtRealtime),
                ["authoredHint"] = entry.authoredHint == null ? null : JObject.FromObject(entry.authoredHint),
                ["pointAtCapture"] = SceneSpatialVector(entry.pointAtCapture),
                ["visibleInCurrentCapture"] = inCurrentCapture && valid,
                ["positionMemoryValid"] = valid,
                ["modelDescription"] = memory.description, ["descriptionCaptureId"] = memory.descriptionCaptureId,
                ["matchedOriginalTarget"] = memory.matchedOriginalTarget, ["targetMatchCaptureId"] = memory.matchCaptureId,
                ["semanticStatus"] = !string.IsNullOrEmpty(memory.matchedOriginalTarget) ? "target-match-confirmed-in-earlier-capture" :
                    !string.IsNullOrEmpty(memory.description) ? "model-described-target-unconfirmed" : "observed-geometry-only",
                ["directionFromCurrentPose"] = current && valid ?
                    SceneVisiblePointBearing(entry.pointAtCapture, view.observation, memory.view.observation) : null,
                ["geometryNeighbor"] = SceneVisibleGeometryNeighborJson(entry.geometryNeighbor) });
        }
        return new JObject {
            ["currentViewUsable"] = current, ["currentCaptureId"] = current ? view.observation.CaptureId : null,
            ["currentCapturedAtUtc"] = current ? view.observation.CapturedAtUtc : null,
            ["currentCatalogCandidates"] = current ? view.catalog.Candidates.Count : 0,
            ["currentObjects"] = currentEntries, ["retainedObjects"] = entries,
            ["scope"] = "Cached actual observations; no editor-driven cognition. Authored names, model descriptions and target matches are separate evidence. Frozen coordinates never become live hidden positions or operation permission." }.ToString(Newtonsoft.Json.Formatting.None);
    }
}
