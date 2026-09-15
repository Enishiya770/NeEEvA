using System;
using UnityEngine;

namespace NeEEvA.Perception
{
    /// <summary>Visible pixel evidence for one confirmed entity in one existing camera frame.
    /// No renderer bounds or unseen pixels are used to claim a complete object or readable text.</summary>
    [Serializable]
    public sealed class SceneObjectViewSample
    {
        public string captureId, entityId, capturedAtUtc;
        public int frame, width, height, visibleOpaquePixels;
        public Vector4 normalizedVisibleBounds;
        public float visiblePixelFraction, cameraDistanceToObservedPoint;
        public bool touchesImageBoundary;
        public string scope = "Measured visible opaque pixels only, top-left image origin. Bounds may be clipped by occlusion; no edge contact does not prove full visibility or readable text. Compare the new RGB against the requested purpose.";

        public static SceneObjectViewSample Measure(SceneVisionObservation observation, SceneObservedObject target)
        {
            if (observation == null || target == null || !target.VisibleConfirmed ||
                target.CaptureId != observation.CaptureId || !target.TryValidate(out _)) return null;
            var pixels = observation.Grounding;
            if (pixels?.entries == null || pixels.depthIds == null || pixels.width != observation.Width ||
                pixels.height != observation.Height || pixels.width < 3 || pixels.height < 3 ||
                pixels.width > 1280 || pixels.height > 960 || pixels.depthIds.Length != pixels.width * pixels.height * 2)
                return null;
            var eligible = new bool[pixels.entries.Length];
            for (int i = 0; i < eligible.Length; i++)
            {
                var entry = pixels.entries[i];
                eligible[i] = entry != null && entry.entityId == target.EntityId && entry.identityRoot == target.Root &&
                    entry.observedRigid && entry.TryValidateObserved(false, out _);
            }
            int count = 0, minX = pixels.width, minY = pixels.height, maxX = -1, maxY = -1;
            for (int y = 0; y < pixels.height; y++) for (int x = 0; x < pixels.width; x++)
            {
                int index = (y * pixels.width + x) * 2;
                float depth = pixels.depthIds[index];
                if (float.IsNaN(depth) || float.IsInfinity(depth) || depth <= .025f || depth > 60f ||
                    !SceneVisionGrounding.ObjectEntryId(Mathf.RoundToInt(pixels.depthIds[index + 1]), eligible.Length,
                        out int entry, out bool opaque) || !opaque || !eligible[entry]) continue;
                int topY = pixels.rowsStartAtTop ? y : pixels.height - 1 - y;
                count++; minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, topY); maxY = Math.Max(maxY, topY);
            }
            if (count == 0) return null;
            return new SceneObjectViewSample {
                captureId = observation.CaptureId, entityId = target.EntityId, capturedAtUtc = observation.CapturedAtUtc,
                frame = observation.Frame, width = pixels.width, height = pixels.height, visibleOpaquePixels = count,
                normalizedVisibleBounds = new Vector4((float)minX / pixels.width, (float)minY / pixels.height,
                    (float)(maxX - minX + 1) / pixels.width, (float)(maxY - minY + 1) / pixels.height),
                visiblePixelFraction = (float)count / (pixels.width * pixels.height),
                cameraDistanceToObservedPoint = Vector3.Distance(observation.CameraPosition, target.WorldPoint),
                touchesImageBoundary = minX <= 1 || minY <= 1 || maxX >= pixels.width - 2 || maxY >= pixels.height - 2
            };
        }
    }
}
