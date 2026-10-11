public partial class ArdyRoomDialogueBridge
{
    public sealed class MovementSpeechReceipt
    {
        public string actionId, targetKind, phase, reason;
        public int operationRevision;
        public bool currentlyNearAndFacingUser;
    }

    // Playback gates may poll while PCM is buffering. Do not rebuild a route,
    // serialize the scene, or copy candidate diagnostics just to check speech.
    public MovementSpeechReceipt ReadMovementSpeechReceipt()
    {
        if (latestDiagnostic == null) return null;
        return new MovementSpeechReceipt {
            actionId = latestDiagnostic.actionId, targetKind = latestDiagnostic.targetKind,
            phase = IsConnected ? latestDiagnostic.phase : "disconnected", reason = latestDiagnostic.reason,
            operationRevision = latestDiagnostic.operationRevision,
            currentlyNearAndFacingUser = IsConnected && latestDiagnostic.phase == "arrived" &&
                latestDiagnostic.targetKind == "user" && PeekCurrentTargetEvidence().currentlyNearAndFacingUser
        };
    }
}
