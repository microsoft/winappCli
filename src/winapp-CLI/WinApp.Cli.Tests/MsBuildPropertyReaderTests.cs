// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class MsBuildPropertyReaderTests
{
    private static readonly string[] MultipleNames = ["TargetDir", "RunCommand", "WindowsPackageType"];
    private static readonly string[] SingleName = ["OutputType"];

    [TestMethod]
    public void Parse_MultipleProperties_ParsesJsonObject()
    {
        var stdout = """
            {
              "Properties": {
                "TargetDir": "C:\\repo\\bin\\x64\\Debug\\net10.0\\win-x64\\",
                "RunCommand": "C:\\repo\\bin\\x64\\Debug\\net10.0\\win-x64\\App.exe",
                "WindowsPackageType": "MSIX"
              }
            }
            """;

        var result = MsBuildPropertyReader.Parse(stdout, MultipleNames);

        Assert.AreEqual(@"C:\repo\bin\x64\Debug\net10.0\win-x64\", result["TargetDir"]);
        Assert.AreEqual(@"C:\repo\bin\x64\Debug\net10.0\win-x64\App.exe", result["RunCommand"]);
        Assert.AreEqual("MSIX", result["WindowsPackageType"]);
    }

    [TestMethod]
    public void Parse_LookupIsCaseInsensitive()
    {
        var stdout = """{ "Properties": { "TargetDir": "X" } }""";

        var result = MsBuildPropertyReader.Parse(stdout, MultipleNames);

        Assert.AreEqual("X", result["targetdir"]);
        Assert.AreEqual("X", result["TARGETDIR"]);
    }

    [TestMethod]
    public void Parse_JsonWithLeadingDiagnosticPreamble_StillParses()
    {
        // Tolerate a stray line before the JSON object (defensive; normally stdout is clean JSON).
        var stdout = "some warning text\n{ \"Properties\": { \"WindowsPackageType\": \"None\" } }";

        var result = MsBuildPropertyReader.Parse(stdout, MultipleNames);

        Assert.AreEqual("None", result["WindowsPackageType"]);
    }

    [TestMethod]
    public void Parse_JsonWithTrailingDiagnostic_StillParses()
    {
        // Spec M4: trailing content after the JSON object must not defeat parsing. A plain
        // JsonDocument.Parse rejects trailing non-whitespace; the reader-based scan must ignore it.
        var stdout = "{ \"Properties\": { \"WindowsPackageType\": \"MSIX\" } }\nBuild succeeded.";

        var result = MsBuildPropertyReader.Parse(stdout, MultipleNames);

        Assert.AreEqual("MSIX", result["WindowsPackageType"]);
    }

    [TestMethod]
    public void Parse_PreambleContainingBrace_StillParses()
    {
        // Spec M4: a '{' in a diagnostic preamble that is NOT the JSON object must be skipped, and the
        // real { "Properties": {...} } object found — the first '{' is a false positive.
        var stdout = "note: token {placeholder} expanded\n{ \"Properties\": { \"WindowsPackageType\": \"None\" } }";

        var result = MsBuildPropertyReader.Parse(stdout, MultipleNames);

        Assert.AreEqual("None", result["WindowsPackageType"]);
    }

    [TestMethod]
    public void Parse_SingleProperty_ScalarContainingBrace_ReturnsWholeValue()
    {
        // Spec M4: a single-property scalar value that merely contains '{' must be returned verbatim,
        // never misread as the JSON shape.
        var result = MsBuildPropertyReader.Parse("TRACE;DEBUG;NET{0}", SingleName);

        Assert.AreEqual("TRACE;DEBUG;NET{0}", result["OutputType"]);
    }

    [TestMethod]
    public void Parse_EmptyStringPropertyValue_Preserved()
    {
        var stdout = """{ "Properties": { "WindowsPackageType": "" } }""";

        var result = MsBuildPropertyReader.Parse(stdout, MultipleNames);

        Assert.IsTrue(result.ContainsKey("WindowsPackageType"));
        Assert.AreEqual(string.Empty, result["WindowsPackageType"]);
    }

    [TestMethod]
    public void Parse_SingleProperty_ScalarOutput_ReturnsWholeValue()
    {
        var result = MsBuildPropertyReader.Parse("WinExe\n", SingleName);

        Assert.AreEqual("WinExe", result["OutputType"]);
    }

    [TestMethod]
    public void Parse_MultipleRequested_ButScalarOutput_ReturnsEmpty()
    {
        // A non-JSON scalar is only meaningful when exactly one property was requested.
        var result = MsBuildPropertyReader.Parse("not-json", MultipleNames);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void Parse_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.AreEqual(0, MsBuildPropertyReader.Parse("", SingleName).Count);
        Assert.AreEqual(0, MsBuildPropertyReader.Parse("   \r\n  ", SingleName).Count);
    }

    [TestMethod]
    public void Parse_MissingPropertiesObject_FallsBackToScalarForSingle()
    {
        // JSON that is not a { "Properties": {...} } wrapper is treated as a scalar for a single request.
        var result = MsBuildPropertyReader.Parse("[1,2,3]", SingleName);

        Assert.AreEqual("[1,2,3]", result["OutputType"]);
    }

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("false", false)]
    [DataRow("TRUE", true)]
    [DataRow("False", false)]
    [DataRow("  true  ", true)]
    public void ParseOptionalBoolean_ValidValue_IsParsedCaseInsensitively(string value, bool expected)
    {
        // Matches what the NuGet targets' '$(Prop)' == 'true' conditions accept, which is case-insensitive.
        var result = MsBuildPropertyReader.ParseOptionalBoolean(value, out var malformed);

        Assert.AreEqual(expected, result);
        Assert.IsFalse(malformed);
    }

    [TestMethod]
    [DataRow(null, DisplayName = "null")]
    [DataRow("", DisplayName = "unset property evaluates to an empty string")]
    [DataRow("   ", DisplayName = "whitespace")]
    public void ParseOptionalBoolean_Unset_IsNoPreferenceAndNotMalformed(string? value)
    {
        Assert.IsNull(MsBuildPropertyReader.ParseOptionalBoolean(value, out var malformed));
        Assert.IsFalse(malformed, "An undeclared property is not a typo");
    }

    [TestMethod]
    [DataRow("ture", DisplayName = "typo")]
    [DataRow("1", DisplayName = "numeric")]
    [DataRow("yes", DisplayName = "yes")]
    [DataRow("on", DisplayName = "on")]
    public void ParseOptionalBoolean_MalformedValue_IsNoPreferenceAndFlagged(string value)
    {
        // Reading these as an explicit false is the bug this guards: the targets forward NO switch for a
        // value they do not recognize, so a typo would otherwise mean opposite things through `dotnet run`
        // and through a direct `winapp run`.
        Assert.IsNull(MsBuildPropertyReader.ParseOptionalBoolean(value, out var malformed));
        Assert.IsTrue(malformed, "The caller has to be able to report the typo");
    }

    [TestMethod]
    public void ParseItems_ValidItemsObject_ReturnsIdentitiesPerGroup()
    {
        var stdout = """
            {
              "Items": {
                "ProjectReference": [
                  { "Identity": "A.csproj" },
                  { "Identity": "B.csproj" }
                ],
                "PackageReference": [
                  { "Identity": "Microsoft.WindowsAppSDK" }
                ]
              }
            }
            """;

        var result = MsBuildPropertyReader.ParseItems(stdout);

        string[] expectedProjects = ["A.csproj", "B.csproj"];
        string[] expectedPackages = ["Microsoft.WindowsAppSDK"];
        CollectionAssert.AreEqual(expectedProjects, result["ProjectReference"].ToList());
        CollectionAssert.AreEqual(expectedPackages, result["PackageReference"].ToList());
    }

    [TestMethod]
    public void ParseItems_LookupIsCaseInsensitive()
    {
        var result = MsBuildPropertyReader.ParseItems("""{ "Items": { "ProjectReference": [ { "Identity": "A.csproj" } ] } }""");

        string[] expected = ["A.csproj"];
        CollectionAssert.AreEqual(expected, result["projectreference"].ToList());
    }

    [TestMethod]
    public void ParseItems_PreambleAndTrailingDiagnostics_StillParses()
    {
        // Mirror Parse's tolerance: a diagnostic line (with a false-positive brace) before the object and
        // trailing build output after it must not defeat item parsing.
        var stdout = "note: token {placeholder} expanded\n{ \"Items\": { \"ProjectReference\": [ { \"Identity\": \"A.csproj\" } ] } }\nBuild succeeded.";

        var result = MsBuildPropertyReader.ParseItems(stdout);

        string[] expected = ["A.csproj"];
        CollectionAssert.AreEqual(expected, result["ProjectReference"].ToList());
    }

    [TestMethod]
    public void ParseItems_MalformedJson_ReturnsEmpty()
    {
        // Truncated/unterminated JSON: no candidate brace begins a valid JSON value → empty, never throws.
        var stdout = """{ "Items": { "ProjectReference": [ { "Identity": "A.csproj" """;

        var result = MsBuildPropertyReader.ParseItems(stdout);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseItems_EmptyObject_ReturnsEmpty()
    {
        var result = MsBuildPropertyReader.ParseItems("{}");

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseItems_MissingItemsKey_ReturnsEmpty()
    {
        // A { "Properties": {...} }-only envelope (properties requested but no items) yields no item groups.
        var result = MsBuildPropertyReader.ParseItems("""{ "Properties": { "TargetDir": "X" } }""");

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseItems_ItemsNotAnObject_ReturnsEmpty()
    {
        var result = MsBuildPropertyReader.ParseItems("""{ "Items": "not-an-object" }""");

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void ParseItems_NullEmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.AreEqual(0, MsBuildPropertyReader.ParseItems(null!).Count);
        Assert.AreEqual(0, MsBuildPropertyReader.ParseItems("").Count);
        Assert.AreEqual(0, MsBuildPropertyReader.ParseItems("   \r\n  ").Count);
    }

    [TestMethod]
    public void ParseItems_GroupWithNoIdentities_ReturnsEmptyList()
    {
        // Array entries lacking an "Identity" string are skipped; the group is still surfaced (empty list).
        var stdout = """{ "Items": { "ProjectReference": [ { "Other": "x" }, { "Identity": "" } ] } }""";

        var result = MsBuildPropertyReader.ParseItems(stdout);

        Assert.IsTrue(result.ContainsKey("ProjectReference"));
        Assert.AreEqual(0, result["ProjectReference"].Count);
    }
}
