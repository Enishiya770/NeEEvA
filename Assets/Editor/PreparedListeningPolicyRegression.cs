using System;
using Newtonsoft.Json.Linq;

/// <summary>Bounded preparation timing with corrections, resumption, and cancelled work.</summary>
public static class PreparedListeningPolicyRegression
{
    private static int checks;
    public static int RunOrThrow()
    {
        checks = 0;
        var policy = new PreparedListeningPolicy();
        var evidence = new PreparedTurnBoundaryEvidence
        {
            CaptureSerial = 1, TextRevision = 1, ActivityRevision = 1, Transcript = "停下。",
            Recording = true, AsrHealthy = true, AudioMs = 600,
        };
        float delay; string reason;
        Check(policy.TryDelay(evidence, 10, 10f, -100f, 4f, out delay, out reason) && Near(delay, .2f),
            "short stop first stable preparation is eligible at .2s");
        Check(policy.MarkSent(evidence, 10, 10.2f), "first dispatch consumes one capture slot");
        Check(!policy.NeedsLatest(evidence, 10), "unchanged text is not repeatedly submitted");
        evidence.AudioMs += 500;
        Check(!policy.NeedsLatest(evidence, 10), "new coverage alone does not multiply model requests");
        evidence.Transcript = "停下。不，继续跟随我。"; evidence.TextRevision++;
        Check(policy.TryDelay(evidence, 10, 10.6f, 10.2f, 4f, out delay, out reason) && Near(delay, .6f),
            "one correction refresh may run after one second without legacy four-second delay");
        Check(policy.MarkSent(evidence, 10, 11.2f), "latest correction consumes second slot");
        // Even cancelled requests remain spent: no reset API exists for the same capture.
        evidence.Transcript = "等等，不要跟随。"; evidence.TextRevision++;
        Check(policy.TryDelay(evidence, 10, 11.4f, 11.2f, 4f, out delay, out reason) && Near(delay, 3.8f),
            "third latest request returns to four-second spacing after cancellation/correction");
        Check(policy.MarkSent(evidence, 10, 15.2f), "third and final ordinary request");
        evidence.Transcript = "请继续。"; evidence.TextRevision++;
        Check(!policy.TryDelay(evidence, 10, 19.2f, 15.2f, 4f, out delay, out reason) &&
            reason == "capture-budget-exhausted", "long rapidly edited capture cannot flood requests");

        evidence.CaptureSerial++;
        Check(policy.TryDelay(evidence, 11, 15.3f, 15.2f, 4f, out delay, out reason) && Near(delay, .9f),
            "new recording resets capture budget but still respects global wire floor");
        Check(policy.MarkSent(evidence, 11, 16.2f), "new recording dispatch");
        evidence.ActivityRevision++;
        Check(policy.NeedsLatest(evidence, 11), "real resumed voice can reprepare even before ASR words change");
        evidence.Singing = true;
        Check(!policy.NeedsLatest(evidence, 11), "singing cannot enter ordinary boundary preparation");
        evidence.Singing = false; evidence.MelodyActive = true;
        Check(!policy.NeedsLatest(evidence, 11), "melodic evidence blocks ordinary preparation");
        evidence.MelodyActive = false; evidence.Recording = false;
        Check(!policy.NeedsLatest(evidence, 11), "closed recording cannot restart low-priority work");
        evidence.Recording = true; evidence.AsrHealthy = false;
        Check(!policy.NeedsLatest(evidence, 11), "failed ASR cannot be worked around by preparation");

        JObject schema = PreparedListeningProtocol.CreateSchema();
        Check(schema["properties"]["quiet_boundary"]["properties"]["action"]["enum"].ToString().Contains("complete"), "schema exposes semantic closure");
        Check(!schema.ToString().Contains("interrupt_user"), "conditional schema excludes interruption");
        string prompt = PreparedListeningProtocol.BuildPrompt("不，别关灯。");
        Check(prompt.Contains("不，别关灯。") && prompt.Contains("没有新声音") && prompt.Contains("完整的否定请求也可以闭合"),
            "full correction and new-sound contract preserved");
        return checks;
    }
    private static bool Near(float a, float b) => Math.Abs(a - b) < .001f;
    private static void Check(bool success, string label)
    { checks++; if (!success) throw new InvalidOperationException("Prepared listening policy: " + label); }
}
