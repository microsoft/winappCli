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
    public sealed record Theme(string Name, string Key);

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
        Assert.AreEqual(("Endless (count unknown)", false), ReactorComponents.Format("", typeof(Endless), endless));
        Assert.AreEqual("Endless (count unknown)", ReactorElementProps.Format("Items", endless).Text);
        Assert.AreEqual(0, endless.Enumerated, "never enumerated");

        Assert.AreEqual("Counted (7 items)", ReactorComponents.Format("", typeof(Counted), new Counted()).Text, "a public Count");
        Assert.AreEqual("CountThrows (count unknown)", ReactorComponents.Format("", typeof(CountThrows), new CountThrows()).Text,
            "a Count that throws is unknown");
        Assert.AreEqual("List<int> (3 items)", ReactorComponents.Format("", typeof(List<int>), new List<int> { 1, 2, 3 }).Text);
        Assert.AreEqual("List<int> (3 items)", ReactorElementProps.Format("Items", new List<int> { 1, 2, 3 }).Text);
    }

    [TestMethod]
    public void Collections_AreSummarisedEvenWhenTheyFormatThemselves()
    {
        var list = new SelfFormatting { 1, 2, 3 };
        Assert.AreEqual(("SelfFormatting (3 items)", false), ReactorComponents.Format("", typeof(SelfFormatting), list));
        Assert.AreEqual("SelfFormatting (3 items)", ReactorElementProps.Format("Items", list).Text);
        Assert.AreEqual(0, list.Formatted, "the collection's own ToString is never called");

        var endless = new EndlessSelfFormatting();
        Assert.AreEqual("EndlessSelfFormatting (count unknown)", ReactorComponents.Format("", typeof(EndlessSelfFormatting), endless).Text);
        Assert.AreEqual("EndlessSelfFormatting (count unknown)", ReactorElementProps.Format("Items", endless).Text);
        Assert.AreEqual(0, endless.Enumerated);
    }

    [TestMethod]
    public void Count_IsReadOnlyThroughAPublicGetter()
    {
        var hidden = new HiddenCount();
        Assert.AreEqual("HiddenCount (count unknown)", ReactorComponents.Format("", typeof(HiddenCount), hidden).Text);
        Assert.AreEqual(0, hidden.Read, "a private getter is not called");
        Assert.IsNull(ReactorValueText.Count(hidden));
        Assert.AreEqual(7, ReactorValueText.Count(new Counted()));
    }

    [TestMethod]
    public void ScalarProps_AreOneValueRow()
    {
        var id = Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff");
        Assert.AreEqual(("Props", "6f9619ff-8b86-d011-b42d-00cf4fc964ff", false), ReactorElementProps.ComponentProps(id).Single());
        Assert.AreEqual(("Props", "00:01:30", false), ReactorElementProps.ComponentProps(TimeSpan.FromSeconds(90)).Single());
        var when = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual("Props", ReactorElementProps.ComponentProps(when).Single().Name);
        Assert.AreEqual("Props", ReactorElementProps.ComponentProps(new DateTimeOffset(when)).Single().Name);
        Assert.AreEqual("Props", ReactorElementProps.ComponentProps(DateOnly.FromDateTime(when)).Single().Name);
        Assert.AreEqual(("Props", "https://example.com/", false), ReactorElementProps.ComponentProps(new Uri("https://example.com")).Single());
        CollectionAssert.AreEqual(new[] { "Name", "Key" }, ReactorElementProps.ComponentProps(new Theme("Dark", "k")).Select(p => p.Name).ToArray(),
            "a record still lists its members");
    }

    [TestMethod]
    public void AThrowingToString_IsReportedNotRaised()
    {
        Assert.AreEqual(("Throws (ToString() threw InvalidOperationException)", false),
            ReactorComponents.Format("", typeof(Throws), new Throws()));
        Assert.AreEqual("Throws (ToString() threw InvalidOperationException)", ReactorElementProps.Format("Content", new Throws()).Text);
        Assert.AreEqual("Throws (ToString() threw InvalidOperationException)",
            ReactorElementProps.ComponentProps(new { Content = new Throws() }).Single().Value);
    }
}
