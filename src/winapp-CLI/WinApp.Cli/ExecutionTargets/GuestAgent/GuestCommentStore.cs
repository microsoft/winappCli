// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.ExecutionTargets.GuestAgent;

// Local store by default; guest invocations persist exclusively through the bound host owner.
internal sealed class GuestCommentContext : IDisposable
{
    internal const int SavedRefreshFailedExitCode = 0x57410001;
    internal const int SavedThenChangedExitCode = 0x57410002;
    internal const int ContextFailureExitCode = 0x57410101;
    internal const int CaptureFailureExitCode = 0x57410102;
    internal const int HostReadFailureExitCode = 0x57410103;
    internal const int HostWriteFailureExitCode = 0x57410104;
    internal const int MissingAcknowledgementExitCode = 0x57410105;
    internal int FailureExitCode { get; private set; } = CaptureFailureExitCode;
    private Process? lifetime;
    internal GuestProcessStart? Process { get; private set; }
    internal string? SourceRoot { get; private set; }
    internal string? ExpectedRevision { get; private set; }
    internal string? OperationId { get; private set; }
    internal GuestCommentSession? Session { get; private set; }
    internal CancellationToken CancellationToken { get; private set; }
    internal bool ScopedInspection { get; private set; }
    internal bool AllowInspectionAttach { get; private set; }
    internal bool HostCommentsUnavailable { get; private set; }

    internal void ActivateInspectionToken(
        string token, string? sourceRoot, string? app, bool allowAttach, CancellationToken cancellationToken)
    {
        var parts = token.Split('.', 4);
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            start <= 0 || start > DateTime.MaxValue.Ticks ||
            (parts[2].Length != 0 && !Guid.TryParseExact(parts[2], "N", out _)) ||
            string.IsNullOrEmpty(parts[3]) || parts[3].Any(char.IsControl) ||
            app != parts[0] ||
            (parts[2].Length != 0 && (sourceRoot is null || !Path.IsPathFullyQualified(sourceRoot))) ||
            (parts[2].Length == 0 && sourceRoot is not null))
        {
            throw new InvalidOperationException("Invalid guest inspection lifetime, binding, source snapshot, or mismatched application.");
        }
        HoldProcess(pid, start);
        Process = new(pid, start);
        SourceRoot = sourceRoot is null ? null : Path.GetFullPath(sourceRoot);
        Session = new(parts[2], string.Empty, parts[3], Process);
        ExpectedRevision = null;
        OperationId = null;
        CancellationToken = cancellationToken;
        ScopedInspection = true;
        AllowInspectionAttach = allowAttach;
        HostCommentsUnavailable = parts[2].Length == 0;
    }

    internal void VerifyInspectionTarget(string? app, long? window)
    {
        if (!ScopedInspection)
        {
            return;
        }
        if (window is not null || Process is null ||
            app != Process.ProcessId.ToString(CultureInfo.InvariantCulture) ||
            lifetime is null || lifetime.HasExited)
        {
            throw new InvalidOperationException("The guest inspection request does not match its live scoped application. Rediscover the app.");
        }
    }

    internal void ActivateToken(string token, string sourceRoot, string? app, CancellationToken cancellationToken)
    {
        var parts = token.Split('.', 6);
        if (parts.Length != 6 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start) ||
            !Guid.TryParseExact(parts[4], "N", out _) || string.IsNullOrWhiteSpace(parts[5]) ||
            (app is not null && app != parts[0]))
        {
            throw new InvalidOperationException("Invalid guest comment application lifetime or mismatched --app.");
        }
        Activate(new(pid, start), sourceRoot, cancellationToken,
            parts[2].Length == 0 ? null : parts[3], parts[2].Length == 0 ? null : parts[2]);
        Session = new(parts[4], string.Empty, parts[5], Process!);
        HoldProcess(pid, start);
    }

    private void HoldProcess(int pid, long start)
    {
        var process = System.Diagnostics.Process.GetProcessById(pid);
        try
        {
            _ = process.Handle;
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != start)
            {
                throw new InvalidOperationException("The guest application lifetime expired. Rediscover the app.");
            }
            lifetime?.Dispose();
            lifetime = process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    public void Dispose() => lifetime?.Dispose();

    internal void Bind(GuestCommentSession session)
    {
        if (session.Process != Process || (Session is { } existing &&
            (existing.BindingId != session.BindingId || existing.Epoch != session.Epoch ||
                (existing.TargetId.Length != 0 && existing.TargetId != session.TargetId))))
        {
            throw new InvalidOperationException("The guest comment binding or target epoch changed. Rediscover the application.");
        }
        Session = session;
    }

    internal bool MatchesAgent(JsonElement metadata) => AgentMismatch(metadata) is null;

    internal string? AgentMismatch(JsonElement metadata)
    {
        if (Process is not { } process || Session is not { } session)
        {
            return "host context is not bound";
        }
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return "guestComments is missing";
        }
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            return "guestComments is not an object";
        }
        return Mismatch("mode", HostCommentsUnavailable ? "unavailable" : "host") ??
            Mismatch("binding", session.BindingId) ??
            Mismatch("epoch", session.Epoch) ??
            Mismatch("start", process.StartTicksUtc.ToString(CultureInfo.InvariantCulture));

        string? Mismatch(string name, string expected)
        {
            if (!metadata.TryGetProperty(name, out var property))
            {
                return $"{name} is missing";
            }
            if (property.ValueKind != JsonValueKind.String)
            {
                return $"{name} is not a string";
            }
            return property.GetString() != expected ? $"{name} is mismatched" : null;
        }
    }

    internal int WriterExitCode(bool markerRefreshFailed, bool currentChanged = false) => OperationId is null ? 0 :
        currentChanged ? SavedThenChangedExitCode : markerRefreshFailed ? SavedRefreshFailedExitCode : 0;

    internal void BeginHostRead() => FailureExitCode = HostReadFailureExitCode;
    internal void BeginHostWrite() => FailureExitCode = HostWriteFailureExitCode;
    internal void MissingAcknowledgement() => FailureExitCode = MissingAcknowledgementExitCode;
    internal int ClassifyWriterResult(int result) => result == 1 && OperationId is not null ? FailureExitCode : result;

    internal void Activate(GuestProcessStart process, string sourceRoot, CancellationToken cancellationToken,
        string? expectedRevision = null, string? operationId = null)
    {
        if (process.ProcessId <= 0 || process.StartTicksUtc <= 0 || !Path.IsPathFullyQualified(sourceRoot) ||
            (expectedRevision is not null && expectedRevision != string.Empty && !CommentStore.ValidRevision(expectedRevision)) ||
            (operationId is not null && !Guid.TryParseExact(operationId, "N", out _)))
        {
            throw new InvalidOperationException("Host comments require an exact guest application lifetime and source snapshot.");
        }
        if (Process != process)
        {
            Session = null;
        }
        Process = process;
        SourceRoot = Path.GetFullPath(sourceRoot);
        ExpectedRevision = expectedRevision;
        OperationId = operationId;
        CancellationToken = cancellationToken;
        ScopedInspection = false;
        AllowInspectionAttach = false;
        HostCommentsUnavailable = false;
    }

    internal static int? TryActivateGuestContext(
        System.CommandLine.ParseResult parsedArgs,
        IServiceProvider serviceProvider,
        bool effectiveJson)
    {
        if (!ExecutionTargetSelection.IsCommandInvocation(parsedArgs))
        {
            return null;
        }

        if (parsedArgs.GetValue(WinAppRootCommand.GuestInspectionOption) is { } guestInspection)
        {
            try
            {
                if (!ExecutionTargetSelection.Resolve(parsedArgs).IsLocal ||
                    !Program.IsDescendantOf(parsedArgs, "devtools") ||
                    parsedArgs.CommandResult.Command is DevToolsListCommand ||
                    parsedArgs.GetValue(WinAppRootCommand.GuestCommentsOption) is not null)
                {
                    throw new InvalidOperationException("Guest inspection context is only valid inside a scoped guest DevTools invocation.");
                }
                var app = parsedArgs.CommandResult.Command switch
                {
                    DevToolsAttachCommand => parsedArgs.GetValue(DevToolsAttachCommand.PidOption)
                        .ToString(CultureInfo.InvariantCulture),
                    DevToolsCommentsAddCommand => parsedArgs.GetValue(DevToolsCommentsAddCommand.AppOption),
                    DevToolsCommentsDeleteCommand => parsedArgs.GetValue(DevToolsCommentsDeleteCommand.AppOption),
                    DevToolsCommentsUpdateCommand => parsedArgs.GetValue(DevToolsCommentsUpdateCommand.AppOption),
                    _ => parsedArgs.GetValue(SharedUiOptions.AppOption),
                };
                if (app is null && Program.IsDescendantOf(parsedArgs, "comments"))
                {
                    app = guestInspection.Split('.', 2)[0];
                }
                var sourceRoot = Environment.GetEnvironmentVariable("WINAPP_DEVTOOLS_SOURCE_ROOT");
                var context = serviceProvider.GetRequiredService<GuestCommentContext>();
                context.ActivateInspectionToken(guestInspection,
                    string.IsNullOrEmpty(sourceRoot) ? null : sourceRoot, app,
                    parsedArgs.CommandResult.Command is DevToolsAttachCommand ||
                        (parsedArgs.CommandResult.Command is DevToolsLiveCommand &&
                            parsedArgs.GetValue(SharedDevToolsOptions.AttachOption)),
                    CancellationToken.None);
                if (!context.HostCommentsUnavailable)
                {
                    _ = serviceProvider.GetRequiredService<ICommentStore>().Locate(context.SourceRoot);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or
                UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                EmitDevToolsError(parsedArgs, effectiveJson, ex.Message);
                return 1;
            }
        }

        if (parsedArgs.GetValue(WinAppRootCommand.GuestCommentsOption) is { } guestComments)
        {
            try
            {
                if (!ExecutionTargetSelection.Resolve(parsedArgs).IsLocal || !Program.IsDescendantOf(parsedArgs, "comments"))
                {
                    throw new InvalidOperationException("Guest comment context is only valid inside the guest comment writer.");
                }
                var source = parsedArgs.GetValue(CommentsSharedOptions.SourceRootOption) ??
                    Environment.GetEnvironmentVariable("WINAPP_DEVTOOLS_SOURCE_ROOT") ??
                    throw new InvalidOperationException("The guest comment source snapshot is unavailable.");
                var app = parsedArgs.CommandResult.Command switch
                {
                    DevToolsCommentsAddCommand => parsedArgs.GetValue(DevToolsCommentsAddCommand.AppOption),
                    DevToolsCommentsDeleteCommand => parsedArgs.GetValue(DevToolsCommentsDeleteCommand.AppOption),
                    DevToolsCommentsUpdateCommand => parsedArgs.GetValue(DevToolsCommentsUpdateCommand.AppOption),
                    _ => null,
                };
                serviceProvider.GetRequiredService<GuestCommentContext>()
                    .ActivateToken(guestComments, source, app, CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                EmitDevToolsError(parsedArgs, effectiveJson, ex.Message);
                return ContextFailureExitCode;
            }
        }

        return null;
    }

    private static void EmitDevToolsError(System.CommandLine.ParseResult parsedArgs, bool effectiveJson, string message)
    {
        if (effectiveJson)
        {
            Program.EmitDevToolsJsonError(parsedArgs, message);
        }
        else
        {
            Console.Error.WriteLine(message);
        }
    }
}

internal sealed class GuestCommentStore(CommentStore local, GuestCommentContext context) : ICommentStore
{
    private readonly Dictionary<string, Comment> originals = new(StringComparer.Ordinal);
    private CommentStoreLocation? location;
    private long generation;
    internal Func<GuestProcessStart, Func<GuestCommentSession, GuestCommentEnvelope>, CancellationToken, Task<GuestCommentReply>>
        ExchangeAsync
    { get; init; } = GuestCommentClient.ExchangeAsync;

    public string? GetRevision(Comment comment) => context.Process is null ? null :
        originals.TryGetValue(comment.Id, out var original) ? CommentStore.Revision(original) :
        throw new InvalidOperationException("The guest comment revision has not been read from the host.");

    public CommentStoreLocation Locate(string? startDirectory = null)
    {
        if (context.HostCommentsUnavailable)
        {
            throw new InvalidOperationException("Persistent host comments are unavailable for this late-attached application. " +
                "Launch its project with 'winapp run --devtools --on sandbox' to bind a host store and source snapshot.");
        }
        if (context.Process is null)
        {
            return local.Locate(startDirectory);
        }
        if (startDirectory is not null && !CommentStoreLocator.SameProject(startDirectory, context.SourceRoot))
        {
            throw new InvalidOperationException("This guest comment owner is bound to another source snapshot.");
        }
        Refresh();
        return location!;
    }

    public string GetStorePath(DirectoryInfo? baseDirectory = null) => Locate(baseDirectory?.FullName).StorePath;

    public bool Exists(string storePath)
    {
        if (context.Process is null)
        {
            return File.Exists(storePath);
        }
        RequireStore(storePath);
        Refresh();
        return true;
    }

    public CommentStoreDocument Load(string storePath)
    {
        if (context.Process is null)
        {
            return local.Load(storePath);
        }
        RequireStore(storePath);
        Refresh();
        return new() { Generation = generation, Comments = originals.Values.Select(ToSnapshot).ToList() };
    }

    public Comment? Get(string storePath, string id) =>
        context.Process is null ? local.Get(storePath, id) : Load(storePath).Comments.Find(row => row.Id == id);

    public Comment Add(string storePath, Comment comment)
    {
        if (context.Process is null)
        {
            return local.Add(storePath, comment);
        }
        RequireStore(storePath);
        return Apply(comment.Id, string.Empty, comment)
            ?? throw new IOException("The host did not return the persisted comment.");
    }

    public Comment AddOrReplace(string storePath, Comment comment, out bool replaced)
    {
        if (context.Process is null)
        {
            return local.AddOrReplace(storePath, comment, out replaced);
        }
        RequireStore(storePath);
        Refresh();
        replaced = originals.TryGetValue(comment.Id, out var previous);
        return Apply(comment.Id, RevisionFor(previous), comment)
            ?? throw new IOException("The host did not return the persisted comment.");
    }

    public Comment? Update(string storePath, string id, Action<Comment> mutate)
    {
        if (context.Process is null)
        {
            return local.Update(storePath, id, mutate);
        }
        var comment = Get(storePath, id);
        if (comment is null)
        {
            return null;
        }
        var revision = RevisionFor(originals[id]);
        mutate(comment);
        return Apply(id, revision, comment);
    }

    public Comment? Delete(string storePath, string id)
    {
        if (context.Process is null)
        {
            return local.Delete(storePath, id);
        }
        var previous = Get(storePath, id);
        if (previous is null)
        {
            return null;
        }
        _ = Apply(id, RevisionFor(originals[id]), null);
        return previous;
    }

    private string RevisionFor(Comment? previous) => context.ExpectedRevision ?? CommentStore.Revision(previous);

    private Comment? Apply(string id, string expected, Comment? replacement)
    {
        context.BeginHostWrite();
        var operation = context.OperationId ?? Guid.NewGuid().ToString("N");
        var reply = ExchangeAsync(context.Process!, hello =>
        {
            context.Bind(hello);
            return new("apply", new(hello.BindingId, hello.TargetId, hello.Epoch, hello.Process, operation, id, expected, replacement));
        },
            context.CancellationToken).GetAwaiter().GetResult();
        if (reply.Commit is not { } commit)
        {
            context.MissingAcknowledgement();
            throw new IOException("The host did not acknowledge durable comment persistence.");
        }
        if (commit.Current is { } row)
        {
            originals[id] = row;
            return ToSnapshot(row);
        }
        originals.Remove(id);
        return null;
    }

    private void Refresh()
    {
        context.BeginHostRead();
        var operation = Guid.NewGuid().ToString("N");
        var reply = ExchangeAsync(context.Process!, hello =>
        {
            context.Bind(hello);
            return new("read", new(hello.BindingId, hello.TargetId, hello.Epoch, hello.Process, operation, string.Empty, string.Empty, null));
        },
            context.CancellationToken).GetAwaiter().GetResult();
        if (reply.Comments is null || string.IsNullOrWhiteSpace(reply.HostStorePath) ||
            !CommentStoreLocator.SameProject(reply.GuestSourceRoot, context.SourceRoot) ||
            !Path.IsPathFullyQualified(reply.HostStorePath))
        {
            throw new IOException("The host did not confirm this guest snapshot's persistent comment store.");
        }
        var root = Path.GetDirectoryName(Path.GetDirectoryName(reply.HostStorePath))!;
        var current = new CommentStoreLocation(root, CommentStoreRootKind.HostBound, context.SourceRoot!);
        if (!string.Equals(current.StorePath, reply.HostStorePath, StringComparison.OrdinalIgnoreCase) ||
            (location is not null && current != location))
        {
            throw new IOException("The host comment store identity changed. Reconnect to the application.");
        }
        location = current;
        generation = reply.Generation;
        originals.Clear();
        foreach (var comment in reply.Comments)
        {
            if (!originals.TryAdd(comment.Id, comment))
            {
                throw new IOException("The host comment response contains duplicate identities.");
            }
        }
    }

    private void RequireStore(string path)
    {
        if (location is null)
        {
            Refresh();
        }
        if (!string.Equals(path, location!.StorePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Guest comments cannot select a different host store.");
        }
    }

    private Comment ToSnapshot(Comment row)
    {
        var sourceRoot = context.SourceRoot ??
            throw new InvalidOperationException("The bound guest comment source root is unavailable.");
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(row, CommentsJsonContext.Default.Comment),
            CommentsJsonContext.Default.Comment)!;
        copy.ProjectRoot = sourceRoot;
        if (copy.Anchor.Authored is { } authored)
        {
            authored.ProjectRoot = sourceRoot;
        }
        return copy;
    }
}
