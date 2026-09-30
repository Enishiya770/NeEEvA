using System;
using System.Linq;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private SingingSessionState m_SingingSession = new SingingSessionState();
    private SenseVoiceSpeechToText m_SingingSessionProvider;

    private JObject SingingSessionInput(JObject input = null)
    {
        input = input ?? RecordingInputIdentity();
        // Ownership uses stable identities, never Single-valued ASR timestamps
        // that change JSON numeric representation across a model round-trip.
        return new JObject { ["turn"] = input["turn"]?.DeepClone(),
            ["capture"] = input["capture"]?.DeepClone(), ["clip"] = input["clip"]?.DeepClone(),
            ["acceptedRevision"] = m_AcceptedRecordingInputRevision };
    }

    private void ClearSingingSession()
    {
        m_SingingSession = new SingingSessionState();
        m_SingingSessionProvider = null;
    }

    private JObject BuildSingingSessionFacts()
    {
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (m_SingingSessionProvider != null && !ReferenceEquals(m_SingingSessionProvider, sense)) ClearSingingSession();
        m_SingingSessionProvider = sense;
        bool bound = FastLoopInputFactsCurrent;
        JObject current = null;
        if (bound && m_FastLoopAcceptedAudioFacts != null && m_FastLoopInputFactsCapture > 0)
        {
            string clip = (string)m_FastLoopAcceptedAudioFacts["clipRef"];
            if (!string.IsNullOrEmpty(clip)) current = new JObject { ["clip"] = clip,
                ["capture"] = m_FastLoopInputFactsCapture,
                ["transcript"] = FastLoopPrompt.ObservationText(m_LastUserMsg, 640),
                ["timeOrder"] = m_FastLoopAcceptedAudioFacts["timeOrder"]?.DeepClone(),
                ["scope"] = "This accepted capture is retained. It is a candidate for semantic observation, not automatically a singing take." };
        }
        bool enabled = bound && (m_SingingSession.HasState || m_FastLoopRecentSingingObservations.Count > 0 ||
            FastLoopPrompt.HasSingingObservation(m_FastLoopAcceptedAudioFacts?["timeOrder"]));
        return m_SingingSession.Facts(SingingSessionInput(), current, enabled);
    }

    private JObject BuildSingingSessionBinding(JObject input) =>
        m_SingingSession.Facts(SingingSessionInput(input), null, m_SingingSession.HasState);

    private JObject BuildActorSingingSessionFacts()
    {
        if (!m_SingingSession.HasState) return null;
        var facts = m_SingingSession.Facts(SingingSessionInput(), null, true);
        var focus = facts["focus"] as JObject;
        return new JObject {
            ["revision"] = facts["revision"].DeepClone(),
            ["focus"] = focus == null ? null : new JObject {
                ["take"] = focus["take"]?.DeepClone(), ["capture"] = focus["capture"]?.DeepClone(),
                ["status"] = focus["status"]?.DeepClone(), ["transcript"] = focus["transcript"]?.DeepClone(),
                ["introduction"] = focus["introduction"]?.DeepClone() },
            ["currentRequest"] = FastLoopInputFactsCurrent ? facts["binding"]?.DeepClone() : null,
            ["scope"] = "Accepted source references only. currentRequest is the current user's source request, not the character's adopted intention. " +
                "Use it to resolve references only when the current draft adopts that request; an older recordingReferent does not replace this current binding. " +
                "A refusal, praise or observation does not become an action because a user request exists. This is not source/range approval or a playback receipt."
        };
    }

    // Called after task-control review, before any public speech/action handoff.
    // A return value of true means this bounded state operation supplied its own
    // factual receipt. Otherwise the role's separate route remains authoritative.
    private bool TryApplySingingSessionUpdate(FastLoopTurnContext turn, FastLoopDecision decision,
        string currentUser, ConversationTrace trace)
    {
        var update = decision?.SessionUpdate;
        if (update == null || update.Operation == "none") return false;
        if (!FastLoopTurnCurrent(turn)) return true;
        if (!FastLoopInputFactsCurrent || !string.Equals(currentUser, m_LastUserMsg, StringComparison.Ordinal))
        { decision.SessionUpdate = null; return false; }
        var input = SingingSessionInput();
        bool accepted = m_SingingSession.Apply(update, input, currentUser, out var receipt);
        decision.SessionUpdate = null; // Re-entering this same decision cannot apply it twice.
        bool terminal = update.Operation == "cancel" || update.Operation == "delete_take";
        if (accepted && terminal) ApplySingingSessionTaskCancellation(update.Operation == "delete_take" ? update.Take : null);
        trace?.Mark("singing_session.update", receipt);
        // Trace observers may accept a new input or replace the output owner.
        if (!FastLoopTurnCurrent(turn) || !JToken.DeepEquals(input, SingingSessionInput())) return true;
        // An optional event hypothesis can lose its session revision while the
        // ordinary role response is still current. Do not replace that response.
        if (!accepted) return false;
        if (terminal)
        {
            string language = decision.Language;
            string text = update.Operation == "delete_take"
                    ? language == "ja" ? "その録音を今回の歌唱候補から外しました。元の音声は保管されています。" : language == "en" ? "I removed that take from this singing session. The original audio is retained." : "已把这份录音从本次歌唱候选中移除，原始音频仍保留。"
                    : language == "ja" ? "歌唱の保留中の作業を取り消しました。録音は残しています。" : language == "en" ? "I cancelled the pending singing task. The recordings are retained." : "已取消待处理的歌唱任务，录音仍保留。";
            CompleteFastLoopReply(turn.Model, turn.Prompt, text, language, turn.Generation, turn.RecordHistory);
            return true;
        }
        return false;
    }

    private bool ValidateSingingSessionSources(string refs, out string reason)
    {
        reason = null;
        var input = SingingSessionInput();
        foreach (string clip in (refs ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
            if (!m_SingingSession.Allows(clip, input, out reason)) return false;
        return true;
    }

    private void RecordSingingSessionPlayback(string refs, int requestTurn, bool completed) =>
        m_SingingSession.PlaybackOutcome(refs, requestTurn, completed);

    private bool RejectSingingSessionBinding(string refs, string reason) =>
        m_SingingSession.RejectBinding(SingingSessionInput(), refs, reason);
}
