using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

public static class ContinuityPolicyRegression
{
    public static int RunChecks()
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new InvalidOperationException(why); }
        foreach (string text in new[] {
            "正面に大きなモニターがありますね。デスクトップ画面のようです。左側には白い棚があって、右側には小さな植物が飾られています。静かで整然とした部屋です。",
            "確認しました。先ほどの照明は正常に点灯していますよ。",
            "好的。报告已经保存。我会继续检查录音里的其它内容。",
            "OK. I saved the report. I will check the rest of the recording next.",
            "你说的‘停下’是在引用原话。当前任务没有改变。" })
        foreach (int packet in new[] { 1, 7, text.Length })
        {
            var parts = new List<string>(); string pending = "";
            for (int at = 0; at < text.Length; at += packet)
            {
                pending += text.Substring(at, Math.Min(packet, text.Length - at));
                int split;
                while ((split = SpeechChunkPolicy.Boundary(pending, parts.Count == 0, 24)) >= 0)
                { parts.Add(pending.Substring(0, split + 1)); pending = pending.Substring(split + 1); }
            }
            if (pending.Length > 0) parts.Add(pending);
            Check(string.Concat(parts) == text, "Chunk policy lost or reordered words at packet " + packet);
            Check(parts.All(SpeechChunkPolicy.Bounded), "One bounded reply became a long prepared paragraph.");
            if (text.StartsWith("確認")) Check(parts.Count == 1, "Short acknowledgement must share its substantive clause.");
        }
        // Reproduce a partial final PCM block while HTTP EOF has not arrived.
        var handlerType = typeof(GPTSoVITSFASTAPI).GetNestedType("PcmStreamingDownloadHandler", BindingFlags.NonPublic);
        object Handler() => Activator.CreateInstance(handlerType, true);
        void Feed(object handler, int samples, bool header)
        {
            byte[] data;
            using (var stream = new System.IO.MemoryStream()) using (var writer = new System.IO.BinaryWriter(stream))
            {
                if (header) {
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36);
                    writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                    writer.Write((short)1); writer.Write((short)1); writer.Write(32000); writer.Write(64000);
                    writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(0);
                }
                for (int i = 0; i < samples; i++) writer.Write((short)1234);
                data = stream.ToArray();
            }
            handlerType.GetMethod("ReceiveData", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(handler, new object[] { data, data.Length });
        }
        foreach (bool morePcm in new[] { false, true })
        {
            object handler = Handler();
            try {
                Feed(handler, 300, true); var block = new float[400];
                handlerType.GetMethod("ReadSamples").Invoke(handler, new object[] { block });
                Check(block.Take(300).All(v => Math.Abs(v - 1234f / 32768) < .000001f) && block.Skip(300).All(v => v == 0),
                    "Partial reads must deliver all available PCM before padding.");
                Check((long)handlerType.GetProperty("LastRealOutputSamplePosition").GetValue(handler) == 300,
                    "Final drain excludes padding after actual PCM.");
                if (morePcm) Feed(handler, 200, false);
                handlerType.GetMethod("MarkInputComplete").Invoke(handler, null);
                handlerType.GetMethod("ReadSamples").Invoke(handler, new object[] { new float[200] });
                Check((int)handlerType.GetProperty("ConfirmedGapCount").GetValue(handler) == (morePcm ? 1 : 0),
                    "Only silence between real PCM blocks is a confirmed speech gap.");
                Check(Math.Abs((float)handlerType.GetProperty("InsertedSilenceSeconds").GetValue(handler) - (morePcm ? 100f / 32000 : 0)) < .000001f,
                    "Gap duration is the actual missing tail, not a whole block or post-speech padding.");
            } finally { ((IDisposable)handler).Dispose(); }
        }
        var host = new GameObject("Continuity policy regression"); host.SetActive(false);
        try
        {
            var tts = host.AddComponent<GPTSoVITSFASTAPI>();
            var factory = typeof(GPTSoVITSFASTAPI).GetMethod("CreateSpeechRequest", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (int mode in new[] { 0, 3 })
            {
                var shortRequest = (GPTSoVITSFASTAPI.RequestData)factory.Invoke(tts, new object[] { "確認しました。先ほどの照明は正常に点灯していますよ。", "ja", mode });
                var longRequest = (GPTSoVITSFASTAPI.RequestData)factory.Invoke(tts, new object[] { new string('文', 100), "zh", mode });
                Check(shortRequest.text_split_method == "cut0" && longRequest.text_split_method == "cut5",
                    "Server split policy must depend on bounded speaking length, not the device task or language.");
            }
            var focus = typeof(ChatQW).GetMethod("TryFocusCurrentUserBodyPlanning", BindingFlags.Static | BindingFlags.NonPublic);
            var input = JObject.Parse(@"{'autonomous':false,'currentUserText':'Follow me while staying two metres away.','currentRequestSemantics':{'source':'independent-first-reply-current-request-review','requestKind':'action','actionAuthorization':false,'planningFocus':{'kind':'current-user-body','currentUserText':'Follow me while staying two metres away.'}},'sceneSpatialMemory':{'viewSamples':[{'captureId':'old','cameraPosition':{'x':2,'y':1,'z':0},'futureConstraint':{'doNotEnter':'private-room'}}]},'currentFacts':{'userSupportVerified':false}}");
            object[] args = { input.ToString(), null };
            Check((bool)focus.Invoke(null, args), "Complete current body request should focus.");
            var view = JObject.Parse((string)args[1]);
            Check(view.SelectToken("sceneSpatialMemory.viewSamples[0].cameraPosition") == null &&
                (string)view.SelectToken("sceneSpatialMemory.viewSamples[0].futureConstraint.doNotEnter") == "private-room" &&
                JToken.DeepEquals(view["currentFacts"], input["currentFacts"]), "Body focus lost an unknown nested constraint or current support.");
        }
        finally { UnityEngine.Object.DestroyImmediate(host); }
        return checks;
    }
}
