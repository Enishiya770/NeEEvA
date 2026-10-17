using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeEEvA.Perception
{
    /// <summary>Capture-local geometry discovered from rendered pixels, never a scene inventory.
    /// Semantic recognition and action authorization are deliberately separate.</summary>
    public sealed class SceneVisibleObjectCatalog
    {
        public const int MaximumCandidates = 96;
        [Serializable] public sealed class Entry
        {
            public string entityId, sourceCaptureId, referenceId, candidateId;
            public int pageIndex, visiblePixelCount, imageRegion;
            public Vector2 imagePoint;
            public Vector3 pointAtCapture;
            public Vector3 observedRendererBoundsCenter, observedRendererBoundsSize;
            public SceneObjectCandidateView.AuthoredHint authoredHint;
            public GeometryNeighbor geometryNeighbor;
            public SceneObjectCandidateView.PixelBox pixelBounds;
            public bool opaquePixelsConfirmed = true, semanticIdentityConfirmed, actionAuthorization;
            public string scope = "Stable opaque ID and depth in this exact RGB capture; world point is measured at capture time. Identity is an engine geometric handle, not semantic recognition, current visibility, reachability or permission.";
            [NonSerialized] internal SceneObservedObject observed;
        }
        [Serializable] public sealed class GeometryNeighbor
        {
            public string source = "same-capture-measured-geometry", sourceCaptureId, entityId;
            public Vector3 observedPointOffset;
            public float observedPointDistanceMeters, verticalBoundsGapMeters;
            public string scope = "Nearest retained observed surface point in this capture, not nearest room object. Offset is neighbor minus this point; vertical gap is this renderer AABB minimum Y minus neighbor maximum Y. Renderer bounds can include occluded parts. These measurements do not establish on/top-of, contact, support or a semantic relation.";
        }
        [Serializable] private sealed class Metadata
        {
            public string catalogId, captureId, capturedAtUtc;
            public int totalCandidates, retainedCandidates, pageCount;
            public bool truncated;
            public Entry[] candidates;
            public string scope = "Full source-frame pixel scan, with candidates paged across six image regions. Only stable opaque interiors are included. Omitted/unsupported/tiny/transparent pixels and truncated entries do not establish absence. Authored hints are explicitly separate from semantic recognition; geometry neighbors assert measurements only, never contact or support.";
        }
        private readonly NevaSceneVisionCapture source;
        private readonly SceneVisionObservation observation;
        private readonly List<SceneObjectCandidateView.Mask> ordered;
        private readonly SceneObjectCandidateView.Mask[] entryMasks;
        private readonly Entry[] entries;
        private readonly Dictionary<int, SceneObjectCandidateView> pages = new Dictionary<int, SceneObjectCandidateView>();
        private readonly Dictionary<int, SceneObjectCandidateView> historicalPages = new Dictionary<int, SceneObjectCandidateView>();
        public string CatalogId => observation.CaptureId + ":visible";
        public string CaptureId => observation.CaptureId;
        public int TotalCandidates { get; }
        public int PageCount => (entries.Length + SceneObjectCandidateView.MaximumCandidates - 1) / SceneObjectCandidateView.MaximumCandidates;
        public bool Truncated => entries.Length < TotalCandidates;
        public IReadOnlyList<Entry> Candidates => entries;
        public string MetadataJson { get; }
        internal SceneVisionObservation Observation => observation;

        private SceneVisibleObjectCatalog(NevaSceneVisionCapture source, SceneVisionObservation observation,
            List<SceneObjectCandidateView.Mask> ordered, SceneObjectCandidateView.Mask[] entryMasks, int total)
        {
            this.source = source; this.observation = observation; this.ordered = ordered; this.entryMasks = entryMasks;
            TotalCandidates = total; entries = new Entry[ordered.Count];
            for (int i = 0; i < entries.Length; i++)
            {
                var mask = ordered[i]; int page = i / SceneObjectCandidateView.MaximumCandidates;
                string local = "c" + (i % SceneObjectCandidateView.MaximumCandidates + 1);
                entries[i] = new Entry {
                    entityId = mask.entity, sourceCaptureId = observation.CaptureId, candidateId = local,
                    referenceId = CatalogId + ":p" + page + ":" + local, pageIndex = page,
                    visiblePixelCount = mask.count, imageRegion = ImageRegion(mask.sample), imagePoint = mask.sample,
                    pointAtCapture = mask.observed.WorldPoint, observed = mask.observed,
                    authoredHint = mask.authoredHint,
                    observedRendererBoundsCenter = mask.observed.entry.bounds.center,
                    observedRendererBoundsSize = mask.observed.entry.bounds.size,
                    pixelBounds = new SceneObjectCandidateView.PixelBox(mask.minX, mask.minY, mask.maxX - mask.minX + 1, mask.maxY - mask.minY + 1)
                };
            }
            PopulateGeometryNeighbors(entries);
            MetadataJson = JsonUtility.ToJson(new Metadata { catalogId = CatalogId, captureId = CaptureId,
                capturedAtUtc = observation.CapturedAtUtc, totalCandidates = total, retainedCandidates = entries.Length,
                pageCount = PageCount, truncated = Truncated, candidates = entries });
        }

        internal static void PopulateGeometryNeighbors(Entry[] entries)
        {
            // At most 96 squared comparisons once per capture, never on UI redraw.
            int count = Math.Min(entries.Length, MaximumCandidates);
            for (int i = 0; i < count; i++)
            {
                var entry = entries[i]; entry.geometryNeighbor = null;
                Entry nearest = null; float distance = float.PositiveInfinity;
                for (int j = 0; j < count; j++)
                {
                    var other = entries[j];
                    if (i == j || other.entityId == entry.entityId || other.sourceCaptureId != entry.sourceCaptureId) continue;
                    float candidate = (other.pointAtCapture - entry.pointAtCapture).sqrMagnitude;
                    if (float.IsNaN(candidate) || float.IsInfinity(candidate)) continue;
                    if (candidate < distance || candidate == distance && nearest != null &&
                        string.CompareOrdinal(other.entityId, nearest.entityId) < 0)
                    { nearest = other; distance = candidate; }
                }
                if (nearest != null) entry.geometryNeighbor = new GeometryNeighbor {
                    sourceCaptureId = entry.sourceCaptureId, entityId = nearest.entityId,
                    observedPointOffset = nearest.pointAtCapture - entry.pointAtCapture,
                    observedPointDistanceMeters = Mathf.Sqrt(distance),
                    verticalBoundsGapMeters = entry.observedRendererBoundsCenter.y - entry.observedRendererBoundsSize.y * .5f -
                        (nearest.observedRendererBoundsCenter.y + nearest.observedRendererBoundsSize.y * .5f) };
            }
        }

        internal static bool TryBuild(NevaSceneVisionCapture source, SceneVisionObservation observation,
            out SceneVisibleObjectCatalog catalog, out string reason)
        {
            catalog = null;
            if (!SceneObjectCandidateView.TryCollect(source, observation, new Vector2(.5f, .5f),
                out var masks, out var entryMasks, out reason)) return false;
            int total = masks.Count;
            catalog = new SceneVisibleObjectCatalog(source, observation, DiverseOrder(masks, MaximumCandidates), entryMasks, total);
            return true; // An empty observed frame is representable; it is not proof of absence.
        }

        internal static int ImageRegion(Vector2 point) => Mathf.Clamp((int)(point.y * 2), 0, 1) * 3 + Mathf.Clamp((int)(point.x * 3), 0, 2);

        internal static List<SceneObjectCandidateView.Mask> DiverseOrder(List<SceneObjectCandidateView.Mask> input, int limit)
        {
            var regions = new List<SceneObjectCandidateView.Mask>[6];
            for (int i = 0; i < regions.Length; i++) regions[i] = new List<SceneObjectCandidateView.Mask>();
            foreach (var mask in input) regions[ImageRegion(mask.sample)].Add(mask);
            // Small visible objects must not lose every slot to floor/furniture nearest
            // an arbitrary pixel. Every retained object is eventually offered on a page.
            foreach (var region in regions) region.Sort((a, b) => {
                int size = a.count.CompareTo(b.count);
                return size != 0 ? size : string.CompareOrdinal(a.entity, b.entity);
            });
            var result = new List<SceneObjectCandidateView.Mask>();
            for (int round = 0; result.Count < limit; round++)
            {
                bool added = false;
                foreach (var region in regions)
                    if (round < region.Count && result.Count < limit) { result.Add(region[round]); added = true; }
                if (!added) break;
            }
            return result;
        }

        public bool TryBuildPage(int pageIndex, out SceneObjectCandidateView view, out string reason)
        {
            view = null;
            if (source == null || !source.IsCurrentViewCompatible(observation, .35f, 25f) || !SceneVisionGrounding.Fresh(observation))
            { reason = "Catalog page needs its exact fresh current source view; old catalogs are search memory only."; return false; }
            if (pageIndex < 0 || pageIndex >= PageCount)
            { reason = "Candidate page is outside this capture's offered page range."; return false; }
            if (pages.TryGetValue(pageIndex, out view)) { reason = ""; return true; }
            int start = pageIndex * SceneObjectCandidateView.MaximumCandidates;
            var chosen = ordered.GetRange(start, Math.Min(SceneObjectCandidateView.MaximumCandidates, ordered.Count - start));
            Vector2 center = Vector2.zero;
            foreach (var item in chosen) center += item.sample;
            center /= chosen.Count;
            if (!SceneObjectCandidateView.TryRender(source, observation, center, chosen, entryMasks, CatalogId,
                pageIndex, PageCount, TotalCandidates, !Truncated && PageCount == 1, out view, out reason)) return false;
            pages.Add(pageIndex, view); return true;
        }

        public bool TryGetCandidate(string referenceId, out SceneObservedObject value, out string reason)
        {
            value = null;
            if (source == null || !source.IsCurrentViewCompatible(observation, .35f, 25f) || !SceneVisionGrounding.Fresh(observation))
            { reason = "Candidate reference is not in a fresh current compatible capture."; return false; }
            foreach (var item in entries)
                if (string.Equals(item.referenceId, referenceId, StringComparison.Ordinal))
                {
                    if (!item.observed.TryValidate(out reason) || !item.observed.GeometryIsCurrent)
                    { reason = "Candidate capture geometry changed; a new view is required."; return false; }
                    value = item.observed; return true;
                }
            reason = "Candidate reference was not supplied by this exact capture catalog."; return false;
        }

        public bool TryBuildHistoricalPage(int pageIndex, out SceneObjectCandidateView view, out string reason)
        {
            view = null;
            float age = Time.realtimeSinceStartup - observation.CapturedAtRealtime;
            if (source == null || !source.IsObservationHistoryBindingCurrent(observation) || !(age >= 0 && age <= SceneVisionObservation.MaximumHistoryAgeSeconds))
            { reason = "Historical catalog session or age is invalid."; return false; }
            if (pageIndex < 0 || pageIndex >= PageCount)
            { reason = "Historical page is outside this retained catalog."; return false; }
            if (historicalPages.TryGetValue(pageIndex, out view)) { reason = null; return true; }
            int start = pageIndex * SceneObjectCandidateView.MaximumCandidates;
            var chosen = ordered.GetRange(start, Math.Min(SceneObjectCandidateView.MaximumCandidates, ordered.Count - start));
            Vector2 center = Vector2.zero;
            foreach (var item in chosen) center += item.sample;
            center /= chosen.Count;
            if (!SceneObjectCandidateView.TryRenderHistorical(source, observation, center, chosen, entryMasks, CatalogId,
                pageIndex, PageCount, TotalCandidates, out view, out reason)) return false;
            historicalPages.Add(pageIndex, view); return true;
        }

        public bool TryBuildHistoricalFocus(string referenceId, out SceneObjectCandidateView view, out string reason)
        {
            view = null;
            int index = Array.FindIndex(entries, e => e.referenceId == referenceId);
            if (index < 0) { reason = "Reference does not belong to retained pixels."; return false; }
            // Render the same frozen RGB with ONE mask and marker. The local
            // marker is c1; the caller separately binds it to the original ID.
            // Do not query the scene again or move a historical world point.
            var mask = ordered[index];
            return SceneObjectCandidateView.TryRenderHistorical(source, observation, mask.sample,
                new List<SceneObjectCandidateView.Mask> { mask }, entryMasks, null,
                0, 1, TotalCandidates, out view, out reason);
        }
    }
}
