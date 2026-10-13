using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private enum RoomPlaybackCheck { Current, Replaced, Drop }

    // Attached by the audited producer, never inferred from words or a model tag.
    // A version also invalidates already synthesized clips and late callbacks.
    private sealed class RoomSpeechPlaybackLease
    {
        public int generation, userTurn, workEpoch, roundId, version, refreshes, heardStart, prefixCursor;
        public string actionId, intent, language, failureNotice, prefix = "", played = "";
        public bool neutral, cancelled, compound, progressHeard;
        public ArdyRoomDialogueBridge bridge;
        public UnityEngine.Object avatar, environment;
        public JObject state;
        public LLM model;
        public LLM.SendData history;
    }
    private RoomSpeechPlaybackLease m_RoomSpeechPlayback;

    private static JObject ReadMovementPlaybackFacts(ArdyRoomDialogueBridge bridge)
    {
        var value = bridge != null ? bridge.ReadMovementSpeechReceipt() : null;
        return value == null ? new JObject() : new JObject {
            ["execution"] = new JObject { ["actionId"] = value.actionId, ["targetKind"] = value.targetKind,
                ["phase"] = value.phase, ["reason"] = value.reason, ["operationRevision"] = value.operationRevision,
                ["approachRetargetStatus"] = value.approachRetargetStatus,
                ["arrivalObservedUtc"] = value.arrivalObservedUtc },
            ["currentFacts"] = new JObject { ["currentlyNearAndFacingUser"] = value.currentlyNearAndFacingUser,
                ["userTrackingValid"] = value.userTrackingValid, ["userSupportVerified"] = value.userSupportVerified,
                ["actualUserDistance"] = value.actualUserDistance, ["actualFacingErrorDegrees"] = value.actualFacingErrorDegrees,
                ["desiredUserDistance"] = value.desiredUserDistance }
        };
    }

    private static JObject MovementPlaybackStamp(JObject room, string intent, string actionId)
    {
        var execution = room?["execution"] as JObject;
        if (string.IsNullOrEmpty(actionId) || (string)execution?["actionId"] != actionId ||
            (string)execution["targetKind"] != "user") return null;
        return new JObject {
            ["actionId"] = actionId, ["phase"] = execution["phase"]?.DeepClone(),
            ["operationRevision"] = execution["operationRevision"]?.DeepClone(),
            ["reportText"] = RoomMovementResultPolicy.Fallback(room, intent, actionId),
            ["arrivalRelation"] = intent == "approach" && (string)execution["phase"] == "arrived"
                ? RoomMovementResultPolicy.ArrivalRelation(room) : null,
            ["expectedReport"] = intent == "stop-moving" && new[] { "stopped", "cancelled", "idle" }.Contains((string)execution["phase"])
                ? new JValue("stopped") : RoomMovementResultPolicy.Requirement(room, intent, actionId)["expectedReport"]?.DeepClone()
        };
    }

    private static bool SameMovementReport(JObject before, JObject after) => JToken.DeepEquals(
        PickRoomFields(before, "actionId", "reportText", "arrivalRelation", "expectedReport"),
        PickRoomFields(after, "actionId", "reportText", "arrivalRelation", "expectedReport"));

    private void BindRoomSpeechPlayback(int generation, string language, ArdyRoomDialogueBridge bridge,
        LLM model, LLM.SendData history)
    {
        m_RoomSpeechPlayback = null;
        if (bridge == null) return;
        bool compound = m_RequestProgressSpeechGeneration == generation && m_RequestProgressSpeechPrefix != null;
        var body = compound ? BuildRoomRequestProgress()?["body"] as JObject : null;
        string intent = compound ? (string)body?["intent"] : m_RoomTaskIntent;
        string action = compound ? (string)body?["actionId"] : m_RoomTaskActionId;
        if (intent != "approach" && intent != "follow" && (!compound || intent != "stop-moving")) return;
        var state = compound ? m_RequestProgressSpeechState : MovementPlaybackStamp(ReadMovementPlaybackFacts(bridge), intent, action);
        if (state == null) return;
        language = SpatialGoalReportLanguage(language);
        m_RoomSpeechPlayback = new RoomSpeechPlaybackLease {
            generation = generation, userTurn = m_ActionConstraints.CurrentUserTurn, workEpoch = m_WorkEpoch,
            roundId = m_CurrentSpeechRoundId, actionId = action, intent = intent, compound = compound,
            prefix = compound ? m_RequestProgressSpeechPrefix : "",
            language = language, bridge = bridge, avatar = bridge.avatar, environment = bridge.environmentRoot,
            state = state, model = model, history = history, heardStart = m_AssistantHeardText.Length,
            failureNotice = CurrentFormalGenerationFailed(generation) ? FormalGenerationFailureNotice(language) : ""
        };
    }

    private bool RoomSpeechLeaseCurrent(RoomSpeechPlaybackLease lease, int generation) => lease != null && !lease.cancelled &&
        generation == m_FormalResponseGeneration && lease.generation == generation && lease.workEpoch == m_WorkEpoch &&
        lease.userTurn == m_ActionConstraints.CurrentUserTurn && lease.bridge != null &&
        lease.bridge == GetComponent<ArdyRoomDialogueBridge>() && lease.bridge.avatar == lease.avatar &&
        lease.bridge.environmentRoot == lease.environment;

    private void RemoveQueuedRoomSpeech(RoomSpeechPlaybackLease lease)
    {
        m_PendingChunks = new Queue<PendingSpeechChunk>(m_PendingChunks.Where(c => c.RoomState != lease));
        var keep = new Queue<PendingSpeechClip>();
        foreach (var clip in m_PendingClips)
            if (clip.RoomState != lease) keep.Enqueue(clip);
            else if (clip.Clip != null) Destroy(clip.Clip);
        m_PendingClips = keep;
        if (lease.roundId == m_CurrentSpeechRoundId) m_SentenceBuffer.Length = 0;
    }

    private RoomPlaybackCheck RefreshRoomSpeechForPlayback(PendingSpeechChunk chunk, int generation)
    {
        var lease = chunk.RoomState;
        if (lease == null) return RoomPlaybackCheck.Current;
        if (!RoomSpeechLeaseCurrent(lease, generation) || chunk.RoomStateVersion != lease.version)
            return RoomPlaybackCheck.Drop;
        if (lease.compound && (!ValidateSpatialGoalTerminalProof(generation) ||
            !RoomRequestProgressOwnerCurrent(lease.actionId, generation)))
        { lease.cancelled = true; RemoveQueuedRoomSpeech(lease); return RoomPlaybackCheck.Drop; }
        var room = ReadMovementPlaybackFacts(lease.bridge);
        var state = MovementPlaybackStamp(room, lease.intent, lease.actionId);
        if (state == null || new[] { "replaced", "disconnected" }.Contains((string)state["phase"]))
        {
            lease.cancelled = true;
            RemoveQueuedRoomSpeech(lease);
            Debug.Log("[Room/SpeechPlaybackRefresh] " + new JObject {
                ["generation"] = generation, ["actionId"] = lease.actionId, ["disposition"] = "dropped-owner-no-longer-current"
            }.ToString(Formatting.None));
            return RoomPlaybackCheck.Drop;
        }
        bool terminal = RoomMovementResultPolicy.IsTerminalFailure((string)state["phase"]) ||
            (string)state["phase"] == "arrived" || (string)state["expectedReport"] == "stopped";
        if (SameMovementReport(lease.state, state) || lease.neutral && !terminal) return RoomPlaybackCheck.Current;
        // Once neutral, live pose changes after a committed arrival must not
        // churn synthesis. A new terminal verdict still overrides intermediate
        // wording, even after the ordinary replacement budget is consumed.
        if (lease.neutral && terminal && (string)lease.state["phase"] == (string)state["phase"])
            return RoomPlaybackCheck.Current;

        var previous = lease.state;
        lease.state = state; lease.version++; lease.refreshes++;
        // Count meaningful speech changes, not preparing/generating or operation
        // revisions. The cap applies to intermediate progress, never a new result.
        lease.neutral = lease.refreshes >= 3;
        string text;
        if (lease.neutral && (string)state["phase"] == "arrived")
            // An immutable completed event is stable across later pose changes.
            // The refresh cap must not invent missing evidence or uncertainty.
            text = lease.language == "ja" ? "さっきあなたのそばに着いたわ。" :
                lease.language == "en" ? "I reached your side a moment ago." : "刚才已经到你身边了。";
        else if (lease.neutral && !terminal)
            text = lease.language == "ja" ? "移動の依頼は受け付けたわ。状況が変わっているので、最終結果はまだ断言できないの。" :
                lease.language == "en" ? "The movement request was accepted. Its state is changing, so I can't confirm a final result yet. " :
                "移动请求已受理，状态还在变化，我暂时不能确认最终结果。";
        else
        {
            text = MovementProgressText(room, lease.intent, lease.actionId, lease.language, lease.progressHeard);
        }
        // Keep every unplayed word of the audited result/answer. A changed body
        // phase replaces only its progress segment, never an independent answer.
        string pendingPrefix = lease.prefix.Substring(lease.prefixCursor).TrimStart();
        if (pendingPrefix.Length > 0) text = pendingPrefix + (lease.language == "en" ? " " : "") + text;
        if (!string.IsNullOrEmpty(lease.failureNotice) && !lease.played.Contains(lease.failureNotice)) text += (lease.language == "en" ? " " : "") + lease.failureNotice;
        RemoveQueuedRoomSpeech(lease);
        chunk.Text = text; chunk.LanguageCode = lease.language; chunk.RoomStateVersion = lease.version;
        if (lease.history != null && lease.model != null && lease.model.m_DataList.Contains(lease.history))
            lease.history.content = lease.played + text;
        var receipt = new JObject {
            ["generation"] = generation, ["userTurn"] = lease.userTurn, ["actionId"] = lease.actionId,
            ["revision"] = lease.version, ["observedUtc"] = DateTime.UtcNow.ToString("o"),
            ["before"] = previous, ["after"] = state.DeepClone(), ["publicText"] = text,
            ["currentRelativePose"] = room["currentFacts"]?.DeepClone(),
            ["arrivalObservedUtc"] = room.SelectToken("execution.arrivalObservedUtc")?.DeepClone(),
            ["phaseNeutral"] = lease.neutral, ["disposition"] = "replace-unplayed-speech",
            ["scope"] = "Playback-boundary text replacement from current owned execution; no new action, model request or proof of audible playback. Previously played words remain historical."
        };
        if ((int?)m_LastFormalReplyReceipt?["generation"] == generation)
            m_LastFormalReplyReceipt["latestPlaybackRevision"] = receipt.DeepClone();
        Debug.Log("[Room/SpeechPlaybackRefresh] " + receipt.ToString(Formatting.None));
        return RoomPlaybackCheck.Replaced;
    }

    private void PrependSpeechChunk(PendingSpeechChunk chunk)
    {
        var queue = new Queue<PendingSpeechChunk>(); queue.Enqueue(chunk);
        foreach (var item in m_PendingChunks) queue.Enqueue(item);
        m_PendingChunks = queue;
        m_TTSSenderDone = false;
    }

    private void RecordRoomSpeechPlayed(RoomSpeechPlaybackLease lease, string text)
    {
        if (lease == null) return;
        // The sentence queue trims inter-sentence whitespace. Track the exact
        // remaining prefix independently of concatenated played chunk lengths.
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character)) continue;
            while (lease.prefixCursor < lease.prefix.Length && char.IsWhiteSpace(lease.prefix[lease.prefixCursor])) ++lease.prefixCursor;
            if (lease.prefixCursor >= lease.prefix.Length) lease.progressHeard = true;
            else if (lease.prefix[lease.prefixCursor] == character) ++lease.prefixCursor;
            else break; // An unexpected transform cannot silently discard unplayed answer text.
        }
        lease.played += text;
        if ((int?)m_LastFormalReplyReceipt?["generation"] == lease.generation)
            m_LastFormalReplyReceipt["completedPlaybackText"] = lease.played;
    }

    private void QueueRoomSpeechTranslation(RoomSpeechPlaybackLease lease, int generation, string text)
    {
        // Only enqueue this changing state after its playback gate has passed.
        if (lease != null && m_SubtitleOverlay != null)
            m_SubtitleOverlay.QueueTranslationChunk(generation, text, true);
    }

    private void FinishRoomSpeechPlaybackHistory(string heard)
    {
        var lease = m_RoomSpeechPlayback;
        if (lease == null) return;
        m_RoomSpeechPlayback = null;
        if (lease.history == null || lease.model == null || !lease.model.m_DataList.Contains(lease.history)) return;
        string spoken = heard != null && heard.Length >= lease.heardStart ? heard.Substring(lease.heardStart) : lease.played;
        if (string.IsNullOrWhiteSpace(spoken)) lease.model.m_DataList.Remove(lease.history);
        else lease.history.content = spoken;
    }
}
