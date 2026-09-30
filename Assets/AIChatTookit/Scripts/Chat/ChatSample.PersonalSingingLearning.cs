using System;
using UnityEngine;

public partial class ChatSample
{
    // This auxiliary path only opens/edits an unconfirmed teaching draft. It never
    // consumes the conversation, executes a singing action, or waits on another LLM.
    private string StagePersonalSingingTeaching(string utterance, bool fromAudio)
    {
        try
        {
            var learner = GetComponent<PersonalSingingLearningClient>();
            if (learner == null || !learner.isActiveAndEnabled ||
                !learner.TryApplySpokenCorrection(utterance, fromAudio, out string receipt)) return null;
            var presentation = GetComponent<NeEEvA.Presentation.CompanionPresentation>();
            if (presentation != null) presentation.OpenVoiceTeaching();
            // An observed outcome is transient context, not a fabricated user utterance
            // or a permanent promise that the voice classifier has already learned.
            return "[本轮声音教学界面观测：" + (receipt ?? "已打开待检查的声音判断。") +
                " 这只是待用户检查的候选；用户还未点确认，不得说已经记住、完成训练或生效。自然继续回应用户。]";
        }
        catch (Exception error)
        {
            Debug.LogWarning("[PersonalLearning] 教学候选未能打开；普通对话继续：" + error.Message, this);
            return null;
        }
    }
}
