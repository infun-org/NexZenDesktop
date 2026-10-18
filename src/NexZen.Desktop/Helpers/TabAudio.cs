using System;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;

namespace NexZen.Desktop.Helpers;

static class TabAudio
{
    public static void Apply(AvaloniaCefBrowser browser, bool muted)
    {
        var posted = CefRuntime.PostTask(CefThreadId.UI, new NexZen.Desktop.UiActionTask(() =>
        {
            try
            {
                var host = browser.GetBrowserHost();
                if (host == null)
                    return;
                try
                {
                    host.SetAudioMuted(muted);
                }
                finally
                {
                    host.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Mute] {ex.Message}");
            }
        }));
        if (!posted)
            Console.WriteLine("[Mute] Could not reach Chromium to change tab audio.");
    }
}
