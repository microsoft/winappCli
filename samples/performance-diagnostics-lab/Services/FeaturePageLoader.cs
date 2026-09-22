using Microsoft.UI.Xaml.Controls;
using System.Reflection;

namespace PerformanceDiagnosticsLab.Services;

public sealed class FeaturePageLoader
{
    private static readonly PageDescriptor StartupPayload = new(
        "PerformanceDiagnosticsLab.Page.StartupPayload",
        "PerformanceDiagnosticsLab.Features.StartupPayload.StartupPayloadPage");

    private static readonly PageDescriptor Rendering = new(
        "PerformanceDiagnosticsLab.Page.Rendering",
        "PerformanceDiagnosticsLab.Features.Rendering.RenderingPage");

    private Page? _startupPayload;
    private Page? _renderingPage;

    public void PreloadStartupPayload()
    {
        _startupPayload ??= Create(StartupPayload);
    }

    public Page GetOrCreateRenderingPage()
    {
        return _renderingPage ??= Create(Rendering);
    }

    private static Page Create(PageDescriptor descriptor)
    {
        var assembly = Assembly.Load(new AssemblyName(descriptor.AssemblyName));
        var type = assembly.GetType(descriptor.TypeName, throwOnError: true)!;
        return (Page)Activator.CreateInstance(type)!;
    }

    private sealed record PageDescriptor(string AssemblyName, string TypeName);
}
