using System;
using System.Collections;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class ChatQW
{
    [SerializeField, Tooltip("Sample configured local or private LAN provider /metrics around formal requests asynchronously; interval statistics never block speech or claim per-request attribution.")]
    private bool m_ObserveProviderIntervals = true;
    [SerializeField, Range(2, 15)] private int m_ProviderMetricsTimeoutSeconds = 8;
    private sealed class ProviderIntervalProbe
    {
        public string url;
        public JObject before;
        public bool beforeFinished;
        public double beforeAt;
        public JObject beforeDiagnostic;
        public ConversationTrace trace;
    }
    private ProviderIntervalProbe BeginProviderInterval(ConversationTrace trace)
    {
        if (!m_ObserveProviderIntervals || !m_LogRequestStats ||
            !Uri.TryCreate(url, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != "http" && endpoint.Scheme != "https") ||
            !(m_Backend == BackendType.Local || m_Backend == BackendType.OpenAICompatible &&
                ProviderTimingInterval.IsLocalMetricsEndpoint(endpoint))) return null;
        var probe = new ProviderIntervalProbe { url = endpoint.GetLeftPart(UriPartial.Authority) + "/metrics", trace = trace };
        StartCoroutine(ReadProviderInterval(probe, true));
        return probe;
    }
    private IEnumerator ReadProviderInterval(ProviderIntervalProbe probe, bool before)
    {
        // Waiting here affects this diagnostic coroutine only, never LLM dispatch,
        // first speech, cancellation, tool review or TTS.
        if (!before)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + Mathf.Clamp(m_ProviderMetricsTimeoutSeconds, 2, 15) + 1;
            while (!probe.beforeFinished && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        }
        using (var request = UnityWebRequest.Get(probe.url))
        {
            request.timeout = Mathf.Clamp(m_ProviderMetricsTimeoutSeconds, 2, 15);
            double startedAt = Time.realtimeSinceStartupAsDouble;
            yield return request.SendWebRequest();
            var sample = request.result == UnityWebRequest.Result.Success
                ? ProviderTimingInterval.Parse(request.downloadHandler.text) : null;
            var diagnostic = new JObject {
                ["httpStatus"] = request.responseCode, ["result"] = request.result.ToString(),
                ["error"] = request.error, ["requestStartedRealtime"] = startedAt,
                ["elapsedSeconds"] = Time.realtimeSinceStartupAsDouble - startedAt,
                ["timeoutSeconds"] = request.timeout, ["counterCount"] = sample?.Count ?? 0 };
            if (before)
            {
                probe.before = sample; probe.beforeAt = Time.realtimeSinceStartupAsDouble;
                probe.beforeDiagnostic = diagnostic;
                probe.beforeFinished = true;
                yield break;
            }
            var observation = ProviderTimingInterval.Difference(probe.before, sample);
            observation["available"] = probe.before != null && probe.before.Count > 0 && sample != null && sample.Count > 0;
            observation["firstSampleReceivedRealtime"] = probe.beforeAt;
            observation["lastSampleReceivedRealtime"] = Time.realtimeSinceStartupAsDouble;
            observation["blockingConversation"] = false;
            observation["firstSample"] = probe.beforeDiagnostic;
            observation["lastSample"] = diagnostic;
            probe.trace?.Mark("llm.provider_interval", observation);
            Debug.Log("[LLM/ProviderInterval] " + observation.ToString(Formatting.None));
        }
    }
}
