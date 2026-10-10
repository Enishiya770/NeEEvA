using System;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    // An unapproved request is retained as exact quoted evidence. This draft has
    // no goal, scene-action lease, entity binding or device-write authority.
    private sealed class PendingDeviceClarification
    {
        public string id, originalRequest, latestUserText, createdUtc;
        public JObject proposal, previousReview;
        public readonly JArray userUpdates = new JArray();
        public Transform environment;
        public UniVRM10.Vrm10Instance avatar;
        public NevaSceneVisionCapture source;
        public LLM model;
        public int createdUserTurn, lastReviewedUserTurn, createdGeneration;
        public bool authorizationHistoryComplete = true;
    }

    private sealed class PendingDeviceClarificationApprovalLease
    {
        public PendingDeviceClarification pending;
        public RoomTaskProtocol.DeviceGoalReview review;
        public string latestUserText;
        public int generation, workEpoch, userTurn, visionEpoch;
        public bool consumed;
    }

    private sealed class PendingDeviceClarificationReviewRequest
    {
        public PendingDeviceClarification pending;
        public string latestUserText;
        public int generation, workEpoch, userTurn, visionEpoch;
        public bool consumed;
    }

    private PendingDeviceClarification m_PendingDeviceClarification;
    private PendingDeviceClarificationReviewRequest m_PendingDeviceClarificationReviewRequest;

    private bool PendingDeviceClarificationOwnerCurrent(PendingDeviceClarification pending)
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        bool current = pending != null && ReferenceEquals(m_PendingDeviceClarification, pending) &&
            bridge != null && pending.environment != null && bridge.environmentRoot == pending.environment &&
            pending.environment.gameObject.scene == gameObject.scene && pending.avatar != null && bridge.avatar == pending.avatar &&
            pending.source != null && pending.source == m_SceneVisionCapture && pending.source.Target == pending.avatar &&
            pending.model != null && pending.model == m_ChatSettings?.m_ChatModel;
        if (pending != null && ReferenceEquals(m_PendingDeviceClarification, pending) && !current)
            ClearPendingDeviceClarification("pending-room-avatar-model-or-source-owner-changed");
        return current;
    }

    private void ClearPendingDeviceClarification(string reason)
    {
        var pending = m_PendingDeviceClarification;
        m_PendingDeviceClarification = null;
        m_PendingDeviceClarificationReviewRequest = null;
        if (pending != null)
            Debug.Log($"[SpatialGoal/Pending] pending={pending.id} cleared={reason}; no pending action authority", this);
    }

    private void RememberPendingDeviceClarification(RoomTaskProtocol.Decision decision, string originalRequest,
        RoomTaskProtocol.DeviceGoalReview review, int generation)
    {
        var proposal = decision?.deviceGoal;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (generation != m_FormalResponseGeneration || decision?.origin != "user" || !RoomTaskProtocol.RetainsPendingDeviceProposal(review) ||
            proposal?.operation != "set" || string.IsNullOrWhiteSpace(originalRequest) ||
            !string.Equals(originalRequest, m_ActionConstraints.CurrentUserText, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(proposal.requestQuote) || proposal.requestQuote.Length > 160 ||
            originalRequest.IndexOf(proposal.requestQuote, StringComparison.Ordinal) < 0 ||
            string.IsNullOrWhiteSpace(proposal.targetDescription) || proposal.targetDescription.Length > 240 ||
            (proposal.desiredState != "on" && proposal.desiredState != "off") ||
            bridge?.environmentRoot == null || bridge.avatar == null || m_SceneVisionCapture == null ||
            m_SceneVisionCapture.Target != bridge.avatar || m_ChatSettings?.m_ChatModel == null) return;
        // A duplicate terminal callback cannot replace the identity of this draft.
        var current = m_PendingDeviceClarification;
        if (PendingDeviceClarificationOwnerCurrent(current) && current.createdGeneration == generation &&
            current.createdUserTurn == m_ActionConstraints.CurrentUserTurn && current.originalRequest == originalRequest) return;
        m_PendingDeviceClarification = new PendingDeviceClarification {
            id = "pending-device-clarification-" + Guid.NewGuid().ToString("N"), originalRequest = originalRequest,
            latestUserText = originalRequest, proposal = (JObject)JObject.FromObject(proposal).DeepClone(),
            previousReview = new JObject { ["verdict"] = review.verdict, ["reason"] = review.reason, ["requestAssessment"] = review.requestAssessment, ["registered"] = false },
            environment = bridge.environmentRoot, avatar = bridge.avatar, source = m_SceneVisionCapture,
            model = m_ChatSettings.m_ChatModel, createdUserTurn = m_ActionConstraints.CurrentUserTurn,
            lastReviewedUserTurn = m_ActionConstraints.CurrentUserTurn, createdGeneration = generation,
            createdUtc = DateTime.UtcNow.ToString("o")
        };
        m_PendingDeviceClarificationReviewRequest = null;
        Debug.Log($"[SpatialGoal/Pending] pending={m_PendingDeviceClarification.id} generation={generation} stored exact unapproved request; no goal or action authority", this);
    }

    private bool NeedsPendingDeviceClarificationReview(bool autonomous)
        => !autonomous && PendingDeviceClarificationOwnerCurrent(m_PendingDeviceClarification) &&
            m_PendingDeviceClarification.lastReviewedUserTurn != m_ActionConstraints.CurrentUserTurn;

    private bool PendingDeviceClarificationReviewRequestCurrent(PendingDeviceClarificationReviewRequest request)
        => request != null && !request.consumed && ReferenceEquals(m_PendingDeviceClarificationReviewRequest, request) &&
            PendingDeviceClarificationOwnerCurrent(request.pending) && request.generation == m_FormalResponseGeneration &&
            request.workEpoch == m_WorkEpoch && request.userTurn == m_ActionConstraints.CurrentUserTurn &&
            request.visionEpoch == m_SceneVisionEpoch &&
            string.Equals(request.latestUserText, m_ActionConstraints.CurrentUserText, StringComparison.Ordinal) &&
            isActiveAndEnabled && !m_UserSpeechActiveForAutonomy && !m_SceneTimePaused && Time.timeScale > 0;

    private bool BeginPendingDeviceClarificationReview(int generation, string latestText)
    {
        if (!NeedsPendingDeviceClarificationReview(false) || generation != m_FormalResponseGeneration ||
            string.IsNullOrWhiteSpace(latestText) || !string.Equals(latestText, m_ActionConstraints.CurrentUserText, StringComparison.Ordinal) ||
            PendingDeviceClarificationReviewRequestCurrent(m_PendingDeviceClarificationReviewRequest) ||
            !isActiveAndEnabled || m_UserSpeechActiveForAutonomy || m_SceneTimePaused || Time.timeScale <= 0) return false;
        m_PendingDeviceClarificationReviewRequest = new PendingDeviceClarificationReviewRequest {
            pending = m_PendingDeviceClarification, latestUserText = latestText, generation = generation,
            workEpoch = m_WorkEpoch, userTurn = m_ActionConstraints.CurrentUserTurn, visionEpoch = m_SceneVisionEpoch
        };
        RememberPendingDeviceUserUpdate(m_PendingDeviceClarification, latestText);
        return true;
    }

    private void RememberPendingDeviceUserUpdate(PendingDeviceClarification pending, string latestText)
    {
        pending.latestUserText = latestText;
        if (pending.userUpdates.Last is JObject last &&
            (int?)last["userTurn"] == m_ActionConstraints.CurrentUserTurn) return;
        // Accepted human words remain evidence even when the reviewer times out
        // or emits invalid output. Otherwise a later identity answer could lose
        // an intervening cancellation or negation.
        pending.userUpdates.Add(new JObject {
            ["userTurn"] = m_ActionConstraints.CurrentUserTurn,
            ["text"] = SpatialGoalBoundText(latestText, 600),
            ["fullTextLength"] = latestText.Length, ["textTruncated"] = latestText.Length > 600,
            ["source"] = "actual-accepted-user-text"
        });
        if (latestText.Length > 600) pending.authorizationHistoryComplete = false;
        while (pending.userUpdates.Count > 6)
        {
            pending.authorizationHistoryComplete = false;
            pending.userUpdates.RemoveAt(0);
        }
    }

    private void SettleFailedPendingDeviceReview(string reason)
    {
        var request = m_PendingDeviceClarificationReviewRequest;
        if (!PendingDeviceClarificationReviewRequestCurrent(request)) return;
        request.consumed = true;
        m_PendingDeviceClarificationReviewRequest = null;
        request.pending.lastReviewedUserTurn = request.userTurn;
        request.pending.previousReview = new JObject {
            ["verdict"] = "uncertain", ["reason"] = reason, ["registered"] = false,
            ["scope"] = "Review unavailable; this is not a user rejection or cancellation."
        };
    }

    private JObject BuildPendingDeviceClarificationContext()
    {
        var pending = m_PendingDeviceClarification;
        if (!PendingDeviceClarificationOwnerCurrent(pending)) return new JObject { ["status"] = "none", ["registered"] = false };
        return new JObject {
            ["pendingId"] = pending.id, ["status"] = "waiting_clarification", ["registered"] = false,
            ["ownerCurrent"] = true, ["originalUserRequest"] = pending.originalRequest,
            ["latestUserText"] = pending.latestUserText, ["proposal"] = pending.proposal.DeepClone(),
            ["actualUserUpdates"] = pending.userUpdates.DeepClone(),
            ["authorizationHistoryComplete"] = pending.authorizationHistoryComplete,
            ["previousReview"] = pending.previousReview?.DeepClone(), ["createdUtc"] = pending.createdUtc,
            ["userTurn"] = pending.createdUserTurn, ["lastReviewedUserTurn"] = pending.lastReviewedUserTurn,
            ["scope"] = "Exact original user words and an UNAPPROVED model proposal, not a standing authorized goal. Review originalUserRequest together with latestUserText and actualUserUpdates. Only actual human words establish or revoke permission; previousReview is a fallible model interpretation, and its rejected verdict does NOT mean the user rejected the operation. An original affirmative state request plus a request to search/confirm that same target may approve an unbound goal without repeating on/off or supplying a historical source. Conversely, an original negation, observation-only request or quoted command cannot gain operation authority from a later identity answer. Actual intervening cancellation/replacement must remain effective unless the human clearly authorizes the old operation again. textTruncated=true cannot establish permission by itself. A relation classification alone never registers the old proposal: only approved clarify may create a reviewed goal; approved cancel clears the pending draft; new/unrelated leave it as quoted background and route the latest request independently. Every actual device write still needs fresh matching, capability, distance and readback checks."
        };
    }

    private bool TryApplyPendingDeviceClarificationUpdate(RoomTaskProtocol.DeviceGoalReview strictReview,
        string latestText, out bool continued, out string reason)
    {
        continued = false; reason = null;
        var pending = m_PendingDeviceClarification;
        var request = m_PendingDeviceClarificationReviewRequest;
        if (!PendingDeviceClarificationReviewRequestCurrent(request) || !ReferenceEquals(request.pending, pending) ||
            strictReview == null || !strictReview.HasUserUpdateFields ||
            string.IsNullOrEmpty(strictReview.pendingClarificationId) || strictReview.pendingClarificationId != pending.id ||
            string.IsNullOrWhiteSpace(latestText) || !string.Equals(latestText, m_ActionConstraints.CurrentUserText, StringComparison.Ordinal) ||
            pending.lastReviewedUserTurn == m_ActionConstraints.CurrentUserTurn)
        { reason = "pending-device-clarification-review-or-owner-not-current"; return false; }
        request.consumed = true;
        m_PendingDeviceClarificationReviewRequest = null;
        RememberPendingDeviceUserUpdate(pending, latestText);
        pending.lastReviewedUserTurn = m_ActionConstraints.CurrentUserTurn;
        pending.previousReview = new JObject { ["verdict"] = strictReview.verdict, ["reason"] = strictReview.reason,
            ["relation"] = strictReview.relation, ["referentKind"] = strictReview.referentKind,
            ["referentSourceId"] = strictReview.referentSourceId, ["registered"] = false };
        if (!strictReview.IsApproved)
        { reason = "pending-device-clarification-not-independently-approved"; return false; }
        switch (strictReview.relation)
        {
            case "cancel":
                ClearPendingDeviceClarification("independently-reviewed-pending-cancellation"); return true;
            case "new":
                // Relation classification alone must not destroy a human request:
                // the model can call an independent topic a replacement. Plan the
                // latest words separately, without any authority from this draft.
                // Actual new-device registration clears it through the normal front door.
                return true;
            case "unrelated":
                return true; // Quoted draft survives; unrelated dialogue never resumes it.
            case "continue":
                reason = "pending-device-clarification-needs-specific-clarification"; return false;
            case "clarify":
                var approval = new PendingDeviceClarificationApprovalLease {
                    pending = pending, review = strictReview, latestUserText = latestText,
                    generation = request.generation, workEpoch = request.workEpoch,
                    userTurn = request.userTurn, visionEpoch = request.visionEpoch
                };
                if (!TryRegisterPendingReviewedSpatialGoal(approval, out reason)) return false;
                continued = true;
                return true;
            default:
                reason = "invalid-pending-device-clarification-relation"; return false;
        }
    }

    private bool TryRegisterPendingReviewedSpatialGoal(PendingDeviceClarificationApprovalLease approval, out string reason)
    {
        reason = null;
        if (approval == null || approval.consumed || !PendingDeviceClarificationOwnerCurrent(approval.pending) ||
            approval.generation != m_FormalResponseGeneration || approval.workEpoch != m_WorkEpoch ||
            approval.userTurn != m_ActionConstraints.CurrentUserTurn || approval.visionEpoch != m_SceneVisionEpoch ||
            !string.Equals(approval.latestUserText, m_ActionConstraints.CurrentUserText, StringComparison.Ordinal) ||
            approval.review?.IsApproved != true || approval.review.pendingClarificationId != approval.pending.id ||
            approval.review.relation != "clarify" ||
            !isActiveAndEnabled || m_UserSpeechActiveForAutonomy || m_SceneTimePaused || Time.timeScale <= 0)
        { reason = "pending-device-clarification-approval-lease-not-current"; return false; }
        approval.consumed = true; // A failed registration cannot replay this approval token.
        bool hasEntity = !string.IsNullOrEmpty(approval.review.referentEntityId);
        if (hasEntity != !string.IsNullOrEmpty(approval.review.referentGoalId))
        { reason = "pending-device-clarification-referent-pair-incomplete"; return false; }
        if (!approval.review.HasTypedReferent && hasEntity)
        {
            bool verified = false;
            foreach (var item in BuildVerifiedDeviceReferentHistory())
                if ((string)item["goalId"] == approval.review.referentGoalId &&
                    (string)item["entityId"] == approval.review.referentEntityId) { verified = true; break; }
            if (!verified)
            { reason = "pending-device-clarification-referent-not-in-verified-history"; return false; }
        }
        var proposal = approval.pending.proposal.ToObject<RoomTaskProtocol.DeviceGoalProposal>();
        if (!TryCreateReviewedSpatialGoal(proposal, approval.pending.originalRequest, approval.latestUserText, approval.review, out reason)) return false;
        m_SpatialDeviceGoal.userUpdates.Add(new JObject {
            ["userTurn"] = approval.userTurn, ["text"] = SpatialGoalBoundText(approval.latestUserText, 600),
            ["relation"] = approval.review.relation, ["reason"] = approval.review.reason,
            ["pendingClarificationId"] = approval.pending.id,
            ["scope"] = "Original unapproved request and actual latest reply were jointly approved before this new goal was registered."
        });
        ClearPendingDeviceClarification("combined-pending-request-independently-approved-and-registered");
        return true;
    }
}
