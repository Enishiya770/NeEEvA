using System;
using System.Collections;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Real empty-final cleanup and provider generations, no network or microphone.
public static class AuxiliaryCancellationOwnershipRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject RunChecks()
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new InvalidOperationException(message); }
        var host = new GameObject("Inactive auxiliary ownership regression") { hideFlags = HideFlags.HideAndDontSave };
        host.SetActive(false);
        UnityWebRequest roleRequest = null, reviewRequest = null, listeningRequest = null;
        try
        {
            var model = host.AddComponent<ChatQW>();
            var chat = host.AddComponent<ChatSample>();
            Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model });
            Set(chat, "m_LogStreamTimings", false);
            Set(chat, "m_FormalResponseInFlight", true);
            Set(chat, "m_FormalResponseGeneration", 12);
            Set(model, "m_RoleDecisionGeneration", 53);
            // Unsent request: observing whether the real cancellation path retains
            // the object and generation does not contact this loopback address.
            roleRequest = new UnityWebRequest("http://127.0.0.1:1/not-sent", "POST");
            Set(model, "m_RoleDecisionRequest", roleRequest);
            int oldAux = (int)Get(model, "m_EphemeralGeneration");
            Set(model, "m_ListeningEphemeralGeneration", oldAux);
            Set(chat, "m_SpeculativeRequestInFlight", true);
            int callbacks = 0;
            var oldListening = (IEnumerator)Call(model, "RequestEphemeralPayload", "{}", oldAux,
                new Action<string>(_ => callbacks++));
            // Same ordering as c6/c7: the preceding accepted turn owns the
            // RoleDecision; a new unconfirmed recording returns empty final.
            Call(chat, "DealingTextCallback", "", false);
            Check((int)Get(model, "m_RoleDecisionGeneration") == 53 &&
                ReferenceEquals(Get(model, "m_RoleDecisionRequest"), roleRequest),
                "Rejected empty capture cancelled the preceding formal skill route.");
            Check((bool)Get(chat, "m_FormalResponseInFlight") && (int)Get(chat, "m_FormalResponseGeneration") == 12,
                "Rejected provisional capture invalidated its predecessor's formal owner.");
            Check((int)Get(model, "m_EphemeralGeneration") > oldAux && !oldListening.MoveNext() && callbacks == 0,
                "Listening cleanup failed to fence its own late callback before HTTP.");
            model.CancelEphemeralMsg();
            Check((int)Get(model, "m_RoleDecisionGeneration") == 53 &&
                ReferenceEquals(Get(model, "m_RoleDecisionRequest"), roleRequest),
                "Repeated auxiliary cleanup crossed into the formal routing lane.");

            // A goal review has since replaced the old listening generation.
            // Empty final is not permission to cancel this newer auxiliary owner.
            int reviewGeneration = (int)Get(model, "m_EphemeralGeneration");
            reviewRequest = new UnityWebRequest("http://127.0.0.1:1/review-not-sent", "POST");
            Set(model, "m_EphemeralRequest", reviewRequest);
            Set(model, "m_ListeningEphemeralGeneration", reviewGeneration - 1);
            var reviewTrace = new ConversationTrace("goal-review", _ => callbacks++);
            Set(model, "m_ReviewOwnerTrace", reviewTrace);
            Set(model, "m_ReviewWireTrace", reviewTrace.Child("review-http"));
            var oldReview = (IEnumerator)Call(model, "RequestWorkReviewPayload", "{}", reviewGeneration,
                new Action<string>(_ => callbacks++), reviewTrace);
            Set(chat, "m_SpeculativeRequestInFlight", false);
            Call(chat, "DealingTextCallback", "", false);
            Check((int)Get(model, "m_EphemeralGeneration") == reviewGeneration &&
                ReferenceEquals(Get(model, "m_EphemeralRequest"), reviewRequest) && callbacks == 0,
                "Empty capture with no listening request cancelled a pending goal review.");
            Set(chat, "m_SpeculativeRequestInFlight", true); // stale caller flag
            Call(chat, "DealingTextCallback", "", false);
            Check((int)Get(model, "m_EphemeralGeneration") == reviewGeneration &&
                ReferenceEquals(Get(model, "m_EphemeralRequest"), reviewRequest) &&
                ReferenceEquals(Get(model, "m_ReviewOwnerTrace"), reviewTrace) && callbacks == 0,
                "Stale listening flag cancelled or ended a newer review owner.");
            model.CancelListeningMsg();
            Check((int)Get(model, "m_EphemeralGeneration") == reviewGeneration && callbacks == 0,
                "Provider listening token ignored generation ownership.");
            int oldBoundary = (int)Get(model, "m_TurnBoundaryGeneration");
            int newBoundary = (int)Call(model, "BeginTurnBoundaryRequest");
            Check(newBoundary > oldBoundary && (int)Get(model, "m_EphemeralGeneration") == reviewGeneration &&
                ReferenceEquals(Get(model, "m_EphemeralRequest"), reviewRequest) &&
                ReferenceEquals(Get(model, "m_ReviewOwnerTrace"), reviewTrace) && callbacks == 0,
                "Provisional capture boundary dispatch cancelled an unrelated goal review before HTTP.");
            int skippedListening = 0;
            model.PostListeningDraftMsg("controlled candidate", value => { if (value == "") skippedListening++; });
            model.ListeningPreparationRequestId = "controlled-preparation";
            model.PostListeningPreparationMsg("controlled candidate", value => { if (value == "") skippedListening++; });
            Check(skippedListening == 2 && callbacks == 0 &&
                (int)Get(model, "m_EphemeralGeneration") == reviewGeneration &&
                ReferenceEquals(Get(model, "m_EphemeralRequest"), reviewRequest) &&
                model.ListeningPreparationRequestId == null,
                "Low-priority listening dispatch replaced a live accepted review instead of returning empty preparation.");
            // A review claims its purpose before coroutine creation, so neither
            // a not-yet-installed request object nor synchronous callbacks create
            // a gap in that ownership. Trace-only ownership is protected too.
            Set(model, "m_EphemeralRequest", null);
            Set(model, "m_EphemeralPurpose", "work-review");
            model.PostListeningDraftMsg("controlled candidate", value => { if (value == "") skippedListening++; });
            Check(skippedListening == 3 && callbacks == 0 &&
                (int)Get(model, "m_EphemeralGeneration") == reviewGeneration &&
                ReferenceEquals(Get(model, "m_ReviewOwnerTrace"), reviewTrace),
                "Pending review ownership was lost before its transport object was installed.");
            Set(model, "m_EphemeralPurpose", "");
            model.PostListeningPreparationMsg("controlled candidate", value => { if (value == "") skippedListening++; });
            Check(skippedListening == 4 && callbacks == 0 &&
                (int)Get(model, "m_EphemeralGeneration") == reviewGeneration,
                "Existing review trace ownership was ignored without a request object.");
            Set(model, "m_EphemeralRequest", reviewRequest);

            // Explicit conversational cancellation retains the original broad
            // authority; it must revoke the actual review and its late callback.
            Set(chat, "m_AutonomyProbeInFlight", true);
            Call(chat, "CancelAutonomyIntentProbe");
            Check((int)Get(model, "m_EphemeralGeneration") > reviewGeneration &&
                Get(model, "m_EphemeralRequest") == null && !oldReview.MoveNext(),
                "Explicit user/autonomy cancellation did not revoke the review.");
            callbacks = 0; // explicit cancellation legitimately emitted trace events

            int listeningGeneration = (int)Get(model, "m_EphemeralGeneration");
            Set(model, "m_ListeningEphemeralGeneration", listeningGeneration);
            listeningRequest = new UnityWebRequest("http://127.0.0.1:1/listening-not-sent", "POST");
            Set(model, "m_EphemeralRequest", listeningRequest);
            var supersededListening = (IEnumerator)Call(model, "RequestEphemeralPayload", "{}", listeningGeneration,
                new Action<string>(_ => callbacks++));
            Call(model, "BeginTurnBoundaryRequest");
            Check((int)Get(model, "m_EphemeralGeneration") > listeningGeneration &&
                Get(model, "m_EphemeralRequest") == null && !supersededListening.MoveNext() && callbacks == 0,
                "Boundary dispatch must still revoke its own listening preparation and late callback.");
            Check((int)Get(model, "m_RoleDecisionGeneration") == 53 &&
                ReferenceEquals(Get(model, "m_RoleDecisionRequest"), roleRequest),
                "Boundary dispatch crossed into the accepted skill route.");

            var oldRole = (IEnumerator)Call(model, "RequestRoleDecisionPayload", "{}", 53,
                new Action<bool, string, string>((ok, output, status) => callbacks++), null);
            model.CancelActiveResponse();
            Check((int)Get(model, "m_RoleDecisionGeneration") > 53 && Get(model, "m_RoleDecisionRequest") == null,
                "Explicit formal cancellation no longer revokes the skill route.");
            Check(!oldRole.MoveNext() && callbacks == 0,
                "Cancelled formal route reached HTTP or delivered a stale callback.");
            int replacement = (int)Get(model, "m_RoleDecisionGeneration");
            model.CancelRoleDecisionMsg();
            Check((int)Get(model, "m_RoleDecisionGeneration") == replacement + 1,
                "Explicit role replacement lost its independent cancellation fence.");
            return new JObject { ["passed"] = true, ["checks"] = checks,
                ["scope"] = "Production empty-final cleanup and actual ChatQW ownership; unsent request objects, no network, microphone or playback. Real confirmed-input takeover is also covered by InputCandidateConfirmationRegression." };
        }
        finally
        {
            // Detach before disposal so OnDisable cannot touch a disposed request.
            var model = host.GetComponent<ChatQW>();
            if (model != null)
            {
                Set(model, "m_RoleDecisionRequest", null);
                Set(model, "m_EphemeralRequest", null);
                Set(model, "m_ReviewWireTrace", null);
                Set(model, "m_ReviewOwnerTrace", null);
            }
            roleRequest?.Dispose();
            reviewRequest?.Dispose();
            listeningRequest?.Dispose();
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    private static object Get(object target, string name) => target.GetType().GetField(name, Flags).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags).SetValue(target, value);
    private static object Call(object target, string name, params object[] arguments)
    {
        var candidates = Array.FindAll(target.GetType().GetMethods(Flags), m => m.Name == name && m.GetParameters().Length == arguments.Length);
        if (candidates.Length != 1) throw new InvalidOperationException("Ambiguous production method: " + name);
        try { return candidates[0].Invoke(target, arguments); }
        catch (TargetInvocationException e) { throw e.InnerException ?? e; }
    }
}
