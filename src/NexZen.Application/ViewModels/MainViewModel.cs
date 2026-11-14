using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using NexZen.Application.Services;
using NexZen.Core;
using NexZen.Core.Entities;
using NexZen.Core.Interfaces;
using System;
using System.Linq;
using System.Threading;
using System.Net.Http;

namespace NexZen.Application.ViewModels;

public sealed record SavedPasswordAccount(int Id, string Origin, string Username);
public sealed record DecryptedSavedCredentials(string Origin, string Username, string Password);

public enum SavedPasswordMatch
{
    None,
    Identical,
    Different
}

public partial class MainViewModel : ObservableObject
{
    private readonly IBookmarkRepository _bookmarkRepository;
    private readonly IHistoryRepository _historyRepository;
    private readonly IDownloadRepository _downloadRepository;
    private readonly ISavedPasswordRepository _savedPasswordRepository;
    private readonly IPasswordEncryptionService _passwordEncryptionService;
    private readonly ISettingsService _settingsService;
    private readonly SemaphoreSlim _savedPasswordsGate = new(1, 1);

    [ObservableProperty]
    private ObservableCollection<Bookmark> _bookmarks = new();

    [ObservableProperty]
    private ObservableCollection<Bookmark> _filteredBookmarks = new();

    [ObservableProperty]
    private string _bookmarkSearchQuery = "";

    [ObservableProperty]
    private string _selectedSearchEngine = "Google";

    public string SearchEngineLabel => SearchTargets.DisplayName(SelectedSearchEngine);

    partial void OnSelectedSearchEngineChanged(string value)
    {
        OnPropertyChanged(nameof(SearchEngineLabel));
    }

    // --- Search Suggestion Properties ---
    [ObservableProperty]
    private ObservableCollection<string> _searchSuggestions = new();

    [ObservableProperty]
    private bool _isSearchSuggestionVisible = false;

    private readonly NexZen.Application.Services.SearchSuggestionService _searchSuggestionService = new();

    public async Task FetchSearchSuggestionsAsync(string query, CancellationToken token)
    {
        var suggestions = await _searchSuggestionService.GetSuggestionsAsync(query, token);
        if (!token.IsCancellationRequested)
        {
            SearchSuggestions.Clear();
            foreach (var s in suggestions)
            {
                SearchSuggestions.Add(s);
            }
            // Show if we have any results
            IsSearchSuggestionVisible = SearchSuggestions.Count > 0;
        }
    }
    // -------------------------------------

    partial void OnBookmarkSearchQueryChanged(string value)
    {
        FilterBookmarks();
    }

    private void FilterBookmarks()
    {
        if (string.IsNullOrWhiteSpace(BookmarkSearchQuery))
        {
            FilteredBookmarks = new ObservableCollection<Bookmark>(Bookmarks);
        }
        else
        {
            var lowerQuery = BookmarkSearchQuery.ToLowerInvariant();
            var filtered = Bookmarks.Where(b => 
                (b.Title != null && b.Title.ToLowerInvariant().Contains(lowerQuery)) ||
                (b.Url != null && b.Url.ToLowerInvariant().Contains(lowerQuery)));
            FilteredBookmarks = new ObservableCollection<Bookmark>(filtered);
        }
    }

    [ObservableProperty]
    private string _historySearchQuery = "";

    partial void OnHistorySearchQueryChanged(string value)
    {
        RebuildVisitHistory();
    }

    [ObservableProperty]
    private DateTime? _historySelectedDate;

    partial void OnHistorySelectedDateChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(HasHistoryDateFilter));
        RebuildVisitHistory();
    }

    public bool HasHistoryDateFilter => HistorySelectedDate.HasValue;

    [RelayCommand]
    private void ClearHistoryDate()
    {
        HistorySelectedDate = null;
    }

    [ObservableProperty]
    private ObservableCollection<VisitHistoryRow> _visitHistory = new();

    public bool HasVisitHistory => VisitHistory.Any(row => !row.IsHeader);

    public bool ShowEmptyHistory => _history.Count == 0;

    public bool ShowNoHistoryMatch => _history.Count > 0 && !HasVisitHistory;

    private readonly List<HistoryItem> _history = new();

    public ObservableCollection<ExtensionItemViewModel> Extensions { get; } = new();

    public bool HasExtensions => Extensions.Count > 0;

    [ObservableProperty]
    private ObservableCollection<DownloadItemViewModel> _downloads = new();

    [ObservableProperty]
    private ObservableCollection<SavedPasswordItemViewModel> _savedPasswords = new();

    public bool IsSavedPasswordListEmpty => SavedPasswords.Count == 0;

    partial void OnSavedPasswordsChanged(ObservableCollection<SavedPasswordItemViewModel> value) =>
        OnPropertyChanged(nameof(IsSavedPasswordListEmpty));

    [ObservableProperty]
    private DownloadItemViewModel? _activeDownload;

    public ConfirmPromptViewModel ConfirmPrompt { get; } = new();

    public bool IsAnyDownloadDialogVisible => Downloads.Any(d => d.IsDialogVisible);

    public void UpdateDialogVisibility()
    {
        OnPropertyChanged(nameof(IsAnyDownloadDialogVisible));
    }

    private static readonly HttpClient s_httpClient = new HttpClient();

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TestDownloadAsync(string? fileType)
    {
        string url = fileType switch 
        {
            "image" => "https://upload.wikimedia.org/wikipedia/commons/4/47/PNG_transparency_demonstration_1.png",
            "video" => "http://commondatastorage.googleapis.com/gtv-videos-bucket/sample/BigBuckBunny.mp4",
            "exe" => "https://github.com/git-for-windows/git/releases/download/v2.42.0.windows.2/Git-2.42.0.2-64-bit.exe",
            "zip" => "https://github.com/AvaloniaUI/Avalonia/archive/refs/heads/master.zip",
            "pdf" => "https://www.w3.org/WAI/ER/tests/xhtml/testfiles/resources/pdf/dummy.pdf",
            _ => "https://speed.hetzner.de/100MB.bin"
        };

        await StartDownloadFromUrlAsync(url);
    }

    public static string NexZenDownloadsFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NexZenDownloads");

    public static string AllocateDownloadPath(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename))
            filename = "download_" + Guid.NewGuid().ToString("N")[..4] + ".bin";
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            filename = filename.Replace(c, '_');

        System.IO.Directory.CreateDirectory(NexZenDownloadsFolder);
        var filePath = System.IO.Path.Combine(NexZenDownloadsFolder, filename);
        if (System.IO.File.Exists(filePath))
        {
            filePath = System.IO.Path.Combine(
                NexZenDownloadsFolder,
                System.IO.Path.GetFileNameWithoutExtension(filename) + "_" + Guid.NewGuid().ToString("N")[..4] + System.IO.Path.GetExtension(filename));
        }
        return filePath;
    }

    public DownloadItemViewModel CreateDownloadItem(string url, string filename, string filePath)
    {
        if (Downloads.Any(d => d.IsDialogVisible))
        {
            foreach (var existing in Downloads.Where(d => d.IsDialogVisible))
                existing.IsDialogMinimized = true;
        }

        var download = new DownloadItemViewModel
        {
            FileName = System.IO.Path.GetFileName(filePath),
            Url = url,
            FilePath = filePath,
            TotalBytes = 0,
            DownloadedBytes = 0,
            Progress = 0,
            State = "Downloading",
            Timestamp = DateTime.Now,
            IsDialogMinimized = Downloads.Any(d => d.IsDialogVisible),
            OnDialogStateChanged = UpdateDialogVisibility
        };

        Downloads.Insert(0, download);
        ActiveDownload = download;
        UpdateDialogVisibility();

        var record = new DownloadRecord
        {
            Id = download.Id,
            FileName = download.FileName,
            Url = download.Url,
            FilePath = download.FilePath,
            TotalBytes = download.TotalBytes,
            DownloadedBytes = download.DownloadedBytes,
            State = download.State,
            Timestamp = DateTime.Now
        };
        if (!IsIncognito)
            _ = _downloadRepository.AddAsync(record);

        download.PropertyChanged += async (s, e) =>
        {
            if (e.PropertyName == nameof(download.State))
                await UpdateDownloadRecordAsync(download);
        };
        return download;
    }

    public async Task StartDownloadFromUrlAsync(string url, string? suggestedName = null)
    {
        string filename = suggestedName ?? string.Empty;
        if (string.IsNullOrEmpty(filename))
        {
            try { filename = System.IO.Path.GetFileName(new Uri(url).LocalPath); }
            catch { filename = string.Empty; }
        }
        var filePath = AllocateDownloadPath(filename);
        var download = CreateDownloadItem(url, System.IO.Path.GetFileName(filePath), filePath);
        download.OnResumeRequested = d => { _ = StartRealDownloadAsync(d); };
        await StartRealDownloadAsync(download);
    }

    private async Task StartRealDownloadAsync(DownloadItemViewModel download)
    {
        try
        {
            download.Cts = new CancellationTokenSource();
            
            using var request = new HttpRequestMessage(HttpMethod.Get, download.Url);
            long existingLength = 0;

            if (System.IO.File.Exists(download.FilePath))
            {
                existingLength = new System.IO.FileInfo(download.FilePath).Length;
                if (existingLength > 0 && download.State != "Completed")
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);
                }
            }

            using var response = await s_httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, download.Cts.Token);
            
            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // File already downloaded or range error. 
                download.State = "Completed";
                await Task.Delay(2000);
                download.IsDialogVisible = false;
                return;
            }
            
            response.EnsureSuccessStatusCode();

            if (download.TotalBytes == 0)
            {
                var contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue)
                {
                    // If it's a 206 Partial Content, contentLength is just the remaining bytes
                    if (response.StatusCode == System.Net.HttpStatusCode.PartialContent)
                    {
                        download.TotalBytes = existingLength + contentLength.Value;
                    }
                    else
                    {
                        download.TotalBytes = contentLength.Value;
                        existingLength = 0; // Server didn't respect range, overwrite
                        if (System.IO.File.Exists(download.FilePath))
                            System.IO.File.Delete(download.FilePath);
                    }
                }
            }

            download.DownloadedBytes = existingLength;

            using var stream = await response.Content.ReadAsStreamAsync(download.Cts.Token);
            using var fileStream = new System.IO.FileStream(download.FilePath, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.None, 8192, true);

            var buffer = new byte[8192];
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, download.Cts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, download.Cts.Token);
                download.DownloadedBytes += bytesRead;
                if (download.TotalBytes > 0)
                {
                    download.Progress = (int)((double)download.DownloadedBytes / download.TotalBytes * 100);
                }
            }

            download.State = "Completed";
            await UpdateDownloadRecordAsync(download);

            await Task.Delay(2000);
            download.IsDialogVisible = false;
        }
        catch (OperationCanceledException)
        {
            if (download.State == "Cancelled")
            {
                if (System.IO.File.Exists(download.FilePath))
                {
                    System.IO.File.Delete(download.FilePath);
                }
                await UpdateDownloadRecordAsync(download);

                await Task.Delay(2000);
                download.IsDialogVisible = false;
            }
        }
        catch (Exception)
        {
            if (download.State != "Cancelled" && download.State != "Paused")
            {
                download.State = "Error";
                await UpdateDownloadRecordAsync(download);
                
                await Task.Delay(2000);
                download.IsDialogVisible = false;
            }
        }
    }

    private async Task UpdateDownloadRecordAsync(DownloadItemViewModel download)
    {
        if (IsIncognito) return;
        var record = await _downloadRepository.GetByIdAsync(download.Id);
        if (record != null)
        {
            record.TotalBytes = download.TotalBytes;
            record.DownloadedBytes = download.DownloadedBytes;
            record.State = download.State;
            await _downloadRepository.UpdateAsync(record);
        }
    }

    private async Task LoadDownloadsAsync()
    {
        var records = await _downloadRepository.GetAllAsync();
        Downloads.Clear();
        foreach (var record in records.OrderByDescending(r => r.Timestamp))
        {
            var download = new DownloadItemViewModel
            {
                Id = record.Id,
                FileName = record.FileName,
                Url = record.Url,
                FilePath = record.FilePath,
                TotalBytes = record.TotalBytes,
                DownloadedBytes = record.DownloadedBytes,
                Progress = record.TotalBytes > 0 ? (int)((double)record.DownloadedBytes / record.TotalBytes * 100) : 0,
                State = record.State == "Downloading" ? "Paused" : record.State,
                Timestamp = record.Timestamp,
                IsDialogVisible = false,
                IsDialogMinimized = false,
                OnDialogStateChanged = UpdateDialogVisibility
            };

            download.OnResumeRequested = d => { _ = StartRealDownloadAsync(d); };
            
            download.PropertyChanged += async (s, e) =>
            {
                if (e.PropertyName == nameof(download.State))
                {
                    await UpdateDownloadRecordAsync(download);
                }
            };

            Downloads.Add(download);
        }
    }

    [ObservableProperty]
    private string _currentUrl = "https://example.com";

    partial void OnCurrentUrlChanged(string value)
    {
        CheckIfCurrentUrlIsBookmarked();
    }

    [ObservableProperty]
    private bool _isCurrentUrlBookmarked = false;

    private void CheckIfCurrentUrlIsBookmarked()
    {
        if (Bookmarks == null || string.IsNullOrEmpty(CurrentUrl)) return;
        IsCurrentUrlBookmarked = Bookmarks.Any(b => string.Equals(b.Url, CurrentUrl, StringComparison.OrdinalIgnoreCase));
    }

    [ObservableProperty]
    private ObservableCollection<TabItemViewModel> _tabs = new();

    [ObservableProperty]
    private TabItemViewModel? _activeTab;

    [ObservableProperty]
    private string _tabBackgroundColor = "#b9b9b9";

    partial void OnTabBackgroundColorChanged(string value)
    {
        if (_settingsService != null)
        {
            _settingsService.TabBackgroundColor = value;
            _settingsService.Save();
        }
    }

    [ObservableProperty]
    private string? _tabBackgroundImagePath;

    partial void OnTabBackgroundImagePathChanged(string? value)
    {
        if (_settingsService != null)
        {
            _settingsService.TabBackgroundImagePath = value;
            _settingsService.Save();
        }
    }

    [ObservableProperty]
    private int _tabColumns = 0;

    partial void OnTabColumnsChanged(int value)
    {
        if (_settingsService != null)
        {
            _settingsService.TabColumns = value;
            _settingsService.Save();
        }
    }

    [ObservableProperty]
    private double _tabManagerWidth = 320;

    [RelayCommand]
    public void SetTabColumns(string columnsStr)
    {
        if (int.TryParse(columnsStr, out int cols))
        {
            TabColumns = cols;
            if (cols == 1) TabManagerWidth = 180;
            else if (cols == 2) TabManagerWidth = 320;
            else if (cols == 3) TabManagerWidth = 460;
            else TabManagerWidth = 320;
        }
    }

    [RelayCommand]
    public void SetSearchEngine(string engine)
    {
        SelectedSearchEngine = engine;
        _settingsService.SearchEngine = engine;
        _settingsService.Save();
    }

    // Events to notify the UI to do View-specific browser setup/teardown
    public event EventHandler<TabItemViewModel>? TabAdded;
    public event EventHandler<string>? TabClosed;
    public event EventHandler<string>? TabSwitched;

    private const int ClosedTabLimit = 25;
    private readonly List<ClosedTab> _closedTabs = new();

    [ObservableProperty]
    private bool _canReopenClosedTab;

    [ObservableProperty]
    private string _reopenClosedTabTip = "No closed tab to reopen";

    private readonly record struct ClosedTab(string Url, string Title);

    public NexShareViewModel NexShare { get; }

    public AiFunctionViewModel AiFunction { get; }

    public MainViewModel(IBookmarkRepository bookmarkRepository, IHistoryRepository historyRepository, IDownloadRepository downloadRepository, ISavedPasswordRepository savedPasswordRepository, IPasswordEncryptionService passwordEncryptionService, ISettingsService settingsService, NexShareViewModel nexShareViewModel, AiFunctionViewModel aiFunctionViewModel, bool subscribeToNexShareRequests = true)
    {
        _bookmarkRepository = bookmarkRepository;
        _historyRepository = historyRepository;
        _downloadRepository = downloadRepository;
        _savedPasswordRepository = savedPasswordRepository;
        _passwordEncryptionService = passwordEncryptionService;
        _settingsService = settingsService;
        NexShare = nexShareViewModel;
        AiFunction = aiFunctionViewModel;
        AiFunction.Attach(this);
        if (subscribeToNexShareRequests)
        {
            NexShare.OnDownloadRequested += (s, args) =>
            {
                _ = StartDownloadFromUrlAsync(args.Url, args.FileName);
            };
            NexShare.OnOpenUrlRequested += (s, url) =>
            {
                AddTab(url);
            };
        }

        // Load settings
        TabBackgroundColor = _settingsService.TabBackgroundColor;
        TabBackgroundImagePath = _settingsService.TabBackgroundImagePath;
        SetTabColumns(_settingsService.TabColumns.ToString());
        SelectedSearchEngine = _settingsService.SearchEngine;

        Extensions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasExtensions));
    }

    private Task? _initialLoad;

    /// <summary>Incognito window: visits and downloads are not written to the database.</summary>
    public bool IsIncognito { get; set; }

    /// <summary>
    /// Reads bookmarks, downloads, history and saved passwords once. The main window starts it right
    /// after it is shown: on the UI thread these reads took ~0.3–0.5 s and used to delay the window.
    /// </summary>
    public Task EnsureInitialDataLoadedAsync() => _initialLoad ??= LoadInitialDataAsync();

    private async Task LoadInitialDataAsync()
    {
        await LoadBookmarksAsync();
        await LoadDownloadsAsync();
        await LoadHistoryAsync();
        await LoadSavedPasswordsAsync();
    }

    public async Task<bool> HasSavedPasswordAsync(string origin, string username)
    {
        return await MatchSavedPasswordAsync(origin, username, password: null) != SavedPasswordMatch.None;
    }

    public async Task<SavedPasswordMatch> MatchSavedPasswordAsync(string origin, string username, string? password)
    {
        var normalized = NormalizeCredentialOrigin(origin);
        username = username?.Trim() ?? string.Empty;
        if (normalized == null || username.Length == 0)
            return SavedPasswordMatch.None;

        await _savedPasswordsGate.WaitAsync();
        try
        {
            var existing = await FindSavedPasswordUnsafeAsync(normalized, username);
            if (existing == null)
                return SavedPasswordMatch.None;
            if (string.IsNullOrEmpty(password))
                return SavedPasswordMatch.Identical;
            try
            {
                var stored = _passwordEncryptionService.Decrypt(existing.EncryptedPassword);
                return string.Equals(stored, password, StringComparison.Ordinal)
                    ? SavedPasswordMatch.Identical
                    : SavedPasswordMatch.Different;
            }
            catch
            {
                return SavedPasswordMatch.Identical;
            }
        }
        finally
        {
            _savedPasswordsGate.Release();
        }
    }

    private async Task<SavedPassword?> FindSavedPasswordUnsafeAsync(string normalizedOrigin, string username)
    {
        var exact = await _savedPasswordRepository.FindAsync(normalizedOrigin, username);
        if (exact != null)
            return exact;

        var saved = await _savedPasswordRepository.GetAllAsync();
        return saved.FirstOrDefault(item =>
            string.Equals(NormalizeCredentialOrigin(item.Origin), normalizedOrigin, StringComparison.Ordinal)
            && string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
    }

    public async Task SavePasswordAsync(string origin, string username, string password, Action<string>? reportStage = null)
    {
        var normalized = NormalizeCredentialOrigin(origin);
        username = username.Trim();
        if (normalized == null)
            throw new ArgumentException("The password origin must be an absolute HTTP or HTTPS URL.", nameof(origin));
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("The username is empty.", nameof(username));
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("The password is empty.", nameof(password));

        await _savedPasswordsGate.WaitAsync();
        try
        {
            reportStage?.Invoke("database-lookup-started");
            var existing = await FindSavedPasswordUnsafeAsync(normalized, username);
            reportStage?.Invoke("database-lookup-completed");
            if (existing == null)
            {
                existing = new SavedPassword
                {
                    Origin = normalized,
                    Username = username,
                    CreatedAt = DateTime.UtcNow
                };
            }
            reportStage?.Invoke("encryption-started");
            existing.EncryptedPassword = await Task.Run(() => _passwordEncryptionService.Encrypt(password));
            reportStage?.Invoke("encryption-completed");
            existing.UpdatedAt = DateTime.UtcNow;
            reportStage?.Invoke("database-write-started");
            await _savedPasswordRepository.SaveAsync(existing);
            reportStage?.Invoke("database-write-completed");

            var saved = await _savedPasswordRepository.FindAsync(normalized, username);
            if (saved == null || saved.Id == 0)
                throw new InvalidOperationException("The saved password could not be read back from the database.");
            reportStage?.Invoke("database-verification-completed");

            await ReloadSavedPasswordsUnsafeAsync();
            reportStage?.Invoke("list-reload-completed");
        }
        finally
        {
            _savedPasswordsGate.Release();
        }
    }

    public async Task<IReadOnlyList<SavedPasswordAccount>> GetSavedPasswordAccountsAsync(string origin)
    {
        var normalized = NormalizeCredentialOrigin(origin);
        if (normalized == null)
            return Array.Empty<SavedPasswordAccount>();

        await _savedPasswordsGate.WaitAsync();
        try
        {
            var saved = await _savedPasswordRepository.GetAllAsync();
            return saved
                .Where(password => string.Equals(NormalizeCredentialOrigin(password.Origin), normalized, StringComparison.Ordinal))
                .OrderBy(password => password.Username, StringComparer.OrdinalIgnoreCase)
                .Select(password => new SavedPasswordAccount(password.Id, normalized, password.Username))
                .ToArray();
        }
        finally
        {
            _savedPasswordsGate.Release();
        }
    }

    public async Task<DecryptedSavedCredentials?> GetSavedCredentialsAsync(int id, string expectedOrigin)
    {
        var normalized = NormalizeCredentialOrigin(expectedOrigin);
        if (normalized == null)
            return null;

        await _savedPasswordsGate.WaitAsync();
        try
        {
            var saved = await _savedPasswordRepository.GetByIdAsync(id);
            if (saved == null || !string.Equals(NormalizeCredentialOrigin(saved.Origin), normalized, StringComparison.Ordinal))
                return null;

            var password = await Task.Run(() => _passwordEncryptionService.Decrypt(saved.EncryptedPassword));
            return new DecryptedSavedCredentials(normalized, saved.Username, password);
        }
        finally
        {
            _savedPasswordsGate.Release();
        }
    }

    public async Task DeleteSavedPasswordAsync(int id)
    {
        await _savedPasswordsGate.WaitAsync();
        try
        {
            await _savedPasswordRepository.DeleteAsync(id);
            var item = SavedPasswords.FirstOrDefault(password => password.Id == id);
            if (item != null)
            {
                SavedPasswords.Remove(item);
                OnPropertyChanged(nameof(IsSavedPasswordListEmpty));
            }
        }
        finally
        {
            _savedPasswordsGate.Release();
        }
    }

    public async Task ToggleSavedPasswordVisibilityAsync(int id)
    {
        var item = SavedPasswords.FirstOrDefault(password => password.Id == id);
        if (item != null)
            await item.ToggleVisibilityAsync();
    }

    public void HideRevealedPasswords()
    {
        foreach (var password in SavedPasswords)
            password.Hide();
    }

    public void CancelActiveDownloadsForDataDeletion()
    {
        foreach (var download in Downloads.Where(item => item.State == "Downloading").ToList())
        {
            download.State = "Cancelled";
            download.Cts?.Cancel();
        }
    }

    public void ClearAllStoredDataFromMemory()
    {
        HideRevealedPasswords();
        Bookmarks.Clear();
        FilteredBookmarks.Clear();
        IsCurrentUrlBookmarked = false;
        _history.Clear();
        RebuildVisitHistory();
        foreach (var download in Downloads)
        {
            download.Cts?.Cancel();
            download.IsDialogVisible = false;
        }
        Downloads.Clear();
        ActiveDownload = null;
        SavedPasswords = new ObservableCollection<SavedPasswordItemViewModel>();
    }

    private async Task LoadSavedPasswordsAsync()
    {
        await _savedPasswordsGate.WaitAsync();
        try
        {
            await ReloadSavedPasswordsUnsafeAsync();
        }
        finally
        {
            _savedPasswordsGate.Release();
        }
    }

    private async Task ReloadSavedPasswordsUnsafeAsync()
    {
        var saved = await _savedPasswordRepository.GetAllAsync();
        SavedPasswords = new ObservableCollection<SavedPasswordItemViewModel>(
            saved.Select(password => new SavedPasswordItemViewModel(password, _passwordEncryptionService)));
    }

    private static string? NormalizeCredentialOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    public async Task RecordVisitAsync(string? title, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || IsIncognito)
            return;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return;

        var cleanTitle = string.IsNullOrWhiteSpace(title) ? url : title.Trim();
        var newest = _history.FirstOrDefault();
        if (newest != null
            && string.Equals(newest.Url, url, StringComparison.OrdinalIgnoreCase)
            && newest.VisitedAt > DateTime.UtcNow.AddMinutes(-30))
        {
            newest.Title = cleanTitle;
            newest.VisitedAt = DateTime.UtcNow;
            await _historyRepository.UpdateAsync(newest);
        }
        else
        {
            var item = new HistoryItem
            {
                Title = cleanTitle,
                Url = url,
                VisitedAt = DateTime.UtcNow
            };
            await _historyRepository.AddAsync(item);
            _history.Insert(0, item);
        }

        RebuildVisitHistory();
    }

    [RelayCommand]
    private async Task DeleteVisitAsync(int id)
    {
        await _historyRepository.DeleteAsync(id);
        _history.RemoveAll(item => item.Id == id);
        RebuildVisitHistory();
    }

    public List<string> HistoryUrlsMatching(DateTime? sinceUtc, string? host)
    {
        return _history
            .Where(item => DataMatch.InRange(item.VisitedAt, storedIsUtc: true, sinceUtc)
                && DataMatch.UrlMatchesHost(item.Url, host))
            .Select(item => item.Url)
            .ToList();
    }

    public async Task<int> DeleteHistoryMatchingAsync(DateTime? sinceUtc, string? host)
    {
        var count = await _historyRepository.DeleteMatchingAsync(sinceUtc, host);
        _history.RemoveAll(item =>
            DataMatch.InRange(item.VisitedAt, storedIsUtc: true, sinceUtc)
            && DataMatch.UrlMatchesHost(item.Url, host));
        RebuildVisitHistory();
        return count;
    }

    public async Task<int> DeleteDownloadHistoryMatchingAsync(DateTime? sinceUtc, string? host)
    {
        var keep = Downloads
            .Where(item => item.State == "Downloading")
            .Select(item => item.Id)
            .ToList();
        var count = await _downloadRepository.DeleteMatchingAsync(sinceUtc, host, keep);
        var remove = Downloads.Where(item =>
            item.State != "Downloading"
            && DataMatch.InRange(item.Timestamp, storedIsUtc: false, sinceUtc)
            && DataMatch.UrlMatchesHost(item.Url, host)).ToList();
        foreach (var item in remove)
            Downloads.Remove(item);
        if (ActiveDownload != null && remove.Contains(ActiveDownload))
            ActiveDownload = null;
        if (remove.Count > 0)
            UpdateDialogVisibility();
        return count;
    }

    private async Task LoadHistoryAsync()
    {
        var items = await _historyRepository.GetAllAsync();
        _history.Clear();
        _history.AddRange(items);
        RebuildVisitHistory();
    }

    private void RebuildVisitHistory()
    {
        var query = HistorySearchQuery?.Trim() ?? "";
        var rows = new ObservableCollection<VisitHistoryRow>();
        DateTime? currentDay = null;
        foreach (var item in _history.OrderByDescending(entry => entry.VisitedAt))
        {
            if (!MatchesHistoryQuery(item, query))
                continue;

            var local = item.VisitedAt.ToLocalTime();
            if (HistorySelectedDate is DateTime selectedDay && local.Date != selectedDay.Date)
                continue;
            if (currentDay != local.Date)
            {
                currentDay = local.Date;
                rows.Add(VisitHistoryRow.ForHeader(FormatHistoryDay(local.Date)));
            }

            rows.Add(VisitHistoryRow.ForVisit(item.Id, item.Title, item.Url, local.ToString("HH:mm")));
        }

        VisitHistory = rows;
        OnPropertyChanged(nameof(HasVisitHistory));
        OnPropertyChanged(nameof(ShowEmptyHistory));
        OnPropertyChanged(nameof(ShowNoHistoryMatch));
    }

    private static bool MatchesHistoryQuery(HistoryItem item, string query)
    {
        if (string.IsNullOrEmpty(query))
            return true;

        if (item.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        return Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)
            && uri.Host.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatHistoryDay(DateTime day)
    {
        if (day == DateTime.Today)
            return "Today";
        if (day == DateTime.Today.AddDays(-1))
            return "Yesterday";
        return day.ToString("MMMM d, yyyy");
    }

    [RelayCommand]
    private async Task LoadBookmarksAsync()
    {
        var bookmarks = await _bookmarkRepository.GetAllAsync();
        Bookmarks.Clear();
        foreach (var bookmark in bookmarks)
        {
            Bookmarks.Add(bookmark);
        }
        FilterBookmarks();
        CheckIfCurrentUrlIsBookmarked();
    }

    [RelayCommand]
    private async Task ToggleBookmarkAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentUrl)) return;

        var existing = Bookmarks.FirstOrDefault(b => string.Equals(b.Url, CurrentUrl, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            await _bookmarkRepository.DeleteAsync(existing.Id);
            Bookmarks.Remove(existing);
        }
        else
        {
            var bookmark = new Bookmark
            {
                Title = ActiveTab?.Title ?? "New Bookmark",
                Url = CurrentUrl
            };
            await _bookmarkRepository.AddAsync(bookmark);
            Bookmarks.Add(bookmark);
        }
        FilterBookmarks();
        CheckIfCurrentUrlIsBookmarked();
    }

    [RelayCommand]
    private async Task DeleteBookmarkAsync(int id)
    {
        var existing = Bookmarks.FirstOrDefault(b => b.Id == id);
        if (existing != null)
        {
            await _bookmarkRepository.DeleteAsync(id);
            Bookmarks.Remove(existing);
            FilterBookmarks();
            CheckIfCurrentUrlIsBookmarked();
        }
    }

    [RelayCommand]
    public void AddTab(string url = "https://example.com")
    {
        var id = Guid.NewGuid().ToString();
        var newTab = new TabItemViewModel(id, url, "New Tab");
        Tabs.Add(newTab);
        
        TabAdded?.Invoke(this, newTab);
        SwitchTab(id);
    }

    [RelayCommand]
    public void CloseTab(string tabId)
    {
        if (Tabs.Count <= 1)
        {
            // Don't close the last tab, or perhaps add a new one first
            return;
        }

        var tabToClose = Tabs.FirstOrDefault(t => t.Id == tabId);
        if (tabToClose != null)
        {
            RememberClosedTab(tabToClose);
            int index = Tabs.IndexOf(tabToClose);
            Tabs.Remove(tabToClose);
            TabClosed?.Invoke(this, tabId);

            if (ActiveTab?.Id == tabId)
            {
                // Switch to adjacent tab
                var newIndex = Math.Max(0, index - 1);
                SwitchTab(Tabs[newIndex].Id);
            }
        }
    }

    [RelayCommand]
    public void SwitchTab(string tabId)
    {
        var newActiveTab = Tabs.FirstOrDefault(t => t.Id == tabId);
        if (newActiveTab != null && newActiveTab != ActiveTab)
        {
            if (ActiveTab != null)
                ActiveTab.IsActive = false;
                
            ActiveTab = newActiveTab;
            ActiveTab.IsActive = true;
            CurrentUrl = ActiveTab.Url;
            
            TabSwitched?.Invoke(this, tabId);
        }
    }

    public void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0)
            return;

        var closed = _closedTabs[0];
        _closedTabs.RemoveAt(0);
        RefreshReopenState();
        AddTab(closed.Url);
        if (ActiveTab != null)
            ActiveTab.Title = closed.Title;
    }

    private void RememberClosedTab(TabItemViewModel tab)
    {
        var url = (tab.Url ?? "").Trim();
        if (url.Length == 0 || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
            return;

        var title = string.IsNullOrWhiteSpace(tab.Title) || tab.Title == "New Tab" ? url : tab.Title;
        _closedTabs.Insert(0, new ClosedTab(url, title));
        if (_closedTabs.Count > ClosedTabLimit)
            _closedTabs.RemoveAt(_closedTabs.Count - 1);
        RefreshReopenState();
    }

    private void RefreshReopenState()
    {
        CanReopenClosedTab = _closedTabs.Count > 0;
        ReopenClosedTabTip = _closedTabs.Count == 0
            ? "No closed tab to reopen"
            : _closedTabs[0].Title;
    }
}
