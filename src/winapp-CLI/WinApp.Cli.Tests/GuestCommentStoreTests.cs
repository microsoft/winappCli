// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services.DevTools.Comments;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class GuestCommentStoreTests
{
    [TestMethod]
    public void WriterFailureClassification_DoesNotChangeNormalOrAcknowledgedExitCodes()
    {
        using var context = new GuestCommentContext();
        Assert.AreEqual(1, context.ClassifyWriterResult(1));
        context.Activate(new(123, 456), Path.GetTempPath(), default, "", Guid.NewGuid().ToString("N"));
        Assert.AreEqual(GuestCommentContext.CaptureFailureExitCode, context.ClassifyWriterResult(1));
        context.BeginHostRead();
        Assert.AreEqual(GuestCommentContext.HostReadFailureExitCode, context.ClassifyWriterResult(1));
        context.BeginHostWrite();
        Assert.AreEqual(GuestCommentContext.HostWriteFailureExitCode, context.ClassifyWriterResult(1));
        context.MissingAcknowledgement();
        Assert.AreEqual(GuestCommentContext.MissingAcknowledgementExitCode, context.ClassifyWriterResult(1));
        foreach (var exit in new[] { 0, 2, GuestCommentContext.SavedRefreshFailedExitCode, GuestCommentContext.SavedThenChangedExitCode })
        {
            Assert.AreEqual(exit, context.ClassifyWriterResult(exit));
        }
    }

    [TestMethod]
    public void UnboundInspection_ProvesLifetimeAndDisablesDisposableComments()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var context = new GuestCommentContext();
        var pid = process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var start = process.StartTime.ToUniversalTime().Ticks;
        const string Epoch = "instance:boot.with.dots";
        context.ActivateInspectionToken($"{pid}.{start}..{Epoch}", null, pid, false, default);
        context.VerifyInspectionTarget(pid, null);
        Assert.IsTrue(context.HostCommentsUnavailable);
        Assert.IsFalse(context.AllowInspectionAttach);
        Assert.IsNull(context.SourceRoot);
        var store = new GuestCommentStore(new CommentStore(), context)
        {
            ExchangeAsync = (_, _, _) => throw new AssertFailedException("Unbound inspection must not contact another app's host store."),
        };
        StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(() => store.Locate()).Message,
            "Persistent host comments are unavailable");
        using var metadata = System.Text.Json.JsonDocument.Parse(
            $$"""{"mode":"unavailable","binding":"","epoch":"{{Epoch}}","start":"{{start}}"}""");
        Assert.IsTrue(context.MatchesAgent(metadata.RootElement));
        using var local = System.Text.Json.JsonDocument.Parse("null");
        Assert.IsFalse(context.MatchesAgent(local.RootElement), "An earlier unscoped injection must not be silently adopted.");
        Assert.ThrowsExactly<InvalidOperationException>(() => context.VerifyInspectionTarget("another-app", null));
        Assert.ThrowsExactly<InvalidOperationException>(() => context.VerifyInspectionTarget(pid, 123));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            context.ActivateInspectionToken($"{pid}.{start + 1}..{Epoch}", null, pid, false, default));
    }

    [TestMethod]
    public void BoundInspection_RequiresHostSnapshotAndPreservesOpaqueEpoch()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var context = new GuestCommentContext();
        var pid = process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var start = process.StartTime.ToUniversalTime().Ticks;
        var binding = Guid.NewGuid().ToString("N");
        var token = $"{pid}.{start}.{binding}.sandbox:boot.with.dots";
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            context.ActivateInspectionToken(token, null, pid, true, default));
        context.ActivateInspectionToken(token, Path.GetTempPath(), pid, true, default);
        Assert.IsFalse(context.HostCommentsUnavailable);
        Assert.AreEqual(binding, context.Session!.BindingId);
        Assert.AreEqual("sandbox:boot.with.dots", context.Session.Epoch);
        Assert.IsTrue(context.AllowInspectionAttach);
    }

    [TestMethod]
    public void WriterToken_PinsProcessLifetimeAndOriginalEditorRevision()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var context = new GuestCommentContext();
        var start = process.StartTime.ToUniversalTime().Ticks;
        var operation = Guid.NewGuid().ToString("N");
        var revision = new string('A', 64);
        var source = Path.GetTempPath();
        var binding = Guid.NewGuid().ToString("N");
        var token = $"{process.Id}.{start}.{operation}.{revision}.{binding}.epoch";
        context.ActivateToken(token, source, process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), default);
        Assert.AreEqual(start, context.Process!.StartTicksUtc);
        Assert.AreEqual(operation, context.OperationId);
        var actualRevision = context.ExpectedRevision;
        Assert.AreEqual(revision, actualRevision);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.ActivateToken(token, source, "another-app", default));
        Assert.ThrowsExactly<InvalidOperationException>(() => context.ActivateToken($"{process.Id}.{start + 1}...{binding}.epoch", source, null, default));
        Assert.ThrowsExactly<InvalidOperationException>(() => context.ActivateToken($"1.2.invalid-operation..{binding}.epoch", source, null, default));
    }

    [TestMethod]
    public void Initialization_EncodesGuestLifetimeWithoutFloatingPointLoss()
    {
        using var context = new GuestCommentContext();
        context.Activate(new(123, 638936747284321987), Path.GetTempPath(), default);
        var json = Services.DevTools.DevToolsService.BuildInitializationData(Services.DevTools.DevToolsAccess.Mutation, null, context);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual("638936747284321987", document.RootElement.GetProperty("guestCommentStart").GetString());
    }

    [TestMethod]
    public void CapturedBinding_RejectsNewOwnerOrEpochBeforeCreatingMutation()
    {
        using var context = new GuestCommentContext();
        var process = new GuestProcessStart(123, 456);
        context.Activate(process, Path.GetTempPath(), default);
        var session = new GuestCommentSession(Guid.NewGuid().ToString("N"), "target", "epoch", process);
        context.Bind(session);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Bind(session with { Epoch = "recreated" }));
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Bind(session with { BindingId = Guid.NewGuid().ToString("N") }));
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Bind(session with { TargetId = "other-project-target" }));
        Assert.AreEqual(session, context.Session);
    }

    [TestMethod]
    [DataRow("host", "epoch", "456", true)]
    [DataRow("unavailable", "epoch", "456", false)]
    [DataRow("host", "recreated", "456", false)]
    [DataRow("host", "epoch", "457", false)]
    public void NativeNegotiation_MustProveTheBoundOwner(string mode, string epoch, string start, bool expected)
    {
        using var fixture = new Fixture();
        fixture.Context.Bind(fixture.Session);
        using var metadata = System.Text.Json.JsonDocument.Parse(
            $$"""{"mode":"{{mode}}","binding":"{{fixture.Binding.Id}}","epoch":"{{epoch}}","start":"{{start}}"}""");
        Assert.AreEqual(expected, fixture.Context.MatchesAgent(metadata.RootElement));
        using var malformed = System.Text.Json.JsonDocument.Parse("""{"mode":false,"start":456}""");
        Assert.IsFalse(fixture.Context.MatchesAgent(malformed.RootElement));
    }

    [TestMethod]
    public void AuthoredIdentityRoundTripUsesTheHostOwnerAndTheGuestSnapshotRoot()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Host, "Main.xaml");
        File.Copy(source, Path.Combine(fixture.Guest, "Main.xaml"));
        var declaration = CommentAuthoredIdentity.Read(source, fixture.Host).Single();
        var note = fixture.Note("Review the page");
        note.Anchor.Identity.Type = "Page";
        note.Anchor.Authored = CommentAuthoredIdentity.Capture(fixture.Guest, "Main.xaml", declaration, declaration.Text, true);
        var store = fixture.CreateStore();
        var location = store.Locate(fixture.Guest);
        var saved = store.Add(location.StorePath, note);
        Assert.AreEqual(fixture.Host, fixture.Local.Get(location.StorePath, saved.Id)!.Anchor.Authored!.ProjectRoot);
        Assert.AreEqual(fixture.Guest, saved.Anchor.Authored!.ProjectRoot);
        Assert.IsTrue(CommentViewBuilder.ToView(saved, new CommentAnchorResolver(), fixture.Guest).AnchorConfirmed);
        store.Update(location.StorePath, saved.Id, row => row.Status = CommentStatus.Resolved);
        Assert.AreEqual(fixture.Host, fixture.Local.Get(location.StorePath, saved.Id)!.Anchor.Authored!.ProjectRoot);
        Assert.AreEqual(fixture.Guest, store.Get(location.StorePath, saved.Id)!.Anchor.Authored!.ProjectRoot);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Guest, ".winapp")));
    }

    [TestMethod]
    public void GuestWorkflow_PersistsOnlyOnHost_WithExactTextAndSnapshotProjection()
    {
        using var fixture = new Fixture();
        var store = fixture.CreateStore();
        var location = store.Locate(fixture.Guest);
        Assert.AreEqual(CommentStoreRootKind.HostBound, location.Kind);
        Assert.AreEqual(fixture.Binding.StorePath, location.StorePath);
        var saved = store.Add(location.StorePath, fixture.Note("first\r\n\n text "));
        Assert.AreEqual(fixture.Guest, saved.ProjectRoot);
        var persisted = fixture.Local.Get(location.StorePath, saved.Id)!;
        Assert.AreEqual(fixture.Host, persisted.ProjectRoot);
        Assert.AreEqual(saved.Text, persisted.Text);
        Assert.AreEqual(Environment.UserName, persisted.Author);
        Assert.AreEqual(CommentStore.Revision(persisted), store.GetRevision(saved));
        Assert.AreNotEqual(CommentStore.Revision(saved), store.GetRevision(saved),
            "The original host revision, not the guest projection's hash, guards the editor.");
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Guest, ".winapp")));
        var originalTime = saved.UpdatedAt;
        var same = store.AddOrReplace(location.StorePath, fixture.Note(saved.Text), out var replaced);
        Assert.IsTrue(replaced);
        Assert.AreEqual(originalTime, same.UpdatedAt);
        _ = store.Update(location.StorePath, saved.Id, row => row.Status = CommentStatus.Resolved);
        Assert.AreEqual(CommentStatus.Resolved, fixture.Local.Get(location.StorePath, saved.Id)!.Status);
        Assert.IsNotNull(store.Delete(location.StorePath, saved.Id));
        Assert.IsNull(fixture.Local.Get(location.StorePath, saved.Id));
    }

    [TestMethod]
    public void ConcurrentHostEdit_AndStaleComposerRevision_AreNotOverwritten()
    {
        using var fixture = new Fixture();
        var store = fixture.CreateStore();
        var path = store.GetStorePath();
        _ = store.Add(path, fixture.Note("original"));
        var revision = CommentStore.Revision(fixture.Local.Get(path, "note"));
        fixture.Context.Activate(fixture.Binding.Process, fixture.Guest, default, revision);
        fixture.Local.Update(path, "note", row => row.Text = "host changed");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            store.AddOrReplace(path, fixture.Note("stale guest draft"), out _));
        StringAssert.Contains(error.Message, "changed on the host");
        Assert.AreEqual("host changed", fixture.Local.Get(path, "note")!.Text);
        fixture.Context.Activate(fixture.Binding.Process, fixture.Guest, default);
        var racing = fixture.CreateStore(beforeApply: () =>
            fixture.Local.Update(path, "note", row => row.Text = "host changed again"));
        _ = racing.GetStorePath();
        Assert.ThrowsExactly<InvalidOperationException>(() => racing.Update(path, "note", row => row.Text = "racing guest"));
        Assert.AreEqual("host changed again", fixture.Local.Get(path, "note")!.Text);
    }

    [TestMethod]
    public void StableSaveReplay_AfterLostAckAndHostEdit_ReturnsCurrentRowWithoutOverwriting()
    {
        using var fixture = new Fixture();
        var store = fixture.CreateStore();
        var path = store.GetStorePath();
        _ = store.Add(path, fixture.Note("original"));
        var revision = CommentStore.Revision(fixture.Local.Get(path, "note"));
        var operation = Guid.NewGuid().ToString("N");
        fixture.Context.Activate(fixture.Binding.Process, fixture.Guest, default, revision, operation);
        _ = store.AddOrReplace(path, fixture.Note("guest save"), out _);
        Assert.IsNull(fixture.Local.Get(path, "note")!.Anchor.SourceProvenance,
            "Manual source coordinates have no inferred attribution to override during replay.");
        fixture.Local.Update(path, "note", row => row.Text = "newer host edit");
        var retry = fixture.Note("guest save");
        retry.CreatedAt = "another client invocation";
        retry.UpdatedAt = "a later client clock value";
        var returned = store.AddOrReplace(path, retry, out _);
        Assert.AreEqual("newer host edit", returned.Text);
        Assert.AreEqual("newer host edit", fixture.Local.Get(path, "note")!.Text);
        Assert.AreEqual(2, fixture.Local.Load(path).GuestReceipts!.Count);
    }

    [TestMethod]
    public async Task ActualWriterCommand_ReplayReportsNewerHostEditInsteadOfOldDraftSuccess()
    {
        using var fixture = new Fixture();
        var store = fixture.CreateStore();
        var path = store.GetStorePath();
        _ = store.Add(path, fixture.Note("original"));
        var revision = CommentStore.Revision(fixture.Local.Get(path, "note"));
        fixture.Context.Activate(fixture.Binding.Process, fixture.Guest, default, revision, Guid.NewGuid().ToString("N"));
        _ = store.AddOrReplace(path, fixture.Note("guest save"), out _);
        fixture.Local.Update(path, "note", row => row.Text = "newer host edit");
        using var output = new TestConsole();
        var command = new DevToolsCommentsAddCommand();
        var handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(),
            new NoMarkerPusher(), new CommentTestTargetResolver(), new CurrentDirectoryProvider(fixture.Guest),
            output, NullLogger<DevToolsCommentsAddCommand>.Instance, fixture.Context);
        var parse = command.Parse(["--id", "note", "--text", "guest save", "--file", "Main.xaml", "--source-root", fixture.Guest, "--json"]);
        Assert.IsEmpty(parse.Errors);
        Assert.AreEqual(GuestCommentContext.SavedThenChangedExitCode, await handler.InvokeAsync(parse), output.Output);
        StringAssert.Contains(output.Output, "newer host edit");
        Assert.AreEqual("newer host edit", fixture.Local.Get(path, "note")!.Text);
        Assert.IsNull(fixture.Local.Get(path, "note")!.Anchor.SourceProvenance);
        Assert.AreEqual(2, fixture.Local.Load(path).GuestReceipts!.Count);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualDeleteCommand_DistinguishesHostPersistenceFromMarkerFailure(bool nativeWriter)
    {
        using var fixture = new Fixture();
        var store = fixture.CreateStore();
        var path = store.GetStorePath();
        _ = store.Add(path, fixture.Note("delete me"));
        fixture.Context.Activate(fixture.Binding.Process, fixture.Guest, default,
            CommentStore.Revision(fixture.Local.Get(path, "note")), nativeWriter ? Guid.NewGuid().ToString("N") : null);
        using var output = new TestConsole();
        var command = new DevToolsCommentsDeleteCommand();
        var handler = new DevToolsCommentsDeleteCommand.Handler(store, new CommentAnchorResolver(), new NoMarkerPusher(),
            new CommentTestTargetResolver(123), new CurrentDirectoryProvider(fixture.Guest), output,
            NullLogger<DevToolsCommentsDeleteCommand>.Instance, fixture.Context);
        var parse = command.Parse(["note", "--app", "123", "--source-root", fixture.Guest, "--json"]);
        Assert.IsEmpty(parse.Errors);
        Assert.AreEqual(nativeWriter ? GuestCommentContext.SavedRefreshFailedExitCode : 0, await handler.InvokeAsync(parse));
        Assert.IsNull(fixture.Local.Get(path, "note"));
        StringAssert.Contains(output.Output, "warning");
    }

    [TestMethod]
    [DataRow("add", false)]
    [DataRow("add", true)]
    [DataRow("update", false)]
    [DataRow("update", true)]
    public async Task ScopedManualWriter_PersistsOnHostAndRefreshesOnlyItsGuestMarkers(string verb, bool refreshFails)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var fixture = new Fixture(new(process.Id, process.StartTime.ToUniversalTime().Ticks));
        var store = fixture.CreateStore();
        var path = store.GetStorePath();
        _ = store.Add(path, fixture.Note("before"));
        var pid = process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        fixture.Context.ActivateInspectionToken(
            $"{pid}.{fixture.Binding.Process.StartTicksUtc}.{fixture.Binding.Id}.{fixture.Binding.Epoch}",
            fixture.Guest, pid, false, default);
        using var output = new TestConsole();
        var pusher = new RecordingPusher(refreshFails);
        System.CommandLine.Command command;
        System.CommandLine.Invocation.AsynchronousCommandLineAction handler;
        string[] args;
        const string exact = "  revised\r\n\r\ntext  ";
        if (verb == "add")
        {
            command = new DevToolsCommentsAddCommand();
            handler = new DevToolsCommentsAddCommand.Handler(store, new CommentAnchorResolver(),
                pusher, new CommentTestTargetResolver(), new CurrentDirectoryProvider(fixture.Guest),
                output, NullLogger<DevToolsCommentsAddCommand>.Instance, fixture.Context);
            args = ["--id", "note", "--text", exact, "--file", "Main.xaml", "--source-root", fixture.Guest, "--json"];
        }
        else
        {
            command = new DevToolsCommentsUpdateCommand();
            handler = new DevToolsCommentsUpdateCommand.Handler(store, new CommentAnchorResolver(),
                pusher, new CommentTestTargetResolver(process.Id),
                new CurrentDirectoryProvider(fixture.Guest), output, NullLogger<DevToolsCommentsUpdateCommand>.Instance,
                fixture.Context);
            args = ["note", "--status", "stale", "--note", exact, "--app", pid, "--source-root", fixture.Guest, "--json"];
        }
        var parsed = command.Parse(args);
        Assert.IsEmpty(parsed.Errors);
        Assert.AreEqual(0, await handler.InvokeAsync(parsed));
        var persisted = fixture.Local.Get(path, "note")!;
        Assert.AreEqual(fixture.Host, persisted.ProjectRoot);
        Assert.AreEqual(verb == "add" ? exact : exact.Trim(), verb == "add" ? persisted.Text : persisted.Resolution!.Note);
        Assert.AreEqual(verb == "add" ? CommentStatus.Open : CommentStatus.Stale, persisted.Status);
        CollectionAssert.AreEqual(new[] { checked((uint)process.Id) }, pusher.Processes);
        Assert.AreEqual(fixture.Guest, pusher.SourceRoot);
        using var result = System.Text.Json.JsonDocument.Parse(output.Output);
        Assert.IsTrue(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.AreEqual(refreshFails, result.RootElement.TryGetProperty("warning", out var warning) &&
            warning.ValueKind == System.Text.Json.JsonValueKind.String);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Guest, ".winapp")));
    }

    [TestMethod]
    [DataRow(CommentStatus.Open)]
    [DataRow(CommentStatus.Resolved)]
    [DataRow(CommentStatus.Stale)]
    [DataRow(CommentStatus.Dismissed)]
    public async Task ActualUpdateCommand_ReplaysAcknowledgedStatusWithoutChangingItsTime(string status)
    {
        using var fixture = new Fixture();
        var store = fixture.CreateStore();
        var path = store.GetStorePath();
        _ = store.Add(path, fixture.Note("keep this exact\r\ntext "));
        fixture.Context.Activate(fixture.Binding.Process, fixture.Guest, default,
            CommentStore.Revision(fixture.Local.Get(path, "note")), Guid.NewGuid().ToString("N"));
        using var output = new TestConsole();
        var command = new DevToolsCommentsUpdateCommand();
        var handler = new DevToolsCommentsUpdateCommand.Handler(store, new CommentAnchorResolver(), new NoMarkerPusher(),
            new CommentTestTargetResolver(123), new CurrentDirectoryProvider(fixture.Guest), output,
            NullLogger<DevToolsCommentsUpdateCommand>.Instance, fixture.Context);
        var parse = command.Parse(["note", "--status", status, "--app", "123", "--note", "fixed", "--source-root", fixture.Guest, "--json"]);
        Assert.IsEmpty(parse.Errors);
        Assert.AreEqual(GuestCommentContext.SavedRefreshFailedExitCode, await handler.InvokeAsync(parse));
        var first = fixture.Local.Get(path, "note")!;
        Assert.AreEqual(status, first.Status);
        Assert.AreEqual(GuestCommentContext.SavedRefreshFailedExitCode, await handler.InvokeAsync(parse));
        Assert.AreEqual(first.Resolution!.ResolvedAt, fixture.Local.Get(path, "note")!.Resolution!.ResolvedAt);
        Assert.AreEqual(2, fixture.Local.Load(path).GuestReceipts!.Count);
        fixture.Local.Update(path, "note", row => row.Text = "a later host edit");
        Assert.AreEqual(1, await handler.InvokeAsync(parse));
        Assert.AreEqual("a later host edit", fixture.Local.Get(path, "note")!.Text);
        Assert.AreEqual(2, fixture.Local.Load(path).GuestReceipts!.Count);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Guest, ".winapp")));
    }

    [TestMethod]
    public void UnavailableOwner_OrWrongMapping_NeverFallsBackToGuestDisk()
    {
        using var fixture = new Fixture();
        var store = fixture.CreateStore();
        Assert.ThrowsExactly<InvalidOperationException>(() => store.Locate(fixture.Host));
        var unavailable = new GuestCommentStore(fixture.Local, fixture.Context)
        {
            ExchangeAsync = (_, _, _) => Task.FromException<GuestCommentReply>(new IOException("owner disconnected")),
        };
        Assert.ThrowsExactly<IOException>(() => unavailable.GetStorePath());
        var wrong = new GuestCommentStore(fixture.Local, fixture.Context)
        {
            ExchangeAsync = (_, request, _) => Task.FromResult(new GuestCommentReply(
                request(fixture.Session).Request.OperationId, Comments: [],
                HostStorePath: fixture.Binding.StorePath, GuestSourceRoot: fixture.Host)),
        };
        Assert.ThrowsExactly<IOException>(() => wrong.GetStorePath());
        Assert.IsFalse(File.Exists(fixture.Binding.StorePath));
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Guest, ".winapp")));
    }

    [TestMethod]
    public void DefaultContext_PreservesTheLocalStore()
    {
        using var fixture = new Fixture();
        var store = new GuestCommentStore(fixture.Local, new GuestCommentContext())
        {
            ExchangeAsync = (_, _, _) => throw new AssertFailedException("Local comments contacted a guest."),
        };
        var path = store.GetStorePath(new(fixture.Host));
        _ = store.Add(path, fixture.Note("local"));
        Assert.AreEqual("local", store.Get(path, "note")!.Text);
        Assert.IsTrue(store.Exists(path));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = TestPaths.TempRoot("guest-comment-store");
        internal string Host { get; }
        internal string Guest { get; }
        internal CommentStore Local { get; } = new();
        internal GuestCommentContext Context { get; } = new();
        internal GuestCommentBinding Binding { get; }
        internal GuestCommentSession Session => new(Binding.Id, Binding.TargetId, Binding.Epoch, Binding.Process);
        private readonly GuestCommentOwner owner;

        internal Fixture(GuestProcessStart? process = null)
        {
            Host = Path.Combine(root, "host");
            Guest = Path.Combine(root, "guest");
            Directory.CreateDirectory(Host);
            Directory.CreateDirectory(Guest);
            File.WriteAllText(Path.Combine(Host, "App.csproj"), "<Project/>");
            File.WriteAllText(Path.Combine(Host, "Main.xaml"), "<Page/>");
            Binding = new(new("sandbox", "default"), new("epoch"), process ?? new(123, 456),
                new(Path.Combine(Host, "App.csproj")), ["Main.xaml"], Guest);
            Context.Activate(Binding.Process, Guest, default);
            owner = new(Binding, Local);
        }

        internal Comment Note(string text) => new()
        {
            Id = "note", Text = text, Status = CommentStatus.Open, ProjectRoot = Guest,
            Anchor = new() { SourceFile = "Main.xaml" },
        };

        internal GuestCommentStore CreateStore(Action? beforeApply = null) => new(Local, Context)
        {
            ExchangeAsync = (_, makeRequest, _) =>
            {
                var request = makeRequest(Session);
                Binding.Validate(request.Request.BindingId, request.Request.TargetId, request.Request.Epoch, request.Request.Process);
                if (request.Kind == "read")
                {
                    return Task.FromResult(new GuestCommentReply(request.Request.OperationId, Comments: owner.Read(),
                        HostStorePath: Binding.StorePath, GuestSourceRoot: Guest));
                }
                beforeApply?.Invoke();
                return Task.FromResult(new GuestCommentReply(request.Request.OperationId, Commit: owner.Apply(request.Request)));
            },
        };

        public void Dispose()
        {
            Context.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingPusher(bool fail) : ICommentPusher
    {
        internal List<uint> Processes { get; } = [];
        internal string? SourceRoot { get; private set; }

        public (int Total, int Placed)? Push(uint pid, string fallbackRoot, string? sourceRootOverride = null,
            CancellationToken cancellationToken = default)
        {
            Processes.Add(pid);
            SourceRoot = sourceRootOverride;
            return fail ? null : (1, 1);
        }

        public (int Refreshed, int Failed) PushStore(string storePath, uint? skipPid, CancellationToken cancellationToken = default) => (0, 0);
    }

    private sealed class NoMarkerPusher : ICommentPusher
    {
        public (int Total, int Placed)? Push(uint pid, string fallbackRoot, string? sourceRootOverride = null,
            CancellationToken cancellationToken = default) => null;

        public (int Refreshed, int Failed) PushStore(string storePath, uint? skipPid, CancellationToken cancellationToken = default) => (0, 0);
    }
}
