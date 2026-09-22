using PerformanceDiagnosticsLab.Contracts;
using System.Runtime.CompilerServices;

namespace PerformanceDiagnosticsLab.Startup.Core;

public sealed class CoreStartupModule : IStartupModule
{
    public string Name => "Core";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Initialize(StartupContext context)
    {
        ConfigurationBootstrapper.Load(context);
    }
}

internal static class ConfigurationBootstrapper
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Load(StartupContext context)
    {
        var sections = ConfigurationSectionReader.Read();
        ConfigurationGraphBuilder.Build(sections, context.Mode);
    }
}

internal static class ConfigurationSectionReader
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string[] Read()
    {
        return Enumerable.Range(0, 256)
            .Select(index => $"Feature:{index:D3}:Enabled={(index % 3 != 0)}")
            .ToArray();
    }
}

internal static class ConfigurationGraphBuilder
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Build(IReadOnlyList<string> sections, StartupMode mode)
    {
        var hash = 17;
        for (var pass = 0; pass < 1_000; pass++)
        {
            foreach (var section in sections)
            {
                hash = HashCode.Combine(hash, section.Length, pass);
            }
        }

        FeaturePolicyResolver.Resolve(hash, mode);
    }
}

internal static class FeaturePolicyResolver
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Resolve(int configurationHash, StartupMode mode)
    {
        var policy = HashCode.Combine(configurationHash, mode);
        GC.KeepAlive(policy);
    }
}
