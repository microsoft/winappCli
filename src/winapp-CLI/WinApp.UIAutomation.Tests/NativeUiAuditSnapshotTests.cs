// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
[DoNotParallelize]
public sealed partial class NativeUiAuditSnapshotTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task WorkerCreatesAndDisposesWithinItsOwnMta()
    {
        var caller = Environment.CurrentManagedThreadId;
        var disposedThread = 0;
        var (thread, apartment) = await NativeUiAuditSnapshotService.RunOnMta(() =>
        {
            using var scope = new DisposeAction(() => disposedThread = Environment.CurrentManagedThreadId);
            return (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState());
        });

        Assert.AreNotEqual(caller, thread);
        Assert.AreEqual(ApartmentState.MTA, apartment);
        Assert.AreEqual(thread, disposedThread);
    }

    [TestMethod]
    public async Task RealReservedSentinelsRemainMixedAndUnsupported()
    {
        await NativeUiAuditSnapshotService.RunOnMta(() =>
        {
            using var owned = new NativeUiAuditSnapshotComScope();
            var automation = owned.Add(CUIAutomation8.CreateInstance<IUIAutomation>())!;
            var mixed = owned.Add(automation.get_ReservedMixedAttributeValue())!;
            var unsupported = owned.Add(automation.get_ReservedNotSupportedValue())!;
            unsafe
            {
                // Each variant owns the reference returned by the source-generated marshaller.
                using var mixedVariant = ComVariant.CreateRaw(VarEnum.VT_UNKNOWN,
                    (nint)ComInterfaceMarshaller<object>.ConvertToUnmanaged(mixed));
                using var unsupportedVariant = ComVariant.CreateRaw(VarEnum.VT_UNKNOWN,
                    (nint)ComInterfaceMarshaller<object>.ConvertToUnmanaged(unsupported));
                Assert.IsTrue(NativeUiAuditSnapshotReader.IsSameUnknown(mixedVariant, mixed));
                Assert.IsFalse(NativeUiAuditSnapshotReader.IsSameUnknown(mixedVariant, unsupported));
                Assert.IsFalse((bool)automation.CheckNotSupported(mixedVariant));
                Assert.IsTrue((bool)automation.CheckNotSupported(unsupportedVariant));

                var mixedField = UiAuditFieldReader.Read(mixedVariant, UiAuditFieldKind.Color, UiAuditFieldSentinel.Mixed);
                var unsupportedField = UiAuditFieldReader.Read(unsupportedVariant, UiAuditFieldKind.Color, UiAuditFieldSentinel.Unsupported);
                Assert.AreEqual("mixed", mixedField.Status);
                Assert.AreEqual("unsupported", unsupportedField.Status);
                Assert.IsNull(mixedField.Number);
                Assert.IsNull(unsupportedField.Number);
                Assert.AreEqual("VT_UNKNOWN", mixedField.NativeType);
                Assert.AreEqual("VT_UNKNOWN", unsupportedField.NativeType);
            }
            return true;
        });
    }

    [TestMethod]
    public unsafe void SafeArrayRuntimeIdsRespectTypeAndNonzeroLowerBounds()
    {
        int[] values = [42, 0x7705fe, 4, 6];
        var array = SafeArrayCreateVector((ushort)VarEnum.VT_I4, 7, (uint)values.Length);
        Assert.IsTrue(array is not null);
        try
        {
            void* data = null;
            PInvoke.SafeArrayAccessData(array, &data).ThrowOnFailure();
            try { values.AsSpan().CopyTo(new Span<int>(data, values.Length)); }
            finally { PInvoke.SafeArrayUnaccessData(array).ThrowOnFailure(); }

            var copied = NativeUiAuditSnapshotArrays.ReadIntegers(array, 128, out var truncated);
            CollectionAssert.AreEqual(values, copied);
            Assert.IsFalse(truncated);
            Assert.AreEqual("[2A,7705FE,4,6]", UiAuditSnapshotTraversal.RuntimeId(copied));
        }
        finally
        {
            PInvoke.SafeArrayDestroy(array).ThrowOnFailure();
        }
    }

    [TestMethod]
    public unsafe void SafeArrayBoundsAreCappedAndNeverReinterpretedAsIntegers()
    {
        var values = Enumerable.Range(0, 200).Select(value => (double)value).ToArray();
        var array = SafeArrayCreateVector((ushort)VarEnum.VT_R8, 0, (uint)values.Length);
        Assert.IsTrue(array is not null);
        try
        {
            void* data = null;
            PInvoke.SafeArrayAccessData(array, &data).ThrowOnFailure();
            try { values.AsSpan().CopyTo(new Span<double>(data, values.Length)); }
            finally { PInvoke.SafeArrayUnaccessData(array).ThrowOnFailure(); }

            var copied = NativeUiAuditSnapshotArrays.ReadDoubles(array, 128, out var truncated);
            Assert.AreEqual(128, copied.Length);
            CollectionAssert.AreEqual(values[..128], copied);
            Assert.IsTrue(truncated);
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeUiAuditSnapshotArrays.ReadIntegers(array, 128, out _));
        }
        finally
        {
            PInvoke.SafeArrayDestroy(array).ThrowOnFailure();
        }
    }

    [TestMethod]
    public void InvalidNativeHandleIsRejectedWithoutAProcessSearch()
    {
        var error = Assert.ThrowsExactly<UiAuditSnapshotException>(() =>
            new NativeUiAuditSnapshotScope(new UiTarget { WindowHandle = long.MaxValue, ProcessId = Environment.ProcessId }));

        Assert.AreEqual("invalid_target", error.Code);
    }

    [TestMethod]
    public void NativeWindowProcessMismatchIsRejectedBeforeUiaCreation()
    {
        RequireInteractiveDesktop();
        using var fixture = new UiaTestFixture();
        var error = Assert.ThrowsExactly<UiAuditSnapshotException>(() =>
            new NativeUiAuditSnapshotScope(new UiTarget { WindowHandle = fixture.Hwnd, ProcessId = fixture.ProcessId + 1 }));

        Assert.AreEqual("target_mismatch", error.Code);
    }

    [TestMethod]
    public void SelectingAChildControlAsTheSnapshotRootIsRejected()
    {
        RequireInteractiveDesktop();
        using var fixture = new UiaTestFixture();
        var child = fixture.HandleOf(fixture.ValueBox);
        var error = Assert.ThrowsExactly<UiAuditSnapshotException>(() =>
            new NativeUiAuditSnapshotScope(new UiTarget { WindowHandle = child, ProcessId = fixture.ProcessId }));

        Assert.AreEqual("invalid_target", error.Code);
    }

    [TestMethod]
    public async Task SnapshotExcludesOwnedTopLevelWindowsButRetainsNativeChildControls()
    {
        RequireInteractiveDesktop();
        using var fixture = new UiaTestFixture();
        var (ownedHwnd, ownedTitle) = fixture.OpenOwnedWindow("Snapshot excluded owned dialog", ownedByMain: true);
        var service = new NativeUiAuditSnapshotService();
        var options = new UiAuditSnapshotOptions { MaxDepth = 2, MaxElements = 500, MaxTextRunsPerNode = 1, TimeoutMs = 60000 };

        var main = await service.CaptureAsync(Target(fixture), options, TestContext.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(75), TestContext.CancellationToken);

        Assert.IsTrue(main.Nodes.Any(node => node.AutomationId == "txtValue"), "A native child HWND whose GA_ROOT is the selected main HWND must remain in scope.");
        Assert.IsFalse(main.Nodes.Any(node => node.AutomationId is "ownedForm" or "btnOwned" or "btnOwnedShared" or "lblOwnedShared" or "btnOwnedOnly"));
        Assert.IsFalse(main.Nodes.Any(node => node.Name == ownedTitle));
        Assert.IsTrue(main.Issues.Any(issue => issue.Code == "window_boundary_excluded" && issue.ExcludedWindowHandle == (long)ownedHwnd),
            "An owned top-level dialog encountered by raw traversal must be reported as an excluded native window boundary.");

        var owned = await service.CaptureAsync(new UiTarget { WindowHandle = ownedHwnd, ProcessId = fixture.ProcessId },
            options, TestContext.CancellationToken).WaitAsync(TimeSpan.FromSeconds(75), TestContext.CancellationToken);

        Assert.AreEqual((long)ownedHwnd, owned.WindowHandle);
        Assert.IsTrue(owned.Nodes.Any(node => node.AutomationId == "btnOwnedOnly"), "Selecting the owned top-level HWND directly must include its native child controls.");
        Assert.IsFalse(owned.Nodes.Any(node => node.AutomationId == "txtValue"), "A selected owned dialog must not expand to its owner.");
        Assert.IsFalse(owned.Issues.Any(issue => issue.Code == "window_boundary_excluded" && issue.ExcludedWindowHandle == (long)ownedHwnd));
    }

    [TestMethod]
    public async Task SnapshotKeepsExactWindowEvenWhenTargetIsMutableAndNotExplicit()
    {
        RequireInteractiveDesktop();
        using var first = new UiaTestFixture();
        using var second = new UiaTestFixture();
        var target = Target(first);
        var service = new NativeUiAuditSnapshotService();
        var capture = service.CaptureAsync(target, new() { MaxDepth = 1, MaxElements = 100, TimeoutMs = 30000 }, TestContext.CancellationToken);
        target.WindowHandle = second.Hwnd;
        target.ProcessId = 0;
        var snapshot = await capture.WaitAsync(TimeSpan.FromSeconds(45), TestContext.CancellationToken);

        Assert.AreEqual((long)first.Hwnd, snapshot.WindowHandle);
        Assert.AreEqual(first.ProcessId, snapshot.ProcessId);
        Assert.IsNotEmpty(snapshot.Nodes);
        Assert.AreEqual("n1", snapshot.Nodes[0].Id);
        Assert.IsNull(snapshot.Nodes[0].ParentId);
        Assert.IsTrue(snapshot.Nodes.All(node => node.Depth <= 1));
        Assert.IsFalse(snapshot.Nodes.Any(node => node.Name == second.Title));
        Assert.AreNotEqual("Do not copy this unverified title", snapshot.Title);
        Assert.AreEqual(1, snapshot.SchemaVersion);
    }

    [TestMethod]
    public async Task PasswordMetadataIsWithheldAndValueContentsNeverEnterSnapshot()
    {
        RequireInteractiveDesktop();
        using var fixture = new UiaTestFixture();
        fixture.OnUiThread(() =>
        {
            fixture.ValueBox.UseSystemPasswordChar = true;
            fixture.ValueBox.AccessibleName = "private-password-accessible-name";
            fixture.ValueBox.Text = "private-password-value";
            fixture.ResultBox.Text = "private-non-password-value";
        });

        var snapshot = await new NativeUiAuditSnapshotService().CaptureAsync(Target(fixture),
            new() { MaxDepth = 3, MaxElements = 200, MaxTextRunsPerNode = 2, TimeoutMs = 30000 }, TestContext.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(45), TestContext.CancellationToken);
        var password = snapshot.Nodes.Single(node => node.AutomationId == "txtValue");

        Assert.AreEqual((bool?)true, password.IsPassword);
        Assert.IsNull(password.Name);
        Assert.IsTrue(password.TextRuns.All(run => run.ForegroundColor.Status == "withheld"
            && run.BackgroundColor.Status == "withheld" && run.FontSize.Status == "withheld"
            && run.FontWeight.Status == "withheld" && run.IsHidden.Status == "withheld" && run.Bounds.Count == 0));
        Assert.IsTrue(password.Issues.Any(issue => issue.Code == "privacy_withheld"));
        Assert.IsTrue(snapshot.Nodes.All(node => node.TextRuns.Count <= 2));
        var json = JsonSerializer.Serialize(snapshot, UiAuditSnapshotTestJsonContext.Default.UiAuditSnapshotResult);
        Assert.IsFalse(json.Contains("private-password-accessible-name", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("private-password-value", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("private-non-password-value", StringComparison.Ordinal));
        Assert.IsTrue(snapshot.Issues.Any(issue => issue.Code == "privacy_notice"));
        Assert.AreEqual("private-non-password-value", fixture.OnUiThread(() => fixture.ResultBox.Text), "Snapshot capture must not change the owned app.");
    }

    private static UiTarget Target(UiaTestFixture fixture) => new()
    {
        WindowHandle = fixture.Hwnd,
        ProcessId = fixture.ProcessId,
        WindowTitle = "Do not copy this unverified title",
        IsExplicitWindow = false,
    };

    private static void RequireInteractiveDesktop()
    {
        if (!Environment.UserInteractive)
        {
            Assert.Inconclusive("Real UIA snapshot tests require an interactive desktop for their owned fixture.");
        }
    }

    [LibraryImport("oleaut32.dll")]
    private static unsafe partial SAFEARRAY* SafeArrayCreateVector(ushort type, int lowerBound, uint count);

    private sealed class DisposeAction(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

[JsonSerializable(typeof(UiAuditSnapshotResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class UiAuditSnapshotTestJsonContext : JsonSerializerContext;
