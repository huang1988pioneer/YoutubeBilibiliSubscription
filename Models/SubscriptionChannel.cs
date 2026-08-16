using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace YoutubeSubscription.Models;

/// <summary>
/// One channel the signed-in user is subscribed to (YouTube feed/channels style row).
/// </summary>
public partial class SubscriptionChannel : ObservableObject
{
    /// <summary>YouTube subscription resource id (used by subscriptions.delete).</summary>
    public string SubscriptionId { get; init; } = string.Empty;

    public string ChannelId { get; init; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Channel About text (same as youtube.com/feed/channels).</summary>
    public string Description { get; set; } = string.Empty;

    public string ThumbnailUrl { get; set; } = string.Empty;

    public string ChannelUrl { get; set; } = string.Empty;

    /// <summary>When the user subscribed (yyyy-MM-dd).</summary>
    public string SubscribedAt { get; set; } = string.Empty;

    /// <summary>@handle from customUrl, e.g. @Howfinity</summary>
    public string CustomUrl { get; set; } = string.Empty;

    public ulong? SubscriberCount { get; set; }

    /// <summary>e.g. @Howfinity · 110萬位訂閱者</summary>
    public string MetaLine { get; set; } = string.Empty;

    /// <summary>API response order index for the current sort mode.</summary>
    public int SortIndex { get; set; }

    /// <summary>Website shelf title: 已購買 or 已訂閱.</summary>
    public string SectionTitle { get; set; } = "已訂閱";

    /// <summary>True for youtube.com/feed/channels 「已購買」rows (no unsubscribe).</summary>
    public bool IsMembership { get; set; }

    /// <summary>Innertube unsubscribe params from the website subscribe button.</summary>
    public string? WebUnsubscribeParams { get; set; }

    public bool CanUnsubscribe => !IsMembership;

    public string StatusPillText => IsMembership ? "已購買" : "已訂閱";

    public bool ShowSectionHeader { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }
}
