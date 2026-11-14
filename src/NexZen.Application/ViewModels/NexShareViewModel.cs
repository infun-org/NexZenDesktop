using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexZen.Application.Services;
using NexZen.Core.NexShare.Interfaces;
using NexZen.Core.NexShare.Models;

using NexZen.Core.Interfaces;

namespace NexZen.Application.ViewModels;

public partial class NexShareViewModel : ObservableObject
{
    private readonly INexShareOrchestrator _orchestrator;
    public INexShareOrchestrator Orchestrator => _orchestrator;
    private readonly ISettingsService _settingsService;

    public Action<Action>? UiInvoker { get; set; }

    [ObservableProperty]
    private string _connectionStatus = "Ready to pair";

    public const string DeviceNamePrefix = "NexZen - ";
    public const int MaxDeviceLabelLength = 40;

    [ObservableProperty]
    private string _deviceName = "NexZen Desktop";

    [ObservableProperty]
    private string _deviceLabel = "";

    public string DeviceNamePreview => FormatDeviceName(DeviceLabel);

    [ObservableProperty]
    private bool _isActive = false;

    [ObservableProperty]
    private string _messageInput = string.Empty;

    public ObservableCollection<ChatMessageItem> ChatMessages { get; } = new();
    public ObservableCollection<DiscoveredDeviceItem> DiscoveredDevices { get; } = new();
    public ObservableCollection<PeerChoiceItem> PeerChoices { get; } = new();

    [ObservableProperty]
    private bool _isPeerPickerOpen;

    private int _peerWaitGeneration;
    private TaskCompletionSource<DiscoveryMessage?>? _peerChoice;
    private PeerLinkState? _peer;
    public ObservableCollection<FileOfferItem> DownloadItems { get; } = new();
    public ObservableCollection<string> UploadItems { get; } = new();
    public ObservableCollection<UrlOfferItem> UrlOffers { get; } = new();
    public bool HasUrlOffers => UrlOffers.Count > 0;
    private int _dbgUiCount = 0;
    
    private string _lastReceivedMessage = string.Empty;

    private readonly object _debounceLock = new object();
    private bool _deferAutoStart = true;
    private int _suppressActiveChange;
    private int _startGeneration;

    public NexShareViewModel(INexShareOrchestrator orchestrator, ISettingsService settingsService)
    {
        UrlOffers.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasUrlOffers));
        
        _orchestrator = orchestrator;
        _settingsService = settingsService;
        
        _orchestrator.OnConnectionStatusChanged += (s, e) =>
            InvokeOnUI(() =>
            {
                // A start that finishes after the switch was turned off must
                // not replace the Offline status.
                if (!IsActive)
                    return;
                ConnectionStatus = e;
            });
            
        _orchestrator.OnPeerConnectionChanged += (s, e) =>
            InvokeOnUI(() =>
            {
                _peer = e;
                ApplyPeerToDevices();
            });

        _orchestrator.OnSasRequested += (s, e) => InvokeOnUI(() => ShowSas(e));

        _orchestrator.OnDeviceFound += (s, e) =>
            InvokeOnUI(() => 
            {
                // One row per deviceId: LAN and BLE sightings merge (badges LAN / BLE).
                var existing = DiscoveredDevices.FirstOrDefault(d => d.DeviceId == e.DeviceId);
                if (existing == null)
                    DiscoveredDevices.Add(new DiscoveredDeviceItem(e));
                else
                    existing.Apply(e);
                ApplyPeerToDevices();
            });

        _orchestrator.OnRefreshed += (s, byPeer) =>
            InvokeOnUI(() =>
            {
                // Android fullReset: back to an empty device list; nothing reconnects by itself.
                System.Threading.Interlocked.Increment(ref _peerWaitGeneration);
                ClosePeerPicker();
                var sas = ActiveSas;
                ActiveSas = null;
                sas?.Reject();
                _peer = null;
                DiscoveredDevices.Clear();
                ChatMessages.Add(SystemChat(byPeer ? "The other device refreshed the connection." : "Refreshed. Looking for devices..."));
            });

        _ = PruneLoopAsync();
            
        _orchestrator.OnTextMessageReceived += (s, e) =>
        {
            var n = System.Threading.Interlocked.Increment(ref _dbgUiCount);
            Console.WriteLine($"[DEBUG-VM] OnTextMessageReceived UI handler #{n}, text={e}");
            InvokeOnUI(() => ChatMessages.Add(new ChatMessageItem { Text = e, IsMine = false }));
        };
            
        _orchestrator.OnFileOfferReceived += (s, e) =>
            InvokeOnUI(() => 
            {
                ChatMessages.Add(new ChatMessageItem { Text = $"[File Offer: {e.FileName}]", IsSystem = true });
                DownloadItems.Add(new FileOfferItem { TransferId = e.TransferId, FileName = e.FileName, FileSize = e.FileSize });
            });

        _orchestrator.OnUrlOfferReceived += (s, e) =>
        {
            // "Open on another device" is an explicit send. Always open a tab; do not wait for
            // TrustedDevices (that list is device names, so a SAS-trusted phone still failed to open).
            InvokeOnUI(() =>
            {
                if (!string.IsNullOrEmpty(e.DeviceName) && !_settingsService.TrustedDevices.Contains(e.DeviceName))
                {
                    _settingsService.TrustedDevices.Add(e.DeviceName);
                    _settingsService.Save();
                }
                ChatMessages.Add(new ChatMessageItem { Text = $"[Shared Link: {e.Title}]", IsSystem = true });
                OnOpenUrlRequested?.Invoke(this, e.Url);
            });
        };

        // NexShare does not start here. MainWindow calls StartAfterBrowserReady
        // once the first page can load, so browsing is not competing with discovery.
        var savedLabel = SanitizeDeviceLabel(_settingsService.NexShareDeviceLabel);
        DeviceLabel = string.IsNullOrEmpty(savedLabel) ? DefaultDeviceLabel() : savedLabel;
        DeviceName = DeviceNamePrefix + DeviceLabel;
        _orchestrator.SetDeviceName(DeviceName);
    }

    // ---- BLE pairing: 6-digit SAS comparison ----

    /// <summary>Raised on the UI thread when a SAS prompt needs the NexShare window.</summary>
    public event EventHandler? OnSasShowRequested;

    [ObservableProperty]
    private SasPrompt? _activeSas;

    [ObservableProperty]
    private int _sasSecondsLeft;

    public bool IsSasOpen => ActiveSas != null;
    public string SasCodeText => ActiveSas?.DisplayCode ?? string.Empty;
    public string SasPeerText
    {
        get
        {
            var s = ActiveSas;
            if (s == null) return string.Empty;
            var name = s.PeerName ?? DiscoveredDevices.FirstOrDefault(d => d.DeviceId == s.PeerId)?.DeviceName;
            return $"Make sure {(string.IsNullOrWhiteSpace(name) ? "the other device" : name)} shows the same code.";
        }
    }

    partial void OnActiveSasChanged(SasPrompt? value)
    {
        OnPropertyChanged(nameof(IsSasOpen));
        OnPropertyChanged(nameof(SasCodeText));
        OnPropertyChanged(nameof(SasPeerText));
    }

    private void ShowSas(SasPrompt prompt)
    {
        var previous = ActiveSas;
        if (previous != null && !ReferenceEquals(previous, prompt)) previous.Reject();
        ActiveSas = prompt;
        SasSecondsLeft = SasPrompt.TimeoutSeconds;
        OnSasShowRequested?.Invoke(this, EventArgs.Empty);
        _ = SasCountdownAsync(prompt);
    }

    private async Task SasCountdownAsync(SasPrompt prompt)
    {
        while (!prompt.IsDone)
        {
            await Task.Delay(1000);
            var left = (int)Math.Ceiling((prompt.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds);
            InvokeOnUI(() =>
            {
                if (!ReferenceEquals(ActiveSas, prompt)) return;
                if (left <= 0)
                {
                    ActiveSas = null;
                    prompt.Reject();
                    ConnectionStatus = "Pairing timed out";
                }
                else SasSecondsLeft = left;
            });
            if (left <= 0) return;
        }
    }

    [RelayCommand]
    private void ConfirmSas()
    {
        var s = ActiveSas;
        ActiveSas = null;
        if (s == null) return;
        s.Confirm();
        ConnectionStatus = "Code confirmed. Waiting for the other device...";
    }

    [RelayCommand]
    private void RejectSas()
    {
        var s = ActiveSas;
        ActiveSas = null;
        if (s == null) return;
        s.Reject();
        ConnectionStatus = "Pairing rejected";
    }

    partial void OnDeviceLabelChanged(string value)
    {
        OnPropertyChanged(nameof(DeviceNamePreview));
    }

    [RelayCommand]
    private void SaveDeviceLabel()
    {
        var clean = SanitizeDeviceLabel(DeviceLabel);
        if (string.IsNullOrEmpty(clean))
            clean = DefaultDeviceLabel();
        DeviceLabel = clean;
        DeviceName = DeviceNamePrefix + clean;
        _settingsService.NexShareDeviceLabel = clean;
        _settingsService.Save();
        _orchestrator.SetDeviceName(DeviceName);
    }

    private static string DefaultDeviceLabel()
    {
        var host = SanitizeDeviceLabel(Environment.MachineName);
        return string.IsNullOrEmpty(host) ? "Desktop" : host;
    }

    private static string SanitizeDeviceLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";
        var text = raw.Trim().Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        while (text.Contains("  ", StringComparison.Ordinal))
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        if (text.StartsWith(DeviceNamePrefix, StringComparison.OrdinalIgnoreCase))
            text = text.Substring(DeviceNamePrefix.Length).Trim();
        text = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > MaxDeviceLabelLength)
            text = text.Substring(0, MaxDeviceLabelLength).Trim();
        return text;
    }

    private static string FormatDeviceName(string? label)
    {
        var clean = SanitizeDeviceLabel(label);
        if (string.IsNullOrEmpty(clean))
            clean = DefaultDeviceLabel();
        return DeviceNamePrefix + clean;
    }

    /// <summary>
    /// Starts the service once, after the browser is ready. No-op if the user
    /// already moved the On/Off switch.
    /// </summary>
    public void StartAfterBrowserReady()
    {
        if (NexShareHost.IsPreviewer)
            return;
        if (!_deferAutoStart || IsActive)
            return;
        _deferAutoStart = false;
        IsActive = true;
    }

    private void InvokeOnUI(Action action)
    {
        if (UiInvoker != null)
            UiInvoker(action);
        else
            action();
    }

    private async Task StartServiceAsync(int generation)
    {
        if (NexShareHost.IsPreviewer)
        {
            InvokeOnUI(() => RefuseStartInPreviewer(generation));
            return;
        }
        var portsOpen = await NexShareFirewall.ArePortsOpenAsync();
        if (generation != _startGeneration || !IsActive)
            return;
        if (!portsOpen)
        {
            InvokeOnUI(() => RefuseStartBecausePortsAreClosed(generation));
            return;
        }
        await _orchestrator.StartAsync(DeviceName);
    }

    private void RefuseStartInPreviewer(int generation)
    {
        if (generation != _startGeneration || !IsActive)
            return;
        ConnectionStatus = "Ready to pair";
        _suppressActiveChange++;
        IsActive = false;
        _suppressActiveChange--;
    }

    private void RefuseStartBecausePortsAreClosed(int generation)
    {
        if (generation != _startGeneration || !IsActive)
            return;
        ConnectionStatus = NexShareFirewall.PortsClosedMessage;
        _suppressActiveChange++;
        IsActive = false;
        _suppressActiveChange--;
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(MessageInput)) return;
        if (!await EnsurePeerForSendAsync()) return;
        var txt = MessageInput;
        if (string.IsNullOrWhiteSpace(txt)) return;
        MessageInput = string.Empty;
        ChatMessages.Add(new ChatMessageItem { Text = txt, IsMine = true });
        await _orchestrator.SendTextMessageAsync(txt);
    }

    [RelayCommand]
    private async Task ScanQrAsync()
    {
        // TODO: Open a webcam popup and scan QR with ZXing.Net.
        // Needs an extra library (OpenCvSharp or AForge).
        ChatMessages.Add(new ChatMessageItem { Text = "System: Camera QR scanning is not available yet.", IsSystem = true });
        await Task.CompletedTask;
    }
    
    [RelayCommand]
    private async Task ConfigureFirewallAsync()
    {
        ChatMessages.Add(new ChatMessageItem { Text = "System: Requesting Firewall Access...", IsSystem = true });
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "pkexec",
                    Arguments = "bash -c \"" + NexShareFirewall.LinuxAllowScript + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using var process = System.Diagnostics.Process.Start(processInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode == 0)
                    {
                        ChatMessages.Add(new ChatMessageItem { Text = "System: Firewall configured successfully. Turn NexShare on.", IsSystem = true });
                    }
                    else
                    {
                        ChatMessages.Add(new ChatMessageItem { Text = $"System: Firewall config failed. (Code: {process.ExitCode})", IsSystem = true });
                    }
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                var processInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = NexShareFirewall.WindowsAllowArgs,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using var process = System.Diagnostics.Process.Start(processInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    if (process.ExitCode == 0)
                        ChatMessages.Add(new ChatMessageItem { Text = "System: Firewall configured successfully. Turn NexShare on.", IsSystem = true });
                    else
                        ChatMessages.Add(new ChatMessageItem { Text = $"System: Firewall config failed. (Code: {process.ExitCode})", IsSystem = true });
                }
            }
            else
            {
                ChatMessages.Add(new ChatMessageItem { Text = "System: Unsupported OS for auto-firewall config.", IsSystem = true });
            }
        }
        catch (Exception ex)
        {
            ChatMessages.Add(new ChatMessageItem { Text = $"System: Error configuring firewall: {ex.Message}", IsSystem = true });
        }
    }

    public async Task SendFileAsync(string filePath)
    {
        if (!await EnsurePeerForSendAsync()) return;
        var name = System.IO.Path.GetFileName(filePath);
        if (!string.IsNullOrWhiteSpace(name))
        {
            InvokeOnUI(() =>
            {
                UploadItems.Remove(name);
                UploadItems.Insert(0, name);
            });
        }
        await _orchestrator.SendFileAsync(filePath);
    }

    /// <summary>
    /// Uses the current session, or scans the LAN the same way as Open in another device.
    /// One reachable device connects automatically. Two or more open the picker.
    /// </summary>
    public async Task<bool> EnsurePeerForSendAsync()
    {
        var generation = System.Threading.Interlocked.Increment(ref _peerWaitGeneration);
        await OnUi(ClosePeerPicker);

        if (await OnUi(() => _peer?.IsConnected == true))
            return true;

        await OnUi(() =>
        {
            if (!IsActive)
                IsActive = true;
        });

        var initial = await OnUi(ReachableDevices);
        var waitMs = initial.Count >= 2 ? 0 : initial.Count == 1 ? 1200 : 4000;
        if (waitMs > 0)
            await OnUi(() => ChatMessages.Add(SystemChat("Looking for devices...")));

        var started = Environment.TickCount64;
        while (Environment.TickCount64 - started < waitMs)
        {
            if (generation != Volatile.Read(ref _peerWaitGeneration))
                return false;
            await Task.Delay(200);
            var stop = await OnUi(() =>
                ReachableDevices().Count >= 2
                || (!IsActive && (ConnectionStatus?.Contains("Ports are closed", StringComparison.Ordinal) == true)));
            if (stop)
                break;
        }

        if (generation != Volatile.Read(ref _peerWaitGeneration))
            return false;

        var devices = await OnUi(ReachableDevices);
        if (devices.Count == 0)
        {
            await OnUi(() =>
            {
                var closed = ConnectionStatus?.Contains("Ports are closed", StringComparison.Ordinal) == true;
                ChatMessages.Add(SystemChat(closed
                    ? "Ports are closed. Press Fix Firewall in the NexShare widget."
                    : "No other device found."));
            });
            return false;
        }

        var device = devices[0];
        if (devices.Count > 1)
        {
            var chosen = await PickPeerAsync(devices);
            if (chosen == null || generation != Volatile.Read(ref _peerWaitGeneration))
                return false;
            device = chosen;
        }

        return await ConnectForSendAsync(device);
    }

    [RelayCommand]
    private void ChoosePeer(PeerChoiceItem? item)
    {
        IsPeerPickerOpen = false;
        var pending = _peerChoice;
        _peerChoice = null;
        pending?.TrySetResult(item?.Device);
    }

    [RelayCommand]
    private void CancelPeerPick()
    {
        ClosePeerPicker();
    }

    private void ClosePeerPicker()
    {
        IsPeerPickerOpen = false;
        var pending = _peerChoice;
        _peerChoice = null;
        pending?.TrySetResult(null);
    }

    /// <summary>Raised when the device picker needs the NexShare window (open-on-device flow).</summary>
    public event EventHandler? OnPickerShowRequested;

    /// <summary>Shows the picker in the NexShare window; null when cancelled.</summary>
    public Task<DiscoveryMessage?> PickDeviceAsync(IReadOnlyList<DiscoveryMessage> devices)
    {
        var t = PickPeerAsync(devices);
        InvokeOnUI(() => OnPickerShowRequested?.Invoke(this, EventArgs.Empty));
        return t;
    }

    private Task<DiscoveryMessage?> PickPeerAsync(IReadOnlyList<DiscoveryMessage> devices)
    {
        var pending = new TaskCompletionSource<DiscoveryMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _peerChoice = pending;
        InvokeOnUI(() =>
        {
            PeerChoices.Clear();
            foreach (var device in devices)
                PeerChoices.Add(new PeerChoiceItem(device));
            IsPeerPickerOpen = true;
        });
        return pending.Task;
    }

    private async Task<bool> ConnectForSendAsync(DiscoveryMessage device)
    {
        var name = string.IsNullOrWhiteSpace(device.DeviceName) ? device.Host : device.DeviceName;
        try
        {
            // LAN, or BLE -> SAS -> Wi-Fi Direct; waits until the HELLO exchange is done.
            if (!await _orchestrator.ConnectAndWaitAsync(device, NexShareOrchestrator.PendingSendTimeout))
            {
                await OnUi(() => ChatMessages.Add(SystemChat($"Couldn't connect to {name}.")));
                return false;
            }
        }
        catch (Exception ex)
        {
            await OnUi(() => ChatMessages.Add(SystemChat($"Couldn't connect to {name}.")));
            Console.WriteLine($"[NexShare] Connect for send failed: {ex.Message}");
            return false;
        }

        var connected = await OnUi(() =>
            _peer?.IsConnected == true
            && !(ConnectionStatus ?? "").StartsWith("Failed", StringComparison.Ordinal));
        if (!connected)
        {
            await OnUi(() => ChatMessages.Add(SystemChat($"Couldn't connect to {name}.")));
            return false;
        }
        return true;
    }

    private static ChatMessageItem SystemChat(string text)
    {
        return new ChatMessageItem { Text = "System: " + text, IsSystem = true };
    }

    private Task OnUi(Action action)
    {
        return OnUi(() =>
        {
            action();
            return true;
        });
    }

    private Task<T> OnUi<T>(Func<T> read)
    {
        if (UiInvoker == null)
            return Task.FromResult(read());
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        UiInvoker(() =>
        {
            try { done.TrySetResult(read()); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return done.Task;
    }

    public async Task SendUrlAsync(string url, string title)
    {
        await _orchestrator.SendUrlAsync(url, title);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _orchestrator.RefreshAsync();
    }

    /// <summary>Every 5 s: drop rows not seen on LAN or BLE for a minute (unless connected), update badges.</summary>
    private async Task PruneLoopAsync()
    {
        while (true)
        {
            await Task.Delay(5000);
            InvokeOnUI(() =>
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var item in DiscoveredDevices.ToList())
                {
                    if (!item.IsConnected && item.Entry.IsStale(now))
                        DiscoveredDevices.Remove(item);
                    else
                        item.RefreshPresence();
                }
            });
        }
    }

    [RelayCommand]
    private async Task ConnectDeviceAsync(DiscoveredDeviceItem? item)
    {
        if (item == null || item.IsConnected)
            return;
        await _orchestrator.ConnectToDeviceAsync(item.Device);
    }

    [RelayCommand]
    private async Task DisconnectDeviceAsync(DiscoveredDeviceItem? item)
    {
        if (item == null || !item.IsConnected)
            return;
        await _orchestrator.DisconnectCurrentAsync();
    }

    private void ApplyPeerToDevices()
    {
        var peer = _peer;
        var connected = peer?.IsConnected == true;
        DiscoveredDeviceItem? byId = null;
        if (connected && !string.IsNullOrEmpty(peer!.DeviceId))
            byId = DiscoveredDevices.FirstOrDefault(d => d.DeviceId == peer.DeviceId);

        foreach (var item in DiscoveredDevices)
        {
            if (!connected)
                item.IsConnected = false;
            else if (byId != null)
                item.IsConnected = ReferenceEquals(item, byId);
            else
                item.IsConnected = !string.IsNullOrEmpty(peer!.Host)
                    && string.Equals(item.Host, peer.Host, StringComparison.OrdinalIgnoreCase);
        }
    }

    public const int ReachableDeviceWindowMs = 15_000;

    /// <summary>
    /// Devices heard on the LAN in the last few seconds. Call on the UI thread.
    /// </summary>
    public List<DiscoveryMessage> ReachableDevices()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return DiscoveredDevices
            .Where(d => d.IsConnected
                || (d.Entry.LanFresh(now) && d.Device.TcpPort > 0)
                || (d.Entry.BleFresh(now) && NexZen.Core.NexShare.NexShareRouting.HasBleWfd(d.Device)))
            .Select(d => d.Device)
            .ToList();
    }

    /// <summary>Queues the link until the device is connected (LAN or BLE -> Wi-Fi Direct), then sends it.</summary>
    public Task<bool> SendUrlToDeviceAsync(DiscoveryMessage device, string url, string title) =>
        _orchestrator.SendUrlToDeviceAsync(device, url, title);

    public event EventHandler<(string Url, string FileName)>? OnDownloadRequested;
    public event EventHandler<string>? OnOpenUrlRequested;

    [RelayCommand]
    private void AcceptUrl(UrlOfferItem item)
    {
        if (item == null) return;
        UrlOffers.Remove(item);
        OnOpenUrlRequested?.Invoke(this, item.Url);
    }

    [RelayCommand]
    private void AlwaysAllowUrl(UrlOfferItem item)
    {
        if (item == null) return;
        if (!string.IsNullOrEmpty(item.DeviceName) && !_settingsService.TrustedDevices.Contains(item.DeviceName))
        {
            _settingsService.TrustedDevices.Add(item.DeviceName);
            _settingsService.Save();
        }
        AcceptUrl(item);
    }

    [RelayCommand]
    private void DeclineUrl(UrlOfferItem item)
    {
        if (item == null) return;
        UrlOffers.Remove(item);
    }

    [RelayCommand]
    private async Task AcceptFileAsync(FileOfferItem item)
    {
        if (item == null) return;
        DownloadItems.Remove(item);
        var url = await _orchestrator.AcceptFileAsync(item.TransferId);
        if (!string.IsNullOrEmpty(url))
        {
            OnDownloadRequested?.Invoke(this, (url, item.FileName));
        }
    }

    partial void OnIsActiveChanged(bool value)
    {
        // The switch now belongs to the user (or to the one deferred start).
        // Turning it off before that start fires must not start the service later.
        if (_suppressActiveChange > 0)
            return;
        _deferAutoStart = false;
        if (value)
        {
            ConnectionStatus = "Starting service...";
            var generation = System.Threading.Interlocked.Increment(ref _startGeneration);
            _ = StartServiceAsync(generation);
        }
        else
        {
            System.Threading.Interlocked.Increment(ref _startGeneration);
            _ = _orchestrator.StopAsync();
            ConnectionStatus = "Offline";
            DiscoveredDevices.Clear();
            _peer = null;
        }
    }
}

public partial class DiscoveredDeviceItem : ObservableObject
{
    public DiscoveredDeviceItem(DiscoveryMessage device)
    {
        Entry = new NexZen.Core.NexShare.NexShareDeviceEntry(device, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _deviceName = device.DeviceName ?? "";
    }

    /// <summary>Merged LAN+BLE state with per-source last-seen times.</summary>
    public NexZen.Core.NexShare.NexShareDeviceEntry Entry { get; }

    public DiscoveryMessage Device => Entry.Device;

    public long LastSeenUnixMs => Entry.LastSeenMs;

    public string DeviceId => Device.DeviceId;

    public string Host => Device.Host;

    [ObservableProperty]
    private string _deviceName;

    [ObservableProperty]
    private bool _isConnected;

    /// <summary>Seen over Bluetooth LE within the last minute.</summary>
    public bool HasBle => Entry.BleFresh(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>Heard on the LAN within the last 15 s.</summary>
    public bool HasLan => Entry.LanFresh(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    public void Apply(DiscoveryMessage device)
    {
        Entry.Touch(device, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        DeviceName = Entry.Device.DeviceName ?? "";
        OnPropertyChanged(nameof(DeviceId));
        OnPropertyChanged(nameof(Host));
        RefreshPresence();
    }

    public void RefreshPresence()
    {
        OnPropertyChanged(nameof(HasBle));
        OnPropertyChanged(nameof(HasLan));
    }
}

public sealed class PeerChoiceItem
{
    public PeerChoiceItem(DiscoveryMessage device)
    {
        Device = device;
        Name = string.IsNullOrWhiteSpace(device.DeviceName) ? device.Host : device.DeviceName;
    }

    public DiscoveryMessage Device { get; }

    public string Name { get; }
}

public class ChatMessageItem
{
    public string Text { get; set; } = string.Empty;
    public bool IsMine { get; set; }
    public bool IsSystem { get; set; }
    
    public string BubbleColor => IsSystem ? "#fff3cd" : (IsMine ? "#C5F0C8" : "#B7DFF5");
    public string TextColor => IsSystem ? "#856404" : "#222222";
    public string Alignment => IsSystem ? "Center" : (IsMine ? "Right" : "Left");
}

public class FileOfferItem
{
    public string TransferId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }

    public bool IsImage
    {
        get
        {
            var ext = System.IO.Path.GetExtension(FileName ?? "").ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
        }
    }
}

public class UrlOfferItem
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
}
