using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NeEEvA.Motion;
using UnityEditor;
using UnityEngine;

/// <summary>Tests source-backed requirements through the actual parser, compiler and ChatSample.</summary>
public static class ArdySemanticMotionRegression
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;
    private static readonly List<string> cases = new List<string>();

    [Serializable] private sealed class Report
    {
        public bool passed;
        public int checks;
        public string[] cases;
        public string failure, checkedAtUtc = DateTime.UtcNow.ToString("o"), unityVersion = Application.unityVersion;
        public string scope = "Production C# parser, requirement provenance and inheritance, compiler, actual inactive ChatSample dispatch/context/lifecycle; no physics or naturalness claim.";
        public string limitation = "Exact quote and numeric-literal checks verify references, not whether an LLM correctly understood the user's intent, negation or which joint a number describes.";
    }

    public static void RunInteractive()
    {
        checks = 0; cases.Clear();
        var report = new Report();
        try
        {
            LedgerBehavior();
            CompileBehavior();
            DefaultAndOptInBehavior();
            ChatBehavior();
            ChannelIsolation();
            report.passed = true;
        }
        catch (Exception error) { report.failure = error.ToString(); throw; }
        finally
        {
            report.checks = checks; report.cases = cases.ToArray();
            string path = Path.GetFullPath("Tools/MotionAdapter/reports/semantic-motion-regression.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(report, true) + "\n");
        }
        Debug.Log("[ArdySemanticMotionRegression] passed " + checks + " checks.");
    }

    public static void RunBatch()
    {
        try { RunInteractive(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static ArdyActionPlan Greeting() => new ArdyActionPlan {
        purpose = "greet", scope = "new", mode = "oscillate", left = "forward", right = "forward",
        leftPalm = "partner", rightPalm = "partner", joint = "wrists", tempo = "brisk",
        size = "small", repeat = "twice", end = "hold"
    };

    private static void LedgerBehavior()
    {
        var ledger = new ArdyActionConstraintLedger();
        const string user = "双臂向前，掌心朝向我，打个招呼。";
        ledger.RecordUserTurn(user);
        var plan = Greeting(); plan.userTurn = 1; plan.evidence = user;
        plan.lockFields = "left,right,leftPalm,rightPalm";
        var prepared = ledger.Prepare(plan, 1);
        Check(!ledger.HasConstraints, "Preparing an uncompiled request mutated requirements");
        ArdyActionPlanCompiler.Compile(prepared.effective); ledger.Commit(prepared);
        Check(ledger.Entries.Length == 4 && ledger.Entries.All(e => e.userTurn == 1 && e.evidence == user), "Source records were lost");
        Reject(() => ledger.Prepare(plan, 1), "An autonomous continuation discarded same-turn requirements by reusing their original quote");
        var selfRelease = plan.Copy(); selfRelease.scope = "continue"; selfRelease.releaseFields = "left";
        Reject(() => ledger.Prepare(selfRelease, 1), "An original quote was reused as authority to revoke itself");
        ledger.ClearGoalOnStop(1);
        Check(ledger.HasConstraints, "A same-user autonomous stop erased the user's requirements");
        var mutable = ledger.Entries; mutable[0].value = "up";
        Check(ledger.Entries.All(e => e.value != "up"), "A consumer mutated live requirements");

        ledger.RecordUserTurn("现在快一些。" );
        var follow = new ArdyActionPlan { scope = "continue", mode = "oscillate", joint = "wrists", tempo = "brisk" };
        var merged = ledger.Prepare(follow, 2);
        var control = ArdyActionPlanCompiler.Compile(merged.effective).controlPlan;
        Check(control.left == "forward" && control.right == "forward" && control.leftPalm == "partner" && control.rightPalm == "partner",
            "Tempo correction discarded original posture or palms");
        Check(control.leftBendAuto && control.rightBendAuto, "Unspecified elbows became hard numeric requirements");
        Check(ledger.Entries.All(e => e.userTurn == 1), "Inheritance falsely attributed old requirements to the new turn");
        var conflict = follow.Copy(); conflict.left = "outward";
        Reject(() => ledger.Prepare(conflict, 2), "A changed direction bypassed an existing requirement");
        var reset = Greeting();
        Reject(() => ledger.Prepare(reset, 2), "A new goal silently discarded requirements without current evidence");
        var stale = Greeting(); stale.userTurn = 1; stale.evidence = user; stale.lockFields = "left";
        Reject(() => ledger.Prepare(stale, 2), "Old evidence acquired current-user authority");
        Reject(() => ledger.Prepare(follow, 1), "A stale response changed requirements");

        ledger.RecordUserTurn("改为左臂向侧方，右臂保持。" );
        var replace = new ArdyActionPlan { scope = "continue", mode = "oscillate", joint = "wrists",
            left = "outward", releaseFields = "left", lockFields = "left", userTurn = 3,
            evidence = "改为左臂向侧方" };
        var replacement = ledger.Prepare(replace, 3); ArdyActionPlanCompiler.Compile(replacement.effective); ledger.Commit(replacement);
        Check(ledger.Entries.Single(e => e.field == "left").value == "outward" &&
              ledger.Entries.Single(e => e.field == "right").userTurn == 1, "Selective replacement lost the unaffected arm or its provenance");
        var race = ledger.Prepare(new ArdyActionPlan { scope = "continue", mode = "oscillate", joint = "wrists" }, 3);
        ledger.RecordUserTurn("停。" );
        Reject(() => ledger.Commit(race), "A plan prepared before newer input still committed");
        ledger.Reset(); Check(!ledger.HasConstraints && ledger.CurrentUserTurn > 4, "Rebinding retained requirements or reused a source ID");
        cases.Add("source quotes; transaction; inherited geometry; unilateral replacement; stale turns; reset");

        var numeric = new ArdyActionConstraintLedger(); numeric.RecordUserTurn("双臂向前平伸打招呼。" );
        var invented = Greeting(); invented.userTurn = 1; invented.evidence = numeric.CurrentUserText;
        invented.leftBend = "0"; invented.lockFields = "leftBend";
        Reject(() => numeric.Prepare(invented, 1), "An invented zero-degree elbow became a hard requirement");
        numeric.RecordUserTurn("双臂必须伸直，挥动两次，幅度10度，用时3秒。" );
        var explicitPlan = Greeting(); explicitPlan.tempo = explicitPlan.size = explicitPlan.repeat = "";
        explicitPlan.leftBend = explicitPlan.rightBend = "0"; explicitPlan.amplitude = "10";
        explicitPlan.cycles = "2"; explicitPlan.seconds = "3";
        explicitPlan.userTurn = 2; explicitPlan.evidence = numeric.CurrentUserText;
        explicitPlan.lockFields = "leftBend,rightBend,amplitude,cycles,seconds";
        var exact = numeric.Prepare(explicitPlan, 2);
        var exactControl = ArdyActionPlanCompiler.Compile(exact.effective).controlPlan;
        numeric.Commit(exact);
        Check(exactControl.leftBend == 0 && exactControl.rightBend == 0 && !exactControl.leftBendAuto &&
            exactControl.cycles == 2 && exactControl.amplitude == 10 && exactControl.seconds == 3 && exactControl.timingDurationFixed,
            "An explicit numeric requirement was relaxed or converted to a default");
        numeric.RecordUserTurn("保持这些要求，再做一次。" );
        var inherited = Greeting(); inherited.scope = "continue"; inherited.tempo = inherited.size = inherited.repeat = "";
        var inheritedControl = ArdyActionPlanCompiler.Compile(numeric.Prepare(inherited, 3).effective).controlPlan;
        Check(inheritedControl.timingDurationFixed && inheritedControl.leftBend == 0, "Inherited numeric requirements demanded new invented quotes");
        var ambiguous = inherited.Copy(); ambiguous.tempo = "brisk";
        Reject(() => ArdyActionPlanCompiler.Compile(numeric.Prepare(ambiguous, 3).effective), "New tempo silently overrode a fixed duration");
        cases.Add("numeric provenance; strict elbows; fixed duration; inherited values; conflicting representations");
    }

    private static void CompileBehavior()
    {
        var greeting = ArdyActionPlanCompiler.Compile(Greeting()).controlPlan;
        var timing = ArdyAdaptiveTiming.Resolve(greeting);
        Check(greeting.timingPolicy == "adaptive-v1" && greeting.axis == "palm-normal" && timing.actualSeconds < 2.1f,
            "Natural greeting still compiled to the old 3.2-second motion or an inappropriate fixed axis");
        var explain = Greeting(); explain.purpose = "explain";
        var same = ArdyActionPlanCompiler.Compile(explain).controlPlan;
        Check(JsonUtility.ToJson(greeting) == JsonUtility.ToJson(same), "Purpose secretly selected a hardcoded pose or gesture clip");
        var head = new ArdyActionPlan { purpose = "agree", mode = "oscillate", joint = "head", axis = "right", tempo = "gentle", size = "small", repeat = "once" };
        var headControl = ArdyActionPlanCompiler.Compile(head).controlPlan;
        Check(headControl.amplitude == 4 && headControl.cycles == 1 && ArdyAdaptiveTiming.Resolve(headControl).resolvedFrequencyHz < .5f,
            "The accepted gentle head preference inherited wrist greeting speed");
        var display = new ArdyActionPlan { purpose = "display", mode = "hold", left = "forward", leftPalm = "up", end = "hold" };
        var staticControl = ArdyActionPlanCompiler.Compile(display).controlPlan;
        Check(staticControl.joint == "none" && ArdyAdaptiveTiming.Resolve(staticControl).actualSeconds == 0, "Static display gained an unwanted oscillation");
        var free = new ArdyActionPlan { purpose = "other", mode = "free", text = "A standing person opens the arms and relaxes them while staying in place." };
        Check(ArdyActionPlanCompiler.Compile(free).name == "generate", "Open motion lost the ARDY generation route");
        free.left = "forward"; Reject(() => ArdyActionPlanCompiler.Compile(free), "Free text silently ignored a structured constraint");
        cases.Add("generic compilation; purpose does not choose clips; wrist/head timing; static and free routes");
    }

    private static void ChatBehavior()
    {
        var host = new GameObject("ArdySemanticMotionRegression"); host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            chat.ConfigureSemanticMotionPlanning(true);
            Set(chat, "m_LogStreamTimings", false);
            var actions = new List<DialogueMotionIntent>(); chat.MotionIntentRequested += actions.Add;
            chat.ConfigureMotionOutput(true, true);
            Call(chat, "RecordAcceptedMotionUserTurn", "双臂向前，掌心朝向我，打招呼。" );
            int generation = (int)Call(chat, "BeginFormalResponseGeneration");
            string facts = (string)Call(chat, "BuildMotionRequestContext", "");
            Check(facts.Contains("\"userTurn\":2") && facts.Contains(DialogueMotionProtocol.SemanticOutputContract), "The real chat request omitted its current source or semantic contract");
            const string tag = "<motion name=\"plan\" purpose=\"greet\" mode=\"oscillate\" scope=\"new\" left=\"forward\" right=\"forward\" leftPalm=\"partner\" rightPalm=\"partner\" joint=\"wrists\" tempo=\"brisk\" end=\"hold\" userTurn=\"2\" evidence=\"双臂向前，掌心朝向我\" lock=\"left,right,leftPalm,rightPalm\"/>";
            var parsed = Parse(tag, generation);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)parsed, false);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)parsed, false);
            Check(actions.Count == 1 && actions[0].Name == "compose" && actions[0].ControlPlan.timingPolicy == "adaptive-v1", "A semantic plan did not dispatch exactly one executable compiled command");
            facts = (string)Call(chat, "BuildMotionRequestContext", "");
            Check(facts.Contains("\"field\":\"leftPalm\"") && facts.Contains("历史要求，不是已达到姿态"), "Chat lost provenance or mislabeled a requested pose as achieved");
            Call(chat, "RecordAcceptedMotionUserTurn", "再做一次。" );
            // A captured previous response keeps its source even if its generation is accidentally current.
            var continuing = Parse("<motion name=\"plan\" scope=\"continue\" mode=\"oscillate\" joint=\"wrists\" tempo=\"brisk\"/>", generation);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)continuing, false);
            Check(actions.Count == 1, "Old response acquired the new user's authority");
            generation = (int)Call(chat, "BeginFormalResponseGeneration");
            continuing = Parse("<motion name=\"plan\" scope=\"continue\" mode=\"oscillate\" joint=\"wrists\" tempo=\"brisk\"/>", generation);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)continuing, false);
            Check(actions.Count == 2 && actions[1].ControlPlan.left == "forward" && actions[1].ControlPlan.leftPalm == "partner", "Actual chat continuation lost the original arm or palm goal");
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("right-wave", "", generation, 99), false);
            Check(actions.Count == 2 && ((string)Call(chat, "BuildMotionRequestContext", "")).Contains("不能通过旧动作通道跳过约束"), "A legacy clip bypassed requirements or the rejection was hidden from the next request");
            Set(chat, "m_ReadingUserInput", true);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("none", "", generation, 100), false);
            Set(chat, "m_ReadingUserInput", false);
            Check(((ArdyActionConstraintLedger)Get(chat, "m_ActionConstraints")).HasConstraints, "Reading user text cleared live requirements");
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("none", "", generation, 101), false);
            Check(actions.Count == 3 && !((ArdyActionConstraintLedger)Get(chat, "m_ActionConstraints")).HasConstraints, "An explicit stop did not clear the action goal");
            chat.ConfigureMotionOutput(true, false);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)Parse(tag, generation), false);
            Check(actions.Count == 3 && !((string)Call(chat, "BuildMotionRequestContext", "")).Contains(DialogueMotionProtocol.SemanticOutputContract), "Basic-only mode executed or advertised semantic plans");
            chat.ConfigureMotionOutput(true, true);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("nod", "", generation, 200), false);
            Check(actions.Count == 3, "An old stream acquired a newly bound avatar's source authority");
            generation = (int)Call(chat, "BeginFormalResponseGeneration");
            var older = (Action<List<SpeechText>, string>)Call(chat, "CaptureMotionResponseCallback");
            var newer = (Action<List<SpeechText>, string>)Call(chat, "CaptureMotionResponseCallback");
            older(null, null);
            Check((int)Get(chat, "m_FormalResponseGeneration") == generation, "An obsolete same-user callback started a new formal generation");
            Call(chat, "BeginFormalResponseGeneration");
            newer(null, null);
            Check((int)Get(chat, "m_FormalResponseGeneration") == generation + 1, "A callback older than the current stream mutated the response generation");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        cases.Add("actual ChatSample source snapshots; duplicate rejection; continuation; rejection feedback; legacy bypass; user reading; stop; capability reset");
    }

    private static void DefaultAndOptInBehavior()
    {
        var host = new GameObject("ArdySemanticPlanningOptInRegression"); host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            int actions = 0; chat.MotionIntentRequested += _ => actions++;
            chat.ConfigureMotionOutput(true, true);
            int generation = (int)Call(chat, "BeginFormalResponseGeneration");
            string context = (string)Call(chat, "BuildMotionRequestContext", "");
            Check(!chat.SemanticMotionPlanningEnabled && context.Contains(DialogueMotionProtocol.GeneratedOutputContract) &&
                !context.Contains(DialogueMotionProtocol.SemanticOutputContract), "Unvalidated semantic planning replaced the default chat contract");
            var plan = Parse("<motion name=\"plan\" joint=\"head\" axis=\"right\" repeat=\"once\"/>", generation);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)plan, false);
            Check(actions == 0, "Semantic planning executed without opting in");
            chat.ConfigureSemanticMotionPlanning(true);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)plan, false);
            Check(actions == 0, "Changing planning mode revived an old response");
            generation = (int)Call(chat, "BeginFormalResponseGeneration");
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)Parse("<motion name=\"plan\" joint=\"head\" axis=\"right\" repeat=\"once\"/>", generation), false);
            Check(actions == 1, "The experimental planning opt-in failed to enable a new valid response");
            chat.ConfigureSemanticMotionPlanning(false);
            Check(((string)Call(chat, "BuildMotionRequestContext", "")).Contains(DialogueMotionProtocol.GeneratedOutputContract), "Disabling the experiment did not restore the previous contract");
            Set(chat, "m_UseSemanticMotionPlanning", true); // Actual serialized Inspector change.
            Call(chat, "RecordAcceptedMotionUserTurn", "轻轻点一次头。" );
            generation = (int)Call(chat, "BeginFormalResponseGeneration");
            var ledger = (ArdyActionConstraintLedger)Get(chat, "m_ActionConstraints");
            int source = ledger.CurrentUserTurn;
            context = (string)Call(chat, "BuildMotionRequestContext", "");
            Check(ledger.CurrentUserTurn == source && ledger.CurrentUserText == "轻轻点一次头。" && context.Contains("轻轻点一次头。"), "Inspector mode synchronization erased the first accepted user request");
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)Parse("<motion name=\"plan\" joint=\"head\" axis=\"right\" repeat=\"once\"/>", generation), false);
            Check(actions == 2, "First response after an Inspector mode change had an obsolete source snapshot");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        cases.Add("default legacy contract; explicit semantic opt-in; mode change source invalidation; restore default");
    }

    private static void ChannelIsolation()
    {
        const string tag = "<motion name=\"plan\" purpose=\"agree\" mode=\"oscillate\" joint=\"head\" axis=\"right\" tempo=\"gentle\" repeat=\"once\"/>";
        const string raw = "<lang code=\"zh\"/>嗯，明白了。" + tag;
        for (int split = 0; split <= raw.Length; split++)
        {
            var speech = new StringBuilder(); var channels = new RoleOutputChannels(part => speech.Append(part.Text));
            channels.Push(raw.Substring(0, split)); channels.Push(raw.Substring(split)); channels.Finish();
            string executable = channels.ToExecutableText();
            Check(speech.ToString() == "嗯，明白了。", "Semantic metadata leaked into speech at split " + split);
            Check(DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out var intent, out _) && intent.ActionPlan != null && executable == "嗯，明白了。", "Semantic action failed streaming at split " + split);
        }
        foreach (string hidden in new[] { "<think>" + tag + "</think>", "`" + tag + "`", "```\n" + tag + "\n```", "<say>" + tag + "</say>" })
        {
            var channels = RoleOutputChannels.Parse(hidden); string executable = channels.ToExecutableText();
            Check(!DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out _, out _), "Private or quoted semantic plan became executable");
        }
        cases.Add("semantic plan streaming at every split; private/quoted/TTS isolation");
    }

    private static DialogueMotionIntent Parse(string raw, int generation)
    {
        Check(DialogueMotionProtocol.TryExtract(ref raw, generation, 1, out var intent, out string error), "Production parser rejected fixture: " + error);
        return intent;
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (ArgumentException) { checks++; return; } catch (InvalidOperationException) { checks++; return; }
        throw new InvalidOperationException(message);
    }
    private static object Get(object target, string name) => target.GetType().GetField(name, Instance).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Instance).SetValue(target, value);
    private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Instance).Invoke(target, args);
}
