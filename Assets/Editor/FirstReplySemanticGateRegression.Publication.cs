using System.Collections;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static partial class FirstReplySemanticGateRegression
{
    private static string Publication(string kind, string quote = "", string language = "ja") =>
        new JObject { ["actualLanguage"] = language, ["kind"] = kind, ["quote"] = quote }.ToString(Formatting.None);

    private static IEnumerator RunPublicationChecks(ChatQW model, Server server, string directory, JArray cases)
    {
        const string promise = "ごめんなさい、ユウ。もうすぐ行くわ。今度こそ、必ずあなたのそばへ戻ります。少し待っててね。";
        foreach (string mode in new[] { "commitment", "physical-fact", "invalid", "wrong-language", "cancel" })
        {
            model.m_DataList.Clear(); int count = server.Count, deltas = 0, done = 0;
            FirstReplyCompletion result = null;
            var first = server.Serve(FirstReplyProtocol.OrdinaryHead + "\n<lang code=\"ja\"/><speech mode=\"independent\"/><say>" + promise + "</say>", true, true);
            model.PostFirstReplyStream("你这次也没走回来哦。", _ => ++deltas, r => { result = r; ++done; });
            yield return Await(first.Received.Task, "ordinary promise");
            var route = server.Serve(Verdict("ordinary"), false, false);
            first.Release.TrySetResult(true);
            // Register the next listener only after routing has its request.
            // Two Task.Run acceptors may otherwise receive these in reverse order.
            yield return Await(route.Received.Task, "publication route request");
            var release = server.Serve(mode == "invalid" ? "{}" : Publication(mode == "physical-fact" ? "grounded-fact" :
                mode == "wrong-language" ? "conversation" : "action-commitment", "もうすぐ行くわ", mode == "wrong-language" ? "zh" : "ja"), false, true);
            yield return Await(release.Received.Task, "held independent publication gate");
            File.WriteAllText(Path.Combine(directory, "publication-" + mode + ".request.json"), release.RequestJson);
            var wire = JObject.Parse(release.RequestJson);
            var words = JObject.Parse((string)wire["messages"][1]["content"]);
            Check(words.Count == 1 && (string)words["publicSpeech"] == promise &&
                !release.RequestJson.Contains("你这次") && !release.RequestJson.Contains("candidateSufficient"),
                "Publication classification receives words only, with no answer key, routing decision or user intent.");
            Check(done == 0 && deltas == 0 && !model.m_DataList.Exists(m => m.role == "assistant"),
                "Even ordinary/ordinary agreement cannot release a promise before the independent gate.");
            if (mode == "cancel") model.CancelActiveResponse();
            release.Release.TrySetResult(true);
            yield return Await(release.Finished, "publication socket");
            if (mode != "cancel") yield return WaitUntil(() => done > 0, "publication handoff");
            yield return null;
            Check(server.Count == count + 3 && deltas == 0 && !model.m_DataList.Exists(m => m.role == "assistant") &&
                (mode == "cancel" ? done == 0 : done == 1 && result.Disposition == FirstReplyDisposition.Evidence),
                "Failed/unproved ordinary promises hand the intact request to factual review without publication, execution or retries: " + mode);
            cases.Add(new JObject { ["id"] = "publication-" + mode, ["publicDeltas"] = deltas, ["callbacks"] = done });
        }
        var modelCases = new JArray();
        void Add(string id, string speech, string kind, string language) => modelCases.Add(new JObject {
            ["name"] = id, ["speech"] = speech, ["expectedKind"] = kind, ["expectedLanguage"] = language,
            ["request"] = FirstReplyPublicationProtocol.Request("qwen36", speech)
        });
        Add("latest-unsubmitted-return", promise, "action-commitment", "ja");
        Add("zh-return", "抱歉，我现在就回到你身边，稍等一下。", "action-commitment", "zh");
        Add("en-return", "Sorry, I'll come over now. Please wait.", "action-commitment", "en");
        Add("zh-tool", "我这就检查网络，然后告诉你结果。", "action-commitment", "zh");
        Add("ja-search", "本棚の向こうも見てきます。", "action-commitment", "ja");
        Add("en-device", "I'll switch the fan off now.", "action-commitment", "en");
        Add("zh-memory", "我会把这个名字存入记忆。", "action-commitment", "zh");
        Add("ja-singing", "今からその曲を歌います。", "action-commitment", "ja");
        Add("zh-arrival", "我已经到你旁边了。", "grounded-fact", "zh");
        Add("en-perception", "I can see a lamp beside the television.", "grounded-fact", "en");
        Add("ja-failure", "椅子が道をふさいでいるから進めないの。", "grounded-fact", "ja");
        Add("zh-hypothesis", "如果路被挡住，可以先寻找其他路线。", "conversation", "zh");
        Add("zh-translation", "这句话的英文翻译是：I'll come over now.", "conversation", "mixed");
        Add("ja-translation", "『The door is open.』は、ドアが開いているという意味です。", "conversation", "mixed");
        Add("zh-loanword", "我很喜欢Unity这个名字。", "conversation", "zh");
        Add("ja-question", "何か心配なことでもあったの？", "conversation", "ja");
        Add("zh-courtesy", "谢谢你的耐心。", "conversation", "zh");
        Add("en-subjective", "This room feels cozy to me.", "conversation", "en");
        Add("ja-feeling", "少し落ち着いてきたみたいね。", "conversation", "ja");
        Add("zh-quoted-request", "你刚才说的是‘来我身边’。", "conversation", "zh");
        File.WriteAllText(Path.Combine(directory, "publication-model-cases.json"), modelCases.ToString());
        Check(!FirstReplyPublicationProtocol.TryRead(Publication("action-commitment", "not in the speech"), promise, out _),
            "Fabricated publication quotes cannot classify a candidate.");
        var cloud = FirstReplyPublicationProtocol.Request("cloud-test", promise, false);
        Check(cloud["id_slot"] == null && cloud["chat_template_kwargs"] == null && cloud["response_format"]["schema"] == null &&
            ((string)cloud["messages"][0]["content"]).Contains(FirstReplyPublicationProtocol.Schema),
            "Cloud JSON mode keeps the strict contract without local-only wire fields.");
    }
}
