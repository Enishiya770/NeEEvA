using System.Linq;
using Newtonsoft.Json.Linq;

public partial class ChatQW
{
    private static void FocusRoomStepContext(JObject input)
    {
        if (input["taskSequence"] is JObject sequence) input["taskSequence"] = RoomTaskContextPolicy.Sequence(sequence);
        var intents = RoomTaskContextPolicy.ReadyUserIntents(input);
        if (intents == null) return;
        var omitted = new JObject();
        foreach (string path in new[] { "visibleObjectCatalog.current.candidates", "visibleObjectCatalog.memory", "sceneSpatialMemory.objects",
            "sceneCognitionSummary.currentObjects", "sceneCognitionSummary.historicalObjects",
            "sceneActionFacts.objectContext.recentObservations", "sceneActionFacts.objectContext.knownObjectHistory" })
        {
            if (!(input.SelectToken(path) is JArray rows)) continue;
            // Unknown/custom restrictions protect the entire row. This phase
            // cannot choose an object: its native menu contains only user moves.
            var keep = rows.Where(r => !(r is JObject obj) || !SceneAttentionOptional(obj)).Select(r => r.DeepClone()).ToArray();
            if (keep.Length == rows.Count) continue;
            omitted[path] = rows.Count - keep.Length;
            rows.Replace(new JArray(keep));
        }
        input["stepAttention"] = new JObject { ["intents"] = new JArray(intents), ["omittedObservedRows"] = omitted,
            ["engineMemoryMutated"] = false,
            ["scope"] = "Current ready user-movement step only. Original request, dependencies, actual user pose/route/support, failures and unknown constraints remain. Object records stay in runtime; next object step gets a full fresh planner. Omitted rows neither prove absence nor authorize action." };
    }
}
