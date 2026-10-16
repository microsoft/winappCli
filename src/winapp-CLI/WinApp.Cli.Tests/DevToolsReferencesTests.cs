// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Tests;

[TestClass]
public class DevToolsReferencesTests
{
    [TestMethod]
    public void Parse_ReadsBothDirectionsAndCycles()
    {
        var parsed = DevToolsReferences.Parse("""
            {"handle":"1","edges":[
              {"kind":"LabeledBy","direction":"out","handle":"42","element":"TextBlock Text=\"Name\"","cycle":false},
              {"kind":"XYFocusRight","direction":"in","element":"Button Content=\"Clear all\"","cycle":true}],
             "incomingTruncated":true}
            """);

        Assert.IsNotNull(parsed);
        var (edges, truncated) = parsed.Value;
        Assert.IsTrue(truncated);
        Assert.HasCount(2, edges);
        Assert.AreEqual(new DevToolsReference("LabeledBy", false, "42", "TextBlock Text=\"Name\"", false), edges[0]);
        Assert.AreEqual("LabeledBy → TextBlock Text=\"Name\"", edges[0].Line);
        Assert.AreEqual("Button Content=\"Clear all\" → this (XYFocusRight)  ↺ cycle", edges[1].Line);
        Assert.IsNull(edges[1].Handle);
    }

    [TestMethod]
    public void Parse_ReadsPendingReactorTargets()
    {
        var parsed = DevToolsReferences.Parse("""
            {"handle":"1","edges":[
              {"kind":"XYFocusRight","direction":"out","element":"Button","pending":true,"targetType":"Button","cycle":false},
              {"kind":"LabeledBy","direction":"out","element":"AutomationId \"Title\"","pending":true,"automationId":"Title","cycle":false}]}
            """);

        Assert.IsNotNull(parsed);
        var edges = parsed.Value.Edges;
        Assert.IsTrue(edges.All(edge => edge.Pending && edge.Handle is null));
        Assert.AreEqual("XYFocusRight → Button (not shown)", edges[0].Line);
        Assert.AreEqual("LabeledBy → AutomationId \"Title\" (not shown)", edges[1].Line);
        Assert.AreEqual("    [grey]XYFocusRight → Button (not shown)[/]", DevToolsReferences.Lines(edges, false).ElementAt(1));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"edges\":[{\"direction\":\"out\"}]}")]
    [DataRow("not json")]
    public void Parse_RefusesWhatItCannotRead(string json) => Assert.IsNull(DevToolsReferences.Parse(json));

    [TestMethod]
    public void Lines_AreEmptyWithoutReferences_AndEscapeMarkup()
    {
        Assert.IsEmpty(DevToolsReferences.Lines([], false));
        var lines = DevToolsReferences.Lines([new("LabeledBy", false, null, "TextBlock [x]", false), new("XYFocusDown", false, "7", "Button", true)], true).ToList();
        Assert.AreEqual("  References:", lines[0]);
        Assert.AreEqual("    LabeledBy → TextBlock [[x]]", lines[1]);
        StringAssert.StartsWith(lines[2], "    [yellow]");
        StringAssert.Contains(lines[3], "Only part of the tree");
    }
}
