using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YoutubeSubscription.Services;

/// <summary>
/// Reads the signed-in YouTube session from local Chrome. Used internally so
/// the YouTube tab does not ask the user to import cookies.txt.
/// </summary>
public static class ChromeYouTubeCookieReader
{
    public static YouTubeWebCredential? TryRead()
    {
        // yt-dlp already knows current Chrome crypto. Local AES decrypt often
        // yields replacement characters that HttpClient rejects as non-ASCII.
        var fromYtDlp = TryReadWithYtDlp();
        if (fromYtDlp?.LooksSignedIn == true)
            return fromYtDlp;

        try
        {
            var key = TryReadChromeKey();
            if (key is null)
                return null;

            foreach (var profile in CandidateProfiles())
            {
                var cred = TryReadProfile(profile, key);
                if (cred?.LooksSignedIn == true)
                    return cred;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static IEnumerable<string> CandidateProfiles()
    {
        var names = new List<string> { "Default" };
        var localState = Path.Combine(ChromeUserData, "Local State");
        if (File.Exists(localState))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(localState));
                if (doc.RootElement.TryGetProperty("profile", out var profile)
                    && profile.TryGetProperty("last_used", out var last)
                    && last.ValueKind == JsonValueKind.String)
                {
                    var lastName = last.GetString();
                    if (!string.IsNullOrWhiteSpace(lastName) && lastName != "Default")
                        names.Add(lastName);
                }
            }
            catch
            {
                // ignore
            }
        }

        return names;
    }

    private static YouTubeWebCredential? TryReadProfile(string profile, byte[] key)
    {
        var db = FindCookieDb(profile);
        if (db is null)
            return null;

        var copy = Path.Combine(Path.GetTempPath(), $"yt-chrome-cookies-{Guid.NewGuid():N}.db");
        try
        {
            File.Copy(db, copy, overwrite: true);
            var journal = db + "-journal";
            if (File.Exists(journal))
                File.Copy(journal, copy + "-journal", overwrite: true);

            var rows = QueryCookies(copy);
            if (rows.Count == 0)
                return null;

            var cookies = new List<YouTubeWebCookie>();
            foreach (var row in rows)
            {
                var value = row.PlainValue;
                if (string.IsNullOrEmpty(value) && row.EncryptedHex.Length > 0)
                    value = DecryptChromeValue(key, row.EncryptedHex) ?? string.Empty;
                if (string.IsNullOrEmpty(value))
                    continue;

                cookies.Add(new YouTubeWebCookie
                {
                    Domain = row.Host,
                    Name = row.Name,
                    Value = value,
                });
            }

            var cred = new YouTubeWebCredential { Cookies = cookies };
            return cred.LooksSignedIn ? cred : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            TryDelete(copy);
            TryDelete(copy + "-journal");
        }
    }

    private static string? FindCookieDb(string profile)
    {
        var dir = Path.Combine(ChromeUserData, profile);
        var network = Path.Combine(dir, "Network", "Cookies");
        if (File.Exists(network))
            return network;
        var legacy = Path.Combine(dir, "Cookies");
        return File.Exists(legacy) ? legacy : null;
    }

    private static List<CookieRow> QueryCookies(string dbPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/sqlite3",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-separator");
        psi.ArgumentList.Add("\t");
        psi.ArgumentList.Add(dbPath);
        psi.ArgumentList.Add(
            "SELECT host_key, name, IFNULL(value,''), hex(encrypted_value) FROM cookies " +
            "WHERE host_key LIKE '%youtube.com%' OR host_key LIKE '%google.com%';");

        using var proc = Process.Start(psi);
        if (proc is null)
            return [];

        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(8000);
        var rows = new List<CookieRow>();
        foreach (var line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var parts = line.Split('\t');
            if (parts.Length < 4)
                continue;
            rows.Add(new CookieRow(parts[0], parts[1], parts[2], parts[3].Trim()));
        }

        return rows;
    }

    private static byte[]? TryReadChromeKey()
    {
        foreach (var service in new[] { "Chrome Safe Storage", "Chrome" })
        {
            var password = RunCapture("/usr/bin/security", [
                "find-generic-password", "-w", "-s", service, "-a", "Chrome",
            ]);
            if (string.IsNullOrWhiteSpace(password))
                continue;

            return Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password.TrimEnd('\n', '\r')),
                "saltysalt"u8.ToArray(),
                1003,
                HashAlgorithmName.SHA1,
                16);
        }

        return null;
    }

    private static string? DecryptChromeValue(byte[] key, string hex)
    {
        try
        {
            var blob = Convert.FromHexString(hex);
            if (blob.Length < 4)
                return null;

            var prefix = Encoding.ASCII.GetString(blob, 0, 3);
            if (prefix is not ("v10" or "v11"))
                return null;

            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = key;
            aes.IV = "                "u8.ToArray();
            using var decrypt = aes.CreateDecryptor();
            var plain = decrypt.TransformFinalBlock(blob, 3, blob.Length - 3);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    private static YouTubeWebCredential? TryReadWithYtDlp()
    {
        var ytDlp = FindYtDlp();
        if (ytDlp is null)
            return null;

        foreach (var profile in CandidateProfiles())
        {
            var cookiesPath = Path.Combine(Path.GetTempPath(), $"yt-dlp-cookies-{Guid.NewGuid():N}.txt");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ytDlp,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("--no-update");
                psi.ArgumentList.Add("--cookies-from-browser");
                psi.ArgumentList.Add("chrome:" + profile);
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(cookiesPath);
                psi.ArgumentList.Add("--skip-download");
                psi.ArgumentList.Add("--ignore-no-formats-error");
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add("https://www.youtube.com");

                using var proc = Process.Start(psi);
                if (proc is null)
                    continue;
                if (!proc.WaitForExit(45000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    continue;
                }

                if (!File.Exists(cookiesPath))
                    continue;
                try
                {
                    var cred = YouTubeCookieTxtParser.ParseFile(cookiesPath);
                    if (cred.LooksSignedIn)
                        return cred;
                }
                catch
                {
                    // try next profile
                }
            }
            finally
            {
                TryDelete(cookiesPath);
            }
        }

        return null;
    }

    private static string? FindYtDlp()
    {
        foreach (var candidate in new[]
                 {
                     "/opt/homebrew/bin/yt-dlp",
                     "/usr/local/bin/yt-dlp",
                     "yt-dlp",
                 })
        {
            if (candidate == "yt-dlp" || File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? RunCapture(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi);
        if (proc is null)
            return null;
        var output = proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(8000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return null;
        }

        return proc.ExitCode == 0 ? output : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // temp file
        }
    }

    private static string ChromeUserData =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Google", "Chrome");

    private readonly record struct CookieRow(string Host, string Name, string PlainValue, string EncryptedHex);
}
