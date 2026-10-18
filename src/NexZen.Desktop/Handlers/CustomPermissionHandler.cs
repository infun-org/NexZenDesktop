using System;
using Avalonia.Threading;
using NexZen.Application.ViewModels;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Handlers;

namespace NexZen.Desktop.Handlers;

public class CustomPermissionHandler : PermissionHandler
{
    private readonly MainViewModel _viewModel;

    public CustomPermissionHandler(MainViewModel viewModel)
    {
        _viewModel = viewModel;
    }

    protected override bool OnShowPermissionPrompt(CefBrowser browser, ulong promptId, string requestingOrigin, CefPermissionRequestTypes requestedPermissions, CefPermissionPromptCallback callback)
    {
        Console.WriteLine($"[CustomPermissionHandler] OnShowPermissionPrompt requested for: {requestedPermissions} by {requestingOrigin}");
        if (requestedPermissions.HasFlag(CefPermissionRequestTypes.Geolocation))
            ApproximateGeolocation.Start();
        // Background runs after the pointer event that triggered the request, so the
        // popup is not created underneath that mouse-up.
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var result = await _viewModel.ConfirmPrompt.ShowAsync(
                    "Permission Request",
                    $"The website {requestingOrigin} is requesting permission for: {Describe(requestedPermissions)}",
                    negativeText: "Deny",
                    positiveText: "Allow");

                if (result == "Positive")
                {
                    if (requestedPermissions.HasFlag(CefPermissionRequestTypes.Geolocation))
                        await ApproximateGeolocation.PublishAsync(() => browser.GetHost());
                    callback.Continue(CefPermissionRequestResult.Accept);
                }
                else
                {
                    callback.Continue(CefPermissionRequestResult.Deny);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CustomPermissionHandler] prompt failed: {ex}");
                callback.Continue(CefPermissionRequestResult.Deny);
            }
            finally
            {
                callback.Dispose();
            }
        }, DispatcherPriority.Background);
        
        return true; // We handled it asynchronously
    }

    protected override bool OnRequestMediaAccessPermission(CefBrowser browser, CefFrame frame, string requestingOrigin, CefMediaAccessPermissionTypes requestedPermissions, CefMediaAccessCallback callback)
    {
        Console.WriteLine($"[CustomPermissionHandler] OnRequestMediaAccessPermission requested for: {requestedPermissions} by {requestingOrigin}");
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var result = await _viewModel.ConfirmPrompt.ShowAsync(
                    "Permission Request",
                    $"The website {requestingOrigin} is requesting permission for: {requestedPermissions}",
                    negativeText: "Deny",
                    positiveText: "Allow");
                
                if (result == "Positive")
                {
                    callback.Continue(requestedPermissions);
                }
                else
                {
                    callback.Continue(CefMediaAccessPermissionTypes.None);
                }
            }
            finally
            {
                callback.Dispose();
            }
        }, DispatcherPriority.Background);
        
        return true;
    }

    private static string Describe(CefPermissionRequestTypes requested)
    {
        if (requested == CefPermissionRequestTypes.None)
            return "an unspecified permission";

        var parts = new System.Collections.Generic.List<string>();
        void Take(CefPermissionRequestTypes flag, string label)
        {
            if (!requested.HasFlag(flag))
                return;
            parts.Add(label);
            requested &= ~flag;
        }

        Take(CefPermissionRequestTypes.Geolocation, "location");
        Take(CefPermissionRequestTypes.Notifications, "notifications");
        Take(CefPermissionRequestTypes.CameraStream, "camera");
        Take(CefPermissionRequestTypes.MicStream, "microphone");
        Take(CefPermissionRequestTypes.Clipboard, "clipboard");

        if (requested != CefPermissionRequestTypes.None)
            parts.Add(requested.ToString());

        return string.Join(", ", parts);
    }
}
