using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Production planner callback repair with actual room observations. The model replies
/// are controlled to reproduce an invalid observation goal; native movement is a separate opt-in.</summary>
public static class SceneDecisionRepairRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const string SeedText = "你有看到电视机柜上的小灯吗？";
    public static JObject LastReport { get; private set; }
    private sealed class Context
    {
        public ChatSample chat; public ArdyRoomDialogueBridge bridge; public NevaSceneVisionIntegrationModel model;
        public List<LLM.SendData> history;
        public SceneObjectLightSwitch lamp; public string directory; public bool native; public int checks;
        public void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException(reason); }
        public void Save()
        {
            LastReport["checks"] = checks; LastReport["lastFact"] = Fact(chat);
            LastReport["lastDispatch"] = (string)Get(chat, "m_RoomTaskDispatch");
            File.WriteAllText(Path.Combine(directory, "report.json"), LastReport.ToString() + "\n");
        }
    }
    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model, string output)
        => Run(chat, bridge, model, output, false);
    public static IEnumerator RunNativeChecks(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model, string output)
        => Run(chat, bridge, model, output, true);

    private static IEnumerator Run(ChatSample chat, ArdyRoomDialogueBridge bridge, NevaSceneVisionIntegrationModel model, string output, bool native)
    {
        if (!Application.isBatchMode || !Application.isPlaying || Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Decision repair regression requires the existing isolated real-room Play fixture.");
        var c = new Context { chat = chat, bridge = bridge, model = model, native = native, history = model.m_DataList,
            directory = Path.Combine(output, native ? "scene-decision-repair-native" : "scene-decision-repair") };
        var savedNonSystem = c.history.Select((message, index) => new { message, index })
            .Where(item => item.message == null || item.message.role != "system").ToArray();
        var savedSystem = c.history.Where(message => message != null && message.role == "system").ToArray();
        Directory.CreateDirectory(c.directory);
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(), ["modelHttpRequests"] = 0,
            ["nativeRequestsEnabled"] = native,
            ["scope"] = "Actual room lamp and current RGB establish the referent. Only planner/matcher replies are controlled. Unknown goal IDs must be rejected before action, followed by one model decision from a new image. No production decision is silently stripped or converted.",
            ["limitations"] = "Controlled model replies do not prove Qwen reliability. Native entry alone performs actual ARDY movement; no actor teleport or device mutation.",
            ["dialogueIsolation"] = new JObject {
                ["scope"] = "Each planner case quotes only the user question actually accepted for this suite's physical seed observation. No assistant confirmation is fabricated. Earlier suite/case user and assistant history is excluded; original non-system message objects are restored in finally without replacing m_DataList.",
                ["seedUserText"] = SeedText, ["originalNonSystemMessages"] = savedNonSystem.Length,
                ["preservedSystemMessages"] = savedSystem.Length, ["originalHistoryRestored"] = false
            } };
        bool awareness = (bool)Get(chat, "m_EnableSceneObjectAwareness"), matching = (bool)Get(chat, "m_EnableSceneObjectTargetMatching");
        object oldGoal = Get(chat, "m_SceneObservationGoal"), oldReview = Get(chat, "m_SceneObjectMatchReceipt");
        var stack = new Stack<IEnumerator>();
        try
        {
            ClearNonSystemHistory(c.history);
            c.Check(bridge != null && bridge.IsConnected && !bridge.ReservesBody && !bridge.IsFollowing, "Actual room starts idle.");
            c.Check(SceneObjectLampCatalog.EnsureRegistered(bridge.environmentRoot, out c.lamp, out string reason) && c.lamp.TryValidate(out reason), "Existing real room lamp: " + reason);
            Set(chat, "m_EnableSceneObjectAwareness", true); Set(chat, "m_EnableSceneObjectTargetMatching", true);
            Set(chat, "m_SceneObservationGoal", null); Set(chat, "m_SceneObjectMatchReceipt", null);
            stack.Push(Exercise(c));
            while (stack.Count > 0)
            {
                object yielded = null;
                try
                {
                    while (stack.Count > 0)
                    {
                        var routine = stack.Peek();
                        if (!routine.MoveNext()) { (routine as IDisposable)?.Dispose(); stack.Pop(); continue; }
                        if (routine.Current is IEnumerator nested) { stack.Push(nested); continue; }
                        yielded = routine.Current; break;
                    }
                }
                catch (Exception error) { LastReport["error"] = error.ToString(); throw; }
                if (stack.Count > 0) yield return yielded;
            }
            LastReport["status"] = "passed";
        }
        finally
        {
            try
            {
                while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
                Call(chat, "CancelSceneAction", "decision-repair-fixture-finished"); Call(chat, "ClearScheduledAgentWake");
                Set(chat, "m_EnableSceneObjectAwareness", awareness); Set(chat, "m_EnableSceneObjectTargetMatching", matching);
                Set(chat, "m_SceneObservationGoal", oldGoal); Set(chat, "m_SceneObjectMatchReceipt", oldReview);
            }
            finally
            {
                ClearNonSystemHistory(c.history);
                foreach (var item in savedNonSystem)
                    c.history.Insert(Math.Min(item.index, c.history.Count), item.message);
                bool restored = ReferenceEquals(model.m_DataList, c.history) && savedNonSystem.All(item =>
                    item.index < c.history.Count && ReferenceEquals(c.history[item.index], item.message));
                bool systemsPreserved = c.history.Where(message => message != null && message.role == "system").SequenceEqual(savedSystem);
                LastReport["dialogueIsolation"]["originalHistoryRestored"] = restored;
                LastReport["dialogueIsolation"]["systemMessagesUnchanged"] = systemsPreserved;
                if ((string)LastReport["status"] == "running" || !restored || !systemsPreserved) LastReport["status"] = "failed";
                c.Save();
                if (!restored || !systemsPreserved)
                    throw new InvalidOperationException("Decision repair fixture did not restore its original history/system ownership.");
            }
        }
    }

    private static IEnumerator Exercise(Context c)
    {
        var chat = c.chat; var model = c.model;
        Vector3 originalRoot = c.bridge.avatar.transform.position;
        var originalPower = JObject.Parse(JsonUtility.ToJson(c.lamp.ReadState()));
        int originalOperation = c.bridge.Controller.OperationRevision;
        yield return Prepare(c);
        SceneVisionObservation source = null;
        yield return Capture(c, "seed-known-lamp", value => source = value);
        c.Check(chat.SceneVisionCapture.TryFindObservedObject(source, c.lamp.EntityId, out var lampPixel, out string reason), "Actual source sees the lamp: " + reason);
        const string seedText = SeedText;
        Call(chat, "RecordAcceptedMotionUserTurn", seedText);
        var seed = new JObject { ["intent"] = "look-at", ["origin"] = "user", ["evidence"] = seedText,
            ["visualTarget"] = new JObject { ["captureId"] = source.CaptureId, ["x"] = lampPixel.ImagePoint.x, ["y"] = lampPixel.ImagePoint.y, ["label"] = "电视机柜上的发光小灯" } };
        c.Check(RoomTaskProtocol.TryDecision(seed.ToString(), seedText, false, out var seedDecision), "Seed direct look is valid.");
        int matchBefore = model.Matches.Count; bool seedDone = false;
        Set(chat, "m_FormalResponseInFlight", true); Set(chat, "m_AgentRoundInFlight", true);
        Call(chat, "BeginSceneAction", seedDecision, source, (int)Get(chat, "m_FormalResponseGeneration"), new Action(() => seedDone = true));
        yield return Wait(() => model.Matches.Count == matchBefore + 1, 5, "actual direct-look matcher");
        CompleteLampMatch(c, model.Matches.Last());
        yield return Wait(() => seedDone, 12, "real head look and new lamp image");
        c.Check((string)Fact(chat)["phase"] == "observed" && (string)Fact(chat)["entityId"] == c.lamp.EntityId,
            "Actual direct look establishes this lamp in a new physical image.");
        c.Check(Get(chat, "m_SceneObservationGoal") == null, "A direct successful look creates no pending observation subgoal.");
        LastReport["seedFact"] = Fact(chat); c.Save();

        foreach (string mode in new[] { "corrected", "second-invalid", "cancelled-late", "stale-evidence" })
        {
            yield return Prepare(c);
            SetSeedOnlyHistory(c);
            int decisions = model.Decisions.Count, matches = model.Matches.Count, operation = c.bridge.Controller.OperationRevision;
            int formal = model.Speech.Count; Vector3 beforeRoot = c.bridge.avatar.transform.position;
            yield return BeginPlanner(c, mode == "stale-evidence" ? "请你靠近他。" : "那你能试着靠近他吗？");
            var first = model.Decisions.Last(); var firstInput = JObject.Parse(first.Input);
            CheckSeedDialogue(c, firstInput, "Initial " + mode);
            c.Check(model.Decisions.Count == decisions + 1 && first.Kind == "room", "One actual initial room planner request.");
            c.Check(firstInput["pendingObservationGoal"] == null || firstInput["pendingObservationGoal"].Type == JTokenType.Null,
                "Current known entity is not exposed as an unresolved observation goal.");
            c.Check(firstInput["decisionContractFacts"]?["allowedObservationGoalIds"] is JArray goalIds && goalIds.Count == 0,
                "Current input explicitly lists no legal observation-goal IDs for the known direct-look lamp.");
            var initialWire = ApproachWire(c, firstInput, true);
            string expectedRejection = "observation-goal-reference-not-pending";
            if (mode == "stale-evidence")
            {
                initialWire.Remove("observationGoalId"); initialWire["intent"] = "inspect-object";
                initialWire["evidence"] = seedText;
                expectedRejection = "evidence-not-in-current-user-text";
            }
            first.Complete(true, initialWire.ToString(), "controlled-invalid-goal");
            yield return Wait(() => model.Decisions.Count == decisions + 2, 6, "single production decision repair after new image");
            var repair = model.Decisions.Last(); var repairedInput = JObject.Parse(repair.Input);
            CheckSeedDialogue(c, repairedInput, "Repair " + mode);
            c.Check(repair.Kind == "room" && (int?)repairedInput["decisionRepair"]?["attempt"] == 1 &&
                (int?)repairedInput["decisionRepair"]?["maximumAttempts"] == 1 &&
                ((string)repairedInput["decisionRepair"]?["rejectionReason"] ?? "").Contains(expectedRejection),
                "Repair input carries the exact contextual rejection and a one-request budget.");
            c.Check((string)repairedInput["visualObservation"]?["captureId"] != (string)firstInput["visualObservation"]?["captureId"],
                "Repair planner gets a distinct actual new image.");
            c.Check(model.Matches.Count == matches && c.bridge.Controller.OperationRevision == operation &&
                Vector3.Distance(beforeRoot, c.bridge.avatar.transform.position) < .0001f,
                "Invalid goal never reaches semantic matching or native execution.");
            // Old transport callback remains callable. The production request revision must fence it.
            first.Complete(true, ApproachWire(c, firstInput, false).ToString(), "late-old-valid-response");
            yield return null;
            c.Check(model.Matches.Count == matches && model.Decisions.Count == decisions + 2,
                "Old initial callback cannot execute while the repair owns the decision.");
            if (mode == "stale-evidence")
            {
                c.Check((string)repairedInput["currentUserText"] == "请你靠近他。" &&
                    ((string)repairedInput["decisionRepair"]?["rejectedDecision"] ?? "").Contains(seedText),
                    "Evidence repair retains the present full user request and labels the prior inspect question as rejected output.");
                repair.Complete(true, new JObject { ["intent"] = "none", ["origin"] = "none", ["evidence"] = "" }.ToString(), "controlled-no-action-after-evidence-repair");
                yield return Wait(() => model.Speech.Count > formal, 5, "new model decision may decline after evidence repair");
                c.Check(model.Decisions.Count == decisions + 2 && model.Matches.Count == matches &&
                    c.bridge.Controller.OperationRevision == operation && (string)Get(chat, "m_RoomTaskIntent") == "none",
                    "Stale inspect evidence never executes; the second model may choose none without forced movement or a third repair.");
            }
            else if (mode == "cancelled-late")
            {
                Call(chat, "InvalidateFormalResponse", "decision-repair-cancelled"); Call(chat, "CancelSceneAction", "decision-repair-cancelled");
                repair.Complete(true, ApproachWire(c, repairedInput, false).ToString(), "late-after-cancel");
                yield return null; yield return null;
                c.Check(model.Matches.Count == matches && model.Speech.Count == formal && c.bridge.Controller.OperationRevision == operation,
                    "Cancellation rejects the late repair without matching, speech or body action.");
            }
            else if (mode == "second-invalid")
            {
                repair.Complete(true, ApproachWire(c, repairedInput, true).ToString(), "controlled-second-invalid-goal");
                yield return Wait(() => model.Speech.Count > formal, 5, "honest response after exhausted repair");
                yield return null;
                c.Check(model.Decisions.Count == decisions + 2 && model.Matches.Count == matches && c.bridge.Controller.OperationRevision == operation,
                    "Second invalid goal terminates; there is no third planner request, matcher call or silent ID removal.");
                c.Check((string)Get(chat, "m_RoomTaskIntent") == "unresolved", "Unrepaired goal remains unresolved instead of claiming movement.");
            }
            else
            {
                repair.Complete(true, ApproachWire(c, repairedInput, false).ToString(), "controlled-corrected-decision");
                yield return Wait(() => model.Matches.Count == matches + 1, 5, "corrected actual approach matcher");
                var match = model.Matches.Last(); var matchInput = JObject.Parse(match.Input);
                c.Check((string)matchInput["intent"] == "approach-object" && (string)matchInput["initialGeometryEntityId"] == c.lamp.EntityId &&
                    (matchInput["observationGoalId"] == null || matchInput["observationGoalId"].Type == JTokenType.Null),
                    "Only the model's new corrected decision reaches same-lamp approach matching, without a fabricated goal.");
                if (!c.native)
                {
                    Call(chat, "InvalidateFormalResponse", "guard-stops-before-native"); Call(chat, "CancelSceneAction", "guard-stops-before-native");
                    CompleteLampMatch(c, match); yield return null; yield return null;
                    c.Check(c.bridge.Controller.OperationRevision == operation, "Guard entry never submits native locomotion, even if a late match arrives.");
                }
                else
                {
                    CompleteLampMatch(c, match);
                    yield return Wait(() => c.bridge.Controller.OperationRevision > operation, 12, "repaired decision submits actual native approach");
                    yield return Wait(() => !(bool)Get(chat, "m_SceneActionActive"), 75, "repaired approach actual arrival and reobservation");
                    var fact = Fact(chat);
                    c.Check((bool?)fact["groundArrived"] == true && (bool?)fact["objectReobserved"] == true &&
                        (string)fact["phase"] == "arrived" && (string)fact["entityId"] == c.lamp.EntityId &&
                        (string)fact["resultCaptureId"] != (string)fact["confirmedCaptureId"],
                        "Corrected production planner completes real ground travel and fresh-image confirmation of the same lamp.");
                    c.Check(Vector3.Distance(beforeRoot, c.bridge.avatar.transform.position) > .3f,
                        "Native repair scenario actually translates the avatar; no synthetic arrival.");
                    yield return Wait(() => !c.bridge.ReservesBody, 5, "native body releases");
                    LastReport["nativeArrival"] = fact;
                }
            }
            ((JArray)LastReport["cases"]).Add(new JObject { ["name"] = mode, ["initialDecision"] = initialWire,
                ["initialRecentDialogue"] = firstInput["recentDialogue"].DeepClone(),
                ["repairInput"] = repairedInput, ["fact"] = Fact(chat), ["decisionRequests"] = model.Decisions.Count - decisions,
                ["matcherRequests"] = model.Matches.Count - matches });
            c.Save();
        }
        c.Check(JToken.DeepEquals(originalPower, JObject.Parse(JsonUtility.ToJson(c.lamp.ReadState()))), "All repair cases leave actual lamp power unchanged.");
        if (!c.native) c.Check(c.bridge.Controller.OperationRevision == originalOperation && Vector3.Distance(originalRoot, c.bridge.avatar.transform.position) < .0001f,
            "Entire guard suite issues no native operation and leaves actor root unchanged.");
        yield return Prepare(c);
    }

    private static void ClearNonSystemHistory(List<LLM.SendData> history)
        => history.RemoveAll(message => message == null || message.role != "system");

    private static void SetSeedOnlyHistory(Context c)
    {
        c.Check(ReferenceEquals(c.model.m_DataList, c.history), "Case history uses the original shared list object.");
        c.Check((string)LastReport["seedFact"]?["phase"] == "observed" &&
            (string)LastReport["seedFact"]?["entityId"] == c.lamp.EntityId,
            "Quoted seed question is backed by this suite's actual completed lamp observation.");
        ClearNonSystemHistory(c.history);
        c.history.Add(new LLM.SendData("user", SeedText));
    }

    private static void CheckSeedDialogue(Context c, JObject input, string stage)
    {
        var dialogue = input["recentDialogue"] as JArray;
        c.Check(dialogue != null && dialogue.Count == 1 && (string)dialogue[0]["role"] == "user" &&
            (string)dialogue[0]["quotedText"] == SeedText,
            stage + " exports only the real seed user question, with no unrelated history or fabricated assistant confirmation.");
    }

    private static JObject ApproachWire(Context c, JObject input, bool invalidGoal)
    {
        var value = new JObject { ["intent"] = "approach-object", ["origin"] = "user", ["evidence"] = (string)input["currentUserText"],
            ["entityTarget"] = new JObject { ["captureId"] = (string)input["visualObservation"]["captureId"], ["entityId"] = c.lamp.EntityId } };
        if (invalidGoal) value["observationGoalId"] = c.lamp.EntityId;
        return value;
    }
    private static void CompleteLampMatch(Context c, NevaSceneVisionIntegrationModel.Request request)
    {
        var candidate = c.chat.LastSceneObjectCandidateView.Candidates.FirstOrDefault(x => x.entityId == c.lamp.EntityId);
        c.Check(candidate != null, "Actual candidate image contains this same lamp.");
        request.Complete(true, new JObject { ["verdict"] = "matched", ["candidateRole"]="target", ["candidateId"] = candidate.candidateId,
            ["reason"] = "Controlled test reply: the current opaque lamp candidate matches the previously observed lamp and the present request." }.ToString(), "controlled-match");
    }
    private static IEnumerator BeginPlanner(Context c, string text)
    {
        Call(c.chat, "RecordAcceptedMotionUserTurn", text);
        SceneVisionObservation observation = null; yield return Capture(c, "decision-repair-source", value => observation = value);
        int before = c.model.Decisions.Count;
        Call(c.chat, "StartStreaming", text, false, null, false, false, false, null, false, observation, true);
        yield return Wait(() => c.model.Decisions.Count == before + 1, 5, "production StartStreaming planner");
    }
    private static IEnumerator Prepare(Context c)
    {
        Call(c.chat, "CancelSceneAction", "decision-repair-next"); Call(c.chat, "ClearScheduledAgentWake");
        yield return Wait(() => !((Get(c.chat, "m_SceneAttention") as NevaSceneAttention)?.IsBusy ?? false), 5, "previous real head releases");
        typeof(SceneActionIntegrationRegression).GetMethod("ResetRound", Flags).Invoke(null, new object[] { c.chat });
        Set(c.chat, "m_EnableSceneObjectAwareness", true); Set(c.chat, "m_EnableSceneObjectTargetMatching", true);
    }
    private static IEnumerator Capture(Context c, string purpose, Action<SceneVisionObservation> completed)
    {
        SceneVisionObservation value = null;
        Call(c.chat, "RequestSceneObservation", purpose, new Action<SceneVisionObservation>(x => value = x));
        yield return Wait(() => value != null, 5, "actual scene capture: " + purpose);
        c.Check(value.HasImage && value.HasSurfaceGrounding, "Actual current RGB/ID capture is available.");
        completed(value);
    }
    private static IEnumerator Wait(Func<bool> ready, float seconds, string label)
    { float until = Time.realtimeSinceStartup + seconds; while (!ready()) { if (Time.realtimeSinceStartup > until) throw new TimeoutException(label); yield return null; } }
    private static JObject Fact(ChatSample chat) => Get(chat, "m_SceneActionFact") is object fact ? JObject.Parse(JsonUtility.ToJson(fact)) : new JObject();
    private static object Call(object target, string name, params object[] args) => NevaSceneVisionIntegrationRegression.Call(target, name, args);
    private static object Get(object target, string field) => target.GetType().GetField(field, Flags).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags).SetValue(target, value);
}
