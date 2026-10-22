// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

/// <summary>
/// Callbacks the agent runs outside any request's catch (UI callbacks after a timeout, the keyed-list timer, render
/// highlights) degrade instead of throwing into the app.
/// </summary>
[TestClass]
public sealed class ReactorAgentCrashSafetyTests
{
    private sealed class Target : IDisposable
    {
        public int Disposed;
        public void Dispose() => Interlocked.Increment(ref Disposed);
        public object Project() => Volatile.Read(ref Disposed) == 0 ? this : throw new ObjectDisposedException(nameof(Target));
    }

    [TestMethod]
    public void RunOnUi_ACallbackAfterTheTimeout_NeverProjectsTheReleasedTarget()
    {
        var target = new Target();
        Action? queued = null;
        var answer = ReactorComponentOps.RunOnUi<Target>(target, target.Project, (_, work) => { queued = work; return true; },
            mutation: true, timeoutMs: 20, (_, done) => done("{\"state\":\"ok\"}"));

        StringAssert.Contains(answer, "\"indeterminate\":true");
        Assert.AreEqual(1, target.Disposed, "the timeout released the target the callback never claimed");
        queued!();
        Assert.AreEqual(1, target.Disposed, "the late callback did nothing: no projection, no second release");
    }

    [TestMethod]
    public void RunOnUi_ATimeoutWhileTheCallbackRuns_LeavesTheTargetToTheCallback()
    {
        var target = new Target();
        using var claimed = new ManualResetEventSlim();
        using var timedOut = new ManualResetEventSlim();
        Thread? ui = null;
        var projected = false;
        var answer = ReactorComponentOps.RunOnUi<Target>(target, () =>
            {
                if (Thread.CurrentThread == ui)
                {
                    claimed.Set();
                    timedOut.Wait(5000);
                    projected = true;
                }
                return target.Project();
            },
            (_, work) => { ui = new Thread(() => work()); ui.Start(); return true; },
            mutation: false, timeoutMs: 200, (_, done) => { claimed.Wait(); });

        timedOut.Set();
        ui!.Join(5000);
        StringAssert.Contains(answer, "\"state\":\"unavailable\"");
        Assert.IsTrue(projected, "the callback projected the target after the request timed out, and that did not throw");
        Assert.AreEqual(1, target.Disposed, "the callback released it once, after it was done with it");
    }

    [TestMethod]
    public void RunOnUi_AProjectionThatThrowsInTheCallback_AnswersUnavailable()
    {
        var target = new Target();
        var calls = 0;
        var answer = ReactorComponentOps.RunOnUi<Target>(target,
            () => ++calls == 1 ? target : throw new ObjectDisposedException("BindingTarget"),
            (_, work) => { work(); return true; }, mutation: false, timeoutMs: 5000, (_, done) => done("{\"state\":\"ok\"}"));

        StringAssert.Contains(answer, "\"state\":\"unavailable\"");
        StringAssert.Contains(answer, "ObjectDisposedException");
        Assert.AreEqual(1, target.Disposed);
    }

    [TestMethod]
    public void RunOnUi_AStartThatThrows_AnswersUnavailable()
    {
        var target = new Target();
        var answer = ReactorComponentOps.RunOnUi<Target>(target, target.Project, (_, work) => { work(); return true; },
            mutation: true, timeoutMs: 5000, (_, _) => throw new InvalidCastException("Reactor changed"));

        StringAssert.Contains(answer, "InvalidCastException");
        Assert.AreEqual(1, target.Disposed);
    }

    [TestMethod]
    public void KeyedListPoll_AFakeReactorThatThrows_DoesNotEscapeTheTimer()
    {
        var source = ReactorEvents.KeyedLists.Source;
        var sink = ReactorEvents.KeyedLists.Sink;
        try
        {
            ReactorEvents.KeyedLists.Source = () => throw new NullReferenceException("Reactor moved its warnings");
            ReactorEvents.KeyedLists.Poll();
            ReactorEvents.KeyedLists.Source = () => new object[] { new { ControlContext = "CrashSafetyList", Kind = "DuplicateKey", Count = 1 } };
            ReactorEvents.KeyedLists.Sink = _ => throw new IOException("the log is gone");
            ReactorEvents.KeyedLists.Poll();
        }
        finally
        {
            ReactorEvents.KeyedLists.Source = source;
            ReactorEvents.KeyedLists.Sink = sink;
        }
    }

    [TestMethod]
    public void RenderHighlights_AComponentThatCannotBeResolved_IsDropped()
    {
        var items = new[]
        {
            new KeyValuePair<long, (string, string)>(1, ("Counter", "state")),
            new KeyValuePair<long, (string, string)>(2, ("Clock", "state")),
        };
        var asked = new List<long>();
        ReactorEvents.RenderHighlights.Send(items, id =>
        {
            asked.Add(id);
            return id == 1 ? throw new InvalidOperationException("the control was collected") : null;
        });
        CollectionAssert.AreEqual(new long[] { 1, 2 }, asked, "the failing component is skipped and the rest are still resolved");
    }
}
