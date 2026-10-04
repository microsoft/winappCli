// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// <c>winapp devtools set-property &lt;selector&gt; &lt;property&gt; &lt;value&gt;</c> — change one
/// dependency property on a live element and report what the app actually did.
/// <para>
/// The command reads the property, writes it, and reads it back, and reports the OBSERVED before → after. It
/// never reports the value it sent as though it were the value that took: a write can be accepted and then
/// overridden by a style, a binding, or the next layout pass, and only the read-back can tell. It also never
/// claims a source file changed — this is a live in-memory edit that disappears when the app restarts.
/// </para>
/// </summary>
internal class DevToolsSetPropertyCommand : DevToolsLiveCommand, IHelpExamples
{
    public override string ShortDescription => "Change a live property and read back what took effect";

    public IReadOnlyList<string> Examples { get; } =
    [
        "winapp devtools set-property <selector> Text \"<text>\" -a <app>",
        "winapp devtools set-property <selector> Width 200 -a <app>",
        "winapp devtools set-property <selector> Background \"#FF0067C0\" -a <app>",
    ];

    public static Argument<string?> SelectorArgument { get; } = new("selector")
    {
        Description = "Element to change: the selector printed in brackets, an x:Name, an AutomationId, or a handle.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string?> PropertyArgument { get; } = new("property")
    {
        Description = "The property to change, e.g. Width. Same as --property.",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public static Argument<string?> ValueArgument { get; } = new("value")
    {
        Description = "The new value, e.g. 200, false, #FF0067C0, or \"Save changes\".",
        Arity = ArgumentArity.ZeroOrOne,
    };

    public DevToolsSetPropertyCommand()
        : base("set-property", "Change a live property and read it back; source files stay unchanged.")
    {
        Arguments.Add(SelectorArgument);
        Arguments.Add(PropertyArgument);
        Arguments.Add(ValueArgument);
        Options.Add(SharedDevToolsOptions.PropertyOption);
        Options.Add(SharedDevToolsOptions.TypeOption);
        DevToolsQueryOptions.Add(this, write: true);
    }

    /// <summary>
    /// <c>set-property &lt;selector&gt; &lt;property&gt; &lt;value&gt;</c>, or <c>set-property &lt;selector&gt; &lt;value&gt; -p &lt;property&gt;</c>.
    /// </summary>
    internal static (string? Property, string? Value, bool Conflict) ResolvePropertyAndValue(ParseResult parseResult)
    {
        var property = parseResult.GetValue(SharedDevToolsOptions.PropertyOption);
        var value = parseResult.GetValue(ValueArgument);
        var second = parseResult.GetValue(PropertyArgument);
        return second is null ? (property, value, false)
            : property is null ? (second, value, false)
            : value is null ? (property, second, false)
            : (property, value, true);
    }

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
            var type = parseResult.GetValue(SharedDevToolsOptions.TypeOption);
            var (property, value, conflict) = ResolvePropertyAndValue(parseResult);
            if (conflict)
            {
                return Task.FromResult(Fail(json, target.Pid, "Pass the property once: positionally or with --property.", "bad-args"));
            }

            if (string.IsNullOrWhiteSpace(property))
            {
                return Task.FromResult(Fail(
                    json,
                    target.Pid,
                    "Provide the property to change, e.g. `winapp devtools set-property SaveButton Width 200`."));
            }

            if (DevToolsQueryOptions.HasCriteria(parseResult))
            {
                if (parseResult.GetValue(SelectorArgument) is not null || value is not null)
                {
                    return Task.FromResult(Fail(json, target.Pid, "Query mode requires --value and no positional selector or value.", "bad-args"));
                }

                if (parseResult.GetValue(DevToolsQueryOptions.Value) is null)
                {
                    return Task.FromResult(Fail(json, target.Pid, "Query-targeted set requires --value.", "bad-args"));
                }

                return Task.FromResult(DevToolsQueryOptions.Run(target, parseResult, Console, json, cancellationToken,
                    exact: true, write: true));
            }
            if (parseResult.GetValue(DevToolsQueryOptions.Value) is not null)
            {
                return Task.FromResult(Fail(json, target.Pid, "--value requires --of-type or --with; selector mode takes a positional value.", "bad-args"));
            }

            if (value is null)
            {
                return Task.FromResult(Fail(
                    json,
                    target.Pid,
                    "Provide the new value, e.g. `winapp devtools set-property SaveButton Width 200`."));
            }

            var handle = RequireHandle(
                target,
                parseResult.GetValue(SelectorArgument),
                json,
                "Run `winapp devtools inspect` to list them.",
                out var exitCode,
                cancellationToken,
                forMutation: true);
            if (handle is null)
            {
                return Task.FromResult(exitCode);
            }

            var tap = target.Tap!;

            // 1. Read first. Without a before-value the command could only echo what it sent, which is exactly
            //    the claim this flow exists to avoid making.
            var beforeResponse = tap.RequestProperties(handle, cancellationToken);
            if (!beforeResponse.Ok)
            {
                return Task.FromResult(Fail(json, target.Pid, beforeResponse.Error!));
            }

            var beforeRows = DevToolsPropertyRow.Parse(beforeResponse.ResultJson);
            var beforeRow = DevToolsPropertyRow.Find(beforeRows, property);
            if (beforeRow is null)
            {
                var known = beforeRows is null or { Count: 0 }
                    ? string.Empty
                    : $" The agent reads: {string.Join(", ", beforeRows.Select(r => r.Name))}.";
                return Task.FromResult(Fail(
                    json,
                    target.Pid,
                    $"The DevTools agent does not read '{property}' on this element.{known}"));
            }

            // The tap already knows the XAML type each property must be written as, so --type is a manual
            // override rather than something a caller has to supply. A property the tap reports no writeType
            // for is one it will not write; saying so now beats a refused write with a vaguer reason.
            var writeType = !string.IsNullOrWhiteSpace(type) ? type! : beforeRow.WriteType;
            if (beforeRow.Redacted)
            {
                return Task.FromResult(Fail(json, target.Pid,
                    $"'{beforeRow.Name}' holds a secret; DevTools does not read or write it."));
            }
            if (string.IsNullOrWhiteSpace(writeType))
            {
                return Task.FromResult(Fail(
                    json,
                    target.Pid,
                    $"The DevTools agent reports '{property}' as not settable on this element. " +
                    "Pass --type <XamlType> to attempt the write anyway."));
            }

            var writeResponse = tap.RequestSetProperty(handle, beforeRow.Name, writeType, value, cancellationToken);
            if (!writeResponse.Ok)
            {
                return Task.FromResult(Fail(json, target.Pid, writeResponse.Error!));
            }

            var afterResponse = tap.RequestProperties(handle, cancellationToken);
            var afterRow = afterResponse.Ok
                ? DevToolsPropertyRow.Find(DevToolsPropertyRow.Parse(afterResponse.ResultJson), property)
                : null;

            var before = beforeRow.Value;
            var after = afterRow?.Value;
            var took = after is not null && !string.Equals(before, after, StringComparison.Ordinal);
            var matchesRequested = after is not null
                && MatchesRequested(after, value, beforeRow.ValueType, writeType);

            // ONE verdict, shared by both output shapes. A `--json` caller that got ok:true and exit 0 for a
            // write the human path calls a failure has no usable signal at all — and this is the agent-facing
            // mode. `took || matchesRequested` is the honest test: a value the framework normalized on the way
            // in (Thickness "10" -> "10,10,10,10") DID take, while a value that neither changed nor equals the
            // request demonstrably did not.
            var succeeded = after is not null && (took || matchesRequested);
            // A local write replaces a {Binding} (confirmed by the read-back) and overrides an x:Bind.
            var replacedBinding = succeeded && (beforeRow.Binding is null || afterRow?.Binding is null)
                ? BoundBy(beforeRow) : null;

            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", succeeded);
                    writer.WriteNumber("processId", target.Pid);
                    WriteIdentity(writer, target, handle, cancellationToken);
                    writer.WriteString("property", beforeRow.Name);
                    writer.WriteString("type", writeType);
                    writer.WriteString("requested", value);
                    writer.WriteString("before", before);
                    if (after is null)
                    {
                        writer.WriteNull("after");
                    }
                    else
                    {
                        writer.WriteString("after", after);
                    }

                    writer.WriteBoolean("changed", took);
                    // Distinct from `changed`: a write can change the value to something OTHER than what was
                    // asked for (a coerced Opacity, a normalized Thickness). A caller that needs to know
                    // whether its exact value took must be able to see that without guessing.
                    writer.WriteBoolean("matchesRequested", matchesRequested);
                    if (replacedBinding is not null)
                    {
                        writer.WriteString("replacedBinding", replacedBinding);
                    }
                    if (afterRow?.ValueSource is string source)
                    {
                        writer.WriteString("valueSource", source);
                    }
                    if (afterRow?.Binding is string binding)
                    {
                        writer.WriteString("binding", binding);
                    }
                    if (afterRow?.Authored is string authored)
                    {
                        writer.WriteString("authored", authored);
                    }

                    if (!succeeded)
                    {
                        DevToolsJson.WriteError(
                            writer,
                            "write-unconfirmed",
                            after is null
                                ? "The write was accepted, but the value could not be read back."
                                : $"The value read back is still {after}, which is neither a change nor the " +
                                  $"requested {value}, so the write could not be confirmed.");
                    }
                }));
                return Task.FromResult(succeeded ? 0 : 1);
            }

            var label = DevToolsRender.Header(handle, Describe(target, handle, cancellationToken), beforeRow.Name);
            if (after is null)
            {
                DevToolsRender.WriteMarkupLine(Console,
                    $"{UiSymbols.Warning} {label}: the write was accepted, but the value could not be read back.");
                return Task.FromResult(1);
            }

            if (took)
            {
                DevToolsRender.WriteMarkupLine(Console,
                    $"{UiSymbols.Check} {label}: {Markup.Escape(before)} [grey]->[/] [green]{Markup.Escape(after)}[/]");
                if (!matchesRequested)
                {
                    // The value changed, but not to what was asked for — the framework coerced or normalized
                    // it. Saying so is the difference between a caller trusting the number it sent and the
                    // number the app actually holds.
                    DevToolsRender.WriteMarkupLine(Console,
                        $"   [grey]The app normalized {Markup.Escape(value)} to {Markup.Escape(after)}.[/]");
                }
            }
            else if (matchesRequested)
            {
                DevToolsRender.WriteMarkupLine(Console, $"{UiSymbols.Check} {label}: already {Markup.Escape(after)} — unchanged.");
            }
            else
            {
                // Accepted-then-overridden is a real and common outcome (a style, a binding, or the next
                // layout pass wins). Reporting it as success would be a lie the next read would expose.
                // Phrased as "could not confirm" rather than "did not take", because a value the tap renders
                // as an opaque {TypeName} reads identically before and after a change that genuinely landed —
                // and asserting a failure we cannot see is the same overclaim in the other direction.
                DevToolsRender.WriteMarkupLine(Console,
                    $"{UiSymbols.Warning} {label}: reads back {Markup.Escape(after)} after writing " +
                    $"{Markup.Escape(value)} — the change could not be confirmed.");
                if ((afterRow?.Binding ?? afterRow?.ValueSource) is string source)
                {
                    DevToolsRender.WriteMarkupLine(Console, $"   [grey]Its effective value comes from {Markup.Escape(source)}.[/]");
                }

                return Task.FromResult(1);
            }

            if (replacedBinding is not null)
            {
                DevToolsRender.WriteMarkupLine(Console, (beforeRow.Binding is not null
                    ? $"{UiSymbols.Warning} This replaced the binding {Markup.Escape(replacedBinding)}; its source no longer updates {Markup.Escape(beforeRow.Name)}."
                    : $"{UiSymbols.Warning} This overrode {Markup.Escape(replacedBinding)} with a local value until the binding updates again.") +
                    " Restart the app to restore the binding.");
            }

            return Task.FromResult(0);
        }

        internal static string? BoundBy(DevToolsPropertyRow row)
            => row.Binding ?? (row.AuthoredKind is "xBind" or "binding" ? row.Authored ?? row.AuthoredKind : null);

        private static bool MatchesRequested(string actual, string requested, string valueType, string writeType)
        {
            if (string.Equals(actual, requested, StringComparison.Ordinal))
            {
                return true;
            }
            var type = DevToolsFormat.ShortTypeName(valueType);
            if (type != DevToolsFormat.ShortTypeName(writeType))
            {
                return false;
            }
            return type switch
            {
                "Byte" or "SByte" or "Int16" or "UInt16" or "Int32" or "UInt32" or "Int64" or "UInt64" =>
                    decimal.TryParse(actual, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) &&
                    decimal.TryParse(requested, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && a == b,
                "Double" => double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
                    double.TryParse(requested, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) &&
                    double.IsFinite(a) && double.IsFinite(b) && a == b,
                "Single" => float.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
                    float.TryParse(requested, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) &&
                    float.IsFinite(a) && float.IsFinite(b) && a == b,
                "Decimal" => decimal.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
                    decimal.TryParse(requested, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a == b,
                "Boolean" => bool.TryParse(actual, out var a) && bool.TryParse(requested, out var b) && a == b,
                "SolidColorBrush" => NormalizeHexColor(actual) is string color && color == NormalizeHexColor(requested),
                _ => false,
            };
        }

        private static string? NormalizeHexColor(string value)
        {
            var text = value.Trim(' ', '\t');
            if (text.Length is not (4 or 5 or 7 or 9) || text[0] != '#' ||
                text.Skip(1).Any(c => !char.IsAsciiHexDigit(c)))
            {
                return null;
            }
            var hex = text[1..].ToUpperInvariant();
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }
            return hex.Length == 6 ? "FF" + hex : hex;
        }
    }
}
