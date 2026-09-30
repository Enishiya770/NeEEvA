using System;
using System.IO;
using System.Reflection;
#if UNITY_EDITOR
using Newtonsoft.Json.Linq;
using UnityEngine;
#endif

/// <summary>Checks the actual bundled prompt projection and provider request,
/// including the custom-prompt fallback. No model request or action is sent.</summary>
public static class FormalPromptProfileRegression
{
    private static int checks;
    private static void Check(bool value, string reason)
    { checks++; if (!value) throw new Exception(reason); }

    public static int RunChecks(string repositoryRoot)
    {
        checks = 0;
        string prompts = Path.Combine(repositoryRoot, "Assets/AIChatTookit/Prompts");
        string behavior = File.ReadAllText(Path.Combine(prompts, "behavior.txt"));
        string language = File.ReadAllText(Path.Combine(prompts, "language.txt"));
        string persona = File.ReadAllText(Path.Combine(prompts, "persona.txt"));
        string singing = File.ReadAllText(Path.Combine(prompts, "Skills/singing.txt"));
        string protocols = Path.Combine(repositoryRoot, "Assets/AIChatTookit/Resources/PromptProtocols");
        string general = File.ReadAllText(Path.Combine(protocols, "general_tools.txt"));
        string singingTools = File.ReadAllText(Path.Combine(protocols, "singing_tools.txt"));
        Check(behavior.Length < 2000 && singing.Length < 2000, "Role/skill principles regained their long tool manuals.");
        Check(!behavior.Contains("<") && !singing.Contains("<"), "Role/skill principles still teach tool markup.");
        string structured = FormalPromptProfile.StructuredRoleSystem(persona, FormalPromptProfile.LanguageRules, behavior);
        Check(structured.Contains(FormalPromptProfile.Normalize(persona)) && structured.Contains(FormalPromptProfile.Normalize(behavior)),
            "Structured role preferences lost authored semantics.");
        foreach (string oldInstruction in new[] { "<say>", "<speech", "<motion", "<sing", "<practice", "stable_id", "trim_head_seconds" })
            Check(!structured.Contains(oldInstruction), "Structured stage inherited legacy authoring syntax: " + oldInstruction);
        foreach (string tool in new[] { "<memory_add", "<memory_update", "<memory_link", "<speaker_manage", "<skill_request", "<skill_control", "<look", "<unlook" })
            Check(general.Contains(tool), "A non-structured tool lost its explicit manual: " + tool);
        foreach (string tool in new[] { "<sing refs=", "<sing_goal", "<clip_confirm", "<clip_revise", "<clip_drop", "<song_catalog", "<song_search", "<song_remember", "<song_rename", "<song_forget", "<hum_back", "<singing_stop", "<singing_policy" })
            Check(singingTools.Contains(tool), "An active Singing compatibility capability was silently removed: " + tool);
        Check(!general.Contains("<silence") && !behavior.Contains("8/20"), "Historical malformed-tag teaching returned to a production prompt.");
        string customized = FormalPromptProfile.StructuredRoleSystem(persona, language,
            behavior + "\nCUSTOM_ROLE_PREFERENCE <say>old protocol example</say>");
        Check(customized.Contains("CUSTOM_ROLE_PREFERENCE") && !customized.Contains("<say>") &&
            customized.Contains(FormalPromptProfile.PreferenceAuthority), "Custom preferences selected an old schema or were silently discarded.");
        Check(FormalPromptProfile.StructuredRoleSystem("", "", null).Contains(FormalPromptProfile.Normalize(FormalPromptProfile.CommonRules)),
            "Missing role assets did not retain default behavior principles.");
        return checks;
    }

#if UNITY_EDITOR
    public static int RunProviderChecks(string repositoryRoot)
    {
        checks = 0;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string prompts = Path.Combine(repositoryRoot, "Assets/AIChatTookit/Prompts");
        var host = new GameObject("prompt-profile-regression");
        host.SetActive(false);
        var persona = new TextAsset(File.ReadAllText(Path.Combine(prompts, "persona.txt"))) { name = "persona" };
        var language = new TextAsset(File.ReadAllText(Path.Combine(prompts, "language.txt"))) { name = "language" };
        var behavior = new TextAsset(File.ReadAllText(Path.Combine(prompts, "behavior.txt"))) { name = "behavior" };
        try
        {
            var provider = host.AddComponent<ChatQW>();
            typeof(LLM).GetField("m_PromptFiles", flags).SetValue(provider, new[] { persona, language, behavior });
            var wire = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildRoleDecisionRequestJson", flags)
                .Invoke(provider, new object[] { "Current facts only.", null }));
            string system = (string)wire["messages"][0]["content"];
            Check(system.Contains(persona.text.Trim()), "RoleDecision lost the actual bundled persona.");
            Check(system.Contains(DecisionCompiler.FastLaneContract), "RoleDecision lost its active JSON protocol.");
            Check(system.Contains(FormalPromptProfile.LanguageRules),
                "RoleDecision lost the actual-language and multilingual quotation rules.");
            Check(!system.Contains("<say>") && !system.Contains("<speech") && !system.Contains("<lang"),
                "RoleDecision persona summary leaked general XML authoring instructions.");
            Check((string)wire["response_format"]["json_schema"]["name"] == "role_decision_v3",
                "RoleDecision changed its native structured schema.");
            Check((int)wire["max_tokens"] == 256 && !(bool)wire["stream"],
                "Prompt cleanup changed fast-lane transport behavior.");

            var chat = host.AddComponent<ChatSample>();
            chat.MotionIntentRequested += _ => { };
            chat.MotionStateContextRequested += () => "CURRENT_BODY_FACT: pose history pending; movement not completed.";
            chat.ConfigureMotionOutput(true, true);
            chat.ConfigureRoomMotionOutput(true);
            string motionMenu = (string)typeof(ChatSample).GetMethod("BuildMotionOutputContract", flags).Invoke(chat, null);
            Check(!string.IsNullOrEmpty(motionMenu) && motionMenu.Contains("<motion"),
                "Regression did not exercise the actual motion authoring contract.");
            string originalContext = (string)typeof(ChatSample).GetMethod("BuildMotionRequestContext", flags)
                .Invoke(chat, new object[] { "CURRENT_OBSERVATION_FACT" }) + SpeechText.OutputContract + "\n\n" + RoleOutputPublicBoundary.OutputContract;
            provider.RecordingDecisionContext = (RecordingSingingDecisionContext)typeof(ChatSample)
                .GetMethod("BuildRecordingSingingDecisionContext", flags).Invoke(chat, null);
            provider.m_DataList.Clear();
            provider.m_DataList.Add(new LLM.SendData("system", provider.BuildSystemPrompt()));
            object[] recordingArgs = { FormalResponseStage.RecordingSingingDecision, originalContext, false, null, 0 };
            var recordingHistory = (System.Collections.Generic.List<LLM.SendData>)typeof(ChatQW)
                .GetMethod("ProjectFormalSource", flags).Invoke(provider, recordingArgs);
            string recordingContext = (string)recordingArgs[1];
            Check((string)recordingArgs[3] == "recording-singing-ir-v1",
                "Bundled recording stage unexpectedly fell back to the legacy planner.");
            Check(!recordingContext.Contains(motionMenu) && !recordingContext.Contains("<motion"),
                "Recording JSON stage retained unrelated motion authoring instructions.");
            Check(!recordingContext.Contains("CURRENT_BODY_FACT") && !recordingContext.Contains("CURRENT_OBSERVATION_FACT") &&
                recordingContext.Contains("current_intent") && recordingContext.Contains("materials"),
                "Recording projection must use its complete route snapshot rather than unrelated generic observations.");
            Check(recordingContext.Contains(RecordingSingingDecisionCompiler.GroundedSourceContract),
                "Recording projection lost its actual field contract.");
            Check(!recordingContext.Contains(SpeechText.OutputContract) && !recordingContext.Contains(RoleOutputPublicBoundary.OutputContract),
                "Recording JSON stage retained the legacy speech envelope.");
            provider.TrailingContext = "legacy-reference-memory-must-not-enter-recording";
            provider.ActiveSkillContext = "<legacy-skill-tool-manual/>";
            provider.SpokenPrefix = "old-promise-must-not-enter-recording";
            var thinkingField = typeof(ChatQW).GetField("m_EnableThinking", flags);
            object originalThinking = thinkingField.GetValue(provider); thinkingField.SetValue(provider, true);
            var recordingWire = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", flags)
                .Invoke(provider, new object[] { recordingHistory, false, recordingContext, null, FormalResponseStage.RecordingSingingDecision }));
            var recordingMessages = (JArray)recordingWire["messages"];
            string recordingPolicy = (string)recordingMessages[0]["content"];
            Check(recordingMessages.Count == 2 && (string)recordingMessages[1]["role"] == "user" &&
                JObject.Parse((string)recordingMessages[1]["content"])["materials"] is JArray &&
                recordingPolicy.IndexOf(RecordingSingingDecisionCompiler.GroundedSourceContract, StringComparison.Ordinal) ==
                    recordingPolicy.LastIndexOf(RecordingSingingDecisionCompiler.GroundedSourceContract, StringComparison.Ordinal) &&
                !recordingWire.ToString().Contains("must-not-enter-recording") && !recordingWire.ToString().Contains("<legacy-skill"),
                "Recording wire must have one domain policy and pure complete facts without legacy overlays or old promises.");
            Check((double)recordingWire["temperature"] == 0 && (int)recordingWire["max_tokens"] == 512 && !(bool)recordingWire["enable_thinking"] &&
                !recordingPolicy.Contains(persona.text.Trim()) && !recordingPolicy.Contains(FormalPromptProfile.LanguageRules),
                "Recording planner must carry deterministic bounded sampling and no persona/language role reenactment.");
            var changedBehavior = new TextAsset(behavior.text + "\nCUSTOM_GENERAL_POLICY_ONLY <say>legacy</say>") { name = "behavior" };
            var promptFilesField = typeof(LLM).GetField("m_PromptFiles", flags);
            string storedSystem = provider.m_DataList[0].content;
            try
            {
                var layouts = new[] {
                    new[] { persona, language, changedBehavior },
                    new[] { persona },
                    new TextAsset[0]
                };
                foreach (var layout in layouts)
                {
                    promptFilesField.SetValue(provider, layout);
                    provider.m_DataList[0].content = "STORED_LEGACY_SYSTEM_DOES_NOT_MATCH_ASSETS <sing/>";
                    object[] independentArgs = { FormalResponseStage.RecordingSingingDecision,
                        "GENERIC_FRAME_WITHOUT_LEGACY_CONTRACT_SUFFIX", false, null, 0 };
                    var independentHistory = (System.Collections.Generic.List<LLM.SendData>)typeof(ChatQW)
                        .GetMethod("ProjectFormalSource", flags).Invoke(provider, independentArgs);
                    Check((string)independentArgs[3] == "recording-singing-ir-v1" && independentHistory.Count == 1 &&
                        (string)independentArgs[1] == recordingContext && !(bool)independentArgs[2],
                        "Recording IR must not depend on old asset count/hash, stored system equality or legacy contract suffix.");
                    var independentWire = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", flags)
                        .Invoke(provider, new object[] { independentHistory, false, independentArgs[1], null,
                            FormalResponseStage.RecordingSingingDecision }));
                    Check(JToken.DeepEquals(independentWire, recordingWire) &&
                        !independentWire.ToString().Contains("CUSTOM_GENERAL_POLICY_ONLY") &&
                        !independentWire.ToString().Contains("STORED_LEGACY_SYSTEM"),
                        "Changing or removing legacy assets must leave the complete dedicated recording wire unchanged.");
                    Check(provider.m_DataList[0].content == "STORED_LEGACY_SYSTEM_DOES_NOT_MATCH_ASSETS <sing/>",
                        "Dedicated recording projection must not rewrite custom source history.");
                }
            }
            finally
            {
                promptFilesField.SetValue(provider, new[] { persona, language, behavior });
                provider.m_DataList[0].content = storedSystem;
                UnityEngine.Object.DestroyImmediate(changedBehavior);
            }
            thinkingField.SetValue(provider, originalThinking);
            provider.TrailingContext = ""; provider.ActiveSkillContext = ""; provider.SpokenPrefix = "";
            object[] generalArgs = { FormalResponseStage.General, originalContext, false, null, 0 };
            typeof(ChatQW).GetMethod("ProjectFormalSource", flags).Invoke(provider, generalArgs);
            Check((string)generalArgs[3] == "explicit-general-tools-v2" && ((string)generalArgs[1]).Contains(motionMenu),
                "The general action planner lost its complete motion manual.");
            Check(provider.m_DataList[0].content == provider.BuildSystemPrompt(),
                "Wire projection rewrote stored system/history content.");
            typeof(LLM).GetField("m_PromptFiles", flags).SetValue(provider, new[] { behavior });
            Check((string)typeof(ChatQW).GetMethod("BuildRoleDecisionPersona", flags).Invoke(provider, null) == "",
                "A missing persona inherited the general tool system.");
            CheckStageOwnedProtocols(provider, persona, language, behavior, flags);
            CheckRecordingBudgetRejection(provider, flags);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(host);
            UnityEngine.Object.DestroyImmediate(persona);
            UnityEngine.Object.DestroyImmediate(language);
            UnityEngine.Object.DestroyImmediate(behavior);
        }
        return checks;
    }

    private static void CheckStageOwnedProtocols(ChatQW provider, TextAsset persona, TextAsset language, TextAsset behavior, BindingFlags flags)
    {
        Check(PromptProtocolAssets.GeneralTools().Contains("<memory_add") &&
            PromptProtocolAssets.SingingTools().Contains("<song_remember"), "The Player-compatible protocol resources did not load.");
        var files = typeof(LLM).GetField("m_PromptFiles", flags);
        var skillFiles = typeof(LLM).GetField("m_SkillFiles", flags);
        object saved = files.GetValue(provider), savedSkills = skillFiles.GetValue(provider);
        string savedActive = provider.ActiveSkillContext;
        var edited = new TextAsset(behavior.text + "\nCUSTOM_ROLE_EDIT_NOW <say>old example</say>") { name = "behavior" };
        var skill = new TextAsset("CUSTOM_SINGING_EDIT_NOW") { name = "singing" };
        try
        {
            foreach (var layout in new[] { new[] { persona, language, edited }, new[] { edited }, new TextAsset[0] })
            {
                files.SetValue(provider, layout);
                provider.m_DataList[0].content = "STALE_OLD_SYSTEM <sing refs=\"old\"/>";
                foreach (var stage in new[] { FormalResponseStage.CommittedSpatialReply, FormalResponseStage.FirstReplyRouting, FormalResponseStage.General })
                {
                    object[] args = { stage, "CURRENT_FACT_WITHOUT_OLD_PROTOCOL_SUFFIX", false, null, 0 };
                    var history = (System.Collections.Generic.List<LLM.SendData>)typeof(ChatQW).GetMethod("ProjectFormalSource", flags).Invoke(provider, args);
                    string system = history[0].content;
                    Check(!system.Contains("STALE_OLD_SYSTEM"), "Asset edits/missing assets reused an old frozen tool prompt.");
                    if (stage == FormalResponseStage.General)
                        Check((string)args[3] == "explicit-general-tools-v2" && system.Contains("<memory_add") && system.Contains("<speaker_manage"),
                            "General lost still-active tools when role assets changed.");
                    else
                    {
                        Check(!(bool)args[2] || (string)args[3] == "committed-speech-ir-v1", "A changed role asset selected a legacy protocol.");
                        Check(!system.Contains("<say>") && !system.Contains("<sing") && !system.Contains("<memory_add"),
                            "Structured system inherited literal legacy tool instructions.");
                        if (layout.Length > 0) Check(system.Contains("CUSTOM_ROLE_EDIT_NOW"), "Structured stage silently ignored a role edit.");
                        if (stage == FormalResponseStage.CommittedSpatialReply)
                            Check((bool)args[2] && ((string)args[1]).Contains(RoleDecisionSpeechStream.Contract),
                                "A missing old suffix demoted the committed reply's schema.");
                    }
                }
                foreach (string builder in new[] { "BuildRoleDecisionRequestJson", "BuildFastLoopRequestJson" })
                {
                    object[] args = builder == "BuildRoleDecisionRequestJson" ? new object[] { "CURRENT_FACT", null }
                        : new object[] { "{}", "CURRENT_USER", null };
                    var wire = JObject.Parse((string)typeof(ChatQW).GetMethod(builder, flags).Invoke(provider, args));
                    string system = (string)wire["messages"][0]["content"];
                    Check(wire["response_format"]["json_schema"] != null && !system.Contains("<say>") && !system.Contains("<sing"),
                        "Role/FastLoop builder leaked a legacy format from a custom layout.");
                    if (layout.Length > 0) Check(system.Contains("CUSTOM_ROLE_EDIT_NOW"), "The actual fast builder lost current role preferences.");
                }
            }
            files.SetValue(provider, new[] { persona, language, behavior });
            skillFiles.SetValue(provider, new[] { skill });
            Check(provider.SetActiveSkills("singing"), "Edited Singing principles did not load.");
            Check(provider.ActiveSkillContext.Contains("CUSTOM_SINGING_EDIT_NOW") && provider.ActiveSkillContext.Contains("<song_remember") &&
                provider.ActiveSkillContext.Contains("<clip_revise"), "A skill edit disabled active compatibility capabilities.");
            var generalHistory = new System.Collections.Generic.List<LLM.SendData> {
                new LLM.SendData("system", provider.BuildSystemPrompt()), new LLM.SendData("user", "CURRENT_USER") };
            var generalWire = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildStageRequestJsonForMessages", flags)
                .Invoke(provider, new object[] { generalHistory, false, "", null, FormalResponseStage.General }));
            Check(generalWire.ToString().Contains("CUSTOM_SINGING_EDIT_NOW") && generalWire.ToString().Contains("<song_remember") &&
                generalWire.ToString().Contains("<memory_add"), "The actual General wire lost edited skill rules or tool interfaces.");
            string scoped = FormalResponseContextPolicy.SkillContext(FormalResponseStage.CommittedSpatialReply, provider.ActiveSkillContext, provider.SkillCatalogContext);
            Check(!scoped.Contains("<sing") && !scoped.Contains("<song_remember"), "General Singing tools leaked into committed speech.");
            var required = typeof(PromptProtocolAssets).GetMethod("Required", BindingFlags.Static | BindingFlags.NonPublic);
            bool rejected = false;
            try { required.Invoke(null, new object[] { "PromptProtocols/missing-regression-contract" }); }
            catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidOperationException && ex.InnerException.Message.Contains("Request construction stopped"); }
            Check(rejected, "Missing protocol assets silently fell back or produced an incomplete tool manual.");
        }
        finally
        {
            files.SetValue(provider, saved); skillFiles.SetValue(provider, savedSkills); provider.ActiveSkillContext = savedActive;
            provider.m_DataList[0].content = provider.BuildSystemPrompt();
            UnityEngine.Object.DestroyImmediate(edited); UnityEngine.Object.DestroyImmediate(skill);
        }
    }

    private static void CheckRecordingBudgetRejection(ChatQW provider, BindingFlags flags)
    {
        string completeUser = new string('证', RecordingSingingDecisionCompiler.MaximumProtectedPromptCharacters + 1) + "完整原话尾部";
        var context = new RecordingSingingDecisionContext { UserText = completeUser, RequireGroundedResolution = true };
        provider.RecordingDecisionContext = context;
        int notices = 0, wires = 0, publicParts = 0, deltaParts = 0, completions = 0;
        var publicText = new System.Text.StringBuilder();
        string completed = null;
        SystemNotice failure = null;
        Action<SystemNotice> onNotice = item => { notices++; failure = item; };
        Action<string> onWire = _ => { wires++; throw new InvalidOperationException("Budget fixture blocked model dispatch before HTTP send."); };
        provider.OnSystemNotice += onNotice;
        provider.OnRequestDiagnostic += onWire;
        int generation = (int)typeof(ChatQW).GetField("m_StreamRequestGeneration", flags).GetValue(provider);
        var routine = (System.Collections.IEnumerator)typeof(ChatQW).GetMethod("RequestStageStreamWithObservation", flags)
            .Invoke(provider, new object[] { completeUser, generation, new Action<string>(_ => deltaParts++),
                new Action<string>(result => { completions++; completed = result; }), false, "", new Action<SpeechText>(part => { publicParts++; publicText.Append(part.Text); }),
                null, null, FormalResponseStage.RecordingSingingDecision });
        try
        {
            // Production now has a nested read-only archive prehook. Advance only
            // managed enumerators; OnRequestDiagnostic throws before SendWebRequest
            // if a budget regression ever attempts a model dispatch.
            var pending = new System.Collections.Generic.Stack<System.Collections.IEnumerator>(); pending.Push(routine);
            int advances = 0;
            while (pending.Count > 0 && ++advances <= 16)
            {
                var current = pending.Peek();
                if (!current.MoveNext()) { (current as IDisposable)?.Dispose(); pending.Pop(); continue; }
                if (current.Current is System.Collections.IEnumerator child) pending.Push(child);
                else throw new Exception("Over-budget evidence reached a non-managed/network yield.");
            }
            Check(pending.Count == 0, "Over-budget evidence did not terminate within the bounded read-only prehook.");
            Check(notices == 1 && failure != null && failure.Code == "recording_context_budget" &&
                failure.Severity == SystemNoticeSeverity.Error && failure.Source == "RecordingSingingDecision" &&
                failure.Message.Contains("超出本次请求预算") && failure.Message.Contains("未提交演唱"),
                "Over-budget recording must publish the explicit runtime failure notice, not fabricate missing audio.");
            var reply = RoleOutputChannels.Parse(completed, RoleOutputBoundaryPolicy.ExplicitPublicSayWithActions);
            Check(wires == 0 && publicParts > 0 && deltaParts == 0 && completions == 1 &&
                !reply.HasActions && !reply.HasInvalidPublicBoundary && !reply.HasInvalidSpeechPhase &&
                publicText.ToString() == reply.Speech && reply.Speech.Contains("演唱还没有开始") &&
                context.UserText == completeUser && provider.RecordingDecisionContext == null,
                "Budget failure must forward exactly its typed no-action explanation once, without truncating evidence or sending a model request.");
        }
        finally
        {
            (routine as IDisposable)?.Dispose();
            provider.OnSystemNotice -= onNotice;
            provider.OnRequestDiagnostic -= onWire;
        }
    }
#endif
}
