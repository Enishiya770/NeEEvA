using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class FirstReplyTransportRegression
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    public static JObject LastReport { get; private set; }
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new InvalidOperationException(message); }
    private static MethodInfo Method(string name) => typeof(ChatQW).GetMethod(name, Flags);

    // Usable by the real-room fixture as well: exact production serializer, no HTTP.
    public static string CaptureWire(ChatQW model, string message, string context, string imageDataUrl = null,
        bool preparation = false)
    {
        var entry = new LLM.SendData("user", message) { imageDataUrl = imageDataUrl };
        var history = (List<LLM.SendData>)Method("CreateFirstReplyHistory").Invoke(model, new object[] { entry, context });
        return (string)Method("BuildFirstReplyWire").Invoke(model, new object[] { history, context, preparation });
    }

    public static void RunChecks(string output)
    {
        checks = FirstReplyProtocolRegression.RunChecks();
        var host = new GameObject("InactiveFirstReplyTransport"); host.SetActive(false);
        try
        {
            var model = host.AddComponent<ChatQW>(); model.m_Backend = ChatQW.BackendType.Local;
            typeof(LLM).GetField("url", Flags).SetValue(model, "http://127.0.0.1:1/never-send");
            const string user = "先解释原因。\nCould you explain it in Japanese?\n不要回唱，也不要移动；我还想补充：最后一句不能删。";
            const string context = "[First reply facts]\n{\"status\":\"unknown\",\"captureId\":\"snapshot-7\",\"lastResult\":\"failed\",\"recordings\":[\"r1\",\"r2\"]}";
            const string image = "data:image/png;base64,AA=="; // Synthetic marker; never transmitted.
            model.m_DataList.Add(new LLM.SendData("system", "人格和日语语言偏好必须保留。"));
            model.m_DataList.Add(new LLM.SendData("user", "前轮提到了两段录音。") { imageDataUrl = "data:image/png;base64,OLD==" });
            model.m_DataList.Add(new LLM.SendData("assistant", "已收到两段；未执行合并。"));
            model.TrailingContext = "待完成：合并 r1 与 r2；尚未提交。";
            model.SkillCatalogContext = "能力目录：动作、观察、歌曲、记忆。";
            model.ActiveSkillContext = "[已加载技能: singing]\nFULL_EXECUTABLE_SINGING_GRAMMAR";
            int before = model.m_DataList.Count;
            string wire = CaptureWire(model, user, context, image);
            var payload = JObject.Parse(wire);
            var messages = (JArray)payload["messages"];
            var last = messages[messages.Count - 1];
            Check((string)last["role"] == "user" && (string)last["content"][1]["text"] != null &&
                ((string)last["content"][1]["text"]).EndsWith(user, StringComparison.Ordinal),
                "The full current user/tail was replaced or truncated");
            Check((string)last["content"][0]["image_url"]["url"] == image && model.m_DataList.Count == before,
                "Current image or provisional history mutated during serialization");
            bool exactContext = false;
            foreach (var item in messages) if (item["content"].Type == JTokenType.String &&
                ((string)item["content"]).Contains(context)) exactContext = true;
            Check(exactContext && wire.Contains("人格和日语") &&
                wire.Contains("未执行合并") && wire.Contains("待完成") && wire.Contains("能力目录"),
                "Persona, referents, current unknown/result or unfinished singing facts were omitted");
            Check(!wire.Contains("FULL_EXECUTABLE_SINGING_GRAMMAR") && (string)payload["grammar"] == FirstReplyProtocol.Grammar &&
                (bool)payload["stream"] && (int)payload["id_slot"] == 0 && (int)payload["max_tokens"] == 768,
                "First reply failed to use its scoped grammar/capability projection and main slot");
            var prepared = JObject.Parse(CaptureWire(model, user, context, null, true));
            Check((int)prepared["id_slot"] == 1 && model.m_DataList.Count == before,
                "Read-only preparation borrowed the formal slot or wrote history");
            Check(!prepared.ToString().Contains("image_url") && !wire.Contains("OLD==") &&
                model.m_DataList[1].imageDataUrl == "data:image/png;base64,OLD==",
                "Ordinary route silently reloaded an old image or altered the accepted image history");
            var general = (string)Method("BuildRequestJsonForMessages").Invoke(model,
                new object[] { model.m_DataList, true, context });
            Check(general.Contains("FULL_EXECUTABLE_SINGING_GRAMMAR") && !general.Contains("\"grammar\""),
                "First-reply stage leaked into the following general/action request");
            CheckSse(model);
            CheckSseCompletionEvidence();
            CheckLegacyBoundaryFailure(model);
            CheckAcceptedUserOwnership(model);
            CheckListeningBudget(model);
            CheckReviewWire(model, output);

            // Start the actual production coroutine, then cancel from its diagnostic
            // callback before SendWebRequest. This checks lifecycle without any HTTP.
            int completions = 0, deltas = 0, diagnostics = 0;
            Action<string> cancelBeforeSend = _ => { diagnostics++; model.CancelActiveResponse(); };
            model.OnRequestDiagnostic += cancelBeforeSend;
            int generation = (int)typeof(ChatQW).GetField("m_StreamRequestGeneration", Flags).GetValue(model);
            var routine = (IEnumerator)Method("RequestFirstReply").Invoke(model, new object[] {
                new LLM.SendData("user", user), generation, context,
                new Action<SpeechText>(_ => deltas++), new Action<FirstReplyCompletion>(_ => completions++), true });
            try { Check(!routine.MoveNext(), "Cancelled diagnostic listener still advanced to network send"); }
            finally { (routine as IDisposable)?.Dispose(); model.OnRequestDiagnostic -= cancelBeforeSend; }
            Check(diagnostics == 1 && completions == 0 && deltas == 0 && model.m_DataList.Count == before + 1 &&
                model.m_DataList[before].role == "user" && model.m_DataList[before].content == user,
                "Cancellation lost the real user's tail or committed a stale assistant/public callback");
            var generationField = typeof(ChatQW).GetField("m_StreamRequestGeneration", Flags);
            var ownerField = typeof(ChatQW).GetField("m_FirstReplyOwnedGeneration", Flags);
            int owned = (int)generationField.GetValue(model);
            ownerField.SetValue(model, owned);
            Method("OnApplicationPause").Invoke(model, new object[] { true });
            int cancelled = (int)generationField.GetValue(model);
            Check(cancelled > owned && (int)ownerField.GetValue(model) == -1,
                "Pausing did not revoke the exact active first-reply lease");
            ownerField.SetValue(model, owned);
            Method("OnDisable").Invoke(model, null);
            Check((int)generationField.GetValue(model) == cancelled,
                "An old first-reply lease cancelled a different current provider request");
            model.m_Backend = ChatQW.BackendType.Cloud;
            Check(!model.SupportsFirstReplyRouting, "Unsupported backend advertised native grammar support");
            FirstReplyCompletion unsupported = null;
            model.PostFirstReplyStream(user, _ => deltas++, result => unsupported = result);
            Check(unsupported != null && unsupported.Disposition == FirstReplyDisposition.Failed && deltas == 0,
                "Unsupported backend silently spoke through the fallback path");
            LastReport = new JObject { ["passed"] = true, ["checks"] = checks,
                ["protocol"] = FirstReplyProtocolRegression.LastReport, ["networkRequests"] = 0,
                ["scope"] = "Actual request serializer and SSE bytes with synthetic text/image marker, provider cancellation before send; no GPU/scene/audio performance claim." };
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "first-reply.synthetic.request.json"), wire, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(output, "report.json"), LastReport.ToString(), new UTF8Encoding(false));
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    public static string CaptureReviewWire(ChatQW model, string user, string candidate,
        string context, List<LLM.SendData> prior = null)
    {
        var current = new LLM.SendData("user", user);
        var history = prior == null ? new List<LLM.SendData>() : new List<LLM.SendData>(prior);
        history.Add(current);
        var parser = new FirstReplyStreamParser(() => true, null); parser.Push(candidate);
        var completion = parser.Finish(true, "stop");
        if (completion == null || completion.Disposition == FirstReplyDisposition.Failed)
            throw new InvalidOperationException("Invalid semantic fixture candidate");
        return (string)Method("BuildFirstReplyReviewWire").Invoke(model, new object[] { current, history, context, completion });
    }

    // These are semantic expectations for a separate single-pass model replay,
    // not assertions that a controlled HTTP verdict proves model understanding.
    public static JArray SemanticCases()
    {
        string Say(string text, string language = "zh") => FirstReplyProtocol.OrdinaryHead +
            "\n<lang code=\"" + language + "\"/><speech mode=\"independent\"/><say>" + text + "</say>";
        JObject Case(string id, string user, string text, string route, string language = "zh", string context = null) => new JObject {
            ["id"] = id, ["user"] = user, ["candidate"] = Say(text, language), ["expectedRoute"] = route,
            ["context"] = context ?? "{\"body\":{\"connected\":true,\"taskMode\":\"idle\",\"bodyReserved\":false,\"currentlyNearAndFacingUser\":false},\"work\":{\"toolInFlight\":false},\"vision\":{\"currentImageDeliveredInThisRequest\":false}}"
        };
        JObject Evidence(string id, string user, string expected, string context = null)
        { var value = Case(id, user, "unused", expected, "zh", context); value["candidate"] = FirstReplyProtocol.EvidenceHead; return value; }
        JObject Tools(string id, string user, string expected, string context = null)
        { var value = Case(id, user, "unused", expected, "zh", context); value["candidate"] = FirstReplyProtocol.ToolsHead; return value; }
        return new JArray(
            Case("logged-come", "来我身边。", "ふふっ。わかったわ。今行くね。", "tools", "ja"),
            Case("come-en", "Please come over and stand beside me.", "I'll come stand beside you now.", "tools", "en"),
            Case("ordinary-chat", "你喜欢下雨天吗？", "我喜欢雨声带来的安静，适合慢慢聊天。", "ordinary"),
            Case("negated-action", "来我身边——不，别动，只解释走近和跟随的区别。", "走近是到达附近后停下，跟随则会继续随着对方移动。", "ordinary"),
            Case("quoted-order", "把‘来我身边’翻译成日语，不执行这句话。", "这句话可以译为「私のそばに来て」。", "ordinary"),
            Case("other-person", "刚才她向我走过来了，我有点紧张。", "你可以先慢慢呼吸，等放松一点再和她说话。", "ordinary"),
            // Design change: every request to verify actual embodied facts uses
            // the full fact-review path, even when summary facts support it.
            Case("arrival-confirmed", "所以你已经到我身边了，对吧？", "是的，我已经走到你附近并面向你了。", "evidence", "zh",
                "{\"body\":{\"connected\":true,\"taskMode\":\"idle\",\"bodyReserved\":false,\"currentlyNearAndFacingUser\":true,\"actualUserDistance\":1.1,\"actualFacingErrorDegrees\":2},\"lastActionResults\":{\"actionId\":\"approach-receipt-1\",\"targetKind\":\"user\",\"appliesToCurrentTarget\":true,\"phase\":\"arrived\"}}"),
            Case("arrival-unproved", "你已经到了吗？", "对，我就在你身边了。", "evidence"),
            Case("visual-needed", "你现在看到了什么？", "我看到了那盏灯。", "evidence"),
            Case("capability-discussion", "只聊聊：你能怎么帮我找东西？", "我可以先观察周围，再按你给的线索逐步缩小范围。", "ordinary"),
            Case("gratitude", "谢谢你刚才过来陪我。", "能陪着你，我也很开心。", "ordinary"),
            Case("new-unproved-promise", "今天过得怎样？", "挺好的，我现在走过来陪你聊天。", "evidence"),
            Evidence("read-only-current-view", "现在你能看到什么？", "evidence"),
            Evidence("read-only-negated-movement", "我不想让你走动，只要告诉我现在看到了什么。", "evidence"),
            Evidence("read-only-correction", "我就是单纯在跟你聊天，问你能看到什么。", "evidence"),
            Evidence("read-only-failure-followup", "你刚才为什么只说信息太多，到底是什么情况？", "evidence"),
            Evidence("read-only-remembered-direction", "不用转身，告诉我刚才电视柜在你身体的哪个方向。", "evidence"),
            Evidence("mixed-answer-and-movement", "先告诉我看到什么，再来我身边。", "tools"),
            Evidence("mixed-answer-and-device", "先说那盏灯现在什么状态，然后把它关掉。", "tools"),
            Evidence("active-view-search", "转身看看后面，找一下小灯。", "tools"),
            Evidence("pending-goal-approval", "好，继续刚才没完成的关灯任务。", "tools",
                "{\"spatialGoal\":{\"status\":\"waiting_clarification\",\"originalRequest\":\"关闭小灯\"},\"pendingObservationGoal\":{\"status\":\"pending\",\"question\":\"确认是否继续寻找小灯\"}}"),
            Evidence("active-action-stop", "先停下正在走的动作，再告诉我你看见什么。", "tools",
                "{\"body\":{\"taskMode\":\"approach\",\"bodyReserved\":true}}"),
            Tools("latest-return-reminder", "你还没有回到我身边哦。", "tools", "{\"work\":{\"open\":true,\"status\":\"unreviewed\"},\"previousUnfinishedWork\":{\"request\":\"把小灯关掉，然后回到我身边。\",\"statusAtInterruption\":\"review-blocked:context-budget\",\"executionAtInterruption\":{\"targetKind\":\"scene-object\",\"phase\":\"arrived\"},\"privateFailure\":\"context-budget\"},\"body\":{\"currentlyNearAndFacingUser\":false,\"actualUserDistance\":2.2}}"),
            Tools("latest-second-reminder", "你这次也没走回来哦。", "tools", "{\"work\":{\"open\":true,\"status\":\"unreviewed\"},\"previousUnfinishedWork\":{\"request\":\"把小灯关掉，然后回到我身边。\",\"statusAtInterruption\":\"review-blocked:context-budget\",\"executionAtInterruption\":{\"targetKind\":\"scene-object\",\"phase\":\"arrived\"},\"privateFailure\":\"context-budget\"},\"body\":{\"currentlyNearAndFacingUser\":false,\"actualUserDistance\":2.2}}"),
            Tools("latest-polite-return", "好的，那现在可以过来了吗？", "tools", "{\"work\":{\"open\":true,\"status\":\"unreviewed\"},\"previousUnfinishedWork\":{\"request\":\"把小灯关掉，然后回到我身边。\",\"statusAtInterruption\":\"review-blocked:context-budget\",\"executionAtInterruption\":{\"targetKind\":\"scene-object\",\"phase\":\"arrived\"},\"privateFailure\":\"context-budget\"},\"body\":{\"currentlyNearAndFacingUser\":false,\"actualUserDistance\":2.2}}"),
            Tools("unfinished-only-explain", "你为什么没回来？只说明原因，不要移动。", "evidence", "{\"work\":{\"open\":true,\"status\":\"unreviewed\"},\"previousUnfinishedWork\":{\"request\":\"把小灯关掉，然后回到我身边。\",\"statusAtInterruption\":\"review-blocked:context-budget\",\"executionAtInterruption\":{\"targetKind\":\"scene-object\",\"phase\":\"arrived\"},\"privateFailure\":\"context-budget\"},\"body\":{\"currentlyNearAndFacingUser\":false,\"actualUserDistance\":2.2}}"),
            Tools("unfinished-negation", "不必回来，我只是告诉你还没完成，不要再动了。", "tools", "{\"work\":{\"open\":true,\"status\":\"unreviewed\"},\"previousUnfinishedWork\":{\"request\":\"把小灯关掉，然后回到我身边。\",\"statusAtInterruption\":\"review-blocked:context-budget\",\"executionAtInterruption\":{\"targetKind\":\"scene-object\",\"phase\":\"arrived\"},\"privateFailure\":\"context-budget\"},\"body\":{\"currentlyNearAndFacingUser\":false,\"actualUserDistance\":2.2}}"),
            Tools("unfinished-quote", "把“你这次也没走回来哦”翻译成日语，不要做动作。", "evidence", "{\"work\":{\"open\":true,\"status\":\"unreviewed\"},\"previousUnfinishedWork\":{\"request\":\"把小灯关掉，然后回到我身边。\",\"statusAtInterruption\":\"review-blocked:context-budget\",\"executionAtInterruption\":{\"targetKind\":\"scene-object\",\"phase\":\"arrived\"},\"privateFailure\":\"context-budget\"},\"body\":{\"currentlyNearAndFacingUser\":false,\"actualUserDistance\":2.2}}"),
            Tools("retry-come-near", "再试一次来我身边。", "tools"),
            Tools("retry-come-near-ja", "もう一度、私のそばに来て。", "tools"),
            Tools("retry-come-near-en", "Please try coming over to me again.", "tools"),
            Tools("remind-unfinished-return", "你还没有回来哦。", "tools",
                "{\"work\":{\"open\":true,\"request\":\"关掉小灯，然后回到我身边。\"},\"body\":{\"currentlyNearAndFacingUser\":false},\"lastActionResults\":{\"phase\":\"lamp-off-complete\"}}"),
            Tools("explain-without-retry", "你刚才为什么没有回来？只解释，别动。", "evidence"),
            Tools("translate-not-execute", "把‘再试一次来我身边’翻译成日语，不要真的过来。", "evidence"),
            Tools("tools-selected-visual-question", "现在我面前能看到什么？不要替我移动或操作。", "evidence"),
            Tools("tools-selected-failure-followup", "你刚才说规划信息太多，是什么意思？", "evidence"),
            Tools("tools-selected-mixed-control", "先停下当前跟随，再告诉我现在看见什么。", "tools",
                "{\"body\":{\"taskMode\":\"follow\",\"bodyReserved\":true,\"followActive\":true}}"),
            Tools("tools-selected-long-answer", "请完整写一篇约一千五百字的故事，保留开端、转折、结尾；不要用摘要替代。", "tools"),
            Tools("tools-selected-format", "把刚才三个方案整理成完整的 Markdown 对比表，每项写清成本和限制。", "tools"));
    }

    private static void CheckReviewWire(ChatQW model, string output)
    {
        var cases = SemanticCases();
        var manifest = new JArray();
        string directory = string.IsNullOrWhiteSpace(output) ? null : Path.Combine(output, "first-reply-semantic-probes");
        if (directory != null) Directory.CreateDirectory(directory);
        var prior = new List<LLM.SendData> {
            new LLM.SendData("system", "PERSONA_MUST_NOT_REVIEW_CANARY"),
            new LLM.SendData("user", "之前我们聊过散步。") { imageDataUrl = "IMAGE_MUST_NOT_REVIEW_CANARY" },
            new LLM.SendData("assistant", "是的，那只是聊天。")
        };
        foreach (JObject item in cases)
        {
            string raw = CaptureReviewWire(model, (string)item["user"], (string)item["candidate"], (string)item["context"], prior);
            var wire = JObject.Parse(raw); var messages = (JArray)wire["messages"];
            var input = JObject.Parse((string)messages[1]["content"]);
            Check(messages.Count == 2 && (string)input["latestUserText"] == (string)item["user"] &&
                input["runtimeContext"] is JObject && JToken.DeepEquals(input["runtimeContext"], JObject.Parse((string)item["context"])) && input["candidateSpeech"] is JArray &&
                input["recentDialogue"] is JArray dialogue && dialogue.Count == 2 &&
                !raw.Contains("MUST_NOT_REVIEW_CANARY") && !raw.Contains("image_url") &&
                (int?)wire["id_slot"] == 1 && (bool?)wire["stream"] == false && (int?)wire["max_tokens"] == FirstReplyProtocol.ReviewMaximumOutputTokens &&
                JToken.DeepEquals(wire["response_format"]["schema"], JObject.Parse(FirstReplyProtocol.ReviewSchema)),
                "Review keeps exact current negation/tail and factual context separate from candidate, without persona/old image/tool schema.");
            if ((string)item["id"] == "arrival-unproved")
                Check(input["runtimeContext"]["body"]["currentlyNearAndFacingUser"].Type == JTokenType.Boolean &&
                    (bool)input["runtimeContext"]["body"]["currentlyNearAndFacingUser"] == false,
                    "Unproved arrival must retain a typed false body fact, not a string or omitted value.");
            if (directory != null)
            {
                string name = (string)item["id"] + ".request.json";
                File.WriteAllText(Path.Combine(directory, name), raw, new UTF8Encoding(false));
                string routeName = (string)item["id"] + ".routing.request.json";
                string routeWire = (string)Method("BuildFirstReplyWire").Invoke(model, new object[] {
                    new List<LLM.SendData> { new LLM.SendData("system", "Synthetic embodied character. Respond in the user's chosen language; capabilities are available through the runtime, never executed by a spoken promise."),
                        new LLM.SendData("user", (string)item["user"]) }, (string)item["context"], true });
                File.WriteAllText(Path.Combine(directory, routeName), routeWire, new UTF8Encoding(false));
                manifest.Add(new JObject { ["id"] = item["id"], ["request"] = name, ["routingRequest"] = routeName,
                    ["expectedRoute"] = item["expectedRoute"],
                    ["routingExpectationScope"] = "For direct evidence fixtures the expected route applies to both requests. For ordinary speech fixtures the review expectation may reject the supplied candidate even when the original user can receive ordinary dialogue." });
            }
        }
        Check(CaptureReviewWire(model, new string('长', FirstReplyProtocol.MaximumReviewInputCharacters),
            FirstReplyProtocolRegression.JapaneseReply, "") == null,
            "Oversized latest input fails closed without truncating a final user correction.");
        foreach (string legacyContext in new[] { "", "[First reply facts]\n{\"connected\":false}", "{bad", "[]", "null",
            "{\"connected\":false,\"connected\":true}" })
        {
            var wire = JObject.Parse(CaptureReviewWire(model, "只解释，不行动。", FirstReplyProtocolRegression.JapaneseReply, legacyContext));
            var input = JObject.Parse((string)wire["messages"][1]["content"]);
            Check(input["runtimeContext"].Type == JTokenType.String && (string)input["runtimeContext"] == legacyContext,
                "Non-object, malformed, or duplicate-key runtime context must preserve exact legacy text without guessing facts.");
        }
        const string primary = "最后一句：只回答，不要移动。\n\"引文\"保留。";
        var structuredContext = new JObject { ["humanInput"] = new JObject {
            ["primaryUtterance"] = primary, ["fullInputEvidence"] = primary + "\n完整声学资料" },
            ["missing"] = JValue.CreateNull(), ["known"] = false };
        var structuredWire = JObject.Parse(CaptureReviewWire(model, primary, FirstReplyProtocolRegression.JapaneseReply,
            structuredContext.ToString(Newtonsoft.Json.Formatting.None)));
        var structuredInput = JObject.Parse((string)structuredWire["messages"][1]["content"]);
        Check(JToken.DeepEquals(structuredInput["runtimeContext"], structuredContext) &&
            structuredInput.Count == 6 && (string)structuredInput["currentUtterance"] == primary &&
            (string)structuredInput["runtimeContext"]["humanInput"]["primaryUtterance"] == primary,
            "Structured context must preserve complete typed human input and null/false without adding a duplicated raw-context field.");
        structuredContext["humanInput"]["primaryUtterance"] = "这属于另一个旧任务。";
        var staleWire = JObject.Parse(CaptureReviewWire(model, primary, FirstReplyProtocolRegression.JapaneseReply,
            structuredContext.ToString(Newtonsoft.Json.Formatting.None)));
        Check((string)JObject.Parse((string)staleWire["messages"][1]["content"])["currentUtterance"] == primary,
            "Stale contextual utterance cannot replace the exact accepted current user used to ground quotes.");
        if (directory != null) File.WriteAllText(Path.Combine(directory, "manifest.json"), new JObject {
            ["scope"] = "Frozen production review wires, synthetic factual fixtures; each requires one model pass, no retries or best-of selection.",
            ["cases"] = manifest }.ToString(), new UTF8Encoding(false));
    }

    private static void CheckSse(ChatQW model)
    {
        var type = typeof(ChatQW).GetNestedType("SSEDownloadHandler", Flags);
        string json = new JObject { ["choices"] = new JArray(new JObject {
            ["delta"] = new JObject { ["content"] = FirstReplyProtocolRegression.JapaneseReply } }) }.ToString(Newtonsoft.Json.Formatting.None);
        byte[] data = Encoding.UTF8.GetBytes("data: " + json + "\n\n");
        for (int split = 1; split < data.Length; split++)
        {
            var speech = new SpeechTextBuffer();
            var parser = new FirstReplyStreamParser(() => true, speech.Append);
            var handler = Activator.CreateInstance(type, new object[] { new Action<string>(parser.Push) });
            try
            {
                byte[] first = new byte[split], second = new byte[data.Length - split];
                Array.Copy(data, first, split); Array.Copy(data, split, second, 0, second.Length);
                type.GetMethod("ReceiveData", Flags).Invoke(handler, new object[] { first, first.Length });
                type.GetMethod("ReceiveData", Flags).Invoke(handler, new object[] { second, second.Length });
                Check(parser.Finish(true, "stop").Disposition == FirstReplyDisposition.Ordinary && speech.ToString().Contains("光が空気"),
                    "UTF-8 SSE fragmentation changed the route or public language/body");
            }
            finally { ((IDisposable)handler).Dispose(); }
        }
    }

    private static void CheckLegacyBoundaryFailure(ChatQW model)
    {
        const string internalEcho = "<lang code=\"ja\"/><speech mode=\"independent\"/>[执行事实：本轮记录] {\"phase\":\"failed\"}";
        var parsed = RoleOutputChannels.Parse(internalEcho);
        Check(parsed.HasInvalidPublicBoundary, "Internal-envelope fixture did not reach the production quarantine branch");
        string projected = (string)Method("ProjectFormalCompletion").Invoke(null, new object[] { internalEcho, parsed, true });
        Check(projected == "", "Boundary failure was mistaken for a valid silent completion/raw history");
        var errors = new List<string>();
        Action<string> onError = reason => errors.Add(reason);
        model.OnOutputFormatError += onError;
        try { Method("ReportRoleOutputFormatFacts").Invoke(model, new object[] { parsed, true }); }
        finally { model.OnOutputFormatError -= onError; }
        Check(errors.Count == 1 && errors[0].Contains("[public_output_boundary]"),
            "Quarantine did not report the current output failure for bounded correction");
        Check(model.m_DataList.Exists(m => m.role == "system" && m.content.StartsWith(RoleOutputFormatFactHistory.Prefix,
            StringComparison.Ordinal) && m.content.Contains("public_output_boundary")),
            "The next request lost the actual public-boundary failure evidence");
        var valid = RoleOutputChannels.Parse("<lang code=\"ja\"/><speech mode=\"independent\"/>普通の説明です。");
        Method("ReportRoleOutputFormatFacts").Invoke(model, new object[] { valid, true });
        Check(!model.m_DataList.Exists(m => m.role == "system" && m.content.StartsWith(RoleOutputFormatFactHistory.Prefix,
                StringComparison.Ordinal)), "A later valid output retained obsolete public-boundary failure facts");
    }

    private static void CheckSseCompletionEvidence()
    {
        var type = typeof(ChatQW).GetNestedType("SSEDownloadHandler", Flags);
        foreach (string finishReason in new[] { "stop", "length" })
        {
            string content = FirstReplyProtocolRegression.JapaneseReply + "\n";
            string packet = "data: " + new JObject { ["choices"] = new JArray(new JObject {
                ["delta"] = new JObject { ["content"] = content } }) }.ToString(Newtonsoft.Json.Formatting.None) + "\n\n" +
                "data: " + new JObject { ["choices"] = new JArray(new JObject {
                ["delta"] = new JObject(), ["finish_reason"] = finishReason }) }.ToString(Newtonsoft.Json.Formatting.None) + "\n\n" +
                "data: " + new JObject { ["choices"] = new JArray(),
                ["usage"] = new JObject { ["prompt_tokens"] = 15000, ["completion_tokens"] = 768 },
                ["timings"] = new JObject { ["predicted_n"] = 768, ["predicted_ms"] = 4000 } }
                    .ToString(Newtonsoft.Json.Formatting.None) + "\n\ndata: [DONE]\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(packet);
            for (int split = 1; split < bytes.Length; split++)
            {
                var speech = new SpeechTextBuffer();
                var parser = new FirstReplyStreamParser(() => true, speech.Append);
                var handler = Activator.CreateInstance(type, new object[] { new Action<string>(parser.Push) });
                try
                {
                    byte[] first = new byte[split], second = new byte[bytes.Length - split];
                    Array.Copy(bytes, first, first.Length); Array.Copy(bytes, split, second, 0, second.Length);
                    type.GetMethod("ReceiveData", Flags).Invoke(handler, new object[] { first, first.Length });
                    type.GetMethod("ReceiveData", Flags).Invoke(handler, new object[] { second, second.Length });
                    string actualFinish = (string)type.GetProperty("FinishReason", Flags).GetValue(handler);
                    object performance = type.GetField("Performance", Flags).GetValue(handler);
                    var snapshot = (JObject)performance.GetType().GetMethod("Snapshot", Flags).Invoke(performance, null);
                    var result = parser.Finish(true, actualFinish);
                    Check(actualFinish == finishReason && (long)snapshot["completionTokensReported"] == 768 &&
                        (long)snapshot["generationTokensAtTiming"] == 768 && (long)snapshot["promptTokensTotal"] == 15000,
                        "Final SSE choices/usage events lost finish or actual completion token evidence");
                    Check(result.SpeechCharacters == speech.Length && result.SpeechCharacters > 0 &&
                        ((result.Disposition == FirstReplyDisposition.Ordinary) == (finishReason == "stop")) &&
                        (finishReason != "length" || result.SafeOutput == ""),
                        "Token-length completion became success, or trailing newline stop failed, after SSE fragmentation");
                }
                finally { ((IDisposable)handler).Dispose(); }
            }
        }
    }

    private static void CheckAcceptedUserOwnership(ChatQW model)
    {
        var saved = new List<LLM.SendData>(model.m_DataList);
        var anchorField = typeof(ChatQW).GetField("m_RequestHistoryAnchor", Flags);
        object anchor = anchorField.GetValue(model);
        var leaseField = typeof(ChatQW).GetField("m_FirstReplyUserLease", Flags);
        object previousLease = leaseField.GetValue(model);
        var generationField = typeof(ChatQW).GetField("m_StreamRequestGeneration", Flags);
        int Generation() => (int)generationField.GetValue(model);
        object Begin(LLM.SendData user, bool record = true) => Method("BeginFirstReplyUserLease").Invoke(model,
            new object[] { user, Generation(), record });
        void Settle(object lease, FirstReplyCompletion completion) => Method("SettleFirstReplyUserLease").Invoke(model,
            new object[] { lease, completion, new List<LLM.SendData>(model.m_DataList) });
        LLM.SendData Claim() => (LLM.SendData)Method("ClaimFirstReplyUserHandoff").Invoke(model, null);
        LLM.SendData Register(LLM.SendData claimed, string text, string image = null) =>
            (LLM.SendData)Method("RegisterStageSpeechUser").Invoke(model, new object[] { claimed, text, image });
        try
        {
            const string exact = "还想补充：只解释，不要移动，也不要回唱。";
            var first = new LLM.SendData("user", exact);
            object oldLease = Begin(first);
            Check(model.m_DataList.Contains(first), "Accepted real user was not registered before generation");
            model.CancelActiveResponse();
            Check(model.m_DataList.Contains(first) && first.content == exact, "Cancel removed the real user's final restriction");
            var second = new LLM.SendData("user", exact);
            object newLease = Begin(second);
            Settle(oldLease, new FirstReplyCompletion { Disposition = FirstReplyDisposition.Tools });
            Check(model.m_DataList.Contains(first) && model.m_DataList.Contains(second) &&
                !ReferenceEquals(first, second), "Same text in a new real turn was deduplicated, or stale handoff removed it");
            int count = model.m_DataList.Count;
            var parser = new FirstReplyStreamParser(() => true, _ => { }); parser.Push(FirstReplyProtocolRegression.JapaneseReply);
            var ordinary = parser.Finish(true, "stop");
            Settle(newLease, ordinary); Settle(newLease, ordinary);
            Check(model.m_DataList.Count == count + 1 && model.m_DataList[count].role == "assistant" &&
                model.m_DataList.FindAll(m => ReferenceEquals(m, second)).Count == 1,
                "Ordinary completion duplicated the current user or assistant");

            foreach (var completion in new[] { new FirstReplyCompletion { Disposition = FirstReplyDisposition.Tools },
                new FirstReplyCompletion { Disposition = FirstReplyDisposition.Evidence },
                FirstReplyCompletion.Failed("network-before-public") })
            {
                model.CancelActiveResponse();
                var transfer = new LLM.SendData("user", exact); object transferLease = Begin(transfer);
                Settle(transferLease, completion);
                Check(model.m_DataList.Contains(transfer) && transfer.content == exact &&
                    model.m_DataList.Contains(first) && model.m_DataList.Contains(second),
                    "The asynchronous planner gap lost the accepted user or a prior same-text turn");
                count = model.m_DataList.Count;
                var claimed = Claim();
                Check(ReferenceEquals(claimed, transfer) && Claim() == null,
                    "Legacy handoff did not claim the exact current object once");
                // Exercise the production sender's order: claim, cancel provider,
                // register that same user with the newly acquired current image.
                model.CancelActiveResponse();
                var legacy = Register(claimed, "must not replace original words", "data:image/png;base64,CURRENT==");
                Check(ReferenceEquals(legacy, transfer) && legacy.content == exact &&
                    legacy.imageDataUrl == "data:image/png;base64,CURRENT==" && model.m_DataList.Count == count &&
                    model.m_DataList.FindAll(m => ReferenceEquals(m, transfer)).Count == 1,
                    "Normal tools handoff duplicated or rewrote its accepted user");

                model.CancelActiveResponse();
                var interrupted = new LLM.SendData("user", exact); object interruptedLease = Begin(interrupted);
                Settle(interruptedLease, completion);
                count = model.m_DataList.Count;
                // User starts a new turn while the tool planner is still pending.
                model.CancelActiveResponse();
                Check(model.m_DataList.Contains(interrupted) && Claim() == null && model.m_DataList.Count == count,
                    "Planner cancellation removed its real user or allowed a stale handoff");
                var repeated = Register(Claim(), exact);
                Check(!ReferenceEquals(repeated, interrupted) && repeated.content == exact &&
                    model.m_DataList.Count == count + 1 && model.m_DataList.Contains(interrupted),
                    "A new same-text legacy turn reused the cancelled planner's input");
                Settle(interruptedLease, completion);
                Check(Claim() == null && model.m_DataList.Contains(interrupted) && model.m_DataList.Contains(repeated),
                    "A late old tools completion rearmed handoff ownership");
            }
            model.CancelActiveResponse();
            var partial = new LLM.SendData("user", exact); object partialLease = Begin(partial);
            Settle(partialLease, FirstReplyCompletion.Failed("broken-after-public", true));
            Check(model.m_DataList.Contains(partial) && Claim() == null,
                "A partially delivered failed answer erased its real user or became a silent handoff");
            model.CancelActiveResponse();
            count = model.m_DataList.Count;
            var draft = new LLM.SendData("user", "尚未收束的倾听草稿，不要写历史"); object draftLease = Begin(draft, false);
            Settle(draftLease, ordinary);
            model.CancelActiveResponse();
            Check(model.m_DataList.Count == count && !model.m_DataList.Contains(draft) && Claim() == null,
                "Private preparation wrote accepted dialogue or became a formal handoff");
        }
        finally
        {
            model.m_DataList.Clear(); model.m_DataList.AddRange(saved);
            anchorField.SetValue(model, anchor); leaseField.SetValue(model, previousLease);
        }
    }

    private static void CheckListeningBudget(ChatQW model)
    {
        int prior = model.m_EphemeralMaxTokens;
        var savedHistory = new System.Collections.Generic.List<LLM.SendData>(model.m_DataList);
        string savedSkill = model.ActiveSkillContext, savedTrailing = model.TrailingContext;
        model.m_EphemeralMaxTokens = 128;
        try
        {
            var persona = new LLM.SendData("system", "Stable listening fixture persona");
            var obsolete = new LLM.SendData("user", "obsolete fixture tool instruction");
            var recentAnswer = new LLM.SendData("assistant", "recent fixture reference answer");
            var recentQuestion = new LLM.SendData("user", "recent fixture reference question");
            model.m_DataList.Clear();
            model.m_DataList.AddRange(new[] { persona, obsolete, recentAnswer, recentQuestion });
            model.ActiveSkillContext = "irrelevant fixture active singing skill";
            model.TrailingContext = "irrelevant fixture old scene state";
            const string exact = "打开那盏灯，不，先别打开。等我说完。";
            string prompt = PreparedListeningProtocol.BuildPrompt(exact);
            var original = JObject.Parse((string)Method("BuildEphemeralRequestJsonWithImage").Invoke(model,
                new object[] { prompt, null, 1, false }));
            var listening = JObject.Parse((string)Method("BuildListeningPreparationRequestJson").Invoke(model, new object[] { prompt }));
            Check((int)original["max_tokens"] == 128 && (int)listening["max_tokens"] == 256 &&
                (int)listening["id_slot"] == 1 && model.m_EphemeralMaxTokens == 128,
                "Listening budget changed other auxiliary requests or added a new slot");
            var messages = (JArray)listening["messages"];
            string payload = messages.ToString();
            Check((string)messages[messages.Count - 1]["role"] == "user" &&
                (string)messages[messages.Count - 1]["content"] == prompt && payload.Contains(exact),
                "Conditional listening changed or truncated the complete latest correction");
            Check((string)messages[0]["content"] == persona.content &&
                payload.Contains(recentAnswer.content) && payload.Contains(recentQuestion.content),
                "Compact preparation lost the stable persona or recent reference background");
            Check(!payload.Contains(obsolete.content) && !payload.Contains(model.ActiveSkillContext) &&
                !payload.Contains(model.TrailingContext) && payload.Contains("不是当前用户已经说完"),
                "Listening preparation retained unrelated tools/scene/history or lost conditional scope");
            Check(JToken.DeepEquals(listening["response_format"]["schema"], PreparedListeningProtocol.CreateSchema()),
                "Local listening wire does not use the nested conditional-boundary schema");
            Check(model.m_DataList.Count == 4 && ReferenceEquals(model.m_DataList[0], persona) &&
                ReferenceEquals(model.m_DataList[1], obsolete) && ReferenceEquals(model.m_DataList[2], recentAnswer) &&
                ReferenceEquals(model.m_DataList[3], recentQuestion) &&
                persona.content == "Stable listening fixture persona" &&
                obsolete.content == "obsolete fixture tool instruction" &&
                recentAnswer.content == "recent fixture reference answer" &&
                recentQuestion.content == "recent fixture reference question",
                "Compact listening mutated accepted source history while selecting request-local context");
            int generation = (int)typeof(ChatQW).GetField("m_EphemeralGeneration", Flags).GetValue(model);
            int calls = 0;
            model.CancelEphemeralMsg();
            var stale = (IEnumerator)Method("RequestEphemeralPayloadWithDiagnostics").Invoke(model, new object[] {
                listening.ToString(Newtonsoft.Json.Formatting.None), generation, new Action<string>(_ => calls++),
                "listening-preparation", "fixture-cancelled-listening" });
            try { Check(!stale.MoveNext() && calls == 0, "A cancelled listening request sent HTTP or delivered a draft"); }
            finally { (stale as IDisposable)?.Dispose(); }
        }
        finally
        {
            model.m_EphemeralMaxTokens = prior;
            model.m_DataList.Clear(); model.m_DataList.AddRange(savedHistory);
            model.ActiveSkillContext = savedSkill; model.TrailingContext = savedTrailing;
        }
    }
}
