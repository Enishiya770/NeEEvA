using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>
/// Geometry-only inspection of a copied environment scene. It never enters Play mode,
/// reads chat components, edits assets, or saves the inspected scene. Run in the isolated
/// validation project with -executeMethod ArdyRoomTerrainAudit.RunBatch.
/// </summary>
public static class ArdyRoomTerrainAudit
{
    private const string DefaultScene = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day.unity";

    [Serializable] private sealed class Report
    {
        public string status = "running", error, scene, unityVersion, generatedUtc;
        public string scope = "Imported environment collider geometry only. Surface hits and missing-own-collider renderer candidates do not establish traversability, complete collision coverage, foot contact, or stairs support.";
        public string mapLegend = "XZ top view, +X right, +Z up. Navy=no hit within vertical band; blue=lower height; yellow=higher height; red=surface steeper than 20 degrees. White marker=reference chat rig XZ. Furniture tops are included; this is not a navigation map.";
        public string heightMapPng;
        public int gameObjects, missingScripts, objectsWithMissingPrefabAsset, missingColliderMeshes, missingRendererMeshes, activeSolidColliders;
        public Vector3 boundsCenter, boundsSize;
        public Vector3 referenceChatRig = new Vector3(-2.9f, -0.72f, -4.6f);
        public List<string> assetDependencies = new List<string>();
        public List<ColliderEntry> colliders = new List<ColliderEntry>();
        public List<RendererEntry> renderersWithoutOwnCollider = new List<RendererEntry>();
        public Grid grid;
    }

    [Serializable] private sealed class ColliderEntry
    {
        public int id;
        public string path, type, meshAsset, meshName;
        public bool enabled, active, trigger, convex, missingMesh;
        public Vector3 position, scale, center, size;
        public Quaternion rotation;
        public float sampledMinimumUpwardHeight, sampledMaximumUpwardHeight;
        public int upwardHitSamples;
        public List<Vector3> upwardSamples = new List<Vector3>();
    }

    [Serializable] private sealed class RendererEntry
    {
        public string path;
        public Vector3 center, size;
        public bool ancestorHasCollider, descendantHasCollider;
    }

    [Serializable] private sealed class Grid
    {
        public float minX, maxX, minZ, maxZ, rayTop, rayBottom;
        public int width, height;
        public float cellSizeX, cellSizeZ;
        public string indexing = "index = z * width + x; sample at cell centre; colliderId -1 means no hit; slope is degrees from world up; no-hit height uses rayBottom.";
        public float[] surfaceY, slopeDegrees;
        public int[] colliderId;
    }

    public static void RunBatch()
    {
        string scenePath = Environment.GetEnvironmentVariable("ARDY_ROOM_AUDIT_SCENE") ?? DefaultScene;
        if (scenePath.IndexOf("_Chat", StringComparison.OrdinalIgnoreCase) >= 0)
            throw new InvalidOperationException("Use the pure environment scene, not a chat scene.");
        string output = Environment.GetEnvironmentVariable("ARDY_ROOM_AUDIT_OUTPUT") ??
            Path.GetFullPath("ArdyRoomTerrainAudit");
        Directory.CreateDirectory(output);
        var report = new Report { scene = scenePath, unityVersion = Application.unityVersion,
            generatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
        Scene scene = default;
        Scene previousActiveScene = SceneManager.GetActiveScene();
        int code = 0;
        try
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
                throw new FileNotFoundException("Copy the pure environment scene and its geometry dependencies first.", scenePath);
            report.assetDependencies = AssetDatabase.GetDependencies(scenePath, true)
                .OrderBy(path => path, StringComparer.Ordinal).ToList();
            if (SceneManager.GetSceneByPath(scenePath).isLoaded)
                throw new InvalidOperationException("The audit requires an unopened copied environment scene in the isolated project.");
            // Additive read-only loading preserves all existing scenes. Closing below never saves.
            scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            var transforms = scene.GetRootGameObjects().SelectMany(root =>
                root.GetComponentsInChildren<Transform>(true)).ToArray();
            report.gameObjects = transforms.Length;
            report.missingScripts = transforms.Sum(t =>
                GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject));
            report.objectsWithMissingPrefabAsset = transforms.Count(t =>
                PrefabUtility.GetPrefabInstanceStatus(t.gameObject) == PrefabInstanceStatus.MissingAsset);
            var colliders = transforms.SelectMany(t => t.GetComponents<Collider>())
                .OrderBy(c => HierarchyPath(c.transform), StringComparer.Ordinal).ToArray();
            Physics.SyncTransforms();
            Bounds combined = default;
            bool hasBounds = false;
            for (int i = 0; i < colliders.Length; ++i)
            {
                Collider collider = colliders[i];
                var mesh = collider as MeshCollider;
                var entry = new ColliderEntry {
                    id = i, path = HierarchyPath(collider.transform), type = collider.GetType().Name,
                    enabled = collider.enabled, active = collider.gameObject.activeInHierarchy,
                    trigger = collider.isTrigger, position = collider.transform.position,
                    scale = collider.transform.lossyScale, rotation = collider.transform.rotation,
                    center = collider.bounds.center, size = collider.bounds.size,
                    missingMesh = mesh != null && mesh.sharedMesh == null,
                    convex = mesh != null && mesh.convex,
                    meshName = mesh != null && mesh.sharedMesh != null ? mesh.sharedMesh.name : null,
                    meshAsset = mesh != null && mesh.sharedMesh != null ? AssetDatabase.GetAssetPath(mesh.sharedMesh) : null
                };
                if (entry.missingMesh) report.missingColliderMeshes++;
                if (collider.enabled && collider.gameObject.activeInHierarchy && !collider.isTrigger)
                {
                    report.activeSolidColliders++;
                    if (!hasBounds) { combined = collider.bounds; hasBounds = true; }
                    else combined.Encapsulate(collider.bounds);
                    SampleCollider(collider, entry);
                }
                report.colliders.Add(entry);
            }
            report.boundsCenter = combined.center;
            report.boundsSize = combined.size;
            foreach (var renderer in transforms.SelectMany(t => t.GetComponents<MeshRenderer>()))
            {
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) report.missingRendererMeshes++;
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.GetComponent<Collider>() != null) continue;
                report.renderersWithoutOwnCollider.Add(new RendererEntry {
                    path = HierarchyPath(renderer.transform), center = renderer.bounds.center, size = renderer.bounds.size,
                    ancestorHasCollider = renderer.transform.parent != null && renderer.transform.parent.GetComponentInParent<Collider>() != null,
                    descendantHasCollider = renderer.GetComponentInChildren<Collider>(true) != null
                });
            }
            report.grid = BuildGrid(colliders);
            report.heightMapPng = Path.Combine(output, "terrain-height-band.png");
            WriteHeightMap(report.grid, report.referenceChatRig, report.heightMapPng);
            report.status = report.missingColliderMeshes == 0 && report.missingRendererMeshes == 0 &&
                report.objectsWithMissingPrefabAsset == 0 ? "complete" : "incomplete_geometry_dependencies";
            if (report.status != "complete") code = 2;
        }
        catch (Exception exception)
        {
            report.status = "failed";
            report.error = exception.ToString();
            code = 1;
        }
        finally
        {
            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                SceneManager.SetActiveScene(previousActiveScene);
            if (scene.IsValid()) EditorSceneManager.CloseScene(scene, true);
            File.WriteAllText(Path.Combine(output, "report.json"), JsonUtility.ToJson(report, true));
        }
        Debug.Log("[ArdyRoomTerrainAudit] " + report.status + "; colliders=" + report.activeSolidColliders +
            "; missing meshes=" + report.missingColliderMeshes + "/" + report.missingRendererMeshes + "; output=" + output);
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }

    private static void SampleCollider(Collider collider, ColliderEntry entry)
    {
        Bounds bounds = collider.bounds;
        entry.sampledMinimumUpwardHeight = bounds.min.y;
        entry.sampledMaximumUpwardHeight = bounds.min.y;
        // Multiple ray heights retain lower surfaces of a combined multi-level mesh,
        // rather than confusing its topmost roof/table surface with the room floor.
        for (int ix = 0; ix < 7; ++ix)
        for (int iz = 0; iz < 7; ++iz)
        for (int level = 0; level < 7; ++level)
        {
            float x = Mathf.Lerp(bounds.min.x, bounds.max.x, (ix + 0.5f) / 7f);
            float z = Mathf.Lerp(bounds.min.z, bounds.max.z, (iz + 0.5f) / 7f);
            float top = Mathf.Lerp(bounds.min.y + 0.02f, bounds.max.y + 0.05f, (level + 1f) / 7f);
            if (!collider.Raycast(new Ray(new Vector3(x, top, z), Vector3.down), out RaycastHit hit,
                Mathf.Max(0.1f, top - bounds.min.y + 0.05f)) || hit.normal.y < 0.8f) continue;
            if (entry.upwardSamples.Any(point => (point - hit.point).sqrMagnitude < 0.000001f)) continue;
            entry.upwardSamples.Add(hit.point);
            if (entry.upwardHitSamples++ == 0)
                entry.sampledMinimumUpwardHeight = entry.sampledMaximumUpwardHeight = hit.point.y;
            else
            {
                entry.sampledMinimumUpwardHeight = Mathf.Min(entry.sampledMinimumUpwardHeight, hit.point.y);
                entry.sampledMaximumUpwardHeight = Mathf.Max(entry.sampledMaximumUpwardHeight, hit.point.y);
            }
        }
    }

    private static Grid BuildGrid(Collider[] colliders)
    {
        var grid = new Grid {
            minX = Setting("MIN_X", -11f), maxX = Setting("MAX_X", 5f),
            minZ = Setting("MIN_Z", -12f), maxZ = Setting("MAX_Z", 4f),
            rayTop = Setting("TOP_Y", 0.4f), rayBottom = Setting("BOTTOM_Y", -2f),
            width = 240, height = 240
        };
        if (grid.maxX <= grid.minX || grid.maxZ <= grid.minZ || grid.rayTop <= grid.rayBottom)
            throw new InvalidOperationException("Invalid audit grid bounds.");
        grid.cellSizeX = (grid.maxX - grid.minX) / grid.width;
        grid.cellSizeZ = (grid.maxZ - grid.minZ) / grid.height;
        int count = grid.width * grid.height;
        grid.surfaceY = new float[count]; grid.slopeDegrees = new float[count]; grid.colliderId = new int[count];
        Bounds[] bounds = colliders.Select(c => c.bounds).ToArray();
        for (int z = 0; z < grid.height; ++z)
        for (int x = 0; x < grid.width; ++x)
        {
            int index = z * grid.width + x;
            Vector3 origin = new Vector3(grid.minX + (x + 0.5f) * grid.cellSizeX,
                grid.rayTop, grid.minZ + (z + 0.5f) * grid.cellSizeZ);
            float distance = grid.rayTop - grid.rayBottom;
            grid.colliderId[index] = -1; grid.surfaceY[index] = grid.rayBottom;
            for (int i = 0; i < colliders.Length; ++i)
            {
                Collider collider = colliders[i]; Bounds b = bounds[i];
                if (!collider.enabled || !collider.gameObject.activeInHierarchy || collider.isTrigger ||
                    origin.x < b.min.x || origin.x > b.max.x || origin.z < b.min.z || origin.z > b.max.z ||
                    b.min.y > grid.rayTop || b.max.y < grid.rayBottom) continue;
                if (!collider.Raycast(new Ray(origin, Vector3.down), out RaycastHit hit, distance)) continue;
                distance = hit.distance;
                grid.colliderId[index] = i; grid.surfaceY[index] = hit.point.y;
                grid.slopeDegrees[index] = Vector3.Angle(Vector3.up, hit.normal);
            }
        }
        return grid;
    }

    private static void WriteHeightMap(Grid grid, Vector3 marker, string output)
    {
        const int scale = 4;
        var image = new Texture2D(grid.width * scale, grid.height * scale, TextureFormat.RGB24, false);
        try
        {
            var pixels = new Color32[image.width * image.height];
            for (int z = 0; z < grid.height; ++z)
            for (int x = 0; x < grid.width; ++x)
            {
                int index = z * grid.width + x;
                Color colour = new Color(0.04f, 0.06f, 0.12f);
                if (grid.colliderId[index] >= 0)
                    colour = grid.slopeDegrees[index] > 20f ? new Color(0.9f, 0.25f, 0.16f) :
                        Color.Lerp(new Color(0.1f, 0.35f, 0.75f), new Color(1f, 0.82f, 0.27f),
                            Mathf.InverseLerp(grid.rayBottom, grid.rayTop, grid.surfaceY[index]));
                float wx = grid.minX + (x + 0.5f) * grid.cellSizeX;
                float wz = grid.minZ + (z + 0.5f) * grid.cellSizeZ;
                if ((new Vector2(wx - marker.x, wz - marker.z)).sqrMagnitude < 0.12f * 0.12f) colour = Color.white;
                for (int dy = 0; dy < scale; ++dy)
                for (int dx = 0; dx < scale; ++dx)
                    pixels[(z * scale + dy) * image.width + x * scale + dx] = colour;
            }
            image.SetPixels32(pixels); image.Apply(false, false);
            File.WriteAllBytes(output, image.EncodeToPNG());
        }
        finally { Object.DestroyImmediate(image); }
    }

    private static float Setting(string name, float fallback)
    {
        string text = Environment.GetEnvironmentVariable("ARDY_ROOM_AUDIT_" + name);
        return string.IsNullOrEmpty(text) ? fallback : float.Parse(text, CultureInfo.InvariantCulture);
    }

    private static string HierarchyPath(Transform transform)
    {
        var names = new List<string>();
        for (Transform current = transform; current != null; current = current.parent) names.Add(current.name);
        names.Reverse(); return string.Join("/", names);
    }
}
