using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YoutubeSubscription.Models;
using YoutubeSubscription.Services;

namespace YoutubeSubscription.ViewModels;

public partial class BilibiliViewModel : ViewModelBase, IDisposable
{
    private readonly BilibiliApiClient _api;
    private readonly FileDialogService _fileDialogs;
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<BilibiliFollowing> Channels { get; } = [];
    public ObservableCollection<BilibiliFollowing> FilteredChannels { get; } = [];

    [ObservableProperty]
    public partial string StatusMessage { get; set; } =
        "请手动导入浏览器导出的 cookies.txt 登录 B 站。";

    [ObservableProperty]
    public partial string AccountText { get; set; } = "未登录";

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
    public partial bool IsConfirmUnfollowVisible { get; set; }

    [ObservableProperty]
    public partial string ConfirmMessage { get; set; } = string.Empty;

    /// <summary>
    /// Sort options matching bilibili.com 关注列表（最常访问 / 最近关注）.
    /// Changing selection re-fetches so order matches the server.
    /// </summary>
    public IReadOnlyList<BilibiliSortOptionItem> SortOptions { get; } =
    [
        new(BilibiliFollowingSortMode.MostVisited, "最常访问"),
        new(BilibiliFollowingSortMode.RecentFollow, "最近关注"),
    ];

    [ObservableProperty]
    public partial BilibiliSortOptionItem? SelectedSortOption { get; set; }

    /// <summary>Avoid re-fetch when binding initially sets SelectedSortOption.</summary>
    private bool _suppressSortReload;

    public string TotalCountText => $"关注 UP 总数：{TotalCount}";
    public string SelectedCountText => $"已勾选：{SelectedCount}";

    public FileDialogService FileDialogs => _fileDialogs;

    public BilibiliViewModel() : this(new BilibiliApiClient(), new FileDialogService())
    {
    }

    public BilibiliViewModel(BilibiliApiClient api, FileDialogService fileDialogs)
    {
        _api = api;
        _fileDialogs = fileDialogs;
        _suppressSortReload = true;
        SelectedSortOption = SortOptions[0]; // 最常访问 = bilibili 默认
        _suppressSortReload = false;
        Channels.CollectionChanged += OnChannelsCollectionChanged;
        _ = TryRestoreSessionAsync();
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _api.Dispose();
    }

    partial void OnTotalCountChanged(long value) => OnPropertyChanged(nameof(TotalCountText));
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(SelectedCountText));
    partial void OnFilterTextChanged(string value) => ApplyFilter();

    partial void OnSelectedSortOptionChanged(BilibiliSortOptionItem? value)
    {
        if (_suppressSortReload || value is null || !IsAuthenticated)
            return;

        // Re-fetch with API order so list matches bilibili.com 关注列表 sorting.
        _ = ReloadWithCurrentSortAsync();
    }

    private async Task ReloadWithCurrentSortAsync()
    {
        if (IsBusy || !IsAuthenticated)
            return;

        IsBusy = true;
        try
        {
            await LoadAfterLoginAsync();
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
            foreach (BilibiliFollowing item in e.OldItems)
                item.PropertyChanged -= OnChannelPropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (BilibiliFollowing item in e.NewItems)
                item.PropertyChanged += OnChannelPropertyChanged;
        }

        RecalculateSelectedCount();
        ApplyFilter();
    }

    private void OnChannelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BilibiliFollowing.IsSelected))
            RecalculateSelectedCount();
    }

    private void RecalculateSelectedCount() =>
        SelectedCount = Channels.Count(c => c.IsSelected);

    private void ApplyFilter()
    {
        var q = (FilterText ?? string.Empty).Trim();
        IEnumerable<BilibiliFollowing> source = Channels.OrderBy(c => c.SortIndex);

        if (!string.IsNullOrEmpty(q))
        {
            source = source.Where(c =>
                c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Sign.Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.Mid.ToString().Contains(q, StringComparison.OrdinalIgnoreCase)
                || c.MetaLine.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        FilteredChannels.Clear();
        foreach (var ch in source)
            FilteredChannels.Add(ch);
    }

    private async Task TryRestoreSessionAsync()
    {
        if (!_api.TryLoadSavedCredential())
            return;

        IsBusy = true;
        StatusMessage = "正在恢复 B 站登录…";
        try
        {
            await LoadAfterLoginAsync();
        }
        catch (Exception ex)
        {
            _api.ClearCredential();
            IsAuthenticated = false;
            StatusMessage = $"登录已失效：{ex.Message}。请重新导入 cookies.txt。";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>手动选择 cookies.txt 并登录（B 站唯一登录入口）。</summary>
    [RelayCommand]
    private async Task ImportCookiesTxtAsync()
    {
        if (IsBusy)
            return;

        var path = await _fileDialogs.PickCookiesTxtAsync();
        if (path is null)
        {
            StatusMessage = "已取消选择 cookies.txt。";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "正在导入 cookies.txt…";
            _api.LoginWithCookiesTxt(path);
            StatusMessage = "cookies.txt 已导入，正在载入…";
            await LoadAfterLoginAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入 cookies.txt 失败：{ex.Message}";
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
        _loadCts?.Cancel();
        _api.ClearCredential();
        Channels.Clear();
        FilteredChannels.Clear();
        TotalCount = 0;
        SelectedCount = 0;
        IsAuthenticated = false;
        AccountText = "未登录";
        StatusMessage = "已登出 B 站。请重新导入 cookies.txt。";
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!IsAuthenticated || IsBusy)
            return;

        IsBusy = true;
        try
        {
            await LoadAfterLoginAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadAfterLoginAsync()
    {
        var (mid, name, followingCount) = await _api.GetNavAsync();
        IsAuthenticated = true;
        AccountText = $"{name}（UID {mid}）";
        TotalCount = followingCount;
        StatusMessage = "正在载入关注列表…";

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;
        var progress = new Progress<string>(msg => StatusMessage = msg);
        var sortMode = SelectedSortOption?.Mode ?? BilibiliFollowingSortMode.MostVisited;

        var (list, total) = await _api.ListFollowingsAsync(mid, sortMode, progress, token);
        Channels.Clear();
        foreach (var ch in list)
            Channels.Add(ch);

        TotalCount = total > 0 ? total : list.Count;
        ApplyFilter();
        var sortLabel = sortMode switch
        {
            BilibiliFollowingSortMode.RecentFollow => "最近关注",
            _ => "最常访问",
        };
        StatusMessage = $"已载入 {Channels.Count} 个关注 UP（总数 {TotalCount}，排序：{sortLabel}）。";

        _ = LoadFacesAsync(list, token);
    }

    private async Task LoadFacesAsync(IReadOnlyList<BilibiliFollowing> list, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(8);
        var tasks = list.Select(async ch =>
        {
            await gate.WaitAsync(token);
            try
            {
                var bmp = await ThumbnailLoader.LoadAsync(ch.FaceUrl, token);
                if (bmp is not null)
                    ch.Face = bmp;
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
        StatusMessage = $"已全选全部 {Channels.Count} 个关注 UP。";
    }

    /// <summary>勾选当前列表（含搜索排序）前 N 个 UP。</summary>
    [RelayCommand]
    private void SelectFirst33()
    {
        SelectFirstN(33);
    }

    private void SelectFirstN(int n)
    {
        foreach (var ch in Channels)
            ch.IsSelected = false;

        var targets = FilteredChannels.Take(n).ToList();
        foreach (var ch in targets)
            ch.IsSelected = true;

        RecalculateSelectedCount();
        StatusMessage = targets.Count == 0
            ? "目前列表没有可勾选的 UP。"
            : $"已勾选前 {targets.Count} 个 UP。";
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var ch in Channels)
            ch.IsSelected = false;
        RecalculateSelectedCount();
        StatusMessage = "已取消所有勾选。";
    }

    /// <summary>在浏览器打开所有已勾选 UP 空间。</summary>
    [RelayCommand]
    private void OpenSelectedInBrowser()
    {
        var targets = Channels
            .Where(c => c.IsSelected && !string.IsNullOrWhiteSpace(c.SpaceUrl))
            .ToList();

        if (targets.Count == 0)
        {
            StatusMessage = "请先勾选要在浏览器打开的 UP。";
            return;
        }

        var opened = 0;
        var failed = 0;
        foreach (var ch in targets)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ch.SpaceUrl,
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
            ? $"已在浏览器打开 {opened} 个 UP 空间。"
            : $"已打开 {opened} 个，失败 {failed} 个。";
    }

    [RelayCommand]
    private void RequestUnfollow()
    {
        if (SelectedCount == 0)
        {
            StatusMessage = "请先勾选要取消关注的 UP。";
            return;
        }

        ConfirmMessage =
            $"确定要取消关注已勾选的 {SelectedCount} 个 UP 吗？\n此操作无法复原（需重新关注）。";
        IsConfirmUnfollowVisible = true;
    }

    [RelayCommand]
    private void CancelConfirmUnfollow()
    {
        IsConfirmUnfollowVisible = false;
        ConfirmMessage = string.Empty;
    }

    [RelayCommand]
    private async Task ConfirmUnfollowAsync()
    {
        if (IsBusy)
            return;

        var targets = Channels.Where(c => c.IsSelected).ToList();
        if (targets.Count == 0)
        {
            IsConfirmUnfollowVisible = false;
            StatusMessage = "没有已勾选的 UP。";
            return;
        }

        IsConfirmUnfollowVisible = false;
        IsBusy = true;
        var success = 0;
        var failed = new List<string>();

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var ch = targets[i];
                StatusMessage = $"取消关注中 ({i + 1}/{targets.Count})：{ch.Name}";
                try
                {
                    await _api.UnfollowAsync(ch.Mid);
                    Channels.Remove(ch);
                    success++;
                    await Task.Delay(300); // gentle rate limit
                }
                catch (Exception ex)
                {
                    failed.Add($"{ch.Name}: {ex.Message}");
                }
            }

            TotalCount = Math.Max(0, TotalCount - success);
            if (TotalCount < Channels.Count)
                TotalCount = Channels.Count;

            ApplyFilter();
            RecalculateSelectedCount();

            StatusMessage = failed.Count == 0
                ? $"已成功取消关注 {success} 个 UP。目前剩余 {Channels.Count} 个。"
                : $"成功 {success} 个，失败 {failed.Count} 个。\n" + string.Join("\n", failed.Take(5));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSpace(BilibiliFollowing? channel)
    {
        if (channel is null || string.IsNullOrWhiteSpace(channel.SpaceUrl))
            return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = channel.SpaceUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"无法开启空间：{ex.Message}";
        }
    }

}
