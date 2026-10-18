using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;

namespace NexZen.Desktop.Helpers;

public class ScreenshotDevToolsObserver : CefDevToolsMessageObserver
{
    private readonly Action<Bitmap?> _onComplete;

    public ScreenshotDevToolsObserver(Action<Bitmap?> onComplete)
    {
        _onComplete = onComplete;
    }

    protected override bool OnDevToolsMessage(CefBrowser browser, IntPtr message, int messageSize)
    {
        return false; // Not handled here
    }

    protected override void OnDevToolsMethodResult(CefBrowser browser, int messageId, bool success, IntPtr result, int resultSize)
    {
        if (!success || result == IntPtr.Zero || resultSize <= 0)
        {
            _onComplete(null);
            return;
        }

        try
        {
            byte[] bytes = new byte[resultSize];
            Marshal.Copy(result, bytes, 0, resultSize);
            string json = System.Text.Encoding.UTF8.GetString(bytes);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("data", out var dataElement))
            {
                string base64Data = dataElement.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(base64Data))
                {
                    byte[] imageBytes = Convert.FromBase64String(base64Data);
                    using var stream = new MemoryStream(imageBytes);
                    var bitmap = new Bitmap(stream);
                    _onComplete(bitmap);
                    return;
                }
            }
            
            _onComplete(null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Screenshot Error] {ex.Message}");
            _onComplete(null);
        }
    }

    protected override void OnDevToolsEvent(CefBrowser browser, string method, IntPtr parameters, int parametersSize)
    {
    }

    protected override void OnDevToolsAgentAttached(CefBrowser browser)
    {
    }

    protected override void OnDevToolsAgentDetached(CefBrowser browser)
    {
    }
}

public static class ScreenshotHelper
{
    private static int _messageIdCounter = 1;

    public static Task<Bitmap?> CaptureAsync(AvaloniaCefBrowser avaloniaBrowser)
    {
        var tcs = new TaskCompletionSource<Bitmap?>();

        var propInfo = typeof(Xilium.CefGlue.Common.BaseCefBrowser).GetProperty("UnderlyingBrowser", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var cefBrowser = propInfo?.GetValue(avaloniaBrowser) as CefBrowser;
        if (cefBrowser == null)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }

        var host = cefBrowser.GetHost();
        if (host == null)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }

        var observer = new ScreenshotDevToolsObserver(bitmap => 
        {
            tcs.TrySetResult(bitmap);
        });

        host.AddDevToolsMessageObserver(observer);

        var parameters = CefDictionaryValue.Create();
        parameters.SetString("format", "jpeg");
        parameters.SetInt("quality", 50);

        int msgId = _messageIdCounter++;
        host.ExecuteDevToolsMethod(msgId, "Page.captureScreenshot", parameters);

        return tcs.Task;
    }
}
