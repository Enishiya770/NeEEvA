using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class AgentSingingGoalRegression
{
    private static RecordingSingingDecisionContext LiveC13BoundaryContext(Fixture f, string root, out string clip)
    {
        string dir = Path.Combine(root, "work/loop-convergence-20260928-2212/live-c13");
        var data = JObject.Parse(File.ReadAllText(Path.Combine(dir, "source.json")));
        byte[] wav = File.ReadAllBytes(Path.Combine(dir, "original.wav"));
        Check(RecordingBoundarySource.Hash(wav) == (string)data["sha256"], "c13 exact retained raw fingerprint");
        string text = (string)data["transcript"];
        f.Begin(text); f.StageCompletedEvidence(text, text.Substring(0, text.IndexOf(" 请你", StringComparison.Ordinal)), .599f, 16.84f, 13.37f, 16.37f);
        var type = typeof(SenseVoiceSpeechToText);
        type.GetField("m_LiveRecordingCandidateSessionSerial", Flags).SetValue(f.sense, 13);
        var evidence = type.GetField("m_LastSingingEvidence", Flags).GetValue(f.sense);
        void Set(string name, object value) => evidence.GetType().GetField(name).SetValue(evidence, value);
        Set("CaptureSessionSerial", 13); Set("RecordingId", (string)data["evidence"]["recordingId"]);
        Set("CleanStartSeconds", 0f); Set("RecoveryStartSeconds", 0f); Set("TimelineOriginSeconds", 0f);
        Set("PitchTimelineMidi", data["pitch"]["midi"].Values<float>().ToArray()); Set("FrameSeconds", .1f);
        Check((bool)typeof(WaveformTrace).GetMethod("RestoreRetainedMapping", Flags).Invoke(null,
            new object[] { wav, data["inputAudio"] }), "c13 full PCM lineage restored from actual upload");
        evidence.GetType().GetProperty("RawWavBytes").SetValue(evidence, wav);
        var segments = new JArray(((JArray)data["segments"]).OfType<JObject>().Select(s => s["value"] as JObject)
            .GroupBy(s => (int)s["id"]).Select(g => g.Last().DeepClone()));
        Set("BoundaryTranscriptEvidence", new JObject { ["audio_sha256"] = data["sha256"], ["capture"] = 13,
            ["recordingId"] = data["evidence"]["recordingId"], ["segments"] = segments });
        Check(f.sense.RetainCurrentSingingEvidenceClip(true), "c13 retained without confirmation or playback");
        object record = ((IList)type.GetField("m_QuarantinedSingingCandidates", Flags).GetValue(f.sense)).Cast<object>().Single();
        object phrase = record.GetType().GetField("Phrase").GetValue(record);
        clip = (string)data["identity"]["clip"];
        phrase.GetType().GetField("ClipRef").SetValue(phrase, clip); phrase.GetType().GetField("FullClipRef").SetValue(phrase, clip);
        Call(f.chat, "BindAcceptedRecordingInput", true);
        var c = (RecordingSingingDecisionContext)Call(f.chat, "BuildRecordingSingingDecisionContext");
        c.Recovery = null;
        Check(f.sense.DescribeRecordingBoundaryEvidence(clip)?["segments"]?.Count() == 4,
            "retention cloning preserves all four exact same-source transcript windows");
        return c;
    }

    private static JObject C13Decision(RecordingSingingDecisionContext c, string clip, string edge = "tail")
    {
        var d = PendingC19Decision(c, clip);
        d["expected"] = "回唱第二段歌词，排除讲话";
        d["selection_evidence"][0]["quote"] = "あの日動き出した車だ 止め られ ない 喪失。";
        d["boundary_edit"] = new JObject { ["kind"] = "trim", ["target"] = "material", ["edge"] = edge,
            ["exclude_text"] = edge == "tail" ? "请你把这一段你唱给我听。" : "很好，接下来我唱第二段",
            ["keep_text"] = edge == "tail" ? "No, you can." : "あの日動き出した車だ" };
        return d;
    }

    public static int RunRecordingBoundaryObligationChecks(string root, string outputDirectory = null)
    {
        int before = checks;
        using (var f = new Fixture())
        {
            var c = LiveC13BoundaryContext(f, root, out string clip);
            Check(RecordingSingingDecisionCompiler.Prompt(c).Contains("boundaryEvidence") &&
                RecordingSingingDecisionCompiler.Prompt(c).Contains("No, you can."),
                "actual wire projection retains source-bound segment evidence, not validator-only hidden facts");
            var d = C13Decision(c, clip);
            Check(RecordingSingingDecisionCompiler.TryCompile(d.ToString(), c, out string compiled, out string error) &&
                compiled == RecordingSingingDecisionCompiler.BoundaryRefinement, "actual c13 tail reaches boundary service: " + error);
            c.ObserveBoundaryProposal(c, d, null);
            Check(c.BoundaryObligations.Count == 1, "valid exact source trim creates durable source obligation");
            c.ObserveBoundaryResult(c, d, new JObject { ["status"] = "boundary_unresolved", ["reason"] = "anchor_not_supported" });
            Check((string)c.BoundaryObligations[0]["state"] == "boundary_unresolved", "service unresolved is retained independently of goal review");
            var none = (JObject)d.DeepClone(); none["boundary_edit"] = new JObject { ["kind"] = "none", ["target"] = "material",
                ["edge"] = "lead", ["exclude_text"] = "", ["keep_text"] = "" };
            Check(!RecordingSingingDecisionCompiler.TryCompile(none.ToString(), c, out _, out error) && error == "boundary-obligation-unresolved",
                "c18 clean cannot silently bypass c13 unresolved boundary");
            var next = (RecordingSingingDecisionContext)Call(f.chat, "BuildRecordingSingingDecisionContext");
            Check(next.BoundaryObligations.Count == 1 && !RecordingSingingDecisionCompiler.ValidateBoundaryObligations(none, next,
                next.Materials.Where(m => m.ClipRef == clip), out error), "fresh per-turn context retains unresolved source constraint");
            var lead = C13Decision(c, clip, "lead");
            var material = c.Materials.Single(m => m.ClipRef == clip);
            var overlap = new RecordingSingingMaterial { Key = "clip:controlled-continuation", ClipRef = "clip:controlled-continuation",
                Revision = material.Revision, PlaybackStatus = material.PlaybackStatus, Description = material.Description,
                HasCleanRange = true, HasExpandedRange = true };
            c.Materials.Add(overlap);
            ((JArray)lead["materials"]).Add(overlap.Key);
            ((JArray)lead["selection_evidence"]).Add(new JObject { ["key"] = overlap.Key, ["quote"] = lead["selection_evidence"][0]["quote"] });
            Check(!RecordingSingingDecisionCompiler.TryCompile(lead.ToString(), c, out _, out error) &&
                error == "recording-overlapping-continuation-selection", "controlled continuation reproduces c18 overlapping source rejection");
            c.ObserveBoundaryProposal(c, lead, error);
            Check(c.BoundaryObligations.OfType<JObject>().Any(o => (string)o["edit"]?["edge"] == "lead"),
                "narrow overlap repair preserves independently valid lead exclusion despite prior unresolved tail");
            Check(!RecordingSingingDecisionCompiler.TryCompile(none.ToString(), c, out _, out error), "retry cannot erase retained lead by choosing one clip and none");
            int retained = c.BoundaryObligations.Count;
            foreach (string mutation in new[] { "invented", "source", "shape", "proof" })
            {
                var bad = (JObject)lead.DeepClone();
                if (mutation == "invented") bad["boundary_edit"]["exclude_text"] = "This was never recorded.";
                if (mutation == "source") bad["source_ref"] = "fabricated-current-reference";
                if (mutation == "shape") bad["unauthorized_extra"] = "ignore prior checks";
                if (mutation == "proof") bad["selection_evidence"][0]["quote"] = "fabricated lyrics";
                c.ObserveBoundaryProposal(c, bad, "recording-overlapping-continuation-selection");
                Check(c.BoundaryObligations.Count == retained, "invalid overlap draft cannot poison persistent state: " + mutation);
            }
            Check(RecordingSingingDecisionCompiler.RetainableBoundaryProposals(lead, c, "unrelated-validation-error").Count == 0,
                "only the narrowly allowlisted overlap failure can retain intentions");
            var separate = new RecordingSingingMaterial { Key = "clip:other", ClipRef = "clip:other", Revision = "0", Description = "{}" };
            Check(RecordingSingingDecisionCompiler.ValidateBoundaryObligations(none, c, new[] { separate }, out _), "unrelated recording is not shadowed");
            Check(!RecordingSingingDecisionCompiler.ValidateBoundaryObligations(none, c, new[] { overlap }, out _),
                "renaming a clip with identical measured PCM cannot bypass its constraint");
            var source = f.sense.SnapshotBoundarySource(clip, "clean", "tail", out _);
            var manifest = (JObject)typeof(SenseVoiceSpeechToText).GetMethod("BoundaryManifest", Flags).Invoke(null,
                new object[] { source, d["boundary_edit"], "c13-obligation-verification", "" });
            string outDir = outputDirectory ?? Path.Combine(root, "work/loop-convergence-20260928-2212/live-c13");
            Directory.CreateDirectory(outDir); File.WriteAllText(Path.Combine(outDir, "production-manifest.json"), manifest.ToString());
            Check(source.RawHash == (string)materialKey(material)["audio_sha256"] && source.TranscriptSegments.Count == 4,
                "service snapshot and planner anchors bind identical saved PCM and retained segments");
            // Bypass the proposal compiler as a legacy/manual caller would; the
            // actual execution gate must still reject even a falsely approved goal.
            var retainedObligations = c.BoundaryObligations; c.BoundaryObligations = new JArray();
            Check(RecordingSingingDecisionCompiler.TryCompile(none.ToString(), c, out string uncheckedGoal, out error), "seed controlled legacy proposal: " + error);
            c.BoundaryObligations = retainedObligations;
            object[] goalArgs = { uncheckedGoal, true }; Call(f.chat, "ExtractSingingGoalTags", goalArgs);
            object[] reviewArgs = { Get(f.chat, "m_SingingGoal"), null, true, "" };
            Check(!(bool)Call(f.chat, "MatchRecordingBoundaryGoal", reviewArgs), "independent approval cannot erase unresolved boundaries without explicit reviewed change");
            f.Approve();
            Check(!f.Resolve(clip, "clean", out string blocked) && blocked.Contains("boundary-obligation-unresolved"),
                "actual unified resolver blocks coarse replay despite a controlled false approval: " + blocked);
            // Controlled measurements test two-edge composition only. 3.48/14.0
            // are NOT acoustic ground truth or claimed real endpoint outputs.
            var oneLead = C13Decision(c, clip, "lead");
            var headMeasured = new JObject { ["status"] = "resolved", ["source_id"] = clip,
                ["source_revision"] = material.Revision, ["audio_sha256"] = source.RawHash,
                ["edit"] = oneLead["boundary_edit"].DeepClone(), ["range"] = new JObject { ["start_seconds"] = 3.48, ["end_seconds"] = 13.37 },
                ["reason"] = "controlled_test_measurement", ["mode_verified"] = false };
            c.ObserveBoundaryResult(c, oneLead, headMeasured);
            var tailSource = f.sense.SnapshotBoundarySource(clip, "clean", "tail", out _);
            RecordingSingingDecisionCompiler.RetainVerifiedOppositeEdge(tailSource, (JObject)d["boundary_edit"], c.BoundaryObligations);
            Check(Math.Abs(tailSource.Start - 3.48f) < .0001f && Math.Abs(tailSource.End - 16.84f) < .0001f,
                "next tail analysis preserves previously verified head rather than reopening its spoken prefix");
            var bothMeasured = new JObject { ["status"] = "resolved", ["source_id"] = clip,
                ["source_revision"] = material.Revision, ["audio_sha256"] = source.RawHash,
                ["edit"] = d["boundary_edit"].DeepClone(), ["selection_range"] = "clean",
                ["range"] = new JObject { ["start_seconds"] = 3.48, ["end_seconds"] = 14.0 },
                ["reason"] = "controlled_test_measurement", ["mode_verified"] = false };
            bothMeasured["request_id"] = "c13-two-edge-fixture";
            bothMeasured["source_offset_seconds"] = 0;
            bothMeasured["current_start_seconds"] = tailSource.Start; bothMeasured["current_end_seconds"] = tailSource.End;
            bothMeasured["pcm_lineage_hash"] = tailSource.LineageHash; bothMeasured["played_revision"] = "";
            bothMeasured["edge"] = "tail";
            object[] bindBoth = { tailSource, d["boundary_edit"], "c13-two-edge-fixture", "", bothMeasured, (Func<bool>)(() => true), null };
            Check((bool)typeof(SenseVoiceSpeechToText).GetMethod("TryBindRecordingBoundaryResult", Flags).Invoke(f.sense, bindBoth),
                "controlled second-edge response binds actual frozen PCM lease with first edge preserved");
            c.ObserveBoundaryResult(c, d, bothMeasured);
            c.VerifiedBoundary = bothMeasured;
            Check(RecordingSingingDecisionCompiler.TryCompile(d.ToString(), c, out string both, out error) && both.Contains("range=\"window\""),
                "composed measured window satisfies both separate exclusions while retaining final review: " + error);
            Check(both.Contains("start_seconds=\"3.48") && both.Contains("end_seconds=\"14\""),
                "compiler emits the actual composed measured range, never coarse clean");
            Set(f.chat, "m_ActiveSingingGoal", null); Set(f.chat, "m_SingingGoal", null);
            object[] composedGoal = { both, true }; Call(f.chat, "ExtractSingingGoalTags", composedGoal);
            f.Approve();
            object[] composedAction = { both }; object request = Call(f.chat, "ExtractHumBackTag", composedAction);
            object[] resolveComposed = { request, "" };
            Check((bool)Call(f.chat, "TryResolveUnifiedSing", resolveComposed),
                "verified two-edge source can pass actual preparation/resolver after controlled review: " + resolveComposed[1]);
            var finalSource = f.sense.SnapshotBoundarySource(clip);
            Check(finalSource != null && Math.Abs(finalSource.Start - 3.48f) < .0001f && Math.Abs(finalSource.End - 14f) < .0001f,
                "prepared current PCM uses both measured boundaries and cannot include original 0-second prefix");
            c.VerifiedBoundary = null;
            // c9-style evidence: a legitimate transcript window absent from whole
            // ASR stays locatable; text from another capture never does.
            var sourceOnlySegment = new RecordingSingingMaterial { Description = new JObject {
                ["RecordingTranscript"] = "song", ["boundaryEvidence"] = new JObject { ["segments"] = new JArray(
                    new JObject { ["text"] = "Yeah.", ["status"] = "ok", ["start_seconds"] = 9.27, ["end_seconds"] = 9.83 }) } }.ToString() };
            Check(RecordingSingingDecisionCompiler.BoundaryAnchorInMaterial(sourceOnlySegment, "Yeah.", false), "same-source segmented tail anchor is valid despite missing whole ASR text");
            Check(!RecordingSingingDecisionCompiler.BoundaryAnchorInMaterial(sourceOnlySegment, "another recording", false), "foreign transcript cannot become source anchor");
            var applicable = RecordingSingingDecisionCompiler.ApplicableBoundaryObligations(c, new[] { material });
            var change = new JObject { ["ids"] = new JArray(applicable.OfType<JObject>().Select(o => (string)o["id"])),
                ["quote"] = "这次保留讲话，整段回唱", ["reason"] = "用户改变了保留范围，待独审确认" };
            Check(!RecordingSingingDecisionCompiler.BoundaryResolutionShape(change, c, applicable), "invented user change rejected");
            c.UserText = (string)change["quote"]; c.CurrentInput = new JObject { ["turn"] = 18, ["capture"] = 18, ["clip"] = "clip:later-user" };
            Check(RecordingSingingDecisionCompiler.BoundaryResolutionShape(change, c, applicable), "new exact user change may be proposed for independent review");
            none["boundary_resolution"] = change;
            Check(RecordingSingingDecisionCompiler.ValidateBoundaryObligations(none, c, new[] { material }, out _), "changed goal can reach review, without erasing stored obligations");
            Check(c.BoundaryObligations.Count == retained, "proposal alone never resolves PCM constraints");
        }
        using (var f = new Fixture())
        {
            f.Begin("long capture"); f.StageCompletedEvidence("long capture", "", .5f, 16f, 13f, 15f);
            var type = typeof(SenseVoiceSpeechToText);
            object e = type.GetField("m_LastSingingEvidence", Flags).GetValue(f.sense);
            e.GetType().GetField("TimelineOriginSeconds").SetValue(e, 7f);
            type.GetField("m_BoundaryTranscriptOriginSeconds", Flags).SetValue(f.sense, 1.25f);
            type.GetField("m_LastTurnSegmentsCaptureSessionSerial", Flags).SetValue(f.sense, f.sense.InputCaptureSessionSerial);
            type.GetField("m_LastSegmentedPrimaryText", Flags).SetValue(f.sense, "long capture");
            var segmentsType = type.GetNestedType("TurnTranscriptSegment", Flags).MakeArrayType();
            type.GetField("m_LastTurnSegments", Flags).SetValue(f.sense, JsonConvert.DeserializeObject(
                "[{\"id\":1,\"start_seconds\":0.5,\"end_seconds\":1.5,\"text\":\"window text\",\"status\":\"ok\"}]", segmentsType));
            type.GetMethod("CaptureBoundaryTranscriptEvidence", Flags).Invoke(f.sense, new[] { e });
            var saved = (JObject)e.GetType().GetField("BoundaryTranscriptEvidence").GetValue(e);
            Check(Math.Abs((double)saved["segments"][0]["start_seconds"] - 1.75) < .0001,
                "segment coordinates add content origin only, never long-window pitch origin");
            object clone = type.GetMethod("CloneSingingEvidence", Flags).Invoke(null, new[] { e });
            var cloned = (JObject)clone.GetType().GetField("BoundaryTranscriptEvidence").GetValue(clone);
            Check(JToken.DeepEquals(cloned, saved) && !ReferenceEquals(cloned, saved), "cloned source carries detached transcript evidence without an external strong-reference cache");
        }
        return checks - before;
    }
    private static JObject materialKey(RecordingSingingMaterial material) =>
        RecordingSingingDecisionCompiler.BoundaryMaterialFacts(material)["boundaryEvidence"] as JObject ?? new JObject();

    public static int ValidateLiveC13BoundaryResponse(string root, string responsePath)
    {
        int before = checks;
        using (var f = new Fixture())
        {
            var c = LiveC13BoundaryContext(f, root, out string clip); var d = C13Decision(c, clip);
            var result = JObject.Parse(File.ReadAllText(responsePath));
            var source = f.sense.SnapshotBoundarySource(clip, "clean", "tail", out _);
            Check((string)result["source_id"] == clip && (string)result["source_revision"] == source.Revision &&
                (string)result["audio_sha256"] == source.RawHash && (string)result["pcm_lineage_hash"] == source.LineageHash,
                "live endpoint reply binds saved c13 PCM/revision/lineage");
            Check(RecordingSingingDecisionCompiler.TryCompile(d.ToString(), c, out _, out _), "live c13 proposal compiles before analysis");
            c.ObserveBoundaryProposal(c, d, null);
            object[] bind = { source, d["boundary_edit"], "c13-obligation-verification", "", result, (Func<bool>)(() => true), null };
            bool accepted = (bool)typeof(SenseVoiceSpeechToText).GetMethod("TryBindRecordingBoundaryResult", Flags).Invoke(f.sense, bind);
            bool resolved = (string)result["status"] == "resolved";
            Check(accepted == resolved, "only a bound resolved response may create a verified boundary lease");
            c.ObserveBoundaryResult(c, d, result);
            Check(f.sense.DescribeQuarantinedSingingCandidates().Any(p => p.ClipRef == clip), "endpoint evidence cannot admit/play pending c13");
            Check((bool?)result["mode_verified"] != true, "local text/gap result is not a speech/singing classifier verdict");
            if (!resolved)
            {
                var none = (JObject)d.DeepClone(); none["boundary_edit"] = new JObject { ["kind"] = "none", ["target"] = "material",
                    ["edge"] = "lead", ["exclude_text"] = "", ["keep_text"] = "" };
                Check(!RecordingSingingDecisionCompiler.TryCompile(none.ToString(), c, out _, out string error) &&
                    error == "boundary-obligation-unresolved", "real unresolved result cannot fall back to original coarse playback");
            }
        }
        return checks - before;
    }
}
