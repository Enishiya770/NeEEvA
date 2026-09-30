using System;
using System.Collections;
using Newtonsoft.Json.Linq;

public partial class ChatQW
{
    private IEnumerator ResolveRecordingBoundary(string raw, RecordingSingingDecisionContext context, int generation,
        bool recordHistory, Action<string> onDelta, Action<SpeechText> onSpeech, Action<string> onComplete, ConversationTrace trace)
    {
        if (generation != m_StreamRequestGeneration) yield break;
        var decision = JObject.Parse(raw);
        JObject result = null;
        string boundaryKind = (string)decision["boundary_edit"]?["kind"];
        if ((boundaryKind == "trim" || boundaryKind == "segments") && context.RefineBoundary != null)
        {
            trace?.Mark("recording_boundary.send", new JObject { ["edit"] = decision["boundary_edit"], ["materials"] = decision["materials"],
                ["scope"] = "read-only local alignment of frozen source; no audio modified or played" });
            yield return context.RefineBoundary(decision, () => generation == m_StreamRequestGeneration, r => result = r);
        }
        if (generation != m_StreamRequestGeneration) yield break;
        string reason = (string)result?["reason"] ?? (context.RefineBoundary == null ? "source_binding_unavailable" : "boundary_service_unavailable");
        trace?.Mark("recording_boundary.result", result ?? new JObject { ["status"] = "boundary_unresolved", ["reason"] = reason });
        context.ObserveBoundaryResult?.Invoke(context, decision, result);
        if (generation != m_StreamRequestGeneration) yield break;
        if ((string)result?["status"] == "resolved")
        {
            context.VerifiedBoundary = result;
            if (RecordingSingingDecisionCompiler.TryCompile(raw, context, out string compiled, out string error) &&
                compiled != RecordingSingingDecisionCompiler.BoundaryRefinement && compiled != RecordingSingingDecisionCompiler.Handoff)
            {
                context.OnCandidateSnapshot?.Invoke(context); // Independent reviewer receives measured evidence, not model-guessed times.
                if (generation != m_StreamRequestGeneration) yield break;
                if (recordHistory) m_DataList.Add(new SendData("assistant", compiled));
                onComplete?.Invoke(compiled);
                yield break;
            }
            reason = error ?? "boundary_compilation_unresolved";
        }
        CompleteRecordingLocalReply(RecordingSingingDecisionCompiler.BoundaryUnresolvedReply(reason), generation,
            recordHistory, onDelta, onSpeech, onComplete, trace);
    }
}
