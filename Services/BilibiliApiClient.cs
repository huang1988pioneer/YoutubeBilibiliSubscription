using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Avalonia.Media.Imaging;
using QRCoder;
using YoutubeSubscription.Models;

namespace YoutubeSubscription.Services;

/// <summary>
/// Sort modes aligned with bilibili.com relation followings list
/// (<c>order_type</c> on <c>/x/relation/followings</c>).
/// </summary>
public enum BilibiliFollowingSortMode
{
    /// <summary>最常访问 — API: order_type=attention</summary>
    MostVisited = 0,

    /// <summary>最近关注 — API: order_type empty (by follow time)</summary>
    RecentFollow = 1,
}

public sealed class BilibiliApiClient : IDisposable
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private BilibiliCredential? _credential;

    public BilibiliApiClient()
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com/");
    }

    public bool IsAuthenticated => BilibiliCredentialStore.IsValid(_credential);

    public BilibiliCredential? Credential => _credential;

    public long? CurrentMid { get; private set; }

    public string? CurrentUserName { get; private set; }

    public void Dispose() => _http.Dispose();

    public void ApplyCredential(BilibiliCredential credential)
    {
        _credential = credential;

        // Expire any previous cookies so re-import does not mix sessions.
        foreach (Cookie c in _cookies.GetAllCookies())
            c.Expired = true;

        SetCookie("SESSDATA", credential.SessData);
        SetCookie("bili_jct", credential.BiliJct);
        if (!string.IsNullOrWhiteSpace(credential.DedeUserId))
            SetCookie("DedeUserID", credential.DedeUserId);

        if (!string.IsNullOrWhiteSpace(credential.Buvid3))
            SetCookie("buvid3", credential.Buvid3);
        else
        {
            var buvid = Guid.NewGuid().ToString("N") + "infoc";
            credential.Buvid3 = buvid;
            SetCookie("buvid3", buvid);
        }

        // Apply full browser cookie set — helps avoid write-API risk control (-352).
        if (credential.ExtraCookies is not null)
        {
            foreach (var (name, value) in credential.ExtraCookies)
            {
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                    continue;
                if (name.Equals("SESSDATA", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("bili_jct", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("DedeUserID", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("buvid3", StringComparison.OrdinalIgnoreCase))
                    continue;
                SetCookie(name, value);
            }
        }
    }

    private void SetCookie(string name, string value)
    {
        // Cookie values from Netscape export are often URL-encoded; CookieContainer expects raw.
        var decoded = Uri.UnescapeDataString(value.Trim());
        try
        {
            _cookies.Add(new Cookie(name, decoded, "/", ".bilibili.com"));
        }
        catch
        {
            // Some cookie values are invalid for System.Net.Cookie; fall back to raw string.
            try
            {
                _cookies.Add(new Cookie(name, value.Trim(), "/", ".bilibili.com"));
            }
            catch
            {
                // ignore unparsable cookies
            }
        }
    }

    public void ClearCredential()
    {
        _credential = null;
        CurrentMid = null;
        CurrentUserName = null;
        // CookieContainer has no clear-all; recreate would need new HttpClient — overwrite with empty values.
        foreach (Cookie c in _cookies.GetAllCookies())
            c.Expired = true;
        BilibiliCredentialStore.Clear();
    }

    public bool TryLoadSavedCredential()
    {
        var saved = BilibiliCredentialStore.Load();
        if (!BilibiliCredentialStore.IsValid(saved))
            return false;
        ApplyCredential(saved!);
        return true;
    }

    // ---------- QR login ----------

    public async Task<(string QrcodeKey, string Url, Bitmap QrBitmap)> CreateLoginQrAsync(
        CancellationToken cancellationToken = default)
    {
        using var resp = await _http.GetAsync(
            "https://passport.bilibili.com/x/passport-login/web/qrcode/generate",
            cancellationToken);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;
        EnsureCodeOk(root, "产生登录二维码");

        var data = root.GetProperty("data");
        var url = data.GetProperty("url").GetString()
                  ?? throw new InvalidOperationException("二维码 URL 为空。");
        var key = data.GetProperty("qrcode_key").GetString()
                  ?? throw new InvalidOperationException("qrcode_key 为空。");

        var bmp = RenderQrBitmap(url);
        return (key, url, bmp);
    }

    /// <summary>
    /// Poll QR status. Returns true when logged in.
    /// </summary>
    public async Task<(bool Success, string Message, int Code)> PollLoginQrAsync(
        string qrcodeKey,
        CancellationToken cancellationToken = default)
    {
        var url =
            "https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key="
            + Uri.EscapeDataString(qrcodeKey);

        using var resp = await _http.GetAsync(url, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Outer code should be 0; inner data.code is the QR state.
        if (root.TryGetProperty("code", out var outer) && outer.GetInt32() != 0)
        {
            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "扫码失败";
            return (false, msg ?? "扫码失败", outer.GetInt32());
        }

        var data = root.GetProperty("data");
        var state = data.TryGetProperty("code", out var sc) ? sc.GetInt32() : -1;
        var message = data.TryGetProperty("message", out var sm) ? sm.GetString() ?? "" : "";

        // 0 = success
        if (state == 0)
        {
            var jump = data.TryGetProperty("url", out var u) ? u.GetString() : null;
            var cred = ParseCredentialFromJumpUrl(jump)
                       ?? ExtractCredentialFromCookies();
            if (!BilibiliCredentialStore.IsValid(cred))
                throw new InvalidOperationException("登录成功但未能解析 Cookie（SESSDATA / bili_jct）。");

            ApplyCredential(cred!);
            BilibiliCredentialStore.Save(cred!);
            return (true, "登录成功", 0);
        }

        // 86101 not scanned, 86090 scanned waiting, 86038 expired
        var friendly = state switch
        {
            86101 => "请使用哔哩哔哩 App 扫描二维码",
            86090 => "已扫码，请在手机上确认登录",
            86038 => "二维码已过期，请刷新",
            _ => string.IsNullOrEmpty(message) ? $"等待扫码（{state}）" : message,
        };
        return (false, friendly, state);
    }

    public void LoginWithManualCookies(
        string sessData,
        string biliJct,
        string? dedeUserId = null,
        string? buvid3 = null)
    {
        var cred = new BilibiliCredential
        {
            SessData = sessData.Trim(),
            BiliJct = biliJct.Trim(),
            DedeUserId = dedeUserId?.Trim() ?? string.Empty,
            Buvid3 = string.IsNullOrWhiteSpace(buvid3)
                ? Guid.NewGuid().ToString("N") + "infoc"
                : buvid3.Trim(),
        };
        LoginWithCredential(cred);
    }

    /// <summary>Apply and persist a full credential (e.g. from cookies.txt import).</summary>
    public void LoginWithCredential(BilibiliCredential credential)
    {
        if (!BilibiliCredentialStore.IsValid(credential))
            throw new ArgumentException("SESSDATA 与 bili_jct 不能为空。");

        if (string.IsNullOrWhiteSpace(credential.Buvid3))
            credential.Buvid3 = Guid.NewGuid().ToString("N") + "infoc";

        ApplyCredential(credential);
        BilibiliCredentialStore.Save(credential);
    }

    /// <summary>Import Netscape cookies.txt and login.</summary>
    public void LoginWithCookiesTxt(string path)
    {
        var cred = BilibiliCookieTxtParser.ParseFile(path);
        LoginWithCredential(cred);
    }

    // ---------- Account / followings ----------

    public async Task<(long Mid, string Name, long FollowingCount)> GetNavAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        using var resp = await _http.GetAsync(
            "https://api.bilibili.com/x/web-interface/nav",
            cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        EnsureCodeOk(root, "获取登录用户信息");

        var data = root.GetProperty("data");
        if (!data.TryGetProperty("isLogin", out var loginEl) || !loginEl.GetBoolean())
            throw new InvalidOperationException("登录已失效，请重新登录。");

        var mid = data.GetProperty("mid").GetInt64();
        var name = data.TryGetProperty("uname", out var n) ? n.GetString() ?? "" : "";
        long following = 0;
        // Some nav payloads include following under data; else use relation/stat.
        if (data.TryGetProperty("following", out var f) && f.ValueKind == JsonValueKind.Number)
            following = f.GetInt64();

        CurrentMid = mid;
        CurrentUserName = name;

        if (following <= 0)
            following = await GetFollowingCountAsync(mid, cancellationToken);

        return (mid, name, following);
    }

    public async Task<long> GetFollowingCountAsync(long mid, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        var url = $"https://api.bilibili.com/x/relation/stat?vmid={mid}";
        using var resp = await _http.GetAsync(url, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        EnsureCodeOk(root, "获取关注数");
        var data = root.GetProperty("data");
        return data.TryGetProperty("following", out var f) ? f.GetInt64() : 0;
    }

    public async Task<(IReadOnlyList<BilibiliFollowing> List, long Total)> ListFollowingsAsync(
        long mid,
        BilibiliFollowingSortMode sortMode = BilibiliFollowingSortMode.RecentFollow,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        var result = new List<BilibiliFollowing>();
        long total = 0;
        var pn = 1;
        const int ps = 50;
        var index = 0;
        var orderTypeQuery = sortMode switch
        {
            // 最常访问
            BilibiliFollowingSortMode.MostVisited => "&order_type=attention",
            // 最近关注（默认）：order_type 留空，按关注时间
            BilibiliFollowingSortMode.RecentFollow => "",
            _ => "",
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url =
                $"https://api.bilibili.com/x/relation/followings?vmid={mid}&pn={pn}&ps={ps}&order=desc{orderTypeQuery}";
            using var resp = await _http.GetAsync(url, cancellationToken);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            EnsureCodeOk(root, "获取关注列表");

            var data = root.GetProperty("data");
            if (total == 0 && data.TryGetProperty("total", out var t))
                total = t.GetInt64();

            if (!data.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array
                                                          || list.GetArrayLength() == 0)
            {
                break;
            }

            foreach (var item in list.EnumerateArray())
            {
                var itemMid = item.TryGetProperty("mid", out var m) ? m.GetInt64() : 0;
                var uname = item.TryGetProperty("uname", out var u) ? u.GetString() ?? "" : "";
                var face = item.TryGetProperty("face", out var f) ? f.GetString() ?? "" : "";
                var sign = item.TryGetProperty("sign", out var s) ? s.GetString() ?? "" : "";
                var mtime = item.TryGetProperty("mtime", out var mt) ? mt.GetInt64() : 0;
                var official = "";
                if (item.TryGetProperty("official_verify", out var ov)
                    && ov.TryGetProperty("desc", out var od))
                {
                    official = od.GetString() ?? "";
                }

                var followedAt = mtime > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(mtime).LocalDateTime.ToString("yyyy-MM-dd")
                    : "";

                result.Add(new BilibiliFollowing
                {
                    Mid = itemMid,
                    Name = uname,
                    Sign = sign.Replace('\n', ' ').Replace('\r', ' ').Trim(),
                    FaceUrl = face,
                    FollowedAt = followedAt,
                    OfficialText = official,
                    MetaLine = BuildMetaLine(itemMid, followedAt, official),
                    SortIndex = index++,
                });
            }

            progress?.Report($"已加载关注 {result.Count} / {(total > 0 ? total.ToString() : "?")}…");

            if (list.GetArrayLength() < ps)
                break;
            if (total > 0 && result.Count >= total)
                break;
            pn++;
            // Safety cap
            if (pn > 500)
                break;
        }

        if (total <= 0)
            total = result.Count;

        return (result, total);
    }

    /// <summary>Unfollow UP. act=2. Retries once on risk-control (-352).</summary>
    public async Task UnfollowAsync(long mid, CancellationToken cancellationToken = default)
    {
        EnsureLoggedIn();
        if (_credential is null || string.IsNullOrWhiteSpace(_credential.BiliJct))
            throw new InvalidOperationException("缺少 bili_jct，无法取消关注。");

        // Align payload with web space-page unfollow (reduces -352 risk control hits).
        var form = new Dictionary<string, string>
        {
            ["fid"] = mid.ToString(),
            ["act"] = "2",
            ["re_src"] = "11",
            ["csrf"] = _credential.BiliJct,
            ["spmid"] = "333.999.0.0",
            ["extend_content"] = $$"""{"entity":"user","entity_id":{{mid}}}""",
            ["jsonp"] = "jsonp",
            ["statistics"] = """{"appId":100,"platform":5}""",
        };

        const int maxAttempts = 2;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var content = new FormUrlEncodedContent(form);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.bilibili.com/x/relation/modify")
            {
                Content = content,
            };
            req.Headers.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
            req.Headers.Referrer = new Uri($"https://space.bilibili.com/{mid}/");
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");

            using var resp = await _http.SendAsync(req, cancellationToken);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            if (code == 0)
                return;

            var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;

            // -352 = risk control / fingerprint. Back off once then surface a clear error.
            if (code == -352 && attempt < maxAttempts)
            {
                await Task.Delay(1500 + Random.Shared.Next(500, 1500), cancellationToken);
                continue;
            }

            throw new InvalidOperationException(FormatApiError("取消关注", code, msg));
        }
    }

    // ---------- helpers ----------

    private void EnsureLoggedIn()
    {
        if (!IsAuthenticated)
            throw new InvalidOperationException("尚未登录 B 站，请手动导入 cookies.txt。");
    }

    private static void EnsureCodeOk(JsonElement root, string action)
    {
        var code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (code == 0)
            return;
        var msg = root.TryGetProperty("message", out var m) ? m.GetString() : null;
        throw new InvalidOperationException(FormatApiError(action, code, msg));
    }

    /// <summary>Human-readable Bilibili API error (especially risk control codes).</summary>
    public static string FormatApiError(string action, int code, string? message)
    {
        var detail = code switch
        {
            -101 => "账号未登录或 Cookie 已失效，请重新导入 cookies.txt。",
            -111 => "csrf 校验失败（bili_jct 无效），请重新导出并导入完整 Cookie。",
            -352 => "触发 B 站风控（-352）。请：① 在浏览器重新导出完整 cookies.txt 并导入；"
                   + "② 放慢批量操作（每次间隔更长）；③ 稍后再试。",
            -400 => "请求参数错误。",
            -403 => "账号异常，无法操作。",
            22001 or 22002 => "操作过于频繁，请稍后再试。",
            _ => string.IsNullOrWhiteSpace(message) || message == code.ToString()
                ? "未知错误"
                : message,
        };
        return $"{action}失败：[{code}] {detail}";
    }

    private static string BuildMetaLine(long mid, string followedAt, string official)
    {
        var parts = new List<string> { $"UID {mid}" };
        if (!string.IsNullOrEmpty(followedAt))
            parts.Add($"关注于 {followedAt}");
        if (!string.IsNullOrEmpty(official))
            parts.Add(official);
        return string.Join(" · ", parts);
    }

    private static BilibiliCredential? ParseCredentialFromJumpUrl(string? jumpUrl)
    {
        if (string.IsNullOrWhiteSpace(jumpUrl))
            return null;

        try
        {
            var sess = GetQueryParam(jumpUrl, "SESSDATA");
            var jct = GetQueryParam(jumpUrl, "bili_jct");
            var dede = GetQueryParam(jumpUrl, "DedeUserID");
            if (string.IsNullOrWhiteSpace(sess) || string.IsNullOrWhiteSpace(jct))
                return null;

            return new BilibiliCredential
            {
                SessData = Uri.UnescapeDataString(sess),
                BiliJct = jct,
                DedeUserId = dede ?? string.Empty,
                Buvid3 = Guid.NewGuid().ToString("N") + "infoc",
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetQueryParam(string url, string key)
    {
        var qIndex = url.IndexOf('?', StringComparison.Ordinal);
        var query = qIndex >= 0 ? url[(qIndex + 1)..] : url;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var k = part[..eq];
            if (!k.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            return part[(eq + 1)..];
        }

        return null;
    }

    private BilibiliCredential? ExtractCredentialFromCookies()
    {
        try
        {
            var all = _cookies.GetCookies(new Uri("https://www.bilibili.com"));
            string? sess = null, jct = null, dede = null, buvid = null;
            foreach (Cookie c in all)
            {
                switch (c.Name)
                {
                    case "SESSDATA": sess = c.Value; break;
                    case "bili_jct": jct = c.Value; break;
                    case "DedeUserID": dede = c.Value; break;
                    case "buvid3": buvid = c.Value; break;
                }
            }

            if (string.IsNullOrWhiteSpace(sess) || string.IsNullOrWhiteSpace(jct))
                return null;

            return new BilibiliCredential
            {
                SessData = sess,
                BiliJct = jct,
                DedeUserId = dede ?? string.Empty,
                Buvid3 = buvid ?? Guid.NewGuid().ToString("N") + "infoc",
            };
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap RenderQrBitmap(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var qr = new PngByteQRCode(data);
        var bytes = qr.GetGraphic(8);
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }
}
