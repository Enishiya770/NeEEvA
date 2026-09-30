using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

public static partial class RecordingSingingDecisionCompiler
{
    public const string BoundaryRefinement = "__recording_boundary_refinement__";
    public const string BoundaryContract =
        "\n去除所选录音头尾讲话且现有current/clean/expanded不能准确表达时，仍提交perform。" +
        "修改已准备/已播放版本用range=current；新录音evidence_only选真实可恢复的clean或expanded作为粗范围，不填不存在的current。" +
        "boundary_edit.kind=trim，target=last_playback表示明确修改刚才实际播出的某一条，或material表示所选当前版本。" +
        "last_playback只是已完成播放的候选来源，不是本轮选择或授权；按完整目标选materials中的真实key，不自动选最近。" +
        "edge=lead/tail，exclude_text逐字摘取要移除的录音讲话，keep_text逐字摘取紧邻要保留的完整歌词短句，不能填泛化指令或只写一个字。" +
        "它们是文本定位线索，不是声学边界；程序核对固定原PCM并局部重识别，只有真实间隙与两侧文字都支持才提出window交独审。" +
        "current只收缩已选范围；clean/expanded尚未选定最终边界，程序将待改一边的原始录音纳入分析以寻找口语，另一端保持粗范围边界，结果不等于已确认来源或已播放。" +
        "不要猜秒数，不把known-source边界细化交source_expansion或反复custom_range。无法给出足够两侧原文时range_uncertain具体澄清。" +
        "不裁边时boundary_edit={kind:none,target:material,edge:lead,exclude_text:'',keep_text:''}。只支持收缩一个边缘；删除内部段落/跨多素材不可伪装成裁边。" +
        "boundary_obligations记录同一真实PCM尚未完成的边界核对；重试、换clip续接版本或改none/clean不能取消它。继续按同源证据裁边，或range_uncertain说明未解。用户明确改变保留要求时可提交boundary_resolution，仍须独立审核，不把重复催促视作改变。";

    public static bool SegmentBoundaryAvailable(RecordingSingingDecisionContext context) =>
        context.SupportsSegmentBoundarySelection && context.RefineSegments != null;

    private static void AddRecordingBoundarySchema(JObject p, string decision, string range, RecordingSingingDecisionContext context)
    {
        JObject Enum(params string[] values) => new JObject { ["type"] = "string", ["enum"] = new JArray(values) };
        JObject Legacy(string kind) => new JObject { ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JObject { ["kind"] = Enum(kind),
                ["target"] = range == "current" ? Enum("material", "last_playback") : Enum("material"), ["edge"] = Enum("lead", "tail"),
                ["exclude_text"] = kind == "none" ? Enum("") : new JObject { ["type"] = "string", ["minLength"] = RecordingSegmentIdentity.MinimumExcludedCharacters,
                    ["maxLength"] = RecordingSegmentIdentity.MaximumAnchorCharacters },
                ["keep_text"] = kind == "none" ? Enum("") : new JObject { ["type"] = "string", ["minLength"] = RecordingSegmentIdentity.MinimumKeptCharacters,
                    ["maxLength"] = RecordingSegmentIdentity.MaximumAnchorCharacters } },
            ["required"] = new JArray("kind", "target", "edge", "exclude_text", "keep_text") };
        var choices = new JArray(Legacy("none"));
        if (decision == "perform")
        {
            choices.Add(Legacy("trim"));
            if (SegmentBoundaryAvailable(context))
            {
                var evidence = context.Materials.Where(m => CanSubmitRange(m, range)).Select(m => BoundaryMaterialFacts(m)["boundaryEvidence"])
                    .OfType<JObject>().Where(e => (int?)e["schema"] == RecordingSegmentIdentity.Schema).ToArray();
                var ids = evidence.SelectMany(e => (e["segments"] as JArray ?? new JArray()).OfType<JObject>())
                    .Select(s => (string)s["segment_id"]).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToArray();
                if (ids.Length > 0) choices.Add(new JObject { ["type"] = "object", ["additionalProperties"] = false,
                    ["properties"] = new JObject { ["kind"] = Enum("segments"), ["target"] = Enum("material"),
                        ["evidence_revision"] = Enum(evidence.Select(e => (string)e["evidence_revision"]).Where(v => !string.IsNullOrEmpty(v)).Distinct().ToArray()),
                        ["keep_segment_ids"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 32, ["uniqueItems"] = true, ["items"] = Enum(ids) },
                        ["exclude_segment_ids"] = new JObject { ["type"] = "array", ["maxItems"] = 32, ["uniqueItems"] = true, ["items"] = Enum(ids) } },
                    ["required"] = new JArray("kind", "target", "evidence_revision", "keep_segment_ids", "exclude_segment_ids") });
            }
        }
        p["boundary_edit"] = choices.Count == 1 ? choices[0].DeepClone() : new JObject { ["anyOf"] = choices };
    }

    private static bool ValidateRecordingBoundary(JObject v, RecordingSingingDecisionContext c,
        IList<RecordingSingingMaterial> materials, out bool refine, out string error)
    {
        refine = false; error = "boundary-edit-invalid";
        if (v.Property("boundary_edit") == null) return ValidateBoundaryObligations(v, c, materials, out error);
        var b = v["boundary_edit"] as JObject;
        if ((string)b?["kind"] == "segments")
        {
            error = "segment-boundary-provider-unavailable";
            if (!SegmentBoundaryAvailable(c)) return false;
            error = "segment-selection-invalid";
            if ((string)v["decision"] != "perform" || materials.Count != 1 || !CanSubmitRange(materials[0], (string)v["range"])) return false;
            if (!RecordingSegmentIdentity.TrySelect(b, BoundaryMaterialFacts(materials[0])["boundaryEvidence"] as JObject, out _, out error)) return false;
            refine = true; error = null; return true;
        }
        var fields = new[] { "kind", "target", "edge", "exclude_text", "keep_text" };
        if (b == null || b.Count != fields.Length || fields.Any(f => b[f]?.Type != JTokenType.String)) return false;
        string kind = (string)b["kind"], target = (string)b["target"], edge = (string)b["edge"];
        string exclude = (string)b["exclude_text"], keep = (string)b["keep_text"];
        if (!new[] { "material", "last_playback" }.Contains(target) || !new[] { "lead", "tail" }.Contains(edge)) return false;
        if (kind == "none") { if (exclude.Length + keep.Length != 0) return false;
            return ValidateBoundaryObligations(v, c, materials, out error); }
        if (kind != "trim" || (string)v["decision"] != "perform" || materials.Count != 1) return false;
        if (!RecordingSegmentIdentity.ValidLegacyAnchors(exclude, keep))
        { error = "boundary-anchor-shape-invalid"; return false; }
        string range = (string)v["range"];
        if (!CanSubmitRange(materials[0], range) || target == "last_playback" && range != "current")
        { error = "boundary-source-range-unavailable"; return false; }
        var material = materials[0];
        if (!BoundaryAnchorInMaterial(material, exclude, false) || !BoundaryAnchorInMaterial(material, keep, true))
        { error = "boundary-anchors-not-in-source"; return false; }
        if (target == "last_playback" && !(c.LastPlaybackBinding?["materials"] is JArray entries && entries.OfType<JObject>().Any(e =>
            (string)e["key"] == material.Key && (string)e["revision"] == material.Revision && (bool?)e["currentVersionMatches"] == true)))
        { error = "boundary-last-playback-stale"; return false; }
        refine = true; error = null; return true;
    }

    private static bool CompileVerifiedBoundary(JObject v, RecordingSingingDecisionContext c,
        RecordingSingingMaterial material, out string attributes, out string error)
    {
        attributes = ""; error = "boundary-result-unbound";
        var b = c.VerifiedBoundary;
        if ((string)b?["status"] != "resolved" || (string)b["source_id"] != material.ClipRef ||
            (string)b["source_revision"] != material.Revision || !JToken.DeepEquals(b["edit"], v["boundary_edit"]) ||
            (string)b["selection_range"] != (string)v["range"] ||
            !(b["range"] is JObject range) || !PcmNumber(range["start_seconds"], out double start) ||
            !PcmNumber(range["end_seconds"], out double end) || start < 0 || end - start < 1) return false;
        if (!ValidateBoundaryObligations(v, c, new[] { material }, out error)) return false;
        attributes = " start_seconds=\"" + start.ToString("R", CultureInfo.InvariantCulture) +
            "\" end_seconds=\"" + end.ToString("R", CultureInfo.InvariantCulture) + "\"";
        error = null; return true;
    }

    public static string BoundaryUnresolvedReply(string reason)
    {
        // No uncertain ASR guess or internal source identifier enters public speech.
        bool source = reason == "boundary-last-playback-stale" || reason == "source_version_changed" ||
            reason == "source_binding_unavailable" || reason == "boundary_result_binding_mismatch" || reason == "boundary-result-unbound";
        bool anchors = reason == "boundary-anchors-not-in-source";
        bool unavailable = reason == "boundary-source-range-unavailable" || reason == "boundary_source_audio_unavailable" ||
            reason == "boundary_source_pitch_unavailable";
        bool service = reason == "boundary_service_unavailable";
        bool contract = reason == "boundary-edit-invalid" || reason == "boundary_compilation_unresolved" || reason == "boundary-anchor-shape-invalid";
        if (reason == "source_evidence_changed") return "<lang code=\"zh\"/><speech mode=\"independent\"/><say>这份录音的分段识别资料需要重新核对，原录音还在；这次没有裁剪或重唱。</say>";
        if (reason == "boundary-obligation-unresolved") return "<lang code=\"zh\"/><speech mode=\"independent\"/><say>这份录音里要去掉的讲话还没有核对完成，我还不能把原来的粗范围当作已处理好的歌声重唱。</say>";
        string text = source ? "这次录音的版本或对应关系已经变化，我还需要重新核对本次选中的那份录音。这次没有裁剪或重唱。" :
            anchors ? "这次识别出的文字还不能可靠对应到要保留的歌声；原录音仍保留，这次没有裁剪或重唱。" :
            unavailable ? "这份录音的所选范围还缺少可用于处理的音频或音高资料，因此这次没有裁剪或重唱。" :
            service ? "这次录音边界分析服务没有返回有效结果，因此没有裁剪或重唱，原录音仍保留。" :
            contract ? "这次裁剪请求的处理格式没有通过检查，因此没有裁剪或重唱，原录音仍保留。" :
            "这次还没能可靠确定讲话和歌声的分界，因此没有裁剪或重唱，原录音仍保留。";
        return "<lang code=\"zh\"/><speech mode=\"independent\"/><say>" + text + "</say>";
    }
}
