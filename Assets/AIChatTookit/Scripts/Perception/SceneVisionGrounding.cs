using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NeEEvA.Perception
{
    /// <summary>An engine-verified visible surface, not an inferred object category or reachable destination.</summary>
    public sealed class SceneGroundedTarget
    {
        public string CaptureId => observation.CaptureId;
        public string SurfaceId => "surface-" + entry.instanceId.ToString("x");
        public Vector3 WorldPoint { get; }
        public Vector2 ImagePoint { get; }
        public bool IsValid => TryValidate(out _);
        private readonly NevaSceneVisionCapture source;
        private readonly SceneVisionObservation observation;
        internal readonly SceneVisionGrounding.Entry entry;

        internal SceneGroundedTarget(NevaSceneVisionCapture source, SceneVisionObservation observation,
            SceneVisionGrounding.Entry entry, Vector2 point, Vector3 world)
        { this.source = source; this.observation = observation; this.entry = entry; ImagePoint = point; WorldPoint = world; }

        public bool TryValidate(out string reason)
        {
            if (!TryValidateGeometry(out reason)) return false;
            if (!SceneVisionGrounding.Fresh(observation))
            { reason = "定位所依据的图片已过期，需要重新观察。"; return false; }
            return true;
        }

        /// <summary>For an already committed movement: continuity of geometry, not a claim of present visibility.</summary>
        public bool TryValidateGeometry(out string reason)
        {
            if (source == null || !source.IsObservationBindingCurrent(observation))
            { reason = "视觉来源已关闭、重新绑定或场景已变化。"; return false; }
            return entry.TryValidate(out reason);
        }

        /// <summary>Projection only. TryConfirmVisible is additionally required before claiming renewed visibility.</summary>
        public bool TryProject(SceneVisionObservation current, out Vector2 imagePoint, out string reason)
        {
            imagePoint = default;
            if (!TryValidateGeometry(out reason)) return false;
            if (current == null || !current.HasImage || !SceneVisionGrounding.Fresh(current) ||
                !source.IsObservationBindingCurrent(current))
            { reason = "重新投影需要当前视觉来源的新图片。"; return false; }
            Vector3 local = Quaternion.Inverse(current.CameraRotation) * (WorldPoint - current.CameraPosition);
            if (local.z <= .025f) { reason = "目标位于视点后方或过近。"; return false; }
            float tangent = Mathf.Tan(current.VerticalFov * Mathf.Deg2Rad * .5f);
            imagePoint = new Vector2(.5f + local.x / (2 * local.z * tangent * current.Width / current.Height),
                .5f - local.y / (2 * local.z * tangent));
            if (!SceneVisionGrounding.InImage(imagePoint)) { reason = "目标不在新图片范围内。"; return false; }
            reason = ""; return true;
        }
    }

    /// <summary>
    /// A bounded companion geometry render at the RGB camera's exact pose/frame. It stores linear eye depth
    /// and opaque renderer IDs only in local memory. No scene inventory, name, inferred category or depth
    /// image is sent to the model. Unknown/transparent shader surfaces block grounding conservatively.
    /// </summary>
    public static class SceneVisionGrounding
    {
        public const float MaximumObservationAge = 12f;
        private const int RendererLimit = 4096;
        // Positive IDs preserve the original stationary surface contract. Negative first-domain
        // IDs identify verified opaque rigid meshes under animation/physics; negative second-domain
        // IDs retain an identity handle for conservative unknown/transparent blockers only.
        private const int CandidateIdOffset = RendererLimit;
        private static readonly int IdProperty = Shader.PropertyToID("_NevaGroundingId");
        private static readonly int CullProperty = Shader.PropertyToID("_NevaGroundingCull");
        private static readonly int DepthTestProperty = Shader.PropertyToID("_NevaGroundingZTest");

        internal sealed class Entry
        {
            internal Renderer renderer;
            internal int instanceId, layer;
            internal Matrix4x4 matrix, transformMatrix;
            internal Bounds bounds;
            internal Mesh mesh;
            internal int vertices, submeshes;
            internal Bounds meshBounds;
            internal Material[] materials;
            internal string[] signatures, observedSignatures;
            internal bool stationary, observedRigid;
            internal SceneObjectIdentity identity;
            internal Transform identityRoot;
            internal string entityId, sceneLabel, identitySource, authoredKey;

            internal bool TryValidateObserved(bool requireCurrentGeometry, out string reason)
            {
                reason = "";
                if (renderer == null || !renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy ||
                    renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly || identityRoot == null || !identityRoot.gameObject.activeInHierarchy)
                { reason = "捕获到的物件部件或实体已经销毁、隐藏或停用。"; return false; }
                var currentIdentity = SceneObjectIdentity.For(renderer);
                if ((identitySource == "scene-authored" && identity == null) || currentIdentity != identity || (identity != null &&
                    (identity.EntityId != entityId || identity.SceneLabel != sceneLabel || identity.AuthoredKey != authoredKey || identity.Root != identityRoot)))
                { reason = "物件的显式身份绑定或场景标注已改变，需要重新观察。"; return false; }
                if (renderer.gameObject.layer != layer)
                { reason = "物件渲染层已改变，需要重新观察。"; return false; }
                if (observedRigid)
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (!(renderer is MeshRenderer) || filter == null || filter.sharedMesh != mesh || mesh == null ||
                        mesh.vertexCount != vertices || mesh.subMeshCount != submeshes || mesh.bounds != meshBounds ||
                        renderer.GetComponentInParent<LODGroup>() != null)
                    { reason = "图片之后物件网格或渲染结构已改变，需要重新观察。"; return false; }
                }
                Material[] now = renderer.sharedMaterials;
                if (now.Length != materials.Length) { reason = "图片之后物件材质已变化。"; return false; }
                for (int i = 0; i < now.Length; i++)
                    if (now[i] != materials[i] || ObservedSignature(now[i]) != observedSignatures[i])
                    { reason = "图片之后物件可见性材质已变化。"; return false; }
                if (requireCurrentGeometry && (!observedRigid || !Same(transformMatrix, renderer.transform.localToWorldMatrix) ||
                    !Same(matrix, renderer.localToWorldMatrix) || Vector3.Distance(bounds.center, renderer.bounds.center) > .002f ||
                    Vector3.Distance(bounds.size, renderer.bounds.size) > .002f))
                { reason = "当前几何已经不同于图片中的位置；捕获点仅是历史快照。"; return false; }
                return true;
            }

            internal bool TryValidate(out string reason)
            {
                reason = "";
                if (renderer == null || !renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy)
                { reason = "原可见表面已销毁、隐藏或停用。"; return false; }
                if (!stationary) { reason = "该表面会变形或使用未验证的渲染方式，本批暂不据此定位。"; return false; }
                // Static batching may retain the renderer's GPU matrix/bounds after a Transform edit.
                // Check the authored transform independently so a committed target is invalidated
                // immediately, including before the renderer's next update.
                if (renderer.gameObject.layer != layer || !Same(transformMatrix, renderer.transform.localToWorldMatrix) ||
                    !Same(matrix, renderer.localToWorldMatrix) ||
                    Vector3.Distance(bounds.center, renderer.bounds.center) > .002f || Vector3.Distance(bounds.size, renderer.bounds.size) > .002f)
                { reason = "图片之后目标几何或位置已变化，需要重新观察。"; return false; }
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh != mesh || mesh == null || mesh.vertexCount != vertices ||
                    mesh.subMeshCount != submeshes || mesh.bounds != meshBounds)
                { reason = "图片之后目标网格已变化，需要重新观察。"; return false; }
                Material[] now = renderer.sharedMaterials;
                if (now.Length != materials.Length) { reason = "图片之后目标材质已变化。"; return false; }
                for (int i = 0; i < now.Length; i++)
                    if (now[i] != materials[i] || Signature(now[i]) != signatures[i])
                    { reason = "图片之后目标可见性材质已变化。"; return false; }
                return true;
            }
        }

        internal sealed class Frame
        {
            internal int width, height;
            internal float[] depthIds;
            internal Entry[] entries;
            internal string failure;
            internal bool rowsStartAtTop;
        }

        internal sealed class Pending
        {
            internal RenderTexture target;
            internal Frame frame;
            internal void Request(Action<Frame> complete)
            {
                AsyncGPUReadback.Request(target, 0, request => {
                    try
                    {
                        if (request.hasError) frame.failure = "表面定位的 GPU 读回失败。";
                        else
                        {
                            frame.depthIds = request.GetData<float>().ToArray();
                            if (frame.depthIds.Length != frame.width * frame.height * 2)
                            { frame.depthIds = null; frame.failure = "表面定位的 GPU 数据长度不匹配。"; }
                        }
                    }
                    catch (Exception e) { frame.failure = "表面定位读回失败：" + e.Message; }
                    finally { Dispose(); complete(frame); }
                });
            }
            internal void Dispose()
            { if (target != null) { target.Release(); SceneVisionSelfView.DestroyOwned(target); target = null; } }
        }

        internal static Pending Render(Camera camera)
        {
            if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGFloat))
                throw new InvalidOperationException("当前图形后端不支持表面定位缓冲。仍可使用 RGB 观察。");
            Shader shader = Resources.Load<Shader>("NevaSceneVisionGrounding");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("表面定位着色器不可用。");
            var entries = new List<Entry>();
            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            // Terrain is not a Renderer and therefore cannot silently be omitted from the occlusion pass.
            foreach (Terrain terrain in Terrain.activeTerrains)
                if (terrain != null && terrain.enabled && terrain.terrainData != null &&
                    (camera.cullingMask & (1 << terrain.gameObject.layer)) != 0 &&
                    GeometryUtility.TestPlanesAABB(planes, new Bounds(terrain.transform.position + terrain.terrainData.size * .5f, terrain.terrainData.size)))
                    throw new InvalidOperationException("视野含本批未验证的 Terrain 几何，暂不定位该图片。");
            var pending = new Pending { target = new RenderTexture(camera.targetTexture.width, camera.targetTexture.height,
                24, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear) {
                name = "NeEEvA local surface grounding", hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1,
                filterMode = FilterMode.Point, useMipMap = false, autoGenerateMips = false } };
            Material opaque = null, blocker = null;
            bool previousInvertCulling = GL.invertCulling;
            var commands = new CommandBuffer { name = "NeEEvA capture-linked local grounding" };
            try
            {
                if (!pending.target.Create()) throw new InvalidOperationException("无法创建表面定位缓冲。");
                opaque = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                blocker = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                opaque.SetFloat(CullProperty, (float)CullMode.Back); blocker.SetFloat(CullProperty, (float)CullMode.Off);
                float depthTest = (float)(SystemInfo.usesReversedZBuffer ? CompareFunction.GreaterEqual : CompareFunction.LessEqual);
                opaque.SetFloat(DepthTestProperty, depthTest); blocker.SetFloat(DepthTestProperty, depthTest);
                commands.SetRenderTarget(pending.target);
                commands.ClearRenderTarget(true, true, Color.clear, SystemInfo.usesReversedZBuffer ? 0f : 1f);
                var gpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
                commands.SetViewProjectionMatrices(camera.worldToCameraMatrix, gpuProjection);
                // Camera.Render performs the render-texture Y-flip winding correction internally.
                // This explicit companion pass must reproduce it, or front-facing opaque Quads
                // are culled while only two-sided blocker geometry survives on D3D11.
                commands.SetInvertCulling(gpuProjection.m11 * camera.projectionMatrix.m11 < 0f);
                // One scan per demand-driven observation (at most 2/s by default), never per animation frame.
                foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (!renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy ||
                        renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly ||
                        (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0 || !GeometryUtility.TestPlanesAABB(planes, renderer.bounds)) continue;
                    if (entries.Count >= RendererLimit) throw new InvalidOperationException("可见几何超过本批定位预算，未使用不完整深度。");
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    Mesh mesh = filter != null ? filter.sharedMesh : null;
                    Material[] materials = renderer.sharedMaterials;
                    var entry = new Entry { renderer = renderer, instanceId = renderer.GetInstanceID(), layer = renderer.gameObject.layer,
                        matrix = renderer.localToWorldMatrix, transformMatrix = renderer.transform.localToWorldMatrix,
                        bounds = renderer.bounds, mesh = mesh,
                        vertices = mesh != null ? mesh.vertexCount : 0, submeshes = mesh != null ? mesh.subMeshCount : 0,
                        meshBounds = mesh != null ? mesh.bounds : default, materials = materials, signatures = new string[materials.Length], observedSignatures = new string[materials.Length],
                        stationary = renderer is MeshRenderer && mesh != null && renderer.GetComponentInParent<LODGroup>() == null &&
                            renderer.GetComponentInParent<Animator>() == null && renderer.GetComponentInParent<Rigidbody>() == null };
                    entry.observedRigid = renderer is MeshRenderer && mesh != null && renderer.GetComponentInParent<LODGroup>() == null;
                    entry.identity = SceneObjectIdentity.For(renderer);
                    entry.identityRoot = entry.identity != null ? entry.identity.Root : renderer.transform;
                    entry.entityId = entry.identity != null ? entry.identity.EntityId : SceneObjectIdentity.RendererId(renderer);
                    entry.sceneLabel = entry.identity != null ? entry.identity.SceneLabel : "";
                    entry.identitySource = entry.identity != null ? entry.identity.IdentitySource : "renderer-session";
                    entry.authoredKey = entry.identity != null ? entry.identity.AuthoredKey : "";
                    entries.Add(entry);
                    for (int sub = 0; sub < materials.Length; sub++)
                    {
                        entry.signatures[sub] = Signature(materials[sub]);
                        entry.observedSignatures[sub] = ObservedSignature(materials[sub]);
                        bool observedOpaque = SupportedObservedOpaque(materials[sub]);
                        bool supported = entry.stationary && SupportedOpaque(materials[sub]);
                        if (supported)
                        {
                            commands.SetGlobalFloat(IdProperty, entries.Count);
                            commands.DrawRenderer(renderer, opaque, sub, 0);
                        }
                        else
                        {
                            // Keep the original conservative two-sided occluder, with a recoverable
                            // identity handle. A candidate never reveals objects behind it.
                            commands.SetGlobalFloat(IdProperty, -(CandidateIdOffset + entries.Count));
                            commands.DrawRenderer(renderer, blocker, sub, 0);
                            if (entry.observedRigid && observedOpaque)
                            {
                                // Overwrite only actual front faces at equal depth. Back faces retain
                                // candidate status, so dynamic object support does not turn previously
                                // blocked rear geometry into a verified physical surface.
                                commands.SetGlobalFloat(IdProperty, -entries.Count);
                                commands.DrawRenderer(renderer, opaque, sub, 0);
                            }
                        }
                    }
                }
                commands.SetGlobalFloat(IdProperty, 0f);
                commands.SetInvertCulling(previousInvertCulling);
                Graphics.ExecuteCommandBuffer(commands);
                pending.frame = new Frame { width = pending.target.width, height = pending.target.height, entries = entries.ToArray(),
                    rowsStartAtTop = SystemInfo.graphicsUVStartsAtTop };
                return pending;
            }
            catch { pending.Dispose(); throw; }
            finally
            {
                commands.Release();
                if (opaque != null) SceneVisionSelfView.DestroyOwned(opaque);
                if (blocker != null) SceneVisionSelfView.DestroyOwned(blocker);
            }
        }

        internal static bool TryResolve(NevaSceneVisionCapture source, SceneVisionObservation observation, Vector2 pixel,
            out SceneGroundedTarget target, out string reason)
        {
            target = null; reason = "";
            if (!InImage(pixel)) { reason = "图片坐标必须在 [0,1] 范围，x 向右、y 向下。"; return false; }
            Frame frame = observation.Grounding;
            if (frame == null || frame.depthIds == null)
            { reason = frame?.failure ?? "该图片没有同帧表面定位数据，请重新观察。"; return false; }
            // Explicit GPU projection + native RGFloat readback has native top-first rows on D3D11,
            // unlike the Camera.Render RGB -> Texture2D/JPEG path. The asymmetric regression
            // verifies both identity and depth, including a farther overlapping surface.
            int x = Mathf.Clamp(Mathf.FloorToInt(pixel.x * frame.width), 0, frame.width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt((frame.rowsStartAtTop ? pixel.y : 1f - pixel.y) * frame.height), 0, frame.height - 1);
            int index = (y * frame.width + x) * 2;
            float depth = frame.depthIds[index]; int id = Mathf.RoundToInt(frame.depthIds[index + 1]);
            if (!Finite(depth) || depth <= .025f || depth > 60f || id <= 0 || id > frame.entries.Length)
            { reason = id < 0 ? "该像素落在透明、动态或未验证材质上，本批无法可靠定位。" : "该像素没有可定位的可见实体表面。"; return false; }
            // A point on a silhouette is ambiguous after JPEG/resizing. Do not average surfaces.
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int xx = Mathf.Clamp(x + dx, 0, frame.width - 1), yy = Mathf.Clamp(y + dy, 0, frame.height - 1);
                int sample = (yy * frame.width + xx) * 2;
                if (Mathf.RoundToInt(frame.depthIds[sample + 1]) != id ||
                    Mathf.Abs(frame.depthIds[sample] - depth) > Mathf.Max(.03f, depth * .025f))
                { reason = "目标像素接近遮挡或几何边缘，请选择表面内部并重新观察。"; return false; }
            }
            Entry entry = frame.entries[id - 1];
            if (!entry.TryValidate(out reason)) return false;
            float centerX = (x + .5f) / frame.width;
            float centerY = frame.rowsStartAtTop ? (y + .5f) / frame.height : 1f - (y + .5f) / frame.height;
            float tangent = Mathf.Tan(observation.VerticalFov * Mathf.Deg2Rad * .5f);
            Vector3 local = new Vector3((centerX * 2 - 1) * tangent * frame.width / frame.height * depth,
                (1 - centerY * 2) * tangent * depth, depth);
            Vector3 world = observation.CameraPosition + observation.CameraRotation * local;
            target = new SceneGroundedTarget(source, observation, entry, new Vector2(centerX, centerY), world);
            return true;
        }

        internal static bool TryResolveObject(NevaSceneVisionCapture source, SceneVisionObservation observation,
            Vector2 pixel, out SceneObservedObject value, out string reason)
        {
            value = null;
            if (!TryReadObjectPixel(observation, pixel, out var entry, out var center, out var world, out bool visible, out reason)) return false;
            if (!entry.TryValidateObserved(false, out reason)) return false;
            value = new SceneObservedObject(source, observation, entry, center, world, visible);
            if (!visible) reason = "该像素只有透明、变形或未验证渲染的几何候选，不能声称已经看清实体；可在同一实体中寻找实际可见的不透明部件。";
            return true;
        }

        internal static bool TryFindObject(NevaSceneVisionCapture source, SceneVisionObservation observation,
            string entityId, out SceneObservedObject value, out string reason)
        {
            value = null; reason = "";
            Frame frame = observation?.Grounding;
            if (string.IsNullOrEmpty(entityId) || frame?.depthIds == null || frame.entries == null)
            { reason = "缺少已选实体或本帧物件像素数据。"; return false; }
            var eligible = new bool[frame.entries.Length];
            int eligibleCount = 0;
            for (int i = 0; i < frame.entries.Length; i++)
            {
                var entry = frame.entries[i];
                if (entry.entityId != entityId || !entry.observedRigid || !entry.TryValidateObserved(false, out _)) continue;
                eligible[i] = true; eligibleCount++;
            }
            if (eligibleCount == 0) { reason = "本次图片没有该实体仍有效的刚性部件，未搜寻其他物件代替。"; return false; }
            // Local bounded lookup in this already-rendered frame only. Never enumerate scene
            // objects into the model context or raycast through a transparent candidate.
            double sumX = 0, sumY = 0; int count = 0;
            for (int y = 0; y < frame.height; y++) for (int x = 0; x < frame.width; x++)
            {
                int sample = (y * frame.width + x) * 2;
                int id = Mathf.RoundToInt(frame.depthIds[sample + 1]);
                if (!ObjectEntryId(id, frame.entries.Length, out int entryIndex, out bool opaque) || !opaque || !eligible[entryIndex]) continue;
                float depth = frame.depthIds[sample]; if (!Finite(depth) || depth <= .025f || depth > 60f) continue;
                sumX += x; sumY += y; count++;
            }
            if (count == 0) { reason = "同帧里没有该实体已验证可见的不透明部件；透明候选没有被当成实体识别。"; return false; }
            float centerX = (float)(sumX / count), centerY = (float)(sumY / count);
            float bestScore = float.PositiveInfinity; Vector2 best = default; bool found = false;
            for (int y = 1; y < frame.height - 1; y++) for (int x = 1; x < frame.width - 1; x++)
            {
                int sample = (y * frame.width + x) * 2;
                int id = Mathf.RoundToInt(frame.depthIds[sample + 1]);
                if (!ObjectEntryId(id, frame.entries.Length, out int entryIndex, out bool opaque) || !opaque || !eligible[entryIndex]) continue;
                float score = (x - centerX) * (x - centerX) + (y - centerY) * (y - centerY);
                if (score >= bestScore || !ConsistentObjectPixel(frame, x, y, id, frame.depthIds[sample])) continue;
                bestScore = score; best = new Vector2((x + .5f) / frame.width,
                    frame.rowsStartAtTop ? (y + .5f) / frame.height : 1f - (y + .5f) / frame.height); found = true;
            }
            if (!found || !TryResolveObject(source, observation, best, out value, out reason) ||
                value.EntityId != entityId || !value.VisibleConfirmed)
            { value = null; if (string.IsNullOrEmpty(reason)) reason = "该实体只有边缘或不可靠像素，尚未确认其可见部件。"; return false; }
            return true;
        }

        internal static bool ObjectEntryId(int id, int count, out int index, out bool confirmed)
        {
            confirmed = id > 0 || (id < 0 && id >= -CandidateIdOffset);
            int encoded = id > 0 ? id : id >= -CandidateIdOffset ? -id : -id - CandidateIdOffset;
            index = encoded - 1;
            return id != 0 && encoded > 0 && index < count;
        }
        internal static bool ConsistentObjectPixel(Frame frame, int x, int y, int id, float depth)
        {
            if (!Finite(depth) || depth <= .025f || depth > 60f) return false;
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int xx = Mathf.Clamp(x + dx, 0, frame.width - 1), yy = Mathf.Clamp(y + dy, 0, frame.height - 1);
                int sample = (yy * frame.width + xx) * 2;
                if (Mathf.RoundToInt(frame.depthIds[sample + 1]) != id || !Finite(frame.depthIds[sample]) ||
                    Mathf.Abs(frame.depthIds[sample] - depth) > Mathf.Max(.03f, depth * .025f)) return false;
            }
            return true;
        }
        private static bool TryReadObjectPixel(SceneVisionObservation observation, Vector2 pixel, out Entry entry,
            out Vector2 center, out Vector3 world, out bool confirmed, out string reason)
        {
            entry = null; center = default; world = default; confirmed = false; reason = "";
            if (!InImage(pixel)) { reason = "物件图片坐标必须在[0,1]内，x向右、y向下。"; return false; }
            Frame frame = observation?.Grounding;
            if (frame?.depthIds == null || frame.entries == null)
            { reason = frame?.failure ?? "该图片没有同帧物件定位数据。"; return false; }
            int x = Mathf.Clamp(Mathf.FloorToInt(pixel.x * frame.width), 0, frame.width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt((frame.rowsStartAtTop ? pixel.y : 1f - pixel.y) * frame.height), 0, frame.height - 1);
            int sample = (y * frame.width + x) * 2;
            float depth = frame.depthIds[sample]; int id = Mathf.RoundToInt(frame.depthIds[sample + 1]);
            if (!ObjectEntryId(id, frame.entries.Length, out int index, out confirmed) || !ConsistentObjectPixel(frame, x, y, id, depth))
            { reason = "该像素没有稳定的物件内部几何，或处于遮挡/轮廓边缘。"; return false; }
            entry = frame.entries[index]; confirmed &= entry.observedRigid;
            center = new Vector2((x + .5f) / frame.width, frame.rowsStartAtTop ? (y + .5f) / frame.height : 1f - (y + .5f) / frame.height);
            float tangent = Mathf.Tan(observation.VerticalFov * Mathf.Deg2Rad * .5f);
            Vector3 local = new Vector3((center.x * 2 - 1) * tangent * frame.width / frame.height * depth,
                (1 - center.y * 2) * tangent * depth, depth);
            world = observation.CameraPosition + observation.CameraRotation * local;
            return true;
        }

        internal static bool Fresh(SceneVisionObservation observation) => observation != null &&
            observation.HasImage && Finite(observation.CapturedAtRealtime) &&
            Time.realtimeSinceStartup >= observation.CapturedAtRealtime &&
            Time.realtimeSinceStartup - observation.CapturedAtRealtime <= MaximumObservationAge;
        internal static bool InImage(Vector2 p) => Finite(p.x) && Finite(p.y) && p.x >= 0 && p.x <= 1 && p.y >= 0 && p.y <= 1;
        private static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        private static bool Same(Matrix4x4 a, Matrix4x4 b)
        { for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > .0001f) return false; return true; }
        private static string Signature(Material m) => m == null ? "missing" :
            (m.shader != null ? m.shader.GetInstanceID().ToString() : "no-shader") + ":" + m.renderQueue + ":" +
            m.GetTag("RenderType", false, "") + ":" + (m.HasProperty("_Mode") ? m.GetFloat("_Mode") : -1) + ":" +
            (m.HasProperty("_Cull") ? m.GetFloat("_Cull") : 2) + ":" + m.enableInstancing;
        private static string ObservedSignature(Material material) => Signature(material) + ":" +
            (material != null && material.HasProperty("_SrcBlend") ? material.GetFloat("_SrcBlend") : -1) + ":" +
            (material != null && material.HasProperty("_DstBlend") ? material.GetFloat("_DstBlend") : -1) + ":" +
            (material != null && material.HasProperty("_ZWrite") ? material.GetFloat("_ZWrite") : -1) + ":" +
            (material != null && material.IsKeywordEnabled("_ALPHATEST_ON")) + ":" +
            (material != null && material.IsKeywordEnabled("_ALPHABLEND_ON")) + ":" +
            (material != null && material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"));

        private static bool SupportedObservedOpaque(Material material)
        {
            if (SupportedOpaque(material)) return true;
            // Unity 2022.3.22f1 official AutodeskInteractive.shader uses the built-in Standard
            // vertex path (no displacement) and alpha variants. Roughness/normal/parallax affect
            // shading/UVs, not mesh position. Accept only actual opaque blend/depth/keyword state.
            // This deliberately does NOT extend SupportedOpaque: old navigation grounding keeps
            // rejecting Autodesk through its negative companion ID, even for a stationary mesh.
            if (material == null || material.shader == null || material.shader.name != "Autodesk Interactive" ||
                material.renderQueue > 2500 || !material.HasProperty("_Mode") || material.GetFloat("_Mode") != 0 ||
                !material.HasProperty("_SrcBlend") || material.GetFloat("_SrcBlend") != (float)BlendMode.One ||
                !material.HasProperty("_DstBlend") || material.GetFloat("_DstBlend") != (float)BlendMode.Zero ||
                !material.HasProperty("_ZWrite") || material.GetFloat("_ZWrite") != 1 ||
                material.IsKeywordEnabled("_ALPHATEST_ON") || material.IsKeywordEnabled("_ALPHABLEND_ON") ||
                material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON")) return false;
            string renderType = material.GetTag("RenderType", false, "");
            return renderType == "Opaque" || renderType == "";
        }

        private static bool SupportedOpaque(Material material)
        {
            if (material == null || material.shader == null || material.renderQueue > 2500 ||
                (material.HasProperty("_Mode") && material.GetFloat("_Mode") != 0)) return false;
            string type = material.GetTag("RenderType", false, "");
            if (type != "Opaque" && type != "") return false;
            switch (material.shader.name)
            {
                case "Standard": case "Standard (Specular setup)": case "Unlit/Color": case "Unlit/Texture":
                case "Legacy Shaders/Diffuse": case "Mobile/Diffuse": case "AIChatToolkit/DesktopScreen": return true;
                default: return false;
            }
        }
    }
}
