// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools default [on|off|headless]</c>: shows or sets how <c>winapp run</c> starts DevTools for a WinUI
/// project when <c>--devtools</c> isn't given. The DevTools toolbar's menu changes the same setting.
/// </summary>
internal class DevToolsDefaultCommand : Command, IShortDescription, IHelpExamples
{
    public string ShortDescription => "Show or set the DevTools mode winapp run uses by default";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools default",
        "winapp devtools default off",
        "winapp devtools default headless",
    ];

    internal static Argument<DevToolsMode?> ModeArgument { get; } = new("mode")
    {
        HelpName = "on|off|headless",
        Description = "on (the in-app toolbar), headless (no toolbar; inspect from the CLI) or off. Omit to show the current default.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsDefaultCommand()
        : base("default", "Show or set the DevTools mode winapp run uses for a WinUI project when --devtools isn't given. " +
            "Runs in CI (the CI environment variable is set) stay off unless --devtools is given.")
    {
        Arguments.Add(ModeArgument);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(IAnsiConsole ansiConsole, ILogger<DevToolsDefaultCommand> logger) : AsynchronousCommandLineAction
    {
        internal string? StateDirectory { get; set; }

        internal Func<string?> ReadCiVariable { get; set; } = () => Environment.GetEnvironmentVariable("CI");

        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            if (!ExecutionTargetSelection.Resolve(parseResult).IsLocal)
            {
                return Task.FromResult(Fail("'winapp devtools default' is a setting on this machine and doesn't take --on.", json));
            }
            var requested = parseResult.GetValue(ModeArgument);
            try
            {
                if (requested is { } mode)
                {
                    DevToolsDefaultSetting.Write(mode, StateDirectory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(Fail($"Couldn't save the DevTools default: {ex.Message}", json));
            }

            var saved = DevToolsDefaultSetting.Read(StateDirectory);
            var current = saved ?? DevToolsMode.On;
            var source = saved is null ? "default" : "setting";
            var ci = DevToolsResolution.IsCi(ReadCiVariable());
            var name = current.ToString().ToLowerInvariant();
            if (json)
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("mode", name);
                    writer.WriteString("source", source);
                    writer.WriteString("settingFile", DevToolsDefaultSetting.FilePath(StateDirectory));
                    writer.WriteBoolean("ci", ci);
                    writer.WriteEndObject();
                }
                ansiConsole.Profile.Out.Writer.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
                return Task.FromResult(0);
            }

            var what = current switch
            {
                DevToolsMode.On => "winapp run starts WinUI projects with DevTools and its toolbar.",
                DevToolsMode.Headless => "winapp run starts WinUI projects with DevTools but no toolbar; inspect them with 'winapp devtools'.",
                _ => "winapp run starts WinUI projects without DevTools; add --devtools on to use it.",
            };
            ansiConsole.WriteLine(requested is null
                ? $"DevTools default: {name} ({(saved is null ? "built in" : "your setting")}). {what}"
                : $"DevTools default set to {name}. {what}");
            if (ci)
            {
                ansiConsole.WriteLine("CI is set, so winapp run stays off here unless --devtools is given.");
            }
            logger.LogDebug("DevTools default {Mode} from {Source}", name, source);
            return Task.FromResult(0);
        }

        private int Fail(string message, bool json)
        {
            if (json)
            {
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("error", message);
                    writer.WriteEndObject();
                }
                ansiConsole.Profile.Out.Writer.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            }
            else
            {
                logger.LogError("{Message}", message);
            }
            return 1;
        }
    }
}
