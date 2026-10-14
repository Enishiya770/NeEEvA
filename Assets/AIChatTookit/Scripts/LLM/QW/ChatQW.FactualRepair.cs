using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

public partial class ChatQW
{
    // Polishing an engine-derived sentence needs that sentence and authored
    // style, not a replay of old imperatives or of the rejected hallucination.
    // Full dialogue/receipts stay resident and still feed the independent audit.
    private string BuildFactualRepairRequest(bool stream, string feedback, string context = null)
    {
        var packet=CommittedSpatialReplyProtocol.ReadRepairFeedback(feedback);
        string baseline=(string)packet?["factualBaseline"], language=(string)packet?["requiredOutputLanguage"];
        var bound = (bool?)packet?["receiptOnly"] == true ? CommittedSpatialReplyProtocol.ReadReplyFacts(context) : null;
        bool hasBaseline = !string.IsNullOrWhiteSpace(baseline);
        if ((!hasBaseline && bound == null) || (baseline?.Length ?? 0)>CommittedSpatialReplyProtocol.MaximumSpeechCharacters ||
            !(new[]{"ja","zh","en"}.Contains(language))) return null;
        var style=new StringBuilder();
        if(m_PromptFiles!=null) foreach(var file in m_PromptFiles)
            if(file!=null&&(string.Equals(file.name,"persona",StringComparison.OrdinalIgnoreCase)||string.Equals(file.name,"language",StringComparison.OrdinalIgnoreCase)))
                style.Append(file.text).Append('\n');
        if(style.Length==0 && !string.IsNullOrWhiteSpace(m_SystemSetting) &&
            SpatialGoalResultContextPolicy.EstimateTextTokens(m_SystemSetting)<=1000)
            style.Append(m_SystemSetting).Append('\n');
        style.Append("\nThis request only polishes an engine-derived factual sentence. Preserve its actor, recipient, tense, outcome and uncertainty. ")
            .Append("No new action, topic, location reassurance or future promise. Keep the factual baseline unchanged if no better wording is needed. ")
            .Append("The required language below controls both vocabulary and lang tag. Return one closed public envelope, no other content.");
        string writing = hasBaseline ? CommittedSpatialReplyProtocol.RepairWritingInstruction(feedback) :
            "Correct the unreleased result using only this bound current purpose and its receipts. " +
            "Report the real outcome and recorded cause. Do not resume historical commands, propose a retry or add a different action. " +
            "Output one closed public envelope with words and lang tag in requiredLanguage.\n" + new JObject {
                ["requiredLanguage"] = language, ["boundReplyFacts"] = bound,
                ["actualReview"] = packet["actualReview"]?.DeepClone(), ["guardRejectedReason"] = packet["guardRejectedReason"]?.DeepClone()
            }.ToString(Formatting.None);
        var request=new JObject { ["model"]=CurrentModelName,["stream"]=stream,["temperature"]=0,
            ["enable_thinking"]=false,["max_tokens"]=256,["messages"]=new JArray(
                new JObject{["role"]="system",["content"]=style.ToString()},
                new JObject{["role"]="user",["content"]=writing}) };
        if(stream)request["stream_options"]=new JObject{["include_usage"]=true};
        if(m_Backend==BackendType.Local) {
            request["grammar"]=SpatialGoalResultContextPolicy.OutputGrammar.Replace("(\"ja\" | \"zh\" | \"en\")","\""+language+"\"");
            request["chat_template_kwargs"]=new JObject{["enable_thinking"]=false};
            var slot=new StringBuilder();AppendSlot(slot,k_SlotAuxiliary);
            if(slot.Length>0)foreach(var p in JObject.Parse("{"+slot.ToString().TrimStart(',')+"}").Properties())request[p.Name]=p.Value;
        }
        string wire=request.ToString(Formatting.None);
        bool admitted=AdmitSceneSpeechWire(wire,out var budget);
        Debug.Log("[LLM/FactualRepair] "+new JObject{["baselineCharacters"]=baseline?.Length ?? 0,["historyMutated"]=false,
            ["boundResultFactsIncluded"]=!hasBaseline,["admitted"]=admitted,["wireBudget"]=budget,
            ["scope"]="One private style correction of an engine-derived sentence; full current facts still required for independent review."}.ToString(Formatting.None));
        return wire; // The normal final HTTP gate still enforces the full budget.
    }
}
