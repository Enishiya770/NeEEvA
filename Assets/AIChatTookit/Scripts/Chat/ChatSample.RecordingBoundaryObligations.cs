using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private readonly JArray m_RecordingBoundaryObligations = new JArray();
    private int m_RecordingBoundaryObligationSequence;
    private JObject m_RecordingBoundaryResolutionProposal;
    private JObject m_RecordingBoundaryResolutionInput;
    private string m_RecordingBoundaryResolutionRefs, m_RecordingBoundaryResolutionRange;
    private int m_RecordingBoundaryOverrideGoal = -1;
    private long m_RecordingBoundaryOverrideInput = -1;
    private JArray m_RecordingBoundaryOverrideIds = new JArray();

    private void RetainRecordingBoundaryProposal(RecordingSingingDecisionContext context, JObject proposal, string error)
    {
        foreach (JObject obligation in RecordingSingingDecisionCompiler.RetainableBoundaryProposals(proposal, context, error))
        {
            if (m_RecordingBoundaryObligations.OfType<JObject>().Any(o =>
                (string)o["clip"] == (string)obligation["clip"] && (string)o["revision"] == (string)obligation["revision"] &&
                (string)o["audio_sha256"] == (string)obligation["audio_sha256"] && JToken.DeepEquals(o["edit"], obligation["edit"]))) continue;
            obligation["id"] = "boundary-obligation-" + (++m_RecordingBoundaryObligationSequence);
            m_RecordingBoundaryObligations.Add(obligation);
            Debug.Log("[Sing/BoundaryObligation] retained " + RecordingSingingDecisionCompiler.BoundaryObligationFacts(new JArray(obligation.DeepClone())).ToString(Formatting.None));
        }
        context.BoundaryObligations = (JArray)m_RecordingBoundaryObligations.DeepClone();
        if (error == null)
        {
            context.BoundaryResolution = proposal["boundary_resolution"] as JObject;
            context.BoundaryResolution = context.BoundaryResolution == null ? null : (JObject)context.BoundaryResolution.DeepClone();
            m_RecordingBoundaryResolutionProposal = context.BoundaryResolution;
            m_RecordingBoundaryResolutionInput = context.CurrentInput == null ? null : (JObject)context.CurrentInput.DeepClone();
            m_RecordingBoundaryResolutionRefs = string.Join(",", (proposal["materials"] as JArray ?? new JArray()).Values<string>());
            m_RecordingBoundaryResolutionRange = (string)proposal["range"];
        }
        context.OnCandidateSnapshot?.Invoke(context);
    }

    private void RetainRecordingBoundaryResult(RecordingSingingDecisionContext context, JObject proposal, JObject result)
    {
        string key = (string)(proposal["materials"] as JArray)?.FirstOrDefault();
        var material = context.Materials.FirstOrDefault(m => m.Key == key);
        if (material == null) return;
        foreach (var obligation in m_RecordingBoundaryObligations.OfType<JObject>().Where(o =>
            RecordingSingingDecisionCompiler.BoundarySameSource(o, material) && JToken.DeepEquals(o["edit"], proposal["boundary_edit"])))
        {
            obligation["reason"] = (string)result?["reason"] ?? "boundary_service_unavailable";
            obligation["state"] = (string)result?["status"] == "resolved" ? "verified_window_available" : "boundary_unresolved";
            if ((string)result?["status"] == "resolved")
            {
                obligation["verified"] = result.DeepClone();
                obligation["verifiedSourceFacts"] = RecordingSingingDecisionCompiler.BoundaryMaterialFacts(material);
            }
        }
        context.BoundaryObligations = (JArray)m_RecordingBoundaryObligations.DeepClone();
        context.OnCandidateSnapshot?.Invoke(context);
    }

    private RecordingSingingDecisionContext BoundaryGoalContext(SingingGoal goal)
    {
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        var context = new RecordingSingingDecisionContext { UserText = m_LastUserMsg ?? "", CurrentInput = RecordingInputIdentity(),
            BoundaryObligations = (JArray)m_RecordingBoundaryObligations.DeepClone() };
        if (sense != null) context.Materials.AddRange(SnapshotRecordingSingingMaterials(sense));
        if (sense != null) foreach (var m in context.Materials)
        {
            var f = RecordingSingingDecisionCompiler.BoundaryMaterialFacts(m);
            f["boundaryEvidence"] = sense.DescribeRecordingBoundaryEvidence(m.ClipRef); m.Description = f.ToString(Formatting.None);
        }
        // A verified source window is checked geometrically against retained PCM;
        // it is not replaced with a newly guessed named range at execution.
        if (goal?.range == "window") foreach (var m in context.Materials)
        {
            var f = RecordingSingingDecisionCompiler.BoundaryMaterialFacts(m);
            f["CleanStartSeconds"] = goal.start; f["CleanEndSeconds"] = goal.end; m.Description = f.ToString(Formatting.None);
        }
        return context;
    }

    private bool MatchRecordingBoundaryGoal(SingingGoal goal, JObject review, bool allowReviewedChange, out string reason)
    {
        reason = "";
        if (goal == null || m_RecordingBoundaryObligations.Count == 0) return true;
        var context = BoundaryGoalContext(goal);
        var refs = (goal.refs ?? "").Split(',');
        var selected = context.Materials.Where(m => refs.Contains(m.ClipRef)).ToArray();
        var applicable = RecordingSingingDecisionCompiler.ApplicableBoundaryObligations(context, selected);
        var proposal = new JObject { ["decision"] = "perform", ["range"] = goal.range == "window" ? "clean" : goal.range };
        if (RecordingSingingDecisionCompiler.ValidateBoundaryObligations(proposal, context, selected, out _)) return true;
        if (!allowReviewedChange && m_RecordingBoundaryOverrideGoal == goal.version &&
            m_RecordingBoundaryOverrideInput == m_AcceptedRecordingInputRevision &&
            applicable.OfType<JObject>().All(o => m_RecordingBoundaryOverrideIds.Values<string>().Contains((string)o["id"]))) return true;
        var resolution = review?["boundary_resolution"] as JObject;
        bool sameProposal = resolution != null && m_RecordingBoundaryResolutionProposal != null &&
            JToken.DeepEquals(resolution["ids"], m_RecordingBoundaryResolutionProposal["ids"]) &&
            (string)resolution["quote"] == (string)m_RecordingBoundaryResolutionProposal["quote"] &&
            JToken.DeepEquals(context.CurrentInput, m_RecordingBoundaryResolutionInput) &&
            goal.refs == m_RecordingBoundaryResolutionRefs && goal.range == m_RecordingBoundaryResolutionRange;
        if (allowReviewedChange && sameProposal &&
            RecordingSingingDecisionCompiler.BoundaryResolutionShape(resolution, context, applicable))
        {
            m_RecordingBoundaryOverrideGoal = goal.version;
            m_RecordingBoundaryOverrideInput = m_AcceptedRecordingInputRevision;
            m_RecordingBoundaryOverrideIds = (JArray)resolution["ids"].DeepClone();
            Debug.Log("[Sing/BoundaryObligation] user_scope_change_reviewed " + resolution.ToString(Formatting.None));
            return true;
        }
        reason = "boundary-obligation-unresolved：同一源仍有未完成的讲话排除核对；none/clean、重试或新目标版本不能替代真实范围验证。明确改变保留要求须本轮逐字引文和独立审核同时确认。";
        return false;
    }
}
