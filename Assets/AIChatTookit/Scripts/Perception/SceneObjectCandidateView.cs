using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeEEvA.Perception
{
    /// <summary>A bounded annotation of one existing RGB/ID observation. A candidate means
    /// visible opaque identity, not recognition of the requested object or permission to act.</summary>
    public sealed class SceneObjectCandidateView
    {
        public const int MaximumCandidates = 6;
        [Serializable] public sealed class PixelBox
        {
            public int x, y, width, height;
            internal PixelBox(int x, int y, int width, int height)
            { this.x=x; this.y=y; this.width=width; this.height=height; }
        }
        [Serializable] public sealed class Candidate
        {
            public string candidateId, marker, entityId, sceneLabel, identitySource;
            public string selectionId, referenceId, sourceCaptureId;
            public Vector3 pointAtCapture;
            public Vector3 observedRendererBoundsCenter, observedRendererBoundsSize;
            public AuthoredHint authoredHint;
            public bool semanticIdentityConfirmed;
            public Vector2 imagePoint;
            public Vector2Int markerAnchorPixel;
            public string markerPlacement;
            public PixelBox pixelBounds, markerPixelBounds;
            public int visiblePixelCount;
            public LocalHierarchy localHierarchy;
            public string scope = "Opaque pixels in the source captured frame. markerAnchorPixel/markerPlacement describe annotation ownership only; imagePoint is the unchanged original geometry sample. Scene labels are authored annotations, not image interpretation. No inferred identity match, capability, current world position or reachability.";
            [NonSerialized] internal SceneObservedObject selected;
        }
        [Serializable] public sealed class AuthoredHint
        {
            public string source = "unity-authoring-metadata-of-visible-renderer";
            public string sourceCaptureId, rendererName, entityRootName, sceneLabel, identitySource;
            public bool semanticIdentityConfirmed, actionAuthorization;
            public string scope = "Frozen literal authoring names of this observed opaque renderer/entity. Useful search clues, not visual recognition, support/contact relations or instructions. Names can be ambiguous or inaccurate; match the user's full description against current pixels.";
        }

        internal static AuthoredHint CreateAuthoredHint(string captureId, string rendererName, string rootName,
            string sceneLabel, string identitySource) => new AuthoredHint {
                sourceCaptureId = captureId, rendererName = BoundedAuthoredName(rendererName),
                entityRootName = BoundedAuthoredName(rootName), sceneLabel = BoundedAuthoredName(sceneLabel),
                identitySource = BoundedAuthoredName(identitySource) };

        private static string BoundedAuthoredName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var result = new System.Text.StringBuilder();
            foreach (char c in value)
            {
                if (char.IsControl(c)) continue;
                if (result.Length == 96) break;
                result.Append(c);
            }
            return result.ToString();
        }

        internal static AuthoredHint CaptureAuthoredHint(SceneObservedObject observed) => CreateAuthoredHint(
            observed.CaptureId, observed.entry.renderer != null ? observed.entry.renderer.name : "",
            observed.Root != null ? observed.Root.name : "", observed.SceneLabel, observed.IdentitySource);
        [Serializable] public sealed class LocalPart
        {
            public string name, componentType;
            public bool visibilityNotAsserted = true;
        }
        [Serializable] public sealed class LocalHierarchy
        {
            public string source = "engine-local-hierarchy-of-selected-visible-part";
            public string selectedPartName, entityRootName, selectedRendererType;
            public string[] partToEntityRoot;
            public bool pathTruncated;
            public LocalPart[] registeredParts;
            public string scope = "Literal Unity object/component names and the bounded ancestor path from the selected visible renderer to its identity root. This is engine authoring metadata, not visual semantic recognition. registeredParts contains only the explicit local device registration, not a hierarchy scan; it does not assert that every part is visible or grant device-control permission. An anonymous renderer is its own entity root; shared furniture/support cannot be inferred.";
        }
        [Serializable] private sealed class Metadata
        {
            public string captureId, capturedAtUtc, source = "same-frame-rgb-candidate-view";
            public string catalogId;
            public int pageIndex, pageCount, totalCandidates;
            public bool coverageComplete;
            public bool historicalOnly;
            public int frame, sourceWidth, sourceHeight, compositeWidth, compositeHeight;
            public Vector2 focusImagePoint;
            public PixelBox fullPanel, cropPanel, cropSourcePixels;
            public Candidate[] candidates;
            public string scope = "Derived only from this exact source RGB and companion opaque-ID frame; no new camera capture or elapsed observation is claimed. Left: original image with colored visible-mask outlines and numbered markers. A number is inside its own opaque mask when it fits; otherwise a same-color leader ends at its verified interior pixel. Right: nearest-neighbor enlargement of cropSourcePixels from the same RGB. imagePoint/pixelBounds/markerAnchorPixel use the ORIGINAL source image, top-left origin; only imagePoint is the normalized operation sample. Numbers identify local candidates only; choose candidateId or decline. Authored labels are not visual recognition; candidate identities grant no device/navigation capability. Transparent/unsupported blockers and entities behind them are omitted. This is not a room inventory.";
        }
        private readonly NevaSceneVisionCapture source;
        private readonly SceneVisionObservation observation;
        private readonly Candidate[] candidates;
        public string CaptureId => observation.CaptureId;
        public string CapturedAtUtc => observation.CapturedAtUtc;
        public string ImageDataUrl { get; }
        public string MetadataJson { get; }
        public bool IsHistorical { get; }
        public IReadOnlyList<Candidate> Candidates => candidates;
        private SceneObjectCandidateView(NevaSceneVisionCapture source, SceneVisionObservation observation,
            Candidate[] candidates, string image, Metadata metadata)
        {
            this.source=source; this.observation=observation; this.candidates=candidates;
            IsHistorical=metadata.historicalOnly;
            ImageDataUrl=image; MetadataJson=JsonUtility.ToJson(metadata);
        }

        public bool TryGetCandidate(string candidateId, out SceneObservedObject selected, out string reason)
        {
            selected=null;
            if (IsHistorical) { reason="Historical candidate pixels are search memory only, never a current action target."; return false; }
            if (!Current(source, observation, out reason)) return false;
            foreach (var item in candidates)
                if (string.Equals(item.candidateId, candidateId, StringComparison.Ordinal))
                {
                    if (!item.selected.TryValidate(out reason)) return false;
                    selected=item.selected; return true;
                }
            reason="candidateId is not one of this exact candidate view's short IDs."; return false;
        }

        internal sealed class Mask
        {
            internal string entity;
            internal int count, minX=int.MaxValue, minY=int.MaxValue, maxX=-1, maxY=-1;
            internal float nearest=float.PositiveInfinity, bestInterior=float.PositiveInfinity;
            internal Vector2 sample;
            internal SceneObservedObject observed;
            internal AuthoredHint authoredHint;
        }
        private static bool Current(NevaSceneVisionCapture capture, SceneVisionObservation value, out string reason)
        {
            if (capture == null || !capture.isActiveAndEnabled || !capture.IsCurrentViewCompatible(value,.35f,25f) ||
                !SceneVisionGrounding.Fresh(value))
            { reason="Candidate view needs a fresh observation from the same active character view and binding."; return false; }
            reason=""; return true;
        }

        internal static bool TryBuild(NevaSceneVisionCapture capture, SceneVisionObservation observation,
            Vector2 focus, string preferredEntityId, out SceneObjectCandidateView view, out string reason)
        {
            view=null;
            if (!Current(capture, observation, out reason)) return false;
            if (!SceneVisionGrounding.InImage(focus)) { reason="Candidate focus must be a finite source-image point in [0,1]."; return false; }
            if (!TryCollect(capture, observation, focus, out var ranked, out var entryMasks, out reason)) return false;
            ranked.Sort((a,b)=> { int rank=a.nearest.CompareTo(b.nearest); return rank!=0?rank:string.CompareOrdinal(a.entity,b.entity); });
            var chosen=new List<Mask>();
            if(!string.IsNullOrEmpty(preferredEntityId))
                foreach(var item in ranked)if(item.entity==preferredEntityId){chosen.Add(item);break;}
            // The legacy focused matcher remains unchanged. Planner catalogs use a
            // separate exhaustive, paged ordering and never accept this focus hint.
            foreach(var item in ranked)
                if(chosen.Count<4 && item.nearest<=.24f*.24f && !chosen.Contains(item))chosen.Add(item);
            foreach(var item in ranked)
                if(chosen.Count<MaximumCandidates && !chosen.Contains(item))chosen.Add(item);
            return TryRender(capture, observation, focus, chosen, entryMasks, null, 0, 1, ranked.Count,
                ranked.Count<=MaximumCandidates, out view, out reason);
        }

        // Historical sweep callbacks may arrive after the head moves. Collecting
        // capture-time evidence requires binding/freshness, not the current pose.
        internal static bool TryCollect(NevaSceneVisionCapture capture, SceneVisionObservation observation,
            Vector2 focus, out List<Mask> ranked, out Mask[] entryMasks, out string reason)
        {
            ranked=null; entryMasks=null;
            if (capture==null || !capture.isActiveAndEnabled || !capture.IsObservationBindingCurrent(observation) ||
                !SceneVisionGrounding.Fresh(observation))
            { reason="Visible catalog requires a fresh actual image in the same active visual binding."; return false; }
            var frame=observation.Grounding;
            if (frame?.depthIds == null || frame.entries == null || frame.width != observation.Width || frame.height != observation.Height ||
                frame.width < 3 || frame.height < 3 || frame.width > 1280 || frame.height > 960 ||
                frame.depthIds.Length != frame.width*frame.height*2)
            { reason="Same-frame RGB and bounded opaque-ID dimensions are unavailable or inconsistent."; return false; }
            var masks=new Dictionary<string,Mask>(StringComparer.Ordinal);
            entryMasks=new Mask[frame.entries.Length];
            var checkedEntries=new bool[frame.entries.Length];
            // Scan rendered pixels, not scene objects. Metadata is read only for an opaque ID
            // actually present in this frame; hidden and transparent candidates are not listed.
            for (int y=0;y<frame.height;y++) for (int x=0;x<frame.width;x++)
            {
                int offset=(y*frame.width+x)*2;
                if (!SceneVisionGrounding.ObjectEntryId(Mathf.RoundToInt(frame.depthIds[offset+1]),frame.entries.Length,out int entryIndex,out bool opaque) || !opaque) continue;
                float depth=frame.depthIds[offset];
                if (float.IsNaN(depth) || float.IsInfinity(depth) || depth<=.025f || depth>60) continue;
                if (!checkedEntries[entryIndex])
                {
                    checkedEntries[entryIndex]=true; var entry=frame.entries[entryIndex];
                    if (entry==null || !entry.observedRigid || string.IsNullOrEmpty(entry.entityId) || !entry.TryValidateObserved(false,out _)) continue;
                    if (!masks.TryGetValue(entry.entityId,out var created))
                    { created=new Mask { entity=entry.entityId }; masks.Add(entry.entityId,created); }
                    entryMasks[entryIndex]=created;
                }
                var mask=entryMasks[entryIndex]; if(mask==null)continue;
                int topY=frame.rowsStartAtTop?y:frame.height-1-y;
                float nx=(x+.5f)/frame.width,ny=(topY+.5f)/frame.height;
                float distance=(nx-focus.x)*(nx-focus.x)+(ny-focus.y)*(ny-focus.y);
                mask.count++; mask.minX=Math.Min(mask.minX,x); mask.maxX=Math.Max(mask.maxX,x);
                mask.minY=Math.Min(mask.minY,topY); mask.maxY=Math.Max(mask.maxY,topY); mask.nearest=Math.Min(mask.nearest,distance);
                // Every returned handle must itself resolve through the existing conservative
                // 3x3 opaque test, including small items beside a large mistaken background.
                if (distance<mask.bestInterior && x>0 && y>0 && x<frame.width-1 && y<frame.height-1 &&
                    SceneVisionGrounding.ConsistentObjectPixel(frame,x,y,Mathf.RoundToInt(frame.depthIds[offset+1]),depth))
                { mask.bestInterior=distance; mask.sample=new Vector2(nx,ny); }
            }
            ranked=new List<Mask>();
            foreach(var mask in masks.Values)
                if(!float.IsPositiveInfinity(mask.bestInterior) &&
                    SceneVisionGrounding.TryResolveObject(capture,observation,mask.sample,out var selected,out _) &&
                    selected.VisibleConfirmed && selected.EntityId==mask.entity)
                { mask.observed=selected; mask.authoredHint=CaptureAuthoredHint(selected); ranked.Add(mask); }
            reason=""; return true;
        }

        internal static bool TryRender(NevaSceneVisionCapture capture, SceneVisionObservation observation,
            Vector2 focus, List<Mask> chosen, Mask[] entryMasks, string catalogId, int pageIndex,
            int pageCount, int totalCandidates, bool coverageComplete, out SceneObjectCandidateView view, out string reason)
            => RenderCapturedView(capture, observation, focus, chosen, entryMasks, catalogId, pageIndex,
                pageCount, totalCandidates, coverageComplete, false, out view, out reason);

        internal static bool TryRenderHistorical(NevaSceneVisionCapture capture, SceneVisionObservation observation,
            Vector2 focus, List<Mask> chosen, Mask[] entryMasks, string catalogId, int pageIndex,
            int pageCount, int totalCandidates, out SceneObjectCandidateView view, out string reason)
            => RenderCapturedView(capture, observation, focus, chosen, entryMasks, catalogId, pageIndex,
                pageCount, totalCandidates, false, true, out view, out reason);

        private static bool RenderCapturedView(NevaSceneVisionCapture capture, SceneVisionObservation observation,
            Vector2 focus, List<Mask> chosen, Mask[] entryMasks, string catalogId, int pageIndex,
            int pageCount, int totalCandidates, bool coverageComplete, bool historical, out SceneObjectCandidateView view, out string reason)
        {
            view=null;
            bool Binding(out string failure)
            {
                if (!historical) return Current(capture, observation, out failure);
                float age = observation == null ? -1 : Time.realtimeSinceStartup - observation.CapturedAtRealtime;
                bool valid = capture != null && capture.IsObservationHistoryBindingCurrent(observation) && age >= 0 && age <= SceneVisionObservation.MaximumHistoryAgeSeconds;
                failure = valid ? null : "Historical image session or age is invalid; current-action binding was not substituted.";
                return valid;
            }
            if (!Binding(out reason)) return false;
            var frame=observation.Grounding;
            if(chosen.Count==0){reason="No stable opaque visible candidate in this captured image; no hidden or transparent substitute was supplied.";return false;}
            var items=new Candidate[chosen.Count];
            for(int i=0;i<chosen.Count;i++)
            {
                var item=chosen[i]; items[i]=new Candidate {
                    candidateId="c"+(i+1),marker=(i+1).ToString(),entityId=item.entity,
                    selectionId=catalogId==null?null:catalogId+":p"+pageIndex+":c"+(i+1),
                    referenceId=catalogId==null?null:catalogId+":p"+pageIndex+":c"+(i+1),
                    sourceCaptureId=observation.CaptureId,pointAtCapture=item.observed.WorldPoint,
                    observedRendererBoundsCenter=item.observed.entry.bounds.center,
                    observedRendererBoundsSize=item.observed.entry.bounds.size,
                    authoredHint=item.authoredHint,
                    sceneLabel=catalogId==null?item.observed.SceneLabel:null,
                    identitySource=catalogId==null?item.observed.IdentitySource:"same-frame-opaque-id-and-depth",
                    imagePoint=item.sample,visiblePixelCount=item.count,
                    pixelBounds=new PixelBox(item.minX,item.minY,item.maxX-item.minX+1,item.maxY-item.minY+1),
                    localHierarchy=catalogId==null?Hierarchy(item.observed):null,selected=item.observed };
            }
            Texture2D original=null,composite=null;
            try
            {
                int comma=observation.ImageDataUrl.IndexOf(',');
                if(comma<0)throw new InvalidOperationException("Source observation has no encoded RGB payload.");
                original=new Texture2D(2,2,TextureFormat.RGBA32,false,false) {hideFlags=HideFlags.HideAndDontSave};
                if(!original.LoadImage(Convert.FromBase64String(observation.ImageDataUrl.Substring(comma+1)),false) ||
                    original.width!=frame.width || original.height!=frame.height)
                    throw new InvalidOperationException("Decoded RGB does not match this exact opaque-ID frame.");
                var rgb=original.GetPixels32();
                var annotated=(Color32[])rgb.Clone();
                var ownership=DrawContours(annotated,frame,entryMasks,chosen);
                var markerOrder=new List<Candidate>(items);markerOrder.Sort((a,b)=>a.visiblePixelCount.CompareTo(b.visiblePixelCount));
                var placed=new List<RectInt>();
                foreach(var item in markerOrder)
                {
                    int number=Array.IndexOf(items,item)+1;
                    RectInt marker=PlaceMarker(item,items,placed,frame.width,frame.height,ownership,number,
                        out Vector2Int anchor,out string placement);placed.Add(marker);
                    item.markerPixelBounds=new PixelBox(marker.x,marker.y,marker.width,marker.height);
                    item.markerAnchorPixel=anchor;item.markerPlacement=placement;
                    if(placement=="external-with-leader")DrawMarkerLeader(annotated,frame.width,frame.height,marker,anchor,Palette[number-1]);
                    DrawNumber(annotated,frame.width,frame.height,marker.x,marker.y,number,Palette[number-1]);
                }
                RectInt crop=FocusCrop(frame.width,frame.height,focus);
                // Enlargement also includes candidate outlines, while all source pixels remain
                // the same frozen observation. Nearest neighbor invents no extra image detail.
                Color32[] pixels=ComposePanels(annotated,frame.width,frame.height,crop);
                composite=new Texture2D(frame.width*2+8,frame.height,TextureFormat.RGBA32,false,false) {hideFlags=HideFlags.HideAndDontSave};
                composite.SetPixels32(pixels);composite.Apply(false,false);
                byte[] bytes=composite.EncodeToPNG();
                if(bytes==null || bytes.Length==0)throw new InvalidOperationException("Candidate panel encoding failed.");
                if(!Binding(out reason))return false;
                if(!historical)foreach(var item in items)if(!item.selected.TryValidate(out reason))return false;
                var metadata=new Metadata { captureId=observation.CaptureId,capturedAtUtc=observation.CapturedAtUtc,frame=observation.Frame,
                    catalogId=catalogId,pageIndex=pageIndex,pageCount=pageCount,totalCandidates=totalCandidates,coverageComplete=coverageComplete,
                    historicalOnly=historical,
                    sourceWidth=frame.width,sourceHeight=frame.height,compositeWidth=composite.width,compositeHeight=composite.height,
                    focusImagePoint=focus,fullPanel=new PixelBox(0,0,frame.width,frame.height),
                    cropPanel=new PixelBox(frame.width+8,0,frame.width,frame.height),cropSourcePixels=new PixelBox(crop.x,crop.y,crop.width,crop.height),candidates=items };
                view=new SceneObjectCandidateView(capture,observation,items,"data:image/png;base64,"+Convert.ToBase64String(bytes),metadata);
                return true;
            }
            catch(Exception error){reason="Candidate view could not be derived from this captured RGB: "+error.Message;return false;}
            finally
            {
                if(original!=null)SceneVisionSelfView.DestroyOwned(original);
                if(composite!=null)SceneVisionSelfView.DestroyOwned(composite);
            }
        }

        private static readonly Color32[] Palette = { new Color32(255,212,0,255),new Color32(0,235,255,255),new Color32(255,74,215,255),
            new Color32(96,255,112,255),new Color32(255,128,38,255),new Color32(148,150,255,255) };
        private static LocalHierarchy Hierarchy(SceneObservedObject selected)
        {
            var entry=selected.entry; var names=new List<string>(); Transform part=entry.renderer.transform;
            while(part!=null && names.Count<6)
            { names.Add(BoundedName(part.name)); if(part==selected.Root)break; part=part.parent; }
            var result=new LocalHierarchy { selectedPartName=BoundedName(entry.renderer.name),entityRootName=BoundedName(selected.Root.name),
                selectedRendererType=entry.renderer.GetType().Name,partToEntityRoot=names.ToArray(),pathTruncated=part!=selected.Root };
            var device=selected.Root.GetComponent<SceneObjectLightSwitch>();
            if(device!=null && device.Identity!=null && device.EntityId==selected.EntityId && device.IsBound && device.TryValidate(out _))
            {
                var parts=new List<LocalPart>();
                if(device.EmissionRenderer!=null)parts.Add(new LocalPart {name=BoundedName(device.EmissionRenderer.name),componentType=device.EmissionRenderer.GetType().Name});
                if(device.DeviceLight!=null)parts.Add(new LocalPart {name=BoundedName(device.DeviceLight.name),componentType="Light"});
                result.registeredParts=parts.ToArray();
            }
            else result.registeredParts=new LocalPart[0];
            return result;
        }
        private static string BoundedName(string value)=>string.IsNullOrEmpty(value)?"":value.Substring(0,Math.Min(value.Length,96));
        private static int[] DrawContours(Color32[] rgb,SceneVisionGrounding.Frame frame,Mask[] entries,List<Mask> selected)
        {
            var lookup=new Dictionary<Mask,int>();for(int i=0;i<selected.Count;i++)lookup[selected[i]]=i;
            var label=new int[frame.width*frame.height];
            for(int y=0;y<frame.height;y++)for(int x=0;x<frame.width;x++)
            {
                int raw=(y*frame.width+x)*2;
                float depth=frame.depthIds[raw]; if(float.IsNaN(depth)||float.IsInfinity(depth)||depth<=.025f||depth>60)continue;
                if(SceneVisionGrounding.ObjectEntryId(Mathf.RoundToInt(frame.depthIds[raw+1]),entries.Length,out int ix,out bool opaque) &&
                    opaque && entries[ix]!=null && lookup.TryGetValue(entries[ix],out int marker))
                    label[(frame.rowsStartAtTop?y:frame.height-1-y)*frame.width+x]=marker+1;
            }
            for(int y=0;y<frame.height;y++)for(int x=0;x<frame.width;x++)
            {
                int index=y*frame.width+x,mark=label[index];if(mark==0)continue;
                if(x==0 || y==0 || x==frame.width-1 || y==frame.height-1 || label[index-1]!=mark || label[index+1]!=mark ||
                    label[index-frame.width]!=mark || label[index+frame.width]!=mark)
                    rgb[(frame.height-1-y)*frame.width+x]=Palette[mark-1];
            }
            return label;
        }
        // Pure layout functions are exercised independently by the Editor regression.
        internal static RectInt FocusCrop(int width,int height,Vector2 point)
        {
            int w=Math.Max(1,Mathf.RoundToInt(width*.4f)),h=Math.Max(1,Mathf.RoundToInt(height*.4f));
            return new RectInt(Mathf.Clamp(Mathf.RoundToInt(point.x*width-w*.5f),0,width-w),
                Mathf.Clamp(Mathf.RoundToInt(point.y*height-h*.5f),0,height-h),w,h);
        }
        internal static Color32[] ComposePanels(Color32[] source,int width,int height,RectInt crop)
        {
            int total=width*2+8;var result=new Color32[total*height];
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            {
                result[(height-1-y)*total+x]=source[(height-1-y)*width+x];
                int sx=crop.x+Math.Min(crop.width-1,x*crop.width/width),sy=crop.y+Math.Min(crop.height-1,y*crop.height/height);
                result[(height-1-y)*total+width+8+x]=source[(height-1-sy)*width+sx];
            }
            return result;
        }
        private static readonly string[] Digits={"010111010010111","110001010100111","110001010001110","101101111001001","111100110001110","011100111101111"};
        // Place a large object's entire label on its own rendered opaque surface.
        // A nearby small object's pixels must never become that label's implied anchor.
        internal static RectInt PlaceMarker(Candidate item,Candidate[] all,List<RectInt> placed,int width,int height,
            int[] ownership,int ownIndex,out Vector2Int anchor,out string placement)
        {
            var box=item.pixelBounds;
            anchor=new Vector2Int(Mathf.Clamp(Mathf.FloorToInt(item.imagePoint.x*width),0,width-1),
                Mathf.Clamp(Mathf.FloorToInt(item.imagePoint.y*height),0,height-1));
            if(box.width>=12&&box.height>=16&&ownership!=null&&ownership.Length==width*height)
            {
                // Summed-area occupancy keeps exhaustive placement linear in image size;
                // every accepted rectangle contains only this candidate's real pixels.
                int stride=width+1;var sums=new int[stride*(height+1)];
                for(int y=0;y<height;y++)
                {
                    int row=0;
                    for(int x=0;x<width;x++)
                    {row+=ownership[y*width+x]==ownIndex?1:0;sums[(y+1)*stride+x+1]=sums[y*stride+x+1]+row;}
                }
                RectInt best=default;float bestCost=float.PositiveInfinity;
                float cx=box.x+box.width*.5f,cy=box.y+box.height*.5f;
                int xmax=Math.Min(width-12,box.x+box.width-12),ymax=Math.Min(height-16,box.y+box.height-16);
                for(int y=Math.Max(0,box.y);y<=ymax;y++)for(int x=Math.Max(0,box.x);x<=xmax;x++)
                {
                    int owned=sums[(y+16)*stride+x+12]-sums[y*stride+x+12]-sums[(y+16)*stride+x]+sums[y*stride+x];
                    if(owned!=12*16)continue;
                    var rect=new RectInt(x,y,12,16);bool overlap=false;
                    foreach(var occupied in placed)if(rect.Overlaps(occupied)){overlap=true;break;}
                    if(overlap)continue;
                    float cost=Mathf.Abs(x+6-cx)+Mathf.Abs(y+8-cy);
                    foreach(var other in all)
                    {
                        if(ReferenceEquals(other,item))continue;
                        var b=other.pixelBounds;
                        if(b.width*b.height<width*height*.2f&&rect.Overlaps(new RectInt(b.x-16,b.y-16,b.width+32,b.height+32)))cost+=100000;
                    }
                    if(cost<bestCost){bestCost=cost;best=rect;}
                }
                if(!float.IsPositiveInfinity(bestCost))
                {
                    anchor=new Vector2Int(best.x+6,best.y+8);placement="inside-own-opaque-mask";return best;
                }
            }
            placement="external-with-leader";
            return MarkerRect(item,all,placed,width,height);
        }

        internal static void DrawMarkerLeader(Color32[] image,int width,int height,RectInt marker,Vector2Int anchor,Color32 color)
        {
            int sx=Mathf.Clamp(anchor.x,marker.x,marker.xMax-1),sy=Mathf.Clamp(anchor.y,marker.y,marker.yMax-1);
            void Pixel(int x,int y,Color32 value)
            {if(x>=0&&y>=0&&x<width&&y<height)image[(height-1-y)*width+x]=value;}
            void Line(bool border)
            {
                int x=sx,y=sy,dx=Math.Abs(anchor.x-sx),dy=-Math.Abs(anchor.y-sy);
                int stepx=sx<anchor.x?1:-1,stepy=sy<anchor.y?1:-1,error=dx+dy;
                while(true)
                {
                    if(border)for(int yy=-1;yy<=1;yy++)for(int xx=-1;xx<=1;xx++)Pixel(x+xx,y+yy,new Color32(16,18,22,255));
                    else Pixel(x,y,color);
                    if(x==anchor.x&&y==anchor.y)break;
                    int twice=2*error;if(twice>=dy){error+=dy;x+=stepx;}if(twice<=dx){error+=dx;y+=stepy;}
                }
            }
            Line(true);Line(false);
            // A compact cross terminates on the already verified opaque interior.
            for(int d=-2;d<=2;d++){Pixel(anchor.x+d,anchor.y,color);Pixel(anchor.x,anchor.y+d,color);}
        }

        private static RectInt MarkerRect(Candidate item,Candidate[] all,List<RectInt> placed,int width,int height)
        {
            var box=item.pixelBounds;int px=Mathf.FloorToInt(item.imagePoint.x*width),py=Mathf.FloorToInt(item.imagePoint.y*height);
            var options=new[]{new Vector2Int(box.x+box.width+3,py-8),new Vector2Int(box.x-15,py-8),
                new Vector2Int(px-6,box.y-19),new Vector2Int(px-6,box.y+box.height+3),
                new Vector2Int(px-6,py-8),new Vector2Int(px+20,py-8),new Vector2Int(px-30,py-8),new Vector2Int(px-6,py+20)};
            RectInt best=new RectInt(0,0,12,16);float score=float.PositiveInfinity;
            foreach(var point in options)
            {
                var rect=new RectInt(Mathf.Clamp(point.x,0,width-12),Mathf.Clamp(point.y,0,height-16),12,16);
                float cost=Mathf.Abs(rect.x-px)+Mathf.Abs(rect.y-py);
                foreach(var candidate in all)
                {
                    var bounds=candidate.pixelBounds;
                    // A background may occupy the full frame; protect compact visible targets
                    // rather than making placement impossible beside that background.
                    if(bounds.width*bounds.height<width*height*.2f && rect.Overlaps(new RectInt(bounds.x,bounds.y,bounds.width,bounds.height)))cost+=100000;
                }
                foreach(var occupied in placed)if(rect.Overlaps(occupied))cost+=100000;
                if(cost<score){score=cost;best=rect;}
            }
            return best;
        }
        private static void DrawNumber(Color32[] image,int width,int height,int left,int top,int number,Color32 color)
        {
            const int scale=2,padding=3;
            for(int yy=0;yy<16;yy++)for(int xx=0;xx<12;xx++)
            {
                int gx=(xx-padding)/scale,gy=(yy-padding)/scale;
                bool ink=xx>=padding&&yy>=padding&&gx>=0&&gx<3&&gy>=0&&gy<5&&Digits[number-1][gy*3+gx]=='1';
                int px=left+xx,py=top+yy;if(px<width&&py<height)image[(height-1-py)*width+px]=ink?color:new Color32(16,18,22,255);
            }
        }
    }
}
