using System;
using System.Linq;
using Newtonsoft.Json.Linq;

public static partial class AgentSingingGoalRegression
{
    public static int RunAcceptedRecordingIdentityChecks()
    {
        int before = checks;
        using (var f = new Fixture())
        {
            const string words = "这是第三段。现在请把这一段唱给我听。";
            f.Begin(words);
            f.StageCompletedEvidence(words, "第三段的歌声", .7f, 3f, 2.4f, 2.8f);
            Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
            Call(f.chat, "BindAcceptedRecordingInput", true);
            var identity = (JObject)Call(f.chat, "RecordingInputIdentity");
            int acceptedCapture = (int)identity["capture"];
            string acceptedClip = (string)identity["clip"];
            Check(acceptedCapture > 0 && !string.IsNullOrEmpty(acceptedClip), "Accepted audio source was not frozen.");
            var history = (JArray)Call(f.chat, "AcceptedRecordingInputHistory");
            Check(history.Count == 1 && (string)history[0]["text"] == words &&
                (string)history[0]["clip"] == acceptedClip, "Audio binding duplicated or truncated the accepted input event.");
            Call(f.chat, "FastLoopCurrentUserText", words);
            var fastBefore = new JObject(); Call(f.chat, "EnrichFastLoopContext", fastBefore);
            Check((string)fastBefore["inputObservation"]["acoustic"]["clipRef"] == acceptedClip,
                "Fast-loop input does not use the accepted audio snapshot.");

            // c19/c34: a noise candidate starts while a correct decision is in flight.
            f.sense.BeginLiveRecordingCandidateSession();
            Check(f.sense.InputCaptureSessionSerial != acceptedCapture, "Fixture did not advance live capture.");
            Check(JToken.DeepEquals(identity, (JObject)Call(f.chat, "RecordingInputIdentity")),
                "Unaccepted live microphone onset invalidated a pending request's identity.");
            var fastAfter = new JObject(); Call(f.chat, "EnrichFastLoopContext", fastAfter);
            Check((string)fastAfter["inputObservation"]["acoustic"]["clipRef"] == acceptedClip,
                "Noise onset removed frozen Fast-loop audio evidence.");
            Check(((string)Call(f.chat, "BuildFirstReplyContext", "")).Contains(acceptedClip),
                "First reply silently lost its original recording after noise onset.");
            identity["capture"] = -123; history[0]["text"] = "mutated caller copy";
            Check((int)((JObject)Call(f.chat, "RecordingInputIdentity"))["capture"] == acceptedCapture &&
                (string)((JArray)Call(f.chat, "AcceptedRecordingInputHistory"))[0]["text"] == words,
                "A consumer changed the immutable accepted source/history.");

            // Same recognized words in a later capture remain a candidate until
            // the actual accepted-user boundary, not a string-based replacement.
            f.StageCompletedEvidence(words, "第三段的新录音", .7f, 3f, 2.4f, 2.8f);
            Call(f.chat, "RetainCompletedRecordingEvidence", f.sense);
            Check((int)((JObject)Call(f.chat, "RecordingInputIdentity"))["capture"] == acceptedCapture,
                "Retained but unaccepted same-text audio replaced the original source.");
            f.Begin(words); Call(f.chat, "BindAcceptedRecordingInput", true);
            var next = (JObject)Call(f.chat, "RecordingInputIdentity");
            Check((int)next["capture"] != acceptedCapture && (string)next["clip"] != acceptedClip,
                "A newly accepted equal-text recording inherited the old source.");
            Check(((JArray)Call(f.chat, "AcceptedRecordingInputHistory")).Count == 2,
                "A real repeated utterance was de-duplicated from accepted history.");
            f.Begin(words);
            var typed = (JObject)Call(f.chat, "RecordingInputIdentity");
            Check((int)typed["capture"] == 0 && typed["clip"].Type == JTokenType.Null,
                "Typed equal text borrowed audio from the previous turn.");
            Check(((JObject)Call(f.chat, "AcceptedCurrentRecordingFacts")) == null,
                "Typed input retained an earlier current-recording claim.");
            Call(f.chat, "ClearSingingGoalSession");
            Check(((JArray)Call(f.chat, "AcceptedRecordingInputHistory")).Count == 0,
                "Clearing the session retained accepted recording history.");
        }
        return checks - before;
    }
}
