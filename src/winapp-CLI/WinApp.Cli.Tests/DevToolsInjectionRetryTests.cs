// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

/// <summary>
/// Unit tests for the cold-start injection retry. The retry loop is pure — inject, liveness, and delay
/// are all injected — so these exercise the policy with synthetic HRESULTs and no live WinUI target and no
/// real waiting. Includes an explicit mutation guard: the same fail-twice-then-succeed sequence that passes
/// with a full budget FAILS when the budget is cut, proving the retry test is not vacuous.
/// </summary>
[TestClass]
public class DevToolsInjectionRetryTests
{
    private const int SOk = 0;
    private const int ErrorNotFound = unchecked((int)0x80070490); // HRESULT_FROM_WIN32(ERROR_NOT_FOUND)
    private const int AccessDenied = unchecked((int)0x80070005);  // E_ACCESSDENIED — terminal, must fail fast
    private const int EUnexpected = unchecked((int)0x8000FFFF);

    // A delay that never actually waits, but counts how many times it was asked to — lets a test assert the
    // retry backed off between attempts without spending wall-clock time.
    private static (Func<TimeSpan, CancellationToken, Task> Delay, Func<int> Count) RecordingDelay()
    {
        var calls = 0;
        Func<TimeSpan, CancellationToken, Task> delay = (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            calls++;
            return Task.CompletedTask;
        };
        return (delay, () => calls);
    }

    // Inject stub that returns each queued HRESULT in turn (repeating the last once exhausted), recording how
    // many times it was actually called.
    private static (Func<int> Attempt, Func<int> Count) ScriptedInject(params int[] results)
    {
        var i = 0;
        Func<int> attempt = () =>
        {
            var value = results[Math.Min(i, results.Length - 1)];
            i++;
            return value;
        };
        return (attempt, () => i);
    }

    [TestMethod]
    public void IsTreeNotReady_IsTrueOnlyForErrorNotFound()
    {
        // The retry is deliberately narrow — only ERROR_NOT_FOUND means "tree not up yet". Everything else,
        // including S_OK and other failures, is terminal.
        Assert.IsTrue(DevToolsInjectionRetry.IsTreeNotReady(ErrorNotFound));
        Assert.IsFalse(DevToolsInjectionRetry.IsTreeNotReady(SOk));
        Assert.IsFalse(DevToolsInjectionRetry.IsTreeNotReady(AccessDenied));
        Assert.IsFalse(DevToolsInjectionRetry.IsTreeNotReady(EUnexpected));
    }

    [TestMethod]
    public async Task RunAsync_RetriesErrorNotFound_ThenSucceeds()
    {
        // The core cold-start scenario: the tree isn't up for the first two attempts, then it is.
        var (inject, injectCount) = ScriptedInject(ErrorNotFound, ErrorNotFound, SOk);
        var (delay, delayCount) = RecordingDelay();

        var outcome = await DevToolsInjectionRetry.RunAsync(
            attemptInject: inject,
            isTargetAlive: () => true,
            maxAttempts: 21,
            retryDelay: TimeSpan.FromMilliseconds(500),
            delayAsync: delay,
            cancellationToken: CancellationToken.None);

        Assert.AreEqual(SOk, outcome.Hr, "Should end on S_OK once the tree comes up");
        Assert.AreEqual(3, outcome.Attempts, "Two failures + one success = three attempts");
        Assert.IsFalse(outcome.TargetExited);
        Assert.IsTrue(outcome.Succeeded);
        Assert.AreEqual(3, injectCount(), "Inject should have been called exactly three times");
        Assert.AreEqual(2, delayCount(), "Should have backed off exactly twice, between the three attempts");
    }

    [TestMethod]
    public async Task RunAsync_BudgetTooSmallForTheRace_FailsToConnect_MutationGuard()
    {
        // MUTATION GUARD: the exact same fail-twice-then-succeed sequence as the success test above, but with a
        // budget of only two attempts — i.e. the retry is effectively "disabled" before it reaches the third,
        // succeeding attempt. If this still reported success, the success test would be vacuous. It must fail.
        var (inject, injectCount) = ScriptedInject(ErrorNotFound, ErrorNotFound, SOk);
        var (delay, _) = RecordingDelay();

        var outcome = await DevToolsInjectionRetry.RunAsync(
            attemptInject: inject,
            isTargetAlive: () => true,
            maxAttempts: 2,
            retryDelay: TimeSpan.FromMilliseconds(500),
            delayAsync: delay,
            cancellationToken: CancellationToken.None);

        Assert.AreEqual(ErrorNotFound, outcome.Hr, "With too few attempts the race is lost and ERROR_NOT_FOUND stands");
        Assert.AreEqual(2, outcome.Attempts);
        Assert.IsFalse(outcome.Succeeded, "A cut budget must NOT report success — this is what makes the retry test non-vacuous");
        Assert.AreEqual(2, injectCount());
    }

    [TestMethod]
    public async Task RunAsync_SucceedsFirstAttempt_DoesNotDelay()
    {
        // The warm path: the tree is already up, so we inject once and never wait.
        var (inject, injectCount) = ScriptedInject(SOk);
        var (delay, delayCount) = RecordingDelay();

        var outcome = await DevToolsInjectionRetry.RunAsync(
            inject, () => true, 21, TimeSpan.FromMilliseconds(500), delay, CancellationToken.None);

        Assert.IsTrue(outcome.Succeeded);
        Assert.AreEqual(1, outcome.Attempts);
        Assert.AreEqual(1, injectCount());
        Assert.AreEqual(0, delayCount(), "A first-attempt success must not back off at all");
    }

    [TestMethod]
    public async Task RunAsync_TerminalHResult_FailsFastWithoutRetry()
    {
        // A non-ERROR_NOT_FOUND failure (here access-denied) is terminal: return immediately even though the
        // budget is large, so the user isn't made to wait 10s for an error that will never clear.
        var (inject, injectCount) = ScriptedInject(AccessDenied, SOk /* never reached */);
        var (delay, delayCount) = RecordingDelay();

        var outcome = await DevToolsInjectionRetry.RunAsync(
            inject, () => true, 21, TimeSpan.FromMilliseconds(500), delay, CancellationToken.None);

        Assert.AreEqual(AccessDenied, outcome.Hr);
        Assert.AreEqual(1, outcome.Attempts, "A terminal HRESULT must not be retried");
        Assert.IsFalse(outcome.Succeeded);
        Assert.AreEqual(1, injectCount());
        Assert.AreEqual(0, delayCount());
    }

    [TestMethod]
    public async Task RunAsync_TargetExitedBeforeFirstAttempt_ReportsExited_NeverInjects()
    {
        // If the process is already gone, we never inject — injecting into a dead target would just surface a
        // different, confusing HRESULT. Report the exit so the caller can say "the app crashed on startup".
        var (inject, injectCount) = ScriptedInject(SOk);
        var (delay, _) = RecordingDelay();

        var outcome = await DevToolsInjectionRetry.RunAsync(
            inject, () => false, 21, TimeSpan.FromMilliseconds(500), delay, CancellationToken.None);

        Assert.IsTrue(outcome.TargetExited);
        Assert.AreEqual(0, outcome.Attempts, "A dead target should not be injected into at all");
        Assert.IsFalse(outcome.Succeeded);
        Assert.AreEqual(0, injectCount());
    }

    [TestMethod]
    public async Task RunAsync_TargetExitsDuringRetry_ReportsExited()
    {
        // Alive for the first attempt (which returns ERROR_NOT_FOUND), then the app crashes: the next liveness
        // check is false, so we stop and report the exit rather than burning the whole budget.
        var aliveChecks = 0;
        var (inject, injectCount) = ScriptedInject(ErrorNotFound, SOk /* never reached */);
        var (delay, delayCount) = RecordingDelay();

        var outcome = await DevToolsInjectionRetry.RunAsync(
            attemptInject: inject,
            isTargetAlive: () => ++aliveChecks == 1, // true on attempt 1, false on attempt 2
            maxAttempts: 21,
            retryDelay: TimeSpan.FromMilliseconds(500),
            delayAsync: delay,
            cancellationToken: CancellationToken.None);

        Assert.IsTrue(outcome.TargetExited);
        Assert.AreEqual(1, outcome.Attempts, "Only the first attempt ran before the target died");
        Assert.AreEqual(1, injectCount());
        Assert.AreEqual(1, delayCount(), "The single backoff between attempt 1 and the aborted attempt 2");
    }

    [TestMethod]
    public async Task RunAsync_ExhaustsBudget_ReturnsErrorNotFound()
    {
        // The tree never comes up: after maxAttempts of ERROR_NOT_FOUND, surface it (the caller turns this into
        // "the visual tree did not appear within Ns"), never a false success.
        var (inject, injectCount) = ScriptedInject(ErrorNotFound);
        var (delay, delayCount) = RecordingDelay();

        var outcome = await DevToolsInjectionRetry.RunAsync(
            inject, () => true, 4, TimeSpan.FromMilliseconds(500), delay, CancellationToken.None);

        Assert.AreEqual(ErrorNotFound, outcome.Hr);
        Assert.AreEqual(4, outcome.Attempts);
        Assert.IsFalse(outcome.Succeeded);
        Assert.AreEqual(4, injectCount());
        Assert.AreEqual(3, delayCount(), "Delays happen between attempts: N attempts => N-1 waits");
    }

    [TestMethod]
    public async Task RunAsync_CancellationRequested_ThrowsWithoutInjecting()
    {
        var (inject, injectCount) = ScriptedInject(SOk);
        var (delay, _) = RecordingDelay();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await DevToolsInjectionRetry.RunAsync(
                inject, () => true, 21, TimeSpan.FromMilliseconds(500), delay, cts.Token));

        Assert.AreEqual(0, injectCount(), "A pre-cancelled call should not inject");
    }

    [TestMethod]
    public async Task RunAsync_ZeroMaxAttempts_Throws()
    {
        var (inject, _) = ScriptedInject(SOk);
        var (delay, _) = RecordingDelay();

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await DevToolsInjectionRetry.RunAsync(
                inject, () => true, 0, TimeSpan.FromMilliseconds(500), delay, CancellationToken.None));
    }
}
