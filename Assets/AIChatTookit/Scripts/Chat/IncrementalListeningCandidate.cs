using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>A bounded, evidenced hypothesis about a partial utterance; never an executable decision.</summary>
public sealed class IncrementalListeningCandidate
{
    public const int ProtocolVersion = 1;
    public const float MaximumAge = 120f;
    public readonly PreparedTurnBoundaryEvidence Source;
    public readonly int InputRevision, RequestVersion;
    public readonly float PreparedAt;
    public readonly string State, Domain, Goal, Target, Reaction, ReactionKind, Language;
    private readonly JArray missing, evidenceSpans, segmentSpans;
    public JArray Missing => (JArray)missing.DeepClone();
    public JArray EvidenceSpans => (JArray)evidenceSpans.DeepClone();
    public JArray SegmentSpans => (JArray)segmentSpans.DeepClone();
    public readonly bool BoundaryClosed;

    private IncrementalListeningCandidate(JObject value, PreparedTurnBoundaryEvidence source,
        int inputRevision, int requestVersion, float preparedAt, JArray spans, JArray segments)
    {
        Source = source; InputRevision = inputRevision; RequestVersion = requestVersion; PreparedAt = preparedAt;
        var intention = (JObject)value["intention"];
        State = (string)intention["state"]; Domain = (string)intention["domain"];
        Goal = (string)intention["goal"]; Target = (string)intention["target"];
        missing = (JArray)intention["missing"].DeepClone(); evidenceSpans = spans; segmentSpans = segments;
        Reaction = (string)value["inner_reaction"]; ReactionKind = (string)value["reaction_kind"];
        Language = (string)value["language"];
        var boundary = value["quiet_boundary"] as JObject;
        BoundaryClosed = (string)boundary?["turn_state"] == "closed" &&
            new[] { "take_turn", "complete", "ask_user" }.Contains((string)boundary?["action"]);
    }

    public static bool TryCreate(string response, PreparedTurnBoundaryEvidence source, int inputRevision,
        int requestVersion, float requestedAt, float preparedAt,
        out IncrementalListeningCandidate candidate, out string reason)
    {
        candidate = null;
        if (!source.Recording || source.CaptureSerial <= 0 || !source.AsrHealthy || source.AudioMs <= 0 ||
            source.Transcript == null || !Finite(source.SampledAt) || !Finite(requestedAt) || !Finite(preparedAt) ||
            requestedAt < source.SampledAt || requestedAt - source.SampledAt > PreparedTurnBoundary.MaximumSnapshotAge ||
            preparedAt < requestedAt || preparedAt - requestedAt > PreparedTurnBoundary.MaximumRequestAge)
            return Reject("invalid-source-lease", out reason);
        if (string.IsNullOrWhiteSpace(response) || response.Length > 10000)
            return Reject("empty-or-oversized-response", out reason);
        try
        {
            var value = JObject.Parse(response, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            var intention = value["intention"] as JObject;
            if (intention == null || value["preparation_version"]?.Type != JTokenType.Integer ||
                (int?)value["preparation_version"] != ProtocolVersion)
                return Reject("missing-versioned-intention", out reason);
            if (!Choice(intention["state"], "open", "closed", "uncertain") ||
                !Choice(intention["domain"], "conversation", "spatial", "singing", "mixed", "unknown") ||
                !Text(intention["goal"], 80) || !Text(intention["target"], 60) ||
                !TextArray(intention["missing"], 3, 32) || !TextArray(intention["evidence"], 3, 120) ||
                !Text(value["inner_reaction"], 80) ||
                !Choice(value["reaction_kind"], "feeling", "tentative", "interpretation", "none") ||
                !Choice(value["language"], "ja", "zh", "en"))
                return Reject("invalid-intention-fields", out reason);
            string state = (string)intention["state"], domain = (string)intention["domain"];
            var evidence = (JArray)intention["evidence"];
            if (state == "closed" && (((JArray)intention["missing"]).Count > 0 ||
                string.IsNullOrWhiteSpace((string)intention["goal"]) || evidence.Count == 0) ||
                domain != "unknown" && evidence.Count == 0)
                return Reject("unsupported-or-incomplete-intention", out reason);
            var spans = new JArray();
            foreach (JToken token in evidence)
            {
                string quote = (string)token;
                int start = source.Transcript.IndexOf(quote, StringComparison.Ordinal);
                if (start < 0) return Reject("evidence-not-in-source", out reason);
                spans.Add(new JObject { ["start"] = start, ["end"] = start + quote.Length, ["quote"] = quote });
            }
            if (!(intention["segments"] is JArray segments) || segments.Count > 3)
                return Reject("invalid-semantic-segments", out reason);
            var mappedSegments = new JArray();
            int cursor = 0;
            foreach (var token in segments)
            {
                if (!(token is JObject row) || !Choice(row["kind"], "spoken_lead", "lyrics_candidate", "spoken_tail", "instruction", "uncertain") ||
                    !Text(row["quote"], 120) || string.IsNullOrWhiteSpace((string)row["quote"]))
                    return Reject("invalid-semantic-segment", out reason);
                string quote = (string)row["quote"];
                int start = source.Transcript.IndexOf(quote, cursor, StringComparison.Ordinal);
                if (start < 0) return Reject("segment-not-in-source-order", out reason);
                cursor = start + quote.Length;
                mappedSegments.Add(new JObject { ["kind"] = (string)row["kind"], ["quote"] = quote,
                    ["start"] = start, ["end"] = cursor });
            }
            candidate = new IncrementalListeningCandidate(value, source, inputRevision, requestVersion, preparedAt, spans, mappedSegments);
            reason = "partial-hypothesis";
            return true;
        }
        catch (JsonException) { return Reject("invalid-json", out reason); }
        catch (ArgumentException) { return Reject("invalid-field-type", out reason); }
        catch (InvalidCastException) { return Reject("invalid-field-type", out reason); }
        catch (FormatException) { return Reject("invalid-field-type", out reason); }
        catch (OverflowException) { return Reject("invalid-field-type", out reason); }
    }

    // The JSON classification is a semantic proposal, not a guarantee of meaning.
    // Unfinished speech may show only a tentative present feeling. No string keywords
    // are used to infer completion or to silently turn a reaction into an action.
    public bool CanPresent(string currentTranscript)
    {
        if (!ExactSource(currentTranscript) || string.IsNullOrWhiteSpace(Reaction) || ReactionKind == "none") return false;
        if (State != "closed") return ReactionKind == "tentative";
        if (ReactionKind == "interpretation") return BoundaryClosed && missing.Count == 0 && evidenceSpans.Count > 0;
        return ReactionKind == "feeling" || ReactionKind == "tentative";
    }

    public bool ExactSource(string currentTranscript) =>
        string.Equals(Source.Transcript ?? "", currentTranscript ?? "", StringComparison.Ordinal);

    public bool Owns(int capture, int revision, float now) => OwnershipFailure(capture, revision, now) == null;

    public string OwnershipFailure(int capture, int revision, float now)
    {
        if (capture <= 0 || capture != Source.CaptureSerial) return "capture-changed";
        if (revision != InputRevision) return "input-revision-changed";
        if (!Finite(now) || now < PreparedAt) return "invalid-age";
        return now - PreparedAt > MaximumAge ? "candidate-expired" : null;
    }

    // Observability only: read the actual serialized request, not a separately built
    // context which might never be sent. Nothing in this method authorizes execution
    // or claims that the model used the candidate to produce its answer.
    public static JObject DescribeWireAttachment(string wire, string knownMultimodalPrefix = null)
    {
        var result = new JObject { ["status"] = "absent", ["reason"] = "no-preparation-in-wire",
            ["modelUse"] = "unknown", ["executionAuthorized"] = false };
        try
        {
            var settings = new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error };
            var envelope = JObject.Parse(wire ?? "", settings);
            if (!(envelope["messages"] is JArray messages)) return Invalid("invalid-messages");
            // The production Fast builder has one user message. Do not accidentally
            // read a quoted older message, current_user prose, image URL or system text.
            var users = messages.OfType<JObject>().Where(v => (string)v["role"] == "user").ToArray();
            if (users.Length != 1) return Invalid("ambiguous-user-message");
            JToken content = users[0]["content"];
            string userText = content?.Type == JTokenType.String ? (string)content : null;
            if (content is JArray parts)
            {
                var texts = parts.OfType<JObject>().Where(v => (string)v["type"] == "text").ToArray();
                if (texts.Length != 1 || texts[0]["text"]?.Type != JTokenType.String) return Invalid("ambiguous-user-text");
                userText = (string)texts[0]["text"];
                if (!string.IsNullOrEmpty(knownMultimodalPrefix) && userText.StartsWith(knownMultimodalPrefix, StringComparison.Ordinal))
                    userText = userText.Substring(knownMultimodalPrefix.Length);
            }
            if (userText == null) return Invalid("missing-user-text");
            var input = JObject.Parse(userText, settings);
            var facts = input["context"] as JObject;
            JToken preparation = facts?["listeningPreparation"];
            if (preparation == null || preparation.Type == JTokenType.Null) return result;
            if (!(preparation is JObject value) || (int?)value["version"] != ProtocolVersion ||
                (string)value["status"] != "uncommitted-hypothesis" ||
                value["executionAuthorized"]?.Type != JTokenType.Boolean || (bool)value["executionAuthorized"] ||
                value["requiresFinalReview"]?.Type != JTokenType.Boolean || !(bool)value["requiresFinalReview"] ||
                value["finalMismatch"]?.Type != JTokenType.Boolean || !(value["source"] is JObject source))
                return Invalid("invalid-preparation-envelope");
            foreach (string key in new[] { "capture", "inputRevision", "requestVersion", "textRevision" })
                if (source[key]?.Type != JTokenType.Integer) return Invalid("invalid-source-identity");
            if ((long)source["capture"] <= 0 || value["acceptedInputRevision"]?.Type != JTokenType.Integer)
                return Invalid("invalid-accepted-identity");
            result["status"] = "present"; result["reason"] = "serialized-candidate-in-user-context";
            result["capture"] = source["capture"].DeepClone();
            result["inputRevision"] = source["inputRevision"].DeepClone();
            result["requestVersion"] = source["requestVersion"].DeepClone();
            result["textRevision"] = source["textRevision"].DeepClone();
            result["acceptedInputRevision"] = value["acceptedInputRevision"].DeepClone();
            result["finalMismatch"] = value["finalMismatch"].DeepClone();
            result["requiresFinalReview"] = true;
            result["preparedMaterialsPresent"] = value["preparedMaterials"] != null && value["preparedMaterials"].Type != JTokenType.Null;
            result["evidenceCount"] = (source["evidenceSpans"] as JArray)?.Count ?? 0;
            result["semanticSegmentCount"] = (source["semanticSegments"] as JArray)?.Count ?? 0;
            return result;
        }
        catch (JsonException) { return Invalid("invalid-wire-json"); }
        catch (ArgumentException) { return Invalid("invalid-wire-type"); }
        catch (InvalidCastException) { return Invalid("invalid-wire-type"); }
        catch (FormatException) { return Invalid("invalid-wire-type"); }
        catch (OverflowException) { return Invalid("invalid-wire-type"); }

        JObject Invalid(string reason) => new JObject { ["status"] = "invalid", ["reason"] = reason,
            ["modelUse"] = "unknown", ["executionAuthorized"] = false };
    }

    public JObject ToFinalContext(string finalTranscript)
    {
        bool mismatch = !ExactSource(finalTranscript);
        return new JObject {
            ["version"] = ProtocolVersion, ["status"] = "uncommitted-hypothesis",
            ["source"] = new JObject {
                ["capture"] = Source.CaptureSerial, ["inputRevision"] = InputRevision,
                ["requestVersion"] = RequestVersion, ["textRevision"] = Source.TextRevision,
                ["audioMs"] = Source.AudioMs, ["text"] = Bound(Source.Transcript, 640),
                ["textTruncated"] = Source.Transcript.Length > 640, ["originalTextLength"] = Source.Transcript.Length,
                ["evidenceSpans"] = evidenceSpans.DeepClone(), ["semanticSegments"] = segmentSpans.DeepClone(),
                ["segmentScope"] = "Text-only candidates, not acoustic singing confirmation or permission to crop/play audio.",
                ["spanUnit"] = "UTF-16; end exclusive; coordinates refer to original untruncated snapshot, not the shortened text field" },
            ["candidate"] = new JObject { ["state"] = State, ["domain"] = Domain,
                ["goal"] = Goal, ["target"] = Target, ["missing"] = missing.DeepClone() },
            ["finalMismatch"] = mismatch, ["requiresFinalReview"] = true,
            ["executionAuthorized"] = false,
            ["scope"] = "Preparation from the identified earlier partial only. Current_user is the complete accepted input and has highest authority. " +
                "Reconsider the full ending, correction, negation, conditions and current execution facts even when finalMismatch=false. " +
                "Neither a domain, goal, closed state nor quoted evidence is an accepted request, selected route, source approval or action receipt. " +
                "Discard or revise any hypothesis contradicted by current_user. No reaction prose or suggested reply is supplied."
        };
    }

    private static bool Choice(JToken token, params string[] values) => token?.Type == JTokenType.String && values.Contains((string)token);
    private static bool Text(JToken token, int maximum) => token?.Type == JTokenType.String && ((string)token).Length <= maximum;
    private static bool TextArray(JToken token, int maximum, int characters) => token is JArray array && array.Count <= maximum &&
        array.All(value => Text(value, characters) && !string.IsNullOrWhiteSpace((string)value));
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static string Bound(string text, int length) => text.Length <= length ? text : text.Substring(0, length);
    private static bool Reject(string value, out string reason) { reason = value; return false; }
}
