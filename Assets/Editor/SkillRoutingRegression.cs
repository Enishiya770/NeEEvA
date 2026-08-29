using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Small dependency-free regression suite for the text side of the generic Skill router.
/// It intentionally uses reflection so the runtime policy can keep its implementation types private.
/// </summary>
public static class SkillRoutingRegression
{
    private struct Case
    {
        public string Text;
        public string Expected;

        public Case(string text, string expected)
        {
            Text = text;
            Expected = expected;
        }
    }

    [MenuItem("Tools/NeEEvA/Run Skill Routing Regression")]
    public static void RunInteractive()
    {
        RunOrThrow();
        Debug.Log("[SkillRoutingRegression] all cases passed");
    }

    public static void RunBatch()
    {
        try
        {
            RunOrThrow();
            Debug.Log("[SkillRoutingRegression] all cases passed");
            EditorApplication.Exit(0);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorApplication.Exit(1);
        }
    }

    private static void RunOrThrow()
    {
        Type chatType = typeof(ChatSample);
        FieldInfo definitionsField = chatType.GetField(
            "s_SkillRouteDefinitions", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo classifyMethod = chatType.GetMethod(
            "ClassifySkillUserIntent", BindingFlags.Static | BindingFlags.NonPublic);
        if (definitionsField == null || classifyMethod == null)
            throw new MissingMemberException("Skill routing internals were renamed without updating the regression suite.");

        Array definitions = definitionsField.GetValue(null) as Array;
        if (definitions == null || definitions.Length == 0)
            throw new InvalidOperationException("No Skill route definitions are registered.");
        object singing = definitions.GetValue(0);

        var cases = new[]
        {
            new Case("现在其实已经不需要唱歌了，所以把相关技能关掉。", "Mention"),
            new Case("刚才唱歌的技能你关掉了吗？还是现在仍然开着？", "Mention"),
            new Case("我没事，那个你现在有把那个唱歌相关的智能关掉吗？", "Mention"),
            new Case("把唱歌相关的那些技能给关掉。", "Mention"),
            new Case("这首歌的旋律很特别。", "Mention"),
            new Case("请再把它唱出来。", "Mention"),
            new Case("不要停止唱歌。", "Mention"),
            new Case("先别主动唱，一会儿再说。", "Mention"),
            new Case("歌唱機能をオフにした？", "Mention"),
            new Case("歌唱機能をオフにして。", "Mention"),
            new Case("Did you turn singing off?", "Mention"),
            new Case("Please sing it again.", "Mention"),
            new Case("第一二三段不要唱出来，只需要管第四段和第五段。", "Mention"),
            new Case("我从来没有禁止过你，你想唱随时都能唱。", "Mention"),
            new Case("唱歌同意你唱歌。", "Mention"),
        };

        for (int i = 0; i < cases.Length; i++)
        {
            string actual = classifyMethod.Invoke(null, new[] { (object)cases[i].Text, singing }).ToString();
            if (!string.Equals(actual, cases[i].Expected, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Case {i + 1} failed: expected {cases[i].Expected}, got {actual}; text={cases[i].Text}");
        }

        RunAutonomyIntentRegression(chatType, singing);
        RunStructuredSingingIntentRegression(chatType);
        RunSingingToolRoutingRegression(chatType);
    }

    private static void RunStructuredSingingIntentRegression(Type chatType)
    {
        MethodInfo extractMethod = chatType.GetMethod(
            "ExtractSingingIntentTag", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo validateMethod = chatType.GetMethod(
            "ValidateSingingIntentFields", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo authorizeMethod = chatType.GetMethod(
            "ValidateUserSingingActionAuthorization", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo unknownTagMethod = chatType.GetMethod(
            "DetectUnknownAgentTag", BindingFlags.Static | BindingFlags.NonPublic);
        if (extractMethod == null || validateMethod == null || authorizeMethod == null ||
            unknownTagMethod == null)
            throw new MissingMemberException(
                "Structured singing intent internals were renamed without updating the regression suite.");

        object[] extractArgs =
        {
            "只唱第四第五段。<singing_intent permission=\"no_change\" " +
            "scope=\"content_constraint\" actor=\"character\" action=\"practice\" order=\"4,5\" " +
            "confidence=\"0.99\" evidence=\"只唱第四第五段\"/>",
        };
        object intent = extractMethod.Invoke(null, extractArgs);
        if (intent == null || extractArgs[0].ToString().Contains("singing_intent"))
            throw new InvalidOperationException("singing_intent tag was not extracted cleanly.");
        FieldInfo permission = intent.GetType().GetField("Permission");
        FieldInfo actor = intent.GetType().GetField("Actor");
        FieldInfo order = intent.GetType().GetField("Order");
        if (permission == null || actor == null || order == null ||
            (string)permission.GetValue(intent) != "no_change" ||
            (string)actor.GetValue(intent) != "character" ||
            (string)order.GetValue(intent) != "4,5")
            throw new InvalidOperationException("singing_intent attributes were parsed incorrectly.");

        object[] scopedSelection =
        {
            "no_change", "content_constraint", "character", "practice", "4,5", 0.99f,
            "第一二三段不要唱出来，只需要管第四段和第五段",
            "第一二三段不要唱出来，只需要管第四段和第五段。", null,
        };
        if (!(bool)validateMethod.Invoke(null, scopedSelection))
            throw new InvalidOperationException(
                "A content-scoped negative instruction was rejected as a singing intent: " +
                scopedSelection[8]);

        object[] reenable =
        {
            "enable", "capability", "character", "none", "", 0.98f,
            "你想唱随时都能唱", "我从来没有禁止过你，你想唱随时都能唱。", null,
        };
        if (!(bool)validateMethod.Invoke(null, reenable))
            throw new InvalidOperationException("Explicit singing reauthorization was rejected: " + reenable[8]);

        object[] userWillSing =
        {
            "no_change", "none", "user", "none", "", 0.99f,
            "我唱另外一首", "不不不，我的意思是我唱另外一首。", null,
        };
        if (!(bool)validateMethod.Invoke(null, userWillSing))
            throw new InvalidOperationException("A valid user-as-singer decision was rejected: " + userWillSing[8]);

        object[] userCannotDriveCharacterSong =
        {
            "no_change", "none", "user", "catalog", "", 0.99f,
            "我唱另外一首", "不不不，我的意思是我唱另外一首。", null,
        };
        if ((bool)validateMethod.Invoke(null, userCannotDriveCharacterSong))
            throw new InvalidOperationException("actor=user was allowed to authorize character singing.");

        object[] emptyPracticeOrder =
        {
            "no_change", "content_constraint", "character", "practice", "", 0.99f,
            "只唱第四第五段", "只唱第四第五段。", null,
        };
        if ((bool)validateMethod.Invoke(null, emptyPracticeOrder))
            throw new InvalidOperationException("Multi-phrase practice intent accepted an empty order.");

        object[] inventedEvidence =
        {
            "disable", "capability", "none", "none", "", 0.99f,
            "以后永久关闭歌唱能力", "第一二三段不要唱，只唱第四第五段。", null,
        };
        if ((bool)validateMethod.Invoke(null, inventedEvidence))
            throw new InvalidOperationException("Invented LLM evidence was allowed to alter Skill permission.");

        string unknown = (string)unknownTagMethod.Invoke(
            null, new object[]
            {
                "<singing_intent permission=\"no_change\" scope=\"none\" actor=\"none\" action=\"none\" " +
                "confidence=\"0.9\" evidence=\"唱歌\"/>"
            });
        if (!string.IsNullOrEmpty(unknown))
            throw new InvalidOperationException("singing_intent is missing from the known tag registry.");

        object[] wrongActorTool = { "user", "none", true, "", null };
        if ((bool)authorizeMethod.Invoke(null, wrongActorTool))
            throw new InvalidOperationException("A song_sing tool escaped with actor=user.");
        object[] missingIntentTool = { "", "", false, "echo", null };
        if ((bool)authorizeMethod.Invoke(null, missingIntentTool))
            throw new InvalidOperationException("A real-user hum_back escaped without singing_intent.");
        object[] catalogTool = { "character", "catalog", true, "", null };
        if (!(bool)authorizeMethod.Invoke(null, catalogTool))
            throw new InvalidOperationException("A matching character catalog action was rejected: " + catalogTool[4]);
        object[] practiceTool = { "character", "practice", false, "practice", null };
        if (!(bool)authorizeMethod.Invoke(null, practiceTool))
            throw new InvalidOperationException("A matching character practice action was rejected: " + practiceTool[4]);

        TextAsset prompt = AssetDatabase.LoadAssetAtPath<TextAsset>(
            "Assets/AIChatTookit/Prompts/Skills/singing.txt");
        if (prompt == null || !prompt.text.Contains("scope=\"content_constraint\"") ||
            !prompt.text.Contains("actor=\"user\" action=\"none\"") ||
            !prompt.text.Contains("第一、二、三段不要唱出来"))
            throw new InvalidOperationException(
                "The singing Skill is missing the structured scope protocol or its critical example.");
    }

    private static void RunSingingToolRoutingRegression(Type chatType)
    {
        MethodInfo practiceIntent = chatType.GetMethod(
            "IsSongSingPracticeIntent", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo titleCompatibility = chatType.GetMethod(
            "AreSongTitlesCompatible", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo resolveExpectedTitle = chatType.GetMethod(
            "ResolveExpectedSongTitle", BindingFlags.Static | BindingFlags.NonPublic);
        if (practiceIntent == null || titleCompatibility == null || resolveExpectedTitle == null)
            throw new MissingMemberException(
                "Singing tool routing internals were renamed without updating the regression suite.");

        bool userPractice = (bool)practiceIntent.Invoke(null, new object[]
        {
            "把我刚刚录的那三段 One Last Kiss 连着唱一遍", "", ""
        });
        bool reasonPractice = (bool)practiceIntent.Invoke(null, new object[]
        {
            "再试一次", "重新尝试演唱《One Last Kiss》的三段练习内容。", ""
        });
        bool orderPractice = (bool)practiceIntent.Invoke(null, new object[]
        {
            "再唱一次", "", "1,2,3"
        });
        bool catalogRequest = (bool)practiceIntent.Invoke(null, new object[]
        {
            "请唱《One Last Kiss》", "从长期曲库演唱", ""
        });
        if (!userPractice || !reasonPractice || !orderPractice || catalogRequest)
            throw new InvalidOperationException("song_sing → hum_back practice routing is incorrect.");

        bool sameEnglishTitle = (bool)titleCompatibility.Invoke(
            null, new object[] { "One Last Kiss", "ONE LAST KISS" });
        bool displaySuffix = (bool)titleCompatibility.Invoke(
            null, new object[] { "One Last Kiss", "One Last Kiss（宇多田光）" });
        bool wrongSong = (bool)titleCompatibility.Invoke(
            null, new object[] { "One Last Kiss", "忘记时间" });
        if (!sameEnglishTitle || !displaySuffix || wrongSong)
            throw new InvalidOperationException("Resolved song title identity checks are incorrect.");

        string expectedFromReason = (string)resolveExpectedTitle.Invoke(null, new object[]
        {
            "", "再试一次", "重新尝试演唱《One Last Kiss》的三段练习内容。"
        });
        if (expectedFromReason != "One Last Kiss")
            throw new InvalidOperationException("Explicit title was lost before resolved identity validation.");
    }

    private static void RunAutonomyIntentRegression(Type chatType, object singing)
    {
        MethodInfo bypassMethod = chatType.GetMethod(
            "ShouldBypassAutonomyIntentProbe", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo parseMethod = chatType.GetMethod(
            "TryParseAutonomyIntentDecision", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo resolveWakeMethod = chatType.GetMethod(
            "ResolveScheduledWakeReason", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo contextualMethod = chatType.GetMethod(
            "ClassifyContextualSkillUserIntent", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo resolveReferenceMethod = chatType.GetMethod(
            "ResolveRecentSkillReference", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo extractControlMethod = chatType.GetMethod(
            "ExtractSkillControlTag", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo unknownTagMethod = chatType.GetMethod(
            "DetectUnknownAgentTag", BindingFlags.Static | BindingFlags.NonPublic);
        if (bypassMethod == null || parseMethod == null || resolveWakeMethod == null ||
            contextualMethod == null || resolveReferenceMethod == null ||
            extractControlMethod == null || unknownTagMethod == null)
            throw new MissingMemberException(
                "Autonomy intent internals were renamed without updating the regression suite.");

        bool scheduledBypasses = (bool)bypassMethod.Invoke(null, new object[] { "scheduled" });
        bool toolBypasses = (bool)bypassMethod.Invoke(null, new object[] { "song-search-result" });
        bool skillBypasses = (bool)bypassMethod.Invoke(null, new object[] { "skill-request:singing" });
        if (scheduledBypasses || !toolBypasses || !skillBypasses)
            throw new InvalidOperationException("Autonomy probe bypass routing is incorrect.");

        string preservedToolReason = (string)resolveWakeMethod.Invoke(
            null, new object[] { "song-search-result", "clock" });
        string ordinaryClockReason = (string)resolveWakeMethod.Invoke(
            null, new object[] { "llm-requested", "clock" });
        string surfacedMemoryReason = (string)resolveWakeMethod.Invoke(
            null, new object[] { "default", "memory" });
        if (preservedToolReason != "song-search-result" ||
            ordinaryClockReason != "scheduled" ||
            surfacedMemoryReason != "memory-surfaced")
            throw new InvalidOperationException("Scheduled wake reasons are not preserved correctly.");

        string contextualDisable = contextualMethod.Invoke(
            null, new[] { (object)"那现在关掉吧。", singing }).ToString();
        string contextualStatus = contextualMethod.Invoke(
            null, new[] { (object)"那现在关掉吗？", singing }).ToString();
        if (contextualDisable != "Mention" || contextualStatus != "Mention")
            throw new InvalidOperationException(
                "Contextual Skill routing must load semantics without deciding permission direction.");

        string freshReference = (string)resolveReferenceMethod.Invoke(
            null, new object[] { "singing", 10f, 20f, 180f });
        string expiredReference = (string)resolveReferenceMethod.Invoke(
            null, new object[] { "singing", 10f, 400f, 180f });
        if (freshReference != "singing" || expiredReference != "")
            throw new InvalidOperationException("Recent Skill reference expiry is incorrect.");

        object[] controlArgs =
        {
            "好，我现在关掉它。<skill_control name=\"singing\" action=\"disable\" reason=\"用户含蓄请求\"/>",
        };
        object control = extractControlMethod.Invoke(null, controlArgs);
        if (control == null || controlArgs[0].ToString().Contains("skill_control"))
            throw new InvalidOperationException("skill_control tag was not extracted cleanly.");
        FieldInfo controlName = control.GetType().GetField(
            "Name", BindingFlags.Instance | BindingFlags.Public);
        FieldInfo controlAction = control.GetType().GetField(
            "Action", BindingFlags.Instance | BindingFlags.Public);
        if (controlName == null || controlAction == null ||
            (string)controlName.GetValue(control) != "singing" ||
            (string)controlAction.GetValue(control) != "disable")
            throw new InvalidOperationException("skill_control attributes were parsed incorrectly.");
        string unknown = (string)unknownTagMethod.Invoke(
            null, new object[] { "<skill_control name=\"singing\" action=\"disable\"/>" });
        if (!string.IsNullOrEmpty(unknown))
            throw new InvalidOperationException("skill_control is missing from the known tag registry.");

        object[] validArgs =
        {
            "prefix {\"proceed\":true,\"intent\":\"换个新话题\"," +
            "\"novelty\":\"不是上一句的改写\",\"wait_seconds\":30} suffix",
            null,
        };
        bool valid = (bool)parseMethod.Invoke(null, validArgs);
        if (!valid || validArgs[1] == null)
            throw new InvalidOperationException("Valid autonomy intent JSON was not parsed.");
        FieldInfo proceedField = validArgs[1].GetType().GetField(
            "proceed", BindingFlags.Instance | BindingFlags.Public);
        if (proceedField == null || !(bool)proceedField.GetValue(validArgs[1]))
            throw new InvalidOperationException("Parsed autonomy intent lost proceed=true.");

        object[] invalidArgs = { "{\"intent\":\"missing proceed\"}", null };
        if ((bool)parseMethod.Invoke(null, invalidArgs))
            throw new InvalidOperationException("Autonomy intent JSON without proceed must fail closed to fallback.");
    }
}
