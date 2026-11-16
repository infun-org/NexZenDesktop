using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Models;

namespace NexZen.Infrastructure.NexShare.Transport;

public class LanWebSocketServer : INexShareTransportServer
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private bool _started;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _hostedFiles = new();
    
    public event EventHandler<INexShareTransport>? OnClientConnected;
    
    public int Port { get; private set; }

    public async Task StartAsync(int port = 0)
    {
        if (_started)
        {
            Console.WriteLine("[LanWebSocketServer] StartAsync called again - ignoring.");
            return;
        }
        _started = true;
        _listener = new HttpListener();
        
        if (port == 0)
        {
            var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            Port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
        }
        else
        {
            Port = port;
        }

        // On Linux/macOS, "*" is preferred over "+" for binding to all interfaces without strict ACL requirements.
        _listener.Prefixes.Add($"http://*:{Port}/nexshare/");
        
        try
        {
            _listener.Start();
            Console.WriteLine($"[LanWebSocketServer] Started on prefix: http://*:{Port}/nexshare/");
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine($"[LanWebSocketServer] Failed to bind to *: {ex.Message}. Falling back to localhost.");
            // Fallback for Windows if not admin
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://localhost:{Port}/nexshare/");
            _listener.Start();
        }
        
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
        
        await Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        Console.WriteLine("[WS-ACCEPT] AcceptLoopAsync started.");
        try
        {
            while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                var remote = context.Request.RemoteEndPoint?.ToString() ?? "unknown";
                Console.WriteLine($"[WS-ACCEPT] ← {context.Request.HttpMethod} {context.Request.Url} from {remote} | IsWebSocket={context.Request.IsWebSocketRequest}");

                if (context.Request.IsWebSocketRequest)
                {
                    Console.WriteLine($"[WS-ACCEPT] Upgrading to WebSocket from {remote}...");
                    try
                    {
                        var wsContext = await context.AcceptWebSocketAsync(null);
                        var ip = context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown";
                        var transport = new LanWebSocketClientTransport(wsContext.WebSocket, ip);
                        Console.WriteLine($"[WS-ACCEPT] WebSocket accepted from {ip}. State={wsContext.WebSocket.State}. Notifying orchestrator...");
                        OnClientConnected?.Invoke(this, transport);
                        Console.WriteLine($"[WS-ACCEPT] Orchestrator notified for {ip}.");
                    }
                    catch (Exception wsEx)
                    {
                        Console.WriteLine($"[WS-ACCEPT] ERROR upgrading WebSocket from {remote}: {wsEx.Message}");
                    }
                }
                else
                {
                    var path = context.Request.Url?.AbsolutePath ?? "";
                    if (path.StartsWith("/nexshare/file/"))
                    {
                        var transferId = path.Substring("/nexshare/file/".Length).TrimEnd('/');
                        Console.WriteLine($"[WS-ACCEPT] File request for transferId={transferId}");
                        if (_hostedFiles.TryGetValue(transferId, out var filePath) && System.IO.File.Exists(filePath))
                        {
                            try
                            {
                                using var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
                                context.Response.ContentLength64 = fs.Length;
                                context.Response.ContentType = "application/octet-stream";
                                context.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                                await fs.CopyToAsync(context.Response.OutputStream, token);
                                Console.WriteLine($"[WS-ACCEPT] File transfer complete for {transferId}.");
                            }
                            catch (Exception fileEx)
                            {
                                Console.WriteLine($"[WS-ACCEPT] File transfer error: {fileEx.Message}");
                                context.Response.StatusCode = 500;
                            }
                            finally
                            {
                                context.Response.Close();
                            }
                            continue;
                        }
                        else
                        {
                            Console.WriteLine($"[WS-ACCEPT] transferId={transferId} NOT FOUND in hosted files.");
                            context.Response.StatusCode = 404;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[WS-ACCEPT] Non-WebSocket, non-file path: {path} — returning 400.");
                        context.Response.StatusCode = 400;
                    }
                    context.Response.Close();
                }
            }
            Console.WriteLine("[WS-ACCEPT] AcceptLoopAsync exiting (cancelled or listener stopped).");
        }
        catch (HttpListenerException ex) { Console.WriteLine($"[WS-ACCEPT] HttpListenerException: {ex.Message}"); }
        catch (ObjectDisposedException ex) { Console.WriteLine($"[WS-ACCEPT] ObjectDisposedException: {ex.Message}"); }
        catch (Exception ex) { Console.WriteLine($"[WS-ACCEPT] Unexpected error: {ex}"); }
    }

    public void RegisterFileOffer(string transferId, string filePath)
    {
        _hostedFiles[transferId] = filePath;
    }

    public void RemoveFileOffer(string transferId)
    {
        _hostedFiles.TryRemove(transferId, out _);
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        
        _listener?.Stop();
        _listener?.Close();
        _listener = null;
        _started = false;

        return Task.CompletedTask;
    }

    public async Task<INexShareTransport> ConnectOutboundAsync(string ip, int port)
    {
        var ws = new ClientWebSocket();
        var uri = new Uri($"ws://{ip}:{port}/nexshare");
        
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.ConnectAsync(uri, cts.Token);
        
        var transport = new LanWebSocketClientTransport(ws, ip);
        return transport;
    }

    public void Dispose()
    {
        _ = StopAsync();
    }
}

public class LanWebSocketClientTransport : INexShareTransport
{
    private readonly WebSocket _webSocket;
    private readonly CancellationTokenSource _cts;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public event EventHandler<WireEnvelope>? OnMessageReceived;
    public event EventHandler? OnConnected;
    public event EventHandler? OnDisconnected;
    private static int _dbgLoopCountGlobal = 0;
    private int _dbgLoopCount = 0;

    public bool IsConnected => _webSocket.State == WebSocketState.Open;
    public string RemoteIpAddress { get; }

    public LanWebSocketClientTransport(WebSocket webSocket, string remoteIpAddress)
    {
        _webSocket = webSocket;
        RemoteIpAddress = remoteIpAddress;
        _cts = new CancellationTokenSource();
    }

    public async Task StartReceiveLoopAsync()
    {
        var n = System.Threading.Interlocked.Increment(ref _dbgLoopCount);
        var gn = System.Threading.Interlocked.Increment(ref _dbgLoopCountGlobal);
        Console.WriteLine($"[WS-RECV #{n}/G{gn}] StartReceiveLoopAsync — transport={this.GetHashCode():X}, ip={RemoteIpAddress}, ws.State={_webSocket.State}");
        OnConnected?.Invoke(this, EventArgs.Empty);
        
        var buffer = new byte[65536];
        // A WebSocket message can span many ReceiveAsync results (EndOfMessage); screen-mirroring
        // frames are hundreds of KB, so messages are assembled before parsing.
        using var message = new System.IO.MemoryStream();
        const int MaxMessageBytes = 64 * 1024 * 1024;

        try
        {
            while (_webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"[WS-RECV #{n}] Close frame received. Closing.");
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    Console.WriteLine($"[WS-RECV #{n}] Message larger than {MaxMessageBytes} bytes, closing.");
                    await _webSocket.CloseAsync(WebSocketCloseStatus.MessageTooBig, string.Empty, CancellationToken.None);
                    break;
                }
                if (!result.EndOfMessage)
                    continue;

                var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                try
                {
                    var envelope = JsonSerializer.Deserialize<WireEnvelope>(json, _jsonOptions);
                    if (envelope != null)
                    {
                        if (envelope.Type is not ("mirror_frame" or "mirror_audio"))
                            Console.WriteLine($"[WS-RECV #{n}] type={envelope.Type} bytes={json.Length} msgId={envelope.MessageId}");
                        OnMessageReceived?.Invoke(this, envelope);
                    }
                    else
                    {
                        Console.WriteLine($"[WS-RECV #{n}] Deserialized envelope is NULL.");
                    }
                }
                catch (Exception parseEx)
                {
                    Console.WriteLine($"[WS-RECV #{n}] Parse error: {parseEx.Message} | raw={(json.Length > 300 ? json[..300] + "…" : json)}");
                }
            }
            Console.WriteLine($"[WS-RECV #{n}] Loop ended normally. ws.State={_webSocket.State}");
        }
        catch (WebSocketException wsEx) { Console.WriteLine($"[WS-RECV #{n}] WebSocketException: {wsEx.Message} (code={wsEx.WebSocketErrorCode})"); }
        catch (OperationCanceledException) { Console.WriteLine($"[WS-RECV #{n}] Cancelled."); }
        catch (Exception ex) { Console.WriteLine($"[WS-RECV #{n}] Unexpected: {ex}"); }
        finally
        {
            Console.WriteLine($"[WS-RECV #{n}] Firing OnDisconnected.");
            OnDisconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task ConnectAsync(string ip, int port)
    {
        throw new NotSupportedException("This is a server-side client connection. It is already connected.");
    }

    public async Task DisconnectAsync()
    {
        _cts.Cancel();
        if (_webSocket.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", CancellationToken.None);
            }
            catch { }
        }
        _webSocket.Dispose();
    }

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task SendMessageAsync(WireEnvelope envelope)
    {
        if (!IsConnected) return;
        
        var json = JsonSerializer.Serialize(envelope, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        // WebSocket allows one SendAsync at a time.
        await _sendLock.WaitAsync();
        try
        {
            await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        _ = DisconnectAsync();
    }
}
