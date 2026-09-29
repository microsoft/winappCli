// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Text.Json;
using Spectre.Console;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

/// <summary>
/// Shared plumbing for the live <c>winapp devtools</c> commands: resolve an explicitly authorized target,
/// then render either compact human output or one JSON object. Keeping this in one place is what makes every
/// command agree on targeting, on how a DevTools failure reads, and on the exit code (0 success, nonzero failure).
/// </summary>
internal abstract class DevToolsLiveCommand : Command, IShortDescription
{
    protected DevToolsLiveCommand(string name, string description)
        : base(name, description)
    {
        Options.Add(SharedUiOptions.AppOption);
        Options.Add(SharedUiOptions.WindowOption);
        Options.Add(SharedDevToolsOptions.RootOption);
        Options.Add(SharedDevToolsOptions.AttachOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public abstract string ShortDescription { get; }

    internal abstract class LiveHandler(
        IDevToolsTargetResolver resolver,
        IAnsiConsole ansiConsole) : AsynchronousCommandLineAction
    {
        protected IAnsiConsole Console { get; } = ansiConsole;

        protected abstract Task<int> RunAsync(
            DevToolsTarget target,
            ParseResult parseResult,
            bool json,
            CancellationToken cancellationToken);

        protected virtual bool AttachIfNeeded(ParseResult parseResult) => parseResult.GetValue(SharedDevToolsOptions.AttachOption);

        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var app = parseResult.GetValue(SharedUiOptions.AppOption);
            var window = parseResult.GetValue(SharedUiOptions.WindowOption);
            var root = parseResult.GetValue(SharedDevToolsOptions.RootOption);
            if (root is not null && (!ulong.TryParse(root, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var rootHandle) || rootHandle == 0))
            {
                return Fail(json, 0, "--root requires a nonzero opaque handle from DevTools inspect or Surface.list.", "bad-root");
            }

            var target = await resolver.ResolveAsync(app, window, cancellationToken, AttachIfNeeded(parseResult));
            if (!target.Ok)
            {
                return target.NotAttached
                    ? Fail(json, target.Pid, target.Error!, "not-attached")
                    : Fail(json, 0, target.Error ?? "Could not resolve a DevTools target.");
            }
            if (root is not null)
            {
                target = DevToolsTarget.Ready(target.Pid, target.ProcessName, target.JustAttached, target.Window, root);
            }

            return await RunAsync(target, parseResult, json, cancellationToken);
        }

        protected int Fail(bool json, int pid, DevToolsProtocolError error)
        {
            if (json)
            {
                Console.Profile.Out.Writer.WriteLine(DevToolsJson.Error(pid, error));
            }
            else
            {
                Console.MarkupLineInterpolated($"[red]Error:[/] {DevToolsErrors.Describe(error)}");
            }

            return 1;
        }

        protected int Fail(bool json, int pid, string message, string token = "cli")
        {
            if (json)
            {
                Console.Profile.Out.Writer.WriteLine(DevToolsJson.Error(pid, message, token));
            }
            else
            {
                Console.MarkupLineInterpolated($"[red]Error:[/] {message}");
            }

            return 1;
        }

        protected void WriteJson(string payload) => Console.Profile.Out.Writer.WriteLine(payload);

        /// <summary>
        /// Reports a selector that did not resolve to exactly one element, listing the candidates so the next
        /// command is a copy-paste away rather than another search.
        /// <para>
        /// The candidates travel in BOTH shapes. A <c>--json</c> caller that got only the message received a
        /// sentence ending in a colon and nothing to act on — on the very workflow that produces ambiguity
        /// most often, a duplicated <c>x:Name</c>, where every candidate has a distinct working selector.
        /// </para>
        /// </summary>
        protected int FailSelector(bool json, int pid, DevToolsSelector.Resolution resolved)
        {
            if (resolved.RemoteError is not null)
            {
                return Fail(json, pid, resolved.RemoteError);
            }
            if (json)
            {
                WriteJson(DevToolsJson.Serialize(writer =>
                {
                    writer.WriteBoolean("ok", false);
                    writer.WriteNumber("processId", pid);
                    DevToolsJson.WriteError(writer, "selector", resolved.Error!);
                    writer.WriteStartArray("candidates");
                    foreach (var candidate in resolved.Candidates)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("selector", candidate.Selector);
                        writer.WriteString("handle", candidate.Handle);
                        if (candidate.Id is not null)
                        {
                            writer.WriteString("id", candidate.Id);
                        }

                        if (candidate.Name.Length > 0)
                        {
                            writer.WriteString("name", candidate.Name);
                            writer.WriteBoolean("uniqueName", candidate.UniqueName);
                        }

                        writer.WriteString("type", candidate.Type);
                        if (candidate.File is not null)
                        {
                            writer.WriteString("file", candidate.File);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }));
                return 1;
            }

            Console.MarkupLineInterpolated($"[red]Error:[/] {resolved.Error}");
            foreach (var candidate in resolved.Candidates)
            {
                DevToolsRender.WriteMarkupLine(Console, "  " + DevToolsRender.Match(candidate));
            }

            return 1;
        }

        protected static VisualTreeNode? DescribeElement(DevToolsTarget target, string handle, CancellationToken cancellationToken)
        {
            var response = target.Tap!.RequestEnumerate(handle, 1, cancellationToken: cancellationToken);
            if (!response.Ok)
            {
                return null;
            }

            var roots = VisualTreeNode.ParseForest(response.ResultJson);
            return roots is { Count: > 0 } ? roots[0] : null;
        }

        private (string Handle, VisualTreeNode? Node)? described;

        protected VisualTreeNode? Describe(DevToolsTarget target, string handle, CancellationToken cancellationToken)
        {
            if (this.described is { } cached && cached.Handle == handle)
            {
                return cached.Node;
            }

            var node = DescribeElement(target, handle, cancellationToken);
            this.described = (handle, node);
            return node;
        }

        /// <summary>
        /// Writes BOTH identities of the element a command is scoped to: the <c>selector</c> a human reads and
        /// retypes, and the <c>handle</c> every DevTools verb accepts. A payload carrying only one forces the
        /// caller to choose between readable and exact, and the handle is the escape hatch when a selector
        /// has gone stale — so it is never dropped.
        /// </summary>
        protected void WriteIdentity(Utf8JsonWriter writer, DevToolsTarget target, string handle, CancellationToken cancellationToken)
        {
            writer.WriteString("selector", Describe(target, handle, cancellationToken)?.Selector ?? handle);
            writer.WriteString("handle", handle);
        }

        /// <summary>
        /// Resolves a required selector to a handle, reporting the failure itself when it does not resolve.
        /// Returns <c>null</c> (with <paramref name="exitCode"/> set) when the caller should stop.
        /// </summary>
        /// <param name="forMutation">
        /// Whether the handle will be WRITTEN to. A node-capped search that found one <c>x:Name</c> may simply not
        /// have looked at a duplicate, so a mutation refuses that resolution outright rather than writing to an
        /// element the caller never saw; a read continues with the incompleteness stated.
        /// </param>
        protected string? RequireHandle(
            DevToolsTarget target,
            string? selector,
            bool json,
            string usage,
            out int exitCode,
            CancellationToken cancellationToken,
            bool forMutation = false)
        {
            if (string.IsNullOrWhiteSpace(selector))
            {
                exitCode = Fail(json, target.Pid, $"Provide a selector: the selector printed in brackets, an x:Name, or a handle. {usage}");
                return null;
            }

            var resolved = DevToolsSelector.Resolve(target.Tap!, selector, cancellationToken);
            if (!resolved.Ok)
            {
                exitCode = FailSelector(json, target.Pid, resolved);
                return null;
            }

            if (resolved.Warning is not null)
            {
                if (forMutation)
                {
                    exitCode = Fail(
                        json,
                        target.Pid,
                        $"Refusing to write through the x:Name '{selector.Trim()}': {resolved.Warning} " +
                        "Pass the selector `winapp devtools inspect` printed, or a handle — a write must not " +
                        "land on an element that was never examined.",
                        "ambiguous-selector");
                    return null;
                }

                // A read may continue, but the incompleteness travels with the result in BOTH shapes: a --json
                // consumer that never sees the warning is exactly the caller that would go on to write.
                LastWarning = resolved.Warning;
                if (!json)
                {
                    Console.MarkupLineInterpolated($"{UiSymbols.Warning} {resolved.Warning}");
                }
            }

            exitCode = 0;
            return resolved.Handle;
        }

        protected string? LastWarning { get; private set; }

        protected void WriteWarning(Utf8JsonWriter writer)
        {
            if (LastWarning is not null)
            {
                writer.WriteString("warning", LastWarning);
            }
        }
    }
}
