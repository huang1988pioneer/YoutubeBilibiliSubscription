using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YoutubeSubscription.Models;
using YoutubeSubscription.Services;

namespace YoutubeSubscription.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly YouTubeAuthService _auth;
    private readonly FileDialogService _fileDialogs;
    private readonly YouTubeWebClient _web = new();
    private YouTubeSubscriptionService? _subscriptions;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _authCts;
    private bool _usingWebList;

    public ObservableCollection<SubscriptionChannel> Channels { get; } = [];

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "請先選擇 client_secrets.json，再按「登入 / 授權」。";

    [ObservableProperty]
    public partial string CredentialsPathText { get; set; } = "尚未設定 client_secrets.json";

    [ObservableProperty]
    public partial bool HasClientSecrets { get; set; }

    [ObservableProperty]
    public partial long TotalCount { get; set; }

    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>True while the Google OAuth browser flow is waiting for a callback.</summary>
    [ObservableProperty]
    public partial bool IsAuthenticating { get; set; }

    [ObservableProperty]
    public partial bool IsAuthenticated { get; set; }

    [ObservableProperty]
    public partial bool HasWebSession { get; set; }

    [ObservableProperty]
    public partial string WebLoadError { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DiscoverySummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConfirmUnsubscribeVisible { get; set; }

    /// <summary>全選後取消訂閱的第二次確認（更強警告）。</summary>
    [ObservableProperty]
    public partial bool IsConfirmUnsubscribeSecondVisible { get; set; }

    /// <summary>全選後在瀏覽器開啟的確認。</summary>
    [ObservableProperty]
    public partial bool IsConfirmOpenBrowserVisible { get; set; }

    [ObservableProperty]
    public partial string ConfirmMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmSecondMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmOpenBrowserMessage { get; set; } = string.Empty;

    /// <summary>Channels matching the filter (bound to list).</summary>
    public ObservableCollection<SubscriptionChannel> FilteredChannels { get; } = [];

    /// <summary>
    /// Sort options matching YouTube Data API / youtube.com/feed/channels.
    /// Changing selection re-fetches so order matches the server (not local only).
    /// </summary>
    public IReadOnlyList<SortOptionItem> SortOptions { get; } =
    [
        new(SubscriptionSortMode.Relevance, "相關度（YouTube 預設）"),
        new(SubscriptionSortMode.Activity, "依最新活動"),
        new(SubscriptionSortMode.Alphabetical, "名稱 A–Z"),
    ];

    [ObservableProperty]
    public partial SortOptionItem? SelectedSortOption { get; set; }

    /// <summary>Avoid re-fetch when binding initially sets SelectedSortOption.</summary>
    private bool _suppressSortReload;

    public string TotalCountText =>
        TotalCount > Channels.Count
            ? $"已訂閱 · {Channels.Count}（API 回報 {TotalCount}）"
            : $"已訂閱 · {TotalCount}";

    public string SelectedCountText => $"已勾選：{SelectedCount}";

    public bool HasDiscoverySummary => !string.IsNullOrWhiteSpace(DiscoverySummary);

    public bool HasApiCountMismatch => IsAuthenticated && TotalCount > Channels.Count;

    public string ApiCountMismatchText =>
        HasApiCountMismatch
            ? $"YouTube API 回報共有 {TotalCount} 個訂閱，但實際只取得 {Channels.Count} 筆頻道列。"
            : string.Empty;

    public bool ShowSetupState => !IsAuthenticated;

    public bool ShowAuthenticatedEmptyState =>
        IsAuthenticated && FilteredChannels.Count == 0;

    public string AuthenticatedEmptyTitle =>
        Channels.Count == 0
            ? (!string.IsNullOrEmpty(WebLoadError)
                ? "無法載入網站訂閱"
                : TotalCount > 0 ? "有訂閱數，但沒有頻道列" : "目前沒有訂閱")
            : "沒有符合搜尋的頻道";

    public string AuthenticatedEmptyBody =>
        Channels.Count == 0
            ? !string.IsNullOrEmpty(WebLoadError)
                ? WebLoadError
                : TotalCount > 0
                    ? $"YouTube Data API 回報 {TotalCount} 個訂閱，但沒有回傳頻道列。程式會改從已登入的 Chrome 讀取 youtube.com/feed/channels。"
                    : HasWebSession
                        ? "已連上網站 session，但 feed/channels 沒有頻道列。"
                        : "這個帳號目前沒有可列出的訂閱頻道。"
            : "試試清空搜尋，或換一個關鍵字。";

    public MainViewModel() : this(new YouTubeAuthService(), new FileDialogService())
    {
    }

    public MainViewModel(YouTubeAuthService auth, FileDialogService fileDialogs)
    {
        _auth = auth;
        _fileDialogs = fileDialogs;
        _suppressSortReload = true;
        SelectedSortOption = SortOptions[0]; // relevance = youtube.com/feed/channels default
        _suppressSortReload = false;
        Channels.CollectionChanged += OnChannelsCollectionChanged;
        RefreshCredentialsStatus();
        _ = TryRestoreSessionAsync();
    }

    public FileDialogService FileDialogs => _fileDialogs;

    public void RefreshCredentialsStatus()
    {
        var path = _auth.FindClientSecretsPath();
        HasClientSecrets = path is not null;
        CredentialsPathText = path is null
            ? "尚未設定 client_secrets.json（請按「選擇憑證檔」）"
            : $"憑證：{path}";

        if (!IsAuthenticated)
        {
            StatusMessage =
                path is null
                    ? "請先選擇 client_secrets.json，再按「登入 / 授權」。之後會用 refresh token 自動登入。"
                    : YouTubeAuthService.HasStoredRefreshToken
                        ? "正在還原上次的 Google 登入…"
                        : "請按「登入 / 授權」。只需授權一次，之後會自動還原。";
        }
    }

    private async Task TryRestoreSessionAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            if (YouTubeAuthService.HasStoredRefreshToken && _auth.FindClientSecretsPath() is not null)
            {
                StatusMessage = "正在還原 Google 登入…";
                var service = await _auth.TryRestoreAsync();
                if (service is not null)
                {
                    _subscriptions = new YouTubeSubscriptionService(service);
                    IsAuthenticated = true;
                }
            }

            await Task.Run(TryAttachBrowserSession);

            if (IsAuthenticated || HasWebSession)
            {
                IsAuthenticated = true;
                StatusMessage = "登入已還原，正在載入訂閱…";
                await LoadSubscriptionsCoreAsync();
            }
            else
            {
                RefreshCredentialsStatus();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"還原登入失敗：{ex.Message}";
            RefreshCredentialsStatus();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Attach a website session from Chrome (or a previously saved one).
    /// Not a user-facing cookie import.
    /// </summary>
    private bool TryAttachBrowserSession()
    {
        if (HasWebSession)
            return true;

        var stored = YouTubeWebCredentialStore.Load();
        if (stored?.LooksSignedIn == true)
        {
            _web.ApplyCredential(stored);
            HasWebSession = true;
            return true;
        }

        if (stored is not null)
            YouTubeWebCredentialStore.Clear();

        StatusMessage = "正在從 Chrome 讀取 YouTube 登入…";
        var chrome = ChromeYouTubeCookieReader.TryRead();
        if (chrome is null)
            return false;

        YouTubeWebCredentialStore.Save(chrome);
        _web.ApplyCredential(chrome);
        HasWebSession = true;
        return true;
    }

    partial void OnTotalCountChanged(long value)
    {
        OnPropertyChanged(nameof(TotalCountText));
        NotifyListSummary();
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedCountText));
        OnPropertyChanged(nameof(AuthenticatedEmptyBody));
    }

    partial void OnIsAuthenticatedChanged(bool value) => NotifyListSummary();

    partial void OnWebLoadErrorChanged(string value) => NotifyListSummary();

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnDiscoverySummaryChanged(string value) =>
        OnPropertyChanged(nameof(HasDiscoverySummary));

    partial void OnSelectedSortOptionChanged(SortOptionItem? value)
    {
        if (_suppressSortReload || value is null || !IsAuthenticated)
            return;

        if (_usingWebList)
        {
            ApplyFilter();
            return;
        }

        if (_subscriptions is null)
            return;

        _ = ReloadWithCurrentSortAsync();
    }

    private async Task ReloadWithCurrentSortAsync()
    {
        if (IsBusy || (!_usingWebList && _subscriptions is null))
            return;

        IsBusy = true;
        try
        {
            await LoadSubscriptionsCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnChannelsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (SubscriptionChannel item in e.OldItems)
                item.PropertyChanged -= OnChannelPropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (SubscriptionChannel item in e.NewItems)
                item.PropertyChanged += OnChannelPropertyChanged;
        }

        RecalculateSelectedCount();
        ApplyFilter();
        NotifyListSummary();
    }

    private void NotifyListSummary()
    {
        OnPropertyChanged(nameof(TotalCountText));
        OnPropertyChanged(nameof(HasApiCountMismatch));
        OnPropertyChanged(nameof(ApiCountMismatchText));
        OnPropertyChanged(nameof(ShowSetupState));
        OnPropertyChanged(nameof(ShowAuthenticatedEmptyState));
        OnPropertyChanged(nameof(AuthenticatedEmptyTitle));
        OnPropertyChanged(nameof(AuthenticatedEmptyBody));
        OnPropertyChanged(nameof(WebLoadError));
    }

    private void OnChannelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubscriptionChannel.IsSelected))
            RecalculateSelectedCount();
    }

    private void RecalculateSelectedCount() =>
        SelectedCount = Channels.Count(c => c.IsSelected);

    private void ApplyFilter()
    {
        var q = (FilterText ?? string.Empty).Trim();

        IEnumerable<SubscriptionChannel> source = Channels;
        var mode = SelectedSortOption?.Mode ?? SubscriptionSortMode.Relevance;
        source = mode == SubscriptionSortMode.Alphabetical
            ? source
                .OrderBy(c => c.IsMembership ? 0 : 1)
                .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            : source.OrderBy(c => c.SortIndex);

        if (!string.IsNullOrEmpty(q))
        {
            source = source.Where(c =>
                c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.CustomUrl.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.ChannelId.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.MetaLine.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.SectionTitle.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var list = source.ToList();
        string? lastSection = null;
        foreach (var ch in list)
        {
            ch.ShowSectionHeader = !string.Equals(ch.SectionTitle, lastSection, StringComparison.Ordinal);
            lastSection = ch.SectionTitle;
        }

        FilteredChannels.Clear();
        foreach (var ch in list)
            FilteredChannels.Add(ch);

        NotifyListSummary();
    }

    [RelayCommand]
    private async Task PickClientSecretsAsync()
    {
        if (IsBusy)
            return;

        var path = await _fileDialogs.PickClientSecretsJsonAsync();
        if (string.IsNullOrEmpty(path))
        {
            StatusMessage = "未選擇檔案。";
            return;
        }

        try
        {
            _auth.SetClientSecretsPath(path);
            RefreshCredentialsStatus();
            StatusMessage = $"已設定憑證檔。\n請按「登入 / 授權」繼續。\n已複製到：{YouTubeAuthService.PreferredSecretsPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            RefreshCredentialsStatus();
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy)
            return;

        if (_auth.FindClientSecretsPath() is null)
        {
            StatusMessage =
                "仍找不到 client_secrets.json。\n請先按「選擇憑證檔」選取 Google 下載的 OAuth JSON。";
            RefreshCredentialsStatus();
            return;
        }

        _authCts?.Cancel();
        _authCts?.Dispose();
        _authCts = new CancellationTokenSource();
        var token = _authCts.Token;

        IsBusy = true;
        IsAuthenticating = true;
        StatusMessage = "正在開啟瀏覽器進行 Google 授權…\n關閉瀏覽器或未完成時，請按「取消授權」。";

        try
        {
            var service = await _auth.AuthenticateAsync(token);
            _subscriptions = new YouTubeSubscriptionService(service);
            IsAuthenticated = true;
            IsAuthenticating = false;
            await Task.Run(TryAttachBrowserSession);
            StatusMessage = "登入成功，正在載入訂閱…";
            await LoadSubscriptionsCoreAsync();
        }
        catch (FileNotFoundError ex)
        {
            StatusMessage = ex.Message;
            if (!HasWebSession)
                IsAuthenticated = false;
            RefreshCredentialsStatus();
        }
        catch (TimeoutException ex)
        {
            StatusMessage = ex.Message;
            if (!HasWebSession)
                IsAuthenticated = false;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消授權。可再按「登入 / 授權」重試。";
            if (!HasWebSession)
                IsAuthenticated = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"登入失敗：{ex.Message}";
            if (!HasWebSession)
                IsAuthenticated = false;
        }
        finally
        {
            IsAuthenticating = false;
            IsBusy = false;
            _authCts?.Dispose();
            _authCts = null;
        }
    }

    [RelayCommand]
    private void CancelLogin()
    {
        if (!IsAuthenticating)
            return;

        StatusMessage = "正在取消授權…";
        _authCts?.Cancel();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            await _auth.SignOutAsync();
            _subscriptions = null;
            _web.Clear();
            YouTubeWebCredentialStore.Clear();
            HasWebSession = false;
            _usingWebList = false;
            Channels.Clear();
            FilteredChannels.Clear();
            TotalCount = 0;
            SelectedCount = 0;
            IsAuthenticated = false;
            StatusMessage = "已登出。";
            RefreshCredentialsStatus();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!IsAuthenticated || IsBusy)
            return;

        IsBusy = true;
        try
        {
            await LoadSubscriptionsCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        if (IsBusy)
            return;

        SubscriptionCache.ClearAll();
        DiscoverySummary = string.Empty;

        if (!IsAuthenticated)
        {
            StatusMessage = "YouTube 訂閱快取已清除。";
            return;
        }

        IsBusy = true;
        StatusMessage = HasWebSession
            ? "快取已清除，正在從 youtube.com/feed/channels 重新載入…"
            : "快取已清除，正在從 YouTube API 重新載入…";
        try
        {
            await LoadSubscriptionsCoreAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DiscoverMoreAsync()
    {
        if (!IsAuthenticated || IsBusy)
            return;

        if (HasWebSession)
        {
            IsBusy = true;
            try
            {
                await LoadSubscriptionsCoreAsync();
                StatusMessage = $"已從 youtube.com/feed/channels 重新載入 {Channels.Count} 個頻道（與網站相同）。";
            }
            finally
            {
                IsBusy = false;
            }
            return;
        }

        if (_subscriptions is null)
            return;

        IsBusy = true;
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        var progress = new Progress<string>(message => StatusMessage = message);

        try
        {
            var result = await _subscriptions.ListMergedSubscriptionsAsync(progress, token);

            _usingWebList = false;
            Channels.Clear();
            foreach (var channel in result.Channels)
                Channels.Add(channel);

            TotalCount = Channels.Count;
            DiscoverySummary =
                $"探索結果：相關度 {result.RelevanceCount}、最新活動 {result.ActivityCount}、" +
                $"名稱 A–Z {result.AlphabeticalCount}；去重後 {Channels.Count} 筆。";
            ApplyFilter();
            StatusMessage = $"已完成三種 API 排序探索，合併後取得 {Channels.Count} 個不重複頻道。";
            _ = LoadThumbnailsAsync(result.Channels, token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "探索已取消。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"探索失敗：{YouTubeApiErrorFormatter.ForLoading(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadSubscriptionsCoreAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        var progress = new Progress<string>(msg => StatusMessage = msg);

        try
        {
            if (!HasWebSession)
                await Task.Run(TryAttachBrowserSession);

            if (HasWebSession)
            {
                var webList = await _web.ListFeedChannelsAsync(progress, token);
                WebLoadError = string.Empty;
                ReplaceChannels(webList);
                _usingWebList = true;
                TotalCount = webList.Count;
                DiscoverySummary = string.Empty;
                ApplyFilter();
                var purchased = webList.Count(c => c.IsMembership);
                var subscribed = webList.Count - purchased;
                StatusMessage =
                    $"已載入 {webList.Count} 個頻道（已購買 {purchased}、已訂閱 {subscribed}）。";
                _ = LoadThumbnailsAsync(webList, token);
                return;
            }

            if (_subscriptions is null)
                return;

            var mode = SelectedSortOption?.Mode ?? SubscriptionSortMode.Relevance;
            var (list, total) = await _subscriptions.ListSubscriptionsAsync(
                mode,
                progress,
                token);

            _usingWebList = false;
            ReplaceChannels(list);
            TotalCount = total;
            DiscoverySummary = string.Empty;
            ApplyFilter();
            if (Channels.Count == 0)
            {
                if (await Task.Run(TryAttachBrowserSession) && HasWebSession)
                {
                    var webList = await _web.ListFeedChannelsAsync(progress, token);
                    WebLoadError = string.Empty;
                    ReplaceChannels(webList);
                    _usingWebList = true;
                    TotalCount = webList.Count;
                    DiscoverySummary = string.Empty;
                    ApplyFilter();
                    StatusMessage = $"Data API 沒有頻道列，已改從 Chrome 載入 {webList.Count} 個頻道。";
                    _ = LoadThumbnailsAsync(webList, token);
                    return;
                }

                if (total > 0)
                {
                    StatusMessage =
                        $"YouTube API 回報有 {total} 個訂閱，但沒有回傳頻道列。請確認 Chrome 已登入同一個 YouTube 帳號後再按重新整理。";
                }
            }
            else
            {
                StatusMessage = mode switch
                {
                    SubscriptionSortMode.Alphabetical =>
                        $"已載入 {Channels.Count} 個訂閱（排序：名稱 A–Z）。",
                    SubscriptionSortMode.Activity =>
                        $"已載入 {Channels.Count} 個訂閱（排序：依最新活動）。",
                    _ =>
                        $"已載入 {Channels.Count} 個訂閱（排序：相關度，同 youtube.com/feed/channels）。",
                };
            }

            _ = LoadThumbnailsAsync(list, token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "載入已取消。";
        }
        catch (Exception ex)
        {
            if (HasWebSession)
            {
                WebLoadError = ex.Message;
                StatusMessage = $"載入 feed/channels 失敗：{ex.Message}";
                NotifyListSummary();
            }
            else
            {
                StatusMessage = $"{YouTubeApiErrorFormatter.ForLoading(ex)} 若已有清單，會維持顯示。";
            }
        }
    }

    private void ReplaceChannels(IReadOnlyList<SubscriptionChannel> list)
    {
        Channels.Clear();
        foreach (var ch in list)
            Channels.Add(ch);
    }

    private async Task LoadThumbnailsAsync(
        IReadOnlyList<SubscriptionChannel> channels,
        CancellationToken cancellationToken)
    {
        // Limit concurrency so we don't open hundreds of HTTP connections at once.
        using var gate = new SemaphoreSlim(8);
        var tasks = channels.Select(async ch =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var bmp = await ThumbnailLoader.LoadAsync(ch.ThumbnailUrl, cancellationToken);
                if (bmp is not null)
                    ch.Thumbnail = bmp;
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        var n = 0;
        foreach (var ch in Channels)
        {
            if (!ch.CanUnsubscribe)
            {
                ch.IsSelected = false;
                continue;
            }

            ch.IsSelected = true;
            n++;
        }

        RecalculateSelectedCount();
        StatusMessage = n == 0
            ? "沒有可取消訂閱的頻道（已購買不能勾選）。"
            : $"已全選 {n} 個可取消訂閱的頻道。";
    }

    /// <summary>勾選目前列表（含搜尋排序）前 N 個頻道。</summary>
    [RelayCommand]
    private void SelectFirst33()
    {
        SelectFirstN(33);
    }

    private void SelectFirstN(int n)
    {
        foreach (var ch in Channels)
            ch.IsSelected = false;

        // 依目前顯示順序（FilteredChannels）勾選前 N 個
        var targets = FilteredChannels.Where(c => c.CanUnsubscribe).Take(n).ToList();
        foreach (var ch in targets)
            ch.IsSelected = true;

        RecalculateSelectedCount();
        StatusMessage = targets.Count == 0
            ? "目前列表沒有可勾選的頻道。"
            : $"已勾選前 {targets.Count} 個頻道。";
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var ch in Channels)
            ch.IsSelected = false;

        RecalculateSelectedCount();
        StatusMessage = "已取消所有勾選。";
    }

    /// <summary>是否為全選狀態（已勾選數等於全部頻道數，且至少 1 個）。</summary>
    private IEnumerable<SubscriptionChannel> UnsubscribeTargets =>
        Channels.Where(c => c.CanUnsubscribe);

    private bool IsAllSelected
    {
        get
        {
            var n = UnsubscribeTargets.Count();
            return n > 0 && SelectedCount == n;
        }
    }

    /// <summary>在瀏覽器開啟所有已勾選頻道；全選時先跳確認。</summary>
    [RelayCommand]
    private void OpenSelectedInBrowser()
    {
        var targets = Channels
            .Where(c => c.IsSelected && !string.IsNullOrWhiteSpace(c.ChannelUrl))
            .ToList();

        if (targets.Count == 0)
        {
            StatusMessage = "請先勾選要在瀏覽器開啟的頻道。";
            return;
        }

        // 全選後開啟瀏覽器：二次確認，避免一次開出大量分頁
        if (IsAllSelected)
        {
            ConfirmOpenBrowserMessage =
                $"您已全選全部 {targets.Count} 個訂閱頻道。\n" +
                "確定要在瀏覽器一次開啟全部嗎？\n" +
                "可能會開啟大量分頁，瀏覽器可能短暫卡頓。";
            IsConfirmOpenBrowserVisible = true;
            return;
        }

        ExecuteOpenInBrowser(targets);
    }

    [RelayCommand]
    private void CancelConfirmOpenBrowser()
    {
        IsConfirmOpenBrowserVisible = false;
        ConfirmOpenBrowserMessage = string.Empty;
    }

    [RelayCommand]
    private void ConfirmOpenBrowser()
    {
        var targets = Channels
            .Where(c => c.IsSelected && !string.IsNullOrWhiteSpace(c.ChannelUrl))
            .ToList();

        IsConfirmOpenBrowserVisible = false;
        ConfirmOpenBrowserMessage = string.Empty;

        if (targets.Count == 0)
        {
            StatusMessage = "沒有已勾選的頻道。";
            return;
        }

        ExecuteOpenInBrowser(targets);
    }

    private void ExecuteOpenInBrowser(List<SubscriptionChannel> targets)
    {
        var opened = 0;
        var failed = 0;
        foreach (var ch in targets)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ch.ChannelUrl,
                    UseShellExecute = true,
                });
                opened++;
            }
            catch
            {
                failed++;
            }
        }

        StatusMessage = failed == 0
            ? $"已在瀏覽器開啟 {opened} 個頻道。"
            : $"已開啟 {opened} 個，失敗 {failed} 個。";
    }

    [RelayCommand]
    private void RequestUnsubscribe()
    {
        if (SelectedCount == 0)
        {
            StatusMessage = "請先勾選要取消訂閱的頻道。";
            return;
        }

        ConfirmMessage =
            $"確定要取消訂閱已勾選的 {SelectedCount} 個頻道嗎？\n此操作無法復原（需重新訂閱）。";
        IsConfirmUnsubscribeSecondVisible = false;
        ConfirmSecondMessage = string.Empty;
        IsConfirmUnsubscribeVisible = true;
    }

    [RelayCommand]
    private void CancelConfirmUnsubscribe()
    {
        IsConfirmUnsubscribeVisible = false;
        IsConfirmUnsubscribeSecondVisible = false;
        ConfirmMessage = string.Empty;
        ConfirmSecondMessage = string.Empty;
    }

    /// <summary>
    /// 第一次確認：一般情況直接執行；全選時改顯示第二次確認。
    /// </summary>
    [RelayCommand]
    private async Task ConfirmUnsubscribeAsync()
    {
        if (IsBusy || (_subscriptions is null && !HasWebSession))
            return;

        var targets = Channels.Where(c => c.IsSelected && c.CanUnsubscribe).ToList();
        if (targets.Count == 0)
        {
            IsConfirmUnsubscribeVisible = false;
            IsConfirmUnsubscribeSecondVisible = false;
            StatusMessage = "沒有已勾選的頻道。";
            return;
        }

        if (IsAllSelected && !IsConfirmUnsubscribeSecondVisible)
        {
            IsConfirmUnsubscribeVisible = false;
            ConfirmSecondMessage =
                $"您已全選全部 {targets.Count} 個訂閱頻道。\n" +
                "再次確認：真的要全部取消訂閱嗎？\n" +
                "此操作無法復原，需逐一手動重新訂閱。";
            IsConfirmUnsubscribeSecondVisible = true;
            return;
        }

        await ExecuteUnsubscribeAsync(targets);
    }

    /// <summary>第二次確認（全選路徑）通過後執行取消訂閱。</summary>
    [RelayCommand]
    private async Task ConfirmUnsubscribeSecondAsync()
    {
        if (IsBusy || (_subscriptions is null && !HasWebSession))
            return;

        var targets = Channels.Where(c => c.IsSelected && c.CanUnsubscribe).ToList();
        if (targets.Count == 0)
        {
            IsConfirmUnsubscribeVisible = false;
            IsConfirmUnsubscribeSecondVisible = false;
            StatusMessage = "沒有已勾選的頻道。";
            return;
        }

        await ExecuteUnsubscribeAsync(targets);
    }

    private async Task ExecuteUnsubscribeAsync(List<SubscriptionChannel> targets)
    {
        IsConfirmUnsubscribeVisible = false;
        IsConfirmUnsubscribeSecondVisible = false;
        ConfirmMessage = string.Empty;
        ConfirmSecondMessage = string.Empty;
        IsBusy = true;

        var failed = new List<string>();
        var success = 0;
        var abortedByQuota = false;

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var ch = targets[i];
                StatusMessage = $"取消訂閱中 ({i + 1}/{targets.Count})：{ch.Title}";
                try
                {
                    await UnsubscribeOneAsync(ch, CancellationToken.None);
                    Channels.Remove(ch);
                    success++;
                    if (HasWebSession)
                        await Task.Delay(400);
                }
                catch (Exception ex)
                {
                    var friendly = YouTubeApiErrorFormatter.ForUnsubscribe(ex);
                    failed.Add($"{ch.Title}: {friendly}");

                    // Daily API quota exhausted — further deletes will also fail.
                    if (IsQuotaExceeded(ex))
                    {
                        abortedByQuota = true;
                        var remaining = targets.Count - i - 1;
                        if (remaining > 0)
                            failed.Add($"已中止剩餘 {remaining} 個（YouTube API 配額已用盡）。");
                        break;
                    }
                }
            }

            TotalCount = Math.Max(0, TotalCount - success);
            if (TotalCount < Channels.Count)
                TotalCount = Channels.Count;

            if (success > 0 && _subscriptions is not null && !_usingWebList)
            {
                var mode = SelectedSortOption?.Mode ?? SubscriptionSortMode.Relevance;
                await _subscriptions.UpdateCacheAsync(mode, Channels, TotalCount);
            }

            ApplyFilter();
            RecalculateSelectedCount();

            if (failed.Count == 0)
            {
                StatusMessage = $"已成功取消訂閱 {success} 個頻道。目前剩餘 {Channels.Count} 個。";
            }
            else if (abortedByQuota)
            {
                StatusMessage =
                    $"成功 {success} 個，失敗/中止 {failed.Count} 條。\n" +
                    "YouTube Data API 每日配額已用盡（預設約 10,000 單位/天）。\n" +
                    $"取消訂閱每次約消耗 50 單位。請等到配額重置（{YouTubeQuotaReset.TaipeiAnnotation()}）後再試，" +
                    "或在 Google Cloud Console 申請提高配額。\n" +
                    string.Join("\n", failed.Take(3));
            }
            else
            {
                StatusMessage =
                    $"成功 {success} 個，失敗 {failed.Count} 個。\n" +
                    string.Join("\n", failed.Take(5));
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UnsubscribeOneAsync(SubscriptionChannel channel, CancellationToken cancellationToken)
    {
        if (HasWebSession && (string.IsNullOrWhiteSpace(channel.SubscriptionId) || _usingWebList))
        {
            await _web.UnsubscribeAsync(channel, cancellationToken);
            return;
        }

        if (_subscriptions is null || string.IsNullOrWhiteSpace(channel.SubscriptionId))
            throw new InvalidOperationException("沒有可用的取消訂閱方式（需要網頁 cookies 或 Data API 訂閱 ID）。");

        await _subscriptions.UnsubscribeAsync(channel.SubscriptionId, cancellationToken);
    }

    private static bool IsQuotaExceeded(Exception ex)
    {
        var text = ex.ToString();
        return text.Contains("quota", StringComparison.OrdinalIgnoreCase)
               || text.Contains("exceeded", StringComparison.OrdinalIgnoreCase)
               || text.Contains("dailyLimitExceeded", StringComparison.OrdinalIgnoreCase)
               || text.Contains("quotaExceeded", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatYouTubeError(Exception ex)
    {
        if (IsQuotaExceeded(ex))
            return "YouTube API 配額已用盡（quota exceeded）。";

        var msg = ex.Message;
        // Strip HTML anchors sometimes embedded in Google API errors.
        msg = System.Text.RegularExpressions.Regex.Replace(
            msg,
            "<[^>]+>",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (msg.Length > 180)
            msg = msg[..177] + "…";
        return msg;
    }

    [RelayCommand]
    private void OpenCredentialsHelp() => YouTubeAuthService.OpenClientSecretsHelp();

    [RelayCommand]
    private void OpenApiHelp() => YouTubeAuthService.OpenApiLibraryHelp();

    [RelayCommand]
    private void OpenChannel(SubscriptionChannel? channel)
    {
        if (channel is null || string.IsNullOrWhiteSpace(channel.ChannelUrl))
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = channel.ChannelUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"無法開啟頻道：{ex.Message}";
        }
    }
}
