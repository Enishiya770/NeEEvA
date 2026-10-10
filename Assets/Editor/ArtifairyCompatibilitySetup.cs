#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using NeEEvA.Motion;
using Newtonsoft.Json;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

/// <summary>Author a separate avatar prefab and private chat scene; never enter Play Mode.</summary>
public static class ArtifairyCompatibilitySetup
{
    private static string Model;
    private static string Prefab;
    private const string SourceScene = "Assets/Private/SailingMoon/Scenes/NeEEvA_SailingMoon_Day_Chat.unity";
    private static string DestinationScene;
    private static string RestAsset;
    private static string Report;
    private static readonly string[] RequiredShapes = { "Fcl_EYE_Close", "Fcl_MTH_A", "Fcl_MTH_I", "Fcl_MTH_U", "Fcl_MTH_E", "Fcl_MTH_O" };
    private static readonly Dictionary<string, object> Results = new Dictionary<string, object>();
    private static Dictionary<string, MissingReference> SourceMissing;

    private sealed class MissingReference
    {
        public string key, hierarchy, componentType, property, objectIdentity, localObjectIdentity;
        public int referencedInstanceID;
    }

    public static void Run()
    {
        SelectProfile(false);
        RunCore();
    }

    public static void RunRenderVariant()
    {
        SelectProfile(true);
        RunCore();
    }

    private static void SelectProfile(bool render)
    {
        string stem = render ? "Artifairy_RenderCompatible" : "Artifairy_Compatible";
        Model = "Assets/Model/Artifairy/" + stem + ".vrm";
        Prefab = "Assets/Model/Artifairy/" + stem + ".prefab";
        DestinationScene = "Assets/Private/Artifairy/Scenes/" + (render ? "Artifairy_RenderChat" : "Artifairy_Chat") + ".unity";
        RestAsset = "Assets/AIChatTookit/Resources/ARDY/RestPoses/" + stem + "-rest.asset";
        Report = "E:/Artifairy/.vrm_work/" + (render ? "unity_render_compatibility_validation" : "unity_compatibility_validation") + ".json";
    }

    private static void RunCore()
    {
        int exit = 1;
        Results.Clear();
        var protectedPaths = new[] { SourceScene, "Assets/Model/NeEEvA.vrm", "Assets/AIChatTookit/Animation/Animator Controller.controller" };
        var before = protectedPaths.ToDictionary(p => p, Hash);
        try
        {
            Require(!EditorApplication.isPlaying, "Setup requires Edit Mode.");
            ImportModel();
            CaptureSourceMissingReferences();
            EnsureFolder(Path.GetDirectoryName(DestinationScene).Replace('\\', '/'));
            if (!File.Exists(DestinationScene)) Require(AssetDatabase.CopyAsset(SourceScene, DestinationScene), "Could not copy the source scene.");
            var scene = EditorSceneManager.OpenScene(DestinationScene, OpenSceneMode.Single);
            // OpenScene unloads unused imported assets. Native objects do not stay alive
            // merely because a managed local holds their wrappers; load AFTER this boundary.
            var imported = LoadImported();
            BakeRestPose(imported);
            var bridge = Components<ArdyRoomDialogueBridge>(scene).Single(b => b.avatar != null);
            Vrm10Instance current = bridge.avatar;
            bool alreadyCompatible = current.Vrm == imported.Vrm;
            if (!alreadyCompatible) Require(Hash(DestinationScene) == before[SourceScene], "Refusing to edit an unrelated existing destination scene.");
            if (!alreadyCompatible)
            {
                var oldRoot = current.gameObject;
                var oldSupport = FootSupport(current);
                float supportY = oldSupport.worldY;
                var asset = CreatePrefab(imported, current);
                var replacement = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene);
                replacement.name = oldRoot.name;
                replacement.transform.SetParent(oldRoot.transform.parent, false);
                replacement.transform.SetSiblingIndex(oldRoot.transform.GetSiblingIndex());
                replacement.transform.localPosition = oldRoot.transform.localPosition;
                replacement.transform.localRotation = oldRoot.transform.localRotation;
                replacement.transform.localScale = oldRoot.transform.localScale;
                var target = replacement.GetComponent<Vrm10Instance>();
                replacement.transform.position += Vector3.up * (supportY - SupportY(target));
                var newSupport = FootSupport(target);
                float supportError = Mathf.Abs(supportY - newSupport.worldY);
                Require(supportError < 0.003f, "New and old feet support planes differ by more than 3 mm.");
                Results["feetAlignment"] = new { previous = oldSupport, compatible = newSupport, errorMeters = supportError,
                    importedPivotGroundY = ArdyLocomotionAvatarReference.FromImportedPrefab(imported, Model).groundY };
                CopyAnimator(current.GetComponent<Animator>(), target.GetComponent<Animator>());
                CopyApplicationComponents(current, target);
                var map = MakeMap(current, target);
                int changed = Rebind(scene, oldRoot, map);
                ConfigureBindings(scene, current, target, imported);
                RequireNoOldReferences(scene, oldRoot);
                Object.DestroyImmediate(oldRoot);
                Results["reboundObjectReferences"] = changed;
                Results["supportPlaneWorldY"] = supportY;
                current = target;
            }
            else
            {
                Require(current.GetComponent<ArtifairySpringBoneDriver>()?.Target == current, "Existing destination is not our compatible scene.");
                ConfigureBindings(scene, null, current, imported);
                Results["reusedOwnedDestination"] = true;
            }
            foreach (var component in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Component>(true)).Where(c => c != null))
                if (PrefabUtility.IsPartOfPrefabInstance(component)) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            ValidateSceneBindings(scene, current, imported);
            ValidateAvatar(current);
            Require(EditorSceneManager.SaveScene(scene, DestinationScene), "Could not save compatible scene.");
            // Reload the saved scene so serialization/reference checks cover the deliverable.
            scene = EditorSceneManager.OpenScene(DestinationScene, OpenSceneMode.Single);
            imported = LoadImported();
            current = Components<ArdyRoomDialogueBridge>(scene).Single(b => b.avatar != null).avatar;
            ValidateSceneBindings(scene, current, imported);
            foreach (var pair in before) Require(Hash(pair.Key) == pair.Value, "A protected source file changed: " + pair.Key);
            Results["protectedSourceHashesUnchanged"] = true;
            Results["scene"] = DestinationScene;
            Results["prefab"] = Prefab;
            Results["restPose"] = RestAsset;
            Results["pass"] = true;
            exit = 0;
        }
        catch (Exception error)
        {
            Results["pass"] = false;
            // Only our authored validation messages/method stack; never serialize component config.
            Results["error"] = error.GetBaseException().Message;
            Debug.LogException(error);
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Report));
            File.WriteAllText(Report, JsonConvert.SerializeObject(Results, Formatting.Indented));
            if (Application.isBatchMode) EditorApplication.Exit(exit);
        }
    }

    private static Vrm10Instance LoadImported()
    {
        var asset = AssetDatabase.LoadAssetAtPath<GameObject>(Model);
        var imported = asset != null ? asset.GetComponent<Vrm10Instance>() : null;
        Require(imported != null && imported.Vrm != null && !imported.gameObject.scene.IsValid(), "Compatible VRM import asset is unavailable.");
        return imported;
    }

    private static void CaptureSourceMissingReferences()
    {
        // A previously generated destination is never trusted as the missing-reference baseline.
        var loaded = SceneManager.GetSceneByPath(SourceScene);
        if (loaded.IsValid() && loaded.isLoaded)
            Require(EditorSceneManager.CloseScene(loaded, true), "Could not unload the source scene for a fresh read-only baseline.");
        var source = EditorSceneManager.OpenScene(SourceScene, OpenSceneMode.Additive);
        try
        {
            SourceMissing = FindMissingReferences(source).ToDictionary(r => r.key, r => r);
            Results["sourceMissingReferenceCount"] = SourceMissing.Count;
            Results["sourceMissingReferences"] = SourceMissing.Values.ToArray();
            Results["missingBaselineSourceSha256"] = Hash(SourceScene);
        }
        finally { Require(EditorSceneManager.CloseScene(source, true), "Could not close the read-only source scene."); }
    }

    private static void ImportModel()
    {
        Require(File.Exists(Model), "Missing compatible VRM: " + Model);
        AssetDatabase.ImportAsset(Model, ImportAssetOptions.ForceSynchronousImport);
        var importer = AssetImporter.GetAtPath(Model);
        Require(importer != null && importer.GetType().FullName == "UniVRM10.VrmScriptedImporter", "Unexpected VRM importer.");
        // VRM10.Editor has autoReferenced=false; use Unity's serialized importer contract.
        var serialized = new SerializedObject(importer);
        var migration = serialized.FindProperty("MigrateToVrm1");
        Require(migration != null, "VRM importer migration property missing.");
        if (!migration.boolValue)
        {
            migration.boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();
        }
    }

    private static void BakeRestPose(Vrm10Instance imported)
    {
        EnsureFolder(Path.GetDirectoryName(RestAsset).Replace('\\', '/'));
        var rest = AssetDatabase.LoadAssetAtPath<ArdyAvatarRestPose>(RestAsset);
        if (rest == null)
        {
            Require(!File.Exists(RestAsset), "The new rest path is occupied by another asset.");
            rest = ScriptableObject.CreateInstance<ArdyAvatarRestPose>();
            AssetDatabase.CreateAsset(rest, RestAsset);
        }
        else Require(rest.avatar == imported.Vrm && rest.sourcePath == Model, "Refusing to overwrite an unrelated rest pose.");
        Require(!imported.gameObject.scene.IsValid(), "Rest pose must come from the uninstantiated imported asset.");
        rest.avatar = imported.Vrm;
        rest.schema = 1;
        rest.sourcePath = Model;
        rest.sourceSha256 = Hash(Model);
        rest.sourceDependencyHash = AssetDatabase.GetAssetDependencyHash(Model).ToString();
        var inverseRoot = Quaternion.Inverse(imported.transform.rotation);
        rest.entries = Enum.GetValues(typeof(HumanBodyBones)).Cast<HumanBodyBones>()
            .Where(b => b < HumanBodyBones.LastBone && imported.Humanoid.GetBoneTransform(b) != null)
            .Select(b => new ArdyAvatarRestPose.Entry { bone = b, restInRoot = (inverseRoot * imported.Humanoid.GetBoneTransform(b).rotation).normalized }).ToArray();
        rest.Validate();
        EditorUtility.SetDirty(rest);
        AssetDatabase.SaveAssetIfDirty(rest);
        Results["bakedRestBoneCount"] = rest.entries.Length;
    }

    private static GameObject CreatePrefab(Vrm10Instance imported, Vrm10Instance source)
    {
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(Prefab);
        if (existing != null)
        {
            var owned = existing.GetComponent<Vrm10Instance>();
            Require(owned != null && owned.Vrm == imported.Vrm && existing.GetComponent<ArtifairySpringBoneDriver>()?.Target == owned,
                "Refusing to overwrite an unrelated compatible prefab.");
            return existing;
        }
        Require(!File.Exists(Prefab), "The new prefab path is occupied.");
        var temp = Object.Instantiate(imported.gameObject);
        try
        {
            temp.name = Path.GetFileNameWithoutExtension(Prefab);
            var target = temp.GetComponent<Vrm10Instance>();
            target.enabled = false;
            target.UpdateType = Vrm10Instance.UpdateTypes.None;
            CopyAnimator(source.GetComponent<Animator>(), target.GetComponent<Animator>());
            CopyApplicationComponents(source, target);
            var map = MakeMap(source, target);
            RewriteReferences(temp.GetComponentsInChildren<Component>(true), source.gameObject, map, true);
            var face = Face(target);
            var blink = temp.GetComponent<BlinkController>() ?? temp.AddComponent<BlinkController>();
            blink.skinnedMeshRenderer = face;
            blink.blinkBlendIndex = Shape(face, "Fcl_EYE_Close");
            var eyes = temp.GetComponent<NevaEyeTracking>() ?? temp.AddComponent<NevaEyeTracking>();
            eyes.Configure(target, null);
            var spring = temp.GetComponent<ArtifairySpringBoneDriver>() ?? temp.AddComponent<ArtifairySpringBoneDriver>();
            spring.Configure(target);
            ConfigureSync(target, face);
            var asset = PrefabUtility.SaveAsPrefabAsset(temp, Prefab);
            Require(asset != null, "Could not save compatible prefab.");
            return asset;
        }
        finally { Object.DestroyImmediate(temp); }
    }

    private static bool ApplicationComponent(Component component) => component is MonoBehaviour &&
        !(component is Vrm10Instance) && !(component is NevaTailSpring) && !(component is ArtifairySpringBoneDriver) &&
        !(component is AvatarBlendShapeSync) && !(component is NevaEyeTracking) &&
        !(component.GetType().Namespace ?? "").StartsWith("Uni", StringComparison.Ordinal);

    private static void CopyApplicationComponents(Vrm10Instance source, Vrm10Instance target)
    {
        foreach (var original in source.GetComponents<Component>().Where(c => c != null && ApplicationComponent(c)))
        {
            var copy = target.GetComponent(original.GetType()) ?? target.gameObject.AddComponent(original.GetType());
            ComponentUtility.CopyComponent(original);
            Require(ComponentUtility.PasteComponentValues(copy), "Could not copy component " + original.GetType().Name);
        }
        var originalEye = source.GetComponent<NevaEyeTracking>();
        var eye = target.GetComponent<NevaEyeTracking>() ?? target.gameObject.AddComponent<NevaEyeTracking>();
        if (originalEye != null)
        {
            eye.enabled = originalEye.enabled;
            eye.smoothTime = originalEye.smoothTime;
            eye.minimumTargetDistance = originalEye.minimumTargetDistance;
        }
        // Copying hidden eye calibration would preserve old bones and a stale rest frame.
        eye.Configure(target, originalEye != null ? originalEye.gazeTarget : null);
        target.enabled = false;
        target.UpdateType = Vrm10Instance.UpdateTypes.None;
    }

    private static void CopyAnimator(Animator original, Animator target)
    {
        Require(original != null && target != null && target.avatar != null && target.avatar.isValid && target.avatar.isHuman,
            "The new model needs its own valid Humanoid Avatar.");
        var ownAvatar = target.avatar;
        target.runtimeAnimatorController = original.runtimeAnimatorController;
        target.applyRootMotion = original.applyRootMotion;
        target.updateMode = original.updateMode;
        target.cullingMode = original.cullingMode;
        target.enabled = original.enabled;
        Require(target.avatar == ownAvatar && target.runtimeAnimatorController != null, "Animator controller assignment failed.");
    }

    private static Dictionary<Object, Object> MakeMap(Vrm10Instance old, Vrm10Instance next)
    {
        var map = new Dictionary<Object, Object>();
        void Pair(Object a, Object b) { if (a != null && b != null) map[a] = b; }
        void TransformPair(Transform a, Transform b) { if (a != null && b != null) { Pair(a, b); Pair(a.gameObject, b.gameObject); } }
        TransformPair(old.transform, next.transform);
        Pair(old, next);
        Pair(old.Humanoid, next.Humanoid);
        Pair(old.Vrm, next.Vrm);
        var oldAnimator = old.GetComponent<Animator>();
        var newAnimator = next.GetComponent<Animator>();
        Pair(oldAnimator, newAnimator); Pair(oldAnimator.avatar, newAnimator.avatar);
        foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            if (bone < HumanBodyBones.LastBone) TransformPair(old.Humanoid.GetBoneTransform(bone), next.Humanoid.GetBoneTransform(bone));
        var nextTransforms = next.GetComponentsInChildren<Transform>(true);
        foreach (var node in old.GetComponentsInChildren<Transform>(true))
        {
            if (!map.ContainsKey(node))
            {
                var same = nextTransforms.Where(n => n.name == node.name).ToArray();
                if (same.Length == 1) TransformPair(node, same[0]);
            }
        }
        var face = Face(next);
        foreach (var renderer in old.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (renderer.sharedMesh?.GetBlendShapeIndex("Fcl_EYE_Close") >= 0) { Pair(renderer, face); TransformPair(renderer.transform, face.transform); }
            else
            {
                var match = next.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.name == renderer.name).ToArray();
                if (match.Length == 1) { Pair(renderer, match[0]); TransformPair(renderer.transform, match[0].transform); }
            }
        }
        foreach (var component in old.GetComponentsInChildren<Component>(true).Where(c => c != null))
        {
            if (component is NevaTailSpring) { map[component] = null; continue; }
            if (map.ContainsKey(component)) continue;
            if (map.TryGetValue(component.gameObject, out Object mapped) && mapped is GameObject mappedGo)
                Pair(component, mappedGo.GetComponent(component.GetType()));
        }
        return map;
    }

    private static int Rebind(Scene scene, GameObject oldRoot, Dictionary<Object, Object> map) =>
        RewriteReferences(scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Component>(true))
            .Where(c => c != null && !Inside(c, oldRoot)), oldRoot, map, false);

    private static int RewriteReferences(IEnumerable<Component> components, GameObject oldRoot, Dictionary<Object, Object> map, bool clearSceneReferences)
    {
        int changed = 0;
        foreach (var component in components.Where(c => c != null))
        {
            var serialized = new SerializedObject(component);
            var iterator = serialized.GetIterator();
            bool dirty = false;
            while (iterator.Next(true))
            {
                if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (StructuralReference(iterator.propertyPath)) continue;
                var value = iterator.objectReferenceValue;
                if (value == null) continue;
                if (map.TryGetValue(value, out Object replacement))
                {
                    iterator.objectReferenceValue = replacement; dirty = true; changed++;
                }
                else if (Inside(value, oldRoot))
                    throw new InvalidOperationException("Unmapped avatar reference: " + component.GetType().Name + "." + iterator.propertyPath + " -> " + value.name);
                else if (clearSceneReferences && IsSceneObject(value) && !Inside(value, component.transform.root.gameObject))
                { iterator.objectReferenceValue = null; dirty = true; }
            }
            if (dirty) serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        return changed;
    }

    private static void ConfigureBindings(Scene scene, Vrm10Instance old, Vrm10Instance target, Vrm10Instance imported)
    {
        target.enabled = false;
        target.UpdateType = Vrm10Instance.UpdateTypes.None;
        var face = Face(target);
        var blink = target.GetComponent<BlinkController>();
        Require(blink != null, "Missing copied BlinkController.");
        blink.skinnedMeshRenderer = face;
        blink.blinkBlendIndex = Shape(face, "Fcl_EYE_Close");
        var eye = target.GetComponent<NevaEyeTracking>();
        Require(eye != null, "Missing eye tracking.");
        var originalEye = old != null ? old.GetComponent<NevaEyeTracking>() : null;
        eye.Configure(target, originalEye != null ? originalEye.gazeTarget : eye.gazeTarget);
        var spring = target.GetComponent<ArtifairySpringBoneDriver>();
        Require(spring != null, "Missing spring-only driver.");
        spring.Configure(target);
        ConfigureSync(target, face);
        foreach (var lip in Components<Audio2LipScript>(scene))
        {
            // Source scene's only avatar lip renderer has just been retargeted above.
            if (lip.meshRenderer == face || (old != null && Inside(lip.meshRenderer, old.gameObject))) BindLip(lip, face);
        }
        foreach (var room in Components<ArdyRoomDialogueBridge>(scene).Where(b => b.avatar == target || b.avatar == old))
        {
            room.avatar = target;
            room.importedReference = ArdyLocomotionAvatarReference.FromImportedPrefab(imported, Model);
            EditorUtility.SetDirty(room);
        }
        foreach (var chat in Components<ChatSample>(scene))
        {
            var serialized = new SerializedObject(chat);
            var animator = serialized.FindProperty("m_Animator");
            var vision = serialized.FindProperty("m_SceneVisionAvatar");
            Require(animator != null && vision != null, "ChatSample avatar binding properties changed.");
            animator.objectReferenceValue = target.GetComponent<Animator>();
            vision.objectReferenceValue = target;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }

    private static void ConfigureSync(Vrm10Instance avatar, SkinnedMeshRenderer primary)
    {
        var extra = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true)
            .Where(r => r != primary && r.sharedMesh != null && RequiredShapes.Any(s => r.sharedMesh.GetBlendShapeIndex(s) >= 0)).ToArray();
        if (extra.Length == 0) return; // The authored merged Face normally needs no secondary writer.
        var sync = avatar.GetComponent<AvatarBlendShapeSync>() ?? avatar.gameObject.AddComponent<AvatarBlendShapeSync>();
        sync.Configure(primary, extra);
    }

    private static void BindLip(Audio2LipScript lip, SkinnedMeshRenderer face)
    {
        lip.meshRenderer = face;
        lip.m_VisemeIndex = new Audio2LipScript.VisemeBlenderShapeIndexMap
        {
            A = Shape(face, "Fcl_MTH_A"), I = Shape(face, "Fcl_MTH_I"), U = Shape(face, "Fcl_MTH_U"),
            E = Shape(face, "Fcl_MTH_E"), O = Shape(face, "Fcl_MTH_O")
        };
        EditorUtility.SetDirty(lip);
    }

    private static void ValidateSceneBindings(Scene scene, Vrm10Instance avatar, Vrm10Instance imported)
    {
        Require(avatar != null && avatar.Vrm == imported.Vrm && !avatar.enabled && avatar.UpdateType == Vrm10Instance.UpdateTypes.None, "Incorrect scene VRM ownership.");
        Require(avatar.GetComponent<NevaTailSpring>() == null, "Tail-specific updater must not be attached.");
        var face = Face(avatar);
        var blink = avatar.GetComponent<BlinkController>();
        Require(blink != null && blink.skinnedMeshRenderer == face && blink.blinkBlendIndex == Shape(face, "Fcl_EYE_Close"), "Invalid blink binding.");
        var eye = avatar.GetComponent<NevaEyeTracking>();
        Require(eye != null && eye.IsInitialized && eye.Target == avatar && eye.gazeTarget != null, "Invalid eye/camera binding.");
        Require(avatar.GetComponent<ArtifairySpringBoneDriver>()?.Target == avatar, "Invalid spring binding.");
        Require(Components<Audio2LipScript>(scene).Any(l => l.meshRenderer == face && l.m_VisemeIndex != null &&
            l.m_VisemeIndex.A == Shape(face, "Fcl_MTH_A") && l.m_VisemeIndex.I == Shape(face, "Fcl_MTH_I") &&
            l.m_VisemeIndex.U == Shape(face, "Fcl_MTH_U") && l.m_VisemeIndex.E == Shape(face, "Fcl_MTH_E") &&
            l.m_VisemeIndex.O == Shape(face, "Fcl_MTH_O")), "Invalid vowel binding.");
        foreach (var bridge in Components<ArdyRoomDialogueBridge>(scene))
        {
            Require(bridge.avatar == avatar && bridge.importedReference.avatar == imported.Vrm && bridge.importedReference.sourcePath == Model, "Invalid locomotion reference.");
            bridge.importedReference.Validate();
        }
        var rest = ArdyAvatarRestPose.Find(avatar);
        Require(rest != null && rest.sourceSha256 == Hash(Model), "Missing new model ARDY rest reference.");
        foreach (var chat in Components<ChatSample>(scene))
        {
            var serialized = new SerializedObject(chat);
            Require(serialized.FindProperty("m_Animator").objectReferenceValue == avatar.GetComponent<Animator>() &&
                serialized.FindProperty("m_SceneVisionAvatar").objectReferenceValue == avatar, "Saved chat action/vision avatar references are invalid.");
        }
        Require(Components<Vrm10Instance>(scene).Count() == 1, "The derivative scene still has another avatar.");
        var missing = FindMissingReferences(scene).ToArray();
        var avatarPrefix = Hierarchy(avatar.transform);
        var added = missing.Where(r => r.hierarchy == avatarPrefix || r.hierarchy.StartsWith(avatarPrefix + "/", StringComparison.Ordinal) ||
            SourceMissing == null || !SourceMissing.ContainsKey(r.key)).ToArray();
        var inherited = missing.Where(r => !added.Contains(r)).ToArray();
        Results["inheritedMissingReferenceCount"] = inherited.Length;
        Results["inheritedMissingReferences"] = inherited;
        Results["addedMissingReferences"] = added;
        Require(added.Length == 0, "New missing serialized reference: " + string.Join("; ", added.Select(r => r.hierarchy + " :: " + r.componentType + "." + r.property + " [" + r.objectIdentity + "]")));
        Results["savedSceneReferenceValidation"] = true;
    }

    private static void ValidateAvatar(Vrm10Instance sceneAvatar)
    {
        var probeScene = EditorSceneManager.NewPreviewScene();
        var probe = Object.Instantiate(sceneAvatar.gameObject);
        SceneManager.MoveGameObjectToScene(probe, probeScene);
        try
        {
            var avatar = probe.GetComponent<Vrm10Instance>();
            var face = Face(avatar);
            ZeroShapes(face);
            var baseline = Bake(face);
            var blink = probe.GetComponent<BlinkController>();
            blink.blinkWeight = 75f;
            Invoke(typeof(BlinkController), "Start", blink);
            float blinkDelta = Delta(baseline, Bake(face));
            Require(blinkDelta > 0.00001f && face.GetBlendShapeWeight(blink.blinkBlendIndex) == 75f, "Blink callback did not deform the face.");
            Results["blinkMaxVertexDisplacementMeters"] = blinkDelta;
            ZeroShapes(face);
            var lipGo = new GameObject("Offline synthetic viseme probe");
            SceneManager.MoveGameObjectToScene(lipGo, probeScene);
            var lip = lipGo.AddComponent<Audio2LipScript>();
            BindLip(lip, face);
            // Never call Audio2Lip.Awake/Update audio processing or its native CreateContext.
            var frame = (OVRLipSync.Frame)typeof(Audio2LipScript).GetField("frame", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(lip);
            var vowelResults = new Dictionary<string, float>();
            var visemes = new[] { OVRLipSync.Viseme.aa, OVRLipSync.Viseme.ih, OVRLipSync.Viseme.ou, OVRLipSync.Viseme.E, OVRLipSync.Viseme.oh };
            var names = new[] { "A", "I", "U", "E", "O" };
            for (int n = 0; n < names.Length; n++)
            {
                ZeroShapes(face); Array.Clear(frame.Visemes, 0, frame.Visemes.Length); frame.Visemes[(int)visemes[n]] = 0.75f;
                Invoke(typeof(Audio2LipScript), "SetBlenderShapes", lip);
                float displacement = Delta(baseline, Bake(face));
                Require(displacement > 0.00001f && face.GetBlendShapeWeight(Shape(face, "Fcl_MTH_" + names[n])) > 50f, "Vowel callback did not deform: " + names[n]);
                vowelResults[names[n]] = displacement;
            }
            Results["vowelMaxVertexDisplacementsMeters"] = vowelResults;
            Object.DestroyImmediate(lipGo);
            var emotionResults = new Dictionary<string, float>();
            foreach (string emotion in new[] { "Angry", "Fun", "Joy", "Sorrow", "Surprised" })
            {
                ZeroShapes(face);
                face.SetBlendShapeWeight(Shape(face, "Fcl_ALL_" + emotion), 75f);
                float displacement = Delta(baseline, Bake(face));
                Require(displacement > 0.00001f, "Emotion shape has no actual deformation: " + emotion);
                emotionResults[emotion] = displacement;
            }
            Results["emotionMaxVertexDisplacementsMeters"] = emotionResults;
            ZeroShapes(face);
            var eye = probe.GetComponent<NevaEyeTracking>();
            eye.Configure(avatar, null);
            var eyeCenter = eye.Head.InverseTransformPoint((eye.LeftEye.position + eye.RightEye.position) * 0.5f);
            float originDifference = Vector3.Distance(eyeCenter, avatar.Vrm.LookAt.OffsetFromHead);
            Results["eyeOriginCalibrationErrorMeters"] = originDifference;
            Results["eyeBoneCenterInHead"] = new[] { eyeCenter.x, eyeCenter.y, eyeCenter.z };
            Require(originDifference < 0.005f, "VRM LookAt origin differs from the actual eye center by more than 5 mm.");
            int leftIndex = Array.IndexOf(face.bones, eye.LeftEye), rightIndex = Array.IndexOf(face.bones, eye.RightEye);
            Require(leftIndex >= 0 && rightIndex >= 0, "Face skin is missing eye bones.");
            var eyeVertices = face.sharedMesh.boneWeights.Select((w, i) => new { w, i }).Where(v => EyeWeight(v.w, leftIndex) > 0 || EyeWeight(v.w, rightIndex) > 0).Select(v => v.i).ToArray();
            int leftCount = face.sharedMesh.boneWeights.Count(w => EyeWeight(w, leftIndex) > 0), rightCount = face.sharedMesh.boneWeights.Count(w => EyeWeight(w, rightIndex) > 0);
            Require(leftCount > 0 && rightCount > 0, "Both eye bones require real nonzero skin weights.");
            var neutralEyes = Bake(face); var leftRest = eye.LeftEye.localRotation; var rightRest = eye.RightEye.localRotation;
            Invoke(typeof(NevaEyeTracking), "ApplyEyes", eye, new Vector2(45f, 20f));
            float eyeDisplacement = Delta(neutralEyes, Bake(face), eyeVertices);
            Require(eyeDisplacement > 0.00001f && Quaternion.Angle(leftRest, eye.LeftEye.localRotation) > 0.05f && Quaternion.Angle(rightRest, eye.RightEye.localRotation) > 0.05f, "Eye tracking did not move skinned iris vertices.");
            Results["weightedEyeVertexCounts"] = new[] { leftCount, rightCount };
            Results["irisMaxVertexDisplacementMeters"] = eyeDisplacement;
            eye.LeftEye.localRotation = leftRest; eye.RightEye.localRotation = rightRest;
            ValidateAnimation(probe.GetComponent<Animator>());
            var player = probe.GetComponent<ArdyMotionPlayer>() ?? probe.AddComponent<ArdyMotionPlayer>();
            player.Bind(avatar);
            Require(player.Target == avatar && player.BoundBoneCount >= 3, "New rest reference failed ARDY motion binding.");
            Results["ardyBoundBones"] = player.BoundBoneCount;
            ValidateSprings(avatar);
        }
        finally { Object.DestroyImmediate(probe); EditorSceneManager.ClosePreviewScene(probeScene); }
    }

    private static void ValidateAnimation(Animator animator)
    {
        Require(animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman && animator.runtimeAnimatorController != null, "Invalid Humanoid animation setup.");
        Require(animator.parameters.Any(p => p.name == "state" && p.type == UnityEngine.AnimatorControllerParameterType.Int), "Controller lacks integer state parameter.");
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.Rebind();
        var arm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Quaternion start = arm.localRotation;
        float maximum = 0;
        var states = new List<object>();
        var visitedStates = new HashSet<int>();
        for (int state = 0; state <= 2; state++)
        {
            animator.SetInteger("state", state);
            for (int i = 0; i < 90; i++) { animator.Update(1f / 60f); maximum = Mathf.Max(maximum, Quaternion.Angle(start, arm.localRotation)); }
            var info = animator.GetCurrentAnimatorStateInfo(0);
            visitedStates.Add(info.fullPathHash);
            states.Add(new { parameter = state, stateHash = info.fullPathHash, normalizedTime = info.normalizedTime });
            Require(info.fullPathHash != 0 && info.normalizedTime > 0, "Animator did not evaluate a local action state.");
        }
        Require(visitedStates.Count == 3, "Idle/thinking/answering parameters did not reach three distinct controller states.");
        Require(maximum > 0.05f, "Humanoid local clips did not animate the new skeleton.");
        Results["animatorStates"] = states;
        Results["animatorArmMaxRotationDegrees"] = maximum;
        animator.Rebind(); animator.Update(0f);
    }

    private static void ValidateSprings(Vrm10Instance avatar)
    {
        var driver = avatar.GetComponent<ArtifairySpringBoneDriver>();
        Require(driver != null && avatar.SpringBone.Springs.Count > 0 && avatar.SpringBone.ColliderGroups.Count > 0, "Missing embedded spring physics.");
        var joints = avatar.SpringBone.Springs.SelectMany(s => s.Joints).Where(j => j != null).Select(j => j.transform).Distinct().ToArray();
        var rotations = joints.Select(j => j.localRotation).ToArray();
        var head = avatar.Humanoid.GetBoneTransform(HumanBodyBones.Head); var hips = avatar.Humanoid.GetBoneTransform(HumanBodyBones.Hips);
        var headRest = head.localRotation; var hipsRest = hips.localPosition;
        float maximum = 0;
        for (int tick = 0; tick < 180; tick++)
        {
            head.localRotation = headRest * Quaternion.Euler(0, Mathf.Sin(tick * 0.12f) * 30f, 0);
            hips.localPosition = hipsRest + Vector3.right * Mathf.Sin(tick * 0.08f) * 0.08f;
            driver.Simulate(1f / 60f);
            for (int j = 0; j < joints.Length; j++)
            {
                Require(Finite(joints[j].position) && Finite(joints[j].localRotation), "Spring produced a non-finite transform.");
                maximum = Mathf.Max(maximum, Quaternion.Angle(rotations[j], joints[j].localRotation));
            }
        }
        Require(maximum > 0.05f && driver.IsInitialized && !avatar.enabled && avatar.UpdateType == Vrm10Instance.UpdateTypes.None, "Spring-only dynamics failed.");
        Results["springGroups"] = avatar.SpringBone.Springs.Count;
        Results["springColliderGroups"] = avatar.SpringBone.ColliderGroups.Count;
        Results["springJointMaxRotationDegrees"] = maximum;
        driver.enabled = false;
        head.localRotation = headRest; hips.localPosition = hipsRest;
    }

    private static float EyeWeight(BoneWeight w, int i) => (w.boneIndex0 == i ? w.weight0 : 0) + (w.boneIndex1 == i ? w.weight1 : 0) + (w.boneIndex2 == i ? w.weight2 : 0) + (w.boneIndex3 == i ? w.weight3 : 0);
    private static bool Finite(Vector3 v) => !(float.IsNaN(v.sqrMagnitude) || float.IsInfinity(v.sqrMagnitude));
    private static bool Finite(Quaternion q) => !(float.IsNaN(q.x + q.y + q.z + q.w) || float.IsInfinity(q.x + q.y + q.z + q.w));
    private static void Invoke(Type owner, string method, object target, params object[] args)
    {
        var callback = owner.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance);
        Require(callback != null, "Verified callback missing: " + owner.Name + "." + method);
        callback.Invoke(target, args);
    }
    private static Vector3[] Bake(SkinnedMeshRenderer renderer)
    {
        var mesh = new Mesh();
        try { renderer.BakeMesh(mesh); return mesh.vertices; }
        finally { Object.DestroyImmediate(mesh); }
    }
    private static float Delta(Vector3[] a, Vector3[] b, IEnumerable<int> indices = null)
    {
        Require(a.Length == b.Length && a.Length > 0, "BakeMesh returned an invalid vertex set.");
        float maximum = 0;
        foreach (int i in indices ?? Enumerable.Range(0, a.Length)) { Require(Finite(b[i]), "BakeMesh produced non-finite geometry."); maximum = Mathf.Max(maximum, Vector3.Distance(a[i], b[i])); }
        return maximum;
    }
    private static void ZeroShapes(SkinnedMeshRenderer face) { for (int i = 0; i < face.sharedMesh.blendShapeCount; i++) face.SetBlendShapeWeight(i, 0); }
    private static SkinnedMeshRenderer Face(Vrm10Instance avatar)
    {
        var candidates = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null && RequiredShapes.All(s => r.sharedMesh.GetBlendShapeIndex(s) >= 0)).ToArray();
        Require(candidates.Length == 1, "Expected one Face renderer containing all blink/vowel shapes.");
        return candidates[0];
    }
    private static int Shape(SkinnedMeshRenderer renderer, string name) { int i = renderer.sharedMesh.GetBlendShapeIndex(name); Require(i >= 0, "Required shape missing: " + name); return i; }
    private static IEnumerable<T> Components<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<T>(true));
    private static bool IsSceneObject(Object value) => value is Component c ? c.gameObject.scene.IsValid() : value is GameObject g && g.scene.IsValid();
    private static bool Inside(Object value, GameObject root)
    {
        var transform = value is Component c ? c.transform : (value as GameObject)?.transform;
        return transform != null && (transform == root.transform || transform.IsChildOf(root.transform));
    }
    private static void RequireNoOldReferences(Scene scene, GameObject root)
    {
        foreach (var component in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Component>(true)).Where(c => c != null && !Inside(c, root)))
        {
            var iterator = new SerializedObject(component).GetIterator();
            while (iterator.Next(true))
                if (iterator.propertyType == SerializedPropertyType.ObjectReference && !StructuralReference(iterator.propertyPath) && Inside(iterator.objectReferenceValue, root))
                    throw new InvalidOperationException("Old avatar reference remains: " + component.GetType().Name + "." + iterator.propertyPath);
        }
    }
    // Unity owns hierarchy/component/prefab links. SetParent and destruction update those
    // links; rewriting m_Children directly would duplicate the newly inserted avatar.
    private static bool StructuralReference(string path)
    {
        string field = path.Split('.')[0];
        return field == "m_Children" || field == "m_Father" || field == "m_GameObject" || field == "m_Component" ||
            field == "m_CorrespondingSourceObject" || field == "m_PrefabInstance" || field == "m_PrefabAsset";
    }
    private static string Hierarchy(Transform node)
    {
        var parts = new Stack<string>();
        for (var current = node; current != null; current = current.parent)
            parts.Push(current.name + "[" + current.GetSiblingIndex() + "]");
        return string.Join("/", parts);
    }
    private static IEnumerable<MissingReference> FindMissingReferences(Scene scene)
    {
        foreach (var component in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Component>(true)).Where(c => c != null))
        {
            string hierarchy = Hierarchy(component.transform), type = component.GetType().FullName;
            int ordinal = Array.IndexOf(component.GetComponents(component.GetType()), component);
            var global = GlobalObjectId.GetGlobalObjectIdSlow(component);
            string localIdentity = global.targetObjectId + "/" + global.targetPrefabId;
            var serialized = new SerializedObject(component); var iterator = serialized.GetIterator();
            while (iterator.Next(true))
            {
                if (iterator.propertyType != SerializedPropertyType.ObjectReference || StructuralReference(iterator.propertyPath) ||
                    iterator.objectReferenceValue != null || iterator.objectReferenceInstanceIDValue == 0) continue;
                yield return new MissingReference
                {
                    key = hierarchy + "::" + type + "[" + ordinal + "]." + iterator.propertyPath + "::" + localIdentity,
                    hierarchy = hierarchy, componentType = type, property = iterator.propertyPath,
                    objectIdentity = global.ToString(), localObjectIdentity = localIdentity,
                    referencedInstanceID = iterator.objectReferenceInstanceIDValue
                };
            }
        }
    }
    private sealed class FeetSupport
    {
        public float worldY, bonePivotWorldY;
        public int weightedVertices;
        public string method;
    }
    private static FeetSupport FootSupport(Vrm10Instance avatar)
    {
        var footBones = new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes }
            .Select(b => avatar.Humanoid.GetBoneTransform(b)).Where(b => b != null).ToArray();
        Require(footBones.Length >= 2, "Avatar is missing feet for support alignment.");
        var result = new FeetSupport { bonePivotWorldY = footBones.Min(b => b.position.y), worldY = float.PositiveInfinity, method = "foot/toe-weighted baked mesh" };
        Sample(footBones);
        if (result.weightedVertices == 0)
        {
            result.method = "lower-leg-weighted baked mesh fallback";
            Sample(new[] { avatar.Humanoid.GetBoneTransform(HumanBodyBones.LeftLowerLeg), avatar.Humanoid.GetBoneTransform(HumanBodyBones.RightLowerLeg) }.Where(b => b != null).ToArray());
        }
        if (result.weightedVertices == 0) { result.worldY = result.bonePivotWorldY; result.method = "bone pivot fallback"; }
        Require(!float.IsNaN(result.worldY) && !float.IsInfinity(result.worldY), "Feet support geometry is non-finite.");
        return result;

        void Sample(Transform[] selected)
        {
            foreach (var renderer in avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer.sharedMesh == null) continue;
                int[] ids = selected.Select(b => Array.IndexOf(renderer.bones, b)).Where(i => i >= 0).ToArray();
                if (ids.Length == 0) continue;
                var weights = renderer.sharedMesh.boneWeights;
                var vertices = Bake(renderer);
                if (weights.Length != vertices.Length) continue;
                for (int i = 0; i < weights.Length; i++)
                {
                    if (!ids.Any(id => EyeWeight(weights[i], id) > 0.01f)) continue;
                    float y = renderer.transform.TransformPoint(vertices[i]).y;
                    result.worldY = Mathf.Min(result.worldY, y); result.weightedVertices++;
                }
            }
        }
    }
    private static float SupportY(Vrm10Instance avatar) => FootSupport(avatar).worldY;
    private static string Hash(string path) { using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(File.ReadAllBytes(path)).Select(b => b.ToString("x2"))); }
    private static void EnsureFolder(string folder)
    {
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
        EnsureFolder(parent); AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
#endif
