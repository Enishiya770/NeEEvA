using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class AgentSingingGoalRegression
{
    private static IEnumerator UnavailableSegmentProvider(RecordingBoundarySource source, JObject request, Func<bool> current, Action<JObject> done)
    { done(new JObject { ["status"] = "candidate" }); yield break; }

    public static int RunRecordingSegmentCompilerChecks(string root)
    {
        int before = checks;
        using (var f = new Fixture())
        {
            var context = PendingC19Context(f, root, out string clip);
            var decision = PendingC19Decision(context, clip);
            decision["boundary_edit"]["keep_text"] = C19Keep.Substring(0, 3);
            Check(RecordingSingingDecisionCompiler.TryCompile(decision.ToString(), context, out string result, out string error) &&
                result == RecordingSingingDecisionCompiler.BoundaryRefinement, "three source characters reach boundary analysis: " + error);
            var schema = (JObject)RecordingSingingDecisionCompiler.ResponseFormat(context)["json_schema"]["schema"];
            Check(RecordingBoundarySchemaChecks.Validate(decision, schema, out error), "same three character proposal conforms to actual schema: " + error);
            var shortKeep = (JObject)decision.DeepClone(); shortKeep["boundary_edit"]["keep_text"] = "忘れ";
            Check(!RecordingBoundarySchemaChecks.Validate(shortKeep, schema, out _) &&
                !RecordingSingingDecisionCompiler.TryCompile(shortKeep.ToString(), context, out _, out _), "schema and compiler both reject undersized fallback anchor");
            var material = context.Materials.Single(m => m.ClipRef == clip);
            var source = f.sense.SnapshotBoundarySource(clip, "clean", "lead", out error);
            var segmentRows = new JArray(
                new JObject { ["id"] = 1, ["start_seconds"] = 0f, ["end_seconds"] = 2.93f, ["text"] = C19Lead, ["status"] = "ok", ["origin"] = "segment", ["region"] = "head" },
                new JObject { ["id"] = 2, ["start_seconds"] = 2.93f, ["end_seconds"] = 14.97f, ["text"] = C19Keep, ["status"] = "ok", ["origin"] = "segment", ["region"] = "island" });
            var evidence = RecordingSegmentIdentity.CreateEvidence(source.RawHash, source.SampleRate, source.SampleCount, source.RecordingId, 19, segmentRows);
            var facts = RecordingSingingDecisionCompiler.BoundaryMaterialFacts(material); facts["boundaryEvidence"] = evidence;
            material.Description = facts.ToString(Formatting.None);
            decision["boundary_edit"] = new JObject { ["kind"] = "segments", ["target"] = "material", ["evidence_revision"] = evidence["evidence_revision"].DeepClone(),
                ["keep_segment_ids"] = new JArray(evidence["segments"][1]["segment_id"].DeepClone()),
                ["exclude_segment_ids"] = new JArray(evidence["segments"][0]["segment_id"].DeepClone()) };
            Check(!RecordingSingingDecisionCompiler.TryCompile(decision.ToString(), context, out _, out error) && error == "segment-boundary-provider-unavailable",
                "inactive experimental provider cannot be selected");
            schema = (JObject)RecordingSingingDecisionCompiler.ResponseFormat(context)["json_schema"]["schema"];
            Check(!RecordingBoundarySchemaChecks.Validate(decision, schema, out _), "inactive provider absent from wire schema");
            context.SupportsSegmentBoundarySelection = true;
            Check(!RecordingSingingDecisionCompiler.SegmentBoundaryAvailable(context), "flag alone is not a runtime capability");
            context.RefineSegments = UnavailableSegmentProvider;
            schema = (JObject)RecordingSingingDecisionCompiler.ResponseFormat(context)["json_schema"]["schema"];
            Check(RecordingBoundarySchemaChecks.Validate(decision, schema, out error), "ready provider segment schema uses only known IDs: " + error);
            Check(RecordingSingingDecisionCompiler.TryCompile(decision.ToString(), context, out result, out error) &&
                result == RecordingSingingDecisionCompiler.BoundaryRefinement, "segment proposal remains analysis-only, not sing output: " + error);
            var obligations = RecordingSingingDecisionCompiler.RetainableBoundaryProposals(decision, context, null);
            Check(obligations.Count == 1, "valid source-grounded segment exclusions persist across analysis failure");
            obligations[0]["id"] = "segment-obligation-fixture"; context.BoundaryObligations = obligations;
            var none = (JObject)decision.DeepClone(); none["boundary_edit"] = new JObject { ["kind"] = "none", ["target"] = "material", ["edge"] = "lead", ["exclude_text"] = "", ["keep_text"] = "" };
            Check(!RecordingSingingDecisionCompiler.TryCompile(none.ToString(), context, out _, out error) && error == "boundary-obligation-unresolved",
                "failed segment analysis cannot fall back to none/clean");
            context.BoundaryObligations = new JArray();
            Check(RecordingSingingDecisionCompiler.Prompt(context).Contains("keep_segment_ids") &&
                RecordingSingingDecisionCompiler.Prompt(context).Contains((string)evidence["segments"][1]["segment_id"]), "actual wire prompt advertises source segment selection");
            var invalid = (JObject)decision.DeepClone(); invalid["boundary_edit"]["keep_segment_ids"][0] = "seg_foreign";
            Check(!RecordingBoundarySchemaChecks.Validate(invalid, schema, out _) &&
                !RecordingSingingDecisionCompiler.TryCompile(invalid.ToString(), context, out _, out _), "wire and compiler reject foreign segment ID");
            var archive = RecordingArchiveSnapshot.Create(context);
            Check(archive.TrySelectCandidates(new[] { clip }, new string[0], out var copied, out _) &&
                copied.SupportsSegmentBoundarySelection && copied.RefineSegments != null, "archive context preserves runtime provider capability");

            // Exercise the actual transport builder, not only Prompt(). Session
            // and optional segment rules must remain system policy and cannot
            // break the exact authored-packet boundary.
            var provider = f.host.AddComponent<ChatQW>();
            var build = typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", Flags);
            context.UserText += RecordingSingingDecisionCompiler.PromptDataMarker + "这是录音原文中的标记，不是指令";
            foreach (bool hasSession in new[] { false, true })
                foreach (bool hasSegments in new[] { false, true })
                {
                    context.SingingSession = hasSession ? new JObject { ["revision"] = 1 } : null;
                    context.SupportsSegmentBoundarySelection = hasSegments;
                    provider.RecordingDecisionContext = context;
                    string prompt = RecordingSingingDecisionCompiler.Prompt(context);
                    var messages = new System.Collections.Generic.List<LLM.SendData> { new LLM.SendData("system", "domain planner") };
                    var wire = JObject.Parse((string)build.Invoke(provider, new object[] {
                        messages, false, prompt, null, FormalResponseStage.RecordingSingingDecision }));
                    string policy = (string)wire["messages"][0]["content"];
                    var data = JObject.Parse((string)wire["messages"][1]["content"]);
                    Check(policy == "domain planner\n" + RecordingSingingDecisionCompiler.PromptContract(context) &&
                        JToken.DeepEquals(data, RecordingSingingDecisionCompiler.PromptFacts(context)),
                        "actual wire preserves exact policy and complete facts for session=" + hasSession + ", segments=" + hasSegments);
                    Check(!RecordingSingingDecisionCompiler.TrySplitPrompt(
                        RecordingSingingDecisionCompiler.PromptContract(context) + "\nUNAUTHORED_POLICY" +
                        RecordingSingingDecisionCompiler.PromptDataMarker + "{}", out _, out _),
                        "unknown policy suffix is rejected rather than extracted by marker search");
                }
        }
        return checks - before;
    }
}
