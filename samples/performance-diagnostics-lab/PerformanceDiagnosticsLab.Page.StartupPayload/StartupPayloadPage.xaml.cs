using Microsoft.UI.Xaml.Controls;
using System.Runtime.CompilerServices;

namespace PerformanceDiagnosticsLab.Features.StartupPayload;

public sealed partial class StartupPayloadPage : Page
{
    public StartupPayloadPage()
    {
        Items = StartupPayloadFactory.Create();
        InitializeComponent();
    }

    public IReadOnlyList<StartupPayloadItem> Items { get; }
}

public sealed record StartupPayloadItem(string Identifier, string Name, string Category);

internal static class StartupPayloadFactory
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IReadOnlyList<StartupPayloadItem> Create()
    {
        return Enumerable.Range(1, 500)
            .Select(index => new StartupPayloadItem(
                $"ITEM-{index:D5}",
                $"Startup payload item {index:N0}",
                $"Category {(index % 12) + 1:D2}"))
            .ToArray();
    }
}
