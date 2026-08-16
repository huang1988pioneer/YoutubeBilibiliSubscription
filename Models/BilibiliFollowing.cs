using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace YoutubeSubscription.Models;

/// <summary>
/// One UP the signed-in Bilibili account is following (订阅/关注).
/// </summary>
public partial class BilibiliFollowing : ObservableObject
{
    public long Mid { get; init; }

    public string Name { get; set; } = string.Empty;

    public string Sign { get; set; } = string.Empty;

    public string FaceUrl { get; set; } = string.Empty;

    public string SpaceUrl => Mid > 0 ? $"https://space.bilibili.com/{Mid}" : string.Empty;

    /// <summary>Follow time (unix seconds → display).</summary>
    public string FollowedAt { get; set; } = string.Empty;

    public string OfficialText { get; set; } = string.Empty;

    /// <summary>e.g. mid · 已关注</summary>
    public string MetaLine { get; set; } = string.Empty;

    public int SortIndex { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial Bitmap? Face { get; set; }
}
