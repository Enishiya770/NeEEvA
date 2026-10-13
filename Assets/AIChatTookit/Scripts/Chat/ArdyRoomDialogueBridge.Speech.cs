public partial class ArdyRoomDialogueBridge
{
    public sealed class MovementSpeechReceipt
    {
        public string actionId, targetKind, phase, reason;
        public int operationRevision;
        public bool currentlyNearAndFacingUser;
        public bool userTrackingValid, userSupportVerified;
        public float actualUserDistance = -1, actualFacingErrorDegrees = -1, desiredUserDistance;
        public string arrivalObservedUtc;
        public string approachRetargetStatus;
    }

    // Playback gates may poll while PCM is buffering. Do not rebuild a route,
    // serialize the scene, or copy candidate diagnostics just to check speech.
    public MovementSpeechReceipt ReadMovementSpeechReceipt()
    {
        if (latestDiagnostic == null) return null;
        // Live relative pose is distinct from the immutable completion diagnostic.
        // This read checks support but never plans or submits another route.
        var current = IsConnected && latestDiagnostic.phase == "arrived" && latestDiagnostic.targetKind == "user"
            ? PeekCurrentTargetEvidence() : null;
        return new MovementSpeechReceipt {
            actionId = latestDiagnostic.actionId, targetKind = latestDiagnostic.targetKind,
            phase = IsConnected ? latestDiagnostic.phase : "disconnected", reason = latestDiagnostic.reason,
            operationRevision = latestDiagnostic.operationRevision,
            approachRetargetStatus = latestDiagnostic.approachRetargetStatus,
            currentlyNearAndFacingUser = IsConnected && latestDiagnostic.phase == "arrived" &&
                latestDiagnostic.targetKind == "user" && current != null && current.currentlyNearAndFacingUser,
            userTrackingValid = current != null && current.userTrackingValid,
            userSupportVerified = current != null && current.userSupportVerified,
            actualUserDistance = current?.actualUserDistance ?? -1,
            actualFacingErrorDegrees = current?.actualFacingErrorDegrees ?? -1,
            desiredUserDistance = current?.desiredUserDistance ?? approachDistance,
            arrivalObservedUtc = latestDiagnostic.phase == "arrived" ? latestDiagnostic.observedUtc : null
        };
    }
}
