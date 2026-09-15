using NeEEvA.Motion;

public sealed partial class ArdyRoomDialogueBridge
{
    /// <summary>An action can be accepted before its deferred controller operation exists.
    /// Only expose the operation actually adopted by this same live bridge action.</summary>
    public bool TryGetSubmittedMotionOperation(string expectedActionId,
        out ArdyRoomLocomotionController submittedController, out int submittedOperation)
    {
        submittedController = null; submittedOperation = 0;
        if (!IsConnected || string.IsNullOrEmpty(expectedActionId) || actionId != expectedActionId ||
            !hasControllerOperation || controller.OperationRevision != operationRevision) return false;
        submittedController = controller; submittedOperation = operationRevision;
        return true;
    }
}
