using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NexZen.Application.Services;
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

    /// <summary>
    /// The site's Site privacy choices for the requested permissions: true = grant, false = deny,
    /// null = ask. Any blocked permission denies the request; all allowed grants it.
    /// </summary>
    internal static bool? Decide(string requestingOrigin, IEnumerable<string> keys)
    {
        var store = App.ServiceProvider?.GetService<SitePermissionStore>();
        if (store == null)
            return null;
        var site = SiteDomains.SiteOf(requestingOrigin);
        var decisions = keys.Select(key => store.Decide(site, key)).ToList();
        if (decisions.Count == 0)
            return null;
        if (decisions.Any(d => d == false))
            return false;
        return decisions.All(d => d == true) ? true : null;
    }

    /// <summary>Saves Allow (for "Positive") or Block (for "Negative") for each permission; other answers (dismissed) are not saved.</summary>
    private static async Task RememberAsync(string requestingOrigin, IEnumerable<string> keys, string? result)
    {
        var value = result switch
        {
            "Positive" => SitePermissionValue.Allow,
            "Negative" => SitePermissionValue.Block,
            _ => SitePermissionValue.Default
        };
        var site = SiteDomains.SiteOf(requestingOrigin);
        var store = App.ServiceProvider?.GetService<SitePermissionStore>();
        if (value == SitePermissionValue.Default || site == null || store == null)
            return;
        foreach (var key in keys)
        {
            try
            {
                await store.SetAsync(site, key, value);
                NexZen.Desktop.Services.SiteContentSettings.Apply(null, site, key, value);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CustomPermissionHandler] remember {key} for {site}: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> KeysOf(CefPermissionRequestTypes requested)
    {
        if (requested.HasFlag(CefPermissionRequestTypes.Geolocation))
            yield return SitePermissionKeys.Location;
        if (requested.HasFlag(CefPermissionRequestTypes.Notifications))
            yield return SitePermissionKeys.Notifications;
        if (requested.HasFlag(CefPermissionRequestTypes.CameraStream))
            yield return SitePermissionKeys.Camera;
        if (requested.HasFlag(CefPermissionRequestTypes.MicStream))
            yield return SitePermissionKeys.Microphone;
        if (requested.HasFlag(CefPermissionRequestTypes.Sensors))
            yield return SitePermissionKeys.Motion;
        if (requested.HasFlag(CefPermissionRequestTypes.MultipleDownloads))
            yield return SitePermissionKeys.AutomaticDownloads;
        if (requested.HasFlag(CefPermissionRequestTypes.MidiSysex))
            yield return SitePermissionKeys.Midi;
        if (requested.HasFlag(CefPermissionRequestTypes.FileSystemAccess))
            yield return SitePermissionKeys.FileEditing;
        if (requested.HasFlag(CefPermissionRequestTypes.Clipboard))
            yield return SitePermissionKeys.Clipboard;
        if (requested.HasFlag(CefPermissionRequestTypes.IdentityProvider))
            yield return SitePermissionKeys.ThirdPartySignIn;
        if (requested.HasFlag(CefPermissionRequestTypes.ArSession))
            yield return SitePermissionKeys.AugmentedReality;
        if (requested.HasFlag(CefPermissionRequestTypes.VrSession))
            yield return SitePermissionKeys.VirtualReality;
        if (requested.HasFlag(CefPermissionRequestTypes.IdleDetection))
            yield return SitePermissionKeys.DeviceUse;
        if (requested.HasFlag(CefPermissionRequestTypes.WindowManagement))
            yield return SitePermissionKeys.WindowManagement;
        if (requested.HasFlag(CefPermissionRequestTypes.LocalFonts))
            yield return SitePermissionKeys.Fonts;
        if (requested.HasFlag(CefPermissionRequestTypes.CapturedSurfaceControl))
            yield return SitePermissionKeys.SharedTabsControl;
        if (requested.HasFlag(CefPermissionRequestTypes.WebAppInstallation))
            yield return SitePermissionKeys.WebAppInstallations;
        if (requested.HasFlag(CefPermissionRequestTypes.LocalNetworkAccess) || requested.HasFlag(CefPermissionRequestTypes.LocalNetwork))
            yield return SitePermissionKeys.LocalNetwork;
        if (requested.HasFlag(CefPermissionRequestTypes.LoopbackNetwork))
            yield return SitePermissionKeys.AppsOnDevice;
    }

    internal static IEnumerable<string> KeysOf(CefMediaAccessPermissionTypes requested)
    {
        if (requested.HasFlag(CefMediaAccessPermissionTypes.DeviceVideoCapture))
            yield return SitePermissionKeys.Camera;
        if (requested.HasFlag(CefMediaAccessPermissionTypes.DeviceAudioCapture))
            yield return SitePermissionKeys.Microphone;
    }

    protected override bool OnShowPermissionPrompt(CefBrowser browser, ulong promptId, string requestingOrigin, CefPermissionRequestTypes requestedPermissions, CefPermissionPromptCallback callback)
    {
        Console.WriteLine($"[CustomPermissionHandler] OnShowPermissionPrompt requested for: {requestedPermissions} by {requestingOrigin}");
        // Site privacy: a saved Allow/Block answers without asking.
        var decided = Decide(requestingOrigin, KeysOf(requestedPermissions));
        if (decided != null)
        {
            if (decided == true && requestedPermissions.HasFlag(CefPermissionRequestTypes.Geolocation))
            {
                ApproximateGeolocation.Start();
                Dispatcher.UIThread.Post(async () =>
                {
                    await ApproximateGeolocation.PublishAsync(() => browser.GetHost());
                    callback.Continue(CefPermissionRequestResult.Accept);
                    callback.Dispose();
                });
                return true;
            }
            callback.Continue(decided == true ? CefPermissionRequestResult.Accept : CefPermissionRequestResult.Deny);
            callback.Dispose();
            return true;
        }
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

                // Like Chrome, the answer is remembered for the site (shown in Site privacy).
                await RememberAsync(requestingOrigin, KeysOf(requestedPermissions), result);
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
        var decided = Decide(requestingOrigin, KeysOf(requestedPermissions));
        if (decided != null)
        {
            callback.Continue(decided == true ? requestedPermissions : CefMediaAccessPermissionTypes.None);
            callback.Dispose();
            return true;
        }
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var result = await _viewModel.ConfirmPrompt.ShowAsync(
                    "Permission Request",
                    $"The website {requestingOrigin} is requesting permission for: {requestedPermissions}",
                    negativeText: "Deny",
                    positiveText: "Allow");
                
                await RememberAsync(requestingOrigin, KeysOf(requestedPermissions), result);
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
