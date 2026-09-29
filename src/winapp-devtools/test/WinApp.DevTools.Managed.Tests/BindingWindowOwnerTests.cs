// Copyright (c) Microsoft Corporation and Contributors.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class BindingWindowOwnerTests
{
    public sealed class WindowOwner : Window
    {
        public object? Bindings;
    }

    public sealed class AppFields
    {
        public Window? First;
        public Window[] Array = [];
        public List<Window> List = [];
        public object? Unsupported;
        public Window Getter => throw new AssertFailedException("App getters must not be evaluated.");
    }

    public sealed class DerivedList : List<Window> { }

    public sealed class StaticOnlyApp
    {
        public static Window? Retained;
        public static Window Getter => throw new AssertFailedException("Static getters must not be evaluated.");
    }

    private static WindowOwner Create(object target)
    {
        var window = (WindowOwner)RuntimeHelpers.GetUninitializedObject(typeof(WindowOwner));
        window.Bindings = new BindingAcceptanceTests.Updates { dataRoot = window, obj1 = target };
        return window;
    }

    [TestMethod]
    public void SameNamedTargetsInTwoWindowsUseExactTargetAndCurrentRoot()
    {
        var firstTarget = new BindingTemplateSafetyTests.Row("Same");
        var secondTarget = new BindingTemplateSafetyTests.Row("Same");
        var first = Create(firstTarget);
        var second = Create(secondTarget);
        var app = new AppFields { First = first, Array = [first, second], List = [second] };
        var owner = BindingDiagnosis.ResolveWindowOwner(firstTarget, app, window => ReferenceEquals(window, first));
        Assert.AreSame(first, owner!.Source);
        Assert.AreSame(first.Bindings, owner.Bindings);
        Assert.IsNull(BindingDiagnosis.ResolveWindowOwner(firstTarget, app, window => ReferenceEquals(window, second)));
        Assert.AreSame(second, BindingDiagnosis.ResolveWindowOwner(secondTarget, app,
            window => ReferenceEquals(window, second))!.Source);
    }

    [TestMethod]
    public void UnrelatedClosedWindowIsNeverDereferenced()
    {
        var target = new object();
        var unrelated = Create(new object());
        var current = Create(target);
        var owner = BindingDiagnosis.ResolveWindowOwner(target,
            new AppFields { First = unrelated, Array = [current] },
            window => ReferenceEquals(window, current) ? true :
                throw new ObjectDisposedException("Unrelated Window"));
        Assert.AreSame(current, owner!.Owner);
    }

    [TestMethod]
    public void AmbiguousProofOrRootChangingDuringReadIsUnavailable()
    {
        var target = new object();
        var first = Create(target);
        var second = Create(target);
        Assert.IsNull(BindingDiagnosis.ResolveWindowOwner(target, new AppFields { Array = [first, second] }, _ => true));
        var reads = 0;
        Assert.IsNull(BindingDiagnosis.ResolveWindowOwner(target, new AppFields { First = first }, _ => ++reads == 1));
        Assert.AreEqual(2, reads, "The winning Window/root association must be rechecked.");
        Assert.IsNull(BindingDiagnosis.ResolveWindowOwner(target, new AppFields { First = first }, _ => false),
            "A closed Window or a reused root cannot satisfy the current association.");
    }

    [TestMethod]
    public void UnretainedAndUnsupportedContainersNeverBecomeOwnerCandidates()
    {
        var target = new object();
        var window = Create(target);
        var unsupported = new object[]
        {
            new AppFields { First = window },
            new DerivedList { window },
            new ThrowsOnEnumeration(),
        };
        foreach (var value in unsupported)
        {
            Assert.IsNull(BindingDiagnosis.ResolveWindowOwner(target, new AppFields { Unsupported = value },
                _ => throw new AssertFailedException("Unsupported containers must not yield a Window.")));
        }
        Assert.IsNull(BindingDiagnosis.ResolveWindowOwner(target, new AppFields(), _ => true));
    }

    private sealed class ThrowsOnEnumeration : System.Collections.IEnumerable
    {
        public System.Collections.IEnumerator GetEnumerator() =>
            throw new AssertFailedException("Arbitrary enumeration must not run.");
    }

    [TestMethod]
    public void StaticWindowIsNotReadOrEnrolled()
    {
        var target = new object();
        StaticOnlyApp.Retained = Create(target);
        try
        {
            Assert.IsNull(BindingDiagnosis.ResolveWindowOwner(target, new StaticOnlyApp(),
                _ => throw new AssertFailedException("Static fields must not yield a candidate.")));
        }
        finally { StaticOnlyApp.Retained = null; }
    }

    [TestMethod]
    [DataRow("missing-source")]
    [DataRow("wrong-source")]
    [DataRow("stale-reference")]
    [DataRow("wrong-target")]
    [DataRow("not-generated")]
    public void RootMatchAloneCannotProveGeneratedOwnership(string invalid)
    {
        var target = new object();
        var window = Create(target);
        var bindings = (BindingAcceptanceTests.Updates)window.Bindings!;
        if (invalid == "missing-source") bindings.dataRoot = null;
        if (invalid == "wrong-source") bindings.dataRoot = Create(target);
        if (invalid == "stale-reference") bindings.obj1 = new WeakReference(null);
        if (invalid == "wrong-target") bindings.obj1 = new object();
        if (invalid == "not-generated") window.Bindings = new object();
        Assert.IsNull(BindingDiagnosis.ResolveWindowOwner(target, new AppFields { First = window }, _ => true));
        Assert.AreEqual(0, bindings.Calls);
    }

    [TestMethod]
    public void UnknownNearerTemplateCannotEscapeToAWindow()
    {
        var target = new object();
        var window = Create(target);
        var app = new AppFields { First = window };
        var owner = BindingDiagnosis.ResolveCompiledOwner(target, _ => null, _ => null,
            () => BindingDiagnosis.ResolveWindowOwner(target, app, _ => true));
        Assert.AreSame(window, owner!.Owner);
        Assert.IsNull(BindingDiagnosis.ResolveCompiledOwner(target, _ => null, _ => new object(),
            () => throw new AssertFailedException("Do not escape a nearer generated scope.")));
    }

    [TestMethod]
    public void OversizedCollectionsFailExplicitlyBeforeSelection()
    {
        var target = new object();
        var app = new AppFields { Array = new Window[257] };
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            BindingDiagnosis.ResolveWindowOwner(target, app, _ => true));
        StringAssert.Contains(error.Message, "collection limit");
    }
}
