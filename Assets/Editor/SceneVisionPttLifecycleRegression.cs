using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NeEEvA.Perception;
using Newtonsoft.Json.Linq;
using UniVRM10;
using UnityEngine;
using UnityEngine.UI;

// Genuine character-camera rendering and production StartStreaming orchestration.
// Only the model transport is intercepted; no microphone, HTTP or audio playback.
public static class SceneVisionPttLifecycleRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static JObject LastReport { get; private set; }

    public static IEnumerator RunChecks(Vrm10Instance avatar, string output)
    {
        if (!Application.isPlaying || !Application.isBatchMode || avatar == null ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Use an imported avatar in the isolated batch Play fixture.");
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || !SystemInfo.supportsAsyncGPUReadback)
            throw new InvalidOperationException("Actual PTT capture requires graphics and async readback; omit -nographics.");
        int checks = 0;
        var cases = new JArray();
        LastReport = new JObject { ["status"] = "running", ["cases"] = cases,
            ["scope"] = "Real character-camera images through production PTT/typed formal capture and RoleDecision transport boundary; controlled model transport; no remote model, microphone or audio." };
        void Check(bool condition, string reason)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(reason);
            cases.Add(reason);
        }
        var host = new GameObject("PTT visual lifecycle isolated chat");
        host.SetActive(false);
        var chat = host.AddComponent<ChatSample>();
        var model = host.AddComponent<SceneVisionPttFixtureModel>();
        var tts = host.AddComponent<TTS>(); tts.enabled = false;
        var audio = host.AddComponent<AudioSource>(); audio.playOnAwake = false;
        var text = new GameObject("Output", typeof(RectTransform), typeof(Text)); text.transform.SetParent(host.transform);
        var button = new GameObject("Commit", typeof(RectTransform), typeof(Button)); button.transform.SetParent(host.transform);
        var input = new GameObject("Input", typeof(RectTransform), typeof(InputField)); input.transform.SetParent(host.transform);
        Set(chat, "m_TextBack", text.GetComponent<Text>()); Set(chat, "m_RecordTips", text.GetComponent<Text>());
        Set(chat, "m_CommitMsgBtn", button.GetComponent<Button>()); Set(chat, "m_InputWord", input.GetComponent<InputField>());
        Set(chat, "m_AudioSource", audio); Set(chat, "m_ChatHistory", new List<string>());
        Set(chat, "m_ChatSettings", new ChatSetting { m_ChatModel = model, m_TextToSpeech = tts });
        Set(chat, "m_SceneVisionAvatar", avatar); Set(chat, "m_ContinuousSceneVision", false);
        Set(chat, "m_SceneVisionOpenOnStart", true); Set(chat, "m_EnableScreenVision", true);
        Set(chat, "m_SystemNoticeMode", SystemNoticeMode.Hidden);
        foreach (string flag in new[] { "m_AutoStartHumSVC", "m_AutoStartSVS", "m_EnableLatencyFiller",
            "m_PersistSubtitleSettings", "m_EnableSubtitleTranslation", "m_EnableMemoryRecall",
            "m_PrewarmHumSVCOnSingingSkillLoad", "m_EnableSceneObjectAwareness" }) Set(chat, flag, false);
        try
        {
            host.SetActive(true);
            yield return null;
            Check(!(bool)Get(chat, "m_AgentRunning") && chat.SceneEyesOpen,
                "Initial manual conversation opens configured eyes without starting autonomy.");
            Dispatch(chat, "你现在能看到什么？");
            yield return Until(() => model.Requests.Count == 1, "Initial PTT image did not reach RoleDecision.");
            var first = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", Get(chat, "m_FormalResponseGeneration"));
            Check(first != null && first.HasImage && first.Source == "character-scene-rgb" &&
                model.Requests[0].Image == first.ImageDataUrl,
                "Agent=false sends actual current character-camera pixels to the formal role request.");
            Check(model.Requests[0].Prompt.Contains("你现在能看到什么？") &&
                model.Requests[0].Prompt.Contains("\"currentImageDeliveredInThisRequest\":true"),
                "The visual request preserves user text and reports delivered current-image evidence.");
            Call(chat, "InvalidateFormalResponse", "ptt-fixture-between-turns");
            Check((bool)Call(chat, "ApplySceneVisionAttention", false) && !chat.SceneEyesOpen &&
                !chat.SceneVisionCapture.IsObserving && !chat.SceneVisionCapture.LatestObservation.HasImage,
                "Closing eyes clears current pixels and observation permission.");
            Check(Call(chat, "ResolveFormalSceneImage", Get(chat, "m_FormalResponseGeneration")) == null,
                "A closed-eye formal request cannot reuse the old attachment.");
            chat.StartAgentLoop(); chat.StopAgentLoop();
            Check(!chat.SceneEyesOpen && !(bool)Get(chat, "m_AgentEyesOpen"),
                "Switching realtime collection on/off preserves explicitly closed eyes.");
            Check((bool)Call(chat, "ApplySceneVisionAttention", true) && chat.SceneEyesOpen &&
                !chat.SceneVisionCapture.LatestObservation.HasImage,
                "Reopening in PTT waits for a new frame rather than reviving old pixels.");
            Dispatch(chat, "睁开眼后现在能看到什么？");
            yield return Until(() => model.Requests.Count == 2, "Reopened PTT image did not reach RoleDecision.");
            var reopened = (SceneVisionObservation)Call(chat, "GetFormalSceneObservation", Get(chat, "m_FormalResponseGeneration"));
            Check(reopened != null && reopened.HasImage && reopened.CaptureId != first.CaptureId &&
                model.Requests[1].Image == reopened.ImageDataUrl,
                "Reopened formal decision uses a newly rendered capture and its exact pixels.");
            Call(chat, "InvalidateFormalResponse", "ptt-fixture-between-turns");
            chat.StartAgentLoop(); chat.StopAgentLoop();
            Check(chat.SceneEyesOpen && chat.SceneVisionCapture.IsObserving && !(bool)Get(chat, "m_AgentRunning"),
                "Stopping realtime collection leaves manual conversation vision available.");

            foreach (string boundary in new[] { "closed", "disabled", "paused", "superseded" })
            {
                int before = model.Requests.Count;
                Dispatch(chat, "这轮采图必须被取消：" + boundary);
                int pending = (int)Get(chat, "m_FormalResponseGeneration");
                Check((int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == pending,
                    boundary + ": production formal generation owns the pending camera request.");
                if (boundary == "closed") Call(chat, "ApplySceneVisionAttention", false);
                else if (boundary == "disabled") chat.enabled = false;
                else if (boundary == "paused") Call(chat, "OnApplicationPause", true);
                else Call(chat, "InvalidateFormalResponse", "ptt-fixture-superseded");
                yield return null; yield return null;
                Check(model.Requests.Count == before && !(bool)Get(chat, "m_FormalResponseInFlight") &&
                    (int)Get(chat, "m_PendingSceneFormalCaptureGeneration") == -1,
                    boundary + ": cancellation rejects the obsolete image and releases formal busy ownership.");
                if (boundary == "disabled") chat.enabled = true;
                else if (boundary == "paused") Call(chat, "OnApplicationPause", false);
                else if (boundary == "closed") Call(chat, "ApplySceneVisionAttention", true);
                Dispatch(chat, "取消后重新看这一轮：" + boundary);
                yield return Until(() => model.Requests.Count == before + 1, boundary + ": next manual turn could not resume capture.");
                Check(!string.IsNullOrEmpty(model.Requests[before].Image) && !(bool)Get(chat, "m_AgentRunning"),
                    boundary + ": next manual turn recovers with current pixels and no autonomous loop.");
                Call(chat, "InvalidateFormalResponse", "ptt-fixture-between-turns");
            }
            Call(chat, "ApplySceneVisionAttention", false);
            chat.enabled = false; chat.enabled = true;
            Check(!chat.SceneEyesOpen, "Component re-enable does not override the user's closed-eye preference.");
            Set(chat, "m_EnableScreenVision", false);
            Check(!(bool)Call(chat, "ApplySceneVisionAttention", true) && !chat.SceneEyesOpen,
                "Role attention cannot override the scene vision master switch.");
            Check(!audio.isPlaying && host.GetComponent<RTSpeechHandler>() == null,
                "Vision lifecycle checks never start an audio source or realtime microphone handler.");
            LastReport["status"] = "passed";
        }
        finally
        {
            LastReport["checks"] = checks;
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "ptt-vision-lifecycle.json"), LastReport.ToString());
            UnityEngine.Object.DestroyImmediate(host);
        }
    }

    private static void Dispatch(ChatSample chat, string prompt)
    {
        Call(chat, "SetCurrentUserInput", prompt, "ptt-vision-fixture");
        Call(chat, "StartStreaming", prompt, false, null, false, false, false, null, false, null, false);
    }
    private static IEnumerator Until(Func<bool> ready, string error)
    {
        float deadline = Time.realtimeSinceStartup + 6f;
        while (!ready() && Time.realtimeSinceStartup < deadline) yield return null;
        if (!ready()) throw new TimeoutException(error);
    }
    private static object Get(object value, string name) => value.GetType().GetField(name, Flags).GetValue(value);
    private static void Set(object value, string name, object data) => value.GetType().GetField(name, Flags).SetValue(value, data);
    private static object Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Flags).Invoke(value, args);
}

public sealed class SceneVisionPttFixtureModel : LLM
{
    public sealed class Request { public string Prompt, Image; }
    public readonly List<Request> Requests = new List<Request>();
    public override bool SupportsVisualDecisionMessages => true;
    public override bool SupportsRoleDecisionMessages => true;
    public override void PostRoleDecisionMsg(string prompt, string imageDataUrl,
        Action<bool, string, string> callback, ConversationTrace trace)
        => Requests.Add(new Request { Prompt = prompt, Image = imageDataUrl });
}
