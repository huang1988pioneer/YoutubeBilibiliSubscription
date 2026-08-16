using YoutubeSubscription.Services;

namespace YoutubeSubscription.ViewModels;

public sealed class BilibiliSortOptionItem
{
    public BilibiliSortOptionItem(BilibiliFollowingSortMode mode, string label)
    {
        Mode = mode;
        Label = label;
    }

    public BilibiliFollowingSortMode Mode { get; }
    public string Label { get; }

    public override string ToString() => Label;
}
