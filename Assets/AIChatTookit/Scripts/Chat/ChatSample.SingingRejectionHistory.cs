using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    // Immutable review facts survive a new accepted input. Resolution is a separate
    // event; neither a new proposal version nor a new utterance erases a rejection.
    private sealed class SingingReviewRejection
    {
        public JObject fact, resolution;
        public string sourceKey, targetKey, evidenceKey;
        public bool sourceMismatch;
    }
    private readonly List<SingingReviewRejection> m_SingingReviewRejections = new List<SingingReviewRejection>();
    private int m_SingingReviewRejectionSequence;

    private static string RecordingFactHash(JToken value)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToString(Formatting.None)))).Replace("-", "").ToLowerInvariant();
    }

    private JObject SingingTargetIdentity(SingingGoal goal)
    {
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        var sources = new JArray();
        foreach (string reference in (goal?.refs ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
        {
            string clip = sense?.CanonicalSingingClipReference(reference) ?? reference;
            object described = sense?.DescribeRecordingSingingDecisionFacts(clip);
            var facts = described == null ? new JObject() : JObject.FromObject(described);
            var bounds = RecordingSingingDecisionCompiler.RangeEvidence(facts, goal.range)?["ranges"]?[goal.range];
            sources.Add(new JObject { ["clip"] = clip,
                ["start"] = goal.range == "window" ? new JValue(goal.start) : bounds?["start"]?.DeepClone(),
                ["end"] = goal.range == "window" ? new JValue(goal.end) : bounds?["end"]?.DeepClone(),
                ["unresolvedRange"] = bounds == null && goal.range != "window" ? goal.range : null });
        }
        return new JObject { ["sources"] = sources, ["completion"] = goal?.completion,
            ["music"] = goal?.music?.DeepClone(), ["repairScope"] = goal?.repairScope };
    }

    private string SingingSourceEvidenceKey(SingingGoal goal)
    {
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        var values = new JArray();
        foreach (string clip in CanonicalOrderedSingingRefs(goal?.refs).Split(',').Where(s => s.Length > 0)
            .Distinct().OrderBy(s => s, StringComparer.Ordinal))
        {
            var described = sense?.DescribeRecordingSingingDecisionFacts(clip);
            var facts = described == null ? new JObject() : JObject.FromObject(described);
            // Re-reading words, readiness or a revision is not a new audio source.
            // The existing read-only snapshot supplies actual PCM and pitch hashes.
            var value = new JObject { ["clip"] = sense?.CanonicalSingingClipReference(clip) ?? clip };
            var audio = sense?.SnapshotBoundarySource(clip, "clean", "lead", out _) ??
                sense?.SnapshotBoundarySource(clip, "expanded", "lead", out _);
            value["rawPcmSha256"] = audio?.RawHash;
            value["rawPitchSha256"] = audio?.EvidencePitchHash;
            foreach (string name in new[] { "RecordingSequence", "CaptureSessionSerial", "RawAudioByteCount", "RawSeconds",
                "CleanStartSeconds", "CleanEndSeconds", "ExpandedStartSeconds", "ExpandedEndSeconds", "pcmLineage" })
                if (facts[name] != null) value[name] = facts[name].DeepClone();
            values.Add(value);
        }
        return RecordingFactHash(values);
    }

    private string SingingSourceIdentityKey(SingingGoal goal)
        => RecordingFactHash(new JArray(CanonicalOrderedSingingRefs(goal?.refs).Split(',')
            .Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.Ordinal)));

    private string CanonicalOrderedSingingRefs(string references)
    {
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        return string.Join(",", (references ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)
            .Select(s => sense?.CanonicalSingingClipReference(s) ?? s));
    }

    private void RetainSingingReviewRejection(JObject reviewFact)
    {
        if (m_SingingGoal == null || (string)reviewFact?["validated"]?["status"] != "revise") return;
        string source = SingingSourceIdentityKey(m_SingingGoal), target = RecordingFactHash(SingingTargetIdentity(m_SingingGoal));
        string evidence = SingingSourceEvidenceKey(m_SingingGoal);
        if (m_SingingReviewRejections.Any(r => (long?)r.fact["inputRevision"] == m_AcceptedRecordingInputRevision &&
            r.targetKey == target && r.evidenceKey == evidence)) return;
        var fact = (JObject)reviewFact.DeepClone();
        fact["id"] = "singing-rejection-" + (++m_SingingReviewRejectionSequence);
        fact["inputRevision"] = m_AcceptedRecordingInputRevision;
        fact["userText"] = m_LastUserMsg ?? "";
        fact["inputBinding"] = RecordingInputIdentity();
        fact["sourceFingerprint"] = source;
        fact["targetFingerprint"] = target;
        fact["evidenceFingerprint"] = evidence;
        fact["physicalTarget"] = SingingTargetIdentity(m_SingingGoal);
        fact["expected"] = reviewFact["reported"]?["expected"]?.DeepClone();
        string expectedRefs = (string)(fact["expected"] as JObject)?["refs"];
        string SourceSet(string refs) => string.Join(",", CanonicalOrderedSingingRefs(refs).Split(',')
            .Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.Ordinal));
        bool sourceMismatch = expectedRefs != null && SourceSet(expectedRefs) != SourceSet(m_SingingGoal.refs);
        fact["constraint"] = sourceMismatch ? "source_identity" : "execution_target";
        m_SingingReviewRejections.Add(new SingingReviewRejection { fact = fact, sourceKey = source,
            targetKey = target, evidenceKey = evidence, sourceMismatch = sourceMismatch });
        while (m_SingingReviewRejections.Count > 16) m_SingingReviewRejections.RemoveAt(0);
    }

    private JArray BuildSingingRejectionHistoryFacts()
    {
        if (m_SingingReviewRejections.Count == 0) return new JArray();
        string source = SingingSourceIdentityKey(m_SingingGoal), evidence = SingingSourceEvidenceKey(m_SingingGoal);
        return new JArray(m_SingingReviewRejections.Select(r => {
            var fact = (JObject)r.fact.DeepClone();
            fact["sameSourceAsCurrent"] = r.sourceKey == source;
            fact["sameEvidenceAsCurrent"] = r.evidenceKey == evidence;
            fact["resolution"] = r.resolution?.DeepClone();
            return fact;
        }));
    }

    private bool MatchPriorSingingReviewConstraints(SingingGoal goal, JObject expected, out string reason)
    {
        reason = "";
        if (m_SingingReviewRejections.Count == 0) return true;
        string source = SingingSourceIdentityKey(goal), target = RecordingFactHash(SingingTargetIdentity(goal));
        var applicable = m_SingingReviewRejections.Where(r => r.resolution == null &&
            (r.sourceMismatch ? r.sourceKey == source : r.targetKey == target)).ToArray();
        if (applicable.Length == 0) return true;
        var prior = applicable[applicable.Length - 1];
        var resolution = expected?["prior_rejection_resolution"] as JObject;
        string basis = (string)resolution?["basis"], quote = ((string)resolution?["quote"] ?? "").Trim();
        bool identified = (string)resolution?["id"] == (string)prior.fact["id"] &&
            !string.IsNullOrWhiteSpace((string)resolution?["reason"]);
        bool changedGoal = identified && basis == "goal_changed" &&
            (long?)prior.fact["inputRevision"] != m_AcceptedRecordingInputRevision &&
            (int?)prior.fact["userTurn"] != m_SingingGoalTurn && quote.Length > 0 &&
            (m_LastUserMsg ?? "").IndexOf(quote, StringComparison.Ordinal) >= 0;
        string currentEvidence = SingingSourceEvidenceKey(goal);
        bool changedEvidence = identified && basis == "source_evidence_changed" &&
            applicable.All(r => r.evidenceKey != currentEvidence);
        if (!changedGoal && !changedEvidence)
        {
            reason = "先前独立审核已拒绝同一来源/执行目标（" + (string)prior.fact["id"] +
                "）；重新措辞、提案版本或新输入本身不能推翻它。须重新选源/修正实际目标，或由独审引用该拒绝及当前明确改变目标的原话/真实新来源证据。";
            return false;
        }
        // The reviewer supplies semantic judgement; exact-quote containment only
        // proves provenance, not that its judgement of a goal change is correct.
        foreach (var item in applicable) item.resolution = new JObject {
            ["inputRevision"] = m_AcceptedRecordingInputRevision, ["userTurn"] = m_SingingGoalTurn,
            ["goalVersion"] = goal.version, ["review"] = resolution.DeepClone(),
            ["scope"] = "Independent semantic resolution, locally checked identity/evidence provenance; not execution or a guarantee of semantic correctness." };
        return true;
    }

    private bool HasChangedRejectedSingingSourceEvidence()
    {
        string source = SingingSourceIdentityKey(m_SingingGoal);
        var prior = m_SingingReviewRejections.LastOrDefault(r => r.sourceKey == source && r.resolution == null);
        return prior != null && prior.evidenceKey != SingingSourceEvidenceKey(m_SingingGoal);
    }
}
