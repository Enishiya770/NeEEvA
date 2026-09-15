using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Actual same-frame small-object/background discrimination and local metadata scope.
/// Runs only in the already-created isolated room fixture, without model calls or camera motion.</summary>
public static class SceneObjectCandidateViewRegression
{
    public static JObject LastReport { get; private set; }
    private static int checks;
    private static GameObject fixture;
    private static readonly List<Material> materials=new List<Material>();
    private static void Check(bool value,string message)
    {checks++;if(!value)throw new InvalidOperationException(message);}

    public static int RunPureChecks()
    {
        int count=0;
        var cropMethod=typeof(SceneObjectCandidateView).GetMethod("FocusCrop",BindingFlags.Static|BindingFlags.NonPublic);
        var composeMethod=typeof(SceneObjectCandidateView).GetMethod("ComposePanels",BindingFlags.Static|BindingFlags.NonPublic);
        if(cropMethod==null||composeMethod==null)throw new InvalidOperationException("Candidate layout functions missing.");
        foreach(var focus in new[]{Vector2.zero,Vector2.one,new Vector2(.5f,.5f),new Vector2(0,1),new Vector2(1,0)})
        {
            var crop=(RectInt)cropMethod.Invoke(null,new object[]{100,50,focus});
            if(crop.width!=40||crop.height!=20||crop.x<0||crop.y<0||crop.xMax>100||crop.yMax>50)
                throw new InvalidOperationException("Crop must retain extent and remain inside the same image at every edge.");
            count++;
        }
        // CPU Texture2D color rows are bottom-up. The crop contract is top-left; an
        // asymmetric color fixture catches accidental flips in either montage panel.
        Color32 red=new Color32(255,0,0,255),green=new Color32(0,255,0,255),blue=new Color32(0,0,255,255),white=new Color32(255,255,255,255);
        var source=new[]{blue,white,red,green};
        var result=(Color32[])composeMethod.Invoke(null,new object[]{source,2,2,new RectInt(0,0,1,1)});
        if(result.Length!=24)throw new InvalidOperationException("Composite extent changed.");count++;
        if(!result[0].Equals(blue)||!result[1].Equals(white)||!result[12].Equals(red)||!result[13].Equals(green))
            throw new InvalidOperationException("Full panel must preserve original source pixels without vertical flipping.");count++;
        if(!result[10].Equals(red)||!result[11].Equals(red)||!result[22].Equals(red)||!result[23].Equals(red))
            throw new InvalidOperationException("Right enlargement must copy only top-left crop pixels, without invented colors.");count++;
        return count;
    }

    public static IEnumerator RunChecks(NevaSceneVisionCapture capture,string output)
    {
        if(!Application.isBatchMode||!Application.isPlaying||Application.dataPath.IndexOf("unity-naturalness-validation",StringComparison.OrdinalIgnoreCase)<0)
            throw new InvalidOperationException("Candidate view regression requires the existing isolated room Play fixture.");
        checks=RunPureChecks();materials.Clear();
        string directory=Path.Combine(output,"scene-object-candidates");Directory.CreateDirectory(directory);
        LastReport=new JObject { ["status"]="running",["scope"]="Actual calibrated character RGB and same-frame opaque IDs. Small visible object beside a mistakenly selected large background, transparent/hidden exclusion, bounded local hierarchy, exact source capture/crop coordinates, stale and source-binding rejection. No semantic model result, device operation or character movement is simulated." };
        var stack=new Stack<IEnumerator>();stack.Push(Exercise(capture,directory));
        try
        {
            while(stack.Count>0)
            {
                object value=null;
                try
                {
                    while(stack.Count>0)
                    {
                        var routine=stack.Peek();
                        if(!routine.MoveNext()){(routine as IDisposable)?.Dispose();stack.Pop();continue;}
                        if(routine.Current is IEnumerator nested){stack.Push(nested);continue;}
                        value=routine.Current;break;
                    }
                }
                catch(Exception error){LastReport["status"]="failed";LastReport["error"]=error.ToString();throw;}
                if(stack.Count>0)yield return value;
            }
            LastReport["status"]="passed";
        }
        finally
        {
            while(stack.Count>0)(stack.Pop() as IDisposable)?.Dispose();
            if(fixture!=null)UnityEngine.Object.DestroyImmediate(fixture);fixture=null;
            foreach(var material in materials)if(material!=null)UnityEngine.Object.DestroyImmediate(material);materials.Clear();
            if(capture!=null)capture.SetObserving(true);
            LastReport["checks"]=checks;File.WriteAllText(Path.Combine(directory,"report.json"),LastReport.ToString());
        }
    }

    private static IEnumerator Exercise(NevaSceneVisionCapture capture,string output)
    {
        Check(capture!=null&&capture.IsObserving&&capture.TryGetCurrentCameraPose(out _),"Real calibrated character view is active.");
        capture.TryGetCurrentCameraPose(out Pose pose);
        fixture=new GameObject("Isolated same-frame candidate panels");fixture.transform.SetPositionAndRotation(pose.position,pose.rotation);
        var background=Panel(fixture.transform,"Candidate background cabinet reference",new Vector3(0,0,.8f),new Vector2(.70f,.43f),Color.blue);
        var lamp=new GameObject("small lamp composite root");lamp.transform.SetParent(fixture.transform,false);
        var lampIdentity=lamp.AddComponent<SceneObjectIdentity>();lampIdentity.Configure("局部测试小灯","candidate-small-fixture");
        var tiny=Panel(lamp.transform,"small opaque lamp base",new Vector3(.055f,.022f,.6f),new Vector2(.012f,.027f),Color.red);
        var cover=Panel(fixture.transform,"unverified transparent foreground",new Vector3(-.1f,.045f,.5f),new Vector2(.075f,.075f),new Color(.4f,.7f,1,.15f),true);
        var hidden=Panel(fixture.transform,"must not appear behind transparent blocker",new Vector3(-.12f,.054f,.6f),new Vector2(.025f,.025f),Color.green);
        var hiddenIdentity=hidden.gameObject.AddComponent<SceneObjectIdentity>();hiddenIdentity.Configure("不可泄露的遮挡后物体");
        var outside=Panel(fixture.transform,"must not appear offscreen",new Vector3(5,0,.6f),new Vector2(.03f,.03f),Color.yellow);
        var outsideIdentity=outside.gameObject.AddComponent<SceneObjectIdentity>();outsideIdentity.Configure("不可泄露的视野外物体");
        SceneVisionObservation observation=null;yield return Capture(capture,value=>observation=value);
        string originalPixels=observation.ImageDataUrl;string originalId=observation.CaptureId;
        Vector2 focus=Pixel(observation,fixture.transform.TransformPoint(new Vector3(.026f,.022f,.6f)));
        Check(capture.TryResolveObservedObject(observation,focus,out var wrong,out string reason)&&wrong.VisibleConfirmed&&wrong.Root==background.transform,
            "Focused pixel really resolves to the large neighboring background before candidate analysis: "+reason);
        var rootPosition=capture.Target.transform.position;var rootRotation=capture.Target.transform.rotation;
        Check(capture.TryBuildObjectCandidateView(observation,focus,wrong.EntityId,out var view,out reason),"Candidate montage built from the existing real frame: "+reason);
        Check(view.Candidates.Count<=SceneObjectCandidateView.MaximumCandidates&&view.Candidates.Count>=2,"Candidate list is bounded and contains alternatives.");
        var small=view.Candidates.FirstOrDefault(item=>item.entityId==lampIdentity.EntityId);
        Check(small!=null&&view.Candidates.Any(item=>item.entityId==wrong.EntityId),"Small lamp remains a candidate beside the preferred mistaken background entity.");
        Check(!view.Candidates.Any(item=>item.entityId==hiddenIdentity.EntityId||item.entityId==outsideIdentity.EntityId),"Only actual visible opaque pixels contribute candidates; hidden/offscreen labels are not supplied.");
        Check(!view.MetadataJson.Contains(hiddenIdentity.SceneLabel)&&!view.MetadataJson.Contains(outsideIdentity.SceneLabel),"Metadata contains no hidden/offscreen local names.");
        Check(view.TryGetCandidate(small.candidateId,out var selected,out reason)&&selected.VisibleConfirmed&&selected.EntityId==lampIdentity.EntityId,
            "Short cN choice maps to the exact visible entity in its own candidate view: "+reason);
        Check(!view.TryGetCandidate("c99",out _,out _)&&!view.TryGetCandidate(lampIdentity.EntityId,out _,out _),"Unknown short choices and long IDs cannot select candidates.");
        Check(small.localHierarchy.selectedPartName==tiny.name&&small.localHierarchy.entityRootName==lamp.name&&
            small.localHierarchy.partToEntityRoot.SequenceEqual(new[]{tiny.name,lamp.name})&&small.localHierarchy.registeredParts.Length==0,
            "Only the selected part-to-identity-root chain is exposed; authored lamp name grants no control or device parts.");
        Check(small.pixelBounds.width<40&&small.pixelBounds.height<40&&small.visiblePixelCount>9,"Small target case occupies a genuinely small source-pixel footprint.");
        Check(!new RectInt(small.markerPixelBounds.x,small.markerPixelBounds.y,small.markerPixelBounds.width,small.markerPixelBounds.height).Overlaps(
            new RectInt(small.pixelBounds.x,small.pixelBounds.y,small.pixelBounds.width,small.pixelBounds.height)),
            "Number label remains outside the small object so the enlargement does not cover its visual detail.");
        foreach(var item in view.Candidates)
        {
            Check(capture.TryResolveObservedObject(observation,item.imagePoint,out var actual,out _)&&actual.VisibleConfirmed&&actual.EntityId==item.entityId,
                "Every candidate source coordinate resolves to exactly its own opaque identity.");
            int x=Mathf.FloorToInt(item.imagePoint.x*observation.Width),y=Mathf.FloorToInt(item.imagePoint.y*observation.Height);
            Check(x>=item.pixelBounds.x&&x<item.pixelBounds.x+item.pixelBounds.width&&y>=item.pixelBounds.y&&y<item.pixelBounds.y+item.pixelBounds.height,
                "Candidate pixelBounds and imagePoint use the original top-left source coordinates.");
        }
        // Preserve the wire string: JObject.Parse otherwise recognizes ISO UTC as a Date
        // token, whose explicit string conversion becomes "MM/dd/yyyy HH:mm:ss". That
        // formatting conversion must not be mistaken for a changed capture timestamp.
        var metadata=JsonConvert.DeserializeObject<JObject>(view.MetadataJson,new JsonSerializerSettings {DateParseHandling=DateParseHandling.None});
        SaveImage(output,"source",originalPixels);SaveImage(output,"candidates",view.ImageDataUrl);
        File.WriteAllText(Path.Combine(output,"candidates.metadata.json"),view.MetadataJson);
        LastReport["captureIdentityEvidence"]=new JObject {
            ["sourceCaptureId"]=originalId,["metadataCaptureId"]=metadata["captureId"],
            ["sourceCapturedAtUtc"]=observation.CapturedAtUtc,["metadataCapturedAtUtc"]=metadata["capturedAtUtc"],
            ["sourceFrame"]=observation.Frame,["metadataFrame"]=metadata["frame"],
            ["originalRgbUnchanged"]=observation.ImageDataUrl==originalPixels,["latestCaptureId"]=capture.LatestObservation.CaptureId
        };
        Check((string)metadata["captureId"]==originalId&&(string)metadata["capturedAtUtc"]==observation.CapturedAtUtc&&
            (int)metadata["frame"]==observation.Frame&&observation.ImageDataUrl==originalPixels&&capture.LatestObservation.CaptureId==originalId,
            "Candidate derivation preserves exact observation identity/time/frame/RGB and does not request a new frame.");
        Check(capture.Target.transform.position==rootPosition&&capture.Target.transform.rotation==rootRotation&&capture.TryGetCurrentCameraPose(out Pose after)&&
            Vector3.Distance(after.position,observation.CameraPosition)<.001f&&Quaternion.Angle(after.rotation,observation.CameraRotation)<.05f,
            "Candidate generation does not move the character or camera.");
        LastReport["sourceCaptureId"]=originalId;LastReport["smallCandidateId"]=small.candidateId;LastReport["smallSourceBounds"]=metadata["candidates"].First(item=>(string)item["entityId"]==small.entityId)["pixelBounds"].DeepClone();
        Check(!capture.TryBuildObjectCandidateView(observation,new Vector2(float.NaN,.5f),null,out _,out _),"Non-finite focus never creates a candidate panel.");
        // Use a stale copy with the original valid binding rather than sleep or a negative
        // timestamp sentinel. This exercises production freshness without advancing Unity.
        var ctor=typeof(SceneVisionObservation).GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single();
        var grounding=typeof(SceneVisionObservation).GetProperty("Grounding",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(observation);
        var stale=(SceneVisionObservation)ctor.Invoke(new object[]{observation.CaptureId,"ready","",observation.CapturedAtUtc,
            Time.realtimeSinceStartup-SceneVisionGrounding.MaximumObservationAge-1,observation.Frame,
            new Pose(observation.CameraPosition,observation.CameraRotation),observation.VerticalFov,observation.Width,observation.Height,
            observation.ImageDataUrl,observation.SceneHandle,observation.SceneName,observation.ScenePath,observation.LoadedSceneFingerprint,grounding});
        Check(!capture.TryBuildObjectCandidateView(stale,focus,null,out _,out _),"Expired frame cannot be re-labelled as a new crop observation.");
        // New real front surface hides the entire previous small entity. A preferred old ID
        // cannot bypass this frame's actual foreground pixels.
        Panel(fixture.transform,"new opaque occluder",new Vector3(0,0,.35f),new Vector2(.8f,.6f),Color.magenta);
        SceneVisionObservation blocked=null;yield return Capture(capture,value=>blocked=value);
        Check(capture.TryBuildObjectCandidateView(blocked,focus,lampIdentity.EntityId,out var blockedView,out reason)&&
            !blockedView.Candidates.Any(item=>item.entityId==lampIdentity.EntityId),"Remembered preference cannot expose a fully occluded identity: "+reason);
        SaveImage(output,"occluded-candidates",blockedView.ImageDataUrl);
        capture.SetObserving(false);
        Check(!view.TryGetCandidate(small.candidateId,out _,out _)&&!capture.TryBuildObjectCandidateView(blocked,focus,null,out _,out _),
            "Closing the visual source immediately revokes stored candidate handles and new generation.");
        capture.SetObserving(true);
        Check(!capture.TryBuildObjectCandidateView(blocked,focus,null,out _,out _),"Reopening does not revive the former observation binding.");
    }
    private static Renderer Panel(Transform parent,string name,Vector3 local,Vector2 size,Color color,bool transparent=false)
    {
        var item=GameObject.CreatePrimitive(PrimitiveType.Quad);item.name=name;item.transform.SetParent(parent,false);item.transform.localPosition=local;
        item.transform.localScale=new Vector3(size.x,size.y,1);UnityEngine.Object.DestroyImmediate(item.GetComponent<Collider>());
        var material=new Material(Shader.Find(transparent?"Standard":"Unlit/Color"));material.color=color;materials.Add(material);
        if(transparent)
        {
            material.SetFloat("_Mode",3);material.SetInt("_SrcBlend",(int)BlendMode.SrcAlpha);material.SetInt("_DstBlend",(int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite",0);material.EnableKeyword("_ALPHABLEND_ON");material.renderQueue=3000;
        }
        var renderer=item.GetComponent<Renderer>();renderer.sharedMaterial=material;return renderer;
    }
    private static Vector2 Pixel(SceneVisionObservation observation,Vector3 point)
    {
        var p=Quaternion.Inverse(observation.CameraRotation)*(point-observation.CameraPosition);
        float tangent=Mathf.Tan(observation.VerticalFov*Mathf.Deg2Rad*.5f);
        return new Vector2(.5f+p.x/(2*p.z*tangent*observation.Width/observation.Height),.5f-p.y/(2*p.z*tangent));
    }
    private static IEnumerator Capture(NevaSceneVisionCapture capture,Action<SceneVisionObservation> complete)
    {
        SceneVisionObservation value=null;capture.RequestCapture(result=>value=result);float deadline=Time.realtimeSinceStartup+5;
        while(value==null){if(Time.realtimeSinceStartup>deadline)throw new TimeoutException("candidate capture");yield return null;}
        Check(value.HasImage&&value.HasSurfaceGrounding,"Actual GPU RGB and opaque-ID observation arrived: "+value.Reason);complete(value);
    }
    private static void SaveImage(string output,string name,string image)=>File.WriteAllBytes(Path.Combine(output,name+(image.StartsWith("data:image/png;")?".png":".jpg")),Convert.FromBase64String(image.Substring(image.IndexOf(',')+1)));
}
