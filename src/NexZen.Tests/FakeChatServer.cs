using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NexZen.Tests;

/// <summary>
/// Minimal OpenAI-compatible endpoint on loopback: answers each POST with the next
/// queued response and records the request bodies.
/// </summary>
internal sealed class FakeChatServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Queue<(int Status, string ContentType, string Body)> _responses = new();
    private readonly CancellationTokenSource _stop = new();

    public FakeChatServer()
    {
        var port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}/v1";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }
    public List<string> Requests { get; } = new();

    public FakeChatServer Json(string body, int status = 200)
    {
        lock (_responses)
            _responses.Enqueue((status, "application/json", body));
        return this;
    }

    public FakeChatServer Sse(params string[] events)
    {
        var body = string.Concat(events.Select(e => "data: " + e + "\n\n")) + "data: [DONE]\n\n";
        lock (_responses)
            _responses.Enqueue((200, "text/event-stream", body));
        return this;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                var body = await reader.ReadToEndAsync();
                lock (Requests)
                    Requests.Add(body);
            }

            (int Status, string ContentType, string Body) next;
            lock (_responses)
                next = _responses.Count > 0 ? _responses.Dequeue() : (500, "application/json", """{"error":"no more responses"}""");
            var bytes = Encoding.UTF8.GetBytes(next.Body);
            context.Response.StatusCode = next.Status;
            context.Response.ContentType = next.ContentType;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}
