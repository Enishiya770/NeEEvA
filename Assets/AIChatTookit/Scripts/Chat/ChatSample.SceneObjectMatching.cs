using System;
using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
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
            var mainGoal = CurrentSceneObjectSpatialGoal();
            string parent = (string)goal?["parentSpatialGoalId"];
            bool sameParent = mainGoal != null && parent == (string)mainGoal["goalId"];
            bool independentCurrent = IsCurrentIndependentSceneObservationGoal(goal, mainGoal);
            if (goal != null && !independentCurrent && (mainGoal != null || !string.IsNullOrEmpty(parent)) && !sameParent &&
                (string)goal["status"] != "resolved")
            {
                goal["status"] = "superseded"; goal["needsOriginalTargetRecheck"] = false;
                goal["parentScope"] = "A different or inactive main device goal cannot inherit this observation objective.";
            }
            else if (goal != null && sameParent && (bool?)mainGoal["executionCurrent"] != true &&
                (string)goal["status"] != "resolved")
            {
                goal["status"] = "suspended"; goal["needsOriginalTargetRecheck"] = false;
                goal["parentScope"] = "Stored child of the standing goal; this independent dialogue/action round does not own its execution.";
            }
            else if (goal != null && !sameParent && (int?)goal["userTurnAtCreation"] != m_ActionConstraints.CurrentUserTurn &&
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

    private JObject CurrentSceneObjectSpatialGoal()
    {
        var goal = BuildSpatialGoalContext();
        string status = (string)goal["status"];
        if ((bool?)goal["ownerCurrent"] != true || string.IsNullOrEmpty((string)goal["goalId"]) ||
            (status != "active" && status != "searching" && status != "suspended" && status != "waiting_clarification")) return null;
        return PickRoomFields(goal, "goalId", "status", "ownerCurrent", "originalRequest", "targetDescription",
            "desiredState", "boundEntityId", "executionCurrent", "executionSuspended", "createdUtc");
    }

    // A paused device task is background, not the parent of a new observation
    // requested this turn. Only an unparented, current-turn observation can use
    // this path; a stored child still requires its own executing device goal.
    private bool IsCurrentIndependentSceneObservationGoal(JObject observationGoal, JObject standingGoal) =>
        observationGoal != null && string.IsNullOrEmpty((string)observationGoal["parentSpatialGoalId"]) &&
        (int?)observationGoal["userTurnAtCreation"] == m_ActionConstraints.CurrentUserTurn &&
        (bool?)standingGoal?["executionCurrent"] != true;

    private bool TryContinueSceneObservationGoal(RoomTaskProtocol.Decision decision, out JObject goal, out string failure)
    {
        goal = null; failure = null;
        if (string.IsNullOrEmpty(decision.observationGoalId)) return true;
        var context = SceneObjectTargetContext();
        var pending = context?["observationGoal"] as JObject;
        if (pending == null || (string)pending["goalId"] != decision.observationGoalId ||
            (string)pending["status"] == "resolved")
        { failure = "observation-goal-reference-not-pending"; return false; }
        var mainGoal = CurrentSceneObjectSpatialGoal();
        string parent = (string)pending["parentSpatialGoalId"];
        bool independentCurrent = IsCurrentIndependentSceneObservationGoal(pending, mainGoal);
        if (!independentCurrent && (mainGoal != null || !string.IsNullOrEmpty(parent)) &&
            (mainGoal == null || parent != (string)mainGoal["goalId"]))
        { failure = "observation-goal-parent-spatial-goal-mismatch"; return false; }
        if (!independentCurrent && mainGoal != null && (bool?)mainGoal["executionCurrent"] != true)
        { failure = "observation-goal-parent-execution-not-current"; return false; }
        if (decision.origin == "autonomous" && (string)pending["status"] != "pending")
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
        var mainGoal = CurrentSceneObjectSpatialGoal();
        if ((bool?)mainGoal?["executionCurrent"] == true) return BoundObjectGoal((string)mainGoal["originalRequest"]);
        string current = SceneObjectCurrentRequest(decision);
        return BoundObjectGoal(string.IsNullOrWhiteSpace(current) ? decision.visualTarget?.label : current);
    }

    private string SceneObjectCurrentRequest(RoomTaskProtocol.Decision decision) =>
        BoundObjectGoal(decision.origin == "autonomous" ? m_SceneAutonomyIntention : m_ActionConstraints.CurrentUserText);

    private JObject BuildSceneObjectMatchInput(RoomTaskProtocol.Decision decision, SceneActionFact fact,
        SceneObservedObject initial, SceneObjectCandidateView view, JObject mainGoal)
    {
        var previousReview = SceneObjectTargetContext();
        if (previousReview != null)
        {
            // c1..c6 and image points belong to the former composite, even when
            // that former review proposed a different stable entity. Preserve
            // the captured source and interpretation without recycling its labels.
            previousReview.Remove("proposedCandidateId");
            previousReview.Remove("proposedImagePoint");
            foreach (string textField in new[] { "reason", "modelReason" })
                if (previousReview[textField]?.Type == JTokenType.String &&
                    !string.IsNullOrEmpty((string)previousReview[textField]))
                    previousReview[textField] = Regex.Replace((string)previousReview[textField],
                        "(?<![A-Za-z0-9_])c[1-6](?![A-Za-z0-9_])", "[former-image-local-candidate]",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            previousReview["historical"] = true;
            previousReview["localCandidateIdsApplicableToCurrentView"] = false;
            previousReview["historyScope"] = "This is the quoted former capture's review. Stable matched/proposed entity IDs and the interpretation retain their actual source. Old c1..c6 mentions in reason/modelReason are masked in this prompt projection only; the original receipt remains diagnostic evidence. Never derive a current candidateId from this history.";
        }
        bool mainExecution = (bool?)mainGoal?["executionCurrent"] == true;
        var pending = previousReview?["observationGoal"] as JObject;
        bool continuable = pending != null && ((string)pending["status"] == "pending" ||
            (decision.origin != "autonomous" && (string)pending["status"] == "historical")) &&
            (IsCurrentIndependentSceneObservationGoal(pending, mainGoal) ||
                (mainGoal == null ? string.IsNullOrEmpty((string)pending["parentSpatialGoalId"]) :
                    mainExecution && (string)pending["parentSpatialGoalId"] == (string)mainGoal["goalId"]));
        return new JObject {
            ["intent"] = decision.intent, ["targetPurpose"] = fact.targetPurpose,
            ["originalGoal"] = fact.targetDescription,
            ["observationGoalId"] = decision.observationGoalId,
            ["currentRequest"] = SceneObjectCurrentRequest(decision),
            ["latestUserRequest"] = BoundObjectGoal(m_ActionConstraints.CurrentUserText),
            ["spatialGoal"] = mainExecution ? mainGoal.DeepClone() : null,
            ["standingGoal"] = mainExecution ? null : mainGoal?.DeepClone(),
            ["verifiedDeviceReferentHistory"] = BuildVerifiedDeviceReferentHistory().DeepClone(),
            ["verifiedObservedDeviceHistory"] = BuildVerifiedObservedDeviceReferentHistory(),
            ["referentSources"] = BuildSpatialGoalReferentSources(),
            ["requestedDescription"] = decision.visualTarget?.label,
            ["requestedEntityId"] = decision.entityTarget?.entityId,
            ["initialGeometryEntityId"] = initial?.EntityId,
            ["explicitReselection"] = decision.RequestsTargetReselection,
            ["recentDialogue"] = JArray.Parse(RecentRoomDialogue()),
            ["previousTargetReview"] = previousReview,
            ["pendingObservationGoal"] = continuable ? pending.DeepClone() : null,
            ["candidates"] = JObject.Parse(view.MetadataJson),
            ["instructionScope"] = "When spatialGoal is present, originalGoal is its retained main task and latestUserRequest/currentRequest preserve the newest words separately. A same-parent observation goal cannot replace that main purpose. standingGoal is suspended background only: it never overrides the originalGoal of this independent current request and cannot authorize its device actions or observation-child continuation. referentSources distinguishes verified observation sources from verified operated-goal sources. These and their histories resolve the requested identity only; a boundEntityId must remain the same entity even after brightness or power state changes. History does not assert present visibility, capabilities, reachability or execution permission. Confirm a CURRENT offered candidate in this exact image, decline if the evidence remains insufficient, and never select an absent entity from history. A prior clicked entity or label alone is not proof of the user's referent. On reselect, discard the old descriptor-to-entity association, keep real history and the independently reviewed bound goal identity. For observe-closer, match the larger visible REFERENCE independently of the main bound target; this does not bind or identify the small original target. No action is performed by this review."
        };
    }

    private IEnumerator MatchSceneObjectTarget(RoomTaskProtocol.Decision decision, SceneVisionObservation observation,
        SceneObservedObject initial, SceneActionFact fact, Func<bool> current,
        Action<SceneObservedObject, string> completed)
    {
        var model = m_ChatSettings.m_ChatModel;
        int owner = ++m_SceneObjectMatchOwner;
        if (current() && TryReuseSceneTargetIdentity(decision, observation, initial, fact))
        { completed(initial, null); yield break; }
        m_SceneTargetIdentity = null;
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
        Vector2 focus = decision.visualTarget != null ? new Vector2(decision.visualTarget.x, decision.visualTarget.y) :
            initial?.ImagePoint ?? new Vector2(.5f, .5f);
        if (!m_SceneVisionCapture.TryBuildObjectCandidateView(observation, focus,
            decision.RequestsTargetReselection ? null : initial?.EntityId, out var view, out string failure))
        { completed(null, "object-candidate-view-unavailable: " + failure); yield break; }
        if (!current()) yield break;
        m_SceneObjectCandidateView = view;
        fact.targetPurpose = decision.IsObservationApproach ? "observe-closer" : "direct-target";
        fact.observationGoalId = decision.observationGoalId;
        var mainGoal = CurrentSceneObjectSpatialGoal();
        fact.targetDescription = SceneObjectGoal(decision);
        if (continuingGoal != null && (bool?)mainGoal?["executionCurrent"] != true)
            fact.targetDescription = BoundObjectGoal((string)continuingGoal["originalGoal"]);
        fact.matchCaptureId = view.CaptureId;
        var input = BuildSceneObjectMatchInput(decision, fact, initial, view, mainGoal);
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
            if ((string)CurrentSceneObjectSpatialGoal()?["goalId"] != (string)mainGoal?["goalId"])
            { completed(null, "object-target-main-goal-changed-during-match"); yield break; }
            RoomTaskProtocol.ObjectMatch match = null;
            bool parsed = received && accepted && RoomTaskProtocol.TryObjectMatch(output,
                view.Candidates.Select(x => x.candidateId), out match);
            fact.targetMatchVerdict = parsed ? match.verdict : "unavailable";
            fact.targetMatchReason = parsed ? match.reason : status;
            fact.semanticTargetConfirmed = false;
            fact.needsCloserObservation = parsed && match.verdict == "uncertain";
            bool observationLandmark = parsed && decision.IsObservationApproach &&
                match.verdict == "matched" && match.candidateRole == "landmark" && match.candidateId != "none";
            bool acceptableSelection = parsed && (match.IsMatched || observationLandmark);
            if (parsed && match.verdict == "matched" && !acceptableSelection)
            {
                fact.targetMatchVerdict = "different-target";
                fact.targetMatchReason = "candidate-is-not-the-requested-target; model reason: " + match.reason;
            }
            SceneObservedObject selected = null;
            SceneObservedObject proposed = null;
            if (parsed && !acceptableSelection && match.candidateId != "none")
                view.TryGetCandidate(match.candidateId, out proposed, out _);
            if (acceptableSelection && !view.TryGetCandidate(match.candidateId, out selected, out failure))
            { fact.targetMatchVerdict = "expired"; fact.targetMatchReason = failure; }
            bool boundConflict = selected != null && !decision.IsObservationApproach &&
                (bool?)mainGoal?["executionCurrent"] == true && !string.IsNullOrEmpty((string)mainGoal["boundEntityId"]) &&
                selected.EntityId != (string)mainGoal["boundEntityId"];
            if (boundConflict)
            {
                // A currently visible object may still be the wrong referent.
                // Do not focus it or repair the model's candidateId from prose;
                // retain the proposal only as a rejected current-frame cue.
                proposed = selected; selected = null;
                fact.targetMatchVerdict = "uncertain";
                fact.targetMatchReason = "matched-candidate-conflicts-with-current-bound-entity; model reason: " + match.reason;
                fact.needsCloserObservation = true;
            }
            // A selected observation landmark is useful for approaching a better
            // viewpoint, but is never semantic proof for the original small target.
            fact.semanticTargetConfirmed = selected != null && !decision.IsObservationApproach && match.IsMatched;
            var bridge = GetComponent<ArdyRoomDialogueBridge>();
            m_SceneMatchEnvironment = bridge.environmentRoot; m_SceneMatchAvatar = bridge.avatar;
            m_SceneMatchUserTurn = m_ActionConstraints.CurrentUserTurn;
            if (selected != null && decision.IsObservationApproach)
                m_SceneObservationGoal = new JObject {
                    ["goalId"] = (string)continuingGoal?["goalId"] ?? "observation-goal-" + Guid.NewGuid().ToString("N"),
                    ["parentSpatialGoalId"] = (bool?)mainGoal?["executionCurrent"] == true ? mainGoal["goalId"].DeepClone() : null,
                    ["status"] = "pending",
                    ["originalGoal"] = fact.targetDescription,
                    ["referenceEntityId"] = selected.EntityId, ["referenceDescription"] = decision.visualTarget?.label,
                    ["approachActionId"] = fact.actionId, ["userTurnAtCreation"] = m_ActionConstraints.CurrentUserTurn,
                    ["needsOriginalTargetRecheck"] = true,
                    ["scope"] = "Unresolved observation objective, separate from the larger reference. Arrival/reobservation of that reference does not resolve the main target. A parentSpatialGoalId permits continuation only under that same current logical device goal, across ordinary user wording changes; another/inactive main goal supersedes it. Unparented legacy observations require explicit goalId continuation. Current images and semantic review are always required."
                };
            m_SceneObjectMatchReceipt = new JObject {
                ["actionId"] = fact.actionId, ["captureId"] = view.CaptureId,
                ["originalGoal"] = fact.targetDescription, ["requestedDescription"] = decision.visualTarget?.label,
                ["parentSpatialGoalId"] = (bool?)mainGoal?["executionCurrent"] == true ? mainGoal["goalId"].DeepClone() : null,
                ["targetPurpose"] = fact.targetPurpose, ["verdict"] = fact.targetMatchVerdict,
                ["associationCurrent"] = selected != null,
                ["reason"] = fact.targetMatchReason, ["semanticTargetConfirmed"] = fact.semanticTargetConfirmed,
                ["modelVerdict"] = parsed ? match.verdict : null,
                ["candidateRole"] = parsed ? match.candidateRole : null,
                ["observationLandmarkConfirmed"] = observationLandmark && selected != null,
                ["modelReason"] = parsed ? match.reason : null,
                ["boundGoalEntityConflict"] = boundConflict,
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
            if (!decision.IsObservationApproach)
            {
                RememberSceneVisibleSemanticMatch(selected, fact.targetDescription, fact.matchCaptureId);
                RememberSceneTargetIdentity(selected, mainGoal);
            }
            completed(selected, null);
        }
        finally { if (owner == m_SceneObjectMatchOwner) model.CancelSceneObjectMatchMessage(); }
    }
}
