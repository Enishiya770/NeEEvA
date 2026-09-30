using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Replays the real c19 raw PCM/transcript/pitch as a still-unprepared candidate.
// Boundary measurements below are controlled inputs, not claims of audible truth.
public static partial class AgentSingingGoalRegression
{
    private const string C19Lead = "我开始喽，我要唱第二段了。";
    private const string C19Keep = "忘れたも通りに帰るよ うに。";
    private const string C19Lyrics = C19Keep + " 古びだ 思い出 の 心よ原。";

    private static RecordingSingingDecisionContext PendingC19Context(Fixture f, string root, out string clip)
    {
        string directory = Path.Combine(root, "work/recording-repair-20260928-2026/live-c19");
        var input = JObject.Parse(File.ReadAllText(Path.Combine(directory, "source.json")));
        byte[] wav = File.ReadAllBytes(Path.Combine(directory, "original.wav"));
        Check(RecordingBoundarySource.Hash(wav) == (string)input["sha256"], "Live c19 PCM fingerprint must match the saved send/evidence/accepted trace chain.");
        string user = (string)input["transcript"];
        f.Begin(user); f.StageCompletedEvidence(user, C19Lyrics, .569f, 19.35f, 14.97f, 17.37f);
        var type = typeof(SenseVoiceSpeechToText);
        type.GetField("m_LiveRecordingCandidateSessionSerial", Flags).SetValue(f.sense, 19);
        object evidence = type.GetField("m_LastSingingEvidence", Flags).GetValue(f.sense);
        void Field(string field, object value) => evidence.GetType().GetField(field).SetValue(evidence, value);
        Field("CaptureSessionSerial", 19); Field("RecordingId", (string)input["evidence"]["recordingId"]);
        Field("CleanStartSeconds", 2.93f); Field("RecoveryStartSeconds", 0f); Field("TimelineOriginSeconds", 0f);
        Field("PitchTimelineMidi", input["pitch"]["midi"].Values<float>().ToArray());
        Field("FrameSeconds", (float)input["pitch"]["frameSeconds"]); Field("AcousticMode", "uncertain");
        Check((bool)typeof(WaveformTrace).GetMethod("RestoreRetainedMapping", Flags).Invoke(null,
            new object[] { wav, input["inputAudio"] }), "Restore the exact hash-verified upload mapping, not a newly guessed capture lineage.");
        evidence.GetType().GetProperty("RawWavBytes").SetValue(evidence, wav);
        Check(f.sense.RetainCurrentSingingEvidenceClip(true), "The real mixed recording must remain available without being prepared.");
        object record = ((IList)type.GetField("m_QuarantinedSingingCandidates", Flags).GetValue(f.sense)).Cast<object>().Single();
        object phrase = record.GetType().GetField("Phrase").GetValue(record);
        clip = (string)input["identity"]["clip"];
        phrase.GetType().GetField("ClipRef").SetValue(phrase, clip);
        phrase.GetType().GetField("FullClipRef").SetValue(phrase, clip);
        Call(f.chat, "BindAcceptedRecordingInput", true);
        var context = (RecordingSingingDecisionContext)Call(f.chat, "BuildRecordingSingingDecisionContext");
        context.CurrentIntent = new JObject { ["roleProposal"] = new JObject { ["task"] = "recording_singing",
            ["goal"] = "回唱这份第二段录音，只保留歌词，排除开头说明", ["originalUserText"] = user, ["text"] = "" } };
        context.Recovery = null;
        return context;
    }

    private static JObject PendingC19Decision(RecordingSingingDecisionContext c, string clip)
    {
        string quote = (string)RecordingSingingDecisionCompiler.QuoteReferences(c).OfType<JObject>()
            .First(q => (string)q["origin"] == "current" && (string)q["text"] == c.UserText)["ref"];
        return new JObject { ["decision"] = "perform", ["materials"] = new JArray(clip), ["range"] = "clean", ["intent"] = "repeat",
            ["expected"] = "只回唱第二段两句日语歌词，排除开头说明与末尾请求", ["source_ref"] = quote, ["request_ref"] = "source",
            ["thought"] = "", ["speech"] = new JObject { ["text"] = "", ["language"] = "zh" },
            ["resolution"] = "matched", ["evidence_ref"] = "none", ["selection_evidence"] = new JArray(new JObject { ["key"] = clip, ["quote"] = C19Keep }),
            ["music"] = new JObject { ["mode"] = "preserve", ["reference"] = "none", ["interval"] = "none" },
            ["boundary_edit"] = new JObject { ["kind"] = "trim", ["target"] = "material", ["edge"] = "lead", ["exclude_text"] = C19Lead, ["keep_text"] = C19Keep } };
    }

    private static JObject C19Measured(RecordingBoundarySource source, JObject decision, float start = 2.7f) => new JObject {
        ["status"] = "resolved", ["request_id"] = "c19-fixture", ["source_id"] = source.Clip,
        ["source_revision"] = source.Revision, ["audio_sha256"] = source.RawHash, ["source_offset_seconds"] = 0,
        ["current_start_seconds"] = source.Start, ["current_end_seconds"] = source.End,
        ["pcm_lineage_hash"] = source.LineageHash, ["played_revision"] = "", ["edge"] = decision["boundary_edit"]["edge"],
        ["quality"] = "local-asr-acoustic-gap", ["word_timestamps"] = false, ["mode_verified"] = false,
        ["range"] = new JObject { ["start_seconds"] = start, ["end_seconds"] = source.End },
        ["boundary_uncertainty"] = new JObject { ["start_seconds"] = 2.6f, ["end_seconds"] = 2.8f },
        ["scope"] = "Controlled boundary response over real PCM; this 2.7s value is not inferred acoustic truth." };

    private static bool BindC19Measured(Fixture f, RecordingBoundarySource source, JObject decision, JObject result, Func<bool> owner)
    {
        object[] args = { source, decision["boundary_edit"], "c19-fixture", "", result, owner, null };
        return (bool)typeof(SenseVoiceSpeechToText).GetMethod("TryBindRecordingBoundaryResult", Flags).Invoke(f.sense, args);
    }

    public static int RunPendingRecordingBoundaryChecks(string root, string outputDirectory = null)
    {
        int before = checks;
        using (var f = new Fixture())
        {
            var context = PendingC19Context(f, root, out string clip);
            var material = context.Materials.Single(m => m.ClipRef == clip);
            Check(material.Revision == "pending" && material.PlaybackStatus == "evidence_only", "Live pending protocol identity is mandatory.");
            var d = PendingC19Decision(context, clip);
            var schema = RecordingSingingDecisionCompiler.ResponseFormat(context);
            Check(RecordingBoundarySchemaChecks.Validate(d, (JObject)schema["json_schema"]["schema"], out string schemaError),
                "Pending clean trim must satisfy the actual strict response schema: " + schemaError);
            var invalidShape = (JObject)d.DeepClone(); invalidShape["materials"] = clip;
            Check(!RecordingBoundarySchemaChecks.Validate(invalidShape, (JObject)schema["json_schema"]["schema"], out _),
                "Recording schema rejects scalar materials instead of silently skipping array validation.");
            invalidShape = (JObject)d.DeepClone(); invalidShape["materials"] = new JArray("clip:not-in-snapshot");
            Check(!RecordingBoundarySchemaChecks.Validate(invalidShape, (JObject)schema["json_schema"]["schema"], out _),
                "Array item validation enforces real material identity enums.");
            Check(RecordingSingingDecisionCompiler.TryCompile(d.ToString(), context, out string pending, out string error) &&
                pending == RecordingSingingDecisionCompiler.BoundaryRefinement, "Schema-valid live pending trim must reach analysis: " + error);
            var multiProof = (JObject)d.DeepClone();
            ((JArray)multiProof["selection_evidence"]).Add(new JObject { ["key"] = clip, ["quote"] = C19Lead });
            Check(RecordingSingingDecisionCompiler.TryCompile(multiProof.ToString(), context, out pending, out error) && pending == RecordingSingingDecisionCompiler.BoundaryRefinement && ((JArray)multiProof["materials"]).Count == 1,
                "Two exact lead/lyric proofs for one selected material must not force a retry or imply two performances: " + error);
            var foreignProof = (JObject)multiProof.DeepClone(); foreignProof["selection_evidence"][1]["key"] = f.first;
            foreignProof["selection_evidence"][1]["quote"] = "public phrase alpha";
            Check(!RecordingSingingDecisionCompiler.TryCompile(foreignProof.ToString(), context, out _, out error), "An exact quote from an unselected recording cannot become selection evidence.");
            var inventedProof = (JObject)multiProof.DeepClone(); inventedProof["selection_evidence"][1]["quote"] = "未说过的口语";
            Check(!RecordingSingingDecisionCompiler.TryCompile(inventedProof.ToString(), context, out _, out error), "Extra proofs must still be exact source quotes.");
            inventedProof["selection_evidence"][1]["quote"] = "";
            Check(!RecordingSingingDecisionCompiler.TryCompile(inventedProof.ToString(), context, out _, out error), "Empty proof cannot replace a retained transcript quote.");
            var bad = (JObject)d.DeepClone(); bad["range"] = "current";
            Check(!RecordingSingingDecisionCompiler.TryCompile(bad.ToString(), context, out _, out error) && error == "boundary-source-range-unavailable", "Pending current must stay unavailable with an accurate cause.");
            bad = (JObject)d.DeepClone(); bad["boundary_edit"]["target"] = "last_playback";
            Check(!RecordingSingingDecisionCompiler.TryCompile(bad.ToString(), context, out _, out error), "Pending material cannot borrow the last playback binding.");
            var source = f.sense.SnapshotBoundarySource(clip, "clean", "lead", out error);
            Check(source != null && source.UsesRetainedEvidence && source.Revision == "pending" && f.sense.BoundarySourceStillCurrent(source), "New unprepared source must have a frozen raw/pitch lease: " + error);
            Check(Math.Abs(source.Start) < .0001f && Math.Abs(source.End - 14.97f) < .0001f && Math.Abs(source.BaselineStart - 2.93f) < .0001f,
                "Analysis includes the spoken lead outside clean; opposite boundary remains fixed.");
            Check(source.CurrentWav == null && f.sense.PracticePhraseCount == 2 && f.sense.DescribeQuarantinedSingingCandidates().Single().SourceStatus == "pending", "Read-only analysis cannot prepare or confirm this source.");
            Check(source.MatchesProposal(material, (JObject)d["boundary_edit"]), "Actual latest pending facts must match the planner snapshot.");
            JObject callback = null;
            var resolve = context.RefineBoundary(d, () => true, value => callback = value);
            Check(resolve.MoveNext() && resolve.Current is IEnumerator && callback == null, "Actual ChatSample source resolver must reach SenseVoice refinement, without running HTTP in the test.");
            (resolve as IDisposable)?.Dispose();
            var manifest = (JObject)typeof(SenseVoiceSpeechToText).GetMethod("BoundaryManifest", Flags).Invoke(null,
                new object[] { source, d["boundary_edit"], "c19-live-verification", "" });
            string live = outputDirectory ?? Path.Combine(root, "work/recording-repair-20260928-2026/live-c19");
            Directory.CreateDirectory(live);
            File.WriteAllText(Path.Combine(live, "production-manifest.json"), manifest.ToString());
            File.WriteAllText(Path.Combine(live, "source-snapshot.json"), source.Facts(true).ToString());
            var measured = C19Measured(source, d);
            var wrong = (JObject)measured.DeepClone(); wrong["current_start_seconds"] = 2.93f;
            Check(!BindC19Measured(f, source, d, wrong, () => true), "Response for coarse-clean analysis cannot bind to raw-edge analysis.");
            wrong = (JObject)measured.DeepClone(); wrong["range"]["end_seconds"] = 15f;
            Check(!BindC19Measured(f, source, d, wrong, () => true), "A lead edit cannot change the opposite boundary.");
            Check(BindC19Measured(f, source, d, measured, () => true), "Bound response inside the raw edge must be accepted before independent review.");
            Check(measured["current_playback"].Type == JTokenType.Null && (bool)measured["source_snapshot"]["usesRetainedEvidence"] && !(bool)measured["mode_verified"],
                "Measured pending source must not be advertised as completed playback or a singing-mode verdict.");
            context.VerifiedBoundary = measured;
            Check(RecordingSingingDecisionCompiler.TryCompile(d.ToString(), context, out string compiled, out error) && compiled.Contains("range=\"window\""), "Verified pending window must compile with the original pending revision: " + error);
            object[] goal = { compiled, true }; Call(f.chat, "ExtractSingingGoalTags", goal);
            object[] input = { compiled }; object sing = Call(f.chat, "ExtractHumBackTag", input); object[] args = { sing, "" };
            Check(!(bool)Call(f.chat, "TryResolveUnifiedSing", args) && f.sense.PracticePhraseCount == 2, "Boundary result must not admit or play before independent review.");
            f.Approve();
            Check((bool)Call(f.chat, "TryResolveUnifiedSing", args), "Reviewed live pending window must prepare and admit exactly this source: " + args[1]);
            var prepared = f.sense.SnapshotBoundarySource(clip);
            Check(prepared != null && prepared.RawHash == source.RawHash && Math.Abs(prepared.Start - 2.7f) < .0001f && Math.Abs(prepared.End - 14.97f) < .0001f,
                "Prepared window must use raw coordinates including a verified cut before coarse clean.");
            Check(f.Phrase(clip).PendingConfirmation && f.sense.DescribeQuarantinedSingingCandidates().Count == 0 && f.sense.PracticePhraseCount == 3,
                "Preparation preserves provenance uncertainty and does not substitute an old song.");
            Check(!f.sense.TrySelectSingingClipWindow(clip, false, 2.7f, 14.97f, false, out _, out string failure) && failure.Contains("already consumed"),
                "A consumed boundary lease cannot retry through the generic window fallback.");
        }
        foreach (string mutation in new[] { "text", "range", "raw", "pitch", "revision", "owner" })
        using (var f = new Fixture())
        {
            var c = PendingC19Context(f, root, out string clip); var d = PendingC19Decision(c, clip);
            var source = f.sense.SnapshotBoundarySource(clip, "clean", "lead", out _); var result = C19Measured(source, d);
            Check(BindC19Measured(f, source, d, result, () => mutation != "owner"), "Seed pending lease for " + mutation);
            object record = ((IList)typeof(SenseVoiceSpeechToText).GetField("m_QuarantinedSingingCandidates", Flags).GetValue(f.sense)).Cast<object>().Single();
            object p = record.GetType().GetField("Phrase").GetValue(record);
            object e = p.GetType().GetField("LatestRecordingEvidence").GetValue(p) ?? p.GetType().GetField("RecordingEvidence").GetValue(p);
            if (mutation == "text") e.GetType().GetField("Text").SetValue(e, c.UserText + "后补更正");
            if (mutation == "range") e.GetType().GetField("CleanStartSeconds").SetValue(e, 3.1f);
            if (mutation == "raw") { var bytes = (byte[])source.RawWav.Clone(); bytes[bytes.Length - 1] ^= 1; e.GetType().GetProperty("RawWavBytes").SetValue(e, bytes); }
            if (mutation == "pitch") { var pitch = (float[])source.Pitch.Clone(); pitch[30] += 1; e.GetType().GetField("PitchTimelineMidi").SetValue(e, pitch); }
            if (mutation == "revision") p.GetType().GetField("Revision").SetValue(p, source.StorageRevision + 1);
            Check(!f.sense.TrySelectSingingClipWindow(clip, false, 2.7f, 14.97f, false, out _, out _) && f.sense.PracticePhraseCount == 2,
                "Late pending " + mutation + " must invalidate the measured window without preparation.");
        }
        Check(!RecordingSingingDecisionCompiler.BoundaryUnresolvedReply("boundary-edit-invalid").Contains("请指出") &&
            !RecordingSingingDecisionCompiler.BoundaryUnresolvedReply("boundary-source-range-unavailable").Contains("原文"), "Protocol/state failures must not blame missing user quotes.");
        Check(!RecordingSingingDecisionCompiler.BoundaryUnresolvedReply("source_version_changed").Contains("刚才播放") &&
            RecordingSingingDecisionCompiler.BoundaryUnresolvedReply("source_version_changed").Contains("本次选中"),
            "A changed pending source cannot be described as already played.");
        return checks - before;
    }
    // Root may call after ONE real endpoint request; this method never performs
    // HTTP, model inference or audible playback, and unresolved stays unresolved.
    public static JObject ValidatePendingRecordingBoundaryLiveReply(string root, string responsePath)
    {
        using (var f = new Fixture())
        {
            var c = PendingC19Context(f, root, out string clip); var d = PendingC19Decision(c, clip);
            var source = f.sense.SnapshotBoundarySource(clip, "clean", "lead", out string error);
            var result = JObject.Parse(File.ReadAllText(responsePath));
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(root, "work/recording-repair-20260928-2026/live-c19/production-manifest.json")));
            if ((string)result["status"] != "resolved")
            {
                Check((string)result["source_id"] == source.Clip && (string)result["audio_sha256"] == source.RawHash,
                    "Unresolved live result must still identify the requested original PCM.");
                Check(f.sense.PracticePhraseCount == 2 && f.sense.DescribeQuarantinedSingingCandidates().Single().PlaybackStatus == "evidence_only",
                    "Unresolved live analysis must leave the candidate and previous practice intact.");
                return new JObject { ["passed"] = true, ["analysisResolved"] = false, ["reason"] = result["reason"],
                    ["publicReply"] = RecordingSingingDecisionCompiler.BoundaryUnresolvedReply((string)result["reason"]), ["prepared"] = false };
            }
            object[] bind = { source, d["boundary_edit"], (string)manifest["request_id"], "", result, new Func<bool>(() => true), null };
            Check((bool)typeof(SenseVoiceSpeechToText).GetMethod("TryBindRecordingBoundaryResult", Flags).Invoke(f.sense, bind), "Live HTTP result must pass production binding: " + bind[6]);
            c.VerifiedBoundary = result;
            Check(RecordingSingingDecisionCompiler.TryCompile(d.ToString(), c, out string compiled, out error), "Live result must compile a measured window: " + error);
            object[] goal = { compiled, true }; Call(f.chat, "ExtractSingingGoalTags", goal);
            object[] input = { compiled }; object sing = Call(f.chat, "ExtractHumBackTag", input); object[] args = { sing, "" };
            Check(!(bool)Call(f.chat, "TryResolveUnifiedSing", args), "Live measurement alone cannot bypass goal review.");
            f.Approve(); Check((bool)Call(f.chat, "TryResolveUnifiedSing", args), "Controlled approved live window must prepare: " + args[1]);
            return new JObject { ["passed"] = true, ["analysisResolved"] = true, ["range"] = result["range"].DeepClone(),
                ["prepared"] = true, ["mode_verified"] = result["mode_verified"],
                ["boundary_uncertainty"] = result["boundary_uncertainty"]?.DeepClone(),
                ["scope"] = "Actual PCM/ASR endpoint result, production binder/compiler/preparation; independent goal approval controlled, no audio played." };
        }
    }

}

// Test-only JSON Schema subset used by the production recording response schema.
// WorkReview's runtime validator intentionally does not support array fields.
public static class RecordingBoundarySchemaChecks
{
    public static bool Validate(JToken value, JObject schema, out string error) => ValidateNode(value, schema, "$", out error);
    private static bool ValidateNode(JToken value, JObject schema, string path, out string error)
    {
        error = "";
        string actual = value == null ? "missing" : value.Type == JTokenType.Object ? "object" : value.Type == JTokenType.Array ? "array" :
            value.Type == JTokenType.String ? "string" : value.Type == JTokenType.Boolean ? "boolean" : value.Type == JTokenType.Null ? "null" :
            value.Type == JTokenType.Integer ? "integer" : value.Type == JTokenType.Float ? "number" : "unsupported";
        bool IsType(string type) => type == actual || type == "number" && actual == "integer";
        if (schema["type"] != null && !(schema["type"] is JArray types ? types.Values<string>().Any(IsType) : IsType((string)schema["type"])))
        { error = path + ":type(" + actual + ")"; return false; }
        if (schema["enum"] is JArray choices && !choices.Any(c => JToken.DeepEquals(c, value))) { error = path + ":enum"; return false; }
        if (schema["anyOf"] is JArray branches)
        {
            string first = "";
            foreach (var branch in branches.OfType<JObject>())
            { if (ValidateNode(value, branch, path, out string why)) return true; if (first == "") first = why; }
            error = path + ":anyOf (first branch: " + first + ")"; return false;
        }
        if (value is JObject obj)
        {
            var properties = schema["properties"] as JObject ?? new JObject();
            if ((bool?)schema["additionalProperties"] == false && obj.Properties().Any(p => properties.Property(p.Name) == null))
            { error = path + ":unknown-field"; return false; }
            if (schema["required"] is JArray required)
                foreach (string key in required.Values<string>()) if (obj.Property(key) == null) { error = path + "." + key + ":required"; return false; }
            foreach (var property in properties.Properties()) if (obj.Property(property.Name) != null &&
                !ValidateNode(obj[property.Name], (JObject)property.Value, path + "." + property.Name, out error)) return false;
        }
        else if (value is JArray array)
        {
            if (schema["minItems"] != null && array.Count < (int)schema["minItems"] || schema["maxItems"] != null && array.Count > (int)schema["maxItems"])
            { error = path + ":items-count"; return false; }
            if ((bool?)schema["uniqueItems"] == true && array.Where((a, i) => array.Take(i).Any(b => JToken.DeepEquals(a, b))).Any())
            { error = path + ":duplicate-item"; return false; }
            if (schema["items"] is JObject itemSchema)
                for (int i = 0; i < array.Count; i++) if (!ValidateNode(array[i], itemSchema, path + "[" + i + "]", out error)) return false;
        }
        else if (actual == "string")
        {
            string text = (string)value;
            if (schema["minLength"] != null && text.Length < (int)schema["minLength"] || schema["maxLength"] != null && text.Length > (int)schema["maxLength"])
            { error = path + ":length"; return false; }
            if (schema["pattern"] != null && !System.Text.RegularExpressions.Regex.IsMatch(text, (string)schema["pattern"]))
            { error = path + ":pattern"; return false; }
        }
        else if (actual == "number" || actual == "integer")
        {
            double number = (double)value;
            if (double.IsNaN(number) || double.IsInfinity(number) || schema["minimum"] != null && number < (double)schema["minimum"] ||
                schema["maximum"] != null && number > (double)schema["maximum"])
            { error = path + ":number-range"; return false; }
        }
        return true;
    }
}
