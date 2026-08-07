using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace YoutubeSubscription.Services;

/// <summary>
/// Thin wrapper around Avalonia StorageProvider for open-file dialogs.
/// Host must be set from the main window after it is shown.
/// </summary>
public sealed class FileDialogService
{
    public TopLevel? Host { get; set; }

    public async Task<string?> PickClientSecretsJsonAsync()
    {
        if (Host is null)
            return null;

        var files = await Host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "選擇 Google OAuth client_secrets.json",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON 憑證檔")
                {
                    Patterns = ["*.json"],
                    MimeTypes = ["application/json"],
                },
                new FilePickerFileType("所有檔案")
                {
                    Patterns = ["*.*"],
                },
            ],
        });

        var file = files.FirstOrDefault();
        if (file is null)
            return null;

        return file.TryGetLocalPath();
    }
}
