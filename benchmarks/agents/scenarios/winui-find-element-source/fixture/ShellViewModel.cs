using System.Collections.ObjectModel;

namespace Lanternfish;

public record Badge(string Label);

public class ShellViewModel
{
    public string Title { get; } = "Lanternfish";
    public ObservableCollection<Badge> Badges { get; } = new();

    public ShellViewModel(AppChannel channel)
    {
        if (channel != AppChannel.Stable) Badges.Add(new Badge(channel.ToString()));
    }
}

public enum AppChannel { Stable, Preview, Dev }
