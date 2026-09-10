// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.Controls;

// Regenerates the find-ui corpus committed under
// src/winapp-CLI/WinApp.Cli/Services/Controls/Data and embedded in the shipped CLI.
//
// Run by scripts/build-cli.ps1 on -Stable, and by the find-ui corpus drift workflow
// (which bakes to a throwaway directory purely to compare against what is committed).
// It is intentionally not reachable from the winapp CLI itself — see the csproj.
//
// With --emit-index it instead writes each source as a sample index in the shared
// contract (docs/winui-sample-index.schema.json). That output is the artifact offered to
// the repositories that own the samples (#703); it is never committed here and never
// ships. See SnapshotBaker.EmitIndexesAsync.
//
// Exit codes: 0 = complete run, 1 = usage error or an incomplete/failed run.
// A partial result is never written as if it were complete, so a non-zero exit means
// the committed corpus is untouched and safe to keep.

const string EmitIndexFlag = "--emit-index";

// The bake invocation is left exactly as it was — build-cli.ps1 and the drift workflow
// both call it positionally, so index emission is an opt-in flag rather than a change
// to how the tool is normally driven.
var emitIndex = args.Length > 0 && string.Equals(args[0], EmitIndexFlag, StringComparison.Ordinal);
var positional = emitIndex ? args[1..] : args;

if (positional.Length != 1 || string.IsNullOrWhiteSpace(positional[0]))
{
    Console.Error.WriteLine($"usage: WinApp.Cli.SnapshotBaker [{EmitIndexFlag}] <output-directory>");
    Console.Error.WriteLine(
        "  bake the embedded corpus (the default; what the release path runs):\n" +
        "    dotnet run --project src/winapp-CLI/WinApp.Cli.SnapshotBaker -- " +
        "src/winapp-CLI/WinApp.Cli/Services/Controls/Data");
    Console.Error.WriteLine(
        $"  generate upstream sample indexes into a scratch directory (never committed):\n" +
        $"    dotnet run --project src/winapp-CLI/WinApp.Cli.SnapshotBaker -- {EmitIndexFlag} ./artifacts/indexes");
    return 1;
}

var outputDirectory = positional[0];

// Ctrl+C should abandon the bake rather than leave it half-written; BakeAsync stages the
// whole set and only moves it into place once every source and the manifest have succeeded.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    if (emitIndex)
    {
        var indexFailures = await SnapshotBaker
            .EmitIndexesAsync(outputDirectory, Console.WriteLine, cancellation.Token)
            .ConfigureAwait(false);

        if (indexFailures.Count > 0)
        {
            Console.Error.WriteLine(
                $"Index generation incomplete — no fresh data fetched for: {string.Join(", ", indexFailures)}.");
            return 1;
        }

        return 0;
    }

    var failures = await SnapshotBaker
        .BakeAsync(outputDirectory, Console.WriteLine, cancellation.Token)
        .ConfigureAwait(false);

    if (failures.Count > 0)
    {
        Console.Error.WriteLine(
            $"Bake incomplete — no fresh data fetched for: {string.Join(", ", failures)}. " +
            "The existing committed snapshot was left untouched.");
        return 1;
    }

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(emitIndex ? "Index generation cancelled." : "Bake cancelled.");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{(emitIndex ? "Index generation" : "Bake")} failed: {ex.Message}");
    return 1;
}
