// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

/// <summary><c>winapp config list|get|set|unset</c>: the per-user settings in <see cref="UserSettings"/>.</summary>
internal class ConfigCommand : Command, IShortDescription
{
    public string ShortDescription => "Show or change your per-user winapp settings";

    internal const string Scope = "These are your per-user winapp settings, separate from a project's winapp.yaml.";

    public ConfigCommand(ConfigListCommand list, ConfigGetCommand get, ConfigSetCommand set, ConfigUnsetCommand unset)
        : base("config", "Show or change your per-user winapp settings, such as the DevTools mode winapp run uses. " +
            "Separate from a project's winapp.yaml.")
    {
        Subcommands.Add(list);
        Subcommands.Add(get);
        Subcommands.Add(set);
        Subcommands.Add(unset);
    }

    private static string KeyNames => string.Join(", ", UserSettings.Keys.Select(key => key.Name));

    internal static string OneOf(IReadOnlyList<string> values) =>
        values.Count < 2 ? string.Concat(values) : $"{string.Join(", ", values.Take(values.Count - 1))} or {values[^1]}";

    // Relaxed escaping keeps quotes and apostrophes readable in the JSON a person or agent reads.
    private static readonly JsonWriterOptions JsonOptions = new()
    {
        Indented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The <c>{"error": "..."}</c> line every config verb prints for a failure under --json.</summary>
    internal static void WriteJsonError(TextWriter output, string message)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions with { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("error", message);
            writer.WriteEndObject();
        }
        output.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    internal static Argument<ConfigKey> KeyArgument() => new("key")
    {
        Description = $"The setting: {KeyNames}",
        CustomParser = result =>
        {
            var name = result.Tokens[0].Value;
            if (UserSettings.Find(name) is { } key)
            {
                return key;
            }
            result.AddError($"Unknown setting '{name}'. Known settings: {KeyNames}");
            return null;
        },
    };

    /// <summary>The one-line error for a <c>config</c> verb's command line, or null when it parsed.</summary>
    internal static string? ParseError(ParseResult parseResult)
    {
        // --help still shows help: only a command line that would fail gets the one-line error.
        if (parseResult.Action is not ParseErrorAction || parseResult.CommandResult.Command is not (ConfigGetCommand or ConfigSetCommand or ConfigUnsetCommand or ConfigListCommand))
        {
            return null;
        }
        var command = parseResult.CommandResult.Command;
        var verb = $"winapp config {command.Name}";
        if (command.Arguments.Count > 0 && parseResult.GetResult(command.Arguments[0]) is not { Tokens.Count: > 0 })
        {
            return $"{verb} needs a setting: {KeyNames}";
        }
        if (command is ConfigSetCommand && parseResult.GetResult(ConfigSetCommand.ValueArgument) is not { Tokens.Count: > 0 } &&
            UserSettings.Find(parseResult.GetResult(ConfigSetCommand.KeyArgument)!.Tokens[0].Value) is { } key)
        {
            return $"{verb} {key.Name} needs a value: {OneOf(key.Values)}";
        }
        return parseResult.Errors[0].Message;
    }

    internal static int Write(IAnsiConsole console, bool json, IReadOnlyList<ConfigEntry> entries, bool list)
    {
        if (json)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, JsonOptions))
            {
                if (list)
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("settings");
                }
                foreach (var entry in entries)
                {
                    writer.WriteStartObject();
                    writer.WriteString("key", entry.Key.Name);
                    writer.WriteString("value", entry.Value);
                    writer.WriteString("source", entry.Source);
                    if (list)
                    {
                        writer.WriteString("default", entry.Key.Default);
                        writer.WriteStartArray("values");
                        foreach (var value in entry.Key.Values)
                        {
                            writer.WriteStringValue(value);
                        }
                        writer.WriteEndArray();
                        writer.WriteString("description", entry.Key.Description);
                    }
                    writer.WriteEndObject();
                }
                if (list)
                {
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
            }
            console.Profile.Out.Writer.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            return 0;
        }

        foreach (var entry in entries)
        {
            console.WriteLine($"{entry.Key.Name} = {entry.Value} ({entry.Source})");
            if (list)
            {
                console.WriteLine($"  {entry.Key.Description}: {OneOf(entry.Key.Values)}");
            }
        }
        if (list)
        {
            console.WriteLine();
            console.WriteLine(Scope);
            console.WriteLine("Change one with: winapp config set <key> <value>");
        }
        return 0;
    }

    internal static int Fail(IAnsiConsole console, ILogger logger, bool json, string message)
    {
        if (json)
        {
            WriteJsonError(console.Profile.Out.Writer, message);
            return 1;
        }
        logger.LogError("{Message}", message);
        return 1;
    }
}

internal class ConfigListCommand : Command, IShortDescription
{
    public string ShortDescription => "List every setting with its value and source";

    public ConfigListCommand() : base("list", "List every per-user setting with its value and whether it is your setting or the default.")
    {
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(IAnsiConsole console, UserSettings settings) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConfigCommand.Write(console, parseResult.GetValue(WinAppRootCommand.JsonOption),
                UserSettings.Keys.Select(settings.Get).ToList(), list: true));
    }
}

internal class ConfigGetCommand : Command, IShortDescription
{
    public string ShortDescription => "Show a setting's value and source";

    internal static Argument<ConfigKey> KeyArgument { get; } = ConfigCommand.KeyArgument();

    public ConfigGetCommand() : base("get", "Show a setting's value and whether it is your setting or the default.")
    {
        Arguments.Add(KeyArgument);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(IAnsiConsole console, UserSettings settings) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConfigCommand.Write(console, parseResult.GetValue(WinAppRootCommand.JsonOption),
                [settings.Get(parseResult.GetRequiredValue(KeyArgument))], list: false));
    }
}

internal class ConfigSetCommand : Command, IShortDescription
{
    public string ShortDescription => "Change a setting";

    internal static Argument<ConfigKey> KeyArgument { get; } = ConfigCommand.KeyArgument();

    internal static Argument<ConfigValue> ValueArgument { get; } = new("value")
    {
        Description = "The new value. 'winapp config list' shows the values each setting accepts.",
        CustomParser = result =>
        {
            var value = result.Tokens[0].Value;
            // The key's own parser reports an unknown key; reading its typed value here would throw.
            if (result.Parent?.GetResult(KeyArgument) is not { Tokens.Count: > 0 } keyResult ||
                UserSettings.Find(keyResult.Tokens[0].Value) is not { } key)
            {
                return null;
            }
            if (key.Normalize(value) is { } normalized)
            {
                return new ConfigValue(normalized);
            }
            result.AddError($"{key.Name} must be {ConfigCommand.OneOf(key.Values)}, not '{value}'");
            return null;
        },
    };

    public ConfigSetCommand() : base("set", "Change a per-user setting. 'winapp config unset' puts back the default.")
    {
        Arguments.Add(KeyArgument);
        Arguments.Add(ValueArgument);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(IAnsiConsole console, UserSettings settings, ILogger<ConfigSetCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var key = parseResult.GetRequiredValue(KeyArgument);
            try
            {
                settings.Set(key, parseResult.GetRequiredValue(ValueArgument));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(ConfigCommand.Fail(console, logger, json, $"Couldn't save {key.Name}: {ex.Message}"));
            }
            return Task.FromResult(ConfigCommand.Write(console, json, [settings.Get(key)], list: false));
        }
    }
}

internal class ConfigUnsetCommand : Command, IShortDescription
{
    public string ShortDescription => "Put a setting back to its default";

    internal static Argument<ConfigKey> KeyArgument { get; } = ConfigCommand.KeyArgument();

    public ConfigUnsetCommand() : base("unset", "Remove your value for a setting, so it goes back to the built-in default.")
    {
        Arguments.Add(KeyArgument);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(IAnsiConsole console, UserSettings settings, ILogger<ConfigUnsetCommand> logger) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var key = parseResult.GetRequiredValue(KeyArgument);
            try
            {
                settings.Unset(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(ConfigCommand.Fail(console, logger, json, $"Couldn't reset {key.Name}: {ex.Message}"));
            }
            return Task.FromResult(ConfigCommand.Write(console, json, [settings.Get(key)], list: false));
        }
    }
}
