using Avalonia.Media.Imaging;

namespace YoutubeSubscription.Services;

public static class ThumbnailLoader
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public static async Task<Bitmap?> LoadAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            await using var stream = await Http.GetStreamAsync(url, cancellationToken);
            await using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }
}
