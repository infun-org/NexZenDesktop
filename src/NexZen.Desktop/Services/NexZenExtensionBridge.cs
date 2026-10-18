using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using NexZen.Application.ViewModels;

namespace NexZen.Desktop.Services;

public class NexZenExtensionBridge
{
    private HttpListener _listener;
    private MainViewModel _viewModel;
    private bool _isRunning;

    public NexZenExtensionBridge(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        _listener = new HttpListener();
        _listener.Prefixes.Add("http://127.0.0.1:32001/");
        _listener.Prefixes.Add("http://localhost:32001/");
    }

    public void Start()
    {
        try
        {
            _isRunning = true;
            _listener.Start();
            Task.Run(ListenAsync);
            Console.WriteLine("[NexZenExtensionBridge] Started listening on port 32001");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NexZenExtensionBridge] Failed to start listener: {ex.Message}");
        }
    }

    public void Stop()
    {
        _isRunning = false;
        _listener.Stop();
    }

    private async Task ListenAsync()
    {
        while (_isRunning)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                var request = context.Request;
                var response = context.Response;

                response.AppendHeader("Access-Control-Allow-Origin", "*");
                response.AppendHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.AppendHeader("Access-Control-Allow-Headers", "Content-Type");

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = 200;
                    response.Close();
                    continue;
                }

                if (request.Url?.AbsolutePath == "/api/active_tab")
                {
                    var activeTab = _viewModel.ActiveTab;
                    string json = "null";
                    if (activeTab != null)
                    {
                        var tabData = new
                        {
                            id = activeTab.Id.GetHashCode(),
                            url = activeTab.Url,
                            title = activeTab.Title,
                            active = true,
                            windowId = 1
                        };
                        json = JsonSerializer.Serialize(tabData);
                    }

                    var buffer = System.Text.Encoding.UTF8.GetBytes(json);
                    response.ContentType = "application/json";
                    response.ContentLength64 = buffer.Length;
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                }
                else
                {
                    response.StatusCode = 404;
                }

                response.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Bridge Error: {ex.Message}");
            }
        }
    }
}
