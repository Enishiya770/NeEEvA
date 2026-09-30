using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

public sealed partial class PersonalSingingLearningClient
{
    // Optional work done while recording. A failure only loses a pitch cache;
    // it never delays or changes the original WAV upload, ASR or user labels.
    private sealed class PitchFlow
    {
        public string Key, Id, Profile, Url, RecordingId;
        public readonly Queue<byte[]> Chunks = new Queue<byte[]>();
        public int Received, Sent, QueuedBytes;
        public bool Running, Cancelled, Sealed;
        public float Expires;
        public UnityWebRequest Request;
    }
    private readonly Dictionary<string, PitchFlow> pitchFlows = new Dictionary<string, PitchFlow>();
    private const int MaximumPitchQueueBytes = 16000 * 2 * 3;

    private void BeginPitchFlow(string root, int serial)
    {
        if (pitchFlows.Count >= 4) return;
        string key = root + "/" + serial;
        pitchFlows[key] = new PitchFlow { Key = key, Id = "ps-" + Guid.NewGuid().ToString("N"),
            Profile = ProfileId, Url = serviceUrl, Expires = Time.unscaledTime + 150f };
    }

    private void ReceivePitchChunk(byte[] pcm, string root, int serial, int startSample)
    {
        if (!pitchFlows.TryGetValue(root + "/" + serial, out var flow) || flow.Cancelled || flow.Sealed) return;
        if (pcm == null || pcm.Length == 0 || pcm.Length % 2 != 0 || pcm.Length > 32000 ||
            startSample != flow.Received || flow.QueuedBytes + pcm.Length > MaximumPitchQueueBytes)
        { CancelPitchFlow(root, serial); return; }
        flow.Chunks.Enqueue(pcm); flow.QueuedBytes += pcm.Length; flow.Received += pcm.Length / 2;
        if (!flow.Running) { flow.Running = true; StartCoroutine(PumpPitchFlow(flow)); }
    }

    private string SealPitchFlow(string root, int serial, int rate, int channels, int samples)
    {
        if (!pitchFlows.TryGetValue(root + "/" + serial, out var flow)) return null;
        if (flow.Cancelled || !flow.Running || rate != 16000 || channels != 1 || flow.Received != samples)
        { CancelPitchFlow(root, serial); return null; }
        flow.Sealed = true; flow.Expires = Time.unscaledTime + 45f;
        return flow.Id;
    }

    private void PitchOriginalUploaded(OutboxItem item)
    {
        string id = (string)item.Manifest["pitch_stream_id"];
        var flow = pitchFlows.Values.FirstOrDefault(x => x.Id == id && x.Profile == item.Profile && x.Url == item.Url);
        if (flow != null && !flow.Cancelled) flow.RecordingId = (string)item.Manifest["recording_id"];
    }

    private void AbortUnsealedPitch(string root, int serial)
    {
        if (pitchFlows.TryGetValue(root + "/" + serial, out var flow) && !flow.Sealed) CancelPitchFlow(root, serial);
    }

    private void CancelPitchFlow(string root, int serial)
    {
        if (!pitchFlows.TryGetValue(root + "/" + serial, out var flow)) return;
        flow.Cancelled = true; flow.Chunks.Clear(); flow.QueuedBytes = 0; flow.Request?.Abort();
        if (!flow.Running) pitchFlows.Remove(flow.Key);
    }

    private void CancelAllPitchFlows()
    {
        foreach (var flow in pitchFlows.Values)
        {
            flow.Cancelled = true; flow.Request?.Abort(); flow.Chunks.Clear();
            // OnDisable stops coroutines. Retire the optional server cache using
            // an operation callback which does not depend on this component.
            if (flow.Running) DeletePitchDetached(flow);
        }
        pitchFlows.Clear();
    }

    private static void DeletePitchDetached(PitchFlow flow)
    {
        var request = UnityWebRequest.Delete(flow.Url + "/learning/profiles/" +
            Uri.EscapeDataString(flow.Profile) + "/pitch-streams/" + flow.Id);
        request.timeout = 3;
        try { request.SendWebRequest().completed += _ => request.Dispose(); }
        catch { request.Dispose(); } // Server TTL covers shutdown and network loss.
    }

    private IEnumerator PitchRequest(PitchFlow flow, string method, string suffix, byte[] data, Action<bool> completed)
    {
        string route = flow.Url + "/learning/profiles/" + Uri.EscapeDataString(flow.Profile) + "/pitch-streams";
        using (var request = new UnityWebRequest(route + suffix, method))
        {
            request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 3;
            if (data != null)
            {
                request.uploadHandler = new UploadHandlerRaw(data);
                request.SetRequestHeader("Content-Type", method == "PUT" ? "application/octet-stream" : "application/json");
            }
            flow.Request = request;
            yield return request.SendWebRequest();
            if (ReferenceEquals(flow.Request, request)) flow.Request = null;
            completed(request.result == UnityWebRequest.Result.Success);
        }
    }

    private static byte[] PitchJson(JObject value) => Encoding.UTF8.GetBytes(value.ToString(Formatting.None));

    private IEnumerator PumpPitchFlow(PitchFlow flow)
    {
        bool success = false;
        yield return PitchRequest(flow, "POST", "", PitchJson(new JObject {
            ["stream_id"] = flow.Id, ["sample_rate"] = 16000, ["channels"] = 1 }), ok => success = ok);
        bool opened = success;
        if (!opened) flow.Cancelled = true;
        while (!flow.Cancelled && isActiveAndEnabled && Time.unscaledTime < flow.Expires)
        {
            if (flow.Chunks.Count > 0)
            {
                byte[] chunk = flow.Chunks.Dequeue(); flow.QueuedBytes -= chunk.Length;
                success = false;
                yield return PitchRequest(flow, "PUT", "/" + flow.Id + "/chunks?start_sample=" + flow.Sent,
                    chunk, ok => success = ok);
                if (!success) { flow.Cancelled = true; break; }
                flow.Sent += chunk.Length / 2;
            }
            else if (flow.Sealed && !string.IsNullOrEmpty(flow.RecordingId))
            {
                yield return PitchRequest(flow, "POST", "/" + flow.Id + "/commit",
                    PitchJson(new JObject { ["recording_id"] = flow.RecordingId }), ok => success = ok);
                if (success) Debug.Log("[PersonalLearning/Pitch] 已核验并复用录音期间的音高特征：" + flow.RecordingId);
                break;
            }
            else yield return null;
        }
        // A timed-out create may still have reached the server; DELETE is idempotent.
        yield return PitchRequest(flow, "DELETE", "/" + flow.Id, null, _ => { });
        if (pitchFlows.TryGetValue(flow.Key, out var current) && ReferenceEquals(current, flow)) pitchFlows.Remove(flow.Key);
        flow.Chunks.Clear(); flow.Running = false;
    }
}
