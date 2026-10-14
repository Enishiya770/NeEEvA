using System;
using UnityEngine;
using UnityEngine.Networking;

namespace NeEEvA.Motion
{
    public sealed partial class ArdyRoomLocomotionController
    {
        [Serializable] private sealed class StreamWireReceipt
        {
            public string characterId, sessionId, requestId, stage, dispatchedUtc, observedUtc, outcome, transportResult, error;
            public int operationRevision, wireRevision, stepIndex;
            public float dispatchedRealtime, elapsedSeconds, dispatchBufferSeconds, terminalBufferSeconds;
            public long httpStatus, downloadedBytes;
            public bool transportCompleted, abortRequested, serverTimingAvailable;
            public StreamTimings timings;
        }
        private StreamWireReceipt streamActiveWire;
        private void BeginStreamWireReceipt()
        {
            streamActiveWire = new StreamWireReceipt { characterId = characterId, sessionId = streamSessionId,
                requestId = requestId, operationRevision = OperationRevision, wireRevision = streamWireRevision,
                stepIndex = streamStepIndex, stage = Stage, dispatchedUtc = DateTime.UtcNow.ToString("o"),
                dispatchedRealtime = Time.realtimeSinceStartup, dispatchBufferSeconds = StreamBufferSeconds };
        }
        private void EndStreamWireReceipt(string outcome, bool abort = false, string expectedRequest = null)
        {
            var receipt = streamActiveWire;
            if (receipt == null || expectedRequest != null && receipt.requestId != expectedRequest) return;
            streamActiveWire = null;
            receipt.outcome = outcome; receipt.abortRequested = abort;
            receipt.observedUtc = DateTime.UtcNow.ToString("o");
            receipt.elapsedSeconds = Mathf.Max(0, Time.realtimeSinceStartup - receipt.dispatchedRealtime);
            receipt.terminalBufferSeconds = StreamBufferSeconds;
            if (pending != null)
            {
                receipt.transportCompleted = pending.isDone;
                receipt.httpStatus = pending.responseCode; receipt.downloadedBytes = (long)pending.downloadedBytes;
                receipt.transportResult = pending.result.ToString(); receipt.error = pending.error;
                if (pending.isDone && pending.result == UnityWebRequest.Result.Success && pending.downloadedBytes <= 8 * 1024 * 1024)
                    try { receipt.timings = JsonUtility.FromJson<StreamResponse>(pending.downloadHandler.text)?.timings;
                        receipt.serverTimingAvailable = receipt.timings != null; } catch (ArgumentException) { }
            }
            Debug.Log("[ARDY room/wire-terminal] " + JsonUtility.ToJson(receipt), this);
        }
    }
}
