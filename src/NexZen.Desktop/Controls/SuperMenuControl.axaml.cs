using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using NexZen.Application.Services;
using NexZen.Application.ViewModels;
using NexZen.Core;
using Avalonia.Markup.Xaml;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.VisualTree;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NexZen.Desktop.Services;
using NexZen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace NexZen.Desktop.Controls;

public partial class SuperMenuControl : UserControl
{
    private int _selectedSuggestionIndex = -1;
    public event EventHandler? BackRequested;
    public event EventHandler? BackHistoryRequested;
    public event EventHandler? ForwardRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler<string>? NavigateRequested;
    public event EventHandler<string>? HistoryItemClicked;
    public event EventHandler? NewTabRequested;
    public event EventHandler? CloseActiveTabRequested;
    public event EventHandler? ReopenClosedTabRequested;
    public event EventHandler<string>? SwitchTabRequested;
    public event EventHandler<string>? CloseTabRequested;
    public event EventHandler<string>? OpenTabInNewWindowRequested;
    public event EventHandler? CopyRequested;
    public event EventHandler? CutRequested;
    public event EventHandler? PasteRequested;
    public event EventHandler? SaveImageRequested;
    public event EventHandler? CopyImageAddressRequested;
    public event EventHandler? InstallExtensionRequested;
    public event EventHandler? InstallCurrentStoreExtensionRequested;
    public event EventHandler<string>? RunExtensionRequested;
    public event EventHandler<string>? StopExtensionRequested;
    public event EventHandler<string>? DeleteExtensionRequested;
    public event EventHandler? OpenLinkInNewTabRequested;
    public event EventHandler? OpenInNewWindowRequested;
    public event EventHandler? NewIncognitoWindowRequested;
    public event EventHandler? OpenOnPinnedWindowRequested;
    public event EventHandler? MuteTabRequested;
    public event EventHandler? InspectRequested;
    public event EventHandler? PageViewRequested;
    public event EventHandler? CreateQrRequested;
    public event EventHandler? AiFunctionRequested;
    public event EventHandler? SearchWithRequested;
    public event EventHandler? LockDomainRequested;
    public event EventHandler<NexZen.Core.NexShare.Models.DiscoveryMessage>? ShareDeviceSelected;
    public event EventHandler? OpenOnAnotherDeviceRequested;
    public event EventHandler<DataClearRequest>? ClearDataRequested;
    public event EventHandler? LayoutChanged;

    private CancellationTokenSource? _longPressCts;
    private CancellationTokenSource? _searchCts;
    private bool _historyExpanded;
    private bool _imageSettingsExpanded;
    private bool _toolsExpanded;
    private bool _imageActionsAvailable;
    private bool _searchActionAvailable;
    private bool _passwordRevealAuthInProgress;
    private bool _pinDataDeletionComplete;
    private bool _wasLongPress = false;
    private MainViewModel? _engineSource;

    public SuperMenuControl()
    {
        InitializeComponent();
        AddHandler(InputElement.KeyDownEvent, OnSuggestionNavigationKeyDown, RoutingStrategies.Tunnel);

        var dataScope = this.FindControl<ComboBox>("DataScope");
        if (dataScope != null)
            dataScope.SelectionChanged += DataScope_Changed;

        var backBtn = this.FindControl<Button>("BackButton");
        if (backBtn != null)
        {
            backBtn.AddHandler(InputElement.PointerPressedEvent, BackButton_PointerPressed, RoutingStrategies.Tunnel);
            backBtn.AddHandler(InputElement.PointerReleasedEvent, BackButton_PointerReleased, RoutingStrategies.Tunnel);
        }

        var shortcut = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
        SetShortcut("CutShortcut", shortcut + "X");
        SetShortcut("CopyShortcut", shortcut + "C");
        SetShortcut("PasteShortcut", shortcut + "V");
        DataContextChanged += (_, _) => AttachEngineLabel();
        AttachedToVisualTree += (_, _) => UpdateCreatePinVisibility();
    }

    private void AttachEngineLabel()
    {
        if (_engineSource != null)
            _engineSource.PropertyChanged -= OnEnginePropertyChanged;
        _engineSource = DataContext as MainViewModel;
        if (_engineSource != null)
            _engineSource.PropertyChanged += OnEnginePropertyChanged;
        UpdateSearchLabel();
    }

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MainViewModel.SelectedSearchEngine) or nameof(MainViewModel.SearchEngineLabel))
            UpdateSearchLabel();
    }

    private void UpdateSearchLabel()
    {
        var label = this.FindControl<TextBlock>("SearchWithLabel");
        if (label == null)
            return;
        label.Text = "Search with " + SearchTargets.DisplayName(_engineSource?.SelectedSearchEngine);
    }

    private void UpdateCreatePinVisibility()
    {
        var vault = App.ServiceProvider?.GetService<PasswordVaultMasterPasswordStore>();
        var isConfigured = vault?.IsConfigured == true;
        var createButton = this.FindControl<Button>("CreateNexZenPinButton");
        var resetButton = this.FindControl<Button>("ResetNexZenPinButton");
        if (createButton != null) createButton.IsVisible = !isConfigured;
        if (resetButton != null) resetButton.IsVisible = isConfigured;
    }

    private async void CreateNexZenPin_Click(object? sender, RoutedEventArgs e)
    {
        var button = this.FindControl<Button>("CreateNexZenPinButton");
        var owner = TopLevel.GetTopLevel(this) as Window;
        var auth = App.ServiceProvider?.GetService<PasswordVaultAuthenticationService>();
        if (button == null || owner?.IsVisible != true || auth == null)
            return;

        button.IsEnabled = false;
        try
        {
            if (await auth.CreatePinAsync(owner))
                UpdateCreatePinVisibility();
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void ResetNexZenPin_Click(object? sender, RoutedEventArgs e)
    {
        _pinDataDeletionComplete = false;
        var pin = this.FindControl<TextBox>("ResetNewPinInput");
        var recovery = this.FindControl<TextBox>("ResetRecoveryKeyInput");
        var status = this.FindControl<TextBlock>("ResetPinStatus");
        this.FindControl<StackPanel>("ResetPinEntryForm")!.IsVisible = true;
        this.FindControl<StackPanel>("DeletePinConfirmPanel")!.IsVisible = false;
        this.FindControl<Button>("ConfirmDeletePinButton")!.Content = "Delete everything";
        this.FindControl<Button>("CancelDeletePinButton")!.IsVisible = true;
        var deleteStatus = this.FindControl<TextBlock>("DeletePinStatus");
        if (deleteStatus != null) deleteStatus.Text = string.Empty;
        if (pin != null) pin.Text = string.Empty;
        if (recovery != null) recovery.Text = string.Empty;
        if (status != null) status.Text = string.Empty;
        ShowOnly("ResetPinPanel");
        pin?.Focus();
    }

    private void CloseResetPin_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly(null);
    }

    private void DeletePin_Click(object? sender, RoutedEventArgs e)
    {
        _pinDataDeletionComplete = false;
        this.FindControl<TextBlock>("DeletePinStatus")!.Text = string.Empty;
        this.FindControl<StackPanel>("ResetPinEntryForm")!.IsVisible = false;
        this.FindControl<StackPanel>("DeletePinConfirmPanel")!.IsVisible = true;
    }

    private void CancelDeletePin_Click(object? sender, RoutedEventArgs e)
    {
        this.FindControl<StackPanel>("DeletePinConfirmPanel")!.IsVisible = false;
        this.FindControl<StackPanel>("ResetPinEntryForm")!.IsVisible = true;
    }

    private async void ConfirmDeletePin_Click(object? sender, RoutedEventArgs e)
    {
        var button = this.FindControl<Button>("ConfirmDeletePinButton");
        var cancelButton = this.FindControl<Button>("CancelDeletePinButton");
        var status = this.FindControl<TextBlock>("DeletePinStatus");
        if (button == null || cancelButton == null || status == null)
            return;

        if (_pinDataDeletionComplete)
        {
            ShowOnly(null);
            return;
        }

        if (DataContext is not MainViewModel viewModel
            || App.ServiceProvider?.GetService<PasswordVaultAuthenticationService>() is not { } auth
            || App.ServiceProvider?.GetService<NexZenDbContext>() is not { } db)
        {
            status.Text = "NexZen could not access the password vault or database.";
            status.Foreground = Avalonia.Media.Brushes.Firebrick;
            return;
        }

        button.IsEnabled = false;
        cancelButton.IsEnabled = false;
        status.Text = "Deleting PIN and stored data…";
        status.Foreground = Avalonia.Media.Brushes.Gray;
        try
        {
            viewModel.CancelActiveDownloadsForDataDeletion();
            await using (db)
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                await db.SavedPasswords.ExecuteDeleteAsync();
                await db.Bookmarks.ExecuteDeleteAsync();
                await db.History.ExecuteDeleteAsync();
                await db.DownloadRecords.ExecuteDeleteAsync();
                auth.DeletePinAndRecoveryKey();
                App.ServiceProvider?.GetService<NexZen.Core.AI.IAiSecretStore>()?.DeleteKey();
                App.ServiceProvider?.GetService<DomainLockStore>()?.ClearAll();
                await transaction.CommitAsync();
            }

            viewModel.ClearAllStoredDataFromMemory();
            HideRevealedPasswords();
            UpdateCreatePinVisibility();
            status.Foreground = Avalonia.Media.Brushes.ForestGreen;
            status.Text = "PIN, recovery key, and database data have been deleted.";
            _pinDataDeletionComplete = true;
            button.Content = "Done";
            cancelButton.IsVisible = false;
            Program.StartupLog.Write("[PasswordVaultPIN] vault and browser database cleared after user confirmation");
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write($"[PasswordVaultPIN] delete failed: {ex.GetType().Name}");
            status.Foreground = Avalonia.Media.Brushes.Firebrick;
            status.Text = "Could not delete all data. Please try again.";
        }
        finally
        {
            button.IsEnabled = true;
            cancelButton.IsEnabled = true;
        }
    }

    private async void SubmitResetNexZenPin_Click(object? sender, RoutedEventArgs e)
    {
        var pinInput = this.FindControl<TextBox>("ResetNewPinInput");
        var recoveryInput = this.FindControl<TextBox>("ResetRecoveryKeyInput");
        var status = this.FindControl<TextBlock>("ResetPinStatus");
        var resetButton = sender as Button;
        if (pinInput == null || recoveryInput == null || status == null || resetButton == null)
            return;

        var newPin = pinInput.Text ?? string.Empty;
        if (newPin.Length != 6 || newPin.Any(character => character is < '0' or > '9'))
        {
            status.Foreground = Avalonia.Media.Brushes.Firebrick;
            status.Text = "Enter exactly six digits for the new PIN.";
            pinInput.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(recoveryInput.Text))
        {
            status.Foreground = Avalonia.Media.Brushes.Firebrick;
            status.Text = "Enter your recovery key.";
            recoveryInput.Focus();
            return;
        }

        var auth = App.ServiceProvider?.GetService<PasswordVaultAuthenticationService>();
        if (auth == null)
        {
            status.Foreground = Avalonia.Media.Brushes.Firebrick;
            status.Text = "Password vault is unavailable.";
            return;
        }

        resetButton.IsEnabled = false;
        status.Text = "Resetting PIN…";
        status.Foreground = Avalonia.Media.Brushes.Gray;
        try
        {
            if (await auth.ResetPinWithRecoveryKeyAsync(newPin, recoveryInput.Text.Trim()))
            {
                HideRevealedPasswords();
                pinInput.Text = string.Empty;
                recoveryInput.Text = string.Empty;
                status.Foreground = Avalonia.Media.Brushes.ForestGreen;
                status.Text = "PIN reset successfully.";
            }
            else
            {
                status.Foreground = Avalonia.Media.Brushes.Firebrick;
                status.Text = "Recovery key is incorrect. PIN was not changed.";
            }
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write($"[PasswordVaultPIN] reset failed: {ex.GetType().Name}");
            status.Foreground = Avalonia.Media.Brushes.Firebrick;
            status.Text = "Could not reset the PIN. Check the recovery key and try again.";
        }
        finally
        {
            resetButton.IsEnabled = true;
        }
    }

    public void SetSearchAvailable(bool visible)
    {
        UpdateSearchLabel();
        _searchActionAvailable = visible;
        UpdateImageSettingVisibility();
    }

    private void SetShortcut(string name, string text)
    {
        var label = this.FindControl<TextBlock>(name);
        if (label != null)
            label.Text = text;
    }

    public void SetSaveImageVisible(bool visible)
    {
        _imageActionsAvailable = visible;
        UpdateImageSettingVisibility();
    }

    private void UpdateImageSettingVisibility()
    {
        var parent = this.FindControl<Button>("ImageSettingButton");
        var separator = this.FindControl<Border>("ImageSettingSeparator");
        var afterSeparator = this.FindControl<Border>("AfterImageSettingSeparator");
        var isVisible = _imageActionsAvailable || _searchActionAvailable;

        if (parent != null)
            parent.IsVisible = isVisible;
        if (separator != null)
            separator.IsVisible = isVisible;
        if (afterSeparator != null)
            afterSeparator.IsVisible = isVisible;
        if (!isVisible)
            _imageSettingsExpanded = false;

        var copyAddress = this.FindControl<Button>("CopyImageAddressButton");
        if (copyAddress != null)
            copyAddress.IsVisible = isVisible && _imageSettingsExpanded && _imageActionsAvailable;
        var saveImage = this.FindControl<Button>("SaveImageButton");
        if (saveImage != null)
            saveImage.IsVisible = isVisible && _imageSettingsExpanded && _imageActionsAvailable;
        var search = this.FindControl<Button>("SearchWithButton");
        if (search != null)
            search.IsVisible = isVisible && _imageSettingsExpanded && _searchActionAvailable;

        var chevron = this.FindControl<PathIcon>("ImageSettingChevron");
        if (chevron != null)
            chevron.Data = StreamGeometry.Parse(_imageSettingsExpanded ? "M7,14L12,9L17,14Z" : "M7,10L12,15L17,10Z");

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ImageSetting_Click(object? sender, RoutedEventArgs e)
    {
        _imageSettingsExpanded = !_imageSettingsExpanded;
        UpdateImageSettingVisibility();
    }

    public void CollapseImageSettings()
    {
        if (!_imageSettingsExpanded)
            return;
        _imageSettingsExpanded = false;
        UpdateImageSettingVisibility();
    }

    private void Tools_Click(object? sender, RoutedEventArgs e)
    {
        _toolsExpanded = !_toolsExpanded;
        UpdateToolsVisibility();
    }

    private void UpdateToolsVisibility()
    {
        var dataManager = this.FindControl<Button>("DataManagerButton");
        if (dataManager != null)
            dataManager.IsVisible = _toolsExpanded;
        var responsiveTools = this.FindControl<Button>("ResponsiveToolsButton");
        if (responsiveTools != null)
            responsiveTools.IsVisible = _toolsExpanded;
        var createQr = this.FindControl<Button>("CreateQrButton");
        if (createQr != null)
            createQr.IsVisible = _toolsExpanded;
        var chevron = this.FindControl<PathIcon>("ToolsChevron");
        if (chevron != null)
            chevron.Data = StreamGeometry.Parse(_toolsExpanded ? "M7,14L12,9L17,14Z" : "M7,10L12,15L17,10Z");
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CollapseTools()
    {
        if (!_toolsExpanded)
            return;
        _toolsExpanded = false;
        UpdateToolsVisibility();
    }

    public void SetOpenInNewWindowVisible(bool visible)
    {
        var button = this.FindControl<Button>("OpenInNewWindowButton");
        if (button != null)
            button.IsVisible = visible;
    }

    public void SetOpenOnPinnedWindowVisible(bool visible)
    {
        var button = this.FindControl<Button>("OpenOnPinnedWindowButton");
        if (button != null)
            button.IsVisible = visible;
    }

    public void SetMuteTabLabel(bool muted)
    {
        var label = this.FindControl<TextBlock>("MuteTabLabel");
        if (label != null)
            label.Text = muted ? "Unmute this tab" : "Mute this tab";
    }

    public void SetTabCount(int count)
    {
        var label = this.FindControl<TextBlock>("TabCountLabel");
        if (label != null)
            label.Text = Math.Max(0, count).ToString();
    }

    private void NewTabButton_Click(object? sender, RoutedEventArgs e)
    {
        NewTabRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TabManagerButton_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly("TabListPanel");
    }

    private void TabRows_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string rows && DataContext is MainViewModel viewModel)
            viewModel.SetTabColumns(rows);
    }

    private void CloseTabList_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly(null);
    }

    private void TabItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tabId)
            SwitchTabRequested?.Invoke(this, tabId);
    }

    private void CloseTabItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tabId)
            CloseTabRequested?.Invoke(this, tabId);
    }

    private void OpenTabInNewWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tabId)
            OpenTabInNewWindowRequested?.Invoke(this, tabId);
        e.Handled = true;
    }

    private void CloseActiveTab_Click(object? sender, RoutedEventArgs e)
    {
        CloseActiveTabRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ReopenClosedTab_Click(object? sender, RoutedEventArgs e)
    {
        ReopenClosedTabRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CopyButton_Click(object? sender, RoutedEventArgs e)
    {
        CopyRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CutButton_Click(object? sender, RoutedEventArgs e)
    {
        CutRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PasteButton_Click(object? sender, RoutedEventArgs e)
    {
        PasteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SaveImageButton_Click(object? sender, RoutedEventArgs e)
    {
        SaveImageRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CopyImageAddressButton_Click(object? sender, RoutedEventArgs e)
    {
        CopyImageAddressRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenLinkInNewTab_Click(object? sender, RoutedEventArgs e)
    {
        OpenLinkInNewTabRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenInNewWindow_Click(object? sender, RoutedEventArgs e)
    {
        OpenInNewWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void NewIncognitoWindow_Click(object? sender, RoutedEventArgs e)
    {
        NewIncognitoWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenOnPinnedWindow_Click(object? sender, RoutedEventArgs e)
    {
        OpenOnPinnedWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MuteTab_Click(object? sender, RoutedEventArgs e)
    {
        MuteTabRequested?.Invoke(this, EventArgs.Empty);
    }

    private void InspectButton_Click(object? sender, RoutedEventArgs e)
    {
        InspectRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AiFunction_Click(object? sender, RoutedEventArgs e)
    {
        AiFunctionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CreateQrButton_Click(object? sender, RoutedEventArgs e)
    {
        CreateQrRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PageViewButton_Click(object? sender, RoutedEventArgs e)
    {
        PageViewRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SearchWithButton_Click(object? sender, RoutedEventArgs e)
    {
        SearchWithRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BackButton_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _wasLongPress = false;
        _longPressCts?.Cancel();
        _longPressCts = new CancellationTokenSource();
        
        var token = _longPressCts.Token;
        Task.Delay(500, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                _wasLongPress = true;
                Dispatcher.UIThread.Post(() =>
                {
                    BackHistoryRequested?.Invoke(this, EventArgs.Empty);
                });
            }
        });
    }

    private void BackButton_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _longPressCts?.Cancel();
    }

    private void BackButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_wasLongPress)
        {
            // Do not trigger normal back if we just long-pressed
            _wasLongPress = false;
            return;
        }
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void LockDomain_Click(object? sender, RoutedEventArgs e) =>
        LockDomainRequested?.Invoke(this, EventArgs.Empty);

    public void SetDomainLockedState(bool isLocked)
    {
        var button = this.FindControl<Button>("LockDomainButton");
        if (button == null)
            return;
        ToolTip.SetTip(button, isLocked ? "Unlock this domain" : "Lock this domain with your NexZen PIN");
        if (button.Content is PathIcon icon)
            icon.Foreground = isLocked ? Avalonia.Media.Brushes.Firebrick : Avalonia.Media.Brushes.ForestGreen;
    }

    private void BackButton_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        BackHistoryRequested?.Invoke(this, EventArgs.Empty);
    }
    
    public void ShowHistoryList(IEnumerable<string> history)
    {
        var mainList = this.FindControl<StackPanel>("MainMenuList");
        var historyList = this.FindControl<StackPanel>("HistoryListPanel");
        var historyItemsControl = this.FindControl<ItemsControl>("HistoryItemsControl");
        
        if (mainList != null && historyList != null && historyItemsControl != null)
        {
            historyItemsControl.ItemsSource = history;
            ShowOnly("HistoryListPanel");
        }
    }

    private void CloseHistoryList_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly(null);
    }
    
    private void HistoryItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string url)
        {
            HistoryItemClicked?.Invoke(this, url);
        }
    }

    private void ShowExtensionList_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly("ExtensionPanel");
    }

    private void CloseExtensionList_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly(null);
    }

    private void InstallExtension_Click(object? sender, RoutedEventArgs e)
    {
        InstallExtensionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenExtensionStore_Click(object? sender, RoutedEventArgs e)
    {
        NavigateRequested?.Invoke(this, "https://chromewebstore.google.com/category/extensions");
    }

    private void GpuInfoButton_Click(object? sender, RoutedEventArgs e)
    {
        NavigateRequested?.Invoke(this, "chrome://gpu");
        ResetToMainMenu();
    }

    private void InstallCurrentStoreExtension_Click(object? sender, RoutedEventArgs e)
    {
        InstallCurrentStoreExtensionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RunExtension_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
            RunExtensionRequested?.Invoke(this, id);
    }

    private void StopExtension_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
            StopExtensionRequested?.Invoke(this, id);
    }

    private void ExtensionToolbarButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
            RunExtensionRequested?.Invoke(this, id);
    }

    private void DeleteExtension_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
            DeleteExtensionRequested?.Invoke(this, id);
    }

    private void ShowVisitHistory_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly("VisitHistoryPanel");
    }

    private void CloseVisitHistory_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly(null);
    }

    private void VisitHistoryItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url && !string.IsNullOrWhiteSpace(url))
            NavigateRequested?.Invoke(this, url);
    }

    private void DeleteVisit_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int id && DataContext is MainViewModel vm)
            vm.DeleteVisitCommand.Execute(id);
    }

    private void ShowBookmarkList_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly("BookmarkListPanel");
    }

    private void CloseBookmarkList_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly(null);
    }

    private void ShowDownloadList_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly("DownloadListPanel");
    }

    private void CloseDownloadList_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly(null);
    }

    private void ShowSavedPasswords_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly("SavedPasswordPanel");
    }

    private void CloseSavedPasswords_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly(null);
    }

    private async void ToggleSavedPassword_Click(object? sender, RoutedEventArgs e)
    {
        if (_passwordRevealAuthInProgress || sender is not Button button || button.Tag is not int id || DataContext is not MainViewModel vm)
            return;

        var item = vm.SavedPasswords.FirstOrDefault(password => password.Id == id);
        if (item == null)
            return;

        if (!item.IsRevealed)
        {
            var auth = App.ServiceProvider?.GetRequiredService<PasswordVaultAuthenticationService>();
            if (auth == null)
            {
                Program.StartupLog.Write("[PasswordVaultAuth] reveal aborted because authentication service is unavailable");
                return;
            }

            _passwordRevealAuthInProgress = true;
            try
            {
                if (!await auth.AuthorizeRevealAsync(() => TopLevel.GetTopLevel(this) as Window))
                {
                    Program.StartupLog.Write($"[PasswordVaultAuth] reveal authorization denied id={id}");
                    return;
                }
            }
            catch (Exception ex)
            {
                Program.StartupLog.Write($"[PasswordVaultAuth] reveal authorization failed: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            finally
            {
                _passwordRevealAuthInProgress = false;
            }
        }

        try
        {
            Program.StartupLog.Write($"[PasswordVaultAuth] decrypt requested id={id}");
            await vm.ToggleSavedPasswordVisibilityAsync(id);
            var updatedItem = vm.SavedPasswords.FirstOrDefault(password => password.Id == id);
            Program.StartupLog.Write(updatedItem == null
                ? $"[PasswordVaultAuth] reveal item disappeared id={id}"
                : $"[PasswordVaultAuth] decrypt completed id={id} revealed={updatedItem.IsRevealed} displayStatus={GetPasswordDisplayStatus(updatedItem.PasswordDisplay)}");
        }
        catch (Exception ex)
        {
            Program.StartupLog.Write($"[PasswordVaultAuth] password reveal failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string GetPasswordDisplayStatus(string display) => display switch
    {
        "Unable to decrypt" => "decrypt-failed",
        "System keyring unavailable" => "key-unavailable",
        "••••••••" => "still-hidden",
        _ => "revealed"
    };

    private async void DeleteSavedPassword_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int id && DataContext is MainViewModel vm)
            await vm.DeleteSavedPasswordAsync(id);
    }

    public void ResetToMainMenu()
    {
        HideRevealedPasswords();
        CollapseImageSettings();
        CollapseTools();
        _historyExpanded = false;
        var more = this.FindControl<Button>("HistoryMoreButton");
        if (more != null)
            more.Content = "Show more";
        ShowOnly(null);
    }

    public void HideRevealedPasswords()
    {
        if (DataContext is MainViewModel vm)
            vm.HideRevealedPasswords();
    }

    private void ShowMoreHistory_Click(object? sender, RoutedEventArgs e)
    {
        _historyExpanded = !_historyExpanded;
        if (sender is Button button)
            button.Content = _historyExpanded ? "Show less" : "Show more";
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowOnly(string? panelName)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsSearchSuggestionVisible = false;
        }

        var mainList = this.FindControl<StackPanel>("MainMenuList");
        if (mainList != null)
            mainList.IsVisible = panelName == null;

        foreach (var name in new[] { "HistoryListPanel", "BookmarkListPanel", "DownloadListPanel", "SavedPasswordPanel", "ResetPinPanel", "TabListPanel", "VisitHistoryPanel", "ExtensionPanel", "DeviceListPanel", "DataManagerPanel" })
        {
            var panel = this.FindControl<StackPanel>(name);
            if (panel != null)
                panel.IsVisible = name == panelName;
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public Size FitToViewport(double availableWidth, double availableHeight)
    {
        availableWidth = Math.Max(1, availableWidth);
        availableHeight = Math.Max(1, availableHeight);

        // DesiredSize includes Margin. The previous open's offset would be
        // added again and flip the menu between two positions.
        Margin = new Thickness(0);
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;

        double panelWidth = Math.Min(300, availableWidth);
        SetPanelWidth("HistoryListPanel", panelWidth);
        SetPanelWidth("BookmarkListPanel", panelWidth);
        SetPanelWidth("DownloadListPanel", panelWidth);
        SetPanelWidth("SavedPasswordPanel", Math.Min(360, availableWidth));
        SetPanelWidth("ResetPinPanel", Math.Min(340, availableWidth));
        SetPanelWidth("VisitHistoryPanel", Math.Min(340, availableWidth));
        SetPanelWidth("ExtensionPanel", Math.Min(340, availableWidth));
        SetPanelWidth("DataManagerPanel", Math.Min(380, availableWidth));

        var tabPanel = this.FindControl<StackPanel>("TabListPanel");
        var tabItems = this.FindControl<ItemsControl>("TabItems");
        double tabWidth = panelWidth;
        int rows = 1;
        if (DataContext is MainViewModel viewModel && viewModel.TabColumns is >= 1 and <= 3)
            rows = viewModel.TabColumns;
        if (tabPanel != null)
        {
            int tabCount = DataContext is MainViewModel vm ? Math.Max(1, vm.Tabs.Count) : 1;
            const double cardStride = 198;
            const double rowStride = 266;
            int columns = (int)Math.Ceiling(tabCount / (double)rows);
            double strip = columns * cardStride + 16;
            tabWidth = tabPanel.IsVisible ? Math.Min(availableWidth, Math.Max(panelWidth, strip)) : panelWidth;
            tabPanel.Width = tabWidth;
            if (tabItems != null)
                tabItems.Width = columns * cardStride;
            var tabStrip = this.FindControl<ScrollViewer>("TabScroll");
            if (tabStrip != null)
                tabStrip.Height = rows * rowStride + 24;
        }
        UpdateRowButtons(rows);

        var addressBar = this.FindControl<Border>("AddressBarBorder");
        if (addressBar != null)
        {
            double bar = Math.Min(500, availableWidth);
            if (tabPanel?.IsVisible == true)
                bar = Math.Max(bar, tabWidth);
            addressBar.Width = Math.Min(availableWidth, bar);
        }

        var historyScroll = this.FindControl<ScrollViewer>("HistoryScroll");
        var bookmarkScroll = this.FindControl<ScrollViewer>("BookmarkScroll");
        var downloadScroll = this.FindControl<ScrollViewer>("DownloadScroll");
        var visitList = this.FindControl<ListBox>("VisitHistoryList");
        var extensionScroll = this.FindControl<ScrollViewer>("ExtensionScroll");
        var dataScroll = this.FindControl<ScrollViewer>("DataManagerScroll");
        var tabScroll = this.FindControl<ScrollViewer>("TabScroll");
        if (historyScroll != null) historyScroll.MaxHeight = 300;
        if (bookmarkScroll != null) bookmarkScroll.MaxHeight = 260;
        if (downloadScroll != null) downloadScroll.MaxHeight = 400;
        if (visitList != null)
            visitList.MaxHeight = _historyExpanded ? availableHeight : 360;
        if (extensionScroll != null) extensionScroll.MaxHeight = 320;
        if (dataScroll != null) dataScroll.MaxHeight = 480;
        if (tabScroll != null) tabScroll.MaxHeight = double.PositiveInfinity;

        var measureSize = new Size(availableWidth, double.PositiveInfinity);
        Measure(measureSize);

        var activeScroll = VisibleScroll(historyScroll) ?? VisibleScroll(bookmarkScroll) ?? VisibleScroll(downloadScroll) ?? VisibleScroll(visitList) ?? VisibleScroll(extensionScroll) ?? VisibleScroll(dataScroll);
        if (activeScroll != null && DesiredSize.Height > availableHeight)
        {
            double overflow = DesiredSize.Height - availableHeight;
            activeScroll.MaxHeight = Math.Max(80, activeScroll.DesiredSize.Height - overflow);
            Measure(measureSize);
        }

        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        return DesiredSize;
    }

    private void UpdateRowButtons(int rows)
    {
        SetRowButton("Row1Button", rows == 1);
        SetRowButton("Row2Button", rows == 2);
        SetRowButton("Row3Button", rows == 3);
    }

    private void SetRowButton(string name, bool selected)
    {
        var button = this.FindControl<Button>(name);
        if (button == null)
            return;
        button.Background = Avalonia.Media.SolidColorBrush.Parse(selected ? "#E8F5E9" : "#F5F5F5");
        button.BorderBrush = Avalonia.Media.SolidColorBrush.Parse(selected ? "#4CAF50" : "#E0E0E0");
        button.BorderThickness = new Thickness(1);
        button.Foreground = Avalonia.Media.SolidColorBrush.Parse("#333333");
    }

    private void SetPanelWidth(string name, double width)
    {
        var panel = this.FindControl<StackPanel>(name);
        if (panel != null)
            panel.Width = width;
    }

    private static Control? VisibleScroll(Control? scroll)
    {
        if (scroll?.Parent is Control parent && parent.IsVisible)
            return scroll;
        return null;
    }

    private void DownloadUrl_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is TextBlock tb && tb.DataContext is NexZen.Application.ViewModels.DownloadItemViewModel dl)
        {
            NavigateRequested?.Invoke(this, dl.Url);
            e.Handled = true;
        }
    }

    private void BookmarkItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url)
        {
            NavigateRequested?.Invoke(this, url);
        }
    }

    private void DeleteBookmark_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is int id)
        {
            if (DataContext is NexZen.Application.ViewModels.MainViewModel vm)
            {
                vm.DeleteBookmarkCommand.Execute(id);
            }
        }
    }

    private void ForwardButton_Click(object? sender, RoutedEventArgs e)
    {
        ForwardRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void AddressBar_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var textBox = sender as TextBox;
            if (textBox != null && !string.IsNullOrWhiteSpace(textBox.Text))
            {
                string searchEngine = "Google";
                if (DataContext is NexZen.Application.ViewModels.MainViewModel vm)
                    searchEngine = vm.SelectedSearchEngine;

                NavigateRequested?.Invoke(this, SearchTargets.FromAddressBar(searchEngine, textBox.Text));
            }
        }
    }

    private void OnSuggestionNavigationKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || !vm.IsSearchSuggestionVisible)
            return;

        int count = vm.SearchSuggestions.Count;
        if (count == 0)
            return;

        if (e.Key is Key.Down or Key.Up)
        {
            if (_selectedSuggestionIndex < 0)
                _selectedSuggestionIndex = e.Key == Key.Down ? 0 : count - 1;
            else
                _selectedSuggestionIndex = (_selectedSuggestionIndex + (e.Key == Key.Down ? 1 : count - 1)) % count;

            UpdateSuggestionSelectionVisuals();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && _selectedSuggestionIndex >= 0 && _selectedSuggestionIndex < count)
        {
            NavigateToSuggestion(vm.SearchSuggestions[_selectedSuggestionIndex]);
            e.Handled = true;
        }
    }

    private void UpdateSuggestionSelectionVisuals()
    {
        var buttons = this.FindControl<StackPanel>("SearchSuggestionPanel")?
            .GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Tag is string)
            .ToList();
        if (buttons == null)
            return;

        for (int i = 0; i < buttons.Count; i++)
        {
            if (i == _selectedSuggestionIndex)
                buttons[i].Classes.Add("suggestion-selected");
            else
                buttons[i].Classes.Remove("suggestion-selected");
        }
    }

    private void NavigateToSuggestion(string suggestion)
    {
        if (DataContext is not MainViewModel vm)
            return;

        vm.IsSearchSuggestionVisible = false;
        NavigateRequested?.Invoke(this, SearchTargets.FromAddressBar(vm.SelectedSearchEngine, suggestion));
        ResetToMainMenu();
    }

    private async void AddressBar_KeyUp(object? sender, KeyEventArgs e)
    {
        // Arrow keys move through the current suggestion list. Do not treat their
        // key-up events as edits or the selected item would be cleared each time.
        if (e.Key is Key.Enter or Key.Escape or Key.Up or Key.Down) return;

        var textBox = sender as TextBox;
        if (textBox != null && DataContext is MainViewModel vm)
        {
            _selectedSuggestionIndex = -1;
            UpdateSuggestionSelectionVisuals();
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            
            string query = textBox.Text ?? "";
            
            if (string.IsNullOrWhiteSpace(query))
            {
                vm.IsSearchSuggestionVisible = false;
                _selectedSuggestionIndex = -1;
                var mainList = this.FindControl<StackPanel>("MainMenuList");
                if (mainList != null) mainList.IsVisible = true;
                return;
            }

            try
            {
                await Task.Delay(300, _searchCts.Token);
                await vm.FetchSearchSuggestionsAsync(query, _searchCts.Token);
                if (vm.IsSearchSuggestionVisible)
                {
                    _selectedSuggestionIndex = -1;
                    // Hide main menu
                    var mainList = this.FindControl<StackPanel>("MainMenuList");
                    if (mainList != null) mainList.IsVisible = false;
                    
                    LayoutChanged?.Invoke(this, EventArgs.Empty);
                    Dispatcher.UIThread.Post(UpdateSuggestionSelectionVisuals, DispatcherPriority.Background);
                }
            }
            catch (TaskCanceledException) { }
        }
    }

    private void SuggestionItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string suggestion)
            NavigateToSuggestion(suggestion);
    }

    private void CloseSearchSuggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsSearchSuggestionVisible = false;
            _selectedSuggestionIndex = -1;
        }
        ShowOnly(null);
    }

    public void FocusAddressBar()
    {
        var textBox = this.FindControl<TextBox>("UrlTextBox");
        if (textBox != null)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void ShowDeviceList_Click(object? sender, RoutedEventArgs e)
    {
        OpenOnAnotherDeviceRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowDeviceList()
    {
        ShowOnly("DeviceListPanel");
    }

    private void CloseDeviceList_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly(null);
    }

    private void DeviceItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            var device = btn.Tag switch
            {
                NexZen.Application.ViewModels.DiscoveredDeviceItem item => item.Device,
                NexZen.Core.NexShare.Models.DiscoveryMessage message => message,
                _ => null
            };
            if (device == null)
                return;
            ShareDeviceSelected?.Invoke(this, device);
            ResetToMainMenu();
            if (Parent is Panel p)
            {
                p.Children.Remove(this);
            }
        }
    }

    private bool _dataBusy;

    private void ShowDataManager_Click(object? sender, RoutedEventArgs e)
    {
        PrefillDataDomain();
        ShowOnly("DataManagerPanel");
    }

    private void CloseDataManager_Click(object? sender, RoutedEventArgs e)
    {
        ShowOnly(null);
    }

    private void DataScope_Changed(object? sender, SelectionChangedEventArgs e)
    {
        TextBox? box;
        ComboBox? scope;
        try
        {
            box = this.FindControl<TextBox>("DataDomainBox");
            scope = this.FindControl<ComboBox>("DataScope");
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (box == null)
            return;
        var oneSite = scope?.SelectedIndex == 1;
        box.IsVisible = oneSite;
        if (oneSite)
            PrefillDataDomain();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PrefillDataDomain()
    {
        var box = this.FindControl<TextBox>("DataDomainBox");
        if (box == null || !string.IsNullOrWhiteSpace(box.Text))
            return;
        if (DataContext is MainViewModel vm)
        {
            var host = DataMatch.NormalizeHost(vm.CurrentUrl);
            if (!string.IsNullOrEmpty(host))
                box.Text = host;
        }
    }

    private void DeleteData_Click(object? sender, RoutedEventArgs e)
    {
        if (_dataBusy)
            return;

        var history = IsDataChecked("DataHistoryBox");
        var cookies = IsDataChecked("DataCookiesBox");
        var cache = IsDataChecked("DataCacheBox");
        var downloads = IsDataChecked("DataDownloadsBox");
        var autofill = IsDataChecked("DataAutofillBox");
        var settings = IsDataChecked("DataSettingsBox");
        var hosted = IsDataChecked("DataHostedBox");
        if (!history && !cookies && !cache && !downloads && !autofill && !settings && !hosted)
        {
            SetDataManagerStatus("Choose at least one kind of data.");
            return;
        }

        string? host = null;
        if (this.FindControl<ComboBox>("DataScope")?.SelectedIndex == 1)
        {
            host = DataMatch.NormalizeHost(this.FindControl<TextBox>("DataDomainBox")?.Text);
            if (host == null)
            {
                SetDataManagerStatus("Enter a domain, or choose All sites.");
                return;
            }
        }

        ClearDataRequested?.Invoke(this, new DataClearRequest
        {
            History = history,
            Cookies = cookies,
            Cache = cache,
            DownloadHistory = downloads,
            Autofill = autofill,
            SiteSettings = settings,
            HostedAppData = hosted,
            SinceUtc = SelectedSinceUtc(),
            Host = host
        });
    }

    public void SetDataManagerBusy(bool busy)
    {
        _dataBusy = busy;
        var button = this.FindControl<Button>("DataDeleteButton");
        if (button != null)
            button.IsEnabled = !busy;
    }

    public void SetDataManagerStatus(string text)
    {
        var status = this.FindControl<TextBlock>("DataStatusText");
        if (status != null)
            status.Text = text;
    }

    private bool IsDataChecked(string name)
    {
        return this.FindControl<CheckBox>(name)?.IsChecked == true;
    }

    private DateTime? SelectedSinceUtc()
    {
        var tag = (this.FindControl<ComboBox>("DataTimeRange")?.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            "15m" => DateTime.UtcNow.AddMinutes(-15),
            "60m" => DateTime.UtcNow.AddMinutes(-60),
            "24h" => DateTime.UtcNow.AddHours(-24),
            "7d" => DateTime.UtcNow.AddDays(-7),
            "all" => null,
            _ => DateTime.UtcNow.AddHours(-24)
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
