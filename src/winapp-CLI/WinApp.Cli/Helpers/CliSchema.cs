// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers;

[JsonSerializable(typeof(CliSchema.RootCommandDetails))]
[JsonSerializable(typeof(CliSchema.CommandDetails))]
[JsonSerializable(typeof(CliSchema.OptionDetails))]
[JsonSerializable(typeof(CliSchema.ArgumentDetails))]
[JsonSerializable(typeof(CliSchema.ArityDetails))]
[JsonSerializable(typeof(Dictionary<string, CliSchema.ArgumentDetails>))]
[JsonSerializable(typeof(Dictionary<string, CliSchema.OptionDetails>))]
[JsonSerializable(typeof(Dictionary<string, CliSchema.CommandDetails>))]
[JsonSerializable(typeof(IfExists))]
[JsonSerializable(typeof(ManifestTemplates))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true)]
internal partial class CliSchemaJsonContext : JsonSerializerContext
{
    internal static CliSchemaJsonContext CreateCustom()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            RespectNullableAnnotations = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        return new CliSchemaJsonContext(options);
    }
}

internal static class CliSchema
{
    public record ArgumentDetails(string? description, int order, string? helpName, string valueType, bool hasDefaultValue, object? defaultValue, ArityDetails arity);
    public record ArityDetails(int minimum, int? maximum);
    public record OptionDetails(
        string? description,
        string[]? aliases,
        string? helpName,
        string valueType,
        bool hasDefaultValue,
        object? defaultValue,
        ArityDetails arity,
        bool required
    );
    public record CommandDetails(
        string? description,
        string[]? aliases,
        Dictionary<string, ArgumentDetails>? arguments,
        Dictionary<string, OptionDetails>? options,
        Dictionary<string, CommandDetails>? subcommands);
    public record RootCommandDetails(
        string name,
        string version,
        string? description,
        string[]? aliases,
        Dictionary<string, ArgumentDetails>? arguments,
        Dictionary<string, OptionDetails>? options,
        Dictionary<string, CommandDetails>? subcommands
    ) : CommandDetails(description, aliases, arguments, options, subcommands);


    public static void PrintCliSchema(CommandResult commandResult, TextWriter outputWriter)
    {
        var command = commandResult.Command;
        RootCommandDetails transportStructure = CreateRootCommandDetails(command);
        var result = JsonSerializer.Serialize(transportStructure, CliSchemaJsonContext.CreateCustom().RootCommandDetails);
        outputWriter.Write(result.AsSpan());
        outputWriter.Flush();
    }

    private static ArityDetails CreateArityDetails(ArgumentArity arity)
    {
        return new ArityDetails(
            minimum: arity.MinimumNumberOfValues,
            maximum: arity.MaximumNumberOfValues == ArgumentArity.ZeroOrMore.MaximumNumberOfValues ? null : arity.MaximumNumberOfValues
        );
    }

    private static RootCommandDetails CreateRootCommandDetails(Command command)
    {
        var arguments = CreateArgumentsDictionary(command.Arguments);
        var options = CreateOptionsDictionary(command.Options);
        var subcommands = CreateSubcommandsDictionary(command.Subcommands);

        return new RootCommandDetails(
            name: command.Name,
            version: System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!.ToString(),
            description: command.Description?.ReplaceLineEndings("\n"),
            aliases: DetermineAliases(command.Aliases),
            arguments: arguments,
            options: options,
            subcommands: subcommands
        );
    }

    private static Dictionary<string, ArgumentDetails>? CreateArgumentsDictionary(IList<Argument> arguments)
    {
        if (arguments.Count == 0)
        {
            return null;
        }
        var dict = new Dictionary<string, ArgumentDetails>();
        foreach ((var index, var argument) in arguments.Index())
        {
            dict[argument.Name] = CreateArgumentDetails(index, argument);
        }
        return dict;
    }

    private static Dictionary<string, OptionDetails>? CreateOptionsDictionary(IList<Option> options)
    {
        if (options.Count == 0)
        {
            return null;
        }
        var dict = new Dictionary<string, OptionDetails>();
        foreach (var option in options.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
        {
            dict[option.Name] = CreateOptionDetails(option);
        }
        return dict;
    }

    private static Dictionary<string, CommandDetails>? CreateSubcommandsDictionary(IList<Command> subcommands)
    {
        if (subcommands.Count == 0)
        {
            return null;
        }
        var dict = new Dictionary<string, CommandDetails>();
        foreach (var subcommand in subcommands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            dict[subcommand.Name] = CreateCommandDetails(subcommand);
        }
        return dict;
    }

    private static string[]? DetermineAliases(ICollection<string> aliases)
    {
        if (aliases.Count == 0)
        {
            return null;
        }

        // Order the aliases to ensure consistent output.
        return [.. aliases.Order()];
    }

    public static string ToCliTypeString(this Type type)
    {
        var typeName = type.FullName ?? string.Empty;
        if (!type.IsGenericType)
        {
            return typeName;
        }

        var genericTypeName = typeName[..typeName.IndexOf('`')];
        var genericTypes = string.Join(", ", type.GenericTypeArguments.Select(generic => generic.ToCliTypeString()));
        return $"{genericTypeName}<{genericTypes}>";
    }

    private static CommandDetails CreateCommandDetails(Command subCommand)
    {
        return new CommandDetails(
                subCommand.Description?.ReplaceLineEndings("\n"),
                DetermineAliases(subCommand.Aliases),
                CreateArgumentsDictionary(subCommand.Arguments),
                CreateOptionsDictionary(subCommand.Options),
                CreateSubcommandsDictionary(subCommand.Subcommands)
            );
    }

    private static OptionDetails CreateOptionDetails(Option option)
    {
        return new OptionDetails(
                option.Description?.ReplaceLineEndings("\n"),
                DetermineAliases(option.Aliases),
                option.HelpName,
                option.ValueType.ToCliTypeString(),
                option.HasDefaultValue,
                option.HasDefaultValue ? HumanizeValue(option.GetDefaultValue()) : null,
                CreateArityDetails(option.Arity),
                option.Required
            );
    }

    /// <summary>
    /// Maps some types that don't serialize well to more human-readable strings.
    /// For example, <see cref="VerbosityOptions"/> is serialized as a string instead of an integer.
    /// </summary>
    private static object? HumanizeValue(object? v)
    {
        return v switch
        {
            //VerbosityOptions o => Enum.GetName(o),
            null => null,
            _ => v // For other types, return as is
        };
    }

    private static ArgumentDetails CreateArgumentDetails(int index, Argument argument)
    {
        return new ArgumentDetails(
                argument.Description?.ReplaceLineEndings("\n"),
                index,
                argument.HelpName,
                argument.ValueType.ToCliTypeString(),
                argument.HasDefaultValue,
                argument.HasDefaultValue ? HumanizeValue(argument.GetDefaultValue()) : null,
                CreateArityDetails(argument.Arity)
            );
    }
}
