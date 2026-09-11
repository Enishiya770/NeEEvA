using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using NeEEvA.Motion;

/// <summary>Protocol boundaries, real chat speech queues and cancellation; no network or model calls.</summary>
public static class ArdyDialogueMotionRegression
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int checks;

    [MenuItem("Tools/NeEEvA/Run ARDY Dialogue Motion Regression")]
    public static void RunInteractive()
    {
        checks = 0;
        CheckRouting();
        CheckValidation();
        CheckControlPlan();
        CheckComposeChannel();
        CheckPalmChannel();
        CheckComposeChatHooks();
        CheckMotionProtocolFeedback();
        CheckAutonomousGeneratedContract();
        CheckCompositionalPlanningContext();
        CheckPureCollectedMotion();
        CheckChatHooks();
        CheckMotionLifetimeAcrossResponses();
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/dialogue-motion-regression.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(new Report { checks = checks }, true) + "\n");
        Debug.Log($"[ArdyDialogueMotionRegression] passed {checks} assertions; {path}");
    }

    public static void RunBatch()
    {
        try { RunInteractive(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    [Serializable] private sealed class Report
    {
        public bool passed = true;
        public int checks;
        public string unityVersion = Application.unityVersion;
        public string checkedAtUtc = DateTime.UtcNow.ToString("o");
        public string environment = "Real Unity Editor; actual inactive ChatSample and production parser sources, no stubs";
        public string scope = "Completed action channel, autonomous and compositional planning contract delivery, every stream boundary, malformed/private/quoted input, TTS queue, stale generation rejection, response-independent body lifetime, exact same-chain idempotency and pure-motion interruption";
        public string excludes = "No model or service request; autonomous trigger appropriateness needs real dialogue review, and generated motion semantics/naturalness need separate motion review";
    }

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void CheckRouting()
    {
        const string raw = "<lang code=\"zh\"/>你好。<motion name=\"right-wave\"/>";
        for (int split = 0; split <= raw.Length; split++)
        {
            var speech = new StringBuilder();
            var channels = new RoleOutputChannels(part => speech.Append(part.Text));
            channels.Push(raw.Substring(0, split));
            channels.Push(raw.Substring(split));
            channels.Finish();
            string executable = channels.ToExecutableText();
            Check(speech.ToString() == "你好。", "Motion metadata leaked into speech at split " + split);
            Check(DialogueMotionProtocol.TryExtract(ref executable, 7, 13, out var intent, out _) &&
                intent.Name == "right-wave" && intent.ResponseGeneration == 7 && intent.Sequence == 13 &&
                executable == "你好。", "Action metadata or identity lost at split " + split);
        }
        foreach (string rawQuoted in new[] {
            "<thought><motion name=\"nod\"/></thought>",
            "<think><motion name=\"nod\"/></think>",
            "`<motion name=\"nod\"/>`", "```\n<motion name=\"nod\"/>\n```",
            "<say><motion name=\"nod\"/>说明。</say>",
            "＜motion name=\"nod\"/＞", "<motion name=\"nod\"" })
        {
            var channels = new RoleOutputChannels();
            foreach (char c in rawQuoted) channels.Push(c.ToString());
            channels.Finish();
            string executable = channels.ToExecutableText();
            Check(!DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out _, out _),
                "Quoted/private/incomplete motion became executable: " + rawQuoted);
            Check(!channels.Speech.Contains("motion"), "Private/malformed metadata leaked into speech");
        }
        string silent = RoleOutputChannels.Parse("<silent/><motion name=\"nod\"/>").ToExecutableText();
        Check(DialogueMotionProtocol.TryExtract(ref silent, 2, 1, out var nod, out _) && nod.Name == "nod",
            "Silent nonverbal gesture was lost");
    }

    private static void CheckValidation()
    {
        foreach (string name in new[] { "left-wave", "right-wave", "nod", "shake-head", "none" })
        {
            string executable = "<motion name=\"" + name + "\"/>";
            Check(DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out var intent, out _) && intent.Name == name,
                "Accepted gesture name rejected: " + name);
        }
        string generated = "<motion name=\"generate\" text=\"A person points forward while standing.\"/>";
        Check(DialogueMotionProtocol.TryExtract(ref generated, 1, 1, out var generation, out _) &&
            generation.Description == "A person points forward while standing.", "Generated description changed");
        foreach (string invalid in new[] {
            "<motion name=\"nod\"/><motion name=\"none\"/>",
            "<motion name=\"nod\" name=\"shake-head\"/>", "<motion name=\"nod\" seed=\"1\"/>",
            "<motion name=\"nod\">", "<motion name=\"jump\"/>", "<motion name=\"generate\"/>",
            "<motion name=\"generate\" text=\"" + new string('a', 241) + "\"/>",
            "<motion name=\"generate\" text=\"&lt;tool/&gt;\"/>",
            "<motion name=\"generate\" text=\"hello > forbidden speech suffix\"/>",
            "<motion name=\"generate\" text=\"<motion name='nod'/>\"/>",
            "<motion name=\"nod\" text=\"twenty times\"/>" })
        {
            string executable = invalid;
            Check(!DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out _, out string reason) &&
                !string.IsNullOrEmpty(reason), "Invalid motion accepted: " + invalid);
            Check(executable.Length == 0, "Rejected action left a suffix in execution text");
        }
    }

    private static void CheckAutonomousGeneratedContract()
    {
        var host = new GameObject("ArdyAutonomousGeneratedContractRegression");
        host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            chat.ConfigureSemanticMotionPlanning(true);
            Set(chat, "m_FormalResponseGeneration", 31);
            Set(chat, "m_LogStreamTimings", false);
            int actions = 0;
            DialogueMotionIntent committed = default;
            chat.MotionIntentRequested += intent => { actions++; committed = intent; };
            chat.ConfigureMotionOutput(true, true);
            string context = (string)Call(chat, "BuildMotionRequestContext", "Public synthetic conversation.");
            Check(context.Contains(DialogueMotionProtocol.SemanticOutputContract) &&
                context.IndexOf(DialogueMotionProtocol.SemanticOutputContract, StringComparison.Ordinal) ==
                    context.LastIndexOf(DialogueMotionProtocol.SemanticOutputContract, StringComparison.Ordinal),
                "Autonomous request did not receive exactly the complete single generated-capable contract");
            foreach (string basic in new[] { "left-wave", "right-wave", "nod", "shake-head", "none" })
                Check(context.Contains(basic), "The single complete contract lost a basic action name: " + basic);
            // Explicit user constraints (for example a straight elbow) must remain in
            // the contract. Check the autonomous permission, not those shared words.
            Check(DialogueMotionProtocol.SemanticOutputContract.Contains("当前交流适合时可自主动作") &&
                DialogueMotionProtocol.SemanticOutputContract.Contains("随意做动作时自行选择"),
                "Generated contract lost autonomous selection during suitable conversation or unspecified motion requests");
            Check(context.Contains("没有姿态、掌向或节奏约束的独立") && context.Contains("不要每句话动"),
                "Autonomous generation removed basic priority or optional motion");
            Check(context.Contains("left/right=") && context.Contains("正前水平") && context.Contains("end=idle|hold") &&
                context.Contains("240字符") && context.Contains("固定6秒"),
                "Generated motion planning lost its observable intent or execution bounds");
            Check(context.Contains("原地限制根与腿，不限制上身关节移动") && context.Contains("显式用户要求优先"),
                "Stationary or subtle defaults incorrectly prohibit upper-body movement or explicit amplitude");
            Check(context.Contains("精细单指") && context.Contains("不执行根腿位移") && context.Contains("真实物体接触"),
                "Planning contract omitted unsupported execution capabilities");
            Check(context.Contains("根据目的选择") && context.Contains("静态展示不添加挥动") &&
                context.Contains("用户指定掌心向下不可被问候默认覆盖") && context.Contains("不绑定固定姿势"),
                "Palm planning lost purpose-based completion, static displays or explicit constraints");
            Check(context.Contains("auto仅在活动手都有非keep掌向时取各自palm-normal") && context.Contains("解算可达屈肘") &&
                context.Contains("伸直对应Bend=0，不能悄悄删掉") && context.Contains("未获执行反馈不说已完成"),
                "Planning again assumes a fixed wave axis or silently discards reachability constraints");
            Check(context.Contains("所有属性值用双引号") && context.Contains("必须以/>结束") &&
                context.Contains("禁止JSON、函数式调用或裸属性") && context.Contains("基本标签严格只有name"),
                "Semantic contract lost strict XML framing or basic/plan attribute separation");
            Check(context.Contains("用户明确要求可执行动作时必须提交标签") && context.Contains("只有口头承诺不算提交"),
                "Semantic action requests can again be satisfied by speech without an executable tag");
            Check(context.Contains("natural是正常交流频率") && context.Contains("自然不等于慢") &&
                context.Contains("gentle是刻意缓慢或轻点头"),
                "Semantic tempo again conflates normal communication with deliberately slow movement");

            // No separate user-request/classifier flag is needed. Use a new, free-form
            // motion and a contact-free clause to exercise syntax, not a word blacklist.
            const string description = "Bring the left forearm slightly forward, ending with the open hand at chest height without touching anything.";
            const string raw = "我可以再解释一下。<motion name=\"generate\" text=\"" + description + "\"/>";
            for (int split = 0; split <= raw.Length; split++)
            {
                var speech = new StringBuilder();
                var channels = new RoleOutputChannels(part => speech.Append(part.Text));
                channels.Push(raw.Substring(0, split));
                channels.Push(raw.Substring(split));
                channels.Finish();
                string executable = channels.ToExecutableText();
                Check(speech.ToString() == "我可以再解释一下。",
                    "Autonomously selected generated description leaked into speech at split " + split);
                Check(DialogueMotionProtocol.TryExtract(ref executable, 31, split, out var intent, out _) &&
                    intent.Name == "generate" && intent.Description == description && executable == "我可以再解释一下。",
                    "Free-form autonomous motion failed production parsing at split " + split);
                Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)intent, false);
            }
            Check(actions == 1 && committed.Name == "generate" && committed.Description == description,
                "Autonomous free-form motion did not dispatch once under the real same-generation deduplication");
            chat.ConfigureMotionOutput(true, false);
            Check(!((string)Call(chat, "BuildMotionRequestContext", "")).Contains(DialogueMotionProtocol.SemanticOutputContract),
                "Disabling generation left autonomous generation instructions active");
            Check(((string)Call(chat, "BuildMotionRequestContext", "")).Contains(DialogueMotionProtocol.BasicOutputContract),
                "A basic-only bridge lost its original output contract");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckCompositionalPlanningContext()
    {
        var host = new GameObject("ArdyCompositionalPlanningContextRegression");
        host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            chat.ConfigureSemanticMotionPlanning(true);
            Set(chat, "m_FormalResponseGeneration", 41);
            Set(chat, "m_LogStreamTimings", false);
            int actions = 0;
            chat.MotionIntentRequested += intent => actions++;
            chat.ConfigureMotionOutput(true, true);
            string fact = "{\"phase\":\"idle\"}";
            chat.MotionStateContextRequested += () => fact;
            string context = (string)Call(chat, "BuildMotionRequestContext", "Synthetic history describes an earlier held posture.");
            Check(context.Contains("Synthetic history") && context.Contains(fact),
                "Current idle facts or earlier conversation context were lost");
            Check(context.Contains("没有姿态、掌向或节奏约束的独立") && context.Contains("有附带约束或新动作默认name=plan") &&
                context.Contains("mode=free表示多部位连续自由上身动作"),
                "Basic gesture priority again discards the rest of a compositional goal");
            Check(context.Contains("准备臂姿并保持") && context.Contains("必须选活动关节") &&
                context.Contains("forward是正前水平，outward是各臂向自身外侧水平"),
                "Compositional planning lost posture, moving-joint or coordinate distinctions");
            Check(context.Contains("继承账本未撤销约束") && context.Contains("历史约束不代表仍在持姿") &&
                context.Contains("idle须重新达到目标"),
                "Follow-up planning confuses remembered goals with current physical pose");
            Check(context.Contains("固定6秒窗口，结束回待机") && context.Contains("没有阶段达标反馈"),
                "Planning implies persistent holding or feedback-controlled event sequencing");

            // Free-form compositions remain one command. These are protocol fixtures,
            // not presets, actual model outputs or evidence of motion quality.
            foreach (string description in new[] {
                "Raise both arms out to the sides at shoulder height, keep the elbows straight and torso still, and gently nod once while holding the arms steady.",
                "Hold the left arm straight forward at shoulder height while extending the right arm out to the right at shoulder height, keeping the torso forward." })
            {
                string raw = "<silent/><motion name=\"generate\" text=\"" + description + "\"/>";
                var channels = RoleOutputChannels.Parse(raw);
                string executable = channels.ToExecutableText();
                Check(DialogueMotionProtocol.TryExtract(ref executable, 41, actions + 1, out var intent, out _) &&
                    intent.Name == "generate" && intent.Description == description && channels.Speech.Length == 0,
                    "A valid single-command composition lost constraints or entered speech");
                Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)intent, false);
            }
            Check(actions == 2, "Distinct compositional goals were not dispatched independently");
            fact = "";
            string idle = (string)Call(chat, "BuildMotionRequestContext", "Synthetic history describes an earlier held posture.");
            Check(!idle.Contains("[当前身体动作；程序事实]") && idle.Contains("历史约束不代表仍在持姿"),
                "A finished action became a fabricated currently-held posture");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckControlPlan()
    {
        foreach (string direction in new[] { "current", "forward", "outward", "up", "down", "forward-up", "outward-up" })
        {
            var plan = new ArdyControlPlan { left = direction };
            plan.Validate();
            Check(plan.right == "none" && plan.leftBend == 8 && plan.seconds == 3.2f && plan.end == "idle" &&
                plan.leftPalm == "keep" && plan.rightPalm == "keep" && !plan.leftBendAuto && !plan.rightBendAuto,
                "Control-plan defaults changed");
        }
        new ArdyControlPlan { joint = "head", axis = "right", amplitude = 12, cycles = 3, seconds = 2 }.Validate();
        new ArdyControlPlan { left = "forward", right = "outward", joint = "wrists", amplitude = 20,
            leftBend = 0, rightBend = 110, seconds = 6, end = "hold" }.Validate();
        Check(true, "Valid control-plan bounds rejected");
        foreach (string palm in new[] { "keep", "partner", "up", "down", "inward", "outward" })
        {
            new ArdyControlPlan { left = "forward", right = "current", leftPalm = palm, rightPalm = palm }.Validate();
            if (palm != "keep") new ArdyControlPlan { left = "current", right = "forward", leftPalm = palm,
                rightPalm = palm, joint = "wrists", axis = "palm-normal" }.Validate();
            Check(true, "Valid bilateral palm goal rejected");
        }
        new ArdyControlPlan { left = "current", leftPalm = "partner", joint = "left-wrist", axis = "palm-normal" }.Validate();
        new ArdyControlPlan { right = "forward", rightPalm = "up", joint = "right-wrist", axis = "palm-normal" }.Validate();
        new ArdyControlPlan { left = "forward", leftPalm = "partner", leftBendAuto = true }.Validate();
        new ArdyControlPlan { right = "outward", rightPalm = "up", rightBendAuto = true }.Validate();
        foreach (Action<ArdyControlPlan> mutate in new Action<ArdyControlPlan>[] {
            p => p.left = "none", p => p.left = null, p => p.left = "Forward", p => p.right = "backward",
            p => p.leftBend = -1, p => p.leftBend = 111, p => p.rightBend = float.NaN,
            p => p.leftBend = float.PositiveInfinity, p => p.rightBend = float.NegativeInfinity,
            p => p.leftPalm = null, p => p.leftPalm = "Partner", p => p.leftPalm = "forward", p => p.rightPalm = "partner",
            p => p.leftBendAuto = true, p => p.rightBendAuto = true,
            p => { p.left = "forward"; p.leftBendAuto = true; },
            p => { p.leftPalm = "partner"; p.leftBendAuto = true; },
            p => p.axis = "palm-normal",
            p => { p.joint = "head"; p.leftPalm = "partner"; p.axis = "palm-normal"; },
            p => { p.joint = "left-wrist"; p.axis = "palm-normal"; },
            p => { p.right = "forward"; p.leftPalm = "partner"; p.joint = "wrists"; p.axis = "palm-normal"; },
            p => { p.right = "forward"; p.rightPalm = "partner"; p.joint = "wrists"; p.axis = "palm-normal"; },
            p => p.joint = "fingers", p => p.axis = "left", p => p.axis = null,
            p => p.amplitude = 0, p => p.amplitude = 21, p => p.amplitude = float.NaN,
            p => { p.joint = "head"; p.amplitude = 13; },
            p => p.cycles = 0, p => p.cycles = 4, p => p.seconds = 0, p => p.seconds = 7,
            p => p.seconds = float.PositiveInfinity, p => { p.cycles = 3; p.seconds = 1; },
            p => p.end = "forever", p => p.joint = "right-wrist", p => p.joint = "wrists" })
        {
            var plan = new ArdyControlPlan { left = "current" };
            mutate(plan);
            bool rejected = false;
            try { plan.Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "An invalid or unbounded control plan was accepted");
        }
        var source = new ArdyControlPlan { left = "forward", right = "current", leftPalm = "partner", rightPalm = "up", leftBendAuto = true, joint = "wrists", end = "hold" };
        var copy = source.Copy();
        var intent = new DialogueMotionIntent("compose", "", 8, 9, source);
        source.left = "none";
        source.leftPalm = "down";
        Check(copy.left == "forward" && intent.ControlPlan.left == "forward" && !ReferenceEquals(copy, source) &&
            !ReferenceEquals(intent.ControlPlan, source) && copy.leftPalm == "partner" && intent.ControlPlan.leftPalm == "partner" &&
            copy.rightPalm == "up" && copy.leftBendAuto && intent.ControlPlan.leftBendAuto && !copy.rightBendAuto &&
            copy.ToString().Contains("palm partner"), "Control plans did not preserve an independent request snapshot");
        Check(new DialogueMotionIntent("nod", "", 8, 9).ControlPlan == null,
            "The existing four-argument intent constructor changed");
    }

    private static void CheckComposeChannel()
    {
        const string tag = "<motion name=\"compose\" left=\"forward\" right=\"forward\" joint=\"wrists\" axis=\"up\" amplitude=\"10\" cycles=\"2\" seconds=\"3.2\" end=\"hold\"/>";
        const string raw = "<lang code=\"zh\"/>好的。" + tag;
        for (int split = 0; split <= raw.Length; split++)
        {
            var channels = new RoleOutputChannels();
            channels.Push(raw.Substring(0, split)); channels.Push(raw.Substring(split)); channels.Finish();
            string executable = channels.ToExecutableText();
            Check(DialogueMotionProtocol.TryExtract(ref executable, 51, split, out var intent, out _) &&
                intent.Name == "compose" && intent.Description == "" && intent.ControlPlan.left == "forward" &&
                intent.ControlPlan.right == "forward" && intent.ControlPlan.joint == "wrists" && intent.ControlPlan.axis == "up" &&
                intent.ControlPlan.amplitude == 10 && intent.ControlPlan.cycles == 2 && intent.ControlPlan.seconds == 3.2f &&
                intent.ControlPlan.end == "hold" && intent.ResponseGeneration == 51 && intent.Sequence == split,
                "Compose controls or identity changed at split " + split);
            Check(channels.Speech == "好的。" && executable == "好的。", "Compose controls leaked into speech");
        }
        foreach (string valid in new[] {
            "<motion name=\"compose\" left=\"outward\" right=\"outward\" joint=\"head\" axis=\"right\" amplitude=\"8\" cycles=\"1\"/>",
            "<motion name=\"compose\" left=\"current\" right=\"forward\" rightBend=\"0\"/>",
            "<motion name=\"compose\" joint=\"head\" amplitude=\"12\"/>",
            "<motion name=\"compose\" left=\"forward-up\" leftBend=\"8.5\"/>" })
        {
            string executable = valid;
            Check(DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out var intent, out _) && intent.ControlPlan != null,
                "Supported open-ended compose combination rejected");
        }
        foreach (string invalid in new[] {
            "<motion name=\"compose\"/>", "<motion name=\"compose\" text=\"wave\" left=\"forward\"/>",
            "<motion name=\"compose\" left=\"forward\" duration=\"3\"/>",
            "<motion name=\"compose\" left=\"forward\" left=\"outward\"/>",
            "<motion name=\"compose\" joint=\"wrists\"/>",
            "<motion name=\"compose\" left=\"current\" joint=\"wrists\"/>",
            "<motion name=\"compose\" left=\"forward\" seconds=\"3,2\"/>",
            "<motion name=\"compose\" left=\"forward\" cycles=\"2.0\"/>",
            "<motion name=\"compose\" left=\"forward\" seconds=\"NaN\"/>",
            "<motion name=\"compose\" left=\"forward\" leftBend=\"Infinity\"/>",
            "<motion name=\"compose\" joint=\"head\" amplitude=\"13\"/>",
            "<motion name=\"compose\" left=\"forward\" cycles=\"3\" seconds=\"1\"/>",
            "<motion name=\"nod\" left=\"current\"/>",
            "<motion name=\"generate\" text=\"Extend the arms.\" joint=\"wrists\"/>",
            tag + "<motion name=\"nod\"/>" })
        {
            string executable = invalid;
            Check(!DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out _, out string reason) &&
                reason.Length > 0 && executable.Length == 0, "Malformed compose or cross-route controls were accepted");
        }
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            string executable = tag;
            Check(DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out var intent, out _) && intent.ControlPlan.seconds == 3.2f,
                "Compose numeric parsing depends on the machine locale");
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    private static void CheckPalmChannel()
    {
        const string tag = "<motion name=\"compose\" left=\"forward\" right=\"forward\" leftBend=\"40\" rightBend=\"40\" leftPalm=\"partner\" rightPalm=\"partner\" joint=\"wrists\" axis=\"palm-normal\" end=\"hold\"/>";
        const string raw = "<lang code=\"zh\"/>你好。" + tag;
        for (int split = 0; split <= raw.Length; split++)
        {
            var channels = new RoleOutputChannels();
            channels.Push(raw.Substring(0, split)); channels.Push(raw.Substring(split)); channels.Finish();
            string executable = channels.ToExecutableText();
            Check(DialogueMotionProtocol.TryExtract(ref executable, 71, split, out var intent, out _) &&
                intent.ControlPlan.leftPalm == "partner" && intent.ControlPlan.rightPalm == "partner" &&
                intent.ControlPlan.leftBend == 40 && intent.ControlPlan.rightBend == 40 && intent.ControlPlan.axis == "palm-normal" &&
                !intent.ControlPlan.leftBendAuto && !intent.ControlPlan.rightBendAuto,
                "Palm or wrist-axis controls lost across a stream boundary");
            Check(channels.Speech == "你好。" && executable == "你好。", "Palm goal entered speech");
        }
        foreach (string palm in new[] { "keep", "partner", "up", "down", "inward", "outward" })
        {
            string executable = "<motion name=\"compose\" left=\"current\" leftPalm=\"" + palm + "\"/>";
            Check(DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out var intent, out _) &&
                intent.ControlPlan.leftPalm == palm && intent.ControlPlan.rightPalm == "keep",
                "Static palm display or default compatibility failed");
        }
        string auto = "<motion name=\"compose\" left=\"forward\" right=\"outward\" leftPalm=\"partner\" rightPalm=\"up\"/>";
        Check(DialogueMotionProtocol.TryExtract(ref auto, 1, 1, out var automatic, out _) &&
            automatic.ControlPlan.leftBendAuto && automatic.ControlPlan.rightBendAuto,
            "An unspecified elbow with a directional palm goal did not leave solving freedom");
        foreach (string fixedElbow in new[] {
            "<motion name=\"compose\" left=\"forward\" leftPalm=\"partner\" leftBend=\"0\"/>",
            "<motion name=\"compose\" left=\"forward\" leftPalm=\"partner\" leftBend=\"8\"/>",
            "<motion name=\"compose\" left=\"current\" leftPalm=\"partner\"/>",
            "<motion name=\"compose\" left=\"forward\"/>" })
        {
            string executable = fixedElbow;
            Check(DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out var fixedPlan, out _) &&
                !fixedPlan.ControlPlan.leftBendAuto && !fixedPlan.ControlPlan.rightBendAuto,
                "Explicit, current or legacy elbow semantics silently became automatic");
        }
        foreach (string invalid in new[] {
            "<motion name=\"compose\" left=\"current\" leftBend=\"0\"/>",
            "<motion name=\"compose\" right=\"current\" rightBend=\"8\"/>",
            "<motion name=\"compose\" left=\"current\" leftBend=\"40\" leftPalm=\"partner\"/>",
            "<motion name=\"compose\" left=\"forward\" leftPalm=\"partner\" leftBendAuto=\"true\"/>",
            "<motion name=\"compose\" right=\"forward\" rightPalm=\"up\" rightBendAuto=\"true\"/>",
            "<motion name=\"compose\" leftPalm=\"partner\"/>",
            "<motion name=\"compose\" left=\"forward\" rightPalm=\"up\"/>",
            "<motion name=\"compose\" left=\"current\" leftPalm=\"forward\"/>",
            "<motion name=\"compose\" left=\"current\" leftPalm=\"Partner\"/>",
            "<motion name=\"compose\" left=\"current\" leftPalm=\"partner\" leftpalm=\"down\"/>",
            "<motion name=\"compose\" left=\"current\" leftPalm=\"partner\" axis=\"palm-normal\"/>",
            "<motion name=\"compose\" left=\"current\" joint=\"left-wrist\" axis=\"palm-normal\"/>",
            "<motion name=\"compose\" left=\"current\" right=\"current\" leftPalm=\"partner\" joint=\"wrists\" axis=\"palm-normal\"/>",
            "<motion name=\"compose\" joint=\"head\" axis=\"palm-normal\"/>",
            "<motion name=\"left-wave\" leftPalm=\"partner\"/>",
            "<motion name=\"generate\" text=\"Show the hand.\" leftPalm=\"partner\"/>" })
        {
            string executable = invalid;
            Check(!DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out _, out var rejection) &&
                !string.IsNullOrEmpty(rejection) && executable == "", "Invalid palm route or cross-field combination accepted");
        }
        foreach (string quoted in new[] { "<thought>" + tag + "</thought>", "<say>" + tag + "</say>", "`" + tag + "`" })
        {
            var channels = RoleOutputChannels.Parse(quoted);
            string executable = channels.ToExecutableText();
            Check(!DialogueMotionProtocol.TryExtract(ref executable, 1, 1, out _, out _), "Quoted palm plan became executable");
        }
    }

    private static void CheckComposeChatHooks()
    {
        var host = new GameObject("ArdyComposeChatHooksRegression");
        host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            chat.ConfigureSemanticMotionPlanning(true);
            Set(chat, "m_FormalResponseGeneration", 61);
            Set(chat, "m_LogAgentLoop", false);
            Set(chat, "m_LogStreamTimings", false);
            var actions = new List<DialogueMotionIntent>();
            chat.MotionIntentRequested += intent => actions.Add(intent);
            chat.ConfigureMotionOutput(true, true);
            var plan = new ArdyControlPlan { left = "current", right = "current", joint = "wrists" };
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("compose", "", 61, 1, plan), false);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("compose", "", 61, 2, plan.Copy()), false);
            Check(actions.Count == 1, "An identical compose plan restarted within one response generation");
            plan.amplitude = 12;
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("compose", "", 61, 3, plan), false);
            Check(actions.Count == 2 && actions[0].ControlPlan.amplitude == 10 && actions[1].ControlPlan.amplitude == 12,
                "Distinct compose numeric parameters were suppressed or mutated a previous plan");
            plan.leftPalm = "partner";
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("compose", "", 61, 4, plan), false);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("compose", "", 61, 5, plan.Copy()), false);
            Check(actions.Count == 3 && actions[1].ControlPlan.leftPalm == "keep" && actions[2].ControlPlan.leftPalm == "partner",
                "A new palm goal was deduplicated against the previous pose or replayed twice");
            chat.ConfigureMotionOutput(true, false);
            plan.amplitude = 11;
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("compose", "", 61, 4, plan), false);
            Check(actions.Count == 3, "A basic-only bridge dispatched compose controls");
            Check(!((string)Call(chat, "BuildMotionRequestContext", "")).Contains("name=\"compose\""),
                "A basic-only bridge advertised compose controls");
            chat.ConfigureMotionOutput(true, true);
            const string heldFact = "{\"phase\":\"holding\",\"previousRequestedPoseHeld\":true}";
            chat.MotionStateContextRequested += () => heldFact;
            string context = (string)Call(chat, "BuildMotionRequestContext", "");
            Check(context.Contains(heldFact) && context.Contains("previousRequestedPoseHeld=true") && context.Contains("最多30秒"),
                "Compose context lost actual holding facts or bounded hold semantics");

            const string raw = "<lang code=\"zh\"/>保持好了。<motion name=\"compose\" left=\"current\" right=\"forward\" rightBend=\"0\" rightPalm=\"partner\" joint=\"right-wrist\" axis=\"palm-normal\" amplitude=\"12.5\" seconds=\"3.2\"/>";
            Set(chat, "m_HoldSpeechForSongMemoryResult", true);
            var channels = new RoleOutputChannels(part => Call(chat, "OnSpeechStreamDelta", part));
            foreach (char c in raw) channels.Push(c.ToString());
            channels.Finish();
            Set(chat, "m_HoldSpeechForSongMemoryResult", false);
            Call(chat, "FlushCompleteSentences", true);
            var queued = new StringBuilder();
            foreach (object chunk in (IEnumerable)Get(chat, "m_PendingChunks")) queued.Append((string)Get(chunk, "Text"));
            Check(queued.ToString() == "保持好了。", "Compose parameters entered the real ChatSample speech queue");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckMotionProtocolFeedback()
    {
        var host = new GameObject("ArdyMotionProtocolFeedbackRegression");
        host.SetActive(false);
        const string marker = "[最近动作协议拒绝；历史解析事实，不是当前身体状态]";
        const string invalidPalm = "<motion name=\"compose\" left=\"forward\" leftPalm=\"invalid-palm\"/>";
        const string invalidAxis = "<motion name=\"compose\" left=\"forward\" joint=\"left-wrist\" axis=\"palm-normal\"/>";
        try
        {
            var chat = host.AddComponent<ChatSample>();
            chat.ConfigureSemanticMotionPlanning(true);
            Set(chat, "m_FormalResponseGeneration", 81);
            Set(chat, "m_LogAgentLoop", false);
            int actions = 0;
            chat.MotionIntentRequested += intent => actions++;
            chat.ConfigureMotionOutput(true, true);
            object rejected = Call(chat, "ExtractDialogueMotion", invalidPalm);
            Call(chat, "DispatchDialogueMotion", rejected, false);
            object fact = Get(chat, "m_LastMotionProtocolRejection");
            Check(rejected == null && actions == 0 && fact != null &&
                (string)Get(fact, "source") == "dialogue-motion-parser" &&
                (string)Get(fact, "status") == "rejected-before-dispatch" &&
                (int)Get(fact, "responseGeneration") == 81 && (int)Get(fact, "sequence") > 0 &&
                !string.IsNullOrEmpty((string)Get(fact, "reason")),
                "An invalid palm command dispatched or failed to record a bounded parser rejection");
            Call(chat, "ExtractDialogueMotion", "Ordinary speech without a motion command.");
            Call(chat, "BeginFormalResponseGeneration");
            string context = (string)Call(chat, "BuildMotionRequestContext", "Synthetic next request.");
            Check(ReferenceEquals(Get(chat, "m_LastMotionProtocolRejection"), fact) && context.Contains(marker) &&
                context.Contains("\"currentResponseGeneration\":82") && context.Contains("\"responseGeneration\":81"),
                "The next request lost the original rejection generation or treated it as current body state");

            object valid = Call(chat, "ExtractDialogueMotion", "<motion name=\"nod\"/>");
            Check(valid != null && Get(chat, "m_LastMotionProtocolRejection") == null &&
                !((string)Call(chat, "BuildMotionRequestContext", "")).Contains(marker),
                "A later valid role command did not clear the historical parser error");
            Set(chat, "m_FormalResponseGeneration", 83);
            Call(chat, "DispatchDialogueMotion", valid, false);
            Check(actions == 0, "Protocol feedback changes bypassed stale-generation rejection");
            valid = Call(chat, "ExtractDialogueMotion", "<motion name=\"nod\"/>");
            Call(chat, "DispatchDialogueMotion", valid, false);
            Check(actions == 1, "A valid current-generation command no longer dispatches after a rejection");

            rejected = Call(chat, "ExtractDialogueMotion", invalidAxis);
            Call(chat, "DispatchDialogueMotion", rejected, false);
            fact = Get(chat, "m_LastMotionProtocolRejection");
            Check(rejected == null && actions == 1 && fact != null &&
                ((string)Get(fact, "reason")).Contains("palm-normal"),
                "An invalid palm-axis dependency dispatched or lost its explanatory feedback");
            Set(chat, "m_ReadingUserInput", true);
            object userTag = Call(chat, "ExtractDialogueMotion", "<motion name=\"nod\"/>");
            Call(chat, "DispatchDialogueMotion", userTag, false);
            Call(chat, "ExtractDialogueMotion", invalidPalm);
            Set(chat, "m_ReadingUserInput", false);
            Check(actions == 1 && ReferenceEquals(Get(chat, "m_LastMotionProtocolRejection"), fact),
                "Reading user text created/cleared role parser feedback or executed the user's tag");
            var privateChannels = RoleOutputChannels.Parse("<thought>" + invalidPalm + "</thought>");
            Call(chat, "ExtractDialogueMotion", privateChannels.ToExecutableText());
            Check(ReferenceEquals(Get(chat, "m_LastMotionProtocolRejection"), fact),
                "Private non-executable syntax became new parser feedback");

            chat.ConfigureMotionOutput(true, false);
            context = (string)Call(chat, "BuildMotionRequestContext", "");
            Check(Get(chat, "m_LastMotionProtocolRejection") == null && !context.Contains(marker) &&
                context.Contains(DialogueMotionProtocol.BasicOutputContract) &&
                !context.Contains(DialogueMotionProtocol.SemanticOutputContract),
                "Switching to basic-only leaked a previous generated-control rejection");
            chat.ConfigureMotionOutput(true, true);
            Check(Get(chat, "m_LastMotionProtocolRejection") == null, "Re-enabling generation resurrected old parser feedback");
            Call(chat, "ExtractDialogueMotion", invalidPalm);
            chat.ConfigureMotionOutput(false, false);
            Call(chat, "ExtractDialogueMotion", invalidAxis);
            Check(Get(chat, "m_LastMotionProtocolRejection") == null &&
                !((string)Call(chat, "BuildMotionRequestContext", "unchanged")).Contains(marker),
                "A disabled motion channel retained or created protocol feedback");
            chat.ConfigureMotionOutput(true, true);
            Check(Get(chat, "m_LastMotionProtocolRejection") == null, "A later binding inherited the previous parser error");
            Call(chat, "ExtractDialogueMotion", invalidPalm);
            Call(chat, "OnDisable");
            Check(Get(chat, "m_LastMotionProtocolRejection") == null,
                "Disabling ChatSample left protocol feedback available to a later session");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckPureCollectedMotion()
    {
        var host = new GameObject("ArdyPureMotionCollectedRegression");
        host.SetActive(false);
        try
        {
            var textObject = new GameObject("InactiveSubtitle", typeof(RectTransform), typeof(UnityEngine.UI.Text));
            textObject.transform.SetParent(host.transform, false);
            var chat = host.AddComponent<ChatSample>();
            chat.ConfigureSemanticMotionPlanning(true);
            Set(chat, "m_TextBack", textObject.GetComponent<UnityEngine.UI.Text>());
            Set(chat, "m_ChatSettings", new ChatSetting());
            Set(chat, "m_PersistSubtitleSettings", false);
            Set(chat, "m_EnableSubtitleTranslation", false);
            Set(chat, "m_SystemNoticeMode", SystemNoticeMode.Hidden);
            int actions = 0;
            int cancellations = 0;
            chat.MotionIntentRequested += intent => actions++;
            chat.MotionCancelled += (generation, reason) => cancellations++;
            chat.ConfigureMotionOutput(true, true);
            foreach (string tag in new[] { "<motion name=\"nod\"/>",
                "<motion name=\"generate\" text=\"A person extends the forearms forward.\"/>",
                "<motion name=\"compose\" left=\"current\" right=\"forward\"/>" })
            {
                Call(chat, "CallBackWithSpeech", new List<SpeechText>(), tag);
                var overlay = (SubtitleOverlay)Get(chat, "m_SubtitleOverlay");
                Check(overlay.NoticeHistory.Count == 0, "Pure action incorrectly reported an empty LLM response");
            }
            Check(actions == 3, "Pure collected basic/generated/composed action was not dispatched");
            Check(!((IEnumerable)Get(chat, "m_PendingChunks")).GetEnumerator().MoveNext(), "Pure motion queued TTS");
            Call(chat, "CallBackWithSpeech", new List<SpeechText>(), "");
            var notices = ((SubtitleOverlay)Get(chat, "m_SubtitleOverlay")).NoticeHistory;
            Check(notices.Count == 1 && notices[0].Code == "llm_response_failed" && actions == 3,
                "Genuinely empty response no longer reports a failure");
            Check(cancellations == 0,
                "A collected reply without a new body action cancelled the committed motion");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckMotionLifetimeAcrossResponses()
    {
        var host = new GameObject("ArdyMotionResponseLifetimeRegression");
        host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            chat.ConfigureSemanticMotionPlanning(true);
            Set(chat, "m_FormalResponseGeneration", 20);
            Set(chat, "m_LogAgentLoop", false);
            Set(chat, "m_LogStreamTimings", false);
            int actions = 0, cancellations = 0;
            bool active = false;
            chat.MotionIntentRequested += intent => { actions++; active = intent.Name != "none"; };
            chat.MotionCancelled += (generation, reason) => { cancellations++; active = false; };
            chat.MotionStateContextRequested += () => active ? "name=generate; description=Raise both hands above the head; state=playing" : "";
            chat.ConfigureMotionOutput(true, true);

            const string description = "Raise both hands above the head";
            var first = new DialogueMotionIntent("generate", description, 20, 1);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)first, false);
            Check(active && actions == 1 && cancellations == 0, "Initial body action was not committed");
            string context = (string)Call(chat, "BuildMotionRequestContext", "existing context");
            Check(context.StartsWith("existing context") && context.Contains(description) && context.Contains("state=playing"),
                "Actual in-progress body facts were missing from the next model request");

            // A continue chain dispatches another completed response using the
            // SAME formal generation; its exact repeated tag must not restart.
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("generate", description, 20, 2), false);
            Check(actions == 1 && active, "Identical motion in a continue chain restarted the body action");
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("generate", description + ", palms forward", 20, 3), false);
            Check(actions == 2 && active, "A new explicit description was incorrectly treated as synonymous repetition");

            // StartStreaming and collected callbacks share this real response
            // boundary. A new autonomous text frame invalidates old LLM callbacks,
            // but it is not a physical action cancellation event.
            int next = (int)Call(chat, "BeginFormalResponseGeneration");
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)null, false);
            Check(next == 21 && active && cancellations == 0,
                "An autonomous response without motion cancelled the body action");
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)first, false);
            Check(actions == 2, "An old response callback bypassed the generation fence");
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("none", "", 21, 4), false);
            Check(actions == 3 && !active, "Explicit none failed to stop motion");
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("generate", description, 21, 5), false);
            Check(actions == 4 && active, "An explicit action after stop was blocked by old idempotency state");
            Call(chat, "InvalidateFormalResponse", "user-started-speaking");
            Check(cancellations == 1 && !active, "Real user speech no longer cancels motion");
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("generate", description, 22, 6), false);
            Check(actions == 5 && active, "A new user response's same requested action was suppressed");
            Call(chat, "CancelDialogueMotion", "new-user-turn");
            Check(cancellations == 2 && !active, "New real user text no longer cancels motion");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckChatHooks()
    {
        var host = new GameObject("ArdyDialogueMotionRegression");
        host.SetActive(false);
        try
        {
            var chat = host.AddComponent<ChatSample>();
            chat.ConfigureSemanticMotionPlanning(true);
            Set(chat, "m_FormalResponseGeneration", 7);
            Set(chat, "m_LogStreamTimings", false);
            Check((string)Call(chat, "BuildMotionRequestContext", "existing") == "existing",
                "Motion contract changed a chat with no bridge");
            int dispatched = 0;
            var cancellations = new List<string>();
            chat.MotionIntentRequested += intent => dispatched++;
            chat.MotionCancelled += (generation, reason) => cancellations.Add(reason);
            chat.ConfigureMotionOutput(true, false);
            string basic = (string)Call(chat, "BuildMotionRequestContext", "existing");
            Check(basic.StartsWith("existing") && basic.Contains("left-wave") && !basic.Contains("name=\"generate\""),
                "Basic-only bridge advertised unavailable generation");
            chat.ConfigureMotionOutput(true, true);
            Check(((string)Call(chat, "BuildMotionRequestContext", "")).Contains(DialogueMotionProtocol.SemanticOutputContract),
                "Generated action contract missing when enabled");

            var active = new DialogueMotionIntent("nod", "", 7, 1);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)active, false);
            Check(dispatched == 1, "Current formal role motion was not dispatched");
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("nod", "", 6, 1), false);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)active, true);
            foreach (string gate in new[] { "m_ReadingUserInput", "m_RoundSilencedForRepeat", "m_UserSpeechActiveForAutonomy" })
            {
                Set(chat, gate, true);
                Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)active, false);
                Set(chat, gate, false);
            }
            Check(dispatched == 1, "Stale/repeated/user-read or listening motion was dispatched");
            chat.ConfigureMotionOutput(true, false);
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)new DialogueMotionIntent("generate", "Point.", 7, 2), false);
            Check(dispatched == 1, "Generated motion bypassed bridge capability");

            // Pure action has no active TTS, pending speech or network request. Interrupt must still notify.
            chat.Interrupt();
            Check(cancellations.Contains("barge-in"), "Pure-motion interrupt returned before cancellation");
            Call(chat, "InvalidateFormalResponse", "regression-new-turn");
            Check(cancellations.Contains("regression-new-turn") && (int)Get(chat, "m_FormalResponseGeneration") == 8,
                "Response invalidation did not cancel motion and advance generation");
            Call(chat, "DispatchDialogueMotion", (DialogueMotionIntent?)active, false);
            Check(dispatched == 1, "Cancelled response reactivated a gesture");
            chat.ConfigureMotionOutput(false, false);
            Check(cancellations.Contains("motion-output-disabled"), "Disconnect did not cancel current motion");

            // Exercise the real ChatSample sentence queue, not only the standalone parser.
            const string raw = "<lang code=\"zh\"/>你好。<motion name=\"left-wave\"/>";
            Set(chat, "m_HoldSpeechForSongMemoryResult", true);
            var channels = new RoleOutputChannels(part => Call(chat, "OnSpeechStreamDelta", part));
            foreach (char c in raw) channels.Push(c.ToString());
            channels.Finish();
            Set(chat, "m_HoldSpeechForSongMemoryResult", false);
            Call(chat, "FlushCompleteSentences", true);
            var queued = new StringBuilder();
            foreach (object chunk in (IEnumerable)Get(chat, "m_PendingChunks")) queued.Append((string)Get(chunk, "Text"));
            Check(queued.ToString() == "你好。", "Motion metadata entered the real TTS queue");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static object Get(object target, string name) => target.GetType().GetField(name, Instance).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Instance).SetValue(target, value);
    private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Instance).Invoke(target, args);
}
