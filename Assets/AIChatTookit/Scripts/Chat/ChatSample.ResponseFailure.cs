public partial class ChatSample
{
    private int m_ResponseFailureGeneration = -1, m_ResponseFailureUserTurn = -1;
    private bool m_ResponseFailureWasDeadline;

    private void HandleResponseGenerationFailure(string reason)
    {
        if (!isActiveAndEnabled || !m_FormalResponseInFlight || m_UserSpeechActiveForAutonomy || m_SceneTimePaused) return;
        // Provider only emits for its still-current request, before completion.
        // Do not reinterpret a response timeout as a failed room/device operation.
        m_ResponseFailureGeneration = m_FormalResponseGeneration;
        m_ResponseFailureUserTurn = m_ActionConstraints.CurrentUserTurn;
        m_ResponseFailureWasDeadline = reason == "no-public-output-deadline" || reason == "total-deadline" || reason == "delta-stalled";
        if (m_RoomSpeechGuardGeneration == m_FormalResponseGeneration) m_RoomTaskFormatError = true;
    }

    private bool CurrentFormalGenerationFailed(int generation) =>
        m_ResponseFailureGeneration == generation && m_ResponseFailureUserTurn == m_ActionConstraints.CurrentUserTurn;

    private string FormalGenerationFailurePrefix(string language) => m_ResponseFailureWasDeadline
        ? language == "ja" ? "返事の生成が時間切れになってしまったわ。" : language == "en"
            ? "Generating my reply timed out. " : "刚才的回复生成超时了。"
        : language == "ja" ? "返事を最後まで生成できなかったわ。" : language == "en"
            ? "My reply could not be completed. " : "刚才没能生成完整的回复。";
}
