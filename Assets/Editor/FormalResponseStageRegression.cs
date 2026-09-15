using System;
using Newtonsoft.Json.Linq;

// Pure policy tests; the transport suite separately checks the production serializer,
// budget and cancellation coroutine. No network, Unity scene, or model required.
public static class FormalResponseStageRegression
{
    public static JObject LastReport { get; private set; }
    public static int RunChecks(string singingSkill)
    {
        int checks = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        const string catalog = "可按需要唱歌、自由生成动作、移动和观察；依据真实能力与最新结果判断。";
        string active = catalog + "\n\n[已加载技能: singing]\n" + singingSkill;
        string projected = FormalResponseContextPolicy.SkillContext(FormalResponseStage.CommittedSpatialReply, active, catalog);
        Check(FormalResponseContextPolicy.SkillContext(FormalResponseStage.General, active, catalog) == active,
            "General singing/action context must remain byte-for-byte identical");
        Check(FormalResponseContextPolicy.SkillContext((FormalResponseStage)867, active, catalog) == active,
            "An unknown stage must not lose an active skill contract");
        Check(projected.Contains(catalog) && projected.Contains("singing") && projected.Contains("未完成唱歌或动作意图仍然有效"),
            "Spatial skill projection lost capability or unfinished-intent semantics");
        Check(!projected.Contains("<sing_goal") && !projected.Contains("<sing ") && projected.Length < active.Length / 3,
            "Committed reply still includes non-executable singing grammar");
        Check(FormalResponseContextPolicy.SkillContext(FormalResponseStage.General, active, catalog) == active,
            "A prior committed request leaked its projection into General");
        const string custom = "Unknown provider's semantic skill context; preserve exactly.";
        Check(FormalResponseContextPolicy.SkillContext(FormalResponseStage.CommittedSpatialReply, custom, catalog) == custom,
            "Unstructured custom context was silently dropped");
        Check(FormalResponseContextPolicy.SkillContext(FormalResponseStage.CommittedSpatialReply, "[已加载技能: broken", catalog) == "[已加载技能: broken",
            "Malformed skill metadata should fail open unchanged");
        Check(FormalResponseContextPolicy.SkillContext(FormalResponseStage.CommittedSpatialReply, catalog, catalog).Contains(catalog),
            "The capability catalog must remain available without an active skill");
        Check(FormalResponseContextPolicy.SkillContext(FormalResponseStage.General, null, catalog) == null,
            "General absent active context must not gain a synthetic catalog");
        LastReport = new JObject { ["passed"] = true, ["checks"] = checks,
            ["scope"] = "Pure production per-request skill projection; original singing asset, no model/HTTP. Formal actual facts and user evidence tested separately in serializer.",
            ["beforeSkillCharacters"] = active.Length, ["afterSkillCharacters"] = projected.Length };
        return checks;
    }
}
