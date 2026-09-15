using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NeEEvA.Motion;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

// Explicit opt-in: real shared Qwen, ARDY, TTS and engine PCM playback. The only
// injected input is a declared finalized text; microphone/ASR time is excluded.
public static class SceneResponseLatencyRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    public static JObject LastReport { get; private set; }
    private static readonly string[] Prompts = {
        "先不要移动，只告诉我你现在眼前的家具是什么。",
        "请看看电视柜上的小灯，告诉我它放在什么家具上。",
        "看着电视，后退半米，试着把整块屏幕看进来。"
    };

    public static IEnumerator RunChecks(ChatSample chat, ArdyRoomDialogueBridge bridge,
        NevaSceneVisionIntegrationModel fixture, JObject roleConfig, JObject ttsConfig, string output)
    {
        if (!Application.isBatchMode || !Application.isPlaying ||
            Application.dataPath.IndexOf("unity-naturalness-validation", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Real response latency fixture is isolated-only.");
        string directory = Path.Combine(output, "response-latency"); Directory.CreateDirectory(directory);
        string caseFile = Environment.GetEnvironmentVariable("NEEEVA_RESPONSE_CASES_FILE");
        string[] prompts = string.IsNullOrEmpty(caseFile) ? Prompts :
            JArray.Parse(File.ReadAllText(caseFile)).Select(x => (string)x).ToArray();
        if (prompts.Length == 0 || prompts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Response cases must contain nonempty finalized inputs.");
        string firstReplyMode = Environment.GetEnvironmentVariable("NEEEVA_RESPONSE_FIRST_REPLY_MODE") ?? "default";
        bool audible = Environment.GetEnvironmentVariable("NEEEVA_RESPONSE_AUDIBLE") == "1";
        string loopbackTrigger = Environment.GetEnvironmentVariable("NEEEVA_RESPONSE_LOOPBACK_TRIGGER");
        var firstReplyField = Field(typeof(ChatSample), "m_EnableFirstReplyRouting");
        if (firstReplyMode == "on" && firstReplyField == null)
            throw new InvalidOperationException("First reply mode requested but runtime has no implementation.");
        if (firstReplyField != null && firstReplyMode != "default") firstReplyField.SetValue(chat, firstReplyMode == "on");
        LastReport = new JObject { ["status"] = "running", ["cases"] = new JArray(),
            ["scope"] = "Actual role prompts/settings, production SendData pipeline, real Qwen and TTS PCM/AudioSource onset. Current room RGB is supplied only on the visual/tools path. Finalized input is injected, not microphone/ASR. " + (audible ? "System output requested; independent waveform matching is needed to establish loopback onset." : "Output is muted locally; this is not a listening-quality test."),
            ["fixedPrompts"] = new JArray(prompts), ["controlledLoadedSkill"] = "singing", ["firstReplyMode"] = firstReplyMode,
            ["audibleOutputRequested"] = audible,
            ["memoryScope"] = "Isolated current scene facts and actual prior replies in this run; not a copy of the user's persistent private memory graph.",
            ["rule"] = "Every declared case is retained, including failure/fallback. No selection of best retries. No teleport or obstacle changes." };
        var logs = new List<JObject>();
        string activeCase = "setup";
        Application.LogCallback listener = (message, trace, type) => {
            if (!message.StartsWith("[", StringComparison.Ordinal)) return;
            if (new[]{"[LLM/", "[Room/", "[FirstReply/", "[Vision/Action]", "[TTS/", "[Timing/Perceived]", "[LLM执行层输入]", "[Agent/Review]", "[Agent/Private"}.Any(message.StartsWith))
                logs.Add(new JObject { ["case"] = activeCase, ["realtime"] = Time.realtimeSinceStartup, ["message"] = message });
        };
        Application.logMessageReceived += listener;
        var settings = (ChatSetting)Get(chat, "m_ChatSettings");
        var outputAudio = (AudioSource)Get(chat, "m_AudioSource");
        FirstAudioReferenceCapture referenceTap = null;
        GameObject isolatedListener = null;
        bool originalMute = outputAudio.mute;
        var originalModel = settings.m_ChatModel; var originalTts = settings.m_TextToSpeech;
        var real = chat.gameObject.AddComponent<ChatQW>();
        var tts = chat.gameObject.AddComponent<GPTSoVITSFASTAPI>();
        int actualWireIndex = 0;
        Action<string> saveActualWire = wire => {
            string name = activeCase + "-formal-actual-" + (actualWireIndex++).ToString("D2") + ".request.json";
            File.WriteAllText(Path.Combine(directory, name), wire + "\n");
        };
        real.OnRequestDiagnostic += saveActualWire;
        try
        {
            foreach (var item in roleConfig.Properties())
            {
                FieldInfo field = Field(typeof(ChatQW), item.Name);
                if (field == null) continue;
                object value = item.Name == "m_PromptFiles" || item.Name == "m_SkillFiles"
                    ? ((JArray)item.Value).Select(x => AssetDatabase.LoadAssetAtPath<TextAsset>((string)x["path"])).ToArray()
                    : item.Value.ToObject(field.FieldType);
                field.SetValue(real, value);
            }
            real.m_Backend = ChatQW.BackendType.Local;
            Set(real, "url", real.m_LocalUrl);
            real.InitSystemMessage(); real.SkillCatalogContext = fixture.SkillCatalogContext;
            real.SetActiveSkills("singing"); real.m_LogRequestStats = true;
            if (!real.HasPromptFiles) throw new InvalidOperationException("Actual role prompts were not loaded.");
            foreach (var item in ttsConfig.Properties())
            {
                FieldInfo field = Field(typeof(GPTSoVITSFASTAPI), item.Name);
                if (field != null) field.SetValue(tts, item.Value.ToObject(field.FieldType));
            }
            Set(tts, "m_WarmUpAllTargetLanguages", false); Set(tts, "m_WarmUpLatencyFillerVariants", false);
            // Existing actual reference file on this host. No voice substitution.
            string reference = (string)Get(tts, "m_ReferWavPath");
            if (!Path.IsPathRooted(reference))
            {
                string sourceAssets = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../../../Assets"));
                string resolved = Path.GetFullPath(Path.Combine(sourceAssets, reference));
                if (!File.Exists(resolved)) throw new FileNotFoundException("Actual voice reference unavailable", resolved);
                Set(tts, "m_ReferWavPath", resolved);
            }
            Set(chat, "m_EnableSceneObjectAwareness", true); Set(chat, "m_EnableSceneObjectTargetMatching", true);
            Set(chat, "m_EnableLatencyFiller", false); Set(chat, "m_LogStreamTimings", true);
            Set(chat, "m_LogAgentLoop", true);
            if (chat.m_InputWord == null)
            {
                var input = new GameObject("Finalized input fixture", typeof(RectTransform), typeof(UnityEngine.UI.InputField));
                input.transform.SetParent(chat.transform, false); chat.m_InputWord = input.GetComponent<UnityEngine.UI.InputField>();
            }
            outputAudio.mute = !audible;
            // The scene-geometry fixture deliberately strips user cameras and
            // their listeners. Audible verification needs its own local listener.
            if (audible && !Object.FindObjectsOfType<AudioListener>().Any(x => x.isActiveAndEnabled))
            {
                isolatedListener = new GameObject("Isolated audible verification listener");
                isolatedListener.AddComponent<AudioListener>();
            }
            if (audible && !string.IsNullOrEmpty(loopbackTrigger))
                referenceTap = outputAudio.gameObject.AddComponent<FirstAudioReferenceCapture>();
            settings.m_ChatModel = real; settings.m_TextToSpeech = tts;
            yield return null;
            foreach (string prompt in prompts)
            {
                Call(chat, "ClearScheduledAgentWake"); Call(chat, "CancelAutonomyIntentProbe");
                while (chat.IsVoiceOutputPlaying || BodyBusy(bridge)) yield return null;
                activeCase = "case-" + ((JArray)LastReport["cases"]).Count;
                var entry = new JObject { ["name"] = activeCase, ["prompt"] = prompt,
                    ["startedRealtime"] = Time.realtimeSinceStartup, ["initialRoot"] = JToken.Parse(JsonUtility.ToJson(bridge.avatar.transform.position)),
                    ["status"] = "running" };
                ((JArray)LastReport["cases"]).Add(entry);
                if (referenceTap != null) referenceTap.Begin(Path.Combine(directory, activeCase + "-reference"));
                float start = Time.realtimeSinceStartup;
                entry["startedUtc"] = DateTime.UtcNow.ToString("O");
                if (!string.IsNullOrEmpty(loopbackTrigger))
                    File.WriteAllText(loopbackTrigger, new JObject { ["case"] = activeCase,
                        ["caseId"] = activeCase, ["utc"] = entry["startedUtc"],
                        ["startedUtc"] = entry["startedUtc"], ["startedRealtime"] = start,
                        ["prompt"] = prompt, ["audible"] = audible }.ToString());
                Set(chat, "m_EouTime", start); Set(chat, "m_ListeningClosedAt", start);
                chat.RecordListeningEndEvidence(start, "fixture-finalized-text-not-recorded-speech", start, start, "fixture");
                chat.SendData(prompt);
                // Reproduce the explicitly declared already-loaded skill condition
                // from the actual log; never fabricate acoustic/singing evidence.
                real.SetActiveSkills("singing");
                float first = -1; bool timedOut = false;
                while (true)
                {
                    if (first < 0 && (bool)Get(chat, "m_RealFirstAudioStarted")) first = Time.realtimeSinceStartup;
                    bool done = (bool)Get(chat, "m_StreamComplete") && !(bool)Get(chat, "m_FormalResponseInFlight") &&
                        (bool)Get(chat, "m_TTSSenderDone") && !chat.IsVoiceOutputPlaying && !BodyBusy(bridge);
                    if (done) break;
                    if (Time.realtimeSinceStartup - start > 65) { timedOut = true; break; }
                    yield return null;
                }
                entry["firstRealPcmPlaybackSeconds"] = first >= 0 ? new JValue(first-start) : JValue.CreateNull();
                entry["completedSeconds"] = Time.realtimeSinceStartup-start;
                entry["status"] = timedOut ? "timeout" : first < 0 ? "no-public-pcm" : "completed";
                entry["intent"] = (string)Get(chat,"m_RoomTaskIntent");
                entry["dispatch"] = (string)Get(chat,"m_RoomTaskDispatch");
                entry["sceneAction"] = ((JObject)Call(chat,"BuildSceneActionFacts")).DeepClone();
                entry["publishedAssistantHistory"] = new JArray(real.m_DataList.Where(x=>x.role=="assistant").Select(x=>x.content));
                if (referenceTap != null) entry["referenceCapture"] = referenceTap.End();
                if (!string.IsNullOrEmpty(loopbackTrigger)) File.WriteAllText(loopbackTrigger + ".done", new JObject {
                    ["utc"] = DateTime.UtcNow.ToString("O"), ["caseId"] = activeCase,
                    ["status"] = entry["status"], ["ttsSource"] = entry["publishedAssistantHistory"]?.Last,
                    ["sourcePcmOnsetSeconds"] = entry["firstRealPcmPlaybackSeconds"] }.ToString());
                File.WriteAllText(Path.Combine(directory,"report.json"),LastReport.ToString()+"\n");
                File.WriteAllText(Path.Combine(directory,"events.json"),new JArray(logs).ToString()+"\n");
                if (timedOut) { Call(chat,"InvalidateFormalResponse","latency-fixture-timeout"); break; }
            }
            LastReport["status"] = ((JArray)LastReport["cases"]).Count == prompts.Length &&
                ((JArray)LastReport["cases"]).All(x=>(string)x["status"]=="completed") ? "completed" : "incomplete";
        }
        finally
        {
            if (referenceTap != null) { referenceTap.End(); Object.Destroy(referenceTap); }
            if (isolatedListener != null) Object.Destroy(isolatedListener);
            real.OnRequestDiagnostic -= saveActualWire;
            Application.logMessageReceived -= listener;
            Call(chat,"InvalidateFormalResponse","latency-fixture-finished"); Call(chat,"ClearScheduledAgentWake");
            Call(chat,"CancelAutonomyIntentProbe"); real.CancelActiveResponse(); tts.CancelStreaming();
            settings.m_ChatModel = originalModel; settings.m_TextToSpeech = originalTts;
            outputAudio.mute = originalMute;
            File.WriteAllText(Path.Combine(directory,"report.json"),LastReport.ToString()+"\n");
            File.WriteAllText(Path.Combine(directory,"events.json"),new JArray(logs).ToString()+"\n");
            Object.Destroy(real); Object.Destroy(tts);
        }
    }
    private static bool BodyBusy(ArdyRoomDialogueBridge bridge)
    {
        if (bridge.ReservesBody || bridge.Controller.IsBusy || bridge.Controller.IsMoving) return true;
        var actor = bridge.avatar;
        var gestures = actor.GetComponent<ArdyLiveMotionController>();
        return (gestures != null && !string.IsNullOrEmpty(gestures.DescribeActiveMotion())) ||
            actor.GetComponents<ArdyMotionPlayer>().Any(player => player.IsPlaying);
    }
    private static FieldInfo Field(Type type,string name) { for(;type!=null;type=type.BaseType) { var f=type.GetField(name,Flags); if(f!=null)return f; } return null; }
    private static object Get(object o,string n) => Field(o.GetType(),n).GetValue(o);
    private static void Set(object o,string n,object v) => Field(o.GetType(),n).SetValue(o,v);
    private static object Call(object o,string n,params object[] args) => o.GetType().GetMethods(Flags).Single(x=>x.Name==n && x.GetParameters().Length==args.Length).Invoke(o,args);
}
