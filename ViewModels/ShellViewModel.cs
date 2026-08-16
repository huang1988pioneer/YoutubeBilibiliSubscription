using CommunityToolkit.Mvvm.ComponentModel;

namespace YoutubeSubscription.ViewModels;

/// <summary>
/// Root shell: YouTube + Bilibili tabs.
/// </summary>
public partial class ShellViewModel : ViewModelBase
{
    public MainViewModel YouTube { get; } = new();

    public BilibiliViewModel Bilibili { get; } = new();

    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }
}
