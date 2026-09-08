// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

[TestClass]
public class SelectorServiceTests
{
    private readonly UiSelectorParser _sut = new();

    [TestMethod]
    public void Parse_Slug_ReturnsSlug()
    {
        var result = _sut.Parse("btn-minimize-c4b9");
        Assert.IsTrue(result.IsSlug);
        Assert.AreEqual("btn-minimize-c4b9", result.Slug);
        Assert.IsNull(result.Query);
    }

    [TestMethod]
    public void Parse_SlugWithName_ReturnsSlug()
    {
        var result = _sut.Parse("itm-samples-3f2c");
        Assert.IsTrue(result.IsSlug);
        Assert.AreEqual("itm-samples-3f2c", result.Slug);
    }

    [TestMethod]
    public void Parse_SlugNameless_ReturnsSlug()
    {
        var result = _sut.Parse("pn-c8a3");
        Assert.IsTrue(result.IsSlug);
        Assert.AreEqual("pn-c8a3", result.Slug);
    }

    [TestMethod]
    public void Parse_TextQuery_ReturnsQuery()
    {
        var result = _sut.Parse("Submit");
        Assert.IsTrue(result.IsQuery);
        Assert.AreEqual("Submit", result.Query);
        Assert.IsNull(result.Slug);
    }

    [TestMethod]
    public void Parse_TextQueryWithSpaces_ReturnsQuery()
    {
        var result = _sut.Parse("Submit Order");
        Assert.IsTrue(result.IsQuery);
        Assert.AreEqual("Submit Order", result.Query);
    }

    [TestMethod]
    public void Parse_TextQueryMixedCase_ReturnsQuery()
    {
        var result = _sut.Parse("Minimize");
        Assert.IsTrue(result.IsQuery);
        Assert.AreEqual("Minimize", result.Query);
    }

    [TestMethod]
    public void Parse_Empty_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _sut.Parse(""));
    }

    [TestMethod]
    public void Parse_Whitespace_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => _sut.Parse("   "));
    }

    [TestMethod]
    public void Parse_NotSlug_UpperCase_ReturnsQuery()
    {
        // "Button" has uppercase — not a valid slug (slugs are all lowercase)
        var result = _sut.Parse("Button");
        Assert.IsTrue(result.IsQuery);
        Assert.AreEqual("Button", result.Query);
    }

    [TestMethod]
    public void Parse_NotSlug_NoDash_ReturnsQuery()
    {
        var result = _sut.Parse("minimize");
        Assert.IsTrue(result.IsQuery);
        Assert.AreEqual("minimize", result.Query);
    }
}
