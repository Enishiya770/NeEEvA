using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using System.Linq;
using UnityEngine;

/// <summary>Pure paging policy checks. Real RGB, hidden-object and lifecycle checks
/// belong to SceneSearchRegression's actual capture fixture.</summary>
public static class SceneVisibleCatalogRegression
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    public static int RunPureChecks()
    {
        int count = 0;
        Type maskType = typeof(SceneObjectCandidateView).GetNestedType("Mask", Flags);
        MethodInfo order = typeof(SceneVisibleObjectCatalog).GetMethod("DiverseOrder", Flags);
        MethodInfo region = typeof(SceneVisibleObjectCatalog).GetMethod("ImageRegion", Flags);
        if (maskType == null || order == null || region == null) throw new InvalidOperationException("Production catalog paging helpers missing.");
        IList NewList() => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(maskType));
        object Mask(string id, int pixels, Vector2 point)
        {
            object value = Activator.CreateInstance(maskType, true);
            maskType.GetField("entity", Flags).SetValue(value, id);
            maskType.GetField("count", Flags).SetValue(value, pixels);
            maskType.GetField("sample", Flags).SetValue(value, point);
            return value;
        }
        string Id(object mask) => (string)maskType.GetField("entity", Flags).GetValue(mask);
        void Check(bool value, string message) { count++; if (!value) throw new InvalidOperationException(message); }

        var memoryAge = typeof(ChatSample).GetMethod("SceneSearchMemoryAgeValid", Flags);
        Check(memoryAge != null, "Search memory uses the production capture-time age gate.");
        bool FreshPoint(float capturedAt, float now) => (bool)memoryAge.Invoke(null, new object[] { capturedAt, now });
        Check(FreshPoint(10f, 10f) && FreshPoint(10f, 1810f), "Frozen historical points use the shared inclusive 30-minute history budget.");
        Check(!FreshPoint(10f, 1810.01f) && !FreshPoint(10f, 9f), "Expired and future-dated historical points cannot be offered or resolved.");
        Check(!FreshPoint(float.NaN, 10f) && !FreshPoint(10f, float.PositiveInfinity) && !FreshPoint(float.NegativeInfinity, 10f),
            "Non-finite capture or clock values cannot bypass the history budget.");

        var shortestYaw = typeof(ChatSample).GetMethod("SceneSearchShortestYaw", Flags);
        Check(shortestYaw != null, "Search uses the production shortest horizontal direction helper.");
        float Yaw(float from, float to) => (float)shortestYaw.Invoke(null, new object[] {
            Quaternion.AngleAxis(from, Vector3.up) * Vector3.forward, Quaternion.AngleAxis(to, Vector3.up) * Vector3.forward });
        Check(Mathf.Abs(Yaw(0, 35) - 35) < .01f && Mathf.Abs(Yaw(0, -35) + 35) < .01f,
            "Measured positive yaw is right and negative yaw is left.");
        Check(Mathf.Abs(Yaw(179, -179) - 2) < .01f && Mathf.Abs(Yaw(-179, 179) + 2) < .01f,
            "Crossing the +/-180 boundary takes the shortest two-degree direction, never a 358-degree loop.");
        Check(Mathf.Abs(Yaw(120, -150) - 90) < .01f && Mathf.Abs(Yaw(150, -150) - 60) < .01f,
            "The same destination has different measured yaw from body and independently turned camera frames.");

        var turnOwner = typeof(ChatSample).GetMethod("SceneSearchBodyTurnOwnsDisplacement", Flags);
        var rootDisplacement = typeof(ChatSample).GetMethod("SceneSearchRootDisplacementAllowed", Flags);
        Check(turnOwner != null && rootDisplacement != null, "Search uses separate exact-turn ownership and bounded root displacement gates.");
        var ownedTurn = new object[] { "moving", "owned-turn", "owned-turn", "turn-in-place", "owned-turn", 7, 7, true };
        bool Owns(object[] stamp) => (bool)turnOwner.Invoke(null, stamp);
        bool Allows(float stationary, float action, object[] stamp) =>
            (bool)rootDisplacement.Invoke(null, new object[] { stationary, action, Owns(stamp) });
        Check(Owns(ownedTurn) && Allows(.10f, .10f, ownedTurn) && Allows(.18f, .16f, ownedTurn),
            "An exact owned native turn may use its own inclusive 16 cm displacement budget, even when the previous stationary baseline was farther away.");
        Check(!Allows(.1601f, .1601f, ownedTurn), "Even the owned turn cannot exceed its native 16 cm station bound.");
        foreach (var change in new[] { new object[] { 0, "observing" }, new object[] { 1, "" },
            new object[] { 2, "foreign-fact" }, new object[] { 3, "orient-view" }, new object[] { 4, "foreign-bridge-action" },
            new object[] { 6, 8 }, new object[] { 7, false } })
        {
            var changed = (object[])ownedTurn.Clone(); changed[(int)change[0]] = change[1];
            Check(!Owns(changed) && !Allows(.05f, .05f, changed) && Allows(.039f, .039f, changed) && !Allows(.04f, .04f, changed),
                "An inactive/replaced/non-body/unverified action keeps the original strict 4 cm stationary boundary; changed owner field " + change[0] + ".");
        }
        Check(!Allows(float.NaN, .1f, ownedTurn) && !Allows(.1f, float.PositiveInfinity, ownedTurn),
            "Non-finite root measurements cannot acquire either stationary or owned-body displacement permission.");

        var authoredFactory = typeof(SceneObjectCandidateView).GetMethod("CreateAuthoredHint", Flags);
        var authored = (SceneObjectCandidateView.AuthoredHint)authoredFactory.Invoke(null,
            new object[] { "captured-frame", "陶器のマグ\nCup", "作業机", "blue ceramic cup", "renderer-session" });
        Check(authored.sourceCaptureId == "captured-frame" && authored.rendererName == "陶器のマグCup" &&
            authored.entityRootName == "作業机" && !authored.semanticIdentityConfirmed && !authored.actionAuthorization,
            "General Unicode authoring names carry frozen source provenance without becoming target recognition or device authority.");
        var boundedName = (SceneObjectCandidateView.AuthoredHint)authoredFactory.Invoke(null,
            new object[] { "captured-frame", new string('x', 300), null, "", "scene-authored" });
        Check(boundedName.rendererName.Length == 96 && boundedName.entityRootName == "",
            "Authored clues have a fixed per-field budget and tolerate missing labels.");
        SceneVisibleObjectCatalog.Entry Geometry(string id, string capture, Vector3 point, Vector3 center, Vector3 size) =>
            new SceneVisibleObjectCatalog.Entry { entityId = id, sourceCaptureId = capture, pointAtCapture = point,
                observedRendererBoundsCenter = center, observedRendererBoundsSize = size };
        var tableGeometry = Geometry("desk", "same-image", new Vector3(0, 1, 0), new Vector3(0, .5f, 0), new Vector3(2, 1, 2));
        var cupGeometry = Geometry("ceramic-cup", "same-image", new Vector3(0, 1.1f, 0), new Vector3(0, 1.1f, 0), new Vector3(.2f, .2f, .2f));
        var otherFrame = Geometry("old-picture", "another-image", cupGeometry.pointAtCapture, Vector3.zero, Vector3.one);
        var neighborBuilder = typeof(SceneVisibleObjectCatalog).GetMethod("PopulateGeometryNeighbors", Flags);
        neighborBuilder.Invoke(null, new object[] { new[] { cupGeometry, otherFrame, tableGeometry } });
        Check(cupGeometry.geometryNeighbor.entityId == "desk" && cupGeometry.geometryNeighbor.sourceCaptureId == "same-image" &&
            Mathf.Abs(cupGeometry.geometryNeighbor.observedPointOffset.y + .1f) < .0001f &&
            Mathf.Abs(cupGeometry.geometryNeighbor.verticalBoundsGapMeters) < .0001f && otherFrame.geometryNeighbor == null,
            "Geometry neighbors use frozen same-frame points and measured bounds, never a closer point from another image.");
        Check(!cupGeometry.semanticIdentityConfirmed && !cupGeometry.actionAuthorization &&
            cupGeometry.geometryNeighbor.scope.Contains("do not establish"),
            "Even a measured zero vertical gap does not assert contact/support or authorize action.");
        var neighborJson = (JObject)typeof(ChatSample).GetMethod("SceneVisibleGeometryNeighborJson", Flags)
            .Invoke(null, new object[] { cupGeometry.geometryNeighbor });
        Check(((JObject)neighborJson["observedPointOffset"]).Properties().Select(property => property.Name)
                .SequenceEqual(new[] { "x", "y", "z" }) && neighborJson.ToString().Length < 1200,
            "Neighbor JSON uses only explicit vector components, never Unity normalized/magnitude properties or recursive serialization.");

        var raw = new JObject { ["task"] = new JObject { ["mode"] = "find" }, ["imageRole"] = "current", ["currentCaptureId"] = "original-frame",
            ["visibleCatalog"] = new JObject { ["current"] = new JObject {
                ["candidates"] = new JArray(new JObject { ["referenceId"] = "original-frame:visible:p0:c1", ["selectionId"] = "original-frame:visible:p0:c1", ["entityId"] = "a", ["selectionAllowed"] = true },
                    new JObject { ["referenceId"] = "original-frame:visible:p0:c2", ["entityId"] = "b", ["selectionAllowed"] = false }),
                ["availablePages"] = new JArray(new JObject { ["referenceId"] = "original-frame:visible:p0", ["reviewed"] = true, ["selectionAllowed"] = false },
                    new JObject { ["referenceId"] = "original-frame:visible:p1", ["reviewed"] = false, ["selectionAllowed"] = true }) },
                ["memory"] = new JArray(new JObject { ["referenceId"] = "known-object-point:older:a", ["selectionAllowed"] = true }), ["historicalViews"] = new JArray() },
            ["explorationRegions"] = new JArray(new JObject { ["referenceId"] = "region-3", ["selectionAllowed"] = true }) };
        string Reply(string choice, string reference) => new JObject { ["choice"] = choice, ["referenceId"] = reference, ["selectionRole"] = choice == "current-candidate" || choice == "historical-object" ? "target" : "exploration", ["reason"] = "Current listed option." }.ToString();
        var landmark = JObject.Parse(Reply("current-candidate", "q1-c1")); landmark["selectionRole"] = "landmark";
        Check(!SceneSearchProtocol.TryDecision(landmark.ToString(), out _, out _),
            "A current landmark cannot enter the target-confirmation path even with a legal candidate reference.");
        landmark["choice"] = "historical-object";
        Check(SceneSearchProtocol.TryDecision(landmark.ToString(), out var landmarkRecall, out _) && landmarkRecall.selectionRole == "landmark",
            "A historical landmark may be a direction clue without being the target itself.");
        landmark.Remove("selectionRole");
        Check(!SceneSearchProtocol.TryDecision(landmark.ToString(), out _, out _), "Old role-free search replies cannot bypass target/landmark classification.");
        var samePose = typeof(ChatSample).GetMethod("SceneSearchSameInspectionPose", Flags);
        bool Same(Vector3 firstPosition, Quaternion first, Vector3 nextPosition, Quaternion next) =>
            (bool)samePose.Invoke(null, new object[] { firstPosition, first * Vector3.forward, nextPosition, next * Vector3.forward });
        Check(Same(Vector3.zero, Quaternion.identity, Vector3.zero, Quaternion.Euler(0, 0, 45)),
            "Camera roll alone does not erase failed-entity evidence.");
        Check(Same(Vector3.zero, Quaternion.identity, new Vector3(.015f, 0, 0), Quaternion.Euler(3, 7, 8)),
            "Observed idle-sized translation and head drift retain the previous inspection result.");
        Check(!Same(Vector3.zero, Quaternion.identity, Vector3.zero, Quaternion.Euler(0, 20, 0)) &&
            !Same(Vector3.zero, Quaternion.identity, new Vector3(.2f, 0, 0), Quaternion.identity),
            "A materially different view or position allows a fresh independent target inspection.");
        var scoped = SceneSearchProtocol.CreateScopedInput(raw, "q1", out var firstScope);
        var allowed = (JArray)scoped["requestChoices"];
        Check(SceneSearchProtocol.TryResolveDecision(Reply("current-candidate", "q1-c1"), firstScope, out var selected, out _) &&
            selected.referenceId == "original-frame:visible:p0:c1", "The current request's short candidate reference resolves to its original exact engine capture reference.");
        Check(!SceneSearchProtocol.TryResolveDecision(Reply("current-candidate", "original-frame:visible:p0:c1"), firstScope, out _, out _) &&
            !SceneSearchProtocol.TryResolveDecision(Reply("current-candidate", "q1-c2"), firstScope, out _, out _),
            "Neither copied long IDs nor current but excluded/rejected candidates bypass the allowed choice set.");
        Check(!SceneSearchProtocol.TryResolveDecision(Reply("next-page", "q1-p1"), firstScope, out _, out _) &&
            SceneSearchProtocol.TryResolveDecision(Reply("next-page", "q1-p2"), firstScope, out _, out _),
            "A reviewed page cannot be selected again while the unreviewed listed page can be selected.");
        SceneSearchProtocol.CreateScopedInput(raw, "q2", out var secondScope);
        Check(!SceneSearchProtocol.TryResolveDecision(Reply("current-candidate", "q1-c1"), secondScope, out _, out _),
            "A once-valid alias from another request never targets a replacement current frame.");
        Check(SceneSearchProtocol.TryDecisionForInput(Reply("current-candidate", "q1-c1"), scoped.ToString(), out _, out _) &&
            !SceneSearchProtocol.TryDecisionForInput(Reply("current-candidate", "q2-c1"), scoped.ToString(), out _, out _) &&
            !SceneSearchProtocol.TryDecisionForInput(Reply("current-candidate", "q1-c2"), scoped.ToString(), out _, out _),
            "Read-only model replays use the same exact request choice boundary rather than accepting structurally valid but unlisted references.");
        var oldPage = new JObject { ["task"] = new JObject { ["mode"] = "find" }, ["imageRole"] = "historical",
            ["historicalReferenceId"] = "older-frame", ["historicalCandidates"] = new JArray(
                new JObject { ["referenceId"] = "historical-point:older-frame:p0:c1", ["selectionAllowed"] = true },
                new JObject { ["referenceId"] = "historical-point:older-frame:p0:c2", ["selectionAllowed"] = false }) };
        var historyScope = SceneSearchProtocol.CreateScopedInput(oldPage, "oldq", out var oldMap);
        string oldReply = new JObject { ["choice"] = "historical-object", ["selectionRole"] = "target", ["referenceId"] = "oldq-m1", ["reason"] = "Old-image hint only.",
            ["observations"] = new JArray(new JObject { ["referenceId"] = "oldq-m1", ["description"] = "a ceramic cup" }) }.ToString();
        Check(SceneSearchProtocol.TryResolveDecision(oldReply, oldMap, out var oldSelection, out _) &&
            oldSelection.referenceId == "historical-point:older-frame:p0:c1" &&
            oldSelection.observations[0].referenceId == "historical-point:older-frame:p0:c1" &&
            SceneSearchProtocol.TryDecisionForInput(oldReply, historyScope.ToString(), out _, out _),
            "Historical short references and description notes resolve only to their actual historical page, without current-candidate authority.");
        var wrongHistoricalNote = JObject.Parse(oldReply); wrongHistoricalNote["observations"][0]["referenceId"] = "oldq-m99";
        Check(!SceneSearchProtocol.TryResolveDecision(Reply("historical-object", "oldq-m2"), oldMap, out _, out _) &&
            !SceneSearchProtocol.TryResolveDecision(oldReply.Replace("oldq-m1", "q1-c1"), oldMap, out _, out _) &&
            !SceneSearchProtocol.TryResolveDecision(wrongHistoricalNote.ToString(), oldMap, out _, out _),
            "Invalidated points and references from the current or another historical page cannot become a recalled target.");
        var noteReply = JObject.Parse(Reply("done", "")); noteReply["observations"] = new JArray(new JObject { ["referenceId"] = "q1-c2", ["description"] = "A different current object." });
        Check(SceneSearchProtocol.TryResolveDecision(noteReply.ToString(), firstScope, out var described, out _) &&
            described.observations[0].referenceId == "original-frame:visible:p0:c2", "Describing a visible non-target object does not authorize selecting it as the target.");
        raw["task"]["mode"] = "survey";
        var survey = SceneSearchProtocol.CreateScopedInput(raw, "q3", out var surveyScope);
        Check(!SceneSearchProtocol.TryResolveDecision(Reply("current-candidate", "q3-c1"), surveyScope, out _, out _) &&
            !((JArray)survey["requestChoices"]).OfType<JObject>().Any(row => (string)row["choice"] == "current-candidate" || (string)row["choice"] == "historical-object"),
            "Survey exposes page/direction/stop decisions and observations, not individual find targets or historical object recall.");
        var dynamicSchema = JObject.Parse(SceneSearchProtocol.SchemaForInput(survey.ToString()));
        Check(dynamicSchema["properties"] == null && ((JArray)dynamicSchema["oneOf"]).OfType<JObject>().All(branch =>
            (string)branch["type"] == "object" && (bool?)branch["additionalProperties"] == false &&
            ((JArray)branch["required"]).Values<string>().SequenceEqual(new[] { "choice", "referenceId", "selectionRole", "reason" }) &&
            new HashSet<string>(((JObject)branch["properties"]).Properties().Select(property => property.Name))
                .SetEquals(new[] { "choice", "referenceId", "selectionRole", "reason", "observations" }) &&
            ((JArray)branch["properties"]["selectionRole"]["enum"]).Values<string>().SequenceEqual(new[] { "exploration" }) &&
            !((JArray)branch["properties"]["choice"]["enum"]).Values<string>().Any(kind => kind == "current-candidate" || kind == "historical-object") &&
            ((JArray)branch["properties"]["referenceId"]["enum"]).Values<string>().All(reference => reference == "" || reference.StartsWith("q3-", StringComparison.Ordinal))),
            "Each local decoder branch is a complete required object restricted to the exact request's allowed kind/short references, without partial root overrides.");
        foreach (var offeredInput in new[] { scoped, historyScope, survey })
        {
            var branches = (JArray)JObject.Parse(SceneSearchProtocol.SchemaForInput(offeredInput.ToString()))["oneOf"];
            var offeredChoices = ((JArray)offeredInput["requestChoices"]).OfType<JObject>().ToArray();
            Check(branches.Count == offeredChoices.Select(row => (string)row["choice"]).Distinct().Count() &&
                branches.OfType<JObject>().All(branch => {
                    var kinds = ((JArray)branch["properties"]["choice"]["enum"]).Values<string>().ToArray();
                    if (kinds.Length != 1) return false;
                    string kind = kinds[0];
                    var references = ((JArray)branch["properties"]["referenceId"]["enum"]).Values<string>().ToArray();
                    var roles = ((JArray)branch["properties"]["selectionRole"]["enum"]).Values<string>();
                    return references.SequenceEqual(offeredChoices.Where(row => (string)row["choice"] == kind).Select(row => (string)row["referenceId"])) &&
                        roles.SequenceEqual(kind == "current-candidate" ? new[] { "target" } :
                            kind == "historical-object" ? new[] { "target", "landmark" } : new[] { "exploration" });
                }), "Current, historical and survey decoder branches preserve exact choice/reference pairs and their required role boundaries.");
        }

        var source = NewList();
        for (int tier = 2; tier >= 0; tier--)
            for (int cell = 5; cell >= 0; cell--)
                source.Add(Mask("cell-" + cell + "-size-" + tier, 9 + tier * 100,
                    new Vector2((cell % 3 + .5f) / 3, (cell / 3 + .5f) / 2)));
        var all = (IList)order.Invoke(null, new object[] { source, SceneVisibleObjectCatalog.MaximumCandidates });
        Check(all.Count == 18, "All stable offered candidates remain available beyond the first six.");
        var unique = new HashSet<string>();
        for (int i = 0; i < all.Count; i++)
        {
            Check(unique.Add(Id(all[i])), "A candidate appears on exactly one page.");
            Check(Id(all[i]) == "cell-" + (i % 6) + "-size-" + (i / 6),
                "Each page covers all populated regions and smaller visible candidates precede large backgrounds.");
        }
        var reversed = NewList();
        for (int i = source.Count - 1; i >= 0; i--) reversed.Add(source[i]);
        var again = (IList)order.Invoke(null, new object[] { reversed, SceneVisibleObjectCatalog.MaximumCandidates });
        for (int i = 0; i < all.Count; i++) Check(Id(all[i]) == Id(again[i]), "Paging is deterministic and independent of enumeration order or planner focus.");
        var capped = (IList)order.Invoke(null, new object[] { source, 7 });
        Check(capped.Count == 7 && Id(capped[6]) == "cell-0-size-1", "Bounded truncation preserves regional round-robin ordering.");
        var crowded = NewList();
        for (int i = 0; i < 110; i++) crowded.Add(Mask("crowded-" + i.ToString("D3"), 1000 + i, new Vector2(.1f, .1f)));
        crowded.Add(Mask("tiny-far-region", 9, new Vector2(.95f, .95f)));
        var bounded = (IList)order.Invoke(null, new object[] { crowded, SceneVisibleObjectCatalog.MaximumCandidates });
        Check(bounded.Count == 96, "Catalog storage remains bounded in a dense view.");
        Check(Id(bounded[1]) == "tiny-far-region", "A small visible object in a distant region is not removed by a dense nearby cluster.");
        Check((int)region.Invoke(null, new object[] { Vector2.zero }) == 0 &&
            (int)region.Invoke(null, new object[] { Vector2.one }) == 5, "Source-image boundary pixels remain inside the six regions.");
        Check(((IList)order.Invoke(null, new object[] { NewList(), 96 })).Count == 0, "An empty capture catalog remains empty without inventing a candidate.");
        MethodInfo place=typeof(SceneObjectCandidateView).GetMethod("PlaceMarker",Flags);
        MethodInfo leader=typeof(SceneObjectCandidateView).GetMethod("DrawMarkerLeader",Flags);
        Check(place!=null&&leader!=null,"Production ownership-aware marker layout and leader renderer exist.");
        SceneObjectCandidateView.PixelBox Box(int x,int y,int w,int h) =>
            (SceneObjectCandidateView.PixelBox)Activator.CreateInstance(typeof(SceneObjectCandidateView.PixelBox),Flags,null,new object[]{x,y,w,h},null);
        const int width=200,height=160;
        var ownership=new int[width*height];
        for(int y=80;y<150;y++)for(int x=40;x<190;x++)ownership[y*width+x]=6;
        for(int y=73;y<81;y++)for(int x=80;x<88;x++)ownership[y*width+x]=3;
        var cabinet=new SceneObjectCandidateView.Candidate {candidateId="c6",imagePoint=new Vector2(90.5f/width,81.5f/height),pixelBounds=Box(40,80,150,70)};
        var small=new SceneObjectCandidateView.Candidate {candidateId="c3",imagePoint=new Vector2(84.5f/width,76.5f/height),pixelBounds=Box(80,73,8,8)};
        var candidates=new[]{small,cabinet};var placed=new List<RectInt>();
        Vector2 originalPoint=cabinet.imagePoint;
        var args=new object[]{cabinet,candidates,placed,width,height,ownership,6,null,null};
        var inside=(RectInt)place.Invoke(null,args);
        Check((string)args[8]=="inside-own-opaque-mask","A large cabinet label stays on the cabinet instead of floating beside a neighboring lamp.");
        bool owned=true;
        for(int y=inside.y;y<inside.yMax;y++)for(int x=inside.x;x<inside.xMax;x++)owned&=ownership[y*width+x]==6;
        Check(owned,"Every pixel of an inside marker belongs to its exact opaque entity, including around a small foreground hole.");
        Check(!inside.Overlaps(new RectInt(64,57,40,40)),"A large marker prefers clearance from the compact neighboring target.");
        Check(cabinet.imagePoint==originalPoint,"Annotation placement never moves the operation's original source image point.");
        placed.Add(inside);
        args=new object[]{small,candidates,placed,width,height,ownership,3,null,null};
        var outside=(RectInt)place.Invoke(null,args);var anchor=(Vector2Int)args[7];
        Check((string)args[8]=="external-with-leader","A target too small for the label gets an explicitly connected external marker.");
        Check(anchor==new Vector2Int(84,76)&&ownership[anchor.y*width+anchor.x]==3,"External leader endpoint belongs to the verified small target, not its supporting furniture.");
        var pixels=new Color32[width*height];var pink=new Color32(255,74,215,255);
        leader.Invoke(null,new object[]{pixels,width,height,outside,anchor,pink});
        Check(pixels[(height-1-anchor.y)*width+anchor.x].Equals(pink),"Leader renderer terminates with the candidate color at its exact top-left-origin anchor.");
        return count;
    }

    // Called by the real-capture search fixture after a normal human-input visual
    // reset and a new pose/capture. This method starts no Unity instance or model.
    public static int RunHistoryContinuityChecks(ChatSample chat, SceneVisionObservation historical, SceneVisionObservation current)
    {
        int count = 0;
        void Check(bool value, string message) { count++; if (!value) throw new InvalidOperationException(message); }
        object Call(string method, params object[] args) => typeof(ChatSample).GetMethod(method, Flags).Invoke(chat, args);
        var source = chat.SceneVisionCapture;
        bool currentBinding = historical != null && (bool)typeof(NevaSceneVisionCapture).GetMethod("IsObservationBindingCurrent", Flags)
            .Invoke(source, new object[] { historical });
        Check(historical != null && current != null && historical.CaptureId != current.CaptureId &&
            !currentBinding && source.IsObservationHistoryBindingCurrent(historical),
            "A real new human turn revokes the old current-pixel lease while retaining the original historical image session.");
        var args = new object[] { historical, current, 0, null, null, null };
        Check((bool)Call("TryGetSceneSearchHistoricalPage", args), "The actual earlier catalog remains renderable after the user-turn reset: " + args[5]);
        var rows = (JArray)args[4];
        Check(rows.Count > 0 && rows.Count <= 6 && ((string)args[3]).StartsWith("data:image/png;base64,", StringComparison.Ordinal) &&
            rows.All(row => (string)row["sourceCaptureId"] == historical.CaptureId && row["authoredHint"] is JObject &&
                (bool?)row["semanticIdentityConfirmed"] == false && (bool?)row["actionAuthorization"] == false),
            "Historical pages contain at most six actual old-image candidates with source-bound authoring clues and no semantic/action authority.");
        var offered = rows.OfType<JObject>().FirstOrDefault(row => (bool?)row["selectionAllowed"] == true);
        Check(offered != null, "The retained static test scene offers at least one geometry-valid historical surface point.");
        var resolve = new object[] { historical, (string)offered["referenceId"], null, null };
        Check((bool)Call("TryResolveSceneSearchHistoricalPoint", resolve), "The offered historical point resolves without making its pixels current.");
        var point = (JObject)offered["worldPoint"];
        Check(Vector3.Distance((Vector3)resolve[2], new Vector3((float)point["x"], (float)point["y"], (float)point["z"])) < .0001f,
            "Historical selection preserves the exact frozen source point rather than updating it from live transforms.");
        Check(!(bool)Call("TryResolveSceneSearchHistoricalPoint", current, (string)offered["referenceId"], null, null) &&
            !(bool)Call("TryResolveSceneSearchHistoricalPoint", historical, "free-object-id", null, null),
            "Wrong-source images and invented entity references cannot resolve a historical point.");
        var preview = chat.LastSceneSearchCandidateView;
        Check(preview != null && preview.IsHistorical && preview.CaptureId == historical.CaptureId &&
            !preview.TryGetCandidate(preview.Candidates[0].candidateId, out _, out _),
            "The diagnostic historical composite retains its old source and always rejects the current-action candidate API.");
        string context = (string)Call("BuildSceneCognitionContext", current);
        Check(context.Contains(historical.CaptureId) && context.Contains("authoredHint") &&
            context.Contains("cameraYawDegreesRight") && context.Contains("bodyYawDegreesRight"),
            "Ordinary dialogue/autonomy cognition includes the earlier source and separate measured camera/body bearings without initiating a search.");
        var diagnostics = JObject.Parse(chat.GetSceneVisibleCatalogDiagnosticsJson());
        Check((string)diagnostics["currentCaptureId"] == current.CaptureId &&
            ((JArray)diagnostics["retainedObjects"]).Any(row => (string)row["sourceCaptureId"] == historical.CaptureId),
            "The read-only diagnostic panel separates the actual current catalog from cross-turn historical object records.");
        return count;
    }
}
