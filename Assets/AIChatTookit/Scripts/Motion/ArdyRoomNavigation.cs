using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace NeEEvA.Motion
{
    /// <summary>One observed rejection, not a claim about all possible routes or every obstacle.</summary>
    [Serializable]
    public sealed class ArdyRoomRouteDiagnostic
    {
        public bool success, hasSample;
        public string stage = "setup", code = "", reason = "";
        // sample is the checked coordinate (or actual sweep hit), not an inferred blockage location.
        // NavMesh boundary/incomplete-path failures do not identify a physical collider.
        public Vector3 start, target, from, to, sample;
        public int segmentIndex = -1, colliderId;
        public string colliderName = "", colliderPath = "", colliderType = "", sourceObjectPath = "";
    }

    /// <summary>A geometric route for the independently driven ARDY body. Points are ground coordinates.</summary>
    public sealed class ArdyRoomRoute
    {
        public Vector3[] Corners { get; internal set; }
        public float GroundY { get; internal set; }
        public float Length { get; internal set; }
    }

    /// <summary>
    /// Conservative, opt-in flat-floor navigation. Owns only its temporary NavMesh, never a character
    /// transform or NavMeshAgent. Navigation is a coarse planner; every planned and executed segment
    /// is checked against current Physics geometry, including obstacles outside the bake hierarchy.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ArdyRoomNavigation : MonoBehaviour
    {
        [Min(.05f)] public float AgentRadius = .30f;
        [Tooltip("Extra planner clearance for voxel/corner rounding; the actual Physics body radius is unchanged.")]
        [Range(0f, .15f)] public float PlanningClearance = .05f;
        [Min(.5f)] public float AgentHeight = 1.70f;
        [Range(0f, 10f)] public float MaxGroundSlope = 3f;
        [Range(.001f, .10f)] public float MaxLocalGroundChange = .025f;
        [Range(.001f, .10f)] public float MaxRouteHeightVariation = .04f;
        [Range(.01f, .15f)] public float SampleSpacing = .06f;
        [Range(.005f, .06f)] public float FloorClearance = .015f;
        [Tooltip("Only verified broad, thin mesh coverings with original solid floor immediately underneath receive this tolerance.")]
        [Range(.001f, .03f)] public float MaxThinCoverHeight = .03f;
        [Min(.1f)] public float MaxGuardedStep = .50f;
        [Min(1f)] public float MaxRouteLength = 50f;
        [Tooltip("Exclude these hierarchies from the environment bake/support sources. They still obstruct the body.")]
        public Transform[] IgnoredRoots = Array.Empty<Transform>();
        [Tooltip("Explicit non-solid visual hierarchies, such as a water surface. Their real Colliders remain active obstacles/support.")]
        public Transform[] ExcludedVisualRoots = Array.Empty<Transform>();
        [Tooltip("The standard Water layer is treated as visual water only when synthesizing missing mesh colliders. Existing colliders are never ignored by this setting.")]
        public bool ExcludeWaterLayerVisuals = true;

        public bool IsBuilt => dataInstance.valid;
        public Transform EnvironmentRoot => environmentRoot;
        public int SourceCount { get; private set; }
        public int GeometryProxyCount => geometryProxies.Count + thinCovers.Count;
        public int ThinCoverCount => thinCovers.Count;
        public IReadOnlyList<string> ThinCoverDiagnostics => thinCoverDiagnostics;
        public ArdyRoomGeometryReference GeometryReference { get; set; }
        public string Status { get; private set; } = "未建立房间地面导航";

        private Transform environmentRoot, avatarRoot;
        private readonly HashSet<Collider> supportColliders = new HashSet<Collider>();
        private readonly Dictionary<BoxCollider, MeshRenderer> geometryProxies = new Dictionary<BoxCollider, MeshRenderer>();
        private sealed class ThinCover
        {
            public MeshRenderer renderer;
            public Matrix4x4 matrix;
            public Mesh original, worldMesh, filterMeshAtCapture;
            public bool invalidated;
        }
        private readonly Dictionary<MeshCollider, ThinCover> thinCovers = new Dictionary<MeshCollider, ThinCover>();
        private readonly List<Mesh> ownedCoverMeshes = new List<Mesh>();
        private readonly List<string> thinCoverDiagnostics = new List<string>();
        private GameObject proxyRoot;
        private NavMeshData data;
        private NavMeshDataInstance dataInstance;
        private int agentTypeId = -1;
        private bool ownsAgentSettings;
        private bool hasRoutePlane;
        private float routeGroundY;
        private static readonly Vector2[] Footprint = {
            Vector2.zero, Vector2.right, Vector2.left, Vector2.up, Vector2.down,
            new Vector2(.7071f, .7071f), new Vector2(-.7071f, .7071f),
            new Vector2(.7071f, -.7071f), new Vector2(-.7071f, -.7071f) };

        public bool Build(Transform environment, Transform avatar, out string reason)
        {
            Clear();
            if (environment == null || avatar == null)
                return Fail("需要明确的环境根节点与角色根节点。", out reason);
            if (environment == avatar || environment.IsChildOf(avatar))
                return Fail("环境根节点不能是角色或角色的子节点。", out reason);
            if (!ValidSettings()) return Fail("导航几何参数无效。", out reason);
            environmentRoot = environment;
            avatarRoot = avatar;
            if (GeometryReference == null || GeometryReference.environmentRoot != environment)
                GeometryReference = environment.GetComponent<ArdyRoomGeometryReference>();
            Physics.SyncTransforms();
            var markups = new List<NavMeshBuildMarkup>();
            markups.Add(new NavMeshBuildMarkup { root = avatar, ignoreFromBuild = true });
            if (IgnoredRoots != null)
                foreach (var ignored in IgnoredRoots)
                    if (ignored != null) markups.Add(new NavMeshBuildMarkup { root = ignored, ignoreFromBuild = true });
            var sources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(environment, ~0, NavMeshCollectGeometry.PhysicsColliders, 0, markups, sources);
            sources.RemoveAll(source => !(source.component is Collider collider) || !IsEnvironmentCollider(collider));
            if (sources.Count == 0) return Fail("环境根节点下没有可用于地面导航的非触发 Collider。", out reason);
#if !UNITY_EDITOR
            foreach (var source in sources)
                if (source.sourceObject is Mesh sourceMesh && !sourceMesh.isReadable)
                    return Fail("打包运行时的导航烘焙需要可读取的碰撞网格；请先准备房间网格 Read/Write：" + sourceMesh.name, out reason);
#endif
            foreach (var source in sources) supportColliders.Add((Collider)source.component);
            try
            {
            CreateGeometryProxies();
            foreach (var pair in geometryProxies)
            {
                var box = pair.Key;
                sources.Add(new NavMeshBuildSource { shape = NavMeshBuildSourceShape.Box,
                    transform = box.transform.localToWorldMatrix, size = box.size, component = box,
                    area = NavMesh.GetAreaFromName("Not Walkable") });
            }
            SourceCount = sources.Count;
            var bounds = new Bounds();
            bool first = true;
            foreach (var source in sources)
            {
                var collider = (Collider)source.component;
                if (first) { bounds = collider.bounds; first = false; }
                else bounds.Encapsulate(collider.bounds);
            }
            bounds.Expand(new Vector3(AgentRadius * 4f, AgentHeight * 2f, AgentRadius * 4f));
                var settings = NavMesh.CreateSettings();
                agentTypeId = settings.agentTypeID;
                ownsAgentSettings = true;
                settings.agentRadius = AgentRadius + PlanningClearance;
                settings.agentHeight = AgentHeight;
                // Thin covers use their original supporting floor for coarse planning. Their real mesh
                // surface is checked by Physics; it is never used to invent another NavMesh platform.
                settings.agentClimb = MaxLocalGroundChange;
                settings.agentSlope = MaxGroundSlope;
                settings.minRegionArea = .01f;
                settings.overrideVoxelSize = true;
                settings.voxelSize = Mathf.Min(.025f, AgentRadius / 8f);
                settings.overrideTileSize = true;
                settings.tileSize = 128;
                data = NavMeshBuilder.BuildNavMeshData(settings, sources, bounds, Vector3.zero, Quaternion.identity);
                if (data == null) throw new InvalidOperationException("Unity did not produce NavMeshData.");
                dataInstance = NavMesh.AddNavMeshData(data);
                if (!dataInstance.valid) throw new InvalidOperationException("Unity could not register NavMeshData.");
                Status = "临时平地导航已建立 · " + SourceCount + " 个碰撞源（含 " + GeometryProxyCount + " 个可见几何保守代理）";
                reason = null;
                return true;
            }
            catch (Exception error)
            {
                Clear();
                return Fail("建立房间导航失败：" + error.Message, out reason);
            }
        }

        /// <summary>Vertical, local support projection only. Never snaps through a wall or to another floor.</summary>
        public bool TryProjectGround(Vector3 point, out Vector3 ground, out string reason)
            => ProjectGround(point, out ground, out reason, null);

        public bool TryProjectGround(Vector3 point, out Vector3 ground, out string reason,
            out ArdyRoomRouteDiagnostic observation)
        {
            observation = new ArdyRoomRouteDiagnostic { stage = "ground-projection", start = point, target = point };
            observation.success = ProjectGround(point, out ground, out reason, observation);
            return observation.success;
        }

        private bool ProjectGround(Vector3 point, out Vector3 ground, out string reason, ArdyRoomRouteDiagnostic observation)
        {
            ground = point;
            if (!IsBuilt) return ObserveFailure(observation, "navigation-unavailable", point, "房间导航尚未建立。", out reason);
            if (!Finite(point)) return ObserveFailure(observation, "invalid-coordinate", point, "地面点包含无效数值。", out reason);
            RefreshGeometryProxies();
            Physics.SyncTransforms();
            if (!Probe(point, out RaycastHit hit, out reason, observation)) return false;
            ground.y = hit.point.y;
            return true;
        }

        public bool TryPlan(Vector3 startGround, Vector3 targetGround, out ArdyRoomRoute route, out string reason)
            => TryPlan(startGround, targetGround, out route, out reason, out _);

        public bool TryPlan(Vector3 startGround, Vector3 targetGround, out ArdyRoomRoute route, out string reason,
            out ArdyRoomRouteDiagnostic observation)
        {
            route = null;
            observation = new ArdyRoomRouteDiagnostic { start = startGround, target = targetGround };
            if (!IsBuilt) return ObserveFailure(observation, "navigation-unavailable", startGround, "房间导航尚未建立。", out reason);
            if (!ValidSettings()) return ObserveFailure(observation, "invalid-settings", startGround, "导航几何参数无效。", out reason);
            if (!Finite(startGround) || !Finite(targetGround)) return ObserveFailure(observation, "invalid-coordinate", startGround, "起点或终点包含无效数值。", out reason);
            RefreshGeometryProxies();
            Physics.SyncTransforms();
            observation.stage = "start";
            if (!ProjectGround(startGround, out var start, out reason, observation)) return false;
            observation.stage = "target";
            if (!ProjectGround(targetGround, out var target, out reason, observation)) return false;
            if (Mathf.Abs(target.y - start.y) > MaxRouteHeightVariation)
                return ObserveFailure(observation, "different-plane", target, "目的地与起点不在本轮允许的同一平面；尚不支持台阶或跨楼层动作。", out reason);
            observation.stage = "start";
            if (!CheckPoint(start, start.y, out _, out _, out reason, observation)) return false;
            observation.stage = "target";
            if (!CheckPoint(target, start.y, out _, out _, out reason, observation)) return false;
            var filter = new NavMeshQueryFilter { agentTypeID = agentTypeId, areaMask = NavMesh.AllAreas };
            const float snap = .08f;
            observation.stage = "start";
            if (!NavMesh.SamplePosition(start, out var navStart, snap, filter) || !CloseProjection(start, navStart.position))
                return ObserveFailure(observation, "navmesh-boundary", start, "起点或终点不在可通行地面内；拒绝跨墙、跨层或大距离吸附。", out reason);
            observation.stage = "target";
            if (!NavMesh.SamplePosition(target, out var navTarget, snap, filter) || !CloseProjection(target, navTarget.position))
                return ObserveFailure(observation, "navmesh-boundary", target, "起点或终点不在可通行地面内；拒绝跨墙、跨层或大距离吸附。", out reason);
            observation.stage = "path";
            observation.from = start;
            observation.to = target;
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(navStart.position, navTarget.position, filter, path) ||
                path.status != NavMeshPathStatus.PathComplete || path.corners.Length == 0)
                return ObserveFailure(observation, "incomplete-navmesh-path", target, "没有到目的地的完整可通行路径。", out reason);
            var points = new List<Vector3> { start };
            foreach (var corner in path.corners)
            {
                if (!ProjectGround(corner, out var projected, out reason, observation)) return false;
                if (Vector3.Distance(points[points.Count - 1], projected) > .001f) points.Add(projected);
            }
            if (Vector3.Distance(points[points.Count - 1], target) > .001f) points.Add(target);
            float length = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                observation.segmentIndex = i - 1;
                observation.from = points[i - 1];
                observation.to = points[i];
                length += Vector3.Distance(points[i - 1], points[i]);
                if (length > MaxRouteLength) return ObserveFailure(observation, "route-length-limit", points[i], "路径超出本轮局部房间移动的长度上限。", out reason);
                if (!CheckSegment(points[i - 1], points[i], start.y, out _, out reason, observation)) return false;
            }
            routeGroundY = start.y;
            hasRoutePlane = true;
            route = new ArdyRoomRoute { Corners = points.ToArray(), GroundY = start.y, Length = length };
            Status = "平地完整路径已验证 · " + length.ToString("F2") + " 米";
            observation.success = true;
            observation.stage = "completed";
            reason = null;
            return true;
        }

        /// <summary>Call before each actual root displacement, including zero displacement while holding a pose.</summary>
        public bool ValidateStep(Vector3 previousGround, Vector3 nextGround, out float groundY, out string reason)
        {
            groundY = previousGround.y;
            if (!IsBuilt) return Fail("房间导航尚未建立。", out reason);
            if (!ValidSettings()) return Fail("导航几何参数无效。", out reason);
            if (!hasRoutePlane) return Fail("必须先成功规划完整路径，再执行逐步移动。", out reason);
            if (!Finite(previousGround) || !Finite(nextGround)) return Fail("执行坐标包含无效数值。", out reason);
            if (Vector3.Distance(previousGround, nextGround) > MaxGuardedStep)
                return Fail("单次位移过大；拒绝跳过逐步地形检查。", out reason);
            RefreshGeometryProxies();
            Physics.SyncTransforms();
            return CheckSegment(previousGround, nextGround, routeGroundY, out groundY, out reason);
        }

        public void Clear()
        {
            if (dataInstance.valid) dataInstance.Remove();
            dataInstance = default;
            if (data != null)
            {
                if (Application.isPlaying) Destroy(data); else DestroyImmediate(data);
                data = null;
            }
            // Agent type IDs are opaque signed integers; Unity can allocate a negative value.
            if (ownsAgentSettings) { NavMesh.RemoveSettings(agentTypeId); ownsAgentSettings = false; agentTypeId = -1; }
            if (proxyRoot != null)
            {
                proxyRoot.SetActive(false);
                if (Application.isPlaying) Destroy(proxyRoot); else DestroyImmediate(proxyRoot);
                proxyRoot = null;
            }
            geometryProxies.Clear();
            foreach (var mesh in ownedCoverMeshes)
                if (mesh != null)
                {
                    if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
                }
            ownedCoverMeshes.Clear();
            thinCovers.Clear();
            thinCoverDiagnostics.Clear();
            supportColliders.Clear();
            SourceCount = 0;
            hasRoutePlane = false;
            environmentRoot = avatarRoot = null;
            Status = "未建立房间地面导航";
        }

        private void OnDisable() { Clear(); }
        private void OnDestroy() { Clear(); }

        private void CreateGeometryProxies()
        {
            foreach (var renderer in environmentRoot.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (!renderer.enabled || IsExcluded(renderer.transform) || IsExcludedVisual(renderer.transform)) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                var authoredMesh = filter.sharedMesh;
                if (renderer.isPartOfStaticBatch &&
                    (GeometryReference == null || !GeometryReference.TryGetAuthoredMesh(renderer, out authoredMesh)))
                    throw new InvalidOperationException("静态合批物体缺少原始网格引用：" + renderer.name +
                        "。请先在编辑模式准备房间几何，再进入 Play；不能把整个合批网格当作该物体。 ");
                var bounds = renderer.bounds;
                if (!Finite(bounds.center) || !Finite(bounds.size) || bounds.size.sqrMagnitude < .00001f) continue;
                bool covered = false;
                foreach (var collider in supportColliders)
                {
                    // An existing solid source that encloses the mesh is already at least as conservative.
                    var solidBounds = collider.bounds;
                    solidBounds.Expand(.002f);
                    if (collider.gameObject == renderer.gameObject && collider is MeshCollider mesh && mesh.sharedMesh == authoredMesh)
                    { covered = true; break; }
                    if (!solidBounds.Contains(bounds.min) || !solidBounds.Contains(bounds.max)) continue;
                    if (!(collider is BoxCollider) && !(collider is SphereCollider) && !(collider is CapsuleCollider) &&
                        !(collider is MeshCollider convexMesh && convexMesh.convex)) continue;
                    bool inside = true;
                    for (int x = 0; x < 2 && inside; x++)
                        for (int y = 0; y < 2 && inside; y++)
                            for (int z = 0; z < 2 && inside; z++)
                            {
                                var corner = new Vector3(x == 0 ? bounds.min.x : bounds.max.x,
                                    y == 0 ? bounds.min.y : bounds.max.y, z == 0 ? bounds.min.z : bounds.max.z);
                                inside = (collider.ClosestPoint(corner) - corner).sqrMagnitude <= .000004f;
                            }
                    if (inside) { covered = true; break; }
                }
                if (covered) continue;
                if (proxyRoot == null)
                {
                    proxyRoot = new GameObject("ARDY temporary room geometry guards") { hideFlags = HideFlags.HideAndDontSave };
                    SceneManager.MoveGameObjectToScene(proxyRoot, environmentRoot.gameObject.scene);
                }
                if (TryCreateThinCover(renderer, authoredMesh)) continue;
                var item = new GameObject("GeometryGuard_" + renderer.name) { hideFlags = HideFlags.HideAndDontSave };
                item.transform.SetParent(proxyRoot.transform, false);
                var box = item.AddComponent<BoxCollider>();
                geometryProxies.Add(box, renderer);
            }
            RefreshGeometryProxies();
            Physics.SyncTransforms();
        }

        private bool TryCreateThinCover(MeshRenderer renderer, Mesh original)
        {
            var bounds = renderer.bounds;
            // A narrow threshold must not acquire a carpet exception. This is a broad planar covering,
            // not a semantic claim that a material is soft: no object-name whitelist is used.
            if (bounds.size.y > .0351f ||
                bounds.size.x < AgentRadius * 2f || bounds.size.z < AgentRadius * 2f) return false;
            bool ownMesh = original.isReadable;
            var matrix = renderer.transform.localToWorldMatrix;
            var scale = renderer.transform.lossyScale;
            // Unity can use a non-readable imported collision mesh with ordinary positive TRS and
            // default cooking. Sheared/negative cases need readable vertices for an exact world mesh.
            if (!ownMesh)
            {
                if (scale.x <= 0f || scale.y <= 0f || scale.z <= 0f)
                { thinCoverDiagnostics.Add(renderer.name + ": rejected non-readable mesh with non-positive scale " + scale); return false; }
                var reconstructed = Matrix4x4.TRS(renderer.transform.position, renderer.transform.rotation, scale);
                for (int i = 0; i < 16; i++) if (Mathf.Abs(matrix[i] - reconstructed[i]) > .0001f)
                { thinCoverDiagnostics.Add(renderer.name + ": rejected non-readable sheared transform, matrix difference " + Mathf.Abs(matrix[i] - reconstructed[i])); return false; }
            }
            var item = new GameObject("ThinSurfaceGuard_" + renderer.name) { hideFlags = HideFlags.HideAndDontSave };
            item.transform.SetParent(proxyRoot.transform, false);
            Mesh mesh = original;
            if (ownMesh)
            {
                mesh = new Mesh { name = "ARDY temporary world cover mesh", indexFormat = original.indexFormat };
                ownedCoverMeshes.Add(mesh);
                var vertices = original.vertices;
                for (int i = 0; i < vertices.Length; i++) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
                mesh.vertices = vertices;
                mesh.triangles = original.triangles;
                mesh.RecalculateBounds();
            }
            else
            {
                item.transform.position = renderer.transform.position;
                item.transform.rotation = renderer.transform.rotation;
                item.transform.localScale = scale;
            }
            var collider = item.AddComponent<MeshCollider>();
            collider.sharedMesh = mesh;
            Physics.SyncTransforms();
            int countX = Mathf.CeilToInt(bounds.size.x / .20f), countZ = Mathf.CeilToInt(bounds.size.z / .20f);
            bool valid = countX * countZ <= 4096;
            int supported = 0;
            float maximumSlope = 0f;
            string rejectedAt = valid ? null : "sampling grid exceeds 4096";
            for (int x = 0; x <= countX && valid; x++)
                for (int z = 0; z <= countZ && valid; z++)
                {
                    var from = new Vector3(Mathf.Lerp(bounds.min.x, bounds.max.x, (x + .25f) / (countX + .5f)),
                        bounds.max.y + .03f, Mathf.Lerp(bounds.min.z, bounds.max.z, (z + .25f) / (countZ + .5f)));
                    if (!collider.Raycast(new Ray(from, Vector3.down), out var hit, bounds.size.y + .06f)) continue;
                    supported++;
                    maximumSlope = Mathf.Max(maximumSlope, Vector3.Angle(hit.normal, Vector3.up));
                    // A tiny bevel must not turn an otherwise flat covering into a room-sized solid box.
                    // Its height and real underlying support are still checked here; Probe applies the
                    // ordinary slope limit to the original supporting floor along the actual path.
                    valid = UnderlyingFloor(hit.point, out _);
                    if (!valid) rejectedAt = "no original floor within " + MaxThinCoverHeight.ToString("F3") + " m below " + hit.point.ToString("F4");
                }
            if (valid && supported >= 4)
            {
                thinCovers.Add(collider, new ThinCover { renderer = renderer, matrix = matrix, original = original,
                    worldMesh = mesh, filterMeshAtCapture = renderer.GetComponent<MeshFilter>().sharedMesh });
                thinCoverDiagnostics.Add(renderer.name + ": accepted real " + (ownMesh ? "world mesh" : "shared non-readable mesh") +
                    ", supported samples=" + supported + ", max sampled surface slope=" + maximumSlope.ToString("F2") + " degrees");
                return true;
            }
            thinCoverDiagnostics.Add(renderer.name + ": rejected " + (rejectedAt ?? "fewer than four real top-surface ray hits") +
                ", supported samples=" + supported + ", max sampled surface slope=" + maximumSlope.ToString("F2") + " degrees");
            item.SetActive(false);
            if (Application.isPlaying) Destroy(item); else DestroyImmediate(item);
            if (ownMesh)
            {
                if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh);
                ownedCoverMeshes.Remove(mesh);
            }
            return false;
        }

        private bool UnderlyingFloor(Vector3 surface, out float floorY)
        {
            floorY = 0f;
            float nearest = float.MaxValue;
            bool found = false;
            foreach (var hit in Physics.RaycastAll(surface + Vector3.up * .005f, Vector3.down,
                MaxThinCoverHeight + .011f, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!supportColliders.Contains(hit.collider) || !IsEnvironmentCollider(hit.collider)) continue;
                float height = surface.y - hit.point.y;
                if (height < -.001f || height > MaxThinCoverHeight + .0005f ||
                    Vector3.Angle(hit.normal, Vector3.up) > MaxGroundSlope + .05f) continue;
                if (hit.distance < nearest) { nearest = hit.distance; floorY = hit.point.y; found = true; }
            }
            return found;
        }

        private void RefreshGeometryProxies()
        {
            foreach (var pair in geometryProxies)
            {
                var box = pair.Key;
                var renderer = pair.Value;
                if (box == null) continue;
                box.enabled = renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy;
                if (!box.enabled) continue;
                var bounds = renderer.bounds;
                box.transform.position = bounds.center;
                box.size = new Vector3(Mathf.Max(.005f, bounds.size.x), Mathf.Max(.005f, bounds.size.y), Mathf.Max(.005f, bounds.size.z));
            }
            foreach (var pair in thinCovers)
            {
                var cover = pair.Value;
                if (pair.Key == null) continue;
                var renderer = cover.renderer;
                bool changed = renderer != null && (renderer.transform.localToWorldMatrix != cover.matrix ||
                    renderer.GetComponent<MeshFilter>() == null || renderer.GetComponent<MeshFilter>().sharedMesh != cover.filterMeshAtCapture ||
                    (renderer.isPartOfStaticBatch && (GeometryReference == null ||
                        !GeometryReference.TryGetAuthoredMesh(renderer, out var currentAuthoredMesh) || currentAuthoredMesh != cover.original)));
                if (changed && !cover.invalidated)
                {
                    // A moved cover has not had its new support verified. Keep it as an obstacle until rebaked.
                    cover.invalidated = true;
                    var item = new GameObject("ChangedThinSurfaceGuard_" + renderer.name) { hideFlags = HideFlags.HideAndDontSave };
                    item.transform.SetParent(proxyRoot.transform, false);
                    var box = item.AddComponent<BoxCollider>();
                    box.transform.position = renderer.bounds.center;
                    box.size = renderer.bounds.size;
                    geometryProxies.Add(box, renderer);
                }
                pair.Key.enabled = !cover.invalidated && renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy;
            }
        }

        private bool CheckSegment(Vector3 from, Vector3 to, float baseline, out float groundY, out string reason,
            ArdyRoomRouteDiagnostic observation = null)
        {
            groundY = from.y;
            float distance = Vector3.Distance(from, to);
            int count = Mathf.Max(1, Mathf.CeilToInt(distance / SampleSpacing));
            if (count > 4096) return ObserveFailure(observation, "terrain-sample-limit", from, "需要检查的地形样本过多。", out reason);
            if (!CheckPoint(from, baseline, out var previousY, out var previousCover, out reason, observation)) return false;
            var last = new Vector3(from.x, previousY, from.z);
            for (int i = 1; i <= count; i++)
            {
                var point = Vector3.Lerp(from, to, (float)i / count);
                if (!CheckPoint(point, baseline, out groundY, out var nextCover, out reason, observation)) return false;
                float allowedChange = previousCover || nextCover ? Mathf.Max(MaxLocalGroundChange, MaxThinCoverHeight) : MaxLocalGroundChange;
                if (Mathf.Abs(groundY - previousY) > allowedChange + .0005f)
                    return ObserveFailure(observation, "local-height-change", point, "路径存在台阶或局部地面高度突变。", out reason);
                point.y = groundY;
                var displacement = point - last;
                float step = displacement.magnitude;
                if (step > .00001f)
                {
                    Capsule(last, out var bottom, out var top);
                    foreach (var hit in Physics.CapsuleCastAll(bottom, top, AgentRadius, displacement / step,
                        step, ~0, QueryTriggerInteraction.Ignore))
                        if (IsBlockingCollider(hit.collider)) return ObserveFailure(observation, "body-sweep-blocked", hit.point,
                            "移动中的身体胶囊碰到障碍：" + hit.collider.name, out reason, hit.collider);
                }
                last = point;
                previousY = groundY;
                previousCover = nextCover;
            }
            reason = null;
            return true;
        }

        private bool CheckPoint(Vector3 point, float baseline, out float groundY, out string reason)
            => CheckPoint(point, baseline, out groundY, out _, out reason);

        private bool CheckPoint(Vector3 point, float baseline, out float groundY, out bool touchesCover, out string reason,
            ArdyRoomRouteDiagnostic observation = null)
        {
            groundY = point.y;
            touchesCover = false;
            float minimum = float.MaxValue, maximum = float.MinValue;
            float originalMinimum = float.MaxValue, originalMaximum = float.MinValue;
            foreach (var offset in Footprint)
            {
                var sample = point + new Vector3(offset.x, 0f, offset.y) * AgentRadius;
                if (!Probe(sample, out var hit, out reason, observation)) return false;
                if (offset == Vector2.zero) groundY = hit.point.y;
                minimum = Mathf.Min(minimum, hit.point.y);
                maximum = Mathf.Max(maximum, hit.point.y);
                float originalY = hit.point.y;
                if (hit.collider is MeshCollider surfaceMesh && thinCovers.ContainsKey(surfaceMesh))
                {
                    touchesCover = true;
                    if (!UnderlyingFloor(hit.point, out originalY))
                        return ObserveFailure(observation, "thin-cover-support-lost", sample, "薄覆盖物下方的原始地板支撑已消失或高差过大。", out reason, hit.collider);
                }
                originalMinimum = Mathf.Min(originalMinimum, originalY);
                originalMaximum = Mathf.Max(originalMaximum, originalY);
                if (Mathf.Abs(hit.point.y - baseline) > MaxRouteHeightVariation)
                    return ObserveFailure(observation, "route-plane-departure", sample, "地形离开已验证平面；拒绝坡道、台阶或落差。", out reason, hit.collider);
            }
            float surfaceTolerance = touchesCover ? Mathf.Max(MaxLocalGroundChange, MaxThinCoverHeight) : MaxLocalGroundChange;
            if (maximum - minimum > surfaceTolerance + .0005f || originalMaximum - originalMinimum > MaxLocalGroundChange + .0005f)
                return ObserveFailure(observation, "footprint-height-change", point, "身体支撑范围内存在台阶、地面断层或过大高差。", out reason);
            point.y = groundY;
            Capsule(point, out var bottom, out var top);
            foreach (var collider in Physics.OverlapCapsule(bottom, top, AgentRadius, ~0, QueryTriggerInteraction.Ignore))
                if (IsBlockingCollider(collider)) return ObserveFailure(observation, "body-overlap", point,
                    "身体或头顶空间被阻挡：" + collider.name, out reason, collider);
            reason = null;
            return true;
        }

        private bool Probe(Vector3 point, out RaycastHit support, out string reason, ArdyRoomRouteDiagnostic observation = null)
        {
            support = default;
            const float reach = .12f;
            bool found = false;
            float nearest = float.MaxValue;
            // Look only very near the requested elevation. Raycasts never search down to another story.
            foreach (var hit in Physics.RaycastAll(point + Vector3.up * reach, Vector3.down,
                reach * 2f, ~0, QueryTriggerInteraction.Ignore))
            {
                bool original = supportColliders.Contains(hit.collider) && IsEnvironmentCollider(hit.collider);
                bool cover = hit.collider is MeshCollider hitMesh && thinCovers.TryGetValue(hitMesh, out var thinCover) && !thinCover.invalidated && hit.collider.enabled;
                if (!original && !cover) continue;
                if (hit.distance < nearest) { nearest = hit.distance; support = hit; found = true; }
            }
            if (!found) return ObserveFailure(observation, "support-missing", point, "脚下没有已登记环境地面的连续支撑（可能是边缘、落差或楼层不符）。", out reason);
            bool supportedThinCover = support.collider is MeshCollider supportMesh && thinCovers.ContainsKey(supportMesh);
            if (supportedThinCover && !UnderlyingFloor(support.point, out _))
                return ObserveFailure(observation, "thin-cover-support-missing", point, "薄覆盖物下方没有连续的原始地板支撑。", out reason, support.collider);
            if (Mathf.Abs(support.point.y - point.y) > .10f)
                return ObserveFailure(observation, "support-height-offset", point, "附近支撑地面的高度偏差过大。", out reason, support.collider);
            // A <= 3 cm verified covering can have a short bevel steeper than the underlying floor.
            // UnderlyingFloor enforces the ordinary slope limit on that real supporting floor; the
            // cover still has exact mesh support, bounded height and per-footprint height checks.
            if (!supportedThinCover && Vector3.Angle(support.normal, Vector3.up) > MaxGroundSlope + .05f)
                return ObserveFailure(observation, "ground-slope", point, "地面坡度超出当前平地步态的允许范围。", out reason, support.collider);
            reason = null;
            return true;
        }

        private void Capsule(Vector3 ground, out Vector3 bottom, out Vector3 top)
        {
            bottom = ground + Vector3.up * (AgentRadius + FloorClearance);
            top = ground + Vector3.up * (AgentHeight - AgentRadius);
        }
        private bool IsEnvironmentCollider(Collider collider)
        {
            if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger ||
                !collider.transform.IsChildOf(environmentRoot) || IsExcluded(collider.transform)) return false;
            return true;
        }
        private bool IsExcluded(Transform candidate)
        {
            if (candidate.IsChildOf(avatarRoot)) return true;
            if (IgnoredRoots != null)
                foreach (var ignored in IgnoredRoots)
                    if (ignored != null && candidate.IsChildOf(ignored)) return true;
            return false;
        }
        private bool IsExcludedVisual(Transform candidate)
        {
            int waterLayer = LayerMask.NameToLayer("Water");
            if (ExcludeWaterLayerVisuals && waterLayer >= 0 && candidate.gameObject.layer == waterLayer) return true;
            if (ExcludedVisualRoots != null)
                foreach (var excluded in ExcludedVisualRoots)
                    if (excluded != null && candidate.IsChildOf(excluded)) return true;
            return false;
        }
        private bool IsBlockingCollider(Collider collider)
        {
            return collider != null && collider.enabled && !collider.isTrigger && collider.gameObject.activeInHierarchy &&
                (avatarRoot == null || !collider.transform.IsChildOf(avatarRoot)) &&
                !(collider is MeshCollider mesh && thinCovers.ContainsKey(mesh));
        }
        private bool CloseProjection(Vector3 original, Vector3 projected)
        {
            // NavMesh rasterization may lift a perfectly flat floor by a voxel. Actual ground support
            // and the route elevation are checked independently with Physics before/after this query.
            return Mathf.Abs(original.y - projected.y) <= .08f &&
                Vector2.Distance(new Vector2(original.x, original.z), new Vector2(projected.x, projected.z)) <= .05f;
        }
        private bool ValidSettings()
        {
            return Finite(AgentRadius) && Finite(AgentHeight) && Finite(MaxGroundSlope) && Finite(MaxLocalGroundChange) &&
                Finite(MaxRouteHeightVariation) && Finite(SampleSpacing) && Finite(FloorClearance) &&
                Finite(MaxGuardedStep) && Finite(MaxRouteLength) && Finite(PlanningClearance) && PlanningClearance >= 0f &&
                PlanningClearance <= .15f && Finite(MaxThinCoverHeight) && MaxThinCoverHeight > 0f &&
                MaxThinCoverHeight <= .03f && AgentRadius >= .05f &&
                AgentHeight >= AgentRadius * 2f + FloorClearance && MaxGroundSlope >= 0f && MaxGroundSlope <= 10f &&
                MaxLocalGroundChange > 0f && MaxLocalGroundChange <= .10f && MaxRouteHeightVariation > 0f &&
                MaxRouteHeightVariation <= .10f && SampleSpacing >= .01f && SampleSpacing <= .15f &&
                FloorClearance >= .005f && FloorClearance < MaxLocalGroundChange && MaxGuardedStep > 0f && MaxRouteLength > 0f;
        }
        private bool ObserveFailure(ArdyRoomRouteDiagnostic observation, string code, Vector3 sample,
            string text, out string reason, Collider collider = null)
        {
            if (observation != null)
            {
                observation.success = false;
                observation.code = code;
                observation.reason = text;
                observation.hasSample = Finite(sample);
                observation.sample = observation.hasSample ? sample : default;
                if (collider != null)
                {
                    observation.colliderId = collider.GetInstanceID();
                    observation.colliderName = collider.name;
                    observation.colliderPath = ObjectPath(collider.transform);
                    observation.colliderType = collider.GetType().Name;
                    if (collider is BoxCollider box && geometryProxies.TryGetValue(box, out var renderer) && renderer != null)
                        observation.sourceObjectPath = ObjectPath(renderer.transform);
                    else if (collider is MeshCollider mesh && thinCovers.TryGetValue(mesh, out var cover) && cover.renderer != null)
                        observation.sourceObjectPath = ObjectPath(cover.renderer.transform);
                    else observation.sourceObjectPath = observation.colliderPath;
                }
            }
            return Fail(text, out reason);
        }
        private static string ObjectPath(Transform transform)
        {
            string path = transform.name;
            for (var parent = transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }
        private bool Fail(string text, out string reason) { Status = text; reason = text; return false; }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z);
    }
}
