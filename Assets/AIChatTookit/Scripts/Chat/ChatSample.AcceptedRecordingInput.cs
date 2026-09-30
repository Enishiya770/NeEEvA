using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    // Capture candidates are mutable microphone state. Accepted user input is
    // a distinct transaction and remains stable throughout asynchronous work.
    private long m_AcceptedRecordingInputRevision;
    private int m_AcceptedRecordingInputTurn = -1;
    private string m_AcceptedRecordingInputText;
    private SenseVoiceSpeechToText m_AcceptedRecordingInputProvider;
    private ConversationTrace m_AcceptedRecordingInputTrace;
    private JObject m_AcceptedRecordingIdentity, m_AcceptedRecordingFacts, m_AcceptedAudioFacts;
    private readonly List<JObject> m_AcceptedRecordingInputs = new List<JObject>();

    private void ResetAcceptedRecordingInput()
    {
        m_AcceptedRecordingInputRevision++;
        m_AcceptedRecordingInputTurn = m_SingingGoalTurn;
        m_AcceptedRecordingInputText = m_LastUserMsg ?? "";
        m_AcceptedRecordingInputProvider = null;
        m_AcceptedRecordingInputTrace = null;
        m_AcceptedRecordingIdentity = EmptyAcceptedRecordingIdentity();
        m_AcceptedRecordingFacts = m_AcceptedAudioFacts = null;
        RememberAcceptedRecordingInput();
    }

    private JObject EmptyAcceptedRecordingIdentity() => new JObject {
        ["turn"] = m_SingingGoalTurn, ["capture"] = 0, ["clip"] = null };

    private bool AcceptedRecordingInputCurrent =>
        m_AcceptedRecordingInputTurn == m_SingingGoalTurn &&
        string.Equals(m_AcceptedRecordingInputText, m_LastUserMsg, StringComparison.Ordinal) &&
        (m_AcceptedRecordingInputProvider == null ||
            ReferenceEquals(m_AcceptedRecordingInputProvider, m_ChatSettings?.m_SpeechToText));

    // Called synchronously at the accepted-input entry, never during a model
    // callback or in response to an unconfirmed microphone onset.
    private void BindAcceptedRecordingInput(bool hasAudioEvidence)
    {
        m_CurrentUserInputHasAudioEvidence = hasAudioEvidence;
        if (!AcceptedRecordingInputCurrent) ResetAcceptedRecordingInput();
        m_AcceptedRecordingInputTrace = hasAudioEvidence ? m_PendingInputTrace : null;
        m_AcceptedRecordingInputProvider = null;
        m_AcceptedRecordingIdentity = EmptyAcceptedRecordingIdentity();
        m_AcceptedRecordingFacts = m_AcceptedAudioFacts = null;
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (hasAudioEvidence && sense != null &&
            string.Equals(m_LastUserMsg, (sense.LastText ?? "").Trim(), StringComparison.Ordinal))
        {
            var value = sense.DescribeRetainedRecordingForFinalInput(sense.LastText);
            var recording = value == null ? null : JObject.FromObject(value);
            if (recording != null && (int?)recording["captureSessionSerial"] == sense.InputCaptureSessionSerial)
            {
                m_AcceptedRecordingInputProvider = sense;
                m_AcceptedRecordingFacts = (JObject)recording.DeepClone();
                m_AcceptedRecordingIdentity["capture"] = recording["captureSessionSerial"].DeepClone();
                m_AcceptedRecordingIdentity["clip"] = recording["clip"].DeepClone();
                var order = sense.DescribeTimeOrderedTranscriptForFinalInput(sense.LastText);
                var segments = order == null ? null : JObject.FromObject(order)["segments"];
                m_AcceptedRecordingIdentity["timeOrder"] = segments?.DeepClone();
                m_AcceptedAudioFacts = new JObject {
                    ["captureSessionSerial"] = recording["captureSessionSerial"].DeepClone(),
                    ["clipRef"] = recording["clip"].DeepClone(),
                    ["singingProbability"] = sense.LastSingingProbability,
                    ["pitchStability"] = sense.LastPitchStability,
                    ["modeUncertain"] = sense.LastAcousticModeUncertain,
                    ["melodicIslandSeconds"] = sense.LastSingingIslandSeconds,
                    ["timeOrder"] = order == null ? null : FastLoopPrompt.TimeOrderedObservation(segments as JArray)
                };
            }
        }
        RememberAcceptedRecordingInput();
        m_AcceptedRecordingInputTrace?.Mark("input.accepted_identity", new JObject {
            ["revision"] = m_AcceptedRecordingInputRevision,
            ["turn"] = m_SingingGoalTurn,
            ["capture"] = m_AcceptedRecordingIdentity["capture"].DeepClone(),
            ["clip"] = m_AcceptedRecordingIdentity["clip"].DeepClone(),
            ["hasAudioEvidence"] = m_AcceptedRecordingFacts != null,
            ["scope"] = "accepted-user-only; microphone candidates cannot replace this identity"
        });
    }

    private void RememberAcceptedRecordingInput()
    {
        if (!AcceptedRecordingInputCurrent || string.IsNullOrWhiteSpace(m_LastUserMsg)) return;
        var row = (JObject)m_AcceptedRecordingIdentity.DeepClone();
        row["text"] = m_AcceptedRecordingInputText;
        row["revision"] = m_AcceptedRecordingInputRevision;
        int existing = m_AcceptedRecordingInputs.FindIndex(x => (long?)x["revision"] == m_AcceptedRecordingInputRevision);
        if (existing >= 0) m_AcceptedRecordingInputs[existing] = row;
        else m_AcceptedRecordingInputs.Add(row);
    }

    private JArray AcceptedRecordingInputHistory() => new JArray(
        m_AcceptedRecordingInputs.ConvertAll(x => x.DeepClone()));

    private JObject AcceptedCurrentRecordingFacts() =>
        AcceptedRecordingInputCurrent && m_CurrentUserInputHasAudioEvidence
            ? m_AcceptedRecordingFacts?.DeepClone() as JObject : null;

    private void ClearAcceptedRecordingInputSession()
    {
        m_AcceptedRecordingInputRevision++;
        m_AcceptedRecordingInputTurn = -1;
        m_AcceptedRecordingInputText = null;
        m_AcceptedRecordingInputProvider = null;
        m_AcceptedRecordingInputTrace = null;
        m_AcceptedRecordingIdentity = m_AcceptedRecordingFacts = m_AcceptedAudioFacts = null;
        m_AcceptedRecordingInputs.Clear();
    }
}
