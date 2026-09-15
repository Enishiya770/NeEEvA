using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    // Separate ownership: a policy review must not cancel an autonomy/work review or formal speech.
    private UnityWebRequest m_SingingPolicyReviewRequest;
    private int m_SingingPolicyReviewGeneration;

    public override void CancelSingingPolicyReview()
    {
        ++m_SingingPolicyReviewGeneration;
        if (m_SingingPolicyReviewRequest != null) m_SingingPolicyReviewRequest.Abort();
        m_SingingPolicyReviewRequest = null;
    }

    public override void PostSingingPolicyReview(SingingPolicyReviewRequest observation, Action<SingingPolicyReviewResult> callback)
    {
        CancelSingingPolicyReview();
        if (observation == null || string.IsNullOrWhiteSpace(observation.UserText) ||
            !SingingPolicyReviewProtocol.IsCandidate(observation.Candidate))
        { callback?.Invoke(SingingPolicyReviewResult.Failed("invalid-policy-request")); return; }
        StartCoroutine(RequestSingingPolicyReview(observation, m_SingingPolicyReviewGeneration, callback));
    }

    private string BuildSingingPolicyReviewRequestJson(SingingPolicyReviewRequest observation)
    {
        var sb = new StringBuilder(4096);
        sb.Append("{\"model\":"); AppendJsonString(sb, CurrentModelName);
        sb.Append(",\"stream\":false,\"enable_thinking\":false,\"temperature\":0,\"max_tokens\":")
            .Append(SingingPolicyReviewProtocol.MaxTokens);
        sb.Append(",\"response_format\":{\"type\":\"json_object\"");
        if (m_Backend == BackendType.Local) sb.Append(",\"schema\":").Append(SingingPolicyReviewProtocol.Schema);
        sb.Append("},\"messages\":[");
        AppendMessage(sb, new SendData("system", SingingPolicyReviewProtocol.Instruction)); sb.Append(',');
        AppendMessage(sb, new SendData("user", SingingPolicyReviewProtocol.BuildObservation(observation)));
        sb.Append(']');
        if (m_Backend == BackendType.Local) sb.Append(",\"chat_template_kwargs\":{\"enable_thinking\":false}");
        AppendSlot(sb, k_SlotAuxiliary); sb.Append('}');
        return sb.ToString();
    }

    private IEnumerator RequestSingingPolicyReview(SingingPolicyReviewRequest observation, int generation,
        Action<SingingPolicyReviewResult> callback)
    {
        using (var request = new UnityWebRequest(url, "POST"))
        {
            m_SingingPolicyReviewRequest = request;
            request.timeout = 12;
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(BuildSingingPolicyReviewRequestJson(observation)));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", string.Format("Bearer {0}", string.IsNullOrEmpty(api_key) ? "ollama" : api_key));
            float began = Time.realtimeSinceStartup;
            yield return request.SendWebRequest();
            if (generation != m_SingingPolicyReviewGeneration) yield break;
            if (ReferenceEquals(m_SingingPolicyReviewRequest, request)) m_SingingPolicyReviewRequest = null;
            var result = request.responseCode == 200 && request.result == UnityWebRequest.Result.Success
                ? SingingPolicyReviewProtocol.ReadResponse(request.downloadHandler.text, observation)
                : SingingPolicyReviewResult.Failed("policy-review-http-failure");
            if (m_LogRequestStats)
                Debug.Log($"[Singing/PolicyWire] code={request.responseCode} transport={request.result} " +
                    $"valid={result.Valid} grant={result.Granted} finish={result.FinishReason} " +
                    $"seconds={Time.realtimeSinceStartup - began:F2} failure={result.Failure}");
            callback?.Invoke(result);
        }
    }
}
