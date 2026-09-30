using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class RecordingSingingDecisionCompiler
{
    public static JObject BoundaryMaterialFacts(RecordingSingingMaterial material)
    { try { return JObject.Parse(material?.Description ?? "{}"); } catch (JsonException) { return new JObject(); } }

    public static bool BoundaryAnchorInMaterial(RecordingSingingMaterial material, string text, bool allowSinging)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var f = BoundaryMaterialFacts(material);
        if (((string)f["RecordingTranscript"] ?? "").Contains(text) ||
            allowSinging && ((string)f["SingingSegmentText"] ?? "").Contains(text)) return true;
        // Only segments frozen with this source are available here. Current user
        // text and another capture's most recent ASR are deliberately excluded.
        return (f["boundaryEvidence"]?["segments"] as JArray ?? new JArray()).OfType<JObject>()
            .Any(s => (string)s["status"] == "ok" && ((string)s["text"] ?? "").Contains(text));
    }

    public static bool BoundarySameSource(JObject obligation, RecordingSingingMaterial material)
    {
        var old = new RecordingSingingMaterial { Description = (string)obligation["sourceFacts"] };
        var current = BoundaryMaterialFacts(material);
        string hash = (string)current["boundaryEvidence"]?["audio_sha256"];
        if (!string.IsNullOrEmpty(hash) && hash == (string)obligation["audio_sha256"]) return true;
        // Continuation clips can contain the exact same recorded PCM with a new
        // hash/key. Only measured source coordinates establish that relation.
        var left = SelectedPcm(old, "raw"); var right = SelectedPcm(material, "raw");
        if (left != null && right != null) return SharedPcmSeconds(left, right) > .001;
        return material.ClipRef == (string)obligation["clip"] && material.Revision == (string)obligation["revision"];
    }

    public static JArray ApplicableBoundaryObligations(RecordingSingingDecisionContext context,
        IEnumerable<RecordingSingingMaterial> materials) => new JArray((context.BoundaryObligations ?? new JArray())
            .OfType<JObject>().Where(o => materials.Any(m => BoundarySameSource(o, m))).Select(o => o.DeepClone()));

    public static JArray BoundaryObligationFacts(JArray obligations) => new JArray((obligations ?? new JArray()).OfType<JObject>().Select(o => {
        var copy = new JObject();
        foreach (string key in new[] { "id", "clip", "revision", "audio_sha256", "range", "edit", "input", "state", "reason" })
            if (o[key] != null) copy[key] = o[key].DeepClone();
        if (o["verified"] is JObject verified)
        {
            var measurement = new JObject();
            foreach (string key in new[] { "status", "source_id", "source_revision", "audio_sha256", "range", "quality", "boundary_uncertainty", "mode_verified" })
                if (verified[key] != null) measurement[key] = verified[key].DeepClone();
            copy["verified"] = measurement;
        }
        return copy;
    }));

    private static bool BoundaryWindowSatisfies(JObject obligation, RecordingSingingMaterial material, JObject verified)
    {
        if (verified == null || (string)verified["status"] != "resolved" ||
            (string)verified["source_id"] != material.ClipRef || (string)verified["source_revision"] != material.Revision ||
            (string)verified["audio_sha256"] != (string)BoundaryMaterialFacts(material)["boundaryEvidence"]?["audio_sha256"] ||
            !JToken.DeepEquals(verified["edit"], obligation["edit"])) return false;
        return verified["range"] is JObject range && PcmNumber(range["start_seconds"], out double start) &&
            PcmNumber(range["end_seconds"], out double end) && end - start >= 1;
    }

    private static bool PreviouslyVerifiedBoundaryAllows(JObject obligation, RecordingSingingMaterial material, string range, JObject currentVerified)
    {
        if (!(obligation["verified"] is JObject verified) || !(verified["range"] is JObject window) ||
            !(obligation["verifiedSourceFacts"] is JObject facts)) return false;
        var f = (JObject)facts.DeepClone();
        // Only the measured edge discharges this obligation. The opposite end
        // was the analysis baseline, not a permanent semantic inclusion limit.
        string edge = (string)obligation["edit"]?["edge"];
        bool allEdges = (string)obligation["edit"]?["kind"] == "segments";
        f["CleanStartSeconds"] = edge == "lead" || allEdges ? window["start_seconds"]?.DeepClone() : new JValue(0);
        f["CleanEndSeconds"] = edge == "tail" || allEdges ? window["end_seconds"]?.DeepClone() : facts["RawSeconds"]?.DeepClone();
        var allowed = SelectedPcm(new RecordingSingingMaterial { Description = f.ToString(Formatting.None) }, "clean");
        var candidate = material;
        if ((string)currentVerified?["status"] == "resolved" && (string)currentVerified["source_id"] == material.ClipRef &&
            (string)currentVerified["source_revision"] == material.Revision && currentVerified["range"] is JObject candidateWindow)
        {
            var c = BoundaryMaterialFacts(material); c["CleanStartSeconds"] = candidateWindow["start_seconds"]?.DeepClone();
            c["CleanEndSeconds"] = candidateWindow["end_seconds"]?.DeepClone();
            candidate = new RecordingSingingMaterial { Description = c.ToString(Formatting.None) }; range = "clean";
        }
        var selected = SelectedPcm(candidate, range);
        return allowed != null && selected != null && selected.Count > 0 &&
            Math.Abs(SharedPcmSeconds(allowed, selected) - selected.Sum(s => s.End - s.Start)) <= .001;
    }

    public static bool BoundaryResolutionShape(JObject resolution, RecordingSingingDecisionContext context, JArray applicable)
    {
        if (resolution == null || resolution.Count != 3 || !(resolution["ids"] is JArray ids) || ids.Count == 0 ||
            ids.Any(id => id.Type != JTokenType.String) || ids.Distinct(JToken.EqualityComparer).Count() != ids.Count ||
            resolution["quote"]?.Type != JTokenType.String || resolution["reason"]?.Type != JTokenType.String) return false;
        string quote = (string)resolution["quote"], reason = (string)resolution["reason"];
        if (quote.Length < 2 || quote.Length > 1024 || reason.Length < 1 || reason.Length > 400 ||
            !(context.UserText ?? "").Contains(quote)) return false;
        var rows = applicable.OfType<JObject>().ToArray();
        return rows.Length > 0 && new HashSet<string>(ids.Values<string>()).SetEquals(rows.Select(o => (string)o["id"])) &&
            rows.All(o => !JToken.DeepEquals(o["input"], context.CurrentInput));
    }

    public static bool ValidateBoundaryObligations(JObject proposal, RecordingSingingDecisionContext context,
        IEnumerable<RecordingSingingMaterial> materials, out string error)
    {
        error = null;
        if ((string)proposal["decision"] != "perform") return true;
        var selected = materials.ToArray();
        var applicable = ApplicableBoundaryObligations(context, selected);
        if (applicable.Count == 0) return true;
        if (applicable.OfType<JObject>().All(o => selected.Where(m => BoundarySameSource(o, m))
            .All(m => BoundaryWindowSatisfies(o, m, context.VerifiedBoundary) ||
                PreviouslyVerifiedBoundaryAllows(o, m, (string)proposal["range"], context.VerifiedBoundary)))) return true;
        // This is a proposal to the independent reviewer, never a state change.
        if (BoundaryResolutionShape(proposal["boundary_resolution"] as JObject, context, applicable)) return true;
        error = "boundary-obligation-unresolved"; return false;
    }

    public static void RetainVerifiedOppositeEdge(RecordingBoundarySource source, JObject edit, JArray obligations)
    {
        // Sequential two-edge analysis: a prior measured opposite edge may narrow
        // this analysis envelope. Its baseline facts stay unchanged for binding.
        // No uncertain ASR text is converted to seconds here.
        foreach (var obligation in (obligations ?? new JArray()).OfType<JObject>())
        {
            var verified = obligation["verified"] as JObject;
            if ((string)verified?["status"] != "resolved" || (string)verified["audio_sha256"] != source.RawHash ||
                !(verified["range"] is JObject range) || !PcmNumber(range["start_seconds"], out double start) ||
                !PcmNumber(range["end_seconds"], out double end)) continue;
            if ((string)edit["edge"] == "tail" && (string)obligation["edit"]?["edge"] == "lead")
                source.Start = Math.Max(source.Start, (float)start);
            if ((string)edit["edge"] == "lead" && (string)obligation["edit"]?["edge"] == "tail")
                source.End = Math.Min(source.End, (float)end);
        }
    }

    public static void AddBoundaryResolutionSchema(JObject properties, RecordingSingingDecisionContext context)
    {
        if (context.BoundaryObligations == null || context.BoundaryObligations.Count == 0) return;
        properties["boundary_resolution"] = new JObject { ["anyOf"] = new JArray(
            new JObject { ["type"] = "null" }, new JObject { ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JObject {
                    ["ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 16,
                        ["items"] = new JObject { ["type"] = "string", ["enum"] = new JArray(context.BoundaryObligations.OfType<JObject>().Select(o => (string)o["id"])) } },
                    ["quote"] = new JObject { ["type"] = "string", ["minLength"] = 2, ["maxLength"] = 1024 },
                    ["reason"] = new JObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 400 } },
                ["required"] = new JArray("ids", "quote", "reason") }) };
    }

    // Only a fully compiled proposal, or the one narrowly repairable overlap
    // failure, may contribute an exclusion. Each selected source is recompiled
    // independently with its original exact proofs and current request binding.
    public static JArray RetainableBoundaryProposals(JObject proposal, RecordingSingingDecisionContext context, string compileError)
    {
        var result = new JArray();
        string boundaryKind = (string)proposal?["boundary_edit"]?["kind"];
        bool retainedSelection = boundaryKind == "trim" || boundaryKind == "segments" &&
            (proposal?["boundary_edit"]?["exclude_segment_ids"] as JArray)?.Count > 0;
        if (compileError != null && compileError != "recording-overlapping-continuation-selection" ||
            (string)proposal?["decision"] != "perform" || !retainedSelection ||
            !(proposal["materials"] is JArray keys)) return result;
        foreach (string key in keys.Values<string>().Distinct())
        {
            var material = context.Materials.SingleOrDefault(m => m.Key == key);
            if (material == null) continue;
            var single = (JObject)proposal.DeepClone(); single["materials"] = new JArray(key);
            if (single["selection_evidence"] is JArray proofs)
                single["selection_evidence"] = new JArray(proofs.OfType<JObject>().Where(p => (string)p["key"] == key).Select(p => p.DeepClone()));
            if (!TryCompile(single.ToString(Formatting.None), context, out _, out _)) continue;
            var facts = BoundaryMaterialFacts(material);
            string hash = (string)facts["boundaryEvidence"]?["audio_sha256"];
            // A legacy fixture/caller with neither retained hash nor measured PCM
            // cannot create durable constraints merely by inventing a clip name.
            if (string.IsNullOrEmpty(hash) && SelectedPcm(material, "raw") == null) continue;
            result.Add(new JObject { ["clip"] = material.ClipRef, ["revision"] = material.Revision,
                ["audio_sha256"] = hash ?? "", ["sourceFacts"] = material.Description,
                ["range"] = proposal["range"], ["edit"] = proposal["boundary_edit"].DeepClone(),
                ["input"] = context.CurrentInput?.DeepClone(), ["userText"] = context.UserText ?? "",
                ["state"] = "requires_boundary_verification", ["reason"] = compileError ?? "proposed_trim" });
        }
        return result;
    }
}
