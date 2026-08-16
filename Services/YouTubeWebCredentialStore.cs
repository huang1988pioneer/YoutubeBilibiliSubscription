using System.Text.Json;
using System.Text.Json.Serialization;

namespace YoutubeSubscription.Services;

public sealed class YouTubeWebCookie
{
    public string Domain { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class YouTubeWebCredential
{
    public List<YouTubeWebCookie> Cookies { get; set; } = [];

    public bool LooksSignedIn
    {
        get
        {
            var sapisid = Find("SAPISID")
                          ?? Find("__Secure-1PAPISID")
                          ?? Find("__Secure-3PAPISID");
            var session = Find("SID") ?? Find("LOGIN_INFO");
            return IsWireCookieValue(sapisid) && IsWireCookieValue(session);
        }
    }

    public static bool IsWireCookieValue(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.All(static c => c is >= (char)0x20 and < (char)0x7F and not ';');

    public string? Find(string name) =>
        SelectPreferred(name, Cookies);

    /// <summary>
    /// Flatten to a Cookie header. Prefer youtube.com over google.com so a
    /// multi-account export does not send the wrong SID and bounce to login.
    /// </summary>
    public Dictionary<string, string> ToHeaderMap()
    {
        var scored = new Dictionary<string, (int Score, string Value)>(StringComparer.OrdinalIgnoreCase);
        foreach (var cookie in Cookies)
        {
            if (string.IsNullOrWhiteSpace(cookie.Name) || string.IsNullOrWhiteSpace(cookie.Value))
                continue;
            var score = DomainScore(cookie.Domain);
            if (!scored.TryGetValue(cookie.Name, out var prev) || score >= prev.Score)
                scored[cookie.Name] = (score, cookie.Value);
        }

        return scored.ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string? SelectPreferred(string name, List<YouTubeWebCookie> cookies)
    {
        YouTubeWebCookie? best = null;
        var bestScore = int.MinValue;
        foreach (var cookie in cookies)
        {
            if (!cookie.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var score = DomainScore(cookie.Domain);
            if (score >= bestScore)
            {
                bestScore = score;
                best = cookie;
            }
        }

        return best?.Value;
    }

    private static int DomainScore(string? domain)
    {
        var d = (domain ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        if (d == "youtube.com" || d.EndsWith(".youtube.com", StringComparison.Ordinal))
            return 3;
        if (d == "google.com" || d.EndsWith(".google.com", StringComparison.Ordinal))
            return 2;
        return 1;
    }
}

/// <summary>Persists YouTube website cookies under LocalAppData.</summary>
public static class YouTubeWebCredentialStore
{
    public static string AppDataDirectory =>
        Path.Combine(YouTubeAuthService.AppDataDirectory, "YouTubeWeb");

    public static string CredentialPath => Path.Combine(AppDataDirectory, "cookies.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Save(YouTubeWebCredential credential)
    {
        Directory.CreateDirectory(AppDataDirectory);
        File.WriteAllText(CredentialPath, JsonSerializer.Serialize(credential, JsonOptions));
    }

    public static YouTubeWebCredential? Load()
    {
        try
        {
            if (!File.Exists(CredentialPath))
                return null;
            var cred = JsonSerializer.Deserialize<YouTubeWebCredential>(
                File.ReadAllText(CredentialPath), JsonOptions);
            return cred is not null && cred.LooksSignedIn ? cred : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(CredentialPath))
                File.Delete(CredentialPath);
        }
        catch
        {
            // best effort
        }
    }
}
