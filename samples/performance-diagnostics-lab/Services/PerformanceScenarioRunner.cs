using PerformanceDiagnosticsLab.Models;
using System.Diagnostics;

namespace PerformanceDiagnosticsLab.Services;

public sealed class PerformanceScenarioRunner
{
    public async Task RunAsync(
        ScenarioDefinition scenario,
        LaunchOptions options,
        CancellationToken cancellationToken)
    {
        var durationMilliseconds = options.DurationMilliseconds ?? scenario.DurationMilliseconds;

        switch (scenario.Id)
        {
            case "baseline":
                await Task.Delay(durationMilliseconds, cancellationToken);
                break;
            case "ui-stall-100":
            case "ui-stall-1000":
            case "ui-stall-4000":
            case "ui-stall-6000":
                Thread.Sleep(durationMilliseconds);
                break;
            case "ui-cpu":
                BurnCpu(durationMilliseconds, CancellationToken.None);
                break;
            case "background-cpu":
                await Task.Run(() => BurnCpu(durationMilliseconds, cancellationToken), cancellationToken);
                break;
            case "deep-call-stack":
                await Task.Run(
                    () => DeepCallStackScenario.Execute(durationMilliseconds, cancellationToken),
                    cancellationToken);
                break;
            case "memory-step":
                await RunMemoryStepAsync(
                    options.MemoryMegabytes ?? scenario.MemoryMegabytes,
                    durationMilliseconds,
                    cancellationToken);
                break;
            case "file-io":
                await RunFileIoAsync(cancellationToken);
                break;
            case "normal-exit":
                await Task.Delay(durationMilliseconds, cancellationToken);
                App.Window.Close();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario.Id, "Unknown performance scenario.");
        }
    }

    private static void BurnCpu(int durationMilliseconds, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var value = 0.5;
        while (stopwatch.ElapsedMilliseconds < durationMilliseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            value = Math.Sqrt(value + 1.23456789);
        }

        GC.KeepAlive(value);
    }

    private static async Task RunMemoryStepAsync(
        int memoryMegabytes,
        int holdMilliseconds,
        CancellationToken cancellationToken)
    {
        const int chunkSize = 1024 * 1024;
        var chunks = new byte[memoryMegabytes][];

        for (var index = 0; index < chunks.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chunks[index] = GC.AllocateUninitializedArray<byte>(chunkSize);
            for (var offset = 0; offset < chunkSize; offset += Environment.SystemPageSize)
            {
                chunks[index][offset] = 1;
            }
        }

        await Task.Delay(holdMilliseconds, cancellationToken);
        GC.KeepAlive(chunks);
        chunks = null!;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task RunFileIoAsync(CancellationToken cancellationToken)
    {
        const int totalMegabytes = 64;
        var path = Path.Combine(Path.GetTempPath(), $"winapp-perf-lab-{Environment.ProcessId}.tmp");
        var buffer = GC.AllocateUninitializedArray<byte>(1024 * 1024);
        new Random(42).NextBytes(buffer);

        try
        {
            await using (var output = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                for (var index = 0; index < totalMegabytes; index++)
                {
                    await output.WriteAsync(buffer, cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
            }

            await using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (await input.ReadAsync(buffer, cancellationToken) > 0)
            {
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
