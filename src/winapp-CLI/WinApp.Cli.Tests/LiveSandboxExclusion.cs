// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Tests;

/// <summary>
/// Serializes every gated live test across the whole assembly, because Windows permits exactly one
/// Sandbox at a time.
/// </summary>
/// <remarks>
/// <c>[DoNotParallelize]</c> only serializes methods within one class, and the live coverage lives
/// in two: <see cref="SandboxLiveE2ETests"/> and <c>SandboxLiveAdoptionTests</c>. MSTest happily
/// runs those two classes at once, and then both drive the same Sandbox — one replaces the agent
/// the other is mid-handshake with, and the second reports "the agent did not report ready in time"
/// as though the product were broken. Every one of these tests passes alone; the failure is the
/// harness, and this is where it is fixed rather than in a comment telling people to run them one
/// at a time.
/// <para>
/// A process-wide semaphore rather than a named system mutex: this serializes the tests in this run,
/// which is the actual problem. Two developers running the suite on one machine at the same time
/// already contend for the single Sandbox itself, and the product's own connection lock is what
/// speaks to that.
/// </para>
/// <para>
/// Callers must acquire only after their skip checks have passed. MSTest does not run
/// <c>[TestCleanup]</c> for a test that skipped during initialization, so a skipped test holding
/// this lock would never return it and every later live test would wait on it forever — which, in
/// the ungated run everyone does by default, means the whole suite hangs.
/// </para>
/// </remarks>
internal static class LiveSandboxExclusion
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Waits for exclusive use of the machine's one Sandbox.</summary>
    public static Task AcquireAsync(CancellationToken cancellationToken) => Gate.WaitAsync(cancellationToken);

    /// <summary>Releases it. Safe to call when it was never acquired.</summary>
    public static void Release()
    {
        try
        {
            Gate.Release();
        }
        catch (SemaphoreFullException)
        {
            // A test that was skipped before acquiring still runs its cleanup. Releasing a lock it
            // never took must not turn a skip into a failure.
        }
    }
}
