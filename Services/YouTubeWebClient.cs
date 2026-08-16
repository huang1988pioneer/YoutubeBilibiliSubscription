using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YoutubeSubscription.Models;

namespace YoutubeSubscription.Services;

/// <summary>
/// Lists / unsubscribes using the same youtube.com/feed/channels session as the browser.
/// Official Data API subscriptions.list often returns pageInfo.totalResults with items=[].
/// </summary>
public sealed class YouTubeWebClient : IDisposable
{
    private const string Origin = "https://www.youtube.com";
    private const string FallbackApiKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
    private const string FallbackClientVersion = "2.20260813.05.00";

    private readonly CookieContainer _cookieJar = new();
    private readonly HttpClient _http;
    private YouTubeWebCredential? _credential;
    private string _apiKey = FallbackApiKey;
    private string _clientVersion = FallbackClientVersion;
    private string? _visitorData;

    public bool HasSession => _credential?.LooksSignedIn == true;

    public YouTubeWebClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookieJar,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            // Raw Cookie header is the source of truth. CookieContainer rejects
            // LOGIN_INFO / __Secure-* values and can attach the wrong Google SID.
            UseCookies = false,
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "Accept-Language",
            "zh-TW,zh;q=0.9,en;q=0.8");
    }

    public void ApplyCredential(YouTubeWebCredential credential)
    {
        _credential = credential;
        foreach (Cookie existing in _cookieJar.GetAllCookies())
            existing.Expired = true;

        foreach (var cookie in credential.Cookies)
        {
            if (string.IsNullOrWhiteSpace(cookie.Name) || string.IsNullOrWhiteSpace(cookie.Value))
                continue;
            try
            {
                var domain = cookie.Domain.Trim();
                var host = domain.TrimStart('.');
                var cookieDomain = domain.StartsWith('.') ? domain : "." + host;
                _cookieJar.Add(new Cookie(cookie.Name, cookie.Value, "/", cookieDomain)
                {
                    Secure = true,
                });
            }
            catch
            {
                // A single bad cookie must not block the rest of the jar.
            }
        }
    }

    public void Clear()
    {
        _credential = null;
        _apiKey = FallbackApiKey;
        _clientVersion = FallbackClientVersion;
        _visitorData = null;
        foreach (Cookie existing in _cookieJar.GetAllCookies())
            existing.Expired = true;
    }

    public async Task<IReadOnlyList<SubscriptionChannel>> ListFeedChannelsAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasSession)
            throw new InvalidOperationException("尚未匯入 YouTube 網頁 cookies。");

        progress?.Report("正在讀取 youtube.com/feed/channels…");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "https://www.youtube.com/feed/channels?hl=zh-TW&gl=TW&persist_hl=1");
        ApplyBrowserHeaders(request, includeAuth: false);
        using var response = await _http.SendAsync(request, cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? string.Empty;

        if (finalUrl.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase)
            || html.Contains("ServiceLogin", StringComparison.Ordinal)
            || !html.Contains("ytInitialData", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "無法開啟 youtube.com/feed/channels（被導向登入頁）。\n" +
                "請確認 Chrome 已登入同一個 YouTube 帳號，然後再按重新整理。");
        }

        ReadYtcfg(html);
        var initial = ExtractJsonAssignment(html, "ytInitialData")
                      ?? throw new InvalidOperationException("頁面沒有 ytInitialData，無法解析訂閱列表。");

        using var doc = JsonDocument.Parse(initial);
        var channels = new List<SubscriptionChannel>();
        ParseBrowseDocument(doc.RootElement, channels);

        var seen = new HashSet<string>(
            channels.Select(c => c.ChannelId),
            StringComparer.Ordinal);
        foreach (var token in FindContinuationTokens(doc.RootElement))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在載入更多頻道（已有 {channels.Count}）…");
            await FetchContinuationAsync(token, channels, seen, cancellationToken);
        }

        for (var i = 0; i < channels.Count; i++)
            channels[i].SortIndex = i;

        progress?.Report($"已從 youtube.com/feed/channels 取得 {channels.Count} 個頻道。");
        return channels;
    }

    public async Task UnsubscribeAsync(
        SubscriptionChannel channel,
        CancellationToken cancellationToken = default)
    {
        if (!HasSession)
            throw new InvalidOperationException("尚未匯入 YouTube 網頁 cookies。");
        if (channel.IsMembership)
            throw new InvalidOperationException($"「{channel.Title}」是已購買／會員頻道，不能用取消訂閱移除。");
        if (string.IsNullOrWhiteSpace(channel.ChannelId))
            throw new ArgumentException("channelId is required.", nameof(channel));

        var payload = new Dictionary<string, object?>
        {
            ["context"] = BuildInnertubeContext(),
            ["channelIds"] = new[] { channel.ChannelId },
        };
        if (!string.IsNullOrWhiteSpace(channel.WebUnsubscribeParams))
            payload["params"] = channel.WebUnsubscribeParams;

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://www.youtube.com/youtubei/v1/subscription/unsubscribe?prettyPrint=false&key={Uri.EscapeDataString(_apiKey)}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        ApplyBrowserHeaders(request, includeAuth: true);
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"網頁取消訂閱失敗（HTTP {(int)response.StatusCode}）。請重新匯入 cookies 後再試。");
        }

        if (body.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
            && body.Contains("\"error\"", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("網頁取消訂閱被拒絕。請重新匯入 cookies 後再試。");
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task FetchContinuationAsync(
        string token,
        List<SubscriptionChannel> channels,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            context = BuildInnertubeContext(),
            continuation = token,
        });
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://www.youtube.com/youtubei/v1/browse?prettyPrint=false&key={Uri.EscapeDataString(_apiKey)}")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        ApplyBrowserHeaders(request, includeAuth: true);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var before = channels.Count;
        ParseBrowseDocument(doc.RootElement, channels);
        for (var i = before; i < channels.Count; i++)
        {
            if (!seen.Add(channels[i].ChannelId))
            {
                channels.RemoveAt(i);
                i--;
            }
        }
    }

    private object BuildInnertubeContext() => new
    {
        client = new
        {
            clientName = "WEB",
            clientVersion = _clientVersion,
            hl = "zh-TW",
            gl = "TW",
            visitorData = _visitorData,
        },
    };

    private void ApplyBrowserHeaders(HttpRequestMessage request, bool includeAuth)
    {
        var cookieHeader = BuildCookieHeader();
        if (!string.IsNullOrEmpty(cookieHeader))
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

        request.Headers.TryAddWithoutValidation("Origin", Origin);
        request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/feed/channels");
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", _clientVersion);
        if (!string.IsNullOrEmpty(_visitorData))
            request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", _visitorData);
        if (includeAuth)
        {
            var auth = BuildSapisidAuthorization();
            if (!string.IsNullOrEmpty(auth))
                request.Headers.TryAddWithoutValidation("Authorization", auth);
            request.Headers.TryAddWithoutValidation("X-Origin", Origin);
            request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
        }
    }

    private string? BuildCookieHeader()
    {
        if (_credential is null)
            return null;

        var map = _credential.ToHeaderMap();
        var parts = map
            .Where(kv => YouTubeWebCredential.IsWireCookieValue(kv.Key)
                         && YouTubeWebCredential.IsWireCookieValue(kv.Value))
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList();
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private string? BuildSapisidAuthorization()
    {
        if (_credential is null)
            return null;

        var parts = new List<string>();
        AppendSapisidPart(parts, "SAPISIDHASH", "SAPISID");
        AppendSapisidPart(parts, "SAPISID1PHASH", "__Secure-1PAPISID");
        AppendSapisidPart(parts, "SAPISID3PHASH", "__Secure-3PAPISID");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    private void AppendSapisidPart(List<string> parts, string scheme, string cookieName)
    {
        var sapisid = _credential?.Find(cookieName);
        if (string.IsNullOrEmpty(sapisid) && scheme == "SAPISIDHASH")
        {
            sapisid = _credential?.Find("SAPISID")
                      ?? _credential?.Find("__Secure-1PAPISID")
                      ?? _credential?.Find("__Secure-3PAPISID");
        }

        if (string.IsNullOrEmpty(sapisid))
            return;

        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var hex = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{ts} {sapisid} {Origin}")))
            .ToLowerInvariant();
        parts.Add($"{scheme} {ts}_{hex}");
    }

    private void ReadYtcfg(string html)
    {
        var key = Regex.Match(html, "\"INNERTUBE_API_KEY\":\"([^\"]+)\"");
        if (key.Success)
            _apiKey = key.Groups[1].Value;
        var ver = Regex.Match(html, "\"INNERTUBE_CLIENT_VERSION\":\"([^\"]+)\"");
        if (ver.Success)
            _clientVersion = ver.Groups[1].Value;
        var vis = Regex.Match(html, "\"VISITOR_DATA\":\"([^\"]+)\"");
        if (vis.Success)
            _visitorData = vis.Groups[1].Value;
    }

    private static string? ExtractJsonAssignment(string html, string name)
    {
        var marker = "var " + name + " = ";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            marker = name + " = ";
            start = html.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                return null;
        }

        start += marker.Length;
        if (start >= html.Length || html[start] != '{')
            return null;

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (inString)
            {
                if (escape)
                    escape = false;
                else if (c == '\\')
                    escape = true;
                else if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{')
                depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return html[start..(i + 1)];
            }
        }

        return null;
    }

    private static void ParseBrowseDocument(JsonElement root, List<SubscriptionChannel> channels)
    {
        Walk(root, currentSection: "已訂閱", channels);
    }

    private static void Walk(JsonElement node, string currentSection, List<SubscriptionChannel> channels)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (node.TryGetProperty("shelfRenderer", out var shelf))
                {
                    var title = TextOf(Get(shelf, "title"));
                    if (IsPurchasedSection(title))
                        currentSection = "已購買";
                    else if (IsSubscribedSection(title))
                        currentSection = "已訂閱";
                    Walk(shelf, currentSection, channels);
                    return;
                }

                if (node.TryGetProperty("channelRenderer", out var renderer))
                {
                    var parsed = ParseChannelRenderer(renderer, currentSection);
                    if (parsed is not null)
                        channels.Add(parsed);
                    return;
                }

                foreach (var prop in node.EnumerateObject())
                    Walk(prop.Value, currentSection, channels);
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                    Walk(item, currentSection, channels);
                break;
        }
    }

    private static IEnumerable<string> FindContinuationTokens(JsonElement root)
    {
        var tokens = new List<string>();
        CollectContinuations(root, tokens);
        return tokens.Distinct(StringComparer.Ordinal);
    }

    private static void CollectContinuations(JsonElement node, List<string> tokens)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty("continuationItemRenderer", out var item))
                {
                    if (item.TryGetProperty("continuationEndpoint", out var endpoint)
                        && endpoint.TryGetProperty("continuationCommand", out var cmd)
                        && cmd.TryGetProperty("token", out var token)
                        && token.ValueKind == JsonValueKind.String)
                    {
                        var value = token.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            tokens.Add(value);
                    }
                }

                foreach (var prop in node.EnumerateObject())
                    CollectContinuations(prop.Value, tokens);
                break;
            case JsonValueKind.Array:
                foreach (var child in node.EnumerateArray())
                    CollectContinuations(child, tokens);
                break;
        }
    }

    private static SubscriptionChannel? ParseChannelRenderer(JsonElement renderer, string sectionTitle)
    {
        var channelId = Str(renderer, "channelId");
        if (string.IsNullOrWhiteSpace(channelId))
            channelId = Str(Get(renderer, "navigationEndpoint", "browseEndpoint"), "browseId");
        if (string.IsNullOrWhiteSpace(channelId))
            return null;

        var title = TextOf(Get(renderer, "title"));
        if (string.IsNullOrWhiteSpace(title))
            title = "(未命名)";

        var handle = TextOf(Get(renderer, "subscriberCountText"));
        if (!handle.StartsWith('@'))
        {
            var canon = Str(Get(renderer, "navigationEndpoint", "browseEndpoint"), "canonicalBaseUrl");
            if (canon.StartsWith("/@"))
                handle = Uri.UnescapeDataString(canon[1..]);
            else
                handle = string.Empty;
        }

        var subscriberText = TextOf(Get(renderer, "videoCountText"));
        var subscriberCount = ParseSubscriberCount(subscriberText);
        if (subscriberCount is null)
        {
            var access = Str(Get(renderer, "videoCountText", "accessibility", "accessibilityData"), "label");
            subscriberCount = ParseSubscriberCount(access);
        }

        var thumb = LargestThumbnail(Get(renderer, "thumbnail", "thumbnails"));
        var url = handle.StartsWith('@')
            ? $"https://www.youtube.com/{handle}"
            : $"https://www.youtube.com/channel/{channelId}";

        var isMembership = IsPurchasedSection(sectionTitle)
                           || !renderer.TryGetProperty("subscribeButton", out _);
        var unsubscribeParams = ExtractUnsubscribeParams(renderer);

        return new SubscriptionChannel
        {
            ChannelId = channelId,
            Title = title,
            Description = NormalizeDescription(TextOf(Get(renderer, "descriptionSnippet"))),
            ThumbnailUrl = thumb,
            ChannelUrl = url,
            CustomUrl = handle,
            SubscriberCount = subscriberCount,
            MetaLine = YouTubeSubscriptionService.BuildMetaLine(handle, subscriberCount),
            SectionTitle = isMembership ? "已購買" : "已訂閱",
            IsMembership = isMembership,
            WebUnsubscribeParams = unsubscribeParams,
        };
    }

    private static string? ExtractUnsubscribeParams(JsonElement renderer)
    {
        if (!renderer.TryGetProperty("subscribeButton", out var button))
            return null;
        return FindUnsubscribeParams(button);
    }

    private static string? FindUnsubscribeParams(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("unsubscribeEndpoint", out var endpoint)
                && endpoint.TryGetProperty("params", out var p)
                && p.ValueKind == JsonValueKind.String)
            {
                return p.GetString();
            }

            foreach (var prop in node.EnumerateObject())
            {
                var found = FindUnsubscribeParams(prop.Value);
                if (!string.IsNullOrEmpty(found))
                    return found;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                var found = FindUnsubscribeParams(item);
                if (!string.IsNullOrEmpty(found))
                    return found;
            }
        }

        return null;
    }

    private static bool IsPurchasedSection(string title) =>
        title.Contains("購買", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Purchased", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Membership", StringComparison.OrdinalIgnoreCase)
        || title.Contains("會員", StringComparison.Ordinal);

    private static bool IsSubscribedSection(string title) =>
        title.Contains("訂閱", StringComparison.Ordinal)
        || title.Contains("订阅", StringComparison.Ordinal)
        || title.Contains("Subscribed", StringComparison.OrdinalIgnoreCase);

    private static JsonElement Get(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                return default;
        }

        return current;
    }

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string TextOf(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return string.Empty;
        if (element.TryGetProperty("simpleText", out var simple) && simple.ValueKind == JsonValueKind.String)
            return simple.GetString() ?? string.Empty;
        if (element.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var run in runs.EnumerateArray())
            {
                if (run.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    sb.Append(text.GetString());
            }

            return sb.ToString();
        }

        return string.Empty;
    }

    private static string LargestThumbnail(JsonElement thumbnails)
    {
        if (thumbnails.ValueKind != JsonValueKind.Array)
            return string.Empty;

        string url = string.Empty;
        var best = -1;
        foreach (var thumb in thumbnails.EnumerateArray())
        {
            var width = thumb.TryGetProperty("width", out var w) && w.TryGetInt32(out var n) ? n : 0;
            var candidate = Str(thumb, "url");
            if (string.IsNullOrEmpty(candidate) || width < best)
                continue;
            best = width;
            url = candidate;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
            url = "https:" + url;
        return url;
    }

    private static string NormalizeDescription(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    internal static ulong? ParseSubscriberCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var compact = text.Trim().Replace(",", string.Empty).Replace(" ", string.Empty);
        var wan = Regex.Match(compact, @"([\d.]+)\s*萬");
        if (wan.Success
            && double.TryParse(wan.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wanN))
        {
            return (ulong)Math.Round(wanN * 10_000d);
        }

        var yi = Regex.Match(compact, @"([\d.]+)\s*億");
        if (yi.Success
            && double.TryParse(yi.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var yiN))
        {
            return (ulong)Math.Round(yiN * 100_000_000d);
        }

        var k = Regex.Match(compact, @"([\d.]+)\s*[Kk]");
        if (k.Success
            && double.TryParse(k.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kN))
        {
            return (ulong)Math.Round(kN * 1_000d);
        }

        var million = Regex.Match(compact, @"([\d.]+)\s*[Mm]");
        if (million.Success
            && double.TryParse(million.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mN))
        {
            return (ulong)Math.Round(mN * 1_000_000d);
        }

        var thousandWord = Regex.Match(text, @"([\d.]+)\s*thousand", RegexOptions.IgnoreCase);
        if (thousandWord.Success
            && double.TryParse(thousandWord.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var thN))
        {
            return (ulong)Math.Round(thN * 1_000d);
        }

        var digits = Regex.Match(compact, @"(\d+)");
        if (digits.Success && ulong.TryParse(digits.Groups[1].Value, out var n))
            return n;

        return null;
    }
}
