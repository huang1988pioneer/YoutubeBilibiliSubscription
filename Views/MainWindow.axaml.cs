using Avalonia.Controls;
using YoutubeSubscription.ViewModels;

namespace YoutubeSubscription.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is ShellViewModel shell)
        {
            shell.YouTube.FileDialogs.Host = this;
            shell.Bilibili.FileDialogs.Host = this;
        }
    }
}
