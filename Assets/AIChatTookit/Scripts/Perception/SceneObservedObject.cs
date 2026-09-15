using UnityEngine;

namespace NeEEvA.Perception
{
    /// <summary>A local association between a capture pixel and one object identity.
    /// WorldPoint is a capture-time geometry sample, never a current navigation destination.
    /// Transparent/unknown candidates deliberately expose no scene label or verified visibility.</summary>
    public sealed class SceneObservedObject
    {
        public string EntityId => entry.entityId;
        public string SceneLabel => VisibleConfirmed ? entry.sceneLabel : "";
        public string IdentitySource => VisibleConfirmed ? entry.identitySource : "geometry-candidate";
        public string CaptureId => observation.CaptureId;
        public string CapturedAtUtc => observation.CapturedAtUtc;
        public Transform Root => entry.identityRoot;
        public Vector3 WorldPoint { get; }
        public Vector2 ImagePoint { get; }
        public bool VisibleConfirmed { get; }
        public bool GeometryIsCurrent => TryValidate(out _) && entry.TryValidateObserved(true, out _);
        public string MeasurementScope => VisibleConfirmed ?
            "opaque surface visible in this captured frame; world point is a capture-time snapshot, not current position or reachability" :
            "conservative companion-geometry candidate only; transparency or unsupported rendering prevents a verified visible identity";
        internal readonly NevaSceneVisionCapture source;
        internal readonly SceneVisionObservation observation;
        internal readonly SceneVisionGrounding.Entry entry;
        internal SceneObservedObject(NevaSceneVisionCapture source, SceneVisionObservation observation,
            SceneVisionGrounding.Entry entry, Vector2 imagePoint, Vector3 worldPoint, bool confirmed)
        {
            this.source = source; this.observation = observation; this.entry = entry;
            ImagePoint = imagePoint; WorldPoint = worldPoint; VisibleConfirmed = confirmed;
        }
        public bool TryValidate(out string reason)
        {
            if (source == null || !source.IsObservationBindingCurrent(observation))
            { reason = "物件观测来源已关闭、重新绑定或场景已变化。"; return false; }
            if (!SceneVisionGrounding.Fresh(observation))
            { reason = "物件观测已过期，需要新图片确认。"; return false; }
            // Motion after capture does not erase what was seen in that capture. It does mean
            // WorldPoint is historical; actions must consume a newly confirmed identity/view.
            return entry.TryValidateObserved(false, out reason);
        }
    }
}
