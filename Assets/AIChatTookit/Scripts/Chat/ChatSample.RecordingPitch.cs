using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatSample
{
    private JObject BuildRecordingPitchFacts()
    {
        var samples = new JArray();
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        if (sense != null) foreach (var source in sense.DescribePracticePhrases())
        {
            if (!m_PracticePitchStates.TryGetValue(source.StableId, out var state) ||
                !state.FullyPlayed || state.Revision <= 0 || !Finite(state.CenterMidi) || state.CenterMidi <= 0) continue;
            if (!m_CompletedRecordingPlaybackFacts.TryGetValue(source.ClipRef, out var played) ||
                (int?)played["revision"] != source.Revision) continue;
            samples.Add(new JObject { ["clip"] = source.ClipRef, ["clipRevision"] = source.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["pitchRevision"] = state.Revision, ["centerMidi"] = state.CenterMidi,
                ["measured"] = state.Measured, ["fullyPlayed"] = true,
                ["playbackSequence"] = played["sequence"]?.DeepClone() });
        }
        return new JObject { ["samples"] = samples,
            ["scope"] = "Actual fully played recording versions. centerMidi is measured output center when measured=true, otherwise the rendered plan estimate. It is not key/tonality, tonic, scale or pitch-class evidence." };
    }

    private static bool TryReadRecordingMusic(string attrs, out JObject binding, out string failure)
    {
        binding = null; failure = "";
        string encoded = ReadToolAttribute(attrs, "recording_music");
        if (string.IsNullOrEmpty(encoded)) return true;
        try
        {
            if (encoded.Length > 16000) throw new FormatException();
            binding = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            if (!(binding["spec"] is JObject) || !(binding["sourceVersions"] is JArray) || !(binding["playbackBases"] is JArray))
                throw new FormatException();
            return true;
        }
        catch (Exception error) when (error is FormatException || error is JsonException || error is ArgumentException)
        { binding = null; failure = "结构化音乐目标绑定无效，未提交演唱。"; return false; }
    }

    private bool ValidateRecordingMusicBinding(JObject binding, string refs, out string failure)
    {
        failure = "";
        if (binding == null) return true;
        var context = new RecordingSingingDecisionContext { Facts = new JObject { ["music"] = BuildRecordingPitchFacts() }.ToString(Formatting.None) };
        context.Materials.AddRange(SnapshotRecordingSingingMaterials(m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText));
        if (!RecordingSingingDecisionCompiler.TryBindRecordingMusic(binding["spec"] as JObject, context,
                (refs ?? "").Split(','), out var current, out var reason) || current == null || !JToken.DeepEquals(binding, current))
        { failure = "音乐目标的素材/参考播放版本或中心观测已变化，未执行；需根据新事实重新核对目标。" + reason; return false; }
        return true;
    }

    private bool BindRecordingMusicGoal(SingingGoal goal, string attrs, out string failure)
    {
        if (!TryReadRecordingMusic(attrs, out var binding, out failure)) return false;
        goal.music = binding;
        return ValidateRecordingMusicBinding(binding, goal.refs, out failure);
    }

    private bool MatchReviewedRecordingMusic(SingingGoal goal, JObject expected, out string failure)
    {
        failure = "";
        var specification = expected?["music"] as JObject;
        if (goal.music == null)
        {
            if (specification == null || JToken.DeepEquals(specification,
                new JObject { ["mode"] = "preserve", ["reference"] = "none", ["interval"] = "none" })) return true;
            failure = "独立审核要求音乐调整，但实际草案没有绑定该音乐目标。"; return false;
        }
        if (!JToken.DeepEquals(specification, goal.music["spec"]))
        { failure = "独立审核的音乐目标/参考录音/相对音程与实际草案不一致。"; return false; }
        return ValidateRecordingMusicBinding(goal.music, goal.refs, out failure);
    }

    private bool ValidateRecordingMusicRequest(AgentHumBackRequest request, SingingGoal goal, out string failure)
    {
        failure = "";
        if (!JToken.DeepEquals(request.RecordingMusic, goal.music))
        { failure = "演唱动作与已审音乐目标不一致，未执行。"; return false; }
        return ValidateRecordingMusicBinding(request.RecordingMusic, request.ClipRefs, out failure);
    }

    private bool TryResolveBoundRecordingPitch(AgentHumBackRequest request,
        SenseVoiceSpeechToText.PracticeComposition composition, out int[] shifts, out float[] centers,
        out string note, out string failure)
    {
        shifts = null; centers = null; note = ""; failure = "";
        if (!IsPracticeHumMode(request.Mode))
        { failure = "结构化音乐目标仅适用于已绑定的练唱录音。"; return false; }
        if (!ValidateRecordingMusicBinding(request.RecordingMusic, request.ClipRefs, out failure)) return false;
        if (composition?.SegmentMedians == null || composition.SegmentWavs == null ||
            composition.SegmentMedians.Count != composition.SegmentWavs.Count || composition.SegmentMedians.Count == 0)
        { failure = "所选录音缺少逐段音高测量，不能计算本次音乐目标。"; return false; }
        var spec = (JObject)request.RecordingMusic["spec"];
        var bases = (JArray)request.RecordingMusic["playbackBases"];
        bool matching = (string)spec["mode"] == "match_recording";
        float delta = 0f;
        if (!matching && !TryParseMusicalInterval((string)spec["interval"], out delta))
        { failure = "音乐目标的相对音程无效。"; return false; }
        int count = composition.SegmentMedians.Count;
        if ((!matching && bases.Count != count) || request.ClipRefs.Split(',').Length != count)
        { failure = "音乐目标的素材顺序与实际音频编排不同。"; return false; }
        var sense = m_ChatSettings?.m_SpeechToText as SenseVoiceSpeechToText;
        var phrases = sense?.DescribePracticePhrases();
        shifts = new int[count]; centers = new float[count];
        for (int i = 0; i < count; i++)
        {
            centers[i] = (float)bases[matching ? 0 : i]["centerMidi"] + delta;
            int index = composition.SegmentSourceIndices != null && i < composition.SegmentSourceIndices.Count
                ? composition.SegmentSourceIndices[i] : i + 1;
            var source = FindPracticePhraseInfo(phrases, index);
            if (source == null || source.ClipRef != request.ClipRefs.Split(',')[i])
            { failure = "音乐目标绑定的素材与真实编排位置不一致。"; shifts = null; centers = null; return false; }
            float bias = 0;
            bool calibrated = TryGetReliablePracticeRendererBias(source.StableId, out bias);
            int shift = QuantizeCalibratedPitchShift(composition.SegmentMedians[i], centers[i], calibrated ? bias : 0);
            if (shift < -12 || shift > 12)
            { failure = "音乐目标超过转换器±12半音量程；未静默截断或执行。"; shifts = null; centers = null; return false; }
            shifts[i] = shift;
        }
        note = matching ? "按固定参考录音已播放版本的演唱中心匹配各段；这不等于识别或改变音乐调性。"
            : "按明确命名音程相对固定的已播放版本移调；底层半音由程序计算。";
        m_ResponseTrace?.Mark("singing.music_resolved", new JObject { ["binding"] = request.RecordingMusic.DeepClone(),
            ["targetCenters"] = new JArray(centers), ["svcShifts"] = new JArray(shifts), ["played"] = false });
        return true;
    }
}
