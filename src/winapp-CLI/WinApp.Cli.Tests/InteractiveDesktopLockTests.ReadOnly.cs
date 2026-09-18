// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

public partial class InteractiveDesktopLockTests
{
    [TestMethod]
    public async Task Observe_BlockedParentAndMissingDirectory_RunsOnceWithoutCreatingState()
    {
        Directory.CreateDirectory(_lockDirectory);
        var blockedParent = Path.Combine(_lockDirectory, "blocked-parent");
        File.WriteAllText(blockedParent, "unchanged");
        Environment.SetEnvironmentVariable(
            InteractiveDesktopPaths.LockDirectoryOverrideVariable, Path.Combine(blockedParent, "ui"));
        var calls = 0;
        using var error = new StringWriter();
        using var output = new StringWriter();
        var parse = ParseObservation(error, output, "--json");
        UiCoordinationTelemetryScope.Begin();

        var exit = await _coordinator.RunCoordinatedAsync(
            UiTurnMode.Observe, "ui inspect", parse, (_, _) =>
            {
                calls++;
                parse.InvocationConfiguration.Output.WriteLine("""{"elements":[]}""");
                return Task.FromResult(0);
            }, CancellationToken.None);

        Assert.AreEqual(0, exit);
        Assert.AreEqual(1, calls);
        Assert.AreEqual("""{"elements":[]}""" + Environment.NewLine, output.ToString());
        AssertStorageWarning(error);
        Assert.AreEqual("unchanged", File.ReadAllText(blockedParent));
        CollectionAssert.AreEqual(new[] { blockedParent }, Directory.GetFileSystemEntries(_lockDirectory));
        Assert.AreEqual(UiTurnAction.Detached, UiCoordinationTelemetryScope.Current!.TurnAction);
        Assert.AreEqual(UiCoordinationOutcome.Completed, UiCoordinationTelemetryScope.Current.Outcome);
    }

    [TestMethod]
    [DataRow((int)UiTurnMode.TurnShared)]
    [DataRow((int)UiTurnMode.DesktopExclusive)]
    public async Task StorageUnavailable_ParticipatingCommandsFailClosed(int mode)
    {
        var store = new FailingStorage { LockFailure = StorageFailure() };
        using var error = new StringWriter();
        var calls = 0;
        var action = new ReadOnlyProbeAction(CreateReadOnlyCoordinator(store), (UiTurnMode)mode, (_, _) =>
        {
            calls++;
            return Task.FromResult(0);
        });

        var exit = await action.InvokeAsync(ParseObservation(error, TextWriter.Null, "--json"));

        Assert.AreEqual(1, exit);
        Assert.AreEqual(0, calls, "mutation, capture and recording must never bypass coordination");
        Assert.AreEqual(1, store.LockAttempts);
        Assert.AreEqual(0, store.Publishes);
        using var document = JsonDocument.Parse(error.ToString());
        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable,
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Observe_StorageWarningHonorsQuiet(bool json)
    {
        var store = new FailingStorage { LockFailure = StorageFailure() };
        using var error = new StringWriter();
        using var output = new StringWriter();
        var args = json ? new[] { "--json", "--quiet" } : new[] { "--quiet" };
        var parse = ParseObservation(error, output, args);

        var exit = await CreateReadOnlyCoordinator(store).RunCoordinatedAsync(
            UiTurnMode.Observe, "ui status", parse, (_, _) => Task.FromResult(0), CancellationToken.None);

        Assert.AreEqual(0, exit);
        Assert.AreEqual(string.Empty, error.ToString());
        Assert.AreEqual(string.Empty, output.ToString());
        Assert.AreEqual(1, store.LockAttempts);
        Assert.AreEqual(0, store.Publishes);
    }

    [TestMethod]
    public async Task Observe_HumanStorageWarningUsesStderrOnly()
    {
        using var error = new StringWriter();
        using var output = new StringWriter();
        var coordinator = CreateReadOnlyCoordinator(new FailingStorage { LockFailure = StorageFailure() });

        Assert.AreEqual(0, await coordinator.RunCoordinatedAsync(
            UiTurnMode.Observe, "ui inspect", ParseObservation(error, output),
            (_, _) => Task.FromResult(0), CancellationToken.None));

        StringAssert.Contains(error.ToString(), "without desktop ordering or workflow continuity");
        Assert.AreEqual(1, error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.AreEqual(string.Empty, output.ToString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Observe_BodyStorageExceptionIsNeverReplayed(bool admissionUnavailable)
    {
        var store = new FailingStorage { LockFailure = admissionUnavailable ? StorageFailure() : null };
        var coordinator = CreateReadOnlyCoordinator(store);
        using var error = new StringWriter();
        var expected = StorageFailure();
        var calls = 0;

        var actual = await Assert.ThrowsExactlyAsync<UiCoordinationException>(() =>
            coordinator.RunCoordinatedAsync(
                UiTurnMode.Observe, "ui inspect", ParseObservation(error, TextWriter.Null, "--json"),
                (_, _) =>
                {
                    calls++;
                    throw expected;
                }, CancellationToken.None));

        Assert.AreSame(expected, actual);
        Assert.AreEqual(1, calls, "storage failure handling must not encompass the command body");
        Assert.AreEqual(1, store.LockAttempts, "a failed detached body must not retry storage on teardown");
        Assert.AreEqual(0, store.Publishes);
        Assert.AreEqual(string.Empty, error.ToString(), "do not add a success warning to a failing command");
    }

    [TestMethod]
    public async Task Observe_FailedBodyPreservesExitAndDoesNotWriteStateOrWarning()
    {
        var store = new FailingStorage { LockFailure = StorageFailure() };
        using var error = new StringWriter();
        var calls = 0;

        var exit = await CreateReadOnlyCoordinator(store).RunCoordinatedAsync(
            UiTurnMode.Observe, "ui get-value", ParseObservation(error, TextWriter.Null, "--json"),
            (_, _) =>
            {
                calls++;
                return Task.FromResult(7);
            }, CancellationToken.None);

        Assert.AreEqual(7, exit);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, store.LockAttempts);
        Assert.AreEqual(0, store.Publishes);
        Assert.AreEqual(string.Empty, error.ToString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Observe_RejectsDesktopSectionsEvenWhenDetached(bool unavailable)
    {
        var store = new FailingStorage { LockFailure = unavailable ? StorageFailure() : null };
        var coordinator = CreateReadOnlyCoordinator(store);
        using var error = new StringWriter();

        var ex = await Assert.ThrowsExactlyAsync<UiCoordinationException>(() =>
            coordinator.RunCoordinatedAsync(
                UiTurnMode.Observe, "ui inspect", ParseObservation(error, TextWriter.Null, "--json"),
                async (turn, token) =>
                {
                    await using var section = await turn.EnterAsync(token);
                    Assert.Fail("an observation must never acquire an input or capture section");
                    return 0;
                }, CancellationToken.None));

        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code);
        Assert.IsFalse(ex.IsStorageUnavailable, "misuse of the observation turn is not storage degradation");
        Assert.IsFalse(File.Exists(_paths.ActiveLockPath));
        Assert.AreEqual(string.Empty, error.ToString());
    }

    [TestMethod]
    public async Task Observe_ReadStorageFailureDetachesWithoutPublishing()
    {
        var store = new FailingStorage { ReadFailure = StorageFailure() };
        using var error = new StringWriter();

        Assert.AreEqual(0, await CreateReadOnlyCoordinator(store).RunCoordinatedAsync(
            UiTurnMode.Observe, "ui list-windows", ParseObservation(error, TextWriter.Null, "--json"),
            (_, _) => Task.FromResult(0), CancellationToken.None));

        Assert.AreEqual(1, store.LockAttempts);
        Assert.AreEqual(1, store.Reads);
        Assert.AreEqual(0, store.Publishes);
        AssertStorageWarning(error);
    }

    [TestMethod]
    public async Task Observe_PublishFailureClosesLeaseWithoutRetryingState()
    {
        var owner = new UiOwnerResolver().Resolve();
        var state = InteractiveDesktopState.CreateFresh();
        state.Owner = new OwnerRecord { Kind = owner.Kind, Key = owner.Key };
        state.IdleExpiresTick64 = Environment.TickCount64 + 60_000;
        var store = new FailingStorage { State = state, PublishFailure = StorageFailure() };
        using var error = new StringWriter();

        Assert.AreEqual(0, await CreateReadOnlyCoordinator(store).RunCoordinatedAsync(
            UiTurnMode.Observe, "ui inspect", ParseObservation(error, TextWriter.Null, "--json"),
            (_, _) => Task.FromResult(0), CancellationToken.None));

        Assert.AreEqual(1, store.LockAttempts, "no cleanup transaction may retry unavailable storage");
        Assert.AreEqual(1, store.Publishes);
        Assert.IsFalse(_participants.AnyLiveParticipant(), "a failed admission must not leave a live lease");
        AssertStorageWarning(error);
    }

    [TestMethod]
    public async Task Observe_ProgrammerErrorsInAdmissionAreNotStorageFallback()
    {
        var expected = new InvalidOperationException("broken coordinator");
        var store = new FailingStorage { LockFailure = expected };
        var calls = 0;

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            CreateReadOnlyCoordinator(store).RunCoordinatedAsync(
                UiTurnMode.Observe, "ui inspect", Parse(), (_, _) =>
                {
                    calls++;
                    return Task.FromResult(0);
                }, CancellationToken.None));

        Assert.AreSame(expected, actual);
        Assert.AreEqual(0, calls);
        Assert.AreEqual(0, store.Publishes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Observe_LiveStateAmbiguityDoesNotBecomeStorageFallback(bool corrupt)
    {
        _paths.EnsureDirectories();
        using var lease = _participants.OpenLease(424242, 12345678);
        if (corrupt)
        {
            File.WriteAllText(_paths.StatePath, "{broken");
        }

        var calls = 0;
        var ex = await Assert.ThrowsExactlyAsync<UiCoordinationException>(() =>
            RunAsync(UiTurnMode.Observe, "ui inspect", (_, _) =>
            {
                calls++;
                return Task.FromResult(0);
            }));

        Assert.AreEqual(0, calls);
        Assert.IsFalse(ex.IsStorageUnavailable);
        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable, ex.Code);
        if (corrupt)
        {
            Assert.AreEqual("{broken", File.ReadAllText(_paths.StatePath));
        }
        else
        {
            Assert.IsFalse(File.Exists(_paths.StatePath));
        }
    }

    [TestMethod]
    public async Task Observe_CancellationAfterDetachingDoesNotRetryStorage()
    {
        var store = new FailingStorage { LockFailure = StorageFailure() };
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        UiCoordinationTelemetryScope.Begin();

        var exit = await CreateReadOnlyCoordinator(store).RunCoordinatedAsync(
            UiTurnMode.Observe, "ui wait-for", ParseObservation(error, TextWriter.Null, "--json"),
            (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(0);
            }, cancellation.Token);

        Assert.AreEqual(InteractiveDesktopLock.CancelledExitCode, exit);
        Assert.AreEqual(1, store.LockAttempts);
        Assert.AreEqual(0, store.Publishes);
        using var document = JsonDocument.Parse(error.ToString());
        Assert.AreEqual(UiCoordinationErrorCodes.Cancelled,
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.AreEqual(UiCoordinationOutcome.Cancelled, UiCoordinationTelemetryScope.Current!.Outcome);
    }

    [TestMethod]
    [DataRow("relative\\locks")]
    [DataRow(@"\\server\share\locks")]
    [DataRow("   ")]
    public async Task InvalidExplicitDirectory_IsDeferredToStructuredCommandError(string invalidDirectory)
    {
        Environment.SetEnvironmentVariable(InteractiveDesktopPaths.LockDirectoryOverrideVariable, invalidDirectory);
        _ = new InteractiveDesktopPaths(new ProcessInspector());
        using var error = new StringWriter();
        var calls = 0;
        var action = new ReadOnlyProbeAction(_coordinator, UiTurnMode.Observe, (_, _) =>
        {
            calls++;
            return Task.FromResult(0);
        });

        var exit = await action.InvokeAsync(ParseObservation(error, TextWriter.Null, "--json"));

        Assert.AreEqual(1, exit);
        Assert.AreEqual(0, calls);
        using var document = JsonDocument.Parse(error.ToString());
        Assert.AreEqual(UiCoordinationErrorCodes.InvalidLockDirectory,
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.IsFalse(Directory.Exists(_lockDirectory));
    }

    [TestMethod]
    public async Task InvalidLocalArguments_AreRejectedBeforeDirectoryResolution()
    {
        Environment.SetEnvironmentVariable(InteractiveDesktopPaths.LockDirectoryOverrideVariable, "relative\\locks");
        using var error = new StringWriter();
        var action = new ReadOnlyProbeAction(_coordinator, UiTurnMode.Observe,
            (_, _) => throw new AssertFailedException("preflight must prevent execution"))
        {
            PreflightResult = 9,
        };

        Assert.AreEqual(9, await action.InvokeAsync(ParseObservation(error, TextWriter.Null, "--json")));
        Assert.AreEqual(string.Empty, error.ToString());
        Assert.IsFalse(Directory.Exists(_lockDirectory));
    }

    private InteractiveDesktopLock CreateReadOnlyCoordinator(IInteractiveDesktopStateStore store)
        => new(store, _paths, _participants, new UiOwnerResolver(), new ProcessInspector(),
            new TickCountClock(), new FakePollDelay(), _signals, new TestConsole(),
            NullLogger<InteractiveDesktopLock>.Instance);

    private static UiCoordinationException StorageFailure()
        => UiCoordinationException.StorageUnavailable("The coordination directory is not writable.");

    private static ParseResult ParseObservation(TextWriter error, TextWriter output, params string[] args)
    {
        var command = new Command("probe");
        command.Options.Add(WinAppRootCommand.JsonOption);
        command.Options.Add(WinAppRootCommand.QuietOption);
        command.Options.Add(WinAppRootCommand.VerboseOption);
        var parse = command.Parse(args);
        parse.InvocationConfiguration.Error = error;
        parse.InvocationConfiguration.Output = output;
        return parse;
    }

    private static void AssertStorageWarning(StringWriter error)
    {
        using var document = JsonDocument.Parse(error.ToString());
        Assert.AreEqual(UiCoordinationErrorCodes.Unavailable,
            document.RootElement.GetProperty("warning").GetProperty("code").GetString());
        StringAssert.Contains(error.ToString(), "without desktop ordering or workflow continuity");
    }

    private sealed class ReadOnlyProbeAction(
        IInteractiveDesktopLock coordinator,
        UiTurnMode mode,
        Func<ParseResult, CancellationToken, Task<int>> body)
        : UiCoordinatedAction(coordinator, NullLogger.Instance)
    {
        public int? PreflightResult { get; init; }

        protected override string Operation => "ui inspect";

        protected override int? Preflight(ParseResult parseResult) => PreflightResult;

        protected override UiTurnMode ResolveMode(ParseResult parseResult) => mode;

        protected override Task<int> ExecuteAsync(ParseResult parseResult, IUiTurn turn, CancellationToken cancellationToken)
            => body(parseResult, cancellationToken);
    }

    private sealed class FailingStorage : IInteractiveDesktopStateStore
    {
        public Exception? LockFailure { get; init; }
        public Exception? ReadFailure { get; init; }
        public Exception? PublishFailure { get; init; }
        public InteractiveDesktopState State { get; init; } = InteractiveDesktopState.CreateFresh();
        public int LockAttempts { get; private set; }
        public int Reads { get; private set; }
        public int Publishes { get; private set; }

        public IDisposable AcquireStateLock(CancellationToken cancellationToken)
        {
            LockAttempts++;
            if (LockFailure is { } failure)
            {
                throw failure;
            }

            return new NoopStateLock();
        }

        public StateReadResult Read()
        {
            Reads++;
            if (ReadFailure is { } failure)
            {
                throw failure;
            }

            return new StateReadResult(State, false, false);
        }

        public void Publish(InteractiveDesktopState state)
        {
            Publishes++;
            if (PublishFailure is { } failure)
            {
                throw failure;
            }
        }

        public bool IsActiveLockFree() => true;

        private sealed class NoopStateLock : IDisposable
        {
            public void Dispose() { }
        }
    }
}
