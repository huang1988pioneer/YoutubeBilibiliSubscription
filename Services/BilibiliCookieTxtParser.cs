namespace YoutubeSubscription.Services;

/// <summary>
/// Parses Netscape HTTP Cookie File (cookies.txt) exports and extracts Bilibili login cookies.
/// Supports optional <c>#HttpOnly_</c> domain prefix used by many browser extensions.
/// </summary>
public static class BilibiliCookieTxtParser
{
    /// <summary>
    /// Parse a cookies.txt file path into a <see cref="BilibiliCredential"/>.
    /// Throws if SESSDATA or bili_jct is missing for bilibili.com domains.
    /// </summary>
    public static BilibiliCredential ParseFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("路径不能为空。", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到 cookies 文件。", path);

        return Parse(File.ReadAllLines(path));
    }

    /// <summary>Parse Netscape cookie lines (tab-separated).</summary>
    public static BilibiliCredential Parse(IEnumerable<string> lines)
    {
        // name -> value (last write wins for duplicate bilibili cookies)
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var line = raw.TrimEnd('\r');
            // Standard comment, but keep #HttpOnly_ cookie lines.
            if (line.StartsWith('#') && !line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
                continue;

            var isHttpOnly = false;
            if (line.StartsWith("#HttpOnly_", StringComparison.OrdinalIgnoreCase))
            {
                isHttpOnly = true;
                line = line["#HttpOnly_".Length..];
            }

            // Netscape: domain \t flag \t path \t secure \t expiration \t name \t value
            var parts = line.Split('\t');
            if (parts.Length < 7)
                continue;

            var domain = parts[0].Trim();
            var name = parts[5].Trim();
            var value = parts[6].Trim();

            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
                continue;

            if (!IsBilibiliDomain(domain))
                continue;

            // Ignore expired entries when expiration is a positive unix timestamp in the past.
            if (parts.Length >= 5
                && long.TryParse(parts[4], out var exp)
                && exp > 0
                && exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                // SESSDATA / bili_jct should not be taken if clearly expired.
                if (name.Equals("SESSDATA", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("bili_jct", StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            _ = isHttpOnly; // reserved for future filtering; value already parsed
            map[name] = value;
        }

        map.TryGetValue("SESSDATA", out var sess);
        map.TryGetValue("bili_jct", out var jct);
        map.TryGetValue("DedeUserID", out var mid);
        map.TryGetValue("buvid3", out var buvid);

        if (string.IsNullOrWhiteSpace(sess) || string.IsNullOrWhiteSpace(jct))
        {
            throw new InvalidOperationException(
                "cookies.txt 中未找到有效的 bilibili SESSDATA 与 bili_jct。"
                + "请确认已登录 bilibili.com 后再导出 Cookie。");
        }

        // Keep remaining cookies (buvid4, b_nut, sid, bili_ticket, …) for write APIs.
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SESSDATA", "bili_jct", "DedeUserID", "buvid3",
        };
        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in map)
        {
            if (known.Contains(name) || string.IsNullOrWhiteSpace(value))
                continue;
            extra[name] = value.Trim();
        }

        return new BilibiliCredential
        {
            SessData = sess.Trim(),
            BiliJct = jct.Trim(),
            DedeUserId = mid?.Trim() ?? string.Empty,
            Buvid3 = string.IsNullOrWhiteSpace(buvid)
                ? Guid.NewGuid().ToString("N") + "infoc"
                : buvid.Trim(),
            ExtraCookies = extra,
        };
    }

    private static bool IsBilibiliDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return false;

        // strip leading dot: .bilibili.com
        var d = domain.Trim().TrimStart('.').ToLowerInvariant();
        return d == "bilibili.com"
               || d.EndsWith(".bilibili.com", StringComparison.Ordinal)
               || d == "biliapi.net"
               || d.EndsWith(".biliapi.net", StringComparison.Ordinal)
               || d == "biliapi.com"
               || d.EndsWith(".biliapi.com", StringComparison.Ordinal);
    }
}
