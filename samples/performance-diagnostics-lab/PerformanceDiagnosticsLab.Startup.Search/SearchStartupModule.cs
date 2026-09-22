using PerformanceDiagnosticsLab.Contracts;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PerformanceDiagnosticsLab.Startup.Search;

public sealed class SearchStartupModule : IStartupModule
{
    public string Name => "Search";

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Initialize(StartupContext _)
    {
        SearchCatalog.Prepare();
    }
}

internal static class SearchCatalog
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Prepare()
    {
        var documents = DocumentSource.Create();
        var tokens = SearchTokenizer.Tokenize(documents);
        PostingListBuilder.Build(tokens);
        KnownStartupHotspot.ConsumeCpu();
    }
}

internal static class DocumentSource
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string[] Create()
    {
        return Enumerable.Range(0, 20_000)
            .Select(index => $"document-{index:D5} diagnostics performance startup")
            .ToArray();
    }
}

internal static class SearchTokenizer
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string[] Tokenize(IEnumerable<string> documents)
    {
        return documents.SelectMany(document => document.Split(' ')).ToArray();
    }
}

internal static class PostingListBuilder
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Build(IEnumerable<string> tokens)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            index[token] = index.GetValueOrDefault(token) + 1;
        }

        GC.KeepAlive(index);
    }
}

internal static class KnownStartupHotspot
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ConsumeCpu()
    {
        const int durationMilliseconds = 750;
        var stopwatch = Stopwatch.StartNew();
        var value = 0.61803398875;
        while (stopwatch.ElapsedMilliseconds < durationMilliseconds)
        {
            for (var iteration = 0; iteration < 20_000; iteration++)
            {
                value = Math.Sqrt(value + iteration + 1);
            }
        }

        GC.KeepAlive(value);
    }
}
