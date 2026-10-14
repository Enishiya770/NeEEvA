using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class ContinuityLiveRegression
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    public static IEnumerator Run(ChatQW model, JObject fixture, string output)
    {
        var rows = new JArray();
        foreach (JObject test in fixture["planning"])
        {
            string input = test["input"].ToString();
            var method = typeof(ChatQW).GetMethod("BuildRoomTaskRequestJsonWithImage", Flags);
            string wire = (string)method.Invoke(model, new object[] { input, null, false });
            var request = JObject.Parse(wire);
            bool done = false, ok = false; string response = null, status = null;
            float began = Time.realtimeSinceStartup;
            model.PostRoomTaskMessage(input, false, (success, text, detail) => { ok = success; response = text; status = detail; done = true; });
            while (!done && Time.realtimeSinceStartup - began < 30) yield return null;
            if (!done) model.CancelRoomTaskPlanningMessage();
            JObject decision = null; try { decision = JObject.Parse(response ?? ""); } catch { }
            bool matched = (string)decision?["intent"] == (string)test["intent"];
            if (test["allowedIntents"] is JArray allowed) foreach (string intent in allowed) matched |= (string)decision?["intent"] == intent;
            string parseFailure = null, planFailure = null, selectFailure = null;
            bool parsed = RoomTaskProtocol.TryDecision(RoomTaskSequenceProtocol.DecisionOnly(response),
                (string)test["input"]["currentUserText"], false, out var parsedDecision, out parseFailure);
            bool planOk = RoomTaskSequenceProtocol.TryPlan(response, (string)test["input"]["currentUserText"], out var plan, out planFailure);
            if (planOk) foreach (JObject step in plan) step["status"] = "pending";
            bool selected = parsed && planOk && RoomTaskSequenceProtocol.TrySelect(response, parsedDecision, plan, out _, out selectFailure);
            bool passed = done && ok && matched && parsed && planOk && selected;
            var row = new JObject { ["name"] = test["name"], ["passed"] = passed, ["seconds"] = Time.realtimeSinceStartup - began,
                ["response"] = response, ["status"] = status, ["decisionValid"] = parsed, ["planValid"] = planOk, ["selectionValid"] = selected,
                ["parseFailure"] = parseFailure, ["planFailure"] = planFailure, ["selectFailure"] = selectFailure, ["systemCharacters"] = ((string)request["messages"][0]["content"]).Length,
                ["wireCharacters"] = wire.Length, ["actionsExecuted"] = false };
            rows.Add(row); File.WriteAllText(Path.Combine(output, "continuity-planning.json"), rows.ToString());
            File.WriteAllText(Path.Combine(output, (string)test["name"] + "-request.json"), wire);
            if (!passed) throw new InvalidOperationException("Focused live planning failed: " + test["name"] + " " + response);
        }
        yield return Audio((JObject)fixture["tts"], output);
    }

    private static IEnumerator Audio(JObject fixture, string output)
    {
        var host = new GameObject("Muted real continuity TTS"); host.SetActive(false);
        var tts = host.AddComponent<GPTSoVITSFASTAPI>();
        typeof(GPTSoVITSFASTAPI).GetField("m_ReferWavPath", Flags).SetValue(tts, (string)fixture["reference"]);
        typeof(GPTSoVITSFASTAPI).GetField("m_ReferenceText", Flags).SetValue(tts, (string)fixture["prompt"]);
        // Use the real scene's Japanese reference language, including English/Chinese output.
        var language = typeof(GPTSoVITSFASTAPI).GetField("m_ReferenceTextLan", Flags);
        language.SetValue(tts, Enum.Parse(language.FieldType, "日文"));
        typeof(GPTSoVITSFASTAPI).GetField("m_WarmUpAllTargetLanguages", Flags).SetValue(tts, false);
        var source = host.AddComponent<AudioSource>(); source.mute = true; host.AddComponent<AudioListener>(); host.SetActive(true);
        var rows = new JArray();
        try
        {
            foreach (JObject test in fixture["cases"])
            {
                var parts = new List<string>(); string remaining = (string)test["text"];
                while (remaining.Length > 0)
                {
                    int split = SpeechChunkPolicy.Boundary(remaining, parts.Count == 0, 24);
                    if (split < 0) split = remaining.Length - 1;
                    parts.Add(remaining.Substring(0, split + 1)); remaining = remaining.Substring(split + 1);
                }
                var chunks = new JArray(); var receipts = new JArray(); AudioClip prepared = null;
                bool preparationDone = false; float previousEnd = -1;
                Application.LogCallback log = (text, stack, kind) => {
                    const string prefix = "[TTS/StreamReceipt] ";
                    if (text.StartsWith(prefix)) receipts.Add(JObject.Parse(text.Substring(prefix.Length)));
                };
                Application.logMessageReceived += log;
                try
                {
                    for (int i = 0; i < parts.Count; i++)
                    {
                        float requestedAt = Time.realtimeSinceStartup;
                        if (i > 0)
                        {
                            while (!preparationDone && Time.realtimeSinceStartup - requestedAt < 20) yield return null;
                            if (prepared == null) throw new InvalidOperationException("Real prefetch failed.");
                        }
                        bool done = false, ok = false, started = false, prefetchIssued = false;
                        AudioClip clip = prepared; prepared = null; preparationDone = false;
                        float began = Time.realtimeSinceStartup, startedAt = -1; int lastSample = 0;
                        if (clip != null) { source.clip = clip; source.loop = false; source.Play(); started = true; }
                        else tts.SpeakStreamingWithPlaybackGate(new SpeechText(parts[i], (string)test["language"]), source, _ => started = true,
                            (success, text, duration) => { ok = success; done = true; }, null);
                        while (!done && Time.realtimeSinceStartup - began < 30)
                        {
                            if (started && source.timeSamples > 0)
                            {
                                if (startedAt < 0) startedAt = Time.realtimeSinceStartup;
                                lastSample = Math.Max(lastSample, source.timeSamples);
                            }
                            if (startedAt > 0 && !prefetchIssued && i + 1 < parts.Count && tts.CanPrefetchNextSpeech)
                            {
                                prefetchIssued = true;
                                tts.PrepareSpeech(new SpeechText(parts[i + 1], (string)test["language"]),
                                    (value, text) => { prepared = value; preparationDone = true; });
                            }
                            if (clip != null && startedAt > 0 && !source.isPlaying)
                            { ok = lastSample >= clip.samples - clip.frequency * .1f; done = true; }
                            yield return null;
                        }
                        chunks.Add(new JObject { ["text"] = parts[i], ["played"] = ok, ["prepared"] = clip != null,
                            ["startSeconds"] = startedAt - requestedAt,
                            ["interChunkGapSeconds"] = previousEnd < 0 ? (float?)null : startedAt - previousEnd });
                        previousEnd = Time.realtimeSinceStartup;
                        if (clip != null) { tts.ObservePreparedSpeech(clip, ok ? "completed" : "interrupted", lastSample); UnityEngine.Object.Destroy(clip); }
                        if (!done || !ok || startedAt < 0) throw new InvalidOperationException("Real muted TTS did not complete: " + test["name"]);
                    }
                    rows.Add(new JObject { ["name"] = test["name"], ["passed"] = true, ["chunks"] = chunks, ["streamReceipts"] = receipts,
                        ["scope"] = "Real service, real Unity PCM/prepared audio advancement, muted output. Controlled serial prefetch schedule; not a subjective listening score or full Chat UI test." });
                    File.WriteAllText(Path.Combine(output, "continuity-tts.json"), rows.ToString());
                }
                finally { Application.logMessageReceived -= log; if (prepared != null) UnityEngine.Object.Destroy(prepared); }
            }
        }
        finally { tts.CancelPreparedSpeech(); tts.CancelStreaming(); UnityEngine.Object.Destroy(host); }
    }
}
