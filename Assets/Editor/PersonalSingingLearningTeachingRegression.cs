using System;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Offline checks: inactive client only, no HTTP, AudioSource, mic or human data.
public static class PersonalSingingLearningTeachingRegression
{
    public static int Run(string root)
    {
        int passed = 0;
        void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); passed++; }
        JObject Region(int a, int b, string label) => new JObject { ["start_sample"] = a, ["end_sample"] = b, ["label"] = label, ["tolerance_samples"] = 0 };
        var original = new JArray(Region(0, 100, "speech"), Region(100, 250, "singing"), Region(250, 300, "environment"));
        Check(PersonalSingingLearningTeachingDraft.TryReplace(original, 300, 80, 130, "uncertain", out var replacement), "replace accepts a genuine interval spanning two classes");
        Check(replacement.Count == 4 && (int)replacement[0]["end_sample"] == 80 && (int)replacement[2]["start_sample"] == 130 && (string)replacement[3]["label"] == "environment", "replace splits both boundary classes without losing trailing environment");
        Check((int)original[0]["end_sample"] == 100 && (int)original[1]["start_sample"] == 100, "pure replace leaves source untouched");
        Check(!PersonalSingingLearningTeachingDraft.TryReplace(original, 300, 200, 100, "speech", out _), "reverse pointer interval rejected");
        Check(!PersonalSingingLearningTeachingDraft.TryReplace(original, 300, 0, 301, "speech", out _), "original source bounds enforced");
        Check(PersonalSingingLearningTeachingDraft.TryResize(original, 300, 100, 250, 120, 260, out var resized), "resize supports shrink left and expand right");
        Check((string)resized[1]["label"] == "uncertain" && (int)resized[1]["start_sample"] == 100 && (int)resized[1]["end_sample"] == 120, "newly uncovered interval is uncertain rather than inferred speech");
        Check((int)resized[2]["end_sample"] == 260 && (int)resized[3]["start_sample"] == 260, "resized class clips overlapping next class");
        Check(PersonalSingingLearningProtocol.ValidateRanges(resized, 300, true, out _), "resize retains exact source coverage and no overlap");

        var go = new GameObject("Inactive teaching proposal checks"); go.SetActive(false);
        var client = go.AddComponent<PersonalSingingLearningClient>();
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        object Read(string name) => typeof(PersonalSingingLearningClient).GetField(name, fields).GetValue(client);
        void Write(string name, object value) => typeof(PersonalSingingLearningClient).GetField(name, fields).SetValue(client, value);
        object Call(string name, params object[] args) => typeof(PersonalSingingLearningClient).GetMethod(name, fields).Invoke(client, args);
        JObject Recording(string id) => new JObject { ["recording_id"] = id, ["audio_sha256"] = "synthetic-source", ["sample_count"] = 300, ["sample_rate"] = 100, ["annotation_revision"] = 0 };
        var proposal = new JObject { ["proposal_id"] = "test-proposal", ["recording_id"] = "previous", ["audio_sha256"] = "synthetic-source", ["sample_count"] = 300, ["sample_rate"] = 100, ["annotation_revision"] = 0, ["regions"] = original.DeepClone(), ["summary"] = "测试候选" };
        try
        {
            Write("<ProfileId>k__BackingField", "fixture-profile");
            Write("<Selected>k__BackingField", Recording("previous"));
            int version = client.DraftRevision;
            var stale = (JObject)proposal.DeepClone(); stale["audio_sha256"] = "other-source";
            Check(!(bool)Call("ApplyProposal", stale, version), "proposal from another original cannot be shown");
            stale = (JObject)proposal.DeepClone(); stale["annotation_revision"] = 2;
            Check(!(bool)Call("ApplyProposal", stale, version), "proposal bound to another human revision rejected");
            Check((bool)Call("ApplyProposal", proposal, version) && client.Draft.Count == 3 && !client.HasUnsavedEdits, "bound proposal becomes visible but is not an artificial user edit or human save");
            int proposalVersion = client.DraftRevision;
            Check(client.ReplaceRange(100, 150, "speech") && client.HasUnsavedEdits, "manual correction marks current source draft dirty");
            Check(!(bool)Call("ApplyProposal", proposal, proposalVersion), "late proposal cannot overwrite manual correction");
            client.UndoDraft();
            Check(JToken.DeepEquals(client.Draft, original), "undo restores exact prior proposal sample boundaries");
            Check(client.ResizeRange(100, 250, 120, 260), "client exposes safe drag resize");
            int submitted = client.DraftRevision;
            client.ReplaceRange(0, 20, "environment");
            Call("TeachingConfirmed", submitted);
            Check(client.HasUnsavedEdits && (string)client.Draft[0]["label"] == "environment", "late save receipt does not clear or replace later manual changes");
            Call("TeachingConfirmed", client.DraftRevision);
            Check(!client.HasUnsavedEdits && client.CurrentProposal == null, "matching save clears submitted dirty state and old proposal provenance");

            Write("<Selected>k__BackingField", Recording("previous"));
            Write("<Draft>k__BackingField", original.DeepClone());
            Write("latestCapturedRecordingId", "previous");
            Call("SnapshotSpokenCorrectionTarget");
            Write("latestCapturedRecordingId", "correction-audio");
            Write("<Selected>k__BackingField", Recording("correction-audio"));
            Check(client.TryApplySpokenCorrection("刚才整段都是在说话", true, out var receipt), "recognized spoken correction starts a review");
            Check((string)Read("pendingCorrectionTarget") == "previous" && (string)client.Selected["recording_id"] == "correction-audio", "voice correction freezes predecessor even after its own upload becomes current");
            Check(!client.HasUnsavedEdits && receipt.Contains("尚未保存"), "pending target lookup cannot relabel correction audio or claim saved learning");
            Write("<Selected>k__BackingField", Recording("previous"));
            Call("UpdatePendingCorrection");
            Check(client.Draft.Count == 1 && (string)client.Draft[0]["label"] == "speech" && client.HasUnsavedEdits, "once predecessor is available only that original gets proposed correction");
            Check((int)client.Selected["annotation_revision"] == 0, "conversational correction never increments human annotation revision");
            Write("latestCapturedRecordingId", "correction-audio");
            Call("SnapshotSpokenCorrectionTarget");
            Check((string)Read("spokenCorrectionTargetId") == "previous", "second spoken correction keeps the recording already under review, not preceding correction audio");
            Check(client.TryApplySpokenCorrection("刚才声音判断错了", true, out receipt) && (string)client.Selected["recording_id"] == "previous", "review-only verbal request also uses stable original target");
            Write("<Busy>k__BackingField", true);
            client.SelectRecording("another-recording", true);
            Check((string)client.Selected["recording_id"] == "previous" && client.HasUnsavedEdits, "busy selection cannot clear selected source or unsaved edit");
            Write("<Busy>k__BackingField", false);

            Write("<HasUnsavedEdits>k__BackingField", false);
            Write("<Draft>k__BackingField", new JArray(Region(0, 300, "uncertain")));
            Check(client.TryApplySpokenCorrection("刚才开头那句是在说话，后面才是在唱歌", false, out receipt), "ordered correction opens review");
            Check(client.Draft.Count == 1 && (string)client.Draft[0]["label"] == "uncertain" && receipt.Contains("没有猜时间"), "ordered correction without an acoustic anchor never invents midpoint");
            Write("<Draft>k__BackingField", original.DeepClone());
            Check(client.TryApplySpokenCorrection("刚才开头那句是在说话，后面才是在唱歌", false, out receipt), "ordered correction with actual boundary is staged");
            Check((int)client.Draft[1]["start_sample"] == 100 && (string)client.Draft[2]["label"] == "environment", "ordered correction uses actual onset and preserves environmental tail");
            Check(!client.TryApplySpokenCorrection("刚才那首歌很好听，再唱一首吧", false, out _), "ordinary singing conversation never enters correction side channel");
            Check(Read("waveformRequest") == null && Read("proposalRequest") == null && Read("previewSource") == null, "checks did not start networking or playback");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
        return passed;
    }
}
