using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Real same-camera RGB/depth-ID rendering in the existing isolated room fixture.
/// Temporary panels exercise identity/opacity without editing any authored scene asset.</summary>
public static class SceneObjectPerceptionRegression
{
    public static JObject LastReport { get; private set; }
    private static int checks;
    private static GameObject fixture;
    private static readonly List<Material> materials = new List<Material>();
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException(message); }

    public static IEnumerator RunChecks(NevaSceneVisionCapture capture, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Object perception regression requires the existing isolated room Play fixture.");
        checks = 0; materials.Clear();
        string directory = Path.Combine(output, "scene-object-perception"); Directory.CreateDirectory(directory);
        LastReport = new JObject { ["status"] = "running", ["scope"] =
            "Actual RGB and companion depth/ID capture through calibrated room character view. Temporary opaque/transparent panels with explicit composite identities, a Rigidbody-marked rigid mesh whose transform changes between real captures, and same-frame occlusion. No object enumeration sent to a model, no HTTP, no interaction or navigation inference." };
        var stack = new Stack<IEnumerator>(); stack.Push(Exercise(capture, directory));
        try
        {
            while (stack.Count > 0)
            {
                object current = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var routine = stack.Peek();
                        if (!routine.MoveNext()) { (routine as IDisposable)?.Dispose(); stack.Pop(); continue; }
                        if (routine.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        current = routine.Current; break;
                    }
                }
                catch (Exception error) { LastReport["status"] = "failed"; LastReport["error"] = error.ToString(); throw; }
                if (stack.Count > 0) yield return current;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            if (fixture != null) UnityEngine.Object.DestroyImmediate(fixture);
            foreach (var material in materials) if (material != null) UnityEngine.Object.DestroyImmediate(material);
            materials.Clear(); fixture = null;
            if (capture != null) capture.SetObserving(true);
            LastReport["checks"] = checks; File.WriteAllText(Path.Combine(directory,"report.json"),LastReport.ToString());
        }
    }
    private static IEnumerator Exercise(NevaSceneVisionCapture capture, string output)
    {
        Check(capture != null && capture.IsObserving && capture.TryGetCurrentCameraPose(out _), "Actual calibrated character view is active.");
        capture.TryGetCurrentCameraPose(out Pose pose);
        fixture = new GameObject("Isolated object perception panels");
        fixture.transform.SetPositionAndRotation(pose.position, pose.rotation);
        var lamp = new GameObject("Fixture composite; authoring name never sent"); lamp.transform.SetParent(fixture.transform,false);
        var lampIdentity = lamp.AddComponent<SceneObjectIdentity>(); lampIdentity.Configure("复合测试物件", "perception-fixture");
        var opaque = Panel(lamp.transform,"opaque base",new Vector3(-.12f,-.05f,.60f),new Vector2(.10f,.10f),false);
        var glass = Panel(lamp.transform,"transparent cover",new Vector3(.03f,-.05f,.56f),new Vector2(.10f,.10f),true);
        var hidden = Panel(fixture.transform,"hidden other entity",new Vector3(.03f*.68f/.56f,-.05f*.68f/.56f,.68f),new Vector2(.04f,.04f),false);
        var hiddenIdentity = hidden.gameObject.AddComponent<SceneObjectIdentity>(); hiddenIdentity.Configure("不应被看见的测试物件");
        var unnamed = Panel(fixture.transform,"Name must not imply semantics",new Vector3(.17f,-.05f,.60f),new Vector2(.08f,.08f),false,"Autodesk Interactive");
        var moving = new GameObject("Controlled dynamic rigid root"); moving.transform.SetParent(fixture.transform,false);
        var movingIdentity = moving.AddComponent<SceneObjectIdentity>(); movingIdentity.Configure("刚性动态测试物件");
        var body = moving.AddComponent<Rigidbody>(); body.useGravity = false; body.isKinematic = true;
        var dynamicSurface = Panel(moving.transform,"moving opaque part",new Vector3(-.10f,.09f,.60f),new Vector2(.08f,.08f),false,"Autodesk Interactive");
        SceneVisionObservation first = null;
        yield return Capture(capture,value=>first=value);
        SaveCapture(output,"source",first); LastReport["sourceCaptureId"] = first.CaptureId;
        Check(capture.TryResolve(first,Pixel(first,opaque.bounds.center),out _,out string physicalReason), "Original static opaque surface contract still resolves: "+physicalReason);
        Check(capture.TryResolveObservedObject(first,Pixel(first,opaque.bounds.center),out var observed,out string reason) &&
            observed.VisibleConfirmed && observed.EntityId == lampIdentity.EntityId && observed.Root == lamp.transform &&
            observed.SceneLabel == "复合测试物件" && observed.IdentitySource == "scene-authored", "Visible opaque child maps to only its explicitly authored entity: "+reason);
        Check(observed.TryValidate(out _) && observed.GeometryIsCurrent, "Stationary opaque object snapshot remains current.");
        Check(capture.TryResolveObservedObject(first,Pixel(first,glass.bounds.center),out var candidate,out reason) &&
            !candidate.VisibleConfirmed && candidate.EntityId == observed.EntityId && candidate.Root == lamp.transform &&
            candidate.SceneLabel == "" && candidate.IdentitySource == "geometry-candidate", "Transparent cover retains only candidate identity, never a verified label: "+reason);
        Check(!capture.TryResolve(first,Pixel(first,glass.bounds.center),out _,out _), "Old physical surface resolution still rejects transparent candidate.");
        Check(capture.TryFindObservedObject(first,candidate.EntityId,out var composite,out reason) && composite.VisibleConfirmed &&
            composite.EntityId == observed.EntityId && Vector3.Distance(composite.WorldPoint,opaque.bounds.center)<.08f,
            "Only a visible opaque part of the same explicit entity can upgrade the candidate: "+reason);
        Check(!capture.TryFindObservedObject(first,hiddenIdentity.EntityId,out _,out _), "No object behind the conservative glass blocker is exposed by ID lookup.");
        Check(capture.TryResolveObservedObject(first,Pixel(first,unnamed.bounds.center),out var anonymous,out reason) && anonymous.VisibleConfirmed &&
            anonymous.IdentitySource == "renderer-session" && anonymous.SceneLabel == "" && anonymous.Root == unnamed.transform,
            "Unregistered object receives a local renderer identity with no hierarchy-derived semantics: "+reason);
        Check(!capture.TryResolve(first,Pixel(first,unnamed.bounds.center),out _,out _), "Autodesk opaque identity support does not expand the old static physical-target whitelist.");
        Check(unnamed.sharedMaterial.shader.name == "Autodesk Interactive" && dynamicSurface.sharedMaterial.shader.name == "Autodesk Interactive" &&
            glass.sharedMaterial.shader.name == "Autodesk Interactive", "GPU cases use the actual built-in Autodesk shader for opaque, animated-rigid and transparent parts.");
        LastReport["observedOpaqueShader"] = unnamed.sharedMaterial.shader.name;
        Check(!capture.TryResolve(first,Pixel(first,dynamicSurface.bounds.center),out _,out _), "Original physical target API still rejects the Rigidbody-marked mesh.");
        Check(capture.TryResolveObservedObject(first,Pixel(first,dynamicSurface.bounds.center),out var dynamicObject,out reason) &&
            dynamicObject.VisibleConfirmed && dynamicObject.EntityId == movingIdentity.EntityId && dynamicObject.GeometryIsCurrent,
            "Actual front-facing dynamic opaque mesh is observed without making it a stationary locomotion target: "+reason);
        Check(!capture.TryResolveObservedObject(first,new Vector2(float.NaN,.5f),out _,out _) &&
            !capture.TryFindObservedObject(first,"invented-unseen-entity",out _,out _), "Invalid pixels and unseen identity handles never resolve.");
        moving.transform.localPosition += Vector3.right*.035f;
        Check(dynamicObject.TryValidate(out _) && !dynamicObject.GeometryIsCurrent,
            "Motion preserves the captured entity observation but explicitly marks its old world point as historical.");
        SceneVisionObservation second = null; yield return Capture(capture,value=>second=value);
        SaveCapture(output,"moved",second);
        Check(capture.TryConfirmObject(second,dynamicObject,out var renewed,out reason) && renewed.VisibleConfirmed &&
            renewed.EntityId == dynamicObject.EntityId && renewed.CaptureId != first.CaptureId &&
            Vector3.Distance(renewed.WorldPoint,dynamicObject.WorldPoint)>.012f,
            "New-frame opaque pixels re-confirm the moving entity using its new captured position: "+reason);
        Check(!capture.TryConfirmObject(first,observed,out _,out _), "An old image cannot masquerade as new object confirmation.");
        LastReport["dynamicBefore"] = Snapshot(dynamicObject); LastReport["dynamicAfter"] = Snapshot(renewed);
        // A manually inconsistent alpha keyword is not made safe by _Mode=0 alone.
        unnamed.sharedMaterial.EnableKeyword("_ALPHATEST_ON");
        unnamed.sharedMaterial.SetFloat("_Cutoff",.5f);
        unnamed.sharedMaterial.color = new Color(.85f,.35f,.15f,0);
        Check(!anonymous.TryValidate(out _), "Post-capture alpha-keyword changes revoke the old opaque identity observation.");
        SceneVisionObservation masked = null; yield return Capture(capture,value=>masked=value);
        SaveCapture(output,"autodesk-alpha-inconsistent",masked);
        Check(capture.TryResolveObservedObject(masked,Pixel(masked,unnamed.bounds.center),out var alphaCandidate,out reason) &&
            !alphaCandidate.VisibleConfirmed && !capture.TryFindObservedObject(masked,anonymous.EntityId,out _,out _),
            "Autodesk _Mode=0 with active alpha-test remains an unverified blocker, never a confirmed opaque object: "+reason);
        movingIdentity.Configure("改过的显式场景标注");
        Check(!renewed.TryValidate(out _), "Changing explicit identity annotation invalidates prior metadata association.");
        var blocker = Panel(fixture.transform,"new foreground occluder",new Vector3(0,0,.42f),new Vector2(.7f,.7f),false);
        SceneVisionObservation occluded = null; yield return Capture(capture,value=>occluded=value);
        SaveCapture(output,"occluded",occluded);
        Check(!capture.TryConfirmObject(occluded,observed,out _,out _) && !capture.TryFindObservedObject(occluded,observed.EntityId,out _,out _),
            "A previously seen composite identity is not asserted visible when its whole entity is now occluded.");
        Check(capture.TryResolveObservedObject(occluded,new Vector2(.5f,.5f),out var front,out _) && front.VisibleConfirmed && front.EntityId != observed.EntityId,
            "Current actual foreground pixel is associated with its own identity, not the remembered object behind it.");
        UnityEngine.Object.DestroyImmediate(lampIdentity);
        Check(!observed.TryValidate(out _), "Removing an explicit identity component revokes its old authored association even when renderers survive.");
        capture.SetObserving(false);
        Check(!observed.TryValidate(out _) && !capture.TryResolveObservedObject(occluded,new Vector2(.5f,.5f),out _,out _),
            "Closed eyes revoke previous object bindings and current lookup.");
        capture.SetObserving(true);
        Check(!observed.TryValidate(out _), "Reopening eyes does not revive old capture identity bindings.");
        LastReport["opaque"] = Snapshot(observed); LastReport["candidate"] = Snapshot(candidate);
        LastReport["anonymous"] = Snapshot(anonymous); LastReport["opaquePartUpgrade"] = Snapshot(composite);
    }
    private static Renderer Panel(Transform parent,string name,Vector3 position,Vector2 size,bool transparent,string shaderName=null)
    {
        var value = GameObject.CreatePrimitive(PrimitiveType.Quad); value.name = name;
        value.transform.SetParent(parent,false); value.transform.localPosition = position;
        value.transform.localScale = new Vector3(size.x,size.y,1);
        var collider=value.GetComponent<Collider>(); if(collider!=null)UnityEngine.Object.DestroyImmediate(collider);
        var material=new Material(Shader.Find(shaderName ?? (transparent?"Autodesk Interactive":"Unlit/Color"))); materials.Add(material);
        material.color=transparent?new Color(.3f,.7f,1f,.2f):new Color(.85f,.35f,.15f,1f);
        if(transparent)
        {
            material.SetFloat("_Mode",3);material.SetInt("_SrcBlend",(int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend",(int)BlendMode.OneMinusSrcAlpha);material.SetInt("_ZWrite",0);
            material.EnableKeyword("_ALPHABLEND_ON");material.renderQueue=3000;
        }
        var renderer=value.GetComponent<Renderer>();renderer.sharedMaterial=material;return renderer;
    }
    private static Vector2 Pixel(SceneVisionObservation observation,Vector3 point)
    {
        Vector3 local=Quaternion.Inverse(observation.CameraRotation)*(point-observation.CameraPosition);
        float tangent=Mathf.Tan(observation.VerticalFov*Mathf.Deg2Rad*.5f);
        return new Vector2(.5f+local.x/(2*local.z*tangent*observation.Width/observation.Height),.5f-local.y/(2*local.z*tangent));
    }
    private static IEnumerator Capture(NevaSceneVisionCapture capture,Action<SceneVisionObservation> complete)
    {
        SceneVisionObservation value=null;capture.RequestCapture(result=>value=result);
        float deadline=Time.realtimeSinceStartup+5;
        while(value==null){if(Time.realtimeSinceStartup>deadline)throw new TimeoutException("object perception capture");yield return null;}
        Check(value.HasImage && value.HasSurfaceGrounding,"Real GPU RGB and companion IDs arrived: "+value.Reason);complete(value);
    }
    private static JObject Snapshot(SceneObservedObject value)=>new JObject{["entityId"]=value.EntityId,["captureId"]=value.CaptureId,
        ["sceneLabel"]=value.SceneLabel,["identitySource"]=value.IdentitySource,["visibleConfirmed"]=value.VisibleConfirmed,
        ["pointAtCapture"]=JObject.Parse(JsonUtility.ToJson(value.WorldPoint)),["scope"]=value.MeasurementScope};
    private static void SaveCapture(string output,string name,SceneVisionObservation value)
    {
        File.WriteAllBytes(Path.Combine(output,name+".jpg"),Convert.FromBase64String(value.ImageDataUrl.Substring(value.ImageDataUrl.IndexOf(',')+1)));
        File.WriteAllText(Path.Combine(output,name+".metadata.json"),value.MetadataJson);
    }
}
