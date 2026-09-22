using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PerformanceDiagnosticsLab.Features.Rendering;

public sealed partial class RenderingPage : Page
{
    private const int TileCount = 144;
    private const int MaximumDurationSeconds = 15;

    private readonly List<RenderTile> _tiles = [];
    private readonly Stopwatch _duration = new();
    private int _frame;
    private bool _isRunning;

    public RenderingPage()
    {
        InitializeComponent();
    }

    private void RenderingPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_tiles.Count == 0)
        {
            CreateScene();
        }
    }

    private void RenderingPage_Unloaded(object sender, RoutedEventArgs e)
    {
        StopRenderingStress("Idle");
    }

    private void StartRenderingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        _frame = 0;
        _duration.Restart();
        StartRenderingButton.IsEnabled = false;
        StopRenderingButton.IsEnabled = true;
        RenderingStatus.Text = "Running";
        CompositionTarget.Rendering += CompositionTarget_Rendering;
    }

    private void StopRenderingButton_Click(object sender, RoutedEventArgs e)
    {
        StopRenderingStress("Stopped");
    }

    private void ResetRenderingButton_Click(object sender, RoutedEventArgs e)
    {
        StopRenderingStress("Idle");
        ResetScene();
    }

    private void CompositionTarget_Rendering(object? sender, object e)
    {
        if (_duration.Elapsed.TotalSeconds >= MaximumDurationSeconds)
        {
            StopRenderingStress("Completed");
            return;
        }

        _frame++;
        if (_frame % 2 != 0)
        {
            return;
        }

        KnownRenderingHotspot.UpdateFrame(
            _tiles,
            _frame,
            Math.Max(1, RenderingSurface.ActualWidth),
            Math.Max(1, RenderingSurface.ActualHeight));
    }

    private void CreateScene()
    {
        var random = new Random(42);
        var background = (Brush)Application.Current.Resources["AccentFillColorSecondaryBrush"];
        var borderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

        for (var index = 0; index < TileCount; index++)
        {
            var width = random.Next(54, 100);
            var height = random.Next(36, 72);
            var border = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(8),
                Background = background,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                Opacity = 0.55 + ((index % 5) * 0.1),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = $"{index + 1:D3}",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            if (index % 6 == 0)
            {
                border.Shadow = new ThemeShadow();
                border.Translation = new Vector3(0, 0, 8);
            }

            var tile = new RenderTile(
                border,
                random.NextDouble(),
                random.NextDouble(),
                width,
                height,
                random.NextDouble() * Math.PI * 2);
            _tiles.Add(tile);
            RenderingSurface.Children.Add(border);
        }

        ResetScene();
    }

    private void ResetScene()
    {
        var width = Math.Max(1, RenderingSurface.ActualWidth);
        var height = Math.Max(1, RenderingSurface.ActualHeight);

        foreach (var tile in _tiles)
        {
            tile.Element.Width = tile.BaseWidth;
            tile.Element.Height = tile.BaseHeight;
            Canvas.SetLeft(tile.Element, tile.XRatio * Math.Max(1, width - tile.BaseWidth));
            Canvas.SetTop(tile.Element, tile.YRatio * Math.Max(1, height - tile.BaseHeight));
        }
    }

    private void StopRenderingStress(string status)
    {
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        _duration.Stop();
        _isRunning = false;
        StartRenderingButton.IsEnabled = true;
        StopRenderingButton.IsEnabled = false;
        RenderingStatus.Text = status;
    }
}

internal sealed record RenderTile(
    Border Element,
    double XRatio,
    double YRatio,
    double BaseWidth,
    double BaseHeight,
    double Phase);

internal static class KnownRenderingHotspot
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void UpdateFrame(
        IReadOnlyList<RenderTile> tiles,
        int frame,
        double surfaceWidth,
        double surfaceHeight)
    {
        for (var index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];
            var phase = tile.Phase + (frame * 0.035) + (index * 0.01);
            var width = tile.BaseWidth + (Math.Sin(phase) * 12);
            var height = tile.BaseHeight + (Math.Cos(phase * 0.9) * 8);

            tile.Element.Width = width;
            tile.Element.Height = height;
            tile.Element.Opacity = 0.45 + ((Math.Sin(phase * 0.7) + 1) * 0.25);
            Canvas.SetLeft(
                tile.Element,
                (tile.XRatio * Math.Max(1, surfaceWidth - width)) + (Math.Sin(phase) * 18));
            Canvas.SetTop(
                tile.Element,
                (tile.YRatio * Math.Max(1, surfaceHeight - height)) + (Math.Cos(phase) * 12));
        }
    }
}
