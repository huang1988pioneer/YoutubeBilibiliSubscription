using YoutubeSubscription.Services;

namespace YoutubeSubscription.ViewModels;

public sealed class SortOptionItem
{
    public SortOptionItem(SubscriptionSortMode mode, string label)
    {
        Mode = mode;
        Label = label;
    }

    public SubscriptionSortMode Mode { get; }
    public string Label { get; }

    public override string ToString() => Label;
}
