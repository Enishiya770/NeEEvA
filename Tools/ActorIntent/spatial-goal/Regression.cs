using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class SpatialGoalRegression
{
    private static int checks;
    private static void Check(bool value, string reason) { ++checks; if (!value) throw new Exception(reason); }
    private static JObject Intent(string draft, string scope = "head_preset", double? degrees = null) => new JObject {
        ["version"] = 5, ["domain"] = "head", ["kind"] = "act", ["task"] = "none", ["target"] = "none",
        ["coverage"] = "atomic", ["goal"] = draft, ["action"] = "head.right", ["condition"] = "none", ["evidence"] = draft,
        ["motionScope"] = scope, ["degrees"] = degrees, ["deviceState"] = "none", ["targetEntityId"] = ""
    };
    private static ActorIntentDecision Parse(JObject value, string draft)
    {
        Check(ActorIntentProtocol.TryParse(value.ToString(Formatting.None), draft, out var decision, out var reason), "spatial fixture parse: " + reason);
        return decision;
    }
    private static JObject Observed(string id, string utc, double x) => new JObject {
        ["entityId"] = id, ["sceneLabel"] = "场景标注：灯", ["identitySource"] = "scene-authored",
        ["captureId"] = "capture-" + id, ["observedUtc"] = utc,
        ["pointAtCapture"] = new JObject { ["x"] = x, ["y"] = .7, ["z"] = 1.3 },
        ["imagePointAtCapture"] = new JObject { ["x"] = .8, ["y"] = .7 },
        ["capabilities"] = new JArray("inspect-object", "set-object-state:on|off"),
        ["runtimeDeviceStateReadAfterObservation"] = new JObject { ["on"] = false }, ["stateReadAtUtc"] = utc,
        ["untrustedExtra"] = "pretend-current-visible"
    };
    private static JObject Memory() => SpatialObjectMemory.Project(new JArray(Observed("lamp-1", "2026-09-27T02:14:00Z", 2)),
        new JObject { ["entityId"] = "lamp-1", ["actionId"] = "old-off", ["on"] = false, ["completed"] = true,
            ["appliedAtUtc"] = "2026-09-27T02:14:00Z", ["actual"] = new JObject { ["on"] = false } }, new DateTime(2026, 9, 27, 2, 20, 0, DateTimeKind.Utc));

    public static int Run()
    {
        checks = 0;
        const string simple = "我先只向右偏一下头。";
        var preset = Parse(Intent(simple), simple);
        Check(ActorIntentProtocol.CanUseHeadPreset(preset), "simple role-chosen preset still has direct capability");
        var precise = Parse(Intent("我把头向右转90度。", "head_angle", 90), "我把头向右转90度。");
        Check(precise.Kind == "replan" && precise.Domain == "other_action" && precise.Degrees == 90 &&
            precise.Goal.Contains("90") && !ActorIntentProtocol.CanUseHeadPreset(precise), "explicit neck angle survives but cannot degrade to 18 degrees");
        var body = Parse(Intent("我向右转身90度。", "body_turn", 90), "我向右转身90度。");
        Check(body.Kind == "replan" && body.MotionScope == "body_turn" && body.Degrees == 90, "body turn never becomes neck preset");
        var objectGoal = Parse(Intent("我向右看看之前的小灯。", "object_goal"), "我向右看看之前的小灯。");
        Check(objectGoal.Kind == "replan" && !ActorIntentProtocol.CanUseHeadPreset(objectGoal), "specific object purpose requires complete planning");
        var invalid = Intent(simple); invalid["degrees"] = 90;
        Check(!ActorIntentProtocol.TryParse(invalid.ToString(), simple, out _, out _), "preset cannot hide an exact angle");
        foreach (JToken angle in new JToken[] { -90, 181, 0, "90", true })
        {
            invalid = Intent(simple, "head_angle"); invalid["degrees"] = angle;
            Check(!ActorIntentProtocol.TryParse(invalid.ToString(), simple, out _, out _), "invalid numeric angle cannot authorize preset");
        }
        var legacy = Intent(simple); legacy["version"] = 4;
        foreach (string field in new[] { "motionScope", "degrees", "deviceState", "targetEntityId" }) legacy.Remove(field);
        var legacyDecision = Parse(legacy, simple);
        Check(legacyDecision.Kind == "replan" && !ActorIntentProtocol.CanUseHeadPreset(legacyDecision), "legacy v4 cannot silently acquire preset capability");
        var release = Intent("我解除刚才保持的头部姿势。"); release["kind"] = "cancel"; release["action"] = "head.release";
        Check(ActorIntentProtocol.CanUseHeadPreset(Parse(release, (string)release["evidence"])), "explicit release preserves existing cancellation semantics");
        release["motionScope"] = "none";
        var noNewMotion = Parse(release, (string)release["evidence"]);
        Check(noNewMotion.Kind == "cancel" && ActorIntentProtocol.CanUseHeadPreset(noNewMotion), "typed release with no new motion can cancel without an invented move or model retry");
        var wait = Intent("等你数到三，我再向右偏头。"); wait["kind"] = "wait"; wait["condition"] = "user_count_three";
        var waiting = Parse(wait, (string)wait["evidence"]);
        Check(waiting.Kind == "wait" && ActorIntentProtocol.CanUseHeadPreset(waiting), "preset counting wait remains deferred, never normalized into immediate act");
        var adoptedWait = (JObject)wait.DeepClone(); adoptedWait["kind"] = "act";
        var deferred = Parse(adoptedWait, (string)adoptedWait["evidence"]);
        Check(deferred.Kind == "wait" && deferred.OriginalKind == "act" && deferred.Condition == "user_count_three",
            "an explicit typed deferred condition survives an emitter's generic adopted-act label");
        var counter = new ActorWaitingTaskRuntime();
        var deferredDraft = new ActorDraft { Id = "typed-deferred", Revision = 1, Text = (string)adoptedWait["evidence"] };
        Check(counter.Register(deferred, deferredDraft, 1, out var registered, out _), "typed-deferred interpretation only arms a waiting task");
        Check(!counter.Observe(registered.Id, registered.Revision, 2, "one", out _) &&
            !counter.Observe(registered.Id, registered.Revision, 3, "two", out _) && counter.Pending.State == "waiting",
            "typed-deferred action does not fire before the third accepted count");
        Check(counter.Observe(registered.Id, registered.Revision, 4, "three", out _) &&
            !counter.Observe(registered.Id, registered.Revision, 5, "three", out _), "one complete count triggers once, not on replay");
        foreach (string field in new[] { "claim", "none", "cancel", "replan" })
        {
            var wrong = (JObject)adoptedWait.DeepClone(); wrong["kind"] = field;
            bool compiled = ActorIntentProtocol.TryCompile(wrong.ToString(), deferredDraft.Text, out var denied, out _);
            Check(!compiled || denied.RequiresFullReview && !ActorIntentProtocol.Validate(denied, deferredDraft.Text, out _),
                "typed condition never converts " + field + " into executable waiting");
        }
        var partialEvidence = (JObject)adoptedWait.DeepClone(); partialEvidence["evidence"] = "向右偏头";
        Check(!ActorIntentProtocol.TryParse(partialEvidence.ToString(), deferredDraft.Text, out _, out _), "partial evidence cannot acquire the deferred normalization");
        var compoundWait = (JObject)adoptedWait.DeepClone(); compoundWait["coverage"] = "compound";
        Check(!ActorIntentProtocol.TryParse(compoundWait.ToString(), deferredDraft.Text, out _, out _), "compound goal cannot hide inside a finite head wait");
        const string refusal = "我不想等你数到三转头，还是继续听你说吧。";
        var declined = Intent(refusal, "none"); declined["domain"] = "dialogue"; declined["kind"] = "none";
        declined["coverage"] = "none"; declined["goal"] = ""; declined["evidence"] = ""; declined["action"] = "none";
        var noAction = Parse(declined, refusal);
        Check(noAction.Kind == "none" && noAction.Condition == "none" && !ActorIntentProtocol.CanUseHeadPreset(noAction),
            "role refusal stays non-action even when its words or current user contain count-three language");
        var claim = Intent("我已经向右转身90度了。", "body_turn", 90); claim["kind"] = "claim";
        var reported = Parse(claim, (string)claim["evidence"]);
        Check(reported.Kind == "claim" && reported.Domain == "other_action" && reported.Task == "none", "precise past claim stays verification-only, never repaired by new act");
        var memory = Memory();
        var historical = (JObject)memory["objects"][0];
        Check((double)historical["ageSeconds"] == 360 && (string)historical["currentVisibility"] == "unknown" &&
            (string)historical["currentReachability"] == "unknown" && !(bool)historical["executionPermission"], "old object has age but no current visibility or execution permit");
        Check(historical["untrustedExtra"] == null && (double)historical["pointAtCapture"]["x"] == 2 &&
            !(bool)historical["lastDeviceOperation"]["actual"]["on"], "bounded historical position and readback preserved without unrelated facts");
        var original = Observed("old-lamp", "2026-09-27T01:00:00Z", 7);
        var bounded = SpatialObjectMemory.Project(new JArray(original, Observed("new1", "2026-09-27T02:00:00Z", 1),
            Observed("new2", "2026-09-27T02:01:00Z", 2)), new JObject { ["entityId"] = "old-lamp" }, DateTime.UtcNow, 2);
        Check(bounded["objects"].Count() == 2 && bounded["objects"].Any(v => (string)v["entityId"] == "old-lamp"), "bounded memory retains operated correction referent");
        original["pointAtCapture"]["x"] = 999;
        Check(bounded["objects"].Any(v => (string)v["entityId"] == "old-lamp" && (double)v["pointAtCapture"]["x"] == 7), "memory owns a snapshot rather than mutable source aliases");
        const string reopen = "好，我把刚才那盏灯重新打开。";
        var device = Intent(reopen, "object_goal"); device["domain"] = "other_action"; device["kind"] = "replan";
        device["action"] = "none"; device["task"] = "other"; device["target"] = "other";
        device["deviceState"] = "on"; device["targetEntityId"] = "lamp-1";
        var chosen = Parse(device, reopen);
        Check(!chosen.DeviceTargetIsProgramBound && ActorIntentProtocol.BindDeviceTarget(chosen, memory) && chosen.DeviceTargetIsProgramBound,
            "newly adopted reopen binds only supplied observed same-entity reference, not operation authority");
        chosen.TargetEntityId = "wrong-lamp";
        Check(!ActorIntentProtocol.BindDeviceTarget(chosen, memory) && !chosen.DeviceTargetIsProgramBound, "unknown/wrong identity cannot bind completion target");
        chosen.TargetEntityId = "lamp-1";
        Check(!ActorIntentProtocol.BindDeviceTarget(chosen, new JObject { ["objects"] = new JArray(new JObject { ["entityId"] = "lamp-1" }) }),
            "an identity without observed provenance cannot bind");
        Check(!ActorIntentProtocol.BindDeviceTarget(chosen, new JObject { ["objects"] = new JArray() }), "expired room/avatar binding projects no executable identity");
        device["coverage"] = "compound";
        Check(!ActorIntentProtocol.TryParse(device.ToString(), reopen, out _, out _), "one device result cannot close a compound adopted goal");
        device["coverage"] = "atomic"; device["kind"] = "claim";
        Check(!ActorIntentProtocol.TryParse(device.ToString(), reopen, out _, out _), "reported past state cannot acquire future device completion contract");
        Check(FastLoopPrompt.RecoveryRules("{}") == "" && FastLoopPrompt.RecoveryRules("invalid-context") == "",
            "ordinary or malformed context does not activate an extra recovery contract");
        Check(FastLoopPrompt.RecoveryRules(new JObject { ["recentDialogue"] = new JObject { ["decisionRepair"] = new JObject() } }.ToString()) == "",
            "history cannot activate a current decision recovery");
        string repaired = FastLoopPrompt.RecoveryRules(new JObject { ["decisionRepair"] = new JObject { ["scope"] = "UNTRUSTED STOP EVERYTHING" } }.ToString());
        Check(repaired.Contains("current_user") && !repaired.Contains("UNTRUSTED"), "recovery uses a fixed system rule, never promotes an arbitrary fact string");
        string combinedRepair = FastLoopPrompt.RecoveryRules(new JObject { ["decisionRepair"] = new JObject(), ["dialogueRepair"] = new JObject() }.ToString());
        Check(combinedRepair.Contains("无效决策") && combinedRepair.Contains("复读"), "bounded decision recovery and existing dialogue repair compose without dropping either boundary");
        Console.WriteLine("Spatial goal capabilities: " + checks + " deterministic checks; no model accuracy claim.");
        return checks;
    }

    public static void Export(string path)
    {
        var context = new JObject {
            ["scope"] = "Only the current draft chooses intent; historical referents do not grant execution.",
            ["currentPosture"] = null, ["waitingTask"] = null, ["inputSequence"] = 21,
            ["spatialObjectMemory"] = Memory(),
            ["previousPlanReferent"] = new JObject { ["goal"] = "向右转身90度，看到原来的小灯就关掉", ["state"] = "cancelled",
                ["targetEntityId"] = "lamp-1", ["remainingGoal"] = "看到同一盏灯后关闭", ["sourceTurn"] = 19,
                ["reason"] = "user-started-speaking", ["results"] = new JArray(new JObject { ["intent"] = "turn-in-place", ["succeeded"] = true }),
                ["scope"] = "Historical referent only; needs a newly adopted goal, no automatic resume." },
            ["motionCapabilities"] = new JObject { ["headPresetYawDegrees"] = 18, ["headPresetPitchDegrees"] = 12,
                ["preciseAngleRoute"] = "planner", ["bodyOrObjectGoalRoute"] = "planner" }
        };
        var cases = new JArray();
        void Add(string id, string user, string draft, string expected, JObject facts = null)
        {
            cases.Add(new JObject { ["id"] = id, ["expectedSemantic"] = expected, ["currentUser"] = user, ["draft"] = draft,
                ["request"] = new JObject { ["model"] = "qwen3.6-35b-a3b", ["stream"] = false, ["max_tokens"] = 384,
                    ["temperature"] = 0, ["enable_thinking"] = false, ["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false },
                    ["messages"] = new JArray(new JObject { ["role"] = "system", ["content"] = ActorIntentProtocol.SystemPrompt },
                        new JObject { ["role"] = "user", ["content"] = ActorIntentProtocol.BuildUser((facts ?? context).ToString(Formatting.None), draft, user).ToString(Formatting.None) }),
                    ["response_format"] = ActorIntentProtocol.ResponseFormat() } });
        }
        Add("simple-head", "稍微向右偏一点头。", "好，我向右稍微偏一下头。", "head_preset; no degrees; immediate head action");
        Add("keep-head", "保持现在的姿势。", "我保持现在的头部姿势。", "head_preset hold; no body action");
        Add("body90-open-compound", "向右转身90度，然后把之前那盏灯重新打开。", "好，我向右转身90度，看到之前那盏灯后把它打开。", "compound; complete turn plus conditional open retained; planner; no atomic device completion");
        Add("c15-elliptical-adoption", "现在请你向右转90度，然后你应该就能看到那个之前被你关掉的小灯了，你试着把它重新再打开。", "好的，我明白了。请稍等一下，我现在就向右转过去。", "preserve adopted referent 90 degrees and object purpose; no head_preset; current-user continuation is not silently erased");
        Add("c21-reopen-correction", "啊，我说错了，不是关掉，是把它重新开起来。", "好，是重新打开，我去把刚才那盏灯开起来。", "atomic other_action; same lamp-1; deviceState on; do not repeat completed turn");
        Add("explicit-refusal", "向右转身90度再打开灯。", "这次我不想转身，也先不开灯，我们继续聊吧。", "none; user command must not be forced");
        Add("partial-head-only", "向右转身90度再打开灯。", "我先只向右偏一点头，暂时不转身，也不开灯。", "head_preset only, explicit partial adoption; no 90 degrees or device completion");
        Add("self-chosen-alternative", "向右转身90度再打开灯。", "灯先不开，我想先走到你身边。", "only adopted approach current user; no body90/open injected");
        Add("inventory-question", "你还记得之前的小灯在哪吗？", "记得上次看到它在右侧，但现在的位置还需要重新看看。", "none/perception or draft-appropriate non-action review; do not fabricate current visibility or execute old plan");
        Add("three-recordings", "把这一段和前面两段连起来唱给我听。", "好，我把这三段按先后连起来唱给你听。", "recording_singing task; motionScope none; no device completion");
        Add("remember-is-not-visible", "你现在看到了那盏灯吗？", "我只记得之前的位置，现在还不能确认看到了。", "none/perception; no head/object action");
        Add("quote-not-motion", "你刚才说了什么？", "刚才我说的是‘向右转90度’，现在只是解释那句话。", "none; quoted number does not authorize motion");
        var held = (JObject)context.DeepClone(); held["currentPosture"] = new JObject { ["candidate"] = "head.raise", ["state"] = "holding", ["physicallyHeld"] = true };
        Add("release-existing-posture", "不用保持了，放松吧。", "好，我解除刚才保持的头部姿势。", "head_preset cancel/head.release, not a new movement", held);
        Add("wait-count-three", "等我数到三你再向右偏头。", "好，等你数到三，我再向右偏头。", "head_preset wait/user_count_three; no immediate action");
        Add("past-body-claim", "你转过去了吗？", "我已经向右转身90度了。", "other_action claim; verify actual results, never execute to make claim true");
        Add("c23-perception-and-reopen", "你有看到那个小灯吗？现在视角里应该有它了，试试看把它关掉啊，不不，把它开起来。",
            "看到了哦，就在电视柜的右下角，那个小方块就是。好，我这就帮你把它重新打开。",
            "Perception sentence is not a physical claim; single adopted same-lamp opening goal, optional atomic on/lamp-1 completion if referent is clear, no inherited body turn.");
        Add("single-known-device-only", "只把刚才那盏lamp-1灯的开关状态改成打开，不用安排别的动作。",
            "好，我只把刚才那盏灯的开关状态设为开启。",
            "atomic other_action/other target other; deviceState on, targetEntityId lamp-1; binding is a historical referent, never current execution permission.");
        File.WriteAllText(path, new JObject { ["scope"] = "Production Actor v5 factories; exact supplied drafts. Semantic accuracy requires live model replay, not these deterministic checks.", ["cases"] = cases }.ToString(Formatting.Indented));
    }

    public static void ValidateResults(string resultPath, string fixturePath, string outputPath)
    {
        var fixtures = JObject.Parse(File.ReadAllText(fixturePath))["cases"].OfType<JObject>().ToDictionary(v => (string)v["id"]);
        var rows = new JArray();
        foreach (var item in JArray.Parse(File.ReadAllText(resultPath)).OfType<JObject>())
        {
            string id = (string)item["id"];
            if (!fixtures.TryGetValue(id, out var fixture)) throw new Exception("Unknown live fixture: " + id);
            string draft = (string)fixture["draft"], raw = (string)item["response"]?["choices"]?[0]?["message"]?["content"];
            if (fixture["draft"] == null)
            {
                string user = (string)fixture["currentUser"];
                var fastInput = JObject.Parse((string)fixture["request"]["messages"][1]["content"]);
                bool fastParsed = FastLoopProtocol.TryParse(raw, user, out _, out string fastParseReason);
                bool bound = FastLoopProtocol.TryReadBoundResponse(item["response"].ToString(Formatting.None), user,
                    FastLoopRequestPolicy.FromContext(fastInput["context"].ToString(Formatting.None)),
                    out var fast, out string finish, out string fastBoundReason);
                string fastValidity = "no-bound-decision";
                bool fastValid = bound && FastLoopProtocol.Validate(fast, user, out fastValidity);
                rows.Add(new JObject { ["id"] = id, ["protocol"] = "Fast", ["tryParse"] = fastParsed,
                    ["parseReason"] = fastParseReason, ["boundResponseAccepted"] = bound, ["boundResponseReason"] = fastBoundReason,
                    ["validated"] = fastValid, ["validationReason"] = fastValidity, ["finishReason"] = finish,
                    ["expectedSemantic"] = fixture["expectedSemantic"].DeepClone(), ["raw"] = raw,
                    ["decision"] = fast == null ? null : new JObject { ["Route"] = fast.Route, ["TaskControl"] = fast.TaskControl,
                        ["ControlEvidence"] = fast.ControlEvidence, ["ControlTargetId"] = fast.ControlTargetId,
                        ["Candidate"] = fast.Candidate, ["Evidence"] = fast.Evidence, ["Speech"] = fast.Speech, ["Language"] = fast.Language },
                    ["elapsedSeconds"] = item["elapsedSeconds"]?.DeepClone() });
                continue;
            }
            bool parsed = ActorIntentProtocol.TryParse(raw, draft, out _, out string parseReason);
            bool compiled = ActorIntentProtocol.TryCompile(raw, draft, out var decision, out string compileReason);
            string validity = "not-compiled";
            bool valid = compiled && ActorIntentProtocol.Validate(decision, draft, out validity);
            var input = JObject.Parse((string)fixture["request"]["messages"][1]["content"]);
            if (valid) ActorIntentProtocol.BindDeviceTarget(decision, input["context"]?["spatialObjectMemory"] as JObject);
            rows.Add(new JObject { ["id"] = id, ["tryParse"] = parsed, ["parseReason"] = parseReason,
                ["tryCompile"] = compiled, ["compileReason"] = compileReason, ["validated"] = valid, ["validationReason"] = validity,
                ["requiresFullReview"] = decision?.RequiresFullReview == true,
                ["canUseHeadPreset"] = valid && ActorIntentProtocol.CanUseHeadPreset(decision),
                ["deviceTargetIsProgramBound"] = decision?.DeviceTargetIsProgramBound == true,
                ["expectedSemantic"] = fixture["expectedSemantic"].DeepClone(),
                ["decision"] = decision == null ? null : new JObject {
                    ["Kind"] = decision.Kind, ["Domain"] = decision.Domain, ["Action"] = decision.Action,
                    ["Condition"] = decision.Condition, ["Goal"] = decision.Goal, ["Coverage"] = decision.Coverage,
                    ["Task"] = decision.Task, ["Target"] = decision.Target, ["Evidence"] = decision.Evidence,
                    ["MotionScope"] = decision.MotionScope, ["Degrees"] = decision.Degrees,
                    ["DeviceState"] = decision.DeviceState, ["TargetEntityId"] = decision.TargetEntityId,
                    ["WireVersion"] = decision.WireVersion, ["OriginalKind"] = decision.OriginalKind, ["RequiresFullReview"] = decision.RequiresFullReview,
                    ["DeviceTargetIsProgramBound"] = decision.DeviceTargetIsProgramBound }, ["raw"] = raw,
                ["elapsedSeconds"] = item["elapsedSeconds"]?.DeepClone(),
                ["finishReason"] = item["response"]?["choices"]?[0]?["finish_reason"]?.DeepClone() });
        }
        File.WriteAllText(outputPath, new JObject { ["scope"] = "Production parser/compiler/reference binding. Schema validity does not prove semantic correctness or Unity execution.",
            ["count"] = rows.Count, ["validated"] = rows.Count(v => (bool)v["validated"]), ["rows"] = rows }.ToString(Formatting.Indented));
        Console.WriteLine("Validated " + rows.Count + " live results with production protocol; saved " + outputPath);
    }

    public static void ExportFast(string path)
    {
        string persona = File.ReadAllText("Assets/AIChatTookit/Prompts/persona.txt");
        string languagePath = "Assets/AIChatTookit/Prompts/language.txt";
        string language = File.Exists(languagePath) ? File.ReadAllText(languagePath) : "";
        var common = new JObject { ["actorDraftCompiler"] = true,
            ["actorDraftCapabilities"] = FastLoopProtocol.ActorDraftCapabilities(true), ["headTask"] = null,
            ["headAvailable"] = true, ["headAngles"] = new JObject { ["yaw"] = 0, ["pitch"] = 0 },
            ["legacyGazeActive"] = false, ["bodyBusy"] = false,
            ["roomTask"] = new JObject { ["connected"] = true, ["activeTaskId"] = "", ["followActive"] = false, ["bodyReserved"] = false, ["phase"] = "idle" },
            ["eyesOpen"] = true, ["vision"] = new JObject { ["imageAttached"] = false, ["state"] = "unavailable" },
            ["waitingTask"] = null, ["executionFeedback"] = null, ["currentTaskControlResult"] = null,
            ["spatialObjectMemory"] = Memory(),
            ["scope"] = "Historical object memory is not current scene contents. Preset head control does not cover numeric body turns or object goals." };
        var cases = new JArray();
        void Add(string id, string user, JObject facts, string expected)
        {
            string context = facts.ToString(Formatting.None);
            cases.Add(new JObject { ["id"] = id, ["currentUser"] = user, ["expectedSemantic"] = expected,
                ["request"] = new JObject { ["model"] = "qwen3.6-35b-a3b", ["stream"] = false, ["max_tokens"] = 256,
                    ["temperature"] = 0, ["enable_thinking"] = false, ["chat_template_kwargs"] = new JObject { ["enable_thinking"] = false },
                    ["messages"] = new JArray(new JObject { ["role"] = "system", ["content"] = FastLoopPrompt.System(persona, language, "ja") + FastLoopPrompt.RecoveryRules(context) },
                        new JObject { ["role"] = "user", ["content"] = FastLoopPrompt.User(context, user) }),
                    ["response_format"] = FastLoopProtocol.ResponseFormatForContext(context) } });
        }
        var lyrics = (JObject)common.DeepClone();
        lyrics["bodyBusy"] = true; lyrics["roomTask"] = new JObject { ["connected"] = true, ["activeTaskId"] = "follow-8", ["followActive"] = true,
            ["bodyReserved"] = true, ["taskMode"] = "follow", ["phase"] = "following" };
        lyrics["recentDialogue"] = FastLoopPrompt.Dialogue(new JArray(new JObject { ["turn"] = 7, ["text"] = "先停下跟随。" },
            new JObject { ["turn"] = 8, ["text"] = "接下来一直跟着我。" }), new JArray(new JObject { ["turn"] = 8, ["text"] = "好，我试着跟上你。" }), 9);
        lyrics["inputObservation"] = new JObject { ["source"] = "accepted-final-audio", ["acoustic"] = new JObject { ["singingProbability"] = .97,
            ["timeOrder"] = new JObject { ["recordingEnded"] = true, ["hasSingingCandidate"] = true, ["scope"] = "Past observed singing, not an instruction." } } };
        Add("lyrics-with-old-follow-stop", "初めてのルーブはなんで断はなかたは私だけのモナリザの男に出会てたから。", lyrics,
            "No task control: current lyrics do not stop follow or request automatic singing/object operation. Natural listening response is appropriate.");
        var repairedLyrics = (JObject)lyrics.DeepClone();
        repairedLyrics["decisionRepair"] = new JObject { ["attempt"] = 1, ["rejection"] = "control_evidence_not_current_user",
            ["scope"] = "The prior response was invalid and has not authorized or published anything. " +
                "Make one fresh role choice for the complete current_user, using current facts. " +
                "Singing, a quotation, feedback or a question alone is not a spatial instruction. " +
                "Use reply for an appropriate response, recording_singing for an adopted recording goal, " +
                "or legacy only when a currently adopted goal actually needs the full planner. " +
                "A failed decision is not evidence of such a goal. Historical control evidence cannot authorize a new stop; " +
                "any control choice must cite the exact current input and match its actual task owner. " +
                "Refusal, clarification and choosing no new action remain available." };
        Add("lyrics-control-evidence-repair", "初めてのルーブはなんで断はなかたは私だけのモナリザの男に出会てたから。", repairedLyrics,
            "One production decisionRepair: task_control none, no spatial action inferred from invalid prior response; listen/respond naturally.");
        var correction = (JObject)common.DeepClone();
        correction["previousPlanReferent"] = new JObject { ["goal"] = "转身90度，看到原灯就关掉", ["state"] = "cancelled", ["targetEntityId"] = "lamp-1",
            ["remainingGoal"] = "确认原灯后关掉", ["reason"] = "user-started-speaking", ["sourceTurn"] = 19,
            ["results"] = new JArray(new JObject { ["intent"] = "turn-in-place", ["succeeded"] = true }), ["scope"] = "Historical referent only; current goal requires new adoption." };
        Add("reopen-correction-same-entity", "啊，我说错了，不是关掉，是把它重新开起来。", correction,
            "New choice about reopening prior same lamp; if adopting use complete planner, no repeat of completed turn or head-preset-only detour. Explicit refusal remains valid.");
        Add("query-history-not-action", "你还记得刚才的小灯在哪里吗？我只是问一下，不用动。", (JObject)common.DeepClone(),
            "reply without action; distinguish historical observed location from current visibility.");
        Add("ordinary-follow", "接下来跟着我一直跟着我。", (JObject)common.DeepClone(),
            "reply may adopt current-user follow or choose legacy; no device goal/control derived from lamp history.");
        Add("compound-body90-open", "向右转身90度，然后试着把之前的小灯重新打开。", (JObject)common.DeepClone(),
            "If accepted, legacy full compound route; no head18 shortcut; refusal/partial adoption must be explicit.");
        File.WriteAllText(path, new JObject { ["scope"] = "Production Fast prompt and context-constrained schema over representative fresh/historical fact snapshots; no image attached, not a full Unity replay.", ["cases"] = cases }.ToString(Formatting.Indented));
    }
}
