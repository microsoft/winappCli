// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.Performance;

internal interface IPerformanceSetComparer
{
    PerformanceComparisonResult Compare(string baselineSet, string candidateSet);
}

internal sealed class PerformanceSetComparer : IPerformanceSetComparer
{
    public PerformanceComparisonResult Compare(string baselineSet, string candidateSet)
    {
        try
        {
            var baseline = Read(baselineSet);
            var candidate = Read(candidateSet);
            ValidateCompatibility(baseline, candidate);
            var comparisons = baseline.MetricDefinitions
                .OrderBy(definition => definition.Name, StringComparer.Ordinal)
                .Select(definition => CompareMetric(definition, baseline, candidate))
                .ToArray();
            return new()
            {
                Status = comparisons.All(metric => metric.Status == "compared")
                    ? "completed"
                    : "invalid",
                BaselineSet = Path.GetFullPath(baselineSet),
                CandidateSet = Path.GetFullPath(candidateSet),
                Metrics = comparisons,
            };
        }
        catch (IncompletePerformanceSetException ex)
        {
            return InvalidResult("incomplete", ex.Message, baselineSet, candidateSet);
        }
        catch (Exception ex) when (ex is IOException
                                    or JsonException
                                    or InvalidDataException
                                    or ArgumentException
                                    or InvalidOperationException)
        {
            return InvalidResult("invalid", ex.Message, baselineSet, candidateSet);
        }
    }

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            throw new InvalidDataException("A median requires at least one value.");
        }
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2d;
    }

    private static PerformanceSetManifest Read(string directory)
    {
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Join(directory, "manifest.json")),
            PerformanceJsonContext.Default.PerformanceSetManifest)
            ?? throw new InvalidDataException("Performance set manifest is empty.");
        if (manifest.SchemaVersion != PerformanceSetWriter.SchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported performance set schema '{manifest.SchemaVersion}'.");
        }
        if (manifest.Status != "completed")
        {
            throw new IncompletePerformanceSetException(
                $"Performance set status is '{manifest.Status}', not 'completed'.");
        }
        return manifest;
    }

    private static void ValidateCompatibility(
        PerformanceSetManifest baseline,
        PerformanceSetManifest candidate)
    {
        if (baseline.Scenario.DefinitionHash != candidate.Scenario.DefinitionHash)
        {
            throw new InvalidDataException("Scenario definition hashes do not match.");
        }
        if (baseline.Warmup != candidate.Warmup || baseline.Repeat != candidate.Repeat)
        {
            throw new InvalidDataException("Warmup or repeat counts do not match.");
        }
        if (baseline.Conditions != candidate.Conditions)
        {
            throw new InvalidDataException(
                "Architecture, OS, logical processor count, probe, cadence, or recording schema conditions do not match.");
        }
        var baselineDefinitions = baseline.MetricDefinitions
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();
        var candidateDefinitions = candidate.MetricDefinitions
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();
        if (!baselineDefinitions.SequenceEqual(candidateDefinitions))
        {
            throw new InvalidDataException("Metric definitions do not match.");
        }
        if (baselineDefinitions.Select(definition => definition.Name).Distinct(StringComparer.Ordinal).Count()
            != baselineDefinitions.Length
            || baselineDefinitions.Any(definition =>
                (definition.AbsoluteTolerance is null) == (definition.PercentTolerance is null)
                || definition.AbsoluteTolerance is < 0
                || definition.PercentTolerance is < 0
                || definition.Direction is not ("lower" or "higher")))
        {
            throw new InvalidDataException("Metric definitions are invalid.");
        }
        ValidateIterations(baseline);
        ValidateIterations(candidate);
    }

    private static void ValidateIterations(PerformanceSetManifest set)
    {
        var measured = set.Iterations.Where(iteration => !iteration.IsWarmup).ToArray();
        if (measured.Length != set.Repeat)
        {
            throw new IncompletePerformanceSetException(
                $"Expected {set.Repeat} measured iterations but found {measured.Length}.");
        }
        if (measured.Any(iteration => iteration.Status != "completed"))
        {
            throw new IncompletePerformanceSetException(
                "Every measured iteration must have completed successfully.");
        }
        foreach (var iteration in measured)
        {
            var values = iteration.Metrics.ToDictionary(metric => metric.Name, StringComparer.Ordinal);
            if (set.MetricDefinitions.Any(definition =>
                    !values.TryGetValue(definition.Name, out var value) || value.Value is null))
            {
                throw new IncompletePerformanceSetException(
                    $"Measured iteration {iteration.Ordinal} is missing a required metric.");
            }
        }
    }

    private static PerformanceMetricComparison CompareMetric(
        PerformanceMetricDefinition definition,
        PerformanceSetManifest baseline,
        PerformanceSetManifest candidate)
    {
        var baselineMedian = Median(Values(baseline, definition.Name));
        var candidateMedian = Median(Values(candidate, definition.Name));
        double allowed;
        if (definition.AbsoluteTolerance is { } absolute)
        {
            allowed = absolute;
        }
        else if (definition.PercentTolerance is { } percent)
        {
            allowed = Math.Abs(baselineMedian) * percent / 100d;
        }
        else
        {
            return new()
            {
                Name = definition.Name,
                Unit = definition.Unit,
                Direction = definition.Direction,
                BaselineMedian = baselineMedian,
                CandidateMedian = candidateMedian,
                Status = "invalid",
                Error = "Metric must define exactly one tolerance.",
            };
        }

        return new()
        {
            Name = definition.Name,
            Unit = definition.Unit,
            Direction = definition.Direction,
            BaselineMedian = baselineMedian,
            CandidateMedian = candidateMedian,
            Difference = candidateMedian - baselineMedian,
            AbsoluteTolerance = definition.AbsoluteTolerance,
            PercentTolerance = definition.PercentTolerance,
            AllowedDifference = allowed,
            Status = "compared",
        };
    }

    private static IEnumerable<double> Values(PerformanceSetManifest set, string name) =>
        set.Iterations
            .Where(iteration => !iteration.IsWarmup)
            .Select(iteration => iteration.Metrics.Single(metric => metric.Name == name).Value!.Value);

    private static PerformanceComparisonResult InvalidResult(
        string status,
        string error,
        string baselineSet,
        string candidateSet) => new()
        {
            Status = status,
            Error = error,
            BaselineSet = Path.GetFullPath(baselineSet),
            CandidateSet = Path.GetFullPath(candidateSet),
            Metrics = [],
        };

    private sealed class IncompletePerformanceSetException(string message) : Exception(message);
}
