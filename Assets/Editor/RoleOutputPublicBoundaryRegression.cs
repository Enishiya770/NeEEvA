using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

/// <summary>Pure parser regression; no editor, model, scene or audio side effects.</summary>
public static class RoleOutputPublicBoundaryRegression
{
    private static int checks;
    public static int RunOrThrow()
    {
        checks = 0;
        string[] internalOutputs = {
            "[执行事实：连续 1 次续接期间，没有歌声生成或播放任务在运行。口头计划与 <continue/> 不会启动演唱。]" +
                "\n[Sing/Execution] request_submitted_since_user_turn=true work_active=false state=idle\n<continue/>",
            "[程序执行事实/当前输出格式] 上一次输出不是成功。<motion name=\"approach\"/>",
            "[继续上一用户事项；不是主动闲聊]\n本轮要推进：。<sing refs=\"clip:synthetic\"/>",
            "[本次正式决策的最新执行观察；旧历史帧只代表当时状态。]<next in=\"2s\"/>",
            "[感知帧 22:06:39]\n[Sing/Execution] state=idle<continue/>",
            "[Room/Trace] {\"phase\":\"arrived\"}<motion name=\"nod\"/>",
            "[Agent/自主意图] 开始预判 trigger=default<continue/>",
            "[AnyProducer/Event] requestId=synthetic status=ok<look/>"
        };
        foreach (string raw in internalOutputs)
        {
            VerifyBlockedEverySplit(raw, RoleOutputBoundaryPolicy.LegacyCompatible, true);
            VerifyBlockedEverySplit(Header + "<say>" + raw + "</say>",
                RoleOutputBoundaryPolicy.ExplicitPublicSay, true);
        }

        foreach (string text in new[] {
            "程序可能出了问题，我先确认一下。", "动作没有执行成功，刚才还没有播放。",
            "うまく再生できなかったみたい。もう一度確かめるね。",
            "中文和日本語を混ぜても、台词不能丢失。", "日志里的 [Room/Trace] 是诊断记录。",
            "[大丈夫]", "[temperature=20]", "《One Last Kiss》という曲の話ね。"
        })
        {
            VerifySpeechEverySplit(Header + text, RoleOutputBoundaryPolicy.LegacyCompatible, text);
            VerifySpeechEverySplit(Header + "<say>" + text + "</say>",
                RoleOutputBoundaryPolicy.ExplicitPublicSay, text);
        }

        VerifySpeechEverySplit("<thought>私人的检查，不发声。<sing refs=\"private\"/></thought>" +
            Header + "<say>確認しているよ。</say><lang code=\"zh\"/><say>正在确认。</say>",
            RoleOutputBoundaryPolicy.ExplicitPublicSay, "確認しているよ。正在确认。");
        foreach (string raw in new[] {
            "无标记内部说明。" + Header + "<say>你好。</say><motion name=\"approach\"/>",
            "<say>没有语言和阶段。</say>", "<lang code=\"ja\"/><say>阶段缺失。</say>",
            "<speech mode=\"independent\"/><say>语言缺失。</say>",
            Header + "<say extra=\"unexpected\">你好。</say>",
            Header + "<say>[motion name=\"approach\"]</say>",
            Header + "<say/>裸露文字", Header + "</say><say>你好。</say>"
        }) VerifyBlockedEverySplit(raw, RoleOutputBoundaryPolicy.ExplicitPublicSay, true);

        foreach (string action in new[] { "motion", "sing", "sing_goal", "look", "continue", "next",
            "skill_request", "memory_add", "body_inspect", "practice_confirm", "future_tool" })
        {
            foreach (string raw in new[] {
                Header + "<" + action + " value=\"synthetic\"/><say>你好。</say>",
                Header + "<say><" + action + " value=\"synthetic\"/>你好。</say>",
                Header + "<say>你好。</say><" + action + " value=\"synthetic\"/>"
            })
            {
                VerifyBlockedEverySplit(raw, RoleOutputBoundaryPolicy.ExplicitPublicSay,
                    !raw.StartsWith(Header + "<say>你好。", StringComparison.Ordinal));
            }
        }

        var legacyAction = RoleOutputChannels.Parse(Header + "走到你身边。<motion name=\"approach\"/>");
        Check(!legacyAction.HasInvalidPublicBoundary && legacyAction.ToExecutableText().Contains("<motion"),
            "Legacy independent action was removed.");
        var dependent = RoleOutputChannels.Parse("<lang code=\"ja\"/><speech mode=\"after_action\"/>" +
            "確認してから歌うね。<sing refs=\"clip:synthetic\"/>");
        Check(!dependent.HasInvalidPublicBoundary && !dependent.HasInvalidSpeechPhase &&
            dependent.ToExecutableText().Contains("<sing"), "Legacy after_action semantics changed.");

        var early = new List<SpeechText>();
        var streamed = new RoleOutputChannels(early.Add, RoleOutputBoundaryPolicy.ExplicitPublicSay);
        streamed.Push(Header + "<say>今の話を聞いているよ。");
        Check(Join(early).Contains("聞いている"), "Explicit public text waited for the entire response.");
        streamed.Push("</say>"); streamed.Finish();
        Check(!streamed.HasInvalidPublicBoundary, "Closed early public text was rejected.");
        foreach (string raw in new[] {
            Header + "<say>自然な先行台詞。</say>無標記の内部文章<continue/>",
            Header + "<say>自然な先行台詞。</say><motion name=\"approach\"/>"
        })
        {
            for (int split = 0; split <= raw.Length; split++)
            {
                var parts = new List<SpeechText>();
                var parser = new RoleOutputChannels(parts.Add, RoleOutputBoundaryPolicy.ExplicitPublicSay);
                parser.Push(raw.Substring(0, split)); parser.Push(raw.Substring(split)); parser.Finish();
                Check(Join(parts) == "自然な先行台詞。" && parser.ToExecutableText() == "<silent/>",
                    "Quarantine changed an already valid public prefix across packet boundaries.");
            }
        }

        // Unknown prose cannot be smuggled into a tools-only projection.
        var partial = new RoleOutputChannels(boundaryPolicy: RoleOutputBoundaryPolicy.ExplicitPublicSay);
        partial.Push(Header + "<say>未完成。"); partial.Finish();
        Check(partial.HasInvalidPublicBoundary && partial.ToExecutableText() == "<silent/>",
            "An unfinished public envelope retained an executable completion.");
        RunToolPublicBoundaryCases();
        return checks;
    }

    private static void RunToolPublicBoundaryCases()
    {
        const RoleOutputBoundaryPolicy policy = RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions;
        foreach (string raw in new[] {
            "lang code=ja\n\nspeech mode=after_action\n\n聞いてね。<sing refs=\"clip:example\"/>",
            "ユーザーは二つの音声を求めている。まず内部の手順を検討する。" + Header +
                "<say>確認しているよ。</say><motion name=\"nod\"/>",
            Header + "無標記の正文。<motion name=\"follow\"/>",
            Header + "<say><motion name=\"follow\"/>ついていくね。</say>",
            Header + "<say><lang code=\"zh\"/>你好。</say>",
            "<say>前置声明なし。</say><motion name=\"stop\"/>",
            Header + "<say extra=\"bad\">你好。</say>",
            Header + "<say>[Sing/Execution] state=idle</say><continue/>",
            Header + "<motion name=\"nod\"/>[程序执行事实/当前输出格式] copied"
        }) VerifyBlockedEverySplit(raw, policy, true);

        foreach (string text in new[] {
            "程序没有执行成功，我还需要确认。", "lang code=ja は言語設定の記述だよ。",
            "歌声还没播放，但移动已经停下了。", "I have not heard the result yet."
        }) VerifySpeechEverySplit(Header + "<say>" + text + "</say>", policy, text);

        var cases = new Dictionary<string, string> {
            { Header + "<say>一緒に行くね。</say><motion name=\"follow\"/>", "一緒に行くね。" },
            { Header + "<motion name=\"stop\"/><say>止まったか確認しているよ。</say>", "止まったか確認しているよ。" },
            { "<thought>内部判断。<sing refs=\"private\"/></thought>" + Header +
                "<say>聞こえたよ。</say><next in=\"5s\"/>", "聞こえたよ。" },
            { "<lang code=\"ja\"/><speech mode=\"after_action\"/><say>準備を確認してから歌うね。</say>" +
                "<sing refs=\"clip:example\"/>", "準備を確認してから歌うね。" },
            { "<silent/><motion name=\"nod\"/>", "" },
            { "<silent/><sing refs=\"clip:example\"/>", "" }
        };
        foreach (var test in cases)
        {
            for (int split = 0; split <= test.Key.Length; split++)
            {
                var parts = new List<SpeechText>(); var parser = new RoleOutputChannels(parts.Add, policy);
                parser.Push(test.Key.Substring(0, split)); parser.Push(test.Key.Substring(split)); parser.Finish();
                Check(!parser.HasInvalidPublicBoundary && !parser.HasInvalidSpeechPhase && parser.HasActions &&
                    Join(parts) == test.Value && !parser.ToExecutableText().Contains("private"),
                    "Valid public/tool separation changed at split " + split);
                foreach (var part in parts)
                    Check(part.LanguageCode == "ja" && part.ActionDependency == parser.SpeechDependency,
                        "Language or action dependency was detached from streamed public speech.");
            }
        }

        var switchedParts = new List<SpeechText>();
        var switched = new RoleOutputChannels(switchedParts.Add, policy);
        string switchedText = Header + "<say>確認したよ。</say><lang code=\"zh\"/><say>我确认过了。</say><next in=\"5s\"/>";
        foreach (char c in switchedText) switched.Push(c.ToString()); switched.Finish();
        Check(!switched.HasInvalidPublicBoundary && switched.HasActions && Join(switchedParts) == "確認したよ。我确认过了。",
            "Language-switch streaming inserted markup, lost speech or removed independent actions.");
        Check(switchedParts.FindAll(p => p.LanguageCode == "ja").Count == "確認したよ。".Length &&
            switchedParts.FindAll(p => p.LanguageCode == "zh").Count == "我确认过了。".Length,
            "Language metadata did not stay attached to each public span.");

        var dependentParts = new List<SpeechText>();
        var earlyDependent = new RoleOutputChannels(dependentParts.Add, policy);
        earlyDependent.Push("<lang code=\"ja\"/><speech mode=\"after_action\"/><say>準備を確認するね。");
        Check(Join(dependentParts) == "準備を確認するね。" &&
            dependentParts.TrueForAll(p => p.ActionDependency == SpeechActionDependency.AfterAction),
            "Dependent speech must stream to the existing validation gate without waiting for completion.");
        earlyDependent.Push("</say><sing refs=\"clip:example\"/>"); earlyDependent.Finish();
        Check(!earlyDependent.HasInvalidSpeechPhase && earlyDependent.HasActions,
            "Valid dependent action was lost after streaming its public prefix.");

        // Existing semantic execution checks still own phase correctness. A syntax
        // boundary must not silently change an independent promise into a singing call.
        var independentSing = RoleOutputChannels.Parse(Header + "<say>確認しているよ。</say><sing refs=\"clip:example\"/>", policy);
        Check(independentSing.HasInvalidSpeechPhase && !independentSing.ToExecutableText().Contains("<sing"),
            "Explicit public boundary bypassed the independent singing phase gate.");
        var emptyDependent = RoleOutputChannels.Parse("<lang code=\"ja\"/><speech mode=\"after_action\"/>" +
            "<say>確認しているよ。</say><motion name=\"approach\"/>", policy);
        Check(emptyDependent.HasInvalidSpeechPhase && emptyDependent.ToExecutableText().Contains("<motion") &&
            !emptyDependent.ToExecutableText().Contains("確認している"),
            "Speech phase rejection changed independent motion execution semantics.");

        string prefixThenFailure = Header + "<say>今の状態を確認しているよ。</say>裸の内部記録<continue/>";
        for (int split = 0; split <= prefixThenFailure.Length; split++)
        {
            var parts = new List<SpeechText>(); var parser = new RoleOutputChannels(parts.Add, policy);
            parser.Push(prefixThenFailure.Substring(0, split)); parser.Push(prefixThenFailure.Substring(split)); parser.Finish();
            Check(parser.HasInvalidPublicBoundary && Join(parts) == "今の状態を確認しているよ。" &&
                parser.ToExecutableText() == "<silent/>", "Malformed tail replayed or retracted an already streamed public prefix.");
        }
    }

    /// <summary>Replay three exact raw responses from the frozen failed Play, never a live service.</summary>
    public static int RunRecordedPlayOrThrow(string logPath)
    {
        byte[] bytes = File.ReadAllBytes(logPath);
        string hash;
        using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        Check(hash == "8778a0a6a49754794c2b24d6ff29e45f92d62d397812d3d493174c5aaeab8347",
            "Recorded regression must use the reviewed frozen Play snapshot.");
        string[] lines = Encoding.UTF8.GetString(bytes).Split('\n');
        int before = checks;
        foreach (int line in new[] { 216551, 218496, 224107 })
        {
            const string prefix = "[LLM原文] ";
            string record = lines[line - 1].TrimEnd('\r');
            Check(record.StartsWith(prefix, StringComparison.Ordinal), "Frozen raw response moved at LF" + line);
            string raw = record.Substring(prefix.Length).Replace("\\n", "\n");
            VerifyBlockedEverySplit(raw, RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions, true);
        }
        return checks - before;
    }

    private const string Header = "<lang code=\"ja\"/><speech mode=\"independent\"/>";

    public static void VerifyRecordedOffender(string raw)
    {
        VerifyBlockedEverySplit(raw, RoleOutputBoundaryPolicy.LegacyCompatible, true);
    }

    private static void VerifyBlockedEverySplit(string raw, RoleOutputBoundaryPolicy policy, bool noEarlySpeech)
    {
        for (int split = 0; split <= raw.Length; split++)
        {
            var parts = new List<SpeechText>(); var parser = new RoleOutputChannels(parts.Add, policy);
            parser.Push(raw.Substring(0, split)); parser.Push(raw.Substring(split)); parser.Finish();
            Check(parser.HasInvalidPublicBoundary, "Boundary not rejected at split " + split);
            Check(parser.ToExecutableText() == "<silent/>" && !parser.HasActions,
                "Quarantined output retained executable actions at split " + split);
            if (noEarlySpeech) Check(string.IsNullOrWhiteSpace(Join(parts)), "Private bytes reached streaming speech at split " + split);
        }
        var chars = new List<SpeechText>(); var characterParser = new RoleOutputChannels(chars.Add, policy);
        foreach (char c in raw) characterParser.Push(c.ToString()); characterParser.Finish();
        Check(characterParser.HasInvalidPublicBoundary && characterParser.ToExecutableText() == "<silent/>",
            "Character deltas bypassed quarantine.");
        if (noEarlySpeech) Check(string.IsNullOrWhiteSpace(Join(chars)), "Character deltas leaked private bytes.");
    }

    private static void VerifySpeechEverySplit(string raw, RoleOutputBoundaryPolicy policy, string expected)
    {
        for (int split = 0; split <= raw.Length; split++)
        {
            var parts = new List<SpeechText>(); var parser = new RoleOutputChannels(parts.Add, policy);
            parser.Push(raw.Substring(0, split)); parser.Push(raw.Substring(split)); parser.Finish();
            Check(!parser.HasInvalidPublicBoundary && Join(parts) == expected && parser.Speech == expected,
                "Legitimate speech changed at split " + split + ": " + Join(parts));
        }
        var chars = new List<SpeechText>(); var characterParser = new RoleOutputChannels(chars.Add, policy);
        foreach (char c in raw) characterParser.Push(c.ToString()); characterParser.Finish();
        Check(!characterParser.HasInvalidPublicBoundary && Join(chars) == expected, "Character stream altered legitimate speech.");
    }

    private static string Join(List<SpeechText> parts)
    {
        var result = new StringBuilder(); foreach (SpeechText part in parts) result.Append(part.Text); return result.ToString();
    }
    private static void Check(bool condition, string error)
    {
        checks++; if (!condition) throw new InvalidOperationException(error);
    }
}
