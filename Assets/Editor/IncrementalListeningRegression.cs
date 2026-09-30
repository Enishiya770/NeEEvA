using System;
using Newtonsoft.Json.Linq;

/// <summary>Pure protocol/evidence tests; no model, recorder, TTS, scene or task execution.</summary>
public static class IncrementalListeningRegression
{
    private static int checks;
    public static int RunOrThrow()
    {
        checks = 0;
        var source = Source("有看到桌上的小灯吗？");
        JObject wire = Wire(source.Transcript, "closed", "interpretation");
        Check(Parse(wire, source, out var candidate), "exact evidence creates a revocable hypothesis");
        Check(candidate.CanPresent(source.Transcript), "closed exact interpretation can be shown");
        Check(!candidate.CanPresent(source.Transcript + "不要关灯。"), "appended negation withdraws old interpretation");
        Check(!candidate.CanPresent("有看到桌上的小灯"), "rollback withdraws interpretation");
        Check(!candidate.CanPresent(source.Transcript + " "), "even minor source change requires a fresh interpretation");
        Check(candidate.Owns(7, 3, 13) && !candidate.Owns(8, 3, 13) &&
            !candidate.Owns(7, 4, 13) && !candidate.Owns(7, 3, 132.1f), "capture, input generation and age bound preparation");
        var facts = candidate.ToFinalContext(source.Transcript + "不，先别管灯。");
        Check((bool)facts["finalMismatch"] && (bool)facts["requiresFinalReview"] && !(bool)facts["executionAuthorized"],
            "correction remains final authority and never auto-adopts a partial plan");
        Check(facts["inner_reaction"] == null && !facts.ToString().Contains("原来你想让我看这盏灯。") &&
            facts["draft"] == null, "formal context carries data, not old private prose or a suggested reply");
        var span = facts["source"]["evidenceSpans"][0];
        Check(source.Transcript.Substring((int)span["start"], (int)span["end"] - (int)span["start"]) == (string)span["quote"],
            "program-computed evidence span exactly indexes the source");
        var returned = candidate.EvidenceSpans; returned.Clear();
        Check(candidate.EvidenceSpans.Count == 1, "consumers cannot mutate the stored evidence lease");

        wire = Wire(source.Transcript, "open", "interpretation");
        Check(Parse(wire, source, out candidate) && !candidate.CanPresent(source.Transcript),
            "open interpretation is stored for review but cannot become a confident bubble");
        wire["reaction_kind"] = "feeling";
        Check(Parse(wire, source, out candidate) && !candidate.CanPresent(source.Transcript), "open feeling must be explicitly tentative");
        wire["reaction_kind"] = "tentative";
        Check(Parse(wire, source, out candidate) && candidate.CanPresent(source.Transcript), "unfinished speech can show a tentative natural feeling");
        wire["reaction_kind"] = "none";
        Check(Parse(wire, source, out candidate) && !candidate.CanPresent(source.Transcript), "none never invents a bubble");
        wire = Wire(source.Transcript, "closed", "interpretation");
        wire["quiet_boundary"]["turn_state"] = "open";
        Check(Parse(wire, source, out candidate) && !candidate.CanPresent(source.Transcript), "conflicting boundary cannot authorize interpretation display");
        Check(candidate.State == "closed" && candidate.ToFinalContext(source.Transcript)["candidate"]["state"].Value<string>() == "closed",
            "complete expressed intention stays closed independently of whether the conversational boundary remains open");
        wire["intention"]["missing"] = new JArray("否定的目标");
        Check(!Parse(wire, source, out candidate), "claimed closed with missing arguments is invalid");
        wire = Wire(source.Transcript); wire["intention"]["evidence"] = new JArray("请把灯关掉");
        Check(!Parse(wire, source, out candidate), "invented or history-derived evidence rejected");
        wire = Wire(source.Transcript); wire["preparation_version"] = 1.5;
        Check(!Parse(wire, source, out candidate), "fractional version cannot alias protocol 1");
        wire = Wire(source.Transcript); wire.Remove("intention");
        Check(!Parse(wire, source, out candidate), "legacy reaction-only JSON cannot create a new hypothesis");
        wire = Wire(source.Transcript); wire["intention"]["target"] = new string('x', 61);
        Check(!Parse(wire, source, out candidate), "bounded target prevents unbounded prompt import");
        wire = Wire(source.Transcript); wire["intention"]["missing"] = new JArray("a", "b", "c", "d");
        Check(!Parse(wire, source, out candidate), "missing arguments list stays bounded");
        Check(!IncrementalListeningCandidate.TryCreate(Wire(source.Transcript).ToString(), source, 3, 5, 10f, 18.01f,
            out candidate, out _), "late wire result expires");
        var invalid = source; invalid.SampledAt = float.NaN;
        Check(!Parse(Wire(source.Transcript), invalid, out candidate), "nonfinite source cannot satisfy lease comparisons");
        invalid = source; invalid.Recording = false;
        Check(!Parse(Wire(source.Transcript), invalid, out candidate), "closed recording is not an eligible source");

        source = Source("接下来唱第二段喽。初めて君と出会った。把开头说话剪掉。");
        wire = Wire(source.Transcript, "closed", "interpretation");
        wire["intention"]["domain"] = "singing";
        wire["intention"]["segments"] = new JArray(
            new JObject { ["kind"] = "spoken_lead", ["quote"] = "接下来唱第二段喽。" },
            new JObject { ["kind"] = "lyrics_candidate", ["quote"] = "初めて君と出会った。" },
            new JObject { ["kind"] = "instruction", ["quote"] = "把开头说话剪掉。" });
        Check(Parse(wire, source, out candidate) && candidate.SegmentSpans.Count == 3, "mixed utterance preserves three semantic spans");
        Check((string)candidate.ToFinalContext(source.Transcript)["source"]["semanticSegments"][1]["kind"] == "lyrics_candidate" &&
            !(bool)candidate.ToFinalContext(source.Transcript)["executionAuthorized"], "lyric text candidate is not singing or crop authorization");
        wire["intention"]["segments"][1]["quote"] = "用户没唱过的歌词";
        Check(!Parse(wire, source, out candidate), "segment text must exist exactly in current partial");
        wire["intention"]["segments"][1]["quote"] = "接下来唱第二段喽。";
        Check(!Parse(wire, source, out candidate), "overlap or out-of-order segments cannot be treated as time order");
        source = Source(new string('前', 650) + "尾部更正");
        wire = Wire(source.Transcript); wire["intention"]["evidence"] = new JArray("尾部更正");
        Check(Parse(wire, source, out candidate), "late source span can survive bounded context projection");
        facts = candidate.ToFinalContext(source.Transcript);
        Check((bool)facts["source"]["textTruncated"] && (int)facts["source"]["originalTextLength"] == 654 &&
            (int)facts["source"]["evidenceSpans"][0]["start"] == 650 &&
            ((string)facts["source"]["spanUnit"]).Contains("original untruncated"),
            "span coordinates explicitly refer to original snapshot while quotes preserve text beyond shortened view");
        Check(candidate.OwnershipFailure(8, 3, 13f) == "capture-changed" &&
            candidate.OwnershipFailure(7, 4, 13f) == "input-revision-changed" &&
            candidate.OwnershipFailure(7, 3, 132.1f) == "candidate-expired" &&
            candidate.OwnershipFailure(7, 3, float.NaN) == "invalid-age",
            "diagnostics distinguish source, input, expiry and invalid clock without relaxing ownership");
        facts["acceptedInputRevision"] = 11;
        var input = new JObject { ["context"] = new JObject { ["listeningPreparation"] = facts }, ["current_user"] = "最终输入" };
        var envelope = new JObject { ["messages"] = new JArray(new JObject { ["role"] = "system", ["content"] = "fixture" },
            new JObject { ["role"] = "user", ["content"] = input.ToString() }) };
        var attachment = IncrementalListeningCandidate.DescribeWireAttachment(envelope.ToString());
        Check((string)attachment["status"] == "present" && (int)attachment["capture"] == 7 &&
            (int)attachment["acceptedInputRevision"] == 11 && (string)attachment["modelUse"] == "unknown" &&
            attachment["requestAttached"] == null, "wire parsing describes identity but never itself claims HTTP sent or model adoption");
        Check(!attachment.ToString().Contains("最终输入") && !attachment.ToString().Contains("尾部更正") &&
            attachment["source"] == null, "attachment receipt contains identity and counts, never raw user or source text");
        const string prefix = "known fixture attachment note\n";
        envelope["messages"][1]["content"] = new JArray(
            new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = "data:image/png;base64,FAKE" } },
            new JObject { ["type"] = "text", ["text"] = prefix + input.ToString() });
        Check((string)IncrementalListeningCandidate.DescribeWireAttachment(envelope.ToString(), prefix)["status"] == "present",
            "known production multimodal prefix can be stripped without searching arbitrary quoted text");
        Check((string)IncrementalListeningCandidate.DescribeWireAttachment(envelope.ToString())["status"] == "invalid",
            "unknown user-text wrapper cannot be guessed as a candidate attachment");
        input["context"]["listeningPreparation"] = null;
        input["current_user"] = facts.ToString();
        envelope["messages"][1]["content"] = input.ToString();
        Check((string)IncrementalListeningCandidate.DescribeWireAttachment(envelope.ToString())["status"] == "absent",
            "candidate-looking user prose is not a formal context attachment");
        input["context"]["listeningPreparation"] = facts; facts["executionAuthorized"] = true;
        input["context"]["listeningPreparation"]["executionAuthorized"] = true;
        envelope["messages"][1]["content"] = input.ToString();
        Check((string)IncrementalListeningCandidate.DescribeWireAttachment(envelope.ToString())["status"] == "invalid",
            "malformed preparation cannot be logged as a valid attachment");
        Check((string)IncrementalListeningCandidate.DescribeWireAttachment("not-json")["status"] == "invalid",
            "diagnostic parsing failure is bounded and non-throwing");
        return checks;
    }

    public static PreparedTurnBoundaryEvidence Source(string text) => new PreparedTurnBoundaryEvidence {
        CaptureSerial = 7, TextRevision = 2, ActivityRevision = 1, AudioMs = 1400,
        Transcript = text, Recording = true, AsrHealthy = true, SampledAt = 10f };

    public static JObject Wire(string text, string state = "open", string reactionKind = "tentative") => new JObject {
        ["preparation_version"] = 1, ["reaction_kind"] = reactionKind,
        ["inner_reaction"] = "原来你想让我看这盏灯。", ["draft"] = "", ["language"] = "zh", ["confidence"] = .9f,
        ["observed_mode"] = "speech", ["mode_confidence"] = .9f,
        ["quiet_boundary"] = new JObject { ["action"] = state == "closed" ? "take_turn" : "continue",
            ["turn_state"] = state, ["mode"] = "speech", ["source"] = "user", ["confidence"] = .9f, ["reason"] = "fixture" },
        ["intention"] = new JObject { ["state"] = state, ["domain"] = "spatial",
            ["goal"] = "确认桌上的灯", ["target"] = "桌上的灯",
            ["missing"] = state == "closed" ? new JArray() : new JArray("后续条件"),
            ["evidence"] = new JArray(text), ["segments"] = new JArray() }
    };
    private static bool Parse(JObject value, PreparedTurnBoundaryEvidence source, out IncrementalListeningCandidate candidate) =>
        IncrementalListeningCandidate.TryCreate(value.ToString(), source, 3, 5, 10f, 12f, out candidate, out _);
    private static void Check(bool value, string label)
    { checks++; if (!value) throw new InvalidOperationException("Incremental listening: " + label); }
}
