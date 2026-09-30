using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using Newtonsoft.Json.Linq;

// vLLM Prometheus samples describe a server interval. Concurrent vision, role and
// formal requests can share it; never label these deltas as one request's phases.
public static class ProviderTimingInterval
{
    private static readonly HashSet<string> Names = new HashSet<string>(StringComparer.Ordinal) {
        "vllm:request_queue_time_seconds_sum", "vllm:request_queue_time_seconds_count",
        "vllm:request_prefill_time_seconds_sum", "vllm:request_prefill_time_seconds_count",
        "vllm:time_to_first_token_seconds_sum", "vllm:time_to_first_token_seconds_count",
        "vllm:prefix_cache_hits_total", "vllm:prefix_cache_queries_total",
        "vllm:prompt_tokens_total", "vllm:prompt_tokens_cached_total",
        "vllm:generation_tokens_total", "vllm:request_success_total",
        "vllm:request_decode_time_seconds_sum", "vllm:request_decode_time_seconds_count",
        "vllm:num_preemptions_total"
    };
    // Only the configured local/LAN authority is sampled; no DNS probing or
    // public OpenAI-compatible provider /metrics requests.
    public static bool IsLocalMetricsEndpoint(Uri endpoint)
    {
        if (endpoint == null || !endpoint.IsAbsoluteUri ||
            (endpoint.Scheme != "http" && endpoint.Scheme != "https")) return false;
        if (endpoint.IsLoopback) return true;
        if (!IPAddress.TryParse(endpoint.Host, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);
    }
    public static JObject Parse(string text)
    {
        var values = new JObject();
        foreach (string raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int nameEnd = line.IndexOfAny(new[] { '{', ' ', '\t' });
            if (nameEnd < 0) continue;
            string name = line.Substring(0, nameEnd);
            if (!Names.Contains(name)) continue;
            int labelsEnd = line[nameEnd] == '{' ? line.LastIndexOf('}') : nameEnd;
            if (labelsEnd < 0) continue;
            string value = line.Substring(labelsEnd + (line[nameEnd] == '{' ? 1 : 0)).Trim();
            int space = value.IndexOfAny(new[] { ' ', '\t' });
            if (space >= 0) value = value.Substring(0, space);
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
                double.IsInfinity(number) || double.IsNaN(number) || number < 0) continue;
            values[name] = (values[name]?.Value<double>() ?? 0) + number;
        }
        return values;
    }
    public static JObject Difference(JObject before, JObject after)
    {
        var delta = new JObject();
        bool reset = false;
        foreach (string name in Names)
        {
            if (before?[name] == null || after?[name] == null) continue;
            double difference = after[name].Value<double>() - before[name].Value<double>();
            if (difference < 0) { reset = true; continue; }
            delta[name] = difference;
        }
        return new JObject { ["counterReset"] = reset, ["delta"] = reset ? new JObject() : delta,
            ["scope"] = "server interval across all model labels; may include concurrent requests; not per-request queue/prefill/cache attribution" };
    }
}
