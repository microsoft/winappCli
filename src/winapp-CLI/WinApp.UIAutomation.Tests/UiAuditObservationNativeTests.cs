// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices.Marshalling;
using Windows.Win32.System.Com;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
public sealed class UiAuditObservationNativeTests
{
    private static readonly string[] ExpectedSubscriptionRemovals = ["structure", "focus"];

    [TestMethod]
    [DataRow(12, 34L, 12u, 34L, true)]
    [DataRow(12, 34L, 99u, 34L, false)]
    [DataRow(12, 34L, 12u, 56L, false)]
    [DataRow(12, 34L, 12u, 0L, false)]
    [DataRow(0, 34L, 0u, 34L, false)]
    [DataRow(12, 0L, 12u, 0L, false)]
    public void NativeScopeRequiresExactProcessAndExactRoot(int processId, long root, uint candidateProcessId,
        long candidateRoot, bool expected)
        => Assert.AreEqual(expected, NativeUiAuditObservationScope.Matches(processId, root, candidateProcessId, candidateRoot));

    [TestMethod]
    public void UnknownPrivacyIsNeverCoercedToFalse()
    {
        using var unknown = default(ComVariant);
        using var number = ComVariant.Create(0);
        using var safe = ComVariant.Create(false);
        using var password = ComVariant.Create(true);

        Assert.IsNull(UiAuditObservationPrivacy.Boolean(unknown));
        Assert.IsNull(UiAuditObservationPrivacy.Boolean(number));
        Assert.AreEqual(false, UiAuditObservationPrivacy.Boolean(safe));
        Assert.AreEqual(true, UiAuditObservationPrivacy.Boolean(password));
    }

    [TestMethod]
    public void PrivacyUnknownPasswordAndTextSourcesNeverReadNames()
    {
        foreach (var password in new bool?[] { null, true, false })
        {
            foreach (var textBearing in new bool?[] { null, true, false })
            {
                var reads = 0;
                var name = UiAuditObservationPrivacy.ReadName(password, textBearing, () =>
                {
                    reads++;
                    return "static label";
                });
                var safe = password == false && textBearing == false;
                Assert.AreEqual(safe ? 1 : 0, reads);
                Assert.AreEqual(safe ? "static label" : null, name);
                Assert.AreEqual(!safe, UiAuditObservationPrivacy.WithholdName(password, textBearing));
            }
        }
    }

    [TestMethod]
    public void RuntimeIdsAreUppercaseHexAndNeverRequireAutomationIds()
    {
        Assert.AreEqual("[2A,7705FE,4,6]", UiAuditObservationPrivacy.RuntimeId([42, 0x7705fe, 4, 6]));
        Assert.AreEqual("[FFFFFFFF,0,AB]", UiAuditObservationPrivacy.RuntimeId([-1, 0, 171]));
        Assert.IsNull(UiAuditObservationPrivacy.RuntimeId([]));
        Assert.IsNull(UiAuditObservationPrivacy.RuntimeId(new int[129]));
    }

    [TestMethod]
    public unsafe void NativeRuntimeArrayRejectsMalformedOrUnboundedInputs()
    {
        Assert.IsNull(NativeUiAuditObservation.ReadRuntimeId(null));
        var array = new SAFEARRAY();
        int* values = stackalloc int[] { 42, 0x7705fe, 4, 6 };
        array.pvData = values;
        array.cDims = 1;
        array.cbElements = sizeof(int);
        array.rgsabound[0].cElements = 4;
        Assert.AreEqual("[2A,7705FE,4,6]", NativeUiAuditObservation.ReadRuntimeId(&array));
        array.cDims = 2;
        Assert.IsNull(NativeUiAuditObservation.ReadRuntimeId(&array));
        array.cDims = 1;
        array.rgsabound[0].cElements = 129;
        Assert.IsNull(NativeUiAuditObservation.ReadRuntimeId(&array));
        array.rgsabound[0].cElements = 4;
        array.cbElements = 8;
        Assert.IsNull(NativeUiAuditObservation.ReadRuntimeId(&array));
    }

    [TestMethod]
    public void EventBudgetHasNoUnboundedOverflowQueue()
    {
        using var budget = new UiAuditObservationBudget(60, TimeProvider.System, CancellationToken.None);
        var buffer = new UiAuditEventBuffer(2, budget);
        for (var i = 0; i < 5001; i++)
        {
            buffer.Add(new UiAuditEvent { Kind = "focus" });
        }

        Assert.HasCount(2, buffer.Events());
        Assert.IsTrue(buffer.EventLimitReached);
        Assert.IsFalse(buffer.CanAccept);
        Assert.AreEqual("event_limit", buffer.StopReason);
    }

    [TestMethod]
    public void CancelledBufferRejectsFurtherCallbacks()
    {
        using var cancellation = new CancellationTokenSource();
        using var budget = new UiAuditObservationBudget(60, TimeProvider.System, cancellation.Token);
        var buffer = new UiAuditEventBuffer(100, budget);
        buffer.Add(new UiAuditEvent { Kind = "focus" });
        cancellation.Cancel();
        buffer.Add(new UiAuditEvent { Kind = "notification" });

        Assert.HasCount(1, buffer.Events());
        Assert.IsFalse(buffer.CanAccept);
    }

    [TestMethod]
    public void LocalIdentitiesOnlyReuseKnownRuntimeIds()
    {
        using var budget = new UiAuditObservationBudget(60, TimeProvider.System, CancellationToken.None);
        var buffer = new UiAuditEventBuffer(100, budget);

        Assert.AreEqual(buffer.Identity("[2A,AA]"), buffer.Identity("[2A,AA]"));
        Assert.AreNotEqual(buffer.Identity("[2A,AA]"), buffer.Identity("[2A,AB]"));
        Assert.AreNotEqual(buffer.Identity(null), buffer.Identity(null));
    }

    [TestMethod]
    public void FailedEventRegistrationIsAnExplicitCoverageIssue()
    {
        using var budget = new UiAuditObservationBudget(60, TimeProvider.System, CancellationToken.None);
        var buffer = new UiAuditEventBuffer(100, budget);
        var subscriptions = new UiAuditEventSubscriptions(buffer);
        var removals = new List<string>();
        Assert.IsTrue(subscriptions.Add("focus", () => { }, () => removals.Add("focus")));
        Assert.IsFalse(subscriptions.Add("notification", () => throw new NotSupportedException("private provider detail"),
            () => removals.Add("notification")));
        Assert.IsTrue(subscriptions.Add("structure", () => { }, () => removals.Add("structure")));

        subscriptions.Dispose();
        subscriptions.Dispose();

        CollectionAssert.AreEqual(ExpectedSubscriptionRemovals, removals);
        Assert.HasCount(1, buffer.Issues());
        Assert.AreEqual("notification_events_unavailable", buffer.Issues()[0].Code);
        StringAssert.Contains(buffer.Issues()[0].Message, "No polling substitute");
        Assert.IsFalse(buffer.Issues()[0].Message.Contains("private provider detail", StringComparison.Ordinal));
        Assert.IsTrue(buffer.CoverageLimited);
        Assert.IsEmpty(buffer.Events());
    }

    [TestMethod]
    public void CleanupFailureDoesNotSkipOtherSubscriptions()
    {
        using var budget = new UiAuditObservationBudget(60, TimeProvider.System, CancellationToken.None);
        var buffer = new UiAuditEventBuffer(100, budget);
        var subscriptions = new UiAuditEventSubscriptions(buffer);
        var owner = Environment.CurrentManagedThreadId;
        var removed = false;
        subscriptions.Add("focus", () => { }, () =>
        {
            Assert.AreEqual(owner, Environment.CurrentManagedThreadId);
            removed = true;
        });
        subscriptions.Add("structure", () => { }, () => throw new InvalidOperationException());

        subscriptions.Dispose();

        Assert.IsTrue(removed);
        Assert.AreEqual("event_cleanup_failed", buffer.Issues().Single().Code);
    }

    [TestMethod]
    public void RepeatedIssuesAreBoundedAndDeduplicated()
    {
        using var budget = new UiAuditObservationBudget(60, TimeProvider.System, CancellationToken.None);
        var buffer = new UiAuditEventBuffer(100, budget);
        for (var i = 0; i < 5000; i++)
        {
            buffer.Issue("privacy_unknown", "Unknown privacy; names were withheld.", limitsCoverage: false);
        }

        Assert.HasCount(1, buffer.Issues());
        Assert.IsFalse(buffer.CoverageLimited);
    }
}
