using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Disposable loopback-only test transport. Every response is obtained from the real
/// service, unchanged; selected successful stream replies are held before delivery.
/// No runtime assembly reference and no synthetic pose/trajectory generation.
/// </summary>
internal sealed class ArdyRoomStreamDelayProxy : IDisposable
{
    [Serializable] internal sealed class Record
    {
        public string scenario, path, session, request, error;
        public int step = -1, status, delayMilliseconds;
        public double receivedSeconds, backendFinishedSeconds, finishedSeconds;
        public bool delivered, finished;
    }
    private readonly object gate = new object();
    private readonly List<Record> records = new List<Record>();
    private readonly HashSet<string> delayedSessions = new HashSet<string>();
    private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly TcpListener listener;
    private readonly Uri upstream;
    private readonly DateTime started = DateTime.UtcNow;
    private string scenario;
    private int delayedStep, delayMilliseconds;
    private bool oncePerSession, delayedOnce;
    internal string Endpoint { get; private set; }

    internal ArdyRoomStreamDelayProxy(string endpoint)
    {
        upstream = new Uri(endpoint.TrimEnd('/') + "/");
        if (upstream.Scheme != "http" || !upstream.IsLoopback || !string.IsNullOrEmpty(upstream.UserInfo))
            throw new ArgumentException("The delay fixture only forwards to a local HTTP ARDY endpoint.");
        listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        Endpoint = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Accept();
    }

    internal void Configure(string name, int step, int milliseconds, bool everySession)
    {
        lock (gate)
        {
            scenario = name; delayedStep = step; delayMilliseconds = milliseconds;
            oncePerSession = everySession; delayedOnce = false; delayedSessions.Clear();
        }
    }

    internal Record[] Snapshot()
    {
        lock (gate)
        {
            var output = new Record[records.Count];
            for (int i = 0; i < output.Length; i++)
            {
                var r = records[i];
                output[i] = new Record { scenario = r.scenario, path = r.path, session = r.session,
                    request = r.request, error = r.error, step = r.step, status = r.status,
                    delayMilliseconds = r.delayMilliseconds, receivedSeconds = r.receivedSeconds,
                    backendFinishedSeconds = r.backendFinishedSeconds, finishedSeconds = r.finishedSeconds,
                    delivered = r.delivered, finished = r.finished };
            }
            return output;
        }
    }

    private async Task Accept()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (Exception) when (stop.IsCancellationRequested) { break; }
            lock (gate) clients.Add(client);
            _ = Task.Run(() => Forward(client));
        }
    }

    private async Task Forward(TcpClient client)
    {
        Record record = null;
        try
        {
            using (client)
            using (var input = client.GetStream())
            {
                input.ReadTimeout = 15000; input.WriteTimeout = 15000;
                byte[] one = new byte[1]; var head = new MemoryStream(); int matched = 0;
                byte[] boundary = { 13, 10, 13, 10 };
                while (matched < 4)
                {
                    if (await input.ReadAsync(one, 0, 1, stop.Token).ConfigureAwait(false) != 1)
                        throw new EndOfStreamException("Client closed before HTTP headers.");
                    head.WriteByte(one[0]);
                    matched = one[0] == boundary[matched] ? matched + 1 : (one[0] == 13 ? 1 : 0);
                    if (head.Length > 65536) throw new InvalidDataException("Oversized HTTP headers.");
                }
                string[] headers = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                string[] first = headers[0].Split(' ');
                if (first.Length < 2 || !first[1].StartsWith("/v1/", StringComparison.Ordinal) || first[1].StartsWith("//", StringComparison.Ordinal))
                    throw new InvalidDataException("Only ARDY /v1/ requests are allowed.");
                int contentLength = 0;
                foreach (string header in headers)
                {
                    if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(header.Substring(header.IndexOf(':') + 1).Trim());
                    if (header.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The fixture requires explicit request Content-Length.");
                }
                if (contentLength < 0 || contentLength > 16777216) throw new InvalidDataException("Oversized HTTP body.");
                byte[] body = new byte[contentLength]; int read = 0;
                while (read < body.Length)
                {
                    int count = await input.ReadAsync(body, read, body.Length - read, stop.Token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException("Client closed before HTTP body.");
                    read += count;
                }
                string json = Encoding.UTF8.GetString(body);
                record = new Record { path = first[1], receivedSeconds = Elapsed,
                    session = StringField(json, "sessionId"), request = StringField(json, "requestId"), step = IntField(json, "stepIndex") };
                lock (gate)
                {
                    record.scenario = scenario;
                    if (record.path == "/v1/locomotion/stream/step" && record.step >= delayedStep &&
                        (oncePerSession ? !delayedSessions.Contains(record.session ?? "") : !delayedOnce))
                    {
                        delayedOnce = true; delayedSessions.Add(record.session ?? ""); record.delayMilliseconds = delayMilliseconds;
                    }
                    records.Add(record);
                }
                var request = (HttpWebRequest)WebRequest.Create(new Uri(upstream, first[1]));
                request.Method = first[0]; request.ContentType = "application/json"; request.Proxy = null;
                request.Timeout = request.ReadWriteTimeout = 15000; request.KeepAlive = false;
                request.ServicePoint.Expect100Continue = false;
                using (stop.Token.Register(request.Abort))
                {
                    if (body.Length > 0)
                    {
                        request.ContentLength = body.Length;
                        using (var output = await request.GetRequestStreamAsync().ConfigureAwait(false))
                            await output.WriteAsync(body, 0, body.Length, stop.Token).ConfigureAwait(false);
                    }
                    HttpWebResponse response;
                    try { response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false); }
                    catch (WebException failure) when (failure.Response != null) { response = (HttpWebResponse)failure.Response; }
                    using (response)
                    using (var result = new MemoryStream())
                    {
                        using (var responseStream = response.GetResponseStream())
                            await responseStream.CopyToAsync(result, 81920, stop.Token).ConfigureAwait(false);
                        if (result.Length > 33554432) throw new InvalidDataException("Oversized ARDY response.");
                        lock (gate) { record.status = (int)response.StatusCode; record.backendFinishedSeconds = Elapsed; }
                        if (record.delayMilliseconds > 0 && record.status >= 200 && record.status < 300)
                            await Task.Delay(record.delayMilliseconds, stop.Token).ConfigureAwait(false);
                        byte[] payload = result.ToArray();
                        byte[] prefix = Encoding.ASCII.GetBytes("HTTP/1.1 " + (int)response.StatusCode + " " + response.StatusDescription +
                            "\r\nContent-Type: application/json\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
                        await input.WriteAsync(prefix, 0, prefix.Length, stop.Token).ConfigureAwait(false);
                        await input.WriteAsync(payload, 0, payload.Length, stop.Token).ConfigureAwait(false);
                        await input.FlushAsync(stop.Token).ConfigureAwait(false);
                        lock (gate) record.delivered = true;
                    }
                }
            }
        }
        catch (Exception error)
        {
            lock (gate)
            {
                if (record == null) { record = new Record { scenario = scenario, receivedSeconds = Elapsed }; records.Add(record); }
                record.error = error.GetType().Name + ": " + error.Message;
            }
        }
        finally
        {
            lock (gate)
            {
                if (record != null) { record.finished = true; record.finishedSeconds = Elapsed; }
                clients.Remove(client);
            }
        }
    }

    private double Elapsed => (DateTime.UtcNow - started).TotalSeconds;
    private static string StringField(string json, string name)
    { var m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"([^\"]*)\""); return m.Success ? m.Groups[1].Value : null; }
    private static int IntField(string json, string name)
    { var m = Regex.Match(json, "\"" + Regex.Escape(name) + "\"\\s*:\\s*(-?\\d+)"); return m.Success ? int.Parse(m.Groups[1].Value) : -1; }
    public void Dispose()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel(); listener.Stop();
        lock (gate) foreach (var client in clients) client.Close();
    }
}
