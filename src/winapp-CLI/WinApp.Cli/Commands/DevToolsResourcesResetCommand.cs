// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools resources reset [&lt;key&gt;]</c>: put back the app's own values for resources that
/// <c>resources set</c> replaced.
/// </summary>
internal class DevToolsResourcesResetCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Restore resources changed by resources set";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools resources reset -a <app>",
        "winapp devtools resources reset <key> -a <app>",
    ];

    public static Argument<string?> KeyArgument { get; } = new("key")
    {
        Description = "The resource key to restore. Omit it to restore every resource DevTools changed.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsResourcesResetCommand()
        : base("reset", "Restore the app's own values for resources replaced by `resources set`. " +
            "Elements that use them update live.")
    {
        Arguments.Add(KeyArgument);
    }

    internal sealed record Restored(string Key, string Value, string? HResult);

    internal static (IReadOnlyList<Restored> Restored, IReadOnlyList<Restored> Failed)? Parse(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson, TapWireJson.DocumentOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return (Read(root, "restored"), Read(root, "failed"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<Restored> Read(JsonElement root, string name)
    {
        var list = new List<Restored>();
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && String(item, "key") is { Length: > 0 } key)
            {
                list.Add(new Restored(key, String(item, "value") ?? string.Empty, String(item, "hresult")));
            }
        }

        return list;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public class Handler(
        IDevToolsTargetResolver resolver,
        IAnsiConsole ansiConsole) : LiveHandler(resolver, ansiConsole)
    {
        protected override Task<int> RunAsync(
            DevToolsTarget target,
            ParseResult parseResult,
            bool json,
            CancellationToken cancellationToken)
        {
            var key = parseResult.GetValue(KeyArgument)?.Trim();
            if (key is { Length: 0 })
            {
                key = null;
            }

            var response = target.Tap!.RequestResourceReset(key, cancellationToken);
            if (!response.Ok)
            {
                return Task.FromResult(Fail(json, target.Pid, response.Error!));
            }

            if (Parse(response.ResultJson) is not { } result)
            {
                return Task.FromResult(Fail(json, target.Pid, "The DevTools agent returned an unreadable result."));
            }

            var ok = result.Failed.Count == 0;
            if (json)
            {
                WriteJson(DevToolsJson.Result(target.Pid, ok, response.ResultJson, writer =>
                {
                    if (!ok)
                    {
                        DevToolsJson.WriteError(writer, "resource-restore-failed",
                            $"WinUI could not restore {string.Join(", ", result.Failed.Select(f => f.Key))}; restart the app.");
                    }
                }));
                return Task.FromResult(ok ? 0 : 1);
            }

            if (result.Restored.Count == 0 && result.Failed.Count == 0)
            {
                DevToolsRender.WriteMarkupLine(Console, key is null
                    ? "No resources were changed by DevTools."
                    : $"{Markup.Escape(key)} was not changed by DevTools.");
                return Task.FromResult(0);
            }

            foreach (var restored in result.Restored)
            {
                DevToolsRender.WriteMarkupLine(Console,
                    $"{UiSymbols.Check} {Markup.Escape(restored.Key)}: restored to {Markup.Escape(restored.Value)}");
            }
            foreach (var failed in result.Failed)
            {
                DevToolsRender.WriteMarkupLine(Console,
                    $"{UiSymbols.Warning} {Markup.Escape(failed.Key)}: could not be restored ({Markup.Escape(failed.HResult ?? "unknown error")}); restart the app.");
            }

            return Task.FromResult(ok ? 0 : 1);
        }
    }
}
