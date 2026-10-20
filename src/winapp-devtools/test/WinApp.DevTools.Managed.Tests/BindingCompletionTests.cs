// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class BindingCompletionTests
{
    [TestMethod]
    public void LateCallbackAfterTimeoutCompletesSafely()
    {
        Action? queued = null;
        bool ran = false;
        var elapsed = Stopwatch.StartNew();
        var result = BindingDiagnosis.RunOnUiThread(action => { queued = action; return true; },
            () => { ran = true; return "completed"; });
        elapsed.Stop();
        StringAssert.Contains(result, "the app UI thread did not respond");
        Assert.IsTrue(elapsed.Elapsed >= TimeSpan.FromSeconds(9.5) && elapsed.Elapsed < TimeSpan.FromSeconds(12),
            $"Actual timeout was {elapsed.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.IsFalse(ran);
        Assert.IsNotNull(queued);
        queued();
        Assert.IsFalse(ran, "Pending timeout must cancel queued work before a late callback.");
    }

    [TestMethod]
    public void SuccessfulCallbackReturnsItsResult()
    {
        Assert.AreEqual("completed", BindingDiagnosis.RunOnUiThread(action => { action(); return true; }, () => "completed"));
    }

    [TestMethod]
    public void RejectedEnqueueDoesNotRunWork()
    {
        using var fixture = new BindingFixtureInterop();
        using var apartment = new BindingTarget.Apartment();
        var owner = BindingTarget.Consume(fixture.Publish());
        fixture.Revoke();
        var result = BindingDiagnosis.RunOnUiThread(_ => false, () => throw new AssertFailedException("Must not run"));
        StringAssert.Contains(result, "not accepting work");
        result = BindingDiagnosis.RunOnUiThread(_ => false, () => "not run", owner);
        Assert.IsTrue(owner.IsClosed);
        fixture.AssertBaseline();
    }

    [TestMethod]
    public void CallbackExceptionIsUnavailable()
    {
        var result = BindingDiagnosis.RunOnUiThread(action => { action(); return true; },
            () => throw new InvalidOperationException("controlled failure"));
        StringAssert.Contains(result, "\"state\":\"unavailable\"");
        StringAssert.Contains(result, "controlled failure");
    }

    [TestMethod]
    public void ActualConsumedOwnerIsIndependentOfRevocationAndRejectedProjectionDisposes()
    {
        using var apartment = new BindingTarget.Apartment();
        using var fixture = new BindingFixtureInterop();
        byte[] packet = fixture.Publish();
        var owner = BindingTarget.Consume(packet);
        fixture.Revoke();
        Assert.ThrowsExactly<COMException>(() => BindingTarget.Consume(packet));
        StringAssert.Contains(BindingDiagnosis.Run(owner, "diagnose", "Text", ""), "not a FrameworkElement");
        Assert.IsTrue(owner.IsClosed);
        // Generic RCW cleanup is normal GC, not explicit transport ownership.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        fixture.AssertBaseline();
    }

    [TestMethod]
    public void RunningTimeoutKeepsActualOwnerUntilCallbackFinishes()
    {
        using var apartment = new BindingTarget.Apartment();
        using var fixture = new BindingFixtureInterop();
        var owner = BindingTarget.Consume(fixture.Publish());
        fixture.Revoke();
        using var entered = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        Task? callback = null;
        var result = Task.Run(() => BindingDiagnosis.RunOnUiThread(action =>
        {
            callback = Task.Run(action);
            return true;
        }, () => { entered.Set(); if (!finish.Wait(15000)) throw new TimeoutException(); return "finished"; }, owner)).GetAwaiter().GetResult();
        StringAssert.Contains(result, "did not respond");
        Assert.IsTrue(entered.IsSet);
        Assert.IsFalse(owner.IsClosed);
        finish.Set();
        Assert.IsNotNull(callback);
        callback.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Assert.IsTrue(owner.IsClosed);
        fixture.AssertBaseline();
    }

    [TestMethod]
    public void PendingTimeoutReleasesActualOwnerAndPreventsLateUse()
    {
        using var apartment = new BindingTarget.Apartment();
        using var fixture = new BindingFixtureInterop();
        var owner = BindingTarget.Consume(fixture.Publish());
        fixture.Revoke();
        Action? queued = null;
        var response = BindingDiagnosis.RunOnUiThread(action => { queued = action; return true; },
            () => throw new AssertFailedException("Cancelled work ran"), owner);
        StringAssert.Contains(response, "did not respond");
        Assert.IsTrue(owner.IsClosed);
        queued!();
        fixture.AssertBaseline();
    }

    [TestMethod]
    public void ConcurrentAcquireAndRevokeEitherOwnsOrRefusesAndCannotRetargetReuse()
    {
        using var apartment = new BindingTarget.Apartment();
        using var fixture = new BindingFixtureInterop();
        for (int cycle = 0; cycle < 8; ++cycle)
        {
            byte[] packet = fixture.Publish();
            using var start = new ManualResetEventSlim();
            var acquire = Task.Run(() =>
            {
                using var mta = new BindingTarget.Apartment();
                start.Wait();
                try { using var owner = BindingTarget.Consume(packet); return true; }
                catch (COMException) { return false; }
            });
            start.Set();
            fixture.Revoke();
            _ = acquire.GetAwaiter().GetResult();
            Assert.ThrowsExactly<COMException>(() => BindingTarget.Consume(packet));
            fixture.AssertBaseline();
        }
    }

    [TestMethod]
    public void ProjectionAndQueueExceptionsReleaseActualTransportOwner()
    {
        using var apartment = new BindingTarget.Apartment();
        using var fixture = new BindingFixtureInterop();
        foreach (bool enqueueFailure in new[] { false, true })
        {
            var owner = BindingTarget.Consume(fixture.Publish());
            fixture.Revoke();
            var result = BindingDiagnosis.RunOnUiThread(action =>
            {
                if (enqueueFailure) throw new InvalidOperationException("queue fault");
                action(); return true;
            }, () => throw new InvalidOperationException("projection fault"), owner);
            StringAssert.Contains(result, enqueueFailure ? "queue fault" : "projection fault");
            Assert.IsTrue(owner.IsClosed);
            fixture.AssertBaseline();
        }
        var disposed = BindingTarget.Consume(fixture.Publish());
        fixture.Revoke();
        disposed.Dispose();
        StringAssert.Contains(BindingDiagnosis.Run(disposed, "diagnose", "Text", ""), "unavailable");
        fixture.AssertBaseline();
    }

    [TestMethod]
    public void AbandonedActualConsumedReferenceFinalizesWithoutPacketLeak()
    {
        using var apartment = new BindingTarget.Apartment();
        using var fixture = new BindingFixtureInterop();
        Abandon(fixture.Publish());
        fixture.Revoke();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        fixture.AssertBaseline();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Abandon(byte[] bytes) { _ = BindingTarget.Consume(bytes); }
}

internal sealed class BindingFixtureInterop : IDisposable
{
    internal static string Path { get; } = Locate();
    private nint value;
    private bool published;
    private readonly BindingTarget.Apartment apartment = new();
    private static string Locate()
    {
        string path = Environment.GetEnvironmentVariable("WINAPP_BINDING_TEST_FIXTURE")
            ?? System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bin", "binding-transfer-fixture.dll"));
        if (!File.Exists(path)) throw new FileNotFoundException("Build the native binding test fixture with build-devtools.ps1 first.", path);
        NativeLibrary.SetDllImportResolver(typeof(BindingFixtureInterop).Assembly,
            (name, _, _) => name == "binding-transfer-fixture" ? NativeLibrary.Load(path) : nint.Zero);
        return path;
    }
    [DllImport("binding-transfer-fixture")] private static extern int BindingFixtureCreate(out nint state);
    [DllImport("binding-transfer-fixture")] private static extern int BindingFixtureDestroy(nint state);
    [DllImport("binding-transfer-fixture")] private static extern int BindingFixturePublish(nint state, byte[] bytes, uint capacity, out uint length);
    [DllImport("binding-transfer-fixture")] private static extern int BindingFixtureRevoke(nint state);
    [DllImport("binding-transfer-fixture")] private static extern void BindingFixtureCounts(nint state, out uint refs, out uint late, out uint revoked);
    internal BindingFixtureInterop() { _ = Path; Marshal.ThrowExceptionForHR(BindingFixtureCreate(out value)); }
    internal byte[] Publish()
    {
        var bytes = new byte[65536];
        Marshal.ThrowExceptionForHR(BindingFixturePublish(value, bytes, (uint)bytes.Length, out var length));
        published = true;
        return bytes.AsSpan(0, (int)length).ToArray();
    }
    internal void Revoke() { Marshal.ThrowExceptionForHR(BindingFixtureRevoke(value)); published = false; }
    internal void AssertBaseline()
    {
        BindingFixtureCounts(value, out var refs, out var late, out _);
        Assert.AreEqual(1u, refs, "Only the observer's retained baseline reference may remain.");
        Assert.AreEqual(0u, late);
    }
    public void Dispose()
    {
        if (published) Revoke();
        Marshal.ThrowExceptionForHR(BindingFixtureDestroy(value));
        value = 0;
        apartment.Dispose();
    }
}
