using System;
using System.Collections;
using System.Linq;
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
        else if (repair != null) {
            words["extractionRepair"] = repair.DeepClone();
            // Even after removing the wrong language label, the rejected
            // extraction anchors a repeated mistake. Diagnostics retain it;
            // the one recovery reads original words plus failed-check names only.
            ((JObject)words["extractionRepair"]).Remove("rejectedOutput");
            contract += "\nThis is the single recovery. Recheck ALL fields from the original clauses, not only the first failed check.\n";
        }
        var sb = new StringBuilder("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":640,\"response_format\":{\"type\":\"json_object\"");
        if (m_Backend == BackendType.Local) sb.Append(",\"schema\":").Append(schema);
        sb.Append("},\"messages\":[");
        AppendMessage(sb, new SendData("system", contract +
            "\nextractionRepair contains failed-check names only. Fix schema, exact quotes and field consistency from publicSpeech alone; never drop assertions to evade repair. Preserve real mixed clauses; no desired language or execution facts are supplied.\n" +
            (m_Backend == BackendType.Local ? "Return the native schema fields exactly." : schema))); sb.Append(',');
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
                var extractionBudget = MeasureRoomDecisionBudget(json, 4096);
                if ((bool?)extractionBudget["allowed"] != true)
                {
                    Debug.Log("[Room/SpeechClaimBudget] " + new JObject { ["generation"] = generation, ["attempt"] = attempt,
                        ["httpRequestSent"] = false, ["budget"] = extractionBudget }.ToString(Formatting.None));
                    callback?.Invoke(false, "", "speech-claim-context-budget"); yield break;
                }
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
                    if (accepted && !(claim["eventClaims"] is JArray))
                    { accepted = false; failure = "eventClaims:required-for-production-review"; }
                    if (accepted && domain == "conversation" && ((string)claim["resultReport"] != "omitted" ||
                        !string.IsNullOrEmpty((string)claim["evidence"]) || !string.IsNullOrEmpty((string)claim["targetQuote"]) ||
                        !string.IsNullOrEmpty((string)claim["pendingSameResultQuote"])))
                    { accepted = false; failure = "conversation:unexpected-result-fields"; }
                    JObject languageDiagnostic = null;
                    try {
                        var raw = JObject.Parse(output, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                        if (raw["actualLanguage"]?.Type == JTokenType.String &&
                            new[] { "ja", "zh", "en", "mixed", "und" }.Contains((string)raw["actualLanguage"]))
                            languageDiagnostic = new JObject { ["actualLanguage"] = raw["actualLanguage"].DeepClone(),
                                ["languageReason"] = raw["languageReason"]?.DeepClone() };
                    } catch (JsonException) { }
                    // Diagnose language even when a separate quote/state check
                    // failed. Diagnostics do not make an invalid claim trusted.
                    bool ambiguousLanguage = (string)languageDiagnostic?["actualLanguage"] == "mixed" || (string)languageDiagnostic?["actualLanguage"] == "und";
                    bool languageDisagreement = languageDiagnostic != null && !string.IsNullOrEmpty((string)input["publicSpeechLanguage"]) &&
                        RoomTaskProtocol.ReviewLanguageRejection(languageDiagnostic, (string)input["publicSpeechLanguage"]) != null;
                    bool recheckLanguage = ambiguousLanguage || languageDisagreement;
                    var issues = new JArray();
                    if (!string.IsNullOrEmpty(failure)) issues.Add(failure);
                    if (recheckLanguage) issues.Add("actualLanguage:recheck-substantive-clause-grammar");
                    // Curl can reject reuse of a closed local connection before
                    // receiving HTTP (error 65 / upload rewind). Rebuild this
                    // read-only classification once, sharing the same two-attempt
                    // ceiling with schema repair; no action is resent.
                    bool reconnect = !received && attempt == 0 && request.responseCode == 0 &&
                        request.result == UnityWebRequest.Result.ConnectionError && Time.realtimeSinceStartup-started < .5f;
                    bool recover = reconnect || received && attempt == 0 && (!accepted || recheckLanguage);
                    if (received) status = accepted ? "received" : "invalid-claim-or-ungrounded-quote";
                    if (accepted && recheckLanguage && recover) failure = "actualLanguage:recheck-substantive-clause-grammar";
                    Debug.Log("[Room/SpeechClaim] " + new JObject {
                        ["generation"] = generation, ["attempt"] = attempt, ["maximumRecoveryAttempts"] = 1,
                        ["accepted"] = accepted, ["httpStatus"] = request.responseCode, ["status"] = status,
                        ["transportResult"] = request.result.ToString(), ["transportError"] = request.error,
                        ["failureDetail"] = failure, ["recoveryScheduled"] = recover,
                        ["validationIssues"] = issues, ["languageDiagnostic"] = languageDiagnostic,
                        ["recoveryKind"] = reconnect ? "early-connection-rebuild" : recover ? "extraction-repair" : "none",
                        ["rejectedOutput"] = accepted && !recover ? null : output.Substring(0, Math.Min(1600, output.Length)),
                        ["outputCharacters"] = output.Length, ["outputTruncated"] = output.Length > 1600,
                        ["elapsedSeconds"] = Time.realtimeSinceStartup - started, ["claim"] = accepted ? claim?.DeepClone() : null,
                        ["scope"] = "Words-only independent classification; execution facts and declared language were not supplied."
                    }.ToString(Formatting.None));
                    if (recover)
                    {
                        if(reconnect) { yield return null; continue; }
                        // The quotes/state already passed independent extraction.
                        // A language recheck cannot rewrite those proven spans.
                        languageSource = accepted && recheckLanguage ? (JObject)claim.DeepClone() : null;
                        repair = new JObject { ["attempt"] = 1, ["maximumAttempts"] = 1, ["failureDetail"] = failure,
                            ["validationIssues"] = issues.DeepClone(),
                            ["rejectedOutput"] = output.Substring(0, Math.Min(1600, output.Length)) };
                        claim = null; continue;
                    }
                    if (!accepted) { callback?.Invoke(false, "", "speech-claim-" + status); yield break; }
                    if (recheckLanguage && languageSource == null) {
                        // A full repair (or reconnect) used the shared recovery
                        // budget. Its unresolved language classification was not
                        // independently verified; it cannot order a public rewrite.
                        callback?.Invoke(false, "", "speech-claim-language-unverified"); yield break;
                    }
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
        if (claim["planningFailureClaim"] is JObject failureClaim && input["decisionFailure"] is JObject actualFailure &&
            (string)failureClaim["kind"] != "not-mentioned" && (string)failureClaim["kind"] != (string)actualFailure["kind"])
        {
            callback?.Invoke(true,new JObject { ["verdict"]="inconsistent",["reason"]="The independently extracted spoken failure cause differs from the current decision receipt.",
                ["factScope"]="scene-action",["resultReport"]=claim["resultReport"].DeepClone(),["actualLanguage"]=claim["actualLanguage"].DeepClone(),
                ["conflict"]="unsupported-claim",["conflictQuote"]=failureClaim["quote"].DeepClone(),["planningFailureReport"]=failureClaim["kind"].DeepClone()
            }.ToString(Formatting.None),"speech-claim-failure-cause-mismatch");yield break;
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
                string eventFailure = SpeechEventBindingProtocol.Reject(input, result, out string eventQuote);
                if (eventFailure != null) {
                    result["verdict"]="inconsistent"; result["reason"]=eventFailure;
                    result["conflict"]="unsupported-claim"; result["conflictQuote"]=eventQuote;
                    result["resultReport"]=claim["resultReport"].DeepClone();
                    result["actualLanguage"]=claim["actualLanguage"].DeepClone();
                    callback?.Invoke(true,result.ToString(Formatting.None),eventFailure); return;
                }
                if (input["eventContext"] is JObject && result["claimTemporalScope"] == null)
                { callback?.Invoke(false, "", "speech-review-temporal-scope-missing"); return; }
                if (RoomSpeechEventEvidenceProtocol.AutonomousConversation(input) && result["eventOrderConsistency"] == null)
                { callback?.Invoke(false, "", "speech-review-event-order-missing"); return; }
                if (RoomSpeechPartsProtocol.Required(input))
                {
                    if (!RoomSpeechPartsProtocol.Resolve(input, result, out string selectionFailure))
                    { callback?.Invoke(false, "", selectionFailure); return; }
                    if (!RoomSpeechPartsProtocol.TryRead(result, (string)input["publicSpeech"], out string stable, out string partFailure))
                    { callback?.Invoke(false, "", partFailure); return; }
                    // The independent extractor's main device/search result may
                    // not disappear into a movement span, even with a valid partition.
                    string evidence = (string)claim["evidence"];
                    string retainedLiteral = string.Concat(((JArray)result["speechParts"]).Where(p => (string)p["owner"] == "independent").Select(p => (string)p["text"]));
                    if (!string.IsNullOrEmpty(evidence) && retainedLiteral.IndexOf(evidence, StringComparison.Ordinal) < 0)
                    { callback?.Invoke(false, "", "speech-parts-main-result-removed"); return; }
                }
                string quote = (string)result["conflictQuote"];
                if (result["conflict"] == null || result["targetComparison"] == null || result["targetConsistency"] == null ||
                    result["otherClaimsComparison"] == null || result["otherClaimsSupport"] == null || result["taskRelation"] == null || result["taskRelationComparison"] == null ||
                    quote == null ||
                    (quote.Length > 0 && ((string)input["publicSpeech"] ?? "").IndexOf(quote, StringComparison.Ordinal) < 0))
                { callback?.Invoke(false, "", "speech-review-conflict-evidence-invalid"); return; }
                // Cloud JSON mode need not honor const. Neither provider may rewrite the independent observation.
                result["resultReport"] = claim["resultReport"].DeepClone();
                result["actualLanguage"] = claim["actualLanguage"].DeepClone();
                if(claim["planningFailureClaim"] is JObject reviewedCause)
                    result["planningFailureReport"]=reviewedCause["kind"].DeepClone();
                result.Remove("movementDependencies");
                // This metadata comes from the separate words-only extraction,
                // never from the factual reviewer's optional scope declaration.
                if (domain == "movement" && claim["movementDependencies"] is JObject dependencies)
                    result["movementDependencies"] = dependencies.DeepClone();
                output = result.ToString(Formatting.None);
            }
            callback?.Invoke(ok, output, status);
        });
    }
}
