using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace AccessLoop.StateDemo;

internal static class WindowSizing
{
    internal static void ResizeAndCenter(Window window, FrameworkElement root, int width, int height)
    {
        var scale = root.XamlRoot.RasterizationScale;
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        window.AppWindow.ResizeClient(new SizeInt32(
            (int)Math.Min(width * scale, Math.Max(1, area.Width - 32 * scale)),
            (int)Math.Min(height * scale, Math.Max(1, area.Height - 56 * scale))));

        var size = window.AppWindow.Size;
        window.AppWindow.Move(new PointInt32(
            area.X + (area.Width - size.Width) / 2,
            area.Y + (area.Height - size.Height) / 2));
    }
}
