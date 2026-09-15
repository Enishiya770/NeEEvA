using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    private int m_WorkRoomTaskEpoch = -1, m_WorkSceneFeedbackGuardGeneration = -1;
    private string m_WorkRoomTaskIntent = "none", m_WorkRoomTaskActionId = "";

    // Called after the structured spatial decision and its preparation have settled.
    // A continuation has a new formal generation but remains in the same actual user-work epoch.
    private void RecordWorkRoomTaskContext()
    {
        m_WorkRoomTaskEpoch = m_WorkEpoch;
        m_WorkRoomTaskIntent = m_RoomTaskIntent;
        m_WorkRoomTaskActionId = m_RoomTaskActionId;
    }

    private bool ShouldGuardWorkSceneFeedback(int generation) =>
        generation == m_FormalResponseGeneration && m_WorkRoomTaskEpoch == m_WorkEpoch &&
        m_WorkRoomTaskIntent != "none" && m_WorkRoomTaskIntent == m_RoomTaskIntent &&
        m_WorkRoomTaskActionId == m_RoomTaskActionId &&
        m_ChatSettings?.m_ChatModel?.SupportsRoomTaskMessages == true;

    private bool IsWorkSceneFeedbackGuard(int generation) =>
        generation == m_WorkSceneFeedbackGuardGeneration && ShouldGuardWorkSceneFeedback(generation);

    private string BuildWorkSceneFeedbackContext()
    {
        var scope = new JObject {
            ["source"] = "same-user-work-spatial-result-feedback",
            ["workEpoch"] = m_WorkEpoch,
            ["intendedAction"] = m_WorkRoomTaskIntent,
            ["actionId"] = m_WorkRoomTaskActionId,
            ["feedbackOnly"] = true,
            ["newSpatialActionSubmittedByThisFeedback"] = false,
            ["evidenceLimit"] = "Resolve failure proves only that the intended visible entity was not confirmed. " +
                "A selected object's missing registered capability does not prove the intended lamp is unplugged, unpowered, broken, or incapable. " +
                "Engine component enablement and runtime-device registration are not measurements of physical mains power or wiring. " +
                "Historical successful off/on results keep their own entity and time; another selected renderer cannot overwrite that identity."
        };
        return "\n[同一用户事项的空间执行结果续报；候选台词核对后才外放]\n" +
            scope.ToString(Newtonsoft.Json.Formatting.None) + "\n" + BuildRoomTaskReplyFacts() +
            "\n这次只是读取已有空间执行事实并汇报，没有重新提交开关、走近或手部操作。" +
            "只解释这次实际可证实的结果与限制；目标未确认或选中物件没有登记控制能力，不能推断目标灯具没电、没接电源、损坏或只是不可控摆件。" +
            "如果仍需重新定位/重新观察，可以明确说明尚未完成；不能用将会操作的承诺或普通motion标签冒充已提交的新设备操作。";
    }
}
