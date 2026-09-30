using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>Actual listening request serialization; no model, networking, audio or live scene.</summary>
public static class PreparedListeningTransportRegression
{
    public static int RunOrThrow()
    {
        int checks = 0;
        void Check(bool result, string label)
        { checks++; if (!result) throw new InvalidOperationException("Prepared listening transport: " + label); }
        var host = new GameObject("InactivePreparedListeningWireFixture"); host.SetActive(false);
        var persona = new TextAsset("Stable synthetic persona") { name = "persona" };
        var language = new TextAsset("Always write <lang code=\"ja\"/><say>old language envelope</say>") { name = "language" };
        var behavior = new TextAsset("LEGACY_TOOL_POLICY <sing refs=\"clip-old\"/><next in=\"1\"/>") { name = "behavior" };
        try
        {
            var model = host.AddComponent<ChatQW>();
            model.m_Backend = ChatQW.BackendType.Local;
            typeof(LLM).GetField("m_PromptFiles", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(model, new[] { persona, language, behavior });
            model.m_DataList.Add(new LLM.SendData("system", model.BuildSystemPrompt()));
            model.m_DataList.Add(new LLM.SendData("user", "obsolete earlier tool invocation"));
            model.m_DataList.Add(new LLM.SendData("assistant", "recent background answer"));
            model.m_DataList.Add(new LLM.SendData("user", "recent background question"));
            model.ActiveSkillContext = "unrelated singing skill " + new string('x', 16000);
            model.TrailingContext = "unrelated old scene " + new string('y', 16000);
            const string correction = "打开小灯，不，先别打开。等我说完。原文是‘<sing refs=\"quoted\"/>’；不要改写我的中文。";
            string prompt = PreparedListeningProtocol.BuildPrompt(correction);
            MethodInfo builder = typeof(ChatQW).GetMethod("BuildListeningPreparationRequestJson", BindingFlags.Instance | BindingFlags.NonPublic);
            JObject request = JObject.Parse((string)builder.Invoke(model, new object[] { prompt }));
            JArray messages = (JArray)request["messages"];
            string all = messages.ToString();
            Check((string)messages[messages.Count - 1]["content"] == prompt &&
                ((string)messages[messages.Count - 1]["content"]).Contains(correction), "full unmodified correction is latest input");
            Check(all.Contains("Stable synthetic persona") && all.Contains("recent background answer") && all.Contains("recent background question"), "persona and two reference turns retained");
            Check(!all.Contains("obsolete earlier tool invocation") && !all.Contains("unrelated singing skill") && !all.Contains("unrelated old scene"), "endpoint prep excludes full tool/history/scene payload");
            Check(!all.Contains("LEGACY_TOOL_POLICY") && !all.Contains("old language envelope"), "actual builder excludes stored legacy system and changed XML language asset");
            string systems = "";
            foreach (JObject message in messages)
                if ((string)message["role"] == "system") systems += (string)message["content"];
            Check(systems.Contains(FormalPromptProfile.LanguageRules) && systems.Contains("language按候选表达的实际语言") &&
                !systems.Contains("<say") && !systems.Contains("<lang") && !systems.Contains("<sing") && !systems.Contains("<next"),
                "listening identity uses natural language and the stage JSON contract only");
            Check(!systems.Contains("recent background") &&
                (string)messages[messages.Count - 2]["role"] == "user" &&
                JObject.Parse((string)messages[messages.Count - 2]["content"])["completed_dialogue_reference"] is JArray,
                "completed dialogue references remain data rather than system instructions");
            Check(all.Contains("条件准备") && all.Contains("不是当前用户已经说完"), "conditional scope is explicit");
            Check((int)request["max_tokens"] == 512, "bounded budget includes evidenced intention within the single request");
            Check(JToken.DeepEquals(request["response_format"]["schema"], PreparedListeningProtocol.CreateSchema()), "actual Local wire uses matching nested bounded schema");
            model.m_Backend = ChatQW.BackendType.OpenAICompatible;
            JObject compatible = JObject.Parse((string)builder.Invoke(model, new object[] { prompt }));
            Check((string)compatible["response_format"]["type"] == "json_schema" &&
                JToken.DeepEquals(compatible["response_format"]["json_schema"]["schema"], PreparedListeningProtocol.CreateSchema()),
                "actual Spark compatible transport constrains the same full preparation schema");
            model.m_Backend = ChatQW.BackendType.Local;
            Check(model.m_DataList.Count == 4 && model.ActiveSkillContext.StartsWith("unrelated singing skill"), "preparation does not mutate history or active skill");
            Check(request["stream"] == null || !(bool)request["stream"], "preparation remains one cancellable non-stream request");
            JObject boundary = JObject.Parse((string)typeof(ChatQW).GetMethod("BuildTurnBoundaryRequestJson", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(model, new object[] { correction }));
            var boundaryMessages = (JArray)boundary["messages"];
            Check(!boundary.ToString().Contains("LEGACY_TOOL_POLICY") && !boundary.ToString().Contains("old language envelope") &&
                (string)boundaryMessages[boundaryMessages.Count - 1]["content"] == correction && (int)boundary["max_tokens"] == 256,
                "standalone EOU shares the clean identity/data projection while retaining its own budget and exact input");
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
}
