using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatQW
{
    [SerializeField, Tooltip("Use stage-owned structured contracts. Role asset edits never select the wire protocol.")]
    private bool m_CompactFormalRules = true;
    [SerializeField, Tooltip("Speech-only Semantic IR after committed spatial decisions. Existing fact review still owns playback.")]
    private bool m_StreamCommittedRoleDecision = true;
    [SerializeField, Tooltip("Compile recording-singing decisions through the existing goal/material/runtime checks.")]
    private bool m_EnableRecordingSingingDecision = true;
    public override bool SupportsRecordingSingingDecision => m_EnableRecordingSingingDecision && m_CompactFormalRules && !m_EnableSemanticRoleDecision;

    private List<SendData> ProjectFormalSource(FormalResponseStage stage, ref string context,
        out bool speechRecords, out string profileReason, out int originalSystemTokens)
    {
        speechRecords = false;
        profileReason = "legacy-disabled-or-custom";
        originalSystemTokens = 0;
        // The old experimental whole-JSON flag remains a separate opt-in.
        if (!m_CompactFormalRules || m_EnableSemanticRoleDecision)
            return m_DataList;
        if (stage == FormalResponseStage.RecordingSingingDecision &&
            SupportsRecordingSingingDecision && RecordingDecisionContext != null)
        {
            // This route owns a complete input/material/role-choice snapshot and
            // its own protocol. General prompt assets, stored history equality,
            // and a legacy output-contract suffix cannot select its wire format.
            context = RecordingSingingDecisionCompiler.Prompt(RecordingDecisionContext);
            originalSystemTokens = EstimateTokens(BuildCharacterPrompt());
            profileReason = "recording-singing-ir-v1";
            return new List<SendData> { new SendData("system",
                "你是录唱素材规划器，负责按专用协议把输入中的角色选择落实成可审核提案，不重新扮演或塑造角色。" +
                "事实、素材身份与动作只依照本轮快照和专用协议；不杜撰曲名、歌词或录音结论。") };
        }
        bool stream = stage == FormalResponseStage.CommittedSpatialReply && m_StreamCommittedRoleDecision;
        bool routing = stage == FormalResponseStage.FirstReplyRouting;
        string original = BuildCharacterPrompt();
        string projected = stream || routing
            ? FormalPromptProfile.StructuredRoleSystem(BuildRoleDecisionPersona(), BuildRoleDecisionLanguageRules(), BuildRoleDecisionBehaviorRules())
            : BuildSystemPrompt();
        if (string.IsNullOrWhiteSpace(projected))
            projected = FormalPromptProfile.RolePreferences(m_Prompt ?? "") + "\n" +
                FormalPromptProfile.LegacyEnvelope + "\n" + PromptProtocolAssets.GeneralTools();
        if (stream)
        {
            string oldContract = SpeechText.OutputContract + "\n\n" + RoleOutputPublicBoundary.OutputContract;
            if ((context ?? "").EndsWith(oldContract, StringComparison.Ordinal))
                context = context.Substring(0, context.Length - oldContract.Length);
            // Missing/changed old suffix cannot demote the stage to tool XML.
            context = (context ?? "") + "\n" + RoleDecisionSpeechStream.Contract;
        }
        var result = new List<SendData>();
        bool replaced = false;
        foreach (var message in m_DataList)
        {
            if (message != null && message.role == "system" && !replaced)
            { result.Add(new SendData("system", projected)); replaced = true; }
            else if (message != null && message.role == "system" && (stream || routing))
                result.Add(new SendData("system", FormalPromptProfile.RolePreferences(message.content)));
            else result.Add(message);
        }
        if (!replaced) result.Insert(0, new SendData("system", projected));
        speechRecords = stream;
        originalSystemTokens = EstimateTokens(original);
        profileReason = stream ? "committed-speech-ir-v1" :
            routing ? "compact-first-reply-route" : "explicit-general-tools-v2";
        return result;
    }

    private string BuildRecordingDecisionRequestJson(List<SendData> source, bool stream, string prompt, string executionFeedback)
    {
        // Split a program-authored exact packet, never search or strip arbitrary
        // user text. This makes the only action policy system and all observations
        // user data; both current input and competing recordings remain complete.
        if (!RecordingSingingDecisionCompiler.TrySplitPrompt(prompt, out string contract, out string factsJson))
            throw new InvalidOperationException("recording-decision-packet-not-exact");
        var facts = JObject.Parse(factsJson);
        if (!string.IsNullOrWhiteSpace(executionFeedback)) facts["execution_feedback"] = executionFeedback;
        string expression = source.Find(m => m != null && m.role == "system")?.content ?? "";
        var sb = new System.Text.StringBuilder(2048);
        sb.Append('{').Append("\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":").Append(stream ? "true" : "false");
        if (stream) sb.Append(",\"stream_options\":{\"include_usage\":true}");
        sb.Append(",\"enable_thinking\":false");
        sb.Append(",\"temperature\":0,\"max_tokens\":512");
        sb.Append(",\"messages\":[");
        AppendMessage(sb, new SendData("system", expression + "\n" + contract)); sb.Append(',');
        AppendMessage(sb, new SendData("user", facts.ToString(Newtonsoft.Json.Formatting.None))); sb.Append(']');
        AppendReasoningControls(sb, false); AppendSlot(sb, k_SlotMainConversation); sb.Append('}');
        return sb.ToString();
    }

    private void ReportFormalPromptProjection(FormalResponseStage stage, string profile, int originalTokens,
        PromptBudgetReport report, int bytes, bool records)
    {
        if (!m_LogRequestStats) return;
        Debug.Log("[LLM/FormalPromptProfile] " + new JObject {
            ["stage"] = stage.ToString(), ["profile"] = profile,
            ["originalSystemTokensEstimate"] = originalTokens > 0 ? (int?)originalTokens : null,
            ["wireSystemTokensEstimate"] = report.systemTokens,
            ["wirePromptTokensEstimate"] = report.estimatedTotal,
            ["requestBytes"] = bytes, ["streamingSemanticRecords"] = records,
            ["sourceRulesAndHistoryUnchanged"] = true,
            ["scope"] = "Estimates are not measured prefill latency; playback still uses the existing delivery gates."
        }.ToString(Newtonsoft.Json.Formatting.None));
    }
}
