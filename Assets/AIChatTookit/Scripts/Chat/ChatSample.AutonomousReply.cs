using Newtonsoft.Json.Linq;

public partial class ChatSample
{
    // Reporting authority comes from the current accepted decision, not an old
    // human command or the absence of a new first-reply classification.
    private sealed class AutonomousReplyOwner
    {
        public int generation, epoch, userTurn;
        public string intention, intent, actionId;
        public LLM model;
        public ArdyRoomDialogueBridge bridge;
        public UnityEngine.Object avatar, environment;
    }
    private AutonomousReplyOwner m_AutonomousReplyOwner;

    private void BindAutonomousReplyOwner(int generation, bool autonomous, bool accepted, ArdyRoomDialogueBridge bridge)
    {
        m_AutonomousReplyOwner = null;
        var attention = ReadAutonomyAttention();
        if (!autonomous || !accepted || bridge == null ||
            (string)attention?["kind"] != "current-user-body" ||
            (bool?)attention?["actionAuthorization"] != false) return;
        m_AutonomousReplyOwner = new AutonomousReplyOwner {
            generation = generation, epoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn,
            intention = m_SceneAutonomyIntention, intent = m_RoomTaskIntent, actionId = m_RoomTaskActionId,
            model = m_ChatSettings?.m_ChatModel, bridge = bridge, avatar = bridge.avatar, environment = bridge.environmentRoot
        };
    }

    private JObject ReadAutonomousBodyReplyPurpose(int generation)
    {
        var owner = m_AutonomousReplyOwner;
        if (owner == null || generation != m_FormalResponseGeneration || generation != m_RoomTaskGeneration ||
            owner.generation != generation || owner.epoch != m_WorkEpoch || owner.userTurn != m_ActionConstraints.CurrentUserTurn ||
            !m_AgentCurrentRoundIsTick || m_WorkReviewOpen || m_UserSpeechActiveForAutonomy || m_SceneTimePaused || !isActiveAndEnabled ||
            owner.model == null || owner.model != m_ChatSettings?.m_ChatModel || owner.bridge == null || !owner.bridge.IsConnected ||
            owner.bridge != GetComponent<ArdyRoomDialogueBridge>() || owner.avatar != owner.bridge.avatar || owner.environment != owner.bridge.environmentRoot ||
            owner.intention != m_SceneAutonomyIntention || (string)ReadAutonomyAttention()?["kind"] != "current-user-body" ||
            ReadCompactCurrentRequestSemantics(generation) != null ||
            owner.intent != m_RoomTaskIntent || owner.actionId != m_RoomTaskActionId ||
            ReadRoomEvidenceReplyFacts(generation) != null || CurrentGenerationRequiresReviewedGoalAnswer(generation) ||
            HasWorkToolInFlight() || SceneActionIsBusy || SpatialGoalUnfinished(m_SpatialDeviceGoal)) return null;
        if (owner.intent != "none" && owner.intent != "inspect" && owner.intent != "approach" && owner.intent != "follow" && owner.intent != "stop-moving") return null;
        // A submitted movement must still be owned by this action. A no-action
        // conversation may read a historical receipt, but cannot adopt its ID.
        if (owner.intent != "none" && owner.intent != "inspect" &&
            (string.IsNullOrEmpty(owner.actionId) || owner.bridge.ActionId != owner.actionId)) return null;
        return new JObject {
            ["source"] = "accepted-current-autonomous-decision", ["generation"] = generation,
            ["workEpoch"] = owner.epoch, ["userTurn"] = owner.userTurn,
            ["purpose"] = string.IsNullOrEmpty(owner.actionId) ? "autonomous-conversation" : "action-result",
            ["intention"] = owner.intention, ["intent"] = owner.intent, ["actionId"] = owner.actionId,
            ["actionAuthorization"] = false,
            ["scope"] = "This is a new autonomous intention, not an unfinished historical command. " +
                "Speak naturally about this intention and its owned execution, or remain silent. " +
                "Historical user text retains constraints and context only; do not repeat its completed operations. " +
                "No current visual description is requested. This reporting purpose grants no action or retry permission."
        };
    }

    private bool UseBodyReplyPacket(int generation) => UseMovementReplyPacket(generation) ||
        ((m_RoomTaskIntent == "none" || m_RoomTaskIntent == "inspect") && ReadAutonomousBodyReplyPurpose(generation) != null);

    private bool UseReceiptOnlySpeechReview(int generation) => UseBodyReplyPacket(generation) || UseWorkResultReplyPacket(generation);
}
