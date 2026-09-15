using System;
using UnityEngine;

namespace NeEEvA.Perception
{
    /// <summary>A single immutable observation. Its pixels and camera facts always describe the same render.</summary>
    public sealed class SceneVisionObservation
    {
        public string CaptureId { get; }
        public string Source => "character-scene-rgb";
        public string State { get; }
        public string Reason { get; }
        public string CapturedAtUtc { get; }
        public float CapturedAtRealtime { get; }
        public int Frame { get; }
        public int SceneHandle { get; }
        public string SceneName { get; }
        public string ScenePath { get; }
        public string LoadedSceneFingerprint { get; }
        public Vector3 CameraPosition { get; }
        public Quaternion CameraRotation { get; }
        public float VerticalFov { get; }
        public int Width { get; }
        public int Height { get; }
        public string ImageDataUrl { get; }
        internal SceneVisionGrounding.Frame Grounding { get; }
        public bool HasSurfaceGrounding => Grounding != null && Grounding.depthIds != null;
        public string SurfaceGroundingReason => HasSurfaceGrounding ? "" : Grounding?.failure ?? "该图片没有同帧表面定位数据。";
        public bool HasImage => State == "ready" && !string.IsNullOrEmpty(ImageDataUrl);
        public string MetadataJson => JsonUtility.ToJson(new Metadata(this));

        internal SceneVisionObservation(string id, string state, string reason, string utc, float time,
            int frame, Pose pose, float fov, int width, int height, string pixels = null,
            int sceneHandle = 0, string sceneName = "", string scenePath = "", string loadedSceneFingerprint = "",
            SceneVisionGrounding.Frame grounding = null)
        {
            CaptureId = id; State = state; Reason = reason ?? ""; CapturedAtUtc = utc ?? "";
            CapturedAtRealtime = time; Frame = frame; CameraPosition = pose.position;
            CameraRotation = pose.rotation; VerticalFov = fov; Width = width; Height = height;
            ImageDataUrl = state == "ready" ? pixels : null;
            SceneHandle = sceneHandle; SceneName = sceneName; ScenePath = scenePath;
            LoadedSceneFingerprint = loadedSceneFingerprint;
            Grounding = state == "ready" ? grounding : null;
        }

        internal static SceneVisionObservation WithoutImage(string state, string reason) =>
            new SceneVisionObservation(Guid.NewGuid().ToString("N"), state, reason, "", -1, -1,
                new Pose(Vector3.zero, Quaternion.identity), 0, 0, 0);

        [Serializable] private sealed class Metadata
        {
            public string captureId, source, state, reason, capturedAtUtc, sceneName, scenePath, loadedSceneFingerprint, projection = "perspective";
            public float capturedAtRealtime, verticalFov;
            public int frame, width, height, sceneHandle;
            public Vector3 cameraPosition;
            public Quaternion cameraRotation;
            public bool hasImage;
            public string scope = "RGB rendered from the character head view at capture time. No inferred object names, depth, reachability or current-visibility guarantee.";
            public Metadata(SceneVisionObservation value)
            {
                captureId = value.CaptureId; source = value.Source; state = value.State; reason = value.Reason;
                capturedAtUtc = value.CapturedAtUtc; capturedAtRealtime = value.CapturedAtRealtime; frame = value.Frame;
                width = value.Width; height = value.Height; cameraPosition = value.CameraPosition;
                cameraRotation = value.CameraRotation; verticalFov = value.VerticalFov; hasImage = value.HasImage;
                sceneHandle = value.SceneHandle; sceneName = value.SceneName; scenePath = value.ScenePath;
                loadedSceneFingerprint = value.LoadedSceneFingerprint;
            }
        }
    }
}
