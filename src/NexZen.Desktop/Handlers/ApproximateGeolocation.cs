using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xilium.CefGlue;

namespace NexZen.Desktop.Handlers;

/// <summary>
/// Chromium on Linux asks Google's geolocation service, and this build has no
/// real Google API key, so a granted permission still fails on the page.
/// An IP fix from BeaconDB is published through the DevTools geolocation override.
/// </summary>
internal static class ApproximateGeolocation
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private static Task<Position?>? _lookup;

    public readonly record struct Position(double Latitude, double Longitude, double Accuracy);

    public static void Start()
    {
        _ = GetAsync();
    }

    public static async Task PublishAsync(Func<CefBrowserHost?> hostFactory)
    {
        var position = await GetAsync();
        if (position == null)
            return;

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CefRuntime.PostTask(CefThreadId.UI, new UiActionTask(() =>
        {
            CefBrowserHost? host = null;
            CefDictionaryValue? parameters = null;
            try
            {
                host = hostFactory();
                if (host == null)
                    return;

                parameters = CefDictionaryValue.Create();
                parameters.SetDouble("latitude", position.Value.Latitude);
                parameters.SetDouble("longitude", position.Value.Longitude);
                parameters.SetDouble("accuracy", position.Value.Accuracy);
                var messageId = host.ExecuteDevToolsMethod(0, "Emulation.setGeolocationOverride", parameters);
                Console.WriteLine($"[ApproximateGeolocation] override id={messageId} lat={position.Value.Latitude} lng={position.Value.Longitude} acc={position.Value.Accuracy}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ApproximateGeolocation] override failed: {ex.Message}");
            }
            finally
            {
                parameters?.Dispose();
                host?.Dispose();
                done.TrySetResult();
            }
        }));
        await done.Task;
    }

    private static Task<Position?> GetAsync()
    {
        var existing = _lookup;
        if (existing is { IsFaulted: false, IsCanceled: false })
            return existing;

        _lookup = FetchAsync();
        return _lookup;
    }

    private static async Task<Position?> FetchAsync()
    {
        try
        {
            using var body = new StringContent("{\"considerIp\":true}", Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync("https://api.beacondb.net/v1/geolocate", body);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            var location = root.GetProperty("location");
            var accuracy = root.TryGetProperty("accuracy", out var accuracyValue) ? accuracyValue.GetDouble() : 5000;
            if (accuracy < 1)
                accuracy = 1;
            return new Position(location.GetProperty("lat").GetDouble(), location.GetProperty("lng").GetDouble(), accuracy);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ApproximateGeolocation] lookup failed: {ex.Message}");
            _lookup = null;
            return null;
        }
    }
}
