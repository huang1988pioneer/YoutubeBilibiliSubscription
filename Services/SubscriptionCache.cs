using System.Text.Json;
using YoutubeSubscription.Models;

namespace YoutubeSubscription.Services;

/// <summary>
/// Persists a recent subscription list per API sort order so reopening or
/// refreshing the app does not repeatedly consume YouTube Data API quota.
/// </summary>
public sealed class SubscriptionCache
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<(IReadOnlyList<SubscriptionChannel> Channels, long TotalCount)?> TryReadFreshAsync(
        SubscriptionSortMode sortMode,
        CancellationToken cancellationToken = default)
    {
        var entry = await ReadAsync(sortMode, cancellationToken);
        if (entry is null || DateTimeOffset.UtcNow - entry.CachedAtUtc > FreshFor)
            return null;

        // YouTube often reports pageInfo.totalResults > 0 with items=[].
        // Do not treat that as a successful 15-minute hit.
        if (entry.Channels.Count == 0 && entry.TotalCount > 0)
            return null;

        return (entry.Channels.Select(ToChannel).ToList(), entry.TotalCount);
    }

    public async Task WriteAsync(
        SubscriptionSortMode sortMode,
        IEnumerable<SubscriptionChannel> channels,
        long totalCount,
        bool clearOtherSorts,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            if (clearOtherSorts)
            {
                foreach (var mode in Enum.GetValues<SubscriptionSortMode>().Where(mode => mode != sortMode))
                    Delete(mode);
            }

            var entry = new CacheEntry
            {
                CachedAtUtc = DateTimeOffset.UtcNow,
                TotalCount = totalCount,
                Channels = channels.Select(ToCacheItem).ToList(),
            };
            var path = GetPath(sortMode);
            var temporaryPath = path + ".tmp";
            var json = JsonSerializer.Serialize(entry, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            // The cache is an optimization; an unavailable cache must never block the app.
        }
    }

    public static void ClearAll()
    {
        foreach (var mode in Enum.GetValues<SubscriptionSortMode>())
            Delete(mode);
    }

    private static async Task<CacheEntry?> ReadAsync(SubscriptionSortMode sortMode, CancellationToken cancellationToken)
    {
        try
        {
            var path = GetPath(sortMode);
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<CacheEntry>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string CacheDirectory => Path.Combine(YouTubeAuthService.AppDataDirectory, "cache");

    private static string GetPath(SubscriptionSortMode sortMode) =>
        Path.Combine(CacheDirectory, $"youtube-subscriptions-{sortMode.ToString().ToLowerInvariant()}.json");

    private static void Delete(SubscriptionSortMode sortMode)
    {
        try
        {
            var path = GetPath(sortMode);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static CacheItem ToCacheItem(SubscriptionChannel channel) => new()
    {
        SubscriptionId = channel.SubscriptionId,
        ChannelId = channel.ChannelId,
        Title = channel.Title,
        Description = channel.Description,
        ThumbnailUrl = channel.ThumbnailUrl,
        ChannelUrl = channel.ChannelUrl,
        SubscribedAt = channel.SubscribedAt,
        CustomUrl = channel.CustomUrl,
        SubscriberCount = channel.SubscriberCount,
        MetaLine = channel.MetaLine,
        SortIndex = channel.SortIndex,
    };

    private static SubscriptionChannel ToChannel(CacheItem item) => new()
    {
        SubscriptionId = item.SubscriptionId,
        ChannelId = item.ChannelId,
        Title = item.Title,
        Description = item.Description,
        ThumbnailUrl = item.ThumbnailUrl,
        ChannelUrl = item.ChannelUrl,
        SubscribedAt = item.SubscribedAt,
        CustomUrl = item.CustomUrl,
        SubscriberCount = item.SubscriberCount,
        MetaLine = item.MetaLine,
        SortIndex = item.SortIndex,
    };

    private sealed class CacheEntry
    {
        public DateTimeOffset CachedAtUtc { get; set; }
        public long TotalCount { get; set; }
        public List<CacheItem> Channels { get; set; } = [];
    }

    private sealed class CacheItem
    {
        public string SubscriptionId { get; set; } = string.Empty;
        public string ChannelId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ThumbnailUrl { get; set; } = string.Empty;
        public string ChannelUrl { get; set; } = string.Empty;
        public string SubscribedAt { get; set; } = string.Empty;
        public string CustomUrl { get; set; } = string.Empty;
        public ulong? SubscriberCount { get; set; }
        public string MetaLine { get; set; } = string.Empty;
        public int SortIndex { get; set; }
    }
}
