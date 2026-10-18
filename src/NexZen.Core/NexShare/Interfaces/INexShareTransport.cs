using System;
using System.Threading.Tasks;
using NexZen.Core.NexShare.Models;

namespace NexZen.Core.NexShare.Interfaces;

public interface INexShareTransport : IDisposable
{
    event EventHandler<WireEnvelope>? OnMessageReceived;
    event EventHandler? OnConnected;
    event EventHandler? OnDisconnected;

    bool IsConnected { get; }
    string RemoteIpAddress { get; }

    Task StartReceiveLoopAsync();
    Task ConnectAsync(string ip, int port);
    Task DisconnectAsync();
    Task SendMessageAsync(WireEnvelope envelope);
}

public interface INexShareTransportServer : IDisposable
{
    event EventHandler<INexShareTransport>? OnClientConnected;
    
    int Port { get; }
    
    Task StartAsync(int port = 0);
    Task StopAsync();
    Task<INexShareTransport> ConnectOutboundAsync(string ip, int port);
    
    void RegisterFileOffer(string transferId, string filePath);
    void RemoveFileOffer(string transferId);
}
