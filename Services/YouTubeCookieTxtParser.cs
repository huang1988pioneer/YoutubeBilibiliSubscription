namespace YoutubeSubscription.Services;

/// <summary>
/// Netscape cookies.txt → YouTube website session (youtube.com / google.com).
/// Needed because subscriptions.list often returns totalResults with empty items,
/// while youtube.com/feed/channels still shows the real list.
/// </summary>
public static class YouTubeCookieTxtParser
{
    public static YouTubeWebCredential ParseFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("路徑不能為空。", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到 cookies 檔案。", path);

        return Parse(File.ReadAllLines(path));
    }

    public static YouTubeWebCredential Parse(IEnumerable<string> lines)
    {
        var cookies = new List<YouTubeWebCookie>();

        foreach (var rawLine in lines)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;

            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith('#') && !line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
                line = line["#HttpOnly_".Length..];

            var parts = line.Split('\t');
            if (parts.Length < 7)
                continue;

            var domain = parts[0].Trim();
            var name = parts[5].Trim();
            var value = parts[6].Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value) || !IsYouTubeDomain(domain))
                continue;

            if (parts.Length >= 5
                && long.TryParse(parts[4], out var exp)
                && exp > 0
                && exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                if (name.Equals("SAPISID", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("LOGIN_INFO", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("SID", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            cookies.Add(new YouTubeWebCookie
            {
                Domain = domain,
                Name = name,
                Value = value,
            });
        }

        var credential = new YouTubeWebCredential { Cookies = cookies };
        if (!credential.LooksSignedIn)
        {
            throw new InvalidOperationException(
                "cookies.txt 中沒有有效的 YouTube 登入 Cookie（需要 SAPISID，以及 SID 或 LOGIN_INFO）。\n" +
                "請用與 youtube.com/feed/channels 同一個瀏覽器帳號匯出 Netscape cookies.txt。");
        }

        return credential;
    }

    private static bool IsYouTubeDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return false;

        var d = domain.Trim().TrimStart('.').ToLowerInvariant();
        return d == "youtube.com"
               || d.EndsWith(".youtube.com", StringComparison.Ordinal)
               || d == "google.com"
               || d.EndsWith(".google.com", StringComparison.Ordinal)
               || d == "youtu.be"
               || d == "ggpht.com"
               || d.EndsWith(".ggpht.com", StringComparison.Ordinal)
               || d == "googleusercontent.com"
               || d.EndsWith(".googleusercontent.com", StringComparison.Ordinal);
    }
}
