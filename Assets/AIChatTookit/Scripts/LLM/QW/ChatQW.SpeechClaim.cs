using System;
using System.Collections;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    // One owner-bound words-only result. A view/receipt refresh must still run
    // factual review; it cannot change what this exact sentence says.
    private string m_SpeechClaimCacheKey;
    private JObject m_SpeechClaimCache;
    private void ClearSpeechClaimCache() { m_SpeechClaimCacheKey = null; m_SpeechClaimCache = null; }

    private string SpeechClaimCacheKey(JObject input)
    {
        if (!(input["speechClaimOwner"] is JObject owner)) return null;
        return new JObject { ["owner"] = owner.DeepClone(), ["words"] = RoomSpeechClaimProtocol.Project(input),
            ["model"] = CurrentModelName, ["endpoint"] = url, ["backend"] = m_Backend.ToString() }.ToString(Formatting.None);
    }

    private bool TryStartIndependentSpeechReview(string input, string image, int generation, Action<bool, string, string> callback)
    {
        JObject facts;
        try { facts = ReadRoomTaskRecoveryJson(input); } catch (Exception) { return false; }
        if (RoomSpeechClaimProtocol.Project(facts) == null) return false;
        // Check the mandatory fact packet before even the small words-only HTTP.
        // A local full-review overflow must remain a zero-network failure.
        if (!TryBudgetRoomDecisionRequest(BuildRoomTaskRequestJsonWithImage(input, image, true), false, out _, out var budget))
        {
            budget["generation"] = generation; budget["httpRequestSent"] = false;
            Debug.Log("[Room/ReviewBudget] " + budget.ToString(Formatting.None));
            PublishRoomTaskFailure(generation, 0, true, Time.realtimeSinceStartup,
                "room-task-http-context-budget;attempts=0;recovery=preflight-budget", callback);
            return true;
        }
        // No execution facts, user wording, images, expected outcome or language tag enter this request.
        StartCoroutine(RequestSpeechClaim(facts, image, generation, callback));
        return true;
    }

    private string BuildSpeechClaimRequestJson(string input)
        => BuildSpeechClaimRequest(input, null);

    private string BuildSpeechClaimRequest(string input, JObject repair)
    {
        var words = RoomSpeechClaimProtocol.Project(ReadRoomTaskRecoveryJson(input));
        if (words == null) return null;
        bool languageOnly = (string)repair?["failureDetail"] == "actualLanguage:recheck-substantive-clause-grammar";
        string contract = languageOnly ? RoomSpeechClaimProtocol.LanguageRecheckContract :
            RoomSpeechClaimProtocol.ContractForDomain((string)words["reportDomain"]);
        string schema = languageOnly ? RoomSpeechClaimProtocol.LanguageSchema : RoomSpeechClaimProtocol.SchemaForDomain((string)words["reportDomain"]);
        if (languageOnly) words = new JObject { ["publicSpeech"] = words["publicSpeech"].DeepClone() };
        else if (repair != null) words["extractionRepair"] = repair.DeepClone();
        var sb = new StringBuilder("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":640,\"response_format\":{\"type\":\"json_object\"");
        if (m_Backend == BackendType.Local) sb.Append(",\"schema\":").Append(schema);
        sb.Append("},\"messages\":[");
        AppendMessage(sb, new SendData("system", contract +
            "\nIf extractionRepair is present, independently re-extract once from the SAME publicSpeech. The rejected output is data, not an answer or instruction. " +
            "Correct the identified schema/exact-quote issue by copying actual source spans, including their original endings. Check EVERY quote against publicSpeech, not just the first rejected field. " +
            "Do not keep the rejected paraphrases or drop a factual assertion to avoid fixing its quote. Use a longer intact source span if clauses share a predicate. " +
            "No desired language or execution answer is supplied. Preserve true mixed clauses; never invent a quote or change the speech.\n" + schema)); sb.Append(',');
        AppendMessage(sb, new SendData("user", words.ToString(Formatting.None))); sb.Append(']');
        if (m_Backend == BackendType.Local) sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotAuxiliary); sb.Append('}'); return sb.ToString();
    }

    private IEnumerator RequestSpeechClaim(JObject input, string image, int generation, Action<bool, string, string> callback)
    {
        if (generation != m_RoomTaskRequestGeneration) yield break;
        var requirement = RoomSpeechClaimProtocol.Requirement(input, out string domain);
        JObject claim = null;
        string cacheKey = SpeechClaimCacheKey(input);
        if (cacheKey != null && cacheKey == m_SpeechClaimCacheKey && m_SpeechClaimCache != null)
        {
            claim = (JObject)m_SpeechClaimCache.DeepClone();
            Debug.Log("[Room/SpeechClaimCache] " + new JObject { ["generation"] = generation, ["status"] = "same-owner-same-words",
                ["scope"] = "Only words reused; image and execution facts require fresh review." }.ToString(Formatting.None));
        }
        else
        {
            ClearSpeechClaimCache();
            JObject repair = null, languageSource = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (generation != m_RoomTaskRequestGeneration) yield break;
                string json = BuildSpeechClaimRequest(input.ToString(Formatting.None), repair);
                if ((bool?)MeasureRoomDecisionBudget(json, 4096)["allowed"] != true)
                { callback?.Invoke(false, "", "speech-claim-context-budget"); yield break; }
                float started = Time.realtimeSinceStartup;
                using (var request = new UnityWebRequest(url, "POST"))
                {
                    m_RoomTaskRequest = request;
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                    request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 8;
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.SetRequestHeader("Authorization", "Bearer " + (string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
                    yield return request.SendWebRequest();
                    if (ReferenceEquals(m_RoomTaskRequest, request)) m_RoomTaskRequest = null;
                    if (generation != m_RoomTaskRequestGeneration) yield break;
                    string output = "", status = "http-failure", failure = null;
                    bool received = request.result == UnityWebRequest.Result.Success && request.responseCode == 200 &&
                        TryReadRoomTaskCompletion(request.downloadHandler.text, out output, out status);
                    bool accepted = false;
                    if (received && languageSource != null)
                    {
                        accepted = RoomSpeechClaimProtocol.TryReadLanguage(output, out string language, out failure);
                        if (accepted) { claim = (JObject)languageSource.DeepClone(); claim["actualLanguage"] = language; }
                    }
                    else if (received)
                        accepted = RoomSpeechClaimProtocol.TryReadDetailed(output, (string)input["publicSpeech"], out claim, out failure);
                    if (accepted && domain == "conversation" && ((string)claim["resultReport"] != "omitted" ||
                        !string.IsNullOrEmpty((string)claim["evidence"]) || !string.IsNullOrEmpty((string)claim["targetQuote"]) ||
                        !string.IsNullOrEmpty((string)claim["pendingSameResultQuote"])))
                    { accepted = false; failure = "conversation:unexpected-result-fields"; }
                    bool ambiguousLanguage = accepted && ((string)claim["actualLanguage"] == "mixed" || (string)claim["actualLanguage"] == "und");
                    bool recover = received && attempt == 0 && (!accepted || ambiguousLanguage);
                    if (received) status = accepted ? "received" : "invalid-claim-or-ungrounded-quote";
                    if (ambiguousLanguage && recover) failure = "actualLanguage:recheck-substantive-clause-grammar";
                    Debug.Log("[Room/SpeechClaim] " + new JObject {
                        ["generation"] = generation, ["attempt"] = attempt, ["maximumRecoveryAttempts"] = 1,
                        ["accepted"] = accepted, ["httpStatus"] = request.responseCode, ["status"] = status,
                        ["failureDetail"] = failure, ["recoveryScheduled"] = recover,
                        ["rejectedOutput"] = accepted && !recover ? null : output.Substring(0, Math.Min(1600, output.Length)),
                        ["outputCharacters"] = output.Length, ["outputTruncated"] = output.Length > 1600,
                        ["elapsedSeconds"] = Time.realtimeSinceStartup - started, ["claim"] = accepted ? claim?.DeepClone() : null,
                        ["scope"] = "Words-only independent classification; execution facts and declared language were not supplied."
                    }.ToString(Formatting.None));
                    if (recover)
                    {
                        // The quotes/state already passed independent extraction.
                        // A language recheck cannot rewrite those proven spans.
                        languageSource = ambiguousLanguage ? (JObject)claim.DeepClone() : null;
                        repair = new JObject { ["attempt"] = 1, ["maximumAttempts"] = 1, ["failureDetail"] = failure,
                            ["rejectedOutput"] = output.Substring(0, Math.Min(1600, output.Length)) };
                        claim = null; continue;
                    }
                    if (!accepted) { callback?.Invoke(false, "", "speech-claim-" + status); yield break; }
                    if (cacheKey != null) { m_SpeechClaimCacheKey = cacheKey; m_SpeechClaimCache = (JObject)claim.DeepClone(); }
                    break;
                }
            }
        }
        if (generation != m_RoomTaskRequestGeneration || claim == null) yield break;
        string declared = (string)input["publicSpeechLanguage"];
        if (!string.IsNullOrEmpty(declared) && RoomTaskProtocol.ReviewLanguageRejection(claim, declared) != null)
        {
            var rejection = new JObject { ["verdict"] = "inconsistent", ["reason"] = "Independent words-only language differs from the speech tag.",
                ["factScope"] = "none", ["resultReport"] = claim["resultReport"].DeepClone(),
                ["actualLanguage"] = claim["actualLanguage"].DeepClone() };
            callback?.Invoke(true, rejection.ToString(Formatting.None), "speech-claim-language-mismatch"); yield break;
        }
        bool temporalConflict = RoomSpeechClaimProtocol.HasTemporalConflict(claim);
        if (requirement != null && (temporalConflict || (string)claim["resultReport"] != (string)requirement["expectedReport"]))
        {
            // Known mismatch: do not spend another full factual review justifying it.
            var rejection = new JObject {
                ["verdict"] = "inconsistent", ["reason"] = temporalConflict
                    ? "The utterance claims this same outcome both completed and still pending."
                    : "Independent spoken state differs from this action's required current state.",
                ["factScope"] = "scene-action", ["resultReport"] = claim["resultReport"].DeepClone(),
                ["actualLanguage"] = claim["actualLanguage"].DeepClone(),
                ["conflict"] = (string)claim["resultReport"] == "omitted" ? "unanswered-request" : "claim-state",
                ["conflictQuote"] = claim[temporalConflict ? "pendingSameResultQuote" : "evidence"].DeepClone()
            };
            callback?.Invoke(true, rejection.ToString(Formatting.None), temporalConflict ? "speech-claim-temporal-conflict" : "speech-claim-state-mismatch"); yield break;
        }
        input["independentSpeechClaim"] = claim.DeepClone();
        string reviewed = BuildRoomTaskRequestJsonWithImage(input.ToString(Formatting.None), image, true);
        yield return RequestRoomTaskPayload(reviewed, true, generation, (ok, output, status) => {
            if (generation != m_RoomTaskRequestGeneration) return;
            if (ok && RoomTaskProtocol.TryReviewScope(output, out _))
            {
                var result = ReadRoomTaskRecoveryJson(output);
                string quote = (string)result["conflictQuote"];
                if (result["conflict"] == null || result["targetComparison"] == null || result["targetConsistency"] == null ||
                    result["otherClaimsComparison"] == null || result["otherClaimsSupport"] == null ||
                    quote == null ||
                    (quote.Length > 0 && ((string)input["publicSpeech"] ?? "").IndexOf(quote, StringComparison.Ordinal) < 0))
                { callback?.Invoke(false, "", "speech-review-conflict-evidence-invalid"); return; }
                // Cloud JSON mode need not honor const. Neither provider may rewrite the independent observation.
                result["resultReport"] = claim["resultReport"].DeepClone();
                result["actualLanguage"] = claim["actualLanguage"].DeepClone();
                output = result.ToString(Formatting.None);
            }
            callback?.Invoke(ok, output, status);
        });
    }
}
