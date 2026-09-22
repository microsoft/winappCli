using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PerformanceDiagnosticsLab.Services;

internal static class DeepCallStackScenario
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Execute(int durationMilliseconds, CancellationToken cancellationToken)
    {
        var result = RequestPipeline.Execute(durationMilliseconds, cancellationToken);
        GC.KeepAlive(result);
    }
}

internal static class RequestPipeline
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double Execute(int durationMilliseconds, CancellationToken cancellationToken)
    {
        return DiagnosticsService.Process(durationMilliseconds, cancellationToken) + 1;
    }
}

internal static class DiagnosticsService
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double Process(int durationMilliseconds, CancellationToken cancellationToken)
    {
        return PayloadReader.Read(durationMilliseconds, cancellationToken) + 1;
    }
}

internal static class PayloadReader
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double Read(int durationMilliseconds, CancellationToken cancellationToken)
    {
        return PayloadParser.Parse(durationMilliseconds, cancellationToken) + 1;
    }
}

internal static class PayloadParser
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double Parse(int durationMilliseconds, CancellationToken cancellationToken)
    {
        return RecordValidator.Validate(durationMilliseconds, cancellationToken) + 1;
    }
}

internal static class RecordValidator
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double Validate(int durationMilliseconds, CancellationToken cancellationToken)
    {
        return IndexBuilder.Build(durationMilliseconds, cancellationToken) + 1;
    }
}

internal static class IndexBuilder
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double Build(int durationMilliseconds, CancellationToken cancellationToken)
    {
        return ResultAggregator.Aggregate(durationMilliseconds, cancellationToken) + 1;
    }
}

internal static class ResultAggregator
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double Aggregate(int durationMilliseconds, CancellationToken cancellationToken)
    {
        return ResultScorer.Score(durationMilliseconds, cancellationToken) + 1;
    }
}

internal static class ResultScorer
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double Score(int durationMilliseconds, CancellationToken cancellationToken)
    {
        return KnownDeepCpuHotspot.Execute(durationMilliseconds, cancellationToken) + 1;
    }
}

internal static class KnownDeepCpuHotspot
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static double Execute(int durationMilliseconds, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var value = 0.61803398875;

        while (stopwatch.ElapsedMilliseconds < durationMilliseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var iteration = 1; iteration <= 50_000; iteration++)
            {
                value = Math.Sqrt(value + iteration);
            }
        }

        return value;
    }
}
