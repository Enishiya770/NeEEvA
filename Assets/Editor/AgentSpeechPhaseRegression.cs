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
        Check(RoleOutputChannels.Parse("<lang code=\"ja\"/>こんにちは。").SpeechDependency == SpeechActionDependency.Unspecified,
            "Stage leaked into an unrelated legacy request.");
        var buffered = new SpeechTextBuffer();
        buffered.Append(new SpeechText("明日。", "ja", actionDependency: SpeechActionDependency.Independent));
        buffered.Append(new SpeechText("後で。", "ja", actionDependency: SpeechActionDependency.AfterAction));
        buffered.Remove(0, 1);
        Check(buffered.Snapshot().Count == 2 && buffered.Snapshot()[0].ActionDependency == SpeechActionDependency.Independent &&
            buffered.Snapshot()[1].ActionDependency == SpeechActionDependency.AfterAction, "A held buffer lost action-stage boundaries.");
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
