using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Actual Qwen wire serialization and provider fallback contracts. No HTTP, model or scene camera.</summary>
public static class SceneVisionTransportRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;
    // Public 1x1 fixture pixels, not a claim about a captured user scene.
    private const string Image = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j1ioAAAAASUVORK5CYII=";
    private const string OldImage = "data:image/png;base64,OLD_PIXEL_CANARY";
    private static int checks;

    public static int RunChecks()
    {
        checks = 0;
        var host = new GameObject("InactiveSceneVisionTransportFixture"); host.SetActive(false);
        try
        {
            var model = host.AddComponent<ChatQW>();
            model.m_LogRequestStats = false;
            model.m_DataList.Clear();
            model.m_DataList.Add(new LLM.SendData("system", "PRIVATE_PERSONA"));
            var prior = new LLM.SendData("user", "PRIVATE_HISTORY: observation=old, captured earlier") { imageDataUrl = OldImage };
            model.m_DataList.Add(prior);
            model.m_DataList.Add(new LLM.SendData("assistant", "PRIVATE_REPLY"));
            model.ActiveSkillContext = "PRIVATE_ACTIVE";
            model.TrailingContext = "PRIVATE_TRAILING";
            model.RequestContext = "PRIVATE_PENDING_FORMAL";
            model.SpokenPrefix = "PRIVATE_SPOKEN";
            Check(model.SupportsVisualDecisionMessages, "Qwen does not advertise visual decision transport.");
            const string prompt = "captureId=scene-17; capturedAt=2026-09-13T00:00:00Z; view=character\n观察 \"茶几\" 与用户。\\literal";
            foreach (ChatQW.BackendType backend in new[] { ChatQW.BackendType.Local, ChatQW.BackendType.Cloud })
            {
                model.m_Backend = backend;
                CheckAuxiliary(model, "BuildEphemeralRequestJsonWithImage", prompt, Image, 1, false);
                CheckAutonomyIntent(model, prompt);
                CheckAuxiliary(model, "BuildWorkReviewRequestJsonWithImage", prompt, Image);
                foreach (bool review in new[] { false, true })
                {
                    string roomInput = review ? new JObject { ["publicSpeech"] = prompt }.ToString(Newtonsoft.Json.Formatting.None) : prompt;
                    JObject request = Build(model, "BuildRoomTaskRequestJsonWithImage", roomInput, Image, review);
                    CheckImage(request, roomInput, review ? -1 : 1);
                    CheckSlot(request, backend, 1);
                    Check(((JArray)request["messages"]).Count == (review ? 2 : 3) && !request.ToString().Contains("PRIVATE_"),
                        "Spatial visual decision inherited private dialogue or shared mutable context.");
                    Check(JToken.DeepEquals(Build(model, "BuildRoomTaskRequestJson", roomInput, review),
                        Build(model, "BuildRoomTaskRequestJsonWithImage", roomInput, null, review)),
                        "Legacy text-only spatial serialization changed.");
                }
                Check(JToken.DeepEquals(Build(model, "BuildEphemeralRequestJson", prompt, 1, false),
                    Build(model, "BuildEphemeralRequestJsonWithImage", prompt, null, 1, false)), "Legacy draft serializer changed.");
                Check(JToken.DeepEquals(Build(model, "BuildWorkReviewRequestJson", prompt),
                    Build(model, "BuildWorkReviewRequestJsonWithImage", prompt, null)), "Legacy work-review serializer changed.");
                CheckContinuation(model, backend, prompt);
                CheckHistory(model, prior);
            }
            CheckRequestIsolation(model, prior);
            CheckCancellation(model);
            CheckLegacyAndUnsupported(host);
            return checks;
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
    }

    private static void CheckAuxiliary(ChatQW model, string method, params object[] args)
    {
        JObject request = Build(model, method, args);
        CheckImage(request, (string)args[0]);
        CheckSlot(request, model.m_Backend, 1);
        Check(!request.ToString().Contains(OldImage) && request.ToString().Contains("历史图像已归档"),
            "Auxiliary history replayed old pixels or omitted their archived status.");
        Check(!request.ToString().Contains("PRIVATE_PENDING_FORMAL") && !request.ToString().Contains("PRIVATE_SPOKEN"),
            "Silent observation consumed a formal-only request context or spoken prefix.");
    }

    private static void CheckAutonomyIntent(ChatQW model, string prompt)
    {
        int oldKeep = model.m_EphemeralHistoryMessages, oldBudget = model.m_EphemeralMaxTokens;
        try
        {
            model.m_EphemeralMaxTokens = 128;
            foreach (int keep in new[] { 0, 1 })
            {
                model.m_EphemeralHistoryMessages = keep;
                JObject generic = Build(model, "BuildEphemeralRequestJsonWithImage", prompt, Image, 1, false);
                JObject autonomy = Build(model, "BuildAutonomyIntentRequestJsonWithImage", prompt, Image);
                CheckImage(autonomy, prompt); CheckSlot(autonomy, model.m_Backend, 1);
                Check(!autonomy.ToString().Contains(OldImage) && !autonomy.ToString().Contains("PRIVATE_PENDING_FORMAL") &&
                    !autonomy.ToString().Contains("PRIVATE_SPOKEN"), "Autonomy reused old pixels or stole formal-only request state.");
                Check((string)autonomy["response_format"]?["type"] == "json_object" &&
                    (int)autonomy["max_tokens"] == 128 && (bool)autonomy["stream"] == false && (bool)autonomy["enable_thinking"] == false,
                    "Autonomy must use structured private output with the unchanged 128-token, non-streaming, no-thinking request policy.");
                var messages = (JArray)autonomy["messages"];
                var scope = messages[messages.Count - 2];
                string scopeText = (string)scope["content"];
                Check((string)scope["role"] == "system" && scopeText.StartsWith("[私有自主意图输出契约]", StringComparison.Ordinal) &&
                    scopeText.Contains("不发声") && scopeText.Contains("不适用于本次") && scopeText.Contains("proceed=false"),
                    "Private autonomy scope must supersede formal speech/tool tag formatting immediately before the current observation.");
                if (keep == 0)
                    foreach (string retained in new[] { "PRIVATE_PERSONA", "PRIVATE_HISTORY", "PRIVATE_REPLY", "PRIVATE_ACTIVE", "PRIVATE_TRAILING" })
                        Check(autonomy.ToString().Contains(retained), "Private autonomy dropped existing role/history/skill/memory: " + retained);
                var normalized = (JObject)autonomy.DeepClone();
                ((JArray)normalized["messages"]).RemoveAt(messages.Count - 2); normalized.Remove("response_format");
                model.m_EphemeralHistoryMessages = 0;
                var fullHistory = Build(model, "BuildEphemeralRequestJsonWithImage", prompt, Image, 1, false);
                model.m_EphemeralHistoryMessages = keep;
                Check(JToken.DeepEquals(normalized, fullHistory),
                    "Unknown autonomy callers retain complete authored history under their separate admission gate; draft keep-N must not drop constraints.");
                Check(generic["response_format"] == null && !generic.ToString().Contains("[私有自主意图输出契约]"),
                    "Generic speculative drafts inherited autonomy-only formatting.");
                if (model.m_Backend == ChatQW.BackendType.Local)
                {
                    var schema = autonomy["response_format"]["schema"] as JObject;
                    var properties = schema?["properties"] as JObject;
                    Check(properties != null && properties.Count == 6 && (bool)schema["additionalProperties"] == false &&
                        JToken.DeepEquals(schema["required"], JArray.Parse("[\"work_evidence\",\"work_status\",\"proceed\",\"intent\",\"novelty\",\"wait_seconds\"]")),
                        "Local autonomy grammar must require exactly the six decision fields.");
                    Check((string)properties["proceed"]["type"] == "boolean" &&
                        (string)properties["wait_seconds"]["type"] == "number" && (int)properties["wait_seconds"]["minimum"] == 0 &&
                        (int)properties["wait_seconds"]["maximum"] == 600 &&
                        JToken.DeepEquals(properties["work_status"]["enum"], JArray.Parse("[\"continue\",\"waiting_tool\",\"waiting_user\",\"closed\",\"none\"]")),
                        "Autonomy grammar lost the bounded recheck time, boolean proceed or supported work states.");
                    Check((string)properties["intent"]["type"] == "string" && properties["intent"]["minLength"] == null &&
                        (string)properties["novelty"]["type"] == "string" && properties["novelty"]["minLength"] == null,
                        "A no-new-intent decision must be allowed to leave intent/novelty empty.");
                }
                else Check(autonomy["response_format"]["schema"] == null && scopeText.Contains("\"required\""),
                    "Cloud autonomy must use JSON-object mode and the same prompt contract without a local-only schema field.");
            }
            // The API decides the contract; user text resembling an autonomy prompt cannot switch a draft.
            var draftWithMarker = Build(model, "BuildEphemeralRequestJsonWithImage", "[内部自主意图预判] quoted user data", null, 1, false);
            Check(draftWithMarker["response_format"] == null && !draftWithMarker.ToString().Contains("[私有自主意图输出契约]"),
                "Autonomy formatting was selected by text-prefix matching instead of the explicit API.");
            var textAutonomy = Build(model, "BuildAutonomyIntentRequestJsonWithImage", prompt, null);
            Check(ImageCount((JArray)textAutonomy["messages"]) == 0 && (string)textAutonomy["response_format"]["type"] == "json_object",
                "Text-only autonomy lost its private structured contract or retained old pixels.");
            var boundary = Build(model, "BuildTurnBoundaryRequestJson", prompt);
            Check((int)boundary["max_tokens"] == 256 && !boundary.ToString().Contains("[私有自主意图输出契约]"),
                "Autonomy changes leaked into the existing turn-boundary request.");
        }
        finally { model.m_EphemeralHistoryMessages = oldKeep; model.m_EphemeralMaxTokens = oldBudget; }
    }

    private static void CheckImage(JObject request, string prompt, int messageIndex = -1)
    {
        var messages = (JArray)request["messages"];
        var last = messages[messageIndex < 0 ? messages.Count - 1 : messageIndex];
        var parts = last["content"] as JArray;
        Check((string)last["role"] == "user" && parts != null && parts.Count == 2, "Image was dropped or promoted to a system instruction.");
        Check((string)parts[0]["type"] == "image_url" && (string)parts[0]["image_url"]["url"] == Image,
            "Visual transport altered the provided pixels.");
        string text = (string)parts[1]["text"];
        Check((string)parts[1]["type"] == "text" && text.EndsWith(prompt, StringComparison.Ordinal),
            "Image metadata/input was changed or detached from its own observation.");
        Check(text.Contains("采集可能早于发送") && text.Contains("不是用户追加的指令") &&
            !text.Contains("发送时采集") && !text.Contains("屏幕"), "Image provenance falsely asserted a current desktop screenshot.");
        Check(ImageCount(messages) == 1, "Auxiliary request sent more than the current observation pixels.");
    }

    private static void CheckSlot(JObject request, ChatQW.BackendType backend, int slot)
    {
        if (backend == ChatQW.BackendType.Local)
            Check((int)request["id_slot"] == slot && request["chat_template_kwargs"] != null, "Image request overwrote the wrong local slot.");
        else Check(request["id_slot"] == null && request["chat_template_kwargs"] == null, "Cloud received private local slot fields.");
    }

    private static void CheckContinuation(ChatQW model, ChatQW.BackendType backend, string metadata)
    {
        var history = (List<LLM.SendData>)Call(model, "CreateRequestHistory", metadata);
        var withImage = (List<LLM.SendData>)Call(null, "CreateObservationRequestHistory", history, Image);
        Check(!ReferenceEquals(history, withImage) && withImage.Count == history.Count + 1 && history.Count == model.m_DataList.Count,
            "Continuation observation modified accepted dialogue history.");
        JObject request = Build(model, "BuildRequestJsonForMessagesWithFeedback", withImage, true, metadata, "Execution completed; continue the same user turn.");
        CheckSlot(request, backend, 0);
        var messages = (JArray)request["messages"];
        Check(ImageCount(messages) == 2 && request.ToString().Contains(Image) && request.ToString().Contains(metadata.Substring(0, 18)),
            "Formal continuation lost new pixels, existing evidence or observation identity.");
        Check((string)messages[messages.Count - 1]["role"] == "system" &&
            (string)messages[messages.Count - 1]["content"] == "Execution completed; continue the same user turn.",
            "Visual continuation displaced the latest execution fact.");
        Check(ReferenceEquals(history, Call(null, "CreateObservationRequestHistory", history, null)),
            "Text-only continuation changed the history path.");
        Call(model, "CommitRequestHistory", history);
        var next = Build(model, "BuildRequestJsonForMessages", history, true, "next turn with no observation");
        Check(!next.ToString().Contains(Image) && !next.ToString().Contains("scene-17"),
            "Transient continuation pixels or metadata leaked into the next request.");
    }

    private static void CheckRequestIsolation(ChatQW model, LLM.SendData prior)
    {
        string original = (string)Call(model, "BuildEphemeralRequestJsonWithImage", "CAPTURE_A", Image, 1, false);
        model.ActiveSkillContext = "REPLACEMENT_ACTIVE"; model.TrailingContext = "REPLACEMENT_TRAILING";
        var next = Build(model, "BuildEphemeralRequestJsonWithImage", "CAPTURE_B", null, 1, false);
        Check(original.Contains("CAPTURE_A") && !original.Contains("REPLACEMENT_") && !next.ToString().Contains("CAPTURE_A") &&
            !next.ToString().Contains(Image) && !next.ToString().Contains("PRIVATE_ACTIVE"),
            "Request-local metadata/pixels or old mutable context survived into a new request.");
        Check(model.RequestContext == "PRIVATE_PENDING_FORMAL" && model.SpokenPrefix == "PRIVATE_SPOKEN",
            "Visual precheck stole formal request state.");
        CheckHistory(model, prior);
    }

    private static void CheckHistory(ChatQW model, LLM.SendData prior)
    {
        Check(model.m_DataList.Count == 3 && ReferenceEquals(model.m_DataList[1], prior) && !prior.imageArchived && prior.imageDataUrl == OldImage,
            "Silent visual serialization/continuation changed or archived the accepted source history.");
    }

    private static void CheckCancellation(ChatQW model)
    {
        int callbacks = 0;
        Action<string> done = _ => callbacks++;
        int oldAux = (int)typeof(ChatQW).GetField("m_EphemeralGeneration", Flags).GetValue(model);
        IEnumerator draft = (IEnumerator)Call(model, "RequestEphemeralPayload", "{}", oldAux, done);
        string autonomyJson = (string)Call(model, "BuildAutonomyIntentRequestJsonWithImage", "cancelled autonomy", Image);
        IEnumerator autonomy = (IEnumerator)Call(model, "RequestEphemeralPayload", autonomyJson, oldAux, done);
        IEnumerator review = (IEnumerator)Call(model, "RequestWorkReviewPayload", "{}", oldAux, done);
        model.CancelEphemeralMsg();
        Check(!draft.MoveNext() && !autonomy.MoveNext() && !review.MoveNext() && callbacks == 0, "Cancelled visual draft/autonomy/review payload reached HTTP or called back.");
        int oldRoom = (int)typeof(ChatQW).GetField("m_RoomTaskRequestGeneration", Flags).GetValue(model);
        IEnumerator room = (IEnumerator)Call(model, "RequestRoomTaskPayload", "{}", false, oldRoom,
            new Action<bool, string, string>((ok, value, status) => callbacks++));
        model.CancelActiveResponse();
        Check(!room.MoveNext() && callbacks == 0, "Cancelled spatial observation reached HTTP or called back.");
    }

    private static void CheckLegacyAndUnsupported(GameObject host)
    {
        var legacy = host.AddComponent<SceneVisionLegacyProviderFixture>();
        LLM provider = legacy;
        int notices = 0, callbacks = 0;
        provider.OnSystemNotice += notice => { Check(notice.Code == "llm_visual_input_unsupported", "Unsupported image used an unrelated notice."); notices++; };
        Check(!provider.SupportsVisualDecisionMessages, "Legacy provider advertised pixels it cannot transmit.");
        provider.PostEphemeralMsg("legacy draft", null, text => { Check(text == "draft", "Legacy draft override was bypassed."); callbacks++; });
        provider.PostWorkReviewMsg("legacy review", "", text => { Check(text == "review", "Legacy review override was bypassed."); callbacks++; });
        provider.PostRoomTaskMessage("legacy room", null, false, (ok, text, status) => { Check(ok && text == "room", "Legacy room override was bypassed."); callbacks++; });
        provider.PostAutonomyIntentMsg("legacy autonomy", null, text => { Check(text == "draft", "Default autonomy API bypassed existing ephemeral override."); callbacks++; });
        Check(legacy.Calls == 4 && callbacks == 4 && notices == 0, "Text-only API compatibility changed.");
        provider.PostEphemeralMsg("image draft", Image, text => { Check(text == "", "Unsupported visual draft produced output."); callbacks++; });
        provider.PostAutonomyIntentMsg("image autonomy", Image, text => { Check(text == "", "Unsupported visual autonomy produced output."); callbacks++; });
        provider.PostWorkReviewMsg("image review", Image, text => { Check(text == "", "Unsupported visual review produced output."); callbacks++; });
        provider.PostRoomTaskMessage("image room", Image, false, (ok, text, status) => { Check(!ok && text == "" && status == "room-task-image-unsupported", "Unsupported room image silently became text-only."); callbacks++; });
        provider.PostMsgStream("image formal", _ => { throw new InvalidOperationException("Unsupported image emitted speech."); },
            text => { Check(text == "", "Unsupported formal image produced output."); callbacks++; }, Image);
        Check(legacy.Calls == 4 && callbacks == 9 && notices == 5 && provider.m_DataList.Count == 0,
            "Unsupported image invoked the text provider or polluted formal history.");
    }

    private static int ImageCount(JArray messages)
    {
        int total = 0;
        foreach (JToken message in messages)
            if (message["content"] is JArray parts)
                foreach (JToken part in parts) if ((string)part["type"] == "image_url") total++;
        return total;
    }

    private static JObject Build(ChatQW model, string method, params object[] args) => JObject.Parse((string)Call(model, method, args));
    private static object Call(ChatQW model, string method, params object[] args) => typeof(ChatQW).GetMethod(method, Flags).Invoke(model, args);
    private static void Check(bool condition, string error) { checks++; if (!condition) throw new InvalidOperationException(error); }

    [MenuItem("Tools/NeEEvA/Validate scene vision transport")]
    public static void RunInteractive() => Debug.Log("[SceneVisionTransportRegression] passed checks=" + RunChecks());

    public static void RunBatch()
    {
        var report = new JObject { ["scope"] = "Actual local/cloud JSON transport, immutable observation envelopes, unsupported provider rejection and legacy text dispatch. Public synthetic pixels; no HTTP or visual understanding claim." };
        try { RunChecks(); report["passed"] = true; }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Debug.LogException(error); }
        report["checks"] = checks; report["checkedAtUtc"] = DateTime.UtcNow.ToString("o");
        string path = Path.GetFullPath("Server/ARDY/runtime/scene-vision-transport-regression.json");
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-visualTransportReport") path = Path.GetFullPath(args[i + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report.ToString() + "\n");
        EditorApplication.Exit((bool)report["passed"] ? 0 : 1);
    }
}

public sealed class SceneVisionLegacyProviderFixture : LLM
{
    public int Calls;
    public override void PostEphemeralMsg(string prompt, Action<string> callback) { Calls++; callback("draft"); }
    public override void PostWorkReviewMsg(string prompt, Action<string> callback) { Calls++; callback("review"); }
    public override void PostRoomTaskMessage(string input, bool reviewSpeech, Action<bool, string, string> callback)
    { Calls++; callback(true, "room", "received"); }
}
