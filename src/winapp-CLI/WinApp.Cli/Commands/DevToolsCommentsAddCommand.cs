// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Commands;

internal class DevToolsCommentsAddCommand : Command, IShortDescription
{
    public string ShortDescription => "Add a UI review comment anchored to a source element";

    public static Option<string> TextOption { get; } = new("--text", "-t") { Description = "The comment text (required).", Required = true };
    public static Option<string?> IdOption { get; } = new("--id") { Description = "Create or replace the comment with this ID." };
    public static Option<string?> KindOption { get; } = new("--kind") { Description = "Triage: visual | binding | behavior | a11y | other." };
    public static Option<bool> FromSelectionOption { get; } = new("--from-selection") { Description = "Use the picked element in a running DevTools app (requires --app)." };
    public static Option<string?> FromElementOption { get; } = new("--from-element") { Description = "Use an element's x:Name or handle in a running DevTools app (requires --app)." };
    public static Option<string?> AppOption { get; } = new("--app") { Description = "Live capture and marker target: PID, process name, or window title." };
    public static Option<string?> FileOption { get; } = new("--file") { Description = "Source file leaf the element lives in (e.g. MainWindow.xaml)." };
    public static Option<int?> LineOption { get; } = new("--line") { Description = "Source line (advisory)." };
    public static Option<int?> ColumnOption { get; } = new("--column") { Description = "Source column (advisory)." };
    public static Option<string?> SourceUriOption { get; } = new("--source-uri") { Description = "Raw ms-appx:/// URI, if known.", Hidden = true };
    public static Option<string?> TypeOption { get; } = new("--type") { Description = "Element control type (e.g. Button).", Hidden = true };
    public static Option<string?> NameOption { get; } = new("--name") { Description = "Element x:Name." };
    public static Option<string?> AutomationIdOption { get; } = new("--automation-id") { Description = "Element AutomationId.", Hidden = true };
    public static Option<string?> ContentOption { get; } = new("--content") { Description = "Element Content/Text snippet (for fuzzy match).", Hidden = true };
    public static Option<string?> TreePathOption { get; } = new("--tree-path") { Description = "Structural path from root (last-resort disambiguator).", Hidden = true };
    public static Option<string?> PropertyOption { get; } = new("--property") { Description = "Property this comment is about, if authored from a property row.", Hidden = true };
    public static Option<string?> BoundsOption { get; } = new("--bounds") { Description = "Author-time bounds as \"x,y,w,h\" (DIP).", Hidden = true };
    public static Option<bool> WeakOption { get; } = new("--weak") { Description = "Mark the anchor weak (uninstrumented / low-confidence identity).", Hidden = true };
    public static Option<bool> TemplatedOption { get; } = new("--templated") { Description = "Mark the element as living inside a template.", Hidden = true };
    public static Option<bool> ConfirmLikelySourceOption { get; } = new("--confirm-likely-source")
    {
        Description = "Confirm the displayed likely source declaration for this capture; its compiled identity is unverified.",
    };

    public DevToolsCommentsAddCommand()
        : base("add", "Author a source-anchored UI review comment into .winapp/ui-comments.json.")
    {
        Options.Add(TextOption);
        Options.Add(IdOption);
        Options.Add(KindOption);
        Options.Add(FromSelectionOption);
        Options.Add(FromElementOption);
        Options.Add(AppOption);
        Options.Add(FileOption);
        Options.Add(LineOption);
        Options.Add(ColumnOption);
        Options.Add(SourceUriOption);
        Options.Add(TypeOption);
        Options.Add(NameOption);
        Options.Add(AutomationIdOption);
        Options.Add(ContentOption);
        Options.Add(TreePathOption);
        Options.Add(PropertyOption);
        Options.Add(BoundsOption);
        Options.Add(WeakOption);
        Options.Add(TemplatedOption);
        Options.Add(ConfirmLikelySourceOption);
        Options.Add(CommentsSharedOptions.SourceRootOption);
        Options.Add(WinAppRootCommand.JsonOption);
    }

    public class Handler(
        ICommentStore store,
        ICommentAnchorResolver resolver,
        ICommentPusher pusher,
        IUiTargetResolver targetResolver,
        ICurrentDirectoryProvider currentDirectory,
        IAnsiConsole ansiConsole,
        ILogger<DevToolsCommentsAddCommand> logger,
        ExecutionTargets.GuestAgent.GuestCommentContext? guestComments = null) : AsynchronousCommandLineAction
    {
        public override Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var json = parseResult.GetValue(WinAppRootCommand.JsonOption);
            var text = parseResult.GetValue(TextOption);
            var kind = parseResult.GetValue(KindOption);

            if (string.IsNullOrWhiteSpace(text))
            {
                return Task.FromResult(Fail(ansiConsole, json, "Comment text is required (--text)."));
            }

            if (kind is not null && !CommentKind.IsValid(kind))
            {
                return Task.FromResult(Fail(ansiConsole, json, $"Invalid --kind '{kind}'. Allowed: {string.Join(", ", CommentKind.All)}."));
            }

            // Live capture from an element in a running --devtools app: either the one the developer picked
            // (--from-selection) or a named/handle-addressed one (--from-element, which is how a script or a
            // protocol client authors a comment with nobody at the app). Populates a bundle that explicit flags
            // below override (explicit always wins).
            CapturedElement? captured = null;
            var fromSelection = parseResult.GetValue(FromSelectionOption);
            var fromElement = Nullify(parseResult.GetValue(FromElementOption));
            var app = parseResult.GetValue(AppOption);
            uint? appPid = null;
            if (fromSelection && fromElement is not null)
            {
                return Task.FromResult(Fail(ansiConsole, json, "--from-selection and --from-element are alternatives; pass one."));
            }

            if (fromSelection || fromElement is not null)
            {
                var flag = fromElement is null ? "--from-selection" : "--from-element";
                if (string.IsNullOrWhiteSpace(app))
                {
                    return Task.FromResult(Fail(ansiConsole, json, $"{flag} requires --app <pid|name> naming the running --devtools app."));
                }

                if (!TryResolvePid(targetResolver, app, cancellationToken, out var pid, out var pidError))
                {
                    return Task.FromResult(Fail(ansiConsole, json, pidError!));
                }

                appPid = pid;
                var result = fromElement is null
                    ? CommentSelectionCapture.Capture(pid, cancellationToken)
                    : CommentSelectionCapture.CaptureElement(pid, fromElement, cancellationToken);
                switch (result.Status)
                {
                    case CaptureStatus.Failed:
                        return Task.FromResult(Fail(ansiConsole, json, result.Error?.Message ?? "Element capture failed."));
                    case CaptureStatus.NoAgent:
                        return Task.FromResult(Fail(ansiConsole, json, $"No DevTools tap answered for pid {pid}. Launch the app with 'winapp run <app> --devtools'."));
                    case CaptureStatus.NoSelection when fromElement is not null:
                        return Task.FromResult(Fail(ansiConsole, json, $"No element '{fromElement}' in the app's visual tree. Pass an x:Name or a handle from 'winapp devtools inspect'."));
                    case CaptureStatus.NoSelection:
                        return Task.FromResult(Fail(ansiConsole, json, "Nothing is picked. In the app's DevTools toolbar click Pick, select an element, then re-run."));
                    default:
                        captured = result.Element;
                        break;
                }
            }
            else if (!string.IsNullOrWhiteSpace(app))
            {
                return Task.FromResult(Fail(ansiConsole, json, "--app only applies with --from-selection or --from-element."));
            }
            else if (guestComments is { ScopedInspection: true, Process: { } process })
            {
                appPid = checked((uint)process.ProcessId);
            }

            var sourceRoot = parseResult.GetValue(CommentsSharedOptions.SourceRootOption) ??
                captured?.SourceRoot ?? currentDirectory.GetCurrentDirectory();
            if (captured?.SourceProvenance == "likely-source-line" && !parseResult.GetValue(ConfirmLikelySourceOption))
            {
                return Task.FromResult(Fail(ansiConsole, json,
                    $"Likely source: {captured.SourceFile}:{captured.Line}. {captured.SourceEvidence} " +
                    "Review get-source, then pass --confirm-likely-source to anchor this comment. Nothing was saved."));
            }
            CommentBounds? bounds;
            if (!TryParseBounds(parseResult.GetValue(BoundsOption), out bounds))
            {
                return Task.FromResult(Fail(ansiConsole, json, "Invalid --bounds; expected \"x,y,w,h\"."));
            }

            var now = CommentTimestamps.Now();
            var providedId = Nullify(parseResult.GetValue(IdOption));
            var overridesSource = Nullify(parseResult.GetValue(FileOption)) is not null ||
                Nullify(parseResult.GetValue(SourceUriOption)) is not null ||
                parseResult.GetValue(LineOption) is not null || parseResult.GetValue(ColumnOption) is not null ||
                captured?.SourceRoot is { Length: > 0 } capturedRoot && !CommentStoreLocator.SameProject(capturedRoot, sourceRoot);
            var overridesIdentity = Nullify(parseResult.GetValue(TypeOption)) is not null ||
                Nullify(parseResult.GetValue(NameOption)) is not null || Nullify(parseResult.GetValue(AutomationIdOption)) is not null ||
                Nullify(parseResult.GetValue(TreePathOption)) is not null;
            var authored = !overridesSource && !overridesIdentity && captured?.Authored is { } evidence &&
                CommentStoreLocator.SameProject(evidence.ProjectRoot, sourceRoot) ? evidence : null;
            var comment = new Comment
            {
                Id = providedId ?? "cmt_" + Guid.NewGuid().ToString("N")[..12],
                Text = text,
                Status = CommentStatus.Open,
                Kind = kind,
                CreatedAt = now,
                UpdatedAt = now,
                Author = Environment.UserName,
                ProjectRoot = Path.GetFullPath(sourceRoot),
                Anchor = new CommentAnchor
                {
                    SourceFile = Nullify(parseResult.GetValue(FileOption)) ?? captured?.SourceFile,
                    SourceUri = Nullify(parseResult.GetValue(SourceUriOption)) ?? captured?.SourceUri,
                    Line = parseResult.GetValue(LineOption) ?? captured?.Line,
                    Column = parseResult.GetValue(ColumnOption) ?? captured?.Column,
                    SourceProvenance = overridesSource && captured?.SourceProvenance is not null ? "user-specified" :
                        captured?.SourceProvenance == "likely-source-line" ? "user-confirmed-likely-source" : captured?.SourceProvenance,
                    SourceEvidence = captured?.SourceEvidence,
                    RawLine = captured?.RawLine,
                    RawColumn = captured?.RawColumn,
                    Authored = authored,
                    Weak = parseResult.GetValue(WeakOption),
                    Templated = parseResult.GetValue(TemplatedOption) || authored?.Templated == true,
                    ElementPath = captured?.ElementPath,
                    Identity = new CommentIdentity
                    {
                        Type = Nullify(parseResult.GetValue(TypeOption)) ?? captured?.Type,
                        Name = Nullify(parseResult.GetValue(NameOption)) ?? captured?.Name,
                        AutomationId = Nullify(parseResult.GetValue(AutomationIdOption)) ?? captured?.AutomationId,
                        Content = Nullify(parseResult.GetValue(ContentOption)) ?? captured?.Content,
                        TreePath = Nullify(parseResult.GetValue(TreePathOption)),
                    },
                },
            };

            var property = Nullify(parseResult.GetValue(PropertyOption));
            if (property is not null || bounds is not null)
            {
                comment.Context = new CommentContext { Property = property, Bounds = bounds };
            }

            // Without a source file the anchor is weak: an agent has to search the project and must not guess.
            if (string.IsNullOrEmpty(comment.Anchor.SourceFile))
            {
                comment.Anchor.Weak = true;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var storePath = store.GetStorePath(new DirectoryInfo(sourceRoot));
                // Upsert when the caller supplied an id (the composer re-saving an element edits in place);
                // otherwise append a fresh comment.
                var replaced = false;
                if (providedId is not null)
                {
                    comment = store.AddOrReplace(storePath, comment, out replaced);
                }
                else
                {
                    comment = store.Add(storePath, comment);
                }

                var verb = replaced ? "Updated" : "Added";
                logger.LogDebug("{Verb} comment {Id} in {Store}", verb, comment.Id, storePath);

                var warning = CommentsSharedOptions.RefreshViews(pusher, appPid, sourceRoot, storePath,
                    parseResult.GetValue(CommentsSharedOptions.SourceRootOption), cancellationToken);

                if (json)
                {
                    var view = CommentViewBuilder.ToView(comment, resolver, sourceRoot);
                    var payload = new CommentResultPayload { Ok = true, Comment = view, Warning = warning };
                    ansiConsole.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(payload, CommentsJsonContext.Default.CommentResultPayload));
                }
                else
                {
                    DevToolsRender.WriteMarkupLine(ansiConsole, $"[green]{verb}[/] comment [cyan]{Markup.Escape(comment.Id)}[/] → {Markup.Escape(storePath)}");
                    if (warning is not null)
                    {
                        DevToolsRender.WriteMarkupLine(ansiConsole, $"[yellow]{Markup.Escape(warning)}[/]");
                    }
                }

                return Task.FromResult(guestComments?.WriterExitCode(warning is not null, comment.Text != text) ?? 0);
            }
            catch (Exception ex)
            {
                return Task.FromResult(Fail(ansiConsole, json, ex.Message));
            }
        }
    }

    internal static string? Nullify(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Resolves <c>--app</c> (a pid, or a process name with/without <c>.exe</c>) to a single pid. Errors when a
    /// name matches zero or more than one process, so a comment is never captured from the wrong app.
    /// </summary>
    internal static bool TryResolvePid(IUiTargetResolver resolver, string app, CancellationToken cancellationToken, out uint pid, out string? error)
    {
        pid = 0;
        error = null;
        try
        {
            var target = resolver.ResolveProcessAsync(app, cancellationToken).GetAwaiter().GetResult();
            if (target.ProcessId <= 0)
            {
                error = $"No process was resolved for '{app}'.";
                return false;
            }
            pid = (uint)target.ProcessId;
            return true;
        }
        catch (Exception ex) when (ex is AppNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryParseBounds(string? s, out CommentBounds? bounds)
    {
        bounds = null;
        if (string.IsNullOrWhiteSpace(s))
        {
            return true;
        }

        var parts = s.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }

        var values = new double[4];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }

        bounds = new CommentBounds { X = values[0], Y = values[1], W = values[2], H = values[3] };
        return true;
    }

    internal static int Fail(IAnsiConsole ansiConsole, bool json, string message)
    {
        if (json)
        {
            var payload = new CommentResultPayload { Ok = false, Error = message };
            ansiConsole.Profile.Out.Writer.WriteLine(JsonSerializer.Serialize(payload, CommentsJsonContext.Default.CommentResultPayload));
        }
        else
        {
            DevToolsRender.WriteMarkupLine(ansiConsole, $"[red]Error:[/] {Markup.Escape(message)}");
        }

        return 1;
    }
}
