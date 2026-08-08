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
    private YouTubeSubscriptionService? _subscriptions;
    private CancellationTokenSource? _loadCts;

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

    [ObservableProperty]
    public partial bool IsAuthenticated { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

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

    public string TotalCountText => $"已訂閱 · {TotalCount}";

    public string SelectedCountText => $"已勾選：{SelectedCount}";

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
    }

    public FileDialogService FileDialogs => _fileDialogs;

    public void RefreshCredentialsStatus()
    {
        var path = _auth.FindClientSecretsPath();
        HasClientSecrets = path is not null;
        CredentialsPathText = path is null
            ? "尚未設定 client_secrets.json（請按「選擇憑證檔」）"
            : $"憑證：{path}";

        if (path is null && !IsAuthenticated)
        {
            StatusMessage =
                "找不到 client_secrets.json。\n" +
                "1. 按「Google Cloud 憑證」建立 OAuth 桌面應用程式並下載 JSON\n" +
                "2. 按「選擇憑證檔」選取下載的 JSON\n" +
                "3. 再按「登入 / 授權」";
        }
    }

    partial void OnTotalCountChanged(long value) =>
        OnPropertyChanged(nameof(TotalCountText));

    partial void OnSelectedCountChanged(int value) =>
        OnPropertyChanged(nameof(SelectedCountText));

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnSelectedSortOptionChanged(SortOptionItem? value)
    {
        if (_suppressSortReload || value is null || !IsAuthenticated || _subscriptions is null)
            return;

        // Re-fetch with API order so list matches youtube.com/feed/channels sorting.
        _ = ReloadWithCurrentSortAsync();
    }

    private async Task ReloadWithCurrentSortAsync()
    {
        if (IsBusy || _subscriptions is null)
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

        // Preserve API order (SortIndex) — same order as youtube.com/feed/channels for that mode.
        IEnumerable<SubscriptionChannel> source = Channels.OrderBy(c => c.SortIndex);

        if (!string.IsNullOrEmpty(q))
        {
            source = source.Where(c =>
                c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.CustomUrl.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.ChannelId.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.MetaLine.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        FilteredChannels.Clear();
        foreach (var ch in source)
            FilteredChannels.Add(ch);
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

        IsBusy = true;
        StatusMessage = "正在開啟瀏覽器進行 Google 授權…";

        try
        {
            var service = await _auth.AuthenticateAsync();
            _subscriptions = new YouTubeSubscriptionService(service);
            IsAuthenticated = true;
            StatusMessage = "登入成功，正在載入訂閱…";
            await LoadSubscriptionsCoreAsync();
        }
        catch (FileNotFoundError ex)
        {
            StatusMessage = ex.Message;
            IsAuthenticated = false;
            RefreshCredentialsStatus();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消授權。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"登入失敗：{ex.Message}";
            IsAuthenticated = false;
        }
        finally
        {
            IsBusy = false;
        }
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
        if (!IsAuthenticated || _subscriptions is null || IsBusy)
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

    private async Task LoadSubscriptionsCoreAsync()
    {
        if (_subscriptions is null)
            return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        var progress = new Progress<string>(msg => StatusMessage = msg);

        try
        {
            var mode = SelectedSortOption?.Mode ?? SubscriptionSortMode.Relevance;
            var (list, total) = await _subscriptions.ListSubscriptionsAsync(
                mode,
                progress,
                token);

            Channels.Clear();
            foreach (var ch in list)
                Channels.Add(ch);

            TotalCount = total;
            ApplyFilter();
            StatusMessage = mode switch
            {
                SubscriptionSortMode.Alphabetical =>
                    $"已載入 {Channels.Count} 個訂閱（排序：名稱 A–Z）。",
                SubscriptionSortMode.Activity =>
                    $"已載入 {Channels.Count} 個訂閱（排序：依最新活動）。",
                _ =>
                    $"已載入 {Channels.Count} 個訂閱（排序：相關度，同 youtube.com/feed/channels）。",
            };

            // Load avatars in background (non-blocking)
            _ = LoadThumbnailsAsync(list, token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "載入已取消。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"載入訂閱失敗：{ex.Message}";
        }
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
        foreach (var ch in Channels)
            ch.IsSelected = true;

        RecalculateSelectedCount();
        StatusMessage = $"已全選全部 {Channels.Count} 個訂閱頻道。";
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
        var targets = FilteredChannels.Take(n).ToList();
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
    private bool IsAllSelected =>
        Channels.Count > 0 && SelectedCount == Channels.Count;

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
        if (_subscriptions is null || IsBusy)
            return;

        var targets = Channels.Where(c => c.IsSelected).ToList();
        if (targets.Count == 0)
        {
            IsConfirmUnsubscribeVisible = false;
            IsConfirmUnsubscribeSecondVisible = false;
            StatusMessage = "沒有已勾選的頻道。";
            return;
        }

        // 全選後取消訂閱：第一次確認通過後，再跳第二次確認
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
        if (_subscriptions is null || IsBusy)
            return;

        var targets = Channels.Where(c => c.IsSelected).ToList();
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
                    await _subscriptions!.UnsubscribeAsync(ch.SubscriptionId);
                    Channels.Remove(ch);
                    success++;
                }
                catch (Exception ex)
                {
                    var friendly = FormatYouTubeError(ex);
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
                    "取消訂閱每次約消耗 50 單位。請等到配額重置（太平洋時間午夜）後再試，" +
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
