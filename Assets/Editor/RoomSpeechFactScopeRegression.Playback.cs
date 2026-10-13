using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static partial class RoomSpeechFactScopeRegression
{
    private static IEnumerator RunMovementPlaybackChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel model)
    {
        yield return PrepareRoomPlanning(chat, bridge, "来我身边。");
        chat.StopAllCoroutines();
        int generation = (int)Get(chat, "m_FormalResponseGeneration");
        var chunkType = typeof(ChatSample).GetNestedType("PendingSpeechChunk", BindingFlags.NonPublic);
        object oldId = Get(bridge, "actionId"), oldPhase = Get(bridge, "phase"), oldReason = Get(bridge, "reason"),
            oldDiagnostic = Get(bridge, "latestDiagnostic"), oldTarget = Get(bridge, "executionTargetKind");
        var settings = (ChatSetting)Get(chat, "m_ChatSettings");
        var oldTts = settings.m_TextToSpeech;
        var tts = chat.gameObject.AddComponent<RoomMovementPlaybackGateTTS>();
        var rows = new JArray();
        void Phase(string phase) { Set(bridge, "actionId", "playback-owned"); Set(bridge, "executionTargetKind", "user"); Call(bridge, "SetFact", phase, "controlled playback transition"); }
        object Chunk(object lease, string text)
        {
            var item = Activator.CreateInstance(chunkType, true);
            Set(item, "RoundId", (int)Get(chat, "m_CurrentSpeechRoundId")); Set(item, "Text", text);
            Set(item, "LanguageCode", "zh"); Set(item, "RoomState", lease); Set(item, "RoomStateVersion", (int)Get(lease, "version"));
            return item;
        }
        LLM.SendData Bind()
        {
            Phase("moving"); Set(chat, "m_RoomTaskIntent", "approach"); Set(chat, "m_RoomTaskActionId", "playback-owned");
            var history = new LLM.SendData("assistant", "我正在尝试走近你。"); model.m_DataList.Add(history);
            Call(chat, "BindRoomSpeechPlayback", generation, "zh", bridge, model, history);
            Require(Get(chat, "m_RoomSpeechPlayback") != null, "Owned movement installs a playback lease.");
            return history;
        }
        try
        {
            int operations = bridge.Controller.OperationRevision;
            int assessments = (int)Get(bridge, "assessmentBuildCount");
            foreach (string language in new[] { "zh", "ja", "en" })
            {
                var facts = new JObject { ["execution"] = new JObject { ["actionId"] = "arrived-owned", ["targetKind"] = "user", ["phase"] = "arrived" },
                    ["currentFacts"] = new JObject { ["currentlyNearAndFacingUser"] = true, ["userTrackingValid"] = true,
                        ["userSupportVerified"] = true, ["actualUserDistance"] = 1.2, ["desiredUserDistance"] = 1.2, ["actualFacingErrorDegrees"] = 1.0 } };
                var pose = (JObject)facts["currentFacts"];
                foreach (string relation in new[] { "near-facing", "distance-changed", "facing-changed", "tracking-unavailable", "support-unverified" })
                {
                    pose["currentlyNearAndFacingUser"] = relation == "near-facing";
                    pose["actualUserDistance"] = relation == "distance-changed" ? 2.0 : 1.2;
                    pose["actualFacingErrorDegrees"] = relation == "facing-changed" ? 35 : 1;
                    pose["userTrackingValid"] = relation != "tracking-unavailable";
                    pose["userSupportVerified"] = relation != "support-unverified";
                    Require(RoomMovementResultPolicy.ArrivalRelation(facts) == relation, "Separate current pose dimensions from historical arrival: " + relation);
                    Require((string)RoomMovementResultPolicy.Requirement(facts, "approach", "arrived-owned")["expectedReport"] == "arrived",
                        "Review retains confirmed arrival after current pose/tracking changes: " + relation);
                    string text = (string)Call(chat, "MovementProgressText", facts, "approach", "arrived-owned", language, false);
                    Require(text.Contains(language == "ja" ? "着いた" : language == "en" ? "reached" : "到你身边") &&
                        (string)facts.SelectToken("execution.phase") == "arrived", "Every pose transition preserves actual completed arrival: " + language + "/" + relation);
                    rows.Add(new JObject { ["arrivalRelation"] = relation, ["language"] = language, ["text"] = text });
                }
                Require(Call(chat, "MovementProgressText", facts, "approach", "replacement-owner", language, false) == null,
                    "Historical arrival cannot cross action ownership.");
            }
            // Real Unity transforms and live receipt reader, without a route or
            // action dispatch. The completion timestamp stays immutable.
            Vector3 headBefore = bridge.userHead.position;
            try {
                var poseHistory = Bind(); Phase("arrived");
                var poseLease = Get(chat, "m_RoomSpeechPlayback"); var poseChunk = Chunk(poseLease, poseHistory.content);
                Call(chat, "RefreshRoomSpeechForPlayback", poseChunk, generation);
                var arrival = bridge.ReadMovementSpeechReceipt();
                Vector3 away = bridge.userHead.position - bridge.avatar.transform.position; away.y = 0;
                bridge.userHead.position += (away.sqrMagnitude > .001f ? away.normalized : Vector3.right) * 2f;
                var moved = bridge.ReadMovementSpeechReceipt();
                Require(moved.phase == "arrived" && moved.arrivalObservedUtc == arrival.arrivalObservedUtc &&
                    moved.actualUserDistance > arrival.actualUserDistance + 1.5f &&
                    bridge.Controller.OperationRevision == operations && (int)Get(bridge, "assessmentBuildCount") == assessments,
                    "User movement changes measured distance without erasing completion or planning another route.");
                rows.Add(new JObject { ["liveUserMove"] = true, ["arrivalObservedUtc"] = moved.arrivalObservedUtc,
                    ["beforeDistance"] = arrival.actualUserDistance, ["afterDistance"] = moved.actualUserDistance,
                    ["afterPhase"] = moved.phase, ["supportVerified"] = moved.userSupportVerified });
                Call(chat, "FinishRoomSpeechPlaybackHistory", "");
            }
            finally { bridge.userHead.position = headBefore; }
            foreach (string phase in new[] { "preparing", "follow-paused", "follow-recovering", "follow-waiting", "failed", "cancelled", "arrived" })
            {
                var history = Bind(); var lease = Get(chat, "m_RoomSpeechPlayback"); var chunk = Chunk(lease, history.content);
                var late = Chunk(lease, history.content); Phase(phase);
                Require(Call(chat, "RefreshRoomSpeechForPlayback", chunk, generation).ToString() == "Replaced" &&
                    history.content == (string)Get(chunk, "Text") && history.content != "我正在尝试走近你。",
                    "Queued speech follows actual execution at playback: " + phase);
                Require(Call(chat, "RefreshRoomSpeechForPlayback", late, generation).ToString() == "Drop",
                    "Previously synthesized clip or late callback cannot play an old version.");
                Require(Call(chat, "RefreshRoomSpeechForPlayback", chunk, generation).ToString() == "Current",
                    "An unchanged current receipt does not synthesize repeatedly.");
                rows.Add(new JObject { ["phase"] = phase, ["text"] = history.content });
                Call(chat, "FinishRoomSpeechPlaybackHistory", "");
                Require(!model.m_DataList.Contains(history), "Unplayed receipt is removed from dialogue history.");
            }
            foreach (string language in new[] { "zh", "ja", "en" })
            {
                var history = Bind(); Phase("preparing");
                Call(chat, "BindRoomSpeechPlayback", generation, language, bridge, model, history);
                var lease = Get(chat, "m_RoomSpeechPlayback");
                Set(lease, "prefix", "Independent result. ");
                var chunk = Chunk(lease, history.content); var late = Chunk(lease, history.content);
                foreach (string phase in new[] { "moving", "returning", "arrived" })
                {
                    Phase(phase);
                    Require(Call(chat, "RefreshRoomSpeechForPlayback", chunk, generation).ToString() == "Replaced",
                        "Latest recorded phase transition updates queued speech: " + phase);
                }
                string arrival = language == "ja" ? "さっきあなたのそばに着いたわ。" : language == "en" ?
                    "I reached your side a moment ago." : "刚才已经到你身边了。";
                Require((int)Get(lease, "refreshes") == 3 && (bool)Get(lease, "neutral") &&
                    ((string)Get(chunk, "Text")).EndsWith(arrival) && ((string)Get(chunk, "Text")).StartsWith("Independent result."),
                    "Third refresh keeps definite arrival and independent outcome, without inventing uncertainty: " + language);
                Vector3 originalHead = bridge.userHead.position;
                try {
                    bridge.userHead.position += Vector3.right * 3;
                    Require(Call(chat, "RefreshRoomSpeechForPlayback", chunk, generation).ToString() == "Current" &&
                        (int)Get(lease, "refreshes") == 3, "Stable past arrival does not resynthesize when the user moves after completion.");
                } finally { bridge.userHead.position = originalHead; }
                Require(Call(chat, "RefreshRoomSpeechForPlayback", late, generation).ToString() == "Drop",
                    "A late clip cannot restore obsolete pre-arrival wording.");
                rows.Add(new JObject { ["terminalAtRefreshCap"] = true, ["language"] = language, ["text"] = Get(chunk, "Text").ToString() });
                Call(chat, "FinishRoomSpeechPlaybackHistory", "");
            }
            var churnHistory = Bind(); var churn = Get(chat, "m_RoomSpeechPlayback"); var churnChunk = Chunk(churn, churnHistory.content);
            Call(bridge, "SetFact", "moving", "same movement, updated progress diagnostic");
            Require(Call(chat, "RefreshRoomSpeechForPlayback", churnChunk, generation).ToString() == "Current",
                "Progress wording alone cannot invalidate truthful movement speech or trigger synthesis churn.");
            Phase("preparing"); Call(chat, "RefreshRoomSpeechForPlayback", churnChunk, generation);
            int meaningfulChanges = (int)Get(churn, "refreshes");
            Phase("generating");
            Require(Call(chat, "RefreshRoomSpeechForPlayback", churnChunk, generation).ToString() == "Current" &&
                (int)Get(churn, "refreshes") == meaningfulChanges,
                "Preparing and generating say the same thing and cannot consume the replacement budget.");
            foreach (string phase in new[] { "moving", "returning" })
            { Phase(phase); Call(chat, "RefreshRoomSpeechForPlayback", churnChunk, generation); }
            Require((int)Get(churn, "refreshes") == 3 && (bool)Get(churn, "neutral"), "Rapid phase changes have a bounded synthesis replacement count.");
            Phase("goal-not-reached");
            Require(Call(chat, "RefreshRoomSpeechForPlayback", churnChunk, generation).ToString() == "Replaced" &&
                ((string)Get(churnChunk, "Text")).Contains("没有达到要求") &&
                !((string)Get(churnChunk, "Text")).Contains("暂时不能确认"),
                "The latest production failure replaces neutral progress before audio starts.");
            Require(Call(chat, "RefreshRoomSpeechForPlayback", churnChunk, generation).ToString() == "Current" &&
                (int)Get(churn, "refreshes") == 4, "A settled terminal receipt does not repeatedly synthesize.");
            Set(bridge, "actionId", "new-owner"); Call(bridge, "SetFact", "moving", "replacement fixture");
            Require(Call(chat, "RefreshRoomSpeechForPlayback", churnChunk, generation).ToString() == "Drop",
                "Even a phase-neutral receipt is revoked when another action owns the body.");
            Call(chat, "FinishRoomSpeechPlaybackHistory", "");

            foreach (string language in new[] { "zh", "ja", "en" })
            {
                Bind(); Phase("follow-waiting"); Set(chat, "m_RoomTaskIntent", "follow");
                var history = new LLM.SendData("assistant", "already heard waiting statement"); model.m_DataList.Add(history);
                Call(chat, "BindRoomSpeechPlayback", generation, language, bridge, model, history);
                var lease = Get(chat, "m_RoomSpeechPlayback");
                Call(chat, "RecordRoomSpeechPlayed", lease, "already heard waiting statement");
                var chunk = Chunk(lease, "obsolete remaining waiting statement"); Phase("moving");
                Require(Call(chat, "RefreshRoomSpeechForPlayback", chunk, generation).ToString() == "Replaced" &&
                    ((string)Get(chunk, "Text")).Contains(language == "zh" ? "现在已经开始" : language == "ja" ? "今は" : "now"),
                    "Heard waiting followed by movement has an explicit temporal transition: " + language);
                rows.Add(new JObject { ["transitionLanguage"] = language, ["text"] = Get(chunk, "Text").ToString() });
                Call(chat, "FinishRoomSpeechPlaybackHistory", "");
            }

            // Exercise the production streaming player. The controlled TTS holds
            // PCM before Play, then asks the actual gate after Unity state changes.
            settings.m_TextToSpeech = tts;
            var gateHistory = Bind(); var gateLease = Get(chat, "m_RoomSpeechPlayback");
            var queue = Get(chat, "m_PendingChunks"); Call(queue, "Clear"); Call(Get(chat, "m_PendingClips"), "Clear");
            Call(queue, "Enqueue", Chunk(gateLease, gateHistory.content));
            Set(chat, "m_StreamComplete", true); Set(chat, "m_LatencyFillerPlayed", false);
            chat.StartCoroutine((IEnumerator)Call(chat, "StreamDirectTTSPlayer", generation));
            yield return Wait(() => tts.Requests.Count == 1, "Movement streaming TTS gate is pending");
            Phase("cancelled");
            Require(tts.Gate() == TTS.StreamingPlaybackPermission.Cancel,
                "State changed while synthesizing: production gate cancels obsolete PCM before Play.");
            Require(tts.Gate() == TTS.StreamingPlaybackPermission.Cancel,
                "A canceled synthesis stays canceled if the provider polls its gate again.");
            tts.Complete(false, tts.Requests[0], 0);
            yield return Wait(() => tts.Requests.Count == 2, "Production player resynthesizes the current receipt");
            Require(tts.Requests[1].Contains("取消") && !tts.Requests[1].Contains("走近") &&
                tts.Gate() == TTS.StreamingPlaybackPermission.Play && bridge.Controller.OperationRevision == operations &&
                (int)Get(bridge, "assessmentBuildCount") == assessments,
                "One replacement reports cancellation without another movement, route assessment or model request.");
            chat.StopAllCoroutines();
            Call(chat, "FinishRoomSpeechPlaybackHistory", "用户实际听到的片段");
            Require(gateHistory.content == "用户实际听到的片段", "Playback history stores heard text, not superseded generation text.");
            LastDecisionFailureReport["movementPlayback"] = new JObject { ["status"] = "passed", ["phases"] = rows,
                ["boundedRefreshes"] = 3, ["streamingGateRequests"] = tts.Requests.Count,
                ["scope"] = "Actual player/gate and Unity diagnostic producer, controlled state transitions and held synthesis; no claim of audible speaker or real locomotion testing." };
        }
        finally
        {
            chat.StopAllCoroutines(); settings.m_TextToSpeech = oldTts; UnityEngine.Object.DestroyImmediate(tts);
            Call(chat, "FinishRoomSpeechPlaybackHistory", "");
            Call(Get(chat, "m_PendingChunks"), "Clear"); Call(Get(chat, "m_PendingClips"), "Clear");
            Set(bridge, "actionId", oldId); Set(bridge, "phase", oldPhase); Set(bridge, "reason", oldReason);
            Set(bridge, "latestDiagnostic", oldDiagnostic); Set(bridge, "executionTargetKind", oldTarget);
        }
    }
}

public sealed class RoomMovementPlaybackGateTTS : TTS
{
    public readonly List<string> Requests = new List<string>();
    public Func<StreamingPlaybackPermission> Gate;
    public Action<bool, string, float> Complete;
    public override bool SupportsStreamingPlayback => true;
    public override void SpeakStreamingWithPlaybackGate(SpeechText speech, AudioSource output, Action<string> started,
        Action<bool, string, float> completed, Func<StreamingPlaybackPermission> gate)
    { Requests.Add(speech.Text); Gate = gate; Complete = completed; }
}
