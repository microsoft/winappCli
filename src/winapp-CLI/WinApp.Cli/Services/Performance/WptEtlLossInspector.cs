// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

internal sealed record EtlLossInspection
{
    public required string Status { get; init; }
    public int? LostBuffers { get; init; }
    public int? LostEvents { get; init; }
    public string? ToolVersion { get; init; }
    public string? Error { get; init; }
}

internal interface IEtlLossInspector
{
    Task<EtlLossInspection> InspectAsync(
        string etlPath,
        CancellationToken cancellationToken);
}

internal sealed class WptEtlLossInspector(
    IWptXamlToolResolver toolResolver,
    IProcessRunner processRunner) : IEtlLossInspector
{
    private static readonly TimeSpan InspectionTimeout = TimeSpan.FromMinutes(2);

    public async Task<EtlLossInspection> InspectAsync(
        string etlPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(etlPath))
        {
            return NotInspected("The ETL artifact was not produced.");
        }

        var resolution = toolResolver.Resolve();
        if (resolution.XperfPath is null)
        {
            return NotInspected(
                "Microsoft-signed xperf.exe was not found in the compatible Windows Performance Toolkit installation.",
                resolution.XperfVersion);
        }

        ProcessRunResult result;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(InspectionTimeout);
        try
        {
            result = await processRunner.RunAsync(
                new(
                    resolution.XperfPath,
                    [
                        "-i",
                        Path.GetFullPath(etlPath),
                        "-a",
                        "tracestats",
                    ]),
                cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && timeout.IsCancellationRequested)
        {
            return Failed(
                $"xperf tracestats did not complete within {InspectionTimeout.TotalSeconds:0} seconds.",
                resolution.XperfVersion);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return Failed(ex.Message, resolution.XperfVersion);
        }

        if (result.ExitCode != 0)
        {
            var error = FirstLine(result.StandardError)
                ?? FirstLine(result.StandardOutput)
                ?? $"xperf tracestats exited with code {result.ExitCode}.";
            return Failed(error, resolution.XperfVersion);
        }

        try
        {
            var lostBuffers = ParseTotal(result.StandardOutput, "Total # Lost Buffers");
            var lostEvents = ParseTotal(result.StandardOutput, "Total # Lost Events");
            return new()
            {
                Status = lostBuffers == 0 && lostEvents == 0 ? "none" : "detected",
                LostBuffers = lostBuffers,
                LostEvents = lostEvents,
                ToolVersion = resolution.XperfVersion,
            };
        }
        catch (InvalidDataException ex)
        {
            return Failed(ex.Message, resolution.XperfVersion);
        }
    }

    private static int ParseTotal(string output, string label)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator < 0
                || !line[..separator].Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(
                line.AsSpan(separator + 1).Trim(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
                && value >= 0)
            {
                return value;
            }

            break;
        }

        throw new InvalidDataException($"xperf tracestats did not report a valid '{label}' value.");
    }

    private static EtlLossInspection NotInspected(
        string error,
        string? toolVersion = null) => new()
        {
            Status = "not-inspected",
            ToolVersion = toolVersion,
            Error = error,
        };

    private static EtlLossInspection Failed(
        string error,
        string? toolVersion) => new()
        {
            Status = "inspection-failed",
            ToolVersion = toolVersion,
            Error = error,
        };

    private static string? FirstLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
}
