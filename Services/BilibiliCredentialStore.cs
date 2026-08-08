using System.Text.Json;
using System.Text.Json.Serialization;

namespace YoutubeSubscription.Services;

public sealed class BilibiliCredential
{
    public string SessData { get; set; } = string.Empty;
    public string BiliJct { get; set; } = string.Empty;
    public string DedeUserId { get; set; } = string.Empty;
    public string Buvid3 { get; set; } = string.Empty;

    /// <summary>
    /// Extra cookies from browser export (buvid4, b_nut, sid, etc.).
    /// Applied wholesale so write APIs are less likely to hit risk control (-352).
    /// </summary>
    public Dictionary<string, string> ExtraCookies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Persists Bilibili login cookies under LocalAppData.</summary>
public static class BilibiliCredentialStore
{
    public static string AppDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YoutubeSubscription",
            "Bilibili");

    public static string CredentialPath => Path.Combine(AppDataDirectory, "credential.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Save(BilibiliCredential credential)
    {
        Directory.CreateDirectory(AppDataDirectory);
        var json = JsonSerializer.Serialize(credential, JsonOptions);
        File.WriteAllText(CredentialPath, json);
    }

    public static BilibiliCredential? Load()
    {
        try
        {
            if (!File.Exists(CredentialPath))
                return null;
            return JsonSerializer.Deserialize<BilibiliCredential>(File.ReadAllText(CredentialPath), JsonOptions);
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

    public static bool IsValid(BilibiliCredential? c) =>
        c is not null
        && !string.IsNullOrWhiteSpace(c.SessData)
        && !string.IsNullOrWhiteSpace(c.BiliJct);
}
