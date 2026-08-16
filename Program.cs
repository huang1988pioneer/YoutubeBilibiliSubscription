using System;
using System.Globalization;
using System.Threading;
using Avalonia;

namespace YoutubeSubscription;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Google.Apis JSON datetime parsing fails under some non-en cultures (e.g. zh-TW)
        // with "String was not recognized as a valid DateTime".
        var invariant = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = invariant;
        CultureInfo.DefaultThreadCurrentUICulture = invariant;
        Thread.CurrentThread.CurrentCulture = invariant;
        Thread.CurrentThread.CurrentUICulture = invariant;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
