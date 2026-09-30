using System;
using System.Linq;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR
using System.Collections;
using System.Reflection;
#endif

// Production state machine + parser checks. Unity checks exercise the real
// actor callback, native-result verifier and watcher with controlled facts;
// they do not claim physical navigation or RGB recognition was exercised.
public static class SpatialFollowthroughRegression
{
    private static int checks;
    private static void Check(bool value, string reason)
    { ++checks; if (!value) throw new InvalidOperationException(reason); }

    private static ActorPlanRuntime Turn(string coverage = "atomic")
    {
        var plan = new ActorPlanRuntime("好的，这就把身体转过去。", "actor_draft", coverage, "身体向左转90度");
        Check(plan.AdoptBodyTurnCompletion(90) == (coverage == "atomic"), "Only a predeclared atomic role goal adopts body completion.");
        return plan;
    }
    private static void Submit(ActorPlanRuntime plan, string remainder, string action = "turn-1")
    {
        Check(plan.Propose(plan.Revision, "turn-in-place", remainder, "body-left-90", out _) &&
            plan.BindAction(plan.Revision, action), "Actual operation has an owned revision and action ID.");
    }
    private static RoomTaskProtocol.ActorProposalSource Source() => new RoomTaskProtocol.ActorProposalSource {
        Id = "current-draft", Revision = 2, Text = "好，我先转过去，再把刚才的小灯打开。", Purpose = "plan",
        PlanId = "current-plan", PlanRevision = 3, Target = "other", Coverage = "compound", BoundEntityId = "lamp-1" };
    private static JObject Wire(RoomTaskProtocol.ActorProposalSource source, string intent = "reobserve-object") => new JObject {
        ["intent"] = intent, ["origin"] = "actor_draft", ["evidence"] = "",
        ["source_ref"] = RoomTaskProtocol.PlanSourceReference(source), ["actorDraftId"] = source.Id,
        ["actorDraftRevision"] = source.Revision, ["planId"] = source.PlanId, ["planRevision"] = source.PlanRevision,
        ["planRemaining"] = source.Text, ["entityTarget"] = new JObject { ["captureId"] = "fresh-image", ["entityId"] = "lamp-1" } };
    private static bool Parse(JObject wire, RoomTaskProtocol.ActorProposalSource source, out RoomTaskProtocol.Decision decision)
        => RoomTaskProtocol.TryDecision(wire.ToString(), "当前用户原话", false, source, out decision, out _);

    private static bool Fits(JToken schema, JToken value)
    {
        string type = (string)schema["type"];
        bool Numeric(JToken t) => t.Type == JTokenType.Integer || t.Type == JTokenType.Float;
        if (type == "object" && value.Type != JTokenType.Object || type == "string" && value.Type != JTokenType.String ||
            type == "number" && !Numeric(value) || type == "integer" && value.Type != JTokenType.Integer) return false;
        if (schema["enum"] is JArray choices && !choices.Any(c => JToken.DeepEquals(c, value) || Numeric(c) && Numeric(value) && (double)c == (double)value)) return false;
        if (value is JObject obj)
        {
            var props = schema["properties"] as JObject;
            if (schema["required"] is JArray required && required.Any(k => obj.Property((string)k) == null)) return false;
            foreach (var p in obj.Properties())
                if (props?[p.Name] == null ? (bool?)schema["additionalProperties"] == false : !Fits(props[p.Name], p.Value)) return false;
        }
        if (schema["oneOf"] is JArray one && one.Count(s => Fits(s, value)) != 1) return false;
        if (schema["anyOf"] is JArray any && !any.Any(s => Fits(s, value))) return false;
        return true;
    }

    public static int RunPureChecks()
    {
        checks = 0;
        var good = Turn(); Submit(good, good.OriginalGoal);
        Check(!good.Resolve(1, "someone-else", true, "observed", "native", null, false, null, -90.09, -90), "Wrong action cannot resolve a goal.");
        Check(good.Resolve(1, "turn-1", true, "observed", "native", null, false, null, -90.09, -90) &&
            good.State == "completed" && good.RemainingGoal == "" && good.Reason == "adopted-body-turn-verified",
            "c37 measured turn closes the predeclared atomic goal even when Room echoes the whole draft.");
        Check(!good.Resolve(1, "turn-1", true, "observed", "duplicate", null, false, null, -90, -90) && good.Results.Count == 1,
            "Duplicate terminal callback cannot resolve twice.");
        foreach (double? measured in new double?[] { null, 90, -9.7, double.NaN, double.PositiveInfinity })
        {
            var p = Turn(); Submit(p, ""); p.Resolve(1, "turn-1", true, "observed", "claimed", null, false, null, measured, -90);
            Check(p.State == "planning" && p.RemainingGoal == p.OriginalGoal && !p.CloseWithoutNextStep(p.Revision, ""),
                "Success alone, missing/invalid measurement, opposite physical turn or wrong amplitude cannot close the atomic goal.");
        }
        foreach (double requested in new[] { -9.7, double.NaN, double.NegativeInfinity })
        {
            var p = Turn(); Submit(p, p.OriginalGoal); p.Resolve(1, "turn-1", true, "observed", "claimed", null, false, null, -90, requested);
            Check(p.Active && !p.MatchesBodyTurnRequest(requested), "Room cannot replace the adopted ninety-degree magnitude.");
        }
        foreach (string coverage in new[] { "atomic", "compound" })
        {
            var p = Turn(coverage); Submit(p, "接着打开灯，再唱第二段");
            p.Resolve(1, "turn-1", true, "observed", "native", null, false, null, -90, -90);
            Check(p.Active && p.RemainingGoal == "接着打开灯，再唱第二段", "A measured turn cannot erase an independently named tail.");
        }
        var cancelled = Turn(); Submit(cancelled, ""); cancelled.Cancel("new-input");
        Check(!cancelled.Resolve(1, "turn-1", true, "observed", "late", null, false, null, -90, -90), "Cancellation revokes late results.");
        var restored = Turn(); Check(restored.RestoreCompletedCheckpoint(good.Facts()) && restored.RemainingGoal == "" &&
            restored.CloseWithoutNextStep(restored.Revision, ""), "A genuinely predeclared measured checkpoint can recover without replay.");
        var untyped = new ActorPlanRuntime(good.OriginalGoal, "actor_draft", "atomic", good.CompiledGoal);
        Submit(untyped, ""); untyped.Resolve(1, "turn-1", true, "observed", "old", null, false);
        Check(!Turn().RestoreCompletedCheckpoint(untyped.Facts()), "New typed completion cannot be retrospectively attached to an unmeasured historical turn.");
        var wrongCheckpoint = good.Facts(); wrongCheckpoint["bodyTurnCompletion"]["planId"] = "foreign";
        Check(!Turn().RestoreCompletedCheckpoint(wrongCheckpoint), "Checkpoint completion must belong to its original owner.");

        var source = Source(); var wire = Wire(source);
        Check(Parse(wire, source, out var parsed) && parsed.intent == "reobserve-object" && parsed.evidence == source.Text &&
            parsed.planRemaining == source.Text && parsed.entityTarget.entityId == "lamp-1",
            "c34/c32 current source citation is resolved by the real parser without model rewriting, and keeps the original goal/ID.");
        string schema = RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(new string[0], source);
        Check(schema.Contains(RoomTaskProtocol.PlanSourceReference(source)) && schema.Contains("source_ref") && schema.Contains("reobserve-object"),
            "Production request grammar exposes the bound citation and explicit observation prerequisite.");
        foreach (string field in new[] { "source_ref", "planId", "actorDraftId", "evidence" })
        {
            var invalid = (JObject)wire.DeepClone(); invalid[field] = field == "evidence" ? "看到你之前关掉的小灯了，现在重新把它打开。" : "other-owner";
            Check(!Parse(invalid, source, out _), "Foreign reference, source or model-written evidence must remain invalid: " + field);
        }
        var otherEntity = (JObject)wire.DeepClone(); otherEntity["entityTarget"]["entityId"] = "another-lamp";
        Check(!Parse(otherEntity, source, out _), "An explicit prerequisite cannot substitute another remembered entity.");
        source.PlanRevision++;
        Check(!Parse(wire, source, out _), "Previous plan revision cannot reuse its source citation.");
        source = Source(); source.Purpose = "verify";
        Check(RoomTaskProtocol.PlanSourceReference(source) == null && !Parse(wire, source, out _), "A past claim cannot gain an action lease from a citation.");
        source = Source(); var approach = Wire(source, "approach-object");
        Check(Parse(approach, source, out parsed) && parsed.planRemaining == source.Text, "Explicit approach retains the whole pending device goal.");
        var none = Wire(source); none.Remove("entityTarget"); none.Remove("actorDraftId"); none.Remove("actorDraftRevision");
        none["intent"] = "none"; none["origin"] = "none"; none["source_ref"] = "none";
        Check(Parse(none, source, out parsed) && parsed.intent == "none", "A role may still decline spatial action; a reference is not compulsory execution.");
        var arbitrary = Wire(source); arbitrary.Remove("planId"); arbitrary.Remove("planRevision"); arbitrary.Remove("planRemaining");
        Check(!RoomTaskProtocol.TryDecision(arbitrary.ToString(), "当前用户原话", false, out _, out _), "Unbound model source_ref never authorizes an action.");

        var turnPlan = Turn();
        var turnSource = RoomTaskProtocol.ReadActorProposal(new JObject { ["source"] = "unpublished-current-role-draft",
            ["draftId"] = "turn-draft", ["draftRevision"] = 1, ["text"] = turnPlan.OriginalGoal, ["purpose"] = "plan",
            ["published"] = false, ["executed"] = false, ["coverage"] = "atomic", ["target"] = "other", ["actorPlan"] = turnPlan.Facts() });
        Check(turnSource?.BodyTurnDegrees == 90, "Direction grammar derives magnitude only from the pre-adopted program plan contract.");
        var directionSchema = RoomTaskProtocol.StructuredResponseFormat(RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null, turnSource))["json_schema"]["schema"];
        foreach (string direction in new[] { "left", "right" })
        {
            var turnWire = Wire(turnSource, "turn-in-place"); turnWire.Remove("entityTarget");
            turnWire["viewChange"] = new JObject { ["captureId"] = "current-view", ["direction"] = direction, ["degrees"] = 90 };
            Check(Parse(turnWire, turnSource, out var decoded) && decoded.viewChange.yaw == (direction == "left" ? -90 : 90) && decoded.viewChange.pitch == 0,
                "Program maps semantic direction to physical sign exactly once: " + direction);
            Check(Fits(directionSchema, turnWire), "Real compatible wire schema admits the explicit direction: " + direction);
            foreach (JToken invalidMagnitude in new JToken[] { new JValue(-90), new JValue(9.7), new JValue(double.NaN), new JValue("90") })
            {
                var bad = (JObject)turnWire.DeepClone(); bad["viewChange"]["degrees"] = invalidMagnitude;
                Check(!Parse(bad, turnSource, out _), "Typed turn rejects changed/invalid absolute angle: " + invalidMagnitude);
            }
            var legacyBypass = (JObject)turnWire.DeepClone(); legacyBypass["viewChange"] = new JObject { ["captureId"] = "current-view", ["yaw"] = 90, ["pitch"] = 0 };
            Check(!Parse(legacyBypass, turnSource, out _), "A numeric yaw cannot bypass the typed body direction schema.");
            Check(!Fits(directionSchema, legacyBypass), "Real compatible schema rejects numeric-yaw bypass of the typed direction.");
            var wrongDirection = (JObject)turnWire.DeepClone(); wrongDirection["viewChange"]["direction"] = "backwards";
            Check(!Parse(wrongDirection, turnSource, out _), "Typed turn rejects an unlisted direction.");
        }
        string turnSchema = RoomTaskProtocol.BuildDecisionSchemaForObservationGoals(null, turnSource);
        Check(turnSchema.Contains("direction") && turnSchema.Contains("degrees"), "Production typed turn schema exposes direction and fixed magnitude.");
        foreach (string intent in new[] { "turn-in-place", "orient-view" })
        {
            var oldWire = new JObject { ["intent"] = intent, ["origin"] = "user", ["evidence"] = "向左转",
                ["viewChange"] = new JObject { ["captureId"] = "current-view", ["yaw"] = -30, ["pitch"] = 0 } };
            Check(RoomTaskProtocol.TryDecision(oldWire.ToString(), "向左转", false, out var old, out _) && old.viewChange.yaw == -30,
                "Existing unbound numeric-yaw path remains compatible: " + intent);
        }

        var prerequisite = new ActorPlanRuntime("打开刚才的小灯", "actor_draft", "atomic");
        Check(prerequisite.RegisterPrerequisiteAttempt("reobserve-object", "lamp-1") && prerequisite.RegisterPrerequisiteAttempt("reobserve-object", "lamp-1") &&
            !prerequisite.RegisterPrerequisiteAttempt("reobserve-object", "lamp-1"), "Same capability prerequisite is bounded to two repair attempts.");
        Check(prerequisite.PreservesPrerequisiteGoal("reobserve-object", "lamp-1", prerequisite.OriginalGoal) &&
            !prerequisite.PreservesPrerequisiteGoal("reobserve-object", "lamp-1", ""), "Observation cannot consume the device goal.");
        Check(prerequisite.RegisterPrerequisiteAttempt("approach-object", "lamp-1") &&
            !prerequisite.PreservesPrerequisiteGoal("approach-object", "lamp-1", ""), "Distance prerequisite cannot consume the device goal.");
        var recovered = new ActorPlanRuntime(prerequisite.OriginalGoal, "actor_draft", "atomic");
        Check(recovered.RestoreCompletedCheckpoint(prerequisite.Facts()) && !recovered.RegisterPrerequisiteAttempt("reobserve-object", "lamp-1"),
            "Recovery cannot reset a spent prerequisite budget.");
        prerequisite.Cancel("new-goal");
        Check(!prerequisite.RegisterPrerequisiteAttempt("approach-object", "lamp-1"), "Old cancelled goals cannot acquire another capability attempt.");
        var device = new ActorPlanRuntime("打开小灯", "actor_draft", "atomic");
        Check(device.Propose(1, "reobserve-object", device.OriginalGoal, "observe", out _) && device.BindAction(1, "seen") &&
            device.Resolve(1, "seen", true, "observed", "fresh-same-entity", "lamp-1", true) &&
            device.AdoptDeviceCompletion(2, "lamp-1", "on", device.OriginalGoal, "actor_draft"),
            "Fresh same-entity reobservation can bind a pre-operation device condition.");
        Check(device.Propose(2, "approach-object", device.OriginalGoal, "walk", out _) && device.BindAction(2, "arrive") &&
            device.Resolve(2, "arrive", true, "arrived", "native", "lamp-1", true) && device.Active,
            "Arrival alone cannot satisfy a pending on condition.");
        Check(device.Propose(3, "set-object-state", "", "on", out _) && device.BindAction(3, "switch") &&
            device.Resolve(3, "switch", true, "performed", "readback", "lamp-1", true, "on") && device.State == "completed",
            "Only the later exact-entity state readback closes the atomic device goal.");
        return checks;
    }

    public static int RunChecks()
    {
        RunPureChecks();
#if UNITY_EDITOR
        foreach (string mutation in new[] { "valid", "wrong-action", "wrong-capture", "nan-request", "nan-distance", "negative-distance", "negative-error", "opposite", "missing" })
        using (var f = new Fixture())
        {
            var step = f.Step(); var plan = f.Plan; var turn = Get(f.Chat, "m_ActorPlanTurn");
            Check(plan.BodyTurnCompletionFacts() != null, "Production Actor callback registers typed body condition before Room.");
            object[] args = { step, f.Generation, null };
            Check((bool)Call(f.Chat, "AcceptActorPlanStep", args) && plan.BindAction(1, "body-1"), "Actual acceptance binds the body step.");
            f.Stage(step, mutation); int calls = f.Model.RoomCalls;
            var measured = (double?)Call(f.Chat, "VerifiedActorPlanBodyYaw", step, "body-1", "observed");
            Check(measured.HasValue == (mutation == "valid" || mutation == "opposite"), "Native verifier rejects corrupt/missing owner facts: " + mutation);
            if (mutation != "valid") continue;
            var watcher = (IEnumerator)Call(f.Chat, "AwaitActorPlanStep", plan, turn, step, "body-1");
            Check(!watcher.MoveNext() && plan.State == "completed" && plan.RemainingGoal == "" && f.Model.RoomCalls == calls,
                "Real watcher consumes measured atomic completion without calling Room again for the echoed sentence.");
            string queued = f.Queued;
            Check(queued.Length > 0 && !queued.Contains("未能确认") && !queued.Contains("没有继续执行"), "c37 final speech reports success instead of a later planner failure.");
            Call(f.Chat, "PublishActorPlanClosure", plan, turn, false, "late-unavailable");
            Check(f.Queued == queued && !(bool)Call(f.Chat, "ClaimActorPlanClosure", plan), "A late failure cannot publish another closure receipt.");
        }
        foreach (string mutation in new[] { "cancel", "replace-plan", "new-generation", "new-input", "work-reset", "advance-revision" })
        using (var f = new Fixture())
        {
            var step = f.Step(); var plan = f.Plan;
            var lease = (Func<bool>)Call(f.Chat, "CaptureSceneActorPlanLease", step, f.Generation);
            Check(lease(), "The actual scene prerequisite lease initially owns the current adopted plan.");
            if (mutation == "cancel") plan.Cancel("test-cancel");
            if (mutation == "replace-plan") Set(f.Chat, "m_ActorPlan", new ActorPlanRuntime("新目标"));
            if (mutation == "new-generation") Set(f.Chat, "m_FormalResponseGeneration", f.Generation + 1);
            if (mutation == "new-input") Call(Get(f.Chat, "m_ActionConstraints"), "RecordUserTurn", "新的目标");
            if (mutation == "work-reset") Set(f.Chat, "m_WorkEpoch", (int)Get(f.Chat, "m_WorkEpoch") + 1);
            if (mutation == "advance-revision") { plan.Propose(1, "inspect", plan.OriginalGoal, "inspect", out _); plan.BindAction(1, "read"); plan.Resolve(1, "read", true, "observed", "fresh", null, false); }
            Check(!lease(), "Old capture, matcher and native validators lose the lease after " + mutation);
        }
        using (var f = new Fixture())
        {
            var memory = (IDictionary)Get(f.Chat, "m_KnownSceneObjects");
            var known = Activator.CreateInstance(typeof(ChatSample).GetNestedType("KnownSceneObject", BindingFlags.NonPublic), true);
            Set(known, "facts", new JObject { ["entityId"] = "old-lamp", ["pointAtCapture"] = new JObject { ["x"] = 2, ["y"] = 1, ["z"] = 0 } });
            memory.Add("old-lamp", known); Set(f.Chat, "m_EnableSceneObjectAwareness", false);
            bool called = false;
            Call(f.Chat, "BeginSceneObjectReobservation", f.Step(), null, f.Generation, (Action)(() => called = true));
            Check(called && ((string)Get(f.Chat, "m_RoomTaskDispatch")).Contains("scene-object-awareness-disabled") &&
                !(bool)Get(f.Chat, "m_SceneActionActive") && memory.Count == 1,
                "Disabling object awareness blocks reobservation before dispatch while historical memory may still exist.");
        }
#endif
        return checks;
    }
#if UNITY_EDITOR
    private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private sealed class Fixture : IDisposable
    {
        private readonly object inner;
        public readonly ChatSample Chat;
        public readonly FastLoopIntegrationModel Model;
        private const string Draft = "好的，这就把身体转过去。";
        public int Generation => (int)Get(Chat, "m_FormalResponseGeneration");
        public ActorPlanRuntime Plan => (ActorPlanRuntime)Get(Chat, "m_ActorPlan");
        public string Queued => (string)Call(inner, "Queued");
        public Fixture()
        {
            inner = Activator.CreateInstance(typeof(FastLoopIntegrationRegression).GetNestedType("Fixture", BindingFlags.NonPublic), true);
            Chat = (ChatSample)Get(inner, "Chat"); Model = (FastLoopIntegrationModel)Get(inner, "Model");
            Call(inner, "Arm", "嗯。", false); Call(inner, "AttachControlledRoom"); Call(inner, "AttachSpeechProvider");
            var judge = (FastLoopIntegrationModel.PendingActorIntent)Call(inner, "CompileActor", Draft);
            judge.Answer(new ActorIntentDecision { Domain = "other_action", Kind = "replan", Task = "other", Target = "other",
                Action = "none", Condition = "none", Evidence = Draft, Goal = "身体向左转90度", Coverage = "atomic", MotionScope = "body_turn", Degrees = 90 }, "ok");
            Call(Chat, "RememberActorTaskLanguage", Generation, "zh");
        }
        public RoomTaskProtocol.Decision Step() => new RoomTaskProtocol.Decision { intent = "turn-in-place", origin = "actor_draft",
            evidence = Draft, planId = Plan.Id, planRevision = Plan.Revision, planRemaining = Draft,
            viewChange = new RoomTaskProtocol.ViewChange { captureId = "before", yaw = -90, pitch = 0 } };
        public void Stage(RoomTaskProtocol.Decision step, string mutation)
        {
            var fact = Activator.CreateInstance(typeof(ChatSample).GetNestedType("SceneActionFact", BindingFlags.NonPublic), true);
            Set(fact, "actionId", "body-1"); Set(fact, "intent", "turn-in-place"); Set(fact, "phase", "observed");
            Set(fact, "resultCaptureId", "after");
            var result = new ArdyRoomDialogueBridge.SceneOrientResult { actionId = "body-1", captureId = "before", terminal = true,
                oriented = true, requestedYaw = -90, actualYaw = -90.09f, actualRootDisplacement = .007f, actualFacingErrorDegrees = .09f };
            if (mutation == "wrong-action") result.actionId = "foreign";
            if (mutation == "wrong-capture") result.captureId = "old";
            if (mutation == "nan-request") result.requestedYaw = float.NaN;
            if (mutation == "nan-distance") result.actualRootDisplacement = float.NaN;
            if (mutation == "negative-distance") result.actualRootDisplacement = -.01f;
            if (mutation == "negative-error") result.actualFacingErrorDegrees = -.01f;
            if (mutation == "opposite") result.actualYaw = 90;
            if (mutation != "missing") Set(fact, "bodyTurnResult", result);
            Set(Chat, "m_SceneActionFact", fact); Set(Chat, "m_SceneActionActive", false);
            Set(Chat, "m_RoomTaskIntent", "turn-in-place"); Set(Chat, "m_RoomTaskActionId", "body-1"); Set(Chat, "m_RoomTaskDispatch", "view-change-completed");
            Call(Chat, "EndSceneOrientWatch", new object[] { null });
        }
        public void Dispose() => ((IDisposable)inner).Dispose();
    }
    private static object Get(object target, string name) => target.GetType().GetField(name, F).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, F).SetValue(target, value);
    private static object Call(object target, string name, params object[] args)
    { try { return target.GetType().GetMethod(name, F).Invoke(target, args); } catch (TargetInvocationException e) { throw e.InnerException ?? e; } }
#endif
}
