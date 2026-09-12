using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif

/// <summary>Public synthetic output only; no scene, model, microphone or audio backend.</summary>
public static class AgentSpeechPhaseRegression
{
    private static int checks;

    public static void RunBatch()
    {
        string error = null;
        try { RunOrThrow(); }
        catch (Exception exception) { error = exception.ToString(); }
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/agent-speech-phase-regression.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string escaped = (error ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\r", "\\r").Replace("\n", "\\n");
        File.WriteAllText(path, "{\"passed\":" + (error == null ? "true" : "false") +
            ",\"checks\":" + checks + ",\"error\":\"" + escaped + "\"}\n");
#if UNITY_EDITOR
        if (error != null) Debug.LogError(error);
        else Debug.Log("[AgentSpeechPhaseRegression] passed " + checks + " checks");
        EditorApplication.Exit(error == null ? 0 : 1);
#else
        if (error != null) throw new Exception(error);
        Console.WriteLine("AgentSpeechPhaseRegression passed " + checks + " checks");
#endif
    }

    public static void RunOrThrow()
    {
        checks = 0;
        const string independent = "<speech mode=\"independent\"/><lang code=\"ja\"/>";
        const string dependent = "<speech mode=\"after_action\"/><lang code=\"ja\"/>";
        VerifyCompatibleDeclarations(independent, dependent);
        VerifyFormatFactHistory();
        foreach (string malformed in new[] { "[body_inspect scope=\"runtime\"]", "[motion name=\"approach\"]", "[body_inspect scope=\"run]time\"]" })
        {
            string raw = independent + "確認。" + malformed + "終わり。<next in=\"15s\"/>";
            for (int split = 0; split <= raw.Length; split++)
            {
                var broken = new RoleOutputChannels();
                broken.Push(raw.Substring(0, split)); broken.Push(raw.Substring(split)); broken.Finish();
                Check(broken.HasMalformedTool && broken.Speech == "確認。終わり。",
                    "Square-bracket pseudo-tool leaked to speech at packet boundary " + split);
                Check(!broken.ToExecutableText().Contains("<next") && !broken.ToExecutableText().Contains("<motion"),
                    "Malformed pseudo-tool was repaired into execution or retained sibling tools.");
            }
        }
        foreach (string ordinary in new[] { "[大丈夫]", "[body_inspect]", "[temperature=20]", "<say>[body_inspect scope=\"runtime\"]</say>", "`[body_inspect scope=\"runtime\"]`" })
        {
            var plain = RoleOutputChannels.Parse(independent + ordinary);
            Check(!plain.HasMalformedTool, "Ordinary bracket text or quoted example was treated as an action.");
        }
        var immediate = new List<SpeechText>();
        var parser = new RoleOutputChannels(immediate.Add);
        parser.Push(independent + "今日は晴れているね。");
        Check(immediate.Count > 0 && immediate[0].Text.Contains("晴れて"), "Independent reply waited for Finish.");
        Check(immediate[0].ActionDependency == SpeechActionDependency.Independent && immediate[0].LanguageCode == "ja",
            "Independent speech lost its explicit stage or language.");
        parser.Push("散歩したくなるね。<next in=\"15s\"/>"); parser.Finish();
        Check(!parser.HasInvalidSpeechPhase && parser.ToExecutableText().Contains("<next"), "Ordinary scheduling was disabled.");

        foreach (string raw in new[] {
            independent + "歌の話も天気の話もできるね。<next in=\"15s\"/>",
            dependent + "確認してから歌うね。<sing_goal refs=\"clip:public-1\"/><sing refs=\"clip:public-1\"/>",
            "<thought><speech mode=\"after_action\"/><sing refs=\"private-example\"/></thought>" + independent + "こんにちは。",
            independent + "<say>例は `<sing refs=\"quoted\"/>` よ。</say>",
            independent + "近くに行くね。<motion name=\"approach\"/>",
            independent + "ここで止まるね。<motion name=\"stop-moving\"/>",
            dependent + "確認してから歌うね。<sing refs=\"clip:public-1\"/><motion name=\"nod\"/>",
            independent + "了解。<lang code=\"en\"/>Understood."
        })
        {
            var referenceParts = new List<SpeechText>();
            var reference = new RoleOutputChannels(referenceParts.Add);
            reference.Push(raw); reference.Finish();
            string expected = Signature(referenceParts);
            for (int split = 0; split <= raw.Length; split++)
            {
                var parts = new List<SpeechText>(); var splitParser = new RoleOutputChannels(parts.Add);
                splitParser.Push(raw.Substring(0, split)); splitParser.Push(raw.Substring(split)); splitParser.Finish();
                Check(Signature(parts) == expected, "Stage changed at packet boundary " + split);
                Check(splitParser.ToExecutableText(true, true) == reference.ToExecutableText(true, true),
                    "Executable projection changed at packet boundary " + split);
                Check(!splitParser.HasInvalidSpeechPhase, "Valid phase rejected at packet boundary " + split);
            }
            var characterParts = new List<SpeechText>(); var characterParser = new RoleOutputChannels(characterParts.Add);
            foreach (char c in raw) characterParser.Push(c.ToString()); characterParser.Finish();
            Check(Signature(characterParts) == expected, "Character deltas changed the stage.");
        }

        foreach (string action in new[] { "sing_goal", "sing", "hum_back", "song_sing", "clip_confirm", "clip_revise",
            "clip_drop", "practice_confirm", "practice_revise", "practice_drop", "song_remember", "song_rename", "song_forget" })
        {
            string raw = independent + "今日は晴れね。<" + action + " refs=\"public\" reason=\"a > b\"/>後続の約束。<next in=\"15s\"/>";
            for (int split = 0; split <= raw.Length; split++)
            {
                var splitParser = new RoleOutputChannels();
                splitParser.Push(raw.Substring(0, split)); splitParser.Push(raw.Substring(split)); splitParser.Finish();
                string executable = splitParser.ToExecutableText(true, true);
                Check(splitParser.HasInvalidSpeechPhase && !string.IsNullOrEmpty(splitParser.SpeechPhaseError), "Late dependent action lacked failure fact.");
                Check(!executable.Contains("<" + action) && !executable.Contains("a > b"), "Rejected action escaped executable projection.");
                Check(!splitParser.Speech.Contains("後続") && executable.Contains("<next"), "Phase error leaked later prose or erased unrelated scheduling.");
                Check(!RoleOutputChannels.Parse(executable).ToExecutableText().Contains("<" + action), "Reparsing resurrected rejected action.");
            }
        }

        foreach (string raw in new[] {
            independent + "こんにちは。<speech mode=\"after_action\"/><sing refs=\"public\"/>",
            "<sing refs=\"public\"/>" + independent + "歌うね。",
            "<speech mode=\"unknown\"/>歌うね。<sing refs=\"public\"/>",
            dependent + "歌うね。",
            "<speech mode=\"independent"
        })
        {
            var invalid = RoleOutputChannels.Parse(raw);
            Check(invalid.HasInvalidSpeechPhase && !invalid.ToExecutableText().Contains("<sing"), "Invalid phase retained dependent action.");
        }
        Check(RoleOutputChannels.Parse(dependent + "歌うね。").ToExecutableText() == "<silent/>",
            "A declared action with no action released a promise.");
        // Reproduce room speech-stage failures without pretending the independently
        // accepted motion was rejected or releasing a withheld promise as speech.
        foreach (string motion in new[] { "approach", "stop-moving", "nod" })
        {
            string raw = dependent + "そばに行くね。<motion name=\"" + motion + "\"/><next in=\"15s\"/>";
            for (int split = 0; split <= raw.Length; split++)
            {
                var invalid = new RoleOutputChannels();
                invalid.Push(raw.Substring(0, split)); invalid.Push(raw.Substring(split)); invalid.Finish();
                string executable = invalid.ToExecutableText(true, true);
                Check(invalid.HasInvalidSpeechPhase && !invalid.HasSingingValidationActions &&
                    executable.Contains("rejected=\"true\"") && !executable.Contains("そばに"),
                    "Room-only after_action released unvalidated speech.");
                Check(executable.Contains("<motion name=\"" + motion + "\"/>") && executable.Contains("<next"),
                    "Speech-stage failure revoked independently validated motion eligibility.");
                Check(invalid.SpeechPhaseCorrection.Contains("不能使用 after_action") &&
                    invalid.SpeechPhaseCorrection.Contains("使用 independent") &&
                    invalid.SpeechPhaseExecutionFact.Contains("是否受理或完成以各自程序事实为准"),
                    "Room-only correction repeated the wrong speech stage or fabricated a motion result.");
            }
        }
        var mixed = RoleOutputChannels.Parse(independent + "こんにちは。<sing refs=\"public\"/><motion name=\"approach\"/>");
        Check(mixed.HasInvalidSpeechPhase && !mixed.ToExecutableText().Contains("<sing") &&
            mixed.ToExecutableText().Contains("<motion name=\"approach\"/>"),
            "Scoped speech correction bypassed singing validation or rejected sibling motion.");
        Check(RoleOutputChannels.Parse("<lang code=\"ja\"/>こんにちは。").SpeechDependency == SpeechActionDependency.Unspecified,
            "Stage leaked into an unrelated legacy request.");
        var buffered = new SpeechTextBuffer();
        buffered.Append(new SpeechText("明日。", "ja", actionDependency: SpeechActionDependency.Independent));
        buffered.Append(new SpeechText("後で。", "ja", actionDependency: SpeechActionDependency.AfterAction));
        buffered.Remove(0, 1);
        Check(buffered.Snapshot().Count == 2 && buffered.Snapshot()[0].ActionDependency == SpeechActionDependency.Independent &&
            buffered.Snapshot()[1].ActionDependency == SpeechActionDependency.AfterAction, "A held buffer lost action-stage boundaries.");
    }

    private static void VerifyCompatibleDeclarations(string independent, string dependent)
    {
        const string prose = "そばに行くね。";
        const string motion = "<motion name=\"approach\"/>";
        const string song = "<sing refs=\"clip:public\"/>";
        string[][] equivalentPairs = {
            new[] { "<speech mode=\"independent\"><lang code=\"ja\"/>" + prose + motion, independent + prose + motion },
            new[] { "<speech mode='independent'><lang code=\"ja\"/>" + prose + "</speech>" + motion, independent + prose + motion },
            new[] { "<speech mode=\"independent\"/>" + independent + prose + motion, independent + prose + motion },
            new[] { "<speech mode=\"independent\"><speech mode=\"independent\"><lang code=\"ja\"/>" + prose + "</speech></speech>" + motion, independent + prose + motion },
            new[] { "<speech mode=\"after_action\"/>" + dependent + prose + song, dependent + prose + song },
            new[] { "<thought><speech mode=\"after_action\"><sing refs=\"private\"/></thought>" + independent + prose, independent + prose },
            new[] { "`<speech mode=\"after_action\">`" + independent + prose, independent + prose },
            new[] { "<say><speech mode=\"after_action\"></say>" + independent + prose, independent + prose },
            new[] { independent + prose + "<silent/><speech mode=\"after_action\">隠れた言葉。", independent + prose },
            new[] { independent + prose + "<thought><speech mode=\"after_action", independent + prose }
        };
        foreach (string[] pair in equivalentPairs)
        {
            var expectedParts = new List<SpeechText>();
            var expectedParser = new RoleOutputChannels(expectedParts.Add);
            expectedParser.Push(pair[1]); expectedParser.Finish();
            for (int split = 0; split <= pair[0].Length; split++)
            {
                var parts = new List<SpeechText>(); var parser = new RoleOutputChannels(parts.Add);
                parser.Push(pair[0].Substring(0, split)); parser.Push(pair[0].Substring(split)); parser.Finish();
                Check(!parser.HasInvalidSpeechPhase, "Compatible declaration rejected at packet boundary " + split);
                Check(Signature(parts) == Signature(expectedParts), "Compatibility changed public speech, language or validation dependency.");
                Check(parser.ToExecutableText(true, true) == expectedParser.ToExecutableText(true, true),
                    "Compatibility changed the executable projection or made a private example executable.");
            }
            var characters = new List<SpeechText>(); var characterParser = new RoleOutputChannels(characters.Add);
            foreach (char c in pair[0]) characterParser.Push(c.ToString()); characterParser.Finish();
            Check(!characterParser.HasInvalidSpeechPhase && Signature(characters) == Signature(expectedParts),
                "Character streaming broke compatible declarations.");
        }

        foreach (string raw in new[] {
            "<speech mode=\"after_action\"><lang code=\"ja\"/>約束。" + song + motion,
            "<speech mode=\"independent\" rejected=\"true\"/><lang code=\"ja\"/>約束。" + song + motion,
            "<speech mode=\"independent\"/><speech mode=\"after_action\"/><lang code=\"ja\"/>約束。" + song + motion,
            "<speech mode=\"after_action\"/><speech mode=\"independent\"/><lang code=\"ja\"/>約束。" + song + motion,
            "<speech mode=\"independent\"><lang code=\"ja\"/>先の雑談。" + song + "後続の約束。" + motion,
            "<speech mode=\"independent\"><lang code=\"ja\"/>先の雑談。<speech mode=\"independent\"/>後続の約束。" + song + motion,
            independent + "先の雑談。</speech>後続の約束。" + song + motion
        })
        {
            for (int split = 0; split <= raw.Length; split++)
            {
                var parts = new List<SpeechText>(); var parser = new RoleOutputChannels(parts.Add);
                parser.Push(raw.Substring(0, split)); parser.Push(raw.Substring(split)); parser.Finish();
                string executable = parser.ToExecutableText(true, true);
                Check(parser.HasInvalidSpeechPhase && !executable.Contains("<sing"),
                    "Compatibility released an invalid dependent action.");
                Check(executable.Contains(motion) && !parser.Speech.Contains("後続"),
                    "True phase conflict leaked subsequent speech or removed independent motion.");
                foreach (SpeechText part in parts)
                    Check(!part.Text.Contains("約束") || part.ActionDependency != SpeechActionDependency.Independent,
                        "Compatibility reclassified an action-dependent promise as independent speech.");
            }
        }
        var noAction = RoleOutputChannels.Parse("<speech mode=\"after_action\"/>" + dependent + "まだ検証されていない約束。" + motion);
        Check(noAction.HasInvalidSpeechPhase && !noAction.ToExecutableText().Contains("約束"),
            "Duplicate after_action metadata bypassed the missing-action gate.");
        var stream = new List<SpeechText>(); var immediate = new RoleOutputChannels(stream.Add);
        immediate.Push("<speech mode=\"independent\"><lang code=\"ja\"/>こんにちは。");
        Check(stream.Count > 0 && stream[0].ActionDependency == SpeechActionDependency.Independent,
            "Compatible independent wrapper waited until completion instead of streaming.");
    }

    private sealed class FactMessage
    {
        public string role, content;
        public FactMessage(string role, string content) { this.role = role; this.content = content; }
    }

    private static void VerifyFormatFactHistory()
    {
        var roomFact = new FactMessage("system", "[程序执行事实] 房间 action=public:1 已到达。");
        var toolFact = new FactMessage("system", "[程序执行事实] 素材校验失败，clip=public-1。");
        var userQuote = new FactMessage("user", RoleOutputFormatFactHistory.Prefix + "引用旧记录");
        var assistantQuote = new FactMessage("assistant", "[程序执行事实] 本条回复的发声阶段校验失败：引用");
        var instruction = new FactMessage("system", "请参考：" + RoleOutputFormatFactHistory.Prefix + "它只是一个示例。");
        var messages = new List<FactMessage> { roomFact, toolFact, userQuote, assistantQuote, instruction,
            new FactMessage("system", "[程序执行事实] 本条回复的发声阶段校验失败：旧错误一"),
            new FactMessage("system", "[程序执行事实] 本条回复的发声阶段校验失败：旧错误二"),
            new FactMessage("system", "[程序执行事实] 检测到方括号或全角括号等错误工具属性语法；旧错误三"),
            new FactMessage("system", "[程序执行事实] 上一条回复因生成长度上限被截断，旧错误四") };
        for (int i = 0; i < 30; i++)
        {
            string current = "current-format-failure-" + i;
            RoleOutputFormatFactHistory.ReplaceCurrent(messages, m => m.role, m => m.content,
                text => new FactMessage("system", text), current);
            Check(messages.Count == 6 && messages[5].content.StartsWith(RoleOutputFormatFactHistory.Prefix, StringComparison.Ordinal) &&
                messages[5].content.EndsWith(current, StringComparison.Ordinal),
                "Repeated correction accumulated permanent format facts or retained a stale current failure.");
            Check(ReferenceEquals(messages[0], roomFact) && ReferenceEquals(messages[1], toolFact) &&
                ReferenceEquals(messages[2], userQuote) && ReferenceEquals(messages[3], assistantQuote) && ReferenceEquals(messages[4], instruction),
                "Format fact replacement removed or reordered unrelated execution history or quoted text.");
        }
        RoleOutputFormatFactHistory.ReplaceCurrent(messages, m => m.role, m => m.content,
            text => new FactMessage("system", text), "");
        Check(messages.Count == 5, "Successful completion left an obsolete format error active.");
    }

    private static string Signature(List<SpeechText> parts)
    {
        var result = new StringBuilder(); var buffer = new SpeechTextBuffer();
        foreach (SpeechText part in parts) buffer.Append(part);
        foreach (SpeechText part in buffer.Snapshot()) result.Append(part.ActionDependency).Append(':')
            .Append(part.LanguageCode).Append(':').Append(part.Text).Append('|');
        return result.ToString();
    }

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
