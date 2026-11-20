// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

/// <summary>
/// How DevTools reads Reactor values as text without running more app code than it must: collections counted, never
/// enumerated, and ToString failures contained.
/// </summary>
[TestClass]
public sealed class ReactorValueTextTests
{
    public sealed class Throws
    {
        public override string ToString() => throw new InvalidOperationException("boom");
    }

    // Enumerating this would never end; it has no Count.
    public sealed class Endless : IEnumerable<int>
    {
        public int Enumerated;
        public IEnumerator<int> GetEnumerator()
        {
            Enumerated++;
            while (true) { yield return 1; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Counted, but not an ICollection.
    public sealed class Counted : IEnumerable<int>
    {
        public int Count => 7;
        public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException("must not be enumerated");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // A collection that formats itself by enumerating; DevTools must not call that ToString.
    public sealed class SelfFormatting : List<int>
    {
        public int Formatted;
        public override string ToString() { Formatted++; return string.Join(",", this); }
    }

    public sealed class EndlessSelfFormatting : IEnumerable<int>
    {
        public int Enumerated;
        public override string ToString() => string.Join(",", this.Take(3));
        public IEnumerator<int> GetEnumerator()
        {
            Enumerated++;
            while (true) { yield return 1; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Count only through a non-public getter, which DevTools does not call.
    public sealed class HiddenCount : IEnumerable<int>
    {
        public int Read;
        public int Count { private get { Read++; return 5; } set { } }
        public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException("must not be enumerated");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class CountThrows : IEnumerable<int>
    {
        public int Count => throw new InvalidOperationException("no");
        public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException("must not be enumerated");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [TestMethod]
    public void Collections_AreCountedWithoutBeingEnumerated()
    {
        var endless = new Endless();
        Assert.AreEqual("Endless (count unknown)", ReactorElementProps.Format("Items", endless).Text);
        Assert.AreEqual(0, endless.Enumerated, "never enumerated");

        Assert.AreEqual("Counted (7 items)", ReactorElementProps.Format("Items", new Counted()).Text, "a public Count");
        Assert.AreEqual("CountThrows (count unknown)", ReactorElementProps.Format("Items", new CountThrows()).Text,
            "a Count that throws is unknown");
        Assert.AreEqual("List<int> (3 items)", ReactorElementProps.Format("Items", new List<int> { 1, 2, 3 }).Text);
    }

    [TestMethod]
    public void Collections_AreSummarisedEvenWhenTheyFormatThemselves()
    {
        var list = new SelfFormatting { 1, 2, 3 };
        Assert.AreEqual("SelfFormatting (3 items)", ReactorElementProps.Format("Items", list).Text);
        Assert.AreEqual(0, list.Formatted, "the collection's own ToString is never called");

        var endless = new EndlessSelfFormatting();
        Assert.AreEqual("EndlessSelfFormatting (count unknown)", ReactorElementProps.Format("Items", endless).Text);
        Assert.AreEqual(0, endless.Enumerated);
    }

    [TestMethod]
    public void Count_IsReadOnlyThroughAPublicGetter()
    {
        var hidden = new HiddenCount();
        Assert.AreEqual("HiddenCount (count unknown)", ReactorElementProps.Format("Items", hidden).Text);
        Assert.AreEqual(0, hidden.Read, "a private getter is not called");
        Assert.IsNull(ReactorValueText.Count(hidden));
        Assert.AreEqual(7, ReactorValueText.Count(new Counted()));
    }

    [TestMethod]
    public void AThrowingToString_IsReportedNotRaised()
    {
        Assert.AreEqual(("Throws (ToString() threw InvalidOperationException)", false),
            ReactorElementProps.Format("Content", new Throws()));
    }
}
