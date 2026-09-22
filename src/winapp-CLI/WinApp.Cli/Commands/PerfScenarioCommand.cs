// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.Performance;

namespace WinApp.Cli.Commands;

internal sealed class PerfScenarioCommand : Command, IShortDescription
{
    internal static readonly Argument<FileInfo> ScenarioArgument = new("scenario")
    {
        Description = "Versioned performance scenario JSON file.",
    };

    internal static readonly Argument<FileSystemInfo> TargetArgument = new("target")
    {
        Description = "Optional app target accepted by winapp run.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    internal static readonly Argument<string[]> PassthroughArgument = new("app-args")
    {
        Arity = ArgumentArity.ZeroOrMore,
        Hidden = true,
    };

    internal static readonly Option<int> WarmupOption = new("--warmup")
    {
        Description = "Warmup iterations retained in the set but excluded from statistics (default: 1; range: 0-20).",
        DefaultValueFactory = _ => 1,
    };

    internal static readonly Option<int> RepeatOption = new("--repeat")
    {
        Description = "Measured iterations (default: 5; range: 1-50).",
        DefaultValueFactory = _ => 5,
    };

    internal static readonly Option<string?> OutputOption = new("--output")
    {
        Description = "New atomic .winappperfset output directory.",
    };

    internal static readonly Option<string> ConfigurationOption = new("--configuration", "-c")
    {
        Description = "Project and single-file mode: build configuration (default: Release).",
        DefaultValueFactory = _ => "Release",
    };

    internal static readonly Option<string?> ArchOption = new("--arch")
    {
        Description = "Project and single-file mode: target architecture (x64, arm64, or x86).",
    };

    internal static readonly Option<string?> RuntimeOption = new("--runtime", "-r")
    {
        Description = "Project mode: target .NET runtime identifier.",
    };

    internal static readonly Option<string?> FrameworkOption = new("--framework", "-f")
    {
        Description = "Project mode: target framework moniker.",
    };

    internal static readonly Option<string?> ProjectOption = new("--project")
    {
        Description = "Select a project when the target is a solution or ambiguous directory.",
    };

    internal static readonly Option<bool> NoBuildOption = new("--no-build")
    {
        Description = "Run existing build output without building.",
    };

    internal static readonly Option<bool> NoRestoreOption = new("--no-restore")
    {
        Description = "Skip restore before building.",
    };

    internal static readonly Option<string[]> PropertyOption = new("--property", "-p")
    {
        Description = "MSBuild property as Name=Value. Repeatable.",
        Arity = ArgumentArity.ZeroOrMore,
        AllowMultipleArgumentsPerToken = false,
    };

    internal static readonly Option<string?> ArgsOption = new("--args")
    {
        Description = "Arguments to pass to the app. Alternatively, place arguments after --.",
    };

    public string ShortDescription => "Validate and run a repeatable UI performance scenario";

    public PerfScenarioCommand()
        : base("scenario", "Validate a versioned UI scenario and produce an atomic repeatable performance set.")
    {
        Arguments.Add(ScenarioArgument);
        Arguments.Add(TargetArgument);
        Arguments.Add(PassthroughArgument);
        Options.Add(WarmupOption);
        Options.Add(RepeatOption);
        Options.Add(OutputOption);
        Options.Add(ConfigurationOption);
        Options.Add(ArchOption);
        Options.Add(RuntimeOption);
        Options.Add(FrameworkOption);
        Options.Add(ProjectOption);
        Options.Add(NoBuildOption);
        Options.Add(NoRestoreOption);
        Options.Add(PropertyOption);
        Options.Add(ArgsOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    internal sealed class Handler(
        IPerformanceScenarioLoader loader,
        IPerformanceScenarioRunner runner,
        IPerformanceSetWriter writer,
        ICurrentDirectoryProvider currentDirectory,
        ILogger<PerfScenarioCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(
            ParseResult parseResult,
            CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            if (RunCommand.Handler.HasValuelessProperty(parseResult, PropertyOption))
            {
                return Fail(
                    parseResult,
                    json,
                    "A --property/-p option was provided without a value. Expected Name=Value (for example: -p WindowsPackageType=None).");
            }
            var warmup = parseResult.GetValue(WarmupOption);
            var repeat = parseResult.GetValue(RepeatOption);
            if (warmup is < 0 or > 20 || repeat is < 1 or > 50)
            {
                return Fail(parseResult, json, "--warmup must be 0-20 and --repeat must be 1-50.");
            }
            var output = parseResult.GetValue(OutputOption);
            if (string.IsNullOrWhiteSpace(output))
            {
                return Fail(parseResult, json, "--output is required.");
            }
            output = Path.GetFullPath(output, currentDirectory.GetCurrentDirectory());
            if (!output.EndsWith(".winappperfset", StringComparison.OrdinalIgnoreCase))
            {
                return Fail(parseResult, json, "--output must name a .winappperfset directory.");
            }

            try
            {
                var scenarioPath = parseResult.GetRequiredValue(ScenarioArgument).FullName;
                var scenario = loader.Load(scenarioPath);
                var iterationRoot = Path.Join(
                    Path.GetDirectoryName(output)!,
                    $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.iterations");
                PerformanceSetManifest manifest;
                try
                {
                    var runResult = await runner.RunAsync(
                        new(
                            scenario,
                            parseResult.GetValue(TargetArgument),
                            warmup,
                            repeat,
                            iterationRoot,
                            BuildAdditionalRunArguments(parseResult)),
                        cancellationToken);
                    manifest = writer.Write(output, scenario, warmup, repeat, runResult);
                }
                finally
                {
                    if (Directory.Exists(iterationRoot))
                    {
                        Directory.Delete(iterationRoot, recursive: true);
                    }
                }
                var result = new PerformanceScenarioCommandResult
                {
                    Status = manifest.Status,
                    Set = output,
                    FailureReason = manifest.FailureReason,
                    ScenarioId = manifest.Scenario.Id,
                    ScenarioHash = manifest.Scenario.DefinitionHash,
                    Warmup = warmup,
                    Repeat = repeat,
                    CompletedMeasuredIterations = manifest.Iterations.Count(iteration =>
                        !iteration.IsWarmup && iteration.Status == "completed"),
                };
                WriteResult(parseResult, result);
                return result.Status switch
                {
                    "completed" => 0,
                    "cancelled" => 130,
                    _ => 1,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return 130;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return Fail(parseResult, json, ex.Message);
            }
        }

        private int Fail(ParseResult parseResult, bool json, string message)
        {
            if (json)
            {
                parseResult.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(
                    new JsonErrorOutput { Error = message },
                    WinAppJsonContext.Default.JsonErrorOutput));
            }
            else
            {
                logger.LogError("{Message}", message);
            }
            return 1;
        }

        private static void WriteResult(
            ParseResult parseResult,
            PerformanceScenarioCommandResult result)
        {
            if (parseResult.GetValue(WinAppRootCommand.JsonOption))
            {
                parseResult.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(
                    result,
                    PerformanceJsonContext.Default.PerformanceScenarioCommandResult));
                return;
            }
            parseResult.InvocationConfiguration.Output.WriteLine(
                $"Performance scenario: {result.Status}");
            parseResult.InvocationConfiguration.Output.WriteLine(
                $"Scenario: {result.ScenarioId} ({result.ScenarioHash})");
            parseResult.InvocationConfiguration.Output.WriteLine(
                $"Measured iterations: {result.CompletedMeasuredIterations}/{result.Repeat}; warmups retained: {result.Warmup}");
            if (result.FailureReason is not null)
            {
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"Reason: {result.FailureReason}");
            }
            parseResult.InvocationConfiguration.Output.WriteLine($"Set: {result.Set}");
        }

        private static List<string> BuildAdditionalRunArguments(ParseResult parseResult)
        {
            var arguments = new List<string>();
            AddOption(arguments, "--configuration", parseResult.GetValue(ConfigurationOption));
            AddOption(arguments, "--arch", parseResult.GetValue(ArchOption));
            AddOption(arguments, "--runtime", parseResult.GetValue(RuntimeOption));
            AddOption(arguments, "--framework", parseResult.GetValue(FrameworkOption));
            AddOption(arguments, "--project", parseResult.GetValue(ProjectOption));
            AddOption(arguments, "--args", parseResult.GetValue(ArgsOption));
            if (parseResult.GetValue(NoBuildOption))
            {
                arguments.Add("--no-build");
            }
            if (parseResult.GetValue(NoRestoreOption))
            {
                arguments.Add("--no-restore");
            }
            foreach (var property in parseResult.GetValue(PropertyOption) ?? [])
            {
                AddOption(arguments, "--property", property);
            }
            var passthrough = parseResult.GetValue(PassthroughArgument) ?? [];
            if (passthrough.Length > 0)
            {
                arguments.Add("--");
                arguments.AddRange(passthrough);
            }
            return arguments;
        }

        private static void AddOption(List<string> arguments, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                arguments.Add(name);
                arguments.Add(value);
            }
        }
    }
}
