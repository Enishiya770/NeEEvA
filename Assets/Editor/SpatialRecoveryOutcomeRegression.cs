using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NeEEvA.Motion;
using Newtonsoft.Json.Linq;
using UniVRM10;
using UnityEngine;

// Replays the logged boundary with real production measurements/receipts and
// controlled controller states. No generated motion, model HTTP or audio claim.
public static class SpatialRecoveryOutcomeRegression
{
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static void Check(bool condition, string message)
    { ++checks; if (!condition) throw new InvalidOperationException(message); }
    public static JObject RunChecks()
    {
        checks = 0;
        ArrivalChecks(); OutcomeChecks(); ReviewChecks(); ServiceChecks();
        return new JObject { ["passed"] = true, ["checks"] = checks,
            ["scope"] = "Real production measured-arrival ownership, historical outcome projection, receipt and strict review/service parsing. Controlled controller states; no HTTP/native motion/audio." };
    }

    private static void ArrivalChecks()
    {
        using (var f = new ArrivalFixture())
        {
            Call(f.Bridge, "ObserveApproachArrival");
            Check(f.Bridge.LastApproachArrival != null && !f.Bridge.LastApproachArrival.terminalConfirmed,
                "Returning at the actual logged target records measurement but is not terminal success.");
            f.User.position += new Vector3(-.00332916f, .08895845f, -.3181405f);
            Property(f.Controller, "ExecutionState", ArdyRoomExecutionState.Arrived);
            Property(f.Controller, "ExecutionReason", ArdyRoomExecutionReason.Completed);
            Check((bool)Call(f.Bridge, "ConfirmApproachArrival"),
                "c7: movement .318m after measured arrival does not erase the accepted snapshot's completed result.");
            var proof = f.Bridge.LastApproachArrival;
            Check(proof.terminalConfirmed && proof.sampledUserDistance > 1.19f && proof.sampledUserDistance < 1.20f &&
                Vector3.Distance(proof.sampledUserHead, f.User.position) > .31f && proof.operationRevision == 11,
                "Historical measurement retains sampled user point, actual distance and owned operation.");
            proof.terminalConfirmed = false;
            Check(f.Bridge.LastApproachArrival.terminalConfirmed, "Public arrival facts are detached copies.");
        }
        foreach (string change in new[] { "cancel", "inactive", "operation", "action", "target-revision", "plan", "plan-point", "user", "user-moved-too-far", "distance", "failed", "wrong-facing", "wrong-position", "nonfinite" })
        using (var f = new ArrivalFixture())
        {
            Call(f.Bridge, "ObserveApproachArrival");
            Property(f.Controller, "ExecutionState", ArdyRoomExecutionState.Arrived);
            Property(f.Controller, "ExecutionReason", ArdyRoomExecutionReason.Completed);
            switch (change)
            {
                case "cancel": Call(f.Bridge, "InvalidateApproachArrival", "cancelled"); break;
                case "inactive": Set(f.Bridge, "activeAction", false); break;
                case "operation": Property(f.Controller, "OperationRevision", 12); break;
                case "action": Set(f.Bridge, "actionId", "new-action"); break;
                case "target-revision": Set(f.Bridge, "lastAttemptTargetRevision", 42); break;
                case "plan": Set(f.Bridge, "plan", new ArdyRoomApproachPlan()); break;
                case "plan-point": Property(f.Plan, "GroundTarget", f.Plan.GroundTarget + Vector3.right); break;
                case "user": f.Bridge.userHead = f.Bridge.transform; break;
                case "user-moved-too-far": f.User.position += Vector3.back; break;
                case "distance": f.Bridge.approachDistance = 1.2f; break;
                case "failed": Property(f.Controller, "ExecutionReason", ArdyRoomExecutionReason.GenerationFailed); break;
                case "wrong-facing": f.Bridge.avatar.transform.rotation = Quaternion.LookRotation(-f.Forward); break;
                case "wrong-position": f.Bridge.avatar.transform.position += Vector3.right; break;
                case "nonfinite": f.Bridge.approachDistance = float.NaN; break;
            }
            Check(!(bool)Call(f.Bridge, "ConfirmApproachArrival") && !f.Bridge.LastApproachArrival.terminalConfirmed,
                "Arrival cannot swallow invalidation or changed measured goal: " + change);
        }
        using (var f = new ArrivalFixture())
        {
            f.Bridge.avatar.transform.position += Vector3.right * 2f;
            Property(f.Controller, "ExecutionState", ArdyRoomExecutionState.Arrived);
            Property(f.Controller, "ExecutionReason", ArdyRoomExecutionReason.Completed);
            Check(!(bool)Call(f.Bridge, "ConfirmApproachArrival") && f.Bridge.LastApproachArrival == null,
                "Controller Completed without actual matching measurements cannot complete approach.");
        }
        using (var f = new ArrivalFixture())
        {
            Property(f.Plan, "GroundTarget", f.Plan.GroundTarget + Vector3.right);
            Call(f.Bridge, "ObserveApproachArrival");
            Check(f.Bridge.LastApproachArrival == null, "Changed submission target before the first measurement cannot borrow the old operation.");
        }
        foreach (bool completedBeforeBoundary in new[] { false, true })
        using (var f = new ArrivalFixture())
        {
            var chat = f.Bridge.gameObject.AddComponent<ChatSample>(); f.Bridge.chat = chat;
            Set(chat, "m_AgentRunning", false); Set(chat, "m_EnableSceneObjectAwareness", false);
            Call(f.Bridge, "BindApproachSubmission"); Call(f.Bridge, "ObserveApproachArrival");
            Property(f.Controller, "ExecutionState", ArdyRoomExecutionState.Arrived);
            Property(f.Controller, "ExecutionReason", ArdyRoomExecutionReason.Completed);
            if (completedBeforeBoundary)
            {
                Check((bool)Call(f.Bridge, "ConfirmApproachArrival"), "Controlled measured arrival completes before session change.");
                Check(((JObject)Call(chat, "BuildRecentActionOutcomes"))["lastMeasuredApproachArrival"].Type == JTokenType.Object,
                    "Current-session measured arrival reaches ordinary explanatory facts.");
            }
            Call(chat, "BeginSceneTimeSession");
            Check(((JObject)Call(chat, "BuildRecentActionOutcomes"))["lastMeasuredApproachArrival"].Type == JTokenType.Null,
                "New scene session excludes an old arrival even within the ten-minute lifetime.");
            Check(!(bool)Call(f.Bridge, "ConfirmApproachArrival"), "An old operation cannot finish into a new session.");
            Check(f.Bridge.LastApproachArrival.terminalConfirmed == completedBeforeBoundary,
                "Session boundary excludes stale context without erasing a genuine historical success.");
        }
    }

    private static void OutcomeChecks()
    {
        var host = new GameObject("Spatial outcome history fixture") { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            var chat = host.AddComponent<ChatSample>();
            Set(chat, "m_AgentRunning", false); Set(chat, "m_EnableSceneObjectAwareness", false);
            const string clearance = "用户位于角色身体净距内，未开始原地转身。";
            Call(chat, "RecordSceneActivity", "lamp-attempt-1", "reobserve-object", "原灯", "rejected", clearance);
            Call(chat, "RecordSceneActivity", "lamp-attempt-2", "reobserve-object", "原灯", "rejected", clearance);
            Call(chat, "RecordSceneActivity", "lamp-success", "set-object-state", "原灯", "performed", "component readback on");
            JObject history = (JObject)Call(chat, "BuildRecentActionOutcomes");
            Check(((JArray)history["events"]).Count == 3 && (string)history["events"][0]["failureCategory"] == "user-clearance",
                "Two actual clearance failures survive a later successful lamp operation.");
            Check(history["events"].All(v => (bool)v["executionPermission"] == false &&
                ((string)v["currentApplicability"]).StartsWith("historical-only") && !string.IsNullOrEmpty((string)v["observedUtc"])),
                "Each past result is timestamped history without new execution authority/current-obstacle claims.");
            history["events"][0]["observedReason"] = "tampered";
            Check((string)((JObject)Call(chat, "BuildRecentActionOutcomes"))["events"][0]["observedReason"] == clearance,
                "Context consumers cannot mutate retained engine facts.");
            JObject fast = JObject.Parse((string)Call(chat, "BuildFastLoopContext"));
            Check(((JArray)fast["recentActionOutcomes"]["events"]).Count == 3,
                "The real ordinary FastLoop builder receives recent concrete failure facts, without General-only context.");
            Call(chat, "RecordSceneActivity", "lamp-success", "set-object-state", "原灯", "cancelled", "new gaze owner");
            history = (JObject)Call(chat, "BuildRecentActionOutcomes");
            Check(history["events"].Any(v => (string)v["actionId"] == "lamp-success" && (string)v["phase"] == "performed"),
                "A later same-action cancellation does not erase an earlier verified physical effect.");
            for (int i = 0; i < 12; i++)
                Call(chat, "RecordSceneActivity", "bounded-" + i, "inspect-object", "", "failed", new string('x', 500));
            history = (JObject)Call(chat, "BuildRecentActionOutcomes");
            Check(((JArray)history["events"]).Count == 6 && history["events"].All(v => ((string)v["observedReason"]).Length <= 281),
                "Historical failures have fixed count and text limits.");
            var retained = (List<JObject>)Get(chat, "m_RecentActionOutcomes");
            foreach (var item in retained) item["observedAtRealtime"] = Time.realtimeSinceStartup - 601f;
            Check(!((JArray)((JObject)Call(chat, "BuildRecentActionOutcomes"))["events"]).Any(), "Expired history cannot masquerade as current context.");
            foreach (var item in retained) { item["observedAtRealtime"] = Time.realtimeSinceStartup; item["sceneSession"] = -900; }
            Check(!((JArray)((JObject)Call(chat, "BuildRecentActionOutcomes"))["events"]).Any(), "A different activity session excludes old failures.");

            foreach (string language in new[] { "zh", "ja", "en" })
            {
                var plan = FailedPlan("reobserve-object", clearance);
                string receipt = (string)Call(chat, "ActorPlanClosureText", plan, language, false);
                Check(!string.IsNullOrEmpty(receipt) && (language == "zh" ? receipt.Contains("安全间距") :
                    language == "ja" ? receipt.Contains("安全") : receipt.Contains("clearance")),
                    "Receipt exposes the verified safety reason in the selected language: " + language);
            }
            var compound = new ActorPlanRuntime("先转身再开灯", "actor_draft", "compound");
            Check(compound.Propose(1, "turn-in-place", "开灯", "turn", out _) && compound.BindAction(1, "turn-done") &&
                compound.Resolve(1, "turn-done", true, "observed", "measured turn", null, false), "Controlled first step completes normally.");
            Check(compound.Propose(2, "set-object-state", "", "lamp", out _) && compound.BindAction(2, "lamp-fail") &&
                compound.Resolve(2, "lamp-fail", false, "rejected", clearance, null, false), "Controlled independent tail remains failed.");
            string completeThenFail = (string)Call(chat, "ActorPlanClosureText", compound, "zh", false);
            Check(completeThenFail.Contains("前面的步骤已经确认完成") && completeThenFail.Contains("安全间距") && compound.State == "blocked",
                "A specific later failure neither erases earlier success nor marks the remaining compound goal complete.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static ActorPlanRuntime FailedPlan(string intent, string reason)
    {
        var plan = new ActorPlanRuntime("重新打开原灯", "actor_draft", "atomic");
        Check(plan.Propose(1, intent, "重新打开原灯", "attempt", out _) && plan.BindAction(1, "failed-action") &&
            plan.Resolve(1, "failed-action", false, "rejected", reason, "lamp", false), "Failure receipt uses an actually resolved plan result.");
        return plan;
    }

    private static void ReviewChecks()
    {
        const string draft = "我先走到你身边，再一直跟着你。";
        foreach (string task in new[] { "follow", "approach" })
        {
            var review = new ActorIntentReviewDecision { Status = "adopted_action", Goal = draft, Evidence = draft,
                Coverage = "compound", Task = task, Target = "current_user", Trigger = "none" };
            Check(ActorIntentProtocol.CompileReview(review, draft, out var result, out _) && result.Task == "other" &&
                result.Kind == "replan" && result.Coverage == "compound" && result.Goal == draft && result.Evidence == draft,
                "Compound shortcut metadata is normalized solely to full-goal planner routing: " + task);
            review.Trigger = "deferred";
            Check(!ActorIntentProtocol.CompileReview(review, draft, out _, out _), "Deferred compound review does not gain immediate authorization.");
            review.Trigger = "none"; review.Evidence = "一直跟着你";
            Check(!ActorIntentProtocol.CompileReview(review, draft, out _, out _), "Partial evidence cannot repair into a new full goal.");
        }
        Check(!ActorIntentProtocol.CompileReview(new ActorIntentReviewDecision { Status = "uncertain", Goal = "", Evidence = "",
            Coverage = "none", Task = "none", Target = "none" }, draft, out _, out _), "Uncertain review remains nonexecuting.");
        Check(ActorIntentProtocol.CompileReview(new ActorIntentReviewDecision { Status = "non_action", Goal = "", Evidence = draft,
            Coverage = "none", Task = "none", Target = "none" }, draft, out var none, out _) && none.Kind == "none",
            "Explicit non-action is not upgraded to a planner task by routing repair.");
    }

    private static void ServiceChecks()
    {
        const string body = "{\"error\":{\"code\":\"stream_quality_rejected\",\"message\":\"All three native candidates failed\",\"details\":{\"timings\":{\"qualityAttempts\":3,\"qualityCandidates\":[{\"attempt\":1,\"accepted\":false,\"windowMs\":24,\"reasons\":[{\"code\":\"root_step\",\"frame\":3,\"value\":0.18,\"maximum\":0.1}]}]},\"authorization\":\"SECRET\"}}}";
        var error = (ArdyServiceErrorObservation)Call(typeof(ArdyRoomLocomotionController), "ReadServiceErrorObservation", body, 422L, "request-422", 59);
        Check(error.code == "stream_quality_rejected" && error.httpStatus == 422 && error.requestId == "request-422" && error.operationRevision == 59 &&
            error.qualityAttempts == 3 && error.candidates.Length == 1 && error.candidates[0].reasons[0].code == "root_step" &&
            Math.Abs(error.candidates[0].reasons[0].value - .18f) < .0001f, "422 preserves exact request identity and bounded real committed-prefix quality reasons.");
        Check(!JsonUtility.ToJson(error).Contains("SECRET"), "Unknown details, including authorization fields, are never reflected into diagnostics.");
        foreach (string raw in new[] { "<html>502 stream_quality_rejected</html>", "", new string('x', 32769) })
        {
            var malformed = (ArdyServiceErrorObservation)Call(typeof(ArdyRoomLocomotionController), "ReadServiceErrorObservation", raw, 502L, "request-bad", 60);
            Check(malformed.code == "" && malformed.detailOmitted && malformed.candidates.Length == 0, "Malformed or excessive errors do not invent quality evidence.");
        }
        string secret = "{\"error\":{\"code\":\"generation_unavailable\",\"message\":\"Authorization: Bearer SECRET sk-SECRET token=SECRET\"}}";
        var redacted = (ArdyServiceErrorObservation)Call(typeof(ArdyRoomLocomotionController), "ReadServiceErrorObservation", secret, 422L, "id", 1);
        Check(!redacted.message.Contains("SECRET"), "Common credential forms in service prose are redacted.");
        var exceptionType = typeof(ArdyRoomLocomotionController).GetNestedType("ServiceRequestException", BindingFlags.NonPublic);
        Exception New(string raw) => (Exception)Activator.CreateInstance(exceptionType, F, null, new object[] { "HTTP422", raw }, null);
        Check((string)Call(typeof(ArdyRoomLocomotionController), "StreamFailureCategory", New(body)) == "motion-quality", "Exact machine code classifies the service quality rejection.");
        Check((string)Call(typeof(ArdyRoomLocomotionController), "StreamFailureCategory", New(secret.Replace("Authorization: Bearer SECRET sk-SECRET token=SECRET", "stream_quality_rejected appears in prose"))) == "service-request",
            "Retained server prose cannot change a different machine code into motion-quality.");
    }

    private sealed class ArrivalFixture : IDisposable
    {
        private readonly GameObject host, avatarHost, userHost;
        public readonly ArdyRoomDialogueBridge Bridge;
        public readonly ArdyRoomLocomotionController Controller;
        public readonly ArdyRoomApproachPlan Plan;
        public Transform User => userHost.transform;
        public readonly Vector3 Forward;
        public ArrivalFixture()
        {
            host = new GameObject("Measured arrival fixture") { hideFlags = HideFlags.HideAndDontSave };
            avatarHost = new GameObject("Measured avatar") { hideFlags = HideFlags.HideAndDontSave };
            userHost = new GameObject("Sampled user") { hideFlags = HideFlags.HideAndDontSave };
            Bridge = host.AddComponent<ArdyRoomDialogueBridge>(); Bridge.connectOnStart = false;
            Controller = host.AddComponent<ArdyRoomLocomotionController>();
            Bridge.avatar = avatarHost.AddComponent<Vrm10Instance>(); Bridge.userHead = User;
            Bridge.importedReference = new ArdyLocomotionAvatarReference { groundY = 0 };
            Vector3 ground = new Vector3(-2.16070056f, -.69721955f, -6.69061947f);
            Vector3 sampled = new Vector3(-1.88533866f, .73362207f, -7.852633f);
            Forward = sampled - ground; Forward.y = 0;
            avatarHost.transform.SetPositionAndRotation(ground, Quaternion.LookRotation(Forward)); User.position = sampled;
            Plan = new ArdyRoomApproachPlan();
            Property(Plan, "GroundTarget", new Vector3(-2.1428566f, -.7298001f, -6.68059158f));
            Property(Plan, "FacePoint", sampled);
            Set(Bridge, "plan", Plan); Set(Bridge, "controller", Controller); Set(Bridge, "activeAction", true);
            Set(Bridge, "hasControllerOperation", true); Set(Bridge, "operationRevision", 11); Set(Bridge, "lastAttemptTargetRevision", 7);
            Set(Bridge, "actionId", "logged-c7-approach"); Set(Bridge, "boundUser", User);
            Property(Controller, "OperationRevision", 11); Property(Controller, "ExecutionState", ArdyRoomExecutionState.Returning);
            Call(Bridge, "BindApproachSubmission");
        }
        public void Dispose()
        {
            Set(Bridge, "activeAction", false); Set(Bridge, "controller", null);
            UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(avatarHost); UnityEngine.Object.DestroyImmediate(userHost);
        }
    }
    private static object Get(object owner, string name) => owner.GetType().GetField(name, F).GetValue(owner);
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, F).SetValue(owner, value);
    private static void Property(object owner, string name, object value) => owner.GetType().GetProperty(name, F).SetValue(owner, value);
    private static object Call(object owner, string name, params object[] args)
    {
        Type type = owner as Type ?? owner.GetType();
        return type.GetMethods(F).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(owner is Type ? null : owner, args);
    }
}
