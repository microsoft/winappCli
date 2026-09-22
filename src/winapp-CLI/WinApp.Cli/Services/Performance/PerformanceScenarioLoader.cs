// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.Performance;

internal interface IPerformanceScenarioLoader
{
    ValidatedPerformanceScenario Load(string path);
}

internal static class PerformanceMetricCatalog
{
    private static readonly Dictionary<string, (string Unit, string Direction)> Definitions =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["startup.firstProcessMs"] = ("ms", "lower"),
            ["startup.firstWindowMs"] = ("ms", "lower"),
            ["startup.firstVisibleMs"] = ("ms", "lower"),
            ["startup.firstResponsiveMs"] = ("ms", "lower"),
            ["measure.durationMs"] = ("ms", "lower"),
            ["measure.cpuTimeMs"] = ("ms", "lower"),
            ["measure.averageCpuCores"] = ("cores", "lower"),
            ["measure.peakPrivateBytes"] = ("bytes", "lower"),
            ["measure.privateBytesChange"] = ("bytes", "lower"),
            ["measure.readBytes"] = ("bytes", "lower"),
            ["measure.writeBytes"] = ("bytes", "lower"),
            ["measure.failedProbeDurationMs"] = ("ms", "lower"),
        };

    public static bool TryGet(string name, out string unit, out string direction)
    {
        if (Definitions.TryGetValue(name, out var definition))
        {
            unit = definition.Unit;
            direction = definition.Direction;
            return true;
        }
        unit = string.Empty;
        direction = string.Empty;
        return false;
    }
}

internal sealed class PerformanceScenarioLoader(UiCommand uiCommand) : IPerformanceScenarioLoader
{
    internal const string SchemaVersion = "0.1";
    private const int MaximumStepsPerPhase = 100;
    private const int MaximumArgumentsPerStep = 64;
    private const int MaximumArgumentLength = 16_384;

    private static readonly string[] ForbiddenOptions =
    [
        "--app", "-a",
        "--window", "-w",
        "--output", "-o",
        "--json",
        "--caller",
        "--project-framework",
        "--verbose", "-v",
        "--quiet", "-q",
        "--help", "-h", "/?",
        "--allow-system-keys",
    ];

    public ValidatedPerformanceScenario Load(string path)
    {
        PerformanceScenarioDefinition definition;
        try
        {
            definition = JsonSerializer.Deserialize(
                File.ReadAllText(path),
                PerformanceJsonContext.Default.PerformanceScenarioDefinition)
                ?? throw new InvalidDataException("Scenario JSON is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Scenario JSON is invalid: {ex.Message}", ex);
        }

        ValidateIdentity(definition);
        var steps = new List<PerformanceScenarioStep>();
        ValidateSteps("setup", definition.Setup, steps);
        ValidateSteps("measure", definition.Measure, steps);
        ValidateSteps("cleanup", definition.Cleanup, steps);
        if (definition.Measure.Count == 0)
        {
            throw new InvalidDataException("Scenario measure must contain at least one step.");
        }

        var metrics = ValidateMetrics(definition.Metrics);
        var canonical = JsonSerializer.Serialize(
            definition,
            PerformanceJsonContext.Default.PerformanceScenarioDefinition);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
        return new(definition, hash, steps, metrics);
    }

    private static void ValidateIdentity(PerformanceScenarioDefinition definition)
    {
        if (!string.Equals(definition.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported scenario schemaVersion '{definition.SchemaVersion}'. Expected '{SchemaVersion}'.");
        }
        if (string.IsNullOrWhiteSpace(definition.Id)
            || definition.Id.Length > 64
            || definition.Id.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw new InvalidDataException(
                "Scenario id must be 1-64 ASCII letters, digits, '.', '_' or '-'.");
        }
        if (definition.Setup is null || definition.Measure is null || definition.Cleanup is null)
        {
            throw new InvalidDataException("Scenario setup, measure, and cleanup arrays are required.");
        }
        if (definition.Metrics is null || definition.Metrics.Count == 0)
        {
            throw new InvalidDataException("Scenario metrics must contain at least one metric.");
        }
    }

    private void ValidateSteps(
        string phase,
        IReadOnlyList<PerformanceScenarioStepDefinition> definitions,
        List<PerformanceScenarioStep> steps)
    {
        if (definitions.Count > MaximumStepsPerPhase)
        {
            throw new InvalidDataException(
                $"Scenario {phase} must contain no more than {MaximumStepsPerPhase} steps.");
        }

        foreach (var definition in definitions)
        {
            var ordinal = steps.Count + 1;
            if (definition.Ui is null
                || definition.Ui.Count == 0
                || definition.Ui.Count > MaximumArgumentsPerStep
                || definition.Ui.Any(argument =>
                    string.IsNullOrEmpty(argument) || argument.Length > MaximumArgumentLength))
            {
                throw new InvalidDataException(
                    $"Scenario {phase} step {ordinal} must contain 1-{MaximumArgumentsPerStep} bounded UI arguments.");
            }
            if (string.Equals(definition.Ui[0], "yield", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Scenario {phase} step {ordinal} cannot invoke 'ui yield'; scenario lifecycle owns yielding.");
            }
            if (definition.Ui.Any(IsForbiddenOption))
            {
                throw new InvalidDataException(
                    $"Scenario {phase} step {ordinal} cannot set app, window, output, JSON, help, or workflow-control options.");
            }

            var parseArguments = new List<string>(definition.Ui.Count + 2);
            parseArguments.AddRange(definition.Ui);
            parseArguments.Add("--window");
            parseArguments.Add("1");
            var parseResult = uiCommand.Parse(
                [.. parseArguments],
                WinAppParserConfiguration.Default);
            if (parseResult.Errors.Count != 0
                || ReferenceEquals(parseResult.CommandResult.Command, uiCommand))
            {
                throw new InvalidDataException(
                    $"Scenario {phase} step {ordinal} is not valid for exact HWND targeting through the winapp ui command parser.");
            }
            steps.Add(new()
            {
                Ordinal = ordinal,
                Phase = phase,
                Verb = parseResult.CommandResult.Command.Name,
            });
        }
    }

    private static bool IsForbiddenOption(string argument)
    {
        foreach (var option in ForbiddenOptions)
        {
            if (string.Equals(argument, option, StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase)
                || (option.Length == 2
                    && option[0] == '-'
                    && argument.StartsWith(option, StringComparison.OrdinalIgnoreCase)
                    && argument.Length > option.Length))
            {
                return true;
            }
        }
        return false;
    }

    private static PerformanceMetricDefinition[] ValidateMetrics(
        IReadOnlyList<PerformanceScenarioMetricTolerance> metrics)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var validated = new List<PerformanceMetricDefinition>(metrics.Count);
        foreach (var metric in metrics)
        {
            if (!names.Add(metric.Name))
            {
                throw new InvalidDataException($"Scenario metric '{metric.Name}' is duplicated.");
            }
            if (!PerformanceMetricCatalog.TryGet(metric.Name, out var unit, out var direction))
            {
                throw new InvalidDataException($"Scenario metric '{metric.Name}' is not in the fixed metric catalogue.");
            }
            if ((metric.AbsoluteTolerance is null) == (metric.PercentTolerance is null))
            {
                throw new InvalidDataException(
                    $"Scenario metric '{metric.Name}' must define exactly one absoluteTolerance or percentTolerance.");
            }
            if (metric.AbsoluteTolerance is < 0
                || metric.PercentTolerance is < 0
                || metric.AbsoluteTolerance is double.NaN or double.PositiveInfinity
                || metric.PercentTolerance is double.NaN or double.PositiveInfinity)
            {
                throw new InvalidDataException(
                    $"Scenario metric '{metric.Name}' tolerance must be a finite non-negative number.");
            }
            validated.Add(new()
            {
                Name = metric.Name,
                Unit = unit,
                Direction = direction,
                AbsoluteTolerance = metric.AbsoluteTolerance,
                PercentTolerance = metric.PercentTolerance,
            });
        }
        return validated.OrderBy(metric => metric.Name, StringComparer.Ordinal).ToArray();
    }
}
