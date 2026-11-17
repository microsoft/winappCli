// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage for <see cref="StatusService"/>'s live-spinner rendering branch and its
/// defensive cancellation/error handling. Both are unreachable through the normal test
/// host (output is redirected, and <see cref="GroupableTask{T}"/> swallows body
/// exceptions), so we drive them through the <c>ShouldUseLiveSpinnerProvider</c> and
/// <c>CreateRootTask</c> seams with a task that returns a faulted <see cref="Task"/>.
/// </summary>
[TestClass]
public class StatusServiceLiveAndErrorTests
{
    private static TestConsole InteractiveConsole()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.Ansi = true;
        return console;
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_LiveSpinner_SuccessfulTask_ReturnsCode()
    {
        var console = InteractiveConsole();
        var logger = new CapturingLogger<StatusService> { MinLevel = LogLevel.Information };
        var svc = new StatusService(console, logger)
        {
            ShouldUseLiveSpinnerProvider = (_, _) => true,
        };

        var rc = await svc.ExecuteWithStatusAsync<string>(
            "running",
            async (_, ct) =>
            {
                // Delay so the live render loop iterates at least once before completion.
                await Task.Delay(30, ct);
                return (0, "done");
            },
            CancellationToken.None);

        Assert.AreEqual(0, rc);
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_LiveSpinner_NonZeroReturn_LogsErrorAndReturnsCode()
    {
        var console = InteractiveConsole();
        var logger = new CapturingLogger<StatusService> { MinLevel = LogLevel.Information };
        var svc = new StatusService(console, logger)
        {
            ShouldUseLiveSpinnerProvider = (_, _) => true,
        };

        var rc = await svc.ExecuteWithStatusAsync<string>(
            "running",
            (_, _) => Task.FromResult((3, "explicit failure")),
            CancellationToken.None);

        Assert.AreEqual(3, rc);
        Assert.IsTrue(logger.Has(LogLevel.Error, "explicit failure"));
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_PlainPath_TaskFaultsCanceled_ReturnsOne()
    {
        var console = new TestConsole();
        var logger = new CapturingLogger<StatusService> { MinLevel = LogLevel.Information };
        var svc = new FaultingStatusService(console, logger, new OperationCanceledException(), live: false);

        var rc = await svc.ExecuteWithStatusAsync<string>(
            "running", (_, _) => Task.FromResult((0, "unused")), CancellationToken.None);

        Assert.AreEqual(1, rc);
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_PlainPath_TaskFaultsGeneric_ErrorSuppressed_WritesJsonAndReturnsOne()
    {
        var console = new TestConsole();
        // MinLevel = None => Error logging disabled => the `when (!IsEnabled(Error))`
        // filtered catch fires and emits a JSON error instead of rethrowing.
        var logger = new CapturingLogger<StatusService> { MinLevel = LogLevel.None };
        var svc = new FaultingStatusService(console, logger, new InvalidOperationException("kaboom"), live: false);

        var rc = await svc.ExecuteWithStatusAsync<string>(
            "running", (_, _) => Task.FromResult((0, "unused")), CancellationToken.None);

        Assert.AreEqual(1, rc);
        Assert.Contains("kaboom", console.Output);
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_PlainPath_TaskFaultsGeneric_ErrorEnabled_Rethrows()
    {
        var console = new TestConsole();
        var logger = new CapturingLogger<StatusService> { MinLevel = LogLevel.Debug };
        var svc = new FaultingStatusService(console, logger, new InvalidOperationException("surface me"), live: false);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            svc.ExecuteWithStatusAsync<string>(
                "running", (_, _) => Task.FromResult((0, "unused")), CancellationToken.None));
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_LiveSpinner_TaskFaultsCanceled_ReturnsOne()
    {
        var console = InteractiveConsole();
        var logger = new CapturingLogger<StatusService> { MinLevel = LogLevel.Information };
        var svc = new FaultingStatusService(console, logger, new OperationCanceledException(), live: true);

        var rc = await svc.ExecuteWithStatusAsync<string>(
            "running", (_, _) => Task.FromResult((0, "unused")), CancellationToken.None);

        Assert.AreEqual(1, rc);
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_LiveSpinner_TaskFaultsGeneric_ErrorSuppressed_ReturnsOne()
    {
        var console = InteractiveConsole();
        var logger = new CapturingLogger<StatusService> { MinLevel = LogLevel.None };
        var svc = new FaultingStatusService(console, logger, new InvalidOperationException("live-kaboom"), live: true);

        var rc = await svc.ExecuteWithStatusAsync<string>(
            "running", (_, _) => Task.FromResult((0, "unused")), CancellationToken.None);

        Assert.AreEqual(1, rc);
    }

    /// <summary>
    /// A <see cref="StatusService"/> whose root task always returns a faulted task,
    /// so the surrounding error/cancellation handling is exercised deterministically.
    /// </summary>
    private sealed class FaultingStatusService : StatusService
    {
        private readonly Exception _fault;

        public FaultingStatusService(IAnsiConsole console, ILogger<StatusService> logger, Exception fault, bool live)
            : base(console, logger)
        {
            _fault = fault;
            ShouldUseLiveSpinnerProvider = (_, _) => live;
        }

        internal override GroupableTask<(int ReturnCode, TInner CompletedMessage)> CreateRootTask<TInner>(
            string inProgressMessage,
            Func<TaskContext, CancellationToken, Task<(int ReturnCode, TInner CompletedMessage)>> taskFunc,
            Lock renderLock)
            => new FaultingTask<(int ReturnCode, TInner CompletedMessage)>(_fault);
    }

    private sealed class FaultingTask<T> : GroupableTask<T>
    {
        private readonly Exception _fault;

        public FaultingTask(Exception fault)
            : base("faulting", null, null, new TestConsole(), NullLogger.Instance, new Lock())
            => _fault = fault;

        public override Task<T?> ExecuteAsync(Action? onUpdate, CancellationToken cancellationToken, bool startSpinner = true)
            => Task.FromException<T?>(_fault);
    }
}
