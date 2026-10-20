// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.ComponentModel;
using System.Globalization;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal sealed class UiAccessibilityCommand : Command, IShortDescription
{
    public string ShortDescription => "Run configured accessibility workflows and attach assessments";

    public UiAccessibilityCommand(
        UiAccessibilitySurveyCommand survey,
        UiAccessibilityExploreCommand explore,
        UiAccessibilityJourneyCommand journey,
        UiAccessibilityLoopCommand loop,
        UiAccessibilityCheckCommand check,
        UiAccessibilityRepairCommand repair,
        UiAccessibilityAssessmentTemplateCommand assessmentTemplate,
        UiAccessibilityAssessCommand assess)
        : base("accessibility",
            "Run configured accessibility workflows and attach assessments using the local accessibility bundle. " +
            "Use 'winapp ui audit' to scan an existing app and its standard tab pages without a profile. " +
            "These active workflows require reviewed " +
            "profiles, journeys or presets and separate action approval; none provides autonomous AI route planning. " +
            "Results are not a whole-app compliance verdict. Worker results, stderr diagnostics and exit codes are preserved, " +
            "including with --quiet; --verbose adds native launcher diagnostics only.")
    {
        Subcommands.Add(survey);
        Subcommands.Add(explore);
        Subcommands.Add(journey);
        Subcommands.Add(loop);
        Subcommands.Add(check);
        Subcommands.Add(repair);
        Subcommands.Add(assessmentTemplate);
        Subcommands.Add(assess);
    }

    public sealed class Handler(IUiAccessibilityWorkerService worker) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var command = (UiAccessibilityOperationCommand)parseResult.CommandResult.Command;
            var json = command.SupportsJson && parseResult.GetValue(WinAppRootCommand.JsonOption);
            var error = parseResult.InvocationConfiguration.Error;
            var verbose = parseResult.GetValue(WinAppRootCommand.VerboseOption)
                && !parseResult.GetValue(WinAppRootCommand.QuietOption) && !json;

            int Fail(string code, string message)
            {
                if (json)
                {
                    UiJsonError.Emit(true, code, message, errorOut: error);
                }
                else
                {
                    error.WriteLine(message);
                }
                return 2;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (verbose)
                {
                    error.WriteLine($"Starting the bundled accessibility worker for '{command.Name}'.");
                }
                var exitCode = await worker.RunAsync(command.CreateWorkerArguments(parseResult),
                    parseResult.InvocationConfiguration.Output, error, cancellationToken);
                if (verbose)
                {
                    error.WriteLine($"Accessibility worker exited with code {exitCode}.");
                }
                return exitCode;
            }
            catch (OperationCanceledException)
            {
                return Fail("cancelled",
                    "Accessibility execution was cancelled. An interrupted run may contain only partial evidence; inspect it before resuming or retrying.");
            }
            catch (FileNotFoundException ex)
            {
                return Fail("accessibility_worker_missing", ex.Message);
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Fail("accessibility_worker_unavailable",
                    $"Could not run the bundled accessibility worker: {ex.Message}");
            }
        }
    }
}

internal abstract class UiAccessibilityOperationCommand(string name, string description)
    : Command(name, description), IShortDescription
{
    private readonly List<Action<ParseResult, List<string>>> _forwarders = [];
    private Argument<string?>? _input;

    public string ShortDescription => Description!;
    internal bool SupportsJson { get; private set; }

    protected Argument<string?> Input(string name, string description, bool required = true)
    {
        var argument = new Argument<string?>(name)
        {
            Description = description,
            Arity = required ? ArgumentArity.ExactlyOne : ArgumentArity.ZeroOrOne,
        };
        argument.Validators.Add(result => ValidateParsedValues(result, () =>
        {
            if (result.GetValue(argument) is { } value && !IsBackendValue(value))
            {
                result.AddError($"<{name}> must be a non-empty path not beginning with '--'.");
            }
        }));
        Arguments.Add(argument);
        _input = argument;
        return argument;
    }

    protected Option<string?> Text(string name, string description, bool required = false, string? defaultValue = null)
    {
        var option = new Option<string?>(name) { Description = description, Required = required };
        if (defaultValue is not null)
        {
            option.DefaultValueFactory = _ => defaultValue;
        }
        option.Validators.Add(result => ValidateParsedValues(result, () =>
        {
            if (result.GetValue(option) is not { } value || !IsBackendValue(value))
            {
                result.AddError($"{name} requires a non-empty value not beginning with '--'.");
            }
        }));
        AddValue(option, static value => value);
        return option;
    }

    protected Option<int?> Number(string name, string description, int minimum, int maximum, int? defaultValue = null)
    {
        var option = new Option<int?>(name) { Description = description };
        if (defaultValue is not null)
        {
            option.DefaultValueFactory = _ => defaultValue;
        }
        option.Validators.Add(result => ValidateParsedValues(result, () =>
        {
            if (result.GetValue(option) is { } value && (value < minimum || value > maximum))
            {
                result.AddError($"{name} must be {minimum}..{maximum}.");
            }
        }));
        AddValue(option, static value => value?.ToString(CultureInfo.InvariantCulture));
        return option;
    }

    protected Option<long?> Window()
    {
        var option = new Option<long?>("--window") { Description = "Positive decimal HWND from 'winapp ui list-windows'." };
        option.Validators.Add(result => ValidateParsedValues(result, () =>
        {
            if (result.GetValue(option) is <= 0)
            {
                result.AddError("--window must be a positive decimal HWND.");
            }
        }));
        AddValue(option, static value => value?.ToString(CultureInfo.InvariantCulture));
        return option;
    }

    protected Option<int?> Pid() => Number("--pid", "Process ID of the already-running target app.", 1, int.MaxValue);

    protected Option<string?> Output(bool required = false) =>
        Text("--output", "New evidence directory; existing evidence is never overwritten.", required);

    protected Option<bool> Flag(string name, string description, bool forward = true)
    {
        var option = new Option<bool>(name) { Description = description };
        ValidateBoolean(option);
        if (forward)
        {
            AddFlag(option);
        }
        else
        {
            Options.Add(option);
        }
        return option;
    }

    protected void Json()
    {
        SupportsJson = true;
        AddFlag(WinAppRootCommand.JsonOption);
    }

    protected void RequireActions()
    {
        var option = new Option<bool>("--accept-actions")
        {
            Description = "Explicitly approve building, launching and performing the declared real keyboard checks in the source-owned app.",
        };
        // These older backend verbs have no action-consent switch. Enforce consent here without
        // forwarding an option that the companion does not understand.
        Options.Add(option);
        ValidateBoolean(option);
        ValidateOptions(result =>
        {
            if (!result.GetValue(option))
            {
                result.AddError("--accept-actions is required to build, launch and perform the declared keyboard checks.");
            }
        });
    }

    protected void ValidateOptions(Action<CommandResult> validate) =>
        Validators.Add(result => ValidateParsedValues(result, () => validate(result)));

    private static void ValidateParsedValues(SymbolResult result, Action validate)
    {
        for (SymbolResult? current = result; current is not null; current = current.Parent)
        {
            if (current is CommandResult command && command.Children.OfType<OptionResult>()
                .Any(option => !option.Implicit && (option.Option is HelpOption || option.Option == WinAppRootCommand.CliSchemaOption)))
            {
                return;
            }
        }
        try
        {
            validate();
        }
        catch (InvalidOperationException ex)
        {
            // GetValue can throw before System.CommandLine records a conversion failure.
            // Preserve that failure as a parse error, never an unhandled exception or consent.
            result.AddError(ex.Message);
        }
    }

    protected Option<string[]> Identifiers(string name, string description)
    {
        var option = new Option<string[]>(name)
        {
            Description = description,
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        option.Validators.Add(result => ValidateParsedValues(result, () =>
        {
            var values = result.GetValue(option) ?? [];
            if (values.Any(value => !IsIdentifier(value)) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            {
                result.AddError($"{name} requires unique literal ASCII identifiers (letters, digits, '_', '-' or '.', not '.' or '..'; maximum 256 characters).");
            }
        }));
        Options.Add(option);
        _forwarders.Add((result, arguments) =>
        {
            foreach (var value in result.GetValue(option) ?? [])
            {
                arguments.Add(option.Name);
                arguments.Add(value);
            }
        });
        return option;
    }

    protected static bool IsIdentifier(string value) =>
        value.Length is > 0 and <= 256 && value is not ("." or "..")
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    protected static void RequireTarget(CommandResult result, Option<int?> pid, Option<long?> window, Option<bool>? owned = null)
    {
        var count = (result.GetValue(pid) is not null ? 1 : 0) + (result.GetValue(window) is not null ? 1 : 0)
            + (owned is not null && result.GetValue(owned) ? 1 : 0);
        if (count != 1)
        {
            result.AddError(owned is null
                ? "Supply exactly one of --pid or --window."
                : "Supply exactly one of --pid, --window or --launch-owned.");
        }
    }

    internal virtual IReadOnlyList<string> CreateWorkerArguments(ParseResult result)
    {
        var arguments = new List<string> { Name };
        if (_input is not null && result.GetValue(_input) is { } input)
        {
            arguments.Add(input);
        }
        foreach (var forward in _forwarders)
        {
            forward(result, arguments);
        }
        return arguments;
    }

    private static bool IsBackendValue(string value) =>
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith("--", StringComparison.Ordinal);

    private static void ValidateBoolean(Option<bool> option) =>
        option.Validators.Add(result =>
        {
            if (result.Tokens.Any(token => !bool.TryParse(token.Value, out _)))
            {
                result.AddError($"{option.Name} accepts only true or false; omit the value to enable it.");
            }
        });

    private void AddValue<T>(Option<T> option, Func<T?, string?> format)
    {
        Options.Add(option);
        _forwarders.Add((result, arguments) =>
        {
            if (result.GetResult(option) is { Implicit: false } && format(result.GetValue(option)) is { } value)
            {
                arguments.Add(option.Name);
                arguments.Add(value);
            }
        });
    }

    private void AddFlag(Option<bool> option)
    {
        Options.Add(option);
        _forwarders.Add((result, arguments) =>
        {
            if (result.GetValue(option))
            {
                arguments.Add(option.Name);
            }
        });
    }
}
