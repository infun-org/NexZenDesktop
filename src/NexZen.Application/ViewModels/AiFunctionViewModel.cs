using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NexZen.Application.Services;
using NexZen.Core.AI;
using NexZen.Core.Interfaces;

namespace NexZen.Application.ViewModels;

public partial class AiChatBubble : ObservableObject
{
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> _imagePaths = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<AiCitation> _citations = Array.Empty<AiCitation>();

    public bool IsMine { get; init; }
    public bool IsSystem { get; init; }
    public bool IsTerminal { get; init; }
    public bool HasCitations => Citations.Count > 0;
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasImage => ImagePaths.Count > 0;

    public string? ImagePath
    {
        get => ImagePaths.Count > 0 ? ImagePaths[0] : null;
        set
        {
            ImagePaths = string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : new[] { value };
        }
    }

    public string BubbleColor => IsMine ? "#C5F0C8" : (IsSystem && !IsTerminal ? "#fff3cd" : "#D8F1FF");
    public string TextColor => IsSystem && !IsTerminal ? "#856404" : "#222222";
    public string Alignment => IsMine ? "Right" : (IsSystem && !IsTerminal ? "Center" : "Left");

    partial void OnCitationsChanged(IReadOnlyList<AiCitation> value)
        => OnPropertyChanged(nameof(HasCitations));
    partial void OnTextChanged(string value)
        => OnPropertyChanged(nameof(HasText));
    partial void OnImagePathsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(ImagePath));
    }
}

public partial class AiFunctionViewModel : ObservableObject
{
    private readonly IAiChatClient _chatClient;
    private readonly IAiSecretStore _secrets;
    private readonly ISettingsService _settings;
    private readonly ILocalLlamaServer? _localLlama;
    private readonly ILocalImageGenerator? _imageGenerator;
    private readonly IWebLookup? _webLookup;
    private readonly IBrowserResearch? _research;
    private readonly LocalLlamaIdleWatch _idleWatch = new();
    private readonly Dictionary<string, TabAiSession> _sessions = new();
    private MainViewModel? _main;
    private TabItemViewModel? _hookedTab;
    private string _tabId = "";
    private CancellationTokenSource? _sendCts;
    private PendingPageSend? _pending;
    private PendingThinkingSend? _thinkingPending;
    private bool _loadingSettings;
    private bool _syncingLocalPicker;
    private AiChatBubble? _setupTerminal;

    public ObservableCollection<string> ProviderChoices { get; } = new()
    {
        AiProviderDefaults.SpaceXaiName,
        AiProviderDefaults.NewCustomName
    };

    public ObservableCollection<string> LanguageChoices { get; } = new()
    {
        "English", "Vietnamese", "Japanese", "Korean", "Chinese", "French", "German", "Spanish"
    };

    public const string ActionChat = "Chat";
    public const string ActionSummarizePage = "Summarize page";
    public const string ActionSummarizeThread = "Summarize thread";
    public const string ActionAskPage = "Ask this page";
    public const string ActionTranslate = "Translate";

    public ObservableCollection<string> ActionChoices { get; } = new()
    {
        ActionChat,
        ActionSummarizePage,
        ActionSummarizeThread,
        ActionAskPage,
        ActionTranslate
    };

    public ObservableCollection<AiChatBubble> Messages { get; } = new();
    public ObservableCollection<LocalLlamaModel> LocalModels { get; } = new();
    public ObservableCollection<string> PendingImagePaths { get; } = new();

    public Func<Task<bool>>? EnsureVaultAsync { get; set; }
    public Func<Task<string?>>? ExtractPageJsonAsync { get; set; }
    public Func<string, Task<string?>>? ExtractRemotePageJsonAsync { get; set; }
    public Func<string, string, Task<bool>>? SaveConversationFileAsync { get; set; }
    public Func<Task<IReadOnlyList<string>?>>? PickImageFilesAsync { get; set; }
    public Action? MessagesLayoutChanged { get; set; }

    public string ActiveTabId => _tabId;

    [ObservableProperty] private string _selectedProvider = AiProviderDefaults.SpaceXaiName;
    [ObservableProperty] private string _baseUrl = AiProviderDefaults.SpaceXaiBaseUrl;
    [ObservableProperty] private string _model = AiProviderDefaults.SpaceXaiModel;
    [ObservableProperty] private string _apiKeyInput = "";
    [ObservableProperty] private bool _hasSavedKey;
    [ObservableProperty] private bool _isCustomProvider;
    [ObservableProperty] private string _customName = "";
    [ObservableProperty] private bool _isNewCustom;
    [ObservableProperty] private bool _canDeleteProvider;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Save an API key, then test the connection.";
    [ObservableProperty] private string _messageInput = "";
    [ObservableProperty] private string _pageTitle = "";
    [ObservableProperty] private string _pageUrl = "";
    [ObservableProperty] private string _headerMeta = AiProviderDefaults.SpaceXaiName;
    [ObservableProperty] private string _outputLanguage = "English";
    [ObservableProperty] private string _selectedAction = ActionChat;
    [ObservableProperty] private int _chatFontSize = 14;
    [ObservableProperty] private bool _isConfirmOpen;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string _confirmMessage = "";
    [ObservableProperty] private string _confirmAcceptLabel = "Send";
    [ObservableProperty] private bool _confirmDontAskAgain;
    [ObservableProperty] private bool _isThinking;
    [ObservableProperty] private bool _showLocalModelPicker;
    [ObservableProperty] private LocalLlamaModel? _selectedLocalModel;

    public string ApiKeyPlaceholder => HasSavedKey ? "Saved — paste a key to replace" : "Paste API key";
    public bool CanPickLocalModel => ShowLocalModelPicker && !IsBusy && LocalModels.Count > 0;
    public bool IsBlankPage => IsNonExtractable(PageUrl);
    public bool CanUsePageActions => !IsBusy && !IsConfirmOpen && !IsBlankPage;
    public bool CanTranslate => CanUsePageActions;
    public bool CanSaveConversation => Messages.Count > 0;
    public bool CanSend
    {
        get
        {
            if (IsBusy || IsConfirmOpen)
                return false;
            return SelectedAction switch
            {
                ActionSummarizePage or ActionSummarizeThread or ActionTranslate => CanUsePageActions,
                ActionAskPage => CanUsePageActions && !string.IsNullOrWhiteSpace(MessageInput),
                _ => !string.IsNullOrWhiteSpace(MessageInput) || HasPendingImages
            };
        }
    }
    public bool ShowEmptyHint => Messages.Count == 0;
    public bool HasPendingImages => PendingImagePaths.Count > 0;

    public AiFunctionViewModel(
        IAiChatClient chatClient,
        IAiSecretStore secrets,
        ISettingsService settings,
        ILocalLlamaServer? localLlama = null,
        ILocalImageGenerator? imageGenerator = null,
        IWebLookup? webLookup = null,
        IBrowserResearch? research = null)
    {
        _chatClient = chatClient;
        _secrets = secrets;
        _settings = settings;
        _localLlama = localLlama;
        _imageGenerator = imageGenerator;
        _webLookup = webLookup;
        _research = research;
        Messages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowEmptyHint));
            OnPropertyChanged(nameof(CanSaveConversation));
        };
        PendingImagePaths.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPendingImages));
            OnPropertyChanged(nameof(CanSend));
        };
        LoadFromSettings();
        _ = SyncRunningLocalModelAsync();
    }

    public void Attach(MainViewModel main)
    {
        if (ReferenceEquals(_main, main))
            return;
        if (_main != null)
        {
            _main.PropertyChanged -= OnMainPropertyChanged;
            _main.TabClosed -= OnTabClosed;
        }
        _main = main;
        main.PropertyChanged += OnMainPropertyChanged;
        main.TabClosed += OnTabClosed;
        HookActiveTab();
    }

    public void DropTab(string tabId)
    {
        _sessions.Remove(tabId);
        if (_tabId == tabId)
        {
            Messages.Clear();
            _tabId = "";
            CloseConfirm();
        }
    }

    [RelayCommand]
    private void OpenSettings()
    {
        LoadFromSettings();
        ApiKeyInput = "";
        IsSettingsOpen = true;
        _ = SyncRunningLocalModelAsync();
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (!await EnsureVaultAsync.InvokeIfSet())
        {
            StatusText = "Unlock NexZen PIN to save the API key.";
            return;
        }

        try
        {
            if (!TryPersistProvider(out var error))
            {
                StatusText = error;
                return;
            }
            if (!string.IsNullOrWhiteSpace(ApiKeyInput) && !string.IsNullOrWhiteSpace(CurrentProfileId))
            {
                _secrets.SaveKeyFor(CurrentProfileId, ApiKeyInput.Trim());
                ApiKeyInput = "";
            }
            HasSavedKey = !string.IsNullOrWhiteSpace(CurrentProfileId) && _secrets.HasKeyFor(CurrentProfileId);
            RefreshHeader();
            StatusText = HasSavedKey ? "Provider saved." : "Provider saved. Add an API key to connect.";
            OnPropertyChanged(nameof(ApiKeyPlaceholder));
        }
        catch (InvalidOperationException)
        {
            StatusText = "Unlock NexZen PIN to save the API key.";
        }
        catch (Exception)
        {
            StatusText = "Could not save settings.";
        }
    }

    [RelayCommand]
    private async Task DeleteKeyAsync()
    {
        if (!await EnsureVaultAsync.InvokeIfSet())
            return;
        if (!string.IsNullOrWhiteSpace(CurrentProfileId))
            _secrets.DeleteKeyFor(CurrentProfileId);
        HasSavedKey = false;
        ApiKeyInput = "";
        OnPropertyChanged(nameof(ApiKeyPlaceholder));
        StatusText = "API key deleted.";
    }

    [RelayCommand]
    private async Task DeleteProviderAsync()
    {
        if (!CanDeleteProvider)
            return;
        if (!await EnsureVaultAsync.InvokeIfSet())
            return;
        var profile = AiProviderCatalog.FindByName(_settings.AiCustomProviders, SelectedProvider);
        if (profile == null)
            return;
        _secrets.DeleteKeyFor(profile.Id);
        _settings.AiCustomProviders.RemoveAll(p => p.Id == profile.Id);
        _settings.AiActiveProfileId = AiProviderDefaults.SpaceXaiId;
        _settings.Save();
        HasSavedKey = false;
        ApiKeyInput = "";
        RebuildProviderChoices(AiProviderDefaults.SpaceXaiName);
        ApplyProviderSelection(AiProviderDefaults.SpaceXaiName);
        StatusText = $"Removed {profile.Name}.";
        OnPropertyChanged(nameof(ApiKeyPlaceholder));
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsBusy)
            return;
        if (!await EnsureVaultAsync.InvokeIfSet())
        {
            StatusText = "Unlock NexZen PIN to test the connection.";
            return;
        }

        string? key;
        try { key = ResolveKey(); }
        catch (InvalidOperationException)
        {
            StatusText = "Unlock NexZen PIN to read the saved key.";
            return;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            StatusText = "Paste an API key first.";
            return;
        }

        if (IsImageModel(SelectedLocalModel))
        {
            StatusText = _imageGenerator is { IsReady: true }
                ? $"{SelectedLocalModel!.Label} is ready for text-to-image. Type a prompt in Chat, then Send."
                : $"{SelectedLocalModel!.Label} GGUF is installed, but the text-to-image runtime is not ready yet.";
            return;
        }

        IsBusy = true;
        _sendCts?.Cancel();
        _sendCts = new CancellationTokenSource();
        try
        {
            if (!await EnsureLocalLlamaRunningAsync(_sendCts.Token))
                return;
            StatusText = "Testing connection…";
            var result = await _chatClient.TestConnectionAsync(BuildEndpoint(), key);
            StatusText = result.Message;
        }
        catch (OperationCanceledException)
        {
            WriteTerminal("Stopped.");
        }
        catch (Exception)
        {
            StatusText = "Could not reach the provider.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (IsBusy || IsConfirmOpen)
            return;

        var text = MessageInput?.Trim() ?? "";
        if (HasPendingImages)
        {
            await SendPlainAsync(text);
            return;
        }

        if (IsImageModel(SelectedLocalModel))
        {
            if (SelectedAction != ActionChat)
            {
                StatusText = $"{SelectedLocalModel!.Label} generates images from Chat. Pick a text model to summarize or translate a page.";
                return;
            }
            if (text.Length == 0)
                return;
            await GenerateImageAsync(text);
            return;
        }

        switch (SelectedAction)
        {
            case ActionSummarizePage:
                await BeginPageSendAsync(AiPageAction.Summarize, null);
                return;
            case ActionSummarizeThread:
                await BeginPageSendAsync(AiPageAction.SummarizeThread, null);
                return;
            case ActionTranslate:
                await BeginPageSendAsync(AiPageAction.Translate, null);
                return;
            case ActionAskPage:
                if (text.Length == 0)
                {
                    StatusText = "Type a question, then Send.";
                    return;
                }
                await BeginPageSendAsync(AiPageAction.Ask, text);
                return;
        }

        if (text.Length == 0)
            return;

        var session = CurrentSession();
        if (!IsThinking && session.Extract != null && UrlsMatch(session.Extract.Url, PageUrl))
        {
            await BeginPageSendAsync(AiPageAction.FollowUp, text);
            return;
        }

        await SendPlainAsync(text);
    }

    [RelayCommand]
    private void ToggleThinking()
    {
        if (IsBusy)
            return;
        IsThinking = !IsThinking;
    }

    [RelayCommand]
    private void Stop()
    {
        _sendCts?.Cancel();
    }

    [RelayCommand]
    private async Task AttachImagesAsync()
    {
        if (PickImageFilesAsync == null)
            return;
        var files = await PickImageFilesAsync();
        if (files == null || files.Count == 0)
            return;
        foreach (var file in files)
            TryAddPendingImageFile(file);
    }

    [RelayCommand]
    private void RemovePendingImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        PendingImagePaths.Remove(path);
    }

    public bool TryAddPendingImageFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusText = "That image file could not be read.";
            return false;
        }
        if (!IsImageExtension(path))
        {
            StatusText = "Use a PNG, JPEG, WebP, GIF, or BMP image.";
            return false;
        }
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxPromptImageBytes)
            {
                StatusText = "That image is larger than 12 MB.";
                return false;
            }
            if (PendingImagePaths.Count >= MaxPendingImages)
            {
                StatusText = $"You can attach up to {MaxPendingImages} images.";
                return false;
            }
            var dest = CopyPromptImage(path);
            PendingImagePaths.Add(dest);
            StatusText = PendingImagePaths.Count == 1
                ? "Image attached. Add a message or Send."
                : $"{PendingImagePaths.Count} images attached.";
            return true;
        }
        catch (Exception)
        {
            StatusText = "Could not attach that image.";
            return false;
        }
    }

    public bool TryAddPendingImageBytes(byte[] bytes, string extension)
    {
        if (bytes.Length == 0)
            return false;
        if (bytes.Length > MaxPromptImageBytes)
        {
            StatusText = "That image is larger than 12 MB.";
            return false;
        }
        if (PendingImagePaths.Count >= MaxPendingImages)
        {
            StatusText = $"You can attach up to {MaxPendingImages} images.";
            return false;
        }
        var ext = NormalizeImageExtension(extension);
        if (ext == null)
        {
            StatusText = "Use a PNG, JPEG, WebP, GIF, or BMP image.";
            return false;
        }
        try
        {
            var dest = Path.Combine(PromptImageDirectory(), $"paste-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}{ext}");
            File.WriteAllBytes(dest, bytes);
            PendingImagePaths.Add(dest);
            StatusText = PendingImagePaths.Count == 1
                ? "Image pasted. Add a message or Send."
                : $"{PendingImagePaths.Count} images attached.";
            return true;
        }
        catch (Exception)
        {
            StatusText = "Could not paste that image.";
            return false;
        }
    }

    [RelayCommand]
    private async Task SaveConversationAsync()
    {
        if (IsConfirmOpen || Messages.Count == 0)
        {
            StatusText = "No conversation to save.";
            return;
        }

        if (SaveConversationFileAsync == null)
            return;

        var name = AiConversationExport.SuggestedFileName(PageTitle);
        var lines = Messages.Select(ToTranscriptLine).ToList();
        var text = AiConversationExport.Format(lines, PageTitle, PageUrl, HeaderMeta, OutputLanguage);
        try
        {
            var saved = await SaveConversationFileAsync(name, text);
            StatusText = saved ? "Conversation saved." : "Save cancelled.";
        }
        catch (Exception)
        {
            StatusText = "Could not save the conversation.";
        }
    }

    [RelayCommand]
    private async Task SummarizePageAsync()
        => await BeginPageSendAsync(AiPageAction.Summarize, null);

    [RelayCommand]
    private async Task SummarizeThreadAsync()
        => await BeginPageSendAsync(AiPageAction.SummarizeThread, null);

    [RelayCommand]
    private async Task AskThisPageAsync()
    {
        var question = MessageInput?.Trim() ?? "";
        if (question.Length == 0)
        {
            StatusText = "Type a question, then Ask this page.";
            return;
        }
        await BeginPageSendAsync(AiPageAction.Ask, question);
    }

    [RelayCommand]
    private async Task TranslatePageAsync()
        => await BeginPageSendAsync(AiPageAction.Translate, null);

    [RelayCommand]
    private async Task ConfirmSendAsync()
    {
        var thinking = _thinkingPending;
        if (thinking != null)
        {
            if (ConfirmDontAskAgain)
                CurrentSession().SkipThinkingConfirm = true;
            CloseConfirm();
            await SendThinkingAsync(thinking.Text, thinking.Images);
            return;
        }

        var pending = _pending;
        if (pending == null)
        {
            CloseConfirm();
            return;
        }

        var session = CurrentSession();
        if (ConfirmDontAskAgain)
            session.SkipConfirm = true;
        CloseConfirm();
        await SendPageAsync(pending);
    }

    [RelayCommand]
    private void ConfirmCancel()
    {
        CloseConfirm();
        StatusText = "Cancelled.";
    }

    partial void OnSelectedProviderChanged(string value)
    {
        if (_loadingSettings)
        {
            RefreshCustomFlags(value);
            RefreshHeader();
            return;
        }
        ApplyProviderSelection(value);
        if (!IsNewCustom)
        {
            _settings.AiActiveProfileId = CurrentProfileId;
            _settings.AiProvider = IsCustomProvider ? AiProviderDefaults.CustomName : AiProviderDefaults.SpaceXaiName;
            _settings.Save();
        }
    }

    partial void OnModelChanged(string value) => RefreshHeader();
    partial void OnBaseUrlChanged(string value)
    {
        RefreshHeader();
        if (_loadingSettings)
            return;
        RefreshLocalModels();
        _ = SyncRunningLocalModelAsync();
    }
    partial void OnShowLocalModelPickerChanged(bool value) => OnPropertyChanged(nameof(CanPickLocalModel));
    partial void OnSelectedLocalModelChanged(LocalLlamaModel? value)
    {
        if (_loadingSettings || _syncingLocalPicker || value == null)
            return;
        _ = SwitchLocalModelAsync(value);
    }
    partial void OnCustomNameChanged(string value) => RefreshHeader();
    partial void OnHasSavedKeyChanged(bool value) => OnPropertyChanged(nameof(ApiKeyPlaceholder));
    partial void OnIsBusyChanged(bool value)
    {
        _idleWatch.SetBusy(value, DateTime.UtcNow);
        RefreshCommandState();
    }
    partial void OnMessageInputChanged(string value) => OnPropertyChanged(nameof(CanSend));
    partial void OnSelectedActionChanged(string value) => RefreshCommandState();
    partial void OnPageUrlChanged(string value) => RefreshCommandState();
    partial void OnIsConfirmOpenChanged(bool value) => RefreshCommandState();
    partial void OnIsThinkingChanged(bool value)
    {
        if (_loadingSettings)
            return;
        _settings.AiThinking = value;
        _settings.Save();
    }
    partial void OnOutputLanguageChanged(string value)
    {
        if (_loadingSettings || string.IsNullOrWhiteSpace(value))
            return;
        _settings.AiOutputLanguage = value;
        _settings.Save();
    }

    partial void OnChatFontSizeChanged(int value)
    {
        var clamped = Math.Clamp(value, 11, 22);
        if (clamped != value)
        {
            ChatFontSize = clamped;
            return;
        }
        if (_loadingSettings)
            return;
        _settings.AiChatFontSize = clamped;
        _settings.Save();
    }

    private void RefreshCommandState()
    {
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanUsePageActions));
        OnPropertyChanged(nameof(CanTranslate));
        OnPropertyChanged(nameof(IsBlankPage));
        OnPropertyChanged(nameof(CanPickLocalModel));
    }

    private void LoadFromSettings()
    {
        _loadingSettings = true;
        try
        {
            MigrateLegacyCustomIfNeeded();
            if (!string.IsNullOrWhiteSpace(_settings.AiOutputLanguage)
                && LanguageChoices.Contains(_settings.AiOutputLanguage))
                OutputLanguage = _settings.AiOutputLanguage;
            ChatFontSize = Math.Clamp(_settings.AiChatFontSize == 0 ? 14 : _settings.AiChatFontSize, 11, 22);
            IsThinking = _settings.AiThinking;
            TryMoveLegacyKeyToActiveCustom();

            var select = AiProviderDefaults.SpaceXaiName;
            var active = AiProviderCatalog.FindById(_settings.AiCustomProviders, _settings.AiActiveProfileId);
            if (active != null)
                select = active.Name;
            else if (string.Equals(_settings.AiProvider, AiProviderDefaults.CustomName, StringComparison.OrdinalIgnoreCase)
                     && AiProviderCatalog.FindByName(_settings.AiCustomProviders, AiProviderDefaults.CustomName) is { } named)
                select = named.Name;

            RebuildProviderChoices(select);
            ApplyProviderSelection(select);
            RefreshLocalModels();
            OnPropertyChanged(nameof(ApiKeyPlaceholder));
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void MigrateLegacyCustomIfNeeded()
    {
        if (_settings.AiCustomProviders.Count > 0)
            return;
        var migrated = AiProviderCatalog.TryMigrateLegacy(_settings.AiProvider, _settings.AiBaseUrl, _settings.AiModel);
        if (migrated == null)
            return;
        migrated.Name = AiProviderCatalog.UniqueName(_settings.AiCustomProviders, migrated.Name, null);
        _settings.AiCustomProviders.Add(migrated);
        _settings.AiActiveProfileId = migrated.Id;
        try
        {
            if (_secrets.HasKeyFor(AiProviderDefaults.SpaceXaiId) && !_secrets.HasKeyFor(migrated.Id))
            {
                var key = _secrets.TryLoadKeyFor(AiProviderDefaults.SpaceXaiId);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    _secrets.SaveKeyFor(migrated.Id, key);
                    _secrets.DeleteKeyFor(AiProviderDefaults.SpaceXaiId);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // PIN not unlocked yet; key stays in the legacy file until the next save.
        }
        _settings.Save();
        TryMoveLegacyKeyToActiveCustom();
    }

    private void TryMoveLegacyKeyToActiveCustom()
    {
        var id = _settings.AiActiveProfileId;
        if (string.IsNullOrWhiteSpace(id)
            || string.Equals(id, AiProviderDefaults.SpaceXaiId, StringComparison.Ordinal))
            return;
        try
        {
            if (_secrets.HasKeyFor(id) || !_secrets.HasKeyFor(AiProviderDefaults.SpaceXaiId))
                return;
            var key = _secrets.TryLoadKeyFor(AiProviderDefaults.SpaceXaiId);
            if (string.IsNullOrWhiteSpace(key))
                return;
            _secrets.SaveKeyFor(id, key);
            _secrets.DeleteKeyFor(AiProviderDefaults.SpaceXaiId);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private bool TryPersistProvider(out string error)
    {
        error = "";
        _settings.AiOutputLanguage = OutputLanguage;
        if (string.Equals(SelectedProvider, AiProviderDefaults.SpaceXaiName, StringComparison.Ordinal)
            || !IsCustomProvider)
        {
            var model = string.IsNullOrWhiteSpace(Model) ? AiProviderDefaults.SpaceXaiModel : Model.Trim();
            _settings.AiProvider = AiProviderDefaults.SpaceXaiName;
            _settings.AiActiveProfileId = AiProviderDefaults.SpaceXaiId;
            _settings.AiSpaceXaiModel = model;
            _settings.AiModel = model;
            _settings.AiBaseUrl = AiProviderDefaults.SpaceXaiBaseUrl;
            _settings.Save();
            BaseUrl = AiProviderDefaults.SpaceXaiBaseUrl;
            Model = model;
            return true;
        }

        var url = BaseUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Enter an http(s) API base URL.";
            return false;
        }

        var name = string.IsNullOrWhiteSpace(CustomName)
            ? AiProviderCatalog.SuggestName(url, Model)
            : CustomName.Trim();
        var existing = IsNewCustom ? null : AiProviderCatalog.FindByName(_settings.AiCustomProviders, SelectedProvider);
        name = AiProviderCatalog.UniqueName(_settings.AiCustomProviders, name, existing?.Id);
        var modelName = (Model ?? "").Trim();
        if (existing == null)
        {
            existing = new AiProviderProfile();
            _settings.AiCustomProviders.Add(existing);
        }
        existing.Name = name;
        existing.BaseUrl = url.TrimEnd('/');
        existing.Model = modelName;
        _settings.AiProvider = AiProviderDefaults.CustomName;
        _settings.AiActiveProfileId = existing.Id;
        _settings.AiBaseUrl = existing.BaseUrl;
        _settings.AiModel = existing.Model;
        _settings.Save();
        CustomName = name;
        BaseUrl = existing.BaseUrl;
        Model = existing.Model;
        RebuildProviderChoices(name);
        RefreshCustomFlags(name);
        return true;
    }

    private void RebuildProviderChoices(string select)
    {
        var loading = _loadingSettings;
        _loadingSettings = true;
        try
        {
            ProviderChoices.Clear();
            ProviderChoices.Add(AiProviderDefaults.SpaceXaiName);
            foreach (var profile in _settings.AiCustomProviders.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(profile.Name))
                    ProviderChoices.Add(profile.Name);
            }
            ProviderChoices.Add(AiProviderDefaults.NewCustomName);
            SelectedProvider = ProviderChoices.Contains(select) ? select : AiProviderDefaults.SpaceXaiName;
        }
        finally
        {
            _loadingSettings = loading;
        }
    }

    private void ApplyProviderSelection(string name)
    {
        RefreshCustomFlags(name);
        ApiKeyInput = "";
        if (IsNewCustom)
        {
            CustomName = "";
            BaseUrl = "";
            Model = "";
            HasSavedKey = false;
            RefreshHeader();
            OnPropertyChanged(nameof(ApiKeyPlaceholder));
            return;
        }

        if (!IsCustomProvider)
        {
            CustomName = "";
            BaseUrl = AiProviderDefaults.SpaceXaiBaseUrl;
            Model = string.IsNullOrWhiteSpace(_settings.AiSpaceXaiModel)
                ? AiProviderDefaults.SpaceXaiModel
                : _settings.AiSpaceXaiModel;
        }
        else
        {
            var profile = AiProviderCatalog.FindByName(_settings.AiCustomProviders, name);
            CustomName = profile?.Name ?? name;
            BaseUrl = profile?.BaseUrl ?? "";
            Model = profile?.Model ?? "";
        }

        HasSavedKey = !string.IsNullOrWhiteSpace(CurrentProfileId) && _secrets.HasKeyFor(CurrentProfileId);
        RefreshHeader();
        OnPropertyChanged(nameof(ApiKeyPlaceholder));
    }

    private void RefreshCustomFlags(string? name)
    {
        IsNewCustom = string.Equals(name, AiProviderDefaults.NewCustomName, StringComparison.Ordinal);
        IsCustomProvider = !string.Equals(name, AiProviderDefaults.SpaceXaiName, StringComparison.Ordinal);
        CanDeleteProvider = IsCustomProvider && !IsNewCustom;
    }

    private string CurrentProfileId
    {
        get
        {
            if (!IsCustomProvider)
                return AiProviderDefaults.SpaceXaiId;
            return AiProviderCatalog.FindByName(_settings.AiCustomProviders, SelectedProvider)?.Id ?? "";
        }
    }

    private AiChatEndpoint BuildEndpoint()
    {
        var url = IsCustomProvider ? BaseUrl : AiProviderDefaults.SpaceXaiBaseUrl;
        var model = string.IsNullOrWhiteSpace(Model)
            ? (IsCustomProvider ? Model : AiProviderDefaults.SpaceXaiModel)
            : Model.Trim();
        if (string.IsNullOrWhiteSpace(model))
            model = AiProviderDefaults.SpaceXaiModel;
        return new AiChatEndpoint((url ?? "").Trim(), model);
    }

    private string? ResolveKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyInput))
            return ApiKeyInput.Trim();
        if (string.IsNullOrWhiteSpace(CurrentProfileId))
            return null;
        return _secrets.TryLoadKeyFor(CurrentProfileId);
    }

    private void RefreshHeader()
    {
        var provider = IsNewCustom
            ? (string.IsNullOrWhiteSpace(CustomName) ? AiProviderDefaults.NewCustomName : CustomName.Trim())
            : (string.IsNullOrWhiteSpace(SelectedProvider) ? AiProviderDefaults.SpaceXaiName : SelectedProvider);
        var model = ShowLocalModelPicker && SelectedLocalModel != null
            ? SelectedLocalModel.Label
            : (string.IsNullOrWhiteSpace(Model) ? "" : Model.Trim());
        HeaderMeta = string.IsNullOrEmpty(model) ? provider : $"{provider} - {model}";
    }

    private void RefreshLocalModels()
    {
        _syncingLocalPicker = true;
        try
        {
            ShowLocalModelPicker = _localLlama != null && LocalLlamaCatalog.IsLoopback(BaseUrl);
            LocalModels.Clear();
            if (!ShowLocalModelPicker || _localLlama == null)
            {
                SelectedLocalModel = null;
                RefreshHeader();
                return;
            }

            foreach (var model in _localLlama.ListInstalled())
                LocalModels.Add(model);

            SelectedLocalModel = MatchLocalModel(_settings.AiLocalModelId)
                ?? MatchLocalModel(Model);
            RefreshHeader();
        }
        finally
        {
            _syncingLocalPicker = false;
            OnPropertyChanged(nameof(CanPickLocalModel));
        }
    }

    private LocalLlamaModel? MatchLocalModel(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return null;
        return LocalModels.FirstOrDefault(m =>
            string.Equals(m.Id, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Alias, text, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.FileName, text, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SyncRunningLocalModelAsync()
    {
        if (_localLlama == null || !ShowLocalModelPicker)
            return;
        try
        {
            var alias = await _localLlama.ProbeRunningAliasAsync(BaseUrl);
            if (string.IsNullOrWhiteSpace(alias))
                return;
            var match = MatchLocalModel(alias);
            if (match == null)
                return;
            _syncingLocalPicker = true;
            try
            {
                SelectedLocalModel = match;
                Model = match.Alias;
                PersistLocalChoice(match, saveProfile: false);
                RefreshHeader();
            }
            finally
            {
                _syncingLocalPicker = false;
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            // Probe failed; leave the last picker selection.
        }
    }

    private async Task SwitchLocalModelAsync(LocalLlamaModel model)
    {
        if (_localLlama == null)
            return;
        if (IsBusy)
        {
            StatusText = "Wait for the current reply before switching models.";
            return;
        }

        IsBusy = true;
        WriteTerminal($"$ start · {model.Label}");
        _sendCts?.Cancel();
        _sendCts = new CancellationTokenSource();
        try
        {
            var result = await _localLlama.SwitchAsync(model, BaseUrl, _sendCts.Token, StatusProgress());
            WriteTerminal(result.Message);
            if (!result.Ok)
            {
                await SyncRunningLocalModelAsync();
                return;
            }

            var alias = result.Alias ?? model.Alias;
            _syncingLocalPicker = true;
            try
            {
                Model = alias;
                PersistLocalChoice(model, saveProfile: true);
                RefreshHeader();
            }
            finally
            {
                _syncingLocalPicker = false;
            }
            RefreshLocalModels();
            if (model.GeneratesImages && _imageGenerator is not { IsReady: true })
                WriteTerminal("Text-to-image runtime is still installing.");
        }
        catch (OperationCanceledException)
        {
            WriteTerminal("Stopped.");
        }
        catch (Exception ex)
        {
            WriteTerminal($"Could not start {model.Label}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void PersistLocalChoice(LocalLlamaModel model, bool saveProfile)
    {
        _settings.AiLocalModelId = model.Id;
        if (saveProfile && IsCustomProvider && !IsNewCustom)
        {
            var profile = AiProviderCatalog.FindByName(_settings.AiCustomProviders, SelectedProvider);
            if (profile != null)
            {
                profile.Model = model.Alias;
                _settings.AiModel = model.Alias;
            }
        }
        _settings.Save();
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
            HookActiveTab();
    }

    private void OnTabClosed(object? sender, string tabId) => DropTab(tabId);

    private void HookActiveTab()
    {
        if (_hookedTab != null)
            _hookedTab.PropertyChanged -= OnTabPropertyChanged;
        _hookedTab = _main?.ActiveTab;
        if (_hookedTab != null)
            _hookedTab.PropertyChanged += OnTabPropertyChanged;
        SwitchTo(_hookedTab?.Id ?? "", _hookedTab?.Title ?? "", _hookedTab?.Url ?? "");
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_hookedTab == null)
            return;
        if (e.PropertyName == nameof(TabItemViewModel.Title))
            PageTitle = _hookedTab.Title ?? "";
        if (e.PropertyName == nameof(TabItemViewModel.Url))
        {
            var url = _hookedTab.Url ?? "";
            var session = CurrentSession();
            if (!UrlsMatch(session.ExtractUrl, url))
            {
                session.SkipConfirm = false;
                session.Extract = null;
                session.ExtractUrl = null;
            }
            PageUrl = url;
        }
    }

    internal void SwitchTo(string tabId, string title, string url)
    {
        StashCurrent();
        CloseConfirm();
        _tabId = tabId;
        PageTitle = title;
        PageUrl = url;
        Messages.Clear();
        if (!string.IsNullOrEmpty(tabId) && _sessions.TryGetValue(tabId, out var session))
        {
            foreach (var bubble in session.Messages)
                Messages.Add(bubble);
        }
        RefreshCommandState();
    }

    private void StashCurrent()
    {
        if (string.IsNullOrEmpty(_tabId))
            return;
        var session = CurrentSession();
        session.Messages.Clear();
        session.Messages.AddRange(Messages);
    }

    private TabAiSession CurrentSession()
    {
        var id = SessionKey(_tabId);
        if (!_sessions.TryGetValue(id, out var session))
        {
            session = new TabAiSession();
            _sessions[id] = session;
        }
        return session;
    }

    private static string SessionKey(string tabId) => string.IsNullOrEmpty(tabId) ? "_" : tabId;

    private void AddBubble(AiChatBubble bubble)
    {
        Messages.Add(bubble);
        StashCurrent();
    }

    // A reply can finish after the user switched tabs: its draft, error or "stopped"
    // cleanup belongs to the tab that asked, not the one on screen.
    private void AddBubbleTo(string tabId, AiChatBubble bubble)
    {
        if (tabId == _tabId)
            AddBubble(bubble);
        else if (_sessions.TryGetValue(SessionKey(tabId), out var session))
            session.Messages.Add(bubble);
    }

    private void RemoveBubbleFrom(string tabId, AiChatBubble bubble)
    {
        if (tabId == _tabId)
        {
            Messages.Remove(bubble);
            StashCurrent();
        }
        else if (_sessions.TryGetValue(SessionKey(tabId), out var session))
            session.Messages.Remove(bubble);
    }

    private async Task BeginPageSendAsync(AiPageAction action, string? question)
    {
        if (IsBusy || IsConfirmOpen)
            return;
        if (IsImageModel(SelectedLocalModel))
        {
            StatusText = $"{SelectedLocalModel!.Label} generates images from Chat. Pick a text model to summarize or translate a page.";
            return;
        }
        if (IsNonExtractable(PageUrl))
        {
            StatusText = "Open a web page first.";
            return;
        }

        IsSettingsOpen = false;
        var isThread = action == AiPageAction.SummarizeThread;
        StatusText = isThread ? "Finding thread pages…" : "Extracting page…";
        _sendCts?.Cancel();
        _sendCts = new CancellationTokenSource();
        var token = _sendCts.Token;
        IsBusy = true;
        string? json;
        AiPageContent page;
        try
        {
            json = ExtractPageJsonAsync == null ? null : await ExtractPageJsonAsync();
            token.ThrowIfCancellationRequested();
            var maxChars = isThread ? PageContentBuilder.ThreadMaxChars : PageContentBuilder.DefaultMaxChars;
            var first = PageContentBuilder.FromExtractJson(json, maxChars);
            if (first.Segments.Count == 0 || string.IsNullOrWhiteSpace(first.CombinedText))
            {
                StatusText = "This page has no extractable text.";
                AddBubble(new AiChatBubble { Text = "This page has no extractable text.", IsSystem = true });
                return;
            }

            page = isThread
                ? await CollectThreadAsync(json, first, token)
                : first;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            StatusText = "Stopped.";
            return;
        }
        catch (Exception)
        {
            StatusText = isThread ? "Could not read this thread." : "Could not read this page.";
            return;
        }
        finally
        {
            IsBusy = false;
        }

        var session = CurrentSession();
        session.Extract = page;
        session.ExtractUrl = PageUrl;
        var visible = AiPagePrompts.VisibleUserText(action, OutputLanguage, question);
        var pending = new PendingPageSend(action, visible, page, question);
        if (session.SkipConfirm)
        {
            await SendPageAsync(pending);
            return;
        }

        _pending = pending;
        ConfirmDontAskAgain = false;
        ConfirmAcceptLabel = "Send";
        ConfirmTitle = isThread
            ? $"Send thread to {SelectedProvider}?"
            : $"Send page to {SelectedProvider}?";
        ConfirmMessage = BuildConfirmMessage(page);
        IsConfirmOpen = true;
        StatusText = isThread ? "Confirm before sending this thread." : "Confirm before sending this page.";
    }

    private string BuildConfirmMessage(AiPageContent page)
    {
        var host = TryHost(BuildEndpoint().BaseUrl);
        var chars = page.CombinedText.Length;
        var lines = new List<string>
        {
            string.IsNullOrWhiteSpace(page.Title) ? "(no title)" : page.Title,
            string.IsNullOrWhiteSpace(page.Url) ? PageUrl : page.Url,
            "",
            $"{chars:N0} characters will be sent to {host} ({Model.Trim()})."
        };
        if (page.IsThread)
        {
            var included = page.IncludedPages;
            var pages = included.Count == 0
                ? $"{page.PagesFetched} page(s)"
                : (included.Count == 1
                    ? $"page {included[0]}"
                    : $"pages {string.Join(", ", included)}");
            var of = page.LastPage > 1 ? $" of {page.LastPage}" : "";
            lines.Add($"Thread: {pages}{of} included.");
        }
        if (page.Truncated)
            lines.Add($"The text was truncated from {page.OriginalCharCount:N0} characters.");
        return string.Join('\n', lines);
    }

    private async Task<AiPageContent> CollectThreadAsync(
        string? firstJson,
        AiPageContent first,
        CancellationToken token)
    {
        var meta = ThreadPagination.ReadMeta(firstJson, PageUrl);
        var currentPage = meta.CurrentPage > 0 ? meta.CurrentPage : 1;
        first = PageContentBuilder.StampPage(first, currentPage);

        var pending = ThreadPagination.Expand(PageUrl, meta).ToList();
        if (pending.Count <= 1
            && !string.IsNullOrWhiteSpace(meta.NextUrl)
            && ThreadPagination.IsSafePageUrl(PageUrl, meta.NextUrl))
        {
            var nextN = ThreadPagination.PageNumberFromUrl(meta.NextUrl);
            if (nextN <= 0)
                nextN = currentPage + 1;
            pending.Add(new ThreadPageLink(nextN, meta.NextUrl));
        }

        var parts = new List<(int Page, AiPageContent Content)> { (currentPage, first) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ThreadPagination.Normalize(PageUrl)
        };

        var lastKnown = Math.Max(meta.LastPage, pending.Count == 0 ? 1 : pending.Max(p => p.Number));
        for (var i = 0; i < pending.Count && parts.Count < ThreadPagination.MaxPages; i++)
        {
            token.ThrowIfCancellationRequested();
            var link = pending[i];
            if (string.IsNullOrWhiteSpace(link.Url) || !ThreadPagination.IsSafePageUrl(PageUrl, link.Url))
                continue;
            var norm = ThreadPagination.Normalize(link.Url);
            if (!seen.Add(norm))
                continue;

            StatusText = $"Reading thread page {link.Number} of {Math.Max(lastKnown, link.Number)}…";
            string? json = null;
            try
            {
                json = ExtractRemotePageJsonAsync == null ? null : await ExtractRemotePageJsonAsync(link.Url);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                json = null;
            }

            if (string.IsNullOrWhiteSpace(json))
                continue;
            var part = PageContentBuilder.FromExtractJson(json, PageContentBuilder.ThreadMaxChars);
            if (part.Segments.Count == 0)
                continue;
            parts.Add((link.Number, PageContentBuilder.StampPage(part, link.Number)));

            var partMeta = ThreadPagination.ReadMeta(json, link.Url);
            lastKnown = Math.Max(lastKnown, partMeta.LastPage);
            if (!string.IsNullOrWhiteSpace(partMeta.NextUrl)
                && ThreadPagination.IsSafePageUrl(PageUrl, partMeta.NextUrl)
                && !seen.Contains(ThreadPagination.Normalize(partMeta.NextUrl))
                && pending.Count < ThreadPagination.MaxPages)
            {
                var nextN = ThreadPagination.PageNumberFromUrl(partMeta.NextUrl);
                if (nextN <= 0)
                    nextN = link.Number + 1;
                pending.Add(new ThreadPageLink(nextN, partMeta.NextUrl));
            }
        }

        var lastPage = Math.Max(lastKnown, parts.Max(p => p.Page));
        if (parts.Count == 1)
            return PageContentBuilder.AsThread(parts[0].Content, 1, lastPage);
        return PageContentBuilder.MergeThread(parts, lastPage);
    }

    private async Task SendPageAsync(PendingPageSend pending)
    {
        if (!await EnsureVaultAsync.InvokeIfSet())
        {
            StatusText = "Unlock NexZen PIN to send.";
            return;
        }

        string? key;
        try { key = ResolveKey(); }
        catch (InvalidOperationException)
        {
            StatusText = "Unlock NexZen PIN to send.";
            return;
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            StatusText = "Save an API key in Settings first.";
            IsSettingsOpen = true;
            return;
        }

        if (pending.Action is AiPageAction.Ask or AiPageAction.FollowUp)
            MessageInput = "";

        AddBubble(new AiChatBubble { Text = pending.VisibleUserText, IsMine = true });
        var messages = BuildPageMessages(pending);
        await StreamAssistantAsync(messages, key, pending.Page.Segments);
    }

    private List<AiChatMessage> BuildPageMessages(PendingPageSend pending)
    {
        var language = string.IsNullOrWhiteSpace(OutputLanguage) ? "English" : OutputLanguage;
        var prior = Messages.Where(m => !m.IsSystem).ToList();
        if (prior.Count > 0 && prior[^1].IsMine
            && string.Equals(prior[^1].Text, pending.VisibleUserText, StringComparison.Ordinal))
            prior.RemoveAt(prior.Count - 1);
        var priorTurns = prior
            .Select(m => new AiChatMessage(m.IsMine ? "user" : "assistant", m.Text))
            .ToList();
        return AiPagePrompts.BuildRequest(
            language,
            PageContentBuilder.FormatForModel(pending.Page),
            AiPagePrompts.TaskText(pending.Action, language, pending.Question),
            priorTurns).ToList();
    }

    private async Task GenerateImageAsync(string prompt)
    {
        if (!await EnsureVaultAsync.InvokeIfSet())
        {
            StatusText = "Unlock NexZen PIN to send.";
            return;
        }

        if (_imageGenerator == null || SelectedLocalModel == null || !_imageGenerator.Supports(SelectedLocalModel))
        {
            StatusText = "This local model cannot generate images.";
            return;
        }

        MessageInput = "";
        var tabId = _tabId;
        AddBubble(new AiChatBubble { Text = prompt, IsMine = true });
        var draft = new AiChatBubble { Text = "Generating image…" };
        AddBubble(draft);
        MessagesLayoutChanged?.Invoke();
        IsBusy = true;
        StatusText = "Generating image on CPU… this can take a while. Press Stop to cancel.";
        _sendCts?.Cancel();
        _sendCts = new CancellationTokenSource();
        try
        {
            var result = await _imageGenerator.GenerateAsync(prompt, _sendCts.Token);
            if (result.Ok && !string.IsNullOrWhiteSpace(result.ImagePath))
            {
                draft.Text = string.IsNullOrWhiteSpace(result.Text) ? "" : result.Text;
                draft.ImagePath = result.ImagePath;
                StatusText = "Generated image.";
            }
            else
            {
                RemoveBubbleFrom(tabId, draft);
                AddBubbleTo(tabId, new AiChatBubble { Text = result.Error ?? "Image generation failed.", IsSystem = true });
                StatusText = result.Error ?? "Image generation failed.";
            }
        }
        catch (OperationCanceledException)
        {
            if (!draft.HasImage)
                RemoveBubbleFrom(tabId, draft);
            StatusText = "Stopped.";
        }
        catch (Exception)
        {
            if (!draft.HasImage)
                RemoveBubbleFrom(tabId, draft);
            StatusText = "Could not generate an image.";
        }
        finally
        {
            IsBusy = false;
            MessagesLayoutChanged?.Invoke();
        }
    }

    private static bool IsImageModel(LocalLlamaModel? model)
        => model is { GeneratesImages: true };

    private const int MaxPendingImages = 4;
    private const long MaxPromptImageBytes = 12L * 1024 * 1024;

    private static string PromptImageDirectory()
    {
        var data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(data, "NexZenDesktop", "ai-images", "prompt");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CopyPromptImage(string sourcePath)
    {
        var ext = NormalizeImageExtension(Path.GetExtension(sourcePath)) ?? ".png";
        var dest = Path.Combine(PromptImageDirectory(), $"img-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}{ext}");
        File.Copy(sourcePath, dest, overwrite: false);
        return dest;
    }

    private static bool IsImageExtension(string path)
        => NormalizeImageExtension(Path.GetExtension(path)) != null;

    private static string? NormalizeImageExtension(string? extension)
    {
        var ext = (extension ?? "").Trim();
        if (ext.Length == 0)
            return null;
        if (!ext.StartsWith('.'))
            ext = "." + ext;
        ext = ext.ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" ? ext : null;
    }

    private async Task SendPlainAsync(string text)
    {
        var key = await ResolveChatKeyAsync();
        if (key == null)
            return;

        var images = PendingImagePaths.ToList();
        if (text.Length == 0 && images.Count == 0)
            return;

        if (IsThinking && text.Length > 0)
        {
            var session = CurrentSession();
            if (!session.SkipThinkingConfirm)
            {
                _thinkingPending = new PendingThinkingSend(text, images);
                ConfirmDontAskAgain = false;
                ConfirmAcceptLabel = "Look up";
                ConfirmTitle = "Look this up on the internet?";
                ConfirmMessage = BuildThinkingConfirmMessage(text);
                IsConfirmOpen = true;
                StatusText = "Confirm before looking up the web.";
                return;
            }

            await SendThinkingAsync(text, images);
            return;
        }

        await SendChatNowAsync(text, images, key);
    }

    private async Task SendThinkingAsync(string text, IReadOnlyList<string> images)
    {
        var key = await ResolveChatKeyAsync();
        if (key == null)
            return;

        PendingImagePaths.Clear();
        MessageInput = "";
        AddBubble(new AiChatBubble { Text = text, IsMine = true, ImagePaths = images });

        IsBusy = true;
        _sendCts?.Cancel();
        _sendCts = new CancellationTokenSource();
        var token = _sendCts.Token;
        StatusText = "Searching…";
        WebLookupResult lookup;
        try
        {
            lookup = await LookupThinkingAsync(text, token);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            StatusText = "Stopped.";
            IsBusy = false;
            return;
        }
        catch (Exception)
        {
            lookup = new WebLookupResult(text, "", Array.Empty<WebLookupHit>(), "The public web lookup failed.");
        }

        var messages = BuildThinkingMessages(text, lookup, images);
        await StreamAssistantAsync(messages, key, segmentsForCitations: null);
    }

    private List<AiChatMessage> BuildThinkingMessages(
        string question, WebLookupResult lookup, IReadOnlyList<string> images)
    {
        var language = string.IsNullOrWhiteSpace(OutputLanguage) ? "English" : OutputLanguage;
        var prior = Messages.Where(m => !m.IsSystem).ToList();
        if (prior.Count > 0 && prior[^1].IsMine
            && string.Equals(prior[^1].Text, question, StringComparison.Ordinal))
            prior.RemoveAt(prior.Count - 1);
        var priorTurns = prior.Select(ToHistoryMessage).ToList();
        var messages = AiPagePrompts.BuildRequest(
            language,
            WebLookupPrompt.FormatAsPage(lookup),
            AiPagePrompts.ThinkingTask(language, question),
            priorTurns,
            AiPagePrompts.ThinkingInstructions(language)).ToList();
        if (images.Count == 0)
            return messages;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                continue;
            messages[i] = messages[i] with { ImagePaths = images };
            break;
        }

        return messages;
    }

    private async Task SendChatNowAsync(string text, IReadOnlyList<string> images, string key)
    {
        PendingImagePaths.Clear();
        MessageInput = "";
        AddBubble(new AiChatBubble { Text = text, IsMine = true, ImagePaths = images });
        var history = Messages
            .Where(m => !m.IsSystem)
            .Select(ToHistoryMessage)
            .ToList();
        await StreamAssistantAsync(history, key, segmentsForCitations: null);
    }

    private async Task<string?> ResolveChatKeyAsync()
    {
        if (!await EnsureVaultAsync.InvokeIfSet())
        {
            StatusText = "Unlock NexZen PIN to send.";
            return null;
        }

        string? key;
        try { key = ResolveKey(); }
        catch (InvalidOperationException)
        {
            StatusText = "Unlock NexZen PIN to send.";
            return null;
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            StatusText = "Save an API key in Settings first.";
            IsSettingsOpen = true;
            return null;
        }

        return key;
    }

    private static AiChatMessage ToHistoryMessage(AiChatBubble m)
        => new(
            m.IsMine ? "user" : "assistant",
            m.Text,
            m.IsMine && m.ImagePaths.Count > 0 ? m.ImagePaths : null);

    private async Task<WebLookupResult> LookupThinkingAsync(string text, CancellationToken token)
    {
        var progress = new Progress<string>(line =>
        {
            if (!string.IsNullOrWhiteSpace(line))
                StatusText = line.Trim();
        });
        WebLookupResult? browser = null;
        if (_research != null)
        {
            string? tabJson = null;
            if (!IsNonExtractable(PageUrl) && SerpParser.QueryLooksRelated(text, PageTitle, PageUrl)
                && ExtractPageJsonAsync != null)
            {
                try { tabJson = await ExtractPageJsonAsync(); }
                catch { tabJson = null; }
            }

            var request = new WebLookupRequest(
                text,
                _main?.SelectedSearchEngine ?? _settings.SearchEngine,
                PageTitle,
                PageUrl,
                tabJson,
                OutputLanguage);
            try
            {
                browser = await _research.ResearchAsync(request, progress, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                browser = null;
            }
        }

        var hasPage = browser?.Hits.Any(h => WebLookupParser.IsPage(h.Source)) == true;
        if (hasPage)
            return browser!;

        WebLookupResult? http = null;
        if (_webLookup != null)
        {
            StatusText = "Looking up the web…";
            http = await _webLookup.SearchAsync(text, token, OutputLanguage);
        }

        if (browser != null && browser.HasContent && http != null && http.HasContent)
            return new WebLookupResult(text, http.InstantAnswer, WebLookupParser.Combine(browser.Hits, http.Hits));
        if (browser != null && browser.HasContent)
            return browser;
        if (http != null && http.HasContent)
            return http;
        return browser ?? http
            ?? new WebLookupResult(text, "", Array.Empty<WebLookupHit>(), "Web lookup is not available.");
    }

    private string BuildThinkingConfirmMessage(string query)
    {
        var preview = WebLookupParser.Clip(query.ReplaceLineEndings(" "), 160);
        var engine = SearchTargets.DisplayName(_main?.SelectedSearchEngine ?? _settings.SearchEngine);
        return
            "NexZen will search " + engine + " for:\n\n" +
            "\"" + preview + "\"\n\n" +
            "then open up to two result pages. Nothing is sent until you confirm.";
    }

    public void NotifyWindowVisible(bool visible)
        => _idleWatch.SetVisible(visible, DateTime.UtcNow);

    public async Task StopIdleLocalServerIfDueAsync()
    {
        if (_localLlama == null || !ShowLocalModelPicker)
            return;
        if (!_idleWatch.TryConsumeStop(DateTime.UtcNow))
            return;
        await _localLlama.StopAsync();
        WriteTerminal("Local model stopped after 5 minutes idle.");
    }

    private IProgress<string> StatusProgress()
        => new Progress<string>(WriteTerminal);

    private void WriteTerminal(string? message)
    {
        var line = (message ?? "").Trim();
        if (line.Length == 0)
            return;
        StatusText = line;
        if (_setupTerminal == null || Messages.Count == 0 || !ReferenceEquals(Messages[^1], _setupTerminal))
        {
            _setupTerminal = new AiChatBubble { IsSystem = true, IsTerminal = true, Text = line };
            AddBubble(_setupTerminal);
        }
        else
            _setupTerminal.Text = LocalSetupTerminal.Apply(_setupTerminal.Text, line);
        MessagesLayoutChanged?.Invoke();
    }

    private async Task<bool> EnsureLocalLlamaRunningAsync(CancellationToken cancellationToken = default)
    {
        if (_localLlama == null || !ShowLocalModelPicker)
            return true;
        var model = SelectedLocalModel;
        if (model == null || model.GeneratesImages)
            return true;
        var alias = await _localLlama.ProbeRunningAliasAsync(BaseUrl, cancellationToken);
        if (!string.IsNullOrWhiteSpace(alias))
            return true;
        WriteTerminal($"$ start · {model.Label}");
        var result = await _localLlama.SwitchAsync(model, BaseUrl, cancellationToken, StatusProgress());
        WriteTerminal(result.Message);
        if (result.Ok)
        {
            RefreshLocalModels();
            return true;
        }
        return false;
    }

    private async Task StreamAssistantAsync(
        IReadOnlyList<AiChatMessage> messages,
        string key,
        IReadOnlyList<AiPageSegment>? segmentsForCitations)
    {
        IsBusy = true;
        _sendCts?.Cancel();
        _sendCts = new CancellationTokenSource();
        if (!await EnsureLocalLlamaRunningAsync(_sendCts.Token))
        {
            IsBusy = false;
            return;
        }
        var tabId = _tabId;
        var draft = new AiChatBubble();
        AddBubble(draft);
        MessagesLayoutChanged?.Invoke();
        StatusText = "Waiting for the model…";
        var streaming = true;
        var progress = new Progress<string>(text =>
        {
            if (!streaming)
                return;
            draft.Text = text;
            StatusText = "Writing…";
            MessagesLayoutChanged?.Invoke();
        });
        // Reasoning models can think for a long time before the first answer word.
        var status = new Progress<string>(line =>
        {
            if (streaming && string.IsNullOrWhiteSpace(draft.Text) && !string.IsNullOrWhiteSpace(line))
                StatusText = line;
        });
        var idleStatus = HeaderMeta;
        try
        {
            var result = await _chatClient.CompleteAsync(
                BuildEndpoint(), key, messages, maxTokens: null, _sendCts.Token, progress, status);
            if (result.Ok)
            {
                var raw = string.IsNullOrWhiteSpace(result.Text) ? draft.Text : result.Text;
                if (segmentsForCitations != null)
                {
                    var parsed = AiReplyParser.Parse(raw, segmentsForCitations);
                    draft.Text = parsed.Answer;
                }
                else
                    draft.Text = string.IsNullOrWhiteSpace(raw) ? "(empty reply)" : raw;
                idleStatus = HeaderMeta;
            }
            else if (string.IsNullOrWhiteSpace(draft.Text))
            {
                RemoveBubbleFrom(tabId, draft);
                AddBubbleTo(tabId, new AiChatBubble { Text = result.Error ?? "The request failed.", IsSystem = true });
                idleStatus = result.Error ?? "The request failed.";
            }
            else
                idleStatus = result.Error ?? "The request failed.";
        }
        catch (OperationCanceledException)
        {
            if (string.IsNullOrWhiteSpace(draft.Text))
                RemoveBubbleFrom(tabId, draft);
            idleStatus = "Stopped.";
        }
        catch (Exception)
        {
            if (string.IsNullOrWhiteSpace(draft.Text))
                RemoveBubbleFrom(tabId, draft);
            idleStatus = "Could not reach the provider.";
        }
        finally
        {
            streaming = false;
            IsBusy = false;
            StatusText = idleStatus;
            MessagesLayoutChanged?.Invoke();
        }
    }

    private void CloseConfirm()
    {
        _pending = null;
        _thinkingPending = null;
        ConfirmAcceptLabel = "Send";
        IsConfirmOpen = false;
    }

    private static bool IsNonExtractable(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;
        var text = url.Trim();
        return text.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("chrome:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("nexzen:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool UrlsMatch(string? left, string? right)
        => string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static string TryHost(string baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl;
    }

    private static AiTranscriptLine ToTranscriptLine(AiChatBubble bubble)
    {
        var role = bubble.IsTerminal ? "Setup" : (bubble.IsSystem ? "Note" : (bubble.IsMine ? "You" : "Assistant"));
        var image = bubble.ImagePaths.Count > 0
            ? string.Join(", ", bubble.ImagePaths)
            : bubble.ImagePath;
        return new AiTranscriptLine(role, bubble.Text ?? "", bubble.Citations, image);
    }

    private sealed class TabAiSession
    {
        public List<AiChatBubble> Messages { get; } = new();
        public bool SkipConfirm { get; set; }
        public bool SkipThinkingConfirm { get; set; }
        public AiPageContent? Extract { get; set; }
        public string? ExtractUrl { get; set; }
    }

    private sealed record PendingPageSend(
        AiPageAction Action,
        string VisibleUserText,
        AiPageContent Page,
        string? Question);

    private sealed record PendingThinkingSend(string Text, IReadOnlyList<string> Images);
}

file static class AiVaultGate
{
    public static Task<bool> InvokeIfSet(this Func<Task<bool>>? gate)
        => gate == null ? Task.FromResult(true) : gate();
}
