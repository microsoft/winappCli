using Microsoft.UI.Xaml.Controls;

namespace TrailBoard.Controls;

public sealed class MauiCommandBar : CommandBar
{
    public MauiCommandBar()
    {
        DefaultLabelPosition = CommandBarDefaultLabelPosition.Right;
    }
}
