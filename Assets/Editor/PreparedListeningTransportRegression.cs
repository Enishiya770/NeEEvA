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
        try
        {
            var model = host.AddComponent<ChatQW>();
            model.m_Backend = ChatQW.BackendType.Local;
            model.m_DataList.Add(new LLM.SendData("system", "Stable synthetic persona"));
            model.m_DataList.Add(new LLM.SendData("user", "obsolete earlier tool invocation"));
            model.m_DataList.Add(new LLM.SendData("assistant", "recent background answer"));
            model.m_DataList.Add(new LLM.SendData("user", "recent background question"));
            model.ActiveSkillContext = "unrelated singing skill " + new string('x', 16000);
            model.TrailingContext = "unrelated old scene " + new string('y', 16000);
            const string correction = "打开小灯，不，先别打开。等我说完。";
            string prompt = PreparedListeningProtocol.BuildPrompt(correction);
            MethodInfo builder = typeof(ChatQW).GetMethod("BuildListeningPreparationRequestJson", BindingFlags.Instance | BindingFlags.NonPublic);
            JObject request = JObject.Parse((string)builder.Invoke(model, new object[] { prompt }));
            JArray messages = (JArray)request["messages"];
            string all = messages.ToString();
            Check((string)messages[messages.Count - 1]["content"] == prompt && all.Contains(correction), "full unmodified correction is latest input");
            Check(all.Contains("Stable synthetic persona") && all.Contains("recent background answer") && all.Contains("recent background question"), "persona and two reference turns retained");
            Check(!all.Contains("obsolete earlier tool invocation") && !all.Contains("unrelated singing skill") && !all.Contains("unrelated old scene"), "endpoint prep excludes full tool/history/scene payload");
            Check(all.Contains("条件准备") && all.Contains("不是当前用户已经说完"), "conditional scope is explicit");
            Check((int)request["max_tokens"] == 256, "no blind budget increase");
            Check(JToken.DeepEquals(request["response_format"]["schema"], PreparedListeningProtocol.CreateSchema()), "actual Local wire uses matching nested bounded schema");
            Check(model.m_DataList.Count == 4 && model.ActiveSkillContext.StartsWith("unrelated singing skill"), "preparation does not mutate history or active skill");
            Check(request["stream"] == null || !(bool)request["stream"], "preparation remains one cancellable non-stream request");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        return checks;
    }
}
