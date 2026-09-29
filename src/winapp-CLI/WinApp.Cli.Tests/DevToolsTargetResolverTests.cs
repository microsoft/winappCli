// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Covers the shared targeting rules with the app-resolution and attach collaborators faked, so the
/// decisions are tested rather than a live desktop. What matters here is that an explicit target is resolved
/// through the same service <c>winapp ui</c> uses and injection requires explicit authorization.
/// Authorized attachment requests mutation because posture is fixed by the first injection.
/// </summary>
[TestClass]
public class DevToolsTargetResolverTests
{
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, true)]
    public async Task ScopedGuest_BypassesLocalDiscoveryAndRequiresExplicitAttachment(
        bool attached, bool allowAttach, bool succeeds)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var context = new ExecutionTargets.GuestAgent.GuestCommentContext();
        var pid = process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.ActivateInspectionToken($"{pid}.{process.StartTime.ToUniversalTime().Ticks}..epoch",
            null, pid, allowAttach, default);
        var sessions = NoSessions();
        var service = new FakeAttach();
        var resolver = new DevToolsTargetResolver(sessions, service,
            () => attached ? [process.Id] : [], context);
        var result = await resolver.ResolveAsync(pid, null, default, attachIfNeeded: allowAttach);
        Assert.AreEqual(succeeds, result.Ok);
        Assert.AreEqual(succeeds, service.WasCalled);
        Assert.AreEqual(!succeeds, result.NotAttached);
        Assert.AreEqual(default, sessions.LastRequest, "A scoped guest request must not resolve a host/window name.");
        Assert.IsFalse(service.ShowOverlay);
        var rejected = await resolver.ResolveAsync("wrong-process", null, default);
        Assert.IsFalse(rejected.Ok);
    }

    [TestMethod]
    public async Task ExplicitApp_ResolvesThroughTheSameSessionServiceAsWinappUi()
    {
        var session = new FakeSessions(new UiTarget { ProcessId = 4242, ProcessName = "myapp" });
        var attach = new FakeAttach();
        var resolver = new DevToolsTargetResolver(session, attach, () => []);

        var target = await resolver.ResolveAsync("myapp", null, CancellationToken.None, attachIfNeeded: true);

        Assert.IsTrue(target.Ok);
        Assert.AreEqual(4242, target.Pid);
        Assert.AreEqual("myapp", target.ProcessName);
        Assert.AreEqual(("myapp", (long?)null), session.LastRequest);
        Assert.IsTrue(session.ProcessOnlyRequested);
        Assert.AreEqual("myapp (PID 4242)", target.Describe());
    }

    [TestMethod]
    public async Task ExplicitWindow_IsForwardedAsTheHwnd()
    {
        var session = new FakeSessions(new UiTarget { ProcessId = 99, ProcessName = "myapp" });
        var resolver = new DevToolsTargetResolver(session, new FakeAttach(), () => [99]);

        var target = await resolver.ResolveAsync(null, 657922, CancellationToken.None);

        Assert.AreEqual((null, (long?)657922), session.LastRequest);
        Assert.AreEqual(657922L, target.Window);
        Assert.IsFalse(session.ProcessOnlyRequested);
    }

    [TestMethod]
    public async Task UnattachedTarget_WithExplicitConsent_IsAttachedWritable()
    {
        var session = new FakeSessions(new UiTarget { ProcessId = 4242, ProcessName = "myapp" });
        var attach = new FakeAttach();
        var resolver = new DevToolsTargetResolver(session, attach, () => []);

        var target = await resolver.ResolveAsync("myapp", null, CancellationToken.None, attachIfNeeded: true);

        Assert.IsTrue(target.Ok);
        Assert.IsTrue(target.JustAttached);
        Assert.AreEqual(DevToolsAccess.Mutation, attach.RequestedAccess,
            "The posture is fixed by the first injection.");
        Assert.IsFalse(attach.ShowOverlay, "Targeting must never open UI the caller did not ask for.");
    }

    [TestMethod]
    public async Task FailedAttach_ReportsTheReasonRatherThanThrowing()
    {
        var session = new FakeSessions(new UiTarget { ProcessId = 4242, ProcessName = "notwinui" });
        var resolver = new DevToolsTargetResolver(session, new FakeAttach("the agent did not come up"), () => []);

        var target = await resolver.ResolveAsync("notwinui", null, CancellationToken.None, attachIfNeeded: true);

        Assert.IsFalse(target.Ok);
        StringAssert.Contains(target.Error!, "notwinui (PID 4242)");
        StringAssert.Contains(target.Error!, "the agent did not come up");
        StringAssert.Contains(target.Error!, "WinUI 3", "The most likely cause is worth naming.");
    }

    [TestMethod]
    public async Task UnresolvableApp_ReportsTheResolverMessage()
    {
        var resolver = new DevToolsTargetResolver(
            new FakeSessions(new InvalidOperationException("No window matched 'ghost'.")),
            new FakeAttach(),
            () => []);

        var target = await resolver.ResolveAsync("ghost", null, CancellationToken.None);

        Assert.IsFalse(target.Ok);
        Assert.AreEqual("No window matched 'ghost'.", target.Error);
    }

    [TestMethod]
    public async Task NoTarget_WithExactlyOneAttachedApp_UsesIt()
    {
        // One attached app needs no explicit target.
        var resolver = new DevToolsTargetResolver(NoSessions(), new FakeAttach(), () => [4242]);

        var target = await resolver.ResolveAsync(null, null, CancellationToken.None);

        Assert.IsTrue(target.Ok);
        Assert.AreEqual(4242, target.Pid);
        Assert.IsFalse(target.JustAttached, "An app that already has a pipe must not be re-attached.");
    }

    [TestMethod]
    public async Task NoTarget_WithNothingAttached_SaysHowToGetOne()
    {
        var resolver = new DevToolsTargetResolver(NoSessions(), new FakeAttach(), () => []);

        var target = await resolver.ResolveAsync(null, null, CancellationToken.None);

        Assert.IsFalse(target.Ok);
        StringAssert.Contains(target.Error!, "run --devtools");
        StringAssert.Contains(target.Error!, "--app/-a");
        StringAssert.Contains(target.Error!, "list --include-available");
        Assert.IsFalse(target.Error!.Contains("--window", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NoTarget_WithSeveralAttached_ListsThemRatherThanGuessing()
    {
        // Picking one silently would send a later `set-property` into whichever app happened to sort first.
        var resolver = new DevToolsTargetResolver(NoSessions(), new FakeAttach(), () => [111, 222, 333]);

        var target = await resolver.ResolveAsync(null, null, CancellationToken.None);

        Assert.IsFalse(target.Ok);
        StringAssert.Contains(target.Error!, "3 apps have DevTools attached");
        foreach (var pid in new[] { "111", "222", "333" })
        {
            StringAssert.Contains(target.Error!, pid, "Every candidate must be named so the next command is copy-paste.");
        }
    }

    [TestMethod]
    public async Task NotAttached_WithoutAttachOptIn_RefusesBeforeInjectingAnything()
    {
        // The security-relevant half of `devtools call`: injection loads a DLL into someone else's process
        // and is irreversible for that process's lifetime. Without the opt-in, nothing may be loaded.
        var session = new FakeSessions(new UiTarget { ProcessId = 4242, ProcessName = "notepad" });
        var attach = new FakeAttach();
        var resolver = new DevToolsTargetResolver(session, attach, () => []);

        var target = await resolver.ResolveAsync("notepad", null, CancellationToken.None);

        Assert.IsFalse(target.Ok);
        Assert.IsTrue(target.NotAttached, "The caller must be able to tell this apart from a targeting failure.");
        Assert.IsNull(attach.RequestedAccess, "NOTHING may be injected when the caller did not opt in.");
        Assert.IsFalse(attach.WasCalled, "The attach service must not be reached at all.");
    }

    [TestMethod]
    public async Task NotAttached_RefusalNamesTheExactPidAndHowToProceed()
    {
        var session = new FakeSessions(new UiTarget { ProcessId = 4242, ProcessName = "notepad" });
        var resolver = new DevToolsTargetResolver(session, new FakeAttach(), () => []);

        var target = await resolver.ResolveAsync("notepad", null, CancellationToken.None, attachIfNeeded: false);

        Assert.AreEqual(4242, target.Pid, "The error is attributed to the target, not to pid 0.");
        StringAssert.Contains(target.Error!, "notepad (PID 4242)");
        StringAssert.Contains(target.Error!, "--attach", "The way forward must be named.");
        StringAssert.Contains(target.Error!, "Nothing was loaded", "Say plainly that the process was left alone.");
    }

    [TestMethod]
    public async Task AlreadyAttached_NeedsNoAttachOptIn()
    {
        // Opting out of injection must not break the ordinary case: an app already being inspected is
        // reachable with no extra flag, because there is nothing left to inject.
        var session = new FakeSessions(new UiTarget { ProcessId = 4242, ProcessName = "myapp" });
        var attach = new FakeAttach();
        var resolver = new DevToolsTargetResolver(session, attach, () => [4242]);

        var target = await resolver.ResolveAsync("myapp", null, CancellationToken.None, attachIfNeeded: false);

        Assert.IsTrue(target.Ok);
        Assert.AreEqual(4242, target.Pid);
        Assert.IsFalse(target.JustAttached);
        Assert.IsFalse(attach.WasCalled, "An attached target needs no injection.");
    }

    [TestMethod]
    public async Task NotAttached_WithAttachOptIn_InjectsExactlyAsTheCuratedCommandsDo()
    {
        var session = new FakeSessions(new UiTarget { ProcessId = 4242, ProcessName = "myapp" });
        var attach = new FakeAttach();
        var resolver = new DevToolsTargetResolver(session, attach, () => []);

        var target = await resolver.ResolveAsync("myapp", null, CancellationToken.None, attachIfNeeded: true);

        Assert.IsTrue(target.Ok);
        Assert.IsTrue(target.JustAttached);
        Assert.AreEqual(4242, target.Pid, "The opt-in must attach the SAME pid that was resolved.");
        Assert.AreEqual(DevToolsAccess.Mutation, attach.RequestedAccess);
        Assert.IsFalse(attach.ShowOverlay);
    }

    [TestMethod]
    public async Task AttachmentDefaultsToRefusal()
    {
        var session = new FakeSessions(new UiTarget { ProcessId = 4242, ProcessName = "myapp" });
        var attach = new FakeAttach();
        var resolver = new DevToolsTargetResolver(session, attach, () => []);

        var target = await resolver.ResolveAsync("myapp", null, CancellationToken.None);

        Assert.IsFalse(target.Ok);
        Assert.IsTrue(target.NotAttached);
        Assert.IsFalse(attach.WasCalled);
    }

    private static FakeSessions NoSessions() =>
        new(new InvalidOperationException("No explicit target was supplied, so this must not be called."));

    private sealed class FakeSessions : IUiTargetResolver
    {
        public bool ProcessOnlyRequested { get; private set; }
        public Task<UiTarget> ResolveProcessAsync(string app, CancellationToken ct)
        {
            ProcessOnlyRequested = true;
            return ResolveAsync(app, null, ct);
        }

        private readonly UiTarget? _session;
        private readonly Exception? _failure;

        public FakeSessions(UiTarget session) => _session = session;

        public FakeSessions(Exception failure) => _failure = failure;

        public (string? App, long? Hwnd) LastRequest { get; private set; }

        public Task<UiTarget> ResolveAsync(string? app, long? hwnd, CancellationToken ct)
        {
            LastRequest = (app, hwnd);
            return _failure is not null ? Task.FromException<UiTarget>(_failure) : Task.FromResult(_session!);
        }
    }

    private sealed class FakeAttach(string? error = null) : IDevToolsService
    {
        public DevToolsAccess? RequestedAccess { get; private set; }

        public bool ShowOverlay { get; private set; }

        /// <summary>Whether injection was reached at all — the assertion that "nothing was loaded" needs.</summary>
        public bool WasCalled { get; private set; }

        public Task<DevToolsConnection> ConnectAsync(
            uint targetPid, bool showOverlay, DevToolsAccess requestedAccess, CancellationToken cancellationToken)
        {
            WasCalled = true;
            RequestedAccess = requestedAccess;
            ShowOverlay = showOverlay;
            return Task.FromResult(error is null ? DevToolsConnection.Ok(42) : DevToolsConnection.Fail(error));
        }
    }
}
