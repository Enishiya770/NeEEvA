// Copy to Assets/Editor/ArtifairyPhysicsValidation.cs only for the requested validation.
// Requires this project's UniVRM10 and UniGLTF.SpringBoneJobs assemblies.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UniGLTF;
using UniGLTF.SpringBoneJobs;
using UniGLTF.SpringBoneJobs.Blittables;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class ArtifairyPhysicsValidation
{
    [Serializable] public sealed class Report
    {
        public bool passed;
        public string unityVersion;
        public string activeRenderPipeline;
        public string method = "VRM0 migration, native UniVRM spring solver, deterministic 60 Hz editor ticks through Runtime.SpringBone.Process";
        public string importedAsset;
        public string generatedPrefab;
        public bool prefabUsesSpringOnlyDriver;
        public bool reusedGeneratedPrefab;
        public DriverReport productionDriver;
        public LegacyReport legacyCompatibility;
        public List<RendererReport> prefabRenderers = new List<RendererReport>();
        public int prefabMainTexturedMaterials;
        public string error;
        public List<ModelReport> models = new List<ModelReport>();
    }
    [Serializable] public sealed class ModelReport
    {
        public string path;
        public bool validHumanoid;
        public int springs, joints, colliderGroups, colliders, weightedSpringVertices, migrationEndNodes;
        public int mainTexturedMaterials;
        public float maxSpringSkinDisplacementMeters;
        public List<SpringReport> chains = new List<SpringReport>();
        public List<MotionReport> motions = new List<MotionReport>();
        public List<RendererReport> renderers = new List<RendererReport>();
    }
    [Serializable] public sealed class DriverReport
    {
        public bool initialized, disabledDisposed, reenabledReinitialized, disabledRestoredPose;
        public bool blendshapeWeightsUnchanged, vrmEnabledUnchanged, vrmUpdateTypeUnchanged;
        public int checkedBlendshapeWeights;
        public List<MotionReport> motions = new List<MotionReport>();
    }
    [Serializable] public sealed class LegacyReport
    {
        public string assetPath;
        public bool driverInitialized, weightsPreserved, foxTailUnchanged, fullVrmRemainedDisabled, allTransformsFinite;
        public int checkedBlendshapeWeights, foxTailTransforms;
        public float maxHairLocalRotationDegrees;
        public List<string> namedExpressionSentinels = new List<string>();
    }
    [Serializable] public sealed class RendererReport
    {
        public string renderer;
        public int vertices, uvCoordinates;
        public List<MaterialReport> materials = new List<MaterialReport>();
    }
    [Serializable] public sealed class MaterialReport
    {
        public string material, shader;
        public bool hasMainTexture;
        public List<TextureReport> textures = new List<TextureReport>();
    }
    [Serializable] public sealed class TextureReport
    {
        public string property, texture;
        public int width, height;
    }
    [Serializable] public sealed class SpringReport
    {
        public string name;
        public string center;
        public string[] joints;
        public int linkedColliderGroups;
        public float minSegmentLengthMeters;
    }
    [Serializable] public sealed class MotionReport
    {
        public string phase;
        public int ticks;
        public float maxHairLocalRotationDegrees, maxSkirtLocalRotationDegrees;
        public float minSphereTailClearanceMeters = 999f;
        public float minHairSphereTailClearanceMeters = 999f, minSkirtSphereTailClearanceMeters = 999f;
        public bool allTransformsFinite = true;
    }

    // The production standalone runtime uses Time.deltaTime. In editor batchmode it
    // can be zero, so this adapter runs the SAME native solver at a fixed timestep.
    private sealed class FixedStepSpringRuntime : IVrm10SpringBoneRuntime
    {
        private readonly Vrm10FastSpringboneRuntimeStandalone inner = new Vrm10FastSpringboneRuntimeStandalone();
        private readonly FastSpringBoneScheduler scheduler;
        private bool disposed;
        public FixedStepSpringRuntime() { scheduler = new FastSpringBoneScheduler(inner.m_bufferCombiner); }
        public async Task InitializeAsync(Vrm10Instance instance, IAwaitCaller caller)
        {
            await inner.InitializeAsync(instance, caller);
            if (!Application.isPlaying && !inner.ReconstructSpringBone()) throw new Exception("Spring buffer construction failed");
        }
        public bool ReconstructSpringBone() { return inner.ReconstructSpringBone(); }
        public void RestoreInitialTransform() { inner.RestoreInitialTransform(); }
        public void SetJointLevel(Transform joint, BlittableJointMutable settings) { inner.SetJointLevel(joint, settings); }
        public void SetModelLevel(Transform root, BlittableModelLevel settings) { inner.SetModelLevel(root, settings); }
        public void Process() { scheduler.Schedule(1f / 60f).Complete(); }
        public void DrawGizmos() { inner.DrawGizmos(); }
        public void Dispose() { if (disposed) return; disposed = true; inner.Dispose(); }
    }

    public static void Run()
    {
        var args = Environment.GetCommandLineArgs();
        var inputs = new List<string>();
        var reportPath = @"E:\Artifairy\.vrm_work\unity_physics_validation.json";
        string assetPath = null;
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "-vrmInput") inputs.Add(args[++i]);
            else if (args[i] == "-vrmReport") reportPath = args[++i];
            else if (args[i] == "-vrmAsset") assetPath = args[++i];
        }
        var report = new Report { unityVersion = Application.unityVersion,
            activeRenderPipeline = GraphicsSettings.currentRenderPipeline == null ? "Built-in" : GraphicsSettings.currentRenderPipeline.name };
        try
        {
            Require(inputs.Count > 0, "Supply one or more -vrmInput absolute paths");
            foreach (var input in inputs) Validate(input, report.models);
            if (!string.IsNullOrEmpty(assetPath))
            {
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                // VRM10.Editor has autoReferenced=false, so default Editor scripts
                // use Unity's serialized importer API rather than its hidden type.
                var importer = AssetImporter.GetAtPath(assetPath);
                Require(importer != null && importer.GetType().FullName == "UniVRM10.VrmScriptedImporter", "Project VRM10 scripted importer was not used");
                var serializedImporter = new SerializedObject(importer);
                var migration = serializedImporter.FindProperty("MigrateToVrm1");
                Require(migration != null && migration.propertyType == SerializedPropertyType.Boolean, "Importer migration property was not found");
                if (!migration.boolValue)
                {
                    migration.boolValue = true;
                    serializedImporter.ApplyModifiedPropertiesWithoutUndo();
                    importer.SaveAndReimport();
                }
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                Require(prefab != null && prefab.GetComponent<Vrm10Instance>() != null, "Imported prefab has no Vrm10Instance");
                var vrm = prefab.GetComponent<Vrm10Instance>();
                Require(vrm.SpringBone.Springs.Count > 0 && vrm.SpringBone.ColliderGroups.Count > 0, "Asset migration dropped springs/colliders");
                report.importedAsset = assetPath;
                var prefabPath = Path.ChangeExtension(assetPath, "prefab").Replace('\\', '/');
                var absolutePrefabPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", prefabPath));
                var saved = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (saved == null)
                {
                    Require(!File.Exists(absolutePrefabPath), "Refusing to overwrite an existing unreadable prefab: " + prefabPath);
                    GameObject configured = null;
                    try
                    {
                        configured = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                        Require(configured != null, "Could not instantiate imported VRM prefab");
                        configured.name = Path.GetFileNameWithoutExtension(assetPath);
                        var configuredVrm = configured.GetComponent<Vrm10Instance>();
                        configuredVrm.UpdateType = Vrm10Instance.UpdateTypes.None;
                        configuredVrm.enabled = false;
                        Require(configured.GetComponent<ArtifairySpringBoneDriver>() == null, "Imported VRM unexpectedly contains a driver");
                        var driver = configured.AddComponent<ArtifairySpringBoneDriver>();
                        driver.Configure(configuredVrm);
                        saved = PrefabUtility.SaveAsPrefabAsset(configured, prefabPath, out bool savedSuccessfully);
                        Require(savedSuccessfully && saved != null, "Saving spring-only prefab failed");
                    }
                    finally { if (configured != null) UnityEngine.Object.DestroyImmediate(configured); }
                }
                else report.reusedGeneratedPrefab = true;
                var savedVrm = saved.GetComponent<Vrm10Instance>();
                var savedDriver = saved.GetComponent<ArtifairySpringBoneDriver>();
                Require(savedVrm != null && !savedVrm.enabled && savedVrm.UpdateType == Vrm10Instance.UpdateTypes.None,
                    "Existing/saved prefab does not use the disabled VRM setup; it will not be overwritten");
                Require(savedDriver != null && savedDriver.enabled && savedDriver.Target == savedVrm,
                    "Existing/saved prefab has no matching Artifairy spring driver; it will not be overwritten");
                report.generatedPrefab = prefabPath;
                report.prefabUsesSpringOnlyDriver = true;
                report.prefabMainTexturedMaterials = ValidateMaterialBindings(saved, report.prefabRenderers);
                ValidateProductionDriver(saved, report);
            }
            ValidateLegacyCompatibility(report);
            report.passed = true;
        }
        catch (Exception e) { report.error = e.ToString(); Debug.LogError(e); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath)));
        File.WriteAllText(reportPath, JsonUtility.ToJson(report, true));
        Debug.Log("Artifairy spring validation: " + (report.passed ? "PASS" : "FAIL") + "; report=" + reportPath);
        EditorApplication.Exit(report.passed ? 0 : 1);
    }

    private static int ValidateMaterialBindings(GameObject model, List<RendererReport> output)
    {
        int boundTextures = 0;
        var mainTexturedMaterials = new HashSet<Material>();
        foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            Mesh mesh = null;
            if (renderer is SkinnedMeshRenderer skin) mesh = skin.sharedMesh;
            else if (renderer is MeshRenderer) mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
            else continue;
            Require(mesh != null, "Renderer has no mesh: " + renderer.name);
            var entry = new RendererReport { renderer = renderer.name, vertices = mesh.vertexCount, uvCoordinates = mesh.uv.Length };
            output.Add(entry);
            int rendererTextures = 0;
            foreach (var material in renderer.sharedMaterials)
            {
                Require(material != null && material.shader != null, "Renderer has a null material/shader: " + renderer.name);
                var shaderName = material.shader.name;
                Require(!string.IsNullOrEmpty(shaderName) && shaderName != "Hidden/InternalErrorShader" &&
                    shaderName.IndexOf("FallbackError", StringComparison.OrdinalIgnoreCase) < 0,
                    "Renderer uses an error shader: " + renderer.name + "/" + material.name);
                var item = new MaterialReport { material = material.name, shader = shaderName };
                entry.materials.Add(item);
                foreach (var property in material.GetTexturePropertyNames())
                {
                    var texture = material.GetTexture(property);
                    if (texture == null) continue;
                    item.textures.Add(new TextureReport { property = property, texture = texture.name, width = texture.width, height = texture.height });
                    if (property == "_MainTex" || property == "_BaseMap")
                    {
                        item.hasMainTexture = true;
                        mainTexturedMaterials.Add(material);
                    }
                    rendererTextures++;
                    boundTextures++;
                }
            }
            if (rendererTextures > 0) Require(entry.uvCoordinates == entry.vertices && entry.vertices > 0,
                "Textured renderer has missing UV coordinates: " + renderer.name);
        }
        Require(output.Count > 0 && boundTextures > 0, "Model has no renderer texture bindings");
        Require(mainTexturedMaterials.Count >= 6, "Model has fewer than six main materials with bound textures");
        return mainTexturedMaterials.Count;
    }

    // Read-only compatibility probe: only this in-memory clone receives sentinels.
    // The original imported VRM and all existing scenes/prefabs are never saved.
    private static void ValidateLegacyCompatibility(Report report)
    {
        const string legacyPath = "Assets/Model/NeEEvA.vrm";
        var result = new LegacyReport { assetPath = legacyPath };
        report.legacyCompatibility = result;
        GameObject subject = null;
        try
        {
            var original = AssetDatabase.LoadAssetAtPath<GameObject>(legacyPath);
            Require(original != null, "Existing reference VRM could not be loaded for compatibility validation");
            subject = PrefabUtility.InstantiatePrefab(original) as GameObject;
            Require(subject != null, "Existing reference VRM could not be instantiated");
            var vrm = subject.GetComponent<Vrm10Instance>();
            Require(vrm != null, "Existing reference VRM has no Vrm10Instance");
            vrm.UpdateType = Vrm10Instance.UpdateTypes.None;
            vrm.enabled = false;
            Require(subject.GetComponent<ArtifairySpringBoneDriver>() == null, "Original reference unexpectedly has an Artifairy driver");
            var renderers = subject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int blinkSentinels = 0, vowelSentinels = 0;
            foreach (var renderer in renderers)
            {
                for (int i = 0; i < renderer.sharedMesh.blendShapeCount; i++)
                {
                    var shapeName = renderer.sharedMesh.GetBlendShapeName(i);
                    var lower = shapeName.ToLowerInvariant();
                    bool blink = lower.Contains("blink") || (lower.Contains("eye") && lower.Contains("close"));
                    bool vowel = new[] { "fcl_mth_a", "fcl_mth_i", "fcl_mth_u", "fcl_mth_e", "fcl_mth_o", "a", "i", "u", "e", "o" }.Contains(lower);
                    float sentinel = 2f + i % 5;
                    if (blink || vowel)
                    {
                        sentinel = blink ? 61f : 37f + i % 7;
                        result.namedExpressionSentinels.Add(shapeName);
                        if (blink) blinkSentinels++;
                        if (vowel) vowelSentinels++;
                    }
                    renderer.SetBlendShapeWeight(i, sentinel);
                }
            }
            var weightsBefore = renderers.Select(r => Enumerable.Range(0, r.sharedMesh.blendShapeCount).Select(r.GetBlendShapeWeight).ToArray()).ToArray();
            result.checkedBlendshapeWeights = weightsBefore.Sum(w => w.Length);
            Require(result.checkedBlendshapeWeights >= 57 && blinkSentinels > 0 && vowelSentinels > 0,
                "Legacy probe did not cover the expected 57 shapes including blink/vowels");
            var foxTail = subject.GetComponentsInChildren<Transform>(true)
                .Where(t => t.name.IndexOf("FoxTail", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
            result.foxTailTransforms = foxTail.Length;
            Require(foxTail.Length >= 5, "Legacy FoxTail transforms were not found");
            var tailBefore = foxTail.ToDictionary(t => t, t => Tuple.Create(t.localPosition, t.localRotation, t.localScale));
            var head = vrm.Humanoid.Head;
            Require(head != null, "Legacy reference has no head humanoid bone");
            var headBefore = head.localRotation;
            var driver = subject.AddComponent<ArtifairySpringBoneDriver>();
            driver.Configure(vrm);
            driver.Simulate(1f / 60f);
            result.driverInitialized = driver.IsInitialized;
            Require(result.driverInitialized, "Spring-only driver did not initialize on the legacy reference");
            for (int tick = 0; tick < 120; tick++) driver.Simulate(1f / 60f);
            var joints = vrm.SpringBone.Springs.SelectMany(s => s.Joints).Distinct().ToArray();
            var baseline = joints.ToDictionary(j => j.transform, j => j.transform.localRotation);
            var motion = Exercise(vrm, baseline, "legacy_head_turn", 180, tick => {
                float t = tick / 60f;
                head.localRotation = headBefore * Quaternion.Euler(12f * Mathf.Sin(t * 2f), 25f * Mathf.Sin(t * 2.5f), 5f * Mathf.Sin(t * 3f));
            }, () => driver.Simulate(1f / 60f));
            result.maxHairLocalRotationDegrees = motion.maxHairLocalRotationDegrees;
            result.allTransformsFinite = motion.allTransformsFinite;
            result.weightsPreserved = renderers.Select((r, index) => Enumerable.Range(0, weightsBefore[index].Length)
                .All(w => r.GetBlendShapeWeight(w) == weightsBefore[index][w])).All(equal => equal);
            result.foxTailUnchanged = tailBefore.All(p => p.Key.localPosition.Equals(p.Value.Item1) &&
                p.Key.localRotation.Equals(p.Value.Item2) && p.Key.localScale.Equals(p.Value.Item3));
            result.fullVrmRemainedDisabled = !vrm.enabled && vrm.UpdateType == Vrm10Instance.UpdateTypes.None;
            Require(result.weightsPreserved && result.foxTailUnchanged && result.fullVrmRemainedDisabled && result.allTransformsFinite,
                "Legacy compatibility probe changed expressions/tail/VRM ownership or generated invalid physics");
            Require(result.maxHairLocalRotationDegrees > 0.01f, "Legacy head turn did not move hair");
            head.localRotation = headBefore;
            driver.enabled = false;
            driver.Simulate(1f / 60f);
            Require(!driver.IsInitialized, "Legacy probe retained native spring buffers after disabling");
        }
        finally { if (subject != null) UnityEngine.Object.DestroyImmediate(subject); }
    }

    private static void ValidateProductionDriver(GameObject prefab, Report report)
    {
        GameObject subject = null;
        var result = new DriverReport();
        report.productionDriver = result;
        try
        {
            subject = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            Require(subject != null, "Could not instantiate the spring-only prefab for validation");
            var vrm = subject.GetComponent<Vrm10Instance>();
            var driver = subject.GetComponent<ArtifairySpringBoneDriver>();
            Require(vrm != null && driver != null && driver.Target == vrm, "Production driver has no matching target");
            var vrmEnabledBefore = vrm.enabled;
            var updateBefore = vrm.UpdateType;
            var renderers = subject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var expressionBefore = renderers.Select(r => Enumerable.Range(0, r.sharedMesh.blendShapeCount)
                .Select(r.GetBlendShapeWeight).ToArray()).ToArray();
            result.checkedBlendshapeWeights = expressionBefore.Sum(w => w.Length);
            result.blendshapeWeightsUnchanged = true;
            result.vrmEnabledUnchanged = true;
            result.vrmUpdateTypeUnchanged = true;
            var joints = vrm.SpringBone.Springs.SelectMany(s => s.Joints).Distinct().ToArray();
            var rest = joints.ToDictionary(j => j.transform, j => j.transform.localRotation);
            var head = vrm.Humanoid.Head;
            var hips = vrm.Humanoid.Hips;
            Require(head != null && hips != null, "Production prefab is missing head/hips");
            var headRotation = head.localRotation;
            var hipsRotation = hips.localRotation;
            var hipsPosition = hips.localPosition;
            driver.Simulate(1f / 60f);
            result.initialized = driver.IsInitialized;
            Require(result.initialized, "Production driver did not initialize native physics");
            for (int i = 0; i < 120; i++) driver.Simulate(1f / 60f);
            var baseline = joints.ToDictionary(j => j.transform, j => j.transform.localRotation);
            result.motions.Add(Exercise(vrm, baseline, "production_head_turn", 240, tick => {
                float t = tick / 60f;
                head.localRotation = headRotation * Quaternion.Euler(15f * Mathf.Sin(t * 2.3f), 30f * Mathf.Sin(t * 1.8f), 8f * Mathf.Sin(t * 2.1f));
            }, () => driver.Simulate(1f / 60f)));
            CheckDriverOwnership(vrm, vrmEnabledBefore, updateBefore, renderers, expressionBefore, result);
            head.localRotation = headRotation;
            driver.enabled = false;
            driver.Simulate(1f / 60f); // Also exercises explicit disabled calls in editor mode.
            result.disabledDisposed = !driver.IsInitialized;
            result.disabledRestoredPose = rest.All(p => Quaternion.Angle(p.Key.localRotation, p.Value) < 0.1f);
            Require(result.disabledDisposed && result.disabledRestoredPose, "Disabling production driver failed to dispose/restore its spring pose");
            driver.enabled = true;
            driver.Simulate(1f / 60f);
            result.reenabledReinitialized = driver.IsInitialized;
            Require(result.reenabledReinitialized, "Reenabling production driver did not reconstruct native physics");
            for (int i = 0; i < 120; i++) driver.Simulate(1f / 60f);
            baseline = joints.ToDictionary(j => j.transform, j => j.transform.localRotation);
            result.motions.Add(Exercise(vrm, baseline, "production_hips_body_motion", 240, tick => {
                float t = tick / 60f;
                hips.localPosition = hipsPosition + new Vector3(0.04f * Mathf.Sin(t * 3f), 0.015f * Mathf.Sin(t * 6f), 0);
                hips.localRotation = hipsRotation * Quaternion.Euler(5f * Mathf.Sin(t * 2.1f), 12f * Mathf.Sin(t * 2.4f), 8f * Mathf.Sin(t * 3f));
            }, () => driver.Simulate(1f / 60f)));
            CheckDriverOwnership(vrm, vrmEnabledBefore, updateBefore, renderers, expressionBefore, result);
            Require(result.motions[0].maxHairLocalRotationDegrees > 0.01f, "Production hair does not respond to head movement");
            Require(result.motions[1].maxSkirtLocalRotationDegrees > 0.01f, "Production skirt does not respond to body movement");
            Require(result.motions.All(m => m.allTransformsFinite), "Production driver produced NaN/Infinity");
            Require(result.blendshapeWeightsUnchanged && result.vrmEnabledUnchanged && result.vrmUpdateTypeUnchanged,
                "Production driver changed expression weights or full VRM update ownership");
            hips.localPosition = hipsPosition;
            hips.localRotation = hipsRotation;
            head.localRotation = headRotation;
            driver.enabled = false;
            driver.Simulate(1f / 60f);
            Require(!driver.IsInitialized, "Final driver disable retained native buffers");
        }
        finally { if (subject != null) UnityEngine.Object.DestroyImmediate(subject); }
    }
    private static void CheckDriverOwnership(Vrm10Instance vrm, bool enabledBefore, Vrm10Instance.UpdateTypes updateBefore,
        SkinnedMeshRenderer[] renderers, float[][] weightsBefore, DriverReport result)
    {
        result.vrmEnabledUnchanged &= vrm.enabled == enabledBefore;
        result.vrmUpdateTypeUnchanged &= vrm.UpdateType == updateBefore;
        for (int r = 0; r < renderers.Length; r++)
            for (int w = 0; w < weightsBefore[r].Length; w++)
                result.blendshapeWeightsUnchanged &= renderers[r].GetBlendShapeWeight(w) == weightsBefore[r][w];
    }

    private static ModelReport Validate(string path, List<ModelReport> output)
    {
        var solver = new FixedStepSpringRuntime();
        Vrm10Instance instance = null;
        var bakedMeshes = new List<Mesh>();
        try
        {
            instance = Vrm10.LoadPathAsync(path, canLoadVrm0X: true,
                controlRigGenerationOption: ControlRigGenerationOption.None,
                showMeshes: true, awaitCaller: new ImmediateCaller(), springboneRuntime: solver).GetAwaiter().GetResult();
            Require(instance != null, "VRM load returned null: " + path);
            instance.UpdateType = Vrm10Instance.UpdateTypes.None;
            var runtime = instance.Runtime;
            Require(ReferenceEquals(runtime.SpringBone, solver), "Runtime uses unexpected spring implementation");
            var result = new ModelReport { path = path };
            output.Add(result); // Preserve diagnostic measurements even if a later assertion fails.
            result.mainTexturedMaterials = ValidateMaterialBindings(instance.gameObject, result.renderers);
            var animator = instance.GetComponent<Animator>();
            result.validHumanoid = animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman;
            Require(result.validHumanoid, "Invalid humanoid avatar");
            var springs = instance.SpringBone.Springs;
            result.springs = springs.Count;
            result.colliderGroups = instance.SpringBone.ColliderGroups.Count;
            var allJoints = springs.SelectMany(s => s.Joints).Distinct().ToArray();
            result.joints = allJoints.Length;
            result.colliders = instance.GetComponentsInChildren<VRM10SpringBoneCollider>(true).Length;
            result.migrationEndNodes = allJoints.Count(j => j.name.EndsWith("_end", StringComparison.Ordinal));
            Require(result.springs >= 2 && result.colliders > 0 && result.colliderGroups > 0, "Missing spring chains or body colliders");
            Require(springs.Any(IsHair) && springs.Any(IsSkirt), "Could not identify both hair and skirt spring chains by name");
            foreach (var spring in springs)
            {
                Require(spring.Joints.Count >= 2 && spring.Joints.All(j => j != null), "Invalid chain: " + spring.Name);
                Require(spring.ColliderGroups.Count > 0 && spring.ColliderGroups.All(g => g != null && g.Colliders.Count > 0), "Chain has no valid colliders: " + spring.Name);
                float minLength = float.MaxValue;
                for (int j = 1; j < spring.Joints.Count; j++)
                    minLength = Mathf.Min(minLength, Vector3.Distance(spring.Joints[j-1].transform.position, spring.Joints[j].transform.position));
                Require(minLength > 0.00001f, "Zero-length chain segment: " + spring.Name);
                result.chains.Add(new SpringReport { name = spring.Name, center = spring.Center == null ? "world" : spring.Center.name,
                    joints = spring.Joints.Select(j => j.name).ToArray(), linkedColliderGroups = spring.ColliderGroups.Count, minSegmentLengthMeters = minLength });
            }
            var head = instance.Humanoid.Head;
            var hips = instance.Humanoid.Hips;
            Require(head != null && hips != null, "No head/hips humanoid transforms");
            var headRotation = head.localRotation;
            var hipsRotation = hips.localRotation;
            var hipsPosition = hips.localPosition;
            // Native jobs allocate and then settle the same graph that the importer produced.
            for (int i = 0; i < 120; i++) runtime.SpringBone.Process();
            var baseline = allJoints.ToDictionary(j => j.transform, j => j.transform.localRotation);
            result.motions.Add(Exercise(instance, baseline, "head_turn", 240, (tick) => {
                float t = tick / 60f;
                head.localRotation = headRotation * Quaternion.Euler(15f * Mathf.Sin(t * 2.3f), 30f * Mathf.Sin(t * 1.8f), 8f * Mathf.Sin(t * 2.1f));
            }));
            head.localRotation = headRotation;
            runtime.SpringBone.RestoreInitialTransform();
            for (int i = 0; i < 120; i++) runtime.SpringBone.Process();
            baseline = allJoints.ToDictionary(j => j.transform, j => j.transform.localRotation);
            result.motions.Add(Exercise(instance, baseline, "hips_body_motion", 240, (tick) => {
                float t = tick / 60f;
                hips.localPosition = hipsPosition + new Vector3(0.04f * Mathf.Sin(t * 3f), 0.015f * Mathf.Sin(t * 6f), 0);
                hips.localRotation = hipsRotation * Quaternion.Euler(5f * Mathf.Sin(t * 2.1f), 12f * Mathf.Sin(t * 2.4f), 8f * Mathf.Sin(t * 3f));
            }));
            hips.localPosition = hipsPosition;
            hips.localRotation = hipsRotation;
            head.localRotation = headRotation;
            runtime.SpringBone.RestoreInitialTransform();
            for (int i = 0; i < 120; i++) runtime.SpringBone.Process();
            baseline = allJoints.ToDictionary(j => j.transform, j => j.transform.localRotation);
            var leftUpperLeg = instance.Humanoid.LeftUpperLeg;
            var rightUpperLeg = instance.Humanoid.RightUpperLeg;
            var leftLowerLeg = instance.Humanoid.LeftLowerLeg;
            var rightLowerLeg = instance.Humanoid.RightLowerLeg;
            Require(leftUpperLeg != null && rightUpperLeg != null && leftLowerLeg != null && rightLowerLeg != null,
                "Missing leg bones for collision movement validation");
            var leftUpperRotation = leftUpperLeg.localRotation;
            var rightUpperRotation = rightUpperLeg.localRotation;
            var leftLowerRotation = leftLowerLeg.localRotation;
            var rightLowerRotation = rightLowerLeg.localRotation;
            result.motions.Add(Exercise(instance, baseline, "leg_swing_and_knee_bend", 240, tick => {
                float wave = Mathf.Sin(tick / 60f * 2.4f);
                leftUpperLeg.localRotation = leftUpperRotation * Quaternion.Euler(35f * wave, 0, 0);
                rightUpperLeg.localRotation = rightUpperRotation * Quaternion.Euler(-35f * wave, 0, 0);
                leftLowerLeg.localRotation = leftLowerRotation * Quaternion.Euler(45f * (0.5f + 0.5f * wave), 0, 0);
                rightLowerLeg.localRotation = rightLowerRotation * Quaternion.Euler(45f * (0.5f - 0.5f * wave), 0, 0);
            }));
            leftUpperLeg.localRotation = leftUpperRotation;
            rightUpperLeg.localRotation = rightUpperRotation;
            leftLowerLeg.localRotation = leftLowerRotation;
            rightLowerLeg.localRotation = rightLowerRotation;
            runtime.SpringBone.RestoreInitialTransform();
            for (int i = 0; i < 120; i++) runtime.SpringBone.Process();
            baseline = allJoints.ToDictionary(j => j.transform, j => j.transform.localRotation);
            var deformJoints = new HashSet<Transform>(springs.SelectMany(s => s.Joints.Take(s.Joints.Count - 1)).Select(j => j.transform));
            var renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var probes = new List<Tuple<SkinnedMeshRenderer, Mesh, Vector3[], int[]>>();
            foreach (var renderer in renderers)
            {
                var boneMask = renderer.bones.Select(deformJoints.Contains).ToArray();
                var weights = renderer.sharedMesh.boneWeights;
                var selected = Enumerable.Range(0, weights.Length).Where(i => HasSpringWeight(weights[i], boneMask)).ToArray();
                if (selected.Length == 0) continue;
                result.weightedSpringVertices += selected.Length;
                var baked = new Mesh(); bakedMeshes.Add(baked); renderer.BakeMesh(baked);
                probes.Add(Tuple.Create(renderer, baked, baked.vertices, selected));
            }
            Require(result.weightedSpringVertices > 0, "Spring joints have no skinned vertices");
            result.motions.Add(Exercise(instance, baseline, "wind_skin_deformation", 180, (tick) => {
                runtime.SpringBone.SetModelLevel(instance.transform, new BlittableModelLevel { ExternalForce = new Vector3(0.08f, 0, 0.04f), SupportsScalingAtRuntime = true });
            }));
            foreach (var probe in probes)
            {
                probe.Item1.BakeMesh(probe.Item2);
                var after = probe.Item2.vertices;
                foreach (var index in probe.Item4)
                    result.maxSpringSkinDisplacementMeters = Mathf.Max(result.maxSpringSkinDisplacementMeters, Vector3.Distance(probe.Item3[index], after[index]));
            }
            Require(result.maxSpringSkinDisplacementMeters > 0.00001f, "Solver moved no spring-weighted mesh vertices");
            Require(result.motions[0].maxHairLocalRotationDegrees > 0.01f, "Hair does not respond to head rotation");
            Require(result.motions[1].maxSkirtLocalRotationDegrees > 0.01f, "Skirt does not respond to body motion");
            Require(result.motions.All(m => m.allTransformsFinite), "Physics generated NaN or Infinity");
            Debug.Log("Validated " + path + ": springs=" + result.springs + ", joints=" + result.joints + ", colliders=" + result.colliders);
            return result;
        }
        finally
        {
            foreach (var mesh in bakedMeshes) UnityEngine.Object.DestroyImmediate(mesh);
            if (instance != null) UnityEngine.Object.DestroyImmediate(instance.gameObject);
            solver.Dispose();
        }
    }

    private static MotionReport Exercise(Vrm10Instance instance, Dictionary<Transform, Quaternion> baseline, string phase, int ticks, Action<int> animate, Action physicsTick = null)
    {
        var report = new MotionReport { phase = phase, ticks = ticks };
        for (int tick = 0; tick < ticks; tick++)
        {
            animate(tick);
            if (physicsTick == null) instance.Runtime.SpringBone.Process();
            else physicsTick();
            foreach (var spring in instance.SpringBone.Springs)
            {
                foreach (var joint in spring.Joints.Take(spring.Joints.Count - 1))
                {
                    var tf = joint.transform;
                    report.allTransformsFinite &= Finite(tf.position) && Finite(tf.localRotation);
                    var angle = Quaternion.Angle(baseline[tf], tf.localRotation);
                    if (IsHair(spring)) report.maxHairLocalRotationDegrees = Mathf.Max(report.maxHairLocalRotationDegrees, angle);
                    if (IsSkirt(spring)) report.maxSkirtLocalRotationDegrees = Mathf.Max(report.maxSkirtLocalRotationDegrees, angle);
                }
                for (int j = 1; j < spring.Joints.Count; j++)
                {
                    var tail = spring.Joints[j].transform.position;
                    report.allTransformsFinite &= Finite(tail);
                    foreach (var collider in spring.ColliderGroups.SelectMany(g => g.Colliders))
                    {
                        if (collider.ColliderType != VRM10SpringBoneColliderTypes.Sphere) continue;
                        var scale = collider.transform.lossyScale;
                        var radius = collider.Radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                        var clearance = Vector3.Distance(tail, collider.transform.TransformPoint(collider.Offset)) - radius - spring.Joints[j-1].m_jointRadius;
                        report.minSphereTailClearanceMeters = Mathf.Min(report.minSphereTailClearanceMeters, clearance);
                        if (IsHair(spring)) report.minHairSphereTailClearanceMeters = Mathf.Min(report.minHairSphereTailClearanceMeters, clearance);
                        if (IsSkirt(spring)) report.minSkirtSphereTailClearanceMeters = Mathf.Min(report.minSkirtSphereTailClearanceMeters, clearance);
                    }
                }
            }
        }
        return report;
    }
    private static bool IsHair(Vrm10InstanceSpringBone.Spring s) { return s.Name.IndexOf("hair", StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.Contains("发"); }
    private static bool IsSkirt(Vrm10InstanceSpringBone.Spring s) { return s.Name.IndexOf("skirt", StringComparison.OrdinalIgnoreCase) >= 0 || s.Name.Contains("裙"); }
    private static bool HasSpringWeight(BoneWeight w, bool[] mask)
    {
        return (w.weight0 > 0.0001f && w.boneIndex0 < mask.Length && mask[w.boneIndex0]) ||
            (w.weight1 > 0.0001f && w.boneIndex1 < mask.Length && mask[w.boneIndex1]) ||
            (w.weight2 > 0.0001f && w.boneIndex2 < mask.Length && mask[w.boneIndex2]) ||
            (w.weight3 > 0.0001f && w.boneIndex3 < mask.Length && mask[w.boneIndex3]);
    }
    private static bool Finite(float n) { return !float.IsNaN(n) && !float.IsInfinity(n); }
    private static bool Finite(Vector3 v) { return Finite(v.x) && Finite(v.y) && Finite(v.z); }
    private static bool Finite(Quaternion q) { return Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
#endif
