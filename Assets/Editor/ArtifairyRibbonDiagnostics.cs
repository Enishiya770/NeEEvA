#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Read-only mesh/skin/solver diagnostics; no chat, audio or Play Mode callbacks.</summary>
public static class ArtifairyRibbonDiagnostics
{
    private const string Output = "E:/Artifairy/.vrm_work/ribbon_fix";
    private const string SourceGeometry = "E:/Artifairy/.vrm_work/physics_geometry_probe.json";
    private const string RendererName = "袖口饰品_VRM";
    private sealed class Loop
    {
        public string name;
        public Vector3[] source;
        public int[] indices;
        public int[][] edges;
        public Vector3[] neutral;
    }
    private static readonly Dictionary<string, object> Result = new Dictionary<string, object>();

    /// <summary>Verify the published material fix and render saved/idle clones, without saving scene/assets.</summary>
    public static void RunFixedVisuals()
    {
        int exit = 1;
        var report = new Dictionary<string, object>();
        var protectedFiles = new[] { "Assets/Private/Artifairy/Scenes/Artifairy_Chat.unity", "Assets/Private/Artifairy/Scenes/Artifairy_RenderChat.unity",
            "Assets/Model/Artifairy/Artifairy_Compatible.vrm", "Assets/Model/Artifairy/Artifairy_Compatible.prefab",
            "Assets/Model/Artifairy/Artifairy_RenderCompatible.vrm", "Assets/Model/Artifairy/Artifairy_RenderCompatible.prefab" };
        var hashes = protectedFiles.ToDictionary(p => p, Hash);
        Directory.CreateDirectory(Output);
        try
        {
            Require(!Application.isPlaying && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "Final ribbon visuals require Edit Mode and a graphics device.");
            report["graphicsDevice"] = SystemInfo.graphicsDeviceType.ToString();
            report["scope"] = "Published material validation; saved and idle pose clones only; no spring or chat callbacks.";
            var models = new Dictionary<string, object>(); report["models"] = models;
            foreach (string stem in new[] { "Artifairy_Compatible", "Artifairy_RenderCompatible" })
            {
                bool renderVariant = stem.Contains("Render");
                var saved = EditorSceneManager.OpenScene("Assets/Private/Artifairy/Scenes/" + (renderVariant ? "Artifairy_RenderChat" : "Artifairy_Chat") + ".unity", OpenSceneMode.Single);
                var avatar = saved.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Vrm10Instance>(true)).Single();
                var existingRenderers = avatar.GetComponentsInChildren<Renderer>(true);
                var rendererEnabled = existingRenderers.ToDictionary(r => r, r => r.enabled);
                var originalLayers = new HashSet<int>(existingRenderers.Select(r => r.gameObject.layer));
                var sceneLights = saved.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Light>(true)).Where(l => l.enabled && l.gameObject.activeInHierarchy).ToArray();
                var lightMasks = sceneLights.ToDictionary(l => l, l => l.cullingMask);
                var cases = new Dictionary<string, object>(); models[stem] = new
                {
                    sceneLights = sceneLights.Select(l => new { l.type, l.intensity, color = new[] { l.color.r, l.color.g, l.color.b }, l.range, l.shadows }).ToArray(),
                    ambientMode = RenderSettings.ambientMode.ToString(), ambientIntensity = RenderSettings.ambientIntensity,
                    cases
                };
                try
                {
                    foreach (string pose in new[] { "savedPose", "idlePose" })
                    foreach (string lighting in new[] { "originalScene", "clearPreview" })
                    {
                        bool reduced = lighting == "clearPreview";
                        var scene = reduced ? EditorSceneManager.NewPreviewScene() : saved;
                        var root = Object.Instantiate(avatar.gameObject); SceneManager.MoveGameObjectToScene(root, scene);
                        var owned = new List<Object>();
                        try
                        {
                            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 30;
                            var vrm = root.GetComponent<Vrm10Instance>(); vrm.enabled = false; vrm.UpdateType = Vrm10Instance.UpdateTypes.None;
                            root.GetComponent<ArtifairySpringBoneDriver>().enabled = false;
                            // This exact getter is intentionally reflected because its return assembly is not autoReferenced.
                            typeof(Vrm10Instance).GetProperty("DefaultTransformStates").GetValue(vrm);
                            var animator = root.GetComponent<Animator>();
                            if (pose == "idlePose") { animator.enabled = true; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.Rebind(); animator.SetInteger("state", 0); for (int t = 0; t < 90; t++) animator.Update(1f / 60f); }
                            animator.enabled = false;
                            var accessory = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == RendererName);
                            cases[pose + "/" + lighting] = ValidatePublishedLoops(root, accessory, renderVariant);
                            var before = Bake(accessory, root.transform);
                            if (reduced)
                            {
                                var copies = new Dictionary<Material, Material>();
                                foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                                    renderer.sharedMaterials = renderer.sharedMaterials.Select(original =>
                                    {
                                        if (!copies.TryGetValue(original, out var copy))
                                        {
                                            copy = new Material(original) { name = original.name + " [clear preview]", hideFlags = HideFlags.DontSave };
                                            copies.Add(original, copy); owned.Add(copy);
                                            if (copy.HasProperty("_EmissionColor")) copy.SetColor("_EmissionColor", copy.GetColor("_EmissionColor") * 0.05f);
                                            if (copy.HasProperty("_RimColor")) copy.SetColor("_RimColor", copy.GetColor("_RimColor") * 0.1f);
                                            if (copy.HasProperty("_MatcapColor")) copy.SetColor("_MatcapColor", copy.GetColor("_MatcapColor") * 0.1f);
                                        }
                                        return copy;
                                    }).ToArray();
                            }
                            else
                            {
                                // Hide the source avatar while the clone shares its actual room illumination/shadows.
                                foreach (var renderer in existingRenderers) renderer.enabled = false;
                                foreach (var light in sceneLights)
                                    if (originalLayers.Any(layer => (lightMasks[light] & (1 << layer)) != 0)) light.cullingMask = lightMasks[light] | (1 << 30);
                            }
                            var after = Bake(accessory, root.transform);
                            Require(before.Length == after.Length && before.Select((p, i) => Vector3.Distance(p, after[i])).Max() < 0.000001f, "Lighting preview changed loop geometry.");
                            Render(root, scene, stem + "_fixed_" + pose + "_" + lighting, reduced ? 0.18f : 0, true, reduced);
                        }
                        finally
                        {
                            Object.DestroyImmediate(root); foreach (var obj in owned) Object.DestroyImmediate(obj);
                            foreach (var renderer in rendererEnabled) renderer.Key.enabled = renderer.Value;
                            foreach (var light in lightMasks) light.Key.cullingMask = light.Value;
                            if (reduced) EditorSceneManager.ClosePreviewScene(scene);
                        }
                    }
                }
                finally
                {
                    foreach (var renderer in rendererEnabled) renderer.Key.enabled = renderer.Value;
                    foreach (var light in lightMasks) light.Key.cullingMask = light.Value;
                }
            }
            foreach (var file in hashes) Require(Hash(file.Key) == file.Value, "Read-only visual check changed an asset: " + file.Key);
            report["protectedAssetsUnchanged"] = true; report["pass"] = true; exit = 0;
        }
        catch (Exception error) { report["pass"] = false; report["error"] = error.GetBaseException().Message; Debug.LogException(error); }
        finally { File.WriteAllText(Output + "/unity_ribbon_fixed_visuals.json", JsonConvert.SerializeObject(report, Formatting.Indented)); if (Application.isBatchMode) EditorApplication.Exit(exit); }
    }

    private static object ValidatePublishedLoops(GameObject root, SkinnedMeshRenderer renderer, bool renderVariant)
    {
        Require(renderer.sharedMesh.subMeshCount == renderer.sharedMaterials.Length, "Published accessory mesh/material slots mismatch.");
        int slot = Array.FindIndex(renderer.sharedMaterials, m => m != null && m.name == "Artifairy_RibbonLoops_Opaque");
        Require(slot >= 0, "Published loop-only opaque material is missing.");
        var material = renderer.sharedMaterials[slot];
        Require(material.shader != null && material.shader.name == "VRM10/MToon10", "Published loop material must use Built-in MToon10.");
        Require(material.GetInt("_AlphaMode") == 0 && material.GetInt("_M_ZWrite") == 1 && material.renderQueue == 2000 && material.GetTag("RenderType", false) == "Opaque" && !material.IsKeywordEnabled("_ALPHABLEND_ON"), "Published loop material is not opaque/depth-writing.");
        var official = new Material(material);
        try
        {
            OfficialMToon(official, 0, false);
            foreach (string property in new[] { "_AlphaMode", "_M_ZWrite", "_M_SrcBlend", "_M_DstBlend", "_M_CullMode" })
                Require(material.GetInt(property) == official.GetInt(property), "Published loop shader state differs from official MToon validator: " + property);
        }
        finally { Object.DestroyImmediate(official); }
        var triangles = renderer.sharedMesh.GetIndices(slot);
        // Both layers of the render variant's Solidify modifier must be fixed, together with its side faces.
        Require(triangles.Length / 3 == (renderVariant ? 1788 : 596), "Published opaque slot does not cover every expected colored loop triangle.");
        var loops = BuildLoops(renderer, root.transform);
        var raw = RootPoints(renderer.sharedMesh.vertices, renderer.transform, root.transform);
        var baked = Bake(renderer, root.transform);
        var opaqueIndices = triangles.Distinct().ToArray();
        var selected = new HashSet<int>(opaqueIndices);
        foreach (var loop in loops) Require(loop.indices.All(selected.Contains), "Base ring vertices escaped the published opaque slot.");
        var thickness = new Dictionary<string, float>();
        foreach (bool negative in new[] { true, false })
        {
            var points = opaqueIndices.Where(i => negative ? raw[i].x < 0 : raw[i].x > 0).Select(i => baked[i]).ToArray();
            Require(points.Length >= 290, "One published ring is missing.");
            var mean = points.Aggregate(Vector3.zero, (a, b) => a + b) / points.Length; var covariance = new double[3, 3];
            foreach (var point in points) { var d = point - mean; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) covariance[i, j] += d[i] * d[j] / points.Length; }
            float span = Eigenvectors(covariance).Select(axis => points.Max(p => Vector3.Dot(p, axis)) - points.Min(p => Vector3.Dot(p, axis))).Min();
            Require(span > 0.015f, "Published ring lost its original 3D thickness."); thickness[negative ? "negativeX" : "positiveX"] = span;
        }
        int transparentBaseTriangles = 0; var baseMask = new HashSet<int>(loops.SelectMany(l => l.indices));
        for (int sub = 0; sub < renderer.sharedMesh.subMeshCount; sub++)
        {
            var other = renderer.sharedMaterials[sub];
            if (!other.HasProperty("_AlphaMode") || other.GetInt("_AlphaMode") != 2) continue;
            var indices = renderer.sharedMesh.GetIndices(sub);
            for (int t = 0; t + 2 < indices.Length; t += 3) if (baseMask.Contains(indices[t]) && baseMask.Contains(indices[t + 1]) && baseMask.Contains(indices[t + 2])) transparentBaseTriangles++;
        }
        Require(transparentBaseTriangles == 0, "Transparent material still draws base loop triangles.");
        return new { loopTriangles = triangles.Length / 3, allColoredLayersCovered = true, transparentBaseTriangles,
            loopThicknessMeters = thickness, baseLoops = Capture(root, renderer, loops, baked, raw),
            materials = renderer.sharedMaterials.Select((m, i) => new { m.name, triangles = renderer.sharedMesh.GetIndices(i).Length / 3, m.renderQueue, renderType = m.GetTag("RenderType", false), state = ScalarProperties(m, new[] { "_AlphaMode", "_TransparentWithZWrite", "_M_ZWrite", "_M_CullMode" }) }).ToArray() };
    }

    public static void RunMaterialVariants()
    {
        int exit = 1;
        var report = new Dictionary<string, object>();
        var protectedFiles = new[] { "Assets/Private/Artifairy/Scenes/Artifairy_Chat.unity", "Assets/Private/Artifairy/Scenes/Artifairy_RenderChat.unity",
            "Assets/Model/Artifairy/Artifairy_Compatible.vrm", "Assets/Model/Artifairy/Artifairy_Compatible.prefab",
            "Assets/Model/Artifairy/Artifairy_RenderCompatible.vrm", "Assets/Model/Artifairy/Artifairy_RenderCompatible.prefab" };
        var hashes = protectedFiles.ToDictionary(p => p, Hash);
        Directory.CreateDirectory(Output);
        try
        {
            Require(!Application.isPlaying && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "Material comparison needs Edit Mode and a graphics device.");
            report["previewOnly"] = new { lightIntensity = 0.18f, emissionScale = 0.05f, rimScale = 0.1f, texturesPreserved = true };
            var models = new Dictionary<string, object>(); report["models"] = models;
            foreach (string stem in new[] { "Artifairy_Compatible", "Artifairy_RenderCompatible" })
            {
                string path = "Assets/Private/Artifairy/Scenes/" + (stem.Contains("Render") ? "Artifairy_RenderChat" : "Artifairy_Chat") + ".unity";
                var saved = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                var avatar = saved.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Vrm10Instance>(true)).Single();
                var cases = new Dictionary<string, object>(); models[stem] = cases;
                foreach (string pose in new[] { "savedPose", "idlePose" })
                foreach (string mode in new[] { "current", "accessoryZWrite", "clothZWrite", "bothZWrite", "loopOpaque", "loopCutout" })
                {
                    var scene = EditorSceneManager.NewPreviewScene(); var root = Object.Instantiate(avatar.gameObject);
                    SceneManager.MoveGameObjectToScene(root, scene);
                    var owned = new List<Object>();
                    try
                    {
                        foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 30;
                        root.GetComponent<Vrm10Instance>().enabled = false;
                        root.GetComponent<Vrm10Instance>().UpdateType = Vrm10Instance.UpdateTypes.None;
                        root.GetComponent<ArtifairySpringBoneDriver>().enabled = false;
                        var animator = root.GetComponent<Animator>();
                        if (pose == "idlePose") { animator.enabled = true; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; animator.Rebind(); animator.SetInteger("state", 0); for (int t = 0; t < 90; t++) animator.Update(1f / 60f); }
                        animator.enabled = false;
                        var accessory = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == RendererName);
                        var loops = BuildLoops(accessory, root.transform);
                        var before = Bake(accessory, root.transform);
                        var copies = new Dictionary<Material, Material>();
                        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        {
                            renderer.sharedMaterials = renderer.sharedMaterials.Select(original =>
                            {
                                if (!copies.TryGetValue(original, out var copy))
                                {
                                    copy = new Material(original) { name = original.name + " [preview clone]", hideFlags = HideFlags.DontSave };
                                    copies.Add(original, copy); owned.Add(copy);
                                    if (copy.HasProperty("_EmissionColor")) copy.SetColor("_EmissionColor", copy.GetColor("_EmissionColor") * 0.05f);
                                    if (copy.HasProperty("_RimColor")) copy.SetColor("_RimColor", copy.GetColor("_RimColor") * 0.1f);
                                    if (copy.HasProperty("_MatcapColor")) copy.SetColor("_MatcapColor", copy.GetColor("_MatcapColor") * 0.1f);
                                }
                                return copy;
                            }).ToArray();
                        }
                        int loopTriangles = 0;
                        if (mode == "accessoryZWrite" || mode == "bothZWrite")
                            foreach (var material in accessory.sharedMaterials.Where(m => m.HasProperty("_AlphaMode") && m.GetInt("_AlphaMode") == 2)) OfficialMToon(material, 2, true);
                        if (mode == "clothZWrite" || mode == "bothZWrite")
                            foreach (var material in root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == "衣服_VRM").sharedMaterials.Where(m => m.HasProperty("_AlphaMode") && m.GetInt("_AlphaMode") == 2)) OfficialMToon(material, 2, true);
                        if (mode == "loopOpaque" || mode == "loopCutout") loopTriangles = SplitLoopMaterial(accessory, loops, mode == "loopOpaque" ? 0 : 1, owned);
                        var after = Bake(accessory, root.transform);
                        Require(before.Length == after.Length && before.Select((p, i) => Vector3.Distance(p, after[i])).Max() < 0.000001f, "Material-only comparison changed ornament geometry.");
                        cases[pose + "/" + mode] = new
                        {
                            loopTriangles, geometryUnchanged = true,
                            materials = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.name == RendererName || r.name == "衣服_VRM")
                                .Select(r => new { renderer = r.name, boundsCenterInRoot = V(root.transform.InverseTransformPoint(r.bounds.center)),
                                    materials = r.sharedMaterials.Select(m => new { m.name, m.renderQueue, renderType = m.GetTag("RenderType", false), state = ScalarProperties(m, new[] { "_AlphaMode", "_TransparentWithZWrite", "_M_ZWrite", "_M_CullMode" }) }).ToArray() }).ToArray()
                        };
                        Render(root, scene, stem + "_material_" + pose + "_" + mode, 0.18f, true);
                    }
                    finally { Object.DestroyImmediate(root); foreach (var obj in owned) Object.DestroyImmediate(obj); EditorSceneManager.ClosePreviewScene(scene); }
                }
            }
            foreach (var file in hashes) Require(Hash(file.Key) == file.Value, "Clone-only comparison changed an asset: " + file.Key);
            report["protectedAssetsUnchanged"] = true; report["pass"] = true; exit = 0;
        }
        catch (Exception error) { report["pass"] = false; report["error"] = error.GetBaseException().Message; Debug.LogException(error); }
        finally { File.WriteAllText(Output + "/unity_ribbon_material_comparison.json", JsonConvert.SerializeObject(report, Formatting.Indented)); if (Application.isBatchMode) EditorApplication.Exit(exit); }
    }

    private static void OfficialMToon(Material material, int alphaMode, bool zWrite)
    {
        Require(material.shader.name == "VRM10/MToon10", "Expected built-in MToon10 for depth comparison.");
        material.SetInt("_AlphaMode", alphaMode); material.SetInt("_TransparentWithZWrite", zWrite ? 1 : 0); material.SetInt("_RenderQueueOffset", 0);
        // This verified vendor runtime assembly is intentionally not autoReferenced.
        // Its validator owns shader keywords, blend/ZWrite/cull state, tags and queues.
        var type = Type.GetType("VRM10.MToon10.MToonValidator, VRM10.MToon10", true);
        // An explicit signature avoids UnityEngine.Object's implicit bool conversion
        // selecting Activator.CreateInstance(Type, bool) and looking for a default ctor.
        var constructor = type.GetConstructor(new[] { typeof(Material) });
        var validate = type.GetMethod("Validate", Type.EmptyTypes);
        Require(constructor != null && validate != null, "Installed MToonValidator Material constructor/Validate API missing.");
        var validator = constructor.Invoke(new object[] { material });
        validate.Invoke(validator, Array.Empty<object>());
    }

    private static int SplitLoopMaterial(SkinnedMeshRenderer renderer, Loop[] loops, int alphaMode, List<Object> owned)
    {
        var source = renderer.sharedMesh;
        var mask = new HashSet<int>(loops.SelectMany(l => l.indices));
        int slot = Array.FindIndex(renderer.sharedMaterials, m => m.name.Contains("饰品袖子"));
        Require(slot >= 0, "Loop source material slot missing.");
        var original = source.GetIndices(slot); var selected = new List<int>(); var remaining = new List<int>();
        for (int i = 0; i + 2 < original.Length; i += 3)
        {
            var destination = mask.Contains(original[i]) && mask.Contains(original[i + 1]) && mask.Contains(original[i + 2]) ? selected : remaining;
            destination.Add(original[i]); destination.Add(original[i + 1]); destination.Add(original[i + 2]);
        }
        Require(selected.Count > 0, "No selected loop triangles for material-only split.");
        var mesh = Object.Instantiate(source); mesh.name = source.name + " [loop material split]"; mesh.hideFlags = HideFlags.DontSave; owned.Add(mesh);
        int addedSlot = source.subMeshCount;
        mesh.subMeshCount = addedSlot + 1; mesh.SetTriangles(remaining, slot); mesh.SetTriangles(selected, addedSlot);
        var material = new Material(renderer.sharedMaterials[slot]) { name = "Loop-only " + (alphaMode == 0 ? "opaque" : "cutout") + " [preview clone]", hideFlags = HideFlags.DontSave };
        owned.Add(material); OfficialMToon(material, alphaMode, false);
        renderer.sharedMesh = mesh; renderer.sharedMaterials = renderer.sharedMaterials.Concat(new[] { material }).ToArray();
        Require(renderer.sharedMesh.subMeshCount == renderer.sharedMaterials.Length, "Loop split material slots are inconsistent.");
        return selected.Count / 3;
    }

    public static void Run()
    {
        int exit = 1;
        Directory.CreateDirectory(Output);
        Result.Clear();
        var protectedFiles = new[] { "Assets/Private/Artifairy/Scenes/Artifairy_Chat.unity", "Assets/Private/Artifairy/Scenes/Artifairy_RenderChat.unity",
            "Assets/Model/Artifairy/Artifairy_Compatible.vrm", "Assets/Model/Artifairy/Artifairy_Compatible.prefab",
            "Assets/Model/Artifairy/Artifairy_RenderCompatible.vrm", "Assets/Model/Artifairy/Artifairy_RenderCompatible.prefab" };
        var hashes = protectedFiles.ToDictionary(p => p, Hash);
        try
        {
            Require(!Application.isPlaying, "Ribbon diagnostics require Edit Mode.");
            Result["graphicsDevice"] = SystemInfo.graphicsDeviceType.ToString();
            Result["sourceGeometry"] = SourceGeometry;
            var reports = new Dictionary<string, object>();
            foreach (string stem in new[] { "Artifairy_Compatible", "Artifairy_RenderCompatible" })
                reports[stem] = Analyze(stem);
            foreach (var file in hashes) Require(Hash(file.Key) == file.Value, "Read-only diagnostics changed an asset: " + file.Key);
            Result["protectedAssetsUnchanged"] = true;
            Result["models"] = reports;
            Result["pass"] = true;
            exit = 0;
        }
        catch (Exception error) { Result["pass"] = false; Result["error"] = error.GetBaseException().Message; Debug.LogException(error); }
        finally
        {
            File.WriteAllText(Output + "/unity_ribbon_diagnostics.json", JsonConvert.SerializeObject(Result, Formatting.Indented));
            if (Application.isBatchMode) EditorApplication.Exit(exit);
        }
    }

    private static object Analyze(string stem)
    {
        string assetPath = "Assets/Model/Artifairy/" + stem + ".prefab";
        var stages = new Dictionary<string, object>();
        var report = new Dictionary<string, object> { ["asset"] = assetPath, ["stages"] = stages };
        string scenePath = "Assets/Private/Artifairy/Scenes/" + (stem.Contains("Render") ? "Artifairy_RenderChat" : "Artifairy_Chat") + ".unity";
        var savedScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        var savedAvatar = savedScene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Vrm10Instance>(true)).Single();
        // First observation is the actual saved scene, before Animator.Update or Simulate.
        stages["savedSceneImmediate"] = StaticSnapshot(savedAvatar.gameObject);
        var posePreview = EditorSceneManager.NewPreviewScene(); var poseCopy = Object.Instantiate(savedAvatar.gameObject);
        SceneManager.MoveGameObjectToScene(poseCopy, posePreview);
        try { foreach (var t in poseCopy.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 30; Render(poseCopy, posePreview, stem + "_savedSceneImmediate"); }
        finally { Object.DestroyImmediate(poseCopy); EditorSceneManager.ClosePreviewScene(posePreview); }
        // Loading a scene unloads unused imported objects; load assets only after that boundary.
        var imported = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Model/Artifairy/" + stem + ".vrm");
        Require(imported != null, "Missing imported VRM: " + stem);
        var importedPreview = EditorSceneManager.NewPreviewScene(); var importedCopy = Object.Instantiate(imported);
        SceneManager.MoveGameObjectToScene(importedCopy, importedPreview);
        try
        {
            importedCopy.GetComponent<Vrm10Instance>().enabled = false;
            stages["importedRestImmediate"] = StaticSnapshot(importedCopy);
        }
        finally { Object.DestroyImmediate(importedCopy); EditorSceneManager.ClosePreviewScene(importedPreview); }
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        Require(asset != null, "Missing read-only target prefab: " + assetPath);
        foreach (string mode in new[] { "neutral", "physicsOnly", "animatorOnly", "animatorAndPhysics" })
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var root = Object.Instantiate(asset);
            SceneManager.MoveGameObjectToScene(root, scene);
            root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            root.transform.localScale = Vector3.one;
            foreach (var node in root.GetComponentsInChildren<Transform>(true)) node.gameObject.layer = 30;
            try
            {
                var vrm = root.GetComponent<Vrm10Instance>();
                Require(vrm != null, "Missing VRM instance in prefab.");
                vrm.enabled = false; vrm.UpdateType = Vrm10Instance.UpdateTypes.None;
                var animator = root.GetComponent<Animator>();
                var spring = root.GetComponent<ArtifairySpringBoneDriver>();
                Require(animator != null && spring != null, "Missing Animator/spring driver.");
                bool animate = mode.StartsWith("animator", StringComparison.Ordinal);
                bool simulate = mode == "physicsOnly" || mode == "animatorAndPhysics";
                animator.enabled = animate;
                spring.enabled = simulate;
                foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    for (int i = 0; i < r.sharedMesh.blendShapeCount; i++) r.SetBlendShapeWeight(i, 0);
                var renderer = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == RendererName);
                var loops = BuildLoops(renderer, root.transform);
                var stage = new Dictionary<string, object>();
                stages[mode] = stage;
                var raw = RootPoints(renderer.sharedMesh.vertices, renderer.transform, root.transform);
                var baked = Bake(renderer, root.transform);
                foreach (var loop in loops) loop.neutral = baked;
                if (mode == "neutral")
                {
                    stage["sharedMesh"] = Capture(root, renderer, loops, raw, null);
                    stage["neutralBake"] = Capture(root, renderer, loops, baked, raw);
                    stage["skin"] = Skin(renderer, loops);
                    stage["materials"] = renderer.sharedMaterials.Select(m => new
                    {
                        name = m.name, shader = m.shader != null ? m.shader.name : null,
                        renderQueue = m.renderQueue, renderType = m.GetTag("RenderType", false), queueTag = m.GetTag("Queue", false), keywords = m.shaderKeywords,
                        state = ScalarProperties(m, new[] { "_Cull", "_CullMode", "_M_CullMode", "_MToonCullMode", "_AlphaMode", "_AlphaClip", "_Cutoff", "_BlendMode", "_SrcBlend", "_DstBlend", "_ZWrite", "_ZWriteControl", "_TransparentWithZWrite", "_AlphaToMask" }),
                        baseColor = m.HasProperty("_Color") ? C(m.GetColor("_Color")) : m.HasProperty("_BaseColor") ? C(m.GetColor("_BaseColor")) : null,
                        textureAlpha = TextureAlpha(m, renderer, loops)
                    }).ToArray();
                    Render(root, scene, stem + "_neutral");
                    continue;
                }
                // The signature exposes TransformState from non-autoReferenced UniGLTF.Utils.
                // Invoke only this verified getter as object; the production Motion asmdef
                // owns the typed solver API. Capture rest BEFORE any Animator evaluation.
                typeof(Vrm10Instance).GetProperty("DefaultTransformStates").GetValue(vrm);
                if (animate)
                {
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.Rebind(); animator.SetInteger("state", 0); animator.Update(0f);
                }
                var samples = new Dictionary<string, object>();
                stage["samples"] = samples;
                for (int tick = 1; tick <= 360; tick++)
                {
                    if (animate) animator.Update(1f / 60f);
                    if (simulate) spring.Simulate(1f / 60f);
                    if (tick == 1 || tick == 60 || tick == 180 || tick == 360)
                        samples[tick.ToString()] = Capture(root, renderer, loops, Bake(renderer, root.transform), loops[0].neutral);
                }
                stage["springInitialized"] = spring.IsInitialized;
                stage["selectedJointRotations"] = vrm.SpringBone.Springs.Where(s => (s.Name ?? "").Contains("Front"))
                    .SelectMany(s => s.Joints).Where(j => j != null).Select(j => new { name = j.name, localRotation = Q(j.transform.localRotation), localPosition = V(j.transform.localPosition) }).ToArray();
                if (mode == "physicsOnly" || mode == "animatorAndPhysics") Render(root, scene, stem + "_" + mode);
                Require(!vrm.enabled && vrm.UpdateType == Vrm10Instance.UpdateTypes.None, "Diagnostics changed VRM ownership.");
            }
            finally { Object.DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(scene); }
        }
        return report;
    }

    private static object StaticSnapshot(GameObject root)
    {
        var renderer = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == RendererName);
        var loops = BuildLoops(renderer, root.transform);
        var raw = RootPoints(renderer.sharedMesh.vertices, renderer.transform, root.transform);
        var baked = Bake(renderer, root.transform);
        return new
        {
            rootPosition = V(root.transform.position), rootRotation = Q(root.transform.rotation), rootScale = V(root.transform.lossyScale),
            sharedMesh = Capture(root, renderer, loops, raw, null), immediateBake = Capture(root, renderer, loops, baked, raw),
            skin = Skin(renderer, loops), influencingBoneTransforms = BoneTransforms(root.transform, renderer, loops),
            animatorEnabled = root.GetComponent<Animator>()?.enabled,
            springDriverEnabled = root.GetComponent<ArtifairySpringBoneDriver>()?.enabled,
            springInitialized = root.GetComponent<ArtifairySpringBoneDriver>()?.IsInitialized
        };
    }

    private static object BoneTransforms(Transform root, SkinnedMeshRenderer renderer, Loop[] loops) =>
        loops.SelectMany(l => l.indices).SelectMany(i => Weights(renderer.sharedMesh.boneWeights[i])).Select(w => w.Key).Distinct()
            .Select(i => new
            {
                bone = renderer.bones[i].name, parent = renderer.bones[i].parent?.name,
                rootPosition = V(root.InverseTransformPoint(renderer.bones[i].position)),
                localPosition = V(renderer.bones[i].localPosition), localRotation = Q(renderer.bones[i].localRotation), localScale = V(renderer.bones[i].localScale),
                bindPose = Enumerable.Range(0, 16).Select(n => renderer.sharedMesh.bindposes[i][n]).ToArray()
            }).ToArray();

    private static Loop[] BuildLoops(SkinnedMeshRenderer renderer, Transform root)
    {
        var source = JObject.Parse(File.ReadAllText(SourceGeometry))[RendererName]["coordinates"];
        var raw = RootPoints(renderer.sharedMesh.vertices, renderer.transform, root);
        var loops = new List<Loop>();
        foreach (var range in new[] { new[] { 5999, 6296 }, new[] { 6297, 6594 } })
        {
            // This VRM0 exporter reflects X; Unity converts the exported Z handedness.
            var points = Enumerable.Range(range[0], range[1] - range[0] + 1).Select(i =>
                new Vector3(-(float)source[i][0], (float)source[i][2], -(float)source[i][1])).ToArray();
            var matched = new List<int>();
            var covered = new HashSet<int>();
            for (int i = 0; i < raw.Length; i++)
            {
                float best = float.PositiveInfinity; int index = -1;
                for (int j = 0; j < points.Length; j++) { float d = (raw[i] - points[j]).sqrMagnitude; if (d < best) { best = d; index = j; } }
                if (best < 0.00003f * 0.00003f) { matched.Add(i); covered.Add(index); }
            }
            Require(covered.Count >= 290, "Loop mask failed to match rest geometry: " + range[0] + ", covered " + covered.Count);
            var mask = new HashSet<int>(matched); var edges = new HashSet<ulong>();
            for (int sub = 0; sub < renderer.sharedMesh.subMeshCount; sub++)
            {
                var triangles = renderer.sharedMesh.GetIndices(sub);
                for (int t = 0; t + 2 < triangles.Length; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int a = triangles[t + e], b = triangles[t + (e + 1) % 3];
                        if (!mask.Contains(a) || !mask.Contains(b)) continue;
                        edges.Add(((ulong)(uint)Math.Min(a, b) << 32) | (uint)Math.Max(a, b));
                    }
            }
            loops.Add(new Loop { name = range[0] == 5999 ? "SourceXNegative" : "SourceXPositive", source = points,
                indices = matched.ToArray(), edges = edges.Select(e => new[] { (int)(e >> 32), (int)(e & uint.MaxValue) }).ToArray() });
        }
        return loops.ToArray();
    }

    private static object Capture(GameObject avatar, SkinnedMeshRenderer ornament, Loop[] loops, Vector3[] points, Vector3[] reference)
    {
        var cloth = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.name == "衣服_VRM");
        var clothVertices = Bake(cloth, avatar.transform);
        var triangles = Enumerable.Range(0, cloth.sharedMesh.subMeshCount).SelectMany(s => cloth.sharedMesh.GetIndices(s)).ToArray();
        return loops.ToDictionary(l => l.name, l => (object)new
        {
            selectedVertices = l.indices.Length, triangleEdges = l.edges.Length,
            shape = Shape(l.indices.Select(i => points[i]).ToArray()),
            displacementFromReference = reference == null ? null : Displacement(l.indices, points, reference),
            edgeDistortion = reference == null ? null : Strain(l.edges, points, reference),
            nearestGarmentSurfaceMeters = SurfaceDistances(l.indices.Select(i => points[i]).ToArray(), clothVertices, triangles)
        });
    }

    private static object Skin(SkinnedMeshRenderer renderer, Loop[] loops)
    {
        var weights = renderer.sharedMesh.boneWeights; var bindposes = renderer.sharedMesh.bindposes;
        return loops.ToDictionary(l => l.name, l => (object)new
        {
            influences = l.indices.SelectMany(i => Weights(weights[i])).GroupBy(v => v.Key).Select(g => new
            {
                bone = renderer.bones[g.Key].name, vertexInfluences = g.Count(), min = g.Min(v => v.Value), max = g.Max(v => v.Value), mean = g.Average(v => v.Value),
                restSkinMatrixIdentityError = MatrixError(renderer.worldToLocalMatrix * renderer.bones[g.Key].localToWorldMatrix * bindposes[g.Key])
            }).ToArray(),
            distinctWeightSignatures = l.indices.Select(i => string.Join(";", Weights(weights[i]).OrderBy(w => w.Key).Select(w => w.Key + ":" + w.Value.ToString("F5")))).Distinct().Count()
        });
    }

    private static IEnumerable<KeyValuePair<int, float>> Weights(BoneWeight w)
    {
        if (w.weight0 > 0) yield return new KeyValuePair<int, float>(w.boneIndex0, w.weight0);
        if (w.weight1 > 0) yield return new KeyValuePair<int, float>(w.boneIndex1, w.weight1);
        if (w.weight2 > 0) yield return new KeyValuePair<int, float>(w.boneIndex2, w.weight2);
        if (w.weight3 > 0) yield return new KeyValuePair<int, float>(w.boneIndex3, w.weight3);
    }
    private static float MatrixError(Matrix4x4 matrix) { float max = 0; for (int i = 0; i < 16; i++) max = Mathf.Max(max, Mathf.Abs(matrix[i] - Matrix4x4.identity[i])); return max; }
    private static object Displacement(int[] ids, Vector3[] a, Vector3[] b)
    {
        var values = ids.Select(i => Vector3.Distance(a[i], b[i])).ToArray();
        return new { maximum = values.Max(), mean = values.Average(), rms = Math.Sqrt(values.Select(v => v * v).Average()) };
    }
    private static object Strain(int[][] edges, Vector3[] points, Vector3[] reference)
    {
        var ratios = edges.Where(e => Vector3.Distance(reference[e[0]], reference[e[1]]) > 0.000001f)
            .Select(e => Vector3.Distance(points[e[0]], points[e[1]]) / Vector3.Distance(reference[e[0]], reference[e[1]])).ToArray();
        return new { minimumRatio = ratios.Min(), maximumRatio = ratios.Max(), meanAbsoluteStrain = ratios.Average(r => Mathf.Abs(r - 1)), fractionOverTwoPercent = ratios.Count(r => Mathf.Abs(r - 1) > 0.02f) / (float)ratios.Length };
    }
    private static object Shape(Vector3[] points)
    {
        Vector3 min = points.Aggregate(Vector3.Min), max = points.Aggregate(Vector3.Max);
        var mean = points.Aggregate(Vector3.zero, (a, b) => a + b) / points.Length;
        double[,] covariance = new double[3, 3];
        foreach (var p in points) { var d = p - mean; for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) covariance[i, j] += d[i] * d[j] / points.Length; }
        var axes = Eigenvectors(covariance);
        float[] span = axes.Select(axis => points.Max(p => Vector3.Dot(p, axis)) - points.Min(p => Vector3.Dot(p, axis))).ToArray();
        Require(points.All(p => !float.IsNaN(p.sqrMagnitude) && !float.IsInfinity(p.sqrMagnitude)), "Nonfinite ornament geometry.");
        return new { minimum = V(min), maximum = V(max), size = V(max - min), center = V(mean), pcaSpan = span, pcaAxes = axes.Select(V).ToArray() };
    }
    private static Vector3[] Eigenvectors(double[,] a)
    {
        double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int iteration = 0; iteration < 25; iteration++)
        {
            int p = 0, q = 1;
            foreach (var pair in new[] { new[] { 0, 2 }, new[] { 1, 2 } }) if (Math.Abs(a[pair[0], pair[1]]) > Math.Abs(a[p, q])) { p = pair[0]; q = pair[1]; }
            if (Math.Abs(a[p, q]) < 1e-14) break;
            double angle = 0.5 * Math.Atan2(2 * a[p, q], a[q, q] - a[p, p]), c = Math.Cos(angle), s = Math.Sin(angle);
            var rotation = new double[,] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            rotation[p, p] = c; rotation[q, q] = c; rotation[p, q] = s; rotation[q, p] = -s;
            var next = new double[3, 3]; var vectors = new double[3, 3];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
            {
                for (int k = 0; k < 3; k++) vectors[i, j] += v[i, k] * rotation[k, j];
                for (int k = 0; k < 3; k++) for (int l = 0; l < 3; l++) next[i, j] += rotation[k, i] * a[k, l] * rotation[l, j];
            }
            a = next; v = vectors;
        }
        return Enumerable.Range(0, 3).OrderByDescending(i => a[i, i]).Select(i => new Vector3((float)v[0, i], (float)v[1, i], (float)v[2, i]).normalized).ToArray();
    }

    private static object SurfaceDistances(Vector3[] selected, Vector3[] cloth, int[] triangles)
    {
        var distances = new List<float>();
        foreach (var point in selected)
        {
            float best = float.PositiveInfinity;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                Vector3 a = cloth[triangles[t]], b = cloth[triangles[t + 1]], c = cloth[triangles[t + 2]];
                if (point.y < Mathf.Min(a.y, Mathf.Min(b.y, c.y)) - 0.25f || point.y > Mathf.Max(a.y, Mathf.Max(b.y, c.y)) + 0.25f) continue;
                best = Mathf.Min(best, (point - Closest(point, a, b, c)).sqrMagnitude);
            }
            distances.Add(Mathf.Sqrt(best));
        }
        distances.Sort();
        return new { minimum = distances[0], median = distances[distances.Count / 2], maximum = distances.Last(), mean = distances.Average(), fractionWithinOneMillimeter = distances.Count(d => d < 0.001f) / (float)distances.Count };
    }
    private static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a; var ac = c - a; var ap = p - a; float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp); if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2; if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp); if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6; if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4; if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
        float sum = va + vb + vc; if (Mathf.Abs(sum) < 1e-15f) return a;
        return a + ab * (vb / sum) + ac * (vc / sum);
    }

    private static void Render(GameObject root, Scene scene, string prefix, float lightIntensity = 1.25f, bool threeQuarter = false, bool diagnosticLight = true)
    {
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
        var cameraGo = new GameObject("Offline ribbon diagnostic camera"); SceneManager.MoveGameObjectToScene(cameraGo, scene);
        var lightGo = diagnosticLight ? new GameObject("Offline ribbon diagnostic light") : null;
        if (lightGo != null) SceneManager.MoveGameObjectToScene(lightGo, scene);
        var camera = cameraGo.AddComponent<Camera>();
        camera.enabled = false; camera.scene = scene; camera.cullingMask = 1 << 30;
        camera.orthographic = true; camera.orthographicSize = 0.32f; camera.nearClipPlane = 0.01f; camera.farClipPlane = 10f;
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        if (lightGo != null)
        {
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional; light.intensity = lightIntensity; light.cullingMask = 1 << 30; light.shadows = LightShadows.None;
            light.transform.rotation = root.transform.rotation * Quaternion.Euler(35f, -25f, 0);
        }
        var target = root.transform.TransformPoint(new Vector3(0, 0.68f, 0.08f));
        var render = RenderTexture.GetTemporary(800, 640, 24, RenderTextureFormat.ARGB32);
        var active = RenderTexture.active;
        try
        {
            camera.targetTexture = render;
            var directions = new List<(string name, Vector3 offset)> { ("front", Vector3.forward), ("side", Vector3.right) };
            if (threeQuarter) directions.Add(("threequarter", new Vector3(0.65f, 0, 0.76f).normalized));
            foreach (var direction in directions)
            {
                camera.transform.position = target + root.transform.TransformDirection(direction.offset) * 2f; camera.transform.LookAt(target); camera.Render();
                RenderTexture.active = render;
                var image = new Texture2D(800, 640, TextureFormat.RGB24, false);
                try { image.ReadPixels(new Rect(0, 0, 800, 640), 0, 0); image.Apply(); File.WriteAllBytes(Output + "/" + prefix + "_" + direction.name + ".png", image.EncodeToPNG()); }
                finally { Object.DestroyImmediate(image); }
            }
        }
        finally { camera.targetTexture = null; RenderTexture.active = active; RenderTexture.ReleaseTemporary(render); Object.DestroyImmediate(cameraGo); if (lightGo != null) Object.DestroyImmediate(lightGo); }
    }
    private static object TextureAlpha(Material material, SkinnedMeshRenderer renderer, Loop[] loops)
    {
        string property = new[] { "_MainTex", "_BaseMap" }.FirstOrDefault(p => material.HasProperty(p) && material.GetTexture(p) != null);
        if (property == null) return null;
        var texture = material.GetTexture(property);
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return new { property, texture = texture.name, available = false };
        int width = Mathf.Min(512, texture.width), height = Mathf.Min(512, texture.height);
        var render = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        var active = RenderTexture.active; var cpu = new Texture2D(width, height, TextureFormat.RGBA32, false);
        try
        {
            Graphics.Blit(texture, render); RenderTexture.active = render;
            cpu.ReadPixels(new Rect(0, 0, width, height), 0, 0); cpu.Apply();
            var pixels = cpu.GetPixels32();
            var scale = material.GetTextureScale(property); var offset = material.GetTextureOffset(property);
            var uv = renderer.sharedMesh.uv;
            var sampled = loops.SelectMany(l => l.indices).Select(i =>
            {
                var value = Vector2.Scale(uv[i], scale) + offset;
                float x = texture.wrapMode == TextureWrapMode.Clamp ? Mathf.Clamp01(value.x) : Mathf.Repeat(value.x, 1f);
                float y = texture.wrapMode == TextureWrapMode.Clamp ? Mathf.Clamp01(value.y) : Mathf.Repeat(value.y, 1f);
                return pixels[Mathf.Min(height - 1, (int)(y * height)) * width + Mathf.Min(width - 1, (int)(x * width))].a;
            }).ToArray();
            return new { property, texture = texture.name, width = texture.width, height = texture.height, wrapMode = texture.wrapMode.ToString(),
                fullTextureMinimumAlpha = pixels.Min(p => p.a), fullTextureMaximumAlpha = pixels.Max(p => p.a), fullTextureTransparentFraction = pixels.Count(p => p.a < 128) / (float)pixels.Length,
                selectedVertexMinimumAlpha = sampled.Min(), selectedVertexMaximumAlpha = sampled.Max(), selectedVertexTransparentFraction = sampled.Count(a => a < 128) / (float)sampled.Length };
        }
        finally { RenderTexture.active = active; RenderTexture.ReleaseTemporary(render); Object.DestroyImmediate(cpu); }
    }
    private static Dictionary<string, float> ScalarProperties(Material material, string[] keys) => keys.Where(material.HasProperty).ToDictionary(k => k, material.GetFloat);
    private static Vector3[] RootPoints(Vector3[] points, Transform renderer, Transform root) => points.Select(p => root.InverseTransformPoint(renderer.TransformPoint(p))).ToArray();
    private static Vector3[] Bake(SkinnedMeshRenderer renderer, Transform root)
    {
        var mesh = new Mesh(); try { renderer.BakeMesh(mesh); return RootPoints(mesh.vertices, renderer.transform, root); } finally { Object.DestroyImmediate(mesh); }
    }
    private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
    private static float[] Q(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
    private static float[] C(Color c) => new[] { c.r, c.g, c.b, c.a };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Hash(string path) { using (var hash = SHA256.Create()) return string.Concat(hash.ComputeHash(File.ReadAllBytes(path)).Select(b => b.ToString("x2"))); }
}
#endif
