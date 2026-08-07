using System.Globalization;
using Google.Apis.YouTube.v3;
using Google.Apis.YouTube.v3.Data;
using YoutubeSubscription.Models;

namespace YoutubeSubscription.Services;

/// <summary>
/// Sort modes aligned with YouTube Data API subscriptions.list <c>order</c>
/// and youtube.com/feed/channels behavior.
/// </summary>
public enum SubscriptionSortMode
{
    /// <summary>API default / feed/channels typical order (relevance).</summary>
    Relevance = 0,

    /// <summary>By recent channel activity (API: unread).</summary>
    Activity = 1,

    /// <summary>A–Z by channel title (API: alphabetical).</summary>
    Alphabetical = 2,
}

public sealed class YouTubeSubscriptionService
{
    private readonly YouTubeService _youtube;

    public YouTubeSubscriptionService(YouTubeService youtube)
    {
        _youtube = youtube;
    }

    /// <summary>
    /// Loads all subscriptions with the given API sort order, then enriches
    /// rows to match youtube.com/feed/channels (avatar, @handle, subscriber count, about).
    /// </summary>
    public async Task<(IReadOnlyList<SubscriptionChannel> Channels, long TotalCount)> ListSubscriptionsAsync(
        SubscriptionSortMode sortMode = SubscriptionSortMode.Relevance,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var channels = new List<SubscriptionChannel>();
        long total = 0;
        string? pageToken = null;
        var index = 0;

        var apiOrder = ToApiOrder(sortMode);

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = _youtube.Subscriptions.List("snippet,contentDetails");
            request.Mine = true;
            request.MaxResults = 50;
            request.Order = apiOrder;
            request.PageToken = pageToken;

            var response = await ExecuteWithInvariantCultureAsync(
                () => request.ExecuteAsync(cancellationToken));

            if (total == 0)
                total = response.PageInfo?.TotalResults ?? 0;

            foreach (var item in response.Items ?? Enumerable.Empty<Subscription>())
            {
                var sn = item.Snippet;
                var channelId = sn?.ResourceId?.ChannelId ?? string.Empty;
                var thumbs = sn?.Thumbnails;
                var thumbUrl =
                    thumbs?.High?.Url
                    ?? thumbs?.Medium?.Url
                    ?? thumbs?.Default__?.Url
                    ?? string.Empty;

                channels.Add(new SubscriptionChannel
                {
                    SubscriptionId = item.Id ?? string.Empty,
                    ChannelId = channelId,
                    Title = sn?.Title ?? "(未命名)",
                    Description = NormalizeDescription(sn?.Description),
                    ThumbnailUrl = thumbUrl,
                    ChannelUrl = string.IsNullOrEmpty(channelId)
                        ? string.Empty
                        : $"https://www.youtube.com/channel/{channelId}",
                    SubscribedAt = FormatSubscribedAt(sn),
                    SortIndex = index++,
                    MetaLine = string.Empty,
                });
            }

            pageToken = response.NextPageToken;
            progress?.Report($"已載入訂閱 {channels.Count} / {(total > 0 ? total.ToString() : "?")}…");
        } while (!string.IsNullOrEmpty(pageToken));

        if (total <= 0)
            total = channels.Count;

        progress?.Report("正在取得頻道資訊（頭像、@handle、訂閱數）…");
        await EnrichChannelDetailsAsync(channels, cancellationToken);

        // Keep exact API response order — do not re-sort client-side after fetch.
        // (Alphabetical is already applied by the API when requested.)
        return (channels, total);
    }

    public static SubscriptionsResource.ListRequest.OrderEnum ToApiOrder(SubscriptionSortMode mode) =>
        mode switch
        {
            SubscriptionSortMode.Alphabetical =>
                SubscriptionsResource.ListRequest.OrderEnum.Alphabetical,
            SubscriptionSortMode.Activity =>
                SubscriptionsResource.ListRequest.OrderEnum.Unread,
            _ =>
                SubscriptionsResource.ListRequest.OrderEnum.Relevance,
        };

    private async Task EnrichChannelDetailsAsync(
        List<SubscriptionChannel> channels,
        CancellationToken cancellationToken)
    {
        var ids = channels
            .Select(c => c.ChannelId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();

        var byId = new Dictionary<string, ChannelEnrichment>(StringComparer.Ordinal);

        foreach (var batch in ids.Chunk(50))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = _youtube.Channels.List("snippet,statistics");
            request.Id = string.Join(',', batch);
            request.MaxResults = 50;

            var response = await ExecuteWithInvariantCultureAsync(
                () => request.ExecuteAsync(cancellationToken));

            foreach (var ch in response.Items ?? Enumerable.Empty<Channel>())
            {
                var id = ch.Id ?? string.Empty;
                if (string.IsNullOrEmpty(id))
                    continue;

                var sn = ch.Snippet;
                var custom = sn?.CustomUrl ?? string.Empty;
                if (!string.IsNullOrEmpty(custom) && !custom.StartsWith('@'))
                    custom = "@" + custom.TrimStart('@');

                ulong? subs = null;
                if (ch.Statistics?.HiddenSubscriberCount != true)
                {
                    var raw = ch.Statistics?.SubscriberCount;
                    if (raw is not null)
                    {
                        if (raw is ulong u)
                            subs = u;
                        else if (ulong.TryParse(
                                     raw.ToString(),
                                     NumberStyles.Integer,
                                     CultureInfo.InvariantCulture,
                                     out var n))
                        {
                            subs = n;
                        }
                    }
                }

                var thumbs = sn?.Thumbnails;
                var thumb =
                    thumbs?.High?.Url
                    ?? thumbs?.Medium?.Url
                    ?? thumbs?.Default__?.Url
                    ?? string.Empty;

                var about = NormalizeDescription(sn?.Description);

                // Prefer @handle URL when available (same as YouTube UI).
                var url = !string.IsNullOrEmpty(custom)
                    ? $"https://www.youtube.com/{(custom.StartsWith('@') ? custom : "@" + custom)}"
                    : $"https://www.youtube.com/channel/{id}";

                byId[id] = new ChannelEnrichment(custom, subs, thumb, about, url);
            }
        }

        foreach (var row in channels)
        {
            if (!byId.TryGetValue(row.ChannelId, out var info))
            {
                row.MetaLine = BuildMetaLine(string.Empty, null);
                continue;
            }

            row.CustomUrl = info.CustomUrl;
            row.SubscriberCount = info.Subscribers;
            row.MetaLine = BuildMetaLine(info.CustomUrl, info.Subscribers);

            if (!string.IsNullOrEmpty(info.ThumbnailUrl))
                row.ThumbnailUrl = info.ThumbnailUrl;

            // YouTube feed/channels shows the channel "About" text.
            if (!string.IsNullOrEmpty(info.Description))
                row.Description = info.Description;

            if (!string.IsNullOrEmpty(info.ChannelUrl))
                row.ChannelUrl = info.ChannelUrl;
        }
    }

    private readonly record struct ChannelEnrichment(
        string CustomUrl,
        ulong? Subscribers,
        string ThumbnailUrl,
        string Description,
        string ChannelUrl);

    /// <summary>YouTube TW style: @handle · 110萬位訂閱者</summary>
    public static string BuildMetaLine(string customUrl, ulong? subscriberCount)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(customUrl))
            parts.Add(customUrl.StartsWith('@') ? customUrl : "@" + customUrl);

        var subs = FormatSubscriberCountTw(subscriberCount);
        if (!string.IsNullOrEmpty(subs))
            parts.Add(subs);

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Format like YouTube Chinese UI: 110萬位訂閱者 / 999 位訂閱者.
    /// </summary>
    public static string FormatSubscriberCountTw(ulong? count)
    {
        if (count is null)
            return string.Empty;

        var n = count.Value;
        if (n >= 100_000_000UL)
        {
            var v = n / 100_000_000d;
            return $"{TrimNumber(v)}億位訂閱者";
        }

        if (n >= 10_000UL)
        {
            var v = n / 10_000d;
            return $"{TrimNumber(v)}萬位訂閱者";
        }

        return $"{n} 位訂閱者";
    }

    private static string TrimNumber(double v)
    {
        if (Math.Abs(v - Math.Round(v)) < 0.05)
            return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        return v.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private static string NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return string.Empty;

        return description
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    public async Task UnsubscribeAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId))
            throw new ArgumentException("subscriptionId is required.", nameof(subscriptionId));

        await ExecuteWithInvariantCultureAsync(
            () => _youtube.Subscriptions.Delete(subscriptionId).ExecuteAsync(cancellationToken));
    }

    private static string FormatSubscribedAt(SubscriptionSnippet? sn)
    {
        if (sn is null)
            return string.Empty;

        var raw = sn.PublishedAtRaw;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var dto))
            {
                return dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            return raw.Length >= 10 ? raw[..10] : raw;
        }

        try
        {
            if (sn.PublishedAtDateTimeOffset is { } offset)
                return offset.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }

    private static async Task<T> ExecuteWithInvariantCultureAsync<T>(Func<Task<T>> action)
    {
        var prevCulture = CultureInfo.CurrentCulture;
        var prevUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            return await action().ConfigureAwait(false);
        }
        finally
        {
            CultureInfo.CurrentCulture = prevCulture;
            CultureInfo.CurrentUICulture = prevUi;
        }
    }

    private static async Task ExecuteWithInvariantCultureAsync(Func<Task> action)
    {
        var prevCulture = CultureInfo.CurrentCulture;
        var prevUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            await action().ConfigureAwait(false);
        }
        finally
        {
            CultureInfo.CurrentCulture = prevCulture;
            CultureInfo.CurrentUICulture = prevUi;
        }
    }
}
