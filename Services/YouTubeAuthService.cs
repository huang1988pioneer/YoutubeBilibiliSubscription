using System.Diagnostics;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using Google.Apis.YouTube.v3;

namespace YoutubeSubscription.Services;

/// <summary>
/// Desktop OAuth for YouTube Data API (list + delete subscriptions).
/// </summary>
public sealed class YouTubeAuthService
{
    // List needs read; delete needs full youtube scope.
    private static readonly string[] Scopes =
    [
        YouTubeService.Scope.Youtube,
        YouTubeService.Scope.YoutubeForceSsl,
    ];

    private const string UserId = "user";
    private const string ClientSecretsFileName = "client_secrets.json";

    private UserCredential? _credential;
    private YouTubeService? _service;
    private string? _explicitSecretsPath;

    public bool IsAuthenticated => _service is not null;

    public YouTubeService? Service => _service;

    public static string AppDataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "YoutubeSubscription");

    /// <summary>Canonical copy of secrets used by the app after the user picks a file.</summary>
    public static string PreferredSecretsPath =>
        Path.Combine(AppDataDirectory, ClientSecretsFileName);

    public static string TokenStoreDirectory =>
        Path.Combine(AppDataDirectory, "Google.Apis.Auth");

    public static string ConfigPath =>
        Path.Combine(AppDataDirectory, "config.json");

    public void SetClientSecretsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundError($"憑證檔不存在：{path}");

        // Validate it looks like Google OAuth client JSON before copying.
        ValidateClientSecretsFile(path);

        Directory.CreateDirectory(AppDataDirectory);
        File.Copy(path, PreferredSecretsPath, overwrite: true);
        _explicitSecretsPath = PreferredSecretsPath;
        SaveRememberedPath(PreferredSecretsPath);
    }

    public static void ValidateClientSecretsFile(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            // Desktop OAuth JSON uses "installed". "web" type usually will not work for this app.
            if (root.TryGetProperty("installed", out _))
                return;

            if (root.TryGetProperty("web", out _))
            {
                throw new FileNotFoundError(
                    "此憑證是「網頁應用程式」類型，本程式需要「桌面應用程式」。\n" +
                    "請在 Google Cloud Console → 憑證 → 建立 OAuth 用戶端 ID → 應用程式類型選「桌面應用程式」，再下載 JSON。");
            }

            throw new FileNotFoundError(
                "此 JSON 不是 Google OAuth 用戶端憑證。\n" +
                "請在 Google Cloud Console → 憑證 → 建立「桌面應用程式」OAuth 用戶端，再下載 JSON。");
        }
        catch (FileNotFoundError)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FileNotFoundError($"無法讀取憑證檔：{ex.Message}");
        }
    }

    public string? FindClientSecretsPath()
    {
        if (!string.IsNullOrEmpty(_explicitSecretsPath) && File.Exists(_explicitSecretsPath))
            return _explicitSecretsPath;

        var remembered = LoadRememberedPath();
        if (!string.IsNullOrEmpty(remembered) && File.Exists(remembered))
            return remembered;

        var candidates = new List<string>
        {
            PreferredSecretsPath,
            Path.Combine(AppContext.BaseDirectory, ClientSecretsFileName),
            Path.Combine(Directory.GetCurrentDirectory(), ClientSecretsFileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ClientSecretsFileName),
        };

        // Common Google download name: client_secret_xxxxx.json
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            if (Directory.Exists(downloads))
            {
                candidates.AddRange(
                    Directory.EnumerateFiles(downloads, "client_secret*.json")
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .Take(5));
                candidates.AddRange(
                    Directory.EnumerateFiles(downloads, "client_secrets*.json")
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .Take(3));
            }
        }
        catch
        {
            // ignore
        }

        foreach (var path in candidates)
        {
            try
            {
                var full = Path.GetFullPath(path);
                if (File.Exists(full))
                    return full;
            }
            catch
            {
                // ignore invalid paths
            }
        }

        return null;
    }

    public string ClientSecretsStatusText
    {
        get
        {
            var path = FindClientSecretsPath();
            return path is null
                ? "尚未設定 client_secrets.json"
                : $"憑證：{path}";
        }
    }

    private static void SaveRememberedPath(string path)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            var json = JsonSerializer.Serialize(new ConfigDto { ClientSecretsPath = path });
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // best effort
        }
    }

    private static string? LoadRememberedPath()
    {
        try
        {
            if (!File.Exists(ConfigPath))
                return null;
            var dto = JsonSerializer.Deserialize<ConfigDto>(File.ReadAllText(ConfigPath));
            return dto?.ClientSecretsPath;
        }
        catch
        {
            return null;
        }
    }

    private sealed class ConfigDto
    {
        public string? ClientSecretsPath { get; set; }
    }

    /// <summary>
    /// Opens the system browser for Google login and builds a YouTubeService.
    /// </summary>
    public async Task<YouTubeService> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        var secretsPath = FindClientSecretsPath()
            ?? throw new FileNotFoundError(
                $"找不到 client_secrets.json。\n" +
                "請先按「選擇憑證檔」，選取從 Google Cloud Console 下載的 OAuth 桌面應用程式 JSON。\n" +
                "或將檔案命名為 client_secrets.json 放到程式目錄。");

        ValidateClientSecretsFile(secretsPath);

        await using var stream = new FileStream(secretsPath, FileMode.Open, FileAccess.Read);
        var secrets = (await GoogleClientSecrets.FromStreamAsync(stream, cancellationToken)).Secrets;

        Directory.CreateDirectory(TokenStoreDirectory);
        var store = new FileDataStore(TokenStoreDirectory, fullPath: true);

        _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
            secrets,
            Scopes,
            UserId,
            cancellationToken,
            store);

        _service = new YouTubeService(new BaseClientService.Initializer
        {
            HttpClientInitializer = _credential,
            ApplicationName = "YoutubeSubscription",
        });

        return _service;
    }

    public async Task SignOutAsync()
    {
        if (_credential is not null)
        {
            try
            {
                await _credential.RevokeTokenAsync(CancellationToken.None);
            }
            catch
            {
                // Token may already be invalid; still clear local store.
            }
        }

        _credential = null;
        _service?.Dispose();
        _service = null;

        try
        {
            if (Directory.Exists(TokenStoreDirectory))
                Directory.Delete(TokenStoreDirectory, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    public static void OpenClientSecretsHelp()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://console.cloud.google.com/apis/credentials",
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }

    public static void OpenApiLibraryHelp()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://console.cloud.google.com/apis/library/youtube.googleapis.com",
                UseShellExecute = true,
            });
        }
        catch
        {
            // ignore
        }
    }
}

/// <summary>Simple typed error so UI can show a clear message.</summary>
public sealed class FileNotFoundError : Exception
{
    public FileNotFoundError(string message) : base(message) { }
}
