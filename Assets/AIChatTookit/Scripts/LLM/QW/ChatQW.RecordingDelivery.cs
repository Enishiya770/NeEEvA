using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

public partial class ChatQW
{
    // Local terminal replies have no HTTP stream. They must nevertheless travel
    // through the same typed public-speech boundary before completion is signalled.
    private void CompleteRecordingLocalReply(string reply, int generation, bool recordHistory,
        Action<string> onDelta, Action<SpeechText> onSpeech, Action<string> onComplete,
        ConversationTrace trace)
    {
        if (generation != m_StreamRequestGeneration) return;
        var parts = new List<SpeechText>();
        var channels = new RoleOutputChannels(parts.Add, RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
        channels.Push(reply); channels.Finish();
        if (channels.HasInvalidPublicBoundary || channels.HasInvalidSpeechPhase ||
            channels.HasInvalidLanguage || channels.HasUndeclaredSpeech || channels.HasMalformedTool ||
            channels.HasActions || !channels.HasSpeech)
            throw new InvalidOperationException("Invalid program-authored recording reply.");

        var receipt = new PublicSpeechStreamReceipt();
        foreach (var part in parts)
        {
            if (generation != m_StreamRequestGeneration) return;
            if (part.SuppressTts || string.IsNullOrWhiteSpace(part.Text)) continue;
            if (onSpeech != null) onSpeech(part);
            else onDelta?.Invoke(part.Text);
            receipt.Record(part.Text, part.ActionDependency);
        }
        if (generation != m_StreamRequestGeneration) return;
        ReportRoleOutputFormatFactsWithReceipt(channels, true, receipt);
        if (generation != m_StreamRequestGeneration) return;
        if (recordHistory) m_DataList.Add(new SendData("assistant", reply));
        trace?.Mark("recording_decision.local_reply_forwarded", new JObject {
            ["characters"] = channels.Speech.Length, ["segments"] = parts.Count,
            ["scope"] = "Validated public text forwarded before completion; not proof of playback or task completion"
        });
        if (generation == m_StreamRequestGeneration) onComplete?.Invoke(reply);
    }

    private const string RecordingBudgetReply = "<lang code=\"zh\"/><speech mode=\"independent\"/><say>这次要核对的录音资料超过了当前处理范围，演唱还没有开始。我还需要缩小核对范围，才能继续处理。</say>";
}
