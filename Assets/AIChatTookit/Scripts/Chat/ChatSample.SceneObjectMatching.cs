using System;
using System.Collections;
using System.Linq;
using NeEEvA.Perception;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    [SerializeField, Tooltip("依据实际候选轮廓和局部层级，再确认选中的物件是否符合本轮目标。")]
    private bool m_EnableSceneObjectTargetMatching = true;
    private JObject m_SceneObjectMatchReceipt;
    private JObject m_SceneObservationGoal;
    private SceneObjectCandidateView m_SceneObjectCandidateView;
    private Transform m_SceneMatchEnvironment;
    private UniVRM10.Vrm10Instance m_SceneMatchAvatar;
    private int m_SceneObjectMatchOwner, m_SceneMatchUserTurn = -1;
    public SceneObjectCandidateView LastSceneObjectCandidateView => m_SceneObjectCandidateView;

    private JObject SceneObjectTargetContext()
    {
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        if (bridge == null || bridge.environmentRoot != m_SceneMatchEnvironment || bridge.avatar != m_SceneMatchAvatar)
            return null;
        var result = m_SceneObjectMatchReceipt?.DeepClone() as JObject;
        if (result != null && m_SceneMatchUserTurn != m_ActionConstraints.CurrentUserTurn)
        { result["associationCurrent"] = false; result["associationScope"] = "Historical target review from another user turn; re-evaluate the present referent."; }
        if (result != null)
        {
            var goal = m_SceneObservationGoal?.DeepClone() as JObject;
            if (goal != null && (int?)goal["userTurnAtCreation"] != m_ActionConstraints.CurrentUserTurn &&
                (string)goal["status"] == "pending")
            { goal["status"] = "historical"; goal["needsOriginalTargetRecheck"] = false; }
            result["observationGoal"] = goal;
        }
        return result;
    }

    private void CancelSceneObjectTargetMatch()
    {
        ++m_SceneObjectMatchOwner;
        m_ChatSettings?.m_ChatModel?.CancelSceneObjectMatchMessage();
    }

    private static string BoundObjectGoal(string text) => string.IsNullOrWhiteSpace(text) ? "" : text.Substring(0, Math.Min(1200, text.Length));

    private bool TryContinueSceneObservationGoal(RoomTaskProtocol.Decision decision, out JObject goal, out string failure)
    {
        goal = null; failure = null;
        if (string.IsNullOrEmpty(decision.observationGoalId)) return true;
        var context = SceneObjectTargetContext();
        var pending = context?["observationGoal"] as JObject;
        if (pending == null || (string)pending["goalId"] != decision.observationGoalId ||
            (string)pending["status"] == "resolved")
        { failure = "observation-goal-reference-not-pending"; return false; }
        if (decision.IsSelfInitiated && (string)pending["status"] != "pending")
        { failure = "newer-user-turn-superseded-observation-goal"; return false; }
        goal = pending; return true;
    }

    private void CompleteSceneObservationGoal(RoomTaskProtocol.Decision decision, SceneObservedObject observed, SceneActionFact fact)
    {
        if (decision.IsObservationApproach || !fact.semanticTargetConfirmed || observed?.VisibleConfirmed != true ||
            string.IsNullOrEmpty(decision.observationGoalId) || m_SceneObservationGoal == null ||
            (string)m_SceneObservationGoal["goalId"] != decision.observationGoalId) return;
        m_SceneObservationGoal["status"] = "resolved";
        m_SceneObservationGoal["needsOriginalTargetRecheck"] = false;
        m_SceneObservationGoal["resolvedEntityId"] = observed.EntityId;
        m_SceneObservationGoal["resolvedCaptureId"] = observed.CaptureId;
        m_SceneObservationGoal["resolvedScope"] = "Explicit original-goal continuation semantically matched, then the same entity confirmed in a new physical head view. This resolves target observation only; touching or device operation are separate outcomes.";
    }

    private string SceneObjectGoal(RoomTaskProtocol.Decision decision)
    {
        if (decision.origin == "actor_draft")
            return "[Current user target context; not a new adoption]\n" + BoundObjectGoal(m_ActionConstraints.CurrentUserText) +
                "\n[Role decision; does not rewrite the user's referent]\n" + BoundObjectGoal(decision.SourceText);
        string current = decision.origin == "autonomous" ? m_SceneAutonomyIntention : m_ActionConstraints.CurrentUserText;
        return BoundObjectGoal(string.IsNullOrWhiteSpace(current) ? decision.visualTarget?.label : current);
    }

    private JObject SceneObjectGoalSources(RoomTaskProtocol.Decision decision) => new JObject {
        ["currentUserContext"] = m_ActionConstraints.CurrentUserText ?? "",
        ["roleDecision"] = decision.origin == "actor_draft" ? decision.SourceText ?? "" :
            decision.origin == "autonomous" ? m_SceneAutonomyIntention ?? "" : "",
        ["plannerSelectionDescription"] = decision.visualTarget?.label ?? "",
        ["requestOrigin"] = decision.origin,
        ["scope"] = "Separate complete sources. The user supplies referents/corrections/restrictions, not proof that the role adopted every request. The role may refuse or choose an autonomous goal. The planner's selected-point label is only its hypothesis: it cannot redefine the requested target to fit its chosen candidate. Compare the actual image with the applicable user target AND role decision; if their referents conflict, do not confirm the substituted referent. No text grants execution or device capability."
    };

    private IEnumerator MatchSceneObjectTarget(RoomTaskProtocol.Decision decision, SceneVisionObservation observation,
        SceneObservedObject initial, SceneActionFact fact, Func<bool> current,
        Action<SceneObservedObject, string> completed)
    {
        var model = m_ChatSettings.m_ChatModel;
        int owner = ++m_SceneObjectMatchOwner;
        var ownerBridge = GetComponent<ArdyRoomDialogueBridge>();
        if (ownerBridge == null || ownerBridge.environmentRoot != m_SceneMatchEnvironment || ownerBridge.avatar != m_SceneMatchAvatar)
        { m_SceneObservationGoal = null; m_SceneObjectMatchReceipt = null; }
        if (m_SceneObjectMatchReceipt != null)
        {
            m_SceneObjectMatchReceipt["associationCurrent"] = false;
            m_SceneObjectMatchReceipt["associationScope"] = decision.RequestsTargetReselection ?
                "Explicit reselection revoked the old description-to-entity association; actual observation history is retained." :
                "A new target review is pending; the old review is historical evidence only.";
        }
        if (!TryContinueSceneObservationGoal(decision, out var continuingGoal, out string goalFailure))
        { completed(null, goalFailure); yield break; }
        if (model == null || !model.SupportsSceneObjectMatchMessages)
        { completed(null, "object-target-matcher-unavailable"); yield break; }
        bool refreshedSource = false;
        // The preceding route/planning requests can consume most of the picture's
        // lifetime. Refresh its exact observed entity once before asking the matcher;
        // the original pixel is never interpreted on a different picture.
        if (NeedsSceneObjectMatchRefresh(observation))
        {
            SceneVisionObservation refreshed = null; SceneObservedObject sameEntity = null; string refreshFailure = null;
            yield return RefreshSceneObjectMatchSource(observation, initial,
                () => owner == m_SceneObjectMatchOwner && current(),
                (value, entity, reason) => { refreshed = value; sameEntity = entity; refreshFailure = reason; });
            if (owner != m_SceneObjectMatchOwner || !current()) yield break;
            if (refreshed == null || sameEntity == null)
            { completed(null, "object-target-refresh-unavailable: " + refreshFailure); yield break; }
            observation = refreshed; initial = sameEntity; refreshedSource = true;
        }
        Vector2 focus = !refreshedSource && decision.visualTarget != null ? new Vector2(decision.visualTarget.x, decision.visualTarget.y) :
            initial?.ImagePoint ?? new Vector2(.5f, .5f);
        if (!m_SceneVisionCapture.TryBuildObjectCandidateView(observation, focus,
            decision.RequestsTargetReselection ? null : initial?.EntityId, out var view, out string failure))
        { completed(null, "object-candidate-view-unavailable: " + failure); yield break; }
        if (!current()) yield break;
        m_SceneObjectCandidateView = view;
        fact.targetPurpose = decision.IsObservationApproach ? "observe-closer" : "direct-target";
        fact.observationGoalId = decision.observationGoalId;
        fact.targetDescription = SceneObjectGoal(decision);
        var previousReview = SceneObjectTargetContext();
        if (continuingGoal != null) fact.targetDescription = BoundObjectGoal((string)continuingGoal["originalGoal"]);
        fact.matchCaptureId = view.CaptureId;
        var input = new JObject {
            ["intent"] = decision.intent, ["targetPurpose"] = fact.targetPurpose,
            ["originalGoal"] = fact.targetDescription,
            ["goalSources"] = SceneObjectGoalSources(decision),
            ["observationGoalId"] = decision.observationGoalId,
            ["currentRequest"] = SceneObjectGoal(decision),
            ["currentUserText"] = m_ActionConstraints.CurrentUserText,
            ["requestOrigin"] = decision.origin,
            ["requestSourceRule"] = "actor_draft is the current role proposal, not a user order. Preserve current user target corrections and restrictions; the proposal grants no target, device or movement permission.",
            ["requestedDescription"] = decision.visualTarget?.label,
            ["requestedEntityId"] = decision.entityTarget?.entityId,
            ["initialGeometryEntityId"] = initial?.EntityId,
            ["explicitReselection"] = decision.RequestsTargetReselection,
            ["recentDialogue"] = JArray.Parse(RecentRoomDialogue()),
            ["previousTargetReview"] = SceneObjectTargetContext(),
            ["pendingObservationGoal"] = previousReview?["observationGoal"]?.DeepClone(),
            ["candidates"] = JObject.Parse(view.MetadataJson),
            ["instructionScope"] = "Confirm what the character actually selected against the present intention using this exact image and bounded engine metadata. A prior clicked entity or label is not proof of the user's referent. On reselect, discard the old descriptor-to-entity association, keep real history. For observe-closer, identify the requested larger visible REFERENCE for a better observation station; this does not identify the small original target. If uncertain, do not guess. No action is performed by this review."
        };
        bool received = false, accepted = false; string output = "", status = "object-match-timeout";
        model.PostSceneObjectMatchMessage(input.ToString(Formatting.None), view.ImageDataUrl, (ok, value, why) => {
            if (owner != m_SceneObjectMatchOwner || !current()) return;
            received = true; accepted = ok; output = value; status = why;
        });
        float deadline = Time.realtimeSinceStartup + 12.5f;
        try
        {
            while (owner == m_SceneObjectMatchOwner && current() && !received && Time.realtimeSinceStartup < deadline) yield return null;
            if (owner != m_SceneObjectMatchOwner || !current()) yield break;
            RoomTaskProtocol.ObjectMatch match = null;
            bool parsed = received && accepted && RoomTaskProtocol.TryObjectMatch(output,
                view.Candidates.Select(x => x.candidateId), out match);
            fact.targetMatchVerdict = parsed ? match.verdict : "unavailable";
            fact.targetMatchReason = parsed ? match.reason : received && accepted ? "object-match-response-invalid" : status;
            fact.semanticTargetConfirmed = false;
            fact.needsCloserObservation = parsed && match.verdict == "uncertain";
            SceneObservedObject selected = null;
            SceneObservedObject proposed = null;
            if (parsed && !match.IsMatched && match.candidateId != "none")
                view.TryGetCandidate(match.candidateId, out proposed, out _);
            bool reobservedAfterMatch = false;
            string reobservationFailure = null;
            var decisionView = m_SceneVisionCapture?.DescribeViewCompatibility(observation, .35f, 25f);
            if (parsed && match.IsMatched && !view.TryGetCandidate(match.candidateId, out selected, out failure))
            {
                fact.targetMatchVerdict = "expired"; fact.targetMatchReason = failure;
                // A semantic answer names a candidate from the original image. If only
                // the camera pose drifted, resolve that SAME still-valid identity in one
                // new image; never use its old pixel on the new view or pick a substitute.
                if (decisionView?.bindingCurrent == true && decisionView.poseAvailable && !decisionView.compatible &&
                    view.TryGetHistoricalCandidateForReobservation(match.candidateId, out var historical, out _))
                {
                    SceneVisionObservation renewedImage = null;
                    yield return RefreshSceneObjectMatchSource(observation, historical,
                        () => owner == m_SceneObjectMatchOwner && current(),
                        (image, entity, why) => { renewedImage = image; selected = entity; reobservationFailure = why; });
                    if (owner != m_SceneObjectMatchOwner || !current()) yield break;
                    reobservedAfterMatch = renewedImage != null && selected?.VisibleConfirmed == true &&
                        selected.EntityId == historical.EntityId && selected.Root == historical.Root;
                    if (reobservedAfterMatch)
                    { fact.targetMatchVerdict = match.verdict; fact.targetMatchReason = match.reason; }
                    else { selected = null; fact.targetMatchReason = "same-entity-reobservation-failed: " + reobservationFailure; }
                }
            }
            Debug.Log("[Vision/ObjectMatchValidation] " + JsonConvert.SerializeObject(new {
                actionId = fact.actionId, captureId = view.CaptureId, refreshedSource,
                sourceAgeSeconds = Time.realtimeSinceStartup - observation.CapturedAtRealtime,
                bindingCurrent = m_SceneVisionCapture != null && m_SceneVisionCapture.IsObservationBindingCurrent(observation),
                viewCompatible = m_SceneVisionCapture != null && m_SceneVisionCapture.IsCurrentViewCompatible(observation, .35f, 25f),
                viewAtDecision = decisionView, reobservedAfterMatch,
                acceptedCaptureId = selected?.CaptureId, reobservationFailure,
                acceptedView = selected == null ? null : m_SceneVisionCapture?.DescribeViewCompatibility(selected.observation, .35f, 25f),
                transportAccepted = received && accepted, responseParsed = parsed,
                verdict = fact.targetMatchVerdict, reason = fact.targetMatchReason
            }), this);
            fact.semanticTargetConfirmed = selected != null;
            var bridge = GetComponent<ArdyRoomDialogueBridge>();
            m_SceneMatchEnvironment = bridge.environmentRoot; m_SceneMatchAvatar = bridge.avatar;
            m_SceneMatchUserTurn = m_ActionConstraints.CurrentUserTurn;
            if (selected != null && decision.IsObservationApproach)
                m_SceneObservationGoal = new JObject {
                    ["goalId"] = (string)continuingGoal?["goalId"] ?? "observation-goal-" + Guid.NewGuid().ToString("N"),
                    ["status"] = "pending",
                    ["originalGoal"] = fact.targetDescription,
                    ["referenceEntityId"] = selected.EntityId, ["referenceDescription"] = decision.visualTarget?.label,
                    ["approachActionId"] = fact.actionId, ["userTurnAtCreation"] = m_ActionConstraints.CurrentUserTurn,
                    ["needsOriginalTargetRecheck"] = true,
                    ["scope"] = "Unresolved observation objective retained across rounds, separate from the larger reference. Arrival/reobservation of that reference does not resolve it. To continue, copy this goalId as observationGoalId in a fresh decision. Omission means a different goal, never an automatic inheritance. New user turns make this historical; only an explicit user-origin continuation may revive it. Current images and semantic review are still required."
                };
            m_SceneObjectMatchReceipt = new JObject {
                ["actionId"] = fact.actionId, ["captureId"] = view.CaptureId,
                ["originalGoal"] = fact.targetDescription, ["requestedDescription"] = decision.visualTarget?.label,
                ["goalSources"] = SceneObjectGoalSources(decision),
                ["reobservedAfterMatch"] = reobservedAfterMatch, ["acceptedCaptureId"] = selected?.CaptureId,
                ["reobservationScope"] = "captureId is the exact image interpreted by the model. acceptedCaptureId is the same entity's valid observation at acceptance; when different, only its identity was renewed from new opaque pixels, without a new semantic claim or a device permission grant.",
                ["targetPurpose"] = fact.targetPurpose, ["verdict"] = fact.targetMatchVerdict,
                ["associationCurrent"] = selected != null,
                ["reason"] = fact.targetMatchReason, ["semanticTargetConfirmed"] = fact.semanticTargetConfirmed,
                ["matchedEntityId"] = selected?.EntityId, ["initialGeometryEntityId"] = initial?.EntityId,
                ["needsCloserObservation"] = fact.needsCloserObservation,
                ["proposedCandidateId"] = parsed ? match.candidateId : null,
                ["proposedEntityId"] = proposed?.EntityId,
                ["proposedImagePoint"] = proposed == null ? null : JObject.FromObject(new { x=proposed.ImagePoint.x, y=proposed.ImagePoint.y }),
                ["needsTargetReselection"] = parsed && match.verdict == "different-target",
                ["proposalScope"] = "A nonmatched proposal is only a cue for the next fresh-image decision, not a semantic match or permission for this action. At most one candidate-match request is made by this action; never retry stale pixels automatically.",
                ["targetSelection"] = decision.targetSelection,
                ["scope"] = "Model interpretation of this exact candidate image, separate from engine identity. A reference selected for observe-closer is not confirmation of the original small target. When uncertain, consider a visible larger reference and targetPurpose=observe-closer in a NEW approach decision, then reobserve and retry the original goal; no automatic device operation. User correction can revoke a referent association without deleting objective seen-object history."
            };
            Debug.Log("[Vision/ObjectMatch] " + m_SceneObjectMatchReceipt.ToString(Formatting.None), this);
            if (selected == null)
            { completed(null, "object-target-" + fact.targetMatchVerdict + ": " + fact.targetMatchReason); yield break; }
            fact.entityId = selected.EntityId; fact.objectConfirmed = selected.VisibleConfirmed;
            fact.focusKind = "visible-object";
            fact.intentFocusWorldPoint = selected.WorldPoint; fact.hasIntentFocus = true;
            fact.targetResolutionSource = initial?.EntityId == selected.EntityId ? "candidate-confirmed" : "candidate-reselected";
            completed(selected, null);
        }
        finally { if (owner == m_SceneObjectMatchOwner) model.CancelSceneObjectMatchMessage(); }
    }

    private bool NeedsSceneObjectMatchRefresh(SceneVisionObservation observation)
        => observation?.HasImage == true &&
            (m_SceneVisionCapture != null && !m_SceneVisionCapture.IsCurrentViewCompatible(observation, .35f, 25f) ||
             Time.realtimeSinceStartup - observation.CapturedAtRealtime >= Mathf.Min(6f, m_SceneObservationMaxAgeSeconds * .5f));

    private IEnumerator RefreshSceneObjectMatchSource(SceneVisionObservation previous, SceneObservedObject selected,
        Func<bool> current, Action<SceneVisionObservation, SceneObservedObject, string> completed)
    {
        if (!current()) yield break;
        var source = m_SceneVisionCapture;
        var avatar = source != null ? source.Target : null;
        var bridge = GetComponent<ArdyRoomDialogueBridge>();
        var environment = bridge != null ? bridge.environmentRoot : null;
        string failure = null;
        // This is renewal of an exact still-valid geometric association, not a new
        // interpretation of old pixels or recovery of an already expired permit.
        if (source == null || selected?.VisibleConfirmed != true || selected.observation != previous ||
            !SceneVisionGrounding.Fresh(previous) || Time.realtimeSinceStartup - previous.CapturedAtRealtime > m_SceneObservationMaxAgeSeconds ||
            !source.IsObservationBindingCurrent(previous) || !selected.TryValidate(out failure))
        { completed(null, null, failure ?? "same-visible-entity-required-for-refresh"); yield break; }
        var root = selected.Root;
        string entityId = selected.EntityId;
        bool received = false; SceneVisionObservation refreshed = null;
        float requestedAt = Time.realtimeSinceStartup;
        RequestFreshSceneObservation("scene-object-match-refresh", value => {
            if (!current()) return;
            refreshed = value; received = true;
        });
        float deadline = requestedAt + 4.5f;
        while (current() && !received && Time.realtimeSinceStartup < deadline) yield return null;
        if (!current()) yield break;
        if (source == null || source != m_SceneVisionCapture || avatar == null || source.Target != avatar || bridge == null ||
            bridge != GetComponent<ArdyRoomDialogueBridge>() || bridge.avatar != avatar ||
            bridge.environmentRoot != environment || !source.IsObservationBindingCurrent(previous))
        { completed(null, null, "source-binding-changed-during-refresh"); yield break; }
        if (!received || refreshed?.HasImage != true || refreshed.CaptureId == previous.CaptureId ||
            refreshed.CapturedAtRealtime < requestedAt || !IsFreshSceneObservation(refreshed))
        { completed(null, null, "new-compatible-image-unavailable"); yield break; }
        if (!source.TryFindObservedObject(refreshed, entityId, out var seen, out failure) || !seen.VisibleConfirmed ||
            seen.Root != root || !seen.TryValidate(out failure))
        { completed(null, null, failure ?? "same-entity-not-visible-in-new-image"); yield break; }
        Debug.Log("[Vision/ObjectMatchRefresh] " + JsonConvert.SerializeObject(new {
            previousCaptureId = previous.CaptureId, captureId = refreshed.CaptureId, entityId,
            previousAgeSeconds = Time.realtimeSinceStartup - previous.CapturedAtRealtime,
            imagePoint = new { x = seen.ImagePoint.x, y = seen.ImagePoint.y },
            scope = "One new frame reidentified the same visible entity. Its current pixels feed the semantic matcher; no action or semantic match is authorized by renewal."
        }), this);
        completed(refreshed, seen, null);
    }
}
