using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Additional lifecycle checks for this latency fix. Reuses the public fixture;
// completion ledger entries are synthetic and no audio or network is produced.
public static class ChatLatencyLifecycleRegression
{
    private const string Key = "NeEEvA.ChatLatency.Lifecycle";
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int checks;

    [InitializeOnLoadMethod]
    private static void Resume()
    {
        EditorApplication.playModeStateChanged -= Changed;
        EditorApplication.playModeStateChanged += Changed;
        if (SessionState.GetBool(Key, false) && EditorApplication.isPlaying) EditorApplication.delayCall += Run;
    }
    public static void RunBatch()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        SessionState.SetBool(Key, true); EditorApplication.EnterPlaymode();
    }
    private static void Changed(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Key, false)) EditorApplication.delayCall += Run;
    }

    private static void Run()
    {
        if (!SessionState.GetBool(Key, false)) return;
        SessionState.SetBool(Key, false); checks = 0;
        object fixture = null;
        var report = new JObject { ["scope"] = "Actual public final-ASR and typed request admission, loop stop/start and original callback epoch fences. Synthetic completed goal/playback ledger; no network, microphone, TTS or physical playback." };
        try
        {
            Type fixtureType = typeof(ChatLatencyRegression).GetNestedType("Fixture", BindingFlags.NonPublic);
            fixture = Activator.CreateInstance(fixtureType, new object[] { "lifecycle_boundaries" });
            var chat = (ChatSample)fixtureType.GetField("chat", Flags).GetValue(fixture);
            var model = (ChatLatencyFakeLLM)fixtureType.GetField("model", Flags).GetValue(fixture);
            void Stage(string text) => typeof(ChatSample).GetMethod("StageSkillInputObservation", Flags).Invoke(chat, new object[] { text, true });
            bool Consume(string text, bool audio) => (bool)typeof(ChatSample).GetMethod("ConsumeSkillInputObservation", Flags).Invoke(chat, new object[] { text, audio });
            Stage("公开的同一句。");
            Check(!Consume("公开的同一句。", false) && !Consume("公开的同一句。", true),
                "A typed turn inherited acoustic singing evidence or left it consumable by a later turn.");
            Stage("公开的同一句。"); Set(chat, "m_InputAudioRevision", (int)Get(chat, "m_InputAudioRevision") + 1);
            Check(!Consume("公开的同一句。", true), "A newer audio revision reused an older observation despite matching text.");
            Stage("公开的同一句。");
            Check(Consume("公开的同一句。", true) && !Consume("公开的同一句。", true), "Matching acoustic evidence was not consumed exactly once.");
            void Submit(string text) => fixtureType.GetMethod("Submit", Flags).Invoke(fixture, new object[] { text, false, false });
            void Complete(string text) => fixtureType.GetMethod("StreamIndependent", Flags).Invoke(fixture, new object[] { text });
            int epoch = (int)Get(chat, "m_WorkEpoch"), goalTurn = (int)Get(chat, "m_SingingGoalTurn");
            Submit("早上好。");
            Check((int)Get(chat, "m_WorkEpoch") == epoch + 1 && (int)Get(chat, "m_SingingGoalTurn") == goalTurn + 1,
                "A single final-ASR user turn advanced work/goal identity more than once.");
            Complete("おはよう。今日はゆっくり過ごす？");
            epoch = (int)Get(chat, "m_WorkEpoch"); goalTurn = (int)Get(chat, "m_SingingGoalTurn");
            chat.SendData("文字输入也想继续聊天。"); chat.StopAllCoroutines();
            Check((int)Get(chat, "m_WorkEpoch") == epoch + 1 && (int)Get(chat, "m_SingingGoalTurn") == goalTurn + 1,
                "A typed user turn advanced work/goal identity more than once.");
            Complete("もちろん。何を話そうか。");

            // Preserve the state that actually supplies repeat prevention together with
            // saved history, rather than merely checking that m_DataList is nonempty.
            string lastUser = (string)Get(chat, "m_LastUserMsg"), lastReply = (string)Get(chat, "m_LastAIMsgPlain");
            var recent = (Queue<KeyValuePair<float, string>>)Get(chat, "m_RecentAIUtterances");
            int recentCount = recent.Count, historyCount = model.m_DataList.Count;
            Check(recentCount > 0 && !string.IsNullOrEmpty(lastReply), "The public fixture did not seed recent reply evidence.");
            Type goalType = typeof(ChatSample).GetNestedType("SingingGoal", Flags);
            Type playbackType = typeof(ChatSample).GetNestedType("SingingPlaybackEvidence", Flags);
            goalTurn = (int)Get(chat, "m_SingingGoalTurn");
            object goal = JsonConvert.DeserializeObject("{\"turn\":" + goalTurn + ",\"version\":7,\"refs\":\"clip:public-completed\",\"review\":\"approved\",\"outcome\":\"user_confirmed\"}", goalType);
            object playback = JsonConvert.DeserializeObject("{\"turn\":" + goalTurn + ",\"goalVersion\":7,\"completed\":true}", playbackType);
            Set(chat, "m_SingingGoal", goal); Set(chat, "m_LastSingingGoalPlayback", playback);
            epoch = (int)Get(chat, "m_WorkEpoch"); int beforeBoundaryGoalTurn = (int)Get(chat, "m_SingingGoalTurn");
            chat.StopAgentLoop(); chat.StopAllCoroutines();
            Check((int)Get(chat, "m_WorkEpoch") > epoch, "Stopping capture did not invalidate old work callbacks.");
            chat.StartAgentLoop(); chat.StopAllCoroutines();
            Check(ReferenceEquals(Get(chat, "m_SingingGoal"), goal) && ReferenceEquals(Get(chat, "m_LastSingingGoalPlayback"), playback),
                "Realtime stop/start discarded the completed goal or playback evidence object.");
            Check((int)Get(chat, "m_SingingGoalTurn") == beforeBoundaryGoalTurn, "Capture restart invented a new user goal turn.");
            Check((string)Get(chat, "m_LastUserMsg") == lastUser && (string)Get(chat, "m_LastAIMsgPlain") == lastReply && recent.Count == recentCount && model.m_DataList.Count == historyCount,
                "Realtime stop/start retained history but erased recent user/reply evidence.");

            // An unfinished callback (not an already consumed callback) must become
            // inert after a mode boundary, then a fresh accepted user must still work.
            Submit("请说说现在的天气。");
            var oldChannels = (RoleOutputChannels)typeof(ChatLatencyFakeLLM).GetField("channels", Flags).GetValue(model);
            var oldCompletion = (Action<string>)typeof(ChatLatencyFakeLLM).GetField("completion", Flags).GetValue(model);
            chat.StopAgentLoop(); chat.StopAllCoroutines(); chat.StartAgentLoop(); chat.StopAllCoroutines();
            Submit("换个话题，聊聊公园吧。");
            int requests = model.Requests.Count;
            var queue = (ICollection)Get(chat, "m_PendingChunks");
            oldChannels.Push("<speech mode=\"independent\"/><lang code=\"ja\"/>古い返事は出さないで。<continue/>"); oldChannels.Finish();
            oldCompletion(oldChannels.ToExecutableText(true, true));
            Check(queue.Count == 0 && model.Requests.Count == requests, "An old mode's unfinished callback leaked speech or chained another request.");
            Check((string)Get(chat, "m_LastUserMsg") == "换个话题，聊聊公园吧。", "The old completion replaced the latest real user.");
            Complete("公園なら木陰の道を歩きたいね。");
            Check(queue.Count > 0, "Fencing the cancelled turn also blocked the current independent reply.");
            report["passed"] = true;
        }
        catch (Exception error) { report["passed"] = false; report["error"] = error.ToString(); Debug.LogException(error); }
        finally { if (fixture != null) ((IDisposable)fixture).Dispose(); }
        report["checks"] = checks;
        string path = Path.GetFullPath("Tools/MotionAdapter/reports/chat-latency-lifecycle-regression.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, report + "\n");
        EditorApplication.Exit((bool)report["passed"] ? 0 : 1);
    }
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
    private static object Get(ChatSample chat, string name) => typeof(ChatSample).GetField(name, Flags).GetValue(chat);
    private static void Set(ChatSample chat, string name, object value) => typeof(ChatSample).GetField(name, Flags).SetValue(chat, value);
}
