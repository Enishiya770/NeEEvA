using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>A search-step view; engine observations and reference offers remain intact.</summary>
public static class SceneSearchContextPolicy
{
    public const string Contract = "searchAttention is a read-only search-step projection. All offered entities, historical identity constraints, original user wording and requestChoices remain. currentPoseRef names the root selfPose/currentSelfPose object: it supplies the EXACT same current captureId, capturedAtUtc, hasBodyPose and camera/body relative angles omitted from this repeated pose. sinceHistoricalCapture always belongs to its own historical capture and stays separate. Differing pose stamps remain inline. Derived camera/body bearings and frozen points retain their provenance. Redundant raw pose/renderer diagnostics stay in Unity; omission never proves absence, recognition or permission. Revisit and independently match a fresh image before acting.";

    public static JObject Project(JObject source, out JObject diagnostic)
    {
        var result = (JObject)source.DeepClone();
        var fields = new JArray();
        string sharedPoseField=result["selfPose"] is JObject ? "selfPose" : "currentSelfPose";
        var sharedPose=result[sharedPoseField] as JObject;
        string[] sharedPoseKeys={ "captureId", "capturedAtUtc", "hasBodyPose", "cameraYawRelativeToBodyDegrees", "cameraPitchRelativeToBodyDegreesUp" };
        void Drop(JObject node, string path, params string[] keys)
        {
            if (node == null) return;
            foreach (string key in keys)
                if (node[key] != null && (RoomTaskContextPolicy.NumericDiagnostic(node[key]) ||
                    ((key == "selectedRendererType" || key == "markerPlacement") && node[key].Type == JTokenType.String))) { fields.Add(new JObject { ["path"] = path + "." + key,
                    ["beforeCharacters"] = node[key].ToString(Formatting.None).Length, ["afterCharacters"] = 0 }); node.Remove(key); }
        }
        // This consumer chooses an offered reference, not a trajectory, renderer
        // hierarchy or a new world coordinate. Retain the actual frozen points,
        // bounds, neighbour relation, identity/provenance and current bearings.
        void Row(JObject row, string path)
        {
            Drop(row, path, "markerPixelBounds", "markerAnchorPixel", "markerPlacement");
            Drop(row["localHierarchy"] as JObject, path + ".localHierarchy", "selectedRendererType", "registeredParts");
            foreach (string bearing in new[] { "directionFromCurrentPose", "relativeToCurrentView", "returnDirection" })
            {
                if (!(row[bearing] is JObject direction)) continue;
                var pose = direction["selfPose"] as JObject;
                Drop(pose, path + "." + bearing + ".selfPose", "cameraPosition", "cameraRotation", "cameraForward",
                    "bodyPosition", "bodyRotation", "bodyForward");
                Drop(pose?["sinceHistoricalCapture"] as JObject, path + "." + bearing + ".selfPose.sinceHistoricalCapture",
                    "cameraDisplacementWorld", "bodyDisplacementWorld");
                Drop(direction["relativeToCurrentBody"] as JObject, path + "." + bearing + ".relativeToCurrentBody",
                    "bodyPosition", "bodyForward");
                if(pose!=null && sharedPose!=null && pose["currentPoseRef"]==null &&
                    sharedPoseKeys.All(key=>pose[key]!=null && sharedPose[key]!=null && JToken.DeepEquals(pose[key],sharedPose[key])))
                {
                    foreach(string key in sharedPoseKeys)
                    {
                        fields.Add(new JObject { ["path"]=path+"."+bearing+".selfPose."+key,
                            ["beforeCharacters"]=pose[key].ToString(Formatting.None).Length,["afterCharacters"]=0,
                            ["exactValueFrom"]=sharedPoseField+"."+key });
                        pose.Remove(key);
                    }
                    pose["currentPoseRef"]=sharedPoseField;
                }
            }
        }
        foreach (string path in new[] { "visibleCatalog.current.candidates", "visibleCatalog.memory",
            "visibleCatalog.historicalViews", "historicalCandidates" })
            if (result.SelectToken(path) is JArray rows)
                for (int i = 0; i < rows.Count; i++) if (rows[i] is JObject row) Row(row, path + "[" + i + "]");
        // Past screen pixels are not in the current image. Frozen world points,
        // geometry neighbours, descriptions and the historical capture stay.
        if(result.SelectToken("visibleCatalog.memory") is JArray memories)
            for(int i=0;i<memories.Count;i++)
                Drop(memories[i] as JObject,"visibleCatalog.memory["+i+"]","imagePointAtCapture","pixelBounds","visiblePixelCount");
        // Older executed search traces are not additional candidate observations.
        // Only omit known low-level pose/renderer fields, never constraints,
        // ownership, failures, semantic verdicts or coverage records.
        if (result["task"] is JObject task && task["receipts"] is JArray receipts)
            foreach (var receipt in receipts.OfType<JObject>())
                Drop(receipt, "task.receipts", "viewBefore", "viewAfter", "actualRoot", "trackingFocusWorldPoint");
        result["searchAttention"] = new JObject { ["purpose"] = "choose-next-observed-reference",
            ["allOfferedReferencesRetained"] = true, ["engineMemoryMutated"] = false, ["scope"] = Contract };
        diagnostic = new JObject { ["purpose"] = "choose-next-observed-reference",
            ["beforeCharacters"] = source.ToString(Formatting.None).Length, ["afterCharacters"] = result.ToString(Formatting.None).Length,
            ["fields"] = fields, ["engineMemoryMutated"] = false };
        return result;
    }
}
