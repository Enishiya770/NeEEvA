using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
#endif

/// <summary>Recorded parser replay and real production request construction; no network or audio.</summary>
public static class SingingOutputContractRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const RoleOutputBoundaryPolicy Policy = RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions;
    private const string Header = "<lang code=\"zh\"/><speech mode=\"after_action\"/>";
    private const string Independent = "<lang code=\"zh\"/><speech mode=\"independent\"/>";
    private const string Sing = "<sing refs=\"clip:9043c26c-1\" reason=\"依据已核验目标\"/>";
    private const string Goal = "<sing_goal origin=\"user_request\" refs=\"clip:9043c26c-1\" range=\"current\" revisions=\"0\" intent=\"perform\" completion=\"playback\" expected=\"演唱所选片段\" feedback=\"none\"/>";
    private static int checks;

    public static JObject RunRecordedOrThrow(string frozenLogPath)
    {
        checks = 0;
        byte[] bytes = File.ReadAllBytes(frozenLogPath);
        string hash = Hash(bytes);
        Check(hash == "421b407b5481a744cd3da21b00b0e7030f4216ff5433189e548e9adbfefcd91f",
            "Recorded singing replay must use the reviewed frozen Play log.");
        string[] lines = Encoding.UTF8.GetString(bytes).Split('\n');
        int[] lineNumbers = { 113461, 113812, 116280, 116622, 117028, 118324, 118797, 119833, 120092 };
        string[] failures = { "public_boundary", "goal_not_reviewed", "public_boundary", "public_boundary",
            "speech_phase", "speech_phase", "goal_not_reviewed", "public_boundary", "public_boundary" };
        // These are hand-authored valid protocol examples, not an automatic repair
        // that could promote private/copied model text into executable actions.
        string[] normalized = {
            Header + "<say>我先确认要唱的片段。</say>" + Goal,
            Header + "<say>我按已核验目标演唱。</say><silent/>内部备注。" + Sing,
            Header + "<say>我先确认这一段。</say>" + Goal,
            "<thought>先核对原请求。<sing refs=\"private-example\"/></thought>" + Header + "<say>我先确认。</say>" + Goal,
            Header + "<say>先核对演唱目标。</say>" + Goal,
            Header + "<say>我按已核验目标唱。</say>" + Sing,
            Header + "<say>我按已核验目标唱。</say>" + Sing,
            "<thought>先核对缺少的步骤。</thought>" + Header + "<say>我先确认目标。</say>" + Goal,
            "<thought>先核对实际结果。</thought>" + Header + "<say>我先确认目标。</say>" + Goal
        };
        var cases = new JArray();
        for (int index = 0; index < lineNumbers.Length; index++)
        {
            int line = lineNumbers[index];
            string record = lines[line - 1].Replace("\r", "");
            int marker = record.IndexOf("[LLM原文] ", StringComparison.Ordinal);
            Check(marker >= 0, "Frozen LF does not contain the expected model response: " + line);
            string raw = record.Substring(marker + "[LLM原文] ".Length).Replace("\\n", "\n");
            for (int split = 0; split <= raw.Length; split++)
            {
                var parser = new RoleOutputChannels(boundaryPolicy: Policy);
                parser.Push(raw.Substring(0, split)); parser.Push(raw.Substring(split)); parser.Finish();
                string executable = parser.ToExecutableText();
                if (failures[index] == "public_boundary")
                {
                    Check(parser.HasInvalidPublicBoundary && !parser.HasActions && executable == "<silent/>",
                        "Recorded invalid public output became executable at LF " + line + " split " + split);
                    if (line != 113461)
                        Check(!parser.HasSpeech, "A private or unframed recorded prefix leaked into public speech at LF " + line);
                }
                else if (failures[index] == "speech_phase")
                    Check(parser.HasInvalidSpeechPhase && !executable.Contains("<sing"),
                        "Recorded independent singing action escaped phase rejection at LF " + line);
                else
                    Check(!parser.HasInvalidPublicBoundary && !parser.HasInvalidSpeechPhase && executable.Contains("<sing "),
                        "A target-review failure was incorrectly relabeled as a format error at LF " + line);
                if (line == 119833 || line == 120092)
                    Check(parser.PublicBoundaryError.Contains("silent") && parser.PublicBoundaryError.Contains("thought"),
                        "Silent-tail rejection does not explain the actual cause and valid alternative.");
            }
            VerifyValidEverySplit(normalized[index]);
            cases.Add(new JObject { ["rawLf"] = line, ["observedFailure"] = failures[index],
                ["normalizedExample"] = normalized[index],
                ["scope"] = "Syntax acceptance only. Goal review and singing authorization remain owned by execution checks." });
        }

        VerifyValidEverySplit("<silent/>" + Sing);
        VerifyValidEverySplit(Header + "<say>我先检查。</say><silent/>只留在心里的说明。" + Sing);
        VerifyValidEverySplit("<thought>私有示例 <sing refs=\"private-example\"/></thought><silent/>" + Sing);
        var privateOnly = RoleOutputChannels.Parse("<thought>未闭合<sing refs=\"private-example\"/>", Policy);
        Check(!privateOnly.HasSpeech && !privateOnly.HasActions, "Unclosed private block executed a singing example.");
        string policy = "<singing_policy value=\"suppress_autonomy\"/>";
        var conflicted = RoleOutputChannels.Parse(Independent + "<say>我先回应。</say>" + Sing + policy +
            "<singing_stop reason=\"停止当前歌声\"/>", Policy);
        Check(conflicted.HasInvalidSpeechPhase && !conflicted.ToExecutableText().Contains("<sing ") &&
            !conflicted.ToExecutableText().Contains("<singing_policy") &&
            conflicted.ToExecutableText().Contains("<singing_stop"),
            "Phase conflict retained a policy mutation or removed the direct stop request.");
        var pendingPolicy = RoleOutputChannels.Parse(Header + "<say>我先确认你的意思。</say>" + policy, Policy);
        Check(!pendingPolicy.HasInvalidSpeechPhase && pendingPolicy.HasSingingValidationActions &&
            pendingPolicy.SpeechDependency == SpeechActionDependency.AfterAction && pendingPolicy.HasActions,
            "Valid policy proposal was not attached to the validation speech gate.");
        var privatePolicy = RoleOutputChannels.Parse("<thought>示例：" + policy + "</thought><silent/>", Policy);
        Check(!privatePolicy.HasActions && !privatePolicy.HasSingingValidationActions,
            "Private policy example became a permission proposal.");
        return new JObject { ["status"] = "passed", ["checks"] = checks, ["sourceSha256"] = hash,
            ["recordedResponses"] = cases, ["networkRequests"] = 0, ["audioPlayback"] = false };
    }

#if UNITY_EDITOR
    public static JObject ExportPublicFormalCasesOrThrow(string outputDirectory)
        => SingingFormalOutputRegression.ExportPublicFormalCasesOrThrow(outputDirectory);

    public static JObject ValidatePublicFormalResponses(string casesPath, string responsesPath, string outputDirectory)
        => SingingFormalOutputRegression.ValidatePublicFormalResponses(casesPath, responsesPath, outputDirectory);

    public static JObject RunPromptAuditOrThrow(string originalBehaviorPath, string outputDirectory)
    {
        checks = 0;
        Directory.CreateDirectory(outputDirectory);
        var host = new GameObject("SingingPromptContractAudit"); host.SetActive(false);
        TextAsset legacy = null;
        try
        {
            var model = host.AddComponent<ChatQW>();
            var behavior = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/AIChatTookit/Prompts/behavior.txt");
            var skill = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/AIChatTookit/Prompts/Skills/singing.txt");
            Check(behavior != null && skill != null, "Actual production prompt assets are required.");
            string original = File.ReadAllText(originalBehaviorPath);
            Check(original.Contains("<!-- SKILL-BEGIN:") && original.Contains("之前的文字会被念出来"),
                "Prompt migration audit requires the pre-change source snapshot.");
            legacy = new TextAsset(original) { name = "ArchivedBehaviorBeforeCleanup" };
            Check(!behavior.text.Contains("SKILL-BEGIN") && !behavior.text.Contains("之前的文字会被念出来") &&
                !skill.text.Contains("把实际问题写在普通正文"), "Conflicting legacy runtime guidance remains.");
            typeof(LLM).GetField("m_SkillFiles", Flags).SetValue(model, new[] { skill });
            var requests = new JArray();
            foreach (bool before in new[] { true, false })
            {
                typeof(LLM).GetField("m_PromptFiles", Flags).SetValue(model, new[] { before ? legacy : behavior });
                string basePrompt = model.BuildSystemPrompt();
                Check(!basePrompt.Contains("<!-- SKILL-BEGIN:") && !basePrompt.Contains("同一句的多次录音只算一段") &&
                    !basePrompt.Contains("来源仍在隔离区的候选不能直接唱"),
                    "Production BuildSystemPrompt leaked an archived embedded skill block.");
                Check(!basePrompt.Contains("## 当前事项与独立核对"), "Detailed singing skill entered the base prompt.");
                foreach (bool active in new[] { false, true })
                foreach (bool streaming in new[] { false, true })
                {
                    Check(model.SetActiveSkills(active ? new[] { "singing" } : new string[0]), "Skill could not be loaded.");
                    model.m_DataList.Clear();
                    model.m_DataList.Add(new LLM.SendData("system", basePrompt));
                    model.m_DataList.Add(new LLM.SendData("user", "请按实际录音状态判断下一步。"));
                    string context = (string)typeof(ChatQW).GetMethod("AddSpeechOutputContract", Flags)
                        .Invoke(null, new object[] { "仅验证生产请求的提示边界。" });
                    string payload = (string)typeof(ChatQW).GetMethod("BuildRequestJson", Flags)
                        .Invoke(model, new object[] { streaming, context });
                    JObject wire = JObject.Parse(payload);
                    string effective = wire["messages"].ToString();
                    Check(!effective.Contains("<!-- SKILL-BEGIN:") && !effective.Contains("同一句的多次录音只算一段"),
                        "Actual production request construction included archived singing instructions.");
                    Check(effective.Contains("[已加载技能: singing]") == active &&
                        effective.Contains("## 当前事项与独立核对") == active,
                        "Production request did not load exactly the selected detailed singing skill.");
                    Check(effective.Contains("之前的文字会被念出来") == before,
                        "Prompt audit lost the distinction between excluded skill blocks and the live legacy prose example.");
                    Check(effective.Contains("silent 是本条回复的静音尾部") && effective.Contains("公开台词只放在 <say>"),
                        "Production transient output contract lost explicit private/public boundaries.");
                    string name = "prompt-" + (before ? "before" : "after") + (active ? "-singing" : "-base") +
                        (streaming ? "-stream" : "-nonstream") + ".request.json";
                    File.WriteAllText(Path.Combine(outputDirectory, name), payload, new UTF8Encoding(false));
                    requests.Add(new JObject { ["file"] = name, ["sha256"] = Hash(Encoding.UTF8.GetBytes(payload)),
                        ["legacySource"] = before, ["singingActive"] = active, ["stream"] = streaming,
                        ["sent"] = false, ["builder"] = "ChatQW.BuildRequestJson" });
                }
            }
            return new JObject { ["status"] = "passed", ["checks"] = checks, ["productionRequests"] = requests,
                ["scope"] = "Actual production request construction, without HTTP submission or model evaluation." };
        }
        finally
        {
            if (legacy != null) UnityEngine.Object.DestroyImmediate(legacy);
            UnityEngine.Object.DestroyImmediate(host);
        }
    }
#endif

    private static void VerifyValidEverySplit(string raw)
    {
        for (int split = 0; split <= raw.Length; split++)
        {
            var parts = new List<SpeechText>();
            var parser = new RoleOutputChannels(parts.Add, Policy);
            parser.Push(raw.Substring(0, split)); parser.Push(raw.Substring(split)); parser.Finish();
            Check(!parser.HasInvalidPublicBoundary && !parser.HasInvalidSpeechPhase && parser.HasActions,
                "Valid singing output lost public/tool separation at split " + split);
            Check(!parser.ToExecutableText().Contains("private-example"), "Private singing example escaped into executable text.");
            foreach (var part in parts)
                Check(part.LanguageCode == "zh" && part.ActionDependency == SpeechActionDependency.AfterAction,
                    "Public speech was detached from its language or singing validation gate.");
        }
    }

    private static string Hash(byte[] bytes)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    private static void Check(bool condition, string error) { checks++; if (!condition) throw new Exception(error); }
}
